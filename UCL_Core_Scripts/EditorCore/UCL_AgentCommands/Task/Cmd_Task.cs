// 區塊職責：任務系統的 Editor 端 Cmd 入口 —— 讀取（list / show / kanban）就地做；**寫入一律轉交 Senate**。
// 物理意義：跨 agent 的**交付承諾**通道。與見叢（`_keys_open.md`）分工，判準是一句當下答得出來的話：
//          **「這件事跟專案有關嗎？」** 有 ⇒ 這裡（Tim 2026-09-07 拍板）；
//          純個人代辦（我的自律、我為什麼又拖了）⇒ 見叢。⛔ **不再兩邊都留**。
// 數值影響：讀 Tasks/ 底下的單檔；寫入 op（create / claim / assign / unassign / update / comment / check / link /
//          resolve / commit / sweep / wrapup）經 `UCL_TaskSenateBridge` spawn 一次 `senate cmd task`。
// 設計沿革：Plan_Task_Management_System.md（gura 撰寫 / Tim 2026-08-24 拍板）。
//
// ⚠ **2026-09-30 改（TASK-0349）**：任務單寫入整格搬到 Senate —— 唯一寫入端是 Senate Server（`task-write`），
//   狀態機與所有閘住在 SCP_Core `SCP_TaskOps`（本檔原本的 op 本體，逐段移植過去），配號改原子建檔。
//   ⇒ 本檔的寫入 op 只剩「驗參數 → 轉交 → 把入口的回傳檔原樣接回來」。⛔ 不再寫出第二份。
//   🩸 搬之前：`UCL_TaskIO` 的鎖只擋得住同一個 process，而 `senate cmd commit` 推單、晚安寫 skip 都得委派進 Editor
//   ⇒ **Editor 沒開，那兩件就做不了**；配號在參數檢查之前 ⇒ 打錯 enum 會吃掉號碼（0351／0352）。
//
// ⚠ **2026-09-07 改**：早安 brief 長出 §2.5 見單（`SCP_WakeBrief.ActiveTasksSection`），
//   每天機械撈「我涉及且 in_progress / in_review」的單 ⇒ **不再靠見叢的引用行**。
//   🩸 舊設計（Tim 2026-08-24「早安零改動」）的代價是：別人指派給我而我沒手抄進見叢的單，
//   早安不會提 —— 那個洞當時補在晚安對帳。手抄是一次性快照：
//   明天新開的單看不見，抄進去的那些在單子關掉後會躺著變成假帳。⇒ 改成每天自己算。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public class Cmd_Task : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Task";

        public override string ShortDescription =>
            "跨 agent 任務管理：create/list/show/claim/assign/unassign/update/comment/check/link/resolve/commit/sweep/wrapup/kanban。"
            + " 一單一檔；跨人承諾建 Task，個人自律留見叢。";

        public override string ArgsSchema =>
            "op=create|list|show|claim|assign|unassign|update|comment|check|link|resolve|commit|sweep|wrapup|kanban（預設 list） | " +
            "sha=<commit SHA，op=commit 必填> | mode=fixes|refs（op=commit 用，預設 fixes） | " +
            "title=<標題，create 必填> | criteria=<驗收標準，create 必填；type=bug 可省（三段骨架自帶）> | description= | " +
            "evidence=<硬證＋讀數怎麼拿到的，type=bug create 必填；不確定算不算就報 —— 拿不出硬證改 type=improvement + tags=friction|suggestion> | " +
            "type=feature|improvement|refactor|spike|subtask|bug|epic（預設 feature；epic＝主 Task 傘；all 僅供篩選不可落盤） | " +
            "priority=urgent|high|normal|low（預設 normal） | " +
            "severity=none|blocking|wrong|annoying（傷害形狀；type=bug 預設 wrong，其餘 none 不落行） | " +
            "status=<create/update 設定值；list 篩選：open（預設）/all/backlog/todo/in_progress/in_review/done/cancelled> | " +
            "index=<單號：show/claim/assign/update/comment/link/resolve 必填；收 TASK-0008 / 8 / 0008> | " +
            "role=dev|design|qa|pm|reviewer|sound|art（claim/assign 用，預設 dev） | " +
            "scope=<施工範圍，絕對路徑，取施工的最大範圍>（op=claim 選填）——**給了就等於「我現在要動工」**："
            + "認領＋開 Coding 場＋把這張單綁上去，三件事一步；開場被擋時**認領也不發生**（TASK-0202） | " +
            "target_persona=<assign 的對象> | assignee=<list 篩選：只看某人參與的單> | " +
            "replace=1（assign 用：**換角色** —— 先拿掉這個人既有的角色再指派；不帶＝加一個角色） | " +
            "body=<comment 內容> | " +
            "criteria_index=<op=check：**未勾清單**的 1-based 序號，逗號分隔可多筆；不帶＝dry-run 印清單、零寫入> | " +
            // ⭐ 2026-09-10 補宣告：`expect_text` 是 TASK-0163 加的，**加完沒有寫進本 schema** ——
            //   而它是「勾錯格子」唯一的那道錨 ⇒ 沒宣告的代價是沒有人知道可以帶它。
            //   ⚠ 而補這一格的過程本身是一次血證：我先用 `sed -n '34,51p'` 讀本 schema，
            //     據此報了「15 個參數未宣告」並動手補 —— 而它們**全都宣告在第 72 行之後**，
            //     只是不在我開的那個窗裡。⇒ 我的插入還把 `remove=1` 那句
            //     「打錯就做反」的警語從它的主詞上切斷了（讀起來像在警告 `unset`）。
            //   📌 同一族第 N 次：射程由我選的窗決定，而**缺的那些不會出現在自己的清單上**。
            //     這一次抓到我的是「缺必填」錯誤訊息把整份 schema 印出來 —— 不是我更仔細。
            "expect_text=<op=check 的錨：未勾清單那一行的**前綴**；多筆用 `|` 分隔，"
            + "筆數必須等於 criteria_index。序號會位移、文字不會 ⇒ 對不上**整批不做**（TASK-0163）> | " +
            "op_link=blocked_by|blocks|subtask_of|has_subtask|related_to（link 用） | target=<link 的對方單號；收 TASK-0008 / 8 / 0008> | " +
            "remove=1（link 用：**解除**該關聯而不是建立 —— 建與解共用同一組 kind 語彙）"
            + "　⚠ 這是「打錯就做反」的那類：`remov=1` 會被靜默丟掉 ⇒ 走預設 ⇒ **建立**關聯（TASK-0109） | " +
            "note=<resolve 的結單說明> | qa_note=<代 QA 結單時的驗收紀錄> | " +
            "milestone= | epic_id= | tags=<逗號分隔> | " +
            "tag=<list 篩選：有這個 tag 的單> | epic=<list 篩選：TASK-0008 / 8 皆可> | " +
            "memory_topic=<create/update 設定；list 篩選：工作記憶主題名> | " +
            "memory_archived_commit=<update：記憶歸檔／刪除後的 commit sha> | " +
            "unset=<update：顯式清空欄位，逗號分隔；可清 memory_topic / memory_archived_commit / milestone" +
            "　—— 給 `--arg <欄位>=` 空值是「這次不動這欄」，清欄位一律走這裡（TASK-0079）> | " +
            "progress=<收工進度，op=wrapup 必填，走 --arg-file> | why=<為什麼卡住／試過什麼不行，選填 ⇒ 寫進工作記憶> | " +
            "memory_type=pitfall|decision|knowhow（op=wrapup 的 why 用，預設 pitfall） | " +
            "confirm=1（resolve 必帶）" +
            "　| allow_shrink=1（update：criteria／description **整段縮水**時的顯式放行 —— 不帶時守衛會擋下並印出將被刪掉的行數與前幾行，TASK-0188）";

        public override string ExampleArgs =>
            "op=create;title=Cmd_Task 接上 Fixes TASK-n 閉環;criteria=- [ ] senate cmd commit 實跑一次並讀回狀態;priority=high";

        // 說明文件住 Senate（TASK-0426）：senate cmd doc --arg op=show --arg name=Task_Management —— Unity HelpURL 解析不到，留空。

        // 區塊職責：**執行前**參數閘的宣告（TASK-0069）——
        //   `UCL_AgentCommandRunner:349 → UCL_CmdArgsValidator.Validate` 讀它，缺必填就**不執行 handler**。
        // 物理意義：`Validate` 對 `ArgsSpec == null` 的 handler **直接 return true** ⇒
        //   本檔在補上這份宣告之前，走 ucmd 這條路是**完全沒有執行前參數閘**的。
        //   （另一個消費端是 `UCL_CmdSchemaExporter` → `commands_schema.json`，但那份產物只有
        //    退場中的 python client 在讀 ⇒ 本宣告的價值在上面那條，不在產物。）
        // 數值影響：⚠ 這裡**只宣告 handler 自己已經會 throw 的必填** —— 多宣告一格就會擋掉
        //   一個今天合法的呼叫，而那種壞法不會有人喊（呼叫端只看到「被擋」，看不到「是誰多寫的」）。
        //   ⇒ 逐格對照：`Require(iArgs, out _)` 的十個 op 要 `index`；`create` 只要 `title`
        //   （`evidence`／`criteria` 是**依 type 而定**的條件必填，留在 handler 判，宣告層表達不了）。
        // ⛔ **`op` 刻意不宣告 Required** —— 它有預設值 `"list"`，不帶 op ＝ 列清單，那是合法呼叫。
        //   把有預設值的參數列進 Required，等於把一條既有的路砍掉。
        //
        // ⭐ 2026-09-21（calli）補上每個 op 的 `Known`（TASK-0258 引進的白名單，TASK-0109 的第二個消費端）。
        //   `Known` ＝ 該 op **實際會去讀的每一個鍵**，逐格對照三種讀法掃出來的：
        //   `GetArg(iArgs,…)`／`ParseEnumArg(iArgs,…)`／`Require(iArgs,…)`（後者讀的是 `index`）。
        // ⚠ **兩欄的保守方向相反，這不是筆誤**：
        //   `Required` 少列沒有代價（handler 自己照樣 throw），多列會砍掉今天走得通的路；
        //   `Known`    多列沒有代價（只是少擋一個），**少列會擋掉今天合法的呼叫**。
        //   ⇒ 所以 `Required` 從嚴、`Known` 從寬，而 `Known` 必須跟著 handler 的讀取一起改。
        //
        // ⛔ **`op=list` 不吃 `type`** —— Editor 這側只篩 status／assignee／epic／tag／milestone／memory_topic，
        //   而 `senate cmd tasks` **吃** `type`。⇒ 補上白名單之後
        //   `ucmd run Task --arg op=list --arg type=bug` 會**被擋下**。
        //   📌 那是刻意的：在本次改動之前，它回的是一份**沒有篩過、卻看起來像篩過**的清單。
        //   「被擋下」與「拿到一份錯的清單」之中，前者是能當場修好的那一種。
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                // 無必填：list / sweep / kanban（handler 內零 reject ⇒ 不宣告，比照 Cmd_Tavern 的 leave）
                // ⚠ 但 `Known` 照宣告 —— **「沒有必填」與「什麼都收」是兩件事**，
                //   而在本次改動之前它們在這裡共用同一個空殼。
                ["list"] = new UCL_CmdOpSpec
                {
                    Known = new[] { "status", "assignee", "epic", "tag", "milestone", "memory_topic" },
                },
                ["sweep"] = new UCL_CmdOpSpec { Known = new[] { "assignee", "confirm" } },
                ["kanban"] = new UCL_CmdOpSpec { Known = new[] { "index" } },

                // create：`title` 是無條件必填（:190 那個 throw 的成員之一）。
                ["create"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "title" },
                    Known = new[]
                    {
                        "title", "type", "description", "evidence", "criteria",
                        "severity", "priority", "status", "epic_id", "milestone",
                        "tags", "memory_topic",
                    },
                },

                // 以下十個都走 `Require(iArgs, out int aIndex)`（:1291）⇒ 無 index 必 throw。
                ["show"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index" },
                    Known = new[] { "index" },
                },
                ["claim"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index" },
                    Known = new[] { "index", "role", "scope" },
                },
                ["update"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index" },
                    Known = new[]
                    {
                        "index", "title", "description", "criteria", "status",
                        "priority", "severity", "milestone", "memory_topic",
                        "memory_archived_commit", "allow_shrink", "unset",
                    },
                },
                ["resolve"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index" },
                    Known = new[] { "index", "status", "confirm", "note", "qa_note" },
                },
                ["assign"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "target_persona" },
                    Known = new[] { "index", "target_persona", "role", "replace" },
                },
                ["unassign"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "target_persona" },
                    Known = new[] { "index", "target_persona", "role" },
                },
                ["comment"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "body" },
                    Known = new[] { "index", "body" },
                },
                // ⛔ `criteria_index` 刻意**不**列 Required —— 不帶它是合法呼叫（dry-run 印未勾清單）。
                ["check"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index" },
                    Known = new[] { "index", "criteria_index", "expect_text" },
                },
                // 🩸 本次補 `Known` 的起點就是這一支（2026-09-21 calli）：
                //   我想建「關聯」，打了 `--arg kind=related_to` —— 真正的參數叫 **`op_link`**，
                //   而 `kind` 被**靜默吃掉** ⇒ `op_link` 取預設值 `blocked_by`，
                //   於是兩張單被標成阻塞，而回傳是 ✓Success、時間線上也只寫「標記被 … 阻塞」。
                //   ⇒ 我要到 `Fixes TASK-0258` 推不動狀態（blocker 閘擋下，而它**擋得對**）才發現。
                //   📌 **打錯參數名的代價不落在打錯的那一刻，落在下一個讀這份資料的機制上。**
                ["link"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "target" },
                    Known = new[] { "index", "target", "op_link", "remove" },
                },
                ["commit"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "sha" },
                    Known = new[] { "index", "sha", "mode" },
                },
                ["wrapup"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "index", "progress" },
                    Known = new[] { "index", "progress", "why", "memory_type" },
                },
            },
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // ⭐ TASK-0162：本 handler 移出主執行緒（0163 上鎖之後的第一支）。
            // 物理意義：慢的是**檔案 IO**，不是酒館公告 —— 讀數（`_cmd_slow.jsonl`，2026-09-10）：
            //   `wrapup` 4185ms／`check` 3640ms／`update` 2564ms **都不發公告**，而 Runner 側
            //   phases 加總只有約 10ms ⇒ 時間全在 handler 內讀寫單檔（一張單近千行，且有些 op 掃全部單）。
            // ⛔ 前置不可反：本族的 RMW 併發安全靠 `UCL_TaskIO` 的鎖（TASK-0163），
            //   **不再**靠「單一主執行緒」那個前提（`AssertMainThread` 已換成 `AssertHoldsRmwLock`）。
            // ⚠ 掃過才敢切：Task 族內**零個**主緒 only 的 Unity API（AssetDatabase／PlayerPrefs／
            //   EditorPrefs／Application.dataPath／EditorUtility 全 0 命中）；唯一碰 Unity 的是
            //   三族**有快取**的路徑解析器，而那正是 `EnterBackground()` 先摸一次的東西。
            // ⚠ **必須帶 `args`**：`EnterBackground` 的讀數（`offloaded` / `bg_tid`）是從 args 的
            //   `_cmd_id` 戳進去的 ⇒ 不帶就**切了但不記錄**，而 `offloaded=false` 同時是
            //   「沒 offload」與「忘了帶 args」兩件事 —— 我 2026-09-10 就照它的用法註解打了無參數版，
            //   拿到一個看起來像「offload 沒生效」的假紅燈（既有 6 個呼叫點全都帶）。
            await UCL_AgentCmdOffload.EnterBackground(args);
            string aOp = GetArg(args, "op", "list").Trim().ToLowerInvariant();
            string aActor = GetArg(args, "persona", "unknown").Trim();
            var aR = new StringBuilder();
            aR.AppendLine($"# Task op={aOp} persona={aActor}  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            // 回傳檔**不論成功或失敗都要寫出來**（Cmd_Plurk 的血證：擋下時直接 throw ⇒
            // 錯誤訊息說「詳見回傳檔」而那個回傳檔從來沒被寫出來）。
            try
            {
                switch (aOp)
                {
                    case "list": OpList(args, aR); break;
                    case "show": OpShow(args, aActor, aR); break;
                    case "kanban": OpKanban(aR); break;
                    case "create": case "claim": case "assign": case "unassign": case "update": case "comment":
                    case "check": case "link": case "resolve": case "commit": case "sweep": case "wrapup":
                        OpDelegate(args, aOp, aActor, aR); break;
                    default:
                        throw new Exception($"[Task] 認不得的 op='{aOp}'"
                            + "（create|list|show|claim|assign|unassign|update|comment|check|link|resolve|commit|sweep|wrapup|kanban）");
                }
                await UniTask.CompletedTask;
            }
            catch (Exception e)
            {
                // 例外的**原因**必須落進回傳檔 —— caller 保證會讀的只有這一份
                //（result json 的 error 與 _cmd_errors/ 是第二現場，不是每個人都會追過去）。
                // 各 op 自己寫的 `## blocked` 段照舊保留，這裡是兜底：沒寫過原因的 throw
                //（例如 enum 參數打錯字）也一定看得到為什麼。
                aR.AppendLine();
                aR.AppendLine("## ❌ 失敗");
                aR.AppendLine($"- reason: {e.Message}");
                throw;
            }
            finally
            {
                // ===========================================================
                // 區塊職責：回傳檔落 **per-persona**，不再落全域單槽。
                //
                // 🩸 血證 2026-08-25（BUG-34 / TASK-0026 ①，summit 現場撞到）：
                //   舊版寫死 `Tasks/_last_task_report.md` —— **一顆全域 slot，last-write-wins**。
                //   實測：08:19:47 basecamp 送出 `op=kanban`；08:19:53 檔案 header 變成 `persona=summit`；
                //   08:20:00 又變成 `persona=gura`。**我讀我自己那次的回傳檔，讀到的是別人的。**
                //   ⚠ 而發現它的唯一原因是 header 上的名字跟我不一樣 ——
                //     **若那次剛好是同一個 persona 的另一個 session 跑的，就永遠不會有人發現。**
                //   📌 這跟 2026-08-16 `s_CurrentCmdOutputs` 那隻是同一族（見 UCL_AgentCommandRunner
                //     L68-75）：queue 依 persona 分 lane 之後 watcher **並行派遣**，
                //     任何全域 slot 都會互相覆蓋，而且**完全無聲**。
                //     那次的解是 per-cmd context；這次的解是 per-persona 落點。
                //
                // ⚠ 舊路徑不留空殼：留著一份**內容過期但長得正常**的檔，
                //   比「檔不見了」更毒 —— 讀的人不會知道自己讀的是三天前的視圖。
                //   ⇒ 覆寫成一行指路 stub（內容固定 ⇒ 多人同時寫也不會漂）。
                // ===========================================================
                string aPayload = UCL_LettersPath.CmdPayload(aActor, "task", aOp);
                Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
                File.WriteAllText(aPayload, aR.ToString(), new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(args, aPayload);
                Debug.Log($"[Task] op={aOp} persona={aActor} → {aPayload}");

                UCL_TaskIO.EnsureDir();
                File.WriteAllText(UCL_TaskIO.LastReportPath,
                    "# （已退場）Task 回傳檔不再寫在這裡\n\n"
                    + "> 這裡曾是**全域單槽**，兩個人同時跑 `run Task` 會互相覆蓋，\n"
                    + "> 而覆蓋是**無聲的**（TASK-0026 ①）。\n\n"
                    + "回傳檔現在落在 **`letters/<persona>/cmd/task_<op>.md`** ——\n"
                    + "派遣 client 會直接印出「📄 回傳檔：<路徑>」，照那一行讀，不要背路徑。\n",
                    new UTF8Encoding(false));
            }
        }

        // ===========================================================
        // 區塊職責：寫入 op —— **轉交 `senate cmd task`**（TASK-0349），把入口的回傳檔原樣接回來。
        // 物理意義：狀態機、閘、配號、酒館通知、工作記憶寫入全在 Senate 那一側（SCP_Core `SCP_TaskOps`）；
        //          本支只把呼叫端給的參數原樣送過去（`ArgsSpec` 的 Known 已經在執行前擋過拼錯的名字）。
        // ⚠ 四態：0 ⇒ 成功；1／2 ⇒ 閘擋下或參數錯（**零寫入**，原因在回傳檔）；6 ⇒ 確定沒寫；
        //   7 ⇒ **不知道**（⛔ 別重打 —— 先 `senate cmd tasks --arg index=<n>` 回讀）。非 0 一律丟例外，讓 Runner 判 Failed。
        // ===========================================================
        void OpDelegate(Dictionary<string, string> iArgs, string iOp, string iActor, StringBuilder ioR)
        {
            var aSend = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] aKnown = ArgsSpec.Ops.TryGetValue(iOp, out UCL_CmdOpSpec aSpec) && aSpec.Known != null ? aSpec.Known : new string[0];
            foreach (string k in aKnown)
                if (iArgs.TryGetValue(k, out string v) && v != null) aSend[k] = v;
            UCL_TaskSenateResult r = UCL_TaskSenateBridge.Run(iOp, iActor, aSend);
            string aPayload = r.ReadPayload();
            ioR.Clear();
            ioR.AppendLine($"> ⤷ 本筆由 Editor 轉交 `senate cmd task op={iOp}`（任務單唯一的寫入端是 Senate Server，TASK-0349）"
                + $"　exit={r.ExitCode}");
            ioR.AppendLine();
            if (aPayload.Length > 0) ioR.Append(aPayload);
            else
            {
                ioR.AppendLine("## senate 的輸出（入口沒有落回傳檔）");
                ioR.AppendLine("```");
                ioR.AppendLine(r.Output.TrimEnd());
                ioR.AppendLine("```");
            }
            if (r.Ok) return;
            throw new Exception(r.Unknown
                ? $"[Task] op={iOp} **結果不明**（等不到任務寫入端回執）—— ⛔ 別重打，先 `senate cmd tasks --arg index=<n>` 回讀那張單"
                : $"[Task] op={iOp} 沒有寫（senate exit {r.ExitCode}）—— 原因見回傳檔的 `## blocked`／`## ❌ 失敗`");
        }

        // ===========================================================
        // 區塊職責：清單。預設只印**還沒關的**（open）——
        //   已關的單混進來會讓「還有多少沒做」這個數字失去意義。
        // ===========================================================
        void OpList(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            var aFilter = ParseEnumArg(iArgs, "status", UCL_TaskStatus.open);
            string aAssignee = GetArg(iArgs, "assignee", "").Trim();
            string aMilestone = GetArg(iArgs, "milestone", "").Trim();
            var aAll = UCL_TaskIO.LoadAll();

            UCL_TaskIO.CountStats(out int aOpen, out int aStale, out int aBroken, out int aBlocked);
            ioR.AppendLine($"## 讀數：總 **{aAll.Count}** 張／未關 **{aOpen}**／"
                + $"被阻塞 **{aBlocked}**／stale(in_progress ≥{UCL_TaskIO.STALE_DAYS} 天) **{aStale}**"
                + (aBroken > 0 ? $"／⚠ 時戳壞掉 **{aBroken}**（不算進 stale —— 不假裝知道它幾天沒動）" : ""));
            ioR.AppendLine();

            var aList = aAll.Where(e =>
            {
                if (aFilter == UCL_TaskStatus.all) return true;
                if (aFilter == UCL_TaskStatus.open) return !e.IsClosed();
                return e.status == aFilter;
            }).ToList();
            if (aAssignee.Length > 0)
                aList = aList.Where(e => e.RolesOf(aAssignee).Count > 0
                    || string.Equals(e.reporter, aAssignee, StringComparison.OrdinalIgnoreCase)).ToList();
            if (aMilestone.Length > 0)
                aList = aList.Where(e => string.Equals(e.milestone, aMilestone, StringComparison.OrdinalIgnoreCase)).ToList();

            // ===========================================================
            // 區塊職責：`tag` 與 `epic` 兩個篩選端（TASK-0009）。
            // 🩸 為什麼要有：這兩個欄位在此之前**只有 create 一個寫入端** ——
            //   basecamp 在 TASK-0008 打了 `tags=[epic, main]`，而那不是追蹤機制，是一個註記
            //   （寫得進去、查不出來 ⇒ 追蹤主 Task 只剩人眼）。
            // ⚠ `epic` 收 `TASK-0008` / `8` / `0008` 三種寫法，**認不出時不猜** ——
            //   靜默把認不出的篩選值當成「全部」會讓人以為那個 epic 底下什麼都沒有。
            // ===========================================================
            string aTag = GetArg(iArgs, "tag", "").Trim();
            if (aTag.Length > 0)
                aList = aList.Where(e => e.tags.Any(t =>
                    string.Equals(t, aTag, StringComparison.OrdinalIgnoreCase))).ToList();

            string aMemFilter = GetArg(iArgs, "memory_topic", "").Trim();
            if (aMemFilter.Length > 0)
                aList = aList.Where(e => string.Equals(e.memory_topic, aMemFilter,
                    StringComparison.OrdinalIgnoreCase)).ToList();

            string aEpicRaw = GetArg(iArgs, "epic", "").Trim();
            int aEpicIdx = -1;
            if (aEpicRaw.Length > 0)
            {
                aEpicIdx = UCL_TaskIO.ParseTaskRef(aEpicRaw);
                if (aEpicIdx <= 0)
                    throw new Exception($"[Task] 認不得的 epic 參照 '{aEpicRaw}'"
                        + "（收 TASK-0008 / 8 / 0008）—— 不猜，因為猜錯會印出一個空清單"
                        + "而那看起來像「這個 epic 底下沒有東西」");
                string aEpicId = "TASK-" + aEpicIdx.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
                var aParent = UCL_TaskIO.Find(aEpicIdx);
                // 兩條路都算：子單自己宣告 epic_id、或父單把它收進 subtask_indices
                // ⇒ 只認一邊的話，關係寫了一半時清單會少人（而少的那個不會叫）
                aList = aList.Where(e =>
                    string.Equals(e.epic_id, aEpicId, StringComparison.OrdinalIgnoreCase)
                    || (aParent != null && aParent.subtask_indices.Contains(e.index))).ToList();
            }

            ioR.AppendLine($"## list（filter=`{aFilter}`"
                + (aAssignee.Length == 0 ? "" : $"　assignee=`{aAssignee}`")
                + (aMilestone.Length == 0 ? "" : $"　milestone=`{aMilestone}`")
                + (aTag.Length == 0 ? "" : $"　tag=`{aTag}`")
                + (aMemFilter.Length == 0 ? "" : $"　memory_topic=`{aMemFilter}`")
                + (aEpicIdx <= 0 ? "" : $"　epic=`TASK-{aEpicIdx:0000}`")
                + $"）—— **{aList.Count}** 張");
            if (aList.Count == 0)
            {
                ioR.AppendLine("- （沒有符合的單。這是「篩不到」不是「系統沒東西」——"
                    + $" 全部有 {aAll.Count} 張，`--arg status=all` 看得到。）");
                return;
            }
            var aNow = DateTime.UtcNow;
            foreach (var e in aList)
            {
                var aBlockers = UCL_TaskIO.OpenBlockers(e);
                int aDays = e.DaysSinceUpdate(aNow);
                ioR.AppendLine($"- **{e.Id}** `{e.status}` / `{e.priority}`　{e.title}");
                ioR.AppendLine($"    · 參與：{Participants(e)}"
                    + (e.commit_shas.Count == 0 ? "" : $"　commit: {string.Join(" ", e.commit_shas)}")
                    + $"　{(aDays < 0 ? "⚠ 時戳壞掉" : aDays + " 天前更新")}");
                if (aBlockers.Count > 0)
                    ioR.AppendLine($"    · 🛑 **被阻塞**：{string.Join("；", aBlockers)}");
            }
        }

        void OpShow(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aPath = UCL_TaskIO.TaskPath(aIndex);
            ioR.AppendLine($"## {e.Id} — {e.title}");
            ioR.AppendLine($"- `{e.type}` / "
                + (e.severity == UCL_TaskSeverity.none ? "" : $"`{e.severity}` / ")
                + $"`{e.priority}` / `{e.status}`　開單：{e.reporter}");
            ioR.AppendLine($"- 參與：{Participants(e)}");
            ioR.AppendLine($"- {LastCommentLine(e, iActor)}");
            ioR.AppendLine($"- blocked_by: {Ids(e.blocked_by)}　blocks: {Ids(e.blocks)}　related_to: {Ids(e.related_to)}");
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aBlockers.Count > 0) ioR.AppendLine($"- 🛑 **未解 blocker**：{string.Join("；", aBlockers)}");
            if (e.epic_id.Length > 0) ioR.AppendLine($"- 屬於主 Task：**{e.epic_id}**");
            // 記憶錨點 —— 四種答案（沒掛／主題在／已歸檔／連結壞了）刻意各自不同形
            ioR.AppendLine($"- 工作記憶：{UCL_TaskMemoryLink.Describe(e)}");
            // 📎 關聯文件（TASK-0037；Tim 2026-08-25「單子可以關聯相關文件」）——
            //   key_docs 早已存在於主題卡，缺的一直只是讀取端。null＝沒有主題可讀（工作記憶那行已講）⇒ 不印；
            //   空清單＝主題在而沒列 ⇒ 必須與「沒綁主題」不同形（讀數 E）。
            var aKeyDocs = UCL_TaskMemoryLink.KeyDocs(e);
            if (aKeyDocs != null)
            {
                if (aKeyDocs.Count == 0)
                    ioR.AppendLine("- 📎 關聯文件：主題卡的 `key_docs` **沒列任何文件**"
                        + "（有綁主題、清單是空的 —— 跟「沒掛工作記憶」是兩回事）");
                else
                {
                    ioR.AppendLine($"- 📎 關聯文件（主題卡 `key_docs`，{aKeyDocs.Count} 份）：");
                    foreach (var aDoc in aKeyDocs) ioR.AppendLine($"    · {aDoc}");
                }
            }
            if (e.tags.Count > 0) ioR.AppendLine($"- tags: {string.Join(" ", e.tags.Select(t => "`" + t + "`"))}");
            // 子任務進度 —— 主 Task 的意義就是這個數字（沒有它，subtask_indices 只是一串號碼）
            UCL_TaskIO.SubtaskProgress(e, out int aSubTotal, out int aSubClosed,
                out var aSubOpen, out var aSubMissing);
            if (aSubTotal > 0)
            {
                ioR.AppendLine($"- 子任務 **{aSubClosed}/{aSubTotal} 已關**"
                    + (aSubOpen.Count == 0 ? "　✅ 全部關了" : $"　還剩 **{aSubOpen.Count}** 張沒關：")
                    + (aSubMissing.Count == 0 ? ""
                        : $"　⚠ 另有 {aSubMissing.Count} 個號碼**查不到單**（{string.Join(",", aSubMissing)}）"
                          + " —— 查不到不等於已完成"));
                foreach (var s2 in aSubOpen) ioR.AppendLine($"    · {s2}");
            }
            if (e.commit_shas.Count > 0) ioR.AppendLine($"- commit_shas: {string.Join(" ", e.commit_shas)}");
            ioR.AppendLine($"- 單檔：`{aPath}`");
            ioR.AppendLine();
            ioR.AppendLine("## 單檔全文（**這是磁碟上的事實，不是我重述的**）");
            ioR.AppendLine();
            ioR.AppendLine("```markdown");
            ioR.AppendLine(File.ReadAllText(aPath, Encoding.UTF8).TrimEnd());
            ioR.AppendLine("```");
        }

        // ===========================================================
        // 區塊職責：摘要區的「最後留言」行（TASK-0037）—— 四種形狀各自不同形。
        // 🩸 血證（PM 本人，2026-08-24）：留言 04:15 已在，07:21 還寫「還剩：等她回」並照那句收工
        //   ⇒ 單卡一天。卡的不是回覆，是「有人回了而我沒讀」沒有任何機械會說。
        // 基準＝caller 在這張單上的**最後一次動作**（留言／開單／認領／指派／收工／commit／update／跳過皆算）
        //   —— gura 拍板：「我在這張單上最後做過什麼」對每一個讀的人都成立；
        //   「我的 wrapup」「我的留言」當基準只對特定人成立（沒收過工的人拿不到基準）。
        // ⚠ 「沒有基準可比」與「零留言」都**不可以印成「你已是最新」**——
        //   兩種狀態摺進一句好話正是彙總漂白（2026-08-25 抓了一整天的同形）。
        // ===========================================================
        static string LastCommentLine(UCL_TaskEntry e, string iActor)
        {
            if (e.comments == null || e.comments.Count == 0)
                return "💬 最後留言：—（**這張單零留言**，沒有「最新」可言）";
            var aLast = e.comments[e.comments.Count - 1];
            string aWhen = FmtLocal(aLast.at);
            DateTime aBase = LastActionUtc(e, iActor);
            if (aBase == DateTime.MinValue)
                return $"💬 最後留言：{aLast.persona} @ {aWhen} —— "
                    + "**你從未在這張單上動過，沒有基準可比**（這不是「已是最新」）";
            if (ParseUtc(aLast.at) > aBase)
                return $"💬 最後留言：{aLast.persona} @ {aWhen} —— ⚠ **在你上次操作之後有新留言**";
            return $"💬 最後留言：{aLast.persona} @ {aWhen} —— 你已是最新";
        }

        // ===========================================================
        // 區塊職責：caller 在這張單上最後一次動作的時戳（UTC）；沒有動過回 MinValue。
        // 物理意義：留言有結構化 persona 欄直接比；時間線的 actor **不是結構化欄位**，
        //   靠事件文字裡的固定措辭認人 —— ⚠ 只認「actor 是動作主詞」的措辭，
        //   「被指派」「被 @」不算你的動作（那正是血證那格：被動出現 ≠ 我讀過）。
        // ⚠ 讀不到／解析不出 ⇒ 少一些基準候選，最壞退成「沒有基準可比」——
        //   那一形不會冒充「已是最新」，倒向的是提醒那一側。
        // ===========================================================
        static DateTime LastActionUtc(UCL_TaskEntry e, string iActor)
        {
            var aOut = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(iActor) || iActor == "unknown") return aOut;
            if (e.comments != null)
                foreach (var c in e.comments)
                    if (string.Equals(c.persona, iActor, StringComparison.OrdinalIgnoreCase))
                    { var t = ParseUtc(c.at); if (t > aOut) aOut = t; }
            // 措辭清單對齊本檔與 UCL_TaskReconcile 實際寫進時間線的每一種事件行 ——
            // 新增事件措辭時這裡要跟著補，漏了的症狀是「自己動過卻顯示沒有基準」（吵的那形，不是靜默的那形）。
            string[] aPatterns = {
                $"由 {iActor} 開單", $"{iActor} 認領", $"{iActor} 指派", $"{iActor} 收工",
                $"by {iActor}", $"{iActor} 留言", $"{iActor}：", $"{iActor} 加入為", $"{iActor} 顯式跳過",
            };
            try
            {
                string aPath = UCL_TaskIO.TaskPath(e.index);
                if (File.Exists(aPath))
                {
                    bool aIn = false;
                    foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                    {
                        if (aLine.StartsWith("## 活動與討論時間線", StringComparison.Ordinal)) { aIn = true; continue; }
                        if (aIn && aLine.StartsWith("## ", StringComparison.Ordinal)) break;
                        if (!aIn) continue;
                        string aTrim = aLine.TrimStart();
                        if (!aTrim.StartsWith("- ", StringComparison.Ordinal)) continue;
                        bool aMine = false;
                        foreach (var p in aPatterns)
                            if (aLine.IndexOf(p, StringComparison.Ordinal) >= 0) { aMine = true; break; }
                        if (!aMine) continue;
                        string aStamp = aTrim.Substring(2).TrimStart();
                        int aCut = aStamp.IndexOfAny(new[] { ' ', '\t', '　' });
                        if (aCut > 0) aStamp = aStamp.Substring(0, aCut);
                        var t = ParseUtc(aStamp);
                        if (t > aOut) aOut = t;
                    }
                }
            }
            catch { /* 同上：退成「沒有基準可比」，不冒充「已是最新」 */ }
            return aOut;
        }

        static DateTime ParseUtc(string iIso)
        {
            if (DateTime.TryParse(iIso ?? "", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)) return aUtc;
            return DateTime.MinValue;
        }

        /// <summary>UTC ISO → 本地 `MM-dd HH:mm`（顯示層才轉當地 —— utc-everywhere-local-display）。</summary>
        static string FmtLocal(string iIso)
        {
            var aUtc = ParseUtc(iIso);
            if (aUtc == DateTime.MinValue) return iIso ?? "";
            return DateTime.SpecifyKind(aUtc, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm");
        }

        // ===========================================================
        // 區塊職責：文字看板。
        // ⚠ 這是**清單的另一種排法**，不是新的事實來源。
        //   看板的價值（看見 WIP 流動）在「同一批人連續數天推進」時才拿得到，
        //   而我們是一天 wake 一次、記憶重置、跨天換人接手 ⇒ 它先當一個總覽用。
        // ===========================================================
        void OpKanban(StringBuilder ioR)
        {
            var aAll = UCL_TaskIO.LoadAll();
            UCL_TaskStatus[] aCols = { UCL_TaskStatus.backlog, UCL_TaskStatus.todo, UCL_TaskStatus.in_progress,
                UCL_TaskStatus.in_review, UCL_TaskStatus.done, UCL_TaskStatus.cancelled };
            UCL_TaskIO.CountStats(out int aOpen, out int aStale, out int aBroken, out int aBlocked);
            ioR.AppendLine($"## kanban —— 總 **{aAll.Count}** 張／未關 **{aOpen}**／被阻塞 **{aBlocked}**"
                + $"／stale **{aStale}**" + (aBroken > 0 ? $"／時戳壞掉 **{aBroken}**" : ""));
            ioR.AppendLine();
            foreach (var aCol in aCols)
            {
                var aIn = aAll.Where(e => e.status == aCol).ToList();
                ioR.AppendLine($"### {aCol}　（{aIn.Count}）");
                if (aIn.Count == 0) { ioR.AppendLine("- —"); continue; }
                foreach (var e in aIn)
                {
                    var aBlockers = UCL_TaskIO.OpenBlockers(e);
                    ioR.AppendLine($"- **{e.Id}** `{e.priority}` {e.title}　[{Participants(e)}]"
                        + (aBlockers.Count > 0 ? $"　🛑 {aBlockers.Count} blocker" : ""));
                }
            }
        }

        // ── 小工具 ────────────────────────────────────────────────
        UCL_TaskEntry Require(Dictionary<string, string> iArgs, out int oIndex)
        {
            // 同上：與 `epic` / `target` 共用一支解析器，⛔ 三處不各寫一次。
            string aIndexRaw = GetArg(iArgs, "index", "").Trim();
            if (aIndexRaw.Length == 0)
                throw new Exception("[Task] 這個 op 需要 --arg index=<單號>（收 TASK-0008 / 8 / 0008）");
            oIndex = UCL_TaskIO.ParseTaskRef(aIndexRaw);
            if (oIndex <= 0)
                throw new Exception($"[Task] 認不得的 index 參照 '{aIndexRaw}'"
                    + "（收 TASK-0008 / 8 / 0008）—— ⛔ 這不是「沒帶」，是**帶了但讀不出來**，不猜。");
            var e = UCL_TaskIO.Find(oIndex);
            if (e == null)
                throw new Exception($"[Task] TASK-{oIndex} 不存在（單檔：{UCL_TaskIO.TaskPath(oIndex)}）"
                    + " —— 「查不到」不等於「已經關掉」，先 `op=list --arg status=all` 看一眼");
            return e;
        }

        static string Participants(UCL_TaskEntry e) => ParticipantsOf(e.participants);

        /// <summary>參與者清單的字串化 —— 收 list 而不是 entry，
        /// 讓「落檔後那一份」也印得出來（`Mutate` 之後呼叫端手上的 `e` 是鎖前的提示）。</summary>
        static string ParticipantsOf(List<UCL_TaskParticipant> iList)
        {
            if (iList == null || iList.Count == 0) return "**無**（沒有人在做這件事）";
            return string.Join("、", iList.Select(p => $"{p.persona}({p.role})"));
        }

        static string Ids(List<int> iList)
            => iList == null || iList.Count == 0 ? "—"
             : string.Join(" ", iList.Select(i => "TASK-" + i.ToString("0000")));

        /// <summary>正規化：小寫、去空白、`InProgress`/`in-progress` 一律吃成 `in_progress`。</summary>
        static string Norm(string iRaw)
        {
            string s = (iRaw ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            return s;
        }

        // 區塊職責：--arg 的 enum 解析（type / priority / status / role 共用）。
        // 物理意義：打錯字要**當場炸並列出合法值** —— 舊版裸字串照單全收，
        //   "featur" 會安靜落盤成一張篩選查不到的單，而那看起來像「單不存在」。
        static T ParseEnumArg<T>(Dictionary<string, string> iArgs, string iKey, T iDefault) where T : struct, Enum
        {
            string v = Norm(GetArg(iArgs, iKey, ""));
            if (v.Length == 0) return iDefault;
            if (UCL_TaskWire.TryParse(v, out T aV)) return aV;
            throw new Exception($"[Task] --arg {iKey}={v} 不是合法值（{string.Join("|", Enum.GetNames(typeof(T)))}）");
        }

    }
}
#endif
