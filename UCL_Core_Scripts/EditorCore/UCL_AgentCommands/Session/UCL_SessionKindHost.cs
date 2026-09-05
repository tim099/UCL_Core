// 區塊職責：**Editor 側「一種 kind 的宿主行為」登記表** —— 收工指令叫什麼、殘留要不要補結算。
// 物理意義：kind 的**名字**住 SCP_Core（`SCP_ActivitySessionKind`，兩個宿主共用）；
//          而「它的收工指令叫什麼」「補結算怎麼跑」是**Editor 才有的東西**（結算是金流，
//          金流不搬 —— TASK-0106 Tim 拍 B）。⇒ 名字在共用層，行為在宿主，兩邊各一份真相源。
//
//          🩸 為什麼要有這張表（TASK-0055 的最後一格，2026-09-05）：在此之前
//          `Cmd_SessionClose` 用 `if (IsStreamWatch(kind))` 決定要不要補結算，
//          `KindCmdName` 用一條 if 鏈決定收工指令名。⇒ **新增一種 kind 要回頭改
//          `Cmd_SessionClose`**，而漏改**不會報錯** —— 它會照常關場，然後印
//          「這個 kind 沒有登記結算 handler ⇒ 只翻三欄」。那句話在「真的沒有結算」與
//          「有結算但沒人來登記」兩種情況下**一模一樣**，而後者是酬勞蒸發。
//
// 數值影響：純記憶體字典，零 IO。登記由各 kind 自己在 `[InitializeOnLoadMethod]` 做
//          ⇒ **新增 kind ＝ 在那個 kind 自己的檔加一行**，不必動這裡，也不必動 Cmd_SessionClose。
//
// ⚠ 沒有登記 ≠ 沒有結算：本表把「查不到這個 kind」與「查得到但 SettleResidueAsync 是 null」
//   分成兩種答案，因為它們的處置不同（前者是有人忘了登記，後者是這個 kind 真的不用結算）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>一種 kind 在 Editor 這側的宿主行為。</summary>
    public sealed class UCL_SessionKindEntry
    {
        /// <summary>kind 值（用 <c>SCP_ActivitySessionKind</c> 的常數，不要打字串）。</summary>
        public string Kind = "";

        /// <summary>
        /// 這個 kind 的正常收工指令叫什麼（`senate ucmd run <這個名字> --arg step=end`）。
        /// <para>⚠ 擋下進行中的場時要把**指令原文**附上，所以這一格不能空。</para>
        /// </summary>
        public string CmdName = "";

        /// <summary>
        /// 這個 kind 有沒有 `step=end`。
        /// <para>⚠ 觀影**沒有** —— 它到期或 Tim 停錄影時由 Cmd 自己收工。
        /// 印一個不存在的指令比不印更糟，所以這一格要顯式說。</para>
        /// </summary>
        public bool HasStepEnd = true;

        /// <summary>
        /// 沒有 `step=end` 的 kind，**還有沒有別條真的能提前收的路**（空＝沒有）。
        /// <para>例：觀影 ⇒「要提前收就請 Tim 停錄影」。</para>
        /// </summary>
        /// <remarks>
        /// 🩸 這一格是 @summit 2026-09-05 在 TASK-0058 撿到的：我把出口 dispatch 改走登記表時，
        /// **靜默丟掉了這句 kind 專屬的處置知識** —— 讀的人從此只知道「等」，
        /// 不知道還有一條真的能提前收的路。⇒ **那是降級不是收斂。**
        /// 她把那條 `if` 刻意留著並寫明理由；正確的收斂是**讓登記表承載它**，不是刪掉它。
        /// </remarks>
        public string EarlyEndHint = "";

        /// <summary>
        /// 殘留補結算（`null` ＝ **這個 kind 真的不需要結算**，不是「還沒接」）。
        /// <para>簽章對齊 `Cmd_StreamWatch.SettleResidueAsync`：args／target／回傳檔 sb／token／reason。</para>
        /// </summary>
        public Func<IDictionary<string, string>, string, StringBuilder, CancellationToken, string, UniTask<bool>>
            SettleResidueAsync;
    }

    /// <summary>
    /// kind → 宿主行為。**新增一種 kind 就在它自己的檔案裡登記一次**，這裡不列名單。
    /// <code>
    /// [UnityEditor.InitializeOnLoadMethod]
    /// static void RegisterSessionKind()
    ///     =&gt; UCL_SessionKindHost.Register(new UCL_SessionKindEntry
    ///     {
    ///         Kind = SCP.Core.Session.SCP_ActivitySessionKind.FreeTime,
    ///         CmdName = "FreeTime",
    ///         SettleResidueAsync = null,   // 自由時間沒有金流結算
    ///     });
    /// </code>
    /// </summary>
    public static class UCL_SessionKindHost
    {
        static readonly Dictionary<string, UCL_SessionKindEntry> s_Entries
            = new Dictionary<string, UCL_SessionKindEntry>(StringComparer.Ordinal);

        /// <summary>登記（同 kind 再登記 ＝ 覆蓋，最後一個贏 —— 同 SCP_ActivitySessionGatewayHost 的形狀）。</summary>
        public static void Register(UCL_SessionKindEntry iEntry)
        {
            if (iEntry == null || string.IsNullOrEmpty(iEntry.Kind)) return;
            lock (s_Entries) s_Entries[iEntry.Kind] = iEntry;
        }

        /// <summary>查一種 kind。**回 null ＝ 沒有人登記過它**（跟「登記了但不用結算」不同形）。</summary>
        public static UCL_SessionKindEntry For(string iKind)
        {
            if (string.IsNullOrEmpty(iKind)) return null;
            lock (s_Entries)
            {
                return s_Entries.TryGetValue(iKind, out UCL_SessionKindEntry aOut) ? aOut : null;
            }
        }

        /// <summary>目前登記了哪些 —— 回報「查不到」時要一起印，否則「沒登記」會被讀成「不存在」。</summary>
        public static string[] RegisteredKinds()
        {
            lock (s_Entries)
            {
                var aKeys = new string[s_Entries.Count];
                s_Entries.Keys.CopyTo(aKeys, 0);
                return aKeys;
            }
        }

        /// <summary>
        /// 收工指令名（擋下時要附指令原文）。**沒登記就照實回 kind 本身**，
        /// ⛔ 不要編一個看起來像指令的字 —— 那會讓人去跑一個不存在的東西。
        /// </summary>
        public static string CmdNameOf(string iKind)
        {
            UCL_SessionKindEntry aE = For(iKind);
            return aE != null && !string.IsNullOrEmpty(aE.CmdName) ? aE.CmdName : iKind;
        }
    }
}
#endif
