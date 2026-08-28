// 區塊職責：任務系統的 Cmd 入口（create / list / show / claim / assign / update / comment / link / resolve / kanban）。
// 物理意義：跨 agent 的**交付承諾**通道。與見叢（`_keys_open.md`）分工，判準是一句當下答得出來的話：
//          **「有沒有第二個人在等這件事？」** 有 ⇒ 這裡；只有我自己要記住 ⇒ 見叢。
//          兩者都是 ⇒ 兩邊都留（Task 記別人在等什麼，見叢記我為什麼又拖了）。
// 數值影響：寫 Tasks/ 底下兩種檔（tasks/<index>.md 一單一檔 / 回傳檔）；不動 Treasury、不發酒館訊息。
// 設計沿革：Plan_Task_Management_System.md（gura 撰寫 / Tim 2026-08-24 拍板）。
//
// ⚠ 早安 brief **不新增任何節**（Tim 2026-08-24 拍板，改掉 RFC §2②）：
//   Task 經由見叢的引用行（`- [ ] [TASK-0042] …`）進入 brief ⇒ 早安流程零改動。
//   代價：別人指派給我而我沒寫進見叢的單，早安不會提 ——
//   那個洞補在**晚安對帳**（多印一類「指派給我但見叢沒引用」），不補在早安。
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
            "跨 agent 任務管理：create/list/show/claim/assign/unassign/update/comment/link/resolve/commit/sweep/wrapup/kanban。"
            + " 一單一檔；跨人承諾建 Task，個人自律留見叢。";

        public override string ArgsSchema =>
            "op=create|list|show|claim|assign|unassign|update|comment|link|resolve|commit|sweep|wrapup|kanban（預設 list） | " +
            "sha=<commit SHA，op=commit 必填> | mode=fixes|refs（op=commit 用，預設 fixes） | " +
            "title=<標題，create 必填> | criteria=<驗收標準，create 必填> | description= | " +
            "type=feature|improvement|refactor|spike|subtask（預設 feature） | " +
            "priority=urgent|high|normal|low（預設 normal） | " +
            "status=<create/update 設定值；list 篩選：open（預設）/all/backlog/todo/in_progress/in_review/done/cancelled> | " +
            "index=<單號：show/claim/assign/update/comment/link/resolve 必填> | " +
            "role=dev|design|qa|pm|reviewer|sound|art（claim/assign 用，預設 dev） | " +
            "target_persona=<assign 的對象> | assignee=<list 篩選：只看某人參與的單> | " +
            "body=<comment 內容> | " +
            "op_link=blocked_by|blocks|subtask_of|has_subtask|related_to（link 用） | target=<link 的對方單號> | " +
            "note=<resolve 的結單說明> | qa_note=<代 QA 結單時的驗收紀錄> | " +
            "milestone= | epic_id= | tags=<逗號分隔> | " +
            "tag=<list 篩選：有這個 tag 的單> | epic=<list 篩選：TASK-0008 / 8 皆可> | " +
            "memory_topic=<create/update 設定；list 篩選：工作記憶主題名> | " +
            "memory_archived_commit=<update：記憶歸檔／刪除後的 commit sha> | " +
            "progress=<收工進度，op=wrapup 必填，走 --arg-file> | why=<為什麼卡住／試過什麼不行，選填 ⇒ 寫進工作記憶> | " +
            "memory_type=pitfall|decision|knowhow（op=wrapup 的 why 用，預設 pitfall） | " +
            "confirm=1（resolve 必帶）";

        public override string ExampleArgs =>
            "op=create;title=Cmd_Task 接上 Fixes TASK-n 閉環;criteria=- [ ] git_commit.py 實跑一次並讀回狀態;priority=high";

        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Task_Management_Workflow.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
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
                    case "create": await OpCreate(args, aActor, aR); break;
                    case "list": OpList(args, aR); break;
                    case "show": OpShow(args, aActor, aR); break;
                    case "claim": await OpClaim(args, aActor, aR); break;
                    case "assign": await OpAssign(args, aActor, aR); break;
                    case "unassign": OpUnassign(args, aActor, aR); break;
                    case "update": OpUpdate(args, aActor, aR); break;
                    case "comment": await OpComment(args, aActor, aR); break;
                    case "link": OpLink(args, aActor, aR); break;
                    case "resolve": await OpResolve(args, aActor, aR); break;
                    case "commit": await OpCommit(args, aActor, aR); break;
                    case "sweep": await OpSweep(args, aActor, aR); break;
                    case "wrapup": await OpWrapup(args, aActor, aR); break;
                    case "kanban": OpKanban(aR); break;
                    default:
                        throw new Exception($"[Task] 認不得的 op='{aOp}'"
                            + "（create|list|show|claim|assign|unassign|update|comment|link|resolve|commit|sweep|wrapup|kanban）");
                }
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
                    + "`run_cmd.py` 會直接印出「📄 回傳檔：<路徑>」，照那一行讀，不要背路徑。\n",
                    new UTF8Encoding(false));
            }
        }

        // ===========================================================
        // 區塊職責：開新單。
        // 物理意義：`criteria`（驗收標準）是**必填**，不是建議 —— RFC §2④。
        //   沒有驗收條件的單，QA 只能憑感覺說「看起來可以」，而那不是驗收。
        //   （Sirius 教的：交件的通過條件比結論值錢。）
        // 數值影響：配一個 index、寫一份 tasks/<index>.md。
        // ===========================================================
        async UniTask OpCreate(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            string aTitle = GetArg(iArgs, "title", "").Trim();
            string aCriteria = GetArg(iArgs, "criteria", "").Trim();

            var aMissing = new List<string>();
            if (string.IsNullOrWhiteSpace(aTitle)) aMissing.Add("title");
            if (string.IsNullOrWhiteSpace(aCriteria)) aMissing.Add("criteria");
            if (aMissing.Count > 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 缺必填欄位：{string.Join(" / ", aMissing)}");
                ioR.AppendLine("- `criteria` 要寫**可以被客觀量測**的條件（QA 有權以「這條驗不了」退回）。");
                ioR.AppendLine("  例：`- [ ] run_cmd op=link 之後兩張單的 blocked_by/blocks 各自讀回有對方`");
                throw new Exception($"[Task] create 缺必填：{string.Join(",", aMissing)}");
            }

            string aNow = UCL_TaskIO.NowUtc();
            var e = new UCL_TaskEntry
            {
                index = UCL_TaskIO.IncrementAndGetIndex(),
                type = ParseEnumArg(iArgs, "type", UCL_TaskType.feature),
                priority = ParseEnumArg(iArgs, "priority", UCL_TaskPriority.normal),
                status = ParseEnumArg(iArgs, "status", UCL_TaskStatus.todo),
                title = aTitle,
                reporter = iActor,
                milestone = GetArg(iArgs, "milestone", "").Trim(),
                epic_id = GetArg(iArgs, "epic_id", "").Trim(),
                memory_topic = GetArg(iArgs, "memory_topic", "").Trim(),
                created_at = aNow,
                updated_at = aNow,
            };
            // `all` / `open` 是篩選成員不是狀態 —— 開單不准帶著它們落盤
            if (e.status == UCL_TaskStatus.all || e.status == UCL_TaskStatus.open)
                throw new Exception($"[Task] status=`{e.status}` 是篩選用的成員，不是可落盤的狀態");
            foreach (var t in SplitList(GetArg(iArgs, "tags", ""))) e.tags.Add(t);

            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, aCriteria, GetArg(iArgs, "description", "").Trim(),
                $"{aNow}　`{e.status}`　由 {iActor} 開單");

            ioR.AppendLine($"## ✅ 已建單 **{e.Id}**");
            ioR.AppendLine($"- `{e.type}` / `{e.priority}` / `{e.status}`　開單：{e.reporter}");
            ioR.AppendLine($"- title: {e.title}");
            ioR.AppendLine($"- 單檔：`{UCL_TaskIO.TaskPath(e.index)}`");
            ioR.AppendLine();
            ioR.AppendLine("## ⚠ 這張單現在沒有任何參與者");
            ioR.AppendLine("- 指派走後台頁或 `op=assign`（Tim 2026-08-24：指派與通知由 Tim 在後台管理）。");
            ioR.AppendLine("- **沒有指名 QA 的單，結單由開單人或 PM 做** —— 那不是預設值，是一個選擇；");
            ioR.AppendLine("  要有人驗就 `op=assign --arg role=qa`，`resolve` 才會有閘門擋。");
            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步");
            ioR.AppendLine($"- 認領 → `run Task --arg op=claim --arg index={e.index} --arg role=dev`");
            ioR.AppendLine($"- 見叢留一行引用（Task 記別人在等什麼，見叢記我為什麼又拖了）：");
            ioR.AppendLine($"  `awakening.py keys --persona <me> --add \"[{e.Id}] {e.title}\"`");
            ioR.AppendLine($"- 做完 commit 訊息帶 `Fixes {e.Id}`（提交時自動推進 —— 有 QA 進 in_review，沒 QA 直接 done）");
            ioR.AppendLine();
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Created, iActor,
                GetArg(iArgs, "description", "").Trim(), iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
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
            ioR.AppendLine($"- `{e.type}` / `{e.priority}` / `{e.status}`　開單：{e.reporter}");
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
        // 區塊職責：認領 —— 自己把自己加進參與者並推到 in_progress。
        // ⚠ 認領會變成**占位**，除非釋放是機械的（persona 會下線、記憶會斷，
        //   明天的我可能不記得認領過）。逾期自動釋放排在 P2（晚安對帳那一步），
        //   目前先讓 `list` 把 stale 印出來 —— **告警是給人看的，釋放才是機械的**，
        //   所以這裡不假裝那一格已經有守衛。
        // ===========================================================
        async UniTask OpClaim(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            var aRole = ParseEnumArg(iArgs, "role", UCL_TaskRole.dev);
            string aNow = UCL_TaskIO.NowUtc();
            bool aNew = AddParticipant(e, iActor, aRole, aNow);
            var aFrom = e.status;

            // ===========================================================
            // 區塊職責：認領要不要推狀態，**由角色決定**。
            // 🩸 basecamp PM 對帳 2026-08-24（酒館 seq 13527）：首版**無條件**寫
            //   `status = "in_progress"` ⇒ `--arg role=qa` 也會把單子推成「進行中」。
            //   而 QA 認領的語意是「我來驗」，不是「開工了」——
            //   那會讓看板上一張等驗收的單看起來像有人在寫 code。
            // 判準：只有**執行角色**（dev/design/sound/art）＋**還沒開工的狀態**（backlog/todo）
            //   才推進；驗收角色（qa/reviewer/pm）與已在 in_progress/in_review 的單一律不動狀態，
            //   並且**明說為什麼沒動** —— 靜默不動跟「推了」在回傳檔上不能長得一樣。
            // ===========================================================
            bool aDoingRole = aRole == UCL_TaskRole.dev || aRole == UCL_TaskRole.design
                || aRole == UCL_TaskRole.sound || aRole == UCL_TaskRole.art;
            bool aNotStarted = aFrom == UCL_TaskStatus.backlog || aFrom == UCL_TaskStatus.todo;
            string aWhyNoMove = null;
            if (!aDoingRole) aWhyNoMove = $"`{aRole}` 是驗收／協調角色，不是「開工」⇒ 狀態不動";
            else if (!aNotStarted) aWhyNoMove = $"單子已經在 `{aFrom}` ⇒ 不往回推（認領只從 backlog/todo 推進）";

            if (aWhyNoMove == null) e.status = UCL_TaskStatus.in_progress;
            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", aWhyNoMove == null
                ? $"{aNow}　`in_progress`　{iActor} 認領（role={aRole}，原狀態 {aFrom}）"
                : $"{aNow}　`{e.status}`　{iActor} 加入為 {aRole}（狀態不動：{aWhyNoMove}）");

            ioR.AppendLine($"## ✅ {e.Id} 已認領");
            ioR.AppendLine(aWhyNoMove == null
                ? $"- {aFrom} → **in_progress**　role=`{aRole}`"
                    + (aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）")
                : $"- 狀態**維持 `{aFrom}`**　role=`{aRole}` —— {aWhyNoMove}"
                    + (aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）"));
            ioR.AppendLine($"- 參與：{Participants(e)}");
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aBlockers.Count > 0)
            {
                ioR.AppendLine($"- 🛑 **注意：這張單有未解 blocker** —— {string.Join("；", aBlockers)}");
                ioR.AppendLine("  認領不擋（也許妳就是要去解它），但 `resolve` 會擋。");
            }
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Status, iActor,
                aWhyNoMove == null
                    ? $"{aFrom} → **in_progress**（{iActor} 認領 role={aRole}）"
                    : $"{iActor} 加入為 `{aRole}`（狀態維持 `{aFrom}` —— {aWhyNoMove}）", iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
        }

        async UniTask OpAssign(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aTarget = GetArg(iArgs, "target_persona", "").Trim();
            if (aTarget.Length == 0) throw new Exception("[Task] op=assign 需要 --arg target_persona=<誰>");
            var aRole = ParseEnumArg(iArgs, "role", UCL_TaskRole.dev);
            string aNow = UCL_TaskIO.NowUtc();
            bool aNew = AddParticipant(e, aTarget, aRole, aNow);
            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`assign`　{iActor} 指派 {aTarget} 為 {aRole}");

            ioR.AppendLine($"## ✅ {e.Id} 參與者已更新");
            ioR.AppendLine($"- {(aNew ? "新增" : "已存在，未重複加")}：{aTarget}（{aRole}）");
            ioR.AppendLine($"- 參與：{Participants(e)}");
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Assigned, iActor,
                $"{aTarget} ← `{aRole}`", iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
            ioR.AppendLine($"- ⚠ 被指派的人若沒在見叢寫一行 `[{e.Id}]`，他的**早安 brief 不會提這張單**");
            ioR.AppendLine("  （早安流程刻意零改動 —— Tim 2026-08-24 拍板）。酒館通知是他知道這件事的那條路。");
        }

        void OpUpdate(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aNow = UCL_TaskIO.NowUtc();
            var aChanges = new List<string>();

            string aStatus = GetArg(iArgs, "status", "").Trim();
            if (aStatus.Length > 0)
            {
                var aNorm = ParseEnumArg(iArgs, "status", UCL_TaskStatus.todo);
                if (aNorm == UCL_TaskStatus.all || aNorm == UCL_TaskStatus.open)
                    throw new Exception($"[Task] status=`{aNorm}` 是篩選用的成員，不是可落盤的狀態");
                // ⛔ 結單只能走 resolve —— 那條路上有 blocker 與 QA 兩道閘。
                //    留一個「用 update 也能推 done」的旁路等於那兩道閘不存在。
                if (aNorm == UCL_TaskStatus.done || aNorm == UCL_TaskStatus.cancelled)
                    throw new Exception("[Task] 結單請走 `op=resolve`（那條路上有 blocker 與 QA 兩道閘，"
                        + "而 update 沒有）。這不是麻煩，是刻意不留旁路。");
                aChanges.Add($"status {e.status} → {aNorm}");
                e.status = aNorm;
                // 🩸 2026-08-24：我誤關了別人的單再改回 todo，而 `closed_at` **留著我那筆取消的時戳** ⇒
                //   status=todo 而 closed_at 有值 —— 資料自己跟自己打架，且看不出哪一邊是真的。
                //   ⇒ 從已關改回未關時一律清掉它，並在時間線寫明清了什麼（不靜默改數字）。
                if (e.closed_at.Length > 0)
                {
                    aChanges.Add($"closed_at 清空（原 {e.closed_at} —— 未關的單不該有結案時間）");
                    e.closed_at = "";
                }
            }
            string aPriority = GetArg(iArgs, "priority", "").Trim();
            if (aPriority.Length > 0)
            {
                var aPri = ParseEnumArg(iArgs, "priority", UCL_TaskPriority.normal);
                aChanges.Add($"priority {e.priority} → {aPri}");
                e.priority = aPri;
            }
            string aTitle = GetArg(iArgs, "title", "").Trim();
            if (aTitle.Length > 0) { aChanges.Add("title 改寫"); e.title = aTitle; }
            string aMilestone = GetArg(iArgs, "milestone", "").Trim();
            if (aMilestone.Length > 0) { aChanges.Add($"milestone → {aMilestone}"); e.milestone = aMilestone; }
            // 記憶錨點（契約①：這兩格歸 Task 側寫，記憶側的 task_indices 歸 CLI）
            string aMemTopic = GetArg(iArgs, "memory_topic", "").Trim();
            if (aMemTopic.Length > 0)
            {
                aChanges.Add($"memory_topic {(e.memory_topic.Length == 0 ? "(空)" : e.memory_topic)} → {aMemTopic}"
                    + (UCL_TaskMemoryLink.TopicExists(aMemTopic) ? "" : "　⚠ **這個主題目前不在磁碟上**（照樣寫入，但要知道）"));
                e.memory_topic = aMemTopic;
            }
            string aMemSha = GetArg(iArgs, "memory_archived_commit", "").Trim();
            if (aMemSha.Length > 0)
            { aChanges.Add($"memory_archived_commit → {aMemSha}"); e.memory_archived_commit = aMemSha; }
            // criteria / description 是 Save 的參數不是 entry 欄位 —— 但它們一樣是變更（TASK-0033 ③）。
            // 🩸 血證（Tim 2026-08-25 撞到）：只給 --arg criteria= 是**靜默 no-op** ——
            //   它沒進 aChanges ⇒ 走「沒有任何變更」那條路 ⇒ 單子一個字都不變，而回傳檔看起來像判斷。
            //   而「擴充當前 Task 的驗收細項」是收斂機制的主要出口，等於主要出口需要 workaround（多帶 title）才會開。
            string aCriteria = GetArg(iArgs, "criteria", "");
            if (aCriteria.Trim().Length > 0) aChanges.Add("criteria 整段改寫");
            string aDescription = GetArg(iArgs, "description", "");
            if (aDescription.Trim().Length > 0) aChanges.Add("description 整段改寫");

            if (aChanges.Count == 0)
            {
                ioR.AppendLine($"## {e.Id} 沒有任何變更");
                // ⚠ 欄位清單要列全 —— 錯誤訊息自己低報的話，讀的人分不出
                //   「這不是欄位」與「這是欄位但沒被計入」（TASK-0033 ③ 的第二格）。
                ioR.AppendLine("- 沒給任何可更新的欄位（status / priority / title / milestone /"
                    + " memory_topic / memory_archived_commit / criteria / description）⇒ **什麼都沒寫**。");
                return;
            }
            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, GetArg(iArgs, "criteria", ""), GetArg(iArgs, "description", ""),
                $"{aNow}　`update`　{iActor}：{string.Join("／", aChanges)}");
            ioR.AppendLine($"## ✅ {e.Id} 已更新");
            foreach (var c in aChanges) ioR.AppendLine($"- {c}");
        }

        async UniTask OpComment(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aBody = GetArg(iArgs, "body", "").Trim();
            if (aBody.Length == 0) throw new Exception("[Task] op=comment 需要 --arg body=<內容>");
            string aNow = UCL_TaskIO.NowUtc();
            var aComment = new UCL_TaskComment
            {
                id = UCL_TaskIO.NextCommentId(e),
                persona = iActor,
                at = aNow,
                body = aBody,
            };
            e.comments.Add(aComment);
            UCL_TaskIO.Touch(e, aNow);
            // 時間線只留一行「有人留言了」的索引 —— 內容在留言區，**不存兩份**
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`comment`　{iActor} 留言 #{aComment.id}");

            ioR.AppendLine($"## ✅ {e.Id} 已留言 #{aComment.id}");
            ioR.AppendLine($"- 作者：{iActor}　時間：{aNow}");
            ioR.AppendLine("- 落點：單檔的 `## 留言` 區塊（時間線只留一行索引）");
            ioR.AppendLine();
            ioR.AppendLine("```markdown");
            ioR.AppendLine(aBody);
            ioR.AppendLine("```");
            ioR.AppendLine();
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Comment, iActor, "", aBody, iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
            ioR.AppendLine("- ⚠ 留言**會推進 `updated_at`** ⇒ 它會讓 stale 計時歸零。");
            ioR.AppendLine("  所以「留言說我還在做」跟「真的有做」在 stale 讀數上長得一樣 —— 這是這個讀數的邊界。");
        }

        // ===========================================================
        // 區塊職責：把「通知有沒有發出去」寫進回傳檔。
        // 物理意義：通知是對別人的動作，而它可能失敗（酒館寫入失敗 / persona 名字打錯）。
        //   ⚠ 沒有這一行的話，「我以為他知道了」會變成一個**沒有人發現**的錯 ——
        //     主動作成功、附帶效果靜默失敗，那正是這個 repo 最貴的形狀。
        // ===========================================================
        static void AppendNotifyLine(StringBuilder ioR, UCL_TaskEntry e, string iActor, bool iOk)
        {
            // ⚠ 名單邏輯必須與 UCL_TaskNotify.BuildBody 一致（參與者 ＋ 開單人 − 動手的人）——
            //   回傳檔說「@ 了誰」而實際 @ 的是另一群人，那比不印更糟。
            var aMentions = e.participants.Select(p => p.persona)
                .Concat(new[] { e.reporter })
                .Where(s => !string.IsNullOrWhiteSpace(s)
                            && !string.Equals(s, iActor, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!iOk)
            {
                ioR.AppendLine("- ⚠ **酒館通知沒發出去**（單子已經寫好了）—— 見 Editor log 的 `[TaskNotify]`。");
                ioR.AppendLine("  ⇒ 相關的人**還不知道這件事**，要自己去講一聲。");
                return;
            }
            ioR.AppendLine(aMentions.Count > 0
                ? $"- 📣 酒館已通知並 @：{string.Join(" ", aMentions.Select(s => "@" + s))}"
                : "- 📣 酒館已發，但**沒有 @ 任何人** —— 參與者與開單人扣掉操作者之後是空的"
                    + "（不是通知失敗；如果連開單人都是我自己，那就真的沒有人需要知道）");
        }

        void OpLink(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            Require(iArgs, out int aIndex);
            string aKind = Norm(GetArg(iArgs, "op_link", "blocked_by"));
            if (!int.TryParse(GetArg(iArgs, "target", "").Trim(), out int aTarget))
                throw new Exception("[Task] op=link 需要 --arg target=<對方單號>");

            // 解除關聯（TASK-0033 ②）：同一個 op、帶 remove=1 —— 建與解共用 kind 語彙，不另造第二套詞
            bool aRemove = GetArg(iArgs, "remove", "").Trim() == "1";
            bool aChanged;
            string aErr;
            if (aRemove) aChanged = UCL_TaskIO.Unlink(aIndex, aTarget, aKind, iActor, out aErr);
            else aChanged = UCL_TaskIO.Link(aIndex, aTarget, aKind, iActor, out aErr);
            if (aErr.Length > 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: {aErr}");
                throw new Exception($"[Task] {(aRemove ? "unlink" : "link")} 失敗：{aErr}");
            }
            var a = UCL_TaskIO.Find(aIndex);
            var b = UCL_TaskIO.Find(aTarget);
            string aHead = aRemove
                ? (aChanged ? "✅ 已解除關聯（雙向對稱移除，時間線兩邊都留了一筆）"
                            : "（這個關聯本來就不存在，沒有東西可解）")
                : (aChanged ? "✅ 已建立關聯" : "（關聯本來就存在，沒有重複寫）");
            ioR.AppendLine($"## {aHead}");
            ioR.AppendLine($"- `{aKind}`：{a.Id} ↔ {b.Id}");
            // 🩸 2026-08-24：首版這裡只印 blocked_by / blocks / related_to 三格 ——
            //   而 `subtask_of` 改的是 `epic_id` 與 `subtask_indices`，**兩格都不在印出來的欄位裡**。
            //   於是回讀那行對父子關係什麼都證明不了，卻長得跟證明過一樣。
            //   ⇒ 判準：**回讀要印「這次動過的那一格」**，不是印一組固定欄位。
            ioR.AppendLine("- 回讀（**雙向都要有，單向寫入是靜默錯**）:");
            ioR.AppendLine($"    · {a.Id}: {RelationLine(a)}");
            ioR.AppendLine($"    · {b.Id}: {RelationLine(b)}");
            if (aKind == "blocked_by" && b.participants.Count == 0)
            {
                ioR.AppendLine($"- ⚠ **{b.Id} 沒有任何參與者，而它現在卡著 {a.Id}。**");
                ioR.AppendLine("  沒有人在解的 blocker 會讓被卡的單永久停住 —— 而永久停住看起來像「有在管」。");
                ioR.AppendLine($"  ⇒ 指派一個人去解它：`op=assign --arg index={b.index} --arg target_persona=<誰> --arg role=dev`");
            }
        }

        // ===========================================================
        // 區塊職責：結單 —— 三道閘（blocker / QA / confirm），一道都不留旁路。
        // 物理意義：① `blocked_by` 還有沒關的單 ⇒ **擋**（機械攔截，不是提醒）
        //          ② 單上指名 QA 而動手的不是那位 QA ⇒ 擋，除非附驗收紀錄（RFC §2④）
        //          ③ `confirm=1` 才真的寫 —— 「我只是想看看」與「我要關掉它」不得同形
        // ===========================================================
        async UniTask OpResolve(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            var aStatus = ParseEnumArg(iArgs, "status", UCL_TaskStatus.done);
            if (aStatus != UCL_TaskStatus.done && aStatus != UCL_TaskStatus.cancelled)
                throw new Exception($"[Task] resolve 的 status 只能是 done|cancelled（收到 '{aStatus}'）");
            string aNote = GetArg(iArgs, "note", "").Trim();
            string aQaNote = GetArg(iArgs, "qa_note", "").Trim();

            ioR.AppendLine($"## resolve 前的閘（{e.Id} `{e.status}` → `{aStatus}`）");

            // ① blocker 閘 —— cancelled 不受此限（取消一張被卡住的單是合理的）
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aStatus == UCL_TaskStatus.done && aBlockers.Count > 0)
            {
                ioR.AppendLine($"- 🛑 **擋下**：還有 {aBlockers.Count} 個未解 blocker —— {string.Join("；", aBlockers)}");
                ioR.AppendLine("  這是機械攔截：blocker 沒解而推 Done，等於宣告一件還做不到的事已經完成。");
                throw new Exception($"[Task] resolve 擋下：{e.Id} 還有 {aBlockers.Count} 個未解 blocker");
            }
            ioR.AppendLine(aBlockers.Count == 0 ? "- ✅ blocker 閘：沒有未解的 blocker"
                : $"- ⚠ blocker 閘：有 {aBlockers.Count} 個未解，但 status=cancelled ⇒ 放行（取消一張被卡的單是合理的）");

            // ② QA 閘
            string aQaBlock = UCL_TaskIO.QaGateBlocked(e, iActor, aQaNote);
            if (aStatus == UCL_TaskStatus.done && aQaBlock != null)
            {
                ioR.AppendLine($"- 🛑 **擋下（QA 閘）**：{aQaBlock}");
                throw new Exception("[Task] resolve 擋下：QA 未簽");
            }
            var aQa = e.QaPersonas();
            ioR.AppendLine(aQa.Count == 0
                ? "- ⚠ QA 閘：**這張單沒有指名 QA** ⇒ 沒有閘可以擋（開單時就沒有人被指名驗收）"
                : $"- ✅ QA 閘：{string.Join(" / ", aQa)}"
                    + (aQaNote.Length > 0 ? $"（代簽，附驗收紀錄：{aQaNote}）" : "（本人結單）"));

            // ③ confirm 閘
            if (GetArg(iArgs, "confirm", "").Trim() != "1")
            {
                ioR.AppendLine("- 🛑 **dry-run**（沒帶 `confirm=1`）⇒ 什麼都沒寫。");
                ioR.AppendLine("  上面兩道閘的結果是真的讀數，重跑同一道指令加 `--arg confirm=1` 才會真的結單。");
                return;
            }

            string aNow = UCL_TaskIO.NowUtc();
            var aFrom = e.status;
            e.status = aStatus;
            e.closed_at = aNow;
            if (aNote.Length > 0) e.resolution_note = aNote;
            if (aQaNote.Length > 0)
                e.resolution_note = (e.resolution_note + "\n\n**QA 代簽紀錄**：" + aQaNote).Trim();
            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`{aStatus}`　{iActor} 結單（原狀態 {aFrom}）"
                + (aNote.Length == 0 ? "" : $"：{aNote.Replace("\r", " ").Replace("\n", " ")}"));

            ioR.AppendLine();
            ioR.AppendLine($"## ✅ {e.Id} 已結單");
            ioR.AppendLine($"- {aFrom} → **{aStatus}**　closed_at: {e.closed_at}");
            if (e.blocks.Count > 0)
            {
                ioR.AppendLine($"- ▶ 它本來卡著 {Ids(e.blocks)} —— 那幾張現在可能可以動了（去看一眼）：");
                foreach (int i in e.blocks)
                {
                    var b = UCL_TaskIO.Find(i);
                    if (b != null && !b.IsClosed())
                        ioR.AppendLine($"    · {b.Id} `{b.status}` {b.title}"
                            + $"　剩餘 blocker: {UCL_TaskIO.OpenBlockers(b).Count}");
                }
            }
            bool aNotified = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Status, iActor,
                $"{aFrom} → **{aStatus}**" + (aNote.Length == 0 ? "" : $"：{aNote}"), iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aNotified);
        }

        // ===========================================================
        // 區塊職責：commit 訊息的 `Fixes TASK-n` / `Refs TASK-n` 落地端（由 git_commit.py 呼叫）。
        // 物理意義：**狀態機只有一份**，住在這裡。python 端只負責 regex 抓單號與轉接 ——
        //          把「有 QA 就推 in_review、沒 QA 才 done」複製到另一個語言，
        //          就是兩份產線：兩邊都不報錯，而它們遲早各說各話（🩸 2026-08-21 一天五次同族）。
        // 數值影響：
        //   `mode=refs`  ⇒ 只追加 sha，不動狀態
        //   `mode=fixes` ⇒ 追加 sha 之後：
        //       · 未解 blocker 存在 ⇒ **不推進**（機械閘照樣生效，commit 不是特權通道）
        //       · 單上有 QA         ⇒ `in_review`（QA 沒簽，commit 不能替他簽）
        //       · 沒有 QA           ⇒ `done`
        // ⚠ 已關的單只追加 sha 並明說「它已經關了」—— 不要靜默重開，也不要假裝有推進。
        // ===========================================================
        async UniTask OpCommit(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aSha = GetArg(iArgs, "sha", "").Trim();
            if (aSha.Length == 0) throw new Exception("[Task] op=commit 需要 --arg sha=<commit SHA>");
            string aMode = Norm(GetArg(iArgs, "mode", "fixes"));
            if (aMode != "fixes" && aMode != "refs")
                throw new Exception($"[Task] op=commit 的 mode 只能是 fixes|refs（收到 '{aMode}'）");

            string aNow = UCL_TaskIO.NowUtc();
            bool aShaNew = !e.commit_shas.Contains(aSha);
            if (aShaNew) e.commit_shas.Add(aSha);

            var aFrom = e.status;
            string aVerdict;
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (e.IsClosed())
            {
                aVerdict = $"這張單已經是 `{aFrom}` ⇒ **只追加 sha，狀態不動**（不靜默重開）";
            }
            else if (aMode == "refs")
            {
                aVerdict = "`Refs` ⇒ 只追加 sha，狀態不動（這是 Refs 的定義，不是失敗）";
            }
            else if (aBlockers.Count > 0)
            {
                aVerdict = $"🛑 **不推進**：還有 {aBlockers.Count} 個未解 blocker —— {string.Join("；", aBlockers)}"
                    + "。commit 不是特權通道，機械閘照樣生效。";
            }
            else
            {
                var aQa = e.QaPersonas();
                if (aQa.Count > 0)
                {
                    e.status = UCL_TaskStatus.in_review;
                    aVerdict = $"→ **in_review**（單上有 QA：{string.Join(" / ", aQa)}"
                        + " —— commit 不能替 QA 簽名）";
                }
                else
                {
                    e.status = UCL_TaskStatus.done;
                    e.closed_at = aNow;
                    aVerdict = "→ **done**（這張單沒有指名 QA ⇒ 沒有人要驗，commit 直接結）";
                    // ⚠ 落差要出聲（basecamp 拍板 ③，TASK-0015）：
                    //   單上有 dev 以外的角色（pm / reviewer / design…）卻**沒有 qa** ⇒
                    //   「沒有人要驗」這個假設要攤在被影響的人面前，而不是靜默生效。
                    //   🩸 血證就是 TASK-0009 本身：basecamp 掛的是 pm，我的 commit 直接把它關了，
                    //     而她一整天都在驗我的交付。閘做對了它的事 —— 錯的是沒有人被告知。
                    //   ⛔ **警示不是擋**：擋會讓真正不需要 QA 的小單無法自動結，而那是設計要的。
                    var aNonDev = e.participants
                        .Where(p => p.role != UCL_TaskRole.dev)
                        .Select(p => $"{p.persona}({p.role})").Distinct().ToList();
                    if (aNonDev.Count > 0)
                        aVerdict += $"\n  ⚠ **本單沒有 QA 卻有其他角色：{string.Join("、", aNonDev)}**"
                            + " —— 若非預期請 reopen 並補 `op=assign --arg role=qa`"
                            + "（`pm` 不是 QA 閘：PM 排序、QA 簽名，混起來會讓「有人管」被讀成「有人驗」）";
                }
            }

            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`{e.status}`　commit `{aSha}`（{aMode}）by {iActor}"
                + (aShaNew ? "" : "（這個 sha 本來就在，沒重複加）"));

            ioR.AppendLine($"## {e.Id} ← commit `{aSha}`（mode=`{aMode}`）");
            // ♻ 重複 sha 要在**回傳檔**分形（TASK-0033 ①）——
            //   時間線那句一直有寫，但「有印」與「印在被讀的地方」是兩件事：
            //   🩸 探針 TASK-0031 同一顆 sha 打兩次，兩次回傳檔逐字相同。時間線那筆照留（稽核），這裡是補不是搬。
            if (!aShaNew)
                ioR.AppendLine($"- ♻ **這顆 sha 本來就在單上，這次呼叫沒有改變 `commit_shas`**（重複掛載，不是新進度）");
            ioR.AppendLine($"- 狀態: `{aFrom}` {(aFrom == e.status ? "（不變）" : $"→ `{e.status}`")}");
            ioR.AppendLine($"- 判定: {aVerdict}");
            ioR.AppendLine($"- commit_shas 回讀: {string.Join(" ", e.commit_shas)}"
                + (aShaNew ? "" : $"（{e.commit_shas.Count} 顆，本次 0 新增）"));
            if (e.status == UCL_TaskStatus.in_review)
            {
                var aQa = e.QaPersonas();
                ioR.AppendLine($"- ▶ 等 QA 結單：`run Task --arg op=resolve --arg index={e.index}"
                    + " --arg status=done --arg note=<驗收讀數> --arg confirm=1`");
                ioR.AppendLine($"  （要由 {string.Join(" / ", aQa)} 跑；別人跑要帶 `--arg qa_note=`）");
            }
            // ⚠ 只有**狀態真的變了**才通知：沒推進（refs / 被 blocker 擋 / 已關）時發通知
            //   等於用一則訊息說「什麼都沒發生」—— 那種訊息會訓練大家忽略這個 tag。
            if (aFrom != e.status)
            {
                bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Status, iActor,
                    $"{aFrom} → **{e.status}**（commit `{aSha}`）", iCallerArgs: iArgs);
                AppendNotifyLine(ioR, e, iActor, aOk);
            }
            else
            {
                ioR.AppendLine("- 📣 狀態沒有變 ⇒ **不發酒館通知**（一則說「什麼都沒發生」的訊息"
                    + "會訓練大家忽略這個 tag）");
            }
        }

        // ===========================================================
        // 區塊職責：收工（`op=wrapup`）—— **一個動作、兩個目的地**。
        //
        // 物理意義（Tim 2026-08-24 補的洞 ＋ basecamp 拍板，TASK-0019）：
        //   `resolve` 是「這件事做完了」，`wrapup` 是「**我今天不做了**」。
        //   混在一起會逼人在還沒做完時按結單、或在收工時什麼都不寫 ——
        //   而**只有 wrapup 是每天都可能發生的那一個**。
        //   ⇒ 所以它 **不改 status**（反向驗收：回讀確認狀態沒動）。
        //
        // 兩個目的地，**分流交給工具不交給人**：
        //   · `progress`（還剩什麼、下一步從哪接）⇒ **Task 留言**（進度真相源是 Task，Tim 拍板）
        //   · `why`（為什麼卡住／試過什麼不行／被否決的選項）⇒ **代跑 `work_memory.py`** 寫 pitfall/decision
        //   🩸 為什麼不讓人自己分：basecamp 的血證 —— 分流交給人＝全部倒進同一個地方
        //     （她今天把看板快照倒進了記憶，而那跟 `op=kanban` 記同一個量）。
        //
        // ⚠ 契約①：**C# 不自己寫記憶檔**。`why` 走 `UCL_TaskWorkMemoryCli`（代跑 python），
        //   而內文一律 `--body-file`（心得含引號／反引號／換行，那些在命令列上是地雷）。
        // ===========================================================
        async UniTask OpWrapup(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aProgress = GetArg(iArgs, "progress", "").Trim();
            string aWhy = GetArg(iArgs, "why", "").Trim();

            // `progress` 必填 —— 收工的意義就是它（沒有它的收工＝只是關掉視窗）
            if (aProgress.Length == 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine("- reason: `progress` 必填 —— **收工的意義就是「還剩什麼、下一步從哪接」**。");
                ioR.AppendLine("  沒有它的收工只是關掉視窗：單子還開著，而沒有人知道停在哪一步。");
                ioR.AppendLine("  用法：`--arg-file progress=<檔>`（走檔案，內文不經過命令列）");
                throw new Exception("[Task] wrapup 缺 progress");
            }

            // 給了 why 卻沒有 memory_topic ⇒ 擋，**不猜主題名**
            string aTopic = (e.memory_topic ?? "").Trim();
            if (aWhy.Length > 0 && aTopic.Length == 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 給了 `why` 但 {e.Id} 沒有 `memory_topic` —— **我不猜主題名**。");
                ioR.AppendLine($"  先設：`run Task --arg op=update --arg index={e.index}"
                    + " --arg memory_topic=<主題>`，或這次只寫 progress（why 留著下次）。");
                throw new Exception("[Task] wrapup: 有 why 但沒有 memory_topic");
            }

            string aNow = UCL_TaskIO.NowUtc();
            var aFrom = e.status;

            // ① progress → Task 留言（＋時間線一筆 `wrapup` 事件，供晚安閘判定「今天收工過了」）
            var aComment = new UCL_TaskComment
            {
                id = UCL_TaskIO.NextCommentId(e),
                persona = iActor,
                at = aNow,
                body = "**[收工 wrapup]**\n\n" + aProgress,
            };
            e.comments.Add(aComment);
            UCL_TaskIO.Touch(e, aNow);
            // ⚠ 等號陷阱（TASK-0036 驗收標準第三條）：`wrapup` 自己會 `Touch` ⇒ 這兩個欄位
            //   在寫完的當下**必然相等**。所以述詞②的判準必須是**嚴格大於**（`updated_at > last_wrapup_at`）——
            //   用 `>=` 的話「剛收完工」會被自己擋住，那是一隻修完立刻天天亮的警示。
            //   ⇒ 這裡刻意跟 `Touch` 共用同一個 `aNow`，讓「相等」是精確的而不是差幾毫秒。
            e.last_wrapup_at = aNow;
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`wrapup`　{iActor} 收工（狀態不動：{aFrom}）留言 #{aComment.id}");

            ioR.AppendLine($"## ✅ {e.Id} 已收工（`wrapup`）");
            ioR.AppendLine($"- 狀態：**維持 `{aFrom}`** —— 收工不是結單，也不是放棄");
            ioR.AppendLine($"- 進度寫進留言 #{aComment.id}（進度真相源是 Task）");
            ioR.AppendLine();
            ioR.AppendLine("```markdown");
            ioR.AppendLine(aProgress);
            ioR.AppendLine("```");

            // ② why → 代跑 work_memory.py（契約①：記憶側唯一寫入端是 python）
            if (aWhy.Length > 0)
            {
                string aType = Norm(GetArg(iArgs, "memory_type", "pitfall"));
                if (aType != "pitfall" && aType != "decision" && aType != "knowhow")
                    aType = "pitfall";
                string aId = $"{aType}_wrapup-{e.index:0000}-{DateTime.UtcNow:yyyyMMddHHmm}";
                string aTitle = $"收工紀錄 {e.Id}：{Trunc(e.title, 40)}";
                string aTmp = Path.Combine(Path.GetTempPath(),
                    $"ucl_wrapup_{e.index}_{DateTime.UtcNow:yyyyMMddHHmmss}.md");
                try
                {
                    File.WriteAllText(aTmp, aWhy, new UTF8Encoding(false));
                    // ⛔⛔ **這個 `await` 必須留在 `UCL_TaskIO.Save` 之後，不可以搬到前面。**
                    //   它內部是 `await Task.Run(...)`（`UCL_TaskWorkMemoryCli.cs:74`）——
                    //   **本檔唯一一個真的會離開主執行緒的地方**。
                    //   本單（TASK-0026）的併發安全完全依賴「read-modify-write 中間沒有 yield 點」，
                    //   而搬動這一句就會把 yield 點放進那個窗口裡。
                    //   🩸 症狀是**靜默的**：整檔覆蓋、留言消失、index 撞號 —— 沒有一格會紅。
                    //   ⚠ 唯一的告警是 `UCL_TaskIO.AssertMainThread`，而它只在**事情已經發生之後**才出聲。
                    //   （通則寫在 UCL_TaskIO 檔頭；這裡指名道姓，因為通則會被讀成建議。）
                    var (aOk, aOut, aDetail) = await UCL_TaskWorkMemoryCli.AddAsync(
                        aTopic, aType, aId, aTitle, aTmp, iActor);
                    ioR.AppendLine();
                    if (aOk)
                    {
                        ioR.AppendLine($"- 🧠 已寫進工作記憶：`{aTopic}` / `{aType}` / `{aId}`（代跑 work_memory.py）");
                        if (aOut.Length > 0) ioR.AppendLine($"    · 工具輸出：{Trunc(aOut, 200)}");
                    }
                    else
                    {
                        // ⚠ 大聲但不致命：進度已經落盤（那是主線），記憶那半沒寫成要看得見
                        ioR.AppendLine($"- ⚠ **記憶那半沒寫成**（{aDetail}）—— 進度已落盤，"
                            + "但「為什麼卡住」還沒有家。");
                        ioR.AppendLine($"    · 手動補：`python <UCL_Core>/Tools~/AgentCommands/work_memory.py add"
                            + $" --topic {aTopic} --type {aType} --id {aId} --title \"{aTitle}\" --body-file <檔> --by {iActor}`");
                    }
                }
                finally
                {
                    try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { }
                }
            }
            else
            {
                ioR.AppendLine();
                ioR.AppendLine("- 🧠 沒帶 `why` ⇒ 沒寫記憶（**這是合法的** —— 不強迫每天都有心得；"
                    + "為了通關而寫的記憶比沒有更糟，它佔著位置又看起來像有人整理過）");
            }

            bool aNotified = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Comment, iActor,
                "", "**[收工 wrapup]**\n\n" + aProgress, iArgs);
            AppendNotifyLine(ioR, e, iActor, aNotified);
        }

        // ===========================================================
        // 區塊職責：逾期認領的機械釋放 —— `in_progress` 且 ≥ STALE_DAYS 沒動 ⇒ 退回 `todo`。
        //
        // 物理意義：**認領會變成占位** —— persona 會下線、記憶會斷，明天的他不記得認領過，
        //   而看板上那張單看起來「有人在做」。告警是給人看的（`list` 早就印 stale 了），
        //   **釋放才是機械的**，而這一支就是那個機械。
        //
        // ⚠ 判準三條：
        //   ① 規則**純時間**、不含判斷（誰該做／做到哪 都不看）⇒ 可以自動，而且可重跑
        //   ② 仍要 `confirm=1`：它改的是別人的單，而「我只是想看看有哪些」與「動手」不得同形
        //   ③ **不在晚安 check 裡自動跑** —— 那一步的契約是唯讀起手，在那裡改狀態的話
        //      那一行沒有人會讀。晚安只印候選 ＋ 這道指令（見 UCL_TaskReconcile）。
        // 數值影響：每張被釋放的單寫一次檔 ＋ 一則酒館通知（狀態真的變了才發）。
        // ===========================================================
        async UniTask OpSweep(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var aNow = DateTime.UtcNow;
            string aOnly = GetArg(iArgs, "assignee", "").Trim();   // 空＝全部人
            var aCandidates = UCL_TaskIO.LoadAll().Where(e => !e.IsClosed()
                    && e.status == UCL_TaskStatus.in_progress
                    && e.DaysSinceUpdate(aNow) >= UCL_TaskIO.STALE_DAYS
                    && (aOnly.Length == 0 || e.RolesOf(aOnly).Count > 0)).ToList();

            ioR.AppendLine($"## sweep（逾期認領釋放 —— in_progress 且 ≥{UCL_TaskIO.STALE_DAYS} 天沒動）");
            ioR.AppendLine($"- 候選 **{aCandidates.Count}** 張"
                + (aOnly.Length == 0 ? "（全部人）" : $"（只看 {aOnly} 參與的）"));
            if (aCandidates.Count == 0)
            {
                ioR.AppendLine("- ✅ 沒有逾期認領 —— 這是「沒有候選」，不是「沒有掃」。");
                return;
            }
            foreach (var e in aCandidates)
                ioR.AppendLine($"    · {e.Id} `{e.status}` {e.title}"
                    + $"　{e.DaysSinceUpdate(aNow)} 天沒動　參與：{Participants(e)}");

            if (GetArg(iArgs, "confirm", "").Trim() != "1")
            {
                ioR.AppendLine("- 🛑 **dry-run**（沒帶 `confirm=1`）⇒ 一張都沒改。");
                ioR.AppendLine("  上面的候選清單是真的讀數；要釋放就重跑同一道指令加 `--arg confirm=1`。");
                return;
            }

            int aDone = 0;
            foreach (var e in aCandidates)
            {
                string aTs = UCL_TaskIO.NowUtc();
                int aDays = e.DaysSinceUpdate(aNow);
                var aFrom = e.status;
                e.status = UCL_TaskStatus.todo;
                UCL_TaskIO.Touch(e, aTs);
                // ⚠ 時間線一定要留一行說**為什麼**被釋放 ——
                //   沒有這行的話，明天看到它從 in_progress 變回 todo 會像有人手動改的
                // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
                UCL_TaskIO.Save(e, "", "", $"{aTs}　`todo`　sweep 釋放（{aFrom} 已 {aDays} 天沒動作，"
                    + $"逾期 {UCL_TaskIO.STALE_DAYS} 天門檻）by {iActor}");
                bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Status, iActor,
                    $"{aFrom} → **todo**（sweep：認領後 {aDays} 天沒動，釋放回待領）", iCallerArgs: iArgs);
                AppendNotifyLine(ioR, e, iActor, aOk);
                aDone++;
            }
            ioR.AppendLine($"- ✅ 已釋放 **{aDone}** 張回 `todo`（每張的時間線都留了釋放理由）");
            ioR.AppendLine("- ⚠ 釋放**不代表那件事不必做** —— 它只是把「有人在做」這個假讀數收回來。");
        }

        // ===========================================================
        // 區塊職責：移除一位參與者。
        // 物理意義：`assign` 的反向動作。沒有它的話，指派錯了只能留著 ——
        //          而一個掛著錯誤名字的角色比空著更糟（有人以為那格有人在守）。
        // 🩸 這個 op 是**用出來的**：我拿 gura 當 QA 閘的測試對象，驗完之後發現沒有路可以拆掉它。
        // ===========================================================
        void OpUnassign(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aTarget = GetArg(iArgs, "target_persona", "").Trim();
            if (aTarget.Length == 0) throw new Exception("[Task] op=unassign 需要 --arg target_persona=<誰>");
            bool aHasRole = GetArg(iArgs, "role", "").Trim().Length > 0;   // 沒帶＝該 persona 的所有角色
            var aRole = aHasRole ? ParseEnumArg(iArgs, "role", UCL_TaskRole.dev) : default;

            int aBefore = e.participants.Count;
            e.participants.RemoveAll(p =>
                string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase)
                && (!aHasRole || p.role == aRole));
            int aRemoved = aBefore - e.participants.Count;

            if (aRemoved == 0)
            {
                ioR.AppendLine($"## {e.Id} 沒有變更");
                ioR.AppendLine($"- {aTarget}"
                    + (!aHasRole ? "" : $"（role={aRole}）")
                    + " 不在參與者裡 ⇒ **什麼都沒寫**（這是「找不到」，不是「移除成功」）");
                ioR.AppendLine($"- 現有參與：{Participants(e)}");
                return;
            }
            string aNow = UCL_TaskIO.NowUtc();
            UCL_TaskIO.Touch(e, aNow);
            // ⛔ [RMW-END] 從本 Op 取得 `e` 到這一行之間**不得出現 `await`** —— 併發安全靠這個（見 UCL_TaskIO 檔頭），破了是靜默的。
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`unassign`　{iActor} 移除 {aTarget}"
                + (!aHasRole ? "（全部角色）" : $"（role={aRole}）") + $"　共 {aRemoved} 筆");
            ioR.AppendLine($"## ✅ {e.Id} 已移除 {aRemoved} 筆參與");
            ioR.AppendLine($"- 移除：{aTarget}{(!aHasRole ? "（全部角色）" : $"（{aRole}）")}");
            ioR.AppendLine($"- 現有參與：{Participants(e)}");
            if (e.QaPersonas().Count == 0)
                ioR.AppendLine("- ⚠ 這張單**現在沒有 QA** ⇒ `resolve` 沒有閘會擋，結單由開單人或 PM 做。");
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
            if (!int.TryParse(GetArg(iArgs, "index", "").Trim(), out oIndex) || oIndex <= 0)
                throw new Exception("[Task] 這個 op 需要 --arg index=<單號>");
            var e = UCL_TaskIO.Find(oIndex);
            if (e == null)
                throw new Exception($"[Task] TASK-{oIndex} 不存在（單檔：{UCL_TaskIO.TaskPath(oIndex)}）"
                    + " —— 「查不到」不等於「已經關掉」，先 `op=list --arg status=all` 看一眼");
            return e;
        }

        static bool AddParticipant(UCL_TaskEntry e, string iPersona, UCL_TaskRole iRole, string iNow)
        {
            foreach (var p in e.participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase)
                    && p.role == iRole) return false;
            e.participants.Add(new UCL_TaskParticipant
            { persona = iPersona, role = iRole, assigned_at = iNow });
            return true;
        }

        static string Participants(UCL_TaskEntry e)
        {
            if (e.participants.Count == 0) return "**無**（沒有人在做這件事）";
            return string.Join("、", e.participants.Select(p => $"{p.persona}({p.role})"));
        }

        /// <summary>一張單的**全部**關係欄位（含 epic_id / subtask_indices）—— 回讀用，別漏欄位。</summary>
        static string RelationLine(UCL_TaskEntry e)
            => $"blocked_by={Ids(e.blocked_by)} blocks={Ids(e.blocks)} related_to={Ids(e.related_to)}"
             + $" epic_id={(e.epic_id.Length == 0 ? "—" : e.epic_id)} subtasks={Ids(e.subtask_indices)}";

        static string Trunc(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
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

        static List<string> SplitList(string iRaw)
        {
            var aOut = new List<string>();
            foreach (var p in (iRaw ?? "").Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0) aOut.Add(t);
            }
            return aOut;
        }
    }
}
#endif
