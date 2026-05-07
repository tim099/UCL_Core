// UCL Chat Tavern — 整合型 Cmd（prototype v1）
// 單一 handler，用 args["op"] 分派到內部子操作。
// 設計取捨：所有酒館操作走同一個 CommandType="Tavern"，避免 registry 暴增 8~10 個 Cmd。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
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
            "op=createroom|listrooms|join|post|read|members|leave|wait|note_write|note_read\n" +
            "createroom: id=房間ID name=顯示名 description=描述\n" +
            "listrooms: (無參數)\n" +
            "join: room=房間ID id=身分ID name=顯示名 kind=agent|human|system\n" +
            "post: room=房間ID sender=身分ID body=訊息內容 [reply_to=seq] [meta=k1:v1;k2:v2] [refs=path1|path2]\n" +
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
            "note_delete: room=房間ID key=筆記key（刪檔）";
        // ExampleArgs：agent-neutral 範例 — 別硬塞 claude-* 讓非 Claude agent 誤以為是預設身分
        public override string ExampleArgs =>
            "op=post;room=demo;sender=<your-agent-id>;body=哼～來打個招呼";
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                FailLastOp("缺少 op 參數。請參考 ArgsSchema。");
                return;
            }
            try
            {
                switch (op)
                {
                    case "createroom": Op_CreateRoom(args); break;
                    case "listrooms": Op_ListRooms(); break;
                    case "join": Op_Join(args); break;
                    case "post": Op_Post(args); break;
                    case "read": Op_Read(args); break;
                    case "members": Op_Members(args); break;
                    case "leave": Op_Leave(args); break;
                    case "wait": Op_Wait(args, token); break;
                    case "wait_check": Op_WaitCheck(args); break;
                    case "note_write": Op_NoteWrite(args); break;
                    case "note_append": Op_NoteAppend(args); break;
                    case "note_read": Op_NoteRead(args); break;
                    case "note_list": Op_NoteList(args); break;
                    case "note_delete": Op_NoteDelete(args); break;
                    default:
                        FailLastOp($"未知 op：{op}");
                        break;
                }
            }
            catch (Exception ex)
            {
                FailLastOp($"執行 op={op} 失敗：{ex.Message}\n{ex.StackTrace}");
                throw;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 區塊：op=createroom
        // ===========================================================
        void Op_CreateRoom(Dictionary<string, string> args)
        {
            string id = GetArg(args, "id", "");
            string name = GetArg(args, "name", id);
            string desc = GetArg(args, "description", "");
            if (string.IsNullOrEmpty(id)) { FailLastOp("createroom 缺少 id"); return; }
            var room = UCL_ChatTavernIO.CreateRoom(id, name, desc);
            string md = $"# ✅ Room ready\n\n- id: `{room.id}`\n- name: {room.name}\n- description: {room.description}\n- created_at: {room.created_at}\n";
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] createroom → {room.id}");
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
            string identityId = GetArg(args, "id", "");
            string displayName = GetArg(args, "name", identityId);
            string kind = GetArg(args, "kind", "agent");
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("join 缺少 room"); return; }
            if (string.IsNullOrEmpty(identityId)) { FailLastOp("join 缺少 id"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}（請先 createroom）"); return; }

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

            var tail = UCL_ChatTavernIO.Tail(roomId, 100);
            string header = $"> 你以「{ident.display_name}」（id=`{ident.id}`）進入房間「{room.name}」(seq={seq})";
            string md = UCL_ChatTavernRender.WriteLastView(roomId, room.name, tail, seq, header);
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] join {roomId} ← {ident.display_name} (seq={seq})");
        }

        // ===========================================================
        // 區塊：op=post — 主功能。寫訊息 + 重渲染 _last_view.md（最新 100 筆）
        // 物理意義：這是 agent 與酒館互動的主要入口；output 直接給 agent 當下回合 prompt
        // ===========================================================
        void Op_Post(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            string senderId = GetArg(args, "sender", "");
            string body = GetArg(args, "body", "");
            string replyToStr = GetArg(args, "reply_to", "");
            string metaStr = GetArg(args, "meta", "");
            string refsStr = GetArg(args, "refs", "");
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("post 缺少 room"); return; }
            if (string.IsNullOrEmpty(senderId)) { FailLastOp("post 缺少 sender"); return; }
            if (string.IsNullOrEmpty(body)) { FailLastOp("post 缺少 body"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }

            // 從 identities.json 取顯示名稱（找不到 → 用 senderId 當顯示名，但記 warning）
            var ident = UCL_ChatTavernIO.LoadIdentities().identities.Find(x => x.id == senderId);
            string senderName = ident?.display_name ?? senderId;
            if (ident == null)
            {
                Debug.LogWarning($"[Tavern] post 的 sender '{senderId}' 不在 identities.json — 建議先 op=join 註冊");
            }

            var msg = new UCL_ChatMessage
            {
                sender_id = senderId,
                sender_name = senderName,
                kind = "chat",
                body = body,
                reply_to = int.TryParse(replyToStr, out var rt) ? rt : (int?)null,
                meta = ParseMeta(metaStr),
                refs = ParseRefs(refsStr),
            };
            int seq = UCL_ChatTavernIO.AppendMessage(roomId, msg);

            var tail = UCL_ChatTavernIO.Tail(roomId, 100);
            string header = $"> 你 ({senderName}) 剛 post：seq={seq} 「{Truncate(body, 80)}」";
            string md = UCL_ChatTavernRender.WriteLastView(roomId, room.name, tail, seq, header);
            UCL_ChatTavernRender.WriteLastOp(md);
            Debug.Log($"[Tavern] post → {roomId} seq={seq} by {senderName}");
        }

        // ===========================================================
        // 區塊：op=read — 切片查詢
        // ===========================================================
        void Op_Read(Dictionary<string, string> args)
        {
            string roomId = GetArg(args, "room", "");
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("read 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }

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
                messages = UCL_ChatTavernIO.Search(roomId, search, limit > 0 ? limit : 100);
                title = $"🔍 {room.name} — 搜尋 \"{search}\"（命中 {messages.Count}）";
            }
            else if (since >= 0)
            {
                messages = UCL_ChatTavernIO.Since(roomId, since, limit > 0 ? limit : 200);
                title = $"📥 {room.name} — since_seq={since}（{messages.Count} 筆）";
            }
            else if (from > 0 || to > 0)
            {
                messages = UCL_ChatTavernIO.Range(roomId, from, to > 0 ? to : int.MaxValue);
                title = $"📐 {room.name} — seq {from}..{(to > 0 ? to.ToString() : "end")}（{messages.Count} 筆）";
            }
            else
            {
                int n = tail > 0 ? tail : 100;
                messages = UCL_ChatTavernIO.Tail(roomId, n);
                title = $"🍺 {room.name} — 最新 {messages.Count} 筆";
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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("members 缺少 room"); return; }
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
            string senderId = GetArg(args, "sender", "");
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(senderId)) { FailLastOp("leave 需要 room + sender"); return; }
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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("wait 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }

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
            if (string.IsNullOrEmpty(waitId)) { FailLastOp("wait_check 缺少 wait_id"); return; }

            var w = UCL_ChatTavernIO.FindWait(waitId);
            if (w == null) { FailLastOp($"找不到 wait_id：{waitId}（可能已被 stale-purge 或從未存在）"); return; }

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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("note_write 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { FailLastOp("note_write 缺少 key"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }
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
            string sender = GetArg(args, "sender", null);
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("note_append 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { FailLastOp("note_append 缺少 key"); return; }
            if (string.IsNullOrEmpty(body)) { FailLastOp("note_append 缺少 body"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }
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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("note_read 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { FailLastOp("note_read 缺少 key"); return; }
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
            if (content == null) { FailLastOp($"note 不存在：{roomId}/{key}"); return; }
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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("note_list 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }
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
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("note_delete 缺少 room"); return; }
            if (string.IsNullOrEmpty(key)) { FailLastOp("note_delete 缺少 key"); return; }
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
            string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", ".."));
            string norm = absPath.Replace('\\', '/');
            string root = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (norm.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return norm.Substring(root.Length);
            return norm;
        }

        /// <summary>解析 "k1:v1;k2:v2" 為 dict。v 內可含 ':' 但第一個 ':' 之後都算 v。</summary>
        static Dictionary<string, string> ParseMeta(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
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

        static void FailLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ❌ Tavern Cmd Failed\n\n{msg}\n");
            Debug.LogError($"[Tavern] {msg}");
            throw new InvalidOperationException(msg);
        }
    }
}
#endif
