
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Queue persistence — read/write AgentCommands/queue.json at repository root.
// 與 RCG 版相容（相同檔案路徑 + 相同 JSON 格式），可平行運作。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// queue.json 的讀寫管理。
    /// 路徑：&lt;repoRoot&gt;/AgentCommands/queues/&lt;persona&gt;/queue[-&lt;lane&gt;].json
    /// repoRoot 由 <see cref="UCL_RepoPath.RepoRoot"/> 解析（git-walk，與 Python run_cmd.py 對齊）。
    /// ⚠ 路徑樣板**兩邊各有一份**（本檔與 run_cmd.py），改動必須同時進行 ——
    ///   任一邊落後，trigger 就寫在對方沒在看的地方，而那種斷線是**靜默**的
    ///   （cmd 永遠 pending 到 timeout，沒有任何錯誤訊息指向真因）。
    /// </summary>
    public static class UCL_AgentCommandQueue
    {
        public const string QueueDirRelative = "AgentCommands";
        public const string QueueFileName = "queue.json";
        // 區塊職責：multi-queue 子資料夾名稱 (agent-command-pipeline-parallelize T02)
        // 物理意義：**persona 資料夾制**（Tim 2026-08-01 拍板，取代原本的平鋪檔名制）——
        //          <AgentCommandsDir>/queues/<persona>/queue.json
        //          <AgentCommandsDir>/queues/<persona>/queue-<lane>.json
        //          舊制是 queues/queue-<persona>.json 與 queues/queue-<persona>-<lane>.json 平鋪，
        //          「這筆是誰派的」得從檔名字串反推，而 queue-ame-design 無法判定
        //          「-design 是用途還是名字的一部分」。改成資料夾之後身分與通道
        //          **在檔案系統層就分開了**，不必解析任何字串。
        // 數值影響：切換式改版，**無相容層**（切換時 36 個舊 queue 全為空、0 筆在途 cmd，
        //          點清後直接刪除；沒有需要搬運的狀態，因此不寫遷移碼也不雙讀）。
        //          最外層共用 <AgentCommandsDir>/queue.json 一併廢除。
        public const string QueuesSubdir = "queues";

        // 區塊職責：未宣告身分者的落點（Tim 2026-08-01）。
        // 物理意義：不帶身分的派遣不再落「最外層共用 queue.json」這個特例，而是落一個
        //          **名字就說明狀態**的資料夾。好處是掃描規則變成一條沒有例外的
        //          「資料夾名 = 身分」，而且 queues/anonymous/ 的流量自己就是
        //          「還有多少未署名派遣」的儀表 —— 不需要有人記得去統計。
        // ⚠ 這是**保留字，不是 persona**：身分解析讀到它必須回「本層沒有答案」，
        //   不可回字串 "anonymous"。否則它會流進記帳層，而解析端的命名慣例
        //   fallback（{canonical}-da-xiaojie）會為一個不存在的人隱含開帳戶。
        public const string AnonymousQueueId = "anonymous";

        // 區塊職責：系統自動產生的派遣落點（Tim 2026-08-18）。
        // 物理意義：commit 領薪公告、daemon 之類**不是人派的**指令，過去跟「忘了帶 --persona」
        //          的人擠同一個 anonymous 資料夾 ⇒ anonymous 的流量同時混了兩種東西：
        //          「系統本來就該匿名」與「有人漏帶旗標」。混在一起，那個資料夾就不再是儀表 ——
        //          數字降不下來，而且看不出哪些是該修的。
        //          分出 system/ 之後，**anonymous 剩下的每一筆都是待修的漏帶**。
        // ⚠ 同樣是**保留字不是 persona**：身分解析讀到它一律回「本層沒有答案」
        //   （理由同 AnonymousQueueId —— 否則會為不存在的人隱含開帳戶）。
        //   系統訊息的真實身分仍走 `--arg persona=<P>`（那是「這筆代表誰」，跟走哪條 lane 無關）。
        public const string SystemQueueId = "system";

        // 區塊職責：queueId 的形狀 —— "<persona>" 或 "<persona>/<lane>"。
        // 物理意義：呼叫端（Watcher / Page / Runner）仍然只傳**一個不透明字串**，簽名不變；
        //          但這個字串現在是**路徑形狀**而不是要猜的名字 —— '/' 是呼叫端自己組出來的
        //          結構分隔符，不是我們從 "ame-design" 這種字串裡猜出來的邊界。
        public const char LaneSeparator = '/';

        /// <summary>把 queueId 拆成 (資料夾, lane)。lane 可為 null。空值 → anonymous。</summary>
        /// <remarks>
        /// 只切**第一個** '/'：persona 名不含 '/'，其餘一律歸 lane。
        /// 含 ".." 或反斜線的段落視為不合法 → 落 anonymous 並警告（這些值來自 CLI，
        /// 不做防護的話是一條寫出 queues/ 之外的路徑穿越）。
        /// </remarks>
        public static void SplitQueueId(string queueId, out string folder, out string lane)
        {
            folder = AnonymousQueueId;
            lane = null;
            if (string.IsNullOrEmpty(queueId)) return;
            string id = queueId.Replace('\\', LaneSeparator).Trim();
            int i = id.IndexOf(LaneSeparator);
            string f = i < 0 ? id : id.Substring(0, i);
            string l = i < 0 ? null : id.Substring(i + 1).Replace(LaneSeparator.ToString(), "-");
            if (!IsSafeSegment(f) || (l != null && !IsSafeSegment(l)))
            {
                Debug.LogWarning($"[UCL_AgentCommandQueue] 不合法的 queueId '{queueId}' → 落 {AnonymousQueueId}。");
                return;
            }
            folder = f;
            lane = string.IsNullOrEmpty(l) ? null : l;
        }

        static bool IsSafeSegment(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "." || s == "..") return false;
            if (s.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            return s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        // 區塊職責：lock-file 機制使用的兩個 trigger 檔名
        // 物理意義：
        //   TriggerFileName        — Python / 外部寫入後留下的「請執行」訊號
        //   RunningTriggerFileName — Watcher 偵測到 Trigger 後 File.Move 為此名，代表「Editor 已接手」
        // 數值影響：兩檔皆為跨 process 同步用的 marker；Runner finally 結束時刪除 .running 檔，外部可監聽該事件作為「執行完成」訊號。
        public const string TriggerFileName = "pending.trigger";
        public const string RunningTriggerFileName = "pending.trigger.running";

        /// <summary>
        /// 取得 queue 檔絕對路徑。
        /// agentId=null → <c>AgentCommands/queues/anonymous/queue.json</c>。
        /// "&lt;persona&gt;" → <c>queues/&lt;persona&gt;/queue.json</c>；
        /// "&lt;persona&gt;/&lt;lane&gt;" → <c>queues/&lt;persona&gt;/queue-&lt;lane&gt;.json</c>。
        /// </summary>
        public static string GetQueuePath(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out string lane);
            string file = string.IsNullOrEmpty(lane) ? QueueFileName : $"queue-{lane}.json";
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder, file);
        }

        /// <summary>取得該 queue 所屬的 persona 資料夾絕對路徑（queues/&lt;persona&gt;/）。</summary>
        public static string GetQueueDir(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out _);
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder);
        }

        /// <summary>取得 pending trigger 路徑：queues/&lt;persona&gt;/pending[-&lt;lane&gt;].trigger。</summary>
        public static string GetTriggerPath(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out string lane);
            string file = string.IsNullOrEmpty(lane) ? TriggerFileName : $"pending-{lane}.trigger";
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder, file);
        }

        /// <summary>取得 running trigger 路徑。對應 GetTriggerPath()。</summary>
        public static string GetRunningTriggerPath(string agentId = null)
        {
            return GetTriggerPath(agentId) + ".running";
        }

        /// <summary>確保該 queue 的 persona 資料夾存在。</summary>
        public static void EnsureDir(string agentId = null)
        {
            string dir = GetQueueDir(agentId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 列舉現存 queue 的 id 清單（掃 queues/&lt;persona&gt;/queue*.json）。Watcher 用。
        /// 回傳形狀："&lt;persona&gt;"（本命 queue）或 "&lt;persona&gt;/&lt;lane&gt;"（子通道）。
        /// </summary>
        public static System.Collections.Generic.List<string> ListAgentIds()
        {
            var list = new System.Collections.Generic.List<string>();
            string queuesDir = Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir);
            if (!Directory.Exists(queuesDir)) return list;
            foreach (var dir in Directory.GetDirectories(queuesDir))
            {
                string persona = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(persona)) continue;
                foreach (var f in Directory.GetFiles(dir, "queue*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name == "queue") list.Add(persona);                       // 本命
                    else if (name.StartsWith("queue-"))                           // 子通道
                        list.Add(persona + LaneSeparator + name.Substring("queue-".Length));
                }
            }
            return list;
        }

        /// <summary>
        /// 列出**有 pending trigger、卻沒有對應 queue 檔**的 lane（TASK-0292）。
        /// 回傳 (queueId, triggerPath)；queueId 形狀同 <see cref="ListAgentIds"/>。
        /// </summary>
        /// <remarks>
        /// 🩸 <see cref="ListAgentIds"/> 以 queue 檔列舉 lane ⇒ queue 檔不在的 lane **不在候選集合裡**，
        /// Watcher 永遠不會去看它的 trigger —— trigger 原樣躺著、送件端只拿到 timeout、Editor 一個字都不說
        /// （2026-09-23 kotoko 於 TASK-0286 QA 活體量到：10 秒＋15 秒無人來收）。
        /// 本函式只負責**看見**，⛔ 不替它補 queue 檔：猜它原本裝著什麼比不修更危險（沿用 TASK-0264 立場）。
        /// 只看 pending；`.running` 不列 —— 它只會出現在已被列舉、派過工的 lane 上。
        /// </remarks>
        public static System.Collections.Generic.List<(string QueueId, string TriggerPath)> ListOrphanTriggers()
        {
            var list = new System.Collections.Generic.List<(string, string)>();
            string queuesDir = Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir);
            if (!Directory.Exists(queuesDir)) return list;
            foreach (var dir in Directory.GetDirectories(queuesDir))
            {
                string persona = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(persona)) continue;
                foreach (var f in Directory.GetFiles(dir, "pending*.trigger"))
                {
                    string name = Path.GetFileName(f);
                    // GetFiles 的萬用字元在 Windows 上會連帶命中 8.3 短檔名 ⇒ 字尾再判一次，擋掉 .running
                    if (!name.EndsWith(".trigger", StringComparison.Ordinal)) continue;
                    string queueId;
                    if (name == TriggerFileName) queueId = persona;                                  // 本命
                    else if (name.StartsWith("pending-", StringComparison.Ordinal))                 // 子通道
                    {
                        string lane = name.Substring("pending-".Length, name.Length - "pending-".Length - ".trigger".Length);
                        // 不合法的 lane 名丟給 GetQueuePath 會落 anonymous 並每拍印一次 warning ⇒ 這裡先擋
                        if (!IsSafeSegment(lane)) continue;
                        queueId = persona + LaneSeparator + lane;
                    }
                    else continue;
                    if (!File.Exists(GetQueuePath(queueId))) list.Add((queueId, f));
                }
            }
            return list;
        }

        /// <summary>
        /// 從 queueId 取得宣告的 persona —— 身分解析階梯 tier 2「queue 反推」。
        /// 查不到 / 匿名一律回 null。
        /// </summary>
        /// <remarks>
        /// ⚠ 回 null 的語意是「**本層沒有答案**」，不是「查無此人」——
        /// 呼叫端不可把它當否定證據，該往解析階梯的下一層走。
        /// anonymous / system 回 null 是刻意的：它們是狀態不是人
        /// （見 AnonymousQueueId / SystemQueueId 註解）。
        /// </remarks>
        public static string GetDeclaredPersona(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            SplitQueueId(agentId, out string folder, out _);
            return IsReservedQueueId(folder) ? null : folder;
        }

        // 區塊職責：判斷一個 queue 資料夾名是不是保留字（狀態，不是人）。
        // 物理意義：保留字有兩個且會再長 —— 逐處寫 `== AnonymousQueueId` 的話，
        //          新增第二個保留字就得去找出所有比對點，而**漏掉的那一處不會報錯**：
        //          它會把 "system" 當成 persona 回出去，下游解析端的
        //          fallback 命名就替一個不存在的人開了帳戶。收成一處＝只有一種讀法。
        // 數值影響：純比對，無 IO。
        public static bool IsReservedQueueId(string folder)
            => folder == AnonymousQueueId || folder == SystemQueueId;

        /// <summary>
        /// 一次 queue 讀取的**結局**。⛔ 三態，不得壓成「有沒有內容」。
        /// <para>🩸 TASK-0264：舊版把 <see cref="QueueReadState.Unreadable"/> 與「檔裡沒東西」
        /// 都回成空 queue ⇒ Runner 對一顆**裝著三筆指令的截斷檔**印出「queue is empty」
        /// （2026-09-21 實測：probe0264，75 bytes 的半截 JSON）。
        /// ⚠ Console 那側其實有紅字，而**回傳值裡沒有** ⇒ 每個程式化消費端都只看得到「空」。</para>
        /// </summary>
        public enum QueueReadState
        {
            /// <summary>檔案不存在 —— 那是合法的「還沒有人送過東西」。</summary>
            Missing = 0,

            /// <summary>讀到了，內容有效（可能是 0 筆，而那是真的 0 筆）。</summary>
            Ok = 1,

            /// <summary>
            /// **讀不到**：檔在、而解析炸了（截斷／壞碼／寫到一半）。
            /// <para>⛔ 呼叫端**不得**把它當成空 queue 然後寫回去 —— 那會把別人的整條 queue 洗掉，
            /// 而「我剛加的那筆在不在」這種回讀驗證**照樣會通過**。</para>
            /// </summary>
            Unreadable = 2,

            /// <summary>
            /// **這一瞬間開不了**：檔在、而 OS 拒絕開檔（換檔正在飛／另一個 process 握著）。重試用完仍是這樣。
            /// <para>🩸 TASK-0264 QA（kotoko 2026-09-22）：第一版把它併進 <see cref="Unreadable"/> ——
            /// 於是一個**毫秒級的爭用**被分類成「檔案壞了」，而 <c>Unreadable</c> 是破壞力最強的那一態
            /// （Runner 整批不執行／收尾放棄寫回 ⇒ 跑完的 OneShot 留在 queue 裡，而 Runner 那圈
            /// 沒有 <c>LastRunResult == "Success"</c> 守衛 ⇒ **下一輪照跑一次**，而那條路上有動錢的 handler）。</para>
            /// <para>⇒ 分開的理由是**下一步不同**：<c>Unreadable</c> ＝去看那顆檔；<c>Busy</c> ＝等一下再來。
            /// ⛔ 而它一樣**不准被當成空 queue 寫回去** —— 寫回的判準是
            /// <c>== Ok</c>，⛔ 不是「不是 Unreadable」（見 <see cref="SaveMerged"/>）。</para>
            /// </summary>
            Busy = 3,
        }

        /// <summary>讀取 queue.json — 不存在或解析失敗時回傳空 queue。</summary>
        /// <remarks>⚠ 這個多載**分不出**「沒有檔」與「讀不到」。
        /// 會回頭寫入的呼叫端請改用 <see cref="Load(string, out QueueReadState)"/>（TASK-0264）。</remarks>
        public static UCL_AgentCommandQueueData Load(string agentId = null)
            => Load(agentId, out _);

        /// <summary>讀取 queue.json，並回報**這一次讀取的結局**（四態）。</summary>
        /// <remarks>
        /// 🔴 **開檔失敗與解析失敗是兩件事**（TASK-0264 QA，kotoko 2026-09-22）：
        /// <c>SwapInto</c> 換檔的那一瞬間，不拿鎖的讀取端**開檔會被拒**
        /// （她的對照組：reader 改成開檔而不是 stat ⇒ Replace 那列 323630/368997 次被拒，
        /// 錯誤碼 <c>SHARING_VIOLATION</c>；⚠ 那是零延遲狂開檔的 harness，**不是線上發生率**）。
        /// <para>⇒ 這裡給讀取端**對等於 <see cref="SwapInto"/> 的重試**：
        /// 寫入端有五次而讀取端一次都沒有，那個不對稱就是把瞬時爭用推進 <c>Unreadable</c> 的原因。
        /// 重試用完仍開不了 ⇒ 回 <see cref="QueueReadState.Busy"/>，⛔ 不回 <c>Unreadable</c>
        /// （前者叫人等一下，後者叫人去修檔 —— 派錯人的代價是整批不執行）。</para>
        /// <para>⚠ 重試跑在**呼叫端的執行緒**上（Editor 主緒也會走這裡），最壞 2+4+6+8 ＝ 20ms／次。
        /// ⛔ 而它換來的是「不把爭用誤判成壞檔」—— 誤判那一邊的代價是重跑一筆動錢的指令。</para>
        /// </remarks>
        public static UCL_AgentCommandQueueData Load(string agentId, out QueueReadState oState)
        {
            string path = GetQueuePath(agentId);

            // 🩸 TASK-0286（@kotoko 開單，而這扇門是 TASK-0264 的修法自己留下的鄰居）：
            //   這裡原本是 `if (!File.Exists(path)) ⇒ Missing ＋ 空 queue`，**在重試迴圈之前**。
            //   ⛔ `File.Exists` 對**任何**失敗都回 false ⇒「檔案真的不存在」與「這一瞬間查不了」
            //   在它的回傳值上**同形**，而 `Missing` 正是下游被教導成「收尾本來就該有一顆」的那個態。
            //   ⇒ 換檔飛過去的那一瞬間，讀取端拿到 Missing ＋ **空 queue**，
            //     而 0264 花三輪建起來的四態**從第一行就被繞過去了**。
            //
            // 📊 活體（Unity 行程內，2026-09-23）：外部以 `File.Replace` 持續換檔時，
            //   Editor 進程內 `File.Exists` 取樣 25 次 ⇒ **False 3 次**，而那個檔全程都在。
            //
            // ⇒ 修法＝**用開檔的結果分類，⛔ 不用 stat**（判準是 @kotoko 在 0264 ⊕ 給的：
            //   `stat` 與「開檔」是兩個不同的 syscall，而只有後者分得出這兩種狀態）。
            //   ⚠ catch 的**順序**是這個修法的本體：`FileNotFoundException` 與
            //   `DirectoryNotFoundException` **繼承自 `IOException`** ⇒ 不先接它們的話，
            //   「真的不存在」會被吃進重試、最後報成 Busy —— 而那會叫呼叫端去等一個不會回來的東西。
            const int aMaxAttempt = 5;
            Exception aLastOpenError = null;
            for (int aAttempt = 1; aAttempt <= aMaxAttempt; ++aAttempt)
            {
                string aJson;
                try
                {
                    aJson = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // ⛔ 開檔這一層的失敗**不落 Unreadable** —— 它多半是換檔在飛，毫秒內自己過去。
                    //
                    // 🩸 **`FileNotFoundException` 也走這裡，而那是量出來的，⛔ 不是設計上的寬容**：
                    //   本單的第一版修法寫「`FileNotFoundException` ⇒ 立刻回 `Missing`，不重試」，
                    //   理由是「重試不會讓一個不存在的檔長出來」。⚠ 那句話是對的，**而前提是錯的**：
                    //   活體（Unity 行程內，換檔 107 次/秒，18 次取樣）⇒ 成功 10／`IOException` **5**／
                    //   **`FileNotFoundException` 3** —— **換檔飛過去的那一瞬間也會丟它**。
                    //   ⇒ 那一版等於把本單要治的病**換一個入口再做一次**：爭用被判成 Missing ＋ 空 queue。
                    //   📌 我為了省 20ms，讓一個爭用長得像「不存在」。
                    //
                    // ⇒ 所以兩者**都重試**；分類留到重試用完之後，看**最後那個例外的型別**（見迴圈外）。
                    //   ⚠ 判準的依據是**時間尺度**不是型別：爭用窗口是毫秒級（@kotoko 量到 ~1.4ms），
                    //   而 2+4+6+8 ＝ 20ms 跨得過去；一個真的不存在的檔，重試 5 次仍然不存在。
                    aLastOpenError = e;
                    if (aAttempt < aMaxAttempt) System.Threading.Thread.Sleep(2 * aAttempt);
                    continue;
                }

                try
                {
                    // Unity JsonUtility 不支援 Dictionary，因此採手寫 JSON parse（極簡）
                    var aData = ParseJson(aJson);
                    oState = QueueReadState.Ok;
                    return aData;
                }
                catch (Exception e)
                {
                    // 這一層是**內容**的問題（截斷／壞碼）⇒ 重試不會變好，直接落 Unreadable。
                    Debug.LogError($"[UCL_AgentCommandQueue] Failed to parse queue: {e}");
                    oState = QueueReadState.Unreadable;
                    return new UCL_AgentCommandQueueData();
                }
            }

            // 重試用完 ⇒ **在這裡分類**，判準是最後那個例外的型別。
            // 🩸 TASK-0286：這裡原本**再問一次** `File.Exists` —— 同一族的第二個實例，
            //   而它是 TASK-0264 的修法自己寫的。爭用時它一樣回 false ⇒ **爭用被判成 Missing**。
            // ⇒ 換成例外型別：`File.Exists` 對**任何**失敗都回 false（兩種狀態同形），
            //   而開檔的例外**分得出來** —— 那是 @kotoko 在 TASK-0264 ⊕ 給的判準：
            //   `stat` 與「開檔」是兩個不同的 syscall，而只有後者回答得了「為什麼拿不到」。
            // ⚠ 而**重試用完仍然是 FileNotFound** 才算不存在：毫秒級的爭用跨不過 20ms 的重試，
            //   ⛔ 單次的 FileNotFound 不算（活體：換檔中 18 次取樣就丟了 3 次）。
            if (aLastOpenError is FileNotFoundException || aLastOpenError is DirectoryNotFoundException)
            {
                oState = QueueReadState.Missing;
                return new UCL_AgentCommandQueueData();
            }
            Debug.LogError($"[UCL_AgentCommandQueue] queue 開不了（重試 {aMaxAttempt} 次）：{path}"
                           + " ⇒ 這是**爭用**不是壞檔，呼叫端請稍後再來，⛔ 不要拿空的寫回去。"
                           + $" 最後一個錯誤：{aLastOpenError}");
            oState = QueueReadState.Busy;
            return new UCL_AgentCommandQueueData();
        }

        /// <summary>
        /// 取這顆 queue 的**跨 process 互斥鎖**（與 TASK-0263 同一支 <c>SCP_FileLock</c>）。
        /// <para>⚠ 「讀 → 改 → 寫回」要**整段**包在裡面；只鎖寫的那一下等於沒鎖。</para>
        /// </summary>
        /// <para>🩸 TASK-0264 QA（kotoko 2026-09-23）：<c>Acquire</c> 等不到鎖時丟的是一顆
        /// **裸 <see cref="IOException"/>**，而本檔唯一的呼叫點（<see cref="SaveMerged"/>）
        /// 跑在 Runner 的外層 <c>try</c> 裡 —— 而那一圈**只有 finally、沒有 catch**
        /// ⇒ 它會炸穿整批、<c>finally</c> 清掉 trigger，而那幾筆指令**原樣留在 queue 裡**
        /// —— 沒有任何東西會再來收。⚠ 而它是**本修法自己新引入的**：
        /// <c>188c3fc3^</c> 那版 grep <c>LockQueue</c> 零命中。</para>
        /// <para>⇒ 這裡把它換成一個**認得出來的型別**，讓呼叫端分得出
        /// 「等不到鎖」與其餘 IO 失敗 —— ⛔ 不靠比對例外訊息字串（那把尺會在
        /// 訊息被改寫的那天**安靜失效**）。</para>
        public static IDisposable LockQueue(string agentId = null)
        {
            EnsureDir(agentId);
            string aPath = GetQueuePath(agentId);
            try
            {
                return SCP.Core.Io.SCP_FileLock.Acquire(aPath);
            }
            catch (IOException e)
            {
                // ⛔ 不吐、不降級（「拿不到鎖還是寫下去」正是 TASK-0264 要根治的病）——
                //    只換一個呼叫端認得出來的型別，成因整顆掛在 inner 上。
                throw new QueueLockTimeoutException(aPath, e);
            }
        }

        /// <summary>
        /// 等不到 <c>queue.json</c> 的互斥鎖（<see cref="LockQueue"/> 逾時，預設 20s）。
        /// <para>⚠ 它的語意是「**有人長期握著那顆檔**」，⛔ 不是「檔壞了」——
        /// 兩者的處置**相反**：前者查是誰握著，後者修那顆檔。</para>
        /// </summary>
        public sealed class QueueLockTimeoutException : IOException
        {
            /// <summary>等不到鎖的那顆 queue 檔。</summary>
            public string QueuePath { get; private set; }

            public QueueLockTimeoutException(string iQueuePath, Exception iInner)
                : base("等不到 queue 的互斥鎖：" + iQueuePath
                       + "　⇒ 有人長期握著它（成因見 inner）。"
                       + "⛔ 本呼叫**沒有**寫入任何東西。", iInner)
            { QueuePath = iQueuePath; }
        }

        /// <summary>寫入 queue.json（會覆寫整個檔案）。</summary>
        /// <remarks>
        /// ⚠ 落盤是 **temp → 換檔**（原子替換），⛔ 不是就地 <c>WriteAllText</c>。
        /// <para>🩸 TASK-0264：就地覆寫在中途斷電／行程被砍時會留下**半截 JSON**，
        /// 而那顆半截檔會被 <see cref="Load(string)"/> 讀成「空 queue」
        /// ⇒ 下一個「讀 → 加一筆 → 寫回」的呼叫端就把整條 queue 洗掉，
        /// 而它的回讀驗證（只問「我這筆在不在」）**照樣通過**。</para>
        /// <para>🩸 TASK-0264 QA（kotoko 2026-09-21）：換檔的第一版寫成 <c>Delete</c> 然後 <c>Move</c>，
        /// 而那兩步之間 <c>queue.json</c> **不存在** —— <see cref="Load(string)"/> 不拿鎖，
        /// 檔不存在時回 <c>Missing</c> ＋空 queue ⇒ **那是本修法自己新引入的窗口，
        /// 而它的失效樣子正是本單在治的那一族**（沒拿鎖的讀取端拿到「空」，讀起來像「沒有待辦」）。
        /// ⇒ 目標存在時走 <c>File.Replace</c>（就地原子替換，Unix 端即 rename），
        /// 中途沒有任何一瞬間檔案是不存在的。</para>
        /// <para>⚠ 射程：只有「目標不存在」那一格走 <c>Move</c>（那是建檔，本來就沒有舊值可讀）。
        /// 🩸 **這一段原本寫著「<c>Exists</c> 與 <c>Replace</c> 之間被人刪掉 ⇒ 大聲失敗」—— 那句是舊的**
        /// （kotoko 2026-09-22 QA 逐格量過）：<see cref="SwapInto"/> 加上重試之後，那兩個方向都**自癒** ——
        /// <c>Exists=true</c> 後被刪 ⇒ <c>Replace</c> 炸 ⇒ 重試時走 <c>Move</c>；
        /// <c>Exists=false</c> 後被建 ⇒ <c>Move</c> 炸 ⇒ 重試時走 <c>Replace</c>。
        /// ⇒ 大聲失敗只發生在**重試用完**（回 <c>false</c>），⛔ 仍然不退回 Delete-then-Move。</para>
        /// </remarks>
        /// <returns><c>true</c> ＝ 換檔真的成立。
        /// <para>🩸 TASK-0264 QA（kotoko 2026-09-22）：本方法原本是 <c>void</c> ＋ 一個吞例外的 catch，
        /// 而 <see cref="SaveMerged"/> 宣稱自己的 <c>true</c> ＝「真的寫回去了」——
        /// <c>SwapInto</c> 重試用完往上丟的那顆例外**只飛一層就死在這裡**，那個 bool 照樣是 true。
        /// ⇒ **一個宣稱了自己沒有的性質的 API**：今天沒有行為損害（兩個呼叫端都不看回傳值），
        /// 而下一個讀它的人會拿那個宣稱當前提。⛔ 錯的不是 catch，是「void 的失敗**沒有出口**」。</para></returns>
        public static bool Save(UCL_AgentCommandQueueData data, string agentId = null)
        {
            EnsureDir(agentId);
            string path = GetQueuePath(agentId);
            try
            {
                string json = SerializeJson(data);
                string tmp = path + ".tmp" + System.Diagnostics.Process.GetCurrentProcess().Id;
                File.WriteAllText(tmp, json, new UTF8Encoding(false)); // no BOM
                SwapInto(tmp, path);
                Debug.Log($"[UCL_AgentCommandQueue] Saved queue -> {path}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandQueue] Failed to save queue: {e}");
                return false;
            }
        }

        /// <summary>把 <paramref name="iTmp"/> 換成 <paramref name="iPath"/> —— 中途**不存在**「檔案不存在」的瞬間。</summary>
        /// <remarks>
        /// 🔬 TASK-0264 對照組（summit 2026-09-22，Windows，3000 次寫入 × 一個不拿鎖的讀取端狂 stat）：
        /// <list type="table">
        /// <item><description><c>Delete</c> 然後 <c>Move</c> ⇒ 取樣 39359 次，**讀到「檔案不存在」20125 次（51.1%）**</description></item>
        /// <item><description><c>Replace</c> ⇒ 取樣 27441 次，**0 次**</description></item>
        /// </list>
        /// ⚠ 而同一組讀數給了第二件事：<c>Replace</c> 在那個極端取樣下被 OS 瞬時拒絕（<c>ACCESS_DENIED</c>）**220 次**，
        /// 而 <c>Delete</c>＋<c>Move</c> 是 0 次 —— **那是這次修法換來的代價，⛔ 不是白拿的。**
        /// <para>⇒ 所以這裡收斂一格有限重試：瞬時拒絕會在毫秒內自己過去，而**沒有重試的失效樣子是
        /// 「Save 悄悄失敗、那一筆指令從此不存在」** —— 正是本單在治的那一族。
        /// ⛔ 重試次數用完仍失敗就讓它往上炸，**不退回 Delete-then-Move**（退回去等於把剛拆掉的窗口靜默裝回來）。</para>
        /// <para>⚠ 射程：那 220 次來自一個 thread 每秒數萬次 stat 的 harness，**真實負載下的發生率我沒有量**。
        /// ⇒ 這格重試是照「代價已知、發生率未知」處理的，不是照一個量到的線上讀數。</para>
        /// </remarks>
        private static void SwapInto(string iTmp, string iPath)
        {
            const int aMaxAttempt = 5;
            for (int aAttempt = 1; ; ++aAttempt)
            {
                try
                {
                    if (File.Exists(iPath)) File.Replace(iTmp, iPath, null); // 原子替換
                    else File.Move(iTmp, iPath);                             // 建檔（沒有舊值可讀）
                    return;
                }
                catch (Exception) when (aAttempt < aMaxAttempt)
                {
                    System.Threading.Thread.Sleep(2 * aAttempt);
                }
            }
        }

        /// <summary>
        /// 收尾寫回 —— 🔴 **重讀磁碟**，只動這一批載入時就在的那些 id（與 TASK-0263 的執行器同構）。
        /// <para>🩸 舊版把「批次開始時載入的那份副本」整個寫回去 ⇒ 批次期間任何人 append 的那一筆
        /// 會被**整個蓋掉**，而下游看不見（它不在 queue、也沒有判定檔）。</para>
        /// <para>⚠ 這裡**不在整批期間握著鎖** —— 一批可能跑好幾十秒，握著它會把送指令的人擋到逾時。
        /// 要的只是「寫回的那一瞬間，依據的是磁碟現況」。</para>
        /// <para>⛔ 磁碟讀不到時**不寫**（回 false）—— 那正是本單重現到的那一格：
        /// 拿一份「讀不到 ⇒ 空」的結果去覆寫，等於刪掉全部。</para>
        /// </summary>
        /// <param name="iOriginalIds">這一批**載入當下**就在 queue 裡的 id。
        /// 磁碟上不在這個集合裡的 ＝ 批次期間新進的 ⇒ 一律保留。</param>
        /// <returns><c>true</c> ＝ 真的寫回去了。</returns>
        public static bool SaveMerged(UCL_AgentCommandQueueData data, ICollection<string> iOriginalIds,
                                      string agentId = null)
        {
            using (LockQueue(agentId))
            {
                var aDisk = Load(agentId, out QueueReadState aState);
                // 🔴 判準是 **== Ok**，⛔ 不是「不是 Unreadable」（TASK-0264 QA，kotoko 2026-09-22）：
                //   後者的失效樣子是「新加一個態就自動變成可以寫回」，而新的態多半正是
                //   「我這次沒讀到真正的內容」。⇒ 允許寫回的條件要**列舉允許**，不是列舉禁止。
                if (aState != QueueReadState.Ok)
                {
                    Debug.LogError($"[UCL_AgentCommandQueue] 收尾放棄寫回：{GetQueuePath(agentId)} 讀取結局＝{aState}"
                                   + "（Unreadable ＝截斷／壞碼，去看那顆檔；Busy ＝開不了，等一下再來；"
                                   + "Missing ＝檔不見了，而收尾本來就該有一顆）。"
                                   + "這一批的出隊結果**沒有落盤**，而那好過拿一份空的覆蓋掉整條 queue。");
                    return false;
                }

                var aMine = new Dictionary<string, UCL_AgentCommand>(StringComparer.Ordinal);
                foreach (var c in data?.Commands ?? new List<UCL_AgentCommand>())
                {
                    if (c != null && !string.IsNullOrEmpty(c.Id)) aMine[c.Id] = c;
                }

                var aOut = new List<UCL_AgentCommand>();
                int aKeptNew = 0;
                foreach (var c in aDisk?.Commands ?? new List<UCL_AgentCommand>())
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    if (aMine.TryGetValue(c.Id, out var aUpdated)) { aOut.Add(aUpdated); continue; }
                    if (iOriginalIds != null && iOriginalIds.Contains(c.Id)) continue;   // 這一批跑完出隊的
                    aOut.Add(c);                                                          // 批次期間新進 => 保留
                    ++aKeptNew;
                }

                var aWrite = data ?? new UCL_AgentCommandQueueData();
                aWrite.Commands = aOut;

                // 🩸 TASK-0264 QA 第二輪（kotoko 2026-09-22）：上一輪我把 `Save` 從 void 改成 bool
                //   並在文件上寫「`SaveMerged` 照實回報」—— 而**傳播沒改**，這裡照樣無條件 `return true`。
                //   ⇒ 同一支檔、同一天：我在新寫的 `MutateQueueOnDisk` 裡接對了，在這支舊的沒有。
                //   📌 她給的成因值得留著：**讀一段 code 去改 A，不會順便讓妳看見 B。**
                bool aSaved = Save(aWrite, agentId);
                if (!aSaved)
                {
                    Debug.LogError($"[UCL_AgentCommandQueue] 收尾寫回**失敗**：{GetQueuePath(agentId)}"
                                   + " —— 合併算完了而換檔沒成立 ⇒ 這一批的出隊結果沒有落盤。"
                                   + " ⛔ 回 false，⛔ 不要把「合併邏輯跑完了」當成「寫回去了」。");
                }
                if (aKeptNew > 0)
                {
                    Debug.Log($"[UCL_AgentCommandQueue] 保留了這一批期間新進的 {aKeptNew} 筆（下一輪跑）");
                }
                return aSaved;
            }
        }

        // ===========================================================
        // 簡易 JSON 序列化（不依賴 Unity JsonUtility，因為要支援 Dictionary）
        // ===========================================================

        static string SerializeJson(UCL_AgentCommandQueueData data)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"Commands\": [");
            bool firstCmd = true;
            foreach (var c in data.Commands ?? new List<UCL_AgentCommand>())
            {
                if (!firstCmd) sb.Append(",");
                firstCmd = false;
                sb.Append("\n    {");
                AppendField(sb, "Id", c.Id, true);
                AppendField(sb, "Type", c.Type, false);
                AppendField(sb, "Mode", c.Mode.ToString(), false);
                AppendInt(sb, "RunCount", c.RunCount);
                sb.Append(",\n      \"Args\": {");
                if (c.Args != null && c.Args.Count > 0)
                {
                    bool firstArg = true;
                    foreach (var kv in c.Args)
                    {
                        if (!firstArg) sb.Append(",");
                        firstArg = false;
                        sb.Append("\n        \"").Append(EscapeStr(kv.Key)).Append("\": \"").Append(EscapeStr(kv.Value)).Append("\"");
                    }
                    sb.Append("\n      ");
                }
                sb.Append("}");
                AppendField(sb, "CreatedAt", c.CreatedAt, false);
                AppendField(sb, "LastRunAt", c.LastRunAt, false);
                AppendField(sb, "LastRunResult", c.LastRunResult, false);
                AppendField(sb, "LastRunError", c.LastRunError, false);
                AppendField(sb, "Description", c.Description, false);
                sb.Append("\n    }");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static void AppendField(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\n      \"").Append(key).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append("\"").Append(EscapeStr(value)).Append("\"");
        }
        static void AppendBool(StringBuilder sb, string key, bool value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value ? "true" : "false");
        }
        static void AppendInt(StringBuilder sb, string key, int value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value);
        }
        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ===========================================================
        // 簡易 JSON 解析（支援我們自寫的格式 + agent 手寫的標準 JSON）
        // ===========================================================

        static UCL_AgentCommandQueueData ParseJson(string json)
        {
            int pos = 0;
            SkipWS(json, ref pos);
            ExpectChar(json, ref pos, '{');
            var result = new UCL_AgentCommandQueueData();
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                if (key == "Commands")
                {
                    result.Commands = ParseCommandArray(json, ref pos);
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return result;
        }

        static List<UCL_AgentCommand> ParseCommandArray(string json, ref int pos)
        {
            var list = new List<UCL_AgentCommand>();
            ExpectChar(json, ref pos, '[');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == ']') { pos++; break; }
                list.Add(ParseCommand(json, ref pos));
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return list;
        }

        static UCL_AgentCommand ParseCommand(string json, ref int pos)
        {
            var c = new UCL_AgentCommand();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);

                switch (key)
                {
                    case "Id":            c.Id = ParseStringOrNull(json, ref pos); break;
                    case "Type":          c.Type = ParseStringOrNull(json, ref pos); break;
                    case "Mode":
                        {
                            string s = ParseStringOrNull(json, ref pos);
                            if (Enum.TryParse<UCL_AgentCommandMode>(s, out var m)) c.Mode = m;
                            break;
                        }
                    case "RunCount":      c.RunCount = ParseInt(json, ref pos); break;
                    case "Executed":      // 向後相容：舊版 bool true 視為 RunCount=1
                        c.RunCount = ParseBool(json, ref pos) ? 1 : 0;
                        break;
                    case "Args":          c.Args = ParseStringDict(json, ref pos); break;
                    case "CreatedAt":     c.CreatedAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunAt":     c.LastRunAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunResult": c.LastRunResult = ParseStringOrNull(json, ref pos); break;
                    case "LastRunError":  c.LastRunError = ParseStringOrNull(json, ref pos); break;
                    case "Description":   c.Description = ParseStringOrNull(json, ref pos); break;
                    default:              SkipValue(json, ref pos); break;
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return c;
        }

        static Dictionary<string, string> ParseStringDict(string json, ref int pos)
        {
            var d = new Dictionary<string, string>();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string k = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                string v = ParseStringOrNull(json, ref pos) ?? "";
                d[k] = v;
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return d;
        }

        /// <summary>
        /// 從 <paramref name="aStart"/> 起讀 4 位 hex（JSON \uXXXX 的那四位），成功回 true 並輸出 code unit。
        /// ⚠ 不移動 pos —— 移動由呼叫端決定，因為代理對要先看完低位才知道要吃幾個字元。
        /// </summary>
        static bool TryReadHex4(string json, int aStart, out int aValue)
        {
            aValue = 0;
            if (aStart + 4 > json.Length) return false;
            for (int i = 0; i < 4; i++)
            {
                char c = json[aStart + i];
                int v;
                if (c >= '0' && c <= '9') v = c - '0';
                else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
                else { aValue = 0; return false; }
                aValue = (aValue << 4) | v;
            }
            return true;
        }

        static string ParseString(string json, ref int pos)
        {
            ExpectChar(json, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char ch = json[pos++];
                if (ch == '"') break;
                if (ch == '\\' && pos < json.Length)
                {
                    char esc = json[pos++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        // \uXXXX —— 缺這一格時會落到 default 把反斜線吃掉，`🩸` 變成裸字 `uD83EuDE78`。
                        // 🩸 2026-08-29 basecamp 開 TASK-0093；2026-09-02 summit 在開遷移單時撞到活體：
                        //    同一份 criteria 走 senate CLI 落檔後非 BMP emoji 全損毀，走 run_cmd.py 完好（對照組在單上）。
                        // 物理意義：兩個 client 對同一份內容的逃逸寫法不同（python json 預設 ensure_ascii 出 \uXXXX），
                        //          而**每一層都回綠** —— Cmd Success、回傳檔正常、單子建得出來，只有內容是壞的。
                        case 'u':
                            if (TryReadHex4(json, pos, out int aHi))
                            {
                                pos += 4;
                                // 代理對：C# string 本身就是 UTF-16，high+low 兩個 char 直接接上即是一個 code point。
                                // ⚠ 單獨的 high surrogate 不合併就吐半個字 —— 所以低位不成對時只吐高位，交給上層顯示層決定怎麼畫。
                                if (aHi >= 0xD800 && aHi <= 0xDBFF
                                    && pos + 6 <= json.Length && json[pos] == '\\' && json[pos + 1] == 'u'
                                    && TryReadHex4(json, pos + 2, out int aLo)
                                    && aLo >= 0xDC00 && aLo <= 0xDFFF)
                                {
                                    pos += 6;
                                    sb.Append((char)aHi);
                                    sb.Append((char)aLo);
                                }
                                else sb.Append((char)aHi);
                            }
                            else
                            {
                                // 非法逃逸（hex 不足 4 位／非 hex）⇒ **原樣保留並喊出來**，不靜默吃掉反斜線。
                                // ⛔ 不 throw：一筆壞 JSON 讓整條 queue 讀不了會把所有 persona 一起卡住，
                                //    而「內容有一段沒解開」比「大家都動不了」輕。喊聲留給人判。
                                Debug.LogWarning($"[AgentCommandQueue] 非法 \\u 逃逸（pos={pos}），原樣保留未解碼。");
                                sb.Append('\\');
                                sb.Append(esc);
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        static string ParseStringOrNull(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos < json.Length && json[pos] == 'n')
            {
                if (pos + 4 <= json.Length && json.Substring(pos, 4) == "null")
                {
                    pos += 4;
                    return null;
                }
            }
            return ParseString(json, ref pos);
        }

        static bool ParseBool(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos + 4 <= json.Length && json.Substring(pos, 4) == "true")  { pos += 4; return true; }
            if (pos + 5 <= json.Length && json.Substring(pos, 5) == "false") { pos += 5; return false; }
            return false;
        }

        static int ParseInt(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            int start = pos;
            if (pos < json.Length && (json[pos] == '-' || json[pos] == '+')) pos++;
            while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            if (pos == start) return 0;
            return int.TryParse(json.Substring(start, pos - start), out var v) ? v : 0;
        }

        static void SkipValue(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length) return;
            char ch = json[pos];
            if (ch == '"') { ParseString(json, ref pos); return; }
            if (ch == '{' || ch == '[')
            {
                char open = ch, close = (ch == '{') ? '}' : ']';
                int depth = 0;
                while (pos < json.Length)
                {
                    char c = json[pos];
                    if (c == '"') { ParseString(json, ref pos); continue; }
                    if (c == open) depth++;
                    else if (c == close) { depth--; pos++; if (depth == 0) return; continue; }
                    pos++;
                }
                return;
            }
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) return;
                pos++;
            }
        }

        static void SkipWS(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }
        static void ExpectChar(string json, ref int pos, char ch)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length || json[pos] != ch)
                throw new Exception($"Expected '{ch}' at pos {pos}");
            pos++;
        }
    }
}
#endif
