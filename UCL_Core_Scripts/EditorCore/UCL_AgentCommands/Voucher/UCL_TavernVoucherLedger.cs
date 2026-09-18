// 區塊職責：酒館券的 Unity 端入口 —— **現在是一層薄殼**，全部委派 <see cref="UCL_VoucherAuthority"/>
//           （＝ `senate cmd voucher --arg voucher=tavern`，由銀行 Server 執行）。
// 物理意義：Tim 2026-09-18 拍板 —— 酒館券是**個人錢包**（另一本帳），面額與 token 1:1，
//           主動消費時自動先扣券。⇒ 它跟繪圖券一樣，落在 `letters/<persona>/vouchers/<券 id>.json`。
// 數值影響：**零**。本檔不再讀寫 `<DataRoot>/ChatTavern/agent_bonus_quota.json` ——
//           那份舊帳已於 2026-09-18 整批遷入新系統（15 人／2019 張，逐人零差額），
//           檔案留著當對帳基準，⛔ 但沒有寫入端了。
//
// 🩸 為什麼**發券端**也一起搬（同一天的血證）：
//   繪圖券那次是「消費端搬了、發放端沒搬」⇒ 每場發的 10 張限時券花不到、到期原地作廢，
//   四個人分岔而沒有任何一層喊。
//   ⇒ 而酒館券這裡**本來就有一個發放端**（`UCL_BooksIO` 打賞發雙券）——
//     只搬消費端的話，明天就是同一隻病的第二次。
//
// ⚠ **`bank` 參數保留但已無作用**：舊 schema 的鍵是 (bank, persona) 兩層，而券**綁 persona**。
//   ⇒ 簽名不動只為了讓既有呼叫端不必同時改；⛔ 傳什麼 bank 都指向同一本帳。
//   （遷移時同一個人散在好幾個 bank 底下 —— apex-one 三個、summit 兩個 —— 是跨 bank 加總過來的。）
using UCL.Core.EditorLib.AgentCommands.Voucher;

namespace UCL.Core.EditorLib.AgentCommands.Voucher
{
    /// <summary>酒館券（券 id ＝ <c>tavern</c>，個人錢包）—— 薄殼，全部委派 <see cref="UCL_VoucherAuthority"/>。</summary>
    public static class UCL_TavernVoucherLedger
    {
        /// <summary>券 id ＝ 檔名（`letters/&lt;persona&gt;/vouchers/tavern.json`）。</summary>
        public const string VoucherId = "tavern";

        /// <summary>
        /// 餘額（可花總額）。<paramref name="bank"/> ⛔ **已無作用** —— 券綁 persona，見檔頭。
        /// </summary>
        public static int GetBalance(string bank, string persona)
            => UCL_VoucherAuthority.GetBalance(persona, VoucherId).Spendable;

        /// <summary>
        /// 發券。<paramref name="bank"/> ⛔ **已無作用**。
        /// <para>回 (可花總額 before, after)。失敗 throw —— ⛔ 不降級寫舊檔。</para>
        /// </summary>
        public static (int before, int after) Grant(string bank, string persona, int amount,
                                                    string source, string refText)
            => UCL_VoucherAuthority.Grant(persona, VoucherId, amount, source, refText);

        /// <summary>
        /// 用券。⚠ 一般的消費**不必呼叫這支** —— 走 `UCL_TreasuryLedger.Pay`，
        /// 由 Server 依白名單自動先扣券（⛔ 兩邊各扣一次就是扣兩次）。
        /// </summary>
        public static (int before, int after) Consume(string persona, int amount, string source, string refText)
            => UCL_VoucherAuthority.Consume(persona, VoucherId, amount, source, refText);
    }
}
