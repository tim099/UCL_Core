// 區塊職責：繪圖券的 Unity 端入口 —— **現在是一層薄殼**，全部委派 <see cref="UCL_VoucherAuthority"/>
//           （＝ `senate cmd voucher --arg voucher=canvas`，由銀行 Server 執行）。
// 物理意義：2026-09-18（TASK-0243）券系統全面遷到新銀行，Tim 拍板兩件事 ——
//           ① 券的讀寫**全面**走新銀行；② 券＝**以 id 區分的貨幣**，繪圖券只是 id `canvas`。
//           ⇒ 本類保留只是為了讓既有呼叫端（FreeTime／Sculpture／Books／BankAdminPage）
//             不必同時改名，⛔ 它已經**不再擁有任何資料**。
// 數值影響：**零**。本檔不再讀寫 `<DataRoot>/Canvas/vouchers/<persona>.json` ——
//           那個目錄與其 history 由 git 保管，之後整批退場（TASK-0242）。
//
// 🩸 為什麼整個搬走而不是「發放端補一下」（2026-09-18 實測，這隻病就是這樣長出來的）：
//   消費端先搬到新系統、發放端留在這裡 ⇒ 每場自由時間發的 10 張限時券花不到、
//   到期原地作廢，而舊帳面上它看起來還在。四個人分岔，且每一位的差額**正好等於那批死掉的限時券**
//   （Sirius 113/103、apex-one 99/89、basecamp 67/57、calli 9/0）。
//   ⛔ 沒有任何一層會喊 —— 兩邊都是合法數字。
//   ⇒ 修法不是「把發放端也改掉」（那只是把同一個賭注再押一次），
//     是**讓 Unity 這側不存在第二個寫入端**。
//
// ⚠ 已移除：`TryGetUsageFromHistoryByRef`。新系統**不記歷史**（Tim 拍板）⇒ 它在結構上沒有資料可讀。
//   ⇒ 批次被清掉之後，「本場用了幾張」只能答**查無**（＝「我不知道」，⛔ 不是「沒用」）。
//   那是「不留歷史」的**已知代價**，不是漏掉的一格。
using UCL.Core.EditorLib.AgentCommands.Voucher;

namespace UCL.Core.EditorLib.AgentCommands.CanvasVoucher
{
    /// <summary>繪圖券（券 id ＝ <c>canvas</c>）—— 薄殼，全部委派 <see cref="UCL_VoucherAuthority"/>。</summary>
    public static class UCL_CanvasVoucherLedger
    {
        /// <summary>券 id。⚠ 這是**檔名**（`letters/&lt;persona&gt;/vouchers/canvas.json`）。</summary>
        public const string VoucherId = UCL_VoucherAuthority.VoucherCanvas;

        // ===========================================================
        // 區塊：讀 —— 三個問題、三個 API（刻意沒有一個叫「GetBalance」的單數）
        // ===========================================================

        /// <summary>**永久券**餘額。查「這個人存了多少券」用這支。</summary>
        public static int GetPermanent(string persona)
            => UCL_VoucherAuthority.GetBalance(persona, VoucherId).Permanent;

        /// <summary>**未過期的限時券**餘額。查「本場還剩幾顆免費像素」用這支。</summary>
        public static int GetExpiring(string persona)
            => UCL_VoucherAuthority.GetBalance(persona, VoucherId).Expiring;

        /// <summary>**可花總額**（未過期限時 ＋ 永久）。規劃付款用這支。</summary>
        public static int GetSpendable(string persona)
            => UCL_VoucherAuthority.GetBalance(persona, VoucherId).Spendable;

        /// <summary>
        /// 某一批（按 <paramref name="refText"/>）**還花得掉**的張數。
        /// <para>⚠ `ref` 空 ⇒ 回 0（⛔ 不回總額 —— 那是拿「沒指定」冒充「全部」）。</para>
        /// </summary>
        public static int GetExpiringByRef(string persona, string refText)
            => UCL_VoucherAuthority.GetAliveByRef(persona, VoucherId, refText);

        /// <summary>
        /// 某一批（按 <paramref name="refText"/>）的用量三值：發了幾張／還剩幾張／用了幾張。
        /// <para>🩸 回 <c>false</c> ＝ **「我不知道」，⛔ 不是「一張都沒用」** ——
        /// 三個 out 值一律 0，讓「發放量 − 0」這個減法**在物理上拿不到數字**（TASK-0195）。</para>
        /// </summary>
        public static bool TryGetUsageByRef(string persona, string refText,
                                            out int granted, out int remain, out int used)
            => UCL_VoucherAuthority.TryGetUsage(persona, VoucherId, refText, out granted, out remain, out used);

        // ===========================================================
        // 區塊：寫
        // ===========================================================

        /// <summary>
        /// 發券：<paramref name="iExpiresAtIso"/> 空 ＝ 永久券，非空 ＝ 限時券。
        /// <para>回 (可花總額 before, after)。失敗 throw —— ⛔ 不降級寫舊檔。</para>
        /// </summary>
        public static (int before, int after) Grant(string persona, int amount, string source, string refText,
                                                    string iExpiresAtIso = null)
            => UCL_VoucherAuthority.Grant(persona, VoucherId, amount, source, refText, iExpiresAtIso);

        /// <summary>
        /// 用券 —— **先花快過期的**（排序在 Server 那一側，本層不重做一次）。
        /// <para>餘額不足 throw（⛔ 不部分扣款 —— 半扣的帳沒有人能對）。</para>
        /// </summary>
        public static (int before, int after) Consume(string persona, int amount, string source, string refText)
            => UCL_VoucherAuthority.Consume(persona, VoucherId, amount, source, refText);
    }
}
