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

        /// <summary>檢查 pending.trigger 是否存在。</summary>
        public static bool PendingExists()
        {
            try { return File.Exists(UCL_AgentCommandQueue.GetTriggerPath()); }
            catch { return false; }
        }

        /// <summary>檢查 pending.trigger.running 是否存在。</summary>
        public static bool RunningExists()
        {
            try { return File.Exists(UCL_AgentCommandQueue.GetRunningTriggerPath()); }
            catch { return false; }
        }

        /// <summary>取得當前狀態（Running 優先於 Pending — 後者只是輸入檔，前者代表已被接手）。</summary>
        public static TriggerState GetState()
        {
            if (RunningExists()) return TriggerState.Running;
            if (PendingExists()) return TriggerState.Pending;
            return TriggerState.Idle;
        }

        // ===========================================================
        // 狀態轉移
        // ===========================================================

        /// <summary>
        /// Editor Watcher 偵測到 pending.trigger 後呼叫：將其改名為 .running 表示「已接手」。
        /// </summary>
        /// <returns>true = 成功接手；false = trigger 不在或被搶先（例如另一個 Editor 同時跑）。</returns>
        public static bool MarkRunning()
        {
            string pendingPath = UCL_AgentCommandQueue.GetTriggerPath();
            string runningPath = UCL_AgentCommandQueue.GetRunningTriggerPath();
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
        public static void Clear()
        {
            TryDelete(UCL_AgentCommandQueue.GetRunningTriggerPath());
            TryDelete(UCL_AgentCommandQueue.GetTriggerPath());
        }

        /// <summary>
        /// 由外部（測試 / 手動）建立 pending.trigger。
        /// 正常情況下由 Python wrapper 寫入；此 API 只是給 Editor 端「按按鈕模擬」或 unit test 用。
        /// </summary>
        public static void CreatePending(string note = null)
        {
            UCL_AgentCommandQueue.EnsureDir();
            string path = UCL_AgentCommandQueue.GetTriggerPath();
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
