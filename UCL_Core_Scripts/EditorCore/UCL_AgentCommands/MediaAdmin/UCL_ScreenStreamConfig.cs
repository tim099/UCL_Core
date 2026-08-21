// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/21 2026
// ScreenStream `_config.json` 的 typed model（取代 Page 與 Cmd_StreamWatch 兩邊各自的 JsonData 逐鍵讀寫）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    // ===========================================================
    // 區塊職責：`AgentCommands/_screenstream/_config.json` 的資料模型 —— **三個讀寫端共用同一份**
    //          （`UCL_ScreenStreamPage` / `Cmd_StreamWatch` / python `screenstream_daemon.py`）。
    // 物理意義：這份檔是「錄影這件事現在是什麼狀態」的唯一真相源。過去 C# 兩端各自用
    //          `JsonData` 逐鍵讀寫，於是同一個鍵在兩處各打一次字 —— 打錯不會編譯錯、也不會執行錯，
    //          **只會讀回預設值**，而讀回預設值長得跟「錄影沒開」一模一樣。
    // 數值影響：序列化結果與手搭 JsonData 的格式**逐鍵相同**（鍵序可能不同，兩端都按鍵取值）。
    //          既有檔不需遷移。
    // ===========================================================
    /// <summary>
    /// ScreenStream daemon 設定（`AgentCommands/_screenstream/_config.json`）。
    /// </summary>
    /// <remarks>
    /// ⚠⚠ **欄位名刻意不走 `m_PascalCase` 慣例** —— 欄位名**就是 JSON 鍵名**
    /// （<see cref="UnityJsonSerializable"/> 走 `FieldNameUnityVer`，只脫 `m_` 前綴，其餘原樣輸出）。
    /// 這份檔的主要讀取端是 **python daemon**（`Tools~/AgentCommands/screenstream_daemon.py`），
    /// 欄位改名 ＝ JSON 鍵跟著改 ＝ daemon 那邊 `cfg.get("enabled")` 拿到 None ⇒
    /// **當成沒開錄影，而且不報錯**。動任何欄位名要同時改 python 端。
    /// </remarks>
    public class UCL_ScreenStreamConfig : UnityJsonSerializable
    {
        // ── daemon 狀態欄位（daemon 擁有；C# 只讀不改，但寫回時必須原樣保留）──
        /// <summary>錄影中。⚠ python 端 `if cfg.get("enabled")` 直接判真假 ⇒ 必須是**原生 bool**。</summary>
        public bool enabled = false;
        /// <summary>`enabled` 最後一次**翻轉**的時刻（UTC ISO）。⚠ 是翻轉不是「停止」—— 開播也會戳。</summary>
        public string enabled_changed_at = "";
        /// <summary>daemon 啟動時刻（UTC ISO），daemon 寫。</summary>
        public string started_at = "";
        /// <summary>已擷取張數（ring buffer 位置由此推），daemon 寫。</summary>
        public int frame_count = 0;

        // ── 擷取設定（Page 擁有）──
        public int fps = 1;
        public int max_frames = 600;
        public string resolution = "1080p";
        public int quality = 65;
        public string monitor = "primary";
        public string format = "jpg";

        // ── 錄播（ring 之外的另一份流）──
        public bool recording_enabled = false;
        public string recording_name = "";
        /// <summary>剩餘磁碟低於此值（MB）時 daemon 自動停錄。</summary>
        public int recording_stop_free_mb = 1024;

        // ── 聲音圖譜 overlay ──
        public bool audio_viz_enabled = false;
        public string audio_viz_mode = "stereo_eq";
        public string audio_viz_position = "bottom-stretch";

        // ── OCR ──
        /// <summary>OCR 實效開關（daemon 讀它決定要不要跑 worker）。</summary>
        public bool ocr_enabled = false;
        public int ocr_workers = 2;
        /// <summary>主帶：距畫面**底部**的百分比。舊檔的 `ocr_y_pct`（距頂端）在載入時換算進來。</summary>
        public float ocr_y_bottom_pct = 0f;
        public float ocr_h_pct = 0.12f;
        public float ocr_x_center_pct = 0.5f;
        public float ocr_w_pct = 1f;
        /// <summary>主帶是否啟用。**舊檔缺席＝開啟**（見 <see cref="DeserializeFromJson"/>）。</summary>
        public bool ocr_main_enable = true;
        public float ocr_min_conf = 0.5f;
        public bool ocr_adaptive = true;
        /// <summary>額外辨識區域。巢狀 model 由 <see cref="UnityJsonSerializable"/> 自動存取
        /// （`List&lt;T&gt;` where T 也是 model ⇒ 不必手刻解析）。舊檔的 `[y,h]` 陣列形另有 shim，見 DeserializeFromJson。</summary>
        public List<UCL_ScreenStreamOcrRegion> ocr_extra_regions = new List<UCL_ScreenStreamOcrRegion>();

        // ── STT ──
        /// <summary>STT **實效**開關 ＝ `enabled &amp;&amp; stt_setting`（daemon 讀這個）。</summary>
        public bool stt_enabled = false;
        /// <summary>STT **意圖**開關（Page 上那個勾）。停播時 stt_enabled 會被關掉，但意圖要留著。</summary>
        public bool stt_setting = false;
        public string stt_backend = "openai-whisper";
        public bool stt_vad_filter = false;
        public string stt_model = "small";
        public string stt_lang = "";
        public int stt_chunk_sec = 15;
        /// <summary>人名詞彙偏置（每場開播會被清空 —— 上一場的人名不該造成跨場幻聽）。</summary>
        public string stt_prompt = "";
        public float stt_rms_gate = 0.005f;
        public float stt_no_speech_max = 0.6f;
        public float stt_logprob_min = -1f;

        // ── 本場節目 ──
        /// <summary>開播酒館廣播「📺 本場節目」那行的唯一來源（**每片一份**，換片要改）。</summary>
        public string stream_title = "";

        public int _schema_version = 1;

        // ===========================================================
        // 區塊職責：**未知鍵原樣保留** —— 這份檔有 C# 以外的寫入端（python daemon）。
        // 物理意義：typed model 只認得自己宣告的欄位；daemon 若加了新鍵而 C# 這邊照 model 寫回去，
        //          那個鍵就**靜默消失**，daemon 下次讀不到只好退回它自己的預設值 ——
        //          而「退回預設值」看起來跟「本來就沒設定」一模一樣。
        // 數值影響：只在序列化時補回；已宣告的欄位一律以 model 為準（不會被舊值蓋回去）。
        // ===========================================================
        [ATTR.UCL_HideInJson] JsonData m_Unknown = null;

        /// <summary>本次載入時檔案裡實際出現過的鍵（給需要分辨「缺席」與「false」的呼叫端用）。</summary>
        [ATTR.UCL_HideInJson] HashSet<string> m_PresentKeys = new HashSet<string>();

        /// <summary>該鍵在載入的那份檔裡存在嗎（缺席與 false 是兩件事）。</summary>
        public bool HasKey(string iKey) => m_PresentKeys != null && m_PresentKeys.Contains(iKey);

        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            m_PresentKeys = new HashSet<string>();
            m_Unknown = new JsonData();
            if (iJson == null) return;

            var aDic = iJson.GetJsonDic();
            if (aDic != null)
            {
                var aKnown = KnownKeys();
                foreach (var kv in aDic)
                {
                    m_PresentKeys.Add(kv.Key);
                    if (!aKnown.Contains(kv.Key)) m_Unknown[kv.Key] = kv.Value;
                }
            }

            // ── 遷移①：`ocr_y_pct`（距頂端）→ `ocr_y_bottom_pct`（距底部）
            //   舊檔只有前者。換算住在 model 而不是呼叫端 —— 兩個 GUI／Cmd 各換算一次遲早給出不同答案。
            if (!m_PresentKeys.Contains("ocr_y_bottom_pct") && m_PresentKeys.Contains("ocr_y_pct"))
                ocr_y_bottom_pct = Mathf.Clamp01(1f - iJson.GetFloat("ocr_y_pct", 0.78f) - ocr_h_pct);

            // ── 遷移②：`stt_setting`（意圖）缺席時沿用 `stt_enabled`（實效）
            //   舊檔只有 stt_enabled；不接的話舊使用者的 STT 意圖會在第一次存檔時被靜默關掉。
            if (!m_PresentKeys.Contains("stt_setting")) stt_setting = stt_enabled;

            // ── ocr_extra_regions：**物件形由 base 自動反序列化**（巢狀 model 內建支援，不手刻）。
            //   這裡只補一個窄 shim：舊檔可能把一筆寫成 `[y,h]` / `[y,h,xc,w]` 陣列
            //   （對齊 python `normalize_regions` 的寬容輸入）。
            //   ⚠ 為什麼不能靜默放過：陣列形的元素在 base 眼裡沒有任何已知欄位 ⇒ 全部落 0，
            //   而 `h_pct=0` 是**一條沒有面積的辨識帶** —— worker 照跑、永遠零產出，
            //   看起來跟「這段沒字幕」一模一樣。所以要嘛正確轉換，要嘛出聲。
            //   移除條件：確認線上與所有備份 config 的 ocr_extra_regions 都是物件形之後。
            MigrateLegacyRegionArrays(iJson);
        }

        public override JsonData SerializeToJson()
        {
            var aData = base.SerializeToJson();

            // ⚠ **bool 一律寫回原生 JSON bool**，不是 "True"/"False" 字串。
            //   UCL_Json 的舊慣例把 bool 存成字串，C# 載入端雙接所以看不出來 ——
            //   但 python `json.loads` 拿到字串 `"False"`，而它在 Python 裡是 **truthy** ⇒
            //   daemon 的 `if cfg.get("enabled")` 會永遠成立（＝停不掉的錄影）。
            //   同族血證見 UCL_SessionBase（2026-08-18 自由時間那次）。
            aData["enabled"] = new JsonData(enabled);
            aData["recording_enabled"] = new JsonData(recording_enabled);
            aData["audio_viz_enabled"] = new JsonData(audio_viz_enabled);
            aData["ocr_enabled"] = new JsonData(ocr_enabled);
            aData["ocr_main_enable"] = new JsonData(ocr_main_enable);
            aData["ocr_adaptive"] = new JsonData(ocr_adaptive);
            aData["stt_enabled"] = new JsonData(stt_enabled);
            aData["stt_setting"] = new JsonData(stt_setting);
            aData["stt_vad_filter"] = new JsonData(stt_vad_filter);

            // ⚠ 只在**空 List** 時補一個空陣列 —— 非空時 base 的巢狀序列化已經正確，不重做一次。
            //   空的走 base 會讓整個鍵消失（`SaveDataToJson` 的 IList 分支不把空 JsonData 標成 array）。
            //   🩸 2026-08-21 round-trip 實測：原檔 `"ocr_extra_regions": []`，寫回後**鍵不見了**。
            //   後果不會叫（python `cfg.get(...,[])` 照樣拿到 []），但檔案少一個欄位而沒人會發現。
            if (ocr_extra_regions == null || ocr_extra_regions.Count == 0)
                aData["ocr_extra_regions"] = new JsonData().ToArray();

            // 未知鍵補回（已宣告欄位不被覆蓋 —— model 才是那些鍵的擁有者）
            if (m_Unknown != null)
            {
                var aDic = m_Unknown.GetJsonDic();
                if (aDic != null)
                    foreach (var kv in aDic)
                        if (!aData.Contains(kv.Key)) aData[kv.Key] = kv.Value;
            }
            return aData;
        }

        static HashSet<string> s_KnownKeys = null;
        /// <summary>本 model 宣告過的 JSON 鍵（用反射取，加欄位時不必回頭維護第二份清單）。</summary>
        static HashSet<string> KnownKeys()
        {
            if (s_KnownKeys != null) return s_KnownKeys;
            var aSet = new HashSet<string>();
            foreach (var f in typeof(UCL_ScreenStreamConfig).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Instance))
            {
                if (f.GetCustomAttributes(typeof(ATTR.UCL_HideInJsonAttribute), true).Length > 0) continue;
                aSet.Add(UCL.Core.UCL_StaticFunctions.FieldNameUnityVer(f.Name));
            }
            s_KnownKeys = aSet;
            return s_KnownKeys;
        }

        // 區塊職責：舊檔把 region 寫成陣列 `[y,h]` / `[y,h,xc,w]` 時，就地換成物件形。
        // 物理意義：**只處理 base 認不出來的那一種形狀** —— 物件形一律由 base 的巢狀反序列化負責，
        //          不在這裡重寫一份解析（兩份解析遲早各說各話，而兩邊都不報錯）。
        // 數值影響：逐 index 對位覆寫；水平兩欄缺席落回 0.5 / 1.0（＝滿寬，加這功能之前的固定行為）。
        void MigrateLegacyRegionArrays(JsonData iJson)
        {
            if (iJson == null || !iJson.Contains("ocr_extra_regions")) return;
            var aArr = iJson["ocr_extra_regions"];
            if (aArr == null || !aArr.IsArray) return;
            if (ocr_extra_regions == null) ocr_extra_regions = new List<UCL_ScreenStreamOcrRegion>();
            for (int i = 0; i < aArr.Count; i++)
            {
                var e = aArr[i];
                if (e == null || !e.IsArray || e.Count < 2) continue;      // 物件形 ⇒ base 已處理
                var aBand = new UCL_ScreenStreamOcrRegion
                {
                    y_bottom_pct = e[0].GetFloat(0f),
                    h_pct = e[1].GetFloat(0f),
                    x_center_pct = e.Count >= 4 ? e[2].GetFloat(0.5f) : 0.5f,
                    w_pct = e.Count >= 4 ? e[3].GetFloat(1f) : 1f,
                };
                if (i < ocr_extra_regions.Count) ocr_extra_regions[i] = aBand;
                else ocr_extra_regions.Add(aBand);
            }
        }

        // ===========================================================
        // 區塊職責：檔案 IO —— 讀寫這份 config 的**唯一入口**。
        // 數值影響：`Load` 讀不到 / 解析失敗回 null（呼叫端自己決定怎麼說，不在這裡塞預設值當事實）；
        //          `Save` 走 UTF-8 無 BOM（python 端 json.loads 吃 BOM 會炸）。
        // ===========================================================
        /// <summary>讀設定；檔案不存在或解析失敗回 null（**不回一個空設定** —— 那會讓「讀不到」看起來像「都沒開」）。</summary>
        public static UCL_ScreenStreamConfig Load(string iPath)
        {
            try
            {
                if (string.IsNullOrEmpty(iPath) || !File.Exists(iPath)) return null;
                var aJd = JsonData.ParseJson(File.ReadAllText(iPath, Encoding.UTF8));
                if (aJd == null) return null;
                var aCfg = new UCL_ScreenStreamConfig();
                aCfg.DeserializeFromJson(aJd);
                return aCfg;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStreamConfig] 讀取失敗 {iPath}：{e.Message}");
                return null;
            }
        }

        /// <summary>寫回設定（UTF-8 無 BOM）。丟例外由呼叫端處理 —— 存檔失敗不該被吞成「已存」。</summary>
        public void Save(string iPath)
        {
            string aDir = Path.GetDirectoryName(iPath);
            if (!string.IsNullOrEmpty(aDir) && !Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllText(iPath, SerializeToJson().ToJsonBeautify(), new UTF8Encoding(false));
        }

        // ===========================================================
        // 區塊職責：錄影開關的**唯一寫入規則**（GUI 按鈕與 Cmd_StreamWatch step=capture 都套這一份）。
        // 物理意義：翻轉 `enabled` 從來不只是改一個 bool，它連帶三件事：
        //          ① 戳 `enabled_changed_at`（下游結算要「什麼時候停的」，不是「什麼時候被發現的」）
        //          ② 連動 `stt_enabled = enabled && stt_setting`（意圖 vs 實效）
        //          ③ 其餘欄位不動
        //          這三件若在兩個地方各寫一次，遲早給出不同答案。
        // 數值影響：同值重複寫入**不重戳時刻**（否則「一直在停」會被讀成「剛剛才停」）。
        // ===========================================================
        /// <summary>套用錄影開關（含 enabled_changed_at 與 stt_enabled 連動）。回傳是否真的改變了狀態。</summary>
        public bool ApplyEnabled(bool iOn)
        {
            bool aChanged = enabled != iOn;
            if (aChanged)
                enabled_changed_at = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            enabled = iOn;
            stt_enabled = iOn && stt_setting;
            return aChanged;
        }
    }

    /// <summary>OCR 額外辨識區域（`ocr_extra_regions` 的一筆）。欄位名即 JSON 鍵名。</summary>
    public class UCL_ScreenStreamOcrRegion : UnityJsonSerializable
    {
        public float y_bottom_pct = 0f;
        public float h_pct = 0f;
        public float x_center_pct = 0.5f;
        public float w_pct = 1f;
        public bool enable = true;

        /// <summary>bool 寫回原生（同 <see cref="UCL_ScreenStreamConfig"/> 的理由：python 端會讀）。</summary>
        public override JsonData SerializeToJson()
        {
            var aData = base.SerializeToJson();
            aData["enable"] = new JsonData(enable);
            return aData;
        }
    }
}
#endif
