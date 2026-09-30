// 區塊職責：訊息檔清單的**落盤索引**（Editor 側）— 讓冷啟動不必列舉整房的訊息檔。
// 物理意義：`seq == 檔名`，而每個日期目錄裝的是一段**連續**的 seq ⇒ 排序後的完整路徑清單
//          可以由一張每日範圍表**算出來**，不必列舉。索引一天一行，大小跟**天數**成正比。
// 數值影響：純加速層。任何一致性檢查不過就退回全量列舉（慢但正確），永不給錯清單。
//
// ⭐ 本檔**只讀，不寫**（TASK-0335，2026-09-30）：
//   索引的格式、驗證與寫檔只有一份，在 SCP_Core `SCP_TavernMsgIndex`；本檔轉呼叫它（同 `UCL_TavernCursor` 的做法）。
//   索引的維護者是**寫入端**：Server 寫完一則訊息就在房間鎖裡刷新那一房的索引。
//   以前 Editor 的讀取端也會寫（缺天就順手 Rebuild）⇒ 兩個 process、兩份格式規則寫同一個檔；那一份已刪除。
//   ⚠ 繞過 Server 直接丟進 messages/ 的檔（遷移工具、人工）不會刷新索引 ⇒ 讀取端多列舉幾天
//     （變慢，不會算錯）。修它：`senate cmd tavern-index --arg op=rebuild`，或等 Server 寫該房下一則。
//
// 為什麼需要索引（2026-08-06 Tim：「卡頓發生在專案重開時」）：
//   `GetSortedMessageFiles` 的記憶體快取是 static 欄位，**domain reload 就整份沒了**
//   （而 domain reload 每次編譯都發生）⇒ 冷啟動每房各付一次全量列舉＋排序。
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEngine;
using SCP.Core.Tavern;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_ChatTavernMessageIndex
    {
        public const string IndexFileName = SCP_TavernMsgIndex.IndexFileName;

        static string DataRoot => UCL_AgentCommandsPath.DataRoot.Replace('\\', '/');

        // SCP 那一側預設把警告印到 stderr，而 Editor 裡沒有人在看 stderr ⇒ 換成 Console 警告。
        // 靜默降級會讓「索引壞了」變成永遠沒人發現的慢。
        static UCL_ChatTavernMessageIndex()
        {
            SCP_TavernMsgIndex.Warn = s => Debug.LogWarning(s);
        }

        // ===========================================================
        // 區塊職責：把 SCP 回傳的路徑接回呼叫端的 messages 根
        // 物理意義：SCP 回的路徑一律 `/` 分隔；Editor 這側的全量列舉回的是 `root\<date>\<seq>.json`。
        //          兩條路給同一則訊息的字串必須**逐字相同** —— 讀取端的訊息快取是用路徑當 key 的，
        //          格式不同就變成同一則訊息兩份快取。⇒ 取最後兩段（日期、檔名），用 root 重新組。
        // 邊界：呼叫端給的 root 跟 SCP 算出來的不是同一個目錄 ⇒ 回 null（退回全量列舉），⛔ 不猜。
        // ===========================================================
        static string[] Remap(string[] iScpPaths, string iMessagesRoot)
        {
            if (iScpPaths == null) return null;
            var aOut = new string[iScpPaths.Length];
            for (int i = 0; i < iScpPaths.Length; i++)
            {
                string p = iScpPaths[i];
                int aFileSep = p.LastIndexOfAny(s_Seps);
                int aDateSep = aFileSep > 0 ? p.LastIndexOfAny(s_Seps, aFileSep - 1) : -1;
                if (aDateSep < 0) return null;
                string aDate = p.Substring(aDateSep + 1, aFileSep - aDateSep - 1);
                aOut[i] = Path.Combine(iMessagesRoot, aDate, p.Substring(aFileSep + 1));
            }
            return aOut;
        }
        static readonly char[] s_Seps = { '/', '\\' };

        static bool SameRoot(string iRoomId, string iMessagesRoot)
        {
            string aScp = SCP_TavernMsgIndex.MessagesDir(DataRoot, iRoomId).TrimEnd('/');
            string aMine = (iMessagesRoot ?? "").Replace('\\', '/').TrimEnd('/');
            return string.Equals(aScp, aMine, StringComparison.OrdinalIgnoreCase);
        }

        // ===========================================================
        // 主入口（簽名沿用舊版，呼叫端不用改）
        // ===========================================================
        /// <summary>
        /// 取該房排序後的完整訊息檔路徑清單，能用索引就不列舉。
        /// <paramref name="usedIndex"/> 回報這次有沒有真的省到 —— 給診斷用，**不要拿它當正確性的證據**。
        /// 邊界：任何不一致 → 回 null，呼叫端退回全量列舉（本檔絕不回「可能錯的清單」）。
        /// </summary>
        public static string[] TryGetOrderedPaths(string roomId, string messagesRoot, out bool usedIndex)
            => TryGetOrderedPaths(roomId, messagesRoot, out usedIndex, out _);

        /// <summary>同上，外加回報**這次有幾天是現場列舉的**（＝索引落後幾天；只回報，⛔ 本側不補寫）。</summary>
        public static string[] TryGetOrderedPaths(string roomId, string messagesRoot,
            out bool usedIndex, out int enumeratedDays)
        {
            usedIndex = false;
            enumeratedDays = 0;
            if (!SameRoot(roomId, messagesRoot)) return null;
            return Remap(SCP_TavernMsgIndex.TryGetOrderedPaths(DataRoot, roomId, out usedIndex, out enumeratedDays),
                         messagesRoot);
        }

        /// <summary>最後 <paramref name="iCount"/> 筆的路徑（直接定址，TASK-0162）。回 null ＝ 這條路走不了，**不是**「沒有訊息」。</summary>
        public static string[] TryGetTailPaths(string roomId, string messagesRoot, int iCount, out int oTotal)
        {
            oTotal = 0;
            if (!SameRoot(roomId, messagesRoot)) return null;
            return Remap(SCP_TavernMsgIndex.TryGetTailPaths(DataRoot, roomId, iCount, out oTotal, out _), messagesRoot);
        }

        /// <summary>回傳「絕對序位 &gt; iAfterIndex0 的那一段」路徑（序位 1-based ＝ seq）。</summary>
        public static string[] TryGetPathsAfter(string roomId, string messagesRoot, int iAfterIndex0, out int oTotal)
        {
            oTotal = 0;
            if (!SameRoot(roomId, messagesRoot)) return null;
            return Remap(SCP_TavernMsgIndex.TryGetPathsAfter(DataRoot, roomId, iAfterIndex0, out oTotal, out _), messagesRoot);
        }

        /// <summary>
        /// 區塊職責：驗證「**本檔**算出來的清單」與「Editor 全量列舉算出來的清單」逐筆相同。
        /// 物理意義：SCP 那一側的 `tavern-index op=verify` 驗的是 SCP 的路徑；這裡驗的是**接回 Editor root 之後**那一份，
        ///          也就是 Editor 讀取端實際拿到的東西。不是抽樣、不是看數量，是逐筆比路徑。
        /// 數值影響：純讀，一個位元組都不寫。慢（等於付一次全量），所以是手動觸發不是自動。
        /// </summary>
        public static string Verify()
        {
            var sb = new StringBuilder();
            string roomsRoot = UCL_ChatTavernIO.GetRoomsRoot();
            if (!Directory.Exists(roomsRoot)) return "找不到 rooms 目錄";
            int rooms = 0, withIndex = 0, mismatch = 0, noIndex = 0, stale = 0;
            long totalFiles = 0;

            foreach (string roomDir in Directory.GetDirectories(roomsRoot))
            {
                string roomId = Path.GetFileName(roomDir);
                string root = UCL_ChatTavernIO_PerMsgFile.GetMessagesRoot(roomId);
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
                string[] fromIndex = TryGetOrderedPaths(roomId, root, out _, out int aEnumDays);
                if (fromIndex == null) { noIndex++; continue; }
                withIndex++;
                if (aEnumDays > 0) stale++;

                if (fromIndex.Length != truth.Length)
                {
                    mismatch++;
                    sb.AppendLine($"  ✗ [{roomId}] 筆數不同：索引 {fromIndex.Length} vs 實際 {truth.Length}");
                    continue;
                }
                for (int i = 0; i < truth.Length; i++)
                {
                    // ⚠ Ordinal（區分大小寫與分隔符）：這一格驗的就是「兩條路逐字相同」，見 Remap
                    if (!string.Equals(fromIndex[i], truth[i], StringComparison.Ordinal))
                    {
                        mismatch++;
                        sb.AppendLine($"  ✗ [{roomId}] seq {i + 1} 路徑不同：\n      索引 {fromIndex[i]}\n      實際 {truth[i]}");
                        break;
                    }
                }
            }

            var head = new StringBuilder();
            head.AppendLine($"房間 {rooms} / 訊息檔 {totalFiles}");
            head.AppendLine($"  走索引 {withIndex} 房（其中 {stale} 房有現場列舉的天 ＝ 索引落後）/ 無索引（走全量） {noIndex} 房");
            head.AppendLine(mismatch == 0
                ? "  ✅ 索引與全量列舉**逐筆相同**（路徑逐一比對，非抽樣）"
                : $"  🚨 有 {mismatch} 房不符 —— 索引不可信，請重建：`senate cmd tavern-index --arg op=rebuild`");
            return head.ToString() + sb.ToString();
        }
    }
}
#endif
