// 區塊職責：文件編輯類活動的 Cmd 入口 —— 一次呼叫 ＝ 改完**一份** .md 之後的登記與驗收。
//          三個自由時間活動共用一支：`doc-reflection` / `letter-to-self` / `constitution`。
//
// 物理意義：Tim 2026-08-18 拍板。這三件活動原本是「C 類：本質不是一步」——
//          它們沒有單一 python CLI 入口（活動就是**編輯一個檔**），所以
//          `Cmd_FreeTimeActivity op=step` 代跑不到，流程一進到編輯就斷在那裡。
//          ⇒ 補一支 Cmd 讓它們也能「做完一步 → 回報 → 被指去下一步」。
//
// ⛔ **本 Cmd 刻意不搬檔案內容**（沒有 `body` 參數，不寫、不覆寫任何 .md）。
//   把整份文件塞進 CLI 參數，等於把編輯器換成一個沒有 diff、沒有復原、沒有語法檢查的通道 ——
//   而 doc-reflection 的價值在「讀懂之後怎麼改」，不在傳輸。
//   Tim 的原話是「一步改一個 Doc，改完後 CMD 一樣提示下一步」——
//   **「改完後」意味著編輯已經發生**，本 Cmd 站在那之後。
//
// ⇒ 那本 Cmd 到底做什麼（三件都是「說得出讀數」的事）：
//   ① **解析目標**：constitution 自動指向該 persona 自己的 `_constitution.md`；
//      letter 沒給 target 時取 letters/<persona>/ 底下最新那份 —— 路徑由本層算，呼叫端不必記慣例。
//   ② **驗收**：檔案存在／是 .md／在 repo 內；印出實際 mtime。
//      在自由時間中時，**拿 session 開場時刻當基準**回答「這份檔在本場真的被改過嗎」——
//      「我說我改了」與「檔案真的動了」因此不同形。
//   ③ **指路**：報告尾端掛 `UCL_FreeTimeHint`（op=done / 換骰），流程不斷。
//
// 數值影響：**唯讀**（只 stat 檔案），另寫一份帶輪替的報告（`UCL_CmdPayloadStore`）。
// 2026-08-18 gura
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.Awakening;   // UCL_AwakeningService.LettersDir（letters 慣例的唯一來源）

namespace UCL.Core.EditorLib.AgentCommands.DocEdit
{
    /// <summary>
    /// 文件編輯一步的登記與驗收（`doc` / `letter` / `constitution` 共用）。**不寫任何檔案內容。**
    /// </summary>
    public class Cmd_DocEdit : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "DocEdit";

        public override string ShortDescription =>
            "文件編輯活動的一步：登記剛改完的那份 .md、驗收它真的動了、並指回自由時間流程。不搬內容、不寫檔。";

        public override string ArgsSchema =>
            "kind=doc|letter|constitution（required）| " +
            "persona=<名字>（**letter / constitution 必填**；doc 選填，帶了才驗得出「本場改過沒」）| " +
            "target=<.md 路徑>（doc **必填**；letter 選填＝取 letters/<persona>/ 最新一份；constitution 忽略，自動指向該 persona 的 _constitution.md）| " +
            "note=<一句心得，選填>";

        public override string ExampleArgs =>
            "kind=doc;persona=gura;target=Docs/AI_READABILITY_GUIDELINES.md;note=補上 group 欄位說明";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_DocEdit.md";

        // 區塊職責：執行前的參數檢查（框架在失敗報告裡主動要求宣告 —— 沒宣告時打錯參數名
        //          會**靜默取預設值**，而那長得跟「這個參數沒作用」一模一樣）。
        // ⚠ 只宣告得出 `kind`：persona 的必填是**條件式的**（只有 letter / constitution 要），
        //   而 spec 表達不了條件 —— 所以那道守衛仍留在 ExecuteAsync 裡，兩者不是重複。
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Required = new[] { "kind" },
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string aKind = GetArg(args, "kind", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            string aTargetArg = GetArg(args, "target", "").Trim();
            string aNote = GetArg(args, "note", "").Trim();

            if (aKind != "doc" && aKind != "letter" && aKind != "constitution")
            {
                throw new Exception($"[DocEdit] kind 必為 doc|letter|constitution（got '{aKind}'）。ArgsSchema: {ArgsSchema}");
            }

            // ── 身分守衛（Tim 2026-08-18 硬規則）─────────────────────────────
            // letter 與 constitution 的落點**綁在某個人身上**，猜錯會寫到／驗到別人的信與憲法，
            // 而那看起來完全正常（路徑合法、檔案存在、報告漂亮）。⇒ 不給預設值，直接擋。
            if ((aKind == "letter" || aKind == "constitution") && string.IsNullOrEmpty(aPersona))
            {
                throw new Exception($"[DocEdit] kind={aKind} 需要 --arg persona=<名字> —— "
                    + "這兩種的目標綁在某個人身上，猜錯會動到別人的信／憲法而且看起來完全正常（不猜身分）");
            }

            var aR = new StringBuilder();
            aR.AppendLine($"# DocEdit kind={aKind}"
                          + (string.IsNullOrEmpty(aPersona) ? "" : $" persona={aPersona}")
                          + $"  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            string aTarget = ResolveTarget(aKind, aPersona, aTargetArg, out string aResolveNote);

            // ── 驗收：存在／副檔名／在 repo 內 ────────────────────────────
            if (string.IsNullOrEmpty(aTarget))
            {
                Fail(args, aR, aKind, aPersona, "找不到目標檔",
                     aResolveNote + "　⇒ 用 --arg target=<.md 路徑> 顯式指定");
                return;
            }
            if (!aTarget.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                Fail(args, aR, aKind, aPersona, "目標不是 .md", $"target: `{aTarget}`");
                return;
            }
            // 路徑守衛：只認 repo 內的檔。repo 外的路徑通常是「另一個宇宙的檔」——
            // 那種失敗會回一個看起來正常的讀數（見 ucl-core-paths 的三則血證）。
            string aRepoRoot = Path.GetFullPath(UCL_RepoPath.RepoRoot);
            string aFull = Path.GetFullPath(aTarget);
            if (!aFull.StartsWith(aRepoRoot, StringComparison.OrdinalIgnoreCase))
            {
                Fail(args, aR, aKind, aPersona, "目標在 repo 之外", $"target: `{aFull}`　repo: `{aRepoRoot}`");
                return;
            }
            if (!File.Exists(aFull))
            {
                Fail(args, aR, aKind, aPersona, "目標檔不存在",
                     $"target: `{aFull}`　⇒ **本 Cmd 不建檔**（它站在「改完之後」，不負責產生內容）");
                return;
            }

            DateTime aMtime = File.GetLastWriteTime(aFull);
            long aBytes = new FileInfo(aFull).Length;

            aR.AppendLine("## 目標（讀回的事實）");
            aR.AppendLine($"- target: `{aFull}`");
            aR.AppendLine($"- 解析方式: {aResolveNote}");
            aR.AppendLine($"- 最後修改: **{aMtime:yyyy-MM-dd HH:mm:ss}**　大小: {aBytes:N0} bytes");
            if (!string.IsNullOrEmpty(aNote)) aR.AppendLine($"- note: {aNote}");
            aR.AppendLine();

            AppendSessionVerdict(aR, aPersona, aMtime);

            // 報告落帶輪替的 payload（`_last_op.md` 共用一格會被下一支 Cmd 蓋掉 ——
            // 見 UCL_CmdPayloadStore 的血證）。順序刻意：hint 在最後，讀的人往下讀就看到下一步。
            UCL_FreeTimeHint.Append(aR, aPersona);
            string aPath = UCL_CmdPayloadStore.Write(CommandType, aKind, aR.ToString(), aPersona);
            if (!string.IsNullOrEmpty(aPath)) UCL_AgentCommandRunner.ReportOutputFile(args, aPath);
            Debug.Log($"[DocEdit] kind={aKind} target={aFull} → {aPath}");
        }

        // ===========================================================
        // 區塊職責：三種 kind 各自的目標解析（**路徑由本層算，呼叫端不必記慣例**）。
        // 物理意義：letter / constitution 的落點是有慣例的（`letters/<persona>/…`），
        //          而慣例寫在呼叫端的記憶裡就會漂 —— 所以由這裡算，並把「怎麼算出來的」
        //          印進報告（oNote）。算法透明才對得起帳。
        // 數值影響：純路徑組合 ＋ letter 情境下一次目錄列舉。回 null ＝ 解析不出來（呼叫端顯式擋）。
        // ===========================================================
        static string ResolveTarget(string iKind, string iPersona, string iTargetArg, out string oNote)
        {
            if (iKind == "constitution")
            {
                // 憲法是**單一檔**，版本史交給 git（活動 md 自己寫著這條）。target 參數刻意忽略：
                // 允許覆寫目標的話，「改自己的憲法」就會變成「可以改任何檔」。
                oNote = "constitution 固定指向該 persona 自己的 `_constitution.md`（忽略 target 參數）";
                return Path.Combine(UCL_LettersPath.PersonaDir(iPersona), "_constitution.md");
            }

            if (!string.IsNullOrEmpty(iTargetArg))
            {
                oNote = "由 --arg target 顯式指定";
                return Path.IsPathRooted(iTargetArg)
                    ? iTargetArg
                    : Path.Combine(UCL_RepoPath.RepoRoot, iTargetArg);
            }

            if (iKind == "letter")
            {
                // 沒給 target ⇒ 取該 persona letters 目錄**頂層最新的一封信**。
                //
                // ⚠ 只看頂層：`wakes/` `rests/` `longterm/` 等子目錄是別的東西（收尾信 / 見林），
                //   遞迴下去會把「剛寫的信」誤判成某份被工具重生成的檔。
                //
                // 🩸 沿革：第一版沒排除任何檔，實跑立刻解析到 `_freetime_next.md`（Cmd 回傳檔，不是信）。
                //   當時的修法是「跳過 `_` 開頭與 README」——**它有效，但那是 heuristic**：
                //   依賴「機器產物都用 `_` 開頭」這條沒人強制執行的慣例，而且它連 `_constitution.md`
                //   這種**耐久**產物也一起跳掉了 —— 那三個檔剛好都不是信，所以沒出事。那是運氣不是設計。
                // ⇒ 2026-08-18 回傳檔全數遷入 `cmd/` 子目錄（Plan_Letters_Dir_Layout §8）之後，
                //   判準改成**具名排除**：頂層的 .md 就是信，除了這三個耐久檔＋README。
                //   前綴規則讀不出「到底排除了什麼」，三個名字讀得出來、加減也看得見。
                // ⚠ 本迴圈只看頂層 ⇒ `cmd/` 底下的回傳檔天然不在候選裡（位置本身承載語意）。
                string aDir = UCL_LettersPath.PersonaDir(iPersona);
                if (!Directory.Exists(aDir)) { oNote = $"letters 目錄不存在：`{aDir}`"; return null; }
                string aNewest = null;
                DateTime aBest = DateTime.MinValue;
                int aSkippedNamed = 0, aSkippedNotLetter = 0;
                foreach (var f in Directory.GetFiles(aDir, "*.md"))
                {
                    if (IsNonLetterTopLevel(Path.GetFileName(f)))
                    {
                        aSkippedNamed++;
                        continue;
                    }
                    // 決定性判準：**信自己會說自己是信**（frontmatter `type`）。
                    // 🩸 只靠檔名排除不夠：回傳檔遷入 `cmd/` 之後，舊位置**還留著**已經不再被寫入的
                    //    殘影（plan §5② 刻意不搬它們），而它們的 mtime 可能比真信新 ——
                    //    實測 calli 的 `_goodmorning_brief.md` 就這樣被挑成「最新那封信」。
                    //    ⇒ 判準對齊 `wake_brief._newest_self_letter`：只認 letter_to_future_self。
                    //    （peer_letter_from_persona 是同事寄來的，不是你要編的那封。）
                    if (UCL_AwakeningService.ReadFrontmatterField(f, "type") != k_LetterType)
                    {
                        aSkippedNotLetter++;
                        continue;
                    }
                    var aT = File.GetLastWriteTime(f);
                    if (aT > aBest) { aBest = aT; aNewest = f; }
                }
                string aSkipNote = $"排除 {aSkippedNamed} 個具名耐久檔／README、"
                                 + $"{aSkippedNotLetter} 個非 `{k_LetterType}`（舊位置回傳檔殘影／同事來信）";
                oNote = aNewest == null
                    ? $"letters 頂層沒有任何「自己寫給自己的信」（{aSkipNote}）：`{aDir}`"
                    : $"letter 未給 target ⇒ 取 letters 頂層最新的信（{aSkipNote}、不遞迴子目錄）";
                return aNewest;
            }

            oNote = "kind=doc 需要顯式 target";
            return null;
        }

        // ===========================================================
        // 區塊職責：letters **頂層**不是信的那幾個檔 —— 具名清單，不是前綴規則。
        // 物理意義：回傳檔（transient）已經全數住 `cmd/` 子目錄，頂層剩下的機器產物只有三個，
        //          而它們是**耐久**的（刪掉就沒了）：憲法 / 見叢 / 最新信指針。
        //          三個名字讀得出來；`_` 前綴讀不出「這是暫存還是耐久」——
        //          那正是 2026-08-18 造的「一符二役」那個詞在講的事。
        // 數值影響：純字串比對；清單要跟這三個檔的實際檔名逐字一致（改名要改這裡）。
        // ===========================================================
        // 「自己寫給自己的信」的 frontmatter type —— 與 wake_brief.py 的 _newest_self_letter 同一個值。
        // ⚠ 兩端共用這個字面：改了任一邊，另一邊會安靜地挑錯檔（沒有例外、沒有紅字）。
        const string k_LetterType = "letter_to_future_self";

        static readonly string[] TOP_LEVEL_NON_LETTERS =
        {
            "_constitution.md",   // 憲法（耐久）
            "_keys_open.md",      // 見叢：當期交棒清單（耐久）
            "_latest.md",         // 最新自寫信的指針（機械維護但耐久）
            "README.md",          // 說明檔
        };

        static bool IsNonLetterTopLevel(string iFileName)
        {
            foreach (var aN in TOP_LEVEL_NON_LETTERS)
                if (iFileName.Equals(aN, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ===========================================================
        // 區塊職責：拿自由時間 session 的開場時刻當基準，回答「這份檔在本場真的被改過嗎」。
        //
        // 物理意義：沒有基準的話，只能印一個 mtime 讓人自己判斷 —— 而人會傾向相信自己剛才做了。
        //          session 開場時刻是**這個場合唯一有意義的基準**：比它舊 ⇒ 這一場沒動過它。
        //          ⇒ 「我說我改了」與「檔案真的動了」因此不同形。
        //
        // 數值影響：純讀 session 檔。不在自由時間 / 沒帶 persona ⇒ **只印 mtime 不下判斷**
        //   （沒有基準時給判斷等於憑感覺蓋章）。判斷為「沒動過」時**不擋** ——
        //   本 Cmd 是登記不是收銀台，擋下來只會讓人改用別的方式繞過登記。
        // ===========================================================
        static void AppendSessionVerdict(StringBuilder ioR, string iPersona, DateTime iMtime)
        {
            ioR.AppendLine("## 本場改過了嗎");
            if (string.IsNullOrEmpty(iPersona))
            {
                ioR.AppendLine("- ⚪ **沒帶 persona，不下判斷** —— 只有上面那個 mtime 是事實。"
                             + "要驗「本場改過沒」請帶 `--arg persona=<名字>`。");
                return;
            }
            var aRunning = UCL_SessionService.FindRunning(iPersona);
            UCL_SessionBase aFreeTime = null;
            foreach (var aKv in aRunning) if (aKv.Key == UCL_SessionKind.FreeTime) { aFreeTime = aKv.Value; break; }
            if (aFreeTime == null)
            {
                ioR.AppendLine($"- ⚪ **{iPersona} 不在自由時間中，沒有基準可比** —— 只有 mtime 是事實。"
                             + $"（掃描範圍：{string.Join(" / ", UCL_SessionService.ScannedKinds())}）");
                return;
            }
            DateTime? aStart = UCL_SessionBase.ParseIsoToLocal(aFreeTime.start_ts);
            if (!aStart.HasValue)
            {
                ioR.AppendLine($"- ⚪ session 的 start_ts 解析不出來（`{aFreeTime.start_ts}`）—— 不下判斷。");
                return;
            }
            if (iMtime >= aStart.Value)
            {
                ioR.AppendLine($"- ✅ **本場改過** —— mtime {iMtime:HH:mm:ss} 晚於本場開場 {aStart.Value:HH:mm:ss}"
                             + $"（session `{aFreeTime.session_id}`）");
            }
            else
            {
                ioR.AppendLine($"- ⚠ **本場沒動過這份檔** —— mtime {iMtime:HH:mm:ss} 早於本場開場 {aStart.Value:HH:mm:ss}。"
                             + "**沒擋你**（本 Cmd 是登記不是收銀台），但這一步在帳上就是「登記了一份沒被改的檔」。");
            }
        }

        // 失敗一律落一份報告再丟例外 —— 只丟例外的話，呼叫端只看到一行錯誤訊息，
        // 而「哪個路徑、怎麼算出來的」全都不見了（那正是最需要看的東西）。
        void Fail(Dictionary<string, string> iArgs, StringBuilder ioR,
                  string iKind, string iPersona, string iReason, string iDetail)
        {
            ioR.AppendLine("## blocked");
            ioR.AppendLine($"- reason: {iReason}");
            ioR.AppendLine($"- {iDetail}");
            UCL_FreeTimeHint.Append(ioR, iPersona);
            string aPath = UCL_CmdPayloadStore.Write(CommandType, iKind + "-blocked", ioR.ToString(), iPersona);
            if (!string.IsNullOrEmpty(aPath)) UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPath);
            throw new Exception($"[DocEdit] blocked：{iReason}（詳見 {aPath}）");
        }
    }
}
#endif
