// 區塊職責：訊息檔清單的**落盤索引** — 讓冷啟動不必列舉整房的訊息檔。
// 物理意義：檔名 migration 之後 `seq == 檔名`，而每個日期目錄裝的是一段**連續**的 seq。
//          於是「排序後的完整路徑清單」可以由一張每日範圍表**算出來**，不必列舉：
//              路徑 = <messages>/<date>/<seq:00000000>.json
//          索引一天一行，所以它的大小跟**天數**成正比，不是跟訊息數成正比 ——
//          一百萬則訊息時它仍然只有幾百行。這是本設計的全部價值所在。
// 數值影響：純加速層。任何一致性檢查不過就退回全量列舉（慢但正確），永不給錯清單。
//
// 為什麼需要它（2026-08-06 Tim：「卡頓發生在專案重開時」）：
//   `GetSortedMessageFiles` 的記憶體快取是 static 欄位，**domain reload 就整份沒了**
//   （而 domain reload 每次編譯都發生，不只重開專案）。冷啟動因此每房各付一次
//   `Directory.GetFiles(AllDirectories)` + 建 N 個 substring key + Array.Sort。
//   實測（檔案系統快取已熱）：tavern 一房 10,300 檔 21.4ms、52 房合計 28.5ms。
//   ⚠ 那是**熱**的數字；真冷啟只會更慢，倍數未知 —— 所以本檔解的是「與訊息量成正比」
//   這個性質，不是那個特定的毫秒數。
//
// 一致性怎麼保證（照 UCL_TreasuryLedger 的 watermark/snapshot 形狀）：
//   索引記每個日期目錄的 (起始 seq, 檔數, 目錄 mtime)。載入時**只 stat 目錄**（60 個約 1ms）：
//     · mtime 相符 → 該日內容沒動過 ⇒ 直接算出路徑，不列舉
//     · mtime 不符 / 不在索引 → **只列舉那一天**
//   最後再驗一次全域 seq 連續性；有斷點就整份丟掉走全量列舉並重建索引。
//   > **把快取失效降級成「變慢」，而不是「算錯」** —— 這裡算錯的後果很具體：
//   > 清單少一筆 → seq 全體位移 → 所有游標指到錯的訊息，而外觀完全正常。
//
// 索引放**房間目錄**而不是 messages/ 底下：寫在 messages/ 內會改動它的 mtime，
// 而那正是判斷「有沒有變」的依據 —— 每寫一次索引就讓自己失效一次。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_ChatTavernMessageIndex
    {
        public const string IndexFileName = "_msgindex.txt";
        const string Header = "ucl_msgindex_v1";
        /// <summary>新格式檔名：8 位補零 seq。字典序 == 數值序。</summary>
        const string SeqFormat = "00000000";

        sealed class DayEntry
        {
            public string Date;         // yyyy-MM-dd（＝目錄名）
            public int FirstSeq;        // 該日第一筆的 seq（1-based）；Count==0 時無意義
            public int Count;
            public long MtimeTicks;     // 目錄的 LastWriteTimeUtc.Ticks —— 「有沒有變」的判準
        }

        static string IndexPath(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), IndexFileName);

        // ===========================================================
        // 讀 / 寫
        // ===========================================================
        static Dictionary<string, DayEntry> Load(string roomId)
        {
            string path = IndexPath(roomId);
            if (!File.Exists(path)) return null;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != Header) return null;   // 版本不合 → 當沒有
                var map = new Dictionary<string, DayEntry>(StringComparer.Ordinal);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    string[] p = lines[i].Split('\t');
                    if (p.Length != 4) return null;                          // 壞行 → 整份不信
                    map[p[0]] = new DayEntry
                    {
                        Date = p[0],
                        FirstSeq = int.Parse(p[1], CultureInfo.InvariantCulture),
                        Count = int.Parse(p[2], CultureInfo.InvariantCulture),
                        MtimeTicks = long.Parse(p[3], CultureInfo.InvariantCulture),
                    };
                }
                return map;
            }
            catch (Exception e)
            {
                // 壞索引**不可當成「沒有索引」以外的任何東西** —— 一律回 null 走全量列舉。
                // 出聲是必要的：靜默降級會讓「索引壞了」變成永遠沒人發現的慢。
                Debug.LogWarning($"[TavernMsgIndex] 索引解析失敗（{roomId}），本次走全量列舉：{e.Message}");
                return null;
            }
        }

        static void Save(string roomId, List<DayEntry> days)
        {
            try
            {
                var sb = new StringBuilder().AppendLine(Header);
                foreach (var d in days)
                    sb.Append(d.Date).Append('\t').Append(d.FirstSeq).Append('\t')
                      .Append(d.Count).Append('\t').Append(d.MtimeTicks).Append('\n');
                string path = IndexPath(roomId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                // 寫不出來只是「下次還是慢」，不影響正確性 —— 但仍要出聲，
                // 否則會出現「明明做了索引卻永遠沒生效」而沒人知道。
                Debug.LogWarning($"[TavernMsgIndex] 索引寫入失敗（{roomId}）：{e.Message}");
            }
        }

        public static void Delete(string roomId)
        {
            try { if (File.Exists(IndexPath(roomId))) File.Delete(IndexPath(roomId)); }
            catch (Exception e) { Debug.LogWarning($"[TavernMsgIndex] 索引刪除失敗：{e.Message}"); }
        }

        // ===========================================================
        // 主入口
        // ===========================================================
        /// <summary>
        /// 區塊職責：取該房排序後的完整訊息檔路徑清單，能用索引就不列舉。
        /// 數值影響：回傳新陣列（呼叫端可自由持有）。<paramref name="usedIndex"/> 回報這次
        ///          有沒有真的省到 —— 給診斷用，**不要拿它當正確性的證據**。
        /// 邊界：任何不一致 → 回 null，呼叫端退回全量列舉（本檔絕不回「可能錯的清單」）。
        /// </summary>
        public static string[] TryGetOrderedPaths(string roomId, string messagesRoot, out bool usedIndex)
        {
            usedIndex = false;
            var idx = Load(roomId);
            if (idx == null) return null;

            string[] dirs;
            try { dirs = Directory.GetDirectories(messagesRoot); }
            catch { return null; }
            Array.Sort(dirs, StringComparer.Ordinal);

            var result = new List<string>(1024);
            int expectedNextSeq = 1;
            bool anyFromIndex = false;

            foreach (string dir in dirs)
            {
                string date = Path.GetFileName(dir);
                long mtime;
                try { mtime = Directory.GetLastWriteTimeUtc(dir).Ticks; }
                catch { return null; }

                if (idx.TryGetValue(date, out var e) && e.MtimeTicks == mtime)
                {
                    // 目錄沒動過 ⇒ 內容不變 ⇒ 直接算路徑，**不列舉**（本設計的收益就在這一行）
                    if (e.Count == 0) continue;                 // 空目錄（實際存在 3 個）
                    if (e.FirstSeq != expectedNextSeq) return null;   // 跨日不連續 → 整份不信
                    for (int i = 0; i < e.Count; i++)
                        result.Add(Path.Combine(dir, (e.FirstSeq + i).ToString(SeqFormat) + ".json"));
                    expectedNextSeq = e.FirstSeq + e.Count;
                    anyFromIndex = true;
                }
                else
                {
                    // 只列舉「動過的那一天」。索引的價值不是全有全無，
                    // 而是把成本從「全部訊息」壓到「今天的訊息」。
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.json"); }
                    catch { return null; }
                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (string f in files)
                    {
                        if (!TryParseSeq(Path.GetFileName(f), out int seq)) return null;  // 還有舊格式 → 不用索引
                        if (seq != expectedNextSeq) return null;                          // 有洞 / 重號
                        result.Add(f);
                        expectedNextSeq++;
                    }
                }
            }

            usedIndex = anyFromIndex;
            return result.ToArray();
        }

        /// <summary>
        /// 區塊職責：驗證「索引算出來的清單」與「全量列舉算出來的清單」逐筆相同。
        /// 物理意義：索引是加速層，而加速層唯一該被問的問題是**它有沒有改變答案**。
        ///          這裡把兩條路各跑一次直接對撞 —— 不是抽樣、不是看數量，是逐筆比路徑。
        /// 數值影響：純讀，兩邊都不寫快取也不寫索引。慢（等於付一次全量），所以是手動觸發不是自動。
        /// </summary>
        public static string Verify()
        {
            var sb = new StringBuilder();
            string roomsRoot = UCL_ChatTavernIO.GetRoomsRoot();
            if (!Directory.Exists(roomsRoot)) return "找不到 rooms 目錄";
            int rooms = 0, withIndex = 0, mismatch = 0, noIndex = 0;
            long totalFiles = 0;

            foreach (string roomDir in Directory.GetDirectories(roomsRoot))
            {
                string roomId = Path.GetFileName(roomDir);
                string root = Path.Combine(roomDir, "messages");
                if (!Directory.Exists(root)) continue;
                rooms++;

                // ① 全量列舉（真值）—— 與 GetSortedMessageFiles 的 fallback 路徑同一套規則
                string[] truth = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
                var keys = new string[truth.Length];
                for (int i = 0; i < truth.Length; i++)
                    keys[i] = truth[i].Substring(root.Length).Replace('\\', '/');
                Array.Sort(keys, truth, StringComparer.Ordinal);
                totalFiles += truth.Length;

                // ② 索引路徑
                string[] fromIndex = TryGetOrderedPaths(roomId, root, out _);
                if (fromIndex == null) { noIndex++; continue; }
                withIndex++;

                if (fromIndex.Length != truth.Length)
                {
                    mismatch++;
                    sb.AppendLine($"  ✗ [{roomId}] 筆數不同：索引 {fromIndex.Length} vs 實際 {truth.Length}");
                    continue;
                }
                for (int i = 0; i < truth.Length; i++)
                {
                    if (!string.Equals(fromIndex[i], truth[i], StringComparison.OrdinalIgnoreCase))
                    {
                        mismatch++;
                        sb.AppendLine($"  ✗ [{roomId}] seq {i + 1} 路徑不同：\n      索引 {fromIndex[i]}\n      實際 {truth[i]}");
                        break;
                    }
                }
            }

            var head = new StringBuilder();
            head.AppendLine($"房間 {rooms} / 訊息檔 {totalFiles}");
            head.AppendLine($"  走索引 {withIndex} 房 / 無索引（走全量） {noIndex} 房");
            head.AppendLine(mismatch == 0
                ? "  ✅ 索引與全量列舉**逐筆相同**（路徑逐一比對，非抽樣）"
                : $"  🚨 有 {mismatch} 房不符 —— 索引不可信，請重建：");
            return head.ToString() + sb.ToString();
        }

        /// <summary>刪掉全部房間的索引（下次讀取自動以全量列舉重建）。</summary>
        public static int DeleteAll()
        {
            string roomsRoot = UCL_ChatTavernIO.GetRoomsRoot();
            if (!Directory.Exists(roomsRoot)) return 0;
            int n = 0;
            foreach (string roomDir in Directory.GetDirectories(roomsRoot))
            {
                string p = Path.Combine(roomDir, IndexFileName);
                try { if (File.Exists(p)) { File.Delete(p); n++; } }
                catch (Exception e) { Debug.LogWarning($"[TavernMsgIndex] 刪除失敗 {p}：{e.Message}"); }
            }
            return n;
        }

        static bool TryParseSeq(string fileName, out int seq)
        {
            seq = 0;
            if (fileName.Length != 13 || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            return int.TryParse(fileName.Substring(0, 8), NumberStyles.None,
                                CultureInfo.InvariantCulture, out seq) && seq > 0;
        }

        /// <summary>
        /// 區塊職責：由**已經排序好的完整清單**重建索引並落盤。
        /// 物理意義：呼叫端（全量列舉那條路）算完之後順手存一份，下次冷啟動就不必再算。
        /// 邊界：清單裡只要有一個檔名不是新格式，就**不建索引**（舊格式房不適用本機制）。
        /// </summary>
        public static void Rebuild(string roomId, string messagesRoot, string[] orderedPaths)
        {
            try
            {
                var byDate = new List<DayEntry>();
                var seen = new Dictionary<string, DayEntry>(StringComparer.Ordinal);
                for (int i = 0; i < orderedPaths.Length; i++)
                {
                    string name = Path.GetFileName(orderedPaths[i]);
                    if (!TryParseSeq(name, out int seq) || seq != i + 1) return;   // 尚未 migrate → 放棄
                    string date = Path.GetFileName(Path.GetDirectoryName(orderedPaths[i]));
                    if (!seen.TryGetValue(date, out var e))
                    {
                        e = new DayEntry { Date = date, FirstSeq = seq, Count = 0 };
                        seen[date] = e; byDate.Add(e);
                    }
                    e.Count++;
                }
                // 空目錄也要入索引 —— 否則下次它會被當成「不在索引」而被列舉，
                // 而列舉一個空目錄雖然便宜，卻會讓「有沒有命中索引」這個診斷訊號變髒。
                foreach (string dir in Directory.GetDirectories(messagesRoot))
                {
                    string date = Path.GetFileName(dir);
                    if (!seen.ContainsKey(date))
                    {
                        var e = new DayEntry { Date = date, FirstSeq = 0, Count = 0 };
                        seen[date] = e; byDate.Add(e);
                    }
                }
                foreach (var e in byDate)
                    e.MtimeTicks = Directory.GetLastWriteTimeUtc(Path.Combine(messagesRoot, e.Date)).Ticks;
                byDate.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
                Save(roomId, byDate);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernMsgIndex] 索引重建失敗（{roomId}）：{e.Message}");
            }
        }
    }
}
#endif
