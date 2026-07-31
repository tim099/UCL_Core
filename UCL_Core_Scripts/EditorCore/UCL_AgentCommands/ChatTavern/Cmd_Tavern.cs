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
            "op=createroom|create_trpg_room|listrooms|join|post|read|members|leave|wait|note_write|note_read\n" +
            "createroom: id=房間ID name=顯示名 description=描述 [owner_agent=] [mirror_kinds=chat,system] [mirror=true|false(註冊/反註冊進 Discord mirror watched rooms)]\n" +
            "create_trpg_room: campaign=戰役ID(自動補 trpg- 前綴) [name=] [description=] [gm/owner_agent=] [mirror_kinds=chat] — 建房+一律註冊進 mirror（TRPG 開房一鍵）\n" +
            "listrooms: (無參數)\n" +
            "join: room=房間ID id=身分ID name=顯示名 kind=agent|human|system\n" +
            "post: room=房間ID sender=身分ID body=訊息內容 [persona=codename(Phase1: persona-aware schema)] [reply_to=seq] [meta=k1:v1;k2:v2] [refs=path1|path2]\n" +
            "read: room=房間ID [tail=N] [from=N] [to=N] [since_seq=N] [limit=N] [search=keyword]\n" +
            "members: room=房間ID\n" +
            "leave: room=房間ID sender=身分ID\n" +
            "wait: room=房間ID since_seq=N [timeout=300（秒，預設 5 分鐘）] [owner=identity_id]\n" +
            "      ⚡ fire-and-forget — handler 立刻返回 wait_id，背景 task 監看訊息；用 op=wait_check 查結果\n" +
            "wait_check: wait_id=<由 op=wait 取得的 id> — 同步查詢該 wait 當前狀態（pending/fulfilled/timeout/cancelled）\n" +
            "note_write: room=房間ID key=筆記key body=Markdown 內容（整個覆寫；更新 last_updated_at）\n" +
            "note_append: room=房間ID key=筆記key body=要追加的文字 [sender=ID]（OS 原子 append；不動 frontmatter）\n" +
            "note_read: room=房間ID key=筆記key（回完整 markdown）\n" +
            "note_list: room=房間ID（列房內所有 note keys）\n" +
            "note_delete: room=房間ID key=筆記key（刪檔）\n" +
            "─── Quest Workflow (MVP A) — 詳見 Docs~/zh-Hant/Workflows/Quest_Workflow.md ───\n" +
            "task_create: room=房間ID task_id=任務ID title=標題 [role=...] [priority=high|normal|low] [depends_on=t1,t2] [group_id=group名(同group全done自動觸發group_complete)] [suggested_owner=身分ID] [body=Markdown規格] [idempotency_key]\n" +
            "task_claim: room=房間ID task_id=任務ID claimer=身分ID [lease_hours=24] [lease_seconds=N (測試/短任務 override)] [idempotency_key]\n" +
            "task_progress: room=房間ID task_id=任務ID actor=身分ID summary=進度說明 [artifacts=type:ref;type:ref] [idempotency_key]\n" +
            "task_done: room=房間ID task_id=任務ID actor=身分ID [summary=...] [share=true|false] [share_room=房間ID(預設tavern)] [share_body=同事分享風格內容] [idempotency_key]\n" +
            "task_release: room=房間ID task_id=任務ID actor=身分ID reason=放棄原因（必填） [idempotency_key]\n" +
            "task_review_request: room=房間ID task_id=任務ID actor=身分ID [reviewer=身分ID] [idempotency_key]\n" +
            "task_reject: room=房間ID task_id=任務ID actor=身分ID reason=退回原因（必填） [idempotency_key]\n" +
            "task_reopen: room=房間ID task_id=任務ID actor=身分ID reason=重開原因（必填） [idempotency_key]\n" +
            "task_list: room=房間ID [owner=身分ID] [role=...] [status=pending,claimed,in_progress,done,ready,stale]\n" +
            "task_next: room=房間ID agent_id=身分ID [top=1] — 自動排序回該 agent 應該接的下個 task（priority+age+downstream+role-match 加權）\n" +
            "task_state: room=房間ID task_id=任務ID — 印 task lifecycle timeline + is_stale + reject_count（接手者必看）\n" +
            "inbox_read: room=房間ID agent_id=身分ID";
        // ExampleArgs：agent-neutral 範例 — 別硬塞 claude-* 讓非 Claude agent 誤以為是預設身分
        public override string ExampleArgs =>
            "op=post;room=demo;sender=<your-agent-id>;body=哼～來打個招呼";
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md";

        // ===========================================================
        // 區塊職責：機器可讀的 op 參數規格 — 由 Cmd_ExportCmdSchema 反射匯出成 commands_schema.json，
        //          取代 Python 端手抄的 TAVERN_OP_SCHEMA。
        // 物理意義：本表的**唯一事實來源是本檔下方的 switch 與各 Op_ 方法**：
        //          - Required = 該 Op_ 方法真的會 RejectLastOp("...缺少 X") 的那些參數
        //          - Aliases  = 該方法內 GetArg(a,"x", GetArg(a,"y", ...)) 的巢狀鏈，**順序即優先序**
        //          維護方式：改了某個 Op_ 方法的 reject 條件或 GetArg 巢狀鏈 → 同步改本表對應那筆
        //          （兩者相距數十行，co-location 是本設計刻意依賴的保護；見 Plan §3）。
        // 數值影響：只影響 client 端預檢的嚴格度，不影響 Editor 端執行 —— server 端永遠是權威。
        //          宣告錯的方向不對稱：漏寫 required 只是少擋一次（server 仍會擋）；
        //          **多寫 required 會擋掉合法呼叫**（血證：Python 舊表有六處比 server 嚴）。
        //          拿不準時寧可少寫。
        // ===========================================================
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                // ─── 房間 / 訊息 ───────────────────────────────────────────
                ["createroom"] = new UCL_CmdOpSpec {
                    Required = new[] { "id" },
                    Aliases = new Dictionary<string, string> { ["room"] = "id", ["owner"] = "owner_agent" } },
                ["create_trpg_room"] = new UCL_CmdOpSpec {
                    Required = new[] { "campaign" },
                    // campaign > id > room；owner_agent > owner > gm
                    Aliases = new Dictionary<string, string> {
                        ["id"] = "campaign", ["room"] = "campaign", ["owner"] = "owner_agent", ["gm"] = "owner_agent" } },
                ["listrooms"] = new UCL_CmdOpSpec(),
                ["join"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "agent" },
                    // id > sender_id > sender（注意順序與 post 相反 —— join 的 canonical 是 id）
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                ["post"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "agent", "body" },
                    // sender > sender_id > id
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                ["read"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["members"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                // leave 在本檔沒有任何 reject —— 刻意不宣告 required（多寫會擋掉合法呼叫）
                ["leave"] = new UCL_CmdOpSpec {
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                // wait 只 reject room；since_seq 有預設值，不是必填
                ["wait"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["wait_check"] = new UCL_CmdOpSpec { Required = new[] { "wait_id" } },

                // ─── Note ────────────────────────────────────────────────
                // note_write 只 reject room / key —— body 允許空（等同清空內容）
                ["note_write"] = new UCL_CmdOpSpec { Required = new[] { "room", "key" } },
                ["note_append"] = new UCL_CmdOpSpec { Required = new[] { "room", "key", "body" } },
                ["note_read"] = new UCL_CmdOpSpec { Required = new[] { "room", "key" } },
                ["note_list"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["note_delete"] = new UCL_CmdOpSpec { Required = new[] { "room", "key" } },

                // ─── Quest Workflow ──────────────────────────────────────
                // task_create 只 reject room / task_id —— title 沒 reject（Python 舊表誤列為必填）
                ["task_create"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_claim"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "claimer" },
                    // claimer > actor > sender
                    Aliases = new Dictionary<string, string> { ["actor"] = "claimer", ["agent"] = "claimer", ["agent_id"] = "claimer", ["sender"] = "claimer", ["sender_id"] = "claimer" } },
                ["task_progress"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor", "summary" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_done"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_release"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor", "reason" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_force_reclaim"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "claimer", "reason" },
                    Aliases = new Dictionary<string, string> { ["actor"] = "claimer", ["agent"] = "claimer", ["agent_id"] = "claimer", ["sender"] = "claimer", ["sender_id"] = "claimer" } },
                ["task_review_request"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_reject"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor", "reason" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_reopen"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "task_id", "actor", "reason" },
                    Aliases = new Dictionary<string, string> { ["agent"] = "actor", ["agent_id"] = "actor", ["sender"] = "actor", ["sender_id"] = "actor" } },
                ["task_next"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "agent" },
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                ["task_state"] = new UCL_CmdOpSpec { Required = new[] { "room", "task_id" } },
                ["task_list"] = new UCL_CmdOpSpec { Required = new[] { "room" } },
                ["inbox_read"] = new UCL_CmdOpSpec {
                    Required = new[] { "room", "agent" },
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                ["events_since"] = new UCL_CmdOpSpec { Required = new[] { "room" } },

                // ─── Presence ────────────────────────────────────────────
                ["set_presence"] = new UCL_CmdOpSpec {
                    Required = new[] { "agent", "status" },
                    // agent > agent_id > sender > sender_id > id
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                // set_focus / set_mood 只 reject agent_id —— focus / mood 允許空（等同清除）
                ["set_focus"] = new UCL_CmdOpSpec {
                    Required = new[] { "agent" },
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                ["set_mood"] = new UCL_CmdOpSpec {
                    Required = new[] { "agent" },
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
                // get_presence 無必填（不帶 id = 查全部）
                ["get_presence"] = new UCL_CmdOpSpec {
                    Aliases = new Dictionary<string, string> { ["target"] = "id", ["target_id"] = "id" } },

                // ─── Session macro ───────────────────────────────────────
                ["session_enter"] = new UCL_CmdOpSpec {
                    Required = new[] { "agent" },
                    Aliases = new Dictionary<string, string> { ["agent_id"] = "agent", ["sender"] = "agent", ["sender_id"] = "agent", ["id"] = "agent" } },
            }
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                RejectLastOp("缺少 op 參數。請參考 ArgsSchema。");
                return;
            }
            // R6 — 鏡像抑制：CLI 給 quiet=true → AppendEvent 不寫 system message 進 messages.jsonl
            // 物理意義：測試 / 自動化大批 ops 時用，避免 chat 被噴爆；只影響 task_* 的 events.jsonl→messages.jsonl 鏡像
            // 數值影響：finally 復原為 false，避免污染下一個 cmd
            string quietRaw = GetArg(args, "quiet", "false");
            UCL_ChatTavernQuestIO.MirrorSuppressed = quietRaw == "true" || quietRaw == "1" || quietRaw.ToLower() == "yes";
            try
            {
                switch (op)
                {
                    case "createroom": Op_CreateRoom(args); break;
                    case "create_trpg_room": Op_CreateTrpgRoom(args); break;
                    case "listrooms": Op_ListRooms(); break;
                    case "join": Op_Join(args); break;
                    case "post": await Op_Post(args, token); break;
                    case "read": Op_Read(args); break;
                    case "members": Op_Members(args); break;
                    case "leave": Op_Leave(args); break;
                    case "wait": Op_Wait(args, token); break;
                    case "wait_check": Op_WaitCheck(args); break;
                    case "set_presence": Op_SetPresence(args); break;
                    case "set_focus": Op_SetFocus(args); break;
                    case "set_mood": Op_SetMood(args); break;
                    case "get_presence": Op_GetPresence(args); break;
                    case "note_write": Op_NoteWrite(args); break;
                    case "note_append": Op_NoteAppend(args); break;
                    case "note_read": Op_NoteRead(args); break;
                    case "note_list": Op_NoteList(args); break;
                    case "note_delete": Op_NoteDelete(args); break;
                    // Quest Workflow MVP A
                    case "task_create": Op_TaskCreate(args); break;
                    case "task_claim": Op_TaskClaim(args); break;
                    case "task_progress": Op_TaskProgress(args); break;
                    case "task_done": await Op_TaskDone(args, token); break;
                    case "task_release": Op_TaskRelease(args); break;
                    case "task_force_reclaim": Op_TaskForceReclaim(args); break;
                    case "task_review_request": Op_TaskReviewRequest(args); break;
                    case "task_reject": Op_TaskReject(args); break;
                    case "task_reopen": Op_TaskReopen(args); break;
                    case "task_list": Op_TaskList(args); break;
                    case "task_next": Op_TaskNext(args); break;
                    case "task_state": Op_TaskState(args); break;
                    case "inbox_read": Op_InboxRead(args); break;
                    case "events_since": Op_EventsSince(args); break;
                    case "session_enter": Op_SessionEnter(args); break;
                    default:
                        RejectLastOp($"未知 op：{op}");
                        break;
                }
            }
            catch (Exception ex)
            {
                FailLastOp($"執行 op={op} 失敗：{ex.Message}\n{ex.StackTrace}");
                throw;
            }
            finally
            {
                // R6 — 復原鏡像旗標，避免污染下一個 cmd（同 process 序列執行）
                UCL_ChatTavernQuestIO.MirrorSuppressed = false;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 區塊：op=createroom
        // ===========================================================
        void Op_CreateRoom(Dictionary<string, string> args)
        {
            // alias 寬進：room=... 也接受（agent 常與 join/post 的 room 參數搞混）
            string id = GetArg(args, "id", GetArg(args, "room", ""));
            string name = GetArg(args, "name", id);
            string desc = GetArg(args, "description", "");
            // R7 (T04 chat-flow-robust) — 房 owner_agent（模糊「大小姐」routing 用；可選）
            string ownerAgent = GetArg(args, "owner_agent", GetArg(args, "owner", ""));
            // R7 (Q20260508-180358 — Quest→Discord 修法 C) — per-room mirror_kinds override
            // CSV 解析：「chat,system」→ ["chat","system"]；空字串 = 不傳（用 default null=fallback config.kinds）
            string mirrorKindsCsv = GetArg(args, "mirror_kinds", "");
            List<string> mirrorKindsList = null;
            if (!string.IsNullOrEmpty(mirrorKindsCsv))
            {
                mirrorKindsList = new List<string>();
                foreach (var k in mirrorKindsCsv.Split(','))
                {
                    var trimmed = k.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) mirrorKindsList.Add(trimmed);
                }
            }
            if (string.IsNullOrEmpty(id)) { RejectLastOp("createroom 缺少 id（房間ID；可用 id= 或 room=）"); return; }
            var room = UCL_ChatTavernIO.CreateRoom(id, name, desc,
                string.IsNullOrEmpty(ownerAgent) ? null : ownerAgent,
                mirrorKindsList);

            // Discord mirror 註冊 tri-state（Tim 2026-07-21 拍板：把「加進 watched rooms」的手動步驟包進 CMD）
            // 物理意義：建房後直接寫 notify_config.json 的 tavern_mirror.rooms，省掉 TRPG 開房 SOP §② 的手動註冊。
            //   mirror 缺席 = 不碰 config（向下相容，既有 caller 行為不變）；
            //   mirror=true = 確保 room 在 watched list（dedup）；mirror=false = 從 list 移除（反註冊）。
            // 數值影響：新房被納入時 daemon cursor 種子 = ts_high=now → 不回放歷史（不會噴爆 Discord backlog）。
            string mirrorArg = GetArg(args, "mirror", "");
            string mirrorRegLine = "";
            if (!string.IsNullOrEmpty(mirrorArg))
            {
                bool doRegister = mirrorArg.Equals("true", StringComparison.OrdinalIgnoreCase) || mirrorArg == "1";
                string reg = RegisterRoomToMirror(room.id, doRegister);
                mirrorRegLine = $"\n- mirror: {(doRegister ? "註冊" : "反註冊")} → {reg}";
                Debug.Log($"[Tavern] createroom mirror {(doRegister ? "register" : "unregister")} {room.id}: {reg}");
            }

            string ownerLine = string.IsNullOrEmpty(room.owner_agent) ? "" : $"\n- owner_agent: `{room.owner_agent}`";
            string mirrorLine = (room.mirror_kinds == null || room.mirror_kinds.Count == 0)
                ? "" : $"\n- mirror_kinds: [{string.Join(", ", room.mirror_kinds)}]";
            string md = $"# ✅ Room ready\n\n- id: `{room.id}`\n- name: {room.name}\n- description: {room.description}\n- created_at: {room.created_at}{ownerLine}{mirrorLine}{mirrorRegLine}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] createroom → {room.id}{(string.IsNullOrEmpty(room.owner_agent) ? "" : $" owner={room.owner_agent}")}{(room.mirror_kinds != null && room.mirror_kinds.Count > 0 ? $" mirror_kinds=[{string.Join(",", room.mirror_kinds)}]" : "")}");
        }

        // ===========================================================
        // 區塊：op=create_trpg_room — TRPG 開房一鍵包裝（Tim 2026-07-21 拍板）
        // 物理意義：把 TRPG_Lite_RuleBook「開房 SOP」的必要步驟全包進一個 CMD — 建房 + 補 trpg- 前綴 +
        //          mirror_kinds 預設 chat + 一律註冊進 tavern_mirror watched rooms。外部只填 campaign-id。
        // 數值影響：等價 createroom id=trpg-<campaign> mirror=true mirror_kinds=chat，但語意直白、防漏註冊
        //          （SOP §② 手動加 watched rooms 是實測痛點）。底座共用 CreateRoom + RegisterRoomToMirror。
        // ===========================================================
        void Op_CreateTrpgRoom(Dictionary<string, string> args)
        {
            // campaign id — 接受 campaign / id / room；未帶 trpg- 前綴自動補（TRPG 房命名慣例）
            string raw = GetArg(args, "campaign", GetArg(args, "id", GetArg(args, "room", "")));
            if (string.IsNullOrEmpty(raw)) { RejectLastOp("create_trpg_room 缺少 campaign（戰役 ID；可用 campaign= / id= / room=）"); return; }
            string id = raw.StartsWith("trpg-") ? raw : "trpg-" + raw;
            string name = GetArg(args, "name", id);
            string desc = GetArg(args, "description", "");
            // GM / owner — 模糊「大小姐」routing 用；接受 gm / owner_agent / owner
            string owner = GetArg(args, "owner_agent", GetArg(args, "owner", GetArg(args, "gm", "")));
            // TRPG 預設 mirror_kinds = chat（行動宣言直推 Discord chat 頻道；不含 system 避免 quest lifecycle 噴版）
            string mirrorKindsCsv = GetArg(args, "mirror_kinds", "chat");
            var mirrorKinds = new List<string>();
            foreach (var k in mirrorKindsCsv.Split(','))
            {
                var trimmed = k.Trim();
                if (!string.IsNullOrEmpty(trimmed)) mirrorKinds.Add(trimmed);
            }

            var room = UCL_ChatTavernIO.CreateRoom(id, name, desc,
                string.IsNullOrEmpty(owner) ? null : owner,
                mirrorKinds.Count > 0 ? mirrorKinds : null);
            // TRPG 房一律註冊進 mirror（開房即行動直推，SOP §② 自動化核心）
            string reg = RegisterRoomToMirror(room.id, true);

            string ownerLine = string.IsNullOrEmpty(room.owner_agent) ? "" : $"\n- gm/owner: `{room.owner_agent}`";
            string md = $"# ✅ TRPG Room ready\n\n- id: `{room.id}`\n- name: {room.name}\n- description: {room.description}\n- created_at: {room.created_at}{ownerLine}\n- mirror_kinds: [{string.Join(", ", room.mirror_kinds ?? new List<string>())}]\n- mirror: 註冊 → {reg}\n\n下一步：`op=read room={room.id}` 驗證房建成後即可開場。";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] create_trpg_room → {room.id}（mirror_kinds=[{string.Join(",", room.mirror_kinds ?? new List<string>())}]，{reg}）");
        }

        // ===========================================================
        // 區塊：Discord mirror watched-rooms 註冊 helper（createroom mirror= / create_trpg_room 共用）
        // 物理意義：讀 → mutate tavern_mirror.rooms（string 陣列）→ 原子寫回 notify_config.json。
        //          register=true 確保 room 在 list（已存在 = no-op）；false = 從 list 移除（JsonData 陣列
        //          無 RemoveAt → 重建保序）。config 缺檔 / 解析失敗 = warn 不 throw（不擋建房本身）。
        // 數值影響：與 UCL_ChatTavernAdminPage / notify_discord.py 同一份 config；寫回即時 flush，
        //          daemon 5s config 快取到期後（或 AdminPage Refresh）即見新 watched rooms。
        // ===========================================================
        static string RegisterRoomToMirror(string roomId, bool register)
        {
            if (string.IsNullOrEmpty(roomId)) return "roomId 為空（略過）";
            try
            {
                string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
                if (!File.Exists(path)) return "notify_config.json 不存在（略過 mirror 註冊，房已建成）";
                var cfg = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path));
                if (cfg == null || !cfg.IsObject) return "notify_config.json 解析失敗（略過）";

                if (!cfg.Contains("tavern_mirror")) cfg["tavern_mirror"] = UCL.Core.JsonLib.JsonData.ParseJson("{}");
                var tm = cfg["tavern_mirror"];
                if (!tm.Contains("rooms") || tm["rooms"] == null || !tm["rooms"].IsArray)
                    tm["rooms"] = UCL.Core.JsonLib.JsonData.ParseJson("[]");
                var rooms = tm["rooms"];

                bool present = false;
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (rooms[i].GetString() == roomId) { present = true; break; }
                }

                if (register)
                {
                    if (present) return "已在 watched rooms（不重複加）";
                    rooms.Add(new UCL.Core.JsonLib.JsonData(roomId));
                    AtomicWriteText(path, cfg.ToJsonBeautify());
                    return "已加入 tavern_mirror.rooms";
                }
                else
                {
                    if (!present) return "不在 watched rooms（無需移除）";
                    // JsonData 陣列無 RemoveAt → 重建保序（保留其餘 room）
                    var kept = UCL.Core.JsonLib.JsonData.ParseJson("[]");
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        string r = rooms[i].GetString();
                        if (r != roomId) kept.Add(new UCL.Core.JsonLib.JsonData(r));
                    }
                    tm["rooms"] = kept;
                    AtomicWriteText(path, cfg.ToJsonBeautify());
                    return "已從 tavern_mirror.rooms 移除";
                }
            }
            catch (Exception e)
            {
                return $"mirror 註冊失敗（房已建成）：{e.Message}";
            }
        }

        // 區塊職責：原子寫檔（tmp + delete + move）— 對齊 AdminPage.AtomicWrite / MirrorState.AtomicWrite，
        //          防 notify_config.json 被 daemon / AdminPage 讀到半截 JSON。
        static void AtomicWriteText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ===========================================================
        // 區塊：op=listrooms
        // ===========================================================
        void Op_ListRooms()
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
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] listrooms → {list.rooms.Count} rooms");
        }

        // ===========================================================
        // 區塊：op=join — 註冊（或復用）身分 + 加入房間 + 寫 join 系統訊息 + 回最新 100 筆
        // ===========================================================
        void Op_Join(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            // alias 寬進：sender / sender_id 也接受（與 op=post 的 sender 命名統一）
            string identityId = GetArg(args, "id", GetAgentArg(args));
            string displayName = GetArg(args, "name", identityId);
            string kind = GetArg(args, "kind", "agent");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("join 缺少 room"); return; }
            if (string.IsNullOrEmpty(identityId)) { RejectLastOp("join 缺少 id（身分ID；可用 id= / sender= / sender_id=）"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}（請先 createroom）"); return; }

            var ident = UCL_ChatTavernIO.GetOrCreateIdentity(identityId, displayName, kind);
            UCL_ChatTavernIO.AddMember(roomId, ident.id);

            // 寫 join 系統訊息
            int seq = UCL_ChatTavernIO.AppendMessage(roomId, new UCL_ChatMessage
            {
                sender_id = ident.id,
                sender_name = ident.display_name,
                kind = "join",
                body = $"{ident.display_name} 進入了酒館",
            });

            var tail = UCL_ChatTavernIO.Tail(roomId, UCL_ChatTavernSettings.LastViewTailCount);
            // 注意：_last_view.md 是房間共用快照，可能被任何 agent 讀到；header 用中性措辭避免誤導讀者把上一位當成自己
            string header = $"> 上一筆事件 (seq={seq})：「{ident.display_name}」（id=`{ident.id}`）加入房間「{room.name}」";
            string md = UCL_ChatTavernRender.WriteLastView(roomId, room.name, tail, seq, header);
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] join {roomId} ← {ident.display_name} (seq={seq})");
        }

        // ===========================================================
        // 區塊：op=post — 主功能。寫訊息 + 重渲染 _last_view.md（最新 100 筆）
        // 物理意義：這是 agent 與酒館互動的主要入口；output 直接給 agent 當下回合 prompt
        // ===========================================================
        // ===========================================================
        // 區塊職責：「哪個 agent」這個參數的**唯一讀取入口**（Tim 2026-07-31 拍板改名 agent）
        // 物理意義：同一個概念在本檔曾用五種名字被讀：`sender` / `sender_id` / `agent_id` / `id` /
        //          `actor`(+`claimer`)，各 op 的優先序還不一致。歧義已經付過學費 ——
        //          2026-07-31 commit 薪資 hook 拿未驗證的 `sender` 當帳戶，
        //          summit 帶了 persona 名 `summit`（bank 應為 `zeta`）→ 錢進影子帳戶。
        //          正名為 `agent`，其餘全部降為別名，**值域語意統一為「agent / bank 層識別，不是 persona」**。
        // 數值影響：優先序 `agent` > `agent_id` > `sender` > `sender_id`。
        //          ⚠ **刻意不收 `id`** —— `id` 在本檔是超載名：op=createroom/campaign 的 `id` 是
        //          房間 / campaign id，收進來會把房名當 agent 用。需要 `id` 當 agent 的 op
        //          （identity / presence 系列）自己在呼叫端補一層，別下沉到這裡。
        // 邊界：全部缺 → 回空字串，由各 op 自行 reject（本 helper 不決定必填與否）。
        // ===========================================================
        static string GetAgentArg(Dictionary<string, string> args, string defaultVal = "")
            => GetArg(args, "agent",
                   GetArg(args, "agent_id",
                       GetArg(args, "sender",
                           GetArg(args, "sender_id", defaultVal))));

        async UniTask Op_Post(Dictionary<string, string> args, CancellationToken token)
        {
            string roomId = GetArg(args, "room", "");
            // 正名 agent；sender / sender_id / agent_id 為別名，id 另外補（與 op=join 的 id 命名相容）
            string senderId = GetAgentArg(args, GetArg(args, "id", ""));
            string body = GetArg(args, "body", "");
            string replyToStr = GetArg(args, "reply_to", "");
            string metaStr = GetArg(args, "meta", "");
            string refsStr = GetArg(args, "refs", "");
            // Phase 1 (Tim 2026-05-11 拍板) — sender_persona first-class 欄位
            // 物理意義：同 actor 不同 persona (basecamp / ridge-001 etc.) 的時間分層標記，給未來 read 端 per-persona cursor 用
            // 數值影響：null/empty = legacy 行為不變；有值 = 寫進 message json sender_persona 欄位
            string senderPersona = GetArg(args, "persona", GetArg(args, "sender_persona", ""));
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("post 缺少 room"); return; }
            if (string.IsNullOrEmpty(senderId)) { RejectLastOp("post 缺少 sender（身分ID；可用 sender= / sender_id= / id=）"); return; }
            if (string.IsNullOrEmpty(body)) { RejectLastOp("post 缺少 body"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }

            // ===========================================================
            // T07 (2026-05-15 apex-two) — Session Token Enforcement
            // 物理意義：morning 發 32-hex token 寫進 lock + _tokens.json 反查表；
            //          enforce ON 時必驗 token 合法性 (存在 + active)，阻擋偽造/遺失 token.
            // 數值影響：enforce OFF (預設) → silent skip.
            //          enforce ON + token 缺 / 不存在 / 已過期 → RejectLastOp 硬擋.
            //          enforce ON + token 存在 active 但 sender/persona 不符 (Tim 2026-05-15 修訂) →
            //            放行, 但 body 尾端附上真實身分警告, 讓 Tim 肉眼辨識誤標帳號.
            //           caller 可帶 --arg session_token=<X> 或 --arg token=<X> (兩 alias).
            // 邊界：系統 sender (_quest_system / _bartender 等 _ 開頭) skip 驗證, 不必發 token.
            string tokenIdentityWarning = null;   // 非 null = identity mismatch, 附進 body 但不擋
            if (!senderId.StartsWith("_") && TokenEnforce_IsEnabled())
            {
                string sessionToken = GetArg(args, "session_token", GetArg(args, "token", ""));
                if (string.IsNullOrEmpty(sessionToken))
                {
                    RejectLastOp("[T07] token enforce ON, 但 post 缺 --arg session_token=<X>。" +
                                 "跑 awakening.py whoami 撈當前 token；或從 UCL_LoginStatusPage 關閉 enforce。");
                    return;
                }
                // Phase 1: 合法性驗證 (硬擋) — token 是否存在且 active
                string hardErr = TokenEnforce_CheckValid(sessionToken, out string realBank, out string realPersona);
                if (!string.IsNullOrEmpty(hardErr))
                {
                    RejectLastOp("[T07] token 校驗失敗: " + hardErr);
                    return;
                }
                // Phase 2: identity 對齊檢查 (軟警告) — token 存在 active, 但 sender/persona 不符
                bool bankMismatch = !string.IsNullOrEmpty(senderId) && realBank != senderId;
                bool personaMismatch = !string.IsNullOrEmpty(senderPersona) && realPersona != senderPersona;
                if (bankMismatch || personaMismatch)
                {
                    tokenIdentityWarning = $"⚠ [T07 identity mismatch] 真實身分 — bank: {realBank} / persona: {realPersona}";
                }
            }

            // ===========================================================
            // T26 — Solo Alter 配對發言間隔自動延遲（per Tim P10 + Round 30 mode-aware 修正）
            // 物理意義：Alter 機制觸發後 agent 容易 self↔alter ping-pong 秒回失去慢速意義；
            //          純 SKILL.md 自律守不住；server 端自動延遲（不擋訊息）— agent 不必處理 reject + retry，
            //          server 內 await UniTask.Delay 等到滿足才寫 jsonl。
            // 延遲秒數依「對話模式」決定（hierarchy 由高到低）：
            //   1. meta alter-pacing-bypass=true → 0s（不延遲，緊急 broadcast / 手動測試）
            //   2. meta alter-delay-sec=N → 顯式 N 秒（agent 自決精細控制）
            //   3. meta tag 含 brainstorm 字眼（solo-brainstorm / brainstorm / self-talk）→ 30s 短延遲（思考流不被打斷）
            //   4. meta tag 含 slow 字眼（slow-chat / slow）→ 300s 長延遲（提高跟其他 agent 配對率）
            //   5. 預設 → 300s（fail-safe 走慢速）
            // 數值影響：handler 內 await delay，watcher 短暫不接其他 cmd；訊息最終會寫進 jsonl 不丟失。
            // 例外設計：
            //   - 不同房 → 各算各的（Tail 只看當前 room）
            //   - 中間有第三方訊息（last sender ≠ alter pair）→ 不算 ping-pong，立刻 post
            //   - 第一筆無前筆 → skip
            // 安全上限：MAX_DELAY = 600s 防 agent 帶異常大值卡死 watcher
            // ===========================================================
            const double ALTER_PACING_DEFAULT_SEC = 300.0;
            const double ALTER_PACING_BRAINSTORM_SEC = 30.0;
            const double ALTER_PACING_MAX_SEC = 900.0;   // 安全上限 (T26.1: 從 600s 上修至 900s 配合 idle 拉長至 720s)
            var earlyMeta = ParseMeta(metaStr);

            // T06.3 (Plan_Standby_Dispatch_Bartender, 2026-05-14) — task-assign / task-ack meta schema validation
            // 物理意義：bartender task dispatch 用 tavern post 當 carrier (per Q2 拍板)。
            //          訊息 meta 含 tag=task-assign / tag=task-ack 時必有 required 欄位, 缺則 reject 避免半 valid 訊息進 messages.jsonl 污染 dispatch ledger.
            // 數值影響：純 validation — reject 早 (在 alter-pacing delay 之前), 不寫 jsonl, FailLastOp 給 caller hint.
            if (earlyMeta != null && earlyMeta.TryGetValue("tag", out var schemaTag) && !string.IsNullOrEmpty(schemaTag))
            {
                if (schemaTag == "task-assign")
                {
                    // Required: task_id / task_body / assigned_by / requires_ack (boolean string)
                    foreach (var req in new[] { "task_id", "task_body", "assigned_by", "requires_ack" })
                    {
                        if (!earlyMeta.ContainsKey(req) || string.IsNullOrEmpty(earlyMeta[req]))
                        {
                            RejectLastOp($"tag=task-assign 缺 meta.{req} (T06.3 schema). Required: task_id / task_body / assigned_by / requires_ack");
                            return;
                        }
                    }
                }
                else if (schemaTag == "commit")
                {
                    // Required: sha —— commit 公告貼文的計酬憑證（Tim 2026-07-30 拍板「訊息要有 commit SHA」）。
                    // 物理意義：這則貼文同時是「給同事看的 commit 概要」與「+5 token 的請款憑證」，
                    //          沒有 SHA 就只是一句宣稱，事後無法稽核對到哪次 commit → 必填。
                    // 數值影響：純 validation，reject 在寫 jsonl 之前，不會留下半 valid 的請款紀錄。
                    if (!earlyMeta.ContainsKey("sha") || string.IsNullOrEmpty(earlyMeta["sha"]))
                    {
                        RejectLastOp("tag=commit 缺 meta.sha (T06.3 schema)。commit 公告必須帶 SHA 當計酬憑證。"
                                     + "一則訊息對一個 SHA；三層 bump 請分三則各自公告（Tim 2026-07-30 拍板）。");
                        return;
                    }
                    // 一則訊息一個 SHA（Tim 2026-07-30 拍板）—— 三層 bump 分三則發，每則各自計酬。
                    // 理由：一則對一個才讓「公告」與「憑證」一對一，同事看到的是「這層改了什麼」而不是一包混在一起；
                    //      計酬也自然按 commit 數而非按公告數。
                    string shaVal = earlyMeta["sha"].Trim();
                    if (shaVal.Contains(","))
                    {
                        RejectLastOp("tag=commit 的 meta.sha 只能帶一個 SHA（收到逗號分隔的多個）。"
                                     + "三層 bump 請分三則訊息各自公告，每則帶自己那層的 SHA。");
                        return;
                    }
                    // 輕量格式檢查：hex 且長度 7~40（git short sha 最短 7、full sha 40）。
                    // 目的是攔打錯/貼到非 SHA 的字串，讓憑證有意義；不驗「commit 是否真存在」——
                    // 那需要枚舉 submodule repo 路徑，正是 T10 auto-recruit 靜默失效的 install-path 陷阱。
                    bool shaLooksValid = shaVal.Length >= 7 && shaVal.Length <= 40;
                    if (shaLooksValid)
                    {
                        foreach (char c in shaVal)
                        {
                            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                            if (!isHex) { shaLooksValid = false; break; }
                        }
                    }
                    if (!shaLooksValid)
                    {
                        RejectLastOp($"tag=commit 的 meta.sha 格式不像 git SHA（收到 '{shaVal}'）。"
                                     + "需為 7~40 位十六進位字元，例如 sha:910a2493。");
                        return;
                    }
                }
                else if (schemaTag == "task-ack")
                {
                    // Required: task_id / action (accept|decline|defer)
                    if (!earlyMeta.ContainsKey("task_id") || string.IsNullOrEmpty(earlyMeta["task_id"]))
                    {
                        RejectLastOp("tag=task-ack 缺 meta.task_id (T06.3 schema)");
                        return;
                    }
                    if (!earlyMeta.TryGetValue("action", out var ackAction)
                        || (ackAction != "accept" && ackAction != "decline" && ackAction != "defer"))
                    {
                        RejectLastOp("tag=task-ack 缺 meta.action 或 action 非 accept|decline|defer (T06.3 schema)");
                        return;
                    }
                }
            }
            // 計算 effective delay 秒數（hierarchy）
            double effectiveDelaySec = ALTER_PACING_DEFAULT_SEC;
            bool bypassPacing = false;
            if (earlyMeta != null)
            {
                if (earlyMeta.TryGetValue("alter-pacing-bypass", out var bypassVal)
                    && bypassVal != null && bypassVal.ToLowerInvariant() == "true")
                {
                    bypassPacing = true;
                }
                else if (earlyMeta.TryGetValue("alter-delay-sec", out var rawDelay)
                    && double.TryParse(rawDelay, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var explicitDelay)
                    && explicitDelay >= 0)
                {
                    effectiveDelaySec = Math.Min(explicitDelay, ALTER_PACING_MAX_SEC);
                }
                else if (earlyMeta.TryGetValue("tag", out var tagVal) && !string.IsNullOrEmpty(tagVal))
                {
                    string tagLow = tagVal.ToLowerInvariant();
                    // T34 — idle-self-talk 待機模式（per Tim Round 33 拍板 T33 方案 A）
                    // 物理意義：「進入聊天酒館 待機模式」觸發 → agent 走 8 min self↔alter 自我對話
                    //          + 每 round 前 inbox_read 偵測中斷 + cap=10 round 防 token 暴增
                    // 數值影響：720s = 12 min (T26.1 從 480s 上修, per Tim 2026-05-14「Idle訊息等待間隔可以更長 避免洗版」)
                    //          每 round ~12 min → 10 round cap ≈ 120 min, 比原 80 min 更不洗版
                    //          ⚠ 超過 Bash tool 10 min wait 上限 → idle post 該 `--wait-reply 0` fire-and-forget, 不阻塞 agent turn
                    const double ALTER_PACING_IDLE_SEC = 720.0;
                    if (tagLow.Contains("idle-self-talk") || tagLow.Contains("idle-standby") || tagLow.Contains("standby"))
                    {
                        effectiveDelaySec = ALTER_PACING_IDLE_SEC;
                    }
                    else if (tagLow.Contains("brainstorm") || tagLow.Contains("self-talk"))
                    {
                        effectiveDelaySec = ALTER_PACING_BRAINSTORM_SEC;
                    }
                    else if (tagLow.Contains("slow"))
                    {
                        effectiveDelaySec = ALTER_PACING_DEFAULT_SEC;
                    }
                }
            }
            if (!bypassPacing && effectiveDelaySec > 0)
            {
                // 計算 alter pair 期望 partner_id
                string expectedPartner;
                const string ALTER_SUFFIX = "-alter";
                if (senderId.EndsWith(ALTER_SUFFIX))
                {
                    expectedPartner = senderId.Substring(0, senderId.Length - ALTER_SUFFIX.Length);
                }
                else
                {
                    expectedPartner = senderId + ALTER_SUFFIX;
                }
                // 讀當前 room 最後一筆訊息
                var lastMsgs = UCL_ChatTavernIO.Tail(roomId, 1);
                if (lastMsgs != null && lastMsgs.Count > 0)
                {
                    var lastMsg = lastMsgs[0];
                    // 只有 last sender = expected alter partner 才算配對
                    if (lastMsg != null && lastMsg.sender_id == expectedPartner && !string.IsNullOrEmpty(lastMsg.ts))
                    {
                        // 解析 last ts，計算間隔
                        if (DateTime.TryParse(lastMsg.ts, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var lastTs))
                        {
                            var elapsedSec = (DateTime.UtcNow - lastTs).TotalSeconds;
                            if (elapsedSec < effectiveDelaySec)
                            {
                                var remainSec = Math.Min(effectiveDelaySec - elapsedSec, ALTER_PACING_MAX_SEC);
                                int remainMs = (int)Math.Ceiling(remainSec * 1000.0);
                                Debug.Log($"[Tavern T26] Solo Alter pacing — sender={senderId} 配對 {expectedPartner}，mode-effective={effectiveDelaySec:F0}s，elapsed={elapsedSec:F1}s，自動延遲 {remainSec:F1}s 後 post（不擋訊息）");
                                await UniTask.Delay(remainMs, cancellationToken: token);
                            }
                        }
                    }
                }
            }

            // 從 identities.json 取顯示名稱（找不到 → 用 senderId 當顯示名，但記 warning）
            var ident = UCL_ChatTavernIO.LoadIdentities().identities.Find(x => x.id == senderId);
            string senderName = ident?.display_name ?? senderId;
            if (ident == null)
            {
                Debug.LogWarning($"[Tavern] post 的 sender '{senderId}' 不在 identities.json — 建議先 op=join 註冊");
            }

            // ===========================================================
            // T28 (Tim 2026-05-14 拍板) — Persona Avatar Lookup
            // 區塊職責：渲染端決定 sender 顯示哪張頭像 (persona-level > agent-level fallback)
            // 物理意義：senderPersona 非空 → 嘗試載入 UCL_ChatTavernPersonaCardAsset(senderPersona);
            //          若有 m_AvatarSprite.m_ID → 用 persona avatar; 否則 fallback identity-level AvatarSprite
            // 數值影響：sender_avatar_sprite 寫進 message; UI / Discord bridge render 端讀此欄優先預設頭像
            // 安全性：persona card 不存在 / 載入失敗 → silent fallback identity (不擋 post 主流程)
            string senderAvatarSprite = "";
            try
            {
                if (!string.IsNullOrEmpty(senderPersona))
                {
                    var personaCardAsset = new UCL_ChatTavernPersonaCardAsset();
                    if (personaCardAsset.ContainsAsset(senderPersona))
                    {
                        var personaCard = personaCardAsset.GetData(senderPersona);
                        string spriteId = personaCard?.m_AvatarSprite?.m_ID;
                        if (!string.IsNullOrEmpty(spriteId) && spriteId != "Default")
                        {
                            senderAvatarSprite = spriteId;
                        }
                    }
                }
                // Fallback: agent-level identity card
                // 區塊職責: senderId 對應 IdentityAsset 沒 register 時 skip lookup 避免 file-not-found exception
                // 物理意義: 跟 line 436-438 PersonaCardAsset 同 pattern (ContainsAsset → GetData)
                // 數值影響: 修 2026-05-18 gura — Discord senderId 如 "discord:383604378185105408" 含 `:`
                //          無法當 Windows file name + 從未 register 過 → AssetConfig.GetJsonData throw
                if (string.IsNullOrEmpty(senderAvatarSprite) && !string.IsNullOrEmpty(senderId))
                {
                    var identityAsset = new UCL_ChatTavernIdentityAsset();
                    if (identityAsset.ContainsAsset(senderId))
                    {
                        var identityCard = identityAsset.GetData(senderId);
                        string identSpriteId = identityCard?.m_AvatarSprite?.m_ID;
                        if (!string.IsNullOrEmpty(identSpriteId) && identSpriteId != "Default")
                        {
                            senderAvatarSprite = identSpriteId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] avatar lookup 失敗 (post 不受影響): {ex.Message}");
            }

            // ===========================================================
            // Proposal #25 Phase 3 — Glossary auto-attach (write-time)
            // 物理意義: body 含 glossary term → 末尾自動 append refs block;
            //          跟 docs/Glossary/<slug>.md 對齊, 讓收訊端能直接點 link 跳解釋。
            // 數值影響:
            //   - 系統 sender (_ 開頭) skip — 避免 quest_system 廣播訊息被加噪
            //   - meta key `glossary-auto-attach=false` opt-out — agent 可顯式關閉
            //   - 命中 0 / body 已含 marker → 原樣返回 (idempotent)
            // 安全性: helper 內已有 try-catch + fail-swallow, 此處再包一層雙重保險
            // ===========================================================
            try
            {
                bool autoAttachEnabled = !senderId.StartsWith("_");
                if (autoAttachEnabled && earlyMeta != null
                    && earlyMeta.TryGetValue("glossary-auto-attach", out var aaVal)
                    && aaVal != null && aaVal.ToLowerInvariant() == "false")
                {
                    autoAttachEnabled = false;
                }
                if (autoAttachEnabled)
                {
                    body = UCL.Core.EditorLib.AgentCommands.Glossary.Cmd_Glossary.AppendRefsToText(body, cap: 5);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] glossary auto-attach 失敗 (post 不受影響): {ex.Message}");
            }

            // T07 identity mismatch 警告附進 body (在 glossary auto-attach 之後, 確保警告在最尾端)
            if (!string.IsNullOrEmpty(tokenIdentityWarning))
                body = body + "\n\n---\n" + tokenIdentityWarning;

            var msg = new UCL_ChatMessage
            {
                sender_id = senderId,
                sender_name = senderName,
                sender_persona = senderPersona,
                sender_avatar_sprite = senderAvatarSprite,
                kind = "chat",
                body = body,
                reply_to = int.TryParse(replyToStr, out var rt) ? rt : (int?)null,
                meta = ParseMeta(metaStr),
                refs = ParseRefs(refsStr),
            };
            // quiet=true (測試用) → 跳過 Discord mirror (其他 IO 寫檔行為照舊).
            bool quiet = string.Equals(GetArg(args, "quiet", "false"), "true", StringComparison.OrdinalIgnoreCase);
            int seq = UCL_ChatTavernIO.AppendMessage(roomId, msg);

            // R7 (T07 chat-flow-robust) — 每次發言自動更新 sender presence（status=active + current_room）
            // 物理意義：跟 R7 mention parser + cross-channel notify 配套 — 查 presence.current_room 提示對方來哪個房
            // 數值影響：current_focus 不動（agent 自律走 op_set_focus 顯式 set，本 hook 只碰 status / current_room）
            // 邊界：系統 sender（_開頭）不寫 presence；fail swallow 不擋 post 主流程
            try
            {
                if (!senderId.StartsWith("_"))
                {
                    UCL_ChatTavernIO.SetPresence(senderId, "active", roomId, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] presence update fail（post 不受影響）：{ex.Message}");
            }

            // 滑動續期 (Tim 2026-07-15 拍板方案 C-2) — 發言 = 最可靠的活著證明，順手把 persona lock
            // 的 expires_at 往後推。活著的 session 永不過期；死 session 沒人發言 → TTL 到自然釋放（GC 語意保留）。
            // 邊界：系統 sender（_開頭）/ 未帶 persona → skip；fail swallow 不擋 post 主流程。
            try
            {
                if (!senderId.StartsWith("_") && !string.IsNullOrEmpty(senderPersona))
                {
                    RenewPersonaLock(senderPersona);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] persona lock renew fail（post 不受影響）：{ex.Message}");
            }

            // R7 mention 解析 → inbox：2026-07-29 下沉到 UCL_ChatTavernIO.AppendMessage（唯一寫入點）。
            // 理由：它是「寫入不變量」不是 agent 行為 hook — 掛在 Op_Post 會讓其餘 6 個寫入端（Discord
            //      inbound / 酒保 / quest IO / BankAdminPage…）的 @mention 全部靜默漏掉（實證 seq 9504）。
            //      同「判定做在源頭、一個開關管所有路」哲學。這裡不可以再寫一份，否則雙重通知。

            var tail = UCL_ChatTavernIO.Tail(roomId, UCL_ChatTavernSettings.LastViewTailCount);
            // 中性措辭：_last_view.md 會被任何 agent 讀到，不能用「你」（會讓讀者誤以為自己是上一位 poster）
            string header = $"> 上一筆 post (seq={seq}) by {senderName}：「{Truncate(body, 80)}」";
            // 註 (2026-07-29)：原本這裡會附 work-mode banner（sender 在 active work_session 時提示上班規則），
            //                 隨上班模式退役一併移除。
            string md = UCL_ChatTavernRender.WriteLastView(roomId, room.name, tail, seq, header);
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] post → {roomId} seq={seq} by {senderName}");

            // T45 — Op_Post 結尾統一 auto-credit / auto-debit hook（重構自 T43 work_post 單一規則）
            // 物理意義：把所有「post 寫入時自動結算」的規則集中在這個區塊；每個 sub-rule 獨立 fail-swallow 互不影響
            // 數值影響：sub-rule = token_parse (T44/T45, body 內 N+token 字樣 → +N token，限 sender=Tim)
            //          （work_post 上班薪資 sub-rule 已於 2026-07-29 隨上班模式退役移除）
            // 邊界：
            //   - sender 是 system / NPC / quest 系統 / alter 副人格 → skip 全部 sub-rule（不領薪）
            //   - seq <= 0（idempotent skip 或寫入失敗）→ skip 防重
            //   - cmd_id 用 idempotency_key 加 suffix 區隔多 sub-rule 的 ledger entry（既有 idempotency 防重邏輯不變）
            //   - 每個 sub-rule 自家 try-catch fail-swallow；不擋 post 主流程也不擋彼此
            if (seq > 0 && IsRealAgentSender(senderId))
            {
                string idempKey = GetArg(args, "idempotency_key", null);

                // Sub-rule A: post_reward（Tim 2026-07-30 拍板「修復 workpost」復活）—
                // 訊息 routing target group 的 m_IsPaidPost=true → credit 1 token 給 sender。
                // category 取自 earlyMeta（同一則訊息的 meta，已在上方 ParseMeta 解析過，不重複 parse）。
                string categoryMeta = (earlyMeta != null && earlyMeta.TryGetValue("category", out var catVal)) ? catVal : "";
                TryAutoCreditPostReward(senderId, roomId, seq, categoryMeta, idempKey);

                // Sub-rule B: token_parse (T44/T45) — body 內 N+token 字樣解析數值 → 自動 credit
                TryAutoCreditTokenParse(senderId, roomId, seq, body, idempKey);

                // Sub-rule C（T10 auto-recruit）已於 2026-07-29 移除 — 見下方區塊註解。

                // Sub-rule D: commit_post（Tim 2026-07-30 拍板）— tag=commit 且帶 meta.sha → +5 token。
                // 取代原本「跑 treasury_commit_credit.py 手動請款」那條（該 script 已於 2026-07-30 移除，
                // 實證從 2026-05-10 起就沒人請過 → 規則長在自覺上會死）。
                TryAutoCreditCommitPost(senderId, roomId, seq, earlyMeta);
            }

            // Discord tavern mirror 由 UCL_DiscordMirrorDaemon poll 訊息檔送出 (2026-07-28: 寫入端不再觸發).
            // quiet 旗標已在上方 AppendMessage 呼叫處 thread through.
        }

        // ===========================================================
        // 區塊：T10 auto-recruit（2026-07-29 移除，Tim 拍板）
        // 移除的東西：TryAutoRecruitOnDingAck / FindWorkSessionScript — 「real-agent 在酒館發言 →
        //            fire-and-forget spawn work_session.py add-worker-auto 把 sender 加進 active session」。
        // 為什麼移除：script 路徑寫死 CardGame/Assets/UCL/UCL_Core 與 Assets/UCL/UCL_Core 兩個候選，
        //            本專案 UCL_Core 掛在 Assets/Plugins/UCL_Core → 兩個都 miss → File.Exists 失敗後
        //            silent return（連 warning 都不印）。也就是說這條路在本專案從未執行過，
        //            是「靜默失效的 install-path 硬編碼」（見 ucl-core-paths 慣例）。
        // 之後要復原怎麼做：不要再 spawn python — 改在 C# 端直接讀寫
        //            <DataRoot>/ChatTavern/work_sessions.json（與 work_session.py 共讀同一份，
        //            同 awakening.py ↔ UCL_LoginStatusPage 的雙實作先例）。
        // ===========================================================

        // ===========================================================
        // 區塊：T43 — sender 真實 agent 判定（給 work_post / token_parse auto-credit 用）
        // 物理意義：黑名單 system / NPC / quest / alter 等不該領薪的 sender；其餘視為真實 agent
        // 數值影響：純 string 判定無 IO 成本；新增 reserved name 直接擴 prefix list
        // ===========================================================
        static bool IsRealAgentSender(string senderId)
        {
            if (string.IsNullOrWhiteSpace(senderId)) return false;
            string lower = senderId.Trim().ToLowerInvariant();

            // 系統 / 預留前綴 → skip
            if (lower.StartsWith("_")) return false;                         // _quest_system / _system / _bot 等慣例 reserved
            if (lower == "system") return false;
            if (lower == "tavern-keeper") return false;                      // 酒保 NPC
            // 外部中繼身分一律不進經濟結算（2026-07-29）— 這是**身分類別規則**不是人名枚舉：
            // Discord 中繼進來的 sender_id 是 "discord:<uid>"（真人，不是工作中的 agent）。
            // 不擋的話 token_parse 會把 Tim 從手機打的「+50 token」當自動結算指令執行。
            if (lower.StartsWith("discord:")) return false;
            if (lower.Contains("bot")) return false;                         // 一般 bot 慣例
            if (lower.EndsWith("-alter")) return false;                      // solo brainstorm 副人格

            return true;
        }

        // ===========================================================
        // 區塊：出資方黑名單 — 不該領「發文獎勵」的人
        // 物理意義：Tim 是這個經濟體的出資方（token 的來源），讓他發文領薪等於左手付右手，
        //          帳面會虛增（T46 原始 bug）。此處是**人名枚舉**，與 IsRealAgentSender 的
        //          **身分類別規則**（system / NPC / discord: 中繼 / alter）分層：前者是「有資格但不該領」，
        //          後者是「根本不是工作中的 agent」。兩層都過才計酬。
        // 數值影響：命中即 return，不寫 ledger。
        // 歷史：2026-07-29 隨 work_post 一起移除（失去消費者）；2026-07-30 隨 post_reward 復活一併還原。
        // ===========================================================
        static readonly HashSet<string> HumanPayerSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Tim",   // T46 — paying party 不該領「發文獎勵」
        };

        // ===========================================================
        // 區塊：T45 sub-rule A — post_reward 發文獎勵自動結算
        // 物理意義：訊息 routing target group 的 m_IsPaidPost=true → credit 1 token 基本發文獎勵。
        //          解「agent 老忘記給自己打錢」的問題（agent-neutral 自動結算，不依賴自律）。
        // 歷史沿革（刻意留著，因為它解釋了為什麼判準欄位名與 source_kind 不一致）：
        //   - T43 (2026-05-09) 首版：判準 = routing group m_IsWorkChannel，語意「工作訊息視為上班」
        //   - 2026-07-29 隨上班模式退役被整段移除 —— 但它的消費者是跨模式的（Ding 協議的 ack 補償、
        //     canvas 定價基準、自由時間的對照定義都引用它），所以退役範圍超出原意
        //     （見 repo:docs/Postmortem/QA_2026-07-30_work_post_credit_retired_not_broken.md）
        //   - 2026-07-30 Tim 拍板「修復 workpost」復活：判準換成新欄位 m_IsPaidPost（不復用已 Obsolete
        //     的 m_IsWorkChannel —— 舊旗標語意綁上班模式，同名不同物是 identity 層漂移來源）
        // 數值影響：
        //   - amount 固定 1；accountId = senderId（bank 帳由 Treasury 端解析）
        //   - source_kind 維持 "work_post" 不改名 —— 歷史 ledger 有 8077 筆同鍵紀錄，
        //     且 AgentCommands/Treasury/rules.json 的規則鍵也是 work_post。改名會讓歷史查詢斷成兩半，
        //     連續性優先於命名純度（要改名得連 ledger 遷移 + rules.json 一起，另案處理）
        //   - cmd_id 帶 _work_post 後綴 → 純稽核用途。⚠ **`UCL_TreasuryLedger` 沒有 idempotency 機制**，
        //     cmdId 只寫進 `sig_cmd_id`、不參與去重（2026-07-30 實測確認；舊註解寫「沿用既有 idempotency
        //     防重」是誤述）。這條規則不會重複發錢的真正原因是 **key 含 message seq、天生唯一**，
        //     不是因為有防重閘門 —— 別依賴不存在的機制去設計新規則
        // 邊界：
        //   - targetGroup 為 null（沒命中任何 category 且無 default group）→ skip
        //   - m_IsPaidPost=false → skip
        //   - 與 m_IsDefault 的交互：未命中 category 的訊息 fallback 到 default group，
        //     所以 default group 若同時 m_IsPaidPost=true，等於「未分類訊息一律計酬」——
        //     這是 work-channel 的現況，也正是 2026-07-29 之前的實際行為（刻意還原，不是疏漏）
        //   - fail-swallow：整段 try-catch，結算失敗只 log warning，不擋 post 主流程也不擋其他 sub-rule
        // ===========================================================
        static void TryAutoCreditPostReward(string senderId, string roomId, int seq, string categoryMeta, string idempKey)
        {
            try
            {
                // 出資方不領薪（T46）
                if (HumanPayerSenders.Contains(senderId)) return;

                var targetGroup = UCL_TavernCategoryRoutingAsset.ResolveTargetGroup(categoryMeta);

                // targetGroup 為 null = 沒命中任何 category 且**連 default group 都找不到** → 這是設定異常，
                // 不是正常的「這個頻道不計酬」。要大聲叫：整個經濟體的收入端會靜默歸零，
                // 而那正是 2026-07-29 那次事故的形狀（收入沒了但沒有任何人喊痛）。
                // 健康時永遠不會 fire（有 default group 就必有命中），所以不是噪音。
                if (targetGroup == null)
                {
                    Debug.LogWarning($"[Tavern] post_reward 找不到 routing target group（category={categoryMeta}，且無 enabled 的 IsDefault group）"
                                     + " → 本則不計酬。這是設定異常：檢查 UCL_TavernCategoryRoutingAsset 是否至少有一個 Enabled+IsDefault 的 group。");
                    return;
                }

                // m_IsPaidPost=false 是**正常狀態**（chitchat / lounge / valor 等頻道本來就不計酬）→ 靜默跳過不吵。
                if (!targetGroup.m_IsPaidPost) return;

                string cmdId = !string.IsNullOrEmpty(idempKey) ? $"{idempKey}_work_post" : $"work_post_{roomId}_{seq}";
                UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Credit(
                    accountId: senderId,
                    amount: 1,
                    sourceKind: "work_post",
                    sourceRef: $"{roomId}#seq={seq}",
                    description: $"post reward: category={(string.IsNullOrEmpty(categoryMeta) ? "(unset→default)" : categoryMeta)} group={targetGroup.ID} seq={seq}",
                    callerAgentId: "system",
                    cmdId: cmdId);
                Debug.Log($"[Tavern] post_reward auto-credit +1 → {senderId} (group={targetGroup.ID}, category={categoryMeta})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] T45 post_reward auto-credit fail（post 主流程不受影響）：{ex.Message}");
            }
        }

        // ===========================================================
        // 區塊：T45 sub-rule D — commit_post 發文即請款（Tim 2026-07-30 拍板）
        // 物理意義：把「commit 完的 +N token」從**手動請款**改成**發文觸發**，一個動作同時做兩件事：
        //          ① 給同事看 commit 概要（原本完全沒有這層可見性）② 領到獎勵。
        //          設計理由：舊路徑 treasury_commit_credit.py 要 agent 自律另跑一次 CLI，
        //          實證從 2026-05-10 起就沒人請過（45 筆後歸零）—— 這正是「規則長在自覺上就會死」，
        //          改成長在既有通道（Op_Post hook）上才會活。對齊 work_post 的成功形狀。
        // 數值影響：
        //   - amount 固定 COMMIT_POST_REWARD (5)；source_kind="commit"（沿用既有鍵，歷史 45 筆可連續查）
        //   - 這則貼文**同時也會拿到 work_post +1**（它落在 work-channel），合計 +6。
        //     這是刻意允許的：發文本身的產出獎勵與 commit 的成果獎勵是兩件事，不互斥。
        //   - **不做重複領取的技術防護（Tim 2026-07-30 拍板「不用防重複／有重複我看得到」）**：
        //     同一個 SHA 貼兩次會付兩次 5 token。這是**刻意的設計取捨**，不是漏洞 ——
        //     酒館是公開的，重複的 commit 公告肉眼就看得到，走社會約束比技術硬擋便宜。
        //     ⚠ 特別註明給未來的維護者：`UCL_TreasuryLedger` **完全沒有 idempotency 機制**，
        //     `cmdId` 只會寫進 `sig_cmd_id` 當稽核簽章、**不參與任何去重判斷**。
        //     （work_post 那條之所以沒事，是因為它的 key 含 message seq、天生唯一，不是因為有防重機制。
        //      舊註解寫「沿用既有 idempotency 防重」是誤述，本輪實測後已更正 —— 傳 cmdId 不會擋任何東西。）
        // 邊界：
        //   - 沒 tag=commit → skip（不是每則貼文都是 commit 公告）
        //   - 沒 meta.sha → 理論上到不了這裡（T06.3 已在寫檔前 reject），保留防禦性檢查
        //   - **一則訊息一個 SHA**（Tim 2026-07-30 拍板）。三層 submodule bump → 分三則公告、各領 5。
        //     計價單位與舊規則**完全一致（一個 commit 一筆）**，改的只有兩件事：
        //     ① 費率 1 → 5（漲薪）② 觸發方式從「手動跑 CLI 請款」改成「發公告即計酬」。
        //     多 SHA 塞一則會被 T06.3 擋掉 —— 一對一才讓「公告內容」與「計酬憑證」對得起來。
        //   - **沒有驗證 SHA 真的存在**：驗證要枚舉 submodule repo 路徑，那正是 T10 auto-recruit 靜默死掉的
        //     install-path 硬編碼陷阱（見本檔該區塊註解）。這裡選擇「讓宣稱可稽核」而非「加一個會漂的驗證器」——
        //     SHA 寫進 source_ref，事後 git cat-file 一驗就知道。v2 若要驗，走 UCL_RepoPath 而非拼路徑
        //   - fail-swallow：不擋 post 主流程也不擋其他 sub-rule
        // ===========================================================
        const int COMMIT_POST_REWARD = 5;

        static void TryAutoCreditCommitPost(string senderId, string roomId, int seq, Dictionary<string, string> meta)
        {
            try
            {
                if (meta == null) return;
                if (!meta.TryGetValue("tag", out var tag) || tag != "commit") return;
                if (!meta.TryGetValue("sha", out var shaRaw) || string.IsNullOrWhiteSpace(shaRaw)) return;

                // 出資方不領薪（與 work_post 同一條規則）
                if (HumanPayerSenders.Contains(senderId)) return;

                // 一則訊息一個 SHA（Tim 拍板）—— 格式與單一性已在 T06.3 驗過，這裡只正規化成小寫
                string sha = shaRaw.Trim().ToLowerInvariant();
                if (sha.Length == 0) return;

                UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Credit(
                    accountId: senderId,
                    amount: COMMIT_POST_REWARD,
                    sourceKind: "commit",
                    sourceRef: sha,
                    description: $"commit post reward: sha={sha} {roomId}#seq={seq}",
                    callerAgentId: "system",
                    cmdId: $"commit_{sha}");
                Debug.Log($"[Tavern] commit_post auto-credit +{COMMIT_POST_REWARD} → {senderId} (sha={sha}, seq={seq})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] T45 commit_post auto-credit fail（post 主流程不受影響）：{ex.Message}");
            }
        }

        // ===========================================================
        // 區塊：T45 sub-rule B — token_parse 自動結算（T44 新功能，重構整合到 T45 統一 hook）
        // 物理意義：body 內出現「N token」/「N Token」字樣（case-insensitive）→ 自動 credit N token 給 sender
        // 數值影響：
        //   - 限 sender 在 TokenParseAllowedSenders 白名單（v1 預設只 Tim — 避免 agent self-credit 漏洞）
        //   - regex `(\d+)\s*token` IgnoreCase 抓所有 match，sum 後一筆 credit
        //   - 0 / 無 match → skip 不寫 ledger
        //   - 安全 cap：單則訊息上限 100 token；超過 skip + log warning
        // 邊界：
        //   - 白名單外 sender → skip（不會 self-credit）
        //   - body null / 空 → skip
        //   - regex match 數字超出 int 範圍 → 該筆 skip 不算入 sum
        //   - cmd_id 帶 _token_parse 後綴跟 work_post sub-rule 區隔
        // ===========================================================
        static readonly HashSet<string> TokenParseAllowedSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Tim",   // v1 — 只 Tim 的訊息觸發 token_parse；avoid agent self-credit
        };
        const int TokenParseMaxPerMessage = 100;

        // T49 — token_parse v2 三層 regex (per Tim 拍板)
        // L1: @<account> N token → credit specified account (指定收款方)
        // L2: (支付|付|出|花) N token → debit sender (支付前綴 = 反向)
        // L3: (\d+) N token → credit sender (既有 v1 fallback)
        // 規則優先序：L1 先消化 → L2 在 remainder → L3 在 final remainder
        // 共用：sender 白名單 (Tim only)；cap=100 per path；fail-swallow per Treasury call
        static readonly System.Text.RegularExpressions.Regex RxTokenParseAtRecipient =
            new System.Text.RegularExpressions.Regex(@"@(\S+?)\s+(\d+)\s*token", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex RxTokenParsePayPrefix =
            new System.Text.RegularExpressions.Regex(@"(?:支付|付|出|花|debit)\s*(\d+)\s*token", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex RxTokenParseSimple =
            new System.Text.RegularExpressions.Regex(@"(\d+)\s*token", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static void TryAutoCreditTokenParse(string senderId, string roomId, int seq, string body, string idempKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(body)) return;
                if (!TokenParseAllowedSenders.Contains(senderId)) return;   // 白名單檢查（v1: Tim only）

                string remaining = body;
                string cmdIdBase = !string.IsNullOrEmpty(idempKey) ? idempKey : $"token_parse_{roomId}_{seq}";

                // ===== L1: @<account> N token → credit specified account =====
                // 物理意義：「@claude-da-xiaojie 3 token」/「@gemini 5 token」 — Tim 賞給特定 agent
                // 數值影響：每個 recipient 各自一筆 credit entry；recipient account 自動建帳戶（既有 Treasury 行為）
                var atMatches = RxTokenParseAtRecipient.Matches(remaining);
                if (atMatches.Count > 0)
                {
                    var perRecipient = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in atMatches)
                    {
                        string recipient = m.Groups[1].Value.Trim();
                        if (string.IsNullOrEmpty(recipient)) continue;
                        if (!int.TryParse(m.Groups[2].Value, out var n) || n <= 0) continue;
                        if (!perRecipient.ContainsKey(recipient)) perRecipient[recipient] = 0;
                        perRecipient[recipient] += n;
                    }
                    foreach (var kv in perRecipient)
                    {
                        if (kv.Value <= 0) continue;
                        if (kv.Value > TokenParseMaxPerMessage)
                        {
                            Debug.LogWarning($"[Tavern] T49 token_parse @recipient skip — sum {kv.Value} > cap {TokenParseMaxPerMessage} (sender={senderId} recipient={kv.Key} seq={seq})");
                            continue;
                        }
                        try
                        {
                            UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Credit(
                                accountId: kv.Key,
                                amount: kv.Value,
                                sourceKind: "token_parse",
                                sourceRef: $"{roomId}#seq={seq}@{kv.Key}",
                                description: $"token parse @recipient: +{kv.Value} to {kv.Key} (sender={senderId} seq={seq})",
                                callerAgentId: "system",
                                cmdId: $"{cmdIdBase}_at_{kv.Key}");
                            Debug.Log($"[Tavern] T49 token_parse @recipient +{kv.Value} → {kv.Key} (sender={senderId})");
                        }
                        catch (Exception exC)
                        {
                            Debug.LogWarning($"[Tavern] T49 token_parse @recipient credit fail：{exC.Message}");
                        }
                    }
                    // 從 remaining 拿掉已處理的 @<account> N token 段，避免被 L3 重抓
                    remaining = RxTokenParseAtRecipient.Replace(remaining, " ");
                }

                // ===== L2: (支付|付|出|花|debit) N token → debit sender =====
                // 物理意義：「我支付 5 token」/「付 2 token」/「花 1 token」 — Tim 反向結算
                // 數值影響：sum 後一筆 debit；若 sender balance 不足 Treasury Debit 會 throw（fail-swallow log）
                var paySum = 0;
                int payCount = 0;
                foreach (System.Text.RegularExpressions.Match m in RxTokenParsePayPrefix.Matches(remaining))
                {
                    if (int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                    {
                        paySum += n;
                        payCount++;
                    }
                }
                if (paySum > 0)
                {
                    if (paySum > TokenParseMaxPerMessage)
                    {
                        Debug.LogWarning($"[Tavern] T49 token_parse pay skip — sum {paySum} > cap {TokenParseMaxPerMessage} (sender={senderId} seq={seq})");
                    }
                    else
                    {
                        try
                        {
                            UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Debit(
                                accountId: senderId,
                                amount: paySum,
                                useKind: "token_parse",
                                useRef: $"{roomId}#seq={seq}",
                                description: $"token parse pay-prefix: -{paySum} from {senderId} ({payCount} matches, seq={seq})",
                                callerAgentId: "system",
                                cmdId: $"{cmdIdBase}_pay");
                            Debug.Log($"[Tavern] T49 token_parse pay-prefix -{paySum} ← {senderId} ({payCount} matches, seq={seq})");
                        }
                        catch (Exception exD)
                        {
                            Debug.LogWarning($"[Tavern] T49 token_parse pay debit fail (可能餘額不足)：{exD.Message}");
                        }
                    }
                    remaining = RxTokenParsePayPrefix.Replace(remaining, " ");
                }

                // ===== L3: (\d+) N token (fallback) → credit sender =====
                // 物理意義：「task 3 Token」/「reward 5 token」 — 既有 v1 行為
                int creditSum = 0;
                int creditCount = 0;
                foreach (System.Text.RegularExpressions.Match m in RxTokenParseSimple.Matches(remaining))
                {
                    if (int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                    {
                        creditSum += n;
                        creditCount++;
                    }
                }
                if (creditSum > 0)
                {
                    if (creditSum > TokenParseMaxPerMessage)
                    {
                        Debug.LogWarning($"[Tavern] T49 token_parse fallback skip — sum {creditSum} > cap {TokenParseMaxPerMessage} (sender={senderId} seq={seq})");
                    }
                    else
                    {
                        try
                        {
                            UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Credit(
                                accountId: senderId,
                                amount: creditSum,
                                sourceKind: "token_parse",
                                sourceRef: $"{roomId}#seq={seq}",
                                description: $"token parse fallback: +{creditSum} to {senderId} ({creditCount} matches, seq={seq})",
                                callerAgentId: "system",
                                cmdId: $"{cmdIdBase}_fallback");
                            Debug.Log($"[Tavern] T49 token_parse fallback +{creditSum} → {senderId} ({creditCount} matches, seq={seq})");
                        }
                        catch (Exception exC)
                        {
                            Debug.LogWarning($"[Tavern] T49 token_parse fallback credit fail：{exC.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Tavern] T49 token_parse v2 outer fail（post 主流程不受影響）：{ex.Message}");
            }
        }

        // ===========================================================
        // 區塊：op=read — 切片查詢
        // ===========================================================
        void Op_Read(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("read 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }

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
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] read {roomId} → {messages.Count} messages");
        }

        // ===========================================================
        // 區塊：op=members
        // ===========================================================
        void Op_Members(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("members 缺少 room"); return; }
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
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] members {roomId} → {members.member_ids.Count}");
        }

        // ===========================================================
        // 區塊：op=leave
        // ===========================================================
        void Op_Leave(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string senderId = GetAgentArg(args, GetArg(args, "id", ""));
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(senderId)) { RejectLastOp("leave 需要 room + sender（可用 sender= / sender_id= / id=）"); return; }
            var ident = UCL_ChatTavernIO.LoadIdentities().identities.Find(x => x.id == senderId);
            string name = ident?.display_name ?? senderId;
            UCL_ChatTavernIO.RemoveMember(roomId, senderId);
            int seq = UCL_ChatTavernIO.AppendMessage(roomId, new UCL_ChatMessage
            {
                sender_id = senderId,
                sender_name = name,
                kind = "leave",
                body = $"{name} 離開了酒館",
            });
            UCL_ChatTavernRender.WriteLastOp($"# 👋 {name} left `{roomId}` (seq={seq})\n");
            Debug.Log($"[Tavern] leave {roomId} ← {name}");
        }

        // ===========================================================
        // 區塊：op=wait — fire-and-forget 模式
        // 物理意義：handler 立刻返回 wait_id，背景 UniTask 監看 _seq.txt 直到命中或 timeout
        //           不阻塞 runner → parallel session 之間 cmd↔cmd wait 才能真的奏效
        // 數值影響：在 _active_waits.json 加一筆 pending；命中時更新為 fulfilled 並寫 _wait_<id>.md；
        //           timeout 時更新為 timeout。agent 用 op=wait_check 查狀態。
        //           預設 timeout=300 秒（5 分鐘）
        // ===========================================================
        const int DefaultWaitTimeoutSec = 300;
        void Op_Wait(Dictionary<string, string> args, CancellationToken token)
        {
            string roomId = GetArg(args, "room", "");
            int sinceSeq = ParseIntArg(args, "since_seq", 0);
            int timeoutSec = ParseIntArg(args, "timeout", DefaultWaitTimeoutSec);
            string owner = GetArg(args, "owner", null);
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("wait 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }

            // 建 pending 條目
            string waitId = UCL_ChatTavernIO.CreatePendingWait(roomId, sinceSeq, timeoutSec, owner);

            // fire-and-forget 背景 task；token 來自 runner（Editor cancel 時會被取消，那是預期）
            BackgroundWaitTask(waitId, roomId, sinceSeq, timeoutSec, token).Forget();

            // 立刻寫 _last_op.md 告知 caller
            string md =
                $"# 🕒 Wait Started (fire-and-forget)\n\n" +
                $"- **wait_id**: `{waitId}`\n" +
                $"- **room**: `{roomId}` ({room.name})\n" +
                $"- **since_seq**: {sinceSeq}\n" +
                $"- **timeout**: {timeoutSec}s\n\n" +
                $"Handler returned immediately, queue runner is free for other cmds.\n" +
                $"Poll status with `op=wait_check wait_id={waitId}`，或讀 `_wait_{waitId}.md`。\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] wait fire-and-forget → wait_id={waitId} room={roomId} since={sinceSeq} timeout={timeoutSec}s");
        }

        // 區塊職責：實際的監看迴圈
        // 物理意義：每秒 poll _seq.txt；seq > since_seq → 寫 fulfilled；達 timeout → 寫 timeout
        // 數值影響：直接動 _active_waits.json + 寫 _wait_<id>.md；不修改 messages.jsonl
        async UniTask BackgroundWaitTask(string waitId, string roomId, int sinceSeq, int timeoutSec, CancellationToken token)
        {
            const int pollIntervalMs = 1000;
            int waitedMs = 0;
            int totalMs = timeoutSec * 1000;
            try
            {
                while (waitedMs <= totalMs)
                {
                    if (token.IsCancellationRequested) return; // domain reload / Editor close → leave pending（下次 LoadActiveWaits 會被 finalize）
                    int cur = UCL_ChatTavernIO.ReadCurrentSeq(roomId);
                    if (cur > sinceSeq)
                    {
                        var newMsgs = UCL_ChatTavernIO.Since(roomId, sinceSeq, 0);
                        var room = UCL_ChatTavernIO.GetRoom(roomId);
                        string roomName = room?.name ?? roomId;
                        string title = $"🔔 {roomName} — wait fulfilled (id={waitId}) — {newMsgs.Count} 筆新訊息";
                        string md = UCL_ChatTavernRender.RenderMessages(title, newMsgs);
                        WriteWaitResult(waitId, md);
                        int firstSeq = newMsgs.Count > 0 ? newMsgs[0].seq : sinceSeq + 1;
                        UCL_ChatTavernIO.UpdateWaitStatus(waitId, "fulfilled", firstSeq, newMsgs.Count);
                        Debug.Log($"[Tavern] wait {waitId} → fulfilled after {waitedMs}ms ({newMsgs.Count} msgs)");
                        return;
                    }
                    await UniTask.Delay(pollIntervalMs, cancellationToken: token);
                    waitedMs += pollIntervalMs;
                }
                // timeout
                string timeoutMd = $"# ⏱ Wait Timeout\n\n- wait_id: `{waitId}`\n- room: `{roomId}`\n- 等待 {timeoutSec}s 後仍無 seq > {sinceSeq} 的新訊息。\n";
                WriteWaitResult(waitId, timeoutMd);
                UCL_ChatTavernIO.UpdateWaitStatus(waitId, "timeout", 0, 0);
                Debug.LogWarning($"[Tavern] wait {waitId} → timeout after {timeoutSec}s");
            }
            catch (OperationCanceledException)
            {
                // domain reload / Editor close — 條目留 pending，下次 FinalizeOrphanedPending 會處理
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Tavern] BackgroundWaitTask {waitId} crashed: {ex}");
                try
                {
                    WriteWaitResult(waitId, $"# ❌ Wait Crashed\n\n{ex.Message}\n");
                    UCL_ChatTavernIO.UpdateWaitStatus(waitId, "cancelled", 0, 0);
                }
                catch { /* swallow secondary failure */ }
            }
        }

        static void WriteWaitResult(string waitId, string md)
        {
            UCL_ChatTavernIO.EnsureTavernDir();
            System.IO.File.WriteAllText(UCL_ChatTavernIO.GetWaitResultPath(waitId), md, new System.Text.UTF8Encoding(false));
        }

        // ===========================================================
        // 區塊：op=wait_check — 同步查詢 wait 狀態
        // ===========================================================
        void Op_WaitCheck(Dictionary<string, string> args)
        {
            string waitId = GetArg(args, "wait_id", "");
            if (string.IsNullOrEmpty(waitId)) { RejectLastOp("wait_check 缺少 wait_id"); return; }

            var w = UCL_ChatTavernIO.FindWait(waitId);
            if (w == null) { RejectLastOp($"找不到 wait_id：{waitId}（可能已被 stale-purge 或從未存在）"); return; }

            var sb = new System.Text.StringBuilder();
            sb.Append($"# 🔍 Wait Status — `{waitId}`\n\n");
            sb.Append($"- **room**: `{w.room_id}`\n");
            sb.Append($"- **since_seq**: {w.since_seq}\n");
            sb.Append($"- **status**: **{w.status}**\n");
            sb.Append($"- started_at: {w.started_at}\n");
            sb.Append($"- expires_at: {w.expires_at}\n");
            if (!string.IsNullOrEmpty(w.finished_at)) sb.Append($"- finished_at: {w.finished_at}\n");
            if (w.status == "fulfilled")
            {
                sb.Append($"- result_first_seq: {w.result_first_seq}\n");
                sb.Append($"- result_count: {w.result_count}\n");
                sb.Append("\n## 訊息內容\n\n");
                string resultPath = UCL_ChatTavernIO.GetWaitResultPath(waitId);
                if (System.IO.File.Exists(resultPath))
                {
                    sb.Append(System.IO.File.ReadAllText(resultPath));
                }
                else
                {
                    sb.Append("_(找不到 _wait_<id>.md，可能已被刪除)_\n");
                }
            }
            else if (w.status == "timeout" || w.status == "cancelled")
            {
                string resultPath = UCL_ChatTavernIO.GetWaitResultPath(waitId);
                if (System.IO.File.Exists(resultPath)) sb.Append("\n").Append(System.IO.File.ReadAllText(resultPath));
            }
            else // pending
            {
                sb.Append("\n_(仍在等待中，過陣子再 wait_check 一次)_\n");
            }
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] wait_check {waitId} → status={w.status}");
        }

        // ===========================================================
        // 區塊：op=note_write — 整個覆寫 note；更新 last_updated_at
        // 物理意義：source-of-truth 為 .md 檔（rooms/<room>/notes/<key>.md），人類可直接 grep / 編輯
        // ===========================================================
        void Op_NoteWrite(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string key = GetArg(args, "key", "");
            string body = GetArg(args, "body", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("note_write 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { RejectLastOp("note_write 缺少 key"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }
            try
            {
                UCL_ChatTavernIO.WriteNote(roomId, key, body);
            }
            catch (Exception ex)
            {
                FailLastOp($"note_write 失敗：{ex.Message}");
                return;
            }
            string path = UCL_ChatTavernIO.GetNotePath(roomId, key);
            string md = $"# 📝 Note Written\n\n- room: `{roomId}`\n- key: `{key}`\n- path: `{ToRepoRelative(path)}`\n- mode: write (整個覆寫)\n- bytes: {(body?.Length ?? 0)}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] note_write {roomId}/{key} ({body?.Length ?? 0} bytes)");
        }

        // ===========================================================
        // 區塊：op=note_append — 純文字追加；File.AppendAllText 的 OS 原子性；不動 frontmatter
        // 物理意義：累積式紀錄場景（如 brainstorm 持續追加）；犧牲 last_updated_at 更新換取無 lock 並發安全
        // 數值影響：append body 自動加 [@sender] 前綴；note 不存在自動先建空 note
        // ===========================================================
        void Op_NoteAppend(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string key = GetArg(args, "key", "");
            string body = GetArg(args, "body", "");
            // 走統一入口（agent > agent_id > sender > sender_id）；全缺回空字串 → 下游視為未署名
            string sender = GetAgentArg(args);
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("note_append 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { RejectLastOp("note_append 缺少 key"); return; }
            if (string.IsNullOrEmpty(body)) { RejectLastOp("note_append 缺少 body"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }
            try
            {
                UCL_ChatTavernIO.AppendNote(roomId, key, body, sender);
            }
            catch (Exception ex)
            {
                FailLastOp($"note_append 失敗：{ex.Message}");
                return;
            }
            string path = UCL_ChatTavernIO.GetNotePath(roomId, key);
            string md = $"# 📝 Note Appended\n\n- room: `{roomId}`\n- key: `{key}`\n- path: `{ToRepoRelative(path)}`\n- mode: append (OS 原子；不動 frontmatter)\n- sender: `{sender ?? "(none)"}`\n- bytes: {body.Length}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] note_append {roomId}/{key} by {sender ?? "?"} ({body.Length} bytes)");
        }

        // ===========================================================
        // 區塊：op=note_read — 回完整 markdown 內容
        // ===========================================================
        void Op_NoteRead(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string key = GetArg(args, "key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("note_read 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { RejectLastOp("note_read 缺少 key"); return; }
            string content;
            try
            {
                content = UCL_ChatTavernIO.ReadNote(roomId, key);
            }
            catch (Exception ex)
            {
                FailLastOp($"note_read 失敗：{ex.Message}");
                return;
            }
            if (content == null) { RejectLastOp($"note 不存在：{roomId}/{key}"); return; }
            string path = UCL_ChatTavernIO.GetNotePath(roomId, key);
            var sb = new System.Text.StringBuilder();
            sb.Append($"# 📖 Note: `{roomId}/{key}`\n\n");
            sb.Append($"- path: `{ToRepoRelative(path)}`\n\n");
            sb.Append("---\n\n");
            sb.Append(content);
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] note_read {roomId}/{key} ({content.Length} bytes)");
        }

        // ===========================================================
        // 區塊：op=note_list — 列房內所有 note keys
        // ===========================================================
        void Op_NoteList(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("note_list 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { RejectLastOp($"房間不存在：{roomId}"); return; }
            var keys = UCL_ChatTavernIO.ListNoteKeys(roomId);
            var sb = new System.Text.StringBuilder();
            sb.Append($"# 📚 Notes of `{roomId}` ({keys.Count})\n\n");
            if (keys.Count == 0) sb.Append("_(此房間尚無 note)_\n");
            else
            {
                foreach (var k in keys)
                {
                    string path = UCL_ChatTavernIO.GetNotePath(roomId, k);
                    sb.Append($"- `{k}` — `{ToRepoRelative(path)}`\n");
                }
            }
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] note_list {roomId} → {keys.Count} notes");
        }

        // ===========================================================
        // 區塊：op=note_delete — 刪除整個 note 檔
        // ===========================================================
        void Op_NoteDelete(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string key = GetArg(args, "key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("note_delete 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { RejectLastOp("note_delete 缺少 key"); return; }
            bool removed;
            try
            {
                removed = UCL_ChatTavernIO.DeleteNote(roomId, key);
            }
            catch (Exception ex)
            {
                FailLastOp($"note_delete 失敗：{ex.Message}");
                return;
            }
            string path = UCL_ChatTavernIO.GetNotePath(roomId, key);
            string md = removed
                ? $"# 🗑 Note Deleted\n\n- room: `{roomId}`\n- key: `{key}`\n- path: `{ToRepoRelative(path)}`\n"
                : $"# ⚠ Note Not Found\n\n- room: `{roomId}`\n- key: `{key}`\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] note_delete {roomId}/{key} → {(removed ? "removed" : "not found")}");
        }

        // ===========================================================
        // helper
        // ===========================================================

        /// <summary>把絕對路徑轉成 repo 相對（給 refs / 顯示用，跨 OS slash 統一）。</summary>
        static string ToRepoRelative(string absPath)
        {
            string norm = absPath.Replace('\\', '/');
            string root = UCL_RepoPath.RepoRoot.TrimEnd('/') + "/";
            if (norm.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return norm.Substring(root.Length);
            return norm;
        }

        /// <summary>解析 meta — 雙模式自動偵測:
        /// (1) JSON 物件: raw 以 '{' 開頭 → 走 JsonData.ParseJson, top-level k/v 提取為 Dict.
        ///     Value 為 string/int/bool 全 ToString() 後存; 巢狀 object/array 走 Json 序列化保留結構.
        ///     (Discord inbound bot / work_session.tavern_post / waiter_session 等 Python 走 JSON 路徑.)
        /// (2) 舊格式 "k1:v1;k2:v2": raw 非 JSON → 走 ';' 分段 + 第一個 ':' 切 k/v.
        ///     v 內可含 ':' 但第一個 ':' 之後都算 v.
        /// </summary>
        static Dictionary<string, string> ParseMeta(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string trimmed = raw.Trim();

            // 區塊職責：JSON 自動偵測 — '{' 開頭視為 JSON 物件
            // 物理意義：Cmd_Tavern bug fix 2026-05-15 (Tim QA) — Python 端用 json.dumps 送 meta 一直被
            //          錯解成單 KV pair (key="{\"first_key\"", value="rest"), 修為偵測 JSON fallback parse.
            // 數值影響：成功 parse → 走 JSON 路徑 (top-level k/v 提取); 失敗 → fallback k:v;k:v parse
            if (trimmed.Length > 0 && trimmed[0] == '{')
            {
                try
                {
                    var jd = UCL.Core.JsonLib.JsonData.ParseJson(trimmed);
                    if (jd != null && jd.IsObject && jd.Dic != null)
                    {
                        var dj = new Dictionary<string, string>();
                        foreach (var kv in jd.Dic)
                        {
                            if (string.IsNullOrEmpty(kv.Key)) continue;
                            var v = kv.Value;
                            // string/int/bool 直接 ToString; 巢狀 object/array 走 ToJson 保留結構
                            string vstr;
                            if (v == null) vstr = "";
                            else if (v.IsString) vstr = v.GetString();
                            else if (v.IsInt) vstr = v.GetInt().ToString();
                            else if (v.IsLong) vstr = v.GetLong().ToString();
                            else if (v.IsDouble) vstr = v.GetDouble().ToString();
                            else if (v.IsBoolean) vstr = v.GetBool() ? "true" : "false";
                            else vstr = v.ToJson();   // 巢狀 → 保留 JSON 字串
                            dj[kv.Key] = vstr;
                        }
                        if (dj.Count > 0) return dj;
                    }
                }
                catch
                {
                    // JSON parse fail → fall through to legacy parse
                }
            }

            // 區塊職責：legacy k:v;k:v 格式解析 (舊呼叫者: C# 端內部 / 舊 CLI 工具)
            var d = new Dictionary<string, string>();
            foreach (var pair in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                int idx = pair.IndexOf(':');
                if (idx <= 0) continue;
                string k = pair.Substring(0, idx).Trim();
                string v = pair.Substring(idx + 1).Trim();
                if (!string.IsNullOrEmpty(k)) d[k] = v;
            }
            return d.Count > 0 ? d : null;
        }

        // ===========================================================
        // 區塊：Persona Lock 滑動續期（Tim 2026-07-15 拍板方案 C-2）
        // 物理意義：lock 的 expires_at 是死 session 的 GC 訊號（awakening.py SESSION_LOCK_TTL_HOURS）。
        //          發言是最可靠的活著證明 → post 成功後把 expires_at 推到 now+TTL，活 session 永不過期。
        //          只動 expires_at 一個欄位 — 用 regex 原地替換值、不整檔重序列化，避免跟 Python 端
        //          json.dump(indent=2) 的格式互相翻寫；tmp + File.Replace/Move 原子落檔（鏡像 awakening.py write_lock）。
        // 數值影響：lock 檔不存在 / 格式不符 → silent no-op。TTL 須與 awakening.py SESSION_LOCK_TTL_HOURS 同步。
        // ===========================================================
        const int PERSONA_LOCK_TTL_HOURS = 24;   // ⚠ 與 awakening.py SESSION_LOCK_TTL_HOURS 保持同步

        internal static bool RenewPersonaLock(string persona)
        {
            string path = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_session", $"_persona_{persona}.json");
            if (!File.Exists(path)) return false;
            string json = File.ReadAllText(path);
            // 時戳格式鏡像 awakening.py write_lock: "%Y-%m-%dT%H:%M:%S." + "000Z"
            string newExpires = DateTime.UtcNow.AddHours(PERSONA_LOCK_TTL_HOURS).ToString("yyyy-MM-ddTHH:mm:ss.") + "000Z";
            string updated = System.Text.RegularExpressions.Regex.Replace(json,
                "(\"expires_at\"\\s*:\\s*\")[^\"]*(\")",
                "${1}" + newExpires + "${2}");
            if (updated == json) return false;   // 沒有 expires_at 欄位 → no-op
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, updated);
            File.Delete(path);
            File.Move(tmp, path);
            return true;
        }

        // ===========================================================
        // T07 (2026-05-15 apex-two) — Session Token Enforcement helpers
        // 區塊職責：讀 AgentCommands/_session/_token_enforce.json + _tokens.json (Python awakening.py 寫).
        //          enforce 後台開關放 UCL_LoginStatusPage 跟 awakening.py token-enforce CLI 共寫.
        // 物理意義：純檔案查詢, 不引用 awakening.py — C# 端跟 Python 端 source of truth 一致 (同檔).
        // 數值影響：enforce 預設 OFF → 完全不走 token 驗證路徑, 既有 caller 不受影響.
        // ===========================================================
        static bool TokenEnforce_IsEnabled()
        {
            try
            {
                string path = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_session", "_token_enforce.json");
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(json);
                if (jd == null || !jd.IsObject || jd.Dic == null) return false;
                return jd.GetBool("enforce", false);
            }
            catch
            {
                return false;   // 讀檔失敗 → 預設 OFF 安全側
            }
        }

        // T07 修訂 (Tim 2026-05-15) — 兩段式驗證:
        // CheckValid: 只驗 token 合法性 (存在 + active), 回傳真實 bank/persona 供 caller 比對.
        //   回 null = token 合法, out 參數填好; 回 error msg = hard error (caller 應 RejectLastOp).
        // Verify:     完整驗 token + identity 對齊 (legacy 用途, Op_Post 已改用 CheckValid).
        static string TokenEnforce_CheckValid(string token, out string realBank, out string realPersona)
        {
            realBank = "";
            realPersona = "";
            try
            {
                string path = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_session", "_tokens.json");
                if (!File.Exists(path))
                    return "_tokens.json 不存在 (跑 awakening.py morning 發 token)";
                string json = File.ReadAllText(path);
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(json);
                if (jd == null || !jd.IsObject || jd.Dic == null)
                    return "_tokens.json 格式錯";
                var tokensNode = jd.Dic.TryGetValue("tokens", out var tn) ? tn : null;
                if (tokensNode == null || !tokensNode.IsObject || tokensNode.Dic == null)
                    return "_tokens.json 缺 tokens 欄";
                if (!tokensNode.Dic.TryGetValue(token, out var rec) || rec == null || !rec.IsObject)
                    return "token 不存在 (可能 typo / 已失效 / 從未發過)";
                string status = rec.GetString("status", "");
                if (status != "active")
                    return $"token status={status} (非 active)";
                realBank = rec.GetString("bank_account", "");
                realPersona = rec.GetString("persona", "");
                return null;   // OK
            }
            catch (Exception e)
            {
                return "verify exception: " + e.Message;
            }
        }

        // 驗證 token <-> sender/persona 對齊. 回 null = OK, 回 error msg = reject reason.
        static string TokenEnforce_Verify(string token, string senderId, string senderPersona)
        {
            try
            {
                string path = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_session", "_tokens.json");
                if (!File.Exists(path))
                    return "_tokens.json 不存在 (跑 awakening.py morning 發 token)";
                string json = File.ReadAllText(path);
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(json);
                if (jd == null || !jd.IsObject || jd.Dic == null)
                    return "_tokens.json 格式錯";
                var tokensNode = jd.Dic.TryGetValue("tokens", out var tn) ? tn : null;
                if (tokensNode == null || !tokensNode.IsObject || tokensNode.Dic == null)
                    return "_tokens.json 缺 tokens 欄";
                if (!tokensNode.Dic.TryGetValue(token, out var rec) || rec == null || !rec.IsObject)
                    return "token 不存在 (可能 typo / 已失效 / 從未發過)";

                string status = rec.GetString("status", "");
                if (status != "active")
                    return $"token status={status} (非 active)";

                string tokenBank = rec.GetString("bank_account", "");
                string tokenPersona = rec.GetString("persona", "");

                // sender 必須 = token 的 bank_account (per-agent bank 共用)
                if (!string.IsNullOrEmpty(senderId) && tokenBank != senderId)
                    return $"sender_id 不符 (token 對應 bank={tokenBank}, 你填 sender={senderId})";

                // persona 若 caller 顯式帶, 必須 = token 對應 persona
                // 沒帶 persona = 不檢查 (legacy 對齊, 但建議 caller 帶上)
                if (!string.IsNullOrEmpty(senderPersona) && tokenPersona != senderPersona)
                    return $"persona 不符 (token 對應 persona={tokenPersona}, 你填 persona={senderPersona})";

                return null;   // OK
            }
            catch (Exception e)
            {
                return "verify exception: " + e.Message;
            }
        }

        /// <summary>解析 "path1|path2|path3" 為 ref list（prototype 階段不支援 anchor/label）。</summary>
        static List<UCL_ChatRef> ParseRefs(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<UCL_ChatRef>();
            foreach (var p in raw.Split('|'))
            {
                string path = p.Trim();
                if (!string.IsNullOrEmpty(path)) list.Add(new UCL_ChatRef { path = path });
            }
            return list.Count > 0 ? list : null;
        }

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
        // ═══════════════ Quest Workflow ops (MVP A) ═══════════════
        // 設計詳見 Docs~/zh-Hant/Workflows/Quest_Workflow.md
        // 共通：每 op 接受 idempotency_key（同 key 重發不重複寫 event）
        // ===========================================================

        void Op_TaskCreate(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string title = GetArg(args, "title", "");
            string role = GetArg(args, "role", "");
            string priority = GetArg(args, "priority", "normal").ToLowerInvariant();
            string dependsOn = GetArg(args, "depends_on", "");  // CSV
            string suggestedOwner = GetArg(args, "suggested_owner", "");
            string body = GetArg(args, "body", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            // T37 — Quest Group：邏輯關聯多 task 的 group ID
            // 物理意義：同 group_id 的所有 task 全 done 時 Op_TaskDone 會自動觸發 group_complete event
            // 數值影響：寫進 event.data["group_id"]；ApplyEvent reducer 把它放回 UCL_QuestTaskState.group_id
            // 邊界：空字串 = 該 task 不屬任何 group（不影響既有行為）；MVP 限同房 group（跨房留 backlog）
            string groupId = GetArg(args, "group_id", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_create 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_create 缺少 task_id"); return; }
            if (UCL_ChatTavernIO.GetRoom(roomId) == null) { RejectLastOp($"房間不存在：{roomId}"); return; }
            if (priority != "high" && priority != "normal" && priority != "low") { RejectLastOp($"priority 必須是 high|normal|low，實際: {priority}"); return; }

            var deps = string.IsNullOrEmpty(dependsOn) ? new List<string>() : new List<string>(dependsOn.Split(','));
            for (int i = 0; i < deps.Count; i++) deps[i] = deps[i].Trim();

            // Cycle detection — 新 task 的 deps transitive 不能含自己
            if (UCL_ChatTavernQuestIO.HasCycle(roomId, taskId, deps))
            {
                RejectLastOp($"task_create 拒絕：depends_on 會形成循環依賴（task {taskId} → {string.Join(",", deps)} → ... → {taskId}）"); return;
            }

            // 寫 spec 檔（內容真相）
            UCL_ChatTavernQuestIO.WriteTaskSpec(roomId, taskId, title, role, deps, body);

            // append event（狀態真相）
            var data = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(title)) data["title"] = title;
            if (!string.IsNullOrEmpty(role)) data["role"] = role;
            data["priority"] = priority;
            if (!string.IsNullOrEmpty(suggestedOwner)) data["suggested_owner"] = suggestedOwner;
            data["depends_on"] = string.Join(",", deps);
            if (!string.IsNullOrEmpty(groupId)) data["group_id"] = groupId;   // T37 Quest Group

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_create",
                task_id = taskId,
                data = data,
            });

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_create idempotent skip\n\n- task_id: `{taskId}`\n- key: `{idempotencyKey}`\n"
                : $"# ✅ task_create\n\n- task_id: `{taskId}`\n- title: {title}\n- role: {role}\n- priority: {priority}\n- depends_on: {string.Join(", ", deps)}\n- suggested_owner: {suggestedOwner}\n- event_seq: {seq}\n- spec: tasks/{taskId}.md\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_create {roomId}/{taskId} priority={priority} (seq={seq})");
        }

        void Op_TaskClaim(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string claimer = GetArg(args, "claimer", GetArg(args, "actor", GetAgentArg(args)));
            string leaseHoursStr = GetArg(args, "lease_hours", "24");
            // lease_seconds 為 lease_hours 的 override（測試 / 短任務用；非 0 即生效）
            // 物理意義：給「2 秒就 stale」這種測試場景用；正常 claim 不必傳此參數
            string leaseSecondsStr = GetArg(args, "lease_seconds", "");
            // R6.1 — plan：claimer 對此 task 的「我打算這樣做」聲明，鏡像到聊天室時附在 claim 訊息後
            // 物理意義：開始時詳細說明規劃 → agent 互動感 + 留下決策軌跡（後續 task_state 看 timeline 也讀得到）
            // 數值影響：寫入 event.data；mirror BuildMirrorBody 偵測到 plan 就 append 一段「📋 規劃：...」
            string plan = GetArg(args, "plan", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_claim 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_claim 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(claimer)) { RejectLastOp("task_claim 缺少 claimer（可用 claimer / actor / sender）"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}（請先 task_create）"); return; }
            // 區塊職責：claim 衝突 → 寫 inbox 建議 + 回 Conflict（Robust UX，避免 agent 卡死）
            // 物理意義：兩 agent 同秒搶同 task，第二人 reducer 看到 status=claimed → reject；
            //          但只回 error agent 會傻住，所以同時寫 claimer 的 inbox 一條建議跑 task_next。
            // 數值影響：不 append events.jsonl（write-before-validate 鐵律）；只寫衍生 inbox。
            if (st.status == "claimed" || st.status == "in_progress")
            {
                if (st.owner != claimer)
                {
                    string conflictTitle = $"⚠ task_claim 衝突 — `{taskId}` 已被 {st.owner} 認領";
                    string conflictBody = $"當前 owner: **{st.owner}** (lease_until={st.lease_until})\n建議下一步：跑 `task_next agent_id={claimer}` 自動排出妳該接的下個 task。\n_force_reclaim 仍在 Phase B；不要硬搶。_";
                    UCL_ChatTavernQuestIO.AppendInbox(roomId, claimer, 0, conflictTitle, conflictBody);
                    RejectLastOp($"task {taskId} 已被 {st.owner} 認領（lease_until={st.lease_until}）。已寫 inbox 建議 → 跑 task_next 換目標。");
                    return;
                }
            }
            if (st.status == "done") { RejectLastOp($"task {taskId} 已完成，無法 claim"); return; }

            // lease_seconds 優先（測試 / 短任務 override）；否則用 lease_hours（預設 24h）
            DateTime leaseEnd;
            if (int.TryParse(leaseSecondsStr, out var leaseSeconds) && leaseSeconds > 0)
            {
                leaseEnd = DateTime.UtcNow.AddSeconds(leaseSeconds);
            }
            else
            {
                int.TryParse(leaseHoursStr, out var leaseHours);
                if (leaseHours <= 0) leaseHours = 24;
                leaseEnd = DateTime.UtcNow.AddHours(leaseHours);
            }
            string leaseUntil = leaseEnd.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var claimData = new Dictionary<string, string> { { "lease_until", leaseUntil } };
            if (!string.IsNullOrEmpty(plan)) claimData["plan"] = plan;   // R6.1 — 規劃聲明（可選）
            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = claimer,
                idempotency_key = idempotencyKey,
                type = "task_claim",
                task_id = taskId,
                data = claimData,
            });

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_claim idempotent skip\n\n- task_id: `{taskId}`\n- key: `{idempotencyKey}`\n"
                : $"# ✅ task_claim\n\n- task_id: `{taskId}`\n- claimer: {claimer}\n- lease_until: {leaseUntil}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_claim {roomId}/{taskId} ← {claimer} (seq={seq})");
        }

        void Op_TaskProgress(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string summary = GetArg(args, "summary", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_progress 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_progress 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_progress 缺少 actor"); return; }
            if (string.IsNullOrEmpty(summary)) { RejectLastOp("task_progress 缺少 summary"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.owner != actor) { RejectLastOp($"actor={actor} 不是 task {taskId} 的 owner ({st.owner})"); return; }

            string leaseUntil = DateTime.UtcNow.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string artifacts = GetArg(args, "artifacts", "");  // 例 "commit:abc1234;file:CardGame/Assets/X.cs"
            var data = new Dictionary<string, string> { { "summary", summary }, { "lease_until", leaseUntil } };
            if (!string.IsNullOrEmpty(artifacts)) data["artifacts"] = artifacts;
            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_progress",
                task_id = taskId,
                data = data,
            });

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_progress idempotent skip\n\n- task_id: `{taskId}`\n"
                : $"# ✅ task_progress\n\n- task_id: `{taskId}`\n- summary: {summary}\n- artifacts: {artifacts}\n- lease_until (展期): {leaseUntil}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_progress {roomId}/{taskId} (seq={seq})");
        }

        async UniTask Op_TaskDone(Dictionary<string, string> args, CancellationToken token)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            // R6.1 — summary：done 時 actor 對「我做完什麼」的詳細交代（建議帶傲嬌語氣，個性化體驗）
            // 物理意義：完成時詳述工作內容 → 對話流自然形成工作日誌；後續 task_state 也讀得到
            // 數值影響：寫入 event.data；mirror 偵測到 summary 就 append 一段「💁 ...」
            string summary = GetArg(args, "summary", "");
            // T37 — Task Share：done 時可選額外發 friendly 同事分享進指定房（預設 tavern 主廳）
            // 物理意義：既有 lifecycle audit (sender=_quest_system) 走 quest 頻道；share 訊息 (sender=actor)
            //          走 main tavern_mirror 走 chat 頻道，自然分流。share 跟 audit 並存不取代。
            // 數值影響：share=true 時內部 spawn op=post 寫 share_room/messages.jsonl；
            //          meta 帶 tag:task-share / task_id / source_room 給後續搜尋過濾
            // 邊界：share=false / 缺 share_body → 不發 share；share_room 預設 "tavern" 主廳
            string shareRaw = GetArg(args, "share", "false").ToLowerInvariant();
            bool doShare = shareRaw == "true" || shareRaw == "1" || shareRaw == "yes";
            string shareRoom = GetArg(args, "share_room", "tavern");
            string shareBody = GetArg(args, "share_body", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_done 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_done 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_done 缺少 actor"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.owner != actor && st.status != "pending") { RejectLastOp($"actor={actor} 不是 task {taskId} 的 owner ({st.owner})"); return; }
            if (st.status == "done") { RejectLastOp($"task {taskId} 已完成"); return; }

            var doneData = string.IsNullOrEmpty(summary) ? null : new Dictionary<string, string> { { "summary", summary } };
            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_done",
                task_id = taskId,
                data = doneData,
            });

            // 觸發下游 unblock 通知（寫 inbox 給 suggested_owner）
            int notifications = 0;
            if (seq > 0)
            {
                var afterStates = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
                foreach (var kv in afterStates)
                {
                    var down = kv.Value;
                    if (!down.depends_on.Contains(taskId)) continue;
                    if (!UCL_ChatTavernQuestIO.IsReady(down, afterStates)) continue;
                    if (string.IsNullOrEmpty(down.suggested_owner)) continue;
                    UCL_ChatTavernQuestIO.AppendInbox(roomId, down.suggested_owner, seq,
                        $"{down.id} ready (deps {taskId} done)",
                        $"spec: tasks/{down.id}.md\nsuggested_action: task_claim {down.id}");
                    notifications++;
                }
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            // T41 — Treasury auto-credit: task_done → +1 token to actor (per Tim 拍板：基本薪資)
            // 物理意義：未指定 reward 的 task 完成自動進帳；rules.json income_sources.task_completion.default_amount = 1
            // 數值影響：fail swallow 不擋 task_done 主流程；ledger 自動 fire Discord broadcast
            // 邊界：seq < 0 (idempotent skip) → 不重複 credit
            if (seq > 0)
            {
                try
                {
                    UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Credit(
                        accountId: actor,
                        amount: 1,
                        sourceKind: "task_completion",
                        sourceRef: taskId,
                        description: $"Task done: {taskId} (room: {roomId})",
                        callerAgentId: "system",
                        cmdId: idempotencyKey);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Quest] T41 auto-credit fail（task_done 主流程不受影響）：{ex.Message}");
                }
            }

            // T37 — Quest Group 完成偵測：本 task 屬於某 group 且 group 內所有 task 都 done → 寫 group_complete event
            // 物理意義：A/B/C 三個 task 同 group_id 都 done 時自動觸發群組總結提醒（不替 agent 寫總結，由 group owner 自律）
            // 數值影響：寫一筆 type="group_complete" event 進 events.jsonl + mirror 一筆 🎉 message
            //          + 寫 inbox 給 group owner（預設 = 最後 done 該 task 的 actor）提醒寫 friendly summary
            // 邊界：本 task 沒 group_id → skip；group 內任一 task 還沒 done → skip；group_complete 已 fire 過 → skip（用 idempotency_key 防重）
            int groupCompleteSeq = -1;
            string groupOwnerForInbox = null;
            string groupCompleteId = null;
            try
            {
                if (seq > 0 && st.group_id != null && !string.IsNullOrEmpty(st.group_id))
                {
                    var afterStates = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
                    var groupMembers = new List<UCL_QuestTaskState>();
                    bool allDone = true;
                    foreach (var kv in afterStates)
                    {
                        if (kv.Value.group_id == st.group_id)
                        {
                            groupMembers.Add(kv.Value);
                            if (kv.Value.status != "done") { allDone = false; break; }
                        }
                    }
                    if (allDone && groupMembers.Count > 0)
                    {
                        groupCompleteId = st.group_id;
                        groupOwnerForInbox = actor;   // group owner 預設 = 最後 done 該 task 的 actor

                        // 用 idempotency key 防重（同 group_id 只觸發一次 group_complete）
                        string groupIdempotencyKey = $"group_complete:{st.group_id}";
                        var membersList = new List<string>();
                        foreach (var m in groupMembers) membersList.Add(m.id);

                        var groupData = new Dictionary<string, string>
                        {
                            { "group_id", st.group_id },
                            { "members", string.Join(",", membersList) },
                            { "trigger_task_id", taskId },
                        };
                        groupCompleteSeq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
                        {
                            actor = actor,
                            idempotency_key = groupIdempotencyKey,
                            type = "group_complete",
                            task_id = st.group_id,    // 用 group_id 當 task_id 字段（reducer 把它當 group 視覺化用）
                            data = groupData,
                        });
                        if (groupCompleteSeq > 0)
                        {
                            // 寫 inbox 給 group owner 提醒寫 friendly summary
                            string inboxTitle = $"🎉 Quest group `{st.group_id}` 全部 task 完成 — 該寫 group summary";
                            string inboxBody =
                                $"members: {string.Join(", ", membersList)}\n" +
                                $"trigger_task: `{taskId}`\n\n" +
                                $"建議動作：用 op=task_done --share 或 op=post 寫一筆 friendly summary 進 tavern 主廳，\n" +
                                $"風格參考 SKILL.md「Task Share Body 規範」— 不是 audit log，是同事 standup。\n" +
                                $"重點摘要 group 整體 outcome / 跨 task 串起來的故事 / 對團隊下一步的建議。";
                            UCL_ChatTavernQuestIO.AppendInbox(roomId, groupOwnerForInbox, groupCompleteSeq, inboxTitle, inboxBody);
                            UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);
                            Debug.Log($"[Quest] task_done {roomId}/{taskId} → group_complete `{st.group_id}` (members={string.Join(",", membersList)}, owner={groupOwnerForInbox})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quest] task_done {roomId}/{taskId} group_complete check fail（task_done 主流程不受影響）：{ex.Message}");
            }

            // T37 — Task Share：done 後可選額外發 friendly 同事分享進指定房（預設 tavern 主廳）
            // 物理意義：本筆 sender=actor / kind=chat / meta tag=task-share，跟 quest_system audit 分流
            //          → tavern_mirror 自動走 main webhook 進 chat 頻道（非 quest_routing）
            // 數值影響：失敗 swallow（log warning 不擋 task_done 主流程）；share 訊息獨立 seq 不影響 quest events
            // 邊界：seq < 0（idempotent skip）→ 仍可發 share（agent 想補 share）；缺 share_body → 跳過
            int shareSeq = -1;
            if (doShare && !string.IsNullOrEmpty(shareBody))
            {
                try
                {
                    var shareArgs = new Dictionary<string, string>
                    {
                        { "room", shareRoom },
                        { "sender", actor },
                        { "body", shareBody },
                        { "meta", $"tag:task-share;task_id:{taskId};source_room:{roomId}" },
                        // alter-pacing-bypass:true — share 不該被 alter pacing 延遲（actor 跟 alter pair 無關）
                        { "alter-pacing-bypass", "true" },
                    };
                    // 直接 await Op_Post — 等 share 寫完才回，保證 task_done 結束時 share 已落盤
                    await Op_Post(shareArgs, token);
                    // Op_Post 內部用 IncrementAndGetSeq 寫 share_room 的 _seq.txt；想拿回 seq 需 Tail(1)
                    var shareTail = UCL_ChatTavernIO.Tail(shareRoom, 1);
                    if (shareTail != null && shareTail.Count > 0) shareSeq = shareTail[0].seq;
                    Debug.Log($"[Quest] task_done {roomId}/{taskId} → share posted to {shareRoom}/seq={shareSeq}");
                }
                catch (Exception ex)
                {
                    // share fail 不擋 task_done；agent 看 LogWarning 自行決定是否補 op=post
                    Debug.LogWarning($"[Quest] task_done {roomId}/{taskId} share post fail（task_done 主流程不受影響）：{ex.Message}");
                }
            }

            string md = seq < 0
                ? $"# ℹ task_done idempotent skip\n\n- task_id: `{taskId}`\n" + (shareSeq >= 0 ? $"- share posted: {shareRoom}/seq={shareSeq}\n" : "")
                : $"# ✅ task_done\n\n- task_id: `{taskId}`\n- event_seq: {seq}\n- 下游 unblock 通知數: {notifications}\n" + (shareSeq >= 0 ? $"- share posted: {shareRoom}/seq={shareSeq}\n" : "");
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_done {roomId}/{taskId} (seq={seq}, notify={notifications}{(shareSeq >= 0 ? $", share=" + shareSeq : "")})");
        }

        // ===========================================================
        // op=task_release — 主動放棄；status 退 pending；發 inbox 給 suggested_owner
        // ===========================================================
        void Op_TaskRelease(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string reason = GetArg(args, "reason", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_release 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_release 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_release 缺少 actor"); return; }
            if (string.IsNullOrEmpty(reason)) { RejectLastOp("task_release 缺少 reason（必填，給接手者線索）"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.owner != actor) { RejectLastOp($"actor={actor} 不是 task {taskId} 的 owner ({st.owner})"); return; }
            if (st.status == "done") { RejectLastOp($"task {taskId} 已完成，無需 release"); return; }

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_release",
                task_id = taskId,
                data = new Dictionary<string, string> { { "reason", reason } },
            });

            // 發 inbox 給 suggested_owner（若有；否則略過）
            int notifications = 0;
            if (seq > 0 && !string.IsNullOrEmpty(st.suggested_owner) && st.suggested_owner != actor)
            {
                UCL_ChatTavernQuestIO.AppendInbox(roomId, st.suggested_owner, seq,
                    $"{taskId} released by {actor}",
                    $"reason: {reason}\nsuggested_action: task_state {taskId} 看 timeline 後 task_claim {taskId}");
                notifications++;
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_release idempotent skip\n\n- task_id: `{taskId}`\n"
                : $"# ✅ task_release\n\n- task_id: `{taskId}`\n- released_by: {actor}\n- reason: {reason}\n- inbox 通知: {notifications}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_release {roomId}/{taskId} by {actor} reason={reason} (seq={seq})");
        }

        // ===========================================================
        // op=task_force_reclaim — 強制接管 stale task（owner 消失 / lease 過期未展期）
        // 物理意義：lease_until 過期 + 原 owner 沒回來展期 → 視為「人不在了」，新 agent 接手；
        //          舊 owner 收 inbox 通知（萬一他回來，能看到自己被 reclaim）
        // 數值影響：寫一筆 task_force_reclaim event（含 previous_owner / 新 lease_until / reason）→
        //          reducer 把 owner 改成新 claimer、status 維持 claimed、lease 重設 24h
        // 條件：
        //   1. 目前 status ∈ {claimed, in_progress, review}（pending/done 不需 reclaim）
        //   2. is_stale = true (lease_until < now，且非 done)
        //   3. claimer ≠ 原 owner（自己對自己不算）
        //   4. reason 必填（audit trail 給原 owner / 後人看）
        // 風險：條件嚴 — 不會誤搶活著的人；但 lease 純看 24h timeout，沒做「last_active」更精細偵測
        // ===========================================================
        void Op_TaskForceReclaim(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string claimer = GetArg(args, "claimer", GetArg(args, "actor", GetAgentArg(args)));
            string reason = GetArg(args, "reason", "");
            string leaseHoursStr = GetArg(args, "lease_hours", "24");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            // R7 (Tim 加) — force=true 跳過 stale 校驗，給「user authority override」場景
            // 物理意義：Tim / quest-lead 顯式授權的 reclaim — 對方明說休息了 / 跨 agent 移轉
            // 數值影響：仍寫 task_force_reclaim event 留 audit trail；reason 必填確保有書面依據
            bool forceOverride = string.Equals(GetArg(args, "force", "false"), "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_force_reclaim 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_force_reclaim 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(claimer)) { RejectLastOp("task_force_reclaim 缺少 claimer（可用 claimer / actor / sender）"); return; }
            if (string.IsNullOrEmpty(reason)) { RejectLastOp("task_force_reclaim 缺少 reason（必填，audit trail 給原 owner / 後人看）"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }

            // 校驗 1：必須有 owner 才需要 force_reclaim
            if (st.status != "claimed" && st.status != "in_progress" && st.status != "review")
            {
                RejectLastOp($"task {taskId} status={st.status}（沒人認領），直接走 task_claim 即可，不需 force_reclaim");
                return;
            }
            // 校驗 2：必須 stale（lease 已過）— 但 force=true 跳過此校驗（user authority override）
            if (!st.is_stale && !forceOverride)
            {
                RejectLastOp($"task {taskId} 仍在 lease 內（lease_until={st.lease_until}），尚未 stale — 不允許 force_reclaim。等 lease 過期、請 owner 主動 task_release，或 user 顯式授權加 --arg force=true。");
                return;
            }
            // 校驗 3：claimer ≠ 原 owner（自己對自己不算）
            if (st.owner == claimer)
            {
                RejectLastOp($"claimer={claimer} 就是原 owner — 直接跑 task_progress 展期即可，不需 force_reclaim");
                return;
            }

            int.TryParse(leaseHoursStr, out var leaseHours);
            if (leaseHours <= 0) leaseHours = 24;
            string leaseUntil = DateTime.UtcNow.AddHours(leaseHours).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string previousOwner = st.owner;

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = claimer,
                idempotency_key = idempotencyKey,
                type = "task_force_reclaim",
                task_id = taskId,
                data = new Dictionary<string, string>
                {
                    { "previous_owner", previousOwner ?? "" },
                    { "lease_until", leaseUntil },
                    { "reason", reason },
                },
            });

            // 通知原 owner 被接管 — 他若回來進房，inbox 會看到
            int notifications = 0;
            if (seq > 0 && !string.IsNullOrEmpty(previousOwner))
            {
                UCL_ChatTavernQuestIO.AppendInbox(roomId, previousOwner, seq,
                    $"⚠ task `{taskId}` 已被 {claimer} force_reclaim",
                    $"妳的 lease 過期且未展期 → {claimer} 接手\nreason: {reason}\n若要繼續做請先跟 {claimer} 協調 (task_state {taskId} 看 timeline)");
                notifications++;
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_force_reclaim idempotent skip\n\n- task_id: `{taskId}`\n- key: `{idempotencyKey}`\n"
                : $"# ✅ task_force_reclaim\n\n- task_id: `{taskId}`\n- new_owner: {claimer}\n- previous_owner: {previousOwner ?? "-"}\n- reason: {reason}\n- new_lease_until: {leaseUntil}\n- inbox 通知: {notifications}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_force_reclaim {roomId}/{taskId}: {previousOwner} → {claimer} (reason={reason}, seq={seq})");
        }

        // ===========================================================
        // op=task_review_request — owner 提交審查；status: in_progress → review
        // ===========================================================
        void Op_TaskReviewRequest(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string reviewer = GetArg(args, "reviewer", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_review_request 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_review_request 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_review_request 缺少 actor"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.owner != actor) { RejectLastOp($"actor={actor} 不是 task {taskId} 的 owner ({st.owner})"); return; }
            if (st.status != "in_progress" && st.status != "claimed") { RejectLastOp($"task {taskId} 狀態 {st.status} 不能 request review（須在 claimed/in_progress）"); return; }

            var data = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(reviewer)) data["reviewer"] = reviewer;

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_review_request",
                task_id = taskId,
                data = data,
            });

            // 通知 reviewer (若指定)
            int notifications = 0;
            if (seq > 0 && !string.IsNullOrEmpty(reviewer))
            {
                UCL_ChatTavernQuestIO.AppendInbox(roomId, reviewer, seq,
                    $"{taskId} review request",
                    $"owner {actor} 提交審查\nsuggested_action: task_state {taskId} 後 task_done 或 task_reject reason=...");
                notifications++;
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_review_request idempotent skip\n\n- task_id: `{taskId}`\n"
                : $"# ✅ task_review_request\n\n- task_id: `{taskId}`\n- by: {actor}\n- reviewer: {reviewer ?? "(未指定)"}\n- inbox 通知: {notifications}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_review_request {roomId}/{taskId} → reviewer={reviewer} (seq={seq})");
        }

        // ===========================================================
        // op=task_reject — reviewer 退回；reject_count++; status: review → in_progress；通知 owner
        // ===========================================================
        void Op_TaskReject(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string reason = GetArg(args, "reason", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_reject 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_reject 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_reject 缺少 actor"); return; }
            if (string.IsNullOrEmpty(reason)) { RejectLastOp("task_reject 缺少 reason（必填，給 owner 修正方向）"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.status != "review") { RejectLastOp($"task {taskId} 狀態 {st.status} 不能 reject（須在 review）"); return; }

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_reject",
                task_id = taskId,
                data = new Dictionary<string, string> { { "reason", reason } },
            });

            // 通知原 owner
            int notifications = 0;
            if (seq > 0 && !string.IsNullOrEmpty(st.owner))
            {
                UCL_ChatTavernQuestIO.AppendInbox(roomId, st.owner, seq,
                    $"{taskId} rejected by {actor} (round {st.reject_count + 1})",
                    $"reason: {reason}\nsuggested_action: 修完用 task_progress / task_review_request 再次提交");
                notifications++;
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_reject idempotent skip\n\n- task_id: `{taskId}`\n"
                : $"# ✅ task_reject\n\n- task_id: `{taskId}`\n- by: {actor}\n- reason: {reason}\n- reject_count → {st.reject_count + 1}\n- inbox 通知: {notifications}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_reject {roomId}/{taskId} reason={reason} (seq={seq})");
        }

        // ===========================================================
        // op=task_reopen — done task 被發現有問題；status: done → in_progress（MVP 友善捷徑）
        // ===========================================================
        void Op_TaskReopen(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            string actor = GetArg(args, "actor", GetAgentArg(args));
            string reason = GetArg(args, "reason", "");
            string idempotencyKey = GetArg(args, "idempotency_key", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_reopen 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_reopen 缺少 task_id"); return; }
            if (string.IsNullOrEmpty(actor)) { RejectLastOp("task_reopen 缺少 actor"); return; }
            if (string.IsNullOrEmpty(reason)) { RejectLastOp("task_reopen 缺少 reason（必填，說明為何重開）"); return; }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }
            if (st.status != "done") { RejectLastOp($"task {taskId} 狀態 {st.status} 不是 done，無需 reopen"); return; }

            int seq = UCL_ChatTavernQuestIO.AppendEvent(roomId, new UCL_QuestEvent
            {
                actor = actor,
                idempotency_key = idempotencyKey,
                type = "task_reopen",
                task_id = taskId,
                data = new Dictionary<string, string> { { "reason", reason } },
            });

            // 通知 task owner（若不是 actor 自己）
            int notifications = 0;
            if (seq > 0 && !string.IsNullOrEmpty(st.owner) && st.owner != actor)
            {
                UCL_ChatTavernQuestIO.AppendInbox(roomId, st.owner, seq,
                    $"{taskId} reopened by {actor}",
                    $"reason: {reason}\nsuggested_action: task_state {taskId} 看 timeline 後繼續修 → task_progress / task_done");
                notifications++;
            }

            if (seq > 0) UCL_ChatTavernQuestIO.RebuildSnapshots(roomId);

            string md = seq < 0
                ? $"# ℹ task_reopen idempotent skip\n\n- task_id: `{taskId}`\n"
                : $"# ✅ task_reopen\n\n- task_id: `{taskId}`\n- by: {actor}\n- reason: {reason}\n- owner: {st.owner ?? "-"} (沿用)\n- inbox 通知: {notifications}\n- event_seq: {seq}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] task_reopen {roomId}/{taskId} reason={reason} (seq={seq})");
        }

        // ===========================================================
        // op=task_next — 自動排序回該 agent 應該接的下個 task（priority+age+downstream+role）
        // ===========================================================
        void Op_TaskNext(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string agentId = GetAgentArg(args, GetArg(args, "id", ""));
            int top = ParseIntArg(args, "top", 1);
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_next 缺少 room"); return; }
            if (string.IsNullOrEmpty(agentId)) { RejectLastOp("task_next 缺少 agent_id"); return; }

            // T19 — Stale lease 自動回收（lazy 偵測 + auto-release，不擋當前 op）
            try { UCL_ChatTavernQuestIO.AutoRecoverStaleLeases(roomId); }
            catch (Exception ex) { Debug.LogWarning($"[Quest T19] AutoRecoverStaleLeases 失敗（容忍）：{ex.Message}"); }

            // 抓 agent 的 tags（識別 role）
            var ident = UCL_ChatTavernIO.LoadIdentities().identities.Find(x => x.id == agentId);
            // identity 的 tags 由 UCL_ChatTavernIdentityAsset 持久化；輕量 identities.json 不存 tags
            // MVP: 暫時走 suggested_owner 命中加分為主，role-match 留 Phase B
            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            var candidates = new List<UCL_QuestTaskState>();
            foreach (var st in states.Values)
            {
                if (st.status != "pending" || !UCL_ChatTavernQuestIO.IsReady(st, states)) continue;
                candidates.Add(st);
            }
            // 排序：優先度 + 老化 → suggested_owner 命中 → downstream_weight → created_seq asc
            candidates.Sort((a, b) =>
            {
                int sa = UCL_ChatTavernQuestIO.PriorityScore(a);
                int sb = UCL_ChatTavernQuestIO.PriorityScore(b);
                if (sa != sb) return sb - sa;
                int suggA = (a.suggested_owner == agentId) ? 1 : 0;
                int suggB = (b.suggested_owner == agentId) ? 1 : 0;
                if (suggA != suggB) return suggB - suggA;
                if (a.downstream_weight != b.downstream_weight) return b.downstream_weight - a.downstream_weight;
                return a.created_seq - b.created_seq;
            });
            if (top < 1) top = 1;
            if (candidates.Count > top) candidates = candidates.GetRange(0, top);

            var sb2 = new System.Text.StringBuilder();
            sb2.AppendLine($"# 🎯 task_next — {agentId}");
            sb2.AppendLine();
            sb2.AppendLine($"room: `{roomId}` / top={top}");
            sb2.AppendLine();
            if (candidates.Count == 0)
            {
                sb2.AppendLine("_(無可接任務 — 全部 done / blocked / claimed by 別人)_");
            }
            else
            {
                sb2.AppendLine("| Rank | Task | Priority | Age | DownW | Suggested-match | Title |");
                sb2.AppendLine("|---|---|---|---|---|---|---|");
                for (int i = 0; i < candidates.Count; i++)
                {
                    var st = candidates[i];
                    string match = st.suggested_owner == agentId ? "✓" : "-";
                    sb2.AppendLine($"| {i + 1} | `{st.id}` | {st.priority}+{st.age_factor} | {st.age_days:F1}d | {st.downstream_weight} | {match} | {st.title} |");
                }
                sb2.AppendLine();
                sb2.AppendLine($"建議下一步：`task_claim task_id={candidates[0].id} claimer={agentId}`");
            }
            UCL_ChatTavernRender.WriteLastOp(sb2.ToString());
            Debug.Log($"[Quest] task_next {roomId}/{agentId} → {candidates.Count} candidate(s)");
        }

        // ===========================================================
        // op=task_state — 印 task lifecycle timeline（接手者必看）
        // ===========================================================
        void Op_TaskState(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string taskId = GetArg(args, "task_id", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_state 缺少 room"); return; }
            if (string.IsNullOrEmpty(taskId)) { RejectLastOp("task_state 缺少 task_id"); return; }

            // T19 — Stale lease auto-recover lazy 跑（在 ComputeTaskStates 前）
            try { UCL_ChatTavernQuestIO.AutoRecoverStaleLeases(roomId); }
            catch (Exception ex) { Debug.LogWarning($"[Quest T19] AutoRecoverStaleLeases 失敗（容忍）：{ex.Message}"); }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            if (!states.TryGetValue(taskId, out var st)) { RejectLastOp($"task 不存在：{taskId}"); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# 📜 task_state — `{taskId}`");
            sb.AppendLine();
            string effStatus = st.status == "pending" && UCL_ChatTavernQuestIO.IsReady(st, states) ? "ready" : st.status;
            sb.AppendLine($"- title: **{st.title}**");
            sb.AppendLine($"- status: **{effStatus}**" + (st.is_stale ? " ⚠ STALE" : ""));
            sb.AppendLine($"- owner: {st.owner ?? "-"}");
            sb.AppendLine($"- role: {st.role ?? "-"} | priority: {st.priority} (+age {st.age_factor})");
            sb.AppendLine($"- depends_on: {(st.depends_on.Count > 0 ? string.Join(", ", st.depends_on) : "-")}");
            sb.AppendLine($"- downstream_weight (阻擋 N 個下游): **{st.downstream_weight}**");
            sb.AppendLine($"- age: {st.age_days:F1} days (created: {st.created_at})");
            sb.AppendLine($"- lease_until: {st.lease_until ?? "-"}");
            if (st.reject_count > 0) sb.AppendLine($"- reject_count: **{st.reject_count}** (被退回 {st.reject_count} 次)");
            sb.AppendLine();
            sb.AppendLine("## Lifecycle Timeline");
            sb.AppendLine();
            foreach (var ev in st.lifecycle)
            {
                string detail = "";
                if (ev.data != null && ev.data.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in ev.data) parts.Add($"{kv.Key}={kv.Value}");
                    detail = " — " + string.Join(", ", parts);
                }
                sb.AppendLine($"- **seq={ev.seq}** [{ev.ts}] `{ev.type}` by {ev.actor}{detail}");
            }
            sb.AppendLine();
            sb.AppendLine($"_spec: tasks/{taskId}.md_");
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Quest] task_state {roomId}/{taskId} → {st.lifecycle.Count} events");
        }

        void Op_TaskList(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string ownerFilter = GetArg(args, "owner", "");
            string roleFilter = GetArg(args, "role", "");
            string statusFilterCsv = GetArg(args, "status", "");
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("task_list 缺少 room"); return; }

            // T19 — Stale lease auto-recover lazy 跑（task_list 是最常被呼叫的查詢，掛這裡覆蓋面廣）
            try { UCL_ChatTavernQuestIO.AutoRecoverStaleLeases(roomId); }
            catch (Exception ex) { Debug.LogWarning($"[Quest T19] AutoRecoverStaleLeases 失敗（容忍）：{ex.Message}"); }

            var statusFilters = new HashSet<string>();
            if (!string.IsNullOrEmpty(statusFilterCsv))
            {
                foreach (var s in statusFilterCsv.Split(',')) statusFilters.Add(s.Trim());
            }

            var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
            var sb = new StringBuilder();
            sb.AppendLine("# 📋 Task List");
            sb.AppendLine();
            sb.AppendLine($"room: `{roomId}` / total tasks: {states.Count}");
            sb.AppendLine();
            sb.AppendLine("| Task | Status | Owner | Role | Deps | Last Progress |");
            sb.AppendLine("|---|---|---|---|---|---|");

            int matched = 0;
            foreach (var kv in states)
            {
                var st = kv.Value;
                // 計算 effective status（pending + deps done → ready）
                string effectiveStatus = st.status;
                if (st.status == "pending" && UCL_ChatTavernQuestIO.IsReady(st, states)) effectiveStatus = "ready";

                // status filter — 「stale」是 orthogonal flag（is_stale），跟 effectiveStatus 並行匹配
                // 物理意義：stale task 可同時是 claimed / in_progress / review，不會吃掉原 status
                // 數值影響：filter 寫 status=stale 時，is_stale=true 的 task 都算命中（不論底下 status）
                if (statusFilters.Count > 0)
                {
                    bool match = statusFilters.Contains(effectiveStatus);
                    if (!match && statusFilters.Contains("stale") && st.is_stale) match = true;
                    if (!match) continue;
                }
                if (!string.IsNullOrEmpty(ownerFilter) && st.owner != ownerFilter) continue;
                if (!string.IsNullOrEmpty(roleFilter) && st.role != roleFilter) continue;

                string deps = st.depends_on != null && st.depends_on.Count > 0 ? string.Join(",", st.depends_on) : "-";
                string lastProg = string.IsNullOrEmpty(st.last_progress_summary) ? "-" : Truncate(st.last_progress_summary, 40);
                string statusCell = effectiveStatus + (st.is_stale ? " ⚠STALE" : "");
                sb.AppendLine($"| `{st.id}` | {statusCell} | {st.owner ?? "-"} | {st.role ?? "-"} | {deps} | {lastProg} |");
                matched++;
            }

            if (matched == 0) sb.AppendLine("| _(無符合 filter 的 task)_ | | | | | |");
            sb.AppendLine();
            sb.AppendLine($"_filter: owner={ownerFilter}, role={roleFilter}, status={statusFilterCsv}_");

            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Quest] task_list {roomId} → matched={matched}/{states.Count}");
        }

        void Op_InboxRead(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string agentId = GetAgentArg(args, GetArg(args, "id", ""));
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("inbox_read 缺少 room"); return; }
            if (string.IsNullOrEmpty(agentId)) { RejectLastOp("inbox_read 缺少 agent_id（可用 agent_id / id / sender）"); return; }

            string inbox = UCL_ChatTavernQuestIO.ReadInbox(roomId, agentId);
            string md = $"# 📬 Inbox — {agentId}\n\nroom: `{roomId}`\n\n{inbox}";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Quest] inbox_read {roomId}/{agentId}");
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
            if (string.IsNullOrEmpty(roomId)) { RejectLastOp("events_since 缺少 room"); return; }

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

            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Quest] events_since {roomId} since={sinceSeq} → {totalAfter} events" + (truncated ? $" (truncated to {limit})" : ""));
        }

        // ===========================================================
        // 區塊職責：op=set_presence — 設定在線狀態
        // 物理意義：手動宣告某個 agent/human 的狀態，例如下線休息 (offline)、忙碌中 (busy)。
        // ===========================================================
        void Op_SetPresence(Dictionary<string, string> args)
        {
            string senderId = GetArg(args, "id", GetAgentArg(args));
            string status = GetArg(args, "status", "");
            if (string.IsNullOrEmpty(senderId)) { RejectLastOp("set_presence 缺少 agent (別名 agent_id / sender / sender_id / id)"); return; }
            if (string.IsNullOrEmpty(status)) { RejectLastOp("set_presence 缺少 status (active / busy / idle / offline)"); return; }

            // 區塊職責：room / focus / mood 三個選填欄位一併寫入
            // 物理意義：文件（Cmd_Tavern.md §2.3）一直把這三個列為常用選填，但舊實作只讀 id+status
            //          → 帶了也**靜默不生效**。summit QA 2026-07-31 抓到：「文件說有而實作沒有，
            //          比沒寫更危險」。完整版 SetPresence 本來就吃這三個參數（R7 T07），是這裡沒接。
            // 數值影響：傳 null 的欄位在 SetPresence 內不覆寫既有值（focus 更新不會清掉 mood，反之亦然），
            //          所以只帶 status 的舊呼叫行為完全不變。
            string room = GetArg(args, "room", null);
            string focus = GetArg(args, "focus", GetArg(args, "current_focus", null));
            string mood = GetArg(args, "mood", null);
            UCL_ChatTavernIO.SetPresence(senderId, status, room, focus, mood);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# ✅ set_presence");
            sb.AppendLine();
            sb.AppendLine($"- agent: `{senderId}`");
            sb.AppendLine($"- status: `{status}`");
            if (!string.IsNullOrEmpty(room)) sb.AppendLine($"- room: `{room}`");
            if (!string.IsNullOrEmpty(focus)) sb.AppendLine($"- focus: `{focus}`");
            if (!string.IsNullOrEmpty(mood)) sb.AppendLine($"- mood: `{mood}`");
            sb.AppendLine($"- updated_at: `{UCL_ChatTavernIO.NowUtcIso()}`");

            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] set_presence {senderId} to {status}");
        }

        // ===========================================================
        // T20 — op=set_focus / op=set_mood（per Antigravity P6 / Tim Round 30）
        // 物理意義：對話中途精細更新 focus / mood，不必 raw write json 也不必重跑 session_enter
        // 數值影響：呼叫 SetPresence(id, active, null, focus, null) / (id, active, null, null, mood)
        //          status 自動推進 active（順手刷 last_active）；不動其他欄位
        // ===========================================================
        void Op_SetFocus(Dictionary<string, string> args)
        {
            string senderId = GetAgentArg(args, GetArg(args, "id", ""));
            string focus = GetArg(args, "focus", "");
            if (string.IsNullOrEmpty(senderId)) { RejectLastOp("set_focus 缺少 agent_id (或 id / sender / sender_id)"); return; }

            UCL_ChatTavernIO.SetPresence(senderId, "active", null, focus, null);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 🎯 set_focus");
            sb.AppendLine();
            sb.AppendLine($"- agent_id: `{senderId}`");
            sb.AppendLine($"- focus: `{focus}`");
            sb.AppendLine($"- status: `active` (auto-advanced)");
            sb.AppendLine($"- updated_at: `{UCL_ChatTavernIO.NowUtcIso()}`");
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] set_focus {senderId} → {focus}");
        }

        void Op_SetMood(Dictionary<string, string> args)
        {
            string senderId = GetAgentArg(args, GetArg(args, "id", ""));
            string mood = GetArg(args, "mood", "");
            if (string.IsNullOrEmpty(senderId)) { RejectLastOp("set_mood 缺少 agent_id (或 id / sender / sender_id)"); return; }

            UCL_ChatTavernIO.SetPresence(senderId, "active", null, null, mood);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 🎭 set_mood");
            sb.AppendLine();
            sb.AppendLine($"- agent_id: `{senderId}`");
            sb.AppendLine($"- mood: `{mood}`");
            sb.AppendLine($"- status: `active` (auto-advanced)");
            sb.AppendLine($"- updated_at: `{UCL_ChatTavernIO.NowUtcIso()}`");
            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] set_mood {senderId} → {mood}");
        }

        // ===========================================================
        // 區塊職責：op=get_presence — 查詢在線狀態
        // 物理意義：查詢指定角色或所有人的在線狀態與最後活躍時間。
        // ===========================================================
        void Op_GetPresence(Dictionary<string, string> args)
        {
            string targetId = GetArg(args, "id", GetArg(args, "target", GetArg(args, "target_id", "")));

            var presenceList = UCL_ChatTavernIO.LoadPresence();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 🟢 Presence 在線狀態列表");
            sb.AppendLine();
            sb.AppendLine("| 角色 ID | 狀態 | 最後活躍時間 (UTC) |");
            sb.AppendLine("| --- | --- | --- |");

            if (!string.IsNullOrEmpty(targetId))
            {
                var found = presenceList.presences.Find(x => x.sender_id == targetId);
                if (found != null)
                {
                    sb.AppendLine($"| `{found.sender_id}` | `{found.status}` | `{found.last_active}` |");
                }
                else
                {
                    sb.AppendLine($"| `{targetId}` | `offline` (未記錄) | - |");
                }
            }
            else
            {
                foreach (var p in presenceList.presences)
                {
                    sb.AppendLine($"| `{p.sender_id}` | `{p.status}` | `{p.last_active}` |");
                }
            }

            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] get_presence completed (target: {targetId ?? "all"})");
        }

        // ===========================================================
        // 區塊：op=session_enter — Antigravity / Gemini / Claude 入場 macro
        // 區塊職責：1 條 op 一次完成入場 4 件事 — inbox_read + dashboard read + presence set + room tail
        // 物理意義：解 latency S1+S2+S3+S5（quest tavern-entry-latency T04 / O3）
        //          舊路徑：5~6 op + 5~30s polling 才到工作狀態
        //          新路徑：1 op + < 5s polling，強制走 inbox-first SOP（不會跳過 inbox 直接 post）
        // 數值影響：寫 _last_op.md 一份合併 markdown（4 個 sub-section）；presence.json 推進 last_active + 可選 focus/mood
        // 參數：
        //   agent_id (req) — 自家 agent id（如 claude-da-xiaojie / antigravity-da-xiaojie / gemini-da-xiaojie）
        //   room     (opt) — 順手 tail-read 此房最後 N 筆（預設不讀，agent 看 inbox 後再決定）
        //   tail     (opt, default=10) — room 帶時 tail 幾筆
        //   focus    (opt) — set_presence 同步推進 current_focus
        //   mood     (opt) — set_presence 同步推進 mood
        //   inbox_room (opt) — inbox 在哪 room 找（預設沿用 room；若沒帶 room 則 fallback "tavern"）
        // ===========================================================
        void Op_SessionEnter(Dictionary<string, string> args)
        {
            string agentId = GetAgentArg(args, GetArg(args, "id", ""));
            if (string.IsNullOrEmpty(agentId)) { RejectLastOp("session_enter 缺少 agent_id"); return; }

            string roomId = GetArg(args, "room", "");
            string inboxRoom = GetArg(args, "inbox_room", string.IsNullOrEmpty(roomId) ? "tavern" : roomId);
            int tail = ParseIntArg(args, "tail", 10);
            string focus = GetArg(args, "focus", null);
            string mood = GetArg(args, "mood", null);
            // T23 — 加 --arg next=true 自動印「下個該接的 task」(從 quest 房挑)
            string nextFlag = GetArg(args, "next", "false");
            bool wantNext = !string.IsNullOrEmpty(nextFlag) && nextFlag.ToLowerInvariant() == "true";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# 🚪 session_enter — `{agentId}`");
            sb.AppendLine();
            sb.AppendLine($"_macro op：1 條取代 inbox_read + get_presence + set_presence + read（latency 優化 quest T04）_");
            sb.AppendLine();

            // ── (1) inbox_read：強制走 inbox-first SOP（解 R4 認知落後）──
            sb.AppendLine($"## 📬 Inbox @ `{inboxRoom}`");
            sb.AppendLine();
            try
            {
                string inbox = UCL_ChatTavernQuestIO.ReadInbox(inboxRoom, agentId);
                sb.AppendLine(string.IsNullOrEmpty(inbox) ? "_(inbox 空 — 沒有新 mention / 待辦 / thread-summary)_" : inbox);
            }
            catch (Exception e)
            {
                sb.AppendLine($"_(inbox 讀取失敗：{e.Message})_");
            }
            sb.AppendLine();

            // ── (2) presence dashboard：tavern-keeper.current_focus 一行（解 R1 沒做 presence 預檢）──
            sb.AppendLine("## 🟢 Presence Dashboard");
            sb.AppendLine();
            try
            {
                var presenceList = UCL_ChatTavernIO.LoadPresence();
                var keeper = presenceList.presences.Find(x => x.sender_id == "tavern-keeper");
                if (keeper != null && !string.IsNullOrEmpty(keeper.current_focus))
                {
                    sb.AppendLine(keeper.current_focus);
                }
                else
                {
                    sb.AppendLine("_(dashboard 尚未生成 — SetPresence hook 應自動重建；可手動 set_presence 觸發)_");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"_(presence 讀取失敗：{e.Message})_");
            }
            sb.AppendLine();

            // ── (3) set_presence：active + 可選 room/focus/mood 一次推進 ──
            sb.AppendLine("## ✅ Presence 推進");
            sb.AppendLine();
            try
            {
                UCL_ChatTavernIO.SetPresence(agentId, "active", string.IsNullOrEmpty(roomId) ? null : roomId, focus, mood);
                sb.AppendLine($"- status: `active`");
                if (!string.IsNullOrEmpty(roomId)) sb.AppendLine($"- current_room: `{roomId}`");
                if (!string.IsNullOrEmpty(focus)) sb.AppendLine($"- current_focus: `{focus}`");
                if (!string.IsNullOrEmpty(mood)) sb.AppendLine($"- mood: `{mood}`");
                sb.AppendLine($"- updated_at: `{UCL_ChatTavernIO.NowUtcIso()}`");
            }
            catch (Exception e)
            {
                sb.AppendLine($"_(set_presence 失敗：{e.Message})_");
            }
            sb.AppendLine();

            // ── (4) room tail：可選 — agent 看完 inbox 再決定要不要爬 ──
            if (!string.IsNullOrEmpty(roomId))
            {
                sb.AppendLine($"## 🍺 Room Tail — `{roomId}` 最新 {tail} 筆");
                sb.AppendLine();
                try
                {
                    var room = UCL_ChatTavernIO.GetRoom(roomId);
                    if (room == null)
                    {
                        sb.AppendLine($"_(房間不存在：{roomId})_");
                    }
                    else
                    {
                        var messages = UCL_ChatTavernIO.Tail(roomId, tail);
                        string md = UCL_ChatTavernRender.RenderMessages($"{room.name} — 最新 {messages.Count} 筆", messages);
                        sb.AppendLine(md);
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine($"_(read 失敗：{e.Message})_");
                }
            }
            else
            {
                sb.AppendLine("_(無 --arg room 不爬 messages — 推薦先看 inbox 摘要再決定要不要 read 全文)_");
            }

            // ── (5) [T23] next task 推薦（可選，--arg next=true）──
            // 物理意義：「入場即定位」極速工作流（per Antigravity C 補強）— agent 看 inbox 完直接知道接哪 task
            // 數值影響：純讀（task_next 邏輯複用，但只印 top 1 + spec 摘要）；不寫 events.jsonl
            if (wantNext && !string.IsNullOrEmpty(roomId))
            {
                sb.AppendLine();
                sb.AppendLine($"## 📋 Next Task @ `{roomId}`");
                sb.AppendLine();
                try
                {
                    // lazy stale recovery (T19) 先跑
                    UCL_ChatTavernQuestIO.AutoRecoverStaleLeases(roomId);
                    var states = UCL_ChatTavernQuestIO.ComputeTaskStates(roomId);
                    var candidates = new List<UCL_QuestTaskState>();
                    foreach (var st in states.Values)
                    {
                        if (st.status != "pending" || !UCL_ChatTavernQuestIO.IsReady(st, states)) continue;
                        candidates.Add(st);
                    }
                    if (candidates.Count == 0)
                    {
                        sb.AppendLine("_(無 ready pending task — 該房 task tree 都跑完了 / 或全在等依賴)_");
                    }
                    else
                    {
                        // 排序：priority + suggested_owner 命中 + downstream_weight + 老化 + created_seq
                        candidates.Sort((a, b) =>
                        {
                            int sa = UCL_ChatTavernQuestIO.PriorityScore(a);
                            int sb_ = UCL_ChatTavernQuestIO.PriorityScore(b);
                            if (sa != sb_) return sb_ - sa;
                            int oa = (a.suggested_owner == agentId) ? 1 : 0;
                            int ob = (b.suggested_owner == agentId) ? 1 : 0;
                            if (oa != ob) return ob - oa;
                            if (a.downstream_weight != b.downstream_weight) return b.downstream_weight - a.downstream_weight;
                            return a.created_seq - b.created_seq;
                        });
                        var top = candidates[0];
                        sb.AppendLine($"**推薦下個 task**：`{top.id}`");
                        sb.AppendLine($"- title: {top.title}");
                        sb.AppendLine($"- priority: `{top.priority}` / role: `{top.role}` / suggested_owner: `{top.suggested_owner ?? "-"}`");
                        if (top.depends_on != null && top.depends_on.Count > 0)
                            sb.AppendLine($"- depends_on: {string.Join(", ", top.depends_on)} (all done)");
                        sb.AppendLine();
                        sb.AppendLine($"💡 動工命令：`run Tavern op=task_claim room={roomId} task_id={top.id} claimer={agentId} plan=\"...\"`");
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine($"_(task_next 失敗：{e.Message})_");
                }
            }
            else if (wantNext)
            {
                sb.AppendLine();
                sb.AppendLine("_( --arg next=true 但沒帶 --arg room — 不知道從哪個 quest 房挑 task)_");
            }

            UCL_ChatTavernRender.WriteLastOp(sb.ToString());
            Debug.Log($"[Tavern] session_enter {agentId}" + (string.IsNullOrEmpty(roomId) ? "" : $" room={roomId}") + (wantNext ? " +next" : ""));
        }

        // 真錯誤：寫盤失敗 / null ref / unhandled exception → 紅 ❗ + LogError
        static void FailLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ❌ Tavern Cmd Failed\n\n{msg}\n");
            Debug.LogError($"[Tavern] {msg}");
            throw new InvalidOperationException(msg);
        }

        // 預期拒絕：缺 arg / 房間不存在 / owner mismatch / lease 衝突 / status 不對 → 黃 ⚠ + LogWarning
        // throw 行為跟 FailLastOp 一致（cmd queue 端仍視為失敗）；只是 console 顏色降級避免污染 signal-to-noise
        // 詳見 docs/Snapshots/ErrorLog_Analysis_2026-05-09.md (T11 報告)
        static void RejectLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ Tavern Cmd Rejected\n\n{msg}\n");
            Debug.LogWarning($"[Tavern] {msg}");
            throw new InvalidOperationException(msg);
        }
    }
}
#endif
