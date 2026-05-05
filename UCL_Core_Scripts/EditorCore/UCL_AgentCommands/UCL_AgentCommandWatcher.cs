
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/05 2026

// Editor 端 lock-file watcher — 在 EditorApplication.update 中輪詢 pending.trigger，
// 偵測到後 MarkRunning + 觸發 UCL_AgentCommandRunner.RunAsync。
//
// 設計理由：
//   - 完全脫離 UCL_AgentCommandsPage（IMGUI 頁面）；即使使用者沒開 Page，watcher 仍持續運作
//   - 用 EditorApplication.update + 1 秒節流取代 FileSystemWatcher，避免跨平台 / domain reload 邊界問題
//   - [InitializeOnLoad] 讓 Unity Editor 載入 / domain reload 後自動啟動
//   - EditorPrefs 提供開關，使用者可暫時停用（Page 上會有 toggle）
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Lock-file watcher：靜態類，[InitializeOnLoad] 觸發註冊到 EditorApplication.update。
    ///
    /// 流程：
    /// <code>
    /// EditorApplication.update (~1 sec throttled)
    ///   └─ if Trigger.PendingExists() AND not Runner.IsRunning
    ///        ├─ Trigger.MarkRunning()  (atomic File.Move → .running)
    ///        └─ Runner.RunAsync()      (執行完會在 finally 清掉 .running)
    /// </code>
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_AgentCommandWatcher
    {
        // EditorPrefs key — 使用者可在 UCL_AgentCommandsPage 上 toggle
        public const string EditorPrefsEnabledKey = "UCL.AgentCmd.Watcher.Enabled";

        // 節流：每 1 秒最多 check 一次（EditorApplication.update 一秒約 60+ 次）
        const double CheckIntervalSec = 1.0;

        static double s_LastCheckTime = 0;
        static DateTime s_LastTriggerAt = DateTime.MinValue;
        static bool s_Registered = false;

        /// <summary>使用者最近一次被偵測到的 trigger 時間（給 Page 顯示用）。</summary>
        public static DateTime LastTriggerAt => s_LastTriggerAt;

        /// <summary>Watcher 是否啟用（讀 EditorPrefs，預設 true）。</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EditorPrefsEnabledKey, true);
            set
            {
                EditorPrefs.SetBool(EditorPrefsEnabledKey, value);
                Debug.Log($"[UCL_AgentCmdWatcher] Enabled = {value}");
            }
        }

        // ===========================================================
        // 註冊（[InitializeOnLoad] 自動觸發）
        // ===========================================================

        static UCL_AgentCommandWatcher()
        {
            Register();
        }

        /// <summary>確保 update 訂閱已註冊（idempotent）。</summary>
        public static void Register()
        {
            if (s_Registered) return;
            EditorApplication.update += OnEditorUpdate;
            s_Registered = true;
            Debug.Log($"[UCL_AgentCmdWatcher] Registered. Watching: {UCL_AgentCommandQueue.GetTriggerPath()}");
        }

        /// <summary>取消訂閱（給 Page 上的 toggle 用；通常不需要）。</summary>
        public static void Unregister()
        {
            if (!s_Registered) return;
            EditorApplication.update -= OnEditorUpdate;
            s_Registered = false;
            Debug.Log("[UCL_AgentCmdWatcher] Unregistered.");
        }

        // ===========================================================
        // 主 loop
        // ===========================================================

        // 區塊職責：每幀檢查 trigger 檔，節流到 1Hz
        // 物理意義：把昂貴的 IO（File.Exists）控制在每秒一次，不讓 Editor update 主迴圈被拖慢
        // 數值影響：偵測延遲 < CheckIntervalSec；對 Python 端「submit → 執行」整體耗時影響可忽略
        static void OnEditorUpdate()
        {
            if (!Enabled) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastCheckTime < CheckIntervalSec) return;
            s_LastCheckTime = now;

            try
            {
                // 已在執行 → 等下輪（s_Running 是 Runner 的內部旗標，這裡靠 Trigger.RunningExists 間接判定）
                if (UCL_AgentCommandTrigger.RunningExists()) return;

                // 沒 pending → 沒事
                if (!UCL_AgentCommandTrigger.PendingExists()) return;

                // 嘗試接手
                if (!UCL_AgentCommandTrigger.MarkRunning())
                {
                    // MarkRunning 失敗（被搶走 / IO 異常）→ 不觸發，下輪再試
                    return;
                }

                s_LastTriggerAt = DateTime.Now;
                Debug.Log($"[UCL_AgentCmdWatcher] Trigger detected at {s_LastTriggerAt:O} → invoking Runner.");

                // 觸發 Runner（async；finally 會 Trigger.Clear()）
                UCL_AgentCommandRunner.Menu_RunPending();
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCmdWatcher] OnEditorUpdate error: {e}");
            }
        }
    }
}
#endif
