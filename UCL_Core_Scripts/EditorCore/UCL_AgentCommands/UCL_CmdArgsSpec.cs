// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 07/29 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：Agent Cmd 的**機器可讀**參數規格 — 供 reflection 匯出成 commands_schema.json，
//          讓 Python client 端預檢不必再手抄一份。
// 物理意義：既有的 ArgsSchema 是自由文字（給人看），無法被程式解析。本型別是它的機器可讀對應物。
//          兩者並存且職責分明：ArgsSchema 寫「這個參數是什麼意思」（人讀），
//          ArgsSpec 寫「哪些必填、哪些是別名」（機器讀）。
//          設計依據：Docs~/zh-Hant/Plan/Plan_AgentCmd_Schema_Reflection_Export.md
// 數值影響：純資料容器，不含任何行為；建構後即唯讀（欄位由 handler 的 property 初始化式一次填好）。
//
// ⚠ 只宣告「Python 端真的會拿來做判斷」的東西 —— Required 與 Aliases。
//   刻意**不收 optional**：Python 端從來沒讀過它，而沒人用的欄位一定會爛
//   （血證：Python 表的 post.optional 少了 persona，錯很久沒人發現，因為它不被 enforce）。
//   參數說明請寫在 ArgsSchema，不要塞進本型別。
//
// ⚠ 本檔**刻意不加 `#if UNITY_EDITOR` guard**，理由同 UCL_AgentCommandHandlerBase.cs（2026-05-13 Zeta）：
//   該基底類別沒有 guard（零 Editor API，純 abstract + helper），而它的 ArgsSpec 屬性回傳本型別 ——
//   本檔若加 guard，player build 會因為找不到型別而編譯失敗。保留 EditorCore/ 路徑慣例，不加隔離。
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 單一 op（子命令）的參數規格。只有具備子 op 的 Cmd（目前僅 Tavern）會用到。
    /// </summary>
    public class UCL_CmdOpSpec
    {
        /// <summary>必填參數名（canonical 名，非別名）。**空字串算缺**（見 RequiredPresent）。</summary>
        public string[] Required = System.Array.Empty<string>();

        // ===========================================================
        // 區塊職責：**必須在場、但值可以是空的**參數（BUG-15，2026-08-19 kiara）。
        // 物理意義：`Required` 的判準是「有值」（空字串算缺）—— 對絕大多數參數是對的，
        //          因為 `--arg foo=` 通常是打錯。但有些參數**空值本身就是合法輸入**
        //          （例：`PersonaProfile op=set` 要把一個欄位清空 ⇒ `value=`）。
        //          這兩種需求用同一個欄位表達不出來，於是每支這樣的 Cmd 都被迫二選一：
        //            · 放進 Required  ⇒ 合法的空值進不來（BUG-15）
        //            · 不放進 Required ⇒ 參數名打錯時靜默走預設值（BUG-14）
        //          🩸 這兩張單是同一天開的、**同一個表達力缺口的兩面**：
        //             BUG-14 的修法把 value 放進 Required，於是當天就長出 BUG-15。
        //          ⇒ 判準改成「在場 vs 有值」兩種，各自有欄位可宣告。
        //          這就是本案 §8.7「兩態不得同形」下移到參數層：
        //          **「沒給」與「給了空的」是兩件事，把它們壓成一件的驗證器會擋掉一半的合法輸入。**
        //
        // ⚠ 刻意**不匯出到 commands_schema.json**：本檔開頭那條規矩是
        //   「只宣告 Python 端真的會拿來做判斷的東西，沒人用的欄位一定會爛」。
        //   目前 python 端只有 `tavern_cmd.py` 會 enforce required，而 Tavern 沒有任何
        //   「空值合法」的參數 ⇒ 現在匯出就是加一個沒有消費端的欄位。
        //   **哪天 Tavern（或別的有 python 預檢的 Cmd）需要它，匯出與 python 端一起做**
        //   —— 順帶提醒：`tavern_cmd.py` 的 required 檢查用的是 `not arg_pairs.get(r)`，
        //   跟這裡修掉的是同一種 falsy 判準，那時要一起改。
        // 數值影響：預設空陣列 ⇒ 不宣告的 Cmd 行為與本次改動前逐字相同。
        // ===========================================================
        public string[] RequiredPresent = System.Array.Empty<string>();

        /// <summary>
        /// 別名對照：alias → canonical。
        ///
        /// <para>⚠ **宣告順序即優先序**。client 端歸一是「先到先得，後到者視為重複而丟棄」，
        /// 所以此處的順序必須對齊 handler 內 <c>GetArg(a,"x", GetArg(a,"y", ...))</c> 的巢狀順序
        /// —— 外層先取的那個要排在前面。順序錯不會報錯，會**安靜地選錯值**。</para>
        /// </summary>
        public Dictionary<string, string> Aliases = new Dictionary<string, string>();
    }

    /// <summary>
    /// 一個 Agent Cmd 的機器可讀參數規格。
    ///
    /// <para>兩種形態：</para>
    /// <list type="bullet">
    /// <item><b>扁平 Cmd</b>（絕大多數）：只填 <see cref="Required"/> / <see cref="Aliases"/>，<see cref="Ops"/> 留空。</item>
    /// <item><b>有子 op 的 Cmd</b>（目前僅 Tavern）：填 <see cref="Ops"/>；
    ///       <see cref="Required"/> / <see cref="Aliases"/> 則表示「不分 op 都適用」的部分。</item>
    /// </list>
    ///
    /// <para>不覆寫 <c>ArgsSpec</c>（維持 null）完全合法 —— 匯出時該 Cmd 只出現名稱，
    /// client 端對它不做參數預檢。**不宣告不是錯誤，是「我沒有要 client 幫忙擋」。**</para>
    /// </summary>
    public class UCL_CmdArgsSpec
    {
        /// <summary>cmd 層必填參數（與 op 無關者）。有子 op 的 Cmd 通常留空，改填在各 op 內。</summary>
        public string[] Required = System.Array.Empty<string>();

        // ===========================================================
        // 區塊職責：**必須在場、但值可以是空的**參數（BUG-15，2026-08-19 kiara）。
        // 物理意義：`Required` 的判準是「有值」（空字串算缺）—— 對絕大多數參數是對的，
        //          因為 `--arg foo=` 通常是打錯。但有些參數**空值本身就是合法輸入**
        //          （例：`PersonaProfile op=set` 要把一個欄位清空 ⇒ `value=`）。
        //          這兩種需求用同一個欄位表達不出來，於是每支這樣的 Cmd 都被迫二選一：
        //            · 放進 Required  ⇒ 合法的空值進不來（BUG-15）
        //            · 不放進 Required ⇒ 參數名打錯時靜默走預設值（BUG-14）
        //          🩸 這兩張單是同一天開的、**同一個表達力缺口的兩面**：
        //             BUG-14 的修法把 value 放進 Required，於是當天就長出 BUG-15。
        //          ⇒ 判準改成「在場 vs 有值」兩種，各自有欄位可宣告。
        //          這就是本案 §8.7「兩態不得同形」下移到參數層：
        //          **「沒給」與「給了空的」是兩件事，把它們壓成一件的驗證器會擋掉一半的合法輸入。**
        //
        // ⚠ 刻意**不匯出到 commands_schema.json**：本檔開頭那條規矩是
        //   「只宣告 Python 端真的會拿來做判斷的東西，沒人用的欄位一定會爛」。
        //   目前 python 端只有 `tavern_cmd.py` 會 enforce required，而 Tavern 沒有任何
        //   「空值合法」的參數 ⇒ 現在匯出就是加一個沒有消費端的欄位。
        //   **哪天 Tavern（或別的有 python 預檢的 Cmd）需要它，匯出與 python 端一起做**
        //   —— 順帶提醒：`tavern_cmd.py` 的 required 檢查用的是 `not arg_pairs.get(r)`，
        //   跟這裡修掉的是同一種 falsy 判準，那時要一起改。
        // 數值影響：預設空陣列 ⇒ 不宣告的 Cmd 行為與本次改動前逐字相同。
        // ===========================================================
        public string[] RequiredPresent = System.Array.Empty<string>();


        /// <summary>cmd 層別名對照。順序語意同 <see cref="UCL_CmdOpSpec.Aliases"/>。</summary>
        public Dictionary<string, string> Aliases = new Dictionary<string, string>();

        /// <summary>
        /// 子 op 規格表：op 名（小寫，對齊 handler 內 <c>switch</c> 的 case 字串）→ 規格。
        /// null / 空 = 本 Cmd 沒有子 op。
        /// </summary>
        public Dictionary<string, UCL_CmdOpSpec> Ops;
    }
}
