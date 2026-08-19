// 區塊職責：試跑結果的 typed model ＋ 落檔（append-only jsonl）。
// 物理意義：試跑的價值在**比較**：換模型、改提示詞、改上限之後好不好。
//          只顯示在頁面上的結果，關掉頁面就沒了 —— 而「上次那顆講得比較好」是靠記憶在比，
//          記憶不可複驗。⇒ 每次試跑落一行，之後要對帳有東西可讀。
// 數值影響：append-only，每行一次試跑（含思考段全文）；不覆寫、不裁切既有內容。
//          落檔失敗只警告不擋 —— 試跑本身比紀錄重要。
// ⚠ 對側契約：欄位名逐字對應 llm_admin.py 的 test 回傳（Unity 模式只脫 m_，不做 snake_case 轉換）。
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.LLMAdmin
{
    /// <summary>一次試跑的結果（llm_admin.py test --format json 的鏡像）。</summary>
    public class LLMTestResult : UnityJsonSerializable
    {
        public bool ok = false;
        public string model = "";
        public string prompt = "";
        public float seconds = 0f;
        public int eval_count = 0;
        public float tokens_per_sec = 0f;
        public string output = "";        // 💬 模型回覆（要拿來當發言的那一段）
        public string thinking = "";      // 🧠 思考過程（thinking 模型才有）
        public string note = "";          // 被截斷之類的「不是失敗但要知道」
        public string error = "";
    }

    /// <summary>試跑紀錄（append-only jsonl）。</summary>
    public static class UCL_LLMTestLog
    {
        public const string DirRelative = "LLMAdmin";
        public const string FileName = "test_log.jsonl";

        /// <summary>紀錄檔路徑（走可 override 的資料根，不硬編）。</summary>
        public static string GetPath()
            => Path.Combine(UCL_AgentCommandsPath.ResolveData(DirRelative), FileName);

        /// <summary>append 一行。失敗只警告 —— 落檔比不上試跑本身重要，但要出聲。</summary>
        public static void Append(LLMTestResult iResult, string iSystemPrompt)
        {
            if (iResult == null) return;
            try
            {
                string aPath = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                var aJson = iResult.SerializeToJson();
                aJson["ts"] = DateTime.UtcNow.ToString("o");      // 落檔時間（結果本身沒帶時間）
                aJson["system"] = iSystemPrompt ?? "";            // 人設會影響輸出，不記就無法重現
                File.AppendAllText(aPath, aJson.ToJson() + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LLMAdmin] 試跑紀錄落檔失敗（結果仍在畫面上）：{e.Message}");
            }
        }

        /// <summary>讀回最後 N 行（新→舊）。讀不到回空陣列。</summary>
        public static string[] TailLines(int iCount)
        {
            try
            {
                string aPath = GetPath();
                if (!File.Exists(aPath)) return new string[0];
                var aLines = File.ReadAllLines(aPath);
                int aFrom = Math.Max(0, aLines.Length - iCount);
                var aOut = new string[aLines.Length - aFrom];
                for (int i = 0; i < aOut.Length; i++)
                {
                    aOut[i] = aLines[aLines.Length - 1 - i];       // 新的在前
                }
                return aOut;
            }
            catch (Exception)
            {
                return new string[0];
            }
        }
    }
}
#endif
