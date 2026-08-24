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
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public class Cmd_Task : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Task";

        public override string ShortDescription =>
            "跨 agent 任務管理：create/list/show/claim/assign/unassign/update/comment/link/resolve/commit/kanban。"
            + " 一單一檔；跨人承諾建 Task，個人自律留見叢。";

        public override string ArgsSchema =>
            "op=create|list|show|claim|assign|unassign|update|comment|link|resolve|commit|kanban（預設 list） | " +
            "sha=<commit SHA，op=commit 必填> | mode=fixes|refs（op=commit 用，預設 fixes） | " +
            "title=<標題，create 必填> | criteria=<驗收標準，create 必填> | description= | " +
            "type=feature|improvement|refactor|spike|subtask（預設 feature） | " +
            "priority=urgent|high|normal|low（預設 normal） | " +
            "status=<create/update 設定值；list 篩選：open（預設）/all/backlog/todo/in_progress/in_review/done/cancelled> | " +
            "index=<單號：show/claim/assign/update/comment/link/resolve 必填> | " +
            "role=dev|design|qa|pm|reviewer|sound|art（claim/assign 用，預設 dev） | " +
            "target_persona=<assign 的對象> | assignee=<list 篩選：只看某人參與的單> | " +
            "body=<comment 內容> | " +
            "op_link=blocked_by|blocks|related_to（link 用） | target=<link 的對方單號> | " +
            "note=<resolve 的結單說明> | qa_note=<代 QA 結單時的驗收紀錄> | " +
            "milestone= | epic_id= | tags=<逗號分隔> | confirm=1（resolve 必帶）";

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
                    case "create": OpCreate(args, aActor, aR); break;
                    case "list": OpList(args, aR); break;
                    case "show": OpShow(args, aR); break;
                    case "claim": OpClaim(args, aActor, aR); break;
                    case "assign": OpAssign(args, aActor, aR); break;
                    case "unassign": OpUnassign(args, aActor, aR); break;
                    case "update": OpUpdate(args, aActor, aR); break;
                    case "comment": OpComment(args, aActor, aR); break;
                    case "link": OpLink(args, aActor, aR); break;
                    case "resolve": OpResolve(args, aActor, aR); break;
                    case "commit": OpCommit(args, aActor, aR); break;
                    case "kanban": OpKanban(aR); break;
                    default:
                        throw new Exception($"[Task] 認不得的 op='{aOp}'"
                            + "（create|list|show|claim|assign|unassign|update|comment|link|resolve|commit|kanban）");
                }
            }
            finally
            {
                UCL_TaskIO.EnsureDir();
                File.WriteAllText(UCL_TaskIO.LastReportPath, aR.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Task] op={aOp} → {UCL_TaskIO.LastReportPath}");
            }
        }

        // ===========================================================
        // 區塊職責：開新單。
        // 物理意義：`criteria`（驗收標準）是**必填**，不是建議 —— RFC §2④。
        //   沒有驗收條件的單，QA 只能憑感覺說「看起來可以」，而那不是驗收。
        //   （Sirius 教的：交件的通過條件比結論值錢。）
        // 數值影響：配一個 index、寫一份 tasks/<index>.md。
        // ===========================================================
        void OpCreate(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
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
                type = Norm(GetArg(iArgs, "type", "feature")),
                priority = Norm(GetArg(iArgs, "priority", "normal")),
                status = Norm(GetArg(iArgs, "status", "todo")),
                title = aTitle,
                reporter = iActor,
                milestone = GetArg(iArgs, "milestone", "").Trim(),
                epic_id = GetArg(iArgs, "epic_id", "").Trim(),
                created_at = aNow,
                updated_at = aNow,
            };
            foreach (var t in SplitList(GetArg(iArgs, "tags", ""))) e.tags.Add(t);

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
            ioR.AppendLine($"- 做完 commit 訊息帶 `Fixes {e.Id}`（P1 上線後自動推進狀態；目前仍需手動 resolve）");
        }

        // ===========================================================
        // 區塊職責：清單。預設只印**還沒關的**（open）——
        //   已關的單混進來會讓「還有多少沒做」這個數字失去意義。
        // ===========================================================
        void OpList(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            string aFilter = Norm(GetArg(iArgs, "status", "open"));
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
                if (aFilter == "all") return true;
                if (aFilter == "open") return !e.IsClosed();
                return string.Equals(e.status, aFilter, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (aAssignee.Length > 0)
                aList = aList.Where(e => e.RolesOf(aAssignee).Count > 0
                    || string.Equals(e.reporter, aAssignee, StringComparison.OrdinalIgnoreCase)).ToList();
            if (aMilestone.Length > 0)
                aList = aList.Where(e => string.Equals(e.milestone, aMilestone, StringComparison.OrdinalIgnoreCase)).ToList();

            ioR.AppendLine($"## list（filter=`{aFilter}`"
                + (aAssignee.Length == 0 ? "" : $"　assignee=`{aAssignee}`")
                + (aMilestone.Length == 0 ? "" : $"　milestone=`{aMilestone}`")
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

        void OpShow(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aPath = UCL_TaskIO.TaskPath(aIndex);
            ioR.AppendLine($"## {e.Id} — {e.title}");
            ioR.AppendLine($"- `{e.type}` / `{e.priority}` / `{e.status}`　開單：{e.reporter}");
            ioR.AppendLine($"- 參與：{Participants(e)}");
            ioR.AppendLine($"- blocked_by: {Ids(e.blocked_by)}　blocks: {Ids(e.blocks)}　related_to: {Ids(e.related_to)}");
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aBlockers.Count > 0) ioR.AppendLine($"- 🛑 **未解 blocker**：{string.Join("；", aBlockers)}");
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
        // 區塊職責：認領 —— 自己把自己加進參與者並推到 in_progress。
        // ⚠ 認領會變成**占位**，除非釋放是機械的（persona 會下線、記憶會斷，
        //   明天的我可能不記得認領過）。逾期自動釋放排在 P2（晚安對帳那一步），
        //   目前先讓 `list` 把 stale 印出來 —— **告警是給人看的，釋放才是機械的**，
        //   所以這裡不假裝那一格已經有守衛。
        // ===========================================================
        void OpClaim(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aRole = Norm(GetArg(iArgs, "role", "dev"));
            string aNow = UCL_TaskIO.NowUtc();
            bool aNew = AddParticipant(e, iActor, aRole, aNow);
            string aFrom = e.status;
            e.status = "in_progress";
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`in_progress`　{iActor} 認領（role={aRole}，原狀態 {aFrom}）");

            ioR.AppendLine($"## ✅ {e.Id} 已認領");
            ioR.AppendLine($"- {aFrom} → **in_progress**　role=`{aRole}`"
                + (aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）"));
            ioR.AppendLine($"- 參與：{Participants(e)}");
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aBlockers.Count > 0)
            {
                ioR.AppendLine($"- 🛑 **注意：這張單有未解 blocker** —— {string.Join("；", aBlockers)}");
                ioR.AppendLine("  認領不擋（也許妳就是要去解它），但 `resolve` 會擋。");
            }
        }

        void OpAssign(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aTarget = GetArg(iArgs, "target_persona", "").Trim();
            if (aTarget.Length == 0) throw new Exception("[Task] op=assign 需要 --arg target_persona=<誰>");
            string aRole = Norm(GetArg(iArgs, "role", "dev"));
            string aNow = UCL_TaskIO.NowUtc();
            bool aNew = AddParticipant(e, aTarget, aRole, aNow);
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`assign`　{iActor} 指派 {aTarget} 為 {aRole}");

            ioR.AppendLine($"## ✅ {e.Id} 參與者已更新");
            ioR.AppendLine($"- {(aNew ? "新增" : "已存在，未重複加")}：{aTarget}（{aRole}）");
            ioR.AppendLine($"- 參與：{Participants(e)}");
            ioR.AppendLine("- ⚠ **指派本身不會通知對方** —— 通知由 Tim 在後台做（2026-08-24 拍板）。");
            ioR.AppendLine($"  被指派的人若沒在見叢寫一行 `[{e.Id}]`，他的早安 brief 不會提這張單。");
        }

        void OpUpdate(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aNow = UCL_TaskIO.NowUtc();
            var aChanges = new List<string>();

            string aStatus = GetArg(iArgs, "status", "").Trim();
            if (aStatus.Length > 0)
            {
                string aNorm = Norm(aStatus);
                // ⛔ 結單只能走 resolve —— 那條路上有 blocker 與 QA 兩道閘。
                //    留一個「用 update 也能推 done」的旁路等於那兩道閘不存在。
                if (aNorm == "done" || aNorm == "cancelled")
                    throw new Exception("[Task] 結單請走 `op=resolve`（那條路上有 blocker 與 QA 兩道閘，"
                        + "而 update 沒有）。這不是麻煩，是刻意不留旁路。");
                aChanges.Add($"status {e.status} → {aNorm}");
                e.status = aNorm;
            }
            string aPriority = GetArg(iArgs, "priority", "").Trim();
            if (aPriority.Length > 0) { aChanges.Add($"priority {e.priority} → {Norm(aPriority)}"); e.priority = Norm(aPriority); }
            string aTitle = GetArg(iArgs, "title", "").Trim();
            if (aTitle.Length > 0) { aChanges.Add("title 改寫"); e.title = aTitle; }
            string aMilestone = GetArg(iArgs, "milestone", "").Trim();
            if (aMilestone.Length > 0) { aChanges.Add($"milestone → {aMilestone}"); e.milestone = aMilestone; }

            if (aChanges.Count == 0)
            {
                ioR.AppendLine($"## {e.Id} 沒有任何變更");
                ioR.AppendLine("- 沒給任何可更新的欄位（status / priority / title / milestone）⇒ **什麼都沒寫**。");
                return;
            }
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, GetArg(iArgs, "criteria", ""), GetArg(iArgs, "description", ""),
                $"{aNow}　`update`　{iActor}：{string.Join("／", aChanges)}");
            ioR.AppendLine($"## ✅ {e.Id} 已更新");
            foreach (var c in aChanges) ioR.AppendLine($"- {c}");
        }

        void OpComment(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aBody = GetArg(iArgs, "body", "").Trim();
            if (aBody.Length == 0) throw new Exception("[Task] op=comment 需要 --arg body=<內容>");
            string aNow = UCL_TaskIO.NowUtc();
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　[{iActor}] {aBody.Replace("\r", " ").Replace("\n", " ")}");
            ioR.AppendLine($"## ✅ {e.Id} 已追加一則時間線");
            ioR.AppendLine($"- [{iActor}] {aBody}");
            ioR.AppendLine("- ⚠ 留言**會推進 `updated_at`** ⇒ 它會讓 stale 計時歸零。");
            ioR.AppendLine("  所以「留言說我還在做」跟「真的有做」在 stale 讀數上長得一樣 —— 這是這個讀數的邊界。");
        }

        void OpLink(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            Require(iArgs, out int aIndex);
            string aKind = Norm(GetArg(iArgs, "op_link", "blocked_by"));
            if (!int.TryParse(GetArg(iArgs, "target", "").Trim(), out int aTarget))
                throw new Exception("[Task] op=link 需要 --arg target=<對方單號>");

            bool aChanged = UCL_TaskIO.Link(aIndex, aTarget, aKind, iActor, out string aErr);
            if (aErr.Length > 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: {aErr}");
                throw new Exception($"[Task] link 失敗：{aErr}");
            }
            var a = UCL_TaskIO.Find(aIndex);
            var b = UCL_TaskIO.Find(aTarget);
            ioR.AppendLine($"## {(aChanged ? "✅ 已建立關聯" : "（關聯本來就存在，沒有重複寫）")}");
            ioR.AppendLine($"- `{aKind}`：{a.Id} ↔ {b.Id}");
            ioR.AppendLine("- 回讀（**雙向都要有，單向寫入是靜默錯**）:");
            ioR.AppendLine($"    · {a.Id}: blocked_by={Ids(a.blocked_by)} blocks={Ids(a.blocks)} related_to={Ids(a.related_to)}");
            ioR.AppendLine($"    · {b.Id}: blocked_by={Ids(b.blocked_by)} blocks={Ids(b.blocks)} related_to={Ids(b.related_to)}");
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
        void OpResolve(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aStatus = Norm(GetArg(iArgs, "status", "done"));
            if (aStatus != "done" && aStatus != "cancelled")
                throw new Exception($"[Task] resolve 的 status 只能是 done|cancelled（收到 '{aStatus}'）");
            string aNote = GetArg(iArgs, "note", "").Trim();
            string aQaNote = GetArg(iArgs, "qa_note", "").Trim();

            ioR.AppendLine($"## resolve 前的閘（{e.Id} `{e.status}` → `{aStatus}`）");

            // ① blocker 閘 —— cancelled 不受此限（取消一張被卡住的單是合理的）
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aStatus == "done" && aBlockers.Count > 0)
            {
                ioR.AppendLine($"- 🛑 **擋下**：還有 {aBlockers.Count} 個未解 blocker —— {string.Join("；", aBlockers)}");
                ioR.AppendLine("  這是機械攔截：blocker 沒解而推 Done，等於宣告一件還做不到的事已經完成。");
                throw new Exception($"[Task] resolve 擋下：{e.Id} 還有 {aBlockers.Count} 個未解 blocker");
            }
            ioR.AppendLine(aBlockers.Count == 0 ? "- ✅ blocker 閘：沒有未解的 blocker"
                : $"- ⚠ blocker 閘：有 {aBlockers.Count} 個未解，但 status=cancelled ⇒ 放行（取消一張被卡的單是合理的）");

            // ② QA 閘
            string aQaBlock = UCL_TaskIO.QaGateBlocked(e, iActor, aQaNote);
            if (aStatus == "done" && aQaBlock != null)
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
            string aFrom = e.status;
            e.status = aStatus;
            e.closed_at = aNow;
            if (aNote.Length > 0) e.resolution_note = aNote;
            if (aQaNote.Length > 0)
                e.resolution_note = (e.resolution_note + "\n\n**QA 代簽紀錄**：" + aQaNote).Trim();
            UCL_TaskIO.Touch(e, aNow);
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
        void OpCommit(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
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

            string aFrom = e.status;
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
                    e.status = "in_review";
                    aVerdict = $"→ **in_review**（單上有 QA：{string.Join(" / ", aQa)}"
                        + " —— commit 不能替 QA 簽名）";
                }
                else
                {
                    e.status = "done";
                    e.closed_at = aNow;
                    aVerdict = "→ **done**（這張單沒有指名 QA ⇒ 沒有人要驗，commit 直接結）";
                }
            }

            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`{e.status}`　commit `{aSha}`（{aMode}）by {iActor}"
                + (aShaNew ? "" : "（這個 sha 本來就在，沒重複加）"));

            ioR.AppendLine($"## {e.Id} ← commit `{aSha}`（mode=`{aMode}`）");
            ioR.AppendLine($"- 狀態: `{aFrom}` {(aFrom == e.status ? "（不變）" : $"→ `{e.status}`")}");
            ioR.AppendLine($"- 判定: {aVerdict}");
            ioR.AppendLine($"- commit_shas 回讀: {string.Join(" ", e.commit_shas)}");
            if (e.status == "in_review")
            {
                var aQa = e.QaPersonas();
                ioR.AppendLine($"- ▶ 等 QA 結單：`run Task --arg op=resolve --arg index={e.index}"
                    + " --arg status=done --arg note=<驗收讀數> --arg confirm=1`");
                ioR.AppendLine($"  （要由 {string.Join(" / ", aQa)} 跑；別人跑要帶 `--arg qa_note=`）");
            }
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
            string aRole = GetArg(iArgs, "role", "").Trim();   // 空＝該 persona 的所有角色

            int aBefore = e.participants.Count;
            e.participants.RemoveAll(p =>
                string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase)
                && (aRole.Length == 0 || string.Equals(p.role, Norm(aRole), StringComparison.OrdinalIgnoreCase)));
            int aRemoved = aBefore - e.participants.Count;

            if (aRemoved == 0)
            {
                ioR.AppendLine($"## {e.Id} 沒有變更");
                ioR.AppendLine($"- {aTarget}"
                    + (aRole.Length == 0 ? "" : $"（role={Norm(aRole)}）")
                    + " 不在參與者裡 ⇒ **什麼都沒寫**（這是「找不到」，不是「移除成功」）");
                ioR.AppendLine($"- 現有參與：{Participants(e)}");
                return;
            }
            string aNow = UCL_TaskIO.NowUtc();
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`unassign`　{iActor} 移除 {aTarget}"
                + (aRole.Length == 0 ? "（全部角色）" : $"（role={Norm(aRole)}）") + $"　共 {aRemoved} 筆");
            ioR.AppendLine($"## ✅ {e.Id} 已移除 {aRemoved} 筆參與");
            ioR.AppendLine($"- 移除：{aTarget}{(aRole.Length == 0 ? "（全部角色）" : $"（{Norm(aRole)}）")}");
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
            string[] aCols = { "backlog", "todo", "in_progress", "in_review", "done", "cancelled" };
            UCL_TaskIO.CountStats(out int aOpen, out int aStale, out int aBroken, out int aBlocked);
            ioR.AppendLine($"## kanban —— 總 **{aAll.Count}** 張／未關 **{aOpen}**／被阻塞 **{aBlocked}**"
                + $"／stale **{aStale}**" + (aBroken > 0 ? $"／時戳壞掉 **{aBroken}**" : ""));
            ioR.AppendLine();
            foreach (var aCol in aCols)
            {
                var aIn = aAll.Where(e => string.Equals(e.status, aCol, StringComparison.OrdinalIgnoreCase)).ToList();
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

        static bool AddParticipant(UCL_TaskEntry e, string iPersona, string iRole, string iNow)
        {
            foreach (var p in e.participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.role, iRole, StringComparison.OrdinalIgnoreCase)) return false;
            e.participants.Add(new UCL_TaskParticipant
            { persona = iPersona, role = iRole, assigned_at = iNow });
            return true;
        }

        static string Participants(UCL_TaskEntry e)
        {
            if (e.participants.Count == 0) return "**無**（沒有人在做這件事）";
            return string.Join("、", e.participants.Select(p => $"{p.persona}({p.role})"));
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
