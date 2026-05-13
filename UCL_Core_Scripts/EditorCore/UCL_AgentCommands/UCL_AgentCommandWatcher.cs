
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
using Cysharp.Threading.Tasks;
using System;
using System.Threading.Tasks;
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
            // 區塊職責：[InitializeOnLoad] cctor 內延到下一個 editor tick 才啟動 Register
            // 物理意義：cctor 在 EditorAssemblies.ProcessInitializeOnLoadAttributes 連續呼叫，
            //          此時 UniTask 的 PlayerLoopHelper 內部尚未 init —— 直接 await UniTask
            //          會在 PlayerLoopHelper.AddAction 撞 NRE。delayCall 推到下一個 editor
            //          tick，UniTask 基礎設施已就緒，後續 await WaitUntilInitialized 才安全。
            // 數值影響：Register chain 啟動延後 ~16ms；對既有「Register 完成前不訂閱 update」
            //          的保證無影響（Register() 函式體不動）。
            EditorApplication.delayCall += () => Register().Forget();
        }

        /// <summary>確保 update 訂閱已註冊（idempotent）。</summary>
        public static async UniTask Register()
        {
            if (s_Registered) return;
            await UCL_ModuleService.WaitUntilInitialized(default); // 等 ModuleService 就緒（通常很快），確保 UCL_AgentCommandQueue.GetTriggerPath() 可用
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
        // 物理意義：掃 default trigger + per-agent triggers (agent-command-pipeline-parallelize T03)
        //          各 agent 獨立 dispatch 可並行 (Runner 端 per-agent IsRunning flag 防同 agent 重入)
        // 數值影響：偵測延遲 < CheckIntervalSec；多 trigger 同時被偵測會並行送 Runner.RunAsync(agentId)
        static void OnEditorUpdate()
        {
            if (!Enabled) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastCheckTime < CheckIntervalSec) return;
            s_LastCheckTime = now;

            try
            {
                // (1) 掃 default trigger (legacy queue.json)
                TryDispatchAgent(null);
                // (2) 掃 per-agent triggers — scan queues/queue-*.json 列表的 agentIds
                foreach (var agentId in UCL_AgentCommandQueue.ListAgentIds())
                {
                    TryDispatchAgent(agentId);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCmdWatcher] OnEditorUpdate error: {e}");
            }
        }

        // 區塊職責：dispatch 單一 agent's pending → Runner
        // 物理意義：per-agent 獨立檢查 + dispatch, Zeta 卡死不影響 Claude 等
        // 數值影響：MarkRunning 成功 → Runner.RunAsync(agentId) async 跑; finally Trigger.Clear(agentId).
        static void TryDispatchAgent(string agentId)
        {
            // 該 agent 已在 running → 跳過
            if (UCL_AgentCommandTrigger.RunningExists(agentId)) return;
            // 沒 pending → 沒事
            if (!UCL_AgentCommandTrigger.PendingExists(agentId)) return;
            // 嘗試接手 (atomic File.Move pending → running)
            if (!UCL_AgentCommandTrigger.MarkRunning(agentId)) return;

            s_LastTriggerAt = DateTime.Now;
            string tag = string.IsNullOrEmpty(agentId) ? "default" : agentId;
            Debug.Log($"[UCL_AgentCmdWatcher] Trigger detected for '{tag}' at {s_LastTriggerAt:O} → invoking Runner.");
            // 觸發 Runner（async；finally 會 Trigger.Clear(agentId)）
            UCL_AgentCommandRunner.RunAsync(agentId, default).Forget();
        }
    }
}
#endif
