
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
    ///    - 成功：RunCount++ / 寫 LastRunAt+Result=Success + result 檔；OneShot 直接從 queue 移除
    ///    - 失敗：寫 LastRunError + Result=Failed + 錯誤報告檔 + result 檔（不中斷，繼續下一筆）；
    ///      **OneShot 失敗也自動出隊**（2026-08-07 —— 殘留會被每批重跑，見 catch 區塊註解），
    ///      Repeatable 留在 queue（語意本來就是反覆跑）
    /// 4. 寫回 queue.json；verdict 一律在 _cmd_results/&lt;id&gt;.json（消失＝結束，不再＝成功）
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

        // 區塊職責：per-cmd 產出檔收集 slot（Tim 2026-08-13 拍板 —— 路徑直接寫進 result 檔）
        // 物理意義：handler 落回傳檔（如 letters/<P>/_goodmorning_wake.md）時 caller 只拿到
        //          Success/Failed，「檔在哪」得靠 skill/文件的文字背 —— letters root 跨專案
        //          會漂，agent 照字面讀就 File not found（wake#48 血證）。handler 經
        //          ReportOutputFile 回報 → WriteCmdResult 寫進 _cmd_results/<id>.json 的
        //          outputs 欄位 → run_cmd.py 隨 verdict 一起印，路徑不再靠背。
        // 數值影響：成功與失敗都寫（blocked 也會先落 payload 再 throw，路徑同樣有用）；
        //          同路徑去重、保序。非 queue 路徑（IMGUI 手動跑 handler）沒有 result 檔，
        //          回報無處可去 —— 清單在下一筆 cmd 起跑前 Clear，不跨筆污染。
        static readonly List<string> s_CurrentCmdOutputs = new List<string>();

        /// <summary>handler 回報本次執行落了哪個檔（絕對路徑）；會寫進 result 檔 outputs 欄，caller 端隨 verdict 印出。</summary>
        public static void ReportOutputFile(string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return;
            lock (s_CurrentCmdOutputs)
            {
                if (!s_CurrentCmdOutputs.Contains(iPath)) s_CurrentCmdOutputs.Add(iPath);
            }
        }

        // 區塊職責：per-cmd 回傳「值」收集 slot（2026-08-15）——與 s_CurrentCmdOutputs 對稱，
        //          差別是那邊裝**檔案路徑**，這邊裝**純量結果**（seq / 筆數 / 判定…）。
        // 物理意義：caller 目前只拿得到 Success/Failed ＋ 產出檔路徑。有一類結果既不是檔也不是
        //          成敗 —— 最典型的是 `op=post` 剛寫進去的那個 seq。agent 拿不到它就只能**用數的**，
        //          而自動公告（git_commit 領薪）會在兩人回合之間吃掉號碼 ⇒ 手數必漂，
        //          且漂掉之後每一則引用都長得完全正常（2026-08-15 實測：兩人各兩筆 ↩seq 指錯）。
        // ⚠ 為什麼不塞進 outputs：那個欄位的語意是**產出檔路徑**，run_cmd.py 印成「📄 回傳檔：…」。
        //          把一個 seq 放進去會印出「📄 回傳檔：15173」——**名字比事實大**，而下游會照字面相信它。
        // 數值影響：**純加法且惰性** —— 沒有 handler 呼叫就永遠是空的、result 檔不長 values 欄、
        //          run_cmd.py 不印。「沒有人用它」是預設狀態，不是失敗狀態。
        // 邊界：同 key 重複回報 → **保留全部、不覆寫**（單一 cmd 內 Op_Post 可能跑不只一次，
        //      例如 task_done→share 的 Cmd_Tavern.cs:2355；後面蓋前面會讓 caller 拿到另一筆的號碼，
        //      而它長得完全正常）。清單在下一筆 cmd 起跑前 Clear，不跨筆污染。
        static readonly List<(string Key, string Value)> s_CurrentCmdValues = new List<(string, string)>();

        /// <summary>handler 回報本次執行的一個純量結果（如 post_seq）；寫進 result 檔 values 欄，caller 端隨 verdict 印出。</summary>
        /// <remarks>
        /// **在產生值的當下呼叫（push），不要事後去撈某個 static（pull）。**
        /// pull 的寫法會讓值離開它的壽命 —— `Cmd_Tavern.LastPostSeq` 的註解明寫「只在同一筆 cmd 的
        /// 執行流程內讀」，而單一 cmd 內 Op_Post 可能跑兩次。push 讓那個競態**不存在**，而不是把它管好。
        /// </remarks>
        public static void ReportOutputValue(string iKey, string iValue)
        {
            if (string.IsNullOrEmpty(iKey)) return;
            lock (s_CurrentCmdValues) s_CurrentCmdValues.Add((iKey, iValue ?? ""));
        }

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
                // 硬規則：C# 開的每顆外部 Process 都要登記（Coding_Standards.md「外部 Process」）。
                // fire-and-forget → StartAndRegister（不 singleton、無 Unregister，靠 CleanupStale 回收）。
                UCL.Core.EditorLib.UCL_ProcessRegistryService.StartAndRegister(
                    new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true },
                    "explorer_open", $"開啟 queue 資料夾：{dir}", nameof(UCL_AgentCommandRunner));
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
                        // 區塊職責：Unknown type 的錯誤訊息帶 did-you-mean 與完整註冊清單進 LastRunError。
                        // 物理意義：過去清單只印 Editor console，CLI 呼叫端只收到一句錯誤，得挖 Editor.log
                        //          才知道正名（summit 血證 2026-07-31）— 錯誤必須離開私有欄位才算存在。
                        // 數值影響：LastRunError 變長（建議 + 32 個名稱 ≈ 數百字元），由 run_cmd 原樣印給呼叫端。
                        var suggestions = UCL_AgentCommandRegistry.SuggestTypes(c.Type);
                        string didYouMean = suggestions.Count > 0 ? $" Did you mean: {string.Join(" / ", suggestions)}?" : "";
                        string registered = string.Join(", ", UCL_AgentCommandRegistry.ListTypes());
                        Debug.LogError($"[UCL_AgentCmd] Unknown command type '{c.Type}' (id={c.Id}).{didYouMean} Registered: {registered}");
                        c.LastRunResult = "Failed";
                        c.LastRunError = $"Unknown command type '{c.Type}'.{didYouMean} Registered: {registered}";
                        c.LastRunAt = DateTime.UtcNow.ToString("o");
                        failed++;
                        // 失敗即出隊（Tim 2026-08-07 拍板，成對改的 Editor 半邊）——
                        // unknown type 留在 queue 只會每批重印一次同樣的錯，永遠不會自己好。
                        WriteCmdErrorReport(c, new InvalidOperationException(c.LastRunError));
                        WriteCmdResult(c, success: false, error: c.LastRunError);
                        if (c.Mode == UCL_AgentCommandMode.OneShot)
                        {
                            commands.RemoveAt(i);
                            removed++;
                        }
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
                    // 產出檔 collector 每筆起跑前歸零 —— WriteCmdResult 在 finally 之前讀，
                    // 這裡不清的話上一筆（或非 queue 路徑）的回報會混進本筆 outputs
                    lock (s_CurrentCmdOutputs) s_CurrentCmdOutputs.Clear();
                    // 回傳值 collector 同理歸零 —— 漏這一行的話「上一筆的 seq」會出現在本筆的
                    // result 檔裡，而它是個完全合理的數字，沒有任何地方會叫。
                    lock (s_CurrentCmdValues) s_CurrentCmdValues.Clear();
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
                        // 區塊職責：**執行前**的 ArgsSpec Required 檢查（2026-08-14 新增）。
                        // 物理意義：ArgsSpec 在此之前是一份沒有人執行的宣告 —— 只有匯出器讀它。
                        //          於是打錯參數名不會報錯，`GetArg(args, key, default)` 會安靜地給預設值，
                        //          而 cmd 照樣回 Success。這道檢查讓「宣告了 Required」第一次有實際效果。
                        // 數值影響：擋下時**不執行 handler**，並走既有的失敗路徑（catch → WriteCmdResult）。
                        //          未宣告 ArgsSpec 的 handler 一律通過（37/39 目前如此）—— 那一態的語意
                        //          尚未拍板，這裡刻意維持現況而不替它決定（見 UCL_CmdArgsValidator.Validate 的 remarks）。
                        //
                        // ⚠ **必須在 try 內。** 第一版我把它寫在 try 之前 —— 擋下時例外繞過了
                        //   catch/WriteCmdResult，於是**沒有任何 result 檔落地**，client 一路輪詢到
                        //   120s timeout。那比它要防的病更糟：原本是「靜默取預設值但會結束」，
                        //   變成「擋住了，但呼叫端不知道，只知道掛住」（2026-08-14 實測自摔）。
                        //   **一道防護的失敗方式，不可以比它防的東西更難診斷。**
                        if (!UCL_CmdArgsValidator.Validate(handler, c.Args, out string aArgsError))
                        {
                            throw new System.ArgumentException(aArgsError);
                        }

                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            // .Preserve()（2026-07-29 修）：UniTask 是 struct-based single-await —
                            // 同一個 UniTask 被 await 兩次會拋
                            // 「Token version is not matched, can not await twice or get Status after await」。
                            // 本區塊下面刻意要「WhenAny 之後再 await handlerTask 一次」來解包 exception，
                            // 兩次 await 同一個 task 正是那個錯誤的成因（血證：Cmd_NoteLesson 每次都掛，
                            // 而且錯誤訊息把真正的 handler 例外整個蓋掉，看起來像 runner 壞了）。
                            // Preserve() 讓它可重複 await，語意不變。
                            var handlerTask = handler.ExecuteAsync(c.Args ?? new Dictionary<string, string>(), cts.Token).Preserve();
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
                        // 成功也落 result 檔 —— 成對改的關鍵：失敗會出隊之後，「從 queue 消失」
                        // 就同時可能是成功或失敗，python 端不能再用消失推論成功，
                        // 要有一份 per-cmd 的 verdict 可讀（消失＝結束，verdict 在 result 檔）。
                        WriteCmdResult(c, success: true, error: null);

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
                            // 詳細錯誤落檔（Tim 2026-07-29）：只印 Editor log 的話，python client 只拿到
                            // e.Message 一行 —— 遇到被遮罩的錯誤（例如 UniTask token 錯誤蓋掉真正的 handler
                            // 例外）就查不動，得請人肉去翻 Editor console。落檔讓 client 端能自己讀 stack。
                            WriteCmdErrorReport(c, e);
                            WriteCmdResult(c, success: false, error: e.Message);
                            // 區塊職責：失敗的 OneShot 即時出隊（Tim 2026-08-07 拍板 —— queue 不堵塞的
                            //          Editor 半邊；python 半邊是 run_cmd 改讀 result 檔，不再消失＝成功）。
                            // 物理意義：舊行為「失敗留在 queue」的災難鏈：caller 沒等到（no-wait / timeout /
                            //          session 死掉）→ 殘留永遠在 → 之後**每一批都重跑它一次**（副作用重放，
                            //          Tavern post 會重發、轉帳會重轉）→ 若它是掛住型失敗，每批多等一次
                            //          per-cmd timeout → ensure_idle 60 秒放棄 → 「後續指令無法執行」。
                            // 數值影響：verdict 不因出隊而遺失 —— _cmd_errors/<id>.md（stack 全文）與
                            //          _cmd_results/<id>.json（機器可讀 verdict）都在出隊前寫完。
                            //          Repeatable 照舊留在 queue（它的語意本來就是反覆跑）。
                            if (c.Mode == UCL_AgentCommandMode.OneShot)
                            {
                                commands.RemoveAt(i);
                                removed++;
                                Debug.Log($"[UCL_AgentCmd] ↳ '{c.Type}' (id={c.Id}) 失敗已自動出隊"
                                          + "（verdict 在 _cmd_results/，詳情在 _cmd_errors/）");
                            }
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
                Debug.Log($"[UCL_AgentCmd:{labelTag}] Done. {succeeded} succeeded / {failed} failed / {removed} OneShot removed (success or auto-dequeued failure).");
                PurgeOldCmdResults();
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

        // ===========================================================
        // 區塊：per-cmd 執行結果落檔（Tim 2026-08-07 拍板 —— 成對改的 Editor 半邊）
        // 物理意義：失敗會自動出隊之後，「cmd 從 queue 消失」同時可能是成功或失敗 ——
        //          python 端需要一份機器可讀的 verdict 檔，不能再用消失推論成功。
        //          成功與失敗都寫（只寫失敗的話，「沒有檔」又變回要推論的空白）。
        // 數值影響：<DataRoot>/_cmd_results/<cmdId>.json；失敗時附 error 與
        //          error_report 路徑（_cmd_errors/<id>.md）。IO 失敗吞掉 ——
        //          result 檔寫不出來時 python 端 fallback 回舊推論，不擋執行。
        // ===========================================================
        static void WriteCmdResult(UCL_AgentCommand c, bool success, string error)
        {
            try
            {
                string dataRoot = UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot;
                string dir = System.IO.Path.Combine(dataRoot, "_cmd_results");
                System.IO.Directory.CreateDirectory(dir);
                var jd = new UCL.Core.JsonLib.JsonData();
                jd["id"] = new UCL.Core.JsonLib.JsonData(c.Id ?? "");
                jd["type"] = new UCL.Core.JsonLib.JsonData(c.Type ?? "");
                jd["mode"] = new UCL.Core.JsonLib.JsonData(c.Mode.ToString());
                jd["result"] = new UCL.Core.JsonLib.JsonData(success ? "Success" : "Failed");
                jd["finished_at"] = new UCL.Core.JsonLib.JsonData(DateTime.UtcNow.ToString("o"));
                // outputs：handler 經 ReportOutputFile 回報的產出檔（回傳檔 / payload）——
                // caller 端（run_cmd.py）隨 verdict 一起印，agent 不用再靠 skill 文字背路徑
                lock (s_CurrentCmdOutputs)
                {
                    if (s_CurrentCmdOutputs.Count > 0)
                    {
                        var aOutputs = new UCL.Core.JsonLib.JsonData();
                        foreach (var aOut in s_CurrentCmdOutputs) aOutputs.Add(aOut);
                        jd["outputs"] = aOutputs;
                    }
                }
                // values：handler 經 ReportOutputValue 回報的純量結果（post_seq 等）。
                // 陣列而非物件 —— 同一 key 可能出現多次（單一 cmd 內 Op_Post 跑兩次），
                // 用物件會後面蓋前面，而被蓋掉的那筆長得完全正常。
                lock (s_CurrentCmdValues)
                {
                    if (s_CurrentCmdValues.Count > 0)
                    {
                        var aValues = new UCL.Core.JsonLib.JsonData();
                        foreach (var aKv in s_CurrentCmdValues)
                        {
                            var aOne = new UCL.Core.JsonLib.JsonData();
                            aOne["key"] = new UCL.Core.JsonLib.JsonData(aKv.Key);
                            aOne["value"] = new UCL.Core.JsonLib.JsonData(aKv.Value);
                            aValues.Add(aOne);
                        }
                        jd["values"] = aValues;
                    }
                }
                if (!success)
                {
                    jd["error"] = new UCL.Core.JsonLib.JsonData(error ?? "");
                    jd["error_report"] = new UCL.Core.JsonLib.JsonData(
                        System.IO.Path.Combine(dataRoot, "_cmd_errors", $"{c.Id}.md"));
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{c.Id}.json"),
                    jd.ToJsonBeautify(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_AgentCmd] result 落檔失敗（python 端會 fallback 舊推論）：{ex.Message}");
            }
        }

        // result 檔只服務「caller 稍後來對答案」的窗口 —— 3 天後還沒人讀就不會有人讀了。
        // 每批結尾清一次；_cmd_errors/ 刻意不清（那是回溯用的永久紀錄，且已 gitignore）。
        static void PurgeOldCmdResults()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "_cmd_results");
                if (!System.IO.Directory.Exists(dir)) return;
                var cutoff = DateTime.UtcNow.AddDays(-3);
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.json"))
                {
                    if (System.IO.File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        System.IO.File.Delete(f);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_AgentCmd] result 檔清理失敗（不影響執行）：{ex.Message}");
            }
        }

        // ===========================================================
        // 區塊：cmd 失敗詳情落檔（Tim 2026-07-29 拍板）
        // 物理意義：失敗時 queue 只留 LastRunError 一行（e.Message），完整 stack 只在 Editor console —
        //          python client 看不到，agent 得請人肉翻 log。落檔後 client 可直接讀，
        //          尤其對「被遮罩的錯誤」（外層例外蓋掉真正的 handler 例外）是唯一線索。
        // 數值影響：寫兩個地方 —
        //          <DataRoot>/_cmd_errors/<cmdId>.md（永久保留，可回溯任何一筆）
        //          <DataRoot>/_last_cmd_error.md（最近一筆，client 預設讀這份）
        //          任何 IO 失敗都吞掉：報告寫不出來不該再蓋掉原始錯誤。
        // ===========================================================
        static void WriteCmdErrorReport(UCL_AgentCommand c, Exception e)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "_cmd_errors");
                System.IO.Directory.CreateDirectory(dir);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# ✗ Cmd 失敗：{c.Type}");
                sb.AppendLine();
                sb.AppendLine($"- **cmd_id**: `{c.Id}`");
                sb.AppendLine($"- **type**: `{c.Type}` / mode: `{c.Mode}`");
                sb.AppendLine($"- **失敗時間**: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local) / {DateTime.UtcNow:o} (UTC)");
                sb.AppendLine($"- **例外型別**: `{e.GetType().FullName}`");
                sb.AppendLine($"- **訊息**: {e.Message}");
                sb.AppendLine();

                // 區塊職責：ArgsSpec 三態提示 —— **只在這裡出現**（2026-08-14 拍板）。
                // 物理意義：「未宣告 ArgsSpec」有 37 個成員，做成清單掛在牆上第三天就沒人看。
                //          裝在失敗報告裡則是長在必經的路上：讀這份報告的人正在查這個 Cmd，
                //          而且一次只會看到一個。已用 [UCL_UnvalidatedArgs] 表態的不提示。
                // 數值影響：純附註，不影響 verdict。取不到 handler（type 已移除）就跳過。
                try
                {
                    var aHandler = UCL_AgentCommandRegistry.Get(c.Type);
                    string aHint = UCL_CmdArgsValidator.DescribeSpecState(aHandler);
                    if (!string.IsNullOrEmpty(aHint))
                    {
                        sb.AppendLine($"> ℹ️ {aHint}");
                        sb.AppendLine();
                    }
                }
                catch (Exception) { /* 提示是加值，取不到就不提示，絕不蓋掉原始錯誤 */ }

                sb.AppendLine("## Args");
                if (c.Args == null || c.Args.Count == 0)
                {
                    sb.AppendLine("(無)");
                }
                else
                {
                    foreach (var kv in c.Args)
                    {
                        string v = kv.Value ?? "";
                        // 長 body 截斷 — 報告是給人看的，全文本來就在 queue/History
                        if (v.Length > 300) v = v.Substring(0, 300) + $"…（共 {kv.Value.Length} 字）";
                        sb.AppendLine($"- `{kv.Key}` = {v.Replace("\n", "\\n")}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("## Stack trace");
                sb.AppendLine("```");
                sb.AppendLine(e.ToString());
                sb.AppendLine("```");

                // inner exception 鏈全展開 — 被遮罩的真兇通常躲在這裡
                var inner = e.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## Inner exception #{++depth}：`{inner.GetType().FullName}`");
                    sb.AppendLine("```");
                    sb.AppendLine(inner.ToString());
                    sb.AppendLine("```");
                    inner = inner.InnerException;
                }

                string report = sb.ToString();
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{c.Id}.md"),
                    report, new System.Text.UTF8Encoding(false));
                System.IO.File.WriteAllText(System.IO.Path.Combine(
                    UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "_last_cmd_error.md"),
                    report, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[UCL_AgentCmd] 失敗詳情落檔失敗（不影響原始錯誤回報）：{ex2.Message}");
            }
        }
    }
}
#endif
