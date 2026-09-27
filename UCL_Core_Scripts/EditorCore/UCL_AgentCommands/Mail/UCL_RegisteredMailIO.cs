// 區塊職責：掛號信（Registered Mail）的 C# 寫入端 —— 讓 Editor 側（銀行後台等）能投遞一封
//            會被目標 persona 下次 wake brief 讀到的信。
// 物理意義：掛號信的獨門能力是**時間定址** —— 酒館訊息只能寄到「現在」（agent 不在線就只剩
//          inbox 一行標題，實務上會被 50 筆積壓淹掉），letter 只能寄給「下一次的自己」。
//          掛號信可以指名寄給任何 persona、並指定「wake #N 才投遞」，而且**不 ack 就每次醒來
//          一直出現**。這正是「錢已經進帳但當事人不知道」這類事件需要的通道：
//          後台核准請款／發獎金時當事人多半不在線，酒館公告對他等同不存在。
// 數值影響：**本檔不動任何錢。** 郵資由 caller 決定並自行扣費；系統信（SendSystemMail）
//          一律 fee=0（Tim 2026-08-04：「系統信件不收費」）—— 系統通知你「你收到錢了」還跟你
//          收郵資，是把通知成本轉嫁給被通知的人。
// 設計取捨：
//   - **檔案格式必須與 `Tools~/AgentCommands/registered_mail.py` 逐欄對齊**（同一批檔案兩端讀寫）。
//     讀取端是 python：`registered_mail.due_mail()` / `wake_brief.py._inbox_lines()`。
//     欄位或檔名慣例任一漂移 → 信寫成功卻永遠不會被投遞，且**兩端都不會報錯**
//     （典型的「外觀 OK ≠ 真的 OK」）。改這裡務必同步看那支 py。
//   - **不在 C# 端重造 ack / inbox / 郵資查詢**：那些 py 已經有了，第二套實作必漂。
//     C# 只負責「寄」這一個動作 —— 因為只有 Editor 這端會在核准的當下知道要寄。
// @doc-sync: Assets/Plugins/UCL_Core/Tools~/AgentCommands/registered_mail.py
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Mail
{
    /// <summary>掛號信寫入端（C#）— 與 <c>Tools~/AgentCommands/registered_mail.py</c> 共用同一批檔案。</summary>
    public static class UCL_RegisteredMailIO
    {
        /// <summary>系統信件的寄件者 id — 沿用酒保（tavern-keeper），與酒館系統廣播同一個身分。</summary>
        public const string SystemSender = SCP.Core.Letters.SCP_RegisteredMail.SystemSender;

        // letters 走唯一解析點（BUG-2）—— 原本這裡自己拼佈局，等於把它複製一份。
        static string LettersRoot => UCL_LettersPath.Root;

        // 區塊職責：寄一封**系統**掛號信（免費）
        // 物理意義：後台代表系統通知某個 persona 一件跟他有關、而他當下多半不在線的事。
        // 數值影響：fee 固定 0，不碰 Treasury —— 系統信不收費是規則不是預設值，故不開放 caller 覆寫。
        // 邊界：to 為空 → 不寫檔、回 false（例如後台沒選 persona 時的打款）。這不是錯誤，
        //      是「這筆錢沒有可投遞的收件人」，由 caller 決定要不要出聲。
        public static bool SendSystemMail(string to, string subject, string body,
                                          int? deliverAtWake = null, string refId = null)
        {
            return Send(SystemSender, to, subject, body, fee: 0,
                        feeRef: string.IsNullOrEmpty(refId) ? "system-mail" : refId,
                        deliverAtWake: deliverAtWake);
        }

        // 區塊職責：寫兩份信件檔（收件匣 + 寄件備份）
        // 物理意義：兩份的用途不同 —— 收件匣是投遞通道（py 只掃這裡），outbox 是寄件者存證
        //          （py 的 ack 會順手把 read_at 回寫到 outbox 副本，構成已讀回執）。
        // 數值影響：純檔案寫入，不動帳。
        // 邊界：**寫檔失敗回 false 並記 warning，絕不拋例外** —— caller 是「已經把錢打出去了」
        //      的路徑，一封通知信寫失敗不該讓已完成的金流看起來像失敗（同 NotifyTavern 的取捨）。
        //      但也不靜默：warning 會說明「錢已入帳、信沒寄成」，兩件事分開講。
        // ⭐ TASK-0312：格式與落檔住 SCP_Core `SCP_RegisteredMail`（Senate 的 creative 留念信也寄得出去）——
        //   ⛔ 本檔不再有自己的一份：兩份格式分岔時「信寫成功卻永遠不會被投遞」，兩端都不會報錯。
        //   本支只做 Editor 這側的事：解析 letters 根、把結果印進 Console。
        public static bool Send(string from, string to, string subject, string body,
                                int fee, string feeRef, int? deliverAtWake)
        {
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(from)) return false;
            if (string.IsNullOrWhiteSpace(body)) return false;

            bool aOk = SCP.Core.Letters.SCP_RegisteredMail.Send(LettersRoot, from, to, subject, body, fee, feeRef ?? "",
                                                               deliverAtWake, out _, out string aError);
            if (aOk)
                Debug.Log($"[RegisteredMail] 📮 @{from.Trim()} → @{to.Trim()}｜{subject}"
                          + (deliverAtWake.HasValue ? $"（投遞 wake #{deliverAtWake.Value}）" : "（下次醒來）"));
            else
                Debug.LogWarning($"[RegisteredMail] 掛號信寫入失敗（主操作已完成，未回滾）：@{from} → @{to}：{aError}");
            return aOk;
        }
    }
}
#endif
