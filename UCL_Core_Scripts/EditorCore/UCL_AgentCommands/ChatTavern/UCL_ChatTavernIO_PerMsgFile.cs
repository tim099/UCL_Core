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
        // 區塊職責：訊息檔名生成 — <HHMMSS>_<MMM>_<UUID6>.json
        // 物理意義：字典序 sort = 時間 sort；prefix HHMMSS_MMM 確保跨檔可比
        // 數值影響：呼叫方需先 mkdir 對應 yyyy-MM-dd 目錄；本函式只回 filename
        // ===========================================================
        public static string BuildMessageFileName(DateTime utcTime, string uuid6)
        {
            return $"{utcTime:HHmmss_fff}_{uuid6}.json";
        }

        public static string BuildEventFileName(DateTime utcTime, string uuid6, string eventType)
        {
            string safeType = string.IsNullOrEmpty(eventType) ? "event" : eventType.Replace("/", "_").Replace("\\", "_");
            return $"{utcTime:HHmmss_fff}_{uuid6}__{safeType}.json";
        }

        // ===========================================================
        // 區塊職責：寫一筆訊息為獨立 .json 檔（取代既有 AppendMessage）
        // 物理意義：no atomic counter、no jsonl append；單檔 atomic create-or-overwrite
        // 數值影響：自動填 ts (含 ms) + uuid + _writer / _pid 簽章 + ensure date dir
        //          回傳 (record, fullPath) 供 caller 後續 derive seq 或 mirror 用
        // 邊界：msg.ts 已填的話沿用（給 migrate 工具用）；否則 DateTime.UtcNow
        //       msg.uuid 已填的話沿用；否則 GenerateUUID6
        //       同 ms + 同 uuid（migrate 罕見）→ 檔名撞 → File.Exists 防呆 + retry uuid
        // ===========================================================
        public static (UCL_ChatMessage record, string fullPath) WriteMessageFile(string roomId, UCL_ChatMessage msg)
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

            // uuid：preserve given or generate
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

            // 檔名 + 路徑（含 ensure date dir）
            string dateDir = GetMessagesDateDir(roomId, utcTime);
            Directory.CreateDirectory(dateDir);
            string filename = BuildMessageFileName(utcTime, msg.uuid);
            string fullPath = Path.Combine(dateDir, filename);

            // 防撞檔（同 ms 同 uuid 極罕見；migrate 多筆同 ts 才可能）
            int retry = 0;
            while (File.Exists(fullPath) && retry < 10)
            {
                msg.uuid = GenerateUUID6();
                filename = BuildMessageFileName(utcTime, msg.uuid);
                fullPath = Path.Combine(dateDir, filename);
                retry++;
            }
            if (File.Exists(fullPath))
            {
                throw new IOException($"[Tavern T38] 寫 message file 失敗 — 10 次 retry 仍撞檔：{fullPath}");
            }

            // serialize（不寫 seq；seq 是 reader derive）
            string json = SerializeMessageNoSeq(msg);
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
            return (msg, fullPath);
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
        // 區塊職責：讀整個房間的 messages — walk dir + ts sort + derive seq
        // 物理意義：取代既有 LoadAllMessages
        // 數值影響：seq 動態算（enumerate 1..N）；merge 後新訊息插中間 seq 會 shift
        // 邊界：messages/ 不存在 → 回空 list；壞 .json silent skip
        // ===========================================================
        public static List<UCL_ChatMessage> LoadAllMessages(string roomId)
        {
            string root = GetMessagesRoot(roomId);
            var list = new List<UCL_ChatMessage>();
            if (!Directory.Exists(root)) return list;

            // walk 全部 .json 檔（含 date sub-dirs）
            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            // ordinal sort = ts sort（per filename convention）
            // 先比相對 root 的 path（含 date dir 前綴）才能跨日 sort 正確
            Array.Sort(files, (a, b) =>
            {
                string ra = a.Substring(root.Length).Replace('\\', '/');
                string rb = b.Substring(root.Length).Replace('\\', '/');
                return string.CompareOrdinal(ra, rb);
            });

            int seq = 0;
            int rejected = 0;
            foreach (var f in files)
            {
                try
                {
                    string json = File.ReadAllText(f, Encoding.UTF8);
                    var m = UCL_ChatTavernIO.ParseMessage(json);
                    if (m != null)
                    {
                        m.seq = ++seq;
                        list.Add(m);
                    }
                    else
                    {
                        rejected++;
                        Debug.LogError($"[Tavern T38] ParseMessage returned null for {Path.GetFileName(f)}");
                    }
                }
                catch (Exception ex)
                {
                    rejected++;
                    Debug.LogError($"[Tavern T38] Skipping malformed message file {Path.GetFileName(f)}: {ex.Message}");
                }
            }
            if (rejected > 0)
            {
                Debug.LogError($"[Tavern T38] LoadAllMessages({roomId}): {rejected} files rejected out of {files.Length} total");
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

        public static List<UCL_ChatMessage> Tail(string roomId, int n)
        {
            var all = LoadAllMessages(roomId);
            if (all.Count <= n) return all;
            return all.GetRange(all.Count - n, n);
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
