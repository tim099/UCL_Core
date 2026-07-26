// 區塊職責: ScreenStream 控制 Page (UCL_Core 版) — 獨立的 toggle UI, 跟 LoginStatusPage 隔離 concern。
//            自 EOV 專案的 RCG_ScreenStreamPage 遷移 (Tim 2026-07-26 拍板「相關功能遷移到 UCL_Core;
//            確認新版可用後移除 RCG 版」); 功能對齊 RCG 版 T13/T14/T19/T-STT-PageToggle/T-STT-StaleFix 全量。
// 物理意義: 讀寫 <主專案>/AgentCommands/_screenstream/_config.json — daemon 端每 loop reload 此檔反應 toggle。
// 設計取捨:
//   - 獨立 Page (per Tim 2026-05-16 拍板) 防誤觸 + 視覺強烈警示「錄影中」
//   - 純讀寫 config + 顯示 daemon 端寫的 frame_count/started_at 資訊
//   - 新增「🎬 影音管理」入口按鈕跳轉 UCL_MediaAdminPage (Tim 2026-07-26 要求) — STT/OCR 安裝與細部設定歸那頁
// 2026-07-26 kaguya (Luna) — 自 RCG_ScreenStreamPage (T13 basecamp 2026-05-16) 遷移
#if UNITY_EDITOR
using System;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// ScreenStream daemon 控制 Page (UCL_Core 版) — 獨立隔離 concern, 提供強烈視覺警示 + 防誤觸。
    /// STT/OCR 的依賴安裝與細部設定請走「影音管理」頁 (UCL_MediaAdminPage, 本頁有跳轉鈕)。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ScreenStreamPage.md")]
    public class UCL_ScreenStreamPage : UCL_CommonEditorPage
    {
        public override string WindowName => "螢幕直播錄影";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // 區塊職責: 路徑常數 — runtime 狀態一律在「主專案」AgentCommands/_screenstream (per-project)
        const string CONFIG_RELATIVE = "AgentCommands/_screenstream/_config.json";
        const string LATEST_TXT_RELATIVE = "AgentCommands/_screenstream/_latest.txt";
        const string LATEST_JPG_RELATIVE = "AgentCommands/_screenstream/_latest.jpg";
        const string PID_FILE_RELATIVE = "AgentCommands/_screenstream/_daemon.pid";
        const string MONITORS_RELATIVE = "AgentCommands/_screenstream/_monitors.json";

        // 區塊職責: config 快取 + 顯示用 state
        // 物理意義: page enter / 每 N 秒 reload 一次, 避免每 OnGUI 都 file IO
        bool m_Enabled = false;
        int m_Fps = 1;
        int m_MaxFrames = 600;
        string m_Resolution = "1080p";
        int m_Quality = 65;
        string m_Monitor = "primary";

        // 區塊職責: STT (語音轉錄) 設定 — 錄影時同步啟動 whisper 語音轉文字 (T-STT-PageToggle, Tim 2026-07-09)
        // 物理意義: m_SttSetting = Tim 的開關意圖 (要不要語音轉錄, 持久化在 config.stt_setting);
        //          config.stt_enabled (daemon 讀的實效值) = m_Enabled && m_SttSetting —
        //          即「錄影中且開了 STT」才真啟動 worker, 停錄影自動停 STT (whisper GPU ~460MB 不空轉).
        // 數值影響: daemon SttCacheWorker lifecycle 綁 config.stt_enabled toggle, 每 loop reload 即生效;
        //          model/lang 改動需 toggle off→on 才重起 (開始/停止錄影天然觸發).
        bool m_SttSetting = false;
        string m_SttModel = "small";
        string m_SttLang = "";
        // T-STT-StaleFix (Tim 2026-07-20): stt_prompt = whisper 人名詞彙偏置 (陪看 skill 寫入) —
        //   Page 不編輯它, 只顯示殘留 + 提供清除; 開始錄影時自動清空, 防上一場人名偏置跨場造成幻聽.
        string m_SttPrompt = "";

        // 區塊職責: 3-way merge baseline — 每個可編輯欄位記「上次從磁碟看到的值」
        // 物理意義: config 檔會被外部工具 (stream_watch_session.py / agent) 併發改寫;
        //          reload 時只有「UI 值 == baseline (Tim 沒動過)」的欄位才吃磁碟新值,
        //          Tim 編輯中的欄位保留 — 修「Page 拿舊快取蓋掉外部新設定」的 stale-clobber bug (Tim 2026-07-20).
        // 數值影響: baseline 每次 reload/save 後更新為磁碟當前值; 判定粒度 = 單一欄位.
        int m_BaseFps = 1;
        int m_BaseMaxFrames = 600;
        string m_BaseResolution = "1080p";
        int m_BaseQuality = 65;
        string m_BaseMonitor = "primary";
        bool m_BaseSttSetting = false;
        string m_BaseSttModel = "small";
        string m_BaseSttLang = "";
        // config 檔 mtime 快照 — 沒變就跳過 parse (省 IO), 變了才走 merge reload
        long m_ConfigMtime = 0;
        static readonly string[] s_SttModelOptions = { "tiny", "base", "small", "medium", "large-v3" };
        // lang: 空字串=自動偵測; 顯示 label 與實際值分開 (空值不好按)
        static readonly string[] s_SttLangLabels = { "自動", "ja 日", "en 英", "zh 中" };
        static readonly string[] s_SttLangValues = { "", "ja", "en", "zh" };

        long m_FrameCount = 0;
        string m_StartedAt = "";
        bool m_DaemonAlive = false;
        string m_LatestFrame = "";

        bool m_ConfigLoaded = false;
        double m_LastReloadTime = -1.0;
        const double RELOAD_INTERVAL_SEC = 2.0;

        // Resolution dropdown options
        static readonly string[] s_ResolutionOptions = { "native", "2k", "1440p", "1080p", "720p", "480p" };

        // T14 — multi-monitor state
        // 物理意義: daemon 啟動時寫 _monitors.json 列舉所有 physical monitor;
        //          page 讀此檔做 dropdown options, 標 primary + 顯示解析度給 Tim 選
        class MonitorInfo
        {
            public int Index;
            public int X, Y, W, H;
            public bool Primary;
            public string Name;
            public string Label => $"#{Index} {W}x{H}{(Primary ? " (primary)" : "")}";
        }
        System.Collections.Generic.List<MonitorInfo> m_Monitors = new System.Collections.Generic.List<MonitorInfo>();

        // T14 — preview texture state
        // 物理意義: 讀 _latest.jpg → Texture2D.LoadImage → GUI.DrawTexture 顯示;
        //          每 PREVIEW_RELOAD_INTERVAL 秒 reload 一次, 避免 OnGUI 每 frame 重 decode
        Texture2D m_PreviewTexture;
        double m_LastPreviewReload = -1.0;
        long m_LastPreviewMtime = 0;

        bool m_ShowPreview = true;

        public float PREVIEW_RELOAD_INTERVAL_SEC => m_Fps <= 0 ? 1 : 1 / m_Fps;

        public static UCL_ScreenStreamPage Create()
        {
            var page = new UCL_ScreenStreamPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        // T14 fix (2026-05-16): UCL_CommonEditorPage 沒有 OnEnter virtual; lazy init 改用 first-OnGUI 觸發
        // 設計取捨: Init 過早 (沒 EditorApplication context); ContentOnGUI 首次跑時 init 最穩
        bool m_FirstGuiDone = false;

        void EnsureInitialReload()
        {
            if (m_FirstGuiDone) return;
            ReloadFromDisk();
            ReloadMonitors();
            ReloadPreview();
            m_FirstGuiDone = true;
        }

        // T14 — Multi-monitor enumeration: 讀 daemon 寫的 _monitors.json
        void ReloadMonitors()
        {
            m_Monitors.Clear();
            try
            {
                string path = Path.Combine(GetRepoRoot(), MONITORS_RELATIVE);
                if (!File.Exists(path)) return;
                string txt = File.ReadAllText(path);
                JsonData data = JsonData.ParseJson(txt);
                if (data == null || !data.Contains("monitors")) return;
                JsonData list = data["monitors"];
                if (list == null || !list.IsArray) return;
                for (int i = 0; i < list.Count; i++)
                {
                    JsonData m = list[i];
                    m_Monitors.Add(new MonitorInfo
                    {
                        Index = m.GetInt("index", i),
                        X = m.GetInt("x", 0),
                        Y = m.GetInt("y", 0),
                        W = m.GetInt("w", 0),
                        H = m.GetInt("h", 0),
                        Primary = m.GetBool("primary", false),
                        Name = m.GetString("name", $"DISPLAY{i + 1}"),
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] reload monitors fail: {e.Message}");
            }
        }

        // T14 — Preview reload: 讀 _latest.jpg byte → Texture2D.LoadImage
        void ReloadPreview()
        {
            try
            {
                string path = Path.Combine(GetRepoRoot(), LATEST_JPG_RELATIVE);
                if (!File.Exists(path)) return;
                long mtime = new FileInfo(path).LastWriteTime.Ticks;
                if (mtime == m_LastPreviewMtime) return;   // 沒新 frame, 不必 reload
                byte[] bytes = File.ReadAllBytes(path);
                if (m_PreviewTexture == null)
                {
                    m_PreviewTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    m_PreviewTexture.hideFlags = HideFlags.HideAndDontSave;
                }
                m_PreviewTexture.LoadImage(bytes);
                m_LastPreviewMtime = mtime;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] preview reload fail: {e.Message}");
            }
        }

        // ===========================================================
        // Config IO
        // 物理意義: JsonData parse _config.json; 寫回時保留 daemon-managed 欄位 (frame_count, started_at)
        // ===========================================================
        // 區塊職責: 3-way merge 輔助 — 決定單一可編輯欄位 reload 時採 UI 值還是磁碟值
        // 物理意義: ui == baseline 代表 Tim 上次 reload 後沒動過此欄 → 吃磁碟新值 (外部工具改的生效);
        //          ui != baseline 代表 Tim 正在編輯 → 保留 UI 值不被磁碟蓋掉.
        // 數值影響: baseline 一律更新為磁碟當前值 (「上次看到的磁碟值」語意).
        static int MergeField(int ui, ref int baseline, int disk)
        {
            int result = (ui == baseline) ? disk : ui;
            baseline = disk;
            return result;
        }
        static bool MergeField(bool ui, ref bool baseline, bool disk)
        {
            bool result = (ui == baseline) ? disk : ui;
            baseline = disk;
            return result;
        }
        static string MergeField(string ui, ref string baseline, string disk)
        {
            string result = (ui == baseline) ? disk : ui;
            baseline = disk;
            return result;
        }

        void ReloadFromDisk()
        {
            // T-STT-StaleFix (Tim 2026-07-20): mtime 感知 reload + 可編輯欄位 3-way merge —
            //   外部工具 (stream_watch_session.py / agent) 改 config 要能即時反映, 又不蓋掉 Tim 編輯中的欄位.
            try
            {
                string repoRoot = GetRepoRoot();
                string path = Path.Combine(repoRoot, CONFIG_RELATIVE);
                if (!File.Exists(path))
                {
                    m_ConfigLoaded = false;
                    return;
                }
                long mtime = new FileInfo(path).LastWriteTime.Ticks;
                if (!m_ConfigLoaded || mtime != m_ConfigMtime)
                {
                    string json = File.ReadAllText(path);
                    JsonData data = JsonData.ParseJson(json);
                    if (data != null)
                    {
                        // daemon-managed / 狀態欄位: 無條件採磁碟值 (Page 從不擁有這些欄位)
                        m_Enabled = data.GetBool("enabled", false);
                        m_FrameCount = data.GetInt("frame_count", 0);
                        m_StartedAt = data.GetString("started_at", "");
                        m_SttPrompt = data.GetString("stt_prompt", "");
                        // 可編輯欄位: 3-way merge — Tim 沒動過的欄位吃磁碟新值, 編輯中的保留
                        m_Fps = MergeField(m_Fps, ref m_BaseFps, data.GetInt("fps", 1));
                        m_MaxFrames = MergeField(m_MaxFrames, ref m_BaseMaxFrames, data.GetInt("max_frames", 600));
                        m_Resolution = MergeField(m_Resolution, ref m_BaseResolution, data.GetString("resolution", "1080p"));
                        m_Quality = MergeField(m_Quality, ref m_BaseQuality, data.GetInt("quality", 65));
                        m_Monitor = MergeField(m_Monitor, ref m_BaseMonitor, data.GetString("monitor", "primary"));
                        // STT: stt_setting 是 Page 意圖; 舊 config 只有 stt_enabled → fallback 讀它做遷移
                        m_SttSetting = MergeField(m_SttSetting, ref m_BaseSttSetting,
                            data.GetBool("stt_setting", data.GetBool("stt_enabled", false)));
                        m_SttModel = MergeField(m_SttModel, ref m_BaseSttModel, data.GetString("stt_model", "small"));
                        m_SttLang = MergeField(m_SttLang, ref m_BaseSttLang, data.GetString("stt_lang", ""));
                    }
                    m_ConfigMtime = mtime;
                }
                m_ConfigLoaded = true;

                // Latest frame info
                string latestPath = Path.Combine(repoRoot, LATEST_TXT_RELATIVE);
                if (File.Exists(latestPath))
                {
                    m_LatestFrame = File.ReadAllText(latestPath).Trim();
                }

                // Daemon alive check
                string pidPath = Path.Combine(repoRoot, PID_FILE_RELATIVE);
                m_DaemonAlive = File.Exists(pidPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] reload fail: {e.Message}");
            }
        }

        /// <summary>
        /// 寫回 _config.json — 保留 daemon-managed 欄位, 覆寫 Page 擁有的設定欄位。
        /// clearSttPrompt=true 時同步清空 stt_prompt (whisper 人名詞彙偏置) —
        /// 開始錄影時帶 true (Tim 2026-07-20 拍板): 每場開播都從乾淨偏置起跑,
        /// 上一場的人名 prompt 不再殘留造成跨場幻聽; 需要偏置的場由陪看 skill 開播後自行寫入。
        /// </summary>
        void SaveToDisk(bool clearSttPrompt = false)
        {
            try
            {
                string repoRoot = GetRepoRoot();
                string path = Path.Combine(repoRoot, CONFIG_RELATIVE);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // 讀現有 (preserve frame_count / started_at 等 daemon 寫的欄位)
                JsonData existing = null;
                if (File.Exists(path))
                {
                    try { existing = JsonData.ParseJson(File.ReadAllText(path)); }
                    catch { existing = null; }
                }
                if (existing == null) existing = new JsonData();

                existing["enabled"] = new JsonData(m_Enabled);
                existing["fps"] = new JsonData(m_Fps);
                existing["max_frames"] = new JsonData(m_MaxFrames);
                existing["resolution"] = new JsonData(m_Resolution);
                existing["quality"] = new JsonData(m_Quality);
                existing["monitor"] = new JsonData(m_Monitor);
                // STT: stt_setting = Tim 意圖 (持久化); stt_enabled = 實效值 (錄影中且開了才真啟動 worker)
                // → 開始錄影 (m_Enabled=true) 且 stt_setting=on → daemon 下次 reload 起 whisper; 停錄影自動關.
                existing["stt_setting"] = new JsonData(m_SttSetting);
                existing["stt_enabled"] = new JsonData(m_Enabled && m_SttSetting);
                existing["stt_model"] = new JsonData(m_SttModel);
                existing["stt_lang"] = new JsonData(m_SttLang);
                // T-STT-StaleFix: 開始錄影時清空人名詞彙偏置 (防上一場殘留跨場幻聽); 其餘 save 保留既有值
                if (clearSttPrompt)
                {
                    existing["stt_prompt"] = new JsonData("");
                    m_SttPrompt = "";
                }

                string newJson = existing.ToJsonBeautify();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, newJson + "\n");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                // T-STT-StaleFix: save 完把 baseline 對齊剛寫下的 UI 值 + 更新 mtime 快照 —
                //   下輪 auto reload 不會把剛存的值誤判成「外部改動」再 merge 一次
                m_BaseFps = m_Fps;
                m_BaseMaxFrames = m_MaxFrames;
                m_BaseResolution = m_Resolution;
                m_BaseQuality = m_Quality;
                m_BaseMonitor = m_Monitor;
                m_BaseSttSetting = m_SttSetting;
                m_BaseSttModel = m_SttModel;
                m_BaseSttLang = m_SttLang;
                m_ConfigMtime = new FileInfo(path).LastWriteTime.Ticks;
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_ScreenStreamPage] save fail: {e.Message}");
            }
        }

        // ===========================================================
        // GUI
        // 物理意義: 強烈視覺警示 (錄影中紅燈大字), 二段確認 toggle 避免誤觸
        // ===========================================================
        protected override void ContentOnGUI()
        {
            EnsureInitialReload();   // T14 fix: lazy init 代 OnEnter (base class 沒 OnEnter virtual)

            // Auto reload every N seconds
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - m_LastReloadTime > RELOAD_INTERVAL_SEC)
            {
                ReloadFromDisk();
                ReloadMonitors();
                m_LastReloadTime = now;
            }
            // T14 — Preview reload (獨立節奏, 比 config reload 頻繁)
            if (now - m_LastPreviewReload > PREVIEW_RELOAD_INTERVAL_SEC)
            {
                if (m_ShowPreview && m_Enabled)
                {
                    ReloadPreview();
                }
                m_LastPreviewReload = now;
            }

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("🎥 ScreenStream Control", new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold });
            GUILayout.FlexibleSpace();
            // 入口按鈕 — 跳轉「影音管理」頁 (Tim 2026-07-26 要求): STT/OCR 依賴安裝、細部設定、試錄都在那頁
            if (GUILayout.Button("🎬 影音管理 (STT/OCR 安裝與設定)", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
            {
                UCL_MediaAdminPage.Create();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("獨立 Page 防誤觸. 錄影中時敏感 Page (含 token / 帳號) 會自動黑屏.");
            GUILayout.Space(8);

            if (!m_ConfigLoaded)
            {
                GUILayout.Label("⚠ _config.json 不存在. daemon 未啟動或首次跑.");
                if (GUILayout.Button("初始化 (建 default config)", GUILayout.Height(30)))
                {
                    SaveToDisk();
                    ReloadFromDisk();
                }
                return;
            }

            // ===========================================================
            // Status banner — 強烈視覺
            // ===========================================================
            var oldColor = GUI.backgroundColor;
            if (m_Enabled)
            {
                GUI.backgroundColor = new Color(0.8f, 0.1f, 0.1f);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("🔴 錄影中 RECORDING", new GUIStyle(GUI.skin.label)
                    { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter });
                GUILayout.Label($"已截 {m_FrameCount} frames, 當前 latest: frame_{m_LatestFrame}",
                    new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
                if (!string.IsNullOrEmpty(m_StartedAt))
                {
                    GUILayout.Label($"started_at: {m_StartedAt}",
                        new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 10 });
                }
                GUILayout.EndVertical();
            }
            else
            {
                GUI.backgroundColor = new Color(0.2f, 0.5f, 0.2f);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("⚫ 停止 (daemon idle 等 toggle on)", new GUIStyle(GUI.skin.label)
                    { fontSize = 18, alignment = TextAnchor.MiddleCenter });
                GUILayout.EndVertical();
            }
            GUI.backgroundColor = oldColor;

            GUILayout.Space(10);

            // Daemon health
            GUILayout.Label($"Daemon process: {(m_DaemonAlive ? "🟢 ALIVE (PID file 存在)" : "🔴 DEAD (PID file 缺, 等 Editor respawn)")}");
            GUILayout.Space(5);

            // T14 — Preview (錄影中才顯示)
            if (m_Enabled)
            {
                GUILayout.BeginHorizontal();
                m_ShowPreview = GUILayout.Toggle(m_ShowPreview, " 顯示當前截圖預覽");
                if (GUILayout.Button("立即 reload", GUILayout.Width(110)))
                {
                    ReloadPreview();
                }
                GUILayout.EndHorizontal();
                if (m_ShowPreview && m_PreviewTexture != null)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label($"當前 frame_{m_LatestFrame} (預覽每 {PREVIEW_RELOAD_INTERVAL_SEC}s 自動 reload)",
                            new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter });
                        // 等比例縮放 — 預覽寬度固定, 省 GUI 重 layout cost
                        float aspect = (float)m_PreviewTexture.width / m_PreviewTexture.height;
                        const float previewWidth = 1080f;
                        float previewHeight = previewWidth / aspect;
                        Rect r = GUILayoutUtility.GetRect(previewWidth, previewHeight);
                        GUI.DrawTexture(r, m_PreviewTexture, ScaleMode.ScaleToFit);
                    }
                }
                else if (m_ShowPreview)
                {
                    GUILayout.Label("(尚無 _latest.jpg, daemon 還沒寫第一張)",
                        new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic });
                }
            }
            GUILayout.Space(8);

            // ===========================================================
            // Toggle — 二段確認防誤觸
            // 物理意義: 第一次點 = arm; 第二次點 (5s 內) = 真 toggle
            // ===========================================================
            GUILayout.BeginHorizontal();
            if (m_Enabled)
            {
                GUI.backgroundColor = new Color(0.8f, 0.3f, 0.3f);
                if (GUILayout.Button("⏹ 停止錄影", GUILayout.Height(40)))
                {
                    m_Enabled = false;
                    SaveToDisk();
                }
            }
            else
            {
                GUI.backgroundColor = new Color(0.3f, 0.6f, 0.3f);
                if (GUILayout.Button("▶ 開始錄影 (將每秒截圖)", GUILayout.Height(40)))
                {
                    m_PendingArmEnable = !m_PendingArmEnable;
                    m_PendingArmEnableTime = UnityEditor.EditorApplication.timeSinceStartup;
                }
                if (m_PendingArmEnable)
                {
                    double remaining = 5.0 - (UnityEditor.EditorApplication.timeSinceStartup - m_PendingArmEnableTime);
                    if (remaining > 0)
                    {
                        GUI.backgroundColor = new Color(0.9f, 0.5f, 0.1f);
                        if (GUILayout.Button($"⚠ 再點一次確認 ({remaining:F0}s 內)", GUILayout.Height(40)))
                        {
                            m_Enabled = true;
                            // T-STT-StaleFix (Tim 2026-07-20 拍板): 開始錄影 = 同步保存當前所有 UI 設定
                            // (不必先按「儲存設定」) + 清空上一場的 stt_prompt 人名偏置防跨場幻聽
                            SaveToDisk(clearSttPrompt: true);
                            m_PendingArmEnable = false;
                        }
                    }
                    else
                    {
                        m_PendingArmEnable = false;
                    }
                }
            }
            GUI.backgroundColor = oldColor;
            GUILayout.EndHorizontal();

            GUILayout.Space(15);

            // ===========================================================
            // Config 設定
            // ===========================================================
            var widthStyle = GUILayout.Width(UCL_GUIStyle.GetScaledSize(80));

            GUILayout.Label("⚙ 設定 (按「儲存設定」或「開始/停止錄影」時自動保存; daemon 1-2s 內生效)",
                new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });

            GUILayout.BeginHorizontal();
            GUILayout.Label("fps:", UCL_GUIStyle.LabelStyle, widthStyle);
            m_Fps = UCL_GUILayout.IntField(m_Fps, widthStyle);
            GUILayout.Label("(每秒截幾張)", UCL_GUIStyle.LabelStyle, widthStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("max_frames:", UCL_GUIStyle.LabelStyle, widthStyle);
            m_MaxFrames = UCL_GUILayout.IntField(m_MaxFrames, widthStyle);
            GUILayout.Label("(ring buffer 大小; 600 = 10 min @ 1fps)");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("resolution:", GUILayout.Width(120));
            int curIdx = Array.IndexOf(s_ResolutionOptions, m_Resolution);
            if (curIdx < 0) curIdx = 3; // default 1080p
            int newIdx = GUILayout.SelectionGrid(curIdx, s_ResolutionOptions, s_ResolutionOptions.Length);
            if (newIdx != curIdx) m_Resolution = s_ResolutionOptions[newIdx];
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("quality:", GUILayout.Width(120));
            m_Quality = (int)GUILayout.HorizontalSlider(m_Quality, 1, 95, GUILayout.Width(200));
            GUILayout.Label($"{m_Quality} (JPEG 1-95, 65 平衡)");
            GUILayout.EndHorizontal();

            // T14 — Monitor selector: primary / all / unity_game / 0 / 1 / 2 ... 列舉真實 monitor
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            GUILayout.Label("monitor:", GUILayout.Width(120));
            if (GUILayout.Toggle(m_Monitor == "primary", "primary", GUILayout.Width(80))) m_Monitor = "primary";
            if (GUILayout.Toggle(m_Monitor == "all", "all (拼接)", GUILayout.Width(90))) m_Monitor = "all";
            // T19 — Unity Game view 渲染輸出來源 (非 OS 螢幕擷取): Unity 端 GameViewCapturer 於 Play mode 供應 frame
            if (GUILayout.Toggle(m_Monitor == "unity_game", "unity_game (Game 視窗)", GUILayout.Width(170))) m_Monitor = "unity_game";
            GUILayout.EndHorizontal();
            if (m_Monitor == "unity_game")
            {
                GUILayout.Label("  ⓘ 擷取 Unity Game view 真實渲染輸出 — 須進 Play mode 才有畫面 (edit mode / 非播放時顯示 placeholder)。",
                    new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Italic });
            }
            if (m_Monitors.Count > 0)
            {
                GUILayout.Label("  或選實體 monitor:", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                foreach (var m in m_Monitors)
                {
                    string label = m.Label;
                    string indexStr = m.Index.ToString();
                    bool selected = m_Monitor == indexStr;
                    if (GUILayout.Toggle(selected, label, GUILayout.MinWidth(160)))
                    {
                        if (!selected) m_Monitor = indexStr;
                    }
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("  (尚未偵測到 monitor; daemon 啟動後 _monitors.json 會更新)",
                    new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
            }
            GUILayout.EndVertical();

            // ===========================================================
            // T-STT-PageToggle (Tim 2026-07-09) — 語音轉錄 (STT) 設定
            // 物理意義: 開關 = 錄影時要不要同步跑 whisper 把系統音訊轉逐句文字 (montage --stt 讀 cache);
            //          stt_enabled 實效值 = 錄影中 && 本開關, 故「開始錄影」即同步啟動、「停止錄影」即停.
            // ===========================================================
            GUILayout.Space(10);
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.BeginHorizontal();
                m_SttSetting = GUILayout.Toggle(m_SttSetting, " 🎙 錄影時同步啟動語音轉錄 (STT)");
                GUILayout.FlexibleSpace();
                // 即時狀態燈: 錄影中且開了 → 實際會跑
                if (m_SttSetting && m_Enabled)
                    GUILayout.Label("🟢 錄影中已啟動", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                else if (m_SttSetting)
                    GUILayout.Label("⚪ 待開始錄影時啟動", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                // 依賴沒裝好? 跳「影音管理」裝 whisper / 換 CUDA 版 (安裝與健檢歸那頁)
                if (GUILayout.Button("🎬 影音管理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_MediaAdminPage.Create();
                }
                GUILayout.EndHorizontal();

                if (m_SttSetting)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("model:", GUILayout.Width(60));
                    int mIdx = Array.IndexOf(s_SttModelOptions, m_SttModel);
                    if (mIdx < 0) mIdx = 2; // default small
                    int mNew = GUILayout.SelectionGrid(mIdx, s_SttModelOptions, s_SttModelOptions.Length);
                    if (mNew != mIdx) m_SttModel = s_SttModelOptions[mNew];
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("語言:", GUILayout.Width(60));
                    int lIdx = Array.IndexOf(s_SttLangValues, m_SttLang);
                    if (lIdx < 0) lIdx = 0; // default 自動
                    int lNew = GUILayout.SelectionGrid(lIdx, s_SttLangLabels, s_SttLangLabels.Length);
                    if (lNew != lIdx) m_SttLang = s_SttLangValues[lNew];
                    GUILayout.EndHorizontal();

                    GUILayout.Label("  ⓘ small 是品質vs速度甜蜜點; 看日番建議選 ja (自動偵測會飄). "
                        + "whisper 常駐 GPU ~460MB, 停錄影自動釋放.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    GUILayout.Label("  ⚠ 若 daemon 端 whisper 不可用 (log 印「whisper 不可用」), STT 靜默跳過不影響截圖; "
                        + "依賴安裝/健檢請開「影音管理」頁.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });

                    // T-STT-StaleFix (Tim 2026-07-20) — 人名詞彙偏置 (stt_prompt) 殘留可視化
                    // 物理意義: 陪看 skill 開播會寫入該片角色名做 whisper 偏置; 換片沒清會讓 whisper
                    //          幻聽出舊片人名。開始錄影已自動清空, 此處另給手動清除鈕 + 讓殘留看得見.
                    if (!string.IsNullOrEmpty(m_SttPrompt))
                    {
                        GUILayout.BeginHorizontal();
                        string promptPreview = m_SttPrompt.Length > 60 ? m_SttPrompt.Substring(0, 60) + "…" : m_SttPrompt;
                        GUILayout.Label($"  🏷 殘留人名偏置 (stt_prompt): {promptPreview}",
                            new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                        if (GUILayout.Button("清除偏置", GUILayout.Width(80)))
                        {
                            SaveToDisk(clearSttPrompt: true);
                        }
                        GUILayout.EndHorizontal();
                        GUILayout.Label("  ⓘ 開始錄影時會自動清空此偏置 (每場從乾淨狀態起跑, 防跨場幻聽).",
                            new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    }
                }
            }

            GUILayout.Space(10);
            if (GUILayout.Button("💾 儲存設定", GUILayout.Height(30)))
            {
                SaveToDisk();
                ReloadFromDisk();
            }

            GUILayout.Space(15);
            GUILayout.Label("📂 路徑", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            GUILayout.Label("config: AgentCommands/_screenstream/_config.json");
            GUILayout.Label("frames: AgentCommands/_screenstream/frames/frame_NNNN.jpg");
            GUILayout.Label("latest: AgentCommands/_screenstream/_latest.jpg");
            GUILayout.Label("tools: <UCL_Core>/Tools~/AgentCommands/screenstream_daemon.py / screenstream_montage.py / stream_watch_session.py");
        }

        // 二段確認狀態
        bool m_PendingArmEnable = false;
        double m_PendingArmEnableTime = -1.0;

        static string GetRepoRoot()
        {
            // repo 根 — Application.dataPath 上兩層 (與 RCG 版一致; UCL_RepoPath.RepoRoot 同值, 此處保持零依賴)
            return Directory.GetParent(Application.dataPath).Parent.FullName;
        }
    }
}
#endif
