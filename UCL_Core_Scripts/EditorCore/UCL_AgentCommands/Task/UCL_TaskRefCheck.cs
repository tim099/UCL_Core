// 區塊職責：留言／收工正文裡的**門牌引用**（`留言 #N`、`第 N 格`）與本單實際編號體系的比對。
// 物理意義：一則打錯 `index` 的留言，在每一個機械欄位上都是合法的 ——
//   作者對、時間戳對、落點是一張真實存在的單、格式完整、措辭精準，
//   **只有它回答的是另一張單的問題**（TASK-0177）。汙染（作者錯）與陳舊（時戳錯）
//   各有守衛擋得到，而「錯位」三道全放行 ⇒ 缺的是這一支。
// 數值影響：**純讀，零副作用** —— 只回一串警語字串給呼叫端印進回傳檔。
//   ⛔ 不 throw、不擋下落檔：引用一個還沒出現的格號可以是合法意圖（「等 #7 補上」），
//   硬擋會製造一種新的「正確的話寫不進去」。本病的殺傷力全部來自它**完全安靜**，
//   ⇒ 只要它出聲，病就治了。
//
// 🩸 為什麼守衛只能站在**落檔那一刻**（TASK-0177 留言 #1 量到的）：
//   2026-09-07 那則錯位 wrapup 落進 0157 時，0157 只有 4 則留言 ⇒ `7 > 4` 會叫。
//   而事後補的留言把「留言 #7」那一格**餵飽了**（0157 今天有 7 則）⇒ 同一個引用回頭變成合法。
//   ⇒ **修復的動作本身會消除偵測它的證據**：任何「事後掃全庫」都會系統性低報，
//     已被處理過的錯位在讀數上跟從沒發生過一模一樣。事後掃描只能當補充，不能當驗收。
//
// 🩸 為什麼 `第 N 格` **不比大小**（同上，175 張單掃出 真陽 0／假陽 6）：
//   分組編號的單（`### ① / ② / ③ / ④` 底下各自掛格子）**根本沒有「第 N 格」這個座標**，
//   而「checkbox 總數 20 ≥ 10」會讓比大小的守衛以為那個座標存在。
//   ⇒ 問的必須是「這個座標在本單的編號體系裡存不存在」，不是比總數大小。
//   ⚠ 而「第 N 格」在實務上常指**別的東西**（`selftest 的第 32 格`）—— 那是假陽的主要來源。
//   📌 一個只比大小的警語會在第一天被所有人學會忽略，**而那比沒有警語更糟**：
//     它會佔掉「這裡有人在看」的位置。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    /// <summary>
    /// 門牌引用守衛（TASK-0177）：掃正文裡的 <c>留言 #N</c> 與 <c>第 N 格</c>，
    /// 與本單實際的留言數／驗收格編號體系比對，回一串**警語**。
    /// <para>⛔ 純讀、不擋下、不 throw —— 呼叫端負責把警語印進回傳檔。</para>
    /// </summary>
    public static class UCL_TaskRefCheck
    {
        // ⚠ 單號在正文裡實測至少三種寫法：`TASK-0107` / `tasks/0107.md` / 裸 `0151`。
        //   ⇒ 引用前方只要出現任一種，就是**跨單引用**，一律放過。
        //   裸號限定「0 開頭的四位數」：`0151` 是單號，`2026` 是年份 —— 兩者必須不同形。
        static readonly Regex s_TicketNear = new Regex(
            @"TASK-\d{3,4}|tasks/\d{3,4}|(?<!\d)0\d{3}(?!\d)", RegexOptions.Compiled);

        static readonly Regex s_CommentRef = new Regex(@"留言\s*#\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex s_CriteriaRef = new Regex(@"第\s*(\d+)\s*格", RegexOptions.Compiled);

        /// <summary>引用前方回看幾個字元算「就近」。40 字足夠涵蓋 `TASK-0151 留言 #7` 這類寫法。</summary>
        const int k_NearWindow = 40;

        /// <summary>
        /// 掃描正文並回警語清單（空清單＝沒有可疑引用）。
        /// </summary>
        /// <param name="iBody">留言／收工正文（原文，未經處理）。</param>
        /// <param name="iCommentCount">本單**落檔前**已有的留言數。</param>
        /// <param name="iCriteria">本單 `## 驗收標準` 整段原文；給不出來就傳 null（該軸自動略過）。</param>
        public static List<string> Scan(string iBody, int iCommentCount, string iCriteria)
        {
            var aOut = new List<string>();
            if (string.IsNullOrEmpty(iBody)) return aOut;

            // ── 軸一：`留言 #N` ────────────────────────────────────
            // `+1` 容許**自指**：一則留言寫「判定見留言 #N」而它自己就是第 N 則，是常見且合法的寫法。
            int aMaxComment = iCommentCount + 1;
            foreach (Match aM in s_CommentRef.Matches(iBody))
            {
                if (IsCrossTicket(iBody, aM.Index)) continue;
                if (!int.TryParse(aM.Groups[1].Value, out int aN)) continue;
                if (aN <= aMaxComment) continue;
                aOut.Add($"引用「留言 #{aN}」，而本單落檔後只有 **{aMaxComment}** 則留言"
                    + "（含本則）⇒ 那個門牌在這張單上不存在。"
                    + "**這是打錯 `index` 最常見的樣子**；跨單引用請寫出單號（`TASK-0157 留言 #7`）。");
            }

            // ── 軸二：`第 N 格` ────────────────────────────────────
            // ⛔ 分組編號的單一律不報（沒有這個座標空間），⛔ 也不比大小。
            if (!string.IsNullOrEmpty(iCriteria) && !HasGroupedNumbering(iCriteria))
            {
                int aBoxes = UCL_TaskIO.ListUncheckedCriteria(iCriteria).Count
                           + UCL_TaskIO.ListCheckedCriteria(iCriteria).Count;
                if (aBoxes > 0)
                {
                    foreach (Match aM in s_CriteriaRef.Matches(iBody))
                    {
                        if (IsCrossTicket(iBody, aM.Index)) continue;
                        if (!int.TryParse(aM.Groups[1].Value, out int aN)) continue;
                        if (aN >= 1 && aN <= aBoxes) continue;
                        aOut.Add($"引用「第 {aN} 格」，而本單的驗收標準是 **{aBoxes}** 格平鋪編號"
                            + "⇒ 那個座標不在範圍內。"
                            + "⚠ 若指的是別處的格號（例如 selftest），請把出處寫在號碼旁邊。");
                    }
                }
            }

            return aOut;
        }

        // 區塊職責：判斷某個引用是不是**跨單引用**（前方就近出現任一種單號寫法）。
        // 物理意義：`TASK-0107 留言 #2` 是完全正當的寫法，而它與打錯 index 的留言在
        //   「N 超出本單數量」這個讀數上同形 —— 分開它們的是號碼旁邊有沒有出處。
        // 數值影響：純讀。回 true ⇒ 呼叫端跳過該筆，不產生警語。
        static bool IsCrossTicket(string iBody, int iMatchIndex)
        {
            int aStart = Math.Max(0, iMatchIndex - k_NearWindow);
            return s_TicketNear.IsMatch(iBody.Substring(aStart, iMatchIndex - aStart));
        }

        // 區塊職責：判斷驗收標準是不是**分組編號**（`### ① …` 這類標題把格子分成幾組）。
        // 物理意義：分組之後「第 N 格」這個平鋪座標**不存在** —— 0157 有 20 個 checkbox
        //   分屬四組，而它沒有第 10 格；拿 `20 ≥ 10` 去放行等於承認了一個不存在的門牌。
        // 數值影響：純讀。回 true ⇒ 軸二整軸略過（⛔ 刻意不改成「換個方式報」：
        //   分組單上的「第 N 格」幾乎都在指別處的格號，報它就是在生產假陽）。
        static bool HasGroupedNumbering(string iCriteria)
        {
            foreach (var aLine in iCriteria.Replace("\r", "").Split('\n'))
            {
                var aTrim = aLine.TrimStart();
                if (!aTrim.StartsWith("#", StringComparison.Ordinal)) continue;
                // ①..⑳ = U+2460..U+2473；分組標題一律用這組圈號（本專案慣例）。
                foreach (var aCh in aTrim)
                    if (aCh >= '\u2460' && aCh <= '\u2473') return true;
            }
            return false;
        }
    }
}
