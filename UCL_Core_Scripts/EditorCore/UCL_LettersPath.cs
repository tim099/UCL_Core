// 區塊職責：letters 目錄**底下的版面**（layout）—— persona 目錄、Cmd 回傳檔子目錄與檔名組法。
//
// 物理意義：letters 根目錄的解析一直都有唯一擁有者（`UCL_AwakeningService.LettersDir`，
//          它同時處理資料根 override），但**根底下的版面沒有** ——
//          於是 `Cmd_FreeTime` / `Cmd_Sculpture` / `Cmd_StreamWatch` 各自組一份
//          `letters/<persona>/_<cmd>_<step>.md`，而 StreamWatch 那份甚至連根都自己推
//          （`DataRoot/ChatTavern/baton/letters`）—— 同一個目錄的第四種算法。
//          ⇒ 版面收攏到這裡：**要改「回傳檔放哪」只改這一支。**
//
// ⚠ 為什麼本類**不自己解析根**：`LettersDir` 的 override 語意住在 `UCL_AwakeningService`
//   （legacy `_config/tavern_paths.json` 的相容處理在那裡）。在這裡重推一次就是第五種算法 ——
//   而路徑重造的失敗是靜默的（找到的是另一個宇宙的檔，回一個看起來正常的讀數）。
//   ⇒ 本類**委派**根、只擁有版面。
//
// ⚠ **對側契約**：Python 等價入口是 `_lib/ucl_paths.py` 的
//   `letters_root()` / `letters_cmd_dir()` / `letters_cmd_payload()`。
//   兩端要一起改 —— 只改一端的後果是兩邊各看各的目錄，而**兩邊都不會報錯**
//   （寫檔會自動建目錄，於是舊位置與新位置各有一份，各自看起來都正常）。
//
// 數值影響：純字串組合，不碰 IO。
// 2026-08-18 gura（Tim 拍板：FreeTime 回傳檔遷入 `cmd/`，且兩端路徑解析必須統一）
#if UNITY_EDITOR
using System.IO;
using UCL.Core.EditorLib.AgentCommands.Awakening;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// letters 目錄底下的版面解析（persona 目錄 / Cmd 回傳檔）。**根目錄委派 <see cref="UCL_AwakeningService.LettersDir"/>。**
    /// </summary>
    public static class UCL_LettersPath
    {
        // ===========================================================
        // 區塊職責：Cmd 回傳檔的子目錄名。
        // 物理意義：letters 頂層原本同時住著**人寫的信**（時間戳命名）與**機器寫的回傳檔**
        //          （`_` 開頭）。兩者混住的代價是實測過的 —— `Cmd_DocEdit` 要「找出最新那封信」時
        //          抓到了 `_freetime_next.md`，因為機器產物每跑一次 Cmd 就更新，
        //          所以「最新的 .md」幾乎永遠是機器的。
        // ⇒ 分目錄之後，「是不是信」不再需要靠檔名前綴猜 —— 它是**位置**的問題。
        //   （設計沿革與完整範圍見 `Docs~/{lang}/Plan/Plan_Letters_Dir_Layout.md`）
        // ===========================================================
        public const string CmdDirName = "cmd";

        /// <summary>letters 根（委派唯一擁有者，本類不自己推導）。</summary>
        public static string Root => UCL_AwakeningService.LettersDir;

        /// <summary>某 persona 的 letters 目錄 —— **人寫的信住這裡**。</summary>
        public static string PersonaDir(string iPersona) => Path.Combine(Root, iPersona);

        /// <summary>某 persona 的 Cmd 回傳檔目錄（`letters/&lt;persona&gt;/cmd/`）。</summary>
        public static string CmdDir(string iPersona) => Path.Combine(PersonaDir(iPersona), CmdDirName);

        // ===========================================================
        // 區塊職責：一份 Cmd 回傳檔的完整路徑。
        // 物理意義：檔名 `<cmd>_<step>.md`，**不再帶 `_` 前綴** —— 目錄本身已經說了它是什麼。
        //          「同一個 (persona, cmd, step) 永遠是同一格、每次覆寫」這個語意由**固定檔名**承載
        //          （agent 記得住 `cmd/freetime_next.md` 是哪一份）。
        // ⚠ 這跟 `UCL_CmdPayloadStore`（`<DataRoot>/_cmd_payloads/`，每次新檔、保留 10 筆）
        //   是**兩種不同形狀**，刻意分開存放與命名 —— 共用一個名字會讓兩種耐久度長得一樣。
        // 數值影響：純字串；建目錄由寫入端負責（寫檔前 CreateDirectory）。
        // ===========================================================
        public static string CmdPayload(string iPersona, string iCmdSlug, string iStep)
            => Path.Combine(CmdDir(iPersona), $"{iCmdSlug}_{iStep}.md");
    }
}
#endif
