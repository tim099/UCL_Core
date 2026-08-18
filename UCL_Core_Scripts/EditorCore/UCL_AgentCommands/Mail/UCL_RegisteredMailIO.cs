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
        public const string SystemSender = "tavern-keeper";

        const string MailboxDirName = "mailbox";   // 收件者端（投遞用）
        const string OutboxDirName = "outbox";     // 寄件者端（存證用）

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
        public static bool Send(string from, string to, string subject, string body,
                                int fee, string feeRef, int? deliverAtWake)
        {
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(from)) return false;
            if (string.IsNullOrWhiteSpace(body)) return false;

            from = from.Trim();
            to = to.Trim();
            // ⚠ ts 格式必須是 py 的 "%Y%m%dT%H%M%SZ" —— ack 靠 `檔名.split("__")[0]` 反查 outbox 副本，
            //   格式一變，已讀回執就悄悄對不上（信照讀，寄件者永遠等不到回執）。
            string ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

            string content = BuildContent(from, to, subject, body, fee, feeRef, deliverAtWake);
            try
            {
                string mailbox = Path.Combine(LettersRoot, to, MailboxDirName);
                string outbox = Path.Combine(LettersRoot, from, OutboxDirName);
                Directory.CreateDirectory(mailbox);
                Directory.CreateDirectory(outbox);
                AtomicWrite(Path.Combine(mailbox, $"{ts}__from_{from}.md"), content);
                AtomicWrite(Path.Combine(outbox, $"{ts}__to_{to}.md"), content);
                Debug.Log($"[RegisteredMail] 📮 @{from} → @{to}｜{subject}"
                          + (deliverAtWake.HasValue ? $"（投遞 wake #{deliverAtWake.Value}）" : "（下次醒來）"));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RegisteredMail] 掛號信寫入失敗（主操作已完成，未回滾）：@{from} → @{to}：{ex.Message}");
                return false;
            }
        }

        // 區塊職責：組出 py `_read_fm()` 讀得懂的 frontmatter + 人讀得懂的信件本文
        // 邊界：frontmatter 是「一行一個 `k: v`」的極簡格式，**值不可含換行**（py 逐行 partition(":")）。
        //      subject 因此壓成單行；多行內容一律留在 body。
        static string BuildContent(string from, string to, string subject, string body,
                                   int fee, string feeRef, int? deliverAtWake)
        {
            string subj = Flatten(subject);
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("type: registered_mail");
            sb.AppendLine($"from: {from}");
            sb.AppendLine($"to: {to}");
            sb.AppendLine($"sent_at: {DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z");
            sb.AppendLine($"fee: {fee}");
            sb.AppendLine($"fee_ref: {Flatten(feeRef)}");
            if (!string.IsNullOrEmpty(subj)) sb.AppendLine($"subject: {subj}");
            if (deliverAtWake.HasValue) sb.AppendLine($"deliver_at_wake: {deliverAtWake.Value}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# 📮 掛號信 — 寄件者 @{from} → 收件者 @{to}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(subj)) { sb.AppendLine($"**主旨**：{subj}"); sb.AppendLine(); }
            sb.AppendLine(deliverAtWake.HasValue ? $"**投遞時點**：wake #{deliverAtWake.Value}" : "**投遞時點**：下次醒來");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(body.Trim());
            return sb.ToString();
        }

        /// <summary>壓成單行 —— frontmatter 的值含換行會把後面的欄位全部吃掉。</summary>
        static string Flatten(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Trim();

        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            // UTF8 無 BOM —— py 端以 encoding="utf-8" 讀，BOM 會混進 frontmatter 的第一個 "---"
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
#endif
