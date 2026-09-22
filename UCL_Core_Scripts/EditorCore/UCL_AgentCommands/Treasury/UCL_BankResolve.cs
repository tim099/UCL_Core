// 區塊職責：**宿主轉接** —— 把 Unity 這一側的三個根（letters／data／region）餵給
//           `SCP_BankAccountResolver`，讓呼叫端不必各自去湊。
// 物理意義：解析規則**一行都不在這裡** —— 本檔零邏輯，只有路徑。
//           規則的唯一實作在 `SCP_Core/Runtime/Bank/SCP_BankAccountResolver.cs`（TASK-0269）。
// 數值影響：無。純轉接。
//
// ⛔ 這不是退場薄殼，⛔ 也不是墓碑 —— 它有一份活的職責：
//   SCP_Core **不准知道任何宿主的安裝位置**（寫死就跨專案漂），所以路徑一定要有人供。
//   而那個「有人」必須**只有一個** ——
//   🩸 每多一個呼叫端自己湊根，就多一個會在下次改制時悄悄過期的地方，
//     而它過期的樣子是「那個帳號突然不存在」，跟帳號真的不存在逐字同形。
//
// ⚠ 要改解析規則 ⇒ 去改 SCP_Core 那支。在這裡加一個 if，就是把三份實作變回來。
#if UNITY_EDITOR
using System.Collections.Generic;
using SCP.Core.Bank;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>帳號解析（宿主轉接）。規則本體在 <see cref="SCP_BankAccountResolver"/>。</summary>
    public static class UCL_BankResolve
    {
        /// <summary>persona 信件夾根。</summary>
        public static string LettersRoot => UCL_LettersPath.Root;

        /// <summary>AgentCommands 資料根。</summary>
        public static string DataRoot => UCL_AgentCommandsPath.DataRoot;

        /// <summary>本專案的區域（貨幣）ID ＝ `letters/&lt;persona&gt;/bank/&lt;此值&gt;.md` 的檔名。</summary>
        public static string Region => UCL_CentralBankSettings.CurrencyId;

        public static void Invalidate() => SCP_BankAccountResolver.Invalidate();

        /// <summary>任何字串（persona／agent／別名／帳號）→ 正式帳號。</summary>
        public static SCP_BankResolution Resolve(string iInput)
            => SCP_BankAccountResolver.Resolve(LettersRoot, DataRoot, Region, iInput);

        /// <summary>persona → 帳戶（**只認 persona**）。查不到回空字串 —— ⛔ 不 derive。</summary>
        public static string ResolvePersonaAccount(string iPersona)
            => SCP_BankAccountResolver.ResolvePersonaAccount(LettersRoot, DataRoot, Region, iPersona, out _);

        public static string ResolvePersonaAccount(string iPersona, out string oTrace)
            => SCP_BankAccountResolver.ResolvePersonaAccount(LettersRoot, DataRoot, Region, iPersona, out oTrace);

        public static bool IsClosed(string iAccountId, out string oReason)
            => SCP_BankAccountResolver.IsClosed(LettersRoot, DataRoot, Region, iAccountId, out oReason);

        public static Dictionary<string, string> GetClosedAccounts()
            => SCP_BankAccountResolver.GetClosedAccounts(LettersRoot, DataRoot, Region);

        public static bool IsCanonicalAccount(string iAccountId)
            => SCP_BankAccountResolver.IsCanonicalAccount(LettersRoot, DataRoot, Region, iAccountId);

        /// <summary>綁在某帳號底下的 persona 清單（**由正向綁定檔導出**）。</summary>
        public static List<string> GetBoundPersonas(string iAccountId)
            => SCP_BankAccountResolver.GetBoundPersonas(LettersRoot, DataRoot, Region, iAccountId);

        /// <summary>銷戶 —— `closed_accounts` 的唯一寫入端。⚠ 不動 ledger、不搬錢。</summary>
        public static bool CloseAccount(string iAccountId, string iReason, string iRenamedTo,
                                        string iActor, out string oError)
            => SCP_BankAccountResolver.CloseAccount(LettersRoot, DataRoot, Region,
                                                    iAccountId, iReason, iRenamedTo, iActor, out oError);
    }
}
#endif
