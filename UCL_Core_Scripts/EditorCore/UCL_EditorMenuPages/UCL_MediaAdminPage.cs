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
        bool m_OcrEnabled = true;       // 字幕 OCR 開關
        int m_OcrWorkers = 1;           // OCR worker 數
        float m_OcrYPct = 0.78f;        // 字幕帶起始 y 比例 (0~1)
        float m_OcrHPct = 0.12f;        // 字幕帶高度比例 (0~1)
        float m_OcrMinConf = 0.5f;      // OCR 最低信度過濾 (0~1)

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

        // 區塊 2：依賴安裝 — whisper / torch CUDA / rapidocr
        void DrawInstallPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>2. 依賴安裝</b>（pip --user 落 user-site；torch 較大，請耐心等）", WrapStyle);
                EditorGUILayout.HelpBox(
                    "安裝走 media_admin.py（op=install），跨專案/機器可重現。" +
                    "whisper 預設拉 CPU 版 torch；有 NVIDIA GPU 再按第二顆換 CUDA 版加速轉錄。",
                    MessageType.None);
                using (new EditorGUI.DisabledScope(m_Busy))
                {
                    if (GUILayout.Button("📦 安裝 STT 依賴（openai-whisper + soundcard + numpy）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(30)))
                        RunOp("安裝 STT 依賴", "install --stt", 1800000);
                    if (GUILayout.Button("⚡ torch 換 CUDA 版（cu124 wheel，GPU 加速）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("torch 換 CUDA 版", "install --torch-cuda", 1800000);
                    if (GUILayout.Button("🔡 安裝 OCR 依賴（rapidocr-onnxruntime）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("安裝 OCR 依賴", "install --ocr", 1800000);
                    if (GUILayout.Button("⚡ OCR 換 CUDA 版（onnxruntime-gpu，GPU 加速）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("OCR 換 CUDA 版", "install --ocr-cuda", 1800000);
                }
            }
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
                m_OcrYPct = EditorGUILayout.Slider("字幕帶起始 y (0~1)", m_OcrYPct, 0f, 1f);
                m_OcrHPct = EditorGUILayout.Slider("字幕帶高度 (0~1)", m_OcrHPct, 0.02f, 0.5f);
                m_OcrMinConf = EditorGUILayout.Slider("最低信度過濾", m_OcrMinConf, 0f, 1f);

                using (new EditorGUI.DisabledScope(m_Busy || !m_ConfigLoaded))
                {
                    if (GUILayout.Button("💾 套用 OCR 設定", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        ApplyConfig(sttOnly: false);
                }
            }
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
                sb.Append($" ocr_y_pct={m_OcrYPct.ToString("0.###", CultureInfo.InvariantCulture)}");
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
                m_OcrYPct = f.GetFloat("ocr_y_pct", m_OcrYPct);
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
        // 區塊：async 執行 — 委派 UCL_MediaAdminRunner，完成後回主執行緒更新 UI + Repaint
        // 物理意義：重活 (install/試錄) 全在背景 python，Editor 不凍結；完成才刷 UI。
        // ===========================================================
        void RunOp(string label, string argLine, int timeoutMs)
        {
            if (m_Busy && label != "讀取設定") return;   // 讀取設定允許與狀態並行 (皆輕量唯讀)
            if (label != "讀取設定") { m_Busy = true; m_BusyLabel = label; }
            var win = EditorWindow.focusedWindow;   // 捕捉當前宿主視窗，完成後主動 Repaint
            RunOpAsync(label, argLine, timeoutMs, win).Forget();
        }

        async UniTaskVoid RunOpAsync(string label, string argLine, int timeoutMs, EditorWindow win)
        {
            var r = await UCL_MediaAdminRunner.RunAsync(argLine, CancellationToken.None, timeoutMs);
            await UniTask.SwitchToMainThread();
            if (label != "讀取設定")
            {
                m_Busy = false;
                m_BusyLabel = "";
                m_LastOutput = $"[{label}]\n{r.DisplayText}";
            }
            if (label == "狀態") m_StatusText = r.DisplayText;
            if (label == "讀取設定") ParseConfigJson(r.Stdout ?? "");
            // 套用設定成功後回讀一次，確保頁面顯示 = 檔案實況 (cross-layer 驗證，不只信自己送出的值)
            if (label.StartsWith("套用") && r.ExitCode == 0)
                RunOp("讀取設定", "get-config", 30000);
            if (win != null) win.Repaint();
        }
    }
}
#endif
