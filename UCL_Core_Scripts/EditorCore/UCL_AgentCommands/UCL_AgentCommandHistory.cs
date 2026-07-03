
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/07 2026
// Agent Commands — History 子系統 (Page 之外的管理層)
// 設計目標：
//   1. 每筆歷史指令以「獨立 JSON 檔」保存於 AgentCommands/History/<Id>.json，
//      讓 agent 可以直接 ls 資料夾、grep 內容當作參考；單檔損毀也只壞一筆。
//   2. 提供搜尋（Type / Description / Args 全文比對）、刪除過舊、刪除重複。
//   3. 完全與 Page UI 解耦 —— UI 層只負責呼叫這裡的 API + 渲染。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：歷史條目資料模型
    // 物理意義：使用者每次「Add 到 queue」就會記錄一筆 → 即使指令在 queue 中被 Remove / 執行完成，
    //          這份歷史仍保留，供未來搜尋重用。
    // 數值影響：純資料容器，不操作任何 IO。
    // ===========================================================
    /// <summary>
    /// 一筆 Agent Command 歷史條目（對應 History/&lt;Id&gt;.json 內單檔資料）。
    /// </summary>
    [Serializable]
    public class UCL_AgentCommandHistoryEntry
    {
        /// <summary>唯一 ID，等同檔名（不含副檔名）。建議格式：yyyyMMdd-HHmmss-&lt;type&gt;-&lt;短雜湊&gt;</summary>
        public string Id;
        /// <summary>原始 Command Type（對應 UCL_AgentCommandRegistry 的 key）</summary>
        public string Type;
        /// <summary>OneShot / Repeatable</summary>
        public UCL_AgentCommandMode Mode = UCL_AgentCommandMode.OneShot;
        /// <summary>當時送進 queue 的 args（key/value 字串對）</summary>
        public Dictionary<string, string> Args = new();
        /// <summary>當時填入的描述（人類筆記）</summary>
        public string Description;
        /// <summary>記錄時間（ISO 8601）</summary>
        public string CreatedAt;
        /// <summary>來源標籤："Manual" / "Template:&lt;name&gt;" / "History:&lt;id&gt;"</summary>
        public string Source;
        /// <summary>使用次數（每次相同簽章被 Re-Add 時 +1，DeleteDuplicates 用此判斷保留哪一筆）</summary>
        public int UseCount = 1;
        /// <summary>最近一次重用的時間（ISO 8601；首次建立 = CreatedAt）</summary>
        public string LastUsedAt;
    }

    /// <summary>
    /// History 的讀寫管理。<br/>
    /// 路徑：&lt;repoRoot&gt;/AgentCommands/History/&lt;Id&gt;.json（一檔一筆）<br/>
    /// 與 queue.json 完全分離，互不影響。
    /// </summary>
    public static class UCL_AgentCommandHistory
    {
        public const string HistoryDirRelative = "History";

        // ===========================================================
        // 區塊職責：路徑解析
        // 物理意義：歷史資料夾位於 AgentCommands/History；首次寫入時自動建立。
        // 數值影響：純路徑運算 + 必要時 mkdir。
        // ===========================================================

        /// <summary>取得 History 資料夾的絕對路徑（不保證存在）。</summary>
        public static string GetHistoryDir()
        {
            string queueDir = UCL_AgentCommandQueue.GetQueueDir();
            return Path.Combine(queueDir, HistoryDirRelative);
        }

        /// <summary>取得指定 Id 對應的歷史檔絕對路徑。</summary>
        public static string GetEntryPath(string id)
        {
            return Path.Combine(GetHistoryDir(), SanitizeFileName(id) + ".json");
        }

        /// <summary>確保 History 資料夾存在。</summary>
        public static void EnsureDir()
        {
            string dir = GetHistoryDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // ===========================================================
        // 區塊職責：寫入新條目 / 重用既有條目
        // 物理意義：以 Type + Args 簽章判定是否為相同指令；
        //          若同簽章已存在，僅更新 LastUsedAt 與 UseCount，不另建新檔，
        //          這樣搜尋結果不會被同一條指令灌爆。
        // 數值影響：寫入 1 個 .json 檔（新增或覆寫既有同簽章檔）。
        // ===========================================================
        /// <summary>
        /// 把一筆 Add-to-queue 的指令記錄進 History。
        /// 同 Type + 同 Args 簽章視為「重用」 → 不寫新檔，只更新計數。
        /// </summary>
        /// <param name="type">Command Type（同 UCL_AgentCommand.Type）</param>
        /// <param name="mode">OneShot / Repeatable</param>
        /// <param name="args">Args dictionary（null 視為空）</param>
        /// <param name="description">人類描述（可為空）</param>
        /// <param name="source">來源標籤（如 "Manual" / "Template:foo"）</param>
        public static UCL_AgentCommandHistoryEntry Record(
            string type,
            UCL_AgentCommandMode mode,
            Dictionary<string, string> args,
            string description,
            string source)
        {
            if (string.IsNullOrEmpty(type)) return null;
            EnsureDir();

            string signature = ComputeSignature(type, mode, args);
            var existing = LoadAll().FirstOrDefault(e => ComputeSignature(e.Type, e.Mode, e.Args) == signature);
            string nowIso = DateTime.UtcNow.ToString("o");

            if (existing != null)
            {
                // 區塊職責：重用既有條目 — first-source-wins，不覆寫 Source 欄位
                // 物理意義：使用者從 UI Add（Source=Manual）後 Runner 又跑了一次（Source=Agent），
                //          若 Source 被覆寫成 Agent，使用者就無法分辨「這是我手動加的還是 agent 灌進來的」。
                //          首次記到的 Source = 真正的來源；後續重用只 bump UseCount + LastUsedAt。
                // 數值影響：existing.Source 維持原值；UseCount +1；LastUsedAt 更新為現在
                existing.UseCount += 1;
                existing.LastUsedAt = nowIso;
                if (!string.IsNullOrEmpty(description)) existing.Description = description;
                WriteEntry(existing);
                return existing;
            }

            var entry = new UCL_AgentCommandHistoryEntry
            {
                Id = MakeId(type),
                Type = type,
                Mode = mode,
                Args = args != null ? new Dictionary<string, string>(args) : new Dictionary<string, string>(),
                Description = description,
                CreatedAt = nowIso,
                LastUsedAt = nowIso,
                Source = string.IsNullOrEmpty(source) ? "Manual" : source,
                UseCount = 1,
            };
            WriteEntry(entry);
            return entry;
        }

        // ===========================================================
        // 區塊職責：讀取整個 History 資料夾
        // 物理意義：每次呼叫實打實掃資料夾 + 解析每個 .json，避免快取失準。
        // 數值影響：純 IO，依資料夾大小可能慢；UI 呼叫者請自行做 throttle。
        // ===========================================================
        /// <summary>
        /// 載入所有歷史條目。順序：LastUsedAt 由新到舊。
        /// 解析失敗的單檔會被忽略並印出警告，不會中斷整批讀取。
        /// </summary>
        public static List<UCL_AgentCommandHistoryEntry> LoadAll()
        {
            var list = new List<UCL_AgentCommandHistoryEntry>();
            string dir = GetHistoryDir();
            if (!Directory.Exists(dir)) return list;

            foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var e = ParseEntry(json);
                    if (e != null) list.Add(e);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UCL_AgentCommandHistory] Skip corrupted entry: {path} — {ex.Message}");
                }
            }
            // 區塊職責：依「最後使用時間」由新到舊排序
            // 物理意義：使用者最常想看的是「我剛剛用過的指令」，因此 LastUsedAt 比 CreatedAt 更實用。
            // 數值影響：不修改任何條目，只決定回傳順序。
            list.Sort((a, b) => string.Compare(b.LastUsedAt ?? "", a.LastUsedAt ?? "", StringComparison.Ordinal));
            return list;
        }

        /// <summary>
        /// 對歷史條目做關鍵字搜尋（不分大小寫）。<br/>
        /// 比對欄位：Type / Description / Args 的 key 與 value / Source。<br/>
        /// keyword 為空 → 回傳全部（等同 LoadAll）。
        /// </summary>
        public static List<UCL_AgentCommandHistoryEntry> Search(string keyword)
        {
            var all = LoadAll();
            if (string.IsNullOrWhiteSpace(keyword)) return all;
            string k = keyword.Trim().ToLowerInvariant();
            return all.Where(e => MatchesKeyword(e, k)).ToList();
        }

        static bool MatchesKeyword(UCL_AgentCommandHistoryEntry e, string k)
        {
            if (e == null) return false;
            if (Contains(e.Type, k)) return true;
            if (Contains(e.Description, k)) return true;
            if (Contains(e.Source, k)) return true;
            if (e.Args != null)
            {
                foreach (var kv in e.Args)
                {
                    if (Contains(kv.Key, k)) return true;
                    if (Contains(kv.Value, k)) return true;
                }
            }
            return false;
        }

        static bool Contains(string s, string lowerKeyword)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.ToLowerInvariant().Contains(lowerKeyword);
        }

        // ===========================================================
        // 區塊職責：刪除單筆 / 全部 / 過舊 / 重複
        // 物理意義：使用者要求「可以刪除過舊、重複性太高的緩存指令」 — 這裡是該需求的實作面。
        // 數值影響：直接刪 .json 檔，不可逆；呼叫前 UI 可加二次確認。
        // ===========================================================

        /// <summary>刪除指定 Id 的歷史條目。回傳是否成功刪除。</summary>
        public static bool Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string path = GetEntryPath(id);
            if (!File.Exists(path)) return false;
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandHistory] Failed to delete {path}: {e.Message}");
                return false;
            }
        }

        /// <summary>清空整個 History 資料夾下的 .json（不刪資料夾本身）。回傳實際刪除筆數。</summary>
        public static int Clear()
        {
            string dir = GetHistoryDir();
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try { File.Delete(path); n++; } catch { /* 忽略單檔失敗，盡量清乾淨 */ }
            }
            return n;
        }

        /// <summary>
        /// 刪除最後使用時間早於指定 cutoff 的條目（單位：天）。<br/>
        /// 例：DeleteOlderThan(30) → 刪除 30 天沒被重用過的歷史。
        /// </summary>
        /// <returns>實際刪除筆數</returns>
        public static int DeleteOlderThan(int olderThanDays)
        {
            if (olderThanDays <= 0) return 0;
            DateTime cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            int n = 0;
            foreach (var e in LoadAll())
            {
                if (!DateTime.TryParse(e.LastUsedAt ?? e.CreatedAt, out var t)) continue;
                if (t.ToUniversalTime() < cutoff)
                {
                    if (Delete(e.Id)) n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 刪除「同簽章但被分成多檔」的多餘條目，每個簽章只保留 LastUsedAt 最新的一筆。<br/>
        /// 注意：Record() 已會自動合併同簽章，因此通常不會有重複；此方法是給「外部 / 舊資料 / 手寫檔」做後處理用的。
        /// </summary>
        /// <returns>實際刪除筆數</returns>
        public static int DeleteDuplicates()
        {
            var all = LoadAll();
            var grouped = all
                .GroupBy(e => ComputeSignature(e.Type, e.Mode, e.Args))
                .Where(g => g.Count() > 1);
            int n = 0;
            foreach (var g in grouped)
            {
                // 已按 LastUsedAt 由新到舊排序 → 第 0 筆保留，其餘刪除
                bool first = true;
                foreach (var e in g)
                {
                    if (first) { first = false; continue; }
                    if (Delete(e.Id)) n++;
                }
            }
            return n;
        }

        // ===========================================================
        // 區塊職責：簽章 / 檔名 / Id 產生器
        // 物理意義：簽章決定「兩筆指令是否視為相同」，檔名安全化避免 Windows 非法字元。
        // 數值影響：純運算、無 IO。
        // ===========================================================

        /// <summary>
        /// 計算指令簽章：Type|Mode|sortedArgs；用於判斷重複。
        /// </summary>
        public static string ComputeSignature(string type, UCL_AgentCommandMode mode, Dictionary<string, string> args)
        {
            var sb = new StringBuilder();
            sb.Append(type ?? "").Append('|').Append(mode);
            if (args != null && args.Count > 0)
            {
                foreach (var kv in args.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value ?? "");
                }
            }
            return sb.ToString();
        }

        static string MakeId(string type)
        {
            // 區塊職責：建立全域唯一檔名
            // 物理意義：時間戳精確到毫秒 + GetHashCode 後綴，避免同一秒內連續 Record 撞名。
            // 數值影響：純字串組合。
            string ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            // 修復（2026-07-03）：原 Substring(0, Math.Min(6, 8)) 恆取 6 字，但 ToString("x") 無前導零、
            //   長度 1~8 不定，當 hash 值 < 0x100000（hex 少於 6 位）時越界拋 ArgumentOutOfRangeException，
            //   約 5% 指令因 Record 先於 handler 執行而整筆靜默失敗。改成取實際長度與 6 的較小值。
            string hex = Math.Abs(Guid.NewGuid().GetHashCode()).ToString("x");
            string suffix = hex.Substring(0, Math.Min(6, hex.Length));
            return $"{ts}-{(string.IsNullOrEmpty(type) ? "cmd" : type.ToLowerInvariant())}-{suffix}";
        }

        static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：JSON 序列化 / 反序列化（單檔單筆）
        // 物理意義：與 UCL_AgentCommandQueue 風格一致 —— 自寫 parser 因為 Unity JsonUtility 不支援 Dictionary。
        // 數值影響：寫入失敗會印 LogError 但不 throw，讓 UI 不卡死。
        // ===========================================================

        static void WriteEntry(UCL_AgentCommandHistoryEntry e)
        {
            EnsureDir();
            string path = GetEntryPath(e.Id);
            try
            {
                File.WriteAllText(path, SerializeEntry(e), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandHistory] Failed to write {path}: {ex}");
            }
        }

        static string SerializeEntry(UCL_AgentCommandHistoryEntry e)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStr(sb, "Id", e.Id, true);
            AppendStr(sb, "Type", e.Type, false);
            AppendStr(sb, "Mode", e.Mode.ToString(), false);
            sb.Append(",\n  \"Args\": {");
            if (e.Args != null && e.Args.Count > 0)
            {
                bool first = true;
                foreach (var kv in e.Args)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\n    \"").Append(EscapeStr(kv.Key)).Append("\": \"").Append(EscapeStr(kv.Value)).Append("\"");
                }
                sb.Append("\n  ");
            }
            sb.Append("}");
            AppendStr(sb, "Description", e.Description, false);
            AppendStr(sb, "CreatedAt", e.CreatedAt, false);
            AppendStr(sb, "LastUsedAt", e.LastUsedAt, false);
            AppendStr(sb, "Source", e.Source, false);
            sb.Append(",\n  \"UseCount\": ").Append(e.UseCount);
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static void AppendStr(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\n  \"").Append(key).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append("\"").Append(EscapeStr(value)).Append("\"");
        }

        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ---- 簡易 parser（與 UCL_AgentCommandQueue 同風格，避免相依） ----

        static UCL_AgentCommandHistoryEntry ParseEntry(string json)
        {
            int pos = 0;
            SkipWS(json, ref pos);
            ExpectChar(json, ref pos, '{');
            var e = new UCL_AgentCommandHistoryEntry();
            while (true)
            {
                SkipWS(json, ref pos);
                if (pos >= json.Length || json[pos] == '}') { if (pos < json.Length) pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                switch (key)
                {
                    case "Id":          e.Id = ParseStringOrNull(json, ref pos); break;
                    case "Type":        e.Type = ParseStringOrNull(json, ref pos); break;
                    case "Mode":
                        {
                            string s = ParseStringOrNull(json, ref pos);
                            if (Enum.TryParse<UCL_AgentCommandMode>(s, out var m)) e.Mode = m;
                            break;
                        }
                    case "Args":        e.Args = ParseStringDict(json, ref pos); break;
                    case "Description": e.Description = ParseStringOrNull(json, ref pos); break;
                    case "CreatedAt":   e.CreatedAt = ParseStringOrNull(json, ref pos); break;
                    case "LastUsedAt":  e.LastUsedAt = ParseStringOrNull(json, ref pos); break;
                    case "Source":      e.Source = ParseStringOrNull(json, ref pos); break;
                    case "UseCount":    e.UseCount = ParseInt(json, ref pos); break;
                    default:            SkipValue(json, ref pos); break;
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            // 兼容性：舊檔可能沒有 LastUsedAt → fallback 為 CreatedAt
            if (string.IsNullOrEmpty(e.LastUsedAt)) e.LastUsedAt = e.CreatedAt;
            return e;
        }

        static Dictionary<string, string> ParseStringDict(string json, ref int pos)
        {
            var d = new Dictionary<string, string>();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
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
                throw new Exception($"Expected '{ch}' at pos {pos}");
            pos++;
        }
    }
}
#endif
