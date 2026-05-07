// UCL Chat Tavern — IO 層（prototype v1）
// 路徑配置 / 身分持久化 / 房間管理 / messages.jsonl 讀寫 / 序號管理。
// 設計取捨：
//   - 訊息採 jsonl append-only，每行一個自包含 JSON object，永不重寫
//   - 序號 _seq.txt 單調遞增（讀 → +1 → 寫 → 用），prototype 階段不做跨 process lock
//     （Editor handler 跑在 main thread，單一 Editor 內天然序列化）
//   - JSON 使用手寫 minimal serializer/parser，與 UCL_AgentCommandQueue 風格對齊
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Chat Tavern 的所有檔案 I/O。
    /// 根目錄：&lt;repoRoot&gt;/AgentCommands/ChatTavern/
    /// </summary>
    public static class UCL_ChatTavernIO
    {
        // 區塊職責：路徑常數 — 與 UCL_AgentCommandQueue 對齊，掛在 AgentCommands/ 下的子資料夾
        // 物理意義：repoRoot = Application.dataPath/../.. （Assets 上兩層）
        public const string TavernDirRelative = "AgentCommands/ChatTavern";
        public const string IdentitiesFile = "identities.json";
        public const string RoomsFile = "rooms.json";
        public const string LastOpFile = "_last_op.md";    // 最近一次 Cmd 結果（給 agent 抓）
        public const string ActiveWaitsFile = "_active_waits.json"; // fire-and-forget wait 全域追蹤
        public const string MessagesFile = "messages.jsonl";
        public const string SeqFile = "_seq.txt";
        public const string MembersFile = "members.json";
        public const string LastViewFile = "_last_view.md";

        // Stale wait 過期門檻：終態（fulfilled / timeout / cancelled）超過此時間 → purge
        const int StaleWaitMinutes = 30;

        // ===========================================================
        // 路徑 helper
        // ===========================================================

        public static string GetTavernDir()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            return Path.Combine(projectRoot, TavernDirRelative);
        }

        public static string GetIdentitiesPath() => Path.Combine(GetTavernDir(), IdentitiesFile);
        public static string GetRoomsPath() => Path.Combine(GetTavernDir(), RoomsFile);
        public static string GetLastOpPath() => Path.Combine(GetTavernDir(), LastOpFile);
        public static string GetActiveWaitsPath() => Path.Combine(GetTavernDir(), ActiveWaitsFile);
        public static string GetWaitResultPath(string waitId) => Path.Combine(GetTavernDir(), $"_wait_{waitId}.md");

        public static string GetRoomDir(string roomId) => Path.Combine(GetTavernDir(), "rooms", roomId);
        public static string GetMessagesPath(string roomId) => Path.Combine(GetRoomDir(roomId), MessagesFile);
        public static string GetSeqPath(string roomId) => Path.Combine(GetRoomDir(roomId), SeqFile);
        public static string GetMembersPath(string roomId) => Path.Combine(GetRoomDir(roomId), MembersFile);
        public static string GetLastViewPath(string roomId) => Path.Combine(GetRoomDir(roomId), LastViewFile);
        public static string GetNotesDir(string roomId) => Path.Combine(GetRoomDir(roomId), "notes");
        public static string GetNotePath(string roomId, string key) => Path.Combine(GetNotesDir(roomId), $"{key}.md");
        public static void EnsureNotesDir(string roomId)
        {
            string dir = GetNotesDir(roomId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static void EnsureTavernDir()
        {
            string dir = GetTavernDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        public static void EnsureRoomDir(string roomId)
        {
            string dir = GetRoomDir(roomId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static string NowUtcIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // ===========================================================
        // 區塊職責：身分（identities.json）— 全域共用，跨房間
        // 物理意義：穩定 id → display_name 的映射；agent 進酒館前必須 join，join 自動建身分
        // ===========================================================

        public static UCL_ChatIdentityList LoadIdentities()
        {
            string path = GetIdentitiesPath();
            if (!File.Exists(path)) return new UCL_ChatIdentityList();
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatIdentityList>(json);
                return data ?? new UCL_ChatIdentityList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load identities: {e}");
                return new UCL_ChatIdentityList();
            }
        }

        public static void SaveIdentities(UCL_ChatIdentityList list)
        {
            EnsureTavernDir();
            try
            {
                string json = JsonUtility.ToJson(list, true);
                File.WriteAllText(GetIdentitiesPath(), json, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to save identities: {e}");
            }
        }

        /// <summary>取得（或建立）身分。若 id 不存在，依 displayName + kind 建一筆並寫回。</summary>
        public static UCL_ChatIdentity GetOrCreateIdentity(string id, string displayName, string kind)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("identity id is required");
            var list = LoadIdentities();
            var found = list.identities.Find(x => x.id == id);
            string now = NowUtcIso();
            if (found != null)
            {
                // 已存在：更新 last_seen，display_name 維持原值（避免無預期改名）
                found.last_seen_at = now;
                SaveIdentities(list);
                return found;
            }
            var ident = new UCL_ChatIdentity
            {
                id = id,
                display_name = string.IsNullOrEmpty(displayName) ? id : displayName,
                kind = string.IsNullOrEmpty(kind) ? "agent" : kind,
                created_at = now,
                last_seen_at = now,
            };
            list.identities.Add(ident);
            SaveIdentities(list);
            return ident;
        }

        // ===========================================================
        // 區塊職責：房間（rooms.json）
        // ===========================================================

        public static UCL_ChatRoomList LoadRooms()
        {
            string path = GetRoomsPath();
            if (!File.Exists(path)) return new UCL_ChatRoomList();
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatRoomList>(json);
                return data ?? new UCL_ChatRoomList();
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load rooms: {e}");
                return new UCL_ChatRoomList();
            }
        }

        public static void SaveRooms(UCL_ChatRoomList list)
        {
            EnsureTavernDir();
            try
            {
                string json = JsonUtility.ToJson(list, true);
                File.WriteAllText(GetRoomsPath(), json, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to save rooms: {e}");
            }
        }

        public static UCL_ChatRoom CreateRoom(string id, string name, string description)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("room id is required");
            var list = LoadRooms();
            var found = list.rooms.Find(x => x.id == id);
            if (found != null) return found; // 冪等：已存在直接回傳
            var room = new UCL_ChatRoom
            {
                id = id,
                name = string.IsNullOrEmpty(name) ? id : name,
                description = description ?? "",
                created_at = NowUtcIso(),
            };
            list.rooms.Add(room);
            SaveRooms(list);
            EnsureRoomDir(id);
            return room;
        }

        public static UCL_ChatRoom GetRoom(string id)
        {
            return LoadRooms().rooms.Find(x => x.id == id);
        }

        // ===========================================================
        // 區塊職責：成員（members.json，per room）
        // ===========================================================

        public static UCL_ChatRoomMembers LoadMembers(string roomId)
        {
            string path = GetMembersPath(roomId);
            if (!File.Exists(path)) return new UCL_ChatRoomMembers { room_id = roomId };
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatRoomMembers>(json);
                return data ?? new UCL_ChatRoomMembers { room_id = roomId };
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load members: {e}");
                return new UCL_ChatRoomMembers { room_id = roomId };
            }
        }
        public static void SaveMembers(UCL_ChatRoomMembers m)
        {
            EnsureRoomDir(m.room_id);
            try
            {
                string json = JsonUtility.ToJson(m, true);
                File.WriteAllText(GetMembersPath(m.room_id), json, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to save members: {e}");
            }
        }

        public static void AddMember(string roomId, string identityId)
        {
            var m = LoadMembers(roomId);
            if (!m.member_ids.Contains(identityId)) m.member_ids.Add(identityId);
            SaveMembers(m);
        }
        public static void RemoveMember(string roomId, string identityId)
        {
            var m = LoadMembers(roomId);
            m.member_ids.Remove(identityId);
            SaveMembers(m);
        }

        // ===========================================================
        // 區塊職責：Notes — per-room 共享筆記，每個 note 為一個 .md 檔
        // 物理意義：notes/<key>.md 為 source-of-truth；frontmatter 4 欄（key/room/created_at/last_updated_at）
        //          write 整個覆寫（last-write-wins）；append 純文字追加（OS 原子性）— 不動 frontmatter
        // 數值影響：人類可直接 grep / 編輯 .md；agent 透過 ops 操作；走 [chat] 獨立 commit
        // ===========================================================

        static readonly System.Text.RegularExpressions.Regex s_NoteKeyRegex
            = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$");

        /// <summary>檢查 key 是否合法。違反 → throw 給 caller 處理。</summary>
        public static void ValidateNoteKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("note key 不能為空");
            if (!s_NoteKeyRegex.IsMatch(key))
                throw new ArgumentException($"note key '{key}' 不合法 — 僅接受 [a-zA-Z0-9_-]");
        }

        /// <summary>整個覆寫 note（write 模式）— frontmatter 重新生成、last_updated_at 更新到當下。</summary>
        public static void WriteNote(string roomId, string key, string body)
        {
            ValidateNoteKey(key);
            EnsureNotesDir(roomId);
            string path = GetNotePath(roomId, key);
            string createdAt;
            if (File.Exists(path))
            {
                createdAt = ExtractFrontmatterField(path, "created_at") ?? NowUtcIso();
            }
            else
            {
                createdAt = NowUtcIso();
            }
            string fm =
                "---\n" +
                $"key: {key}\n" +
                $"room: {roomId}\n" +
                $"created_at: {createdAt}\n" +
                $"last_updated_at: {NowUtcIso()}\n" +
                "---\n\n";
            File.WriteAllText(path, fm + (body ?? ""), new UTF8Encoding(false));
        }

        /// <summary>純文字 append 模式 — File.AppendAllText 利用 OS 原子性；不更新 frontmatter。
        /// body 前會自動加 "[@sender] " 行；若 note 不存在 → 自動以空 body 建立後再 append。</summary>
        public static void AppendNote(string roomId, string key, string body, string sender)
        {
            ValidateNoteKey(key);
            EnsureNotesDir(roomId);
            string path = GetNotePath(roomId, key);
            if (!File.Exists(path))
            {
                WriteNote(roomId, key, ""); // 先建立空 note 帶 frontmatter
            }
            string senderTag = string.IsNullOrEmpty(sender) ? "" : $"[@{sender}] ";
            string toAppend = "\n" + senderTag + (body ?? "") + "\n";
            File.AppendAllText(path, toAppend, new UTF8Encoding(false));
        }

        public static string ReadNote(string roomId, string key)
        {
            ValidateNoteKey(key);
            string path = GetNotePath(roomId, key);
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static List<string> ListNoteKeys(string roomId)
        {
            string dir = GetNotesDir(roomId);
            var result = new List<string>();
            if (!Directory.Exists(dir)) return result;
            foreach (var p in Directory.GetFiles(dir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(p);
                if (s_NoteKeyRegex.IsMatch(name)) result.Add(name);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static bool DeleteNote(string roomId, string key)
        {
            ValidateNoteKey(key);
            string path = GetNotePath(roomId, key);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        /// <summary>從 .md frontmatter 抓某個 key 的值（簡易解析；YAML 不依賴第三方）。失敗回 null。</summary>
        static string ExtractFrontmatterField(string path, string field)
        {
            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2 || lines[0].Trim() != "---") return null;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Trim() == "---") break;
                    int idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    string k = line.Substring(0, idx).Trim();
                    if (k == field) return line.Substring(idx + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        // ===========================================================
        // 區塊職責：active waits（_active_waits.json）— fire-and-forget wait 全域追蹤
        // 物理意義：op=wait 改為非阻塞模式後，每次發起寫一筆 pending 進來；背景 UniTask 監看
        //           messages.jsonl 並在命中 / timeout 時改 status；agent 用 op=wait_check 查狀態
        // 數值影響：每次讀檔自動 purge 終態超過 StaleWaitMinutes 的條目（避免檔案無限長大）
        // ===========================================================

        public static UCL_ChatActiveWaitList LoadActiveWaits()
        {
            string path = GetActiveWaitsPath();
            if (!File.Exists(path)) return new UCL_ChatActiveWaitList();
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatActiveWaitList>(json);
                if (data == null) data = new UCL_ChatActiveWaitList();
                // purge stale 終態條目
                PurgeStaleInPlace(data);
                return data;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load active waits: {e}");
                return new UCL_ChatActiveWaitList();
            }
        }

        public static void SaveActiveWaits(UCL_ChatActiveWaitList list)
        {
            EnsureTavernDir();
            try
            {
                string json = JsonUtility.ToJson(list, true);
                File.WriteAllText(GetActiveWaitsPath(), json, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to save active waits: {e}");
            }
        }

        /// <summary>建一筆 pending 條目並 append 到清單，回傳 wait_id。</summary>
        public static string CreatePendingWait(string roomId, int sinceSeq, int timeoutSec, string owner)
        {
            string waitId = NewWaitId();
            DateTime now = DateTime.UtcNow;
            var w = new UCL_ChatActiveWait
            {
                wait_id = waitId,
                room_id = roomId,
                since_seq = sinceSeq,
                timeout_sec = timeoutSec,
                started_at = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                expires_at = now.AddSeconds(timeoutSec).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                status = "pending",
                result_first_seq = 0,
                result_count = 0,
                finished_at = null,
                owner = owner,
            };
            var list = LoadActiveWaits();
            list.waits.Add(w);
            SaveActiveWaits(list);
            return waitId;
        }

        /// <summary>更新指定 wait_id 的 status / 結果欄位（fulfilled / timeout / cancelled）。</summary>
        public static void UpdateWaitStatus(string waitId, string status, int resultFirstSeq, int resultCount)
        {
            var list = LoadActiveWaits();
            var w = list.waits.Find(x => x.wait_id == waitId);
            if (w == null) return;
            w.status = status;
            w.result_first_seq = resultFirstSeq;
            w.result_count = resultCount;
            w.finished_at = NowUtcIso();
            SaveActiveWaits(list);
        }

        public static UCL_ChatActiveWait FindWait(string waitId)
        {
            return LoadActiveWaits().waits.Find(x => x.wait_id == waitId);
        }

        /// <summary>把清單內所有 status==pending 但已超過 expires_at 的條目改成 timeout（被 reload 之類事件孤兒化）。</summary>
        public static int FinalizeOrphanedPending()
        {
            var list = LoadActiveWaits();
            int n = 0;
            DateTime now = DateTime.UtcNow;
            foreach (var w in list.waits)
            {
                if (w.status != "pending") continue;
                if (DateTime.TryParse(w.expires_at, out var exp) && now > exp)
                {
                    w.status = "cancelled";
                    w.finished_at = NowUtcIso();
                    n++;
                }
            }
            if (n > 0) SaveActiveWaits(list);
            return n;
        }

        static string NewWaitId()
        {
            // 區塊職責：產生 unique wait_id — yyyyMMdd-HHmmss-NNN-tail6
            // 物理意義：tail6 為 GUID 前 6 字元，避免同秒衝突
            string ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string tail = Guid.NewGuid().ToString("N").Substring(0, 6);
            return $"{ts}-{tail}";
        }

        static void PurgeStaleInPlace(UCL_ChatActiveWaitList list)
        {
            if (list?.waits == null) return;
            DateTime threshold = DateTime.UtcNow.AddMinutes(-StaleWaitMinutes);
            // 只清終態（pending 不論多久都保留 — 由 FinalizeOrphanedPending 另外處理）
            list.waits.RemoveAll(w =>
            {
                if (w.status == "pending") return false;
                if (string.IsNullOrEmpty(w.finished_at)) return false;
                if (DateTime.TryParse(w.finished_at, out var fin)) return fin < threshold;
                return false;
            });
        }

        // ===========================================================
        // 區塊職責：序號（_seq.txt）— 單調遞增
        // 物理意義：每 append 一筆訊息前 ReadAndIncrement 拿到 seq；prototype 不做跨 process lock
        // ===========================================================

        public static int ReadCurrentSeq(string roomId)
        {
            string path = GetSeqPath(roomId);
            if (!File.Exists(path)) return 0;
            try
            {
                string s = File.ReadAllText(path, Encoding.UTF8).Trim();
                return int.TryParse(s, out var v) ? v : 0;
            }
            catch { return 0; }
        }

        static int IncrementAndGetSeq(string roomId)
        {
            EnsureRoomDir(roomId);
            int next = ReadCurrentSeq(roomId) + 1;
            File.WriteAllText(GetSeqPath(roomId), next.ToString(), new UTF8Encoding(false));
            return next;
        }

        // ===========================================================
        // 區塊職責：訊息（messages.jsonl）— append-only
        // 物理意義：每行一個訊息 JSON。讀取時 split by '\n'，逐行 parse。
        // ===========================================================

        /// <summary>追加一筆訊息（自動分配 seq + ts）。回傳分配後的 seq。</summary>
        public static int AppendMessage(string roomId, UCL_ChatMessage msg)
        {
            EnsureRoomDir(roomId);
            msg.seq = IncrementAndGetSeq(roomId);
            if (string.IsNullOrEmpty(msg.ts)) msg.ts = NowUtcIso();
            string line = SerializeMessage(msg) + "\n";
            File.AppendAllText(GetMessagesPath(roomId), line, new UTF8Encoding(false));
            return msg.seq;
        }

        /// <summary>讀取整個 jsonl（小規模時 OK；房間訊息上萬時應改 streaming，列為 v2）。</summary>
        public static List<UCL_ChatMessage> LoadAllMessages(string roomId)
        {
            string path = GetMessagesPath(roomId);
            var list = new List<UCL_ChatMessage>();
            if (!File.Exists(path)) return list;
            try
            {
                using var sr = new StreamReader(path, Encoding.UTF8);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var m = ParseMessage(line);
                        if (m != null) list.Add(m);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ChatTavern] Skipping malformed message line: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load messages: {e}");
            }
            return list;
        }

        public static List<UCL_ChatMessage> Tail(string roomId, int n)
        {
            var all = LoadAllMessages(roomId);
            if (all.Count <= n) return all;
            return all.GetRange(all.Count - n, n);
        }

        public static List<UCL_ChatMessage> Range(string roomId, int from, int to)
        {
            var all = LoadAllMessages(roomId);
            var result = new List<UCL_ChatMessage>();
            foreach (var m in all)
            {
                if (m.seq >= from && m.seq <= to) result.Add(m);
            }
            return result;
        }

        public static List<UCL_ChatMessage> Since(string roomId, int sinceSeq, int limit)
        {
            var all = LoadAllMessages(roomId);
            var result = new List<UCL_ChatMessage>();
            foreach (var m in all)
            {
                if (m.seq > sinceSeq) result.Add(m);
                if (limit > 0 && result.Count >= limit) break;
            }
            return result;
        }

        public static List<UCL_ChatMessage> Search(string roomId, string keyword, int limit)
        {
            if (string.IsNullOrEmpty(keyword)) return new List<UCL_ChatMessage>();
            var all = LoadAllMessages(roomId);
            var result = new List<UCL_ChatMessage>();
            foreach (var m in all)
            {
                if (m.body != null && m.body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(m);
                    if (limit > 0 && result.Count >= limit) break;
                }
            }
            return result;
        }

        // ===========================================================
        // 區塊職責：訊息 JSON 序列化 / 解析
        // 物理意義：JsonUtility 不支援 Dict / nullable int / List of object；改手寫 minimal serializer
        //          格式為單行 JSON object（jsonl 每行一筆）
        // ===========================================================

        public static string SerializeMessage(UCL_ChatMessage m)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"seq\":").Append(m.seq);
            sb.Append(",\"ts\":\"").Append(EscapeStr(m.ts)).Append("\"");
            sb.Append(",\"sender_id\":\"").Append(EscapeStr(m.sender_id)).Append("\"");
            sb.Append(",\"sender_name\":\"").Append(EscapeStr(m.sender_name)).Append("\"");
            sb.Append(",\"kind\":\"").Append(EscapeStr(m.kind ?? "chat")).Append("\"");
            sb.Append(",\"body\":\"").Append(EscapeStr(m.body ?? "")).Append("\"");
            if (m.reply_to.HasValue) sb.Append(",\"reply_to\":").Append(m.reply_to.Value);
            if (m.meta != null && m.meta.Count > 0)
            {
                sb.Append(",\"meta\":{");
                bool first = true;
                foreach (var kv in m.meta)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\"").Append(EscapeStr(kv.Key)).Append("\":\"").Append(EscapeStr(kv.Value)).Append("\"");
                }
                sb.Append("}");
            }
            if (m.refs != null && m.refs.Count > 0)
            {
                sb.Append(",\"refs\":[");
                bool first = true;
                foreach (var r in m.refs)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"path\":\"").Append(EscapeStr(r.path ?? "")).Append("\"");
                    if (!string.IsNullOrEmpty(r.anchor)) sb.Append(",\"anchor\":\"").Append(EscapeStr(r.anchor)).Append("\"");
                    if (!string.IsNullOrEmpty(r.label)) sb.Append(",\"label\":\"").Append(EscapeStr(r.label)).Append("\"");
                    sb.Append("}");
                }
                sb.Append("]");
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static UCL_ChatMessage ParseMessage(string json)
        {
            int pos = 0;
            SkipWS(json, ref pos);
            ExpectChar(json, ref pos, '{');
            var m = new UCL_ChatMessage();
            while (true)
            {
                SkipWS(json, ref pos);
                if (pos >= json.Length) break;
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                switch (key)
                {
                    case "seq": m.seq = ParseInt(json, ref pos); break;
                    case "ts": m.ts = ParseStringOrNull(json, ref pos); break;
                    case "sender_id": m.sender_id = ParseStringOrNull(json, ref pos); break;
                    case "sender_name": m.sender_name = ParseStringOrNull(json, ref pos); break;
                    case "kind": m.kind = ParseStringOrNull(json, ref pos); break;
                    case "body": m.body = ParseStringOrNull(json, ref pos); break;
                    case "reply_to":
                        SkipWS(json, ref pos);
                        if (pos < json.Length && json[pos] == 'n') { SkipValue(json, ref pos); m.reply_to = null; }
                        else m.reply_to = ParseInt(json, ref pos);
                        break;
                    case "meta": m.meta = ParseStringDict(json, ref pos); break;
                    case "refs": m.refs = ParseRefs(json, ref pos); break;
                    default: SkipValue(json, ref pos); break;
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return m;
        }

        static List<UCL_ChatRef> ParseRefs(string json, ref int pos)
        {
            var list = new List<UCL_ChatRef>();
            ExpectChar(json, ref pos, '[');
            while (true)
            {
                SkipWS(json, ref pos);
                if (pos >= json.Length) break;
                if (json[pos] == ']') { pos++; break; }
                ExpectChar(json, ref pos, '{');
                var r = new UCL_ChatRef();
                while (true)
                {
                    SkipWS(json, ref pos);
                    if (pos >= json.Length) break;
                    if (json[pos] == '}') { pos++; break; }
                    string key = ParseString(json, ref pos);
                    SkipWS(json, ref pos);
                    ExpectChar(json, ref pos, ':');
                    SkipWS(json, ref pos);
                    switch (key)
                    {
                        case "path": r.path = ParseStringOrNull(json, ref pos); break;
                        case "anchor": r.anchor = ParseStringOrNull(json, ref pos); break;
                        case "label": r.label = ParseStringOrNull(json, ref pos); break;
                        default: SkipValue(json, ref pos); break;
                    }
                    SkipWS(json, ref pos);
                    if (pos < json.Length && json[pos] == ',') { pos++; continue; }
                }
                list.Add(r);
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return list;
        }

        static Dictionary<string, string> ParseStringDict(string json, ref int pos)
        {
            var d = new Dictionary<string, string>();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (pos >= json.Length) break;
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
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
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
                if (pos + 4 <= json.Length && json.Substring(pos, 4) == "null") { pos += 4; return null; }
            }
            return ParseString(json, ref pos);
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
                throw new Exception($"Expected '{ch}' at pos {pos}, got '{(pos<json.Length?json[pos]:'?')}'");
            pos++;
        }
        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
#endif
