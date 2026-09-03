
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// per-persona 酒館已讀游標的 C# 端讀寫（與 python tavern_catchup.py 共用同一個檔與同一條規則）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    // ===========================================================
    // 區塊職責：讀「這個 persona 還沒看過哪些訊息」＋ 推進已讀游標。
    // 物理意義：游標檔 `ChatTavern/_inbox_cursor/<persona>.json` 只有一個判準欄位
    //          `last_seen_ts`（ISO 字串），規則是 **ts > last_seen_ts 即未讀**。
    //
    // ⚠⚠ **這是第三份實作** —— 另兩份都在 python：`Tools/tavern_catchup.py`（叮 catchup）與
    //   `Tools~/AgentCommands/tavern_cmd.py`（pending/commit 兩階段：**開口才算真的讀了**）。
    //   我一開始只 grep 到一份就寫了「第二份」—— 「我沒找到」不等於「不存在」，當場更正。
    //   原第一份參照：`AgentCommands/Tools/tavern_catchup.py`
    //   （叮 catchup）。兩邊讀寫**同一個檔、同一條規則**，所以任何一邊改判準都必須改兩邊。
    //   為什麼還是寫了第二份：自由時間換骰要求「骰面與酒館訊息在**同一份回傳檔**、一定會看到」，
    //   而回傳檔是 C# 寫的；讓 C# 去 spawn python 只為了讀幾行訊息，代價是 process 生命週期
    //   （domain reload / timeout / 屍潮）遠大於這段邏輯本身。
    //   ⇒ 取捨是刻意的，代價寫在這裡：**兩份實作漂移時不會有任何錯誤訊息** ——
    //     症狀會是「叮說有未讀、換骰說沒有」，或反過來。改判準時 grep `last_seen_ts` 兩端一起改。
    //
    // 數值影響：讀取**由舊到新**交付一批 SCAN_LIMIT 則（必要時往回捲到 BACKLOG_SCAN_CAP
    //          去確認手上真的握著最舊的那則未讀）；推進游標寫一次檔（原子）。
    // ===========================================================
    public static class UCL_TavernCursor
    {
        /// <summary>一次交付的未讀**批量**（不是「只看得到這麼多」）。未讀超過這個數時
        /// 由舊到新分批消化，剩下的留在未讀裡，下次 catchup 接著給。</summary>
        public const int SCAN_LIMIT = 60;

        /// <summary>為了找出「最舊的那則未讀」最多往回捲多少則。
        /// ⚠ 這是**成本上限，不是正確性上限** —— 回捲不到底時 oNewestTs 回 null（拒推游標），
        /// 絕不用「推到看得見的最新」去換一個好看的結果。</summary>
        public const int BACKLOG_SCAN_CAP = 4000;

        public static string CursorPath(string iPersona)
            => Path.Combine(UCL_RepoPath.AgentCommandsDir, "ChatTavern", "_inbox_cursor", iPersona + ".json");

        /// <summary>讀 last_seen_ts；沒有游標檔回 null（語意＝「全部都算未讀」）。</summary>
        public static string ReadCursor(string iPersona)
        {
            try
            {
                string aPath = CursorPath(iPersona);
                if (!File.Exists(aPath)) return null;
                var aJd = JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
                string aTs = aJd?.GetString("last_seen_ts", "");
                return string.IsNullOrEmpty(aTs) ? null : aTs;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernCursor] 讀取失敗（{iPersona}）: {e.Message}");
                return null;
            }
        }

        // 區塊職責：推進游標（原子寫）。
        // 物理意義：半寫的游標檔會被下一次讀取判成「沒有游標」⇒ **整個歷史重新變成未讀**。
        //          所以先寫 tmp 再 rename，與 python 端同樣做法。
        // 數值影響：寫入 last_seen_ts + updated_at 兩欄（欄位名與 python 端一致，別改）。
        // ⚠ **單調**：只前進不後退（與 python `tavern_cmd.py` 的提交規則一致 ——
        //   「pending <= 現有 last_seen_ts 就不動」）。少了這道，某輪讀到 0 筆或讀到舊訊息時
        //   會把游標打回去 ⇒ 整批已讀重新變未讀，而那長得像「突然湧入一堆新訊息」。
        public static void WriteCursor(string iPersona, string iLastSeenTs)
        {
            if (string.IsNullOrEmpty(iLastSeenTs)) return;
            string aCur = ReadCursor(iPersona);
            if (!string.IsNullOrEmpty(aCur) && string.CompareOrdinal(iLastSeenTs, aCur) <= 0) return;
            try
            {
                string aPath = CursorPath(iPersona);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                var aJd = new JsonData();
                aJd["last_seen_ts"] = new JsonData(iLastSeenTs);
                aJd["updated_at"] = new JsonData(DateTime.UtcNow.ToString("o"));
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aJd.ToJsonBeautify(), new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernCursor] 推進失敗（{iPersona}）: {e.Message}");
            }
        }


        // ===========================================================
        // 區塊職責：把全域已讀游標推到「seq <= iSeq 的最新那則」的時戳。
        // 物理意義：觀影 sidecar 的水位是 **seq**（per-session，語意＝這場開始以來），
        //          而叮/自由時間的全域游標是 **ts**。兩者鍵不同、語意不同，但
        //          「觀影期間顯示過的訊息**確實已經進到眼裡**」（Tim 2026-08-18）——
        //          所以顯示過就該一併消化全域游標，否則整場結束後未讀會累成一堵牆。
        // 數值影響：讀最近 SCAN_LIMIT 則找 ts、寫一次游標（單調，只前進）。
        //          找不到對應 seq 回 null 且**不動游標** —— 寧可原地，不可亂跳
        //          （游標亂跳的兩個方向都會壞：往前跳＝永久漏訊息，往後跳＝重播一堵牆）。
        // ⛔ 呼叫端要自己確保「真的顯示過」（shown > 0）才呼叫 —— 沒讀到東西就沒有「已讀」到那裡。
        // ===========================================================
        public static string AdvanceToSeq(string iPersona, string iRoom, int iSeq)
        {
            if (iSeq <= 0) return null;
            try
            {
                string aBestTs = null;
                foreach (var aMsg in UCL_ChatTavernIO.Tail(iRoom, SCAN_LIMIT))
                {
                    if (aMsg == null || aMsg.seq <= 0 || aMsg.seq > iSeq) continue;
                    string aTs = aMsg.ts ?? "";
                    if (string.IsNullOrEmpty(aTs)) continue;
                    if (aBestTs == null || string.CompareOrdinal(aTs, aBestTs) > 0) aBestTs = aTs;
                }
                if (string.IsNullOrEmpty(aBestTs)) return null;
                WriteCursor(iPersona, aBestTs);
                return aBestTs;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernCursor] AdvanceToSeq 失敗（{iPersona}/{iRoom}/{iSeq}）: {e.Message}");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：取未讀訊息（不推進 —— 推進由呼叫端在**印出來之後**才做）。
        // 物理意義：游標是單一水位，而水位只有一個方向。所以「哪一批該先交付」不是喜好問題：
        //          **必須由舊到新**。交付最新的那一批再推水位，等於把中間沒印出來的那段
        //          一次標成已讀 —— 它們不會出現在任何人的未讀裡，也沒有任何錯誤訊息。
        // 🩸 2026-09-03 apex-one 早安實測：游標 09-01T15:48、真未讀 **293** 則，
        //          舊實作只掃尾端 60 則、印出 57 筆，然後把水位推到最新
        //          ⇒ **232 則被靜默標成已讀**，而回傳檔同時印著「清單不完整」的警告。
        //          警告有印、資料照丟 —— 這就是為什麼判準要寫在寫入端，不是寫在畫面上。
        // 數值影響：oNewestTs 是**這一批真的交付出去的**最新 ts（＝可安全推進的水位）；
        //          回 null 時呼叫端一律不得推進。oTruncated＝還有未讀沒交付（更新的那段）。
        // ⚠ 沒有游標（從未讀過）時不回放整部歷史 —— 只給最近 SCAN_LIMIT 則，
        //   那是「第一次登入」而不是「積了一整部歷史沒讀」。
        // ===========================================================
        public static List<UCL_ChatMessage> ReadUnread(
            string iPersona, string iRoom, out string oNewestTs, out bool oTruncated)
        {
            oNewestTs = null;
            oTruncated = false;
            var aResult = new List<UCL_ChatMessage>();
            string aCursor = ReadCursor(iPersona);
            try
            {
                // ── 從未設過游標：不回放歷史，給最近一批 ──
                if (string.IsNullOrEmpty(aCursor))
                {
                    foreach (var aMsg in UCL_ChatTavernIO.Tail(iRoom, SCAN_LIMIT))
                    {
                        string aTs0 = aMsg?.ts ?? "";
                        if (string.IsNullOrEmpty(aTs0)) continue;
                        aResult.Add(aMsg);
                        if (oNewestTs == null || string.CompareOrdinal(aTs0, oNewestTs) > 0) oNewestTs = aTs0;
                    }
                    return aResult;
                }

                // ── 往回捲到「窗口最舊的那則已經讀過」為止 ──
                // 只有這個條件成立，才證明**最舊的未讀確實在窗口內**；
                // 否則我拿到的是尾端的一段，前面還有東西看不見。
                int aWindow = SCAN_LIMIT;
                List<UCL_ChatMessage> aScan;
                bool aReachedOldest = false;
                while (true)
                {
                    aScan = UCL_ChatTavernIO.Tail(iRoom, aWindow);
                    if (aScan.Count < aWindow) { aReachedOldest = true; break; }   // 整房都進窗口了
                    string aOldestTs = null;
                    foreach (var aMsg in aScan)
                    {
                        string aT = aMsg?.ts ?? "";
                        if (string.IsNullOrEmpty(aT)) continue;
                        aOldestTs = aT;
                        break;
                    }
                    if (aOldestTs == null || string.CompareOrdinal(aOldestTs, aCursor) <= 0)
                    {
                        aReachedOldest = true;
                        break;
                    }
                    if (aWindow >= BACKLOG_SCAN_CAP) break;
                    aWindow = Math.Min(aWindow * 4, BACKLOG_SCAN_CAP);
                }

                var aUnread = new List<UCL_ChatMessage>();
                foreach (var aMsg in aScan)
                {
                    string aTs = aMsg?.ts ?? "";
                    if (string.IsNullOrEmpty(aTs)) continue;
                    // 規則：ts > cursor 即未讀（字串比較 —— ISO-8601 UTC 同格式時字典序＝時間序）
                    if (string.CompareOrdinal(aTs, aCursor) <= 0) continue;
                    aUnread.Add(aMsg);
                }

                if (!aReachedOldest)
                {
                    // 回捲到上限仍沒碰到已讀邊界 ⇒ 最舊的未讀不在手上。
                    // 這時**任何**推進都會跳過它們，所以拒推（oNewestTs 留 null）並大聲說。
                    oTruncated = true;
                    oNewestTs = null;
                    for (int i = 0; i < aUnread.Count && i < SCAN_LIMIT; i++) aResult.Add(aUnread[i]);
                    return aResult;
                }

                // ── 由舊到新交付一批；水位只推到這一批的最新 ──
                int aTake = Math.Min(aUnread.Count, SCAN_LIMIT);
                for (int i = 0; i < aTake; i++)
                {
                    aResult.Add(aUnread[i]);
                    string aTs = aUnread[i].ts ?? "";
                    if (oNewestTs == null || string.CompareOrdinal(aTs, oNewestTs) > 0) oNewestTs = aTs;
                }
                oTruncated = aUnread.Count > aTake;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernCursor] 讀訊息失敗（{iPersona}/{iRoom}）: {e.Message}");
            }
            return aResult;
        }
    }
}
#endif
