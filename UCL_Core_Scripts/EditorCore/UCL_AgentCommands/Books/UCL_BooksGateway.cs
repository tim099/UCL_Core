// 區塊職責：`SCP_IBooksGateway` 的 **Editor 端實作** —— 把書店本體要的四格宿主能力接上 Unity。
// 物理意義：本體（`SCP_BooksOps`）住在 SCP_Core，它只認得檔案與規則；
//           而「動錢、發券、寫 log、投遞續寫包」在 Editor 與 Senate Server 上是兩條不同的路。
//           ⇒ 本檔是 Editor 那一條。
// 數值影響：錢走 `UCL_TreasuryLedger.Pay`（權威＝新銀行，主動消費自動先扣酒館券）；
//           券走 `UCL_VoucherAuthority`（券 id 由本體給：`canvas` / `tavern`）。
//           ⛔ 本層**不判斷**哪些 kind 算主動消費 —— 那條規則只住在 Server 的 `SCP_SpendPolicy`。
//
// 🩸 判準：
//   ① **失敗一律讓例外往上飛**（介面判準②）。吞掉的話留下的是「書登記了、錢沒扣」，
//      而那張登記之後沒有人會回來看。
//   ② **`Warn` 真的要印**。本體有兩處刻意 fail-soft（券發不出去記 pending、續寫包投遞失敗）——
//      那些地方少了一行字，「沒發生」與「發生了但沒成功」就同形了。
//   ③ **型別邊界只有一個點**：`SCP_JsonData` → 文字 → `JsonData`（續寫包那支吃 Unity 的型別）。
//      收成一個點的理由是**它可驗**，⛔ 不在兩邊各自維護一組欄位對應。
#if UNITY_EDITOR
using System;
using SCP.Core.Books;
using SCP.Core.Json;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    /// <summary>書店本體在 Editor 這一側的宿主能力。</summary>
    public sealed class UCL_BooksGateway : SCP_IBooksGateway
    {
        public (int Voucher, int Token) Pay(string iBank, string iWalletPersona, int iAmount, string iKind,
                                            string iRef, string iDescription, string iIdemKey)
        {
            // ⚠ 錢包綁 persona，而捐贈那一支的 persona 可以是空的 ⇒ 沒有錢包可扣，走純 token。
            //   （出聲那一行由本體負責 —— 它才知道這是哪一支的哪一筆。）
            if (string.IsNullOrEmpty(iWalletPersona))
            {
                Treasury.UCL_TreasuryLedger.Debit(
                    accountId: iBank, amount: iAmount, useKind: iKind, useRef: iRef,
                    description: iDescription, callerAgentId: iBank, cmdId: iIdemKey,
                    idempotencyKey: iIdemKey);
                return (0, iAmount);
            }
            return Treasury.UCL_TreasuryLedger.Pay(
                accountId: iBank, walletPersona: iWalletPersona, amount: iAmount, useKind: iKind,
                useRef: iRef, description: iDescription, callerAgentId: iBank, cmdId: iIdemKey,
                idempotencyKey: iIdemKey);
        }

        public void GrantVoucher(string iPersona, string iVoucherId, int iAmount, string iSource, string iRef)
            => Voucher.UCL_VoucherAuthority.Grant(iPersona, iVoucherId, iAmount, iSource, iRef);

        public void Warn(string iMessage) => Debug.LogWarning(iMessage);

        // 判準③：型別邊界收在這一個點上。
        public string DeliverDossier(string iBook, string iAuthorPersona, SCP_JsonData iEntry, out string oError)
        {
            oError = "";
            try
            {
                JsonData aEntry = JsonData.ParseJson(SCP_JsonWriter.Write(iEntry, iIndented: false));
                return UCL_BookDossier.Deliver(iBook, iAuthorPersona, aEntry, out oError);
            }
            catch (Exception e)
            {
                // ⚠ 這一步**非致命**（書已經登記了）⇒ 回 null ＋ 理由，⛔ 不往上丟例外。
                oError = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }
    }
}
#endif
