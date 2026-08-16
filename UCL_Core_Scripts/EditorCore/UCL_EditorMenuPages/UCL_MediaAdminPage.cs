// 區塊職責：影音管理頁 (Media Admin) — 語音轉文字 (STT) 與字幕讀取 (OCR) 的可視化管理入口。
//            (Tim 2026-07-25 拍板；參考 UCL_KnowledgeBaseAdminPage 結構，命名走「影音」抽象 —
//             先收 STT (whisper) 的安裝/設定，之後字幕 OCR 讀取也整合進本頁，換後端不必改頁名。)
// 物理意義：真正的環境檢查 / 安裝 / config 讀寫都在 media_admin.py；本頁只是 runner 之上的薄 UI —
//          按鈕 → UCL_MediaAdminRunner async spawn python → 顯示結果。不在 main thread 跑重活 (不凍結)。
// 設計取捨：STT/OCR 的 runtime 設定住「主專案」AgentCommands/_screenstream/_config.json (daemon 讀)，
//          本頁經 python get-config/set-config 讀寫白名單欄位 — 與 RCG_ScreenStreamPage 的錄影欄位分權：
//          錄影開關歸錄影頁，影音辨識欄位歸本頁。UI 字串仿慣例 zh-Hant 硬編 (內部管理頁，不走 CodeLocalize)。
#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.MediaAdmin;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 影音管理頁 — STT (whisper 語音轉文字) / OCR (字幕讀取) 的環境狀態、依賴安裝、設定調整與試錄。
    /// 全部操作委派給 media_admin.py (經 UCL_MediaAdminRunner)，python 為唯一真相源。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MediaAdminPage.md")]
    public class UCL_MediaAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "影音管理";
        public override bool ShowInPageMenu => true;

        public static UCL_MediaAdminPage Create() => UCL_EditorPage.Create<UCL_MediaAdminPage>();

        // ==== 狀態快取 ====
        string m_StatusText = "(尚未載入 — 按「🔄 重新整理狀態」)";
        string m_LastOutput = "";
        bool m_Busy = false;
        string m_BusyLabel = "";
        bool m_Loaded = false;          // 首幀 lazy-load 守門 (OnResume 不會在首次 Push 觸發，比照 KB 頁慣例)
        bool m_ConfigLoaded = false;    // get-config 是否已回填欄位 (未回填前鎖「套用」防空值覆寫)
        string m_ConfigPath = "";       // daemon config 實際路徑 (顯示用)

        // ==== STT 可調欄位 (對齊 media_admin.py EDITABLE_KEYS 白名單) ====
        bool m_SttSetting = false;      // STT 意圖開關 (錄影時同步啟動語音轉錄)
        string m_SttModel = "small";    // whisper 模型 (tiny/base/small/medium/large-v3)
        string m_SttLang = "ja";        // 轉錄語言 hint (ja/zh/en; 空 = auto)
        int m_SttChunkSec = 15;         // daemon 連續轉錄分段秒數
        string m_SttPrompt = "";        // whisper initial_prompt 詞彙偏置 (人名用原文字形，如片假名)

        // ==== OCR 可調欄位 (字幕讀取 — 本頁的第二塊拼圖) ====
        // 字幕帶座標 — 底部原點語意 (Tim 2026-07-28 拍板: 0=畫面下方, 高度往上長)
        bool m_OcrEnabled = true;       // 字幕 OCR 開關
        int m_OcrWorkers = 1;           // OCR worker 數
        float m_OcrYBottomPct = 0f;     // 帶底邊離畫面下緣距離比例 (0=貼底)
        float m_OcrHPct = 0.12f;        // 字幕帶高度比例 (從底邊往上長)
        float m_OcrMinConf = 0.5f;      // OCR 最低信度過濾 (0~1)

        // ==== 插件清單 (media_admin.py list-plugins 的 C# 鏡像；本頁不自己維護清單) ====
        class PluginAction { public string Id, Label, Hint; public bool Danger; }
        class PluginInfo
        {
            public string Id, Name, Desc, ProbeSummary;
            public bool Installed;
            public PluginAction[] Actions = new PluginAction[0];
        }
        PluginInfo[] m_Plugins = new PluginInfo[0];
        int m_PluginIdx = 0;

        // ==== 模型權重清單 (media_admin.py list-models 的 C# 鏡像) ====
        // 物理意義：插件是 pip 套件、模型是快取目錄，兩者生命週期不同 —— 卸掉套件不會收回 1.5GB 權重。
        // ⚠ partial 欄是「下載中斷留下的碎片」：它會佔磁碟但**不算已安裝**
        //   （🩸 2026-08-16 本機曾累積 1383MB 殘骸，而第一版把它算成「已安裝」）。
        class ModelInfo
        {
            public string Id, Backend, SizeName, PathStr;
            public float Mb, PartialMb;
            public bool Installed;
        }
        ModelInfo[] m_Models = new ModelInfo[0];
        int m_ModelIdx = 0;

        // ==== 試錄參數 ====
        int m_TestSec = 8;              // 試錄秒數 (擷取即 wall-clock 阻塞，別設太長)

        // whisper 模型下拉選項 — 對齊 audio_transcribe.py 支援清單
        static readonly string[] ModelOptions = { "tiny", "base", "small", "medium", "large-v3" };

        GUIStyle m_WrapStyle;
        private UCL_ObjectDictionary m_Dic = new();
        GUIStyle WrapStyle
        {
            get
            {
                if (m_WrapStyle == null)
                    m_WrapStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapStyle;
            }
        }

        // 區塊職責：整頁刷新 — 狀態健檢 + 讀回 config 欄位
        void RefreshAll()
        {
            RunOp("狀態", "status", 60000);
            RunOp("讀取設定", "get-config", 30000);
            RunOp("插件清單", "list-plugins", 60000);
            RunOp("模型清單", "list-models", 60000);
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Busy))
            {
                if (GUILayout.Button("🔄 重新整理狀態", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    RefreshAll();
            }
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) { m_Loaded = true; RefreshAll(); }   // 首幀 lazy-load
            GUILayout.Label("🎬 影音處理 — 語音轉文字 (STT) / 字幕讀取 (OCR)", WrapStyle);
            EditorGUILayout.HelpBox(
                "管理 stream-watch 觀影工具鏈的影音辨識層：whisper 語音轉文字的安裝與設定，" +
                "以及字幕 OCR (RapidOCR) 的參數。計算全在 media_admin.py 與既有工具鏈 " +
                "(audio_transcribe.py / screenstream daemon / montage)，本頁為薄 UI。" +
                "錄影本體的開關請至 ScreenStream 錄影頁 — 本頁只管『辨識』欄位。",
                MessageType.Info);

            if (m_Busy)
                EditorGUILayout.HelpBox($"⏳ 執行中：{m_BusyLabel}…（python 於背景執行，完成後自動更新）", MessageType.Warning);

            GUILayout.Space(6);
            DrawStatusPanel();
            GUILayout.Space(6);
            DrawInstallPanel();
            GUILayout.Space(6);
            DrawModelPanel();
            GUILayout.Space(6);
            DrawSttSettingsPanel();
            GUILayout.Space(6);
            DrawOcrSettingsPanel();
            GUILayout.Space(6);
            DrawTestPanel();
            GUILayout.Space(6);
            DrawOutputPanel();
        }

        // 區塊 1：環境 / 依賴 / config 狀態 — UCL_GUILayout.Toggle 折疊 (Tim 2026-07-26 要求；狀態文長，摺起省版面)
        void DrawStatusPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    // ▼/► 折疊鈕 — 開合狀態存 m_Dic (頁面生命週期內記住)；預設展開 (首開頁要看得到健檢)
                    aShow = UCL_GUILayout.Toggle(m_Dic, "StatusFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>1. 環境與依賴狀態</b>", WrapStyle);
                    GUILayout.FlexibleSpace();
                }
                if (aShow)
                    GUILayout.Label(string.IsNullOrEmpty(m_StatusText) ? "(無)" : m_StatusText, WrapStyle);
            }
        }

        // 區塊 2：插件管理 — 下拉選插件 → 只顯示該插件的動作（安裝 / 解除安裝 / 切換後端）
        // 物理意義：清單與動作全部由 media_admin.py 的 PLUGINS 註冊表生成 (list-plugins)。
        //          新增插件只改 python 那張表，本頁一行都不用動 —— 這是 Tim 2026-08-11 拍板的理由：
        //          插件會越來越多，繼續一顆一顆加按鈕會讓這一區無限膨脹。
        void DrawInstallPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>2. 插件管理</b>（pip --user 落 user-site；torch 較大，請耐心等）", WrapStyle);
                if (m_Plugins == null || m_Plugins.Length == 0)
                {
                    EditorGUILayout.HelpBox("插件清單尚未載入 — 按上方「🔄 重新整理狀態」。", MessageType.None);
                    return;
                }

                // 下拉選單：顯示名前綴安裝狀態，讓「這個裝了沒」不必展開就看得到
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("插件", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                    m_PluginIdx = Mathf.Clamp(m_PluginIdx, 0, m_Plugins.Length - 1);
                    m_PluginIdx = UCL_GUILayout.PopupSearchCache(m_PluginIdx, PluginMenuNames(), m_Dic, "m_PluginIdx");
                }

                var p = m_Plugins[Mathf.Clamp(m_PluginIdx, 0, m_Plugins.Length - 1)];
                GUILayout.Label(p.Desc, WrapStyle);
                GUILayout.Label(p.ProbeSummary, WrapStyle);
                GUILayout.Space(4);

                using (new EditorGUI.DisabledScope(m_Busy))
                {
                    foreach (var a in p.Actions)
                    {
                        if (GUILayout.Button(a.Label, UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                            InvokePluginAction(p, a);
                        if (!string.IsNullOrEmpty(a.Hint))
                            GUILayout.Label($"　↳ {a.Hint}", WrapStyle);
                    }
                }
            }
        }

        // 區塊職責：模型權重管理 — 下載 / 刪除 / 清殘骸。
        // 物理意義：權重不在本機就沒得跑，而它們是 GB 級的東西 —— 需要看得到「佔多少、在哪、完不完整」。
        // ⚠ 首次下載刻意做在**這裡**而不是讓即時 worker 邊跑邊下載：
        //   🩸 2026-08-16 實測 worker 首跑下載 medium 期間沒有產物，被 supervisor 的 90s
        //   停滯偵測連砍 3 次，下載永遠完不成（且留下 1383MB 殘骸）。管理頁沒有那個門檻。
        void DrawModelPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>3. 模型權重</b>（GB 級；下載請在此做，不要靠 worker 首跑邊跑邊下）", WrapStyle);
                if (m_Models == null || m_Models.Length == 0)
                {
                    EditorGUILayout.HelpBox("模型清單尚未載入 — 按上方「🔄 重新整理狀態」。", MessageType.None);
                    return;
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("模型", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                    m_ModelIdx = Mathf.Clamp(m_ModelIdx, 0, m_Models.Length - 1);
                    m_ModelIdx = UCL_GUILayout.PopupSearchCache(m_ModelIdx, ModelMenuNames(), m_Dic, "m_ModelIdx");
                }
                var m = m_Models[Mathf.Clamp(m_ModelIdx, 0, m_Models.Length - 1)];
                GUILayout.Label(m.Installed ? $"已安裝 {m.Mb} MB" : "未安裝", WrapStyle);
                GUILayout.Label(m.PathStr, WrapStyle);
                if (m.PartialMb > 0f)
                    GUILayout.Label($"⚠ 另有 <b>{m.PartialMb} MB</b> 未完成碎片（下載被中斷留下的，"
                                    + "不算模型的一部分、可安全清掉）", WrapStyle);
                GUILayout.Space(4);
                using (new EditorGUI.DisabledScope(m_Busy))
                {
                    if (!m.Installed && GUILayout.Button("📥 下載", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp($"下載模型 {m.Id}", $"model --id {m.Id} --action download", 3600000);
                    if (m.PartialMb > 0f && GUILayout.Button("🧹 清掉未完成碎片", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp($"清碎片 {m.Id}", $"model --id {m.Id} --action clean-partial", 120000);
                    if (m.Installed && GUILayout.Button("🗑 刪除權重", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                    {
                        // 不可逆且要重下 GB 級檔案 —— 把「你正要刪什麼、多大、在哪」原樣攤開，不用泛稱
                        bool ok = EditorUtility.DisplayDialog(
                            "確認刪除模型權重",
                            $"模型：{m.Id}\n大小：{m.Mb} MB\n路徑：{m.PathStr}\n\n"
                            + "刪掉要重新下載（GB 級）。若 STT 正在跑會被拒絕，請先停錄影。\n\n確定刪除？",
                            "確定刪除", "取消");
                        if (ok) RunOp($"刪除模型 {m.Id}", $"model --id {m.Id} --action delete", 300000);
                    }
                }
            }
        }

        string[] ModelMenuNames()
        {
            var names = new string[m_Models.Length];
            for (int i = 0; i < m_Models.Length; i++)
            {
                var m = m_Models[i];
                string mark = m.Installed ? "✅" : (m.PartialMb > 0f ? "⚠" : "⬜");
                names[i] = $"{mark} {m.Id}" + (m.Installed ? $"（{m.Mb} MB）" : "");
            }
            return names;
        }

        void ParseModelsJson(string iStdout)
        {
            try
            {
                var d = JsonData.ParseJson(iStdout);
                var arr = d?.Get("models");
                if (arr == null || !arr.IsArray) return;
                var list = new System.Collections.Generic.List<ModelInfo>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var e = arr[i];
                    if (e == null) continue;
                    string id = e.GetString("id", "");
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new ModelInfo
                    {
                        Id = id,
                        Backend = e.GetString("backend", ""),
                        SizeName = e.GetString("size_name", ""),
                        PathStr = e.GetString("path", ""),
                        Mb = e.GetFloat("mb", 0f),
                        PartialMb = e.GetFloat("partial_mb", 0f),
                        Installed = e.GetBool("installed", false),
                    });
                }
                m_Models = list.ToArray();
            }
            catch (System.Exception ex)
            {
                // 解析失敗不靜默 —— 面板空著跟「沒有模型」長得一樣
                Debug.LogWarning($"[MediaAdmin] list-models 解析失敗：{ex.Message}");
            }
        }

        // 下拉顯示名 — 「✅/⬜ 插件名」；狀態直接寫進選項字串，收合時也看得到
        string[] PluginMenuNames()
        {
            var names = new string[m_Plugins.Length];
            for (int i = 0; i < m_Plugins.Length; i++)
                names[i] = (m_Plugins[i].Installed ? "✅ " : "⬜ ") + m_Plugins[i].Name;
            return names;
        }

        // 執行插件動作 — danger 動作先跳確認框
        // 物理意義：解除安裝/降級不可逆（重裝要重新下載數 GB），而按鈕誤觸的成本全落在人身上，
        //          所以把「你正要卸什麼」原樣攤在對話框裡，不用泛稱。
        void InvokePluginAction(PluginInfo p, PluginAction a)
        {
            if (a.Danger)
            {
                bool ok = EditorUtility.DisplayDialog(
                    "確認執行",
                    $"插件：{p.Name}\n動作：{a.Label}\n\n{a.Hint}\n\n這個動作不可逆，確定執行？",
                    "確定執行", "取消");
                if (!ok) return;
            }
            RunOp($"{p.Name} — {a.Label}", $"plugin --id {p.Id} --action {a.Id}", 1800000);
        }

        // 區塊 3：STT 設定 — 對齊 daemon config 白名單欄位
        void DrawSttSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>3. STT 語音轉文字設定</b>", WrapStyle);
                EditorGUILayout.HelpBox(
                    "寫入主專案 AgentCommands/_screenstream/_config.json（daemon 每 loop reload）。" +
                    "stt_model / stt_prompt 改動需 toggle STT 重起 worker 才吃到（daemon 會 log 警告不靜默）。",
                    MessageType.None);
                if (!string.IsNullOrEmpty(m_ConfigPath))
                    GUILayout.Label($"config: {m_ConfigPath}", WrapStyle);

                m_SttSetting = EditorGUILayout.ToggleLeft(" 🎙 錄影時同步啟動語音轉錄 (stt_setting)", m_SttSetting);

                // whisper 模型下拉 (固定選項，避免手滑打錯模型名)
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("模型", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    int idx = System.Array.IndexOf(ModelOptions, m_SttModel);
                    if (idx < 0) idx = 2;   // 不在清單 (e.g. 手改 config) → 顯示預設 small，套用時才覆寫
                    idx = UCL_GUILayout.PopupSearchCache(idx, ModelOptions, m_Dic, "m_SttModel");
                    m_SttModel = ModelOptions[Mathf.Clamp(idx, 0, ModelOptions.Length - 1)];
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("語言 hint", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_SttLang = GUILayout.TextField(m_SttLang, UCL_GUIStyle.TextFieldStyle);
                    GUILayout.Label("(ja/zh/en；留空=auto)", UCL_GUIStyle.LabelStyle);
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("分段秒數", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    // IntField 沿用 EditorGUILayout（管理頁內部工具，數值輸入以正確為先）
                    m_SttChunkSec = Mathf.Clamp(EditorGUILayout.IntField(m_SttChunkSec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))), 5, 120);
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("詞彙偏置", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_SttPrompt = GUILayout.TextField(m_SttPrompt, UCL_GUIStyle.TextFieldStyle);
                }
                GUILayout.Label("↑ initial_prompt：登場人物名的「原文字形」(日番用片假名)，壓 ASR 人名咬字。", WrapStyle);

                using (new EditorGUI.DisabledScope(m_Busy || !m_ConfigLoaded))
                {
                    if (GUILayout.Button("💾 套用 STT 設定", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        ApplyConfig(sttOnly: true);
                }
            }
        }

        // 區塊 4：OCR 字幕讀取設定 — 本頁的整合第二塊（Tim 拍板：字幕讀取之後也歸本頁）
        void DrawOcrSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>4. OCR 字幕讀取設定</b>（RapidOCR — montage --ocr / daemon OCR worker 共用）", WrapStyle);
                m_OcrEnabled = EditorGUILayout.ToggleLeft(" 🔡 啟用字幕 OCR (ocr_enabled)", m_OcrEnabled);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("worker 數", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_OcrWorkers = Mathf.Clamp(EditorGUILayout.IntField(m_OcrWorkers, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))), 1, 8);
                    GUILayout.FlexibleSpace();
                }
                // 字幕帶裁切三參數 — slider 直觀調 (字幕位置隨播放器版面跑，見 stream-watch 字幕帶自校準教訓)
                // 底部原點語意 (Tim 2026-07-28): 起始 y = 帶底邊離畫面下緣距離 (0=貼底), 高度往上長
                m_OcrYBottomPct = EditorGUILayout.Slider("字幕帶起始 y (0=畫面下方)", m_OcrYBottomPct, 0f, 1f);
                m_OcrHPct = EditorGUILayout.Slider("字幕帶高度 (從 y 往上長)", m_OcrHPct, 0.02f, 0.5f);
                m_OcrMinConf = EditorGUILayout.Slider("最低信度過濾", m_OcrMinConf, 0f, 1f);
                GUILayout.Label("ⓘ 額外字幕判定區域 (字幕偶爾跑上方的影片) 請至「螢幕直播錄影」頁設定。", WrapStyle);

                // 視覺化字幕帶位置 (Tim 2026-07-27) — 灰框=螢幕(16:9)、橘框=OCR 讀取的字幕帶
                DrawOcrBandPreview();

                using (new EditorGUI.DisabledScope(m_Busy || !m_ConfigLoaded))
                {
                    if (GUILayout.Button("💾 套用 OCR 設定", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        ApplyConfig(sttOnly: false);
                }
            }
        }

        // 當前畫面預覽底圖 (Tim 2026-07-27) — 墊在字幕帶預覽底下方便對齊字幕
        Texture2D m_FramePreview;
        long m_FramePreviewMtime = 0;
        double m_LastFramePreviewLoad = -1;

        // 讀 _screenstream/_latest.jpg → Texture (節流 ~1s, mtime 沒變不重載); 仿 UCL_ScreenStreamPage.ReloadPreview
        void ReloadFramePreview()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - m_LastFramePreviewLoad < 1.0) return;
            m_LastFramePreviewLoad = now;
            try
            {
                string path = Path.Combine(UCL_RepoPath.RepoRoot, "AgentCommands", "_screenstream", "_latest.jpg");
                if (!File.Exists(path)) return;
                long mtime = new FileInfo(path).LastWriteTime.Ticks;
                if (mtime == m_FramePreviewMtime && m_FramePreview != null) return;
                byte[] bytes = File.ReadAllBytes(path);
                if (m_FramePreview == null)
                {
                    m_FramePreview = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    m_FramePreview.hideFlags = HideFlags.HideAndDontSave;
                }
                m_FramePreview.LoadImage(bytes);
                m_FramePreviewMtime = mtime;
            }
            catch { /* fail-soft: 沒圖就回退灰底 */ }
        }

        // 字幕帶視覺化 (Tim 2026-07-27) — 有當前畫面則墊底圖對齊字幕、無則灰框；橘半透明=OCR 讀取的字幕帶。
        // 物理意義：y_pct/h_pct 是抽象比例，疊在真畫面上讓人直接把橘框對準字幕；沒圖時退回純比例示意。
        void DrawOcrBandPreview()
        {
            ReloadFramePreview();
            bool hasImg = m_FramePreview != null && m_FramePreview.width > 4;
            GUILayout.Label(hasImg
                ? "字幕範圍預覽（底圖＝當前畫面，橘框＝OCR 字幕帶 — 把橘框對準字幕）:"
                : "字幕範圍預覽（灰框＝螢幕，橘色＝OCR 字幕帶）:", WrapStyle);
            float aspect = hasImg ? (float)m_FramePreview.width / Mathf.Max(1, m_FramePreview.height) : (16f / 9f);
            float vizW = hasImg ? 360f : 240f;
            float vizH = vizW / Mathf.Max(0.1f, aspect);
            Rect box = GUILayoutUtility.GetRect(vizW, vizH, GUILayout.ExpandWidth(false));
            // 底圖 (當前畫面, 滿框對齊) 或灰底
            if (hasImg) GUI.DrawTexture(box, m_FramePreview, ScaleMode.StretchToFill);
            else EditorGUI.DrawRect(box, new Color(0.13f, 0.13f, 0.16f));
            DrawRectBorder(box, new Color(0.65f, 0.65f, 0.72f), 1.5f);
            // 字幕帶 — 底部原點轉 GUI top-down 座標: 帶頂 = box.yMax - (y_bottom + h) * H, 帶底 = box.yMax - y_bottom * H
            float yB = Mathf.Clamp01(m_OcrYBottomPct);
            float h = Mathf.Clamp(m_OcrHPct, 0f, 1f);
            float bandTop = box.yMax - Mathf.Min(1f, yB + h) * box.height;
            float bandBottom = box.yMax - yB * box.height;
            bool offScreen = yB >= 0.999f || bandBottom - bandTop <= 0.5f;
            if (!offScreen)
            {
                var bandRect = new Rect(box.x, bandTop, box.width, bandBottom - bandTop);
                EditorGUI.DrawRect(bandRect, new Color(1f, 0.6f, 0.1f, 0.55f));
                DrawRectBorder(bandRect, new Color(1f, 0.7f, 0.2f), 1f);
            }
            int pctLo = Mathf.RoundToInt(yB * 100f);
            int pctHi = Mathf.RoundToInt(Mathf.Min(1f, yB + h) * 100f);
            if (offScreen)
                GUILayout.Label($"⚠ 起始 y={m_OcrYBottomPct:0.##} 太高，字幕帶超出畫面頂 → OCR 只掃到空白。0=畫面下方，字幕通常貼底，把「起始 y」調回 0 附近。", WrapStyle);
            else
                GUILayout.Label($"字幕帶：距畫面下緣 {pctLo}% ~ {pctHi}%（滿寬）。字幕若沒被橘框罩到，調「起始 y」對準它。", WrapStyle);
        }

        // 畫矩形邊框（四條 t 粗細的線）
        static void DrawRectBorder(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        // 區塊 5：STT 試錄 — 委派專案端 audio_transcribe.py live N（擷取為 wall-clock 阻塞）
        void DrawTestPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>5. STT 試錄</b>（抓最近 N 秒系統音訊 → whisper 轉錄，驗證整條鏈通不通）", WrapStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("秒數", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_TestSec = Mathf.Clamp(EditorGUILayout.IntField(m_TestSec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))), 3, 30);
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUI.DisabledScope(m_Busy))
                {
                    if (GUILayout.Button($"🎙 試錄 {m_TestSec} 秒（model={m_SttModel}, lang={(string.IsNullOrEmpty(m_SttLang) ? "auto" : m_SttLang)}）",
                                         UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                    {
                        string lang = string.IsNullOrEmpty(m_SttLang) ? "auto" : m_SttLang;
                        // 擷取 N 秒 + 模型載入/轉錄 → 給寬鬆 timeout (首跑載模型可能數十秒)
                        RunOp("STT 試錄", $"test-stt --sec {m_TestSec} --model {m_SttModel} --lang {lang}", 300000);
                    }
                }
            }
        }

        // 區塊 6：輸出
        void DrawOutputPanel()
        {
            if (string.IsNullOrEmpty(m_LastOutput)) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("<b>📋 最近操作結果</b>", WrapStyle);
                if (GUILayout.Button("Copy", UCL_GUIStyle.ButtonStyle))
                {
                    EditorGUIUtility.systemCopyBuffer = m_LastOutput;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Box(m_LastOutput, UCL_GUIStyle.BoxStyle);
            }
        }

        // ===========================================================
        // 區塊：套用設定 — 組 set-config k=v 參數串 (float 用 InvariantCulture 防逗號小數點)
        // 物理意義：一次寫回一組欄位；python 端白名單 + 型別再驗一次 (雙保險)。
        // ===========================================================
        void ApplyConfig(bool sttOnly)
        {
            var sb = new System.Text.StringBuilder("set-config");
            if (sttOnly)
            {
                sb.Append($" stt_setting={(m_SttSetting ? "true" : "false")}");
                sb.Append($" stt_model={m_SttModel}");
                sb.Append($" stt_lang={m_SttLang}");
                sb.Append($" stt_chunk_sec={m_SttChunkSec}");
                // prompt 可能含空白/中日文 → 整個 pair 加引號 (python 端收單一 argv)
                sb.Append($" {UCL_MediaAdminRunner.QuoteArg($"stt_prompt={m_SttPrompt}")}");
            }
            else
            {
                sb.Append($" ocr_enabled={(m_OcrEnabled ? "true" : "false")}");
                sb.Append($" ocr_workers={m_OcrWorkers}");
                // 底部原點語意 (2026-07-28) — 舊頂部原點 key ocr_y_pct 已退役, set-config 會拒收
                sb.Append($" ocr_y_bottom_pct={m_OcrYBottomPct.ToString("0.###", CultureInfo.InvariantCulture)}");
                sb.Append($" ocr_h_pct={m_OcrHPct.ToString("0.###", CultureInfo.InvariantCulture)}");
                sb.Append($" ocr_min_conf={m_OcrMinConf.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            RunOp(sttOnly ? "套用 STT 設定" : "套用 OCR 設定", sb.ToString(), 30000);
        }

        // ===========================================================
        // 區塊：get-config JSON 回填 — python 純 JSON → 欄位快取
        // 物理意義：頁面欄位以 config 現值為初值，避免「套用」時拿預設值蓋掉 Tim 手調的參數。
        // ===========================================================
        void ParseConfigJson(string json)
        {
            try
            {
                var root = JsonData.ParseJson(json);
                if (root == null || !root.IsObject) return;
                m_ConfigPath = root.GetString("config_path", "");
                if (!root.Contains("fields")) return;
                var f = root["fields"];
                if (f == null || !f.IsObject) return;
                // 一律走安全具名 getter (implicit 轉換已 [Obsolete]、型別不符會 throw)；缺鍵保留現值
                m_SttSetting = f.GetBool("stt_setting", m_SttSetting);
                m_SttModel = f.GetString("stt_model", m_SttModel);
                m_SttLang = f.GetString("stt_lang", m_SttLang);
                m_SttChunkSec = f.GetInt("stt_chunk_sec", m_SttChunkSec);
                m_SttPrompt = f.GetString("stt_prompt", m_SttPrompt);
                m_OcrEnabled = f.GetBool("ocr_enabled", m_OcrEnabled);
                m_OcrWorkers = f.GetInt("ocr_workers", m_OcrWorkers);
                // 底部原點 key — 舊 config 只有 ocr_y_pct 時 get-config 端已代為換算成 ocr_y_bottom_pct
                m_OcrYBottomPct = f.GetFloat("ocr_y_bottom_pct", m_OcrYBottomPct);
                m_OcrHPct = f.GetFloat("ocr_h_pct", m_OcrHPct);
                m_OcrMinConf = f.GetFloat("ocr_min_conf", m_OcrMinConf);
                m_ConfigLoaded = true;   // 回填成功才解鎖「套用」
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MediaAdminPage] 解析 get-config JSON 失敗: {e.Message}");
                Debug.LogException(e);
            }
        }

        // ===========================================================
        // 區塊：list-plugins JSON 回填 — python 註冊表 → 頁面下拉選單
        // 物理意義：頁面完全不知道有哪些插件，全部問 python；解析失敗保留舊清單而不是清空
        //          （清空會讓整區消失，看起來像「沒有插件」而不是「讀取失敗」）。
        // ===========================================================
        void ParsePluginsJson(string json)
        {
            try
            {
                var root = JsonData.ParseJson(json);
                var arr = root?.Get("plugins");
                if (arr == null || !arr.IsArray) return;
                var list = new System.Collections.Generic.List<PluginInfo>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var d = arr[i];
                    if (d == null) continue;
                    var info = new PluginInfo
                    {
                        Id = d.GetString("id", ""),
                        Name = d.GetString("name", "(未命名)"),
                        Desc = d.GetString("desc", ""),
                        Installed = d.GetBool("installed", false),
                    };
                    if (string.IsNullOrEmpty(info.Id)) continue;
                    // 依賴探測摘要 — 讓「哪一個沒裝」看得到，不是只給一個總結的 ✅/⬜
                    var probes = d.Get("probes");
                    if (probes != null && probes.IsArray)
                    {
                        var sb = new System.Text.StringBuilder("依賴：");
                        for (int j = 0; j < probes.Count; j++)
                        {
                            var pr = probes[j];
                            if (pr == null) continue;
                            if (j > 0) sb.Append("　");
                            sb.Append(pr.GetBool("ok", false) ? "✅ " : "❌ ");
                            sb.Append(pr.GetString("module", "?"));
                            string ver = pr.GetString("info", "");
                            if (pr.GetBool("ok", false) && !string.IsNullOrEmpty(ver) && ver != "?")
                                sb.Append($" {ver}");
                        }
                        info.ProbeSummary = sb.ToString();
                    }
                    var acts = d.Get("actions");
                    if (acts != null && acts.IsArray)
                    {
                        var al = new System.Collections.Generic.List<PluginAction>();
                        for (int j = 0; j < acts.Count; j++)
                        {
                            var ad = acts[j];
                            if (ad == null) continue;
                            string aid = ad.GetString("id", "");
                            if (string.IsNullOrEmpty(aid)) continue;
                            al.Add(new PluginAction
                            {
                                Id = aid,
                                Label = ad.GetString("label", aid),
                                Hint = ad.GetString("hint", ""),
                                Danger = ad.GetBool("danger", false),
                            });
                        }
                        info.Actions = al.ToArray();
                    }
                    list.Add(info);
                }
                if (list.Count == 0) return;   // 空清單視為讀取異常，保留舊值
                m_Plugins = list.ToArray();
                m_PluginIdx = Mathf.Clamp(m_PluginIdx, 0, m_Plugins.Length - 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MediaAdminPage] 解析 list-plugins JSON 失敗: {e.Message}");
                Debug.LogException(e);
            }
        }

        // ===========================================================
        // 區塊：async 執行 — 委派 UCL_MediaAdminRunner，完成後回主執行緒更新 UI + Repaint
        // 物理意義：重活 (install/試錄) 全在背景 python，Editor 不凍結；完成才刷 UI。
        // ===========================================================
        // 唯讀輕量 op — 可與其他操作並行，且不搶 m_Busy 旗標
        static bool IsReadOnlyOp(string label) =>
            label == "讀取設定" || label == "插件清單" || label == "模型清單" || label == "狀態刷新";

        void RunOp(string label, string argLine, int timeoutMs)
        {
            if (m_Busy && !IsReadOnlyOp(label)) return;
            if (!IsReadOnlyOp(label)) { m_Busy = true; m_BusyLabel = label; }
            var win = EditorWindow.focusedWindow;   // 捕捉當前宿主視窗，完成後主動 Repaint
            RunOpAsync(label, argLine, timeoutMs, win).Forget();
        }

        async UniTaskVoid RunOpAsync(string label, string argLine, int timeoutMs, EditorWindow win)
        {
            var r = await UCL_MediaAdminRunner.RunAsync(argLine, CancellationToken.None, timeoutMs);
            await UniTask.SwitchToMainThread();
            if (!IsReadOnlyOp(label))
            {
                m_Busy = false;
                m_BusyLabel = "";
                m_LastOutput = $"[{label}]\n{r.DisplayText}";
            }
            if (label == "狀態" || label == "狀態刷新") m_StatusText = r.DisplayText;
            if (label == "讀取設定") ParseConfigJson(r.Stdout ?? "");
            if (label == "插件清單") ParsePluginsJson(r.Stdout ?? "");
            if (label == "模型清單") ParseModelsJson(r.Stdout ?? "");
            // 套用設定成功後回讀一次，確保頁面顯示 = 檔案實況 (cross-layer 驗證，不只信自己送出的值)
            if (label.StartsWith("套用") && r.ExitCode == 0)
                RunOp("讀取設定", "get-config", 30000);
            // 插件動作跑完一律重新探測 — 「pip 說成功」不算數，import 得回來才算 (判準④)。
            // ⚠ 走「狀態刷新」這個唯讀 label 而非「狀態」：後者會覆寫 m_LastOutput，
            //   把使用者正要讀的 pip 安裝紀錄洗掉。
            if (argLine.StartsWith("plugin "))
            {
                RunOp("狀態刷新", "status", 60000);
                RunOp("插件清單", "list-plugins", 60000);
            }
            if (win != null) win.Repaint();
        }
    }
}
#endif
