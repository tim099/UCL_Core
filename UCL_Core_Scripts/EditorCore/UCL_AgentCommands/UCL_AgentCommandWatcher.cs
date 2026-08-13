
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
            get => UCL_ProjectEditorPrefs.GetBool(EditorPrefsEnabledKey, true);
            set
            {
                UCL_ProjectEditorPrefs.SetBool(EditorPrefsEnabledKey, value);
                Debug.Log($"[UCL_AgentCmdWatcher] Enabled = {value}");
            }
        }

        // ===========================================================
        // 註冊（[InitializeOnLoad] 自動觸發）
        // ===========================================================

        static UCL_AgentCommandWatcher()
        {
            // 區塊職責：就地、同步訂閱 EditorApplication.update。
            // 物理意義：**訂閱不得經過 EditorApplication.delayCall。**
            //   舊版是 `delayCall += () => Register().Forget()`，理由是「cctor 內 await UniTask 會撞
            //   PlayerLoopHelper NRE」—— 那個理由對 **await** 成立，但訂閱一個 delegate 不碰 UniTask，
            //   被一起推到 delayCall 是搭便車。而 delayCall 是**單次 schedule**，
            //   domain reload 後若沒有觸發它的那一拍（Editor 在背景、沒人動它），它就不會來 ——
            //   於是 update 永遠不訂閱，**整條 AgentCommand 通道靜默死亡**。
            //   ⚠ 同 repo 早有這一課：UCL_BartenderDaemon 檔頭第 5 行寫著
            //   「用 EditorApplication.update（非 delayCall）—— 持續 tick，不靠單次 schedule」，
            //   而它的心跳在事故現場照跳，watcher 卻死了 —— 兩者差別只有註冊方式。
            //   血證（2026-08-13 basecamp wake#57，同日重現三次）：reload 後 `Registered.` 一次都沒出現、
            //   pending.trigger 躺 24 分鐘、run_cmd 端只有 timeout、Editor 端零 error、心跳全程正常。
            //   唯一復原路徑是人去 focus Unity —— 那正是「補上缺的那一拍」。
            // 數值影響：訂閱從「下一個 delayCall 拍（可能永遠不來）」提前到「cctor 當下」。
            //   路徑字串只用於 log，取得失敗不影響訂閱（已就地 try 起來）。
            EditorApplication.update += OnEditorUpdate;
            s_Registered = true;
            try
            {
                Debug.Log($"[UCL_AgentCmdWatcher] Registered. Watching: {UCL_AgentCommandQueue.GetTriggerPath()}");
            }
            catch (Exception e)
            {
                Debug.Log($"[UCL_AgentCmdWatcher] Registered.（trigger 路徑取得失敗，不影響監看：{e.Message}）");
            }

            // 區塊職責：監聽編輯器 PlayMode 狀態變更事件
            // 物理意義：在進入 PlayMode 或退出 PlayMode 時，觸發對應的清理或自我修復動作。
            //          特別是當進入 PlayMode 時，由於 UniTask 會自動中斷/清除 EditMode 下的 async 鏈，我們必須重置 Runner 的記憶體狀態，以便重新驅動 Runner。
            // 數值影響：訂閱 playModeStateChanged 事件。
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // 區塊職責：PlayMode 狀態變更時的回呼邏輯
        // 物理意義：在進入 PlayMode (EnteredPlayMode) 時，主動清除 Runner 的記憶體狀態 (ResetRunningAgents)，為 Watcher 對孤兒鎖 (.running) 的自我修復鋪路。
        // 數值影響：無直接數值影響，僅驅動 ResetRunningAgents 的調用。
        private static void OnPlayModeStateChanged(PlayModeStateChange iState)
        {
            // 判斷狀態：若編輯器剛成功進入 PlayMode
            if (iState == PlayModeStateChange.EnteredPlayMode)
            {
                // 執行重置：清空 Runner 的執行中列表，允許重新偵測孤兒鎖並自我恢復
                UCL_AgentCommandRunner.ResetRunningAgents();
            }
        }

        // 區塊職責：手動補註冊入口（idempotent）—— 正常路徑是 cctor 已就地訂閱，這裡是給
        //          Unregister() 之後想再開回來的呼叫端用。
        // 物理意義：**已無任何 await** —— 訂閱不再依賴 ModuleService。
        //   舊版第一行是 `await UCL_ModuleService.WaitUntilInitialized(default)`，理由寫「確保
        //   GetTriggerPath() 可用」，而那個理由不成立：GetTriggerPath / ListAgentIds 只用到
        //   `UCL_RepoPath.AgentCommandsDir`（＝ RepoRoot 字串組合），與 ModuleService 無關；
        //   而 m_Initialized 只在 InitAsync 最後一行才設，等不到就是無聲無限期掛著。
        //   （簽名保留 UniTask 回傳型別以免動到呼叫端；實際同步完成。）
        // 數值影響：訂閱時機由「ModuleService 就緒後」提前到「呼叫當下」。
        public static UniTask Register()
        {
            if (s_Registered) return UniTask.CompletedTask;
            EditorApplication.update += OnEditorUpdate;
            s_Registered = true;
            Debug.Log($"[UCL_AgentCmdWatcher] Registered (手動). Watching: {UCL_AgentCommandQueue.GetTriggerPath()}");
            return UniTask.CompletedTask;
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
                // 掃 per-persona triggers — scan queues/<persona>/queue*.json 列出的 queue id。
                // ⚠ **不再另外呼叫 TryDispatchAgent(null)**（2026-08-01 雙扣款事故修）：
                //   persona 資料夾制之後 null 已經對應到 queues/anonymous/，
                //   而 ListAgentIds() 也會把 anonymous 當成一般 persona 資料夾列出來
                //   → **同一條 queue 被派兩次**，Runner 對同一筆 OneShot 執行兩遍。
                //   實害：gura 捐書 20 token 被扣兩次（ledger 兩筆 debit 相隔 5ms、
                //   同 pid、History UseCount=2）。錢的事沒有「大概不會怎樣」。
                //   改版前 null → legacy AgentCommands/queue.json、與 queue-*.json 不重疊，
                //   所以兩段掃描是安全的；改版把兩者合流卻沒拿掉其中一段 —— 是我的回歸。
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

        // 區塊職責：分配特定 Agent 的待執行指令至 Runner，並負責偵測與修復因狀態變更（如 PlayMode 轉換）導致的「孤兒鎖」死鎖狀態。
        // 物理意義：若偵測到硬碟上存在 `.running` 鎖檔案，但記憶體中的 Runner 實際上並未執行（此狀態通常發生在 Unity 進入 PlayMode 時 UniTask 被強制清空），
        //          本機制會主動進行自我修復，重新調用 Runner 恢復未完的指令佇列，保障 RCG_StartNewGame 等跨 PlayMode 指令的端到端穩定執行。
        // 數值影響：若命中孤兒鎖，將重新啟動對應 Agent 的 Runner 異步工作，無其他副作用。
        static void TryDispatchAgent(string agentId)
        {
            // 讀取硬碟狀態：確認當前 Agent 是否在硬碟上存在正在執行的 Running 鎖檔案
            bool runningFileExists = UCL_AgentCommandTrigger.RunningExists(agentId);
            // 讀取記憶體狀態：確認當前 Agent 的 Runner 實例是否正於記憶體中運行
            bool isRunningInMemory = UCL_AgentCommandRunner.IsRunningForAgent(agentId);

            // 判斷邏輯：若硬碟上存在執行鎖
            if (runningFileExists)
            {
                // 記憶體中亦在運行：屬於正常忙碌狀態，直接跳過不予干涉
                if (isRunningInMemory)
                {
                    return;
                }
                else
                {
                    // 記憶體中未運行：判定為孤兒鎖異常狀態（由 PlayMode 切換或編譯重載導致 Task 消失）
                    // 記錄時間：更新最近一次觸發的時間標記
                    s_LastTriggerAt = DateTime.Now;
                    // 解析標籤：設定用於輸出日誌的 Agent 識別字串
                    string tag = string.IsNullOrEmpty(agentId) ? "default" : agentId;
                    // 警告日誌：於控制台輸出孤兒鎖警告，表明正在啟動自我恢復流程
                    Debug.LogWarning($"[UCL_AgentCmdWatcher] Orphan running lock detected for '{tag}' (file exists, memory runner idle). Resuming runner!");
                    // 啟動恢復：直接拉起對應 Agent 的 Runner 異步流程來消耗 queue.json 中未完成的指令
                    UCL_AgentCommandRunner.RunAsync(agentId, default).Forget();
                    return;
                }
            }

            // 讀取待處理狀態：若硬碟上不存在 Pending 觸發檔案，代表該 Agent 當前無指令，直接返回
            if (!UCL_AgentCommandTrigger.PendingExists(agentId)) return;
            // 搶佔原子鎖：試圖將 pending 重新命名為 running（進行原子操作以搶佔執行權）
            if (!UCL_AgentCommandTrigger.MarkRunning(agentId)) return;

            // 記錄時間：更新最近一次成功偵測的觸發時間
            s_LastTriggerAt = DateTime.Now;
            // 解析標籤：設定日誌用標籤
            string tag2 = string.IsNullOrEmpty(agentId) ? "default" : agentId;
            // 偵測日誌：記錄新觸發事件，並附帶精準時間戳記
            Debug.Log($"[UCL_AgentCmdWatcher] Trigger detected for '{tag2}' at {s_LastTriggerAt:O} → invoking Runner.");
            // 啟動 Runner：執行對應 Agent 的指令佇列，並在 `finally` 區塊中自動清除硬碟上的執行鎖
            UCL_AgentCommandRunner.RunAsync(agentId, default).Forget();
        }
    }
}
#endif
