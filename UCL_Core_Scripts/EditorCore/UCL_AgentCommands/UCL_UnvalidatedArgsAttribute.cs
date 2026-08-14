// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/14 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：把「這個 Cmd 刻意不做參數驗證」變成一個**帶署名的主張**。
// 物理意義：在此之前 `ArgsSpec == null` 同時表示兩件事 —— 「明示不驗」與「還沒宣告」。
//          同一個 null 兩種意思，於是兩者長得一模一樣（apex-one 2026-08-14 Q1；
//          與 summit 早上撞的 `total_messages=0` 同時表示「沒有錯」與「沒有編」同構）。
//          2026-08-14 拍板：**null = 還沒宣告（未決）**，「明示不驗」改由本 attribute 表達。
// 數值影響：**不改變執行期行為** —— 有沒有這個 attribute，未宣告 ArgsSpec 的 Cmd 都照樣通過驗證。
//          它唯一的作用是讓**診斷提示**知道該不該出現（見 UCL_CmdArgsValidator.DescribeSpecState）。
//
// ⚠ 為什麼一定要帶理由（Reason 是必填的建構參數）：
//   sentinel 物件與 bool 旗標都能表達「不驗」，但**存不下「為什麼」**。
//   而「為什麼」正是唯一能區分「想清楚了所以不驗」與「還沒想」的東西 ——
//   沒有理由的 opt-out 過三個月之後，跟忘了宣告長得一模一樣，等於沒有解決原本那個問題。
//   （形式由 gura 2026-08-14 提案，理由必填由 apex-one 拍板時加上。）
//
// ⚠ 這個 attribute **不是** 用來讓 Cmd 逃過驗證的捷徑：
//   未宣告 ArgsSpec 本來就不會被驗。掛上它只表示「我看過這個 Cmd，並認為它不需要 Required」。
//   要有驗證請去宣告 ArgsSpec，不是來掛這個。
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 標記於 <see cref="UCL_AgentCommandHandlerBase"/> 子類：**刻意**不宣告 ArgsSpec（不做參數驗證）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class UCL_UnvalidatedArgsAttribute : Attribute
    {
        /// <summary>為什麼這個 Cmd 不需要參數驗證（必填 —— 見型別註解）。</summary>
        public string Reason { get; private set; }

        public UCL_UnvalidatedArgsAttribute(string iReason)
        {
            Reason = iReason;
        }
    }
}
#endif
