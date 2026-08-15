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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UCL.Core.ATTR;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;
// 開播/停播廣播用 (酒保 NPC 身分 append 一則 tavern 訊息) —— 照本 repo 慣例用 alias, 不整包 using
using UCL_ChatTavernIO = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernIO;
using UCL_ChatMessage = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatMessage;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// ScreenStream daemon 控制 Page (UCL_Core 版) — 獨立隔離 concern, 提供強烈視覺警示 + 防誤觸。
    /// STT/OCR 的依賴安裝與細部設定請走「影音管理」頁 (UCL_MediaAdminPage, 本頁有跳轉鈕)。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ScreenStreamPage.md")]
    [RequiresConstantRepaint]
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
        // STT / OCR cache 目錄 (Tim 2026-07-27) — daemon 寫 stt/stt_<epoch>.json、ocr/frame_NNNN.json
        const string STT_DIR_RELATIVE = "AgentCommands/_screenstream/stt";
        const string OCR_DIR_RELATIVE = "AgentCommands/_screenstream/ocr";
        const int SttOcrPageSize = 10;

        // 區塊職責: config 快取 + 顯示用 state
        // 物理意義: page enter / 每 N 秒 reload 一次, 避免每 OnGUI 都 file IO
        bool m_Enabled = false;
        int m_Fps = 1;
        int m_MaxFrames = 600;
        // ==== T-SSREC-01 錄播模式（Tim 2026-08-01；規格見 ucl_core:Docs~/{lang}/Plan/Plan_ScreenStream_Recording_Mode.md）====
        // 物理意義：直播是 ring（會繞回去覆寫），錄播是流（不繞、不覆寫）。兩者**同時**進行（雙寫）——
        //          錄播不影響 ring，所以陪看的 montage 一行都不用改。
        // 數值影響：daemon 每張 frame 多寫一份進 _screenstream/recording/；停錄時 rename 成
        //          recordings/<名稱>/（同磁碟 O(1)，不搬檔）並重建空資料夾。
        bool m_Recording = false;
        string m_RecordingName = "";
        string m_Resolution = "1080p";
        // ==== T-AudioViz 開關（Tim 2026-08-01 要求搬進本頁）====
        // 物理意義：daemon 截圖前把系統音訊 FFT 疊成聲音圖譜 overlay 到畫面角落/底部 ——
        //          agent 沒有耳朵，那條圖譜是它判讀「現在有沒有聲音、左右聲道、爆音」的替代感官。
        // 血證：本專案（Bar）遷移後 audio_viz_enabled 是預設 false，而 EOV 是 true ——
        //      功能沒壞、code 也在，只是設定沒跟過來，於是「聲音圖譜消失了」。
        //      跟同日 routing asset 遺漏是同一族：**遷移漏的是設定，不是程式**。
        bool m_AudioViz = false;
        string m_AudioVizMode = "stereo_eq";
        string m_AudioVizPosition = "bottom-stretch";
        int m_Quality = 65;
        string m_Monitor = "primary";

        // 區塊職責: STT (語音轉錄) 設定 — 錄影時同步啟動 whisper 語音轉文字 (T-STT-PageToggle, Tim 2026-07-09)
        // 物理意義: m_SttSetting = Tim 的開關意圖 (要不要語音轉錄, 持久化在 config.stt_setting);
        //          config.stt_enabled (daemon 讀的實效值) = m_Enabled && m_SttSetting —
        //          即「錄影中且開了 STT」才真啟動 worker, 停錄影自動停 STT (whisper GPU ~460MB 不空轉).
        // 數值影響: daemon SttCacheWorker lifecycle 綁 config.stt_enabled toggle, 每 loop reload 即生效;
        //          model/lang 改動需 toggle off→on 才重起 (開始/停止錄影天然觸發).
        bool m_SttSetting = false;
        // ==== 靜音幻覺防治門檻（Tim 2026-08-01 要求可在本頁調）====
        // 血證：首版把後過濾寫成 OR（no_speech_prob 高 **或** avg_logprob 低就丟），
        //      直播現場實測把五段真對白全砍光（那批 nsp=0.685 但 logp=-0.291，模型其實很有信心）。
        //      改回官方 AND 語意後正常。門檻放上來是為了讓這種事**下次能當場調出來**，不用改 code。
        float m_SttRmsGate = 0.005f;      // 低於此 RMS 的 chunk 不送 whisper（0 = 停用前置閘）
        float m_SttNoSpeechMax = 0.6f;    // 對齊官方 no_speech_threshold
        float m_SttLogprobMin = -1.0f;    // 對齊官方 logprob_threshold
        string m_SttModel = "small";
        string m_SttLang = "";
        // 區塊職責: stt_prompt = whisper 人名詞彙偏置 (initial_prompt) —— **Page 可編輯欄位** (Tim 2026-08-11)。
        // 沿革與取捨 (改了 T-STT-StaleFix 的一半, 記在這裡免得下次有人以為是退化):
        //   ① 2026-07-20 T-STT-StaleFix: Page 只顯示殘留 + 手動清除, 且**開始錄影自動清空** ——
        //      當時 stt_prompt 的唯一寫入者是陪看 skill, 血證是「換片後殘留讓 whisper 幻聽出舊片人名」。
        //      在「Tim 看不到也改不到」的前提下, 自動清空是對的: 看不見的殘留只能靠自動清。
        //   ② 2026-08-11 改為可編輯 + **開始錄影不再清空**: 因為自動清空與可編輯**直接互斥** ——
        //      填完 prompt 按「開始錄影」就被抹掉, 欄位等於裝飾。
        //      跨場殘留的防護改由「欄位常駐可見 + 提示每片一份」承擔: 殘留看得見就不是殘留, 是設定。
        //   ③ 為什麼值得做: 2026-08-11 陪看《もののけ姫》八輪實測, **專有名詞崩壞是最大失效類**
        //      (シシ神 七種寫法 / ナゴ→名古屋 / サン→カゴは3だ / 天王様→店長様 / もののけ→物抜け),
        //      而 initial_prompt 正是它的正解, 管線 (config→daemon→worker→transcribe) 早就通到底, 只差內容。
        // 物理意義: whisper 的 initial_prompt 是**偏置不是約束** —— 提高這些詞的機率, 不保證命中。
        // 數值影響: 上限約 224 token, 塞太多會把前面擠掉 → 只放**當前這一部片**的專有名詞, 不要寫句子;
        //          換片要改 (這是它跟 stream_title 同一種「每片一份」的欄位)。
        //          daemon 偵測 prompt 變更會自動重起 worker → 存檔即生效, 不必停/啟錄影。
        string m_SttPrompt = "";
        // 片名/描述 (Tim 2026-07-27): 可空; 有填則 daemon 開播的酒館廣播附加「📺 本場節目: <此文字>」。
        //   持久化在 config.stream_title, 不自動清空 (欄位在頁面上看得見, 換片自行改/清)。
        string m_StreamTitle = "";

        // 區塊職責: OCR 字幕讀取設定 (Tim 2026-07-28: 自 UCL_MediaAdminPage 整合進本頁)
        // 物理意義: 字幕帶座標為「底部原點」語意 — y_bottom=0 表示帶底貼畫面下緣, 高度從底邊往上長
        //          (例: y=0, h=0.1 → 覆蓋畫面最下方 10%)。額外判定區域給「字幕偶爾跑到上方」的影片用 (可空)。
        // 數值影響: 寫 config.ocr_y_bottom_pct / ocr_h_pct / ocr_extra_regions;
        //          daemon 每 loop reload, band 改動觸發 T-OCR-AutoRestart 自動重起 pool 套用。
        bool m_OcrEnabled = false;
        int m_OcrWorkers = 2;
        float m_OcrMinConf = 0.5f;
        // 主字幕帶 — 與額外區域共用 OcrBand 型別 (Tim 2026-08-04: 抽成同一個 class)
        readonly OcrBand m_OcrBand = new OcrBand(0f, 0.12f);
        // 額外字幕判定區域 — 同型別, 同 UI, 同序列化
        readonly List<OcrBand> m_OcrExtraRegions = new List<OcrBand>();
        bool m_ShowOcrExtraRegions = false;   // 可折疊 List 開合狀態

        // 區塊職責: 一條 OCR 判定帶的幾何 — **主字幕帶與額外區域共用同一型別**
        //          (原本主帶是兩個散落的 float、額外區域是 Vector2 借位當載體:
        //           x 存 y_bottom、y 存 h —— 欄名與內容不同義, 加水平欄位後只會更難讀)。
        // 物理意義: 垂直 = 底部原點 (YBottomPct = 帶底離畫面下緣, HPct 從底邊往上長);
        //          水平 = **中心 + 寬度** (XCenterPct 0.5 = 畫面正中, WPct 1 = 滿寬)。
        //          用「中心+寬」而不是「左緣+寬」: 字幕本來就對齊畫面中央, 調寬時人要的是
        //          「往中間收」, 而左緣制會讓收窄同時把帶往右推, 每次都得再補調左緣。
        // 數值影響: 對應 config 的 y_bottom_pct / h_pct / x_center_pct / w_pct。
        //          **舊 config 沒有水平兩欄 → 落回 0.5 / 1.0 = 滿寬 = 改動前的行為**,
        //          所以舊設定檔讀進來的 OCR 結果一格都不會變。
        public class OcrBand
        {
            public float YBottomPct;
            public float HPct;
            public float XCenterPct = DEFAULT_X_CENTER_PCT;
            public float WPct = DEFAULT_W_PCT;

            public OcrBand() { }
            public OcrBand(float iYBottom, float iH) { YBottomPct = iYBottom; HPct = iH; }
            public OcrBand(float iYBottom, float iH, float iXCenter, float iW)
            { YBottomPct = iYBottom; HPct = iH; XCenterPct = iXCenter; WPct = iW; }

            /// <summary>水平範圍 → [0,1] 的左右緣 (clamp 進畫面; 寬度不足時回 false)。</summary>
            public bool TryHorizontal(out float oLeft, out float oRight)
            {
                float w = Mathf.Clamp(WPct, 0f, 1f);
                float xc = Mathf.Clamp01(XCenterPct);
                oLeft = Mathf.Clamp01(xc - w * 0.5f);
                oRight = Mathf.Clamp01(xc + w * 0.5f);
                return (oRight - oLeft) > 0.0001f;
            }
        }

        const float DEFAULT_X_CENTER_PCT = 0.5f;   // 畫面正中
        const float DEFAULT_W_PCT = 1f;            // 滿寬 = 加這功能之前的固定行為

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
        // stt_prompt 進 3-way merge (Tim 2026-08-11): 它現在有**兩個寫入者** (Tim 手打 / 陪看 skill),
        // 而 MergeField 的語意剛好解這題 —— Tim 沒動過就吃 skill 寫的新值, 正在編輯就不被蓋掉。
        string m_BaseSttPrompt = "";
        string m_BaseStreamTitle = "";
        bool m_BaseOcrEnabled = false;
        int m_BaseOcrWorkers = 2;
        float m_BaseOcrYBottomPct = 0f;
        float m_BaseOcrHPct = 0.12f;
        float m_BaseOcrXCenterPct = DEFAULT_X_CENTER_PCT;
        float m_BaseOcrWPct = DEFAULT_W_PCT;
        float m_BaseOcrMinConf = 0.5f;
        // 額外區域的 baseline 用序列化字串比對 (list 整體當單一欄位做 3-way merge)
        string m_BaseOcrExtraRegions = "";
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
        // ── STT / OCR 顯示 (Tim 2026-07-27)：當前最新 + 可展開分頁歷史 (每頁 10) ──
        // 增量快取 (2026-07-27 修): 檔路徑 → 該檔 entries; watermark = 已 parse 的最大 mtime ticks。
        // 修兩隻 bug: ① 「最新 OCR」舊版按檔名倒掃 — OCR 是 ring buffer 就地覆寫 (frame_NNNN.json),
        //   檔名最大 ≠ 資料最新, 顯示會落後最多一整圈 (~max_frames 秒); mtime/epoch 才是新舊 ground truth。
        //   ② 歷史展開後凍結不追新 — 改為每次 reload tick 增量掃, 有變動即標 dirty 讓第 1 頁自動更新。
        // 成本: 每 2s stat 全目錄 (~2400 檔, 毫秒級); parse 只發生在 mtime 超過 watermark 的檔
        //   (穩態每 tick 0~2 檔; 首次 tick 全量 parse 一次)。
        string m_LatestSttText = "", m_LatestSttTime = "";
        string m_LatestOcrText = "", m_LatestOcrTime = "";
        double m_LatestSttEpoch = 0, m_LatestOcrEpoch = 0;
        bool m_ShowSttHistory = false, m_ShowOcrHistory = false;
        int m_SttHistPage = 0, m_OcrHistPage = 0;
        readonly Dictionary<string, List<(double epoch, string text)>> m_SttFileEntries = new();
        readonly Dictionary<string, List<(double epoch, string text)>> m_OcrFileEntries = new();
        long m_SttWatermark = 0, m_OcrWatermark = 0;
        bool m_SttHistDirty = false, m_OcrHistDirty = false;   // 快取有變動、顯示列表待重建
        List<(double epoch, string text)> m_SttHistory;   // 顯示用排序列表 (null = 尚未建)
        List<(double epoch, string text)> m_OcrHistory;
        // daemon STT worker 寫進 stt/_status.json 的 error 欄 — 「禁靜默失敗」的 UI 端出口
        // (2026-07-27 靜默殭屍事故: worker 內部有記錯誤但無人能讀 → 加此顯示通道)
        string m_SttStatusError = "";
        long m_SttStatusMtime = 0;
        // config 內 daemon 實效開關 (影音管理頁設定) — 給 staleness 警示判斷「該不該有新資料」
        bool m_SttEnabledCfg = false, m_OcrEnabledCfg = false;
        GUIStyle m_SubWrapStyle;
        GUIStyle SubWrapStyle
        {
            get
            {
                if (m_SubWrapStyle == null)
                    m_SubWrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return m_SubWrapStyle;
            }
        }

        bool m_ConfigLoaded = false;
        double m_LastReloadTime = -1.0;
        const double RELOAD_INTERVAL_SEC = 2.0;

        // 區塊折疊開合狀態 (Tim 2026-07-28: 錄影設定/STT/OCR 區塊可折疊) — 頁面生命週期內記住, 預設收合
        private UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();

        // Resolution dropdown options
        static readonly string[] s_ResolutionOptions = { "native", "2k", "1440p", "1080p", "720p", "480p" };
        static readonly string[] s_AudioVizModeOptions = { "stereo_eq", "spectrogram" };
        static readonly string[] s_AudioVizPosOptions = { "bottom-stretch", "bottom-right", "bottom-left", "top-right", "top-left" };

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
            // 螢幕清單預熱 (Tim 2026-07-28): daemon 存活已綁錄影開關, 未錄影時 _monitors.json 可能缺/舊
            // → 開啟本頁時 one-shot 枚舉補寫快取 (fire-and-forget ~1s, 2s reload tick 自動撿到);
            //   daemon 運行中則它啟動時已寫過, 不必重複 spawn。熱插拔螢幕另有「🔄」鈕手動刷新。
            if (!m_DaemonAlive)
                AgentCommands.MediaAdmin.UCL_ScreenStreamDaemon.EnumerateMonitorsOneShot();
            m_FirstGuiDone = true;
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            // 區塊職責: 開始/停止錄影鈕 (Tim 2026-07-28: 自內文移到 Top Bar + 移除 5 秒二段確認, 點擊直接生效)
            // 物理意義: 開始錄影 = 同步保存當前所有 UI 設定 (含 stt_prompt 人名偏置 —— 2026-08-11 起
            //          該欄由 Page 擁有, 開播不再清空); daemon 每 loop reload config 反應 enabled toggle。
            // 數值影響: TopBarButtons 可能先於 ContentOnGUI 執行 → 先走 EnsureInitialReload 保 config 已載;
            //          config 未載入 (檔不存在) 時不畫錄影鈕, 走內文的「初始化」流程。
            EnsureInitialReload();
            if (m_ConfigLoaded)
            {
                var aOldBg = GUI.backgroundColor;
                if (m_Enabled)
                {
                    GUI.backgroundColor = new Color(0.8f, 0.3f, 0.3f);
                    if (GUILayout.Button("⏹ 停止錄影", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
                    {
                        m_Enabled = false;
                        PostStreamAnnounce(false);
                        SaveToDisk();
                        // 停止錄影 → daemon 同步收掉 (Tim 2026-07-28), 不等 manager 下一 tick (最多 5s)
                        AgentCommands.MediaAdmin.UCL_ScreenStreamDaemon.RequestSyncNow();
                    }
                }
                else
                {
                    GUI.backgroundColor = new Color(0.3f, 0.6f, 0.3f);
                    if (GUILayout.Button("▶ 開始錄影", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
                    {
                        m_Enabled = true;
                        PostStreamAnnounce(true);
                        // 2026-08-11 起**不再清空 stt_prompt** —— 它已是 Page 可編輯欄位, 清掉等於
                        // 每次開播都抹掉 Tim 剛填的人名偏置 (原因見 m_SttPrompt 宣告處沿革註解)。
                        SaveToDisk();
                        // 開始錄影 → daemon 立即 spawn (存活綁 config.enabled, 不再常駐 idle)
                        AgentCommands.MediaAdmin.UCL_ScreenStreamDaemon.RequestSyncNow();
                    }
                }
                GUI.backgroundColor = aOldBg;
            }
            // 開啟 _screenstream 資料夾 (Tim 2026-07-27) — 直接看 frames / stt / ocr cache / config
            if (GUILayout.Button("📂 開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
            {
                string dir = Path.Combine(GetRepoRoot(), "AgentCommands", "_screenstream");
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    UnityEditor.EditorUtility.RevealInFinder(dir);
                }
                catch (Exception e) { Debug.LogWarning($"[ScreenStreamPage] 開啟資料夾失敗: {e.Message}"); }
            }
            // 錄播成品資料夾 (Tim 2026-08-01) — recordings/<名稱>/ 各段一夾，內含 frames + ocr/ + stt/ + manifest
            if (GUILayout.Button("⏺ 開啟錄播資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
            {
                string recDir = Path.Combine(GetRepoRoot(), "AgentCommands", "_screenstream", "recordings");
                try
                {
                    if (!Directory.Exists(recDir)) Directory.CreateDirectory(recDir);
                    UnityEditor.EditorUtility.RevealInFinder(recDir);
                }
                catch (Exception e) { Debug.LogWarning($"[ScreenStreamPage] 開啟錄播資料夾失敗: {e.Message}"); }
            }
            // 入口按鈕 — 跳轉「影音管理」頁 (Tim 2026-07-26 要求): STT/OCR 依賴安裝、細部設定、試錄都在那頁
            if (GUILayout.Button("🎬 影音管理 (STT/OCR 安裝與設定)", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
            {
                UCL_MediaAdminPage.Create();
            }
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
        static float MergeField(float ui, ref float baseline, float disk)
        {
            // float 比對用小容差 — slider 量化 / json round-trip 的尾數差不該被誤判成「Tim 編輯中」
            float result = (Mathf.Abs(ui - baseline) < 0.0001f) ? disk : ui;
            baseline = disk;
            return result;
        }

        // ── OCR 額外區域 serialize/parse (config json ↔ List<OcrBand>) ──

        // 序列化成穩定字串 (InvariantCulture) — 3-way merge 的 baseline 比對鍵
        static string SerializeRegions(List<OcrBand> iRegions)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in iRegions)
            {
                // 四個欄位都要進 baseline 鍵 —— 少寫水平兩欄的話, 只改寬度/中心會被
                // 3-way merge 判成「Tim 沒動過」, 下一次 reload 就被磁碟值靜默蓋掉。
                sb.Append(r.YBottomPct.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.HPct.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.XCenterPct.ToString("0.####", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.WPct.ToString("0.####", CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }

        // 讀 config.ocr_extra_regions — 接受物件 {"y_bottom_pct","h_pct"[,"x_center_pct","w_pct"]}
        // 或陣列 [y,h] / [y,h,xc,w] (對齊 python normalize_regions)。
        // 水平兩欄缺席一律落回 0.5 / 1.0 = 滿寬, 所以舊 config 讀進來行為不變。
        static List<OcrBand> ParseRegions(JsonData iData)
        {
            var list = new List<OcrBand>();
            if (iData == null || !iData.IsArray) return list;
            for (int i = 0; i < iData.Count; i++)
            {
                JsonData e = iData[i];
                if (e == null) continue;
                if (e.IsObject)
                    list.Add(new OcrBand(e.GetFloat("y_bottom_pct", 0f), e.GetFloat("h_pct", 0f),
                                         e.GetFloat("x_center_pct", DEFAULT_X_CENTER_PCT),
                                         e.GetFloat("w_pct", DEFAULT_W_PCT)));
                else if (e.IsArray && e.Count >= 2)
                    list.Add(new OcrBand(e[0].GetFloat(0f), e[1].GetFloat(0f),
                                         e.Count >= 4 ? e[2].GetFloat(DEFAULT_X_CENTER_PCT) : DEFAULT_X_CENTER_PCT,
                                         e.Count >= 4 ? e[3].GetFloat(DEFAULT_W_PCT) : DEFAULT_W_PCT));
            }
            return list;
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
                        // 可編輯欄位 → 走 3-way merge (skill 寫入會生效, Tim 編輯中不被蓋)
                        m_SttPrompt = MergeField(m_SttPrompt, ref m_BaseSttPrompt, data.GetString("stt_prompt", ""));
                        // daemon 實效開關 (staleness 警示用): 沒開的功能本來就不會有新資料, 不該警告
                        m_SttEnabledCfg = data.GetBool("stt_enabled", false);
                        m_OcrEnabledCfg = data.GetBool("ocr_enabled", false);
                        // 可編輯欄位: 3-way merge — Tim 沒動過的欄位吃磁碟新值, 編輯中的保留
                        m_Fps = MergeField(m_Fps, ref m_BaseFps, data.GetInt("fps", 1));
                        m_MaxFrames = MergeField(m_MaxFrames, ref m_BaseMaxFrames, data.GetInt("max_frames", 600));
                        m_Recording = data.GetBool("recording_enabled", false);
                        m_RecordingName = data.GetString("recording_name", "");
                        m_Resolution = MergeField(m_Resolution, ref m_BaseResolution, data.GetString("resolution", "1080p"));
                        m_AudioViz = data.GetBool("audio_viz_enabled", false);
                        m_AudioVizMode = data.GetString("audio_viz_mode", "stereo_eq");
                        m_AudioVizPosition = data.GetString("audio_viz_position", "bottom-stretch");
                        m_Quality = MergeField(m_Quality, ref m_BaseQuality, data.GetInt("quality", 65));
                        m_Monitor = MergeField(m_Monitor, ref m_BaseMonitor, data.GetString("monitor", "primary"));
                        // STT: stt_setting 是 Page 意圖; 舊 config 只有 stt_enabled → fallback 讀它做遷移
                        m_SttSetting = MergeField(m_SttSetting, ref m_BaseSttSetting,
                            data.GetBool("stt_setting", data.GetBool("stt_enabled", false)));
                        m_SttModel = MergeField(m_SttModel, ref m_BaseSttModel, data.GetString("stt_model", "small"));
                        m_SttRmsGate = (float)data.GetDouble("stt_rms_gate", 0.005);
                        m_SttNoSpeechMax = (float)data.GetDouble("stt_no_speech_max", 0.6);
                        m_SttLogprobMin = (float)data.GetDouble("stt_logprob_min", -1.0);
                        m_SttLang = MergeField(m_SttLang, ref m_BaseSttLang, data.GetString("stt_lang", ""));
                        m_StreamTitle = MergeField(m_StreamTitle, ref m_BaseStreamTitle, data.GetString("stream_title", ""));
                        // OCR 欄位 (Tim 2026-07-28 整合自影音管理頁) — 底部原點語意, 同套 3-way merge
                        m_OcrEnabled = MergeField(m_OcrEnabled, ref m_BaseOcrEnabled, data.GetBool("ocr_enabled", false));
                        m_OcrWorkers = MergeField(m_OcrWorkers, ref m_BaseOcrWorkers, data.GetInt("ocr_workers", 2));
                        float diskOcrH = data.GetFloat("ocr_h_pct", 0.12f);
                        // 舊 config 遷移: 只有頂部原點 ocr_y_pct → 換算 y_bottom = 1 - y_top - h (帶底離下緣)
                        float diskOcrYBottom = data.Contains("ocr_y_bottom_pct")
                            ? data.GetFloat("ocr_y_bottom_pct", 0f)
                            : (data.Contains("ocr_y_pct")
                                ? Mathf.Clamp01(1f - data.GetFloat("ocr_y_pct", 0.78f) - diskOcrH)
                                : 0f);
                        m_OcrBand.YBottomPct = MergeField(m_OcrBand.YBottomPct, ref m_BaseOcrYBottomPct, diskOcrYBottom);
                        m_OcrBand.HPct = MergeField(m_OcrBand.HPct, ref m_BaseOcrHPct, diskOcrH);
                        m_OcrBand.XCenterPct = MergeField(m_OcrBand.XCenterPct, ref m_BaseOcrXCenterPct,
                            data.GetFloat("ocr_x_center_pct", DEFAULT_X_CENTER_PCT));
                        m_OcrBand.WPct = MergeField(m_OcrBand.WPct, ref m_BaseOcrWPct,
                            data.GetFloat("ocr_w_pct", DEFAULT_W_PCT));
                        m_OcrMinConf = MergeField(m_OcrMinConf, ref m_BaseOcrMinConf, data.GetFloat("ocr_min_conf", 0.5f));
                        // 額外區域: list 整體視為單一欄位 — UI 沒動過 (序列化 == baseline) 才吃磁碟值
                        var diskRegions = ParseRegions(data.Contains("ocr_extra_regions") ? data["ocr_extra_regions"] : null);
                        string diskRegionsSer = SerializeRegions(diskRegions);
                        if (SerializeRegions(m_OcrExtraRegions) == m_BaseOcrExtraRegions)
                        {
                            m_OcrExtraRegions.Clear();
                            m_OcrExtraRegions.AddRange(diskRegions);
                        }
                        m_BaseOcrExtraRegions = diskRegionsSer;
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

                // Daemon alive check — 不只看 PID 檔存在, 還驗檔內 PID 真的是活的 process。
                // 物理意義: 只看 File.Exists 有兩種誤判 — ① 硬殺殘留 stale PID 檔 → 誤判 ALIVE；
                //          ② overlap daemon 誤刪活 daemon 的 PID 檔 (已於 daemon 端 cleanup_pid 修) → 誤判 DEAD。
                //          讀 PID → Process.GetProcessById 驗存活, 兩個方向都準 (2026-07-27 Tim QA)。
                string pidPath = Path.Combine(repoRoot, PID_FILE_RELATIVE);
                m_DaemonAlive = IsPidAlive(pidPath);
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
        /// <summary>簡易 float 輸入欄 —— 打到一半（"-" / "0." / 空字串）不會把值歸零。
        /// 刻意不用 UCL_GUILayout.FloatField：那支的簽名是 (string label, float value)，
        /// 這裡的版面已經自己畫了 Label，混用只會多一層對不上的參數。</summary>
        static float FloatFieldSimple(float value, float width)
        {
            string txt = GUILayout.TextField(value.ToString("0.####"), GUILayout.Width(width));
            return float.TryParse(txt, out float v) ? v : value;   // parse 不掉就保留原值
        }

        // ===========================================================
        // 區塊：錄影開關的**唯一寫入規則** — GUI 按鈕與 Cmd_StreamWatch step=capture 都套這一份
        // 物理意義：翻轉 `enabled` 從來不只是改一個 bool，它連帶三件事：
        //          ① 戳 `enabled_changed_at`（下游結算要「什麼時候停的」，不是「什麼時候被發現的」）
        //          ② 連動 `stt_enabled = enabled && stt_setting`（stt_setting 是意圖、stt_enabled 是實效值）
        //          ③ 其餘欄位不動
        // ⚠ 這三件若在兩個地方各寫一次，遲早給出不同答案 —— 那是本專案今天已經栽過兩次的形狀。
        // 數值影響：同值重複寫入**不重戳時刻**（否則「一直在停」會被讀成「剛剛才停」）。
        // ===========================================================
        public static void ApplyEnabledInto(JsonData ioCfg, bool iOn)
        {
            bool aPrev = ioCfg.Contains("enabled") && (bool)ioCfg["enabled"];
            if (aPrev != iOn)
                ioCfg["enabled_changed_at"] = new JsonData(System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            ioCfg["enabled"] = new JsonData(iOn);
            bool aSetting = ioCfg.Contains("stt_setting") && (bool)ioCfg["stt_setting"];
            ioCfg["stt_enabled"] = new JsonData(iOn && aSetting);
        }

        /// <summary>
        /// 錄影開關的**程式入口**（Cmd_StreamWatch step=capture 用）—— 與 GUI 按鈕同一條規則。
        /// <para>回傳一行給人看的讀數；不丟例外（錄影開關失敗不該炸掉呼叫端的流程）。</para>
        /// ⚠ 它會發酒保公告並要求 daemon 立刻同步 —— 跟按鈕的行為一致，
        /// 否則「Cmd 開的播」跟「人開的播」在酒館裡看起來不一樣。
        /// </summary>
        public static string SetRecordingEnabled(bool iOn, string iBy)
        {
            try
            {
                string aPath = Path.Combine(GetRepoRoot(), CONFIG_RELATIVE);
                if (!File.Exists(aPath)) return $"⚠ 找不到 {aPath} —— 先開一次 ScreenStream 頁初始化";
                var aCfg = JsonData.ParseJson(File.ReadAllText(aPath, System.Text.Encoding.UTF8));
                if (aCfg == null) return "⚠ _config.json 解析失敗";
                bool aPrev = aCfg.Contains("enabled") && (bool)aCfg["enabled"];
                if (aPrev == iOn)
                    return $"已經是「{(iOn ? "錄影中" : "已停止")}」—— 未動作（`enabled` 讀值 {aPrev.ToString().ToLowerInvariant()}）";

                ApplyEnabledInto(aCfg, iOn);
                File.WriteAllText(aPath, aCfg.ToJsonBeautify(), new System.Text.UTF8Encoding(false));
                PostStreamAnnounceStatic(iOn, aCfg);
                AgentCommands.MediaAdmin.UCL_ScreenStreamDaemon.RequestSyncNow();
                bool aStt = aCfg.Contains("stt_enabled") && (bool)aCfg["stt_enabled"];
                return $"{(iOn ? "▶ 已開始錄影" : "⏹ 已停止錄影")}（by {iBy}）"
                     + $"｜`enabled`={iOn.ToString().ToLowerInvariant()}｜`stt_enabled`={aStt.ToString().ToLowerInvariant()}"
                     + $"｜已戳 `enabled_changed_at`｜已發酒保公告並要求 daemon 同步";
            }
            catch (System.Exception e) { return $"⚠ 切換失敗：{e.Message}"; }
        }

        /// <summary>公告的靜態版 —— 標題/解析度/fps/monitor 一律**讀 config**，不讀 GUI 欄位
        /// （頁面沒開時 GUI 欄位是空的，而公告內容不該取決於有沒有人開著那一頁）。</summary>
        static void PostStreamAnnounceStatic(bool iStart, JsonData iCfg)
        {
            try
            {
                string body, ev;
                if (iStart)
                {
                    string aTitle = iCfg.Contains("stream_title") ? (iCfg["stream_title"].ToString() ?? "") : "";
                    string aTitleLine = string.IsNullOrEmpty(aTitle.Trim()) ? "" : $"📺 本場節目: {aTitle}\n";
                    string aRes = iCfg.Contains("resolution") ? iCfg["resolution"].ToString() : "?";
                    string aFps = iCfg.Contains("fps") ? iCfg["fps"].ToString() : "?";
                    string aMon = iCfg.Contains("monitor") ? iCfg["monitor"].ToString() : "?";
                    body = "🍺📹 *咳咳, 諸位.* ScreenStream 直播開始啦!\n" + aTitleLine
                         + $"每秒一張快照 ({aRes} @ {aFps} fps, monitor={aMon}).\n"
                         + "想看在播什麼就 Read AgentCommands/_screenstream/_latest.jpg 吧.\n"
                         + "——酒保提醒: 不 @ everyone 不擾人, 大家自由觀察.";
                    ev = "screenstream-start";
                }
                else
                {
                    body = "🍺⏹ *直播結束.* ScreenStream 已停止 capture.\n"
                         + "ring buffer 的畫面 rolling 之後自動覆蓋, 想找剛剛某張的同事們抓緊看.\n"
                         + "——酒保關燈了.";
                    ev = "screenstream-stop";
                }
                UCL_ChatTavernIO.AppendMessage("tavern", new UCL_ChatMessage
                {
                    sender_id = UCL_ChatTavernIO.BartenderSenderId,
                    sender_name = "酒保",
                    sender_persona = UCL_ChatTavernIO.BartenderSenderId,
                    kind = "chat",
                    body = body,
                    meta = new Dictionary<string, string>
                    {
                        { "tag", "bartender-rule-announce" }, { "category", "meta" }, { "event", ev },
                    },
                });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] 開播/停播廣播失敗（不影響錄影）: {e.Message}");
            }
        }

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

                // 錄影開關的寫入規則**只有一份**（ApplyEnabledInto）—— GUI 按鈕與 Cmd 都套它。
                // 🩸 昨天才因為「同一件事兩個寫入端、責任邊界只存在於註解裡」栽過（PersistEnabled vs LoadConfig）。
                ApplyEnabledInto(existing, m_Enabled);
                existing["fps"] = new JsonData(m_Fps);
                existing["max_frames"] = new JsonData(m_MaxFrames);
                existing["recording_enabled"] = new JsonData(m_Recording);
                existing["recording_name"] = new JsonData(m_RecordingName ?? "");
                existing["resolution"] = new JsonData(m_Resolution);
                existing["audio_viz_enabled"] = new JsonData(m_AudioViz);
                existing["audio_viz_mode"] = new JsonData(m_AudioVizMode ?? "stereo_eq");
                existing["audio_viz_position"] = new JsonData(m_AudioVizPosition ?? "bottom-stretch");
                existing["quality"] = new JsonData(m_Quality);
                existing["monitor"] = new JsonData(m_Monitor);
                // STT: stt_setting = Tim 意圖 (持久化); stt_enabled = 實效值 (錄影中且開了才真啟動 worker)
                // → 開始錄影 (m_Enabled=true) 且 stt_setting=on → daemon 下次 reload 起 whisper; 停錄影自動關.
                existing["stt_setting"] = new JsonData(m_SttSetting);
                existing["stt_enabled"] = new JsonData(m_Enabled && m_SttSetting);
                existing["stt_model"] = new JsonData(m_SttModel);
                existing["stt_rms_gate"] = new JsonData(m_SttRmsGate);
                existing["stt_no_speech_max"] = new JsonData(m_SttNoSpeechMax);
                existing["stt_logprob_min"] = new JsonData(m_SttLogprobMin);
                existing["stt_lang"] = new JsonData(m_SttLang);
                // 人名詞彙偏置 — Page 擁有此欄 (Tim 2026-08-11), 每次 save 一律寫回 UI 當前值。
                // 清除 = 把欄位清空再存 (見下方 clearSttPrompt), 不再由「開始錄影」代勞。
                existing["stt_prompt"] = new JsonData(m_SttPrompt ?? "");
                existing["stream_title"] = new JsonData(m_StreamTitle ?? "");
                // OCR 欄位 (底部原點語意) — daemon 每 loop reload, band 改動走 T-OCR-AutoRestart 自動重起 pool
                existing["ocr_enabled"] = new JsonData(m_OcrEnabled);
                existing["ocr_workers"] = new JsonData(m_OcrWorkers);
                existing["ocr_y_bottom_pct"] = new JsonData(m_OcrBand.YBottomPct);
                existing["ocr_h_pct"] = new JsonData(m_OcrBand.HPct);
                existing["ocr_x_center_pct"] = new JsonData(m_OcrBand.XCenterPct);
                existing["ocr_w_pct"] = new JsonData(m_OcrBand.WPct);
                existing["ocr_min_conf"] = new JsonData(m_OcrMinConf);
                var regionsArr = new JsonData().ToArray();
                foreach (var r in m_OcrExtraRegions)
                {
                    var o = new JsonData();
                    o["y_bottom_pct"] = new JsonData(r.YBottomPct);
                    o["h_pct"] = new JsonData(r.HPct);
                    o["x_center_pct"] = new JsonData(r.XCenterPct);
                    o["w_pct"] = new JsonData(r.WPct);
                    regionsArr.Add(o);
                }
                existing["ocr_extra_regions"] = regionsArr;
                // 「清除偏置」鈕走這條 (Tim 2026-08-11 起僅此一處呼叫; 開始錄影已不再自動清空 —— 原因見
                //  m_SttPrompt 宣告處的沿革註解: 自動清空與可編輯欄位互斥)
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
                m_BaseSttPrompt = m_SttPrompt;
                m_BaseStreamTitle = m_StreamTitle;
                m_BaseOcrEnabled = m_OcrEnabled;
                m_BaseOcrWorkers = m_OcrWorkers;
                m_BaseOcrYBottomPct = m_OcrBand.YBottomPct;
                m_BaseOcrHPct = m_OcrBand.HPct;
                m_BaseOcrXCenterPct = m_OcrBand.XCenterPct;
                m_BaseOcrWPct = m_OcrBand.WPct;
                m_BaseOcrMinConf = m_OcrMinConf;
                m_BaseOcrExtraRegions = SerializeRegions(m_OcrExtraRegions);
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
                ReloadLatestSttOcr();   // 增量掃 STT/OCR cache (mtime watermark; 最新+歷史共用快取)
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

            GUILayout.EndHorizontal();
            GUILayout.Label("獨立 Page 防誤觸. 錄影中時敏感 Page (含 token / 帳號) 會自動黑屏. 開始/停止錄影鈕在最上方 Top Bar.");
            GUILayout.Space(8);

            // 片名/描述輸入 (Tim 2026-07-27; 2026-07-28 移到頁面最上方) — 可空; 有填則開播酒館廣播附加「📺 本場節目: <此文字>」
            // 開播前第一眼看得見、來得及填; 按 Top Bar「開始/停止錄影」或「儲存設定」時隨 config 保存
            if (m_ConfigLoaded)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("📺 片名/描述:", GUILayout.Width(100));
                m_StreamTitle = GUILayout.TextField(m_StreamTitle ?? "", GUILayout.MinWidth(300));
                GUILayout.Label("(可空; 有填則開播酒館廣播附加此段)", new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

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
            // Status strip — 單行狀態條 (Tim 2026-08-11: 原本三塊佔掉太多版面, 併成一行)
            // 區塊職責: 錄影狀態 + 幀數 + daemon 健康**一行講完**; 細節 (started_at / DEAD 說明) 收摺頁。
            // 沿革: 原本是「fontSize 24 大字 box」+「停止 box」+ 獨立一行 Daemon process, 共約 5 行高。
            //       Tim 的理由: Top Bar 的「⏹ 停止錄影 / ▶ 開始錄影」鈕本身就已經是錄影狀態指示器,
            //       頁內再用大字重述一次是重複資訊。
            // 物理意義: **底色仍保留紅/綠** —— 本頁存在的理由之一就是「錄影中」要不可誤認 (2026-05-16 拍板),
            //          所以壓縮的是字級與行數, 不是警示本身。
            // 數值影響: 純版面; m_FrameCount / m_LatestFrame / m_DaemonAlive 來源與更新節奏都沒變。
            var oldColor = GUI.backgroundColor;
            GUI.backgroundColor = m_Enabled ? new Color(0.8f, 0.1f, 0.1f) : new Color(0.2f, 0.5f, 0.2f);
            bool aShowStatusDetail;
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                GUI.backgroundColor = oldColor;   // 只染 box 底, 內部元件維持正常色
                GUILayout.BeginHorizontal();
                aShowStatusDetail = UCL_GUILayout.Toggle(m_Dic, "StatusFold", 18, iDefaultValue: false);
                if (m_Enabled)
                {
                    GUILayout.Label("🔴 錄影中", new GUIStyle(GUI.skin.label)
                    { fontSize = 15, fontStyle = FontStyle.Bold });
                    GUILayout.Label($"{m_FrameCount} frames · latest frame_{m_LatestFrame}",
                        new GUIStyle(GUI.skin.label) { fontSize = 11 });
                }
                else
                {
                    GUILayout.Label("⚫ 停止", new GUIStyle(GUI.skin.label)
                    { fontSize = 15, fontStyle = FontStyle.Bold });
                    GUILayout.Label("按 Top Bar「▶ 開始錄影」啟動", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                }
                GUILayout.FlexibleSpace();
                // daemon 健康併進同一行 — 只在「不健康」時才需要文字說明, 正常態一顆燈就夠
                string aDaemonShort = m_DaemonAlive ? "🟢 daemon"
                    : m_Enabled ? "🔴 daemon DEAD" : "⚫ daemon";
                GUILayout.Label(aDaemonShort, new GUIStyle(GUI.skin.label) { fontSize = 11 });
                GUILayout.EndHorizontal();

                if (aShowStatusDetail)
                {
                    // 摺頁內容 = 原本常駐但少看的兩筆; 展開才佔版面
                    if (!string.IsNullOrEmpty(m_StartedAt))
                    {
                        GUILayout.Label($"  started_at: {m_StartedAt}",
                            new GUIStyle(GUI.skin.label) { fontSize = 10 });
                    }
                    string aDaemonState = m_DaemonAlive ? "🟢 ALIVE (daemon 運行中)"
                        : m_Enabled ? "🔴 DEAD (錄影中但無存活 daemon — 等 Editor respawn, ~5s)"
                                    : "⚫ 停止 (未錄影時 daemon 同步停止, 開始錄影自動啟動)";
                    GUILayout.Label($"  Daemon process: {aDaemonState}",
                        new GUIStyle(GUI.skin.label) { fontSize = 10 });
                }
            }
            GUI.backgroundColor = oldColor;
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

            // STT / OCR 顯示 (Tim 2026-07-27) — 最新 + 可展開分頁歷史
            DrawSttOcrPanel();
            GUILayout.Space(8);

            GUILayout.Space(15);

            // ===========================================================
            // Config 設定
            // ===========================================================
            var widthStyle = GUILayout.Width(UCL_GUIStyle.GetScaledSize(80));

            // 折疊 (Tim 2026-07-28): 錄影參數不常動, 預設收合省版面; 開合狀態存 m_Dic (頁面生命週期內記住)
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            bool aShowRecSettings = UCL_GUILayout.Toggle(m_Dic, "RecSettingsFold", 21, iDefaultValue: false);
            GUILayout.Label("⚙ 錄影設定 (fps / ring buffer / 解析度 / 畫質 / monitor — 隨「儲存設定 / 開始·停止錄影」保存)",
                new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (aShowRecSettings)
            {
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

            // ==== T-SSREC-01 錄播模式 ====
            // 錄播無視 max_frames（不繞回去），寫進 _screenstream/recording/；
            // 關掉時 daemon 會把它 rename 成 recordings/<名稱>/ 並重建空資料夾。
            // 名稱可留空 → 用起始時間戳；事後手動改資料夾名不影響任何機制（真相在 manifest）。
            using (new GUILayout.HorizontalScope("box"))
            {
                bool newRec = GUILayout.Toggle(m_Recording,
                    m_Recording ? " ⏺ 錄播中（不覆寫，關閉即歸檔）" : " ○ 錄播關閉（僅 ring buffer）",
                    new GUIStyle(UCL_GUIStyle.ButtonStyle) { richText = true }, GUILayout.ExpandWidth(false));
                if (newRec != m_Recording)
                {
                    m_Recording = newRec;
                    SaveToDisk();   // 立即落檔 — daemon 每 loop 重讀 config，下一秒生效
                }
                GUILayout.Label("名稱:", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                m_RecordingName = GUILayout.TextField(m_RecordingName ?? "", UCL_GUIStyle.TextFieldStyle,
                                                      GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                GUILayout.Label("(留空=用時間戳; 約 245 MB/小時 @1fps)", UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("resolution:", GUILayout.Width(120));
            int curIdx = Array.IndexOf(s_ResolutionOptions, m_Resolution);
            if (curIdx < 0) curIdx = 3; // default 1080p
            int newIdx = GUILayout.SelectionGrid(curIdx, s_ResolutionOptions, s_ResolutionOptions.Length);
            if (newIdx != curIdx) m_Resolution = s_ResolutionOptions[newIdx];
            GUILayout.EndHorizontal();

            // ==== 🎵 聲音圖譜 overlay（Tim 2026-08-01）====
            // agent 沒耳朵 —— 這條圖譜是它判讀音訊狀態的替代感官（有沒有聲音 / 左右聲道 / 爆音）。
            // 判讀方式見 docs/Workflows/Audio_Viz_Reading_Guide.md。
            using (new GUILayout.HorizontalScope("box"))
            {
                bool newViz = GUILayout.Toggle(m_AudioViz,
                    m_AudioViz ? " 🎵 聲音圖譜 開（疊在截圖上）" : " ○ 聲音圖譜 關",
                    new GUIStyle(UCL_GUIStyle.ButtonStyle) { richText = true }, GUILayout.ExpandWidth(false));
                if (newViz != m_AudioViz)
                {
                    m_AudioViz = newViz;
                    SaveToDisk();   // 立即落檔 — daemon 每 loop 重讀 config，下一秒生效
                }
                using (new UnityEditor.EditorGUI.DisabledScope(!m_AudioViz))
                {
                    int mi = Array.IndexOf(s_AudioVizModeOptions, m_AudioVizMode);
                    if (mi < 0) mi = 0;
                    int nmi = GUILayout.SelectionGrid(mi, s_AudioVizModeOptions, s_AudioVizModeOptions.Length,
                                                      GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    if (nmi != mi) { m_AudioVizMode = s_AudioVizModeOptions[nmi]; SaveToDisk(); }

                    int pi = Array.IndexOf(s_AudioVizPosOptions, m_AudioVizPosition);
                    if (pi < 0) pi = 0;
                    int npi = GUILayout.SelectionGrid(pi, s_AudioVizPosOptions, s_AudioVizPosOptions.Length,
                                                      GUILayout.Width(UCL_GUIStyle.GetScaledSize(380)));
                    if (npi != pi) { m_AudioVizPosition = s_AudioVizPosOptions[npi]; SaveToDisk(); }
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("quality:", GUILayout.Width(120));
            m_Quality = (int)GUILayout.HorizontalSlider(m_Quality, 1, 95, GUILayout.Width(200));
            GUILayout.Label($"{m_Quality} (JPEG 1-95, 65 平衡)");
            GUILayout.EndHorizontal();

            // T14 — Monitor selector: primary / all / unity_game / 0 / 1 / 2 ... 列舉真實 monitor
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            GUILayout.Label("monitor:", GUILayout.Width(120));
            // 🔄 手動刷新螢幕清單 (Tim 2026-07-28) — 熱插拔外接螢幕後按此重枚舉; one-shot ~1s 後 tick 自動更新
            if (GUILayout.Button("🔄", UCL_GUIStyle.ButtonStyle, GUILayout.Width(30)))
                AgentCommands.MediaAdmin.UCL_ScreenStreamDaemon.EnumerateMonitorsOneShot();
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
            }   // end aShowRecSettings
            GUILayout.EndVertical();   // end 錄影設定 fold box

            // ===========================================================
            // T-STT-PageToggle (Tim 2026-07-09) — 語音轉錄 (STT) 設定
            // 物理意義: 開關 = 錄影時要不要同步跑 whisper 把系統音訊轉逐句文字 (montage --stt 讀 cache);
            //          stt_enabled 實效值 = 錄影中 && 本開關, 故「開始錄影」即同步啟動、「停止錄影」即停.
            // ===========================================================
            GUILayout.Space(10);
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.BeginHorizontal();
                // 折疊 (Tim 2026-07-28): 開關常駐 header 一眼可見/可切, 細項 (model/lang/偏置) 收進摺頁
                bool aShowStt = UCL_GUILayout.Toggle(m_Dic, "SttSettingsFold", 21, iDefaultValue: false);
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

                if (aShowStt)
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

                    // ==== 靜音幻覺防治門檻（Tim 2026-08-01）====
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("靜音閘 RMS:", GUILayout.Width(90));
                    m_SttRmsGate = FloatFieldSimple(m_SttRmsGate, 70);
                    GUILayout.Label("no_speech≤", GUILayout.Width(80));
                    m_SttNoSpeechMax = FloatFieldSimple(m_SttNoSpeechMax, 60);
                    GUILayout.Label("logprob≥", GUILayout.Width(70));
                    m_SttLogprobMin = FloatFieldSimple(m_SttLogprobMin, 60);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Label("  ⓘ 靜音閘: chunk 音量低於此值不送 whisper (0=停用). 對純靜音最有效, 治「對著無聲吐出 1/2/3」. "
                        + "後兩者對齊 whisper 官方門檻, 且**兩者同時**成立才丟棄段落 —— "
                        + "單獨用 no_speech 會把真對白砍掉 (2026-08-01 實測: 真對白的 no_speech_prob 可達 0.685).",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    GUILayout.Label("  ⚠ 改這幾個值要 **toggle STT 開關 off→on** 才生效 —— python 模組已載入記憶體, 改 config 不會重載 code.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });

                    GUILayout.Label("  ⓘ small 是品質vs速度甜蜜點; 看日番建議選 ja (自動偵測會飄). "
                        + "whisper 常駐 GPU ~460MB, 停錄影自動釋放.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    GUILayout.Label("  ⚠ 若 daemon 端 whisper 不可用 (log 印「whisper 不可用」), STT 靜默跳過不影響截圖; "
                        + "依賴安裝/健檢請開「影音管理」頁.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });

                    // ==== 人名詞彙偏置 (stt_prompt) — 可編輯 (Tim 2026-08-11) ====
                    // 區塊職責: whisper initial_prompt 的輸入欄 + 字數/token 風險提示 + 清除鈕。
                    // 物理意義: 專有名詞是 STT 最大失效類 (2026-08-11 陪看八輪實測), 而這是它的正解;
                    //          偏置是機率加權不是白名單, 填了仍可能沒命中。
                    // 數值影響: 存檔即生效 (daemon 偵測 prompt 變更自動重起 worker, 不必停/啟錄影)。
                    GUILayout.Space(4);
                    GUILayout.Label("  🏷 人名詞彙偏置 (whisper initial_prompt) — 只填**這一部片**的專有名詞, 逗號分隔:",
                        new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold });
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(12);
                    m_SttPrompt = GUILayout.TextArea(m_SttPrompt ?? "", GUILayout.MinHeight(38));
                    using (new GUILayout.VerticalScope(GUILayout.Width(80)))
                    {
                        if (GUILayout.Button("清除", GUILayout.Width(76)))
                        {
                            SaveToDisk(clearSttPrompt: true);
                        }
                        // 長度警示 —— whisper prompt 上限約 224 token; 中日文粗估 1 字≈1 token,
                        // 超了是**靜默截斷前段**, 所以要在 UI 就擋住 (不會有 error log 告訴你)
                        int aPromptLen = (m_SttPrompt ?? "").Length;
                        GUILayout.Label(aPromptLen > 200 ? $"⚠ {aPromptLen} 字" : $"{aPromptLen} 字",
                            new GUIStyle(GUI.skin.label)
                            { fontSize = 10, alignment = TextAnchor.MiddleCenter });
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Label("  ⓘ 上限約 224 token (中日文粗估 1 字≈1 token) — **超過會靜默截掉前段**, 所以只放專有名詞、別寫句子.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    // 生效時機 —— 打字不會自動生效, 要按下方「💾 儲存設定」寫 config, daemon 才看得到。
                    // 錄影中改也可以: daemon 的 T-STT-AutoRestart 把 (model, lang, prompt) 當簽章比對,
                    // 任一改變就換一顆 worker（prompt 綁建構子, 不可熱改）→ 代價是卸/載 whisper 數秒,
                    // **銜接損失 ≤1 個 chunk**（現行 chunk_sec 設定值）。截圖與 OCR 完全不受影響（不同 worker）。
                    GUILayout.Label("  ⓘ 生效時機: 打完要按下方「💾 儲存設定」. **錄影中改也會套用** —— daemon 偵測到變更會換一顆 "
                        + "whisper worker, 代價是重載模型數秒、STT 銜接**損失 ≤1 個 chunk**; 截圖／OCR 不受影響.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                    GUILayout.Label("  ⚠ **每片一份**: 換片沒改 → whisper 會把上一部片的人名硬套到這一部 (2026-07-20 血證). "
                        + "換片時跟「📺 片名/描述」一起改.",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                }
            }

            // ===========================================================
            // OCR 字幕讀取設定 (Tim 2026-07-28 — 自 UCL_MediaAdminPage 整合進本頁)
            // ===========================================================
            GUILayout.Space(10);
            DrawOcrSettingsPanel();

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

        // ===========================================================
        // 區塊：STT / OCR 顯示 (Tim 2026-07-27) — 最新 + 可展開分頁歷史 (每頁 10)
        // 物理意義：讀 _screenstream/stt/stt_<epoch>.json (whisper) + ocr/frame_NNNN.json (字幕 OCR) cache。
        //          mtime watermark 增量掃 (2026-07-27 修): 「最新」與「歷史」共用同一份檔案快取,
        //          每 2s tick 只 stat 目錄 + parse 有變動的檔 — 同時修正 ring buffer 檔名序陷阱
        //          (最新 OCR 落後) 與歷史凍結不追新兩隻 bug。
        // ===========================================================
        // 區塊職責: 增量掃描字幕 cache 目錄 — 只 parse mtime 超過 watermark 的檔, 已刪檔同步移除。
        // 物理意義: OCR ring buffer 就地覆寫 (frame_NNNN.json), 檔名序 ≠ 時間序; mtime 才是新舊依據。
        //          STT 是 append-only (stt_<epoch>.json) + retention 刪舊檔, 同一套機制天然涵蓋。
        // 數值影響: 回傳是否有變動 (供 latest 重算 + 歷史 dirty 標記);
        //          全目錄 mtime 整批倒退 (目錄被清空重建) → 重置 watermark 全量重掃, 不會卡死在舊水位。
        static bool ScanSubtitleDir(string dir, string pattern,
            Dictionary<string, List<(double epoch, string text)>> fileEntries, ref long watermark,
            Func<string, List<(double epoch, string text)>> parser)
        {
            bool changed = false;
            try
            {
                if (!Directory.Exists(dir))
                {
                    if (fileEntries.Count > 0) { fileEntries.Clear(); changed = true; }
                    watermark = 0;
                    return changed;
                }
                for (int pass = 0; pass < 2; pass++)   // 第 2 輪只在 watermark 重置時跑
                {
                    var files = Directory.GetFiles(dir, pattern);
                    var seen = new HashSet<string>(files);
                    List<string> removed = null;
                    foreach (var k in fileEntries.Keys)
                        if (!seen.Contains(k)) (removed ??= new List<string>()).Add(k);
                    if (removed != null)
                    {
                        foreach (var k in removed) fileEntries.Remove(k);
                        changed = true;
                    }
                    long curMax = 0, newMark = watermark;
                    foreach (var f in files)
                    {
                        long t = File.GetLastWriteTimeUtc(f).Ticks;
                        if (t > curMax) curMax = t;
                        if (t <= watermark) continue;
                        fileEntries[f] = parser(f);
                        changed = true;
                        if (t > newMark) newMark = t;
                    }
                    if (curMax < watermark)   // mtime 倒退 = 目錄整批重建 → 重掃
                    {
                        watermark = 0;
                        fileEntries.Clear();
                        changed = true;
                        continue;
                    }
                    watermark = newMark;
                    break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] scan subtitle dir fail ({dir}): {e.Message}");
            }
            return changed;
        }

        // 每 2s tick 進入點: 增量掃兩個目錄, 有變動才重算「最新」+ 標歷史 dirty
        void ReloadLatestSttOcr()
        {
            string root = GetRepoRoot();
            if (ScanSubtitleDir(Path.Combine(root, STT_DIR_RELATIVE), "stt_*.json",
                    m_SttFileEntries, ref m_SttWatermark, ParseSttFile))
            {
                m_SttHistDirty = true;
                RecomputeLatest(m_SttFileEntries, ref m_LatestSttEpoch, ref m_LatestSttText, ref m_LatestSttTime);
            }
            if (ScanSubtitleDir(Path.Combine(root, OCR_DIR_RELATIVE), "frame_*.json",
                    m_OcrFileEntries, ref m_OcrWatermark, ParseOcrFileEntries))
            {
                m_OcrHistDirty = true;
                RecomputeLatest(m_OcrFileEntries, ref m_LatestOcrEpoch, ref m_LatestOcrText, ref m_LatestOcrTime);
            }
            ReloadSttStatusError();
        }

        // 「最新」= 快取內 epoch 最大的非空 entry — 不再依賴檔名序 (ring buffer 陷阱), 刪檔也正確回退
        static void RecomputeLatest(Dictionary<string, List<(double epoch, string text)>> fileEntries,
            ref double latestEpoch, ref string latestText, ref string latestTime)
        {
            double bestEp = 0; string bestText = "";
            foreach (var kv in fileEntries)
                foreach (var e in kv.Value)
                    if (e.text.Length > 0 && e.epoch >= bestEp) { bestEp = e.epoch; bestText = e.text; }
            latestEpoch = bestEp;
            latestText = bestText;
            latestTime = bestText.Length > 0 ? EpochToHms(bestEp) : "";
        }

        // 讀 stt/_status.json 的 error 欄 (daemon worker 失敗時寫入) — mtime 沒變就跳過 parse
        void ReloadSttStatusError()
        {
            try
            {
                string p = Path.Combine(GetRepoRoot(), STT_DIR_RELATIVE, "_status.json");
                if (!File.Exists(p)) { m_SttStatusError = ""; m_SttStatusMtime = 0; return; }
                long t = new FileInfo(p).LastWriteTimeUtc.Ticks;
                if (t == m_SttStatusMtime) return;
                m_SttStatusMtime = t;
                var d = JsonData.ParseJson(File.ReadAllText(p));
                m_SttStatusError = d != null ? (d.GetString("error", "") ?? "") : "";
            }
            catch { m_SttStatusError = ""; }
        }

        // 顯示列表重建 (從檔案快取 flatten + newest-first 排序; 只在 dirty 且停在第 1 頁時做)。
        // 註: 短時間多筆同句 OCR 是「同一句字幕停留 N 秒、1fps 逐幀各記一筆」的正常現象;
        //     不做摺疊 — 重複台詞可能是真的重複說 (Tim 2026-07-27 拍板), 逐幀真相原樣呈現。
        static List<(double epoch, string text)> BuildHistory(
            Dictionary<string, List<(double epoch, string text)>> fileEntries)
        {
            var list = new List<(double epoch, string text)>();
            foreach (var kv in fileEntries) list.AddRange(kv.Value);
            list.Sort((a, b) => b.epoch.CompareTo(a.epoch));
            return list;
        }

        // 🔄 手動重新整理 = 不信任快取的逃生門: 重置 watermark 全量重掃 + 立即重建列表回第 1 頁
        void ForceRescan(bool stt)
        {
            if (stt) { m_SttWatermark = 0; m_SttFileEntries.Clear(); }
            else { m_OcrWatermark = 0; m_OcrFileEntries.Clear(); }
            ReloadLatestSttOcr();
            if (stt) { m_SttHistory = BuildHistory(m_SttFileEntries); m_SttHistDirty = false; m_SttHistPage = 0; }
            else { m_OcrHistory = BuildHistory(m_OcrFileEntries); m_OcrHistDirty = false; m_OcrHistPage = 0; }
        }

        static List<(double epoch, string text)> ParseSttFile(string path)
        {
            var result = new List<(double epoch, string text)>();
            try
            {
                var d = JsonData.ParseJson(File.ReadAllText(path));
                if (d != null && d.Contains("segments") && d["segments"].IsArray)
                {
                    var segs = d["segments"];
                    for (int i = 0; i < segs.Count; i++)
                    {
                        string t = (segs[i].GetString("text", "") ?? "").Trim();
                        if (t.Length == 0) continue;
                        double ep = segs[i].GetDouble("end_epoch", segs[i].GetDouble("start_epoch", 0));
                        result.Add((ep, t));
                    }
                }
            }
            catch { }
            return result;
        }

        // ScanSubtitleDir 用的 OCR parser: 單檔 0/1 筆 entry (空字幕不入快取)
        static List<(double epoch, string text)> ParseOcrFileEntries(string path)
        {
            var e = ParseOcrFile(path);
            var list = new List<(double epoch, string text)>(1);
            if (e.text.Length > 0) list.Add(e);
            return list;
        }

        static (double epoch, string text) ParseOcrFile(string path)
        {
            try
            {
                var d = JsonData.ParseJson(File.ReadAllText(path));
                if (d == null) return (0, "");
                string t = (d.GetString("text", "") ?? "").Trim();
                double ep = d.GetDouble("ocr_at", d.GetDouble("mtime", 0));
                return (ep, t);
            }
            catch { return (0, ""); }
        }

        static string EpochToHms(double epoch)
        {
            if (epoch <= 0) return "--:--:--";
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000.0)).LocalDateTime.ToString("HH:mm:ss"); }
            catch { return "--:--:--"; }
        }

        // 區塊職責: staleness 警示後綴 — 錄影中且功能開著、最新資料卻超過門檻沒更新 → 標警告。
        // 物理意義: 修「停了沒人知道」(2026-07-27 STT 靜默停擺 2h): 資料流凍結必須在 UI 上看得見。
        // 數值影響: STT 門檻 60s ≈ 4×chunk(15s); OCR 30s (1fps 下有字幕時每秒都該有新 entry, 給緩衝)。
        //          非錄影中 / 功能沒開 = 本來就不會有新資料, 不警告。
        string StaleSuffix(double latestEpoch, double thresholdSec, bool featureActive)
        {
            if (!m_Enabled || !featureActive || latestEpoch <= 0) return "";
            double age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - latestEpoch;
            if (age < thresholdSec) return "";
            string ageStr = age >= 90 ? $"{age / 60.0:F0} 分鐘" : $"{age:F0} 秒";
            return $"  ⚠ 已 {ageStr}沒新資料";
        }

        GUIStyle m_ErrWrapStyle;
        GUIStyle ErrWrapStyle
        {
            get
            {
                if (m_ErrWrapStyle == null)
                    m_ErrWrapStyle = new GUIStyle(GUI.skin.label)
                    { wordWrap = true, fontSize = 11, normal = { textColor = new Color(1f, 0.45f, 0.35f) } };
                return m_ErrWrapStyle;
            }
        }

        // UI：最新 STT/OCR + 兩個可展開分頁歷史
        void DrawSttOcrPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("🎙 STT / 📖 OCR（語音轉錄 / 字幕辨識）",
                    new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
                GUILayout.Label("🎙 最新 STT：" + (m_LatestSttText.Length == 0
                    ? "(無 — daemon STT 未啟用或無音訊)" : $"[{m_LatestSttTime}] {m_LatestSttText}")
                    + StaleSuffix(m_LatestSttEpoch, 60, m_SttEnabledCfg), SubWrapStyle);
                GUILayout.Label("📖 最新 OCR：" + (m_LatestOcrText.Length == 0
                    ? "(無 — daemon OCR 未啟用或無字幕)" : $"[{m_LatestOcrTime}] {m_LatestOcrText}")
                    + StaleSuffix(m_LatestOcrEpoch, 30, m_OcrEnabledCfg), SubWrapStyle);
                // daemon STT worker 的失敗原因 (stt/_status.json error 欄) — 禁靜默失敗的 UI 出口
                if (m_SttStatusError.Length > 0)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // 複製鈕 (Tim 2026-07-27): 一鍵把完整錯誤進剪貼簿, 方便直接貼給 agent 查案
                        if (GUILayout.Button("📋 複製", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            UnityEditor.EditorGUIUtility.systemCopyBuffer = $"STT worker 錯誤: {m_SttStatusError}";
                            Debug.Log("[UCL_ScreenStreamPage] STT 錯誤訊息已複製到剪貼簿");
                        }
                        GUILayout.Label($"⛔ STT worker 錯誤: {m_SttStatusError}", ErrWrapStyle);
                    }
                }

                GUILayout.Space(4);
                // 展開即顯示 (快取常駐, 由 2s tick 增量維護); 第 1 頁 dirty 時自動重建 = 自動追新
                bool newStt = GUILayout.Toggle(m_ShowSttHistory,
                    m_ShowSttHistory ? "▼ 隱藏歷史 STT" : "▶ 展開歷史 STT", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                if (newStt != m_ShowSttHistory) { m_ShowSttHistory = newStt; if (newStt) m_SttHistDirty = true; }
                if (m_ShowSttHistory)
                {
                    if ((m_SttHistDirty && m_SttHistPage == 0) || m_SttHistory == null)
                    { m_SttHistory = BuildHistory(m_SttFileEntries); m_SttHistDirty = false; }
                    DrawSubtitleHistory(m_SttHistory, ref m_SttHistPage, "STT", m_SttHistDirty, () => ForceRescan(stt: true));
                }

                GUILayout.Space(2);
                bool newOcr = GUILayout.Toggle(m_ShowOcrHistory,
                    m_ShowOcrHistory ? "▼ 隱藏歷史 OCR" : "▶ 展開歷史 OCR", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                if (newOcr != m_ShowOcrHistory) { m_ShowOcrHistory = newOcr; if (newOcr) m_OcrHistDirty = true; }
                if (m_ShowOcrHistory)
                {
                    if ((m_OcrHistDirty && m_OcrHistPage == 0) || m_OcrHistory == null)
                    { m_OcrHistory = BuildHistory(m_OcrFileEntries); m_OcrHistDirty = false; }
                    DrawSubtitleHistory(m_OcrHistory, ref m_OcrHistPage, "OCR", m_OcrHistDirty, () => ForceRescan(stt: false));
                }
            }
        }

        // 分頁歷史列表 — newest-first, page 0 = 最新頁 (仿聊天酒館分頁)。
        // 第 1 頁自動追新; 停在舊頁時凍結顯示 (避免閱讀中內容被新資料推走), 另給提示。
        void DrawSubtitleHistory(List<(double epoch, string text)> list, ref int page, string label,
            bool hasNewData, Action forceReload)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                if (list == null) { GUILayout.Label("(載入中…)"); return; }
                int total = list.Count;
                if (total == 0) { GUILayout.Label($"（{label} 歷史為空 — daemon 未啟用或還沒產出）"); return; }
                int maxPage = (total - 1) / SttOcrPageSize;
                if (page < 0) page = 0;
                if (page > maxPage) page = maxPage;

                using (new GUILayout.HorizontalScope())
                {
                    bool oldE = GUI.enabled;
                    GUI.enabled = page > 0;
                    if (GUILayout.Button("◀ 較新", GUILayout.ExpandWidth(false))) page--;
                    GUI.enabled = oldE;
                    GUILayout.Label($"第 {page + 1}/{maxPage + 1} 頁（共 {total} 筆 · 每頁 {SttOcrPageSize}）", GUILayout.ExpandWidth(false));
                    GUI.enabled = page < maxPage;
                    if (GUILayout.Button("較舊 ▶", GUILayout.ExpandWidth(false))) page++;
                    GUI.enabled = oldE;
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🔄 重新整理", GUILayout.ExpandWidth(false))) forceReload();
                }
                if (hasNewData && page > 0)
                    GUILayout.Label("ⓘ 有新資料 — 回到第 1 頁自動更新, 或按 🔄",
                        new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
                int start = page * SttOcrPageSize;
                int end = Math.Min(start + SttOcrPageSize, total);
                for (int i = start; i < end; i++)
                    GUILayout.Label($"[{EpochToHms(list[i].epoch)}] {list[i].text}", SubWrapStyle);
            }
        }

        // ===========================================================
        // 區塊：OCR 字幕讀取設定 (Tim 2026-07-28 — 自 UCL_MediaAdminPage 整合)
        // 物理意義: 字幕帶座標為底部原點 — 「起始 y」= 帶底邊離畫面下緣的距離比例 (0=貼底, 初始值 0),
        //          「高度」從底邊往上長 (例: y=0, h=0.1 → 覆蓋畫面最下方 10%)。
        //          額外判定區域 (可空) 給「字幕偶爾跑到上方」的影片 — daemon 對每幀逐區域 OCR 合併結果。
        // 數值影響: 隨「儲存設定 / 開始·停止錄影」寫 config; daemon 每 loop reload,
        //          band 改動觸發 T-OCR-AutoRestart 自動重起 worker pool 套用, 不必手動 toggle。
        // ===========================================================
        void DrawOcrSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.BeginHorizontal();
                // 折疊 (Tim 2026-07-28): 開關常駐 header, 帶位/區域/預覽細項收進摺頁
                bool aShowOcr = UCL_GUILayout.Toggle(m_Dic, "OcrSettingsFold", 21, iDefaultValue: false);
                m_OcrEnabled = GUILayout.Toggle(m_OcrEnabled, " 📖 字幕 OCR (RapidOCR — daemon 錄影中逐幀辨識)");
                GUILayout.FlexibleSpace();
                if (m_OcrEnabled && m_Enabled)
                    GUILayout.Label("🟢 錄影中運作", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                else if (m_OcrEnabled)
                    GUILayout.Label("⚪ 待開始錄影時運作", new GUIStyle(GUI.skin.label) { fontSize = 11 });
                // 依賴安裝 (rapidocr-onnxruntime) / 細部健檢歸影音管理頁
                if (GUILayout.Button("🎬 影音管理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_MediaAdminPage.Create();
                }
                GUILayout.EndHorizontal();

                if (!aShowOcr) return;   // 摺頁收合 — 細項全部隱藏 (開關仍在 header 可切)

                GUILayout.BeginHorizontal();
                GUILayout.Label("worker 數:", GUILayout.Width(120));
                m_OcrWorkers = Mathf.Clamp(UCL_GUILayout.IntField(m_OcrWorkers, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))), 1, 8);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                // 主字幕帶 — 與額外區域走同一段繪製 (Tim 2026-08-04: 同一個 class 同一套 UI)
                DrawBandFields(m_OcrBand, "字幕帶");
                m_OcrMinConf = UnityEditor.EditorGUILayout.Slider("最低信度過濾", m_OcrMinConf, 0f, 1f);

                // 額外字幕判定區域 — 可折疊 List (可空; 有些影片字幕偶爾跑到上方)
                string foldLabel = (m_ShowOcrExtraRegions ? "▼" : "▶") + $" 額外字幕判定區域 ({m_OcrExtraRegions.Count})";
                if (GUILayout.Button(foldLabel, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    m_ShowOcrExtraRegions = !m_ShowOcrExtraRegions;
                if (m_ShowOcrExtraRegions)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label("  ⓘ 主字幕帶外的加掃區域 — 例如字幕偶爾跳到畫面上方的影片, 加一條上方帶。座標同為底部原點。",
                            new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic, wordWrap = true });
                        int removeIdx = -1;
                        for (int i = 0; i < m_OcrExtraRegions.Count; i++)
                        {
                            var r = m_OcrExtraRegions[i];
                            GUILayout.BeginHorizontal();
                            GUILayout.Label($"#{i + 1}", GUILayout.Width(30));
                            GUILayout.BeginVertical();
                            DrawBandFields(r, "");
                            GUILayout.EndVertical();
                            if (GUILayout.Button("✕", UCL_GUIStyle.ButtonStyle, GUILayout.Width(28), GUILayout.Height(36)))
                                removeIdx = i;
                            GUILayout.EndHorizontal();
                        }
                        if (removeIdx >= 0) m_OcrExtraRegions.RemoveAt(removeIdx);
                        if (GUILayout.Button("➕ 新增區域", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            // 預設值取「畫面上方帶」— 本 List 的典型用途 (底邊離下緣 85%, 高 10% → 覆蓋 85%~95%)
                            m_OcrExtraRegions.Add(new OcrBand(0.85f, 0.1f));
                            m_ShowOcrExtraRegions = true;
                        }
                    }
                }

                DrawOcrBandPreview();
                GUILayout.Label("  ⓘ 設定隨「儲存設定 / 開始·停止錄影」寫入 config; daemon 偵測 band 改動自動重起 OCR pool 套用。",
                    new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic });
            }
        }

        // 區塊職責: 開播 / 停播的酒館廣播（酒保 NPC 身分）— 讓同事與 Discord 端知道 Tim 開台了。
        // 物理意義: **事件的所有者是這顆按鈕**，不是 daemon 的存活狀態。
        //          舊實作掛在 daemon 端「cfg.enabled 由 false 翻成 true」的 transition 上，
        //          而 2026-07-28 daemon 生命週期改成「存活綁 enabled」之後，
        //          daemon 一啟動 enabled 就已經是 true → **那個 transition 再也不會發生**，
        //          停播時 daemon 直接被 kill → 也沒機會發。start/stop 兩個廣播就這樣一起消失，
        //          而且沒有任何錯誤訊息（實證：酒館最後一筆是 2026-07-27，daemon log 內
        //          announce 出現 0 次 —— 那支函式成功與失敗都會 log，所以是根本沒被呼叫）。
        //          搬到按鈕端＝廣播不再需要從別的狀態推導出來。
        // 數值影響: 純 append 一則 tavern 訊息（sender=tavern-keeper）；失敗只 LogWarning，
        //          絕不擋開始/停止錄影本身 —— 廣播是通知，不是錄影的前置條件。
        void PostStreamAnnounce(bool iStart)
        {
            try
            {
                string body;
                string ev;
                if (iStart)
                {
                    string title = (m_StreamTitle ?? "").Trim();
                    string titleLine = string.IsNullOrEmpty(title) ? "" : $"📺 本場節目: {title}\n";
                    body = "🍺📹 *咳咳, 諸位.* ScreenStream 直播開始啦!\n"
                         + titleLine
                         + $"Tim 開了錄影機, 每秒一張快照 ({m_Resolution} @ {m_Fps} fps, monitor={m_Monitor}).\n"
                         + "想看 Tim 在玩什麼就 Read AgentCommands/_screenstream/_latest.jpg 吧.\n"
                         + "——酒保提醒: 不 @ everyone 不擾人, 大家自由觀察.";
                    ev = "screenstream-start";
                }
                else
                {
                    body = "🍺⏹ *直播結束.* ScreenStream 已停止 capture.\n"
                         + "ring buffer 的畫面 10 min rolling 之後自動覆蓋, 想找剛剛某張的同事們抓緊看.\n"
                         + "——酒保關燈了.";
                    ev = "screenstream-stop";
                }
                UCL_ChatTavernIO.AppendMessage("tavern", new UCL_ChatMessage
                {
                    sender_id = UCL_ChatTavernIO.BartenderSenderId,
                    sender_name = "酒保",
                    sender_persona = UCL_ChatTavernIO.BartenderSenderId,
                    kind = "chat",
                    body = body,
                    meta = new Dictionary<string, string>
                    {
                        { "tag", "bartender-rule-announce" },
                        { "category", "meta" },
                        { "event", ev },
                    },
                });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamPage] 開播/停播廣播失敗（不影響錄影）: {e.Message}");
            }
        }

        // 區塊職責: 一條帶的四個 slider — 主帶與額外區域共用, 避免兩處各長一套欄位而漂移。
        // 物理意義: 垂直兩個 (底部原點) + 水平兩個 (中心 / 寬度)。
        // 數值影響: 寬度下限 0.05 (再窄字幕一定被切); 中心 0~1 且會被 clamp 進畫面。
        static void DrawBandFields(OcrBand iBand, string iPrefix)
        {
            string p = string.IsNullOrEmpty(iPrefix) ? "" : iPrefix;
            iBand.YBottomPct = UnityEditor.EditorGUILayout.Slider(p + "起始 y (0=畫面下方)", iBand.YBottomPct, 0f, 1f);
            iBand.HPct = UnityEditor.EditorGUILayout.Slider(p + "高度 (從 y 往上長)", iBand.HPct, 0.02f, 0.5f);
            iBand.WPct = UnityEditor.EditorGUILayout.Slider(p + "寬度 (1=滿寬)", iBand.WPct, 0.05f, 1f);
            iBand.XCenterPct = UnityEditor.EditorGUILayout.Slider(p + "x 中心 (0.5=正中)", iBand.XCenterPct, 0f, 1f);
        }

        // 區塊職責: 字幕帶視覺化 — 底圖=當前畫面 (錄影中有 _latest.jpg) 或灰框 (16:9);
        //          橘半透明=主字幕帶, 青半透明=額外判定區域。
        // 物理意義: 底部原點比例 → GUI rect (top-down): bandTop = box.yMax - (y_bottom + h) * H。
        void DrawOcrBandPreview()
        {
            bool hasImg = m_PreviewTexture != null && m_PreviewTexture.width > 4;
            GUILayout.Label(hasImg
                ? "字幕範圍預覽（底圖＝當前畫面; 橘＝主字幕帶, 青＝額外區域）:"
                : "字幕範圍預覽（灰框＝螢幕; 橘＝主字幕帶, 青＝額外區域）:",
                new GUIStyle(GUI.skin.label) { fontSize = 11 });
            float aspect = hasImg ? (float)m_PreviewTexture.width / Mathf.Max(1, m_PreviewTexture.height) : (16f / 9f);
            float vizW = hasImg ? 360f : 240f;
            float vizH = vizW / Mathf.Max(0.1f, aspect);
            Rect box = GUILayoutUtility.GetRect(vizW, vizH, GUILayout.ExpandWidth(false));
            if (hasImg) GUI.DrawTexture(box, m_PreviewTexture, ScaleMode.StretchToFill);
            else UnityEditor.EditorGUI.DrawRect(box, new Color(0.13f, 0.13f, 0.16f));
            DrawRectBorder(box, new Color(0.65f, 0.65f, 0.72f), 1.5f);
            // 主帶 (橘) + 額外區域 (青) — 同一套底部原點轉換
            DrawBandRect(box, m_OcrBand, new Color(1f, 0.6f, 0.1f, 0.5f), new Color(1f, 0.7f, 0.2f));
            foreach (var r in m_OcrExtraRegions)
                DrawBandRect(box, r, new Color(0.2f, 0.85f, 0.9f, 0.4f), new Color(0.3f, 0.9f, 1f));
            int pctLo = Mathf.RoundToInt(Mathf.Clamp01(m_OcrBand.YBottomPct) * 100f);
            int pctHi = Mathf.RoundToInt(Mathf.Clamp01(m_OcrBand.YBottomPct + m_OcrBand.HPct) * 100f);
            string wDesc = m_OcrBand.WPct >= 0.999f
                ? "滿寬"
                : $"寬 {Mathf.RoundToInt(m_OcrBand.WPct * 100f)}%、中心 {m_OcrBand.XCenterPct:0.##}";
            GUILayout.Label($"主字幕帶：距畫面下緣 {pctLo}% ~ {pctHi}%（{wDesc}）。字幕沒被橘框罩到就調「起始 y」／「寬度」。",
                new GUIStyle(GUI.skin.label) { fontSize = 10 });
        }

        // 單一帶位 → 預覽矩形 (底部原點轉 GUI top-down 座標; 水平用中心+寬; clamp 不出框)
        static void DrawBandRect(Rect iBox, OcrBand iBand, Color iFill, Color iBorder)
        {
            float yB = Mathf.Clamp01(iBand.YBottomPct);
            float h = Mathf.Clamp01(iBand.HPct);
            if (h <= 0f || yB >= 1f) return;
            float top = iBox.yMax - Mathf.Min(1f, yB + h) * iBox.height;
            float bottom = iBox.yMax - yB * iBox.height;
            if (bottom - top < 0.5f) return;
            if (!iBand.TryHorizontal(out float left, out float right)) return;
            var band = new Rect(iBox.x + left * iBox.width, top, (right - left) * iBox.width, bottom - top);
            UnityEditor.EditorGUI.DrawRect(band, iFill);
            DrawRectBorder(band, iBorder, 1f);
        }

        // 畫矩形邊框（四條 t 粗細的線）
        static void DrawRectBorder(Rect r, Color c, float t)
        {
            UnityEditor.EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            UnityEditor.EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            UnityEditor.EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            UnityEditor.EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        // 讀 PID 檔 → 驗該 PID 是否為存活 process。檔缺 / 內容非數字 / process 不存在 → false。
        static bool IsPidAlive(string pidPath)
        {
            try
            {
                if (!File.Exists(pidPath)) return false;
                string s = File.ReadAllText(pidPath).Trim();
                if (!int.TryParse(s, out int pid) || pid <= 0) return false;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    return !p.HasExited;
                }
                catch { return false; }   // ArgumentException = 無此 process
            }
            catch { return false; }
        }

        static string GetRepoRoot()
        {
            // repo 根 — 走 UCL_RepoPath.RepoRoot (.git walk, 跨專案安全)。
            // 舊版用 Application.dataPath「上兩層」，假設 EoV 式巢狀 (repo/CardGame/Assets)；但本專案
            // project 根 = repo 根，上兩層會多爬一層飛出 repo → PID/config/latest 全讀錯路徑，頁面永遠顯示
            // DEAD、toggle 寫到幻影路徑 daemon 收不到 (2026-07-27 Tim QA)。
            return UCL_RepoPath.RepoRoot;
        }
    }
}
#endif
