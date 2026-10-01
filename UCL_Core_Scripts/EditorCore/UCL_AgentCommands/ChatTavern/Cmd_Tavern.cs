// UCL Chat Tavern — 整合型 Cmd（prototype v1）
// 單一 handler，用 args["op"] 分派到內部子操作。
// 設計取捨：所有酒館操作走同一個 CommandType="Tavern"，避免 registry 暴增 8~10 個 Cmd。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Debug = UnityEngine.Debug;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 酒館聊天室整合指令。
    /// 第一個 arg "op" 表示子操作類型；後續 args 為該操作的參數。
    /// </summary>
    public class Cmd_Tavern : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Tavern";
        public override string ShortDescription => "Chat Tavern — 多 agent 聊天室（op 派遣式）";
        public override string ArgsSchema =>
            "op=listrooms|read|members|catchup|query|events_since（**只剩讀取**；發文與寫入全在 Senate）\n" +
            "post: ⛔ 已退場（TASK-0366）⇒ `senate cmd tavern-post --arg persona=<你> --arg-file body=<檔>`（系統發言：`tavern-post-system --arg sender=<身分>`）\n" +
            "wait／wait_check: ⛔ 已退場（TASK-0364）⇒ 等人回話用 `senate cmd tavern-wait`\n" +
            "task_*／inbox_read／session_enter／create_trpg_room: ⛔ 已退場（TASK-0364；酒館任務板整組移除）—— 開房 ⇒ `senate cmd channel --arg op=create`\n" +
            "createroom: ⛔ 已搬到 Senate（TASK-0328）⇒ `senate cmd channel --arg op=create --arg room=<id> [--arg name=] [--arg description=] [--arg category=<分類>]`\n" +
            "join／leave: ⛔ 已廢棄（TASK-0328；Tim：發言不用進房）\n" +
            "note_write／note_append／note_read／note_list／note_delete: ⛔ 留言本已整組移除（TASK-0328；skill 沒有使用、最後一次寫入是 5 月）\n" +
            "listrooms: (無參數)\n" +
            "read: room=房間ID [tail=N] [from=N] [to=N] [since_seq=N] [limit=N] [search=keyword]\n" +
            "members: room=房間ID\n" +
            "catchup: persona=<你> [room=] [min=] [advance=0]\n" +
            "events_since: room=房間ID [since_seq=N] [filter_type=csv] [limit=N]";
        // ExampleArgs：agent-neutral 範例 —— 只剩讀取 op
        public override string ExampleArgs =>
            "op=read;room=tavern;tail=10";
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md";

        // ===========================================================
        // 區塊職責：機器可讀的 op 參數規格 — 由 Cmd_ExportCmdSchema 反射匯出成 commands_schema.json。
        // 物理意義：本表的**唯一事實來源是本檔下方的 switch 與各 Op_ 方法**：
        //          Required = 該 Op_ 方法真的會 RejectLastOp("...缺少 X") 的那些參數。
        // 數值影響：只影響 client 端預檢的嚴格度，不影響 Editor 端執行 —— server 端永遠是權威。
        //          **多寫 required 會擋掉合法呼叫** ⇒ 拿不準時寧可少寫。
        // ⚠ 退場的 op 留**空規格**（TASK-0328 起的慣例）：參數不驗、一定走到 Op_Retired 的指路，
        //   ⛔ 不會先被參數檢查擋成別的錯（那會讓「它搬到哪了」這句話永遠印不出來）。
        // ===========================================================
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                ["listrooms"] = new UCL_CmdOpSpec(),
                ["read"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["members"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                // 🩸 TASK-0069：`catchup` 曾經只在 switch 的 case 裡、沒進這張表 —— 漏宣告的 op 在匯出產物上與「不存在」同形。
                ["catchup"] = new UCL_CmdOpSpec { Required = new[] { "persona" } },
                ["events_since"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["query"] = new UCL_CmdOpSpec(),

                // ─── ⛔ 退場（只回指路）──────────────────────────────────────
                ["post"] = new UCL_CmdOpSpec(),
                ["createroom"] = new UCL_CmdOpSpec(),
                ["create_trpg_room"] = new UCL_CmdOpSpec(),
                ["join"] = new UCL_CmdOpSpec(),
                ["leave"] = new UCL_CmdOpSpec(),
                ["wait"] = new UCL_CmdOpSpec(),
                ["wait_check"] = new UCL_CmdOpSpec(),
                ["note_write"] = new UCL_CmdOpSpec(),
                ["note_append"] = new UCL_CmdOpSpec(),
                ["note_read"] = new UCL_CmdOpSpec(),
                ["note_list"] = new UCL_CmdOpSpec(),
                ["note_delete"] = new UCL_CmdOpSpec(),
                ["task_create"] = new UCL_CmdOpSpec(),
                ["task_claim"] = new UCL_CmdOpSpec(),
                ["task_progress"] = new UCL_CmdOpSpec(),
                ["task_done"] = new UCL_CmdOpSpec(),
                ["task_release"] = new UCL_CmdOpSpec(),
                ["task_force_reclaim"] = new UCL_CmdOpSpec(),
                ["task_review_request"] = new UCL_CmdOpSpec(),
                ["task_reject"] = new UCL_CmdOpSpec(),
                ["task_reopen"] = new UCL_CmdOpSpec(),
                ["task_next"] = new UCL_CmdOpSpec(),
                ["task_state"] = new UCL_CmdOpSpec(),
                ["task_list"] = new UCL_CmdOpSpec(),
                ["inbox_read"] = new UCL_CmdOpSpec(),
                ["session_enter"] = new UCL_CmdOpSpec(),
            }
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                RejectLastOp(args, "缺少 op 參數。請參考 ArgsSchema。");
                return;
            }
            try
            {
                switch (op)
                {
                    case "listrooms": Op_ListRooms(args); break;
                    // ⏱ 移出主執行緒（TASK-0162）：`read` 純讀；`catchup` 只寫該 persona 自己的游標與 inbox。
                    case "read": await UCL_AgentCmdOffload.EnterBackground(args); Op_Read(args); break;
                    case "members": Op_Members(args); break;
                    case "events_since": Op_EventsSince(args); break;
                    case "query": Op_Query(args); break;
                    case "catchup": await UCL_AgentCmdOffload.EnterBackground(args); Op_Catchup(args); break;
                    case "post":
                    case "createroom":
                    case "create_trpg_room":
                    case "join":
                    case "leave":
                    case "wait":
                    case "wait_check":
                    case "note_write":
                    case "note_append":
                    case "note_read":
                    case "note_list":
                    case "note_delete":
                    case "task_create":
                    case "task_claim":
                    case "task_progress":
                    case "task_done":
                    case "task_release":
                    case "task_force_reclaim":
                    case "task_review_request":
                    case "task_reject":
                    case "task_reopen":
                    case "task_next":
                    case "task_state":
                    case "task_list":
                    case "inbox_read":
                    case "session_enter": Op_Retired(args, op); break;
                    default:
                        RejectLastOp(args, $"未知 op：{op}");
                        break;
                }
            }
            catch (Exception ex)
            {
                FailLastOp(args, $"執行 op={op} 失敗：{ex.Message}\n{ex.StackTrace}");
                throw;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 區塊職責：**已退場的 op** 一律走這裡 —— 印「搬到哪／為什麼廢棄」，⛔ 不做任何寫入。
        // 物理意義：走到這裡的人手上就是舊指令 ⇒ 給他那一行要怎麼改，⛔ 不是只回「未知 op」。
        //   · post ⇒ Senate `tavern-post`／`tavern-post-system`（TASK-0366：組訊息與寫入都在 Senate）
        //   · wait／wait_check ⇒ Senate `tavern-wait`（TASK-0364：非同步等待引擎移除）
        //   · task_*／inbox_read／session_enter／create_trpg_room ⇒ 酒館任務板整組移除（TASK-0364；Tim 2026-10-01 拍板）
        //     —— 既有 `rooms/<room>/events/` 等資料留作紀錄，沒有刪
        //   · createroom ⇒ Senate `channel --arg op=create`（TASK-0328）
        //   · join／leave ⇒ 廢棄（Tim：發言不用進房）
        //   · note_* ⇒ 留言本整組移除（TASK-0328；既有 `rooms/<room>/notes/*.md` 留作紀錄）
        // ===========================================================
        void Op_Retired(Dictionary<string, string> args, string op)
        {
            string how;
            string task;
            if (op == "post")
            {
                task = "TASK-0366，2026-10-01";
                how = "發文已搬到 Senate：`senate cmd tavern-post --arg persona=<你> --arg-file body=<檔>`"
                      + "（沒有 persona 的系統發言：`senate cmd tavern-post-system --arg sender=<身分>`；Editor 內部呼叫端走 `UCL_TavernSenatePost`）";
            }
            else if (op == "wait" || op == "wait_check")
            {
                task = "TASK-0364，2026-10-01";
                how = "非同步等待引擎已移除 —— 等人回話用 `senate cmd tavern-wait --arg persona=<你>`";
            }
            else if (op.StartsWith("task_") || op == "inbox_read" || op == "session_enter" || op == "create_trpg_room")
            {
                task = "TASK-0364，2026-10-01";
                how = "酒館任務板整組移除（沒有 skill 在用；既有資料留作紀錄）"
                      + (op == "create_trpg_room" ? "；開房改用 `senate cmd channel --arg op=create --arg room=trpg-<戰役>`" : "")
                      + (op == "inbox_read" ? "；收件匣由 `senate cmd tavern-catchup --arg persona=<你>` 一起列出" : "")
                      + (op.StartsWith("task_") ? "；跨 agent 任務走 `senate cmd task`" : "");
            }
            else
            {
                task = "TASK-0328，2026-09-28";
                how = op == "createroom"
                    ? "已搬到 Senate：`senate cmd channel --arg op=create --arg room=<id> [--arg name=<顯示名>] [--arg description=...] [--arg category=<分類>]`"
                      + "（⚠ 沒給分類 ⇒ 不會轉發到 Discord）"
                    : op == "join" || op == "leave"
                        ? "已廢棄 —— 發言不用進房（直接 `senate cmd tavern-post --arg room=<房>`）"
                        : "留言本已整組移除 —— skill 沒有使用、最後一次寫入是 5 月；既有的 `rooms/<room>/notes/*.md` 留作紀錄";
            }
            RejectLastOp(args, $"⛔ op={op} 已退場（{task}）：{how}");
        }

        // ===========================================================
        // 區塊：op=listrooms
        // ===========================================================
        void Op_ListRooms(Dictionary<string, string> args)
        {
            var list = UCL_ChatTavernIO.LoadRooms();
            var sb = new System.Text.StringBuilder();
            sb.Append("# 🍺 Rooms\n\n");
            if (list.rooms.Count == 0) sb.Append("_(尚無房間)_\n");
            else
            {
                foreach (var r in list.rooms)
                {
                    int seq = UCL_ChatTavernIO.ReadCurrentSeq(r.id);
                    sb.Append($"- `{r.id}` — {r.name} (seq={seq}) — {r.description}\n");
                }
            }
            UCL_ChatTavernRender.WriteLastOp(sb.ToString(), args, "tavern");
            Debug.Log($"[Tavern] listrooms → {list.rooms.Count} rooms");
        }

        // ===========================================================
        // 區塊：op=read — 切片查詢
        // ===========================================================
        // ===========================================================
        // 區塊職責：op=catchup —— 叮／醒來的酒館 catch-up（`senate ucmd run Tavern --arg op=catchup` 那條路）。
        // 物理意義：組裝與游標邏輯在 SCP_Core `SCP_TavernCatchup`（TASK-0303）—— Senate 的 morning-catchup
        //          呼叫同一份，Editor 不再有自己的一份。本方法只解參數、落回傳檔、推游標。
        // ⚠ 順序：**先落回傳檔、再推游標**。舊版反過來（Build 內就推了），回傳檔寫不出來時訊息已被標成已讀。
        // 數值影響：唯一寫入是游標推進（SCP_TavernCursor，跨 process 鎖）＋一份回傳檔。
        // ===========================================================
        void Op_Catchup(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(persona))
            { RejectLastOp(args, "catchup 缺少 persona（要知道是誰的游標與 inbox）"); return; }

            string dataRoot = UCL_AgentCommandsPath.DataRoot.Replace('\\', '/');
            var built = SCP.Core.Tavern.SCP_TavernCatchup.Build(
                dataRoot, Awakening.UCL_AwakeningService.LettersDir.Replace('\\', '/'), persona,
                GetArg(args, "room", "tavern"),
                ParseIntArg(args, "min", 0),
                GetArg(args, "quiet_system", "1") != "0",
                GetArg(args, "include_self", "0") == "1",
                ParseIntArg(args, "inbox_show", 0));

            string path = UCL_LettersPath.CmdPayload(persona, "ding", "brief");
            SCP.Core.Letters.SCP_CmdPayload.Write(path, built.Body
                + "- 游標：推進中…（若停在這行，代表推進那一步沒跑完 —— 下次會重讀這一段）\n");
            var (cursorLine, advancedTo) = SCP.Core.Tavern.SCP_TavernCatchup.AdvanceAfterWrite(
                dataRoot, persona, built, GetArg(args, "advance", "1") != "0");
            SCP.Core.Letters.SCP_CmdPayload.Write(path, built.Body + cursorLine + Environment.NewLine);
            int unread = built.Unread;
            UCL_AgentCommandRunner.ReportOutputFile(args, path);
            UCL_AgentCommandRunner.ReportOutputValue(args, "unread", unread.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "cursor_advanced_to", advancedTo ?? "(未推進)");
            Debug.Log($"[Tavern] catchup {persona} → unread={unread} cursor={advancedTo ?? "(未推進)"} → {path}");
        }

        // ===========================================================
        // 區塊職責：op=query —— 酒館訊息查詢的**入口**（Tim 2026-08-20 拍板取代 tavern_query.py）。
        // 物理意義：本方法只做三件事：解參數 → 呼叫 `UCL_TavernQueryService` → 落回傳檔。
        //          **查詢與呈現邏輯一行都不寫在這裡** —— 那一層要能被後台頁與 catchup 共用，
        //          寫進 Cmd 的話第二個呼叫端只能複製一份（Tim：邏輯抽 static class，不放 Cmd 內）。
        // 數值影響：純讀。不寫訊息、不推游標、不動金流。
        // ===========================================================
        void Op_Query(Dictionary<string, string> args)
        {
            string kind = GetArg(args, "kind", "tail").Trim().ToLowerInvariant();
            string persona = GetArg(args, "persona", "").Trim();
            string room = GetArg(args, "room", "");
            string since = GetArg(args, "since", "");
            int limit = ParseIntArg(args, "limit", 0);
            string md;
            switch (kind)
            {
                case "rooms":
                    md = UCL_TavernQueryService.Rooms(string.IsNullOrEmpty(since) ? "24h" : since);
                    break;
                case "tail":
                    md = UCL_TavernQueryService.Tail(room, limit);
                    break;
                case "search":
                    md = UCL_TavernQueryService.Search(GetArg(args, "keyword", ""), room, since,
                            GetArg(args, "case_sensitive", "0") == "1", limit);
                    break;
                case "by_sender":
                    md = UCL_TavernQueryService.BySender(GetArg(args, "sender", ""), since, limit);
                    break;
                case "timeline":
                    md = UCL_TavernQueryService.Timeline(string.IsNullOrEmpty(since) ? "24h" : since, limit);
                    break;
                case "stats":
                    md = UCL_TavernQueryService.Stats(string.IsNullOrEmpty(since) ? "24h" : since);
                    break;
                case "seq":
                    md = UCL_TavernQueryService.Seq(room,
                            ParseIntArg(args, "seq", 0), ParseIntArg(args, "from", 0), ParseIntArg(args, "to", 0),
                            ParseIntArg(args, "last", 0),
                            GetArg(args, "sender_persona", ""), GetArg(args, "sender", ""),
                            GetArg(args, "tag", ""), GetArg(args, "grep", ""),
                            GetArg(args, "full", "0") == "1");
                    break;
                default:
                    RejectLastOp(args, $"未知 query kind：{kind}"
                        + "（rooms / tail / search / by_sender / timeline / stats / seq）");
                    return;
            }

            // 落回傳檔：沒帶 persona 時退回 _last_op.md —— 兩條路都存在，且**回傳檔會說走了哪條**，
            // 因為「檔案在哪」是下一步要 Read 的東西，猜錯就是讀到別人的或讀到舊的。
            if (string.IsNullOrEmpty(persona))
            {
                UCL_ChatTavernRender.WriteLastOp(md, args, "tavern");
                Debug.Log($"[Tavern] query kind={kind} → _last_op.md（未帶 persona；帶了就落 letters/<persona>/cmd/）");
                return;
            }
            string path = UCL_LettersPath.CmdPayload(persona, "tavern", "query");
            UCL_LettersPath.EnsurePayloadDir(path);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, md, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            UCL_AgentCommandRunner.ReportOutputFile(args, path);
            Debug.Log($"[Tavern] query kind={kind} → {path}");
        }

        void Op_Read(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp(args, "read 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp(args, $"房間不存在：{roomId}"); return; }

            string search = GetArg(args, "search", "");
            int tail = ParseIntArg(args, "tail", 0);
            int from = ParseIntArg(args, "from", 0);
            int to = ParseIntArg(args, "to", 0);
            int since = ParseIntArg(args, "since_seq", -1);
            int limit = ParseIntArg(args, "limit", 0);

            List<UCL_ChatMessage> messages;
            string title;
            if (!string.IsNullOrEmpty(search))
            {
                messages = UCL_ChatTavernIO.Search(roomId, search, limit > 0 ? limit : UCL_ChatTavernSettings.SearchLimit);
                title = $"🔍 {room.name} — 搜尋 \"{search}\"（命中 {messages.Count}）";
            }
            else if (since >= 0)
            {
                messages = UCL_ChatTavernIO.Since(roomId, since, limit > 0 ? limit : UCL_ChatTavernSettings.SinceLimit);
                title = $"📥 {room.name} — since_seq={since}（{messages.Count} 筆）";
            }
            else if (from > 0 || to > 0)
            {
                messages = UCL_ChatTavernIO.Range(roomId, from, to > 0 ? to : int.MaxValue);
                title = $"📐 {room.name} — seq {from}..{(to > 0 ? to.ToString() : "end")}（{messages.Count} 筆）";
            }
            else
            {
                // 純尾讀的筆數：tail > limit > 後台預設。
                // 2026-07-31（Tim 拍板）：`limit` 在本分支**改為 tail 的同義字**，不再靜默丟掉。
                //   病灶：limit 只在 search / since 分支生效，純尾讀吃 tail —— 打 `limit=12`
                //   會被讀進來、解析成 12、然後丟掉，回你預設筆數且零徵兆（實測代價 66k token）。
                //   判準（apex-one 2026-07-31）：**寬容的正確判準不是「守衛會不會很煩」，
                //   是「被擋下來的那些呼叫裡，有沒有一個是對的」。** 打 limit 想少讀幾筆的人
                //   意圖百分之百正確，擋他換不到任何東西 → 收下它，但要出聲說「我當 tail 用了」。
                //   （未知鍵如 `tial=12` 是另一種病：沒有正當用例，該在 schema 層擋 —— 另案。）
                int n = tail > 0 ? tail : (limit > 0 ? limit : UCL_ChatTavernSettings.ReadTailCount);
                if (tail <= 0 && limit > 0)
                {
                    Debug.LogWarning($"[Tavern] op=read 純尾讀不吃 limit，已當成 tail={limit} 使用"
                                     + "（下次直接帶 --arg tail= 更精確）");
                }
                messages = UCL_ChatTavernIO.Tail(roomId, n);
                title = $"🍺 {room.name} — 最新 {messages.Count} 筆"
                        + (tail <= 0 && limit > 0 ? $"（`limit={limit}` 已當成 tail 用）" : "");
            }
            string md = UCL_ChatTavernRender.RenderMessages(title, messages);
            UCL_ChatTavernRender.WriteLastOp(md, args, "tavern");
            Debug.Log($"[Tavern] read {roomId} → {messages.Count} messages");
        }

        // ===========================================================
        // 區塊：op=members
        // ===========================================================
        void Op_Members(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp(args, "members 缺少 room"); return; }
            var members = UCL_ChatTavernIO.LoadMembers(roomId);
            var idents = UCL_ChatTavernIO.LoadIdentities();
            var sb = new System.Text.StringBuilder();
            sb.Append($"# 👥 Members of `{roomId}` ({members.member_ids.Count})\n\n");
            foreach (var mid in members.member_ids)
            {
                var ident = idents.identities.Find(x => x.id == mid);
                if (ident == null) sb.Append($"- `{mid}` _(no identity record)_\n");
                else sb.Append($"- `{ident.id}` — **{ident.display_name}** ({ident.kind})\n");
            }
            UCL_ChatTavernRender.WriteLastOp(sb.ToString(), args, "tavern");
            Debug.Log($"[Tavern] members {roomId} → {members.member_ids.Count}");
        }

        // ===========================================================
        // helper
        // ===========================================================

        static int ParseIntArg(Dictionary<string, string> args, string key, int def)
        {
            string s = GetArg(args, key, "");
            return int.TryParse(s, out var v) ? v : def;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }


        // ===========================================================
        // 區塊：op=events_since
        // 區塊職責：給 agent re-enter 看「上次離開後發生什麼」delta 視角
        // 物理意義：events.jsonl 是 truth，task_list / task_state 都是 snapshot；
        //          events_since 印 [since_seq+1, latest] 之間的事件 timeline。
        // 數值影響：純查詢，不寫 events / inbox / 衍生 cache。
        // 參數：
        //   room          (req) — 房間 ID
        //   since_seq     (opt, default=0) — 從哪個 seq 之後開始列（不含此筆）
        //   filter_type   (opt) — CSV 過濾 event type，例 "task_claim,task_done"；空=全部
        //   limit         (opt, default=50) — 最多列幾筆（避免 events 太長爆量）
        // ===========================================================
        void Op_EventsSince(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string sinceStr = GetArg(args, "since_seq", "0");
            string filterCsv = GetArg(args, "filter_type", "");
            string limitStr = GetArg(args, "limit", "50");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp(args, "events_since 缺少 room"); return; }

            // 參數解析：since_seq < 0 → clamp 0；limit <= 0 → 預設 50
            if (!int.TryParse(sinceStr, out int sinceSeq) || sinceSeq < 0) sinceSeq = 0;
            if (!int.TryParse(limitStr, out int limit) || limit <= 0) limit = 50;

            // type 過濾 set；空集 → 不過濾
            var typeFilter = new HashSet<string>();
            if (!string.IsNullOrEmpty(filterCsv))
            {
                foreach (var t in filterCsv.Split(',')) { var s = t.Trim(); if (!string.IsNullOrEmpty(s)) typeFilter.Add(s); }
            }

            // 讀全 events，篩 seq > sinceSeq；按 seq 升冪（既有實作就是 append 順序，但為求穩仍排序）
            var all = UCL_ChatTavernQuestIO.LoadAllEvents(roomId);
            var deltas = new List<UCL_QuestEvent>();
            foreach (var ev in all)
            {
                if (ev.seq <= sinceSeq) continue;
                if (typeFilter.Count > 0 && !typeFilter.Contains(ev.type)) continue;
                deltas.Add(ev);
            }
            deltas.Sort((a, b) => a.seq.CompareTo(b.seq));

            int totalAfter = deltas.Count;                    // 過濾後總筆數（給「還有 N 筆未顯示」提示用）
            bool truncated = totalAfter > limit;              // 是否有截斷
            if (truncated) deltas = deltas.GetRange(0, limit);

            int latestSeq = all.Count > 0 ? all[all.Count - 1].seq : 0;

            // 渲染 markdown timeline — 給 agent 直接 catch up
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# 🕒 events_since — `{roomId}`");
            sb.AppendLine();
            sb.AppendLine($"- since_seq: **{sinceSeq}** → latest_seq: **{latestSeq}**");
            sb.AppendLine($"- delta count: **{totalAfter}**" + (truncated ? $" (顯示前 {limit} 筆，請拉大 limit 或縮窄 filter_type)" : ""));
            if (typeFilter.Count > 0) sb.AppendLine($"- filter_type: {string.Join(",", typeFilter)}");
            sb.AppendLine();

            if (deltas.Count == 0)
            {
                sb.AppendLine("_(無新事件 — 自上次離開後本房安靜如雞)_");
            }
            else
            {
                sb.AppendLine("| seq | ts | type | actor | task | summary |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var ev in deltas)
                {
                    // 從 data 萃取 1~2 個關鍵欄位濃縮顯示（lease_until / summary / reason / status）
                    string detail = "";
                    if (ev.data != null && ev.data.Count > 0)
                    {
                        var pieces = new List<string>();
                        // 偏好順序：summary > reason > lease_until > 其它頭兩個
                        if (ev.data.TryGetValue("summary", out var s)) pieces.Add(Truncate(s, 40));
                        else if (ev.data.TryGetValue("reason", out var r)) pieces.Add("reason: " + Truncate(r, 40));
                        else if (ev.data.TryGetValue("lease_until", out var lu)) pieces.Add("lease→" + lu);
                        else
                        {
                            int taken = 0;
                            foreach (var kv in ev.data)
                            {
                                pieces.Add($"{kv.Key}={Truncate(kv.Value, 20)}");
                                if (++taken >= 2) break;
                            }
                        }
                        detail = string.Join("; ", pieces);
                    }
                    sb.AppendLine($"| {ev.seq} | {ev.ts} | `{ev.type}` | {ev.actor} | {ev.task_id ?? "-"} | {detail} |");
                }
                sb.AppendLine();
                sb.AppendLine($"_提示：下次 re-enter 用 `since_seq={latestSeq}` 看新增 delta；單 task 完整 timeline 走 `task_state task_id=...`_");
            }

            UCL_ChatTavernRender.WriteLastOp(sb.ToString(), args, "tavern");
            Debug.Log($"[Quest] events_since {roomId} since={sinceSeq} → {totalAfter} events" + (truncated ? $" (truncated to {limit})" : ""));
        }

        // 真錯誤：寫盤失敗 / null ref / unhandled exception → 紅 ❗ + LogError
        static void FailLastOp(System.Collections.Generic.IDictionary<string, string> iArgs, string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ❌ Tavern Cmd Failed\n\n{msg}\n", iArgs, "tavern");
            Debug.LogError($"[Tavern] {msg}");
            throw new InvalidOperationException(msg);
        }

        // 預期拒絕：缺 arg / 房間不存在 / owner mismatch / lease 衝突 / status 不對 → 黃 ⚠ + LogWarning
        // throw 行為跟 FailLastOp 一致（cmd queue 端仍視為失敗）；只是 console 顏色降級避免污染 signal-to-noise
        // 詳見 docs/Snapshots/ErrorLog_Analysis_2026-05-09.md (T11 報告)
        static void RejectLastOp(System.Collections.Generic.IDictionary<string, string> iArgs, string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ Tavern Cmd Rejected\n\n{msg}\n", iArgs, "tavern");
            Debug.LogWarning($"[Tavern] {msg}");
            throw new InvalidOperationException(msg);
        }
    }
}
#endif
