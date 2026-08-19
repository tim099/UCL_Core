// 區塊職責：llm_admin.py 回傳 JSON 的 typed model（C# 端唯一的欄位定義處）。
// 物理意義：python 是本地 LLM 管理的真相源，本檔是它輸出的**型別化鏡像** ——
//          裸 JsonData 逐鍵讀寫時，鍵名打錯不會編譯錯也不會執行錯，**只會讀回預設值**，
//          而讀回預設值長得跟「這個模型不存在」一模一樣（Tim 2026-08-18 拍板）。
// 數值影響：純資料；不含行為。
// ⚠ **欄位名即 JSON 鍵名**：`JsonConvert` 的 Unity 模式只脫 `m_` 前綴，不做 snake_case 轉換。
//   ⇒ 這裡刻意**不走 `m_PascalCase` 命名**，直接用 python 那端的 snake_case，
//     否則鍵名對不上、每個欄位都靜默讀成 0/空字串。改任一端都要同時改另一端。
#if UNITY_EDITOR
using System.Collections.Generic;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.LLMAdmin
{
    /// <summary>已安裝的一顆模型（`ollama list` 的一行）。</summary>
    public class LLMInstalledModel : UnityJsonSerializable
    {
        public string id = "";
        public string size = "";
        /// <summary>`ollama ps` 的 PROCESSOR 欄（"100% GPU" / "40%/60% CPU/GPU"）—— 只有載入清單才有。</summary>
        public string processor = "";
    }

    /// <summary>目錄裡的一顆候選模型（策展清單 ＋ 對帳出來的安裝狀態）。</summary>
    public class LLMCatalogEntry : UnityJsonSerializable
    {
        public string id = "";
        public string name = "";
        public string params_ = "";     // ⚠ `params` 是 C# 關鍵字 —— 見下方 FixUp
        public float size_gb = 0f;      // **下載量／磁碟佔用**（Q4_K_M 權重檔本身）
        public float vram_gb = 0f;      // **顯存需求估值** ＝ 權重 ＋ KV cache ＋ 執行期開銷
        public bool fits_budget = false;// 這張卡的顯存預算放不放得下（門檻在 python 端）
        public int zh = 0;              // 中文能力 0-5（策展評分，不是 benchmark）
        public string family = "";
        public bool recommend = false;
        public string note = "";
        public bool installed = false;
        public bool exact = false;      // 精確 tag 命中 vs 變體命中
    }

    // ═══════════════════════════════════════════════════════════════════
    // 區塊職責：顯存讀數與門檻（`status` 與 `list` 兩支都回同一組扁平欄位）。
    // 物理意義：門檻不是常數 —— 它有**來源**，而來源決定這個數字可不可信：
    //            manual   使用者自己填的（他知道要留多少給 Unity）
    //            gpu_free nvidia-smi 的 free 欄（會隨 Unity 開了什麼而變動）
    //            gpu_total 卡的總量（固定值，但不代表現在放得下）
    //            fallback **偵測失敗的保底值** —— 這個一定要在畫面上講出來
    // 數值影響：只影響目錄的 fits_budget 過濾；不影響安裝與實際載入。
    // ⚠ 欄位名即 JSON 鍵名（同本檔開頭的規範）—— 與 llm_admin.py 的鍵名逐字對齊，
    //   改任一端都要同時改另一端；打錯不會編譯錯，只會靜默讀成 0，
    //   而「門檻 0GB」跟「這張卡什麼都放不下」在畫面上長得一樣。
    // ═══════════════════════════════════════════════════════════════════
    public class LLMVramInfo : UnityJsonSerializable
    {
        public float vram_budget_gb = 0f;        // 這次實際採用的門檻
        public string vram_budget_source = "";   // manual / gpu_free / gpu_total / fallback
        public string vram_basis = "free";       // 自動偵測時拿哪一欄當門檻
        public string vram_budget_note = "";     // python 端寫的一句人話說明
        public float vram_total_gb = 0f;
        public float vram_free_gb = 0f;
        public float vram_used_gb = 0f;
        public string gpu_name = "";
        public bool gpu_detected = false;        // false ＝ 沒量到（不是「沒有卡」，可能是 PATH 舊的）
        public string vram_error = "";

        public bool IsManual => vram_budget_source == "manual";
        public bool IsFallback => vram_budget_source == "fallback";

        /// <summary>來源的人話標籤 —— 保底值刻意帶 ⚠，那個數字不是量到的。</summary>
        public string SourceLabel
        {
            get
            {
                switch (vram_budget_source)
                {
                    case "manual": return "手動指定";
                    case "gpu_free": return "偵測·可用(free)";
                    case "gpu_total": return "偵測·總量(total)";
                    case "fallback": return "⚠ 保底值（沒量到）";
                    default: return string.IsNullOrEmpty(vram_budget_source) ? "(未載入)" : vram_budget_source;
                }
            }
        }
    }

    /// <summary>`status` 的回傳。</summary>
    public class LLMStatusResult : UnityJsonSerializable
    {
        public bool ollama_installed = false;
        public string ollama_path = "";
        public bool on_path = false;        // 找得到但不在 PATH ⇒ 這個 Editor 行程的環境是舊的
        public string version = "";
        public bool service_reachable = false;
        public int installed_count = 0;
        public int loaded_count = 0;     // 現在佔著顯存的顆數（`ollama ps`）—— 與 installed 是兩件事
        public string error = "";
        public string hint = "";
    }

    /// <summary>
    /// 解析輔助 —— `params` 撞 C# 關鍵字，只好在這裡手動補一手。
    /// 放在同一檔、緊鄰欄位定義：這種例外散出去就再也沒人記得它存在。
    /// </summary>
    public static class LLMAdminParse
    {
        public static List<LLMCatalogEntry> Catalog(JsonData iJson)
        {
            var aList = new List<LLMCatalogEntry>();
            if (iJson == null || !iJson.Contains("catalog")) return aList;
            var aArr = iJson["catalog"];
            for (int i = 0; i < aArr.Count; i++)
            {
                var aEntry = new LLMCatalogEntry();
                aEntry.DeserializeFromJson(aArr[i]);
                aEntry.params_ = aArr[i].GetString("params", "");   // 鍵名與欄位名不同的唯一一格
                aList.Add(aEntry);
            }
            return aList;
        }

        /// <summary>
        /// 顯存讀數／門檻 —— `status` 與 `list` 的回傳都是**同一組扁平鍵**，所以共用這一支。
        /// ⚠ 回 null 代表這次回傳裡沒有這組鍵（舊版 python）—— 呼叫端要能分辨「沒有」與「0」。
        /// </summary>
        public static LLMVramInfo Vram(JsonData iJson)
        {
            if (iJson == null || !iJson.Contains("vram_budget_gb")) return null;
            var aInfo = new LLMVramInfo();
            aInfo.DeserializeFromJson(iJson);
            return aInfo;
        }

        public static List<LLMInstalledModel> Installed(JsonData iJson, string iKey = "installed")
        {
            var aList = new List<LLMInstalledModel>();
            if (iJson == null || !iJson.Contains(iKey)) return aList;
            var aArr = iJson[iKey];
            for (int i = 0; i < aArr.Count; i++)
            {
                var aModel = new LLMInstalledModel();
                aModel.DeserializeFromJson(aArr[i]);
                aList.Add(aModel);
            }
            return aList;
        }
    }
}
#endif
