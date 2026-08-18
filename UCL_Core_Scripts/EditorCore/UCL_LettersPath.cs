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
// 數值影響：路徑組法純字串；**唯一碰 IO 的成員是 `EnsureCmdDir` / `EnsurePayloadDir`**
//          （建目錄＋補 `cmd/.gitignore`）。刻意收在本類：`cmd/` 的「不入版控」語意屬於版面，
//          而版面只有一個擁有者 —— 交給各寫入端各自記得，就是下一次靜默漂移。
// 2026-08-18 gura（Tim 拍板：FreeTime 回傳檔遷入 `cmd/`，且兩端路徑解析必須統一）
#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEngine;
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

        // ===========================================================
        // 區塊職責：`cmd/` 目錄裡那份 `.gitignore` 的內容。
        // 物理意義：回傳檔是 transient（每跑一次重生、手改無效），**不該進版控**。
        //          在此之前這件事靠「每個 letters repo 的根 `.gitignore` 逐檔列名」維持，
        //          而那份清單天生會落後：新增一支 Cmd／新增一個 step 就漏一個。
        //   🩸 血證一：FreeTime 回傳檔 2026-08-18 遷進 `cmd/` 之後，根 `.gitignore` 裡
        //      `_freetime_next.md` 那幾行**全部失效**（檔名與位置都變了）⇒ gura 的 4 份
        //      回傳檔就這樣被 commit 進去，沒有任何一格會紅。
        //   🩸 血證二（更重）：`_wake_brief.md` 被 ignore 的理由是它含**活的 session_token
        //      與個人信箱**，而 letters repo 的 origin 是公開 remote。它照計畫要搬進 `cmd/`
        //      —— 搬的那一刻舊規則失配，一枚活憑證就會進公開 history（history 刪不掉）。
        // ⇒ 改成「目錄自帶 ignore」：規則跟著位置走，新增幾支 Cmd 都不必再維護清單。
        // 數值影響：`*` 連子目錄內容一起擋；`!.gitignore` 讓規則本身入版控（否則規則不會傳給別人）。
        // ===========================================================
        public const string CmdDirGitignore =
            "# Cmd 回傳檔（transient）—— 每跑一次就重生、手改無效，一律不入版控。\n" +
            "# 有些回傳檔含 session_token / 信箱等憑證，而 letters remote 可能是公開的；\n" +
            "# 這份 ignore 是「目錄層」的，所以新增任何 Cmd / step 都不必再維護逐檔清單。\n" +
            "# 本檔由 UCL_LettersPath.EnsureCmdDir() / ucl_paths.ensure_letters_cmd_dir() 自動建立（兩端同一份字面）。\n" +
            "*\n" +
            "!.gitignore\n";

        /// <summary>
        /// 建好某 persona 的 `cmd/` 目錄，並確保裡面有 `.gitignore`（缺才寫，**不覆蓋既有的**）。
        /// 回傳該目錄路徑。IO 失敗不丟例外 —— 回傳檔本身比 ignore 重要，不該因為這步讓 Cmd 掛掉。
        /// </summary>
        public static string EnsureCmdDir(string iPersona)
        {
            string aDir = CmdDir(iPersona);
            try
            {
                Directory.CreateDirectory(aDir);
                string aIgnore = Path.Combine(aDir, ".gitignore");
                // 缺才寫：有人手改過（例如放行某一份）時不該被機器蓋回去。
                if (!File.Exists(aIgnore)) File.WriteAllText(aIgnore, CmdDirGitignore, new UTF8Encoding(false));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LettersPath] cmd/ 目錄或 .gitignore 準備失敗（回傳檔仍會嘗試寫入）：{aDir} — {e.Message}");
            }
            return aDir;
        }

        /// <summary>
        /// 寫回傳檔前的唯一建目錄入口：建 `iPayloadPath` 的父目錄；父目錄若是 `cmd/` 就順手補 `.gitignore`。
        /// 寫入端一律走這支，不要自己 `Directory.CreateDirectory` —— 否則新寫入端會漏掉 ignore（靜默）。
        /// </summary>
        public static void EnsurePayloadDir(string iPayloadPath)
        {
            string aDir = Path.GetDirectoryName(iPayloadPath);
            if (string.IsNullOrEmpty(aDir)) return;
            try
            {
                Directory.CreateDirectory(aDir);
                if (Path.GetFileName(aDir) == CmdDirName)
                {
                    string aIgnore = Path.Combine(aDir, ".gitignore");
                    if (!File.Exists(aIgnore)) File.WriteAllText(aIgnore, CmdDirGitignore, new UTF8Encoding(false));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LettersPath] 回傳檔目錄準備失敗（仍會嘗試寫入）：{aDir} — {e.Message}");
            }
        }
    }
}
#endif
