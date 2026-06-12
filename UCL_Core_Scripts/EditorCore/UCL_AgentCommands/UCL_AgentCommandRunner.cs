
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Async runner — awaits UCL_ModuleService.WaitUntilInitialized before executing commands.
// 提供 Tools/UCL/Agent Commands/ 下的 MenuItem 入口。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command Runner — 從 queue.json 讀取待執行指令並依序跑。
    ///
    /// 流程：
    /// 1. 讀取 queue.json
    /// 2. await UCL_ModuleService.WaitUntilInitialized(token)（會自動觸發初始化 + 等待完成）
    /// 3. 反向遍歷每筆指令（方便就地移除）：
    ///    - 從 Registry 找 handler，呼叫 handler.ExecuteAsync(args, token)
    ///    - 成功：RunCount++ / 寫 LastRunAt+Result=Success；若 Mode==OneShot 直接從 queue 移除
    ///    - 失敗：寫 LastRunError + Result=Failed（不中斷，繼續下一筆，指令仍留在 queue）
    /// 4. 寫回 queue.json
    /// </summary>
    public static class UCL_AgentCommandRunner
    {
        // 區塊職責：per-agent running flag (agent-command-pipeline-parallelize T04)
        // 物理意義：multi-queue 後每 agent 有自己的 running state, default queue (agentId=null) key=""
        //          各自互不阻塞 — Zeta 卡死不影響 Claude / Gemini
        // 數值影響：Watcher 看單一 agentId 的 IsRunning 決定是否重入該 agent's Runner
        static readonly System.Collections.Generic.HashSet<string> s_RunningAgents = new System.Collections.Generic.HashSet<string>();
        static readonly object s_RunningLock = new object();

        // 區塊職責：觸發編輯器編譯與領域重載以重設記憶體狀態
        // 物理意義：藉由修改程式碼的微小變更，強迫 Unity Editor 偵測檔案異動並重新編譯，進而清空殘留於靜態變數中的死鎖狀態 (如 s_RunningAgents)。[Antigravity domain reload trigger 2026-05-30]
        // 數值影響：不影響任何核心計算與業務邏輯，僅用於維護管線健康。
        static string NormAgent(string agentId) => agentId ?? "";

        // 區塊職責：per-cmd 執行中的 cmd_id static slot（T-LastOp-CmdId 2026-06-12）
        // 物理意義：handler 收到的只有 Args dict，不知道自己是 queue 裡哪筆 cmd；Runner 在 ExecuteAsync
        //          前把 c.Id 放進本 slot → 下游 UCL_ChatTavernRender.WriteLastOp 寫 _last_op.md 時
        //          stamp `<!-- cmd_id: X -->`，Python 端 check_cmd_result_file 比對 cmd_id 相符才認帳
        //          （解多 Claude session 並發對同一 Editor 發 cmd 時 fail marker 互相污染誤報）。
        // 數值影響：沒設（IMGUI 手動跑 handler 等非 queue 路徑）→ null → WriteLastOp 不 stamp，行為不變。
        public static string CurrentCmdId = null;

        /// <summary>對外查詢：runner 是否正忙著跑 default queue（legacy API）。</summary>
        public static bool IsRunning => IsRunningForAgent(null);

        /// <summary>對外查詢：runner 是否正忙著跑某 agent 的 queue (agentId=null → default).</summary>
        public static bool IsRunningForAgent(string agentId)
        {
            lock (s_RunningLock) return s_RunningAgents.Contains(NormAgent(agentId));
        }

        // 區塊職責：提供清空運行中 Agent 列表的對外介面
        // 物理意義：在編輯器狀態轉換（例如進入 PlayMode）時，主動釋放記憶體中的鎖定狀態，以便配合 Watcher 的自我修復機制，避免因 UniTask 執行緒被 Unity 中斷而造成的死鎖。
        // 數值影響：重置並清空 s_RunningAgents 雜湊集合的內容，無其他副作用。
        public static void ResetRunningAgents()
        {
            // 鎖定狀態：防止多線程同時寫入導致競態條件
            lock (s_RunningLock)
            {
                // 清空集合：移除所有記錄的運行中 agentId
                s_RunningAgents.Clear();
            }
            // 輸出日誌：在控制台記錄重置行為以利除錯
            Debug.Log("[UCL_AgentCmd] Reset running agents list due to PlayMode state change.");
        }

        // ===========================================================
        // Menu Items（Tools/UCL/Agent Commands/）
        // ===========================================================

        [MenuItem("Tools/UCL/Agent Commands/Run Pending Commands", priority = 100)]
        public static void Menu_RunPending()
        {
            if (IsRunningForAgent(null))
            {
                Debug.LogWarning("[UCL_AgentCmd] Already running — ignored.");
                return;
            }
            RunAsync(default).Forget();
        }

        [MenuItem("Tools/UCL/Agent Commands/Open Queue Folder", priority = 101)]
        public static void Menu_OpenQueueFolder()
        {
            UCL_AgentCommandQueue.EnsureDir();
            string dir = UCL_AgentCommandQueue.GetQueueDir();
            // EditorUtility.RevealInFinder(dir) 在 Windows 會打開父資料夾並選取本資料夾，
            // 不是「進入」本資料夾。這裡直接用 OS shell 打開資料夾本身。
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCmd] Open folder failed ({dir}): {e}");
                // fallback：至少把上層資料夾秀出來
                EditorUtility.RevealInFinder(dir);
            }
        }

        [MenuItem("Tools/UCL/Agent Commands/Show Queue Status", priority = 102)]
        public static void Menu_ShowStatus()
        {
            var data = UCL_AgentCommandQueue.Load();
            int total = data.Commands?.Count ?? 0;
            int oneshot = 0, repeatable = 0;
            foreach (var c in data.Commands ?? new List<UCL_AgentCommand>())
            {
                if (c.Mode == UCL_AgentCommandMode.Repeatable) repeatable++;
                else oneshot++;
            }
            Debug.Log($"[UCL_AgentCmd] Queue: {total} total / {oneshot} OneShot / {repeatable} Repeatable\n" +
                      $"Path: {UCL_AgentCommandQueue.GetQueuePath()}\n" +
                      $"Registered types: {string.Join(", ", UCL_AgentCommandRegistry.ListTypes())}");
        }

        // ===========================================================
        // Async runner
        // ===========================================================

        /// <summary>對外 API — 非阻塞執行 default queue pending commands (legacy)。</summary>
        public static UniTask RunAsync(CancellationToken token)
        {
            return RunAsync(null, token);
        }

        /// <summary>對外 API — 非阻塞執行 per-agent queue pending commands (agent-command-pipeline-parallelize T04).</summary>
        public static async UniTask RunAsync(string agentId, CancellationToken token)
        {
            string norm = NormAgent(agentId);
            lock (s_RunningLock)
            {
                if (s_RunningAgents.Contains(norm))
                {
                    Debug.LogWarning($"[UCL_AgentCmd] Already running for agentId='{norm}' — ignored.");
                    return;
                }
                s_RunningAgents.Add(norm);
            }
            string labelTag = string.IsNullOrEmpty(agentId) ? "default" : agentId;
            bool isPlayModeInterrupted = false;
            try
            {
                var data = UCL_AgentCommandQueue.Load(agentId);
                int total = data.Commands?.Count ?? 0;
                if (total == 0)
                {
                    Debug.Log($"[UCL_AgentCmd:{labelTag}] queue is empty (path: {UCL_AgentCommandQueue.GetQueuePath(agentId)})");
                    return;
                }

                Debug.Log($"[UCL_AgentCmd:{labelTag}] Loaded {total} command(s). Waiting for UCL_ModuleService...");

                // ★ 必做：先等模組系統就緒（WaitUntilInitialized 會自動觸發 Ins → Init → InitAsync）
                try
                {
                    await UCL_ModuleService.WaitUntilInitialized(token);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UCL_AgentCmd] UCL_ModuleService.WaitUntilInitialized failed: {e}");
                    return;
                }

                int succeeded = 0, failed = 0, removed = 0;
                var commands = data.Commands ?? new List<UCL_AgentCommand>();

                // 區塊職責：依序執行每筆指令
                // 物理意義：成功的 OneShot 指令會被立刻從 queue 中移除（執行完畢 = 任務結束）；
                //          Repeatable 與失敗的 OneShot 都留在 queue，並更新 LastRun* 與 RunCount。
                // 數值影響：執行完畢後 commands 內可能少於初始 count，最後 Save 會把刪除結果寫回 queue.json。
                for (int i = commands.Count - 1; i >= 0; i--) // 反向掃，方便移除
                {
                    if (token.IsCancellationRequested) break;
                    var c = commands[i];

                    // 區塊職責：把這筆指令登記到 History（讓使用者日後可從 UI 重用 agent 送來的指令）
                    // 物理意義：UI 端的 Add 路徑已會直接 Record(source=Manual)；這裡補的是
                    //          外部寫入 queue.json（Python wrapper / 手寫 / agent submit）這條路徑。
                    //          Record 內部會以 Type+Args 簽章 dedup，所以不會灌爆 History。
                    // 數值影響：寫 History/<Id>.json — 讀 queue 時若是相同簽章，只會 bump UseCount
                    //          且不會覆寫已存在的 Source 欄位（避免「Manual」紀錄被改成「Agent」）。
                    UCL_AgentCommandHistory.Record(c.Type, c.Mode, c.Args, c.Description, source: "Agent");

                    var handler = UCL_AgentCommandRegistry.Get(c.Type);
                    if (handler == null)
                    {
                        Debug.LogError($"[UCL_AgentCmd] Unknown command type '{c.Type}' (id={c.Id}). Registered: {string.Join(", ", UCL_AgentCommandRegistry.ListTypes())}");
                        c.LastRunResult = "Failed";
                        c.LastRunError = $"Unknown command type '{c.Type}'";
                        c.LastRunAt = DateTime.UtcNow.ToString("o");
                        failed++;
                        continue;
                    }

                    Debug.Log($"[UCL_AgentCmd] ▶ Run '{c.Type}' (id={c.Id}, mode={c.Mode}, runCount={c.RunCount})");
                    // 區塊職責：重置執行結果並立即存檔
                    // 物理意義：防範在跨 PlayMode 恢復時，殘留的舊 "Failed" 狀態未被清空，導致 Python 端 wrapper 輪詢時誤判失敗。
                    //          重置後立即同步存檔至 queue.json，確保 Python 端輪詢看到的狀態與執行一致。
                    // 數值影響：重置 c.LastRunResult = null, c.LastRunError = null，並 Save 磁碟。
                    c.LastRunResult = null;
                    c.LastRunError = null;
                    UCL_AgentCommandQueue.Save(data, agentId);

                    // 區塊職責: caller env_marker thread-through (Tim 2026-05-11 QA bug fix TreasuryEnvMarker)
                    // 物理意義: Python caller-side detect 寫進 args._caller_env_marker → runner 設 static slot →
                    //          下游 (UCL_TreasuryLedger.DetectEnvMarker / Cmd handler) 優先讀 slot 而非 in-process env
                    // 數值影響: 沒帶 _caller_env_marker (e.g. 手動寫 queue.json) → slot 設 null → DetectEnvMarker 走 fallback
                    string callerEnvMarker = null;
                    if (c.Args != null && c.Args.TryGetValue("_caller_env_marker", out var cem) && !string.IsNullOrEmpty(cem))
                    {
                        callerEnvMarker = cem;
                    }
                    UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.CurrentCallerEnvMarker = callerEnvMarker;
                    // T-LastOp-CmdId (2026-06-12)：把當前 cmd 的 queue Id 放進 static slot，
                    // 供下游 WriteLastOp stamp 進 _last_op.md（per-cmd finally 清掉防 cross-cmd leak）
                    CurrentCmdId = c.Id;
                    // 區塊職責: per-cmd timeout (agent-command-handler-timeout T02, Tim 2026-05-13 拍板)
                    // 物理意義: handler.TimeoutSeconds (default 1200 = 20min) 為 type-level default
                    //          caller 帶 args._timeout_sec=N → 即時覆寫該筆 cmd timeout (per-call override)
                    // 數值影響: timeout fire → 對 handler 發 CancellationToken cancel + 標 LastRunError=timeout
                    //          handler 不 honor token (e.g. sync File IO) 仍跑到結束 — Runner 不被卡死,
                    //          下一筆 cmd 照常跑 (Cancel ≠ Timeout caveat per Zeta 2026-05-13).
                    int timeoutSec = handler.TimeoutSeconds;
                    if (c.Args != null && c.Args.TryGetValue("_timeout_sec", out var tsRaw)
                        && int.TryParse(tsRaw, out var tsOverride) && tsOverride > 0)
                    {
                        timeoutSec = tsOverride;
                    }
                    try
                    {
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            var handlerTask = handler.ExecuteAsync(c.Args ?? new Dictionary<string, string>(), cts.Token);
                            var timeoutTask = UniTask.Delay(System.TimeSpan.FromSeconds(timeoutSec), DelayType.Realtime, cancellationToken: cts.Token);
                            var winner = await UniTask.WhenAny(handlerTask, timeoutTask);
                            if (winner == 1)
                            {
                                // timeout 先到 — cancel handler (handler 自己若 honor token 會停)
                                cts.Cancel();
                                throw new System.TimeoutException(
                                    $"cmd '{c.Type}' exceeded timeout {timeoutSec}s (handler.TimeoutSeconds={handler.TimeoutSeconds}, args._timeout_sec={(c.Args != null && c.Args.ContainsKey("_timeout_sec") ? c.Args["_timeout_sec"] : "(unset)")})");
                            }
                            else
                            {
                                // 區塊職責：等待執行完畢並解包 Exception
                                // 物理意義：UniTask.WhenAny 只等待 Task 完成（無論成功或異常），不拋出其內部的 exception。
                                //          若 handlerTask 發生例外 (例如跨 PlayMode 被 cancellation 中斷)，在此 await 能將異常拋出，
                                //          使 Runner 的 catch 區塊能補捉並寫入 queue.json "Failed" 狀態以利後續 Watcher 自癒重啟。
                                // 數值影響：若任務失敗則拋出異常；若成功則無影響。
                                await handlerTask;
                            }
                        }
                        c.LastRunResult = "Success";
                        c.LastRunError = null;
                        c.LastRunAt = DateTime.UtcNow.ToString("o");
                        c.RunCount++;
                        succeeded++;
                        Debug.Log($"[UCL_AgentCmd] ✓ '{c.Type}' (id={c.Id}) succeeded. RunCount={c.RunCount}");

                        // OneShot 成功 → 直接從 queue 中移除（任務已完成）
                        if (c.Mode == UCL_AgentCommandMode.OneShot)
                        {
                            commands.RemoveAt(i);
                            removed++;
                        }
                    }
                    catch (Exception e)
                    {
                        if (EditorApplication.isPlayingOrWillChangePlaymode && c.Mode == UCL_AgentCommandMode.OneShot)
                        {
                            // 區塊職責：進入 PlayMode 轉移期間的特殊處理
                            // 物理意義：因為進入 PlayMode 會強制中斷並銷毀 EditMode 的 UniTask 執行緒，
                            //          這會引發預期的 NullReferenceException 或 OperationCanceledException。
                            //          我們不應將其視為「真正失敗」，而應保留其在 queue 中的 Pending 狀態，
                            //          以便在進入 PlayMode 後由 Watcher 的自癒機制接手恢復執行。
                            // 數值影響：不將 LastRunResult 設為 "Failed"（保持 null），LastRunError 設為轉移標記，
                            //          不增加 failed 計數。
                            isPlayModeInterrupted = true;
                            c.LastRunResult = null;
                            c.LastRunError = "Interrupted by PlayMode transition, waiting for self-healing resumption...";
                            Debug.Log($"[UCL_AgentCmd] ℹ '{c.Type}' (id={c.Id}) interrupted by PlayMode transition. Keeping in queue for resumption.");
                        }
                        else
                        {
                            c.LastRunResult = "Failed";
                            c.LastRunError = e.Message;
                            c.LastRunAt = DateTime.UtcNow.ToString("o");
                            failed++;
                            Debug.LogError($"[UCL_AgentCmd] ✗ '{c.Type}' (id={c.Id}) failed: {e}");
                        }
                    }
                    finally
                    {
                        // 清掉 per-cmd 的 caller env_marker slot, 防 cross-cmd leak
                        UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.CurrentCallerEnvMarker = null;
                        // T-LastOp-CmdId：同步清 cmd_id slot — 防下一筆 cmd（或非 queue 路徑的 WriteLastOp）誤 stamp 上一筆的 id
                        CurrentCmdId = null;
                    }
                }

                data.Commands = commands;
                UCL_AgentCommandQueue.Save(data, agentId);
                Debug.Log($"[UCL_AgentCmd:{labelTag}] Done. {succeeded} succeeded / {failed} failed / {removed} OneShot removed after success.");
            }
            finally
            {
                // 區塊職責：無論成功 / 失敗 / 例外都要清掉 trigger 檔 (per-agent)，但 PlayMode 轉移中斷除外
                // 物理意義：pending.trigger.running 是 Python 端「Editor 是否還在執行」的判定依據。
                //          但在 PlayMode 轉移中斷時，我們必須保留這個執行鎖，以便 PlayMode 啟動後 Watcher 能偵測到「孤兒鎖」並重啟 Runner 自癒。
                // 數值影響：若 isPlayModeInterrupted 為 true 則跳過 Clear(agentId) 保留鎖檔案，否則刪除檔案解鎖。
                if (isPlayModeInterrupted)
                {
                    Debug.Log($"[UCL_AgentCmd:{labelTag}] PlayMode transition detected. Preserving running trigger file on disk for self-healing resumption.");
                }
                else
                {
                    UCL_AgentCommandTrigger.Clear(agentId);
                }
                lock (s_RunningLock) s_RunningAgents.Remove(norm);
            }
        }
    }
}
#endif
