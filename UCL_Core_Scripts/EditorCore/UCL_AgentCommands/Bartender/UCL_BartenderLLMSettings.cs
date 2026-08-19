// 區塊職責：酒保發言的 LLM 設定（要不要用模型、用哪顆、閒置多久卸載）＋ 讀寫 IO。
// 物理意義：酒保原本只會發**罐頭**（trigger 裡寫死的 message）。接上本機 LLM 之後，
//          發言內容多了一條來源。本檔就是那個切換開關的持久狀態。
// 數值影響：只影響「發言內容從哪來」與「模型在顯存待多久」；不影響觸發判定、不影響時間規則。
//
// 設計取捨：
//   · **預設是罐頭**（`model_id` 空字串＝罐頭）—— 沒設定過的機器、沒裝 ollama 的機器、
//     模型被刪掉的機器，行為都跟接 LLM 之前**逐字相同**。新功能的預設值不該改變既有行為。
//   · **罐頭是 fallback 不是替代**：LLM 失敗（服務沒開／逾時／空輸出）時退回罐頭，
//     ⇒ 通道上永遠有話可講。靜默不發言的症狀跟「觸發沒命中」一模一樣，最難查。
//   · **keep_alive 隨每次請求送**，不改 ollama 服務的全域設定 ——
//     全域設定會影響別人（其他工具也在用同一個服務），而 per-request 只影響這一次。
//   · 存 `ChatTavern/bartender/llm_settings.json`，與 triggers/time_rules 同層、同一套原子寫入。
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>酒保 LLM 發言設定。<c>model_id</c> 空 ＝ 只發罐頭（預設）。</summary>
    [Serializable]
    public class UCL_BartenderLLMSettings : UnityJsonSerializable
    {
        /// <summary>
        /// 要用的 ollama 模型 tag（例如 `qwen3:0.6b`）。
        /// **空字串＝罐頭回應**（預設）—— 這個預設值刻意讓行為等同「沒有這個功能」。
        /// </summary>
        public string model_id = "";

        /// <summary>
        /// 閒置多久把模型從顯存卸載（秒）。隨每次請求以 ollama 的 `keep_alive` 送出。
        /// 預設 120 —— 顯存跟 Unity 共用，酒保發言又是低頻事件，長期佔著不划算。
        /// ⚠ 設 0 ＝ 每次用完立刻卸 ⇒ 每次發言都要付冷啟動（實測 0.6b 冷啟動約 30 秒）。
        /// </summary>
        public int keep_alive_seconds = 120;

        /// <summary>單次發言的生成上限（token）。酒保只要短句，別讓它寫論文。</summary>
        public int max_tokens = 120;

        /// <summary>等模型的上限（秒）。逾時就退罐頭 —— 不讓一次卡住變成一次沉默。</summary>
        public int timeout_seconds = 30;

        /// <summary>酒保人設（system prompt）。空 ＝ 用內建預設。</summary>
        public string persona_prompt =
            "你是酒館的酒保，講話簡短、親切、帶點幽默。一律使用繁體中文（台灣用語）。" +
            "只輸出要說的那一句話本身，不要解釋、不要前言、不要列點。";

        /// <summary>是否啟用 LLM 發言（總開關；關掉＝純罐頭，等同沒有這個功能）。</summary>
        public bool enabled = false;

        /// <summary>罐頭模式？—— 判準只有一個：沒啟用或沒指定模型。</summary>
        public bool IsCannedOnly => !enabled || string.IsNullOrEmpty(model_id);
    }

    /// <summary>酒保 LLM 設定的讀寫（與 triggers/time_rules 同一套原子寫入慣例）。</summary>
    public static class UCL_BartenderLLMSettingsIO
    {
        public const string SettingsFile = "llm_settings.json";

        public static string GetPath()
            => Path.Combine(UCL_BartenderIO.GetBartenderDir(), SettingsFile);

        /// <summary>讀設定。檔案不存在或壞掉都回**預設值**（＝罐頭模式）—— 壞檔不該把酒保變啞巴。</summary>
        public static UCL_BartenderLLMSettings Load()
        {
            string aPath = GetPath();
            if (!File.Exists(aPath)) return new UCL_BartenderLLMSettings();
            try
            {
                string aJson = File.ReadAllText(aPath);
                var aData = new UCL_BartenderLLMSettings();
                if (!string.IsNullOrEmpty(aJson)) aData.DeserializeFromJson(JsonData.ParseJson(aJson));
                return aData;
            }
            catch (Exception e)
            {
                // 出聲但不擋 —— 靜默退預設會讓「設定沒生效」跟「我沒設定過」長得一樣
                Debug.LogWarning($"[Bartender] LLM 設定讀取失敗，退回預設（罐頭）：{e.Message}");
                return new UCL_BartenderLLMSettings();
            }
        }

        /// <summary>原子寫入（先 .tmp 再 rename）—— daemon 隨時可能在讀，不能讓它讀到半寫檔。</summary>
        public static void Save(UCL_BartenderLLMSettings iData)
        {
            UCL_BartenderIO.EnsureBartenderDir();
            string aPath = GetPath();
            string aTmp = aPath + ".tmp";
            string aJson = (iData ?? new UCL_BartenderLLMSettings()).SerializeToJson().ToJsonBeautify();
            File.WriteAllText(aTmp, aJson, new UTF8Encoding(false));
            if (File.Exists(aPath)) File.Delete(aPath);
            File.Move(aTmp, aPath);
        }
    }
}
#endif
