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
            "wait: room=房間ID since_seq=N [timeout=300（秒，預設 5 分鐘）]\n" +
            "note_write: room=房間ID key=筆記key body=內容（v2 預留）\n" +
            "note_read: room=房間ID key=筆記key（v2 預留）";
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
                    case "wait": await Op_Wait(args, token); break;
                    case "note_write":
                    case "note_read":
                        FailLastOp($"op={op} 為 v2 規劃，prototype 階段尚未實作。");
                        break;
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
        // 區塊：op=wait — polling 直到 since_seq 之後出現新訊息或 timeout
        // 物理意義：解鎖「我問你答」turn-based 協作模式
        // 數值影響：每秒 poll 一次 _seq.txt，命中即回；timeout 後寫 timeout 標記
        //          預設 timeout=300 秒（5 分鐘）— 對應「對方太久沒回就放棄」的場景；
        //          若 client 用 run_cmd.py --timeout 包裝，wrapper 端 timeout 應 ≥ 此值
        //          否則 client 會先放棄（cmd 仍會跑完）
        // ===========================================================
        const int DefaultWaitTimeoutSec = 300;
        async UniTask Op_Wait(Dictionary<string, string> args, CancellationToken token)
        {
            string roomId = GetArg(args, "room", "");
            int sinceSeq = ParseIntArg(args, "since_seq", 0);
            int timeoutSec = ParseIntArg(args, "timeout", DefaultWaitTimeoutSec);
            if (string.IsNullOrEmpty(roomId)) { FailLastOp("wait 缺少 room"); return; }
            var room = UCL_ChatTavernIO.GetRoom(roomId);
            if (room == null) { FailLastOp($"房間不存在：{roomId}"); return; }

            const int pollIntervalMs = 1000;
            int waitedMs = 0;
            int totalMs = timeoutSec * 1000;

            while (waitedMs <= totalMs)
            {
                token.ThrowIfCancellationRequested();
                int cur = UCL_ChatTavernIO.ReadCurrentSeq(roomId);
                if (cur > sinceSeq)
                {
                    var newMsgs = UCL_ChatTavernIO.Since(roomId, sinceSeq, 0);
                    string title = $"🔔 {room.name} — 新訊息 since_seq={sinceSeq}（{newMsgs.Count} 筆，等了 {waitedMs / 1000}s）";
                    string md = UCL_ChatTavernRender.RenderMessages(title, newMsgs);
                    UCL_ChatTavernRender.WriteLastOp(md);
                    Debug.Log($"[Tavern] wait {roomId} → got {newMsgs.Count} after {waitedMs}ms");
                    return;
                }
                await UniTask.Delay(pollIntervalMs, cancellationToken: token);
                waitedMs += pollIntervalMs;
            }
            // timeout
            string timeoutMd = $"# ⏱ Timeout\n\n房間 `{roomId}` 等待 {timeoutSec}s 後仍無 seq > {sinceSeq} 的新訊息。\n";
            UCL_ChatTavernRender.WriteLastOp(timeoutMd);
            Debug.LogWarning($"[Tavern] wait {roomId} → timeout after {timeoutSec}s");
        }

        // ===========================================================
        // helper
        // ===========================================================

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
