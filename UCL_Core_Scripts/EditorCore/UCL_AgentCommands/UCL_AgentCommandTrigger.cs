// Trigger file helpers — 集中管理 pending.trigger / pending.trigger.running 的 file ops。
// 設計理由：避免 Watcher / Runner / Page 三處重複處理 File.Move / Delete / Exists 等細節。
//          所有 IO 失敗都吞例外並用 Debug.LogWarning 回報，盡量讓上層流程不被 IO race 中斷。
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Lock-file 機制的中央封裝。
    ///
    /// 三方協議（Python ↔ Editor Watcher ↔ Editor Runner）：
    /// <code>
    /// [idle]   ──Python.Create()──▶  [pending]
    /// [pending] ──Watcher 偵測 + MarkRunning()──▶  [running]
    /// [running] ──Runner finally + Clear()──▶  [idle]
    /// </code>
    ///
    /// Python 在 submit 之前會自行 ensure_idle() 等到沒有 pending / running 才寫入。
    /// </summary>
    public static class UCL_AgentCommandTrigger
    {
        public enum TriggerState
        {
            Idle = 0,     // 都沒有檔
            Pending = 1,  // 有 pending.trigger（等待 Watcher 接手）
            Running = 2,  // 有 pending.trigger.running（Editor 處理中）
        }

        // ===========================================================
        // 狀態查詢
        // ===========================================================

        /// <summary>檢查 pending.trigger 是否存在 (agentId=null → default trigger)。</summary>
        public static bool PendingExists(string agentId = null)
        {
            try { return File.Exists(UCL_AgentCommandQueue.GetTriggerPath(agentId)); }
            catch { return false; }
        }

        /// <summary>檢查 pending.trigger.running 是否存在 (agentId=null → default).</summary>
        public static bool RunningExists(string agentId = null)
        {
            try { return File.Exists(UCL_AgentCommandQueue.GetRunningTriggerPath(agentId)); }
            catch { return false; }
        }

        /// <summary>取得當前狀態（Running 優先於 Pending — 後者只是輸入檔，前者代表已被接手）。</summary>
        public static TriggerState GetState(string agentId = null)
        {
            if (RunningExists(agentId)) return TriggerState.Running;
            if (PendingExists(agentId)) return TriggerState.Pending;
            return TriggerState.Idle;
        }

        // ===========================================================
        // 狀態轉移
        // ===========================================================

        /// <summary>
        /// Editor Watcher 偵測到 pending.trigger 後呼叫：將其改名為 .running 表示「已接手」。
        /// </summary>
        /// <returns>true = 成功接手；false = trigger 不在或被搶先（例如另一個 Editor 同時跑）。</returns>
        public static bool MarkRunning(string agentId = null)
        {
            string pendingPath = UCL_AgentCommandQueue.GetTriggerPath(agentId);
            string runningPath = UCL_AgentCommandQueue.GetRunningTriggerPath(agentId);
            try
            {
                if (!File.Exists(pendingPath)) return false;
                // 若殘留舊的 running 檔（前次未 Clear），先刪除避免 File.Move 失敗
                if (File.Exists(runningPath))
                {
                    Debug.LogWarning($"[UCL_AgentCmdTrigger] Stale running file detected, removing: {runningPath}");
                    File.Delete(runningPath);
                }
                File.Move(pendingPath, runningPath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCmdTrigger] MarkRunning failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Runner finally 階段呼叫：刪除 pending.trigger.running，回到 idle。
        /// 同時也刪除可能殘留的 pending.trigger（保險，正常流程不會殘留）。
        /// </summary>
        public static void Clear(string agentId = null)
        {
            TryDelete(UCL_AgentCommandQueue.GetRunningTriggerPath(agentId));
            TryDelete(UCL_AgentCommandQueue.GetTriggerPath(agentId));
        }

        /// <summary>
        /// 由外部（測試 / 手動）建立 pending.trigger。
        /// 正常情況下由 Python wrapper 寫入；此 API 只是給 Editor 端「按按鈕模擬」或 unit test 用。
        /// </summary>
        public static void CreatePending(string note = null, string agentId = null)
        {
            UCL_AgentCommandQueue.EnsureDir(agentId);
            string path = UCL_AgentCommandQueue.GetTriggerPath(agentId);
            // 把內嵌字面值先抽到變數，避免 C# 9（Unity 6 預設）對 $"...{x ?? "literal"}..." 這種 nested string literal 的限制
            string source = string.IsNullOrEmpty(note) ? "editor-manual" : note;
            string ts = DateTime.UtcNow.ToString("o");
            string body = "{\n  \"createdAt\": \"" + ts + "\",\n  \"submittedBy\": \"" + source + "\"\n}\n";
            try
            {
                File.WriteAllText(path, body, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCmdTrigger] CreatePending failed: {e.Message}");
            }
        }

        // ===========================================================
        // 區塊職責：Busy（queue 開不了）那一輪的**有界重新武裝**（TASK-0264 ⊕）。
        // 物理意義：Busy 是正常返回 ⇒ Runner 的 finally 會 `Clear` 掉兩個 trigger 檔，
        //          而 Watcher 只看 `PendingExists`（⛔ 不掃 queue.json 內容）
        //          ⇒ 那筆指令原地擱淺，等某個不相干的未來 trigger 才跑，
        //          而呼叫端此時已經拿到 timeout。**「延後執行」比「沒執行」難查。**
        // ⚠ 為什麼要有界：無界重下 trigger ＝ 只要那顆檔一直被獨佔，Watcher 就每輪回來一次（連跳）。
        //          次數寫在 trigger 檔自己身上（`busyRetry`）⇒ 計數**跟著那一輪走**，
        //          ⛔ 不放 static（domain reload 會清掉，而清掉之後計數從頭開始 ＝ 又變成無界）。
        // 數值影響：最多再武裝 <see cref="BUSY_REARM_MAX"/> 次；用完只留一行大聲的 LogError，
        //          ⛔ 不再自己重來 —— 那時候「等下一輪」這句話就不成立，要說實話。
        // ===========================================================

        /// <summary>Busy 最多重新武裝幾次 —— ⛔ 不是重試次數上限的猜測，是「連跳」的閘。</summary>
        public const int BUSY_REARM_MAX = 3;

        /// <summary>讀出這一輪 trigger 上的 <c>busyRetry</c>（running 優先，其次 pending）。讀不到回 0。</summary>
        public static int ReadBusyRetry(string agentId = null)
        {
            int aFromRunning = ReadBusyRetryFrom(UCL_AgentCommandQueue.GetRunningTriggerPath(agentId));
            if (aFromRunning > 0) return aFromRunning;
            return ReadBusyRetryFrom(UCL_AgentCommandQueue.GetTriggerPath(agentId));
        }

        /// <summary>
        /// 重新武裝一次 pending.trigger 並把 <c>busyRetry</c> 加一。
        /// <para>⚠ 呼叫時機是 Runner finally 裡 <see cref="Clear"/> **之後** —— 在那之前寫的會被 Clear 掉，
        /// 而那個失效樣子是「我明明重下了而它不見了」。</para>
        /// </summary>
        /// <param name="iNextRetry">這一次要寫進 trigger 的第幾次。
        /// 🩸 **必須由呼叫端在 <see cref="Clear"/> 之前讀好再傳進來**（2026-09-22 活體抓到）：
        /// 第一版讓本函式自己去讀，而它跑在 Clear 之後 ⇒ 兩個 trigger 檔都不在了 ⇒ 永遠讀到 0、
        /// 永遠寫「第 1 次」⇒ **那個上限根本沒有生效**，12 秒內連跳至少三輪。
        /// ⚠ 而畫面上完全正常：每一輪都印「已重新武裝（第 1/3 次）」，⛔ 它跟真的有界長得一樣。</param>
        /// <returns>true ＝ 真的重新武裝了；false ＝ 次數用完（呼叫端要出聲說實話）。</returns>
        public static bool RearmForBusy(string agentId, int iNextRetry)
        {
            int aNext = iNextRetry;
            if (aNext > BUSY_REARM_MAX) return false;

            UCL_AgentCommandQueue.EnsureDir(agentId);
            string aPath = UCL_AgentCommandQueue.GetTriggerPath(agentId);
            string aBody = "{\n  \"createdAt\": \"" + DateTime.UtcNow.ToString("o") + "\",\n"
                         + "  \"submittedBy\": \"busy-rearm\",\n"
                         + "  \"busyRetry\": " + aNext + "\n}\n";
            try
            {
                File.WriteAllText(aPath, aBody, new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCmdTrigger] RearmForBusy failed: {e.Message}");
                return false;
            }
        }

        static int ReadBusyRetryFrom(string iPath)
        {
            try
            {
                if (!File.Exists(iPath)) return 0;
                string aText = File.ReadAllText(iPath);
                const string aKey = "\"busyRetry\"";
                int aAt = aText.IndexOf(aKey, StringComparison.Ordinal);
                if (aAt < 0) return 0;
                int aColon = aText.IndexOf(':', aAt + aKey.Length);
                if (aColon < 0) return 0;
                int aEnd = aColon + 1;
                while (aEnd < aText.Length && (char.IsWhiteSpace(aText[aEnd]))) ++aEnd;
                int aNumStart = aEnd;
                while (aEnd < aText.Length && char.IsDigit(aText[aEnd])) ++aEnd;
                if (aEnd == aNumStart) return 0;
                return int.TryParse(aText.Substring(aNumStart, aEnd - aNumStart), out int aN) ? aN : 0;
            }
            catch (Exception)
            {
                // ⛔ 讀不到就當 0：它只會讓我們**多**武裝幾次（有上限），
                //   而反過來（讀壞了當成已達上限）會安靜地把自癒關掉。
                return 0;
            }
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCmdTrigger] Delete failed ({path}): {e.Message}");
            }
        }
    }
}
#endif
