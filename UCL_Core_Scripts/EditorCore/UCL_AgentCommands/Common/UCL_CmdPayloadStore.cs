// 區塊職責：Cmd 回傳檔（給人讀的 markdown 報告）的**帶輪替**落點 —— 寫一筆、只留最近 N 筆。
//
// 物理意義：Cmd 的報告原本有兩種落點，而它們的耐久度差一個等級，**而差別完全沉默**：
//   ① `letters/<persona>/_<cmd>_<step>.md`（FreeTime / Sculpture / StreamWatch）
//      —— 固定檔名，每次覆寫。同一個 (persona, step) 永遠只有最新那份，不會長大，但也沒有歷史。
//   ② `ChatTavern/_last_op.md`（Tavern / Treasury / Glossary 共用一格）
//      —— **全 Cmd 共用單一檔**，下一支 Cmd 一寫就整份蓋掉。
//   🩸 gura 2026-08-18 實測：register 一個 glossary 詞條之後幾秒，一筆 Treasury 查詢
//      就把 `_last_op.md` 蓋掉了，我回頭讀不到自己的報告 —— 而報告尾端正好掛著
//      「你在自由時間中，下一步是…」那段指路。**掛了但沒人看到，效果等於沒掛。**
//
// ⇒ 本 store 是第三種：**每次一個新檔（時間戳命名）＋ 只留最近 N 筆**。
//   Tim 2026-08-18 拍板：「補 per-op payload，不過需要一個清理機制避免無限增長
//   （例如寫入時假如超過 10 筆，刪除最舊的），另外要整理到資料夾。」
//
// 數值影響：一次寫入 ＋ 一次目錄列舉；超過 iKeep 才刪檔。落點
//   `<DataRoot>/_cmd_payloads/<CmdType>/<yyyyMMdd-HHmmssfff>_<op>[_<scope>].md`
//   （`_cmd_results` / `_cmd_errors` 已是同層慣例，放一起才找得到）。
//
// ⛔ 不取代上面的 ①：那些是「同一格永遠最新」的語意，**固定檔名本身就是那個語意的載體**
//   （agent 記得住 `_freetime_next.md` 是哪一份）。要把它們搬過來是另一件事，
//   得先決定「固定入口」怎麼保留 —— 沒決定就搬會讓所有既有指路失效。
// 2026-08-18 gura（配套 Cmd_Glossary 的耐久度修正）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Cmd markdown 報告的帶輪替落點（每次一個新檔，只留最近 N 筆）。
    /// </summary>
    public static class UCL_CmdPayloadStore
    {
        /// <summary>目錄名 —— 同時是刪檔守衛的判準（見 <see cref="Rotate"/>）。</summary>
        public const string RootDirName = "_cmd_payloads";

        /// <summary>預設保留筆數（Tim 2026-08-18 拍板 10 筆）。</summary>
        public const int DEFAULT_KEEP = 10;

        /// <summary>某 CmdType 的 payload 目錄。</summary>
        public static string DirFor(string iCmdType)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, RootDirName, SafeSeg(iCmdType, "Cmd"));

        // ===========================================================
        // 區塊職責：寫一筆報告並輪替舊檔，回傳實際落點（呼叫端要拿去 ReportOutputFile）。
        //
        // 物理意義：檔名帶**時間戳前綴**，所以「最舊」＝字典序最小 —— 不用 mtime 判斷。
        //          mtime 會被複製 / 還原 / 同步工具改掉，而那種錯誤的症狀是「刪錯了那一筆」，
        //          事後完全看不出來（被刪的檔不會留下痕跡）。
        //
        // 數值影響：iKeep <= 0 一律當 1（保留 0 筆等於寫完就刪，那是一個沒有人想要的行為，
        //          而它會長得像「功能壞了」）。寫入失敗只印 warning 並回 null ——
        //          報告寫不進去不該讓 Cmd 本體失敗，但**也不靜默**（靜默的話
        //          「沒寫」與「寫了但被輪替掉」在事後長得一模一樣）。
        // ===========================================================
        public static string Write(string iCmdType, string iOp, string iMarkdown,
                                   string iScope = null, int iKeep = DEFAULT_KEEP)
        {
            try
            {
                string aDir = DirFor(iCmdType);
                Directory.CreateDirectory(aDir);

                string aStamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
                string aName = aStamp + "_" + SafeSeg(iOp, "op")
                             + (string.IsNullOrEmpty(iScope) ? "" : "_" + SafeSeg(iScope, "scope"))
                             + ".md";
                string aPath = Path.Combine(aDir, aName);
                File.WriteAllText(aPath, iMarkdown ?? "", new UTF8Encoding(false));

                Rotate(aDir, Math.Max(1, iKeep));
                return aPath;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CmdPayloadStore] {iCmdType}/{iOp} 報告寫入失敗（Cmd 本體不受影響）：{e.Message}");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：只留最新 iKeep 筆，其餘刪除。
        //
        // ⚠ 刪檔守衛（刻意的三道，因為這是本檔唯一會刪東西的地方）：
        //   ① 目錄路徑必須含 `_cmd_payloads` —— 呼叫端傳錯路徑時**不刪任何東西**，
        //     而不是「刪了才發現不對」。
        //   ② 只看 `*.md`，其他副檔名一律不碰（有人往裡面放筆記不該被清掉）。
        //   ③ 排序用**檔名**（時間戳前綴），不用 mtime —— 見 Write 的註解。
        //
        // 數值影響：刪 N-iKeep 個檔。個別刪除失敗只印 warning 繼續（檔案被別的程式開著時，
        //   下一次寫入會再試一遍；為了刪不掉一個舊檔而讓整筆報告失敗是不划算的）。
        // ===========================================================
        static void Rotate(string iDir, int iKeep)
        {
            if (string.IsNullOrEmpty(iDir) || iDir.IndexOf(RootDirName, StringComparison.Ordinal) < 0)
            {
                Debug.LogWarning($"[CmdPayloadStore] 拒絕輪替 —— 目錄不在 {RootDirName} 底下：{iDir}");
                return;
            }
            var aFiles = new List<string>(Directory.GetFiles(iDir, "*.md"));
            if (aFiles.Count <= iKeep) return;

            aFiles.Sort(StringComparer.Ordinal);          // 檔名 = 時間戳前綴 ⇒ 字典序即時序
            int aDeleteCount = aFiles.Count - iKeep;
            for (int i = 0; i < aDeleteCount; i++)
            {
                try { File.Delete(aFiles[i]); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CmdPayloadStore] 舊檔刪除失敗（下次寫入會再試）：{aFiles[i]}（{e.Message}）");
                }
            }
        }

        /// <summary>檔名片段消毒：非法字元換 `-`，空值回 fallback（**不丟例外** —— 命名問題不該擋住報告）。</summary>
        static string SafeSeg(string iRaw, string iFallback)
        {
            string aVal = (iRaw ?? "").Trim();
            if (aVal.Length == 0) return iFallback;
            var aSb = new StringBuilder(aVal.Length);
            foreach (char c in aVal)
                aSb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c);
            return aSb.ToString();
        }
    }
}
#endif
