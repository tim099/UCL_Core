// 區塊職責：Awakening 狀態檔的 typed models — persona 檔 / registry meta / session lock
//          （Plan_Awakening_Flow_Simplification §8.8 R15；GoodMorning Cmd 遷移的資料層）。
// 物理意義：與 awakening.py 共讀同一批 JSON —
//          AwakenInit/personas/<name>.json（persona 檔）、AwakenInit/_registry_meta.json（agent→bank）、
//          _session/_persona_<name>.json（lock）。schema 由 Python 端先行定義，改欄位務必兩端同看。
// 數值影響：讀取走 typed class（UnityJsonSerializable）；⚠ 寫回一律 patch-write —— 載原 JsonData、
//          只改自己擁有的欄、存回。SerializeToJson 只吐 class 有宣告的欄位，整包 roundtrip 會把
//          identity_vector(64 維) / vector_history / persona_spec 等未建模欄位**靜默抹掉**（R15 硬規則）。
// 命名注意：欄位名必須與 JSON key **逐字相同**（snake_case）—— UnityJsonSerializable 的欄名匹配
//          是 exact match（僅剝 m_ 前綴），camelCase 對不上時不報錯、靜默留預設值
//          （UCL_PersonaAgentAdminPage.PersonaRow 的 wakeCount/layerRole 即此症狀）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Awakening
{
    /// <summary>
    /// persona 檔（AwakenInit/personas/&lt;name&gt;.json）的 typed model。
    /// 欄位集合對齊 awakening.py 全 persona 實掃聯集（2026-08-13，21 檔）；
    /// 罕見欄（persona_spec / last_session_keys / relogin_count）不建模 —— patch-write 保護它們。
    /// </summary>
    public class UCL_PersonaData : UnityJsonSerializable
    {
        // 欄名 = JSON key（snake_case），不遵循 C# 慣例是刻意的 —— 見檔頭「命名注意」。
        public string agent = "";
        public string model = "";
        public string layer_role = "";
        public int wake_count = 0;
        public string status = "offline";
        public string availability = "offline";
        public string last_active = "";
        public string actual_agent = "";
        public string forked_from = "";
        public string forked_at = "";
        public string created_at = "";
        public string email = "";
        /// <summary>見林書籤 — 缺欄時維持 -1（與「值為 0」區分；rebase 判定要用）。</summary>
        public int last_consolidated_wake = -1;
        public List<string> fork_lineage = new List<string>();

        /// <summary>檔名（不在 JSON 內，載入時由呼叫端補）。</summary>
        [UCL.Core.ATTR.UCL_HideOnGUI] public string name = "";

        public static UCL_PersonaData LoadFromFile(string iPath)
        {
            var aJson = JsonData.ParseJson(File.ReadAllText(iPath));
            if (aJson == null || !aJson.IsObject) return null;
            var aData = new UCL_PersonaData();
            aData.DeserializeFromJson(aJson);
            aData.name = Path.GetFileNameWithoutExtension(iPath);
            return aData;
        }
    }

    /// <summary>
    /// session lock（_session/_persona_&lt;name&gt;.json）的 typed model。
    /// 11 欄逐欄對齊 awakening.py write_lock 實寫 schema（P0 基線 Template repo _baseline/ 有實測清單）。
    /// </summary>
    public class UCL_SessionLockData : UnityJsonSerializable
    {
        public string persona = "";
        public string agent = "";
        public string actual_agent = "";
        public string model = "";
        public string bank_account = "";
        public string session_key = "";
        public string session_token = "";
        public string claim_origin = "";
        public string locked_at = "";
        /// <summary>登入時蓋章的**本次 wake 期望編號**（＝當時 wakes/ 信數 + 1）。
        /// 為什麼要存：`wake_count` 2026-08-21 起改成由 wakes/ 信數推導 ⇒ sleep 端的
        /// letter-before-sleep 閘門若拿它比信數，就是**同源同時刻自己比自己**
        /// （apex-one 2026-08-13 預言的失效形狀）。這一欄是「期望」那一半的獨立來源。
        /// 0 ＝ 舊 lock 沒有這一欄（走 mtime 備援判準）。</summary>
        public int wake_expected = 0;
        public int pid = 0;

        public static UCL_SessionLockData LoadFromFile(string iPath)
        {
            var aJson = JsonData.ParseJson(File.ReadAllText(iPath));
            if (aJson == null || !aJson.IsObject) return null;
            var aData = new UCL_SessionLockData();
            aData.DeserializeFromJson(aJson);
            return aData;
        }
    }

    /// <summary>
    /// registry meta（AwakenInit/_registry_meta.json）的 typed model —— 只建模 resolver 需要的兩欄。
    /// dict 欄位走 customize deserialize（泛型 Dictionary 不吃自動欄位映射的保守作法）。
    /// </summary>
    public class UCL_RegistryMeta : UnityJsonSerializable
    {
        /// <summary>canonical agent → bank account（agent→bank 唯一權威表）。</summary>
        public Dictionary<string, string> agent_banks = new Dictionary<string, string>();
        /// <summary>小寫 alias → canonical agent（registry override，優先於內建 DEFAULT_AGENT_ALIASES）。</summary>
        public Dictionary<string, string> agent_aliases = new Dictionary<string, string>();

        public override void DeserializeFromJson(JsonData iJson)
        {
            // 不呼叫 base —— 本類只有兩個 Dictionary 欄，全部手動讀（泛型 Dictionary 的自動映射
            // 行為不在本工項的驗證範圍內，保守起見不依賴）。
            agent_banks.Clear();
            agent_aliases.Clear();
            ReadDict(iJson, "agent_banks", agent_banks);
            ReadDict(iJson, "agent_aliases", agent_aliases);
        }

        static void ReadDict(JsonData iJson, string iKey, Dictionary<string, string> oDict)
        {
            if (iJson == null || !iJson.Contains(iKey)) return;
            var aNode = iJson[iKey];
            if (!aNode.IsObject || aNode.Dic == null) return;
            foreach (var aKey in aNode.Dic.Keys)
            {
                if (aKey.StartsWith("_")) continue;   // _doc / _note 類註解欄
                oDict[aKey] = aNode.GetString(aKey, "");
            }
        }

        public static UCL_RegistryMeta LoadFromFile(string iPath)
        {
            if (!File.Exists(iPath)) return new UCL_RegistryMeta();
            var aJson = JsonData.ParseJson(File.ReadAllText(iPath));
            var aData = new UCL_RegistryMeta();
            if (aJson != null && aJson.IsObject) aData.DeserializeFromJson(aJson);
            return aData;
        }
    }
}
#endif
