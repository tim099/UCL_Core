// 區塊職責：**關場／補收工的唯一 Cmd 入口**（TASK-0127 ④，＝ TASK-0055「所有關場路徑走同一個門」的 Editor 半邊）。
// 物理意義：在此之前「補收工」只有一個入口 —— `UCL_SessionAdminPage` 的那顆鈕，而它**直接呼叫**
//          `SCP_ActivitySessionStore.Close`（三欄一翻就走），於是觀影場的**結算被跳過**：
//          酬勞蒸發、seq 區間永久消失（那場觀察再也匯不進書），而印出來的字跟正常收工一模一樣。
//          ⇒ 這支 Cmd 把那條路變成：① 權威狀態 ② 結算（per-kind）③ 回報，三段分開講。
// 數值影響：會**寫別人的 session 檔**、可能觸發發薪（觀影場走 SettleAsync）⇒ `confirm=1` 是必填。
//
// ⚠ 這支存在的第二個理由（比第一個更重要）：管理頁要搬去 Senate（TASK-0127 ⑥），
//   而結算是金流、金流不搬（TASK-0106 Tim 拍 B 不動）⇒ Senate 那側**只能委派回 Editor**。
//   委派需要一個目標，而「頁面上的一顆鈕」不是目標。⇒ 先有這支 Cmd，頁面才搬得動。
//   ⭐ 順帶的收益：補收工從此**有回傳檔、有讀數、agent 也能用**（在此之前只有 GUI 按得到）。
//
// ⛔ 射程只有「殘留」（active 但已過 end_ts）。進行中的場**不從這裡關** ——
//   那要走各 kind 自己的收工步驟（`step=end`），因為正常收工還有收工公告與同場者判定。
//   這條界線是從管理頁原樣搬過來的，**不是新加的限制**。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.StreamWatch;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 關場／補收工（殘留專用）。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// # 幫 basecamp 把過期沒收工的那場收掉（觀影場會補結算）
    /// senate ucmd run SessionClose --persona basecamp --arg target_persona=basecamp --arg confirm=1
    ///
    /// # 帶一句原因（會寫進 session 的 end_reason，之後查得到是誰／為什麼關的）
    /// senate ucmd run SessionClose --persona basecamp --arg target_persona=gura --arg confirm=1 --arg reason=admin-page
    /// </code>
    /// </summary>
    public class Cmd_SessionClose : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SessionClose";

        public override string ShortDescription =>
            "關掉某 persona **過期殘留**的 session（觀影場會補結算）。進行中的場不從這裡關 —— 那要走該 kind 的 step=end。";

        public override string ArgsSchema =>
            "target_persona=<誰的場>（必填，不猜身分） | " +
            "confirm=1（必填 —— 這會寫別人的 session 檔，觀影場還會發薪） | " +
            "reason=<一句話>（選填，預設 closed-by-cmd；會寫進 end_reason）";

        public override string ExampleArgs => "target_persona=basecamp confirm=1";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_SessionClose.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            // ⚠ 目標與呼叫者是**兩個** key：`--persona` 會戳進 args 的 `persona`（AgentCmdClient 那條律），
            //   兩者共用一個 key 的話，呼叫者身分會被目標蓋掉 —— 回傳檔就會落進**別人的** letters 夾。
            //   🩸 2026-09-04 首跑實撞：`--arg persona=probe-0127` 讓工具替一個不存在的人長出 letters 目錄。
            //   ⇒ 沿用 Cmd_Task 既有的慣例：目標叫 target_persona。
            string aTarget = GetArg(args, "target_persona", "").Trim();
            string aReason = GetArg(args, "reason", "closed-by-cmd").Trim();
            string aConfirm = GetArg(args, "confirm", "").Trim();
            if (aReason.Length == 0) aReason = "closed-by-cmd";

            // 不猜「現在是誰」—— 多 persona 環境猜錯會關掉別人的場，而那看起來完全正常。
            if (string.IsNullOrEmpty(aTarget))
                throw new Exception("[SessionClose] 需要 --arg target_persona=<誰的場>（不猜身分 —— 猜錯會關掉別人的場）");

            var aR = new StringBuilder();
            aR.AppendLine($"# SessionClose persona={aTarget}  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            string aActor = GetArg(args, "persona", "unknown").Trim();
            if (aActor.Length == 0) aActor = "unknown";

            // ⚠ 讀取端**不過濾 kind**：要問的是「這個人有沒有一場需要收的」，不是「有沒有我這種」。
            //   🩸 過濾 kind 的那個問法正是 TASK-0056 那個洞 —— 它會讓別 kind 的場在你眼裡等於不存在。
            var aSession = SCP.Core.Session.SCP_ActivitySessionStore.Load(UCL_AgentCommandsPath.ScpDataRoot, aTarget);
            if (aSession == null)
            {
                aR.AppendLine("## 未動作");
                aR.AppendLine($"- `{aTarget}` 沒有 session 檔（或檔壞了）—— 掃描範圍："
                              + string.Join(" / ", SCP.Core.Session.SCP_ActivitySessionKind.Kinds));
                aR.AppendLine("- ⚠ 「沒查到」不等於「他不在任何 session」：未登記的種類本層看不到。");
                Finish(args, aActor, aR, "none", false, false);
                return;
            }

            bool aRunning = aSession.IsRunningAt(DateTime.Now, out DateTime? aEnd);
            string aKind = string.IsNullOrEmpty(aSession.kind) ? "(未標 kind)" : aSession.kind;
            aR.AppendLine($"- 場次：kind=**{aKind}**　session_id=`{aSession.session_id}`"
                          + $"　預定收工 {aSession.until_local}　active={aSession.active}");

            // ── 三態：進行中 ⇒ 擋而指路；已收工 ⇒ 冪等 no-op；殘留 ⇒ 這支的射程 ──
            if (aRunning)
            {
                aR.AppendLine();
                aR.AppendLine("## blocked —— 這場**還在進行中**，不從這裡關");
                aR.AppendLine($"- 原因：{aKind} 的場預定到 {(aEnd.HasValue ? aEnd.Value.ToString("HH:mm") : "（無截止）")} 本地，此刻仍在射程內");
                aR.AppendLine("- 處理方式（擇一，指令可直接複製執行）：");
                aR.AppendLine($"    `senate ucmd run {KindCmdName(aSession.kind)} --persona {aTarget} --arg step=end`");
                aR.AppendLine("    或等它到期之後再跑本 Cmd（到期未收＝殘留，那才是本 Cmd 的射程）");
                aR.AppendLine("- ⛔ 為什麼不給 force：正常收工還有**收工公告**與**同場者判定**，那些不是本 Cmd 做的事。");
                Finish(args, aActor, aR, aKind, false, false);
                throw new Exception($"[SessionClose] blocked：`{aTarget}` 的 {aKind} 場還在進行中（詳見回傳檔）");
            }

            if (!aSession.active)
            {
                aR.AppendLine();
                aR.AppendLine("## 未動作（冪等）");
                aR.AppendLine($"- 這場已經收過工：end_reason=`{aSession.end_reason}`　ended_at=`{aSession.ended_at}`");
                aR.AppendLine("- ⛔ 不重複結算 —— 重複發薪不會有人喊，而帳對不上的時候沒有人查得出是這裡。");
                Finish(args, aActor, aR, aKind, false, false);
                return;
            }

            // 殘留 ⇒ 真的要動了，這裡才檢查 confirm（前面幾條路都沒有寫入，先擋 confirm 只會擋掉查詢）
            if (aConfirm != "1")
            {
                aR.AppendLine();
                aR.AppendLine("## blocked —— 缺 confirm");
                aR.AppendLine($"- `{aTarget}` 有一場**過期殘留**的 {aKind}（{aSession.session_id}），可以收。");
                aR.AppendLine("- 這一步會寫別人的 session 檔"
                              + (UCL_SessionKindHost.For(aSession.kind)?.SettleResidueAsync != null ? "，而且**會發薪**（這個 kind 登記了補結算）" : "")
                              + " ⇒ 要顯式確認：");
                aR.AppendLine($"    `senate ucmd run SessionClose --persona {aActor} --arg target_persona={aTarget} --arg confirm=1`");
                Finish(args, aActor, aR, aKind, false, false);
                throw new Exception($"[SessionClose] blocked：缺 --arg confirm=1（詳見回傳檔）");
            }

            aR.AppendLine();
            aR.AppendLine("## 收工（三段分開報 —— 任何一段炸掉都不冒充其他段）");

            // ①② 走**共用的關場流程**（`UCL_SessionCloseFlow`）—— TASK-0057 之後這裡不是唯一呼叫端了，
            //     「所有關場路徑走同一個門」從此靠**同一個函式**成立，不是靠「同一支 Cmd」。
            var aOutcome = await UCL_SessionCloseFlow.CloseAndSettleAsync(args, aTarget, aSession, aReason, aR, token);
            bool aSettled = aOutcome.Settled;
            if (!aOutcome.Closed)
            {
                Finish(args, aActor, aR, aKind, false, false);
                throw new Exception($"[SessionClose] `{aTarget}` 的 session 檔寫不進去（詳見回傳檔）");
            }

            // ③ 廣播：本 Cmd 不發 —— 補收工是行政動作，不是收工儀式（正常收工的公告在 step=end）。
            aR.AppendLine("- ③ 廣播：**略過**（補收工是行政動作；收工公告只在正常 step=end 發，不重複打擾同事）");

            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"- 回讀：`senate ucmd run SessionStatus --persona {aActor} --arg persona={aTarget}`（SessionStatus 那支的 persona ＝查誰）");
            aR.AppendLine("- ⚠ 回讀是必要的：本回傳檔說的是**我做了什麼**，不是**磁碟上現在長怎樣**。");
            Finish(args, aActor, aR, aKind, true, aSettled);
        }

        /// <summary>
        /// 那一 kind 的正常收工指令叫什麼（擋下時要把指令原文附上，不能只講「去收工」）。
        /// <para>⚠ 走登記表 —— 未登記的 kind 照實回 kind 本身，**不編一個看起來像指令的字**。</para>
        /// </summary>
        static string KindCmdName(string iKind) => UCL_SessionKindHost.CmdNameOf(iKind);

        /// <summary>回傳檔落 per-persona ＋ 機讀值（三個布林分開報，呼叫端不必解析內文）。</summary>
        static void Finish(Dictionary<string, string> iArgs, string iActor, StringBuilder ioR,
                           string iKind, bool iClosed, bool iSettled)
        {
            string aPayload = UCL_LettersPath.CmdPayload(iActor, "sessionclose", "last");
            Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
            File.WriteAllText(aPayload, ioR.ToString(), new UTF8Encoding(false));
            Debug.Log($"[SessionClose] actor={iActor} kind={iKind} closed={iClosed} settled={iSettled} → {aPayload}");
            UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPayload);
            UCL_AgentCommandRunner.ReportOutputValue(iArgs, "session_kind", iKind);
            UCL_AgentCommandRunner.ReportOutputValue(iArgs, "closed", iClosed ? "1" : "0");
            UCL_AgentCommandRunner.ReportOutputValue(iArgs, "settled", iSettled ? "1" : "0");
        }
    }
}
#endif
