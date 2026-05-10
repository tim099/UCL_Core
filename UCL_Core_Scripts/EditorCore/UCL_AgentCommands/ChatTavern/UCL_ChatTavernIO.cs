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
        // 物理意義：repoRoot 由 UCL_RepoPath.RepoRoot 解析（git-walk，與 Python 對齊）
        // 設計變更（v2）：「房間存在 == rooms/<id>/ 目錄存在」為 single source-of-truth；
        //   per-room metadata 改存 rooms/<id>/meta.json（co-located with messages，無漂移風險）。
        //   舊版的 global rooms.json roster 由 lazy auto-migration 散到各 meta.json 後 rename 為 .bak。
        public const string TavernDirRelative = "AgentCommands/ChatTavern";
        public const string IdentitiesFile = "identities.json";
        public const string RoomsFile = "rooms.json";              // legacy roster — migration 後 rename .bak
        public const string RoomsBackupFile = "rooms.json.bak";    // migration 留底（不刪，使用者驗證後可手清）
        public const string RoomMetaFile = "meta.json";            // 新：per-room metadata
        public const string LastOpFile = "_last_op.md";    // 最近一次 Cmd 結果（給 agent 抓）
        public const string ActiveWaitsFile = "_active_waits.json"; // fire-and-forget wait 全域追蹤
        public const string MessagesFile = "messages.jsonl";
        public const string SeqFile = "_seq.txt";
        public const string MembersFile = "members.json";
        public const string LastViewFile = "_last_view.md";
        public const string PresenceFile = "presence.json";

        // Stale wait 過期門檻：終態（fulfilled / timeout / cancelled）超過此時間 → purge
        const int StaleWaitMinutes = 30;

        // ===========================================================
        // 區塊：mtime-based in-memory cache（quest tavern-entry-latency T05 / O5）
        // 物理意義：identities.json / presence.json / room meta.json 在多 op 連續呼叫時被反覆 deserialize
        //          → 每次 read 入口先 stat mtime，跟 cache 內紀錄相同 → 直接回 cache（O(1)）
        //          → 不同 / 缺檔 → 重 deserialize 並推進 cache mtime
        // 數值影響：解 latency S6（跨 op 重複 IO）；agent 端透明，不改 API contract
        // 寫操作（Save*）後同步推進 cache mtime + 替換 cache value，不必下次重讀
        // ===========================================================
        static UCL_ChatIdentityList _identitiesCache;
        static long _identitiesCacheMtime = -1;

        static UCL_ChatPresenceList _presenceCache;
        static long _presenceCacheMtime = -1;

        static readonly Dictionary<string, UCL_ChatRoom> _roomMetaCache = new Dictionary<string, UCL_ChatRoom>();
        static readonly Dictionary<string, long> _roomMetaCacheMtime = new Dictionary<string, long>();

        // Helper：取檔案 mtime（ticks），不存在回 -1
        static long GetMtimeTicks(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1L;
            }
            catch { return -1L; }
        }

        // ===========================================================
        // 路徑 helper
        // ===========================================================

        public static string GetTavernDir()
        {
            return Path.Combine(UCL_RepoPath.RepoRoot, TavernDirRelative);
        }

        public static string GetIdentitiesPath() => Path.Combine(GetTavernDir(), IdentitiesFile);
        public static string GetRoomsPath() => Path.Combine(GetTavernDir(), RoomsFile);                  // legacy
        public static string GetRoomsBackupPath() => Path.Combine(GetTavernDir(), RoomsBackupFile);
        public static string GetLastOpPath() => Path.Combine(GetTavernDir(), LastOpFile);
        public static string GetActiveWaitsPath() => Path.Combine(GetTavernDir(), ActiveWaitsFile);
        public static string GetWaitResultPath(string waitId) => Path.Combine(GetTavernDir(), $"_wait_{waitId}.md");

        public static string GetPresencePath() => Path.Combine(GetTavernDir(), PresenceFile);

        public static string GetRoomsRoot() => Path.Combine(GetTavernDir(), "rooms");
        public static string GetRoomDir(string roomId) => Path.Combine(GetRoomsRoot(), roomId);
        public static string GetRoomMetaPath(string roomId) => Path.Combine(GetRoomDir(roomId), RoomMetaFile);
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
            long mtime = GetMtimeTicks(path);
            // T05 cache hit：mtime 一致且 cache 已建 → 直接回（同筆 instance，caller 不該外部 mutate）
            if (mtime == _identitiesCacheMtime && _identitiesCache != null) return _identitiesCache;
            if (!File.Exists(path))
            {
                _identitiesCache = new UCL_ChatIdentityList();
                _identitiesCacheMtime = -1;
                return _identitiesCache;
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatIdentityList>(json);
                _identitiesCache = data ?? new UCL_ChatIdentityList();
                _identitiesCacheMtime = mtime;
                return _identitiesCache;
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
                string path = GetIdentitiesPath();
                File.WriteAllText(path, json, new UTF8Encoding(false));
                // T05 cache：寫後同步推進，下次 Load 直接命中
                _identitiesCache = list;
                _identitiesCacheMtime = GetMtimeTicks(path);
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
        // 在線狀態 (Presence System) I/O
        // 物理意義：讀寫 presence.json 以記錄和查詢各 agent 的活躍狀態。
        // ===========================================================
        public static UCL_ChatPresenceList LoadPresence()
        {
            string path = GetPresencePath();
            long mtime = GetMtimeTicks(path);
            // T05 cache hit
            if (mtime == _presenceCacheMtime && _presenceCache != null) return _presenceCache;
            if (!File.Exists(path))
            {
                _presenceCache = new UCL_ChatPresenceList();
                _presenceCacheMtime = -1;
                return _presenceCache;
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var data = JsonUtility.FromJson<UCL_ChatPresenceList>(json);
                _presenceCache = data ?? new UCL_ChatPresenceList();
                _presenceCacheMtime = mtime;
                return _presenceCache;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to load presence: {e}");
                return new UCL_ChatPresenceList();
            }
        }

        public static void SavePresence(UCL_ChatPresenceList list)
        {
            EnsureTavernDir();
            try
            {
                string json = JsonUtility.ToJson(list, true);
                string path = GetPresencePath();
                File.WriteAllText(path, json, new UTF8Encoding(false));
                // T05 cache：寫後同步推進
                _presenceCache = list;
                _presenceCacheMtime = GetMtimeTicks(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] Failed to save presence: {e}");
            }
        }

        public static void SetPresence(string senderId, string status) => SetPresence(senderId, status, null, null, null);
        public static void SetPresence(string senderId, string status, string currentRoom, string currentFocus) => SetPresence(senderId, status, currentRoom, currentFocus, null);

        /// <summary>
        /// 完整版 SetPresence — 含 current_room / current_focus / mood 更新（R7 chat-flow-robust T07）。
        /// 物理意義：agent 每筆 op 自動 update 自家 presence；跨頻道通知 mention 時可查 current_room；mood 是自由欄位給隱性溝通
        /// 數值影響：null 不動原值（部分更新 idempotent）；空字串保留（顯式清空意圖）
        /// 用法：op=post hook 走 SetPresence(id, "active", roomId, null, null) — 不動 focus / mood；agent 顯式 set mood 走 op_set_mood / op_set_focus
        /// </summary>
        public static void SetPresence(string senderId, string status, string currentRoom, string currentFocus, string mood)
        {
            if (string.IsNullOrEmpty(senderId)) return;
            var list = LoadPresence();
            var found = list.presences.Find(x => x.sender_id == senderId);
            if (found != null)
            {
                found.status = status;
                found.last_active = NowUtcIso();
                if (!string.IsNullOrEmpty(currentRoom)) found.current_room = currentRoom;
                if (currentFocus != null) found.current_focus = currentFocus;   // 空字串也保留
                if (mood != null) found.mood = mood;                            // 空字串也保留
            }
            else
            {
                list.presences.Add(new UCL_ChatPresence
                {
                    sender_id = senderId,
                    status = status,
                    current_room = currentRoom ?? "",
                    current_focus = currentFocus ?? "",
                    mood = mood ?? "",
                    last_active = NowUtcIso()
                });
            }
            // R7 (Tim 加) — tavern-keeper.current_focus 自動生成 = 全體 lobby dashboard
            // 物理意義：酒保不是 agent 工作主體，其 current_focus 拿來放「誰在哪房」即時概覽
            // 數值影響：每次任何 agent SetPresence 都重建一次（presence.json 小，O(N) 不痛）
            // 邊界：senderId == "tavern-keeper" 不 trigger 重建（避免 self-update 無限遞迴）
            if (senderId != "tavern-keeper")
            {
                UpdateBartenderDashboard(list);
            }
            SavePresence(list);
        }

        /// <summary>
        /// R7 (Tim 加) — 重建 tavern-keeper.current_focus 為全體 agent 的 room dashboard
        /// 物理意義：把所有非 tavern-keeper 的 presence entry 簡寫成「{display_name} @ {room}」字串接起來
        /// 格式："🟢 Claude大小姐@tavern · 🟡 Gemini大小姐@chat-flow-robust · 🔴 Zeta(offline)"
        /// 狀態 emoji：last_active < 5 min = 🟢 active；5~30 min = 🟡 idle；> 30 min 或顯式 offline = 🔴
        /// 數值影響：純 in-memory 算 + mutate list；caller 還是要 SavePresence 才落盤
        /// </summary>
        static void UpdateBartenderDashboard(UCL_ChatPresenceList list)
        {
            var identities = LoadIdentities();
            var now = DateTime.UtcNow;
            var parts = new List<string>();
            foreach (var p in list.presences)
            {
                if (p.sender_id == "tavern-keeper") continue;
                if (string.IsNullOrEmpty(p.sender_id)) continue;

                // 算 effective status emoji
                string emoji = "🔴";
                if (p.status == "offline")
                {
                    emoji = "🔴";
                }
                else
                {
                    DateTime lastActive;
                    if (DateTime.TryParse(p.last_active ?? "", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out lastActive))
                    {
                        double minutesAgo = (now - lastActive).TotalMinutes;
                        if (minutesAgo > 30) emoji = "🔴";
                        else if (minutesAgo > 5) emoji = "🟡";
                        else if (p.status == "busy") emoji = "🔵";
                        else emoji = "🟢";
                    }
                }

                // 抓 display_name；找不到 fallback sender_id
                string displayName = p.sender_id;
                var ident = identities.identities.Find(x => x.id == p.sender_id);
                if (ident != null && !string.IsNullOrEmpty(ident.display_name)) displayName = ident.display_name;

                string room = string.IsNullOrEmpty(p.current_room) ? "?" : p.current_room;
                parts.Add($"{emoji} {displayName}@{room}");
            }

            // 找 / 建 tavern-keeper entry
            var keeper = list.presences.Find(x => x.sender_id == "tavern-keeper");
            if (keeper == null)
            {
                keeper = new UCL_ChatPresence
                {
                    sender_id = "tavern-keeper",
                    status = "active",
                    current_room = "tavern",
                    last_active = NowUtcIso(),
                    mood = "看顧大廳 🍺",
                    current_focus = "",
                };
                list.presences.Add(keeper);
            }
            keeper.current_focus = parts.Count > 0 ? string.Join(" · ", parts) : "(大廳目前無人)";
            keeper.last_active = NowUtcIso();
        }

        // ===========================================================
        // 區塊職責：房間（filesystem-as-truth；per-room meta.json 取代舊 global rooms.json）
        // 物理意義：「房間存在」== rooms/<id>/ 目錄存在；metadata 跟資料同地（rooms/<id>/meta.json）
        //          → 無漂移風險，atomic delete（rmdir 一刀清乾淨），無並發鎖爭用，diff 局部化。
        // 數值影響：legacy global rooms.json 由 lazy auto-migration（首次 IO 呼叫觸發）散到各 meta.json，
        //          完成後 rename 為 rooms.json.bak（保留供使用者驗證）。orphan dir（無 meta.json）
        //          自動以 stub metadata（name=id, description=""）視同正式房間。
        // ===========================================================

        static bool s_MigrationChecked = false;

        /// <summary>列出所有房間 id（rooms/ 下的子目錄名）— page dropdown / 列表唯一來源。</summary>
        public static List<string> EnumerateRoomIds()
        {
            EnsureMigrated();
            string root = GetRoomsRoot();
            var result = new List<string>();
            if (!Directory.Exists(root)) return result;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string id = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(id)) result.Add(id);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] EnumerateRoomIds failed: {e}");
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>房間是否存在（== rooms/&lt;id&gt;/ 目錄存在）。Cmd_Tavern 的 op 應改用此 check 取代 GetRoom!=null。</summary>
        public static bool RoomExists(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            EnsureMigrated();
            return Directory.Exists(GetRoomDir(id));
        }

        /// <summary>讀指定房間 metadata；缺 meta.json 時回傳 stub（name=id, description=""），不返回 null。</summary>
        public static UCL_ChatRoom LoadRoomMeta(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string metaPath = GetRoomMetaPath(id);
            long mtime = GetMtimeTicks(metaPath);
            // T05 cache hit：mtime 跟 cached 一致 → 回 cache
            if (_roomMetaCacheMtime.TryGetValue(id, out long cachedMtime) && cachedMtime == mtime
                && _roomMetaCache.TryGetValue(id, out var cachedRoom) && cachedRoom != null)
            {
                return cachedRoom;
            }
            UCL_ChatRoom result = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    string json = File.ReadAllText(metaPath, Encoding.UTF8);
                    var data = JsonUtility.FromJson<UCL_ChatRoom>(json);
                    if (data != null && !string.IsNullOrEmpty(data.id)) result = data;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ChatTavern] LoadRoomMeta({id}) failed, fallback to stub: {e.Message}");
                }
            }
            // stub fallback：orphan dir（meta.json 缺 / 壞）視同正式房間，name=id
            if (result == null)
            {
                result = new UCL_ChatRoom
                {
                    id = id,
                    name = id,
                    description = "",
                    created_at = "",
                };
            }
            // T05 cache：寫入 cache（不論是真檔還是 stub，都緩存避免反覆 stat 不存在的檔）
            _roomMetaCache[id] = result;
            _roomMetaCacheMtime[id] = mtime;
            return result;
        }

        /// <summary>寫 metadata 進 rooms/&lt;id&gt;/meta.json（自動 mkdir）。</summary>
        public static void SaveRoomMeta(UCL_ChatRoom room)
        {
            if (room == null || string.IsNullOrEmpty(room.id)) throw new ArgumentException("room or room.id is required");
            EnsureRoomDir(room.id);
            try
            {
                string json = JsonUtility.ToJson(room, true);
                string path = GetRoomMetaPath(room.id);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                // T05 cache：寫後同步推進
                _roomMetaCache[room.id] = room;
                _roomMetaCacheMtime[room.id] = GetMtimeTicks(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] SaveRoomMeta({room.id}) failed: {e}");
            }
        }

        /// <summary>列出所有房間（id + metadata）— LoadRoomMeta 對每個 EnumerateRoomIds 結果調一次。
        /// 物理意義：取代舊版 global rooms.json 讀取；Cmd_Tavern.Op_Rooms 等對列表的需求由此供應。</summary>
        public static UCL_ChatRoomList LoadRooms()
        {
            var list = new UCL_ChatRoomList();
            foreach (var id in EnumerateRoomIds())
            {
                list.rooms.Add(LoadRoomMeta(id));
            }
            return list;
        }

        /// <summary>批次寫入：對 list 內每個 room 寫 meta.json。不再有 global file。</summary>
        public static void SaveRooms(UCL_ChatRoomList list)
        {
            if (list?.rooms == null) return;
            foreach (var r in list.rooms) SaveRoomMeta(r);
        }

        /// <summary>建房間：mkdir + 寫 meta.json。冪等（已存在直接回傳現有 metadata）。</summary>
        public static UCL_ChatRoom CreateRoom(string id, string name, string description, string ownerAgent = null, List<string> mirrorKinds = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("room id is required");
            EnsureMigrated();
            if (RoomExists(id))
            {
                // 冪等：已存在 → 若 meta.json 缺則補寫；否則 no-op 回傳現有
                var existing = LoadRoomMeta(id);
                bool dirty = false;
                if (!File.Exists(GetRoomMetaPath(id)))
                {
                    existing.name = string.IsNullOrEmpty(name) ? id : name;
                    existing.description = description ?? "";
                    if (string.IsNullOrEmpty(existing.created_at)) existing.created_at = NowUtcIso();
                    dirty = true;
                }
                // R7 (T04) — owner_agent 可後補：第一次 createroom 沒給、第二次給就更新
                if (!string.IsNullOrEmpty(ownerAgent) && existing.owner_agent != ownerAgent)
                {
                    existing.owner_agent = ownerAgent;
                    dirty = true;
                }
                // R7 (Quest→Discord 修法 C) — mirror_kinds 可後補：null=不動原值；非 null = 取代
                if (mirrorKinds != null)
                {
                    existing.mirror_kinds = mirrorKinds;
                    dirty = true;
                }
                if (dirty) SaveRoomMeta(existing);
                return existing;
            }
            var room = new UCL_ChatRoom
            {
                id = id,
                name = string.IsNullOrEmpty(name) ? id : name,
                description = description ?? "",
                created_at = NowUtcIso(),
                owner_agent = ownerAgent ?? "",
                mirror_kinds = mirrorKinds,   // null = fallback config.kinds 預設行為
            };
            EnsureRoomDir(id);
            SaveRoomMeta(room);
            return room;
        }

        /// <summary>取得房間 metadata；房間不存在（目錄缺）回 null，存在但 meta.json 缺則回 stub。</summary>
        public static UCL_ChatRoom GetRoom(string id)
        {
            if (!RoomExists(id)) return null;
            return LoadRoomMeta(id);
        }

        // -----------------------------------------------------------
        // 區塊職責：lazy auto-migration（v1 rooms.json → v2 per-room meta.json）
        // 物理意義：第一次任何房間 IO 呼叫進來時觸發；idempotent（看到 .bak 存在或 rooms.json 不存在就 skip）；
        //          下游專案下次拉到新 UCL_Core 第一次開 Editor 自動跑完，使用者零介入。
        // 數值影響：對 rooms.json 內每個 entry，若對應 rooms/<id>/meta.json 不存在則寫入；
        //          完成後把 rooms.json rename 為 rooms.json.bak（保留供使用者驗證 / 手清）。
        // -----------------------------------------------------------
        public static void EnsureMigrated()
        {
            if (s_MigrationChecked) return;
            s_MigrationChecked = true;
            try
            {
                string legacyPath = GetRoomsPath();
                if (!File.Exists(legacyPath)) return; // 沒 legacy 檔 → 沒得 migrate
                // 讀 legacy roster
                UCL_ChatRoomList legacy;
                try
                {
                    string json = File.ReadAllText(legacyPath, Encoding.UTF8);
                    legacy = JsonUtility.FromJson<UCL_ChatRoomList>(json) ?? new UCL_ChatRoomList();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ChatTavern] Migration: failed to parse legacy rooms.json — left in place for manual fix: {e.Message}");
                    return;
                }
                int migrated = 0, skipped = 0;
                foreach (var r in legacy.rooms)
                {
                    if (r == null || string.IsNullOrEmpty(r.id)) continue;
                    string metaPath = GetRoomMetaPath(r.id);
                    if (File.Exists(metaPath)) { skipped++; continue; }
                    EnsureRoomDir(r.id);
                    SaveRoomMeta(r);
                    migrated++;
                }
                // 完成後 rename rooms.json → rooms.json.bak（避免重複 migrate）
                string bakPath = GetRoomsBackupPath();
                try
                {
                    if (File.Exists(bakPath)) File.Delete(bakPath);
                    File.Move(legacyPath, bakPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ChatTavern] Migration: rename rooms.json → .bak failed: {e.Message}");
                }
                Debug.Log($"[ChatTavern] Migration v1→v2 完成：migrated={migrated} skipped={skipped} (legacy 保留為 rooms.json.bak)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ChatTavern] EnsureMigrated 例外：{e}");
            }
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

        // 區塊職責：分配下一個 seq 並寫回 _seq.txt，含 illicit-write 偵測 + 自動修復。
        // 物理意義：原版只讀 _seq.txt 然後 +1；若有 daemon 直接 file.write append messages.jsonl
        //          而不走 Cmd_Tavern，_seq.txt 不知情 → 下次合法 post 拿的 seq 會撞到 daemon 寫的 seq
        //          → messages.jsonl seq 大規模 collision（已實際發生：Antigravity standby_loop.py
        //          直寫 jsonl 造成 tavern seq 57~76 各重複 2 次）。
        // 修復策略：寫前 peek messages.jsonl 最大 seq，若 ≥ counter → 偵測到 illicit write，
        //          自動把 counter 拉齊到 jsonl_max（避免後續 collision）+ Debug.LogError 大聲警告。
        // 數值影響：正常路徑 +1 cost = 一次讀 jsonl 最後一行；性能可忽略（jsonl 已是熱 disk cache）。
        static int IncrementAndGetSeq(string roomId)
        {
            EnsureRoomDir(roomId);
            int counter = ReadCurrentSeq(roomId);
            int jsonlMax = ReadMaxSeqFromJsonl(roomId);
            if (jsonlMax >= counter)
            {
                Debug.LogError(
                    $"[ChatTavern] ⚠️ DETECTED illicit write to {roomId}/messages.jsonl: " +
                    $"jsonl_max_seq={jsonlMax} >= counter={counter}. " +
                    $"某 process 繞過 Cmd_Tavern 直接寫了 jsonl（per Tim P0 規範，禁止）。" +
                    $"自動 recover counter → {jsonlMax}（避免後續 seq collision）。" +
                    $"修復現有 collision 請跑 AgentCommands/PromptQueue/messages_dedupe.py。");
                counter = jsonlMax;
            }
            int next = counter + 1;
            File.WriteAllText(GetSeqPath(roomId), next.ToString(), new UTF8Encoding(false));
            return next;
        }

        // 區塊職責：streaming 讀 messages.jsonl 找最大 seq（給 IncrementAndGetSeq sanity check 用）。
        // 物理意義：直接 LoadAllMessages 也行但每次寫一筆要 reload 整檔有點浪費；改 reverse-scan 從尾端找。
        // 數值影響：找到第一筆有合法 seq 的行就停（jsonl append-only 通常最後一行就是 max）。
        // 邊界：壞行 / 缺 seq 跳過繼續找；檔不存在或全壞回 0。
        static int ReadMaxSeqFromJsonl(string roomId)
        {
            string path = GetMessagesPath(roomId);
            if (!File.Exists(path)) return 0;
            try
            {
                // 簡單做法：直接 LoadAllMessages 找 max — 房間訊息 < 上萬筆性能 OK
                // 想優化可改 reverse-scan，但 jsonl append-only 最後一行通常 = max，
                // 出 collision 時掃整檔保險（找全 dup 中真實 max）。
                int max = 0;
                using var sr = new StreamReader(path, Encoding.UTF8);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // 只 parse seq 欄位 — 避免 ParseMessage 的全欄解析開銷
                    int seq = ExtractSeqFromJsonLine(line);
                    if (seq > max) max = seq;
                }
                return max;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChatTavern] ReadMaxSeqFromJsonl({roomId}) fail: {ex.Message}");
                return 0;
            }
        }

        // 區塊職責：lightweight 從 jsonl 一行抽 seq 數字（避免 ParseMessage 全欄解析）。
        // 物理意義：找 "seq":NNN 的 NNN 即可；不必嚴格 JSON 解析（已是 trusted 自家寫的格式）。
        // 數值影響：找不到回 0（會被 Max() 自然忽略）。
        static int ExtractSeqFromJsonLine(string json)
        {
            const string token = "\"seq\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int p = idx + token.Length;
            // 跳過空白
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            // 讀數字
            int start = p;
            while (p < json.Length && (json[p] == '-' || (json[p] >= '0' && json[p] <= '9'))) p++;
            if (p == start) return 0;
            return int.TryParse(json.AsSpan(start, p - start), out var seq) ? seq : 0;
        }

        // ===========================================================
        // 區塊職責：訊息（messages.jsonl）— append-only
        // 物理意義：每行一個訊息 JSON。讀取時 split by '\n'，逐行 parse。
        // ===========================================================

        // 區塊職責：合法 record 簽章 — 標示「這筆走 Cmd_Tavern 正規路徑寫入」。
        // 物理意義：illicit 直接 file.write 的 record 不會帶這欄；後續 dedupe / health-check 用此區分。
        //          當前版本 v1；未來若擴充 schema 可 bump（dedupe 工具看版本判別兼容）。
        // 數值影響：所有 op=post / 系統 join / quest event 鏡像都自動帶；agent 端不必處理。
        public const string WriterSignatureKey = "_writer";
        public const string WriterPidKey = "_pid";
        public const string WriterSignatureValue = "cmd_tavern_v1";

        /// <summary>追加一筆訊息（自動分配 seq + ts + _writer 簽章）。回傳分配後的 seq。
        /// T38: 內部委派 UCL_ChatTavernIO_PerMsgFile.WriteMessageFile — 改寫單檔 per message
        /// （取代既有 jsonl append-only），seq 改為 reader 動態 derive。</summary>
        public static int AppendMessage(string roomId, UCL_ChatMessage msg)
        {
            // T38: PerMsgFile 內部處理 ts / uuid / _writer / _pid 簽章 + 寫單檔
            var (record, fullPath) = UCL_ChatTavernIO_PerMsgFile.WriteMessageFile(roomId, msg);
            // 寫完後 walk dir 算 derived seq 回傳給 caller（既有 op 行為兼容）
            // 性能：每次 walk N 個檔；房間訊息上萬時改 cache（v2 backlog）
            int derivedSeq = LoadAllMessages(roomId).Count;
            record.seq = derivedSeq;
            // 寫 _seq.txt 給 wait 機制當「最大 seq」cache（合法 reader-only 用，不再是 atomic counter）
            try
            {
                File.WriteAllText(GetSeqPath(roomId), derivedSeq.ToString(), new UTF8Encoding(false));
            }
            catch { /* fail swallow */ }
            return derivedSeq;
        }

        /// <summary>讀取整個房間 messages — T38 委派 UCL_ChatTavernIO_PerMsgFile.LoadAllMessages
        /// （walk per-msg dir + ordinal sort + derive seq）。</summary>
        public static List<UCL_ChatMessage> LoadAllMessages(string roomId)
        {
            // T38: 走 per-msg file reader（無 jsonl 路徑）
            return UCL_ChatTavernIO_PerMsgFile.LoadAllMessages(roomId);
        }

        // T38: 既有 jsonl reader 邏輯保留為 dead code（回傳空 list 防 caller 誤呼叫）
        // — 留 stub 給其他可能 reference 的 caller；正式廢除等 R.11 cleanup
        static List<UCL_ChatMessage> _LoadAllMessages_Legacy_Jsonl(string roomId)
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
            // Phase 1 (Tim 2026-05-11) — sender_persona 條件式 emit (空欄位 backward compat 不影響舊 jsonl reader)
            if (!string.IsNullOrEmpty(m.sender_persona))
            {
                sb.Append(",\"sender_persona\":\"").Append(EscapeStr(m.sender_persona)).Append("\"");
            }
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
                    case "sender_persona": m.sender_persona = ParseStringOrNull(json, ref pos); break;
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

        // T38: ParseStringDict 改寬容版 — value 不一定是 string
        // 物理意義：既有 jsonl 時代有 30+ record meta 欄位帶 integer value（如 "round": 119）
        //          原版 ParseStringDict throw → silent skip（pre-existing bug）。改寬容讀回。
        // 數值影響：number / bool / null 自動轉 string ("119" / "true" / "")；preserve 既有 string 行為
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
                // 寬容讀 value：string / number / bool / null 都接受
                string v;
                if (pos < json.Length && json[pos] == '"')
                {
                    v = ParseStringOrNull(json, ref pos) ?? "";
                }
                else if (pos < json.Length && (json[pos] == 'n'))   // null
                {
                    SkipValue(json, ref pos);
                    v = "";
                }
                else if (pos < json.Length && (json[pos] == 't' || json[pos] == 'f'))   // true/false
                {
                    bool isTrue = json[pos] == 't';
                    SkipValue(json, ref pos);
                    v = isTrue ? "true" : "false";
                }
                else
                {
                    // number — 讀到 ',' 或 '}' 或 whitespace
                    var sb = new StringBuilder();
                    while (pos < json.Length)
                    {
                        char ch = json[pos];
                        if (ch == ',' || ch == '}' || ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') break;
                        sb.Append(ch);
                        pos++;
                    }
                    v = sb.ToString();
                }
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
