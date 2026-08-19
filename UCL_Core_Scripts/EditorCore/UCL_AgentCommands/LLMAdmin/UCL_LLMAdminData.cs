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

    /// <summary>`status` 的回傳。</summary>
    public class LLMStatusResult : UnityJsonSerializable
    {
        public bool ollama_installed = false;
        public string ollama_path = "";
        public bool on_path = false;        // 找得到但不在 PATH ⇒ 這個 Editor 行程的環境是舊的
        public string version = "";
        public bool service_reachable = false;
        public int installed_count = 0;
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
