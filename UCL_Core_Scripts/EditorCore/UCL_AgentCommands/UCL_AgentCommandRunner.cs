
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
        /// <summary>是否正在執行（防止重複觸發）。</summary>
        static bool s_Running = false;

        /// <summary>對外查詢：runner 是否正忙著跑 batch。Watcher 用此避免重入。</summary>
        public static bool IsRunning => s_Running;

        // ===========================================================
        // Menu Items（Tools/UCL/Agent Commands/）
        // ===========================================================

        [MenuItem("Tools/UCL/Agent Commands/Run Pending Commands", priority = 100)]
        public static void Menu_RunPending()
        {
            if (s_Running)
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

        /// <summary>對外 API — 非阻塞執行 pending commands。</summary>
        public static async UniTask RunAsync(CancellationToken token)
        {
            if (s_Running)
            {
                Debug.LogWarning("[UCL_AgentCmd] Already running — ignored.");
                return;
            }
            s_Running = true;
            try
            {
                var data = UCL_AgentCommandQueue.Load();
                int total = data.Commands?.Count ?? 0;
                if (total == 0)
                {
                    Debug.Log($"[UCL_AgentCmd] queue.json is empty (path: {UCL_AgentCommandQueue.GetQueuePath()})");
                    return;
                }

                Debug.Log($"[UCL_AgentCmd] Loaded {total} command(s). Waiting for UCL_ModuleService...");

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
                    try
                    {
                        await handler.ExecuteAsync(c.Args ?? new Dictionary<string, string>(), token);
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
                        c.LastRunResult = "Failed";
                        c.LastRunError = e.Message;
                        c.LastRunAt = DateTime.UtcNow.ToString("o");
                        failed++;
                        Debug.LogError($"[UCL_AgentCmd] ✗ '{c.Type}' (id={c.Id}) failed: {e}");
                    }
                    finally
                    {
                        // 清掉 per-cmd 的 caller env_marker slot, 防 cross-cmd leak
                        UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.CurrentCallerEnvMarker = null;
                    }
                }

                data.Commands = commands;
                UCL_AgentCommandQueue.Save(data);
                Debug.Log($"[UCL_AgentCmd] Done. {succeeded} succeeded / {failed} failed / {removed} OneShot removed after success.");
            }
            finally
            {
                // 區塊職責：無論成功 / 失敗 / 例外都要清掉 trigger 檔
                // 物理意義：pending.trigger.running 是 Python 端「Editor 是否還在執行」的判定依據；
                //          殘留會導致下一次 Python ensure_idle() 永遠等不到 idle，整個流程鎖死。
                // 數值影響：刪除 .running（與保險用的 .trigger）→ 狀態回到 idle，外部可繼續 submit。
                UCL_AgentCommandTrigger.Clear();
                s_Running = false;
            }
        }
    }
}
#endif
