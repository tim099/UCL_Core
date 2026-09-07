
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
        // 物理意義：handler 落回傳檔（如 letters/<P>/cmd/goodmorning_wake.md）時 caller 只拿到
        //          Success/Failed，「檔在哪」得靠 skill/文件的文字背 —— letters root 跨專案
        //          會漂，agent 照字面讀就 File not found（wake#48 血證）。handler 經
        //          ReportOutputFile 回報 → WriteCmdResult 寫進 _cmd_results/<id>.json 的
        //          outputs 欄位 → run_cmd.py 隨 verdict 一起印，路徑不再靠背。
        // 數值影響：成功與失敗都寫（blocked 也會先落 payload 再 throw，路徑同樣有用）；
        //          同路徑去重、保序。非 queue 路徑（IMGUI 手動跑 handler）沒有 result 檔，
        //          回報無處可去 —— 取不到 context 時 fail-soft 略過並出聲。
        //
        // 🩸 2026-08-16 改制（basecamp，Tim 拍板「讓 Cmd 能平行跑」）：
        //    原本這裡是一顆 `static readonly List<string> s_CurrentCmdOutputs`，每筆 cmd 起跑前 Clear。
        //    那個設計**只在串行下正確** —— queue 依 persona 分 lane 後 watcher 會並行派遣，
        //    兩條 lane 會互相 Clear／互相寫入，而結果是「拿到別人的回傳檔路徑」，**完全無聲**。
        //    ⇒ 改成 per-cmd context，由 `args["_cmd_id"]` 顯式索引（不是 ambient —— AsyncLocal
        //      在 UniTask 下不隔離，實測見 `UCL_AgentCmdScopeProbe.SelfTestConcurrent`）。
        //    ⚠ 舊的無參數多載**刻意不保留**：留著它，漏改的呼叫端會安靜地走回全域；
        //      刪掉它，漏改的呼叫端是**編譯錯誤**。壞掉要壞在看得見的地方。

        /// <summary>handler 回報本次執行落了哪個檔（絕對路徑）；會寫進 result 檔 outputs 欄，caller 端隨 verdict 印出。</summary>
        /// <param name="iArgs">handler 收到的那份 args —— context 由其中的 <c>_cmd_id</c> 索引。</param>
        public static void ReportOutputFile(IDictionary<string, string> iArgs, string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return;
            UCL_AgentCmdContexts.FromArgs(iArgs, nameof(ReportOutputFile))?.AddOutput(iPath);
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
        //      而它長得完全正常）。存放處同 outputs：per-cmd context，不再是全域清單。

        /// <summary>handler 回報本次執行的一個純量結果（如 post_seq）；寫進 result 檔 values 欄，caller 端隨 verdict 印出。</summary>
        /// <remarks>
        /// **在產生值的當下呼叫（push），不要事後去撈某個 static（pull）。**
        /// pull 的寫法會讓值離開它的壽命，而單一 cmd 內 Op_Post 可能跑兩次。
        /// push 讓那個競態**不存在**，而不是把它管好。
        /// </remarks>
        public static void ReportOutputValue(IDictionary<string, string> iArgs, string iKey, string iValue)
        {
            if (string.IsNullOrEmpty(iKey)) return;
            UCL_AgentCmdContexts.FromArgs(iArgs, nameof(ReportOutputValue))?.AddValue(iKey, iValue);
        }

        // ===========================================================
        // 區塊職責：本次 cmd 若**沒有**鏡寫 per-persona last_op，就地留下一份「本支不寫」的 stub。
        // 物理意義：`letters/<p>/cmd/<slug>_last_op.md` 是 agent 判斷「我剛才那筆做了什麼」的第一手來源。
        //   不寫 last_op 的 op（如 `AutoCommit op=scan`）**不會覆蓋**上一份 ⇒ 上一份就永久留在原地，
        //   而它格式完整、數字合理、看起來像剛產生的。**「陳舊」與「剛產生」在檔案上同形。**
        // 數值影響：只在「拿得到 lane」且「本次 outputs 裡沒有任何 *_last_op.md」時寫；
        //   內容含本次 cmd_id／型別／時間，並把**被取代的那一份**的 cmd_id 與 mtime 一起印回去
        //   （不靜默丟掉前一份的身分 —— 那會讓「被 stub 蓋掉」與「本來就沒有」再同形一次）。
        // ⛔ 刻意**不刪檔**：刪掉之後「這支不寫 last_op」與「這個 persona 沒跑過這支」又會同形。
        // ===========================================================
        static void WriteLastOpStubIfAbsent(UCL_AgentCommand iCmd)
        {
            if (iCmd == null || string.IsNullOrEmpty(iCmd.Id)) return;
            var aCtx = UCL_AgentCmdContexts.Get(iCmd.Id);
            if (aCtx == null || string.IsNullOrEmpty(aCtx.AgentId)) return;   // 非 queue 路徑：不動

            // lane ＝ AgentId 的第一段（per-room 子佇列長 `summit/chess-5`，其餘是房間路由不是身分）
            string aPersona = aCtx.AgentId;
            int aSep = aPersona.IndexOfAny(new[] { '/', '\\' });
            if (aSep >= 0) aPersona = aPersona.Substring(0, aSep);
            if (aPersona.Length == 0) return;

            foreach (var aOut in aCtx.SnapshotOutputs())
                if (!string.IsNullOrEmpty(aOut) && aOut.EndsWith("_last_op.md", StringComparison.OrdinalIgnoreCase))
                    return;   // 本次真的寫過 ⇒ 什麼都不做

            int aCut = iCmd.Id.LastIndexOf('-');
            string aSlug = aCut >= 0 && aCut < iCmd.Id.Length - 1 ? iCmd.Id.Substring(aCut + 1) : "cmd";
            string aPath = UCL_LettersPath.CmdPayload(aPersona, aSlug, "last_op");

            // 被取代的那一份：把它的身分抄進 stub，不靜默丟掉
            string aPrev = "（本次之前沒有這個檔）";
            if (System.IO.File.Exists(aPath))
            {
                string aPrevId = "(無 cmd_id 章)";
                try
                {
                    // ⚠ 抄的是**那顆 id**，不是整行 HTML 註解 —— 直接 Trim() 整行會印成
                    //   「cmd_id `<!-- cmd_id: … -->`」（標籤重複＋註解語法漏進正文）。
                    //   這一行是 stub 唯一保留前一份身分的地方，它自己不能難讀。
                    const string aOpen = "<!-- cmd_id:";
                    foreach (var aLine in System.IO.File.ReadAllLines(aPath))
                    {
                        int aS = aLine.IndexOf(aOpen, StringComparison.Ordinal);
                        if (aS < 0) continue;
                        aS += aOpen.Length;
                        int aE = aLine.IndexOf("-->", aS, StringComparison.Ordinal);
                        string aId = (aE > aS ? aLine.Substring(aS, aE - aS) : aLine.Substring(aS)).Trim();
                        if (aId.Length > 0) aPrevId = aId;
                        break;
                    }
                }
                catch { aPrevId = "(讀不到，已覆寫)"; }
                aPrev = $"cmd_id `{aPrevId}`／mtime {System.IO.File.GetLastWriteTime(aPath):yyyy-MM-dd HH:mm:ss}";
            }

            var aSb = new System.Text.StringBuilder();
            aSb.AppendLine("# ⚠ 本次 op 沒有產出 last_op");
            aSb.AppendLine($"<!-- cmd_id: {iCmd.Id} -->");
            aSb.AppendLine();
            aSb.AppendLine($"- cmd   : `{iCmd.Type}`　persona: `{aPersona}`");
            aSb.AppendLine($"- 時間  : {DateTime.Now:yyyy-MM-dd HH:mm:sszzz}（本地時間）");
            aSb.AppendLine("- 這一支**沒有呼叫 `WriteLastOp`** ⇒ 它的讀數不在這裡：");
            aSb.AppendLine("  看 `_cmd_results/<id>.json` 的 `values`（CLI 印成 `🔢 k = v`），或它自己的回傳檔。");
            aSb.AppendLine($"- 被本行取代的前一份：{aPrev}");
            aSb.AppendLine();
            aSb.AppendLine("> 📌 這一行是**刻意寫的**（TASK-0116）。沒有它的話，上一次的內容會留在原地，");
            aSb.AppendLine("> 而「三天前別人的讀數」與「剛剛我自己的讀數」在這個檔上長得一模一樣。");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aPath));
            System.IO.File.WriteAllText(aPath, aSb.ToString(), new System.Text.UTF8Encoding(false));
            aCtx.AddOutput(aPath);
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
                // ⏱ 批次前奏的秒錶（TASK-0162）—— queue load ＋ ModuleService 等待都在主緒上，
                //   而 2026-09-07 的讀數說 AutoCommit offload 之後仍有 1.3s 斷拍落在 handler **之前**。
                //   ⇒ 這一格是那個嫌疑犯，先量再改。
                var batchWatch = System.Diagnostics.Stopwatch.StartNew();
                var data = UCL_AgentCommandQueue.Load(agentId);
                double queueLoadMs = batchWatch.Elapsed.TotalMilliseconds;
                int total = data.Commands?.Count ?? 0;
                if (total == 0)
                {
                    Debug.Log($"[UCL_AgentCmd:{labelTag}] queue is empty (path: {UCL_AgentCommandQueue.GetQueuePath(agentId)})");
                    return;
                }

                Debug.Log($"[UCL_AgentCmd:{labelTag}] Loaded {total} command(s). Waiting for UCL_ModuleService...");
                double beforeModuleWaitMs = batchWatch.Elapsed.TotalMilliseconds;

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

                // ⏱ ModuleService 等待的實際耗時（前奏第二格）
                double moduleWaitMs = batchWatch.Elapsed.TotalMilliseconds - beforeModuleWaitMs;

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
                    // queueId：Runner 的 agentId 為 null＝跑的是預設 queue，而預設 queue 的實體
                    // 資料夾就是 queues/anonymous/ —— 所以這裡顯式正規化成 AnonymousQueueId。
                    // ⚠ 這不是「猜它是匿名」，是**讀出它真的落在哪個資料夾**（GetQueueDir(null) 同解）。
                    UCL_AgentCommandHistory.Record(c.Type, c.Mode, c.Args, c.Description, source: "Agent",
                        queueId: string.IsNullOrEmpty(agentId) ? UCL_AgentCommandQueue.AnonymousQueueId : agentId);

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

                    // ⏱ Runner 這一圈的秒錶（TASK-0161）—— **起點刻意在 handler 之外**：
                    //   本圈的 queue reset+Save、ArgsSpec 檢查、result 落檔、last_op stub 都在裡面。
                    // 🩸 第一版我把它起在 Begin 旁邊，量出 runner_ms 818.3 / handler 819.0 ——
                    //   兩個數字幾乎相同 ⇒ 那個位置**什麼都沒多包到**，而它看起來完全正常。
                    //   實測要包的是這一格：handler 869.3ms 而同一區間主執行緒斷拍 2007.1ms。
                    var cmdIterWatch = System.Diagnostics.Stopwatch.StartNew();
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

                    // ===========================================================
                    // 區塊職責：建立本筆 cmd 的 context，並把 id **顯式塞進 args** 讓 handler 拿得到
                    // 物理意義：併行下不能靠「全域清空再寫入」——那是串行才成立的假設。
                    //          context 以 cmd id 為鍵；handler 由自己手上的 args 索引回來。
                    // 數值影響：args 多一個 `_cmd_id` 欄（底線前綴＝框架欄，與 `_caller_env_marker` 同族）；
                    //          舊 handler 不讀它也不受影響。⚠ in-process 呼叫別的 Cmd 時，
                    //          呼叫端要把這個欄位帶進子 args，否則子流程的回報無處可去（會出聲）。
                    // ===========================================================
                    if (c.Args == null) c.Args = new Dictionary<string, string>();
                    UCL_AgentCmdContexts.Create(c.Id, norm, callerEnvMarker);
                    c.Args[UCL_AgentCmdContexts.ARG_CMD_ID] = c.Id;
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
                    // ===========================================================
                    // 區塊職責：起量具（TASK-0161）—— 這支 cmd 花了多久，落一行可排序的讀數
                    // 物理意義：**必須夾在 handler 的兩側，而不是整批的兩側。** 整批只能回答
                    //          「這一輪很久」，而一輪可能有 N 筆；要排序 48 支 handler 就得 per-cmd。
                    //          ⚠ 起點刻意放在 ArgsSpec 檢查**之前**：被擋下也是一段耗時，
                    //          而「擋下時零讀數」會讓那條路徑在統計上不存在。
                    // 數值影響：一個小物件 ＋ 一筆進 ring；未超門檻不落檔（見 UCL_AgentCmdSlowLog）。
                    //          量具自己壞掉回 null，End() 收得住 ⇒ 不影響本次 cmd 的成敗。
                    // ===========================================================
                    UCL_AgentCmdProbe cmdProbe = UCL_AgentCmdSlowLog.Begin(c.Id, c.Type, norm, c.Args);
                    // ⏱ 批次前奏兩格掛在本圈上。⚠ 定語：**這是整批的前奏，不是這一支的** ——
                    //   一批多筆時每一支都會掛到同一組數字。欄名保持 batch_ 前綴讓讀的人分得出來。
                    UCL_AgentCmdSlowLog.MarkPhase(cmdProbe, "batch_queue_load", queueLoadMs);
                    UCL_AgentCmdSlowLog.MarkPhase(cmdProbe, "batch_module_wait", moduleWaitMs);
                    // 本圈到 handler 起跑前的耗時（reset + queue Save + ArgsSpec 之前那幾格）
                    double preHandlerMs = cmdIterWatch.Elapsed.TotalMilliseconds;
                    UCL_AgentCmdSlowLog.MarkPhase(cmdProbe, "pre_handler", preHandlerMs);
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
                        // ===========================================================
                        // 區塊職責：handler 結束後把**自己**拉回主執行緒（TASK-0162 的前置）
                        // 物理意義：handler 現在可以合法地把自己移到 thread pool（走
                        //          UCL_AgentCmdOffload.EnterBackground）。它這麼做之後，
                        //          `await handlerTask` 的續接就落在背景緒上 —— 而本迴圈接下來會碰
                        //          `EditorApplication.isPlayingOrWillChangePlaymode`（catch 裡，主緒 only）。
                        //          ⇒ 由 Runner 統一拉回來，**offload 的 handler 就不必各自記得回家**。
                        // 數值影響：已經在主緒時 SwitchToMainThread 幾乎是 no-op（UniTask 直接同步返回）；
                        //          在背景緒時多等一拍 Editor update。⛔ 不可以省 —— 省掉之後
                        //          失效樣子是「偶爾在 catch 裡丟一個跟本次錯誤無關的 Unity 例外」，
                        //          而那會把真正的 handler 錯誤蓋掉（本檔已有 UniTask token 蓋錯誤的血證）。
                        // ===========================================================
                        await UniTask.SwitchToMainThread();
                        double afterHandlerMs = cmdIterWatch.Elapsed.TotalMilliseconds;
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

                        // ⏱ 成功路徑的收尾格（WriteCmdResult ＋ queue 欄位更新）
                        UCL_AgentCmdSlowLog.MarkPhase(cmdProbe, "post_handler_success",
                            cmdIterWatch.Elapsed.TotalMilliseconds - afterHandlerMs);
                        // OneShot 成功 → 直接從 queue 中移除（任務已完成）
                        if (c.Mode == UCL_AgentCommandMode.OneShot)
                        {
                            commands.RemoveAt(i);
                            removed++;
                        }
                    }
                    catch (Exception e)
                    {
                        // ⚠ 先回主緒再讀 Editor 狀態 —— offload 過的 handler 拋錯時，這個 catch
                        //   本來就跑在背景緒上，而下一行的 `EditorApplication.*` 是主緒 only。
                        //   （C# 允許在 catch 裡 await；這行在已經是主緒時等同 no-op。）
                        await UniTask.SwitchToMainThread();
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
                            // 區塊職責：多寫一份**可補跑**的結構化紀錄（Tim 2026-08-21 派單）
                            // 物理意義：上面兩份都不足以重跑 —— result 檔沒有 Args 且 3 天後被 Purge，
                            //          error 報告有 Args 但是給人讀的 markdown。補跑需要 Type+Mode+Args，
                            //          所以在失敗當下就把它結構化落檔（見 UCL_AgentCommandFailedStore）。
                            // 數值影響：_cmd_failed/<id>.json；**不自動重試**（重跑會重放副作用：
                            //          酒館公告重發、轉帳重轉），補跑一律由人在 UCL_AgentCommandsPage 按下去。
                            UCL_AgentCommandFailedStore.Record(c, e.Message,
                                string.IsNullOrEmpty(agentId) ? UCL_AgentCommandQueue.AnonymousQueueId : agentId);
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
                        // 區塊職責：**沒有產出 last_op 的那幾支，也要留下一行讀數**（TASK-0116 第二半）。
                        // 🩸 血證（@summit 2026-09-06 量到）：`letters/summit/cmd/autocommit_last_op.md` 讀回來是
                        //   @basecamp 09-03 的繪圖券報告，**而那個檔的 mtime 就是 09-03** ——
                        //   汙染只解釋了「內容為什麼是別人的」，沒解釋「它為什麼還在那裡」：
                        //   `AutoCommit op=scan` 根本不寫 last_op ⇒ **後續沒有任何一次會覆蓋它**。
                        // ⇒ 「陳舊」與「剛產生」在檔案上同形，而陳舊那半**不需要任何併發**就會發生。
                        // 數值影響：本次沒鏡寫過 last_op 時，就地覆寫成一份**明說「這一支不寫」**的 stub。
                        //   ⚠ 不刪檔：刪掉之後「這支不寫」與「這個 persona 沒跑過」又會同形。
                        //   ⚠ 只在拿得到 lane（AgentId）時做；非 queue 路徑不動，行為與舊版全等。
                        double beforeStubMs = cmdIterWatch.Elapsed.TotalMilliseconds;
                        try { WriteLastOpStubIfAbsent(c); } catch (System.Exception e)
                        { Debug.LogWarning($"[UCL_AgentCmd] last_op stub 寫入失敗（不影響本次結果）：{e.Message}"); }
                        // per-cmd context 退場 —— **必須在 WriteCmdResult 之後**（result 檔要讀它的 outputs/values）。
                        // ⚠ 這裡若提早釋放，症狀是 result 檔的 outputs 欄空掉，而 cmd 本身 Success ——
                        //   又是一個「成功了但東西不見」的無聲失敗。
                        UCL_AgentCmdSlowLog.MarkPhase(cmdProbe, "last_op_stub",
                            cmdIterWatch.Elapsed.TotalMilliseconds - beforeStubMs);
                        UCL_AgentCmdContexts.Release(c.Id);
                        // 區塊職責：收量具（TASK-0161）—— 成功、失敗、PlayMode 中斷三條路都要收
                        // 物理意義：放在 finally 的**最後一行**，讓 last_op stub 與 context Release 也算進
                        //          runner_ms；放 finally（而不是成功那一支）是因為**失敗的那幾支往往才是慢的**
                        //          （逾時、卡在同步 IO），只量成功的會系統性地漏掉最貴的樣本。
                        // 數值影響：verdict 取自 c.LastRunResult（PlayMode 中斷那格是 null ⇒ 記為未成功，
                        //          error 欄會寫著中斷標記，讀的人分得出來）。
                        try { UCL_AgentCmdSlowLog.End(cmdProbe, c.LastRunResult == "Success", c.LastRunError, cmdIterWatch.Elapsed.TotalMilliseconds); }
                        catch { /* 量具不影響 cmd 本業 */ }
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
                // client：這一筆是**哪個 client 送進來的**（`run_cmd.py` / `senate-cli` / …）。
                // 🩸 2026-08-31 缺這一格的代價：早安改走 Senate CLI 之後，「某人今天走了新入口」
                //   這件事**系統本身答不出來** —— 判定檔只有 id/type/mode/result/finished_at，
                //   而 `_caller_env_marker` 分得出環境（claude-code / codex）卻分不出 client
                //   （兩個 client 在 Claude Code 底下都回 claude-code）。只能去問本人。
                // ⚠ 缺席時寫 "unstated" 而不留空：**「送它的人沒說」與「這一欄還沒接上」不可同形** ——
                //   舊 client 不寫這個 arg，而空字串會讓兩者長得一樣。
                string aClient = "";
                if (c.Args != null && c.Args.TryGetValue("_caller_client", out var aCallerClient)
                    && !string.IsNullOrEmpty(aCallerClient)) aClient = aCallerClient;
                jd["client"] = new UCL.Core.JsonLib.JsonData(aClient.Length > 0 ? aClient : "unstated");
                // outputs：handler 經 ReportOutputFile 回報的產出檔（回傳檔 / payload）——
                // caller 端（run_cmd.py）隨 verdict 一起印，agent 不用再靠 skill 文字背路徑
                // ⚠ context 由 cmd id 取回（不是全域清單）—— 本函式必須在 finally 的 Release 之前被呼叫，
                //   否則 outputs 會空掉而 cmd 仍 Success（「成功了但東西不見」的無聲失敗）。
                var aCtx = UCL_AgentCmdContexts.Get(c.Id);
                var aCtxOutputs = aCtx?.SnapshotOutputs();
                if (aCtxOutputs != null && aCtxOutputs.Count > 0)
                {
                    var aOutputs = new UCL.Core.JsonLib.JsonData();
                    foreach (var aOut in aCtxOutputs) aOutputs.Add(aOut);
                    jd["outputs"] = aOutputs;
                }
                // values：handler 經 ReportOutputValue 回報的純量結果（post_seq 等）。
                // 陣列而非物件 —— 同一 key 可能出現多次（單一 cmd 內 Op_Post 跑兩次），
                // 用物件會後面蓋前面，而被蓋掉的那筆長得完全正常。
                var aCtxValues = aCtx?.SnapshotValues();
                if (aCtxValues != null && aCtxValues.Count > 0)
                {
                    var aValues = new UCL.Core.JsonLib.JsonData();
                    foreach (var aKv in aCtxValues)
                    {
                        var aOne = new UCL.Core.JsonLib.JsonData();
                        aOne["key"] = new UCL.Core.JsonLib.JsonData(aKv.Key);
                        aOne["value"] = new UCL.Core.JsonLib.JsonData(aKv.Value);
                        aValues.Add(aOne);
                    }
                    jd["values"] = aValues;
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
