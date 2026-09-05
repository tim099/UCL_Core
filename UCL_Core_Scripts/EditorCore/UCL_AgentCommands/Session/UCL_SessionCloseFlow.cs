// 區塊職責：**關場的那兩段本體**（① 權威狀態＋回讀確認 ② 查登記表補結算）—— 只有這一份實作。
// 物理意義：TASK-0055 要的「所有關場路徑走同一個門」，在 2026-09-05 之前是**同一支 Cmd**
//          （`Cmd_SessionClose`）；而 TASK-0057（晚安自動關）是**第二個呼叫端**
//          ⇒ 那句話從此要靠「同一個函式」成立，不能靠「同一支 Cmd」——
//          🩸 否則第二個呼叫端就是第二份實作，而兩份會漂：漂掉的症狀是
//          「晚安關掉的場沒有結算」，而它跟正常收工在畫面上一模一樣（那正是 0055 的病灶本身）。
// 數值影響：寫一次 session 檔（`Close`）＋ 可能**發薪**（該 kind 登記了 `SettleResidueAsync` 時）。
//          ⛔ 本層**不做守衛**：confirm、殘留 vs 進行中、要不要廣播 —— 那些是呼叫端的事，
//          因為它們逐呼叫端不同（補收工只收殘留；晚安要連進行中的一起收）。
//
// ⚠ 次序不可換：**權威狀態先落地，再結算**。先結算再翻狀態的話，結算成功而狀態沒寫
//   ⇒ 下次再結算一次（重複發薪不會有人喊，而帳對不上時沒有人查得出是這裡）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>關場的結果 —— **三個布林分開回**，呼叫端不必解析內文。</summary>
    public struct UCL_SessionCloseOutcome
    {
        /// <summary>權威狀態真的落地了嗎（**回讀確認過**，不是「呼叫沒丟例外」）。</summary>
        public bool Closed;

        /// <summary>結算真的跑了嗎。⚠ `false` 有三種成因，內文有寫：沒登記／登記為不需要／跑了但失敗。</summary>
        public bool Settled;

        /// <summary>那一場的 kind（給呼叫端回報用）。</summary>
        public string Kind;
    }

    /// <summary>
    /// 關掉一場 session：① 權威狀態＋回讀確認 ② 查 <see cref="UCL_SessionKindHost"/> 補結算。
    /// <para>⚠ 呼叫前請自己決定「這場該不該關」—— 本層不判斷殘留或進行中。</para>
    /// </summary>
    public static class UCL_SessionCloseFlow
    {
        public static async UniTask<UCL_SessionCloseOutcome> CloseAndSettleAsync(
            IDictionary<string, string> iArgs, string iTarget,
            SCP.Core.Session.SCP_ActivitySession iSession, string iReason,
            StringBuilder ioR, CancellationToken iToken)
        {
            var aOut = new UCL_SessionCloseOutcome
            {
                Kind = string.IsNullOrEmpty(iSession.kind) ? "(未標 kind)" : iSession.kind,
            };

            // ① 權威狀態先落地。
            SCP.Core.Session.SCP_ActivitySessionStore.Close(
                UCL_AgentCommandsPath.ScpDataRoot, iTarget, iSession, iReason);
            var aReadBack = SCP.Core.Session.SCP_ActivitySessionStore.Load(
                UCL_AgentCommandsPath.ScpDataRoot, iTarget);
            aOut.Closed = aReadBack != null && !aReadBack.active;
            ioR.AppendLine($"- ① 權威狀態：active=false／end_reason=`{iReason}`／ended_at=`{aReadBack?.ended_at}`"
                           + $"　**回讀確認={aOut.Closed}**");
            if (!aOut.Closed)
            {
                ioR.AppendLine("- ⛔ 狀態沒落地 ⇒ **不跑結算**（避免結算完狀態沒寫、下次再結一次）");
                return aOut;
            }

            // ② 結算：per-kind，走登記表不是 if 鏈（TASK-0055）。
            //    ⚠ 三種「沒結算」要**不同形** —— 它們的處置不同：
            //      沒登記 ⇒ 去補登記／登記為不需要 ⇒ 什麼都不用做／跑了但失敗 ⇒ 去查那個例外。
            UCL_SessionKindEntry aEntry = UCL_SessionKindHost.For(iSession.kind);
            if (aEntry == null)
            {
                ioR.AppendLine($"- ② 結算：⚠ **這個 kind（{aOut.Kind}）沒有人登記過** ⇒ 只翻三欄。"
                               + $"已登記的：{string.Join(" / ", UCL_SessionKindHost.RegisteredKinds())}");
                ioR.AppendLine("    ⚠ 這**不是**「這個 kind 不用結算」——「不用結算」是登記表裡的一個顯式答案。"
                               + "新增 kind 時漏了登記，症狀就長這樣。");
                return aOut;
            }
            if (aEntry.SettleResidueAsync == null)
            {
                ioR.AppendLine($"- ② 結算：這個 kind（{aOut.Kind}）**登記為不需要結算** ⇒ 只翻三欄（顯式，不是漏接）");
                return aOut;
            }
            try
            {
                aOut.Settled = await aEntry.SettleResidueAsync(iArgs, iTarget, ioR, iToken, iReason);
            }
            catch (Exception e)
            {
                // 🩸 結算炸掉**不得冒充關場失敗**（0043/0044 那族：回報層炸掉冒充主動作失敗）。
                ioR.AppendLine($"- ② 結算：**失敗** —— {e.GetType().Name}: {e.Message}");
                ioR.AppendLine("    ⚠ 而場**已經關了**（① 回讀確認過）—— 這兩件事分開看。");
                return aOut;
            }
            if (aOut.Settled) ioR.AppendLine("- ② 結算：**已跑**（台帳 append ＋ 發薪，走與補收工同一條路）");
            return aOut;
        }
    }
}
#endif
