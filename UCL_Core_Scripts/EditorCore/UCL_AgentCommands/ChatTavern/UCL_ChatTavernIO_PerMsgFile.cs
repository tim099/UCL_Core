// 區塊職責：T38 — Per-message file IO（每訊息一獨立 .json 檔）
// 物理意義：取代既有 messages.jsonl 單檔 append-only；
//          檔名約定：rooms/<room>/messages/<YYYY-MM-DD>/<HHMMSS>_<MMM>_<UUID6>.json
//          字典序 sort = 時間 sort（檔名 ts prefix 設計）
//          UUID6 (16M^6 種) 確保並發 0 撞檔機率
// 數值影響：seq 不寫進檔，reader 動態 derive（walk + sort + enumerate）
//          → 並發 race-free（不靠 atomic counter）
//          → cross-branch git merge 自動保留所有訊息（檔名各異）
// 設計取捨：本檔純新增不動既有 UCL_ChatTavernIO；R.6 切換時 caller 改用本 module 的 helper
//          inbox / notes / meta 不分檔（per Plan §2.8 trade-off）

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Per-message file IO helper（T38）— 每訊息一獨立 .json 檔。
    /// 跟既有 UCL_ChatTavernIO append-only jsonl 邏輯並存（R.6 整批切換時 caller 改用本 module）。
    /// </summary>
    public static class UCL_ChatTavernIO_PerMsgFile
    {
        // 區塊職責：路徑常數 / helpers — 對應 Python _lib.tavern_paths 的 per-msg path scheme
        public const string MessagesDirName = "messages";
        public const string EventsDirName = "events";
        public const string BackupDirName = "_backup";

        public const string WriterSignatureKey = "_writer";
        public const string WriterPidKey = "_pid";
        public const string WriterSignatureValue = "cmd_tavern_v2";   // bump from v1（jsonl era）

        public static string GetMessagesRoot(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), MessagesDirName);

        public static string GetEventsRoot(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), EventsDirName);

        public static string GetMessagesDateDir(string roomId, DateTime utcDate)
            => Path.Combine(GetMessagesRoot(roomId), utcDate.ToString("yyyy-MM-dd"));

        public static string GetEventsDateDir(string roomId, DateTime utcDate)
            => Path.Combine(GetEventsRoot(roomId), utcDate.ToString("yyyy-MM-dd"));

        public static string GetBackupRoot(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), BackupDirName);

        // ===========================================================
        // 區塊職責：UUID6 生成 — 6-char hex random
        // 物理意義：同 ms 內並發寫的訊息靠 UUID6 區別；16M 種足夠 99.99%+ 並發場景
        // 數值影響：用 RNGCryptoServiceProvider 強隨機（避免 Random() 同 ms 內同 seed）
        // ===========================================================
        static readonly RandomNumberGenerator s_Rng = RandomNumberGenerator.Create();
        public static string GenerateUUID6()
        {
            byte[] buf = new byte[3];
            s_Rng.GetBytes(buf);
            return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
        }

        // ===========================================================
        // 區塊職責：訊息檔名生成 — <SEQ:D8>.json (2026-07-27 改版, 取代舊版 <HHMMSS>_<MMM>_<UUID6>.json)
        // 物理意義：seq 現在由 UCL_ChatTavernWriteService 在 per-room lock 內先算好才傳進來
        //          (寫入時已知、保證同房間內不重複)，字典序 sort (zero-pad) = seq 數字序，
        //          比舊版「靠時間前綴排序」更直接——seq 本身就是排序依據，不必再繞時間。
        //          時間資訊仍完整保留在訊息內容的 ts 欄位 (SerializeMessageNoSeq 一律寫 ts)，
        //          檔名不需要重複帶時間。
        // 數值影響：只在「今天」這個資料夾內生效的日期 (2026-07-27 遷移) 之後，全房間統一走本格式；
        //          舊日期資料夾維持舊格式不動——兩者互不干擾, 因為跨日排序看的是日期資料夾名稱
        //          (yyyy-MM-dd) 而不是檔名, 檔名格式只在「同一天資料夾內」才需要互相可比。
        // 設計取捨 (Tim 2026-07-27 拍板)：拿掉 uuid6 — seq 已保證不重複, 不再需要隨機尾碼防撞檔。
        //          防撞檔機制從「換個隨機尾碼重試」改成「偵測到撞檔 = 快取跟磁碟真實狀態不同步的訊號,
        //          回頭問磁碟真相重新算 seq」(見 WriteMessageFileWithSeq 呼叫端 UCL_ChatTavernWriteService
        //          的 self-heal 邏輯), 比亂數重試更精確——直接找出「正確」的號碼而不是隨便挑一個沒撞到的。
        // ===========================================================
        public static string BuildMessageFileNameFromSeq(int seq)
        {
            return $"{seq:D8}.json";
        }

        public static string BuildEventFileName(DateTime utcTime, string uuid6, string eventType)
        {
            string safeType = string.IsNullOrEmpty(eventType) ? "event" : eventType.Replace("/", "_").Replace("\\", "_");
            return $"{utcTime:HHmmss_fff}_{uuid6}__{safeType}.json";
        }

        // ===========================================================
        // 區塊職責：寫一筆訊息為獨立 .json 檔，檔名直接用呼叫端已算好的 seq (取代舊版 WriteMessageFile)。
        // 物理意義：no atomic counter (仍然)、no jsonl append；單檔 atomic create-or-overwrite。
        // 數值影響：自動填 ts (含 ms) + uuid (仍寫進訊息內容, 給 reply_to_uuid 等功能用；只是不再
        //          出現在檔名裡) + _writer / _pid 簽章 + ensure date dir。
        // 回傳：(record, fullPath, wrote) — wrote=false 代表該 seq 對應的檔名已存在 (理論上不該發生,
        //       代表呼叫端快取跟磁碟真實檔案數不同步)。本函式不自己重試亂猜新號碼——正確作法是讓
        //       呼叫端 (UCL_ChatTavernWriteService) 重新問磁碟真相 (CountMessageFiles) 拿到正確 seq
        //       再呼叫一次本函式, 而不是在這裡隨便換個檔名蒙混過去。
        // 邊界：msg.ts 已填的話沿用（給 migrate 工具用）；否則 DateTime.UtcNow。
        //       msg.uuid 已填的話沿用；否則 GenerateUUID6（僅供內容識別 / reply 用，跟檔名無關）。
        // ===========================================================
        public static (UCL_ChatMessage record, string fullPath, bool wrote) WriteMessageFileWithSeq(string roomId, UCL_ChatMessage msg, int seq)
        {
            UCL_ChatTavernIO.EnsureRoomDir(roomId);

            // ts：preserve given (migrate) or 用 now
            DateTime utcTime;
            if (!string.IsNullOrEmpty(msg.ts) && DateTime.TryParse(
                msg.ts, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                utcTime = parsed;
            }
            else
            {
                utcTime = DateTime.UtcNow;
                msg.ts = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }

            // uuid：preserve given or generate — 仍寫進內容 (reply_to_uuid 等功能用)，只是不再進檔名
            if (string.IsNullOrEmpty(msg.uuid))
            {
                msg.uuid = GenerateUUID6();
            }

            // _writer / _pid 簽章
            if (msg.meta == null) msg.meta = new Dictionary<string, string>();
            msg.meta[WriterSignatureKey] = WriterSignatureValue;
            try
            {
                msg.meta[WriterPidKey] = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            }
            catch { /* sandbox 環境受限 */ }

            // 檔名 + 路徑（含 ensure date dir）— 檔名純用 seq，不再需要時間/uuid 尾碼
            string dateDir = GetMessagesDateDir(roomId, utcTime);
            Directory.CreateDirectory(dateDir);
            string filename = BuildMessageFileNameFromSeq(seq);
            string fullPath = Path.Combine(dateDir, filename);

            if (File.Exists(fullPath))
            {
                // 不在這裡亂猜新號碼——回報 wrote=false，讓呼叫端回頭問磁碟真相重新算 seq 後再試一次。
                return (msg, fullPath, false);
            }

            // serialize（不寫 seq 進 JSON 內容；seq 只活在檔名裡，reader 仍可從檔名或 position 兩種方式得到）
            string json = SerializeMessageNoSeq(msg);
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
            return (msg, fullPath, true);
        }

        // ===========================================================
        // 區塊職責：寫一筆 quest event 為獨立 .json 檔（取代既有 AppendEvent）
        // ===========================================================
        public static (UCL_QuestEvent record, string fullPath) WriteEventFile(string roomId, UCL_QuestEvent ev)
        {
            UCL_ChatTavernIO.EnsureRoomDir(roomId);

            DateTime utcTime;
            if (!string.IsNullOrEmpty(ev.ts) && DateTime.TryParse(
                ev.ts, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                utcTime = parsed;
            }
            else
            {
                utcTime = DateTime.UtcNow;
                ev.ts = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }

            // event 沒 uuid 欄位（既有 schema），但檔名仍需要 — 內部生成不寫進 record
            string uuid6 = GenerateUUID6();

            string dateDir = GetEventsDateDir(roomId, utcTime);
            Directory.CreateDirectory(dateDir);
            string filename = BuildEventFileName(utcTime, uuid6, ev.type);
            string fullPath = Path.Combine(dateDir, filename);

            int retry = 0;
            while (File.Exists(fullPath) && retry < 10)
            {
                uuid6 = GenerateUUID6();
                filename = BuildEventFileName(utcTime, uuid6, ev.type);
                fullPath = Path.Combine(dateDir, filename);
                retry++;
            }
            if (File.Exists(fullPath))
            {
                throw new IOException($"[Tavern T38] 寫 event file 失敗 — 10 次 retry 仍撞檔：{fullPath}");
            }

            string json = SerializeEventNoSeq(ev);
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
            return (ev, fullPath);
        }

        // ===========================================================
        // 區塊職責：LoadAllMessages parse cache（path-keyed, immutable-file 前提）
        // 物理意義：per-message 檔一旦寫出永不改寫（WriteMessageFile 只 create-or-throw, 從不 overwrite）
        //          → 舊檔 parse 結果可永久 cache, 按 full path 當 key 絕不失效.
        //          每次 LoadAllMessages 仍重跑 Directory.GetFiles 列「路徑」（便宜, 不讀內容）,
        //          只對沒見過的新檔 read+parse; 消失的檔（刪除 / 歸檔）從 cache 剔除.
        //          → freshness 不犧牲: 跨 process（Python）新寫的檔照樣被列舉命中.
        // 數值影響：LoadAllMessages 由 O(全部檔 read+parse) 降到 O(新增檔). 無變動時 0 次讀檔.
        //          直接救 BartenderDaemon 每 5s 全讀 8000+ 檔卡頓 + DeriveSeq 每寫一筆全讀.
        // 設計取捨：
        //   - 只快取 parse 結果（immutable）, 不快取「檔案清單」（每 call 重列 → 跨 process fresh）.
        //   - 壞檔記 badPaths, 不重複 read+parse（也不重複洗 error log）.
        //   - 回傳 List 每次 new, 但 message 物件共享 ref; seq 每 call 依當前檔序重算（deterministic）.
        //     caller 約定 read-only（既有 caller 皆是）— 勿原地改 message 物件, 否則污染 cache.
        //   - 全 caller 皆 Editor main thread（daemon update / page poll / cmd）; lock 純防禦.
        // ===========================================================
        sealed class RoomMsgCache
        {
            public readonly Dictionary<string, UCL_ChatMessage> byPath = new Dictionary<string, UCL_ChatMessage>();
            public readonly HashSet<string> badPaths = new HashSet<string>();
        }
        static readonly Dictionary<string, RoomMsgCache> s_RoomCache = new Dictionary<string, RoomMsgCache>();
        static readonly object s_CacheLock = new object();

        /// <summary>清空指定房間（roomId 為 null/空 → 全部）的 message parse cache.
        /// 手動修檔 / 測試 / 確知檔被原地改寫後才需要; 正常 append 不必呼叫（自動列舉新檔）.</summary>
        public static void InvalidateMessageCache(string roomId = null)
        {
            lock (s_CacheLock)
            {
                if (string.IsNullOrEmpty(roomId))
                {
                    s_RoomCache.Clear();
                    s_RoomFiles.Clear();
                }
                else
                {
                    s_RoomCache.Remove(roomId);
                    s_RoomFiles.Remove(roomId);
                }
            }
        }

        // ===========================================================
        // 區塊職責：排序後「路徑清單」快取 — 用目錄指紋判失效，取代每次 Directory.GetFiles 全房列舉
        // 物理意義：上面那份 parse cache 刻意「不快取檔案清單」（設計取捨第一條），因為要保跨 process 新鮮度。
        //          代價是每次呼叫都得列舉全房 —— tavern 房 14,242 檔實測 **32.4 ms**，而 mirror daemon 的
        //          CollectScanMessages 一個 tick 會倍增回頁呼叫 7 次 Tail → 每秒 ~227 ms 光在列目錄。
        //          本快取用「日期資料夾的 mtime 指紋」換掉全房列舉：71 個 dir 做 stat 實測 **0.86 ms**（38× 便宜），
        //          且**新鮮度不打折** —— 任一 dir 內新增/刪除檔都會推進該 dir 的 LastWriteTime。
        // 數值影響：指紋未變 → 直接回上次排好序的陣列（0 次列舉、0 次排序）；變了才重列重排。
        // 設計取捨：
        //   - 為何 stat 每個 date dir，不是「只看最新那個」：**git pull 會把訊息補進舊日期夾**
        //     （本 repo 的訊息走 git 同步，2026-08-01 現場驗證過）。只看最新夾會漏掉那批。
        //   - 為何連 root 自身 mtime 也算進去：涵蓋 date dir 的增刪、與直接落在 messages/ 下的檔。
        //   - 回傳的是 **cache 內的陣列本體**（不複製，省 alloc）→ caller 約定 read-only，勿排序或改元素。
        //     現有 caller（LoadAllMessages / Tail / LoadMessagesAfterSeq / CountMessageFiles）皆只讀。
        // ===========================================================
        sealed class RoomFileListCache
        {
            public string[] files;      // 已按 root-relative ordinal 排序
            public string signature;    // 目錄指紋
        }
        static readonly Dictionary<string, RoomFileListCache> s_RoomFiles = new Dictionary<string, RoomFileListCache>();

        static string BuildDirSignature(string root)
        {
            var sb = new StringBuilder();
            var dirs = Directory.GetDirectories(root);
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var d in dirs)
            {
                sb.Append(Path.GetFileName(d)).Append(':')
                  .Append(Directory.GetLastWriteTimeUtc(d).Ticks).Append('|');
            }
            sb.Append("root:").Append(Directory.GetLastWriteTimeUtc(root).Ticks);
            return sb.ToString();
        }

        /// <summary>取該房「排序後的全部訊息檔路徑」（目錄指紋命中則走快取）。回傳陣列為 cache 本體 —— 只讀，勿改。</summary>
        static string[] GetSortedMessageFiles(string roomId, string root)
        {
            string sig;
            try { sig = BuildDirSignature(root); }
            catch { sig = null; }   // 指紋算不出（權限/競態）→ 退回每次重列（安全側，只是慢）

            if (sig != null)
            {
                lock (s_CacheLock)
                {
                    if (s_RoomFiles.TryGetValue(roomId, out var hit) && hit.signature == sig) return hit.files;
                }
            }

            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            // ordinal sort = ts sort；先比 root-relative path（含 date dir 前綴）才能跨日正確。
            // 預算 sort key 一次（避免 comparison delegate 每次比較 new 2 個 substring 的 O(N log N) alloc）。
            var keys = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
                keys[i] = files[i].Substring(root.Length).Replace('\\', '/');
            Array.Sort(keys, files, StringComparer.Ordinal);

            if (sig != null)
            {
                lock (s_CacheLock)
                {
                    s_RoomFiles[roomId] = new RoomFileListCache { files = files, signature = sig };
                }
            }
            return files;
        }

        // ===========================================================
        // 區塊職責：讀整個房間的 messages — walk dir + ts sort + derive seq（path-keyed cache）
        // 物理意義：取代既有 LoadAllMessages
        // 數值影響：seq 動態算（enumerate 1..N, 只算成功 parse 的）；merge 後新訊息插中間 seq 會 shift
        // 邊界：messages/ 不存在 → 回空 list；壞 .json skip（記 badPaths 不重讀）
        // ===========================================================
        public static List<UCL_ChatMessage> LoadAllMessages(string roomId)
        {
            string root = GetMessagesRoot(roomId);
            var list = new List<UCL_ChatMessage>();
            if (!Directory.Exists(root)) return list;

            // 列「路徑」（便宜, 不讀內容）+ 排序 → 走目錄指紋快取（見 GetSortedMessageFiles）.
            // 指紋涵蓋每個 date dir 的 mtime, 跨 process（Python / git pull）新檔照樣看得到.
            string[] files = GetSortedMessageFiles(roomId, root);
            if (files.Length == 0) return list;

            lock (s_CacheLock)
            {
                if (!s_RoomCache.TryGetValue(roomId, out var cache))
                {
                    cache = new RoomMsgCache();
                    s_RoomCache[roomId] = cache;
                }

                // 剔除已消失的檔（刪除 / 歸檔）— 僅 cache 總數超過當前檔數時才掃, 純記憶體衛生.
                // (輸出正確性不依賴此步: 下方只從當前 files 建 list, stale entry 永不被回傳.)
                if (cache.byPath.Count + cache.badPaths.Count > files.Length)
                {
                    var present = new HashSet<string>(files);
                    foreach (var p in cache.byPath.Keys.Where(p => !present.Contains(p)).ToList())
                        cache.byPath.Remove(p);
                    cache.badPaths.RemoveWhere(p => !present.Contains(p));
                }

                int seq = 0;
                int rejected = 0;
                foreach (var f in files)
                {
                    if (cache.badPaths.Contains(f)) continue;   // 已知壞檔, 不重讀不重 log
                    if (!cache.byPath.TryGetValue(f, out var m))
                    {
                        // cache miss → 沒見過的新檔, read+parse 一次後存入
                        UCL_ChatMessage parsed;
                        try
                        {
                            string json = File.ReadAllText(f, Encoding.UTF8);
                            parsed = UCL_ChatTavernIO.ParseMessage(json);
                        }
                        catch (Exception ex)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] Skipping malformed message file {Path.GetFileName(f)}: {ex.Message}");
                            continue;
                        }
                        if (parsed == null)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] ParseMessage returned null for {Path.GetFileName(f)}");
                            continue;
                        }
                        cache.byPath[f] = parsed;
                        m = parsed;
                    }
                    m.seq = ++seq;   // seq 每 call 依當前檔序重算（只算成功 parse 的, 與舊行為一致）
                    list.Add(m);
                }
                if (rejected > 0)
                {
                    Debug.LogError($"[Tavern T38] LoadAllMessages({roomId}): {rejected} new files rejected this call; files={files.Length}, cached={cache.byPath.Count}, bad={cache.badPaths.Count}");
                }
            }
            return list;
        }

        public static List<UCL_QuestEvent> LoadAllEvents(string roomId)
        {
            string root = GetEventsRoot(roomId);
            var list = new List<UCL_QuestEvent>();
            if (!Directory.Exists(root)) return list;

            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, (a, b) =>
            {
                string ra = a.Substring(root.Length).Replace('\\', '/');
                string rb = b.Substring(root.Length).Replace('\\', '/');
                return string.CompareOrdinal(ra, rb);
            });

            int seq = 0;
            foreach (var f in files)
            {
                try
                {
                    string json = File.ReadAllText(f, Encoding.UTF8);
                    var e = UCL_ChatTavernQuestIO.ParseEvent(json);
                    if (e != null)
                    {
                        e.seq = ++seq;
                        list.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Tavern T38] Skipping malformed event file {f}: {ex.Message}");
                }
            }
            return list;
        }

        // ===========================================================
        // 區塊職責：只讀「尾端 n 筆」訊息 — 大房間 IMGUI page 卡頓主修
        // 物理意義：
        //   舊版 Tail = LoadAllMessages（read+parse 全部檔）再 GetRange 取尾，O(N) IO+parse。
        //   ChatTavernPage AutoPoll 每 2s 呼叫一次 → 房間幾千則時每 2 秒幾千次 File.ReadAllText
        //   + ParseMessage 在主執行緒 → 嚴重卡頓。
        //   本版只「列舉路徑」(Directory.GetFiles 不讀內容，比 read+parse 便宜 ~100×) + 排序，
        //   然後只 read+parse 尾端 n 筆。讀檔次數從 N 降到 min(N, n)。
        // 數值影響：
        //   - seq 用「檔案序位 (i+1, 1-based)」= 該訊息在完整時間序中的位置；
        //     無壞檔時與 LoadAllMessages 的 derive seq 完全一致（壞檔極罕見，且本就是錯誤路徑）。
        //   - 回傳筆數 == min(N, n)，保留 page「Count >= limit → 顯示載入更早」按鈕邏輯。
        // 排序優化：原 LoadAllMessages 的 comparison delegate 每次比較都 new 2 個 substring
        //   (O(N log N) alloc)；本版預算 root-relative key 一次 → Array.Sort(keys, files) 鍵排序。
        // ===========================================================
        public static List<UCL_ChatMessage> Tail(string roomId, int n)
        {
            var list = new List<UCL_ChatMessage>();
            if (n <= 0) return list;
            string root = GetMessagesRoot(roomId);
            if (!Directory.Exists(root)) return list;

            // 路徑列舉 + 排序走目錄指紋快取（2026-08-01）
            string[] files = GetSortedMessageFiles(roomId, root);
            if (files.Length == 0) return list;

            int total = files.Length;
            int start = total > n ? total - n : 0;   // 只讀尾端 n 筆（不足 n 則全讀）

            // 2026-08-01：本迴圈原本每次都 File.ReadAllText + ParseMessage，**完全繞過** 上方那份
            // path-keyed parse cache（那份只有 LoadAllMessages 在用）。mirror daemon 一秒倍增回頁七次
            // → 每秒 ~3810 次 read+parse 在主執行緒上 = 可見卡頓。改走同一份 cache 後穩態只 parse 新檔。
            // 共用 cache 也表示：同房若已被 LoadAllMessages 讀過，這裡的增量記憶體是零（同一個 dictionary）。
            int rejected = 0;
            lock (s_CacheLock)
            {
                if (!s_RoomCache.TryGetValue(roomId, out var cache))
                {
                    cache = new RoomMsgCache();
                    s_RoomCache[roomId] = cache;
                }
                for (int i = start; i < total; i++)
                {
                    string f = files[i];
                    if (cache.badPaths.Contains(f)) continue;   // 已知壞檔, 不重讀不重洗 error log
                    if (!cache.byPath.TryGetValue(f, out var m))
                    {
                        try
                        {
                            string json = File.ReadAllText(f, Encoding.UTF8);
                            m = UCL_ChatTavernIO.ParseMessage(json);
                        }
                        catch (Exception ex)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] Tail skipping malformed message file {Path.GetFileName(f)}: {ex.Message}");
                            continue;
                        }
                        if (m == null)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] Tail ParseMessage returned null for {Path.GetFileName(f)}");
                            continue;
                        }
                        cache.byPath[f] = m;
                    }
                    m.seq = i + 1;   // 絕對序位（1-based），與 LoadAllMessages enumerate 順序一致
                    list.Add(m);
                }
            }
            if (rejected > 0)
            {
                Debug.LogError($"[Tavern T38] Tail({roomId}, {n}): {rejected} files rejected in tail slice");
            }
            return list;
        }

        // ===========================================================
        // 區塊職責：只「數」房間訊息檔數 — 純列路徑, 完全不 read+parse
        // 物理意義：給只需 count 不需內容的 caller（e.g. daemon 游標初始化）用,
        //          避免為了拿一個數字 cold-parse 整房 8000+ 檔.
        // 數值影響：O(列舉) — 不讀任何檔內容, 不碰 cache.
        // 邊界：messages/ 不存在 → 0. 數的是「檔數」(含潛在壞檔), 與 LoadAllMessages
        //       回傳的「成功 parse 數」在無壞檔時相等(壞檔極罕見).
        // ===========================================================
        public static int CountMessageFiles(string roomId)
        {
            string root = GetMessagesRoot(roomId);
            if (!Directory.Exists(root)) return 0;
            return GetSortedMessageFiles(roomId, root).Length;   // 走目錄指紋快取（2026-08-01）
        }

        // ===========================================================
        // 區塊職責：只讀「檔序位 > afterSeq 的訊息」— 增量游標讀取（cache-aware）
        // 物理意義：daemon CheckKeywordTriggers / CheckWorkSessionStart 只關心游標後的新訊息,
        //          舊訊息讀了也丟. 本 helper 只 read+parse 尾段 files[afterSeq..], 永不 cold-parse 歷史.
        //          → 即使 domain reload 後 cache 冷, 第一 tick 也只 parse「自游標起的新檔」(通常 0~少數).
        // 數值影響：seq = 檔序位 (i+1, 1-based), 與 Tail 一致; 共用 LoadAllMessages 同一 path-keyed cache.
        //   - 與舊 LoadAllMessages 的「成功-parse 序位」在無壞檔時完全一致(壞檔極罕見且本就是錯誤路徑).
        //   - caller 約定 read-only(勿原地改 message 物件, 否則污染 cache).
        // 邊界：afterSeq < 0 視為 0(全讀); afterSeq >= 檔數 → 回空(無新訊息).
        // ===========================================================
        public static List<UCL_ChatMessage> LoadMessagesAfterSeq(string roomId, int afterSeq)
        {
            var list = new List<UCL_ChatMessage>();
            string root = GetMessagesRoot(roomId);
            if (!Directory.Exists(root)) return list;

            // 路徑列舉 + 排序走目錄指紋快取（2026-08-01）
            string[] files = GetSortedMessageFiles(roomId, root);
            if (files.Length == 0) return list;

            int start = afterSeq < 0 ? 0 : afterSeq;
            if (start >= files.Length) return list;   // 無新訊息

            lock (s_CacheLock)
            {
                if (!s_RoomCache.TryGetValue(roomId, out var cache))
                {
                    cache = new RoomMsgCache();
                    s_RoomCache[roomId] = cache;
                }
                int rejected = 0;
                for (int i = start; i < files.Length; i++)
                {
                    string f = files[i];
                    if (cache.badPaths.Contains(f)) continue;
                    if (!cache.byPath.TryGetValue(f, out var m))
                    {
                        UCL_ChatMessage parsed;
                        try
                        {
                            parsed = UCL_ChatTavernIO.ParseMessage(File.ReadAllText(f, Encoding.UTF8));
                        }
                        catch (Exception ex)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] LoadMessagesAfterSeq skip malformed {Path.GetFileName(f)}: {ex.Message}");
                            continue;
                        }
                        if (parsed == null)
                        {
                            cache.badPaths.Add(f);
                            rejected++;
                            Debug.LogError($"[Tavern T38] LoadMessagesAfterSeq ParseMessage null for {Path.GetFileName(f)}");
                            continue;
                        }
                        cache.byPath[f] = parsed;
                        m = parsed;
                    }
                    m.seq = i + 1;   // 檔序位(1-based), 與 Tail / LoadAllMessages(無壞檔時)一致
                    list.Add(m);
                }
                if (rejected > 0)
                    Debug.LogError($"[Tavern T38] LoadMessagesAfterSeq({roomId}, {afterSeq}): {rejected} files rejected in tail slice");
            }
            return list;
        }

        // ===========================================================
        // 區塊職責：用 since_ts 取代 since_seq
        // 物理意義：對 wait / read 等 op 提供時間基準的「自此之後的訊息」查詢
        // 數值影響：純 walk + 比 ts string（ISO 8601 字典序 = 時間序）
        // ===========================================================
        public static List<UCL_ChatMessage> LoadMessagesSinceTs(string roomId, string sinceTs)
        {
            var all = LoadAllMessages(roomId);
            if (string.IsNullOrEmpty(sinceTs)) return all;
            // ISO 8601 ASCII 字典序 = 時間序（"2026-05-09T08:47:52.312Z" 比 string）
            return all.Where(m => string.CompareOrdinal(m.ts, sinceTs) > 0).ToList();
        }

        public static int CountMessagesSinceTs(string roomId, string sinceTs)
        {
            // 比 LoadMessagesSinceTs 輕：只 walk 計數不 parse
            string root = GetMessagesRoot(roomId);
            if (!Directory.Exists(root)) return 0;
            // 把 sinceTs 轉成「對應 filename prefix」比 — 例如 "2026-05-09T08:47:52.312Z"
            // 對 root-relative path 比就好（因為 dir 結構是 yyyy-MM-dd/HHmmss_fff_uuid.json）
            // 但簡單起見直接 LoadAllMessages 再 filter
            return LoadMessagesSinceTs(roomId, sinceTs).Count;
        }

        // ===========================================================
        // 區塊職責：訊息 JSON 序列化 — 不寫 seq（per T38 設計）
        // 物理意義：seq 是 reader derive 不能寫進檔；其他欄位跟既有 SerializeMessage 一致
        // 數值影響：呼叫 UCL_ChatTavernIO.SerializeMessage 後手動把 "seq":N 拿掉
        //          （簡化：直接複製 SerializeMessage 邏輯但跳過 seq）
        // ===========================================================
        public static string SerializeMessageNoSeq(UCL_ChatMessage m)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            // 不寫 seq！
            bool first = true;
            void Comma() { if (!first) sb.Append(","); first = false; }

            Comma(); sb.Append("\"ts\":\"").Append(EscapeStr(m.ts)).Append("\"");
            if (!string.IsNullOrEmpty(m.uuid))
            {
                Comma(); sb.Append("\"uuid\":\"").Append(EscapeStr(m.uuid)).Append("\"");
            }
            Comma(); sb.Append("\"sender_id\":\"").Append(EscapeStr(m.sender_id)).Append("\"");
            Comma(); sb.Append("\"sender_name\":\"").Append(EscapeStr(m.sender_name)).Append("\"");
            // Phase 1 (Tim 2026-05-11 拍板) — sender_persona only emit when 非空, 維持 backward compat (legacy 訊息無此欄位)
            if (!string.IsNullOrEmpty(m.sender_persona))
            {
                Comma(); sb.Append("\"sender_persona\":\"").Append(EscapeStr(m.sender_persona)).Append("\"");
            }
            // T28 (Tim 2026-05-14) — sender_avatar_sprite 條件式 emit (per-msg writer)
            if (!string.IsNullOrEmpty(m.sender_avatar_sprite))
            {
                Comma(); sb.Append("\"sender_avatar_sprite\":\"").Append(EscapeStr(m.sender_avatar_sprite)).Append("\"");
            }
            Comma(); sb.Append("\"kind\":\"").Append(EscapeStr(m.kind ?? "chat")).Append("\"");
            Comma(); sb.Append("\"body\":\"").Append(EscapeStr(m.body ?? "")).Append("\"");
            if (m.reply_to.HasValue)
            {
                Comma(); sb.Append("\"reply_to\":").Append(m.reply_to.Value);
            }
            if (!string.IsNullOrEmpty(m.reply_to_uuid))
            {
                Comma(); sb.Append("\"reply_to_uuid\":\"").Append(EscapeStr(m.reply_to_uuid)).Append("\"");
            }
            if (m.meta != null && m.meta.Count > 0)
            {
                Comma(); sb.Append("\"meta\":{");
                bool firstMeta = true;
                foreach (var kv in m.meta)
                {
                    if (!firstMeta) sb.Append(",");
                    firstMeta = false;
                    sb.Append("\"").Append(EscapeStr(kv.Key)).Append("\":\"").Append(EscapeStr(kv.Value)).Append("\"");
                }
                sb.Append("}");
            }
            if (m.refs != null && m.refs.Count > 0)
            {
                Comma(); sb.Append("\"refs\":[");
                bool firstRef = true;
                foreach (var r in m.refs)
                {
                    if (!firstRef) sb.Append(",");
                    firstRef = false;
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

        public static string SerializeEventNoSeq(UCL_QuestEvent e)
        {
            // 直接呼叫既有 SerializeEvent 然後拿掉 seq 欄位
            // 簡化：用既有 SerializeEvent，然後 string replace
            string raw = UCL_ChatTavernQuestIO.SerializeEvent(e);
            // raw 開頭格式："{\"seq\":N,\"ts\":..."
            // 拿掉 "seq":N, 部分（regex / manual scan）
            int seqIdx = raw.IndexOf("\"seq\":", StringComparison.Ordinal);
            if (seqIdx < 0) return raw;
            int colon = raw.IndexOf(':', seqIdx);
            int comma = raw.IndexOf(',', colon);
            if (comma < 0) return raw;
            // remove "seq":N, ；保留前綴 "{" 之後從 comma+1 起
            string before = raw.Substring(0, seqIdx);                    // "{"
            string after = raw.Substring(comma + 1);                     // "\"ts\":..."
            return before + after;
        }

        // 既有 EscapeStr 私有；複製一份簡化版
        static string EscapeStr(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
