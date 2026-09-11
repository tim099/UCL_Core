// 區塊職責：任務系統的 Cmd 入口（create / list / show / claim / assign / update / comment / link / resolve / kanban）。
// 物理意義：跨 agent 的**交付承諾**通道。與見叢（`_keys_open.md`）分工，判準是一句當下答得出來的話：
//          **「這件事跟專案有關嗎？」** 有 ⇒ 這裡（Tim 2026-09-07 拍板）；
//          純個人代辦（我的自律、我為什麼又拖了）⇒ 見叢。⛔ **不再兩邊都留**。
// 數值影響：寫 Tasks/ 底下兩種檔（tasks/<index>.md 一單一檔 / 回傳檔）；不動 Treasury、不發酒館訊息。
// 設計沿革：Plan_Task_Management_System.md（gura 撰寫 / Tim 2026-08-24 拍板）。
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
            "confirm=1（resolve 必帶）";

        public override string ExampleArgs =>
            "op=create;title=Cmd_Task 接上 Fixes TASK-n 閉環;criteria=- [ ] git_commit.py 實跑一次並讀回狀態;priority=high";

        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Task_Management_Workflow.md";

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
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                // 無必填：list / sweep / kanban（handler 內零 reject ⇒ 不宣告，比照 Cmd_Tavern 的 leave）
                ["list"] = new UCL_CmdOpSpec(),
                ["sweep"] = new UCL_CmdOpSpec(),
                ["kanban"] = new UCL_CmdOpSpec(),

                // create：`title` 是無條件必填（:190 那個 throw 的成員之一）。
                ["create"] = new UCL_CmdOpSpec { Required = new[] { "title" } },

                // 以下十個都走 `Require(iArgs, out int aIndex)`（:1291）⇒ 無 index 必 throw。
                ["show"] = new UCL_CmdOpSpec { Required = new[] { "index" } },
                ["claim"] = new UCL_CmdOpSpec { Required = new[] { "index" } },
                ["update"] = new UCL_CmdOpSpec { Required = new[] { "index" } },
                ["resolve"] = new UCL_CmdOpSpec { Required = new[] { "index" } },
                ["assign"] = new UCL_CmdOpSpec { Required = new[] { "index", "target_persona" } },
                ["unassign"] = new UCL_CmdOpSpec { Required = new[] { "index", "target_persona" } },
                ["comment"] = new UCL_CmdOpSpec { Required = new[] { "index", "body" } },
                // ⛔ `criteria_index` 刻意**不**列 Required —— 不帶它是合法呼叫（dry-run 印未勾清單）。
                ["check"] = new UCL_CmdOpSpec { Required = new[] { "index" } },
                ["link"] = new UCL_CmdOpSpec { Required = new[] { "index", "target" } },
                ["commit"] = new UCL_CmdOpSpec { Required = new[] { "index", "sha" } },
                ["wrapup"] = new UCL_CmdOpSpec { Required = new[] { "index", "progress" } },
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
                    case "create": await OpCreate(args, aActor, aR); break;
                    case "list": OpList(args, aR); break;
                    case "show": OpShow(args, aActor, aR); break;
                    case "claim": await OpClaim(args, aActor, aR); break;
                    case "assign": await OpAssign(args, aActor, aR); break;
                    case "unassign": OpUnassign(args, aActor, aR); break;
                    case "update": OpUpdate(args, aActor, aR); break;
                    case "comment": await OpComment(args, aActor, aR); break;
                    case "check": OpCheck(args, aActor, aR); break;
                    case "link": OpLink(args, aActor, aR); break;
                    case "resolve": await OpResolve(args, aActor, aR); break;
                    case "commit": await OpCommit(args, aActor, aR); break;
                    case "sweep": await OpSweep(args, aActor, aR); break;
                    case "wrapup": await OpWrapup(args, aActor, aR); break;
                    case "kanban": OpKanban(aR); break;
                    default:
                        throw new Exception($"[Task] 認不得的 op='{aOp}'"
                            + "（create|list|show|claim|assign|unassign|update|comment|check|link|resolve|commit|sweep|wrapup|kanban）");
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
                    + "派遣 client 會直接印出「📄 回傳檔：<路徑>」，照那一行讀，不要背路徑。\n",
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

            // ===========================================================
            // 區塊職責：必填閘依 type 分流（TASK-0086）。
            // 物理意義：bug 單的可驗性來自 evidence（硬證＋出處），criteria 由三段骨架自帶
            //   ⇒ evidence 必填、criteria 可省；其餘 type 照舊 criteria 必填。
            //   繼承自 BugReport：**守則靠人記得，欄位靠 schema 擋。**
            // ===========================================================
            string aEvidence = GetArg(iArgs, "evidence", "").Trim();
            var aType = ParseEnumArg(iArgs, "type", UCL_TaskType.feature);
            if (aType == UCL_TaskType.all)
                throw new Exception("[Task] type=`all` 是篩選用的成員，不是任務種類");
            var aMissing = new List<string>();
            if (string.IsNullOrWhiteSpace(aTitle)) aMissing.Add("title");
            if (aType == UCL_TaskType.bug)
            {
                if (aEvidence.Length == 0) aMissing.Add("evidence");
            }
            else if (string.IsNullOrWhiteSpace(aCriteria)) aMissing.Add("criteria");
            if (aMissing.Count > 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 缺必填欄位：{string.Join(" / ", aMissing)}");
                if (aMissing.Contains("evidence"))
                {
                    ioR.AppendLine("- `evidence` 要放**感官騙不了的硬證**（error code／log 行號／重現指令／round-trip diff），");
                    ioR.AppendLine("  並寫明**這個讀數是怎麼拿到的** —— 閘擋得住「沒證據」，出處才擋得住「假證據」。重述現象不算。");
                    ioR.AppendLine("  用法：`--arg-file evidence=<檔>`（走檔案，內文不經過命令列）");
                    ioR.AppendLine("- 📣 **不確定算不算，就報** —— 判斷夠不夠格開單的成本，比誤開一張高；");
                    ioR.AppendLine("  提示缺漏／流程摩擦這類拿不出硬證的，改 `--arg type=improvement --arg tags=friction`（或 `suggestion`）照樣開。");
                }
                else
                {
                    ioR.AppendLine("- `criteria` 要寫**可以被客觀量測**的條件（QA 有權以「這條驗不了」退回）。");
                    ioR.AppendLine("  例：`- [ ] run_cmd op=link 之後兩張單的 blocked_by/blocks 各自讀回有對方`");
                }
                throw new Exception($"[Task] create 缺必填：{string.Join(",", aMissing)}");
            }

            string aNow = UCL_TaskIO.NowUtc();
            // ⭐ TASK-0163：**配號與落檔在同一把鎖內**（`UCL_TaskIO.Create`）。
            //   🩸 舊路是「先 `IncrementAndGetIndex()` 配號、離開那段之後才 `Save`」——
            //   而配號自己就是一段 RMW（讀計數檔 → +1 → 寫回）⇒ 兩條 lane 同時開單會**配到同一個號**，
            //   接著兩次整檔重寫，第二張把第一張**整份覆蓋**，而兩邊都回 Success、都印得出自己的單號。
            //   ⇒ 這裡把整段建構搬進 lambda：號碼由入口在鎖內發，`e` 由它落盤。
            //   ⚠ 內文兩區（criteria／description）走 `UCL_TaskWrite.Body` —— 開單是唯一必須寫它們的時機
            //   （其餘 op 給空字串＝沿用磁碟那一份）。
            UCL_TaskEntry e = null;
            string aDescription = null;
            int aNewIndex = UCL_TaskIO.Create(aIdx =>
            {
            e = new UCL_TaskEntry
            {
                index = aIdx,
                type = aType,
                priority = ParseEnumArg(iArgs, "priority", UCL_TaskPriority.normal),
                // 傷害形狀（TASK-0086）：bug 單沒給就沿 BugReport 舊預設 wrong，其餘 none（未標注）
                severity = ParseEnumArg(iArgs, "severity",
                    aType == UCL_TaskType.bug ? UCL_TaskSeverity.wrong : UCL_TaskSeverity.none),
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

            // evidence 落進任務描述的固定子區 —— 描述整段由 Save/ReadSection 原樣保存，免動檔案 schema
            aDescription = GetArg(iArgs, "description", "").Trim();
            if (aEvidence.Length > 0)
                aDescription = (aDescription.Length == 0 ? "" : aDescription + "\n\n")
                    + "### 🔬 證據（開單時附；含「讀數怎麼拿到的」）\n\n" + aEvidence;
            // bug 單 criteria 三段骨架（TASK-0086）：開單人只負責 ①（＝evidence），②③ 骨架自帶
            if (aType == UCL_TaskType.bug)
            {
                // ⚠ 骨架是**兩段**，⛔ 不再自帶「異源複驗」那一格（Tim 2026-09-08 拍板）。
                //   理由是規模：本專案 3~5 人，**問題多半在使用過程中被發現、然後直接修**——
                //   那條迴圈比「每張單都欠一次複驗」實際得多，而後者在人手不足時只會累積空白格。
                //   🩸 兩次實測：TASK-0149 ④ 與 TASK-0170 ③ 被加嚴成「由另一個人跑」，
                //   兩張各留一格**結構上簽不掉**的空白 —— 而當天兩張都已經有第二條路徑的讀數
                //   （0170：curl 對 python urllib，兩個獨立客戶端連 mid 都一致）。
                //   ⇒ **一個做不到的驗收條件跟一個沒有驗收條件，在看板上長得一樣。**
                // 📌 需要第二條路徑的單（尤其「錯了會給假讀數」那種）**自己加一行**，
                //   最便宜的是反向對照（不給 confirm ⇒ 一個位元組都沒寫）。
                //   ⛔ 但那是開單人的判斷，不是骨架替所有人先決定。
                string aSkeleton = "- [ ] ① 重現讀數：見「任務描述 › 🔬 證據」（讀數＋怎麼拿到的）\n"
                    + $"- [ ] ② 修正落盤（commit 帶 `Fixes {e.Id}`）";
                aCriteria = aCriteria.Length == 0 ? aSkeleton : aCriteria.TrimEnd() + "\n" + aSkeleton;
            }

            // ⛔ 舊的 `[RMW-END]` 前哨在此退場 —— 跨度現在由型別決定（`e` 由入口在鎖內落盤）。
            return (e, UCL_TaskWrite.Body($"{aNow}　`{e.status}`　由 {iActor} 開單", aCriteria, aDescription));
            });
            if (aNewIndex < 0 || e == null)
                throw new Exception("[Task] op=create 沒有落檔 —— 配號成功但建構回了 null，"
                    + "⇒ **一個位元組都沒寫**（號碼已消耗，下一張單會跳號，那是刻意的：號碼不回收）。");

            ioR.AppendLine($"## ✅ 已建單 **{e.Id}**");
            ioR.AppendLine($"- `{e.type}` / "
                + (e.severity == UCL_TaskSeverity.none ? "" : $"`{e.severity}` / ")
                + $"`{e.priority}` / `{e.status}`　開單：{e.reporter}");
            ioR.AppendLine($"- title: {e.title}");
            ioR.AppendLine($"- 單檔：`{UCL_TaskIO.TaskPath(e.index)}`");
            AppendSimilar(ioR, e);
            ioR.AppendLine();
            ioR.AppendLine("## ⚠ 這張單現在沒有任何參與者");
            ioR.AppendLine("- 指派走後台頁或 `op=assign`（Tim 2026-08-24：指派與通知由 Tim 在後台管理）。");
            ioR.AppendLine("- **沒有指名 QA 的單，結單由開單人或 PM 做** —— 那不是預設值，是一個選擇；");
            ioR.AppendLine("  要有人驗就 `op=assign --arg role=qa`，`resolve` 才會有閘門擋。");
            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步");
            ioR.AppendLine($"- 認領 → `run Task --arg op=claim --arg index={e.index} --arg role=dev`");
            ioR.AppendLine("- ⛔ **不要抄進見叢**（2026-09-07 起見叢只放個人代辦）——"
                + " 認領之後它會自己出現在你早安 brief 的 **§2.5 見單**。");
            ioR.AppendLine($"- 做完 commit 訊息帶 `Fixes {e.Id}`（提交時自動推進 —— 有 QA 進 in_review，沒 QA 直接 done）");
            ioR.AppendLine();
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Created, iActor,
                aDescription, iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
        }

        // ===========================================================
        // 區塊職責：查重提示 —— 只呈現，不阻擋（v1 粗篩，TASK-0086 自 BugReport 搬入）。
        // 物理意義：「回報前先檢索」寫成守則會失敗：關鍵字查失敗的樣子跟「不存在」一模一樣，
        //   所以它不會叫 —— 照守則辦事的人拿到乾淨的空結果，開一張重複單，還以為查證過了。
        // ⚠ 標題字詞重疊＋tags 交集，**不是語意檢索** —— 查不到 ≠ 不存在。
        //   刻意不阻擋：阻擋要判斷「這算不算同一件」，而那正是會判錯的地方。
        // ===========================================================
        static void AppendSimilar(StringBuilder ioR, UCL_TaskEntry iNew)
        {
            var aHits = new List<(int score, UCL_TaskEntry t)>();
            var aWords = Tokens(iNew.title);
            foreach (var t in UCL_TaskIO.LoadAll())
            {
                if (t.index == iNew.index || t.IsClosed()) continue;
                int aScore = 0;
                foreach (var w in Tokens(t.title)) if (aWords.Contains(w)) aScore++;
                foreach (var aTag in iNew.tags)
                    if (t.tags.Contains(aTag)) { aScore += 1; break; }
                if (aScore > 0) aHits.Add((aScore, t));
            }
            if (aHits.Count == 0) return;
            aHits.Sort((a, b) => b.score.CompareTo(a.score));
            ioR.AppendLine();
            ioR.AppendLine("⚠ **可能重複（未阻擋，請自行判斷）** —— v1 粗篩：標題字詞重疊＋tags 交集，");
            ioR.AppendLine("　 **不是語意檢索**。查不到 ≠ 不存在。");
            for (int i = 0; i < aHits.Count && i < 3; i++)
                ioR.AppendLine($"  - {aHits[i].t.Id}　`{aHits[i].t.status}`　{aHits[i].t.title}");
        }

        static HashSet<string> Tokens(string s)
        {
            var aSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return aSet;
            foreach (var t in s.Split(new[] { ' ', '\t', '/', '\\', '(', ')', '[', ']', '，', '、', '：', ':', '。', '—' },
                         StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) aSet.Add(t);
            return aSet;
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

            // ===========================================================
            // ⭐ TASK-0202：**給了 `scope` ＝「我現在要動工」** ⇒ 認領＋開場＋綁單，一步。
            //   而它必須**原子**：開場被擋時認領一個位元組都不寫。
            //   🩸 半套的失效樣子是「單子寫著我在做，而我其實進不了場」——
            //     看板上跟「有人在做」一模一樣，而實際上沒有人動得了它。
            //   ⛔ 順序不可對調：先寫認領再開場的話，擋下那一刻已經留下假狀態了。
            // ⚠ 不給 `scope` ＝「我先記錄我在做這件事」⇒ 行為**一格都不變**。
            //   那兩件事常常差好幾天，把它們綁死會讓一個便宜的動作變貴，而貴的動作會被繞過。
            // ===========================================================
            string aScopeRaw = GetArg(iArgs, "scope", "").Trim();
            string aSessionNote = "";
            if (aScopeRaw.Length > 0
                && !TryStartOrBindCodingSession(iActor, aIndex, aScopeRaw,
                                                out aSessionNote, out string aSessionFail))
            {
                ioR.AppendLine("## ⛔ 沒有認領 —— 開場被擋");
                ioR.AppendLine();
                ioR.AppendLine(aSessionFail);
                ioR.AppendLine();
                ioR.AppendLine("⚠ **認領一個位元組都沒寫** —— 妳帶了 `scope`，那句話的意思是「我現在要動工」，"
                               + "而動不了。⛔ 不留一張「寫著有人在做、其實沒人動得了」的單。");
                ioR.AppendLine("⇒ 只想記錄「我在做這件事」的話，拿掉 `--arg scope=` 再跑一次：");
                ioR.AppendLine($"　 `senate ucmd run Task --persona {iActor} --arg op=claim --arg index={aIndex}`");
                throw new Exception($"[Task] TASK-{aIndex} 沒有認領 —— 開場被擋，詳見回傳檔");
            }

            string aNow = UCL_TaskIO.NowUtc();
            bool aNew = false;
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
            string aWhyNoMove = null;
            // ⭐ TASK-0163：加人＋推狀態的整段 RMW 進 `Mutate`。
            //   ⚠ 「該不該推狀態」是拿**狀態**做的判斷（backlog/todo 才推）⇒ 照形狀乙，
            //   那個判斷必須對鎖內重讀的那一份重做：別人剛把它推成 in_review 的話，
            //   用鎖外的舊讀數會把它**往回推**成 in_progress，而時間線會留一行有出處的假話。
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aNew = AddParticipant(m, iActor, aRole, aNow);
                aFrom = m.status;
                bool aNotStarted = aFrom == UCL_TaskStatus.backlog || aFrom == UCL_TaskStatus.todo;
                aWhyNoMove = null;
                if (!aDoingRole) aWhyNoMove = $"`{aRole}` 是驗收／協調角色，不是「開工」⇒ 狀態不動";
                else if (!aNotStarted) aWhyNoMove = $"單子已經在 `{aFrom}` ⇒ 不往回推（認領只從 backlog/todo 推進）";

                if (aWhyNoMove == null) m.status = UCL_TaskStatus.in_progress;
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line(aWhyNoMove == null
                    ? $"{aNow}　`in_progress`　{iActor} 認領（role={aRole}，原狀態 {aFrom}）"
                    : $"{aNow}　`{m.status}`　{iActor} 加入為 {aRole}（狀態不動：{aWhyNoMove}）");
            });
            if (!aWrote)
            {
                // ⚠ 認領沒落檔而場已經開了 ⇒ **收掉它**，⛔ 不留一個沒有單的孤兒場擋住所有人。
                //   這一格是原子那句話的另一半：前半擋「開不了場就別認領」，後半擋「認領沒成就別留著場」。
                string aRollback = aScopeRaw.Length > 0 ? CloseCodingSessionQuietly(iActor) : "";
                throw new Exception($"[Task] TASK-{aIndex} 認領沒有落檔 —— 鎖內重讀時那張單不在了"
                    + "（被刪或被搬）⇒ **寫入沒有發生**，妳沒有被加進參與者。" + aRollback);
            }

            // ⚠ 回報一律用**落檔後**那一份（同 `OpCommit` 那格的理由）——
            //   🩸 2026-09-11 實測：Template 認領成功，而這行印「參與：basecamp(dev)」
            //     （`e` 是 `Mutate` **之前**讀的那一份）⇒ 剛加進去的人不在自己的成功回報裡。
            //     那不會報錯，它只是**少印一個人**，而讀的人會以為自己沒被加進去。
            e = UCL_TaskIO.Find(aIndex) ?? e;

            ioR.AppendLine($"## ✅ {e.Id} 已認領");
            ioR.AppendLine(aWhyNoMove == null
                ? $"- {aFrom} → **in_progress**　role=`{aRole}`"
                    + (aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）")
                : $"- 狀態**維持 `{aFrom}`**　role=`{aRole}` —— {aWhyNoMove}"
                    + (aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）"));
            ioR.AppendLine($"- 參與：{Participants(e)}");
            // ⚠ 沒開場那一行**要印**，不要安靜 —— 安靜的話「我只是記錄」與「我以為我已經進場了」同形。
            ioR.AppendLine(aSessionNote.Length > 0
                ? aSessionNote
                : "- ⚠ **沒有開 Coding 場**（沒給 `--arg scope=`）⇒ 這只是「記錄我在做這件事」。"
                  + $"要動工（會改 C#）請帶範圍：`senate ucmd run Task --persona {iActor} "
                  + $"--arg op=claim --arg index={aIndex} --arg scope=<絕對路徑>`");
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

        // 區塊職責：認領時「開一場新的」或「綁到現有那一場」—— 兩條路，**而它們不同形**。
        // 物理意義：軸1（每人一場）仍然成立 ⇒ 已經有場的人**不可以**因為認領第二張單就開第二場，
        //           那一格的正解是**補綁**（同 `op=bind`），不是開場。
        // 數值影響：成功時寫一份 session 檔（或改一個欄位）；失敗時**一個位元組都不寫**
        //           （`TryStart` 自己保證前半，補綁那半由 `BindTasks` 回讀確認）。
        // ⚠ 綁的單號走 `SCP_Cmd_Coding.NormalizeTasks` —— ⛔ 不自己 `ToString()` 拼：
        //   「去前導零」那層只要有一邊沒做，自動收場就永遠不成立，而它不會叫。
        static bool TryStartOrBindCodingSession(string iPersona, int iIndex, string iScopeRaw,
                                                out string oNote, out string oFail)
        {
            oNote = "";
            oFail = "";
            if (!SCP.Core.Session.SCP_SessionScope.TryNormalize(iScopeRaw, out string aScope, out string aScopeErr))
            {
                oFail = $"- `--arg scope` 解析不了：`{iScopeRaw}`"
                        + (aScopeErr.Length > 0 ? $"（{aScopeErr}）" : "")
                        + "\n- 要的是**絕對路徑**，例：`D:/Unity/LY/Assets/Plugins/UCL_Core`";
                return false;
            }

            var aRoot = UCL_AgentCommandsPath.ScpDataRoot;
            string aKind = SCP.Core.Session.SCP_ActivitySessionKind.Coding;
            var aMine = SCP.Core.Session.SCP_ActivitySessionStore
                .Load<SCP.Core.Session.SCP_CodingSession>(aRoot, iPersona, aKind);
            if (aMine != null && aMine.active && aMine.IsRunningAt(DateTime.Now, out _))
            {
                string aRead = SCP.Core.Cmd.SCP_Cmd_Coding.BindTasks(aRoot, iPersona, iIndex.ToString());
                if (aRead == null)
                {
                    oFail = "- 妳已經有一場 Coding，但**補綁沒有落檔**（回讀不到那一場）"
                            + "\n- 先查現況：`senate cmd coding --arg data_root=<AgentCommands 根>`";
                    return false;
                }
                oNote = $"- 🛠 **綁到妳現有的場**（⛔ 沒有開第二場）：`{aMine.session_id}`"
                        + $"　回讀 tasks = **{aRead}**（回讀單檔，不是寫入端的回傳值）";
                string aHave = SCP.Core.Session.SCP_ActivitySessionStore.ScopeOf(aMine);
                if (!string.Equals(aHave, aScope, StringComparison.OrdinalIgnoreCase))
                {
                    // ⛔ **不自動擴大現有場的範圍** —— 靜默擴大會在別人完全不知情的狀況下擋掉更多人，
                    //    而被擋的人看到的理由會是一個我從來沒宣告過的路徑。
                    oNote += $"\n- ⚠ 現有場的範圍**沒有變**："
                             + (aHave.Length > 0 ? $"`{aHave}`" : "**（沒宣告 ⇒ 整棵樹）**")
                             + $"；這次給的 `{aScope}` **沒有被套用**。"
                             + $"要換範圍得先 `senate ucmd run Coding --persona {iPersona} --arg step=end` 再開新的場。";
                }
                return true;
            }

            int aHours = SCP.Core.Cmd.SCP_Cmd_Coding.DefaultLeaseHours;
            DateTime aUntil = DateTime.Now.AddHours(aHours);
            var aSession = new SCP.Core.Session.SCP_CodingSession
            {
                persona = iPersona,
                kind = aKind,
                session_id = "coding-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + iPersona,
                start_ts = SCP.Core.Session.SCP_ActivitySession.NowIso(),
                // ⚠ 兩個欄位的格式與另外兩個入口（`Cmd_Coding` / `SCP_Cmd_Coding`）**逐字一致** ——
                //   同一個檔被三個宿主讀寫，格式不一致的那天沒有任何一層會出聲。
                end_ts = aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                until_local = aUntil.ToString("yyyy-MM-dd HH:mm"),
                active = true,
                status = $"TASK-{iIndex:0000}（由 op=claim 開場）",
                status_updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz"),
                scope = aScope,
                tasks = SCP.Core.Cmd.SCP_Cmd_Coding.NormalizeTasks(iIndex.ToString()),
            };
            if (!UCL_SessionStartGuard.TryStart(iPersona, aSession, aKind,
                                                out string aReason, out string aExit, aScope))
            {
                oFail = $"- 原因：{aReason}\n- 處理方式：{aExit}";
                return false;
            }
            oNote = $"- 🛠 **已開 Coding 場**：`{aSession.session_id}`　範圍 `{aScope}`　"
                    + $"租期至 **{aSession.until_local}**（{aHours} 小時）"
                    + $"\n- ⭐ 綁定單 **{aSession.tasks}** ⇒ 它離開施工狀態（`in_review`／`done`）時本場**自動收**";
            return true;
        }

        /// <summary>認領落檔失敗時的回捲：把剛剛開起來的場收掉。回一句可以接在例外訊息後面的話。</summary>
        /// <remarks>⚠ 回捲**本身也可能失敗** —— 失敗要說出來，⛔ 不可以靜默（那會留下一個沒人知道的鎖）。</remarks>
        static string CloseCodingSessionQuietly(string iPersona)
        {
            try
            {
                var aRoot = UCL_AgentCommandsPath.ScpDataRoot;
                string aKind = SCP.Core.Session.SCP_ActivitySessionKind.Coding;
                var aS = SCP.Core.Session.SCP_ActivitySessionStore
                    .Load<SCP.Core.Session.SCP_CodingSession>(aRoot, iPersona, aKind);
                if (aS == null || !aS.active) return "";
                SCP.Core.Session.SCP_ActivitySessionStore.Close(aRoot, iPersona, aS, "task-claim-rollback");
                var aBack = SCP.Core.Session.SCP_ActivitySessionStore.Load(aRoot, iPersona);
                bool aClosed = aBack != null && !aBack.active;
                return aClosed
                    ? "（剛開的場已回捲收掉，回讀確認=True）"
                    : "（⚠ **回捲失敗：那一場還開著** —— 請手動 `senate ucmd run Coding --persona "
                      + iPersona + " --arg step=end`）";
            }
            catch (Exception ex)
            {
                return "（⚠ **回捲丟例外，那一場可能還開著**：" + ex.GetType().Name + "）";
            }
        }

        async UniTask OpAssign(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aTarget = GetArg(iArgs, "target_persona", "").Trim();
            if (aTarget.Length == 0) throw new Exception("[Task] op=assign 需要 --arg target_persona=<誰>");
            var aRole = ParseEnumArg(iArgs, "role", UCL_TaskRole.dev);
            bool aReplace = GetArg(iArgs, "replace", "").Trim() == "1";
            string aNow = UCL_TaskIO.NowUtc();

            // 🩸 TASK-0131（2026-09-05）：本 Op 是 **append**，而**加一個角色**與**換角色**
            //   在指令上長得一模一樣（`op=assign` 帶不同的 role）。⇒ 想換角色的人得到的是
            //   「同一個人同時掛兩個角色」，三步全回 Success，而**「誰是這張單的 dev」從此沒有答案**。
            // ⚠ 一人多角色**是合法的、而且是常態**（掃過既有單子：12 張是 `pm`＋`qa` 這種刻意的組合）
            //   ⇒ 修法**不是**把它改成一人一角，那會靜靜拆掉那 12 張的參與者。
            //   ⇒ 修法是：① 給「換角色」一條**顯式**的路（`--arg replace=1`）
            //             ② 沒帶 replace 而那個人已經有別的角色時，**在回傳檔說出來**——
            //                讓「我加了一個角色」與「我以為我換了角色」不再同形。
            // ⭐ TASK-0163：讀既有角色 → 決定要不要拿掉 → 加人 —— 整段是 RMW，進 `Mutate`。
            //   ⚠ `replace=1` 那條路特別需要它：既有角色清單是**判斷的輸入**，
            //   鎖外讀的話，別人剛指派的角色會被這一次的整檔重寫**靜默吃掉**
            //   （而回傳檔會說「換角色：拿掉 X」—— 一句有出處而不完整的話）。
            string aRemoved = "";
            bool aNew = false;
            var aExisting = new List<UCL_TaskRole>();     // 鎖內讀到的既有角色（回報用）
            var aOtherRoles = new List<UCL_TaskRole>();
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aExisting = m.participants
                    .Where(p => string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.role).ToList();
                aOtherRoles = aExisting.Where(r => r != aRole).ToList();
                aRemoved = "";
                if (aReplace && aOtherRoles.Count > 0)
                {
                    m.participants.RemoveAll(p => string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase)
                                                  && p.role != aRole);
                    aRemoved = string.Join("／", aOtherRoles);
                }
                aNew = AddParticipant(m, aTarget, aRole, aNow);
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line($"{aNow}　`assign`　{iActor} 指派 {aTarget} 為 {aRole}"
                    + (aRemoved.Length > 0 ? $"（**換角色**：拿掉 {aRemoved}）" : ""));
            });
            if (!aWrote)
                throw new Exception($"[Task] TASK-{aIndex} 指派沒有落檔 —— 鎖內重讀時那張單不在了"
                    + $"（被刪或被搬）⇒ **寫入沒有發生**，{aTarget} 沒有被指派。");

            ioR.AppendLine($"## ✅ {e.Id} 參與者已更新");
            ioR.AppendLine($"- {(aNew ? "新增" : "已存在，未重複加")}：{aTarget}（{aRole}）");
            if (aRemoved.Length > 0)
            {
                ioR.AppendLine($"- 🔁 **換角色**（`replace=1`）：{aTarget} 原本的 `{aRemoved}` 已拿掉");
            }
            else if (aOtherRoles.Count > 0)
            {
                // ⚠ 這一段是本次修正的本體：**不擋，但要說**。
                //   擋的話會拆掉「一人多角」這個合法用法；不說的話「加角色」與「換角色」同形。
                ioR.AppendLine($"- ⚠ **{aTarget} 本來就有角色 `{string.Join("／", aOtherRoles)}`** ——"
                               + $" 本次是**加上** `{aRole}`，現在他同時是 `{string.Join("／", aExisting.Append(aRole).Distinct())}`。");
                ioR.AppendLine($"  · 想**換角色**（拿掉舊的）請帶 `--arg replace=1`："
                               + $"`op=assign --arg index={e.index} --arg target_persona={aTarget} --arg role={aRole} --arg replace=1`");
                ioR.AppendLine("  · ⚠ 一人多角是合法的（`pm`＋`qa` 很常見）—— 這一行不是錯誤，是**要你確認你按的是哪一個**。");
            }
            ioR.AppendLine($"- 參與：{Participants(e)}");
            bool aOk = await UCL_TaskNotify.PostAsync(e, UCL_TaskNotify.Kind.Assigned, iActor,
                $"{aTarget} ← `{aRole}`", iCallerArgs: iArgs);
            AppendNotifyLine(ioR, e, iActor, aOk);
            ioR.AppendLine($"- 📌 這張單會出現在 {aTarget} 早安 brief 的 **§2.5 見單** ——"
                + " 但只有它進到 `in_progress` / `in_review` 才逐張列；");
            ioR.AppendLine("  還在 `todo` / `backlog` 時只算進張數。⇒ **酒館通知仍是他今天就知道這件事的那條路。**");
        }

        // 可被 `--arg unset=` 清空的欄位白名單（TASK-0079）。
        // ⚠ 白名單而不是「所有 string 欄位」：`status` / `priority` / `title` 沒有「空」這個合法狀態，
        //   能清它們等於開一條讓單子變成無效資料的路。
        static readonly string[] UNSETTABLE = { "memory_topic", "memory_archived_commit", "milestone" };

        void OpUpdate(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aNow = UCL_TaskIO.NowUtc();
            var aChanges = new List<string>();
            var aUnsetNotes = new List<string>();
            // ⭐ TASK-0163：整段「逐欄位比對舊值 → 改 → 落檔」進 `Mutate`。
            //   ⚠ 這一支的每一行 `aChanges.Add($"status {舊} → {新}")` 都是**拿舊值講給人看的話**
            //   ⇒ 舊值必須是鎖內重讀的那一份，否則時間線會寫「status todo → in_progress」
            //   而它其實是從別人剛改成的 in_review 被推過去的 —— 一句有出處而錯的紀錄。
            //   ⚠ 內文兩區走 `UCL_TaskWrite.Body`（`op=update` 是唯一會整段改寫驗收標準的入口）。
            bool aFoundEntry = false;
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aFoundEntry = true;
                aChanges.Clear(); aUnsetNotes.Clear();   // 鎖內重跑一次 ⇒ 清掉上一輪（Mutate 只呼叫一次，這是防呆）

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
                    aChanges.Add($"status {m.status} → {aNorm}");
                    m.status = aNorm;
                    // 🩸 2026-08-24：我誤關了別人的單再改回 todo，而 `closed_at` **留著我那筆取消的時戳** ⇒
                    //   status=todo 而 closed_at 有值 —— 資料自己跟自己打架，且看不出哪一邊是真的。
                    //   ⇒ 從已關改回未關時一律清掉它，並在時間線寫明清了什麼（不靜默改數字）。
                    if (m.closed_at.Length > 0)
                    {
                        aChanges.Add($"closed_at 清空（原 {m.closed_at} —— 未關的單不該有結案時間）");
                        m.closed_at = "";
                    }
                }
                string aPriority = GetArg(iArgs, "priority", "").Trim();
                if (aPriority.Length > 0)
                {
                    var aPri = ParseEnumArg(iArgs, "priority", UCL_TaskPriority.normal);
                    aChanges.Add($"priority {m.priority} → {aPri}");
                    m.priority = aPri;
                }
                // 傷害形狀（TASK-0086）：severity=none 即顯式清回「未標注」，合法
                string aSeverityArg = GetArg(iArgs, "severity", "").Trim();
                if (aSeverityArg.Length > 0)
                {
                    var aSev = ParseEnumArg(iArgs, "severity", UCL_TaskSeverity.none);
                    aChanges.Add($"severity {m.severity} → {aSev}");
                    m.severity = aSev;
                }
                string aTitle = GetArg(iArgs, "title", "").Trim();
                if (aTitle.Length > 0) { aChanges.Add("title 改寫"); m.title = aTitle; }
                string aMilestone = GetArg(iArgs, "milestone", "").Trim();
                if (aMilestone.Length > 0) { aChanges.Add($"milestone → {aMilestone}"); m.milestone = aMilestone; }
                // 記憶錨點（契約①：這兩格歸 Task 側寫，記憶側的 task_indices 歸 CLI）
                string aMemTopic = GetArg(iArgs, "memory_topic", "").Trim();
                if (aMemTopic.Length > 0)
                {
                    aChanges.Add($"memory_topic {(m.memory_topic.Length == 0 ? "(空)" : m.memory_topic)} → {aMemTopic}"
                        + (UCL_TaskMemoryLink.TopicExists(aMemTopic) ? "" : "　⚠ **這個主題目前不在磁碟上**（照樣寫入，但要知道）"));
                    m.memory_topic = aMemTopic;
                }
                string aMemSha = GetArg(iArgs, "memory_archived_commit", "").Trim();
                if (aMemSha.Length > 0)
                { aChanges.Add($"memory_archived_commit → {aMemSha}"); m.memory_archived_commit = aMemSha; }

                // ===========================================================
                // 顯式清除（TASK-0079，與 BUG-16 `PersonaProfile op=unset` 同形）
                // 物理意義：`--arg <欄位>=` 給空值在這支是**保留原值**（上面每一格都是 `.Length > 0` 才寫），
                //   所以「打錯字的 memory_topic」沒有任何回頭路 —— 而晚安對帳每天為它亮一次警示。
                //   ⇒ 補的是**逆操作**，不是把空值改成有意義：空值仍然是「這次不動這欄」，
                //     要清就得指名道姓 `--arg unset=memory_topic`（BUG-16 選的也是這個形狀：
                //     另立一個方向明確的入口，而不是讓 set 兼差）。
                // 數值影響：把指名的欄位寫回 ""，並在 aChanges 留痕（⇒ 時間線那一行會寫清了什麼、原值是什麼）。
                // ⚠ 冪等：本來就空 ⇒ **不計入變更**（不留一筆看起來發生過的帳），但仍逐格印出「本來就是空的」——
                //   「清掉了」與「本來就空」不可以同形，否則讀的人分不出自己清的是哪一格。
                // ===========================================================
                string aUnsetArg = GetArg(iArgs, "unset", "").Trim();
                if (aUnsetArg.Length > 0)
                {
                    foreach (string aRawField in aUnsetArg.Split(','))
                    {
                        string aField = aRawField.Trim();
                        if (aField.Length == 0) continue;
                        // 認不得的欄位名**不靜默略過** —— 打錯字的失效樣子會跟「清過了」一模一樣，
                        // 而這張單修的正是「打錯字沒有回頭路」。
                        if (!UNSETTABLE.Contains(aField))
                            throw new Exception($"[Task] unset 認不得欄位 `{aField}`"
                                + $"（可清的只有：{string.Join(" / ", UNSETTABLE)}）"
                                + "　—— status／priority／title 這種**沒有「空」這個合法狀態**的欄位不在清單上。");
                        string aOld = aField switch
                        {
                            "memory_topic" => m.memory_topic ?? "",
                            "memory_archived_commit" => m.memory_archived_commit ?? "",
                            "milestone" => m.milestone ?? "",
                            _ => "",
                        };
                        if (aOld.Trim().Length == 0) { aUnsetNotes.Add($"`{aField}` 本來就是空的 ⇒ 沒有寫入"); continue; }
                        switch (aField)
                        {
                            case "memory_topic": m.memory_topic = ""; break;
                            case "memory_archived_commit": m.memory_archived_commit = ""; break;
                            case "milestone": m.milestone = ""; break;
                        }
                        aChanges.Add($"{aField} 清空（原 `{aOld}`）");
                    }
                }
                // criteria / description 是 Save 的參數不是 entry 欄位 —— 但它們一樣是變更（TASK-0033 ③）。
                // 🩸 血證（Tim 2026-08-25 撞到）：只給 --arg criteria= 是**靜默 no-op** ——
                //   它沒進 aChanges ⇒ 走「沒有任何變更」那條路 ⇒ 單子一個字都不變，而回傳檔看起來像判斷。
                //   而「擴充當前 Task 的驗收細項」是收斂機制的主要出口，等於主要出口需要 workaround（多帶 title）才會開。
                string aCriteria = GetArg(iArgs, "criteria", "");
                if (aCriteria.Trim().Length > 0) aChanges.Add("criteria 整段改寫");
                string aDescription = GetArg(iArgs, "description", "");
                if (aDescription.Trim().Length > 0) aChanges.Add("description 整段改寫");

                if (aChanges.Count == 0) return UCL_TaskWrite.Skip;
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Body($"{aNow}　`update`　{iActor}：{string.Join("／", aChanges)}",
                    aCriteria, aDescription);
            });
            if (!aWrote)
            {
                if (!aFoundEntry)
                    throw new Exception($"[Task] TASK-{aIndex} 更新沒有落檔 —— 鎖內重讀時那張單不在了"
                        + "（被刪或被搬）⇒ **寫入沒有發生**。");
                ioR.AppendLine($"## {e.Id} 沒有任何變更");
                // ⚠ 冪等的 unset 走到這裡 —— 要印出「我確實看了那幾格，它們本來就空」，
                //   否則它跟「我根本沒收到 unset」同形（TASK-0079）。
                foreach (var n in aUnsetNotes) ioR.AppendLine($"- ✓ {n}");
                if (aUnsetNotes.Count > 0) return;
                ioR.AppendLine("- 沒給任何可更新的欄位（status / priority / title / milestone /"
                    + " memory_topic / memory_archived_commit / criteria / description"
                    + " / unset=<欄位>）⇒ **什麼都沒寫**。");
                return;
            }
            ioR.AppendLine($"## ✅ {e.Id} 已更新");
            foreach (var c in aChanges) ioR.AppendLine($"- {c}");
            foreach (var n in aUnsetNotes) ioR.AppendLine($"- ✓ {n}");
        }

        async UniTask OpComment(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aBody = GetArg(iArgs, "body", "").Trim();
            if (aBody.Length == 0) throw new Exception("[Task] op=comment 需要 --arg body=<內容>");
            string aNow = UCL_TaskIO.NowUtc();
            // ⭐ TASK-0163 形狀甲：整段 RMW 走 `UCL_TaskIO.Mutate`（鎖內重讀 → 改 → 寫）。
            //   🩸 這一支最值錢的一格是 `NextCommentId`：它是 `max(e.comments.id) + 1`，
            //   而 `e` 原本是**鎖外**讀的 ⇒ 兩條緒同時留言會算出**同一個 id**，
            //   接著兩次整檔重寫 ⇒ **其中一則留言靜默消失**（回傳都是 Success、兩邊都印得出自己的 #N）。
            //   ⇒ 配號必須在鎖內對重讀的 `e` 算，這才是把它變成原子的那一步。
            // ⛔ 舊的 `[RMW-END]` 前哨在此退場 —— 跨度現在由型別決定，不由註解宣告。
            int aCommentId = 0;
            bool aWrote = UCL_TaskIO.Mutate(aIndex, e =>
            {
                aCommentId = UCL_TaskIO.NextCommentId(e);
                e.comments.Add(new UCL_TaskComment
                {
                    id = aCommentId,
                    persona = iActor,
                    at = aNow,
                    body = aBody,
                });
                UCL_TaskIO.Touch(e, aNow);
                // 時間線只留一行「有人留言了」的索引 —— 內容在留言區，**不存兩份**
                return UCL_TaskWrite.Line($"{aNow}　`comment`　{iActor} 留言 #{aCommentId}");
            });
            if (!aWrote)
                throw new Exception($"[Task] TASK-{aIndex} 留言沒有落檔 —— 鎖內重讀時那張單不在了"
                    + "（被刪或被搬）。⛔ 這不是「留言失敗」的泛稱，是**寫入沒有發生**，妳的內容沒有進磁碟。");

            ioR.AppendLine($"## ✅ {e.Id} 已留言 #{aCommentId}");
            ioR.AppendLine($"- 作者：{iActor}　時間：{aNow}");
            ioR.AppendLine("- 落點：單檔的 `## 留言` 區塊（時間線只留一行索引）");
            ioR.AppendLine();
            ioR.AppendLine("```markdown");
            ioR.AppendLine(aBody);
            ioR.AppendLine("```");
            ioR.AppendLine();
            // ⚠ 通知要用**落檔之後**的那一份：`e` 是鎖前讀的提示，而公告要 @ 的參與者清單
            //   可能在鎖內那一刻已經不同（別人剛 assign）。⇒ 重讀一次；讀不到就退回用提示，
            //   ⛔ 不因為讀不到就不發公告（那會讓「發不出去」與「沒有人該被通知」同形）。
            var aFresh = UCL_TaskIO.Find(aIndex) ?? e;
            bool aOk = await UCL_TaskNotify.PostAsync(aFresh, UCL_TaskNotify.Kind.Comment, iActor, "", aBody, iArgs);
            AppendNotifyLine(ioR, aFresh, iActor, aOk);
            ioR.AppendLine("- ⚠ 留言**會推進 `updated_at`** ⇒ 它會讓 stale 計時歸零。");
            ioR.AppendLine("  所以「留言說我還在做」跟「真的有做」在 stale 讀數上長得一樣 —— 這是這個讀數的邊界。");
        }

        // ===========================================================
        // 區塊職責：把某幾格驗收標準勾起來並**留下是誰勾的**（TASK-0119）。
        // 物理意義：在此之前 `criteria` 只有 `op=create` 能設、`op=update` 不吃它 ⇒
        //   一張單交付完成之後**沒有任何 op 能打勾** ⇒
        //   **「已驗但無處打勾」與「從沒人驗過」在那一欄上完全同形**，
        //   而看板讀者唯一看得到的就是那一欄。
        //
        // ⛔ **刻意不是「開放整份 criteria 覆寫」**（本單明寫的邊界）：
        //   `--arg-file criteria=` 那條整份覆蓋的路已經存在，本 op **不擴大**它 ——
        //   這裡只翻某一行的勾選格並接上署名，其餘位元組不動。
        //   ⇒ 差別在權限而不在方便：整份覆寫讓「正在做它的人」可以把驗收標準整段換掉。
        //
        // 🩸 閘為什麼比 `resolve` 嚴（`resolve` 有 `--arg qa_note=` 代簽，本 op **沒有**）：
        //   `resolve` 需要代簽出口，因為 QA 不在時**一張單會關不掉**，那是真的卡住。
        //   而一格沒被勾的驗收標準**不卡任何人** ⇒ 沒有正當的破例用例 ⇒ 不留出口。
        //   ⚠ 這是判斷不是讀數：若哪天量到「有人因為勾不了而卡住」，那個出口再加。
        // 數值影響：一次讀（Require → Find）＋ 一次寫（Save）＋ 一次**回讀**（印勾後的分母）。
        // ===========================================================
        void OpCheck(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR)
        {
            var e = Require(iArgs, out int aIndex);
            string aCriteria = UCL_TaskIO.ReadCriteria(aIndex);
            var aOpen = UCL_TaskIO.ListUncheckedCriteria(aCriteria);
            var aDone = UCL_TaskIO.ListCheckedCriteria(aCriteria);

            // ── 誰可以勾 ──────────────────────────────────────────
            // 有指名 QA ⇒ 只有 QA（驗收是他的簽名）；沒有 QA ⇒ 參與者＋開單人。
            var aQa = e.QaPersonas();
            bool aAllowed;
            string aWho;
            if (aQa.Count > 0)
            {
                aAllowed = aQa.Any(s => string.Equals(s, iActor, StringComparison.OrdinalIgnoreCase));
                aWho = "本單指名的 QA：" + string.Join(" / ", aQa);
            }
            else
            {
                aAllowed = e.RolesOf(iActor).Count > 0
                    || string.Equals(e.reporter, iActor, StringComparison.OrdinalIgnoreCase);
                var aNames = e.participants.Select(p => p.persona)
                    .Concat(new[] { e.reporter })
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                aWho = "本單**沒有指名 QA** ⇒ 參與者與開單人：" + string.Join(" / ", aNames);
            }

            // ── ⭐ TASK-0194：一格一個尺度的簽名人（`[signer:<persona>]`）────────
            //   整張單的尺度（上面那段）回答不了「⑤c 只有本人能簽」這種條文 ——
            //   而它失效的方向是**相反的兩邊**：本人被擋下，開單人卻代簽得掉。
            //   ⇒ 帶標記的那一格改由標記說了算：**只有 owner 能簽，其他人一律不行**
            //     （含 QA 與開單人 —— 那正是條文要防的代簽），
            //     而 owner **不需要**先入列（要求他入列＝為了簽一格而拿到整張單的簽名權）。
            //   ⛔ 沒有標記的行完全維持舊行為 ⇒ 既有的單一個字都不受影響。
            var aOwners = aOpen.Select(UCL_TaskIO.CriteriaSigner).ToList();
            bool aOwnsAny = aOwners.Any(o => o != null
                && string.Equals(o, iActor, StringComparison.OrdinalIgnoreCase));

            ioR.AppendLine($"## {e.Id} 驗收標準　已勾 **{aDone.Count}** / 未勾 **{aOpen.Count}**");
            ioR.AppendLine($"- 可以勾的人：{aWho}");
            ioR.AppendLine($"- 你是：`{iActor}`　⇒ {(aAllowed ? "✅ 有權" : "🛑 **無權**")}（整張單的尺度）");
            if (aOwners.Any(o => o != null))
            {
                ioR.AppendLine($"- ⭐ 本單有 **{aOwners.Count(o => o != null)}** 格帶 `[signer:…]` 指定簽名人"
                    + " ⇒ 那幾格**只有本人**簽得掉（QA 與開單人也不行），且本人不必先入列。"
                    + $"　你{(aOwnsAny ? "**有**" : "沒有")}這樣的格子。");
            }
            ioR.AppendLine();

            // ⚠ 擋下的時機**往後挪**：整張單無權、但持有某一格的 owner 仍該看得到清單、簽得掉那一格。
            //   ⇒ 這裡只擋「整張單無權**且**一格都不屬於他」的人。逐格判定在下面。
            if (!aAllowed && !aOwnsAny)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine("- reason: 勾驗收標準是**簽名行為** —— 不是參與者也不是 QA 的人勾了它，");
                ioR.AppendLine("  那個勾對讀單的人**看起來跟真的驗收一模一樣**，而它沒有任何人負責。");
                ioR.AppendLine("- exits:");
                ioR.AppendLine($"    · 先入列 → `run Task --arg op=claim --arg index={aIndex} --arg role=qa`");
                ioR.AppendLine($"    · 或請上面那些人跑，或請他們 `op=assign` 把你加進來");
                ioR.AppendLine("- ⛔ 本 op **沒有** `qa_note=` 代簽出口（`resolve` 有）——");
                ioR.AppendLine("  理由：關不掉的單會卡住工作，**沒勾的驗收格不卡任何人** ⇒ 沒有正當的破例用例。");
                // 🩸 措辭：**不可以寫「不是參與者/QA」** —— 有指名 QA 時，一個「是參與者但不是那個 QA」
                //   的人也會被擋（basecamp 2026-09-08 在 0114 上實際撞到：她是 dev＋pm）。
                //   那句話會讓被擋的人去查名單、發現自己明明在上面 ⇒ **下一步他懷疑守衛壞了，而守衛是好的**。
                //   ⇒ 訊息要說**這張單當下的規則是哪一條**，不要說一個他可以自己反駁的身分判定。
                throw new Exception($"[Task] op=check 擋下：`{iActor}` 不在 {e.Id} 的可簽名名單裡"
                    + $"（{(aQa.Count > 0 ? "本單有指名 QA ⇒ 只有 " + string.Join(" / ", aQa) + " 能簽" : "本單沒有指名 QA ⇒ 參與者與開單人能簽")}）"
                    + "，不能替它簽名");
            }

            // ── 沒給序號 ⇒ dry-run：印未勾清單（⛔ 零寫入）───────────
            string aRaw = GetArg(iArgs, "criteria_index", "").Trim();
            if (aRaw.Length == 0)
            {
                ioR.AppendLine("## 未勾的驗收格（序號＝**未勾清單**的序號，不是檔案行號）");
                // ⭐ TASK-0163：**零個勾選格**與**全部都勾了**是兩件相反的事，而它們原本共用同一句話。
                //   🩸 basecamp 2026-09-09 在本單上實撞：①〜⑤ 是散文行（沒有 `- [ ]`）⇒ 讀數 0/0，
                //     而工具印「全部都勾了」⇒ 看板上 `in_review` ＋「全都勾了」＝**看起來已驗完，而一格都沒簽**。
                //   ⇒ 分辨不靠猜：兩邊都 0 ＝ 這一段裡一格勾選格都沒有。
                if (aOpen.Count == 0 && aDone.Count == 0)
                {
                    int aTextLines = CountCriteriaTextLines(aCriteria);
                    ioR.AppendLine("- 🛑 **這張單一格勾選格都沒有**（⛔ 不是「全部都勾了」）"
                        + (aTextLines > 0
                            ? $" —— 驗收標準那一段有 {aTextLines} 行文字，但沒有一行是 `- [ ]` 開頭"
                            : " —— 驗收標準那一段是空的"));
                    ioR.AppendLine("  ⇒ 它現在**結構上簽不掉**：`op=check` 沒有東西可以勾，"
                        + "而看板只看得到「這張單有驗收標準」。");
                    ioR.AppendLine("  ▶ 修法：每一條改寫成 `- [ ] <一格一行>`，走整份覆寫（title 要原封不動）：");
                    ioR.AppendLine($"    `run Task --arg op=update --arg index={aIndex}"
                        + " --arg title=\"<原標題>\" --arg-file criteria=<整段>`");
                }
                else if (aOpen.Count == 0) ioR.AppendLine($"- （全部都勾了 —— 已勾 {aDone.Count} 格）");
                for (int i = 0; i < aOpen.Count; i++)
                {
                    // ⭐ TASK-0194：owner 標在清單上 —— 「這一格我簽不掉」要在**選號之前**就看得到，
                    //   不然讀的人會挑一個號碼、跑一次、才被擋（而那時他已經以為自己在簽了）。
                    string aOwn = aOwners[i];
                    string aTag = aOwn == null ? ""
                        : (string.Equals(aOwn, iActor, StringComparison.OrdinalIgnoreCase)
                            ? $"　🖊 **只有 `{aOwn}` 能簽 ⇒ 那是你**"
                            : $"　🔒 只有 `{aOwn}` 能簽");
                    ioR.AppendLine($"- #{i + 1}　{Trunc(aOpen[i], 160)}{aTag}");
                }
                ioR.AppendLine();
                ioR.AppendLine("- 🛑 **dry-run**（沒帶 `criteria_index=`）⇒ **一個位元組都沒寫**。");
                ioR.AppendLine($"  勾它：`run Task --arg op=check --arg index={aIndex} --arg criteria_index=<n[,n...]>`");
                return;
            }

            // ── 序號解析（不猜：非數字／越界一律整批不做）──────────
            var aWant = new List<int>();
            foreach (var aTok in aRaw.Split(','))
            {
                string t = aTok.Trim();
                if (t.Length == 0) continue;
                if (!int.TryParse(t, out int v))
                    throw new Exception($"[Task] criteria_index 裡有不是數字的東西：「{t}」—— 整批不執行");
                // ⚠ 未勾清單是空的時候**不可以**印「範圍 1..0」—— 那是一個不存在的區間，
                //   而它出現的時機正好是「全部都勾完了」，讀的人最需要一句話講清楚。
                if (aOpen.Count == 0)
                {
                    // ⭐ TASK-0163：兩種相反的 0 —— 「一格勾選格都沒有」與「全部都勾了」。
                    if (aDone.Count == 0)
                        throw new Exception($"[Task] {e.Id} **一格勾選格都沒有**（⛔ 不是「全部都勾了」）"
                            + $" —— 驗收標準那一段有 {CountCriteriaTextLines(aCriteria)} 行文字，"
                            + "但沒有一行是 `- [ ]` 開頭 ⇒ 這張單結構上簽不掉。"
                            + " 不帶 `criteria_index` 跑一次，它會印修法");
                    throw new Exception($"[Task] {e.Id} 的驗收標準**全部都勾了**（已勾 {aDone.Count} 格）"
                        + " —— 沒有格子可以勾。⚠ 已勾的行不在序號範圍內（勾兩次不是冪等，是打錯了）");
                }
                if (v < 1 || v > aOpen.Count)
                    throw new Exception($"[Task] criteria_index={v} 在範圍外（目前未勾 1..{aOpen.Count}）"
                        + " —— ⚠ 序號是**未勾清單**的序號，不是檔案行號。整批不執行"
                        + "（不帶 criteria_index 跑一次可以看清單）");
                if (!aWant.Contains(v)) aWant.Add(v);
            }
            if (aWant.Count == 0)
                throw new Exception("[Task] criteria_index 解析後一個序號都不剩（只有分隔符？）");

            // ── ⭐ TASK-0194：逐格判定簽名權（整張單的尺度只管沒有標記的那些行）──
            //   ⚠ 整批不做 —— 一批裡有一格簽不掉就零寫入，⛔ 不做「能簽的先簽掉」，
            //     那會讓呼叫端拿到一個部分成功而**回傳碼是成功**。
            foreach (int v in aWant)
            {
                string aOwn = aOwners[v - 1];
                if (aOwn == null)
                {
                    if (aAllowed) continue;
                    ioR.AppendLine("## blocked");
                    ioR.AppendLine($"- reason: 序號 #{v} 沒有 `[signer:…]` 標記 ⇒ 它走**整張單**的尺度，"
                        + $"而 `{iActor}` 不在名單裡（{aWho}）。⇒ **一個位元組都沒寫**。");
                    throw new Exception($"[Task] op=check 擋下：序號 #{v} 走整張單的尺度，"
                        + $"而 `{iActor}` 不在 {e.Id} 的可簽名名單裡，不能替它簽名");
                }
                if (string.Equals(aOwn, iActor, StringComparison.OrdinalIgnoreCase)) continue;
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 序號 #{v} 帶 `[signer:{aOwn}]` ⇒ **只有 `{aOwn}` 本人簽得掉**。");
                ioR.AppendLine($"  ⛔ 你是 `{iActor}`　—— 就算你是 QA 或開單人也不行，"
                    + "**代簽正是這個標記要防的那件事**。⇒ 一個位元組都沒寫。");
                ioR.AppendLine($"  ▶ 出口：請 `{aOwn}` 自己跑（他不必先入列）；"
                    + "或他把書面同意留在單上，由你**另開一格**紀錄，⛔ 不要替他勾這一格。");
                throw new Exception($"[Task] op=check 擋下：序號 #{v} 指定簽名人是 `{aOwn}`，"
                    + $"而你是 `{iActor}` —— 這一格不接受代簽");
            }

            // ⭐ TASK-0163：`expect_text` —— 讓**呼叫端**把「我看到的那一行」帶進來當錨。
            //   🩸 為什麼鎖內那個錨不夠：它錨的是「**本次 handler 鎖外那一讀**」（毫秒級），
            //     而人的決定來自**更早一次** dry-run（秒／分鐘級）—— 那份清單從來不進到這支 cmd 裡。
            //   🩸 活體（basecamp 2026-09-09，兩條真 lane 相距 31ms）：意圖是「A 勾甲、B 勾乙」，
            //     而 B 的 `criteria_index=2` 落在**丙** —— A 先勾掉甲 ⇒ 未勾清單位移
            //     ⇒ 一個署名落在呼叫端從來沒選過的那條標準上，**兩邊都回 Success**。
            //   📌 形狀照 `senate cmd msg --arg expect_uuid`：**序號會位移，文字不會。**
            //   ⛔ 選填 —— 不帶＝行為與過去完全一樣（既有呼叫端不受影響）。
            var aWantAsTyped = new List<int>(aWant);
            string aExpectRaw = GetArg(iArgs, "expect_text", "").Trim();
            if (aExpectRaw.Length > 0)
            {
                var aParts = aExpectRaw.Split('|')
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (aParts.Count != aWantAsTyped.Count)
                    throw new Exception($"[Task] expect_text 給了 {aParts.Count} 筆、"
                        + $"criteria_index 給了 {aWantAsTyped.Count} 個 —— **筆數必須相同**"
                        + "（用 `|` 分隔，照 criteria_index 的順序配對）。整批不執行");
                for (int i = 0; i < aParts.Count; i++)
                {
                    int v = aWantAsTyped[i];
                    string aSeen = aOpen[v - 1].Trim();
                    if (aSeen.StartsWith(aParts[i], StringComparison.Ordinal)) continue;
                    ioR.AppendLine("## blocked");
                    ioR.AppendLine($"- reason: `expect_text` 對不上 —— 序號 #{v} 現在指到的是");
                    ioR.AppendLine($"    「{Trunc(aSeen, 60)}」");
                    ioR.AppendLine($"  而你帶進來的是「{Trunc(aParts[i], 60)}」"
                        + " ⇒ **一個位元組都沒寫**，沒有任何格子被簽名。");
                    ioR.AppendLine("  📌 序號是**未勾清單**的序號 ⇒ 別人在你讀清單之後勾了任何一格，"
                        + "同一個號碼就指到另一條驗收標準。");
                    ioR.AppendLine($"  ▶ 重讀清單再決定：`run Task --arg op=check --arg index={aIndex}`");
                    throw new Exception("[Task] check 沒有落檔（expect_text 對不上 ⇒ 序號位移，整批不做）");
                }
            }

            // ⚠ **由大到小**套用 —— 勾掉一格會讓未勾清單縮短，小的先做會讓大的序號位移。
            aWant.Sort();
            aWant.Reverse();

            var aNow = DateTime.Now;
            string aNowUtc = UCL_TaskIO.NowUtc();
            var aHit = new List<string>();

            // ⭐ TASK-0163：本支的危害**不是掉更新，是簽到別的格子上。**
            //   序號是「未勾清單」的序號 ⇒ 別人在這中間勾掉任何一格，同一個號碼就指向**另一條驗收標準**，
            //   而 `op=check` 是**簽名行為**（勾完的行尾接上 `✅ <persona> <日期>`）
            //   ⇒ 失效樣子是「我的名字出現在一格我沒有驗過的標準上」，兩邊都回 Success。
            //   ⛔ 所以鎖內不只重讀，還要**比對文字**：把鎖外看到的那幾行原文帶進來當錨，
            //   對不上就整批不做（回 `Skip` ⇒ 零位元組）—— 序號可以位移，文字不會。
            var aWantAnchor = aWant.ToDictionary(v => v, v => aOpen[v - 1]);
            string aRaceNote = null;
            bool aFoundEntry = false;
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aFoundEntry = true;
                string aFresh = UCL_TaskIO.ReadCriteria(aIndex);          // 鎖內重讀那一欄
                var aFreshOpen = UCL_TaskIO.ListUncheckedCriteria(aFresh);
                foreach (int v in aWant)                                   // 已由大到小
                {
                    if (v > aFreshOpen.Count)
                    {
                        aRaceNote = $"序號 {v} 現在超出未勾清單（鎖內只剩 {aFreshOpen.Count} 格）"
                            + " —— 有人在這中間勾了格子";
                        return UCL_TaskWrite.Skip;
                    }
                    if (aFreshOpen[v - 1] != aWantAnchor[v])
                    {
                        aRaceNote = $"序號 {v} 指到的已經不是同一條驗收標準了"
                            + $"（我要簽的是「{Trunc(aWantAnchor[v], 40)}」，鎖內那一格是「{Trunc(aFreshOpen[v - 1], 40)}」）"
                            + " —— 序號位移了，整批不做";
                        return UCL_TaskWrite.Skip;
                    }
                    string aBody = UCL_TaskIO.CheckOffCriteria(ref aFresh, v, iActor, aNow);
                    if (aBody == null)
                    {
                        aRaceNote = $"序號 {v} 勾不起來（鎖內重讀的清單與預期不符）—— 整批不做";
                        return UCL_TaskWrite.Skip;
                    }
                    aHit.Add(aBody);
                }
                aHit.Reverse();   // 印出來照序號由小到大，讀的人才對得上剛才那份清單
                UCL_TaskIO.Touch(m, aNowUtc);
                return UCL_TaskWrite.Body(
                    $"{aNowUtc}　`check`　{iActor} 勾了 {aHit.Count} 格驗收標準（{string.Join("，", aWant.OrderBy(x => x))}）",
                    aFresh, "");
            });
            if (!aWrote)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine(aRaceNote != null
                    ? $"- reason: {aRaceNote} ⇒ **一個位元組都沒寫**，沒有任何格子被簽名。"
                    : (aFoundEntry
                        ? "- reason: 鎖內判定不成立 ⇒ **零寫入**。"
                        : $"- reason: 鎖內重讀時 TASK-{aIndex} 不在了（被刪或被搬）⇒ **寫入沒有發生**。"));
                ioR.AppendLine($"  ▶ 重跑一次不帶 `criteria_index` 看現在的清單：`run Task --arg op=check --arg index={aIndex}`");
                throw new Exception("[Task] check 沒有落檔（鎖內重判：序號位移或單子不在）");
            }

            // ── 回讀：分母從**磁碟**再數一次，不印剛才算出來的值 ──────
            string aBack = UCL_TaskIO.ReadCriteria(aIndex);
            int aBackDone = UCL_TaskIO.ListCheckedCriteria(aBack).Count;
            int aBackOpen = UCL_TaskIO.ListUncheckedCriteria(aBack).Count;

            ioR.AppendLine($"## ✅ 勾了 {aHit.Count} 格（署名 `{iActor}` {aNow:yyyy-MM-dd}）");
            foreach (string s in aHit) ioR.AppendLine($"- [x] {Trunc(s, 160)}");
            ioR.AppendLine();
            ioR.AppendLine($"- 已勾 {aDone.Count} → **{aBackDone}**　未勾 {aOpen.Count} → **{aBackOpen}**"
                + "（⭐ 這兩個後值是**回讀單檔**數的，不是寫入端的回傳值）");
            ioR.AppendLine("- ⚠ 勾**不會**推進 status —— 結單仍走 `op=resolve`（本 op 只動那一欄）。");
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
            // ⛔ 走既有的 `ParseTaskRef`（收 `TASK-0008` / `8` / `0008`）—— **不在這裡重造第二套**。
            // 🩸 血證 2026-09-06（summit 現場）：本行原本是裸 `int.TryParse` ⇒ `--arg target=TASK-0119` 直接失敗，
            //   而拋出的訊息是「**需要** --arg target」—— 同一份回傳檔的 `## Args` 區裡明明印著 `target = TASK-0119`。
            //   ⇒ 「**沒帶**」與「**帶了但解析不出**」在那句話上同形，而處置相反（補參數 ／ 換寫法）。
            //   ⚠ 更難發現的是它與 `epic` 篩選的分岔：同一份 ArgsSchema 寫著「epic：TASK-0008 / 8 皆可」，
            //   於是人會合理推論 target 也一樣 —— **一個系統裡兩套單號規矩，而只有一套寫在說明上。**
            string aTargetRaw = GetArg(iArgs, "target", "").Trim();
            if (aTargetRaw.Length == 0)
                throw new Exception("[Task] op=link 需要 --arg target=<對方單號>（收 TASK-0008 / 8 / 0008）");
            int aTarget = UCL_TaskIO.ParseTaskRef(aTargetRaw);
            if (aTarget <= 0)
                throw new Exception($"[Task] op=link 認不得的 target 參照 '{aTargetRaw}'"
                    + "（收 TASK-0008 / 8 / 0008）—— ⛔ 這不是「沒帶」，是**帶了但讀不出來**，不猜。");

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
            // ⭐ TASK-0163：結單的整段 RMW 進 `Mutate`，而**三道閘在鎖內重判一次**（形狀乙）。
            //   🩸 為什麼不是「閘在上面驗過就好」：那三道閘的輸入是**鎖外讀的狀態** ——
            //   別人在這中間掛上一個新 blocker、或把 QA 移掉，舊讀數會讓這一次照樣關單，
            //   而時間線留下的是「結單（原狀態 X）」這種**有出處而已經不為真**的句子。
            //   ⇒ 鎖內不成立就回 `Skip`（零位元組），並把原因帶出來報。
            string aRaceBlock = null;
            bool aFoundEntry = false;
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aFoundEntry = true;
                var aNowBlockers = UCL_TaskIO.OpenBlockers(m);
                if (aStatus == UCL_TaskStatus.done && aNowBlockers.Count > 0)
                {
                    aRaceBlock = $"鎖內重讀時它有 {aNowBlockers.Count} 個未解 blocker"
                        + $"（{string.Join("；", aNowBlockers)}）—— 上面那道閘讀的是鎖外的快照";
                    return UCL_TaskWrite.Skip;
                }
                string aNowQa = UCL_TaskIO.QaGateBlocked(m, iActor, aQaNote);
                if (aStatus == UCL_TaskStatus.done && aNowQa != null)
                {
                    aRaceBlock = $"鎖內重讀時 QA 閘不放行：{aNowQa}（有人在這中間改了參與者）";
                    return UCL_TaskWrite.Skip;
                }
                aFrom = m.status;
                m.status = aStatus;
                m.closed_at = aNow;
                if (aNote.Length > 0) m.resolution_note = aNote;
                if (aQaNote.Length > 0)
                    m.resolution_note = (m.resolution_note + "\n\n**QA 代簽紀錄**：" + aQaNote).Trim();
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line($"{aNow}　`{aStatus}`　{iActor} 結單（原狀態 {aFrom}）"
                    + (aNote.Length == 0 ? "" : $"：{aNote.Replace("\r", " ").Replace("\n", " ")}"));
            });
            if (!aWrote)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine(aRaceBlock != null
                    ? $"- reason: {aRaceBlock} ⇒ **一個位元組都沒寫**，單子沒有被關。"
                    : (aFoundEntry
                        ? "- reason: 鎖內判定不成立 ⇒ **零寫入**。"
                        : $"- reason: 鎖內重讀時 TASK-{aIndex} 不在了（被刪或被搬）⇒ **寫入沒有發生**。"));
                throw new Exception("[Task] resolve 沒有落檔（鎖內重判）");
            }
            // ⚠ 回報用落檔後那一份 —— 底下印的 closed_at／blocks 是它的欄位（鎖前那份的 closed_at 是空的）。
            e = UCL_TaskIO.Find(aIndex) ?? e;

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
            // ⭐ TASK-0163：整段判定＋落檔進 `Mutate`。這一支的判定**全部**吃 entry 的狀態
            //   （已關？有 blocker？有 QA？）⇒ 鎖外讀的話，`git_commit.py` 打進來的那一刻
            //   單子可能剛被別人關掉或剛被掛上 blocker，而這裡會照舊讀數推狀態 ——
            //   失效樣子是「commit 把一張已經有 blocker 的單推成 done」，時間線還留一行有出處的判定。
            bool aShaNew = false;
            var aFrom = e.status;
            string aVerdict = "";
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aShaNew = !m.commit_shas.Contains(aSha);
                if (aShaNew) m.commit_shas.Add(aSha);
                aFrom = m.status;
                var aBlockers = UCL_TaskIO.OpenBlockers(m);   // ⭐ 鎖內重讀的那一份（形狀乙：判定不用鎖外的舊讀數）
                if (m.IsClosed())
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
                    var aQa = m.QaPersonas();
                    if (aQa.Count > 0)
                    {
                        m.status = UCL_TaskStatus.in_review;
                        aVerdict = $"→ **in_review**（單上有 QA：{string.Join(" / ", aQa)}"
                            + " —— commit 不能替 QA 簽名）";
                    }
                    else
                    {
                        m.status = UCL_TaskStatus.done;
                        m.closed_at = aNow;
                        aVerdict = "→ **done**（這張單沒有指名 QA ⇒ 沒有人要驗，commit 直接結）";
                        // ⚠ 落差要出聲（basecamp 拍板 ③，TASK-0015）：
                        //   單上有 dev 以外的角色（pm / reviewer / design…）卻**沒有 qa** ⇒
                        //   「沒有人要驗」這個假設要攤在被影響的人面前，而不是靜默生效。
                        //   🩸 血證就是 TASK-0009 本身：basecamp 掛的是 pm，我的 commit 直接把它關了，
                        //     而她一整天都在驗我的交付。閘做對了它的事 —— 錯的是沒有人被告知。
                        //   ⛔ **警示不是擋**：擋會讓真正不需要 QA 的小單無法自動結，而那是設計要的。
                        var aNonDev = m.participants
                            .Where(p => p.role != UCL_TaskRole.dev)
                            .Select(p => $"{p.persona}({p.role})").Distinct().ToList();
                        if (aNonDev.Count > 0)
                            aVerdict += $"\n  ⚠ **本單沒有 QA 卻有其他角色：{string.Join("、", aNonDev)}**"
                                + " —— 若非預期請 reopen 並補 `op=assign --arg role=qa`"
                                + "（`pm` 不是 QA 閘：PM 排序、QA 簽名，混起來會讓「有人管」被讀成「有人驗」）";
                    }
                }
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line($"{aNow}　`{m.status}`　commit `{aSha}`（{aMode}）by {iActor}"
                    + (aShaNew ? "" : "（這個 sha 本來就在，沒重複加）"));
            });
            if (!aWrote)
                throw new Exception($"[Task] TASK-{aIndex} 掛 commit 沒有落檔 —— 鎖內重讀時那張單不在了"
                    + "（被刪或被搬）⇒ **寫入沒有發生**，那顆 sha 沒有掛上去。");
            // ⚠ 回報一律用**落檔後**那一份 —— 下面印的 status／commit_shas／closed_at 都是它的欄位。
            e = UCL_TaskIO.Find(aIndex) ?? e;

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

            // ① progress → Task 留言（＋時間線一筆 `wrapup` 事件，供晚安閘判定「今天收工過了」）
            // ⭐ TASK-0163 形狀甲：整段 RMW 走 `UCL_TaskIO.Mutate`（鎖內重讀 → 改 → 寫）。
            //   跟 `OpComment` 同族的那一格：`NextCommentId` 是 `max(id) + 1`，在鎖外算會**撞號**
            //   ⇒ 兩條 lane 同時收工、兩次整檔重寫，**其中一則收工紀錄靜默消失**
            //   （兩邊都回 Success、兩邊都印得出自己的 #N）。
            //   ⚠ 而本支多一格 `OpComment` 沒有的：`last_wrapup_at` 是**晚安收工閘的述詞來源**
            //   （述詞②＝`updated_at > last_wrapup_at`，TASK-0036）⇒ 它必須跟 `Touch` 落在**同一次寫入**裡；
            //   否則那道閘會讀到「`updated_at` 被別人推進了、而 `last_wrapup_at` 還是上一輪」——
            //   而那個組合它會判成「收工後又改了」，也就是**它會誤擋一個剛收完工的人**。
            // ⛔ 舊的 `[RMW-END]` 前哨在此退場 —— 跨度現在由型別決定（`m` 只活在 lambda 裡），不由註解宣告。
            int aCommentId = 0;
            var aFrom = e.status;
            string aTopicAtWrite = aTopic;
            bool aTopicLostInLock = false;
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                // ⛔ 形狀乙（判定搬進鎖內重做，@basecamp 2026-09-08 立的條文）：
                //   上面那道「給了 why 卻沒有 memory_topic」的閘判的是**鎖外**讀到的欄位
                //   ⇒ 這裡對重讀的那一份再判一次；不成立就回 null ⇒ **一個位元組都不寫**。
                aTopicAtWrite = (m.memory_topic ?? "").Trim();
                if (aWhy.Length > 0 && aTopicAtWrite.Length == 0)
                { aTopicLostInLock = true; return UCL_TaskWrite.Skip; }

                aFrom = m.status;
                aCommentId = UCL_TaskIO.NextCommentId(m);
                m.comments.Add(new UCL_TaskComment
                {
                    id = aCommentId,
                    persona = iActor,
                    at = aNow,
                    body = "**[收工 wrapup]**\n\n" + aProgress,
                });
                UCL_TaskIO.Touch(m, aNow);
                // ⚠ 等號陷阱（TASK-0036 驗收標準第三條）：`wrapup` 自己會 `Touch` ⇒ 這兩個欄位
                //   在寫完的當下**必然相等**。所以述詞②的判準必須是**嚴格大於** ——
                //   用 `>=` 的話「剛收完工」會被自己擋住，那是一隻修完立刻天天亮的警示。
                //   ⇒ 這裡刻意跟 `Touch` 共用同一個 `aNow`，讓「相等」是精確的而不是差幾毫秒。
                m.last_wrapup_at = aNow;
                return UCL_TaskWrite.Line($"{aNow}　`wrapup`　{iActor} 收工（狀態不動：{aFrom}）留言 #{aCommentId}");
            });
            if (!aWrote)
            {
                // ⚠ 兩種「沒寫成」要分得出來 —— 單子不在了 vs 鎖內判定不成立，處置不同，
                //   而它們在一個布林值上同形。
                ioR.AppendLine("## blocked");
                if (aTopicLostInLock)
                    ioR.AppendLine($"- reason: 鎖內重讀時 {e.Id} 的 `memory_topic` 是空的"
                        + $"（鎖外讀到的是 `{aTopic}`，中間被清掉了）⇒ **一個位元組都沒寫**。");
                else
                    ioR.AppendLine($"- reason: 鎖內重讀時 TASK-{aIndex} 不在了（被刪或被搬）⇒ **寫入沒有發生**。");
                ioR.AppendLine("  ⛔ 這不是「收工失敗」的泛稱：**進度沒有進磁碟**，記憶那半也沒有跑。");
                throw new Exception("[Task] wrapup 沒有落檔（鎖內重讀）");
            }

            ioR.AppendLine($"## ✅ {e.Id} 已收工（`wrapup`）");
            ioR.AppendLine($"- 狀態：**維持 `{aFrom}`** —— 收工不是結單也不是放棄（`{aFrom}` 是鎖內重讀的那一份）");
            ioR.AppendLine($"- 進度寫進留言 #{aCommentId}（進度真相源是 Task）");
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
                    //   ⚠ 這一段原本寫「併發安全完全依賴 RMW 中間沒有 yield 點」，而 TASK-0163 之後
                    //   **那句已經不為真**：安全來自 `UCL_TaskIO` 的那把鎖（唯一寫入面 `Mutate`／`Create`，
                    //   `Save` 已 private），⛔ 不再是靠這裡不出現 yield 點。
                    //   🩸 症狀仍然是**靜默的**：整檔覆蓋、留言消失、index 撞號 —— 沒有一格會紅。
                    //   ⚠ 而告警也換代了：`UCL_TaskIO.AssertHoldsRmwLock`（`Monitor.IsEntered`）——
                    //   它量的是「寫的時候鎖在手上嗎」，不是「我在主緒嗎」。舊名 `AssertMainThread` 已不存在。
                    //   （通則寫在 UCL_TaskIO 檔頭；這裡指名道姓，因為通則會被讀成建議。）
                    // ⚠ 主題名用**鎖內重讀**的那一份（`aTopicAtWrite`）—— 鎖外那個 `aTopic` 只是提示。
                    var (aOk, aOut, aDetail) = await UCL_TaskWorkMemoryCli.AddAsync(
                        aTopicAtWrite, aType, aId, aTitle, aTmp, iActor);
                    ioR.AppendLine();
                    if (aOk)
                    {
                        ioR.AppendLine($"- 🧠 已寫進工作記憶：`{aTopicAtWrite}` / `{aType}` / `{aId}`（代跑 work_memory.py）");
                        if (aOut.Length > 0) ioR.AppendLine($"    · 工具輸出：{Trunc(aOut, 200)}");
                    }
                    else
                    {
                        // ⚠ 大聲但不致命：進度已經落盤（那是主線），記憶那半沒寫成要看得見
                        ioR.AppendLine($"- ⚠ **記憶那半沒寫成**（{aDetail}）—— 進度已落盤，"
                            + "但「為什麼卡住」還沒有家。");
                        ioR.AppendLine($"    · 手動補：`python <UCL_Core>/Tools~/AgentCommands/work_memory.py add"
                            + $" --topic {aTopicAtWrite} --type {aType} --id {aId} --title \"{aTitle}\" --body-file <檔> --by {iActor}`");
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

            // ⚠ 通知用**落檔之後**那一份（比照 `OpComment`）：`e` 是鎖前讀的提示，而要 @ 的參與者
            //   清單可能在鎖內那一刻已經不同（別人剛 assign）。讀不到就退回用提示，
            //   ⛔ 不因為讀不到就不發（那會讓「發不出去」與「沒有人該被通知」同形）。
            var aFresh = UCL_TaskIO.Find(aIndex) ?? e;
            bool aNotified = await UCL_TaskNotify.PostAsync(aFresh, UCL_TaskNotify.Kind.Comment, iActor,
                "", "**[收工 wrapup]**\n\n" + aProgress, iArgs);
            AppendNotifyLine(ioR, aFresh, iActor, aNotified);
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

            // ⭐ TASK-0163 形狀乙：**候選清單從這裡起降級成「提示」**。
            //   上面那份 `aCandidates` 是在迴圈**外**讀的，而本迴圈每一輪尾端有 `await`
            //   ⇒ 第 k 張落檔的時候，那份判定已經是 k-1 次 await 之前的事了。
            //   而 sweep 是 check-then-act：判定（in_progress 且 ≥N 天沒動）與動作（改回 todo）
            //   必須原子，否則別人剛剛才認領／剛剛才留言的單會被我釋放掉，
            //   🩸 而失效的樣子是**時間線上多一行「sweep 釋放」而它的理由已經不為真** —— 沒有任何一層會叫。
            //   ⇒ 走 `UCL_TaskIO.Mutate`：鎖內重讀那張單、**把判定原樣再跑一次**，不成立就回 null 不寫。
            int aDone = 0;
            int aSkipped = 0;
            foreach (var aHint in aCandidates)
            {
                string aTs = UCL_TaskIO.NowUtc();
                var aFromCaptured = UCL_TaskStatus.todo;
                int aDaysCaptured = 0;
                bool aWrote = UCL_TaskIO.Mutate(aHint.index, e =>
                {
                    // ⛔ 判定在鎖內對**重讀的 e** 重做一次 —— 不用 aHint 的欄位。
                    if (e.status != UCL_TaskStatus.in_progress) return UCL_TaskWrite.Skip;
                    int aDays = e.DaysSinceUpdate(aNow);
                    if (aDays < UCL_TaskIO.STALE_DAYS) return UCL_TaskWrite.Skip;
                    if (aOnly.Length > 0 && e.RolesOf(aOnly).Count == 0) return UCL_TaskWrite.Skip;

                    aFromCaptured = e.status;
                    aDaysCaptured = aDays;
                    e.status = UCL_TaskStatus.todo;
                    UCL_TaskIO.Touch(e, aTs);
                    // ⚠ 時間線一定要留一行說**為什麼**被釋放 ——
                    //   沒有這行的話，明天看到它從 in_progress 變回 todo 會像有人手動改的
                    return UCL_TaskWrite.Line($"{aTs}　`todo`　sweep 釋放（{aFromCaptured} 已 {aDaysCaptured} 天沒動作，"
                        + $"逾期 {UCL_TaskIO.STALE_DAYS} 天門檻）by {iActor}");
                });
                if (!aWrote)
                {
                    aSkipped++;
                    ioR.AppendLine($"- ⏭ {aHint.Id} **跳過**：鎖內重讀之後它已經不符合釋放條件"
                        + "（有人剛動了它，或它已經不是 `in_progress`）⇒ 這不是失敗，是判定在寫入時重做的結果。");
                    continue;
                }
                bool aOk = await UCL_TaskNotify.PostAsync(aHint, UCL_TaskNotify.Kind.Status, iActor,
                    $"{aFromCaptured} → **todo**（sweep：認領後 {aDaysCaptured} 天沒動，釋放回待領）", iCallerArgs: iArgs);
                AppendNotifyLine(ioR, aHint, iActor, aOk);
                aDone++;
            }
            ioR.AppendLine($"- ✅ 已釋放 **{aDone}** 張回 `todo`（每張的時間線都留了釋放理由）");
            if (aSkipped > 0)
                ioR.AppendLine($"- ⏭ **{aSkipped}** 張在鎖內重判時已不符條件而跳過"
                    + "（候選清單是迴圈外的讀數 ⇒ 它是提示不是判定）。");
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

            string aNow = UCL_TaskIO.NowUtc();
            // ⭐ TASK-0163：數幾筆被移除 → 移除 → 落檔，是同一段 RMW（`aRemoved` 是判斷也是回報）。
            //   ⚠ 「找不到那個人 ⇒ 什麼都沒寫」這個出口照形狀乙搬進鎖內：回 `Skip` ⇒ 零位元組，
            //   而它跟「單子不在了」用 `aFoundEntry` 分辨 —— 兩者都讓 `Mutate` 回 false，
            //   ⛔ 而處置不同（前者是正常結果，後者是寫入沒發生）。
            int aRemoved = 0;
            bool aFoundEntry = false;
            var aAfter = new List<UCL_TaskParticipant>();
            bool aWrote = UCL_TaskIO.Mutate(aIndex, m =>
            {
                aFoundEntry = true;
                int aBefore = m.participants.Count;
                m.participants.RemoveAll(p =>
                    string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase)
                    && (!aHasRole || p.role == aRole));
                aRemoved = aBefore - m.participants.Count;
                aAfter = new List<UCL_TaskParticipant>(m.participants);
                if (aRemoved == 0) return UCL_TaskWrite.Skip;
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line($"{aNow}　`unassign`　{iActor} 移除 {aTarget}"
                    + (!aHasRole ? "（全部角色）" : $"（role={aRole}）") + $"　共 {aRemoved} 筆");
            });
            if (!aWrote)
            {
                if (!aFoundEntry)
                    throw new Exception($"[Task] TASK-{aIndex} 移除沒有落檔 —— 鎖內重讀時那張單不在了"
                        + "（被刪或被搬）⇒ **寫入沒有發生**。");
                ioR.AppendLine($"## {e.Id} 沒有變更");
                ioR.AppendLine($"- {aTarget}"
                    + (!aHasRole ? "" : $"（role={aRole}）")
                    + " 不在參與者裡 ⇒ **什麼都沒寫**（這是「找不到」，不是「移除成功」）");
                ioR.AppendLine($"- 現有參與：{ParticipantsOf(aAfter)}");
                return;
            }
            e.participants = aAfter;   // 回報用落檔後那一份（`e` 是鎖前的提示）
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

        static bool AddParticipant(UCL_TaskEntry e, string iPersona, UCL_TaskRole iRole, string iNow)
        {
            foreach (var p in e.participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase)
                    && p.role == iRole) return false;
            e.participants.Add(new UCL_TaskParticipant
            { persona = iPersona, role = iRole, assigned_at = iNow });
            return true;
        }

        static string Participants(UCL_TaskEntry e) => ParticipantsOf(e.participants);

        /// <summary>參與者清單的字串化 —— 收 list 而不是 entry，
        /// 讓「落檔後那一份」也印得出來（`Mutate` 之後呼叫端手上的 `e` 是鎖前的提示）。</summary>
        static string ParticipantsOf(List<UCL_TaskParticipant> iList)
        {
            if (iList == null || iList.Count == 0) return "**無**（沒有人在做這件事）";
            return string.Join("、", iList.Select(p => $"{p.persona}({p.role})"));
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

        // 區塊職責：驗收標準那一段裡「有幾行文字」—— 給「零個勾選格」那句話當定語。
        // 物理意義：**有寫東西但沒有一行是 `- [ ]`** 與 **那一段是空的**，處置不同：
        //           前者要改寫成勾選格，後者是根本還沒寫驗收標準。
        // 🩸 TASK-0163：這兩種都會讓 `op=check` 回 0/0，而它原本只有「全部都勾了」一句話可講。
        static int CountCriteriaTextLines(string iCriteria)
        {
            if (string.IsNullOrWhiteSpace(iCriteria)) return 0;
            int n = 0;
            foreach (string aLine in iCriteria.Replace("\r", "").Split('\n'))
                if (aLine.Trim().Length > 0) n++;
            return n;
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
