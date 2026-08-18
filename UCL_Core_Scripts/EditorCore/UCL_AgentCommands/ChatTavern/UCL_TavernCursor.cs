
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
    // 數值影響：讀取掃最近 SCAN_LIMIT 則；推進游標寫一次檔（原子）。
    // ===========================================================
    public static class UCL_TavernCursor
    {
        /// <summary>掃描上限。⚠ 未讀超過這個數時**會漏**（與 python 端同形狀的取捨）——
        /// 呼叫端拿 oTruncated 回報，不可靜默吞掉。</summary>
        public const int SCAN_LIMIT = 60;

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
        // 物理意義：先印再推，順序不可反。反過來的話，回傳檔寫入失敗時訊息已被標成已讀
        //          ⇒ 那批訊息永遠不會再出現在任何人的未讀裡，而且沒有錯誤訊息。
        // 數值影響：oNewestTs 是這批裡最新的 ts（推進用）；沒有未讀時回空清單且 oNewestTs 為 null。
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
                var aRecent = UCL_ChatTavernIO.Tail(iRoom, SCAN_LIMIT);
                int aScanned = 0;
                foreach (var aMsg in aRecent)
                {
                    aScanned++;
                    string aTs = aMsg?.ts ?? "";
                    if (string.IsNullOrEmpty(aTs)) continue;
                    // 規則：ts > cursor 即未讀（字串比較 —— ISO-8601 UTC 同格式時字典序＝時間序）
                    if (!string.IsNullOrEmpty(aCursor) && string.CompareOrdinal(aTs, aCursor) <= 0) continue;
                    aResult.Add(aMsg);
                    if (oNewestTs == null || string.CompareOrdinal(aTs, oNewestTs) > 0) oNewestTs = aTs;
                }
                // 掃到上限而且整批都是未讀 → 很可能還有更舊的沒被看到
                oTruncated = aScanned >= SCAN_LIMIT && aResult.Count >= SCAN_LIMIT;
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
