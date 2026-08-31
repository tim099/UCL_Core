// 區塊職責：自動 commit 的 **Cmd 入口** —— 讓 agent（`/ucl-commit` 流程）也能把「機器生成的檔」
//          分群整批 commit，不必自己分類、也不必請人去按後台那顆按鈕。
// 物理意義：Tim 2026-08-20 要求 `/ucl-commit` 流程改用自動 commit 收重複性檔案。
//          分群規則與 `UCL_AutoCommitPage` **共用** `UCL_AutoCommitRules`（單一真相源）——
//          本檔只負責「掃 → 分群 → 逐群 git commit」，規則不在這裡。
// 數值影響：
//   · **`op=scan` 是預設**（純讀）。要真的 commit 得顯式 `op=commit` ——
//     批次提交的預設值必須是「不提交」，它的破壞面是整個 repo 的 index。
//   · **不 push、不 bump 父層 pointer**（同後台頁；那兩件是人的決定）。
//   · **走純 git commit，不走 `git_commit.py`**（Tim 2026-08-07 拍板）——
//     那支工具的 trailer／酒館公告／領薪是給「有作者的工作產出」用的；
//     這裡收的是機器生成的狀態殘渣，掛誰的名字領誰的薪都是假帳。
//     ⇒ **兩條路不混**：agent 自己的 code／文件 commit 照舊走 `git_commit.py`。
// ⚠ 三個硬擋（都是「不會當場叫」的那種錯，所以擋在必經路上）：
//   ① `__other`（未分類）與 `__subptr`（巢狀 submodule pointer）**永遠不自動收** ——
//      前者是規則沒認出來的檔（可能是別人正在寫的產出），後者 bump 了別人會 pull 不到 hash。
//      要收得**顯式列進 `groups=`**。
//   ② **detached HEAD 的 repo 直接跳過** —— 那裡 commit 出來的是游離 commit，
//      沒有分支指到它，下次 checkout 只剩 reflog 找得到。
//   ③ letters 模式**預設跳過在線的 persona** —— 她可能正在寫，而「動別人正在寫的東西」
//      的後果不是衝突報錯，是靜默把工作清掉。要收得 `include_online=1`。
//   ④ **呼叫前 index 已有 staged 檔的 repo，op=commit 直接擋下**（BUG-30）——
//      分群只決定「我 stage 哪些檔」，index 裡本來就有的東西會被併進第一個成功的群，
//      掛上那個群的訊息。🩸 2026-08-21：`git mv` 21 個 persona 檔後直接跑 op=commit，
//      那批改名落進 `[chat] sync tavern messages (auto) [3 files]` ——
//      訊息說 3 個檔、實際 24 個，而 `[chat] 獨立 commit` 是 CLAUDE.md 級硬規則。
//      ⇒ 這一族**不會叫**：commit 成功，而 `[N files]` 是分群自己算的，從不跟實際 diff 對帳。
//      三層一起上：擋 index（本條）＋ pathspec 提交（CommitGroup）＋ 提交後對帳（ReconcileCommit）。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/Commit_Workflow.md（§2.5 機器生成的檔交給自動 commit／規則住在哪）
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/AutoCommit_Config_Workflow.md（mode=submodules 與設定檔）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands
{
    public class Cmd_AutoCommit : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "AutoCommit";

        public override string ShortDescription =>
            "把機器生成的檔分群整批 commit（規則與自動提交頁共用；預設只掃不提交）。";

        public override string ArgsSchema =>
            "op=scan（預設）— 只掃描分群並回報，不動 index | " +
            "op=commit — 逐群 commit（純 git commit，無 trailer／無公告／不領薪；不 push、不 bump 父層；" +
            "⛔ 呼叫前 index 已有 staged 檔的 repo 會被擋下 —— 先自己 commit 或 unstage，見 BUG-30） | " +
            "mode=agent（預設，掃 AgentCommands 本層）｜letters（掃 letters/<persona>/ 每個 repo）" +
            "｜submodules（掃 .gitmodules，只收自帶 .ucl_autocommit.json 的 repo，分群由該檔宣告） | " +
            "[groups=<key1,key2>] 只做這幾群（預設＝**每個 repo 各自**所有 DefaultOn 的群；" +
            "`__other` / `__subptr` 只有顯式列出才會做） | " +
            "[only_persona=<name>] letters 模式只做這一位 | " +
            "[include_online=1] letters 模式含在線 persona（預設跳過）";

        public override string ExampleArgs => "op=scan;mode=letters";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md";

        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                ["scan"] = new UCL_CmdOpSpec(),
                ["commit"] = new UCL_CmdOpSpec(),
            }
        };

        const string PROC_TAG = "cmd_auto_commit_git";
        const int GIT_TIMEOUT_MS = 2 * 60 * 1000;
        const int CHUNK = 40;      // 每批 git add 的路徑數（Windows 命令列 32k 上限）

        class RepoTarget
        {
            public string Root = "";
            public string Name = "";
            public string Branch = "";
            public bool Online;
            public string Blocked = "";
            public Dictionary<string, List<string>> Groups = new Dictionary<string, List<string>>();
            /// <summary>這個 repo 自己宣告的分群（submodules 模式來自 .ucl_autocommit.json）；null ＝ 用模式的預設。</summary>
            public UCL_AutoCommitRules.GroupDef[] Defs;
            /// <summary>設定檔路徑（有的話），純顯示用。</summary>
            public string ConfigPath = "";
            /// <summary>設定檔把自己標為停用（`Enabled=false`）。**不是錯誤**，所以不計入 blocked。</summary>
            public bool Disabled;
            /// <summary>呼叫本 Cmd **之前**就已經 staged 的檔（`git diff --cached --name-only`）。
            /// 非空 ⇒ op=commit 擋下這個 repo（檔頭硬擋④）。scan 只警告，因為 scan 不動 index。</summary>
            public List<string> PreStaged = new List<string>();
        }

        /// <summary>這個 repo 該用哪一組分群規則：自己宣告的優先，否則用模式預設。</summary>
        static UCL_AutoCommitRules.GroupDef[] DefsOf(RepoTarget iRepo, UCL_AutoCommitRules.GroupDef[] iFallback)
            => iRepo.Defs ?? iFallback;

        /// <summary>一組規則裡 DefaultOn 的群 key。`__other`／`__subptr` 不在其中（它們不是 GroupDef）。</summary>
        static HashSet<string> DefaultOnGroups(UCL_AutoCommitRules.GroupDef[] iDefs)
        {
            var aSet = new HashSet<string>();
            foreach (var aDef in iDefs) if (aDef.DefaultOn) aSet.Add(aDef.Key);
            return aSet;
        }

        /// <summary>顯式 `groups=` 參數 → 群集合；沒給回 **null**（呼叫端據此改用各 repo 自己的預設）。
        /// ⚠ 回 null 而不是回空集合：空集合的語意是「什麼都不要做」，兩者不可同形。</summary>
        static HashSet<string> ParseExplicitGroups(string iArg)
        {
            string a = (iArg ?? "").Trim();
            if (string.IsNullOrEmpty(a)) return null;
            var set = new HashSet<string>();
            foreach (var raw in a.Split(','))
            {
                string k = raw.Trim();
                if (k.Length == 0) continue;
                set.Add(k);
            }
            return set;
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string op = GetArg(args, "op", "scan").Trim().ToLowerInvariant();
            if (op != "scan" && op != "commit")
                throw new Exception($"[AutoCommit] 未知 op '{op}'（scan / commit）");

            string modeArg = GetArg(args, "mode", "agent").Trim().ToLowerInvariant();
            bool letters = modeArg == "letters";
            bool submodules = modeArg == "submodules";
            if (modeArg != "letters" && modeArg != "agent" && modeArg != "submodules")
                throw new Exception($"[AutoCommit] 未知 mode '{modeArg}'（agent / letters / submodules）");

            // submodules 模式沒有「全域規則」—— 每個 repo 的分群來自它自己的 .ucl_autocommit.json。
            // ⇒ defs 只當 agent/letters 的預設，實際用的是 RepoTarget.Defs（見 DefsOf）。
            var defs = submodules ? new UCL_AutoCommitRules.GroupDef[0] : UCL_AutoCommitRules.Defs(letters);
            // 顯式指定的群對所有 repo 一體適用；沒指定則**每個 repo 各自取自己的 DefaultOn**
            //（submodules 模式下各 repo 的群 key 不同，一份全域清單會把別人的群漏掉）。
            var explicitGroups = ParseExplicitGroups(GetArg(args, "groups", ""));
            // ⚠ 參數名刻意**不叫** `persona`：`run_cmd.py --persona <me>` 會把 persona 戳進 args
            //   （那是「這筆是誰派的」宣告，見 ucl-coding）⇒ 叫 persona 就會被那個宣告當成篩選條件。
            // 🩸 實測踩過：`--persona kiara` 讓 letters 模式的掃描範圍從 9 個 repo 縮成 1 個，
            //   而輸出是「repos=1」—— 看起來像「找不到其他 repo」的探索 bug，不像參數撞名。
            string personaFilter = GetArg(args, "only_persona", "").Trim();
            bool includeOnline = GetArg(args, "include_online", "0").Trim() == "1";

            var targets = CollectTargets(letters, submodules, personaFilter, includeOnline);
            if (targets.Count == 0)
                throw new Exception("[AutoCommit] 沒有可處理的 repo —— 檢查 mode / persona 參數");

            var sb = new StringBuilder();
            sb.AppendLine($"[AutoCommit] op={op} mode={modeArg} repos={targets.Count} "
                + $"groups={(explicitGroups == null ? "(各 repo 的 DefaultOn)" : string.Join(",", new List<string>(explicitGroups).ToArray()))}");

            int ephemeral = 0, scannedFiles = 0;
            foreach (var t in targets) ephemeral += ScanOne(t, DefsOf(t, defs), ref scannedFiles);

            int committed = 0, skippedRepos = 0, emptyGroups = 0, disabledRepos = 0, preStagedRepos = 0;
            // 🩸 2026-08-31（summit）：`op=commit` 回 `candidate_files=270 / commits=0`，
            //   而 blocked／prestaged／disabled **全部 0** ⇒ 呼叫端手上沒有任何一格能解釋那個 0。
            //   真因在 Editor log 裡（`git add` 撞 `index.lock: File exists`），
            //   而那條路徑呼叫端看不到 ⇒ 那是**空讀數**：工具什麼都沒說，於是填空的人填「大概沒東西可收」。
            //   ⇒ 修法不是讓它更會 commit，是讓「為什麼是 0」變成一個**機讀欄位**。
            //   commits=0 有三種成因，過去三種在機讀值上長得一模一樣：
            //     ① git 操作失敗（本欄 failed_groups）② 選到的群都是空的（empty_groups）
            //     ③ 候選檔全落在不自動收的 `__other`（other_files）
            //   ④ 候選檔是 submodule pointer（subptr_files）—— bump 了別人會 pull 不到 hash，所以永遠不自動收
            // ⇒ 對帳式（實測 2026-08-31）：
            //   `candidate_files − other_files − subptr_files` ＝ **現在可自動收的檔數**。
            //   那個差額 > 0 而 `commits` 是 0 ⇒ 真的有事發生（看 failed_groups / blocked_repos）；
            //   差額 ＝ 0 才叫「沒東西可收」。⚠ `op=scan` 的 commits 恆為 0（它不提交），別拿它當讀數。
            //   當日讀數：候選 25 ＝ `__other` 7（Lessons/Plurk/PromptQueue）
            //   ＋ `__subptr` 10（ArtGallery／Chess／Tasks ＋ 7 個 persona 信件庫）＋ 可收的 8。
            int failedGroups = 0, otherFiles = 0, subPtrFiles = 0;
            foreach (var t in targets)
            {
                if (t.Groups.TryGetValue(UCL_AutoCommitRules.KEY_OTHER, out var aOther)) otherFiles += aOther.Count;
                if (t.Groups.TryGetValue(UCL_AutoCommitRules.KEY_SUBPTR, out var aPtr)) subPtrFiles += aPtr.Count;
            }
            var shas = new List<string>();
            foreach (var t in targets)
            {
                if (t.Disabled)
                {
                    disabledRepos++;
                    sb.AppendLine($"  ・{t.Name}：{t.Blocked}");
                    continue;
                }
                if (!string.IsNullOrEmpty(t.Blocked))
                {
                    skippedRepos++;
                    sb.AppendLine($"  ⛔ {t.Name}：{t.Blocked} —— 跳過");
                    continue;
                }
                // 檔頭硬擋④：index 不是空的 ⇒ 這裡不猜「那些是不是也該收」，把選擇還給呼叫端。
                // ⚠ scan 只警告不擋：scan 純讀，擋了就看不到分群結果 —— 而「先 scan 再決定」
                //   正是這條擋下之後唯一的出路，把它一起關掉等於只留一條死巷。
                if (t.PreStaged.Count > 0)
                {
                    string aHint = $"呼叫前 index 已有 {t.PreStaged.Count} 個 staged 檔"
                        + " —— 它們會被併進第一個群並掛上那個群的訊息（BUG-30）；"
                        + "請先自己 commit 或 unstage，再跑本 Cmd";
                    preStagedRepos++;
                    if (op == "commit")
                    {
                        skippedRepos++;
                        sb.AppendLine($"  ⛔ {t.Name}：{aHint} —— 跳過");
                        foreach (string f in PreviewPaths(t.PreStaged)) sb.AppendLine($"      {f}");
                        continue;
                    }
                    sb.AppendLine($"  ⚠ {t.Name}：{aHint}（op=commit 會擋下這個 repo）");
                    foreach (string f in PreviewPaths(t.PreStaged)) sb.AppendLine($"      {f}");
                }
                bool any = false;
                var tDefs = DefsOf(t, defs);
                foreach (var g in explicitGroups ?? DefaultOnGroups(tDefs))
                {
                    if (!t.Groups.TryGetValue(g, out var files) || files.Count == 0) { emptyGroups++; continue; }
                    any = true;
                    string label = MessageOf(g, tDefs, files.Count);
                    if (op == "scan")
                    {
                        sb.AppendLine($"  → {t.Name} [{g}] {files.Count} 檔：{label}");
                        foreach (var f in files) sb.AppendLine($"      {f}");
                        continue;
                    }
                    string sha = CommitGroup(t, files, label, sb);
                    if (!string.IsNullOrEmpty(sha)) { committed++; shas.Add($"{t.Name}:{sha}"); }
                    // 失敗要被**數**出來 —— CommitGroup 已經把原因寫進 oLog，但 log 不是呼叫端的通道。
                    else failedGroups++;
                }
                if (!any) sb.AppendLine($"  ・{t.Name}：這幾群都沒有候選檔");
            }

            sb.AppendLine($"  ⇒ {(op == "scan" ? "掃描" : "提交")}完成："
                + $"候選檔 {scannedFiles}／ephemeral 略過 {ephemeral}／"
                + $"commit {committed}／失敗的群 {failedGroups}／空的群 {emptyGroups}／"
                + $"__other（不自動收）{otherFiles}／__subptr（不自動收）{subPtrFiles}／擋下的 repo {skippedRepos}");
            Debug.Log(sb.ToString());

            UCL_AgentCommandRunner.ReportOutputValue(args, "op", op);
            UCL_AgentCommandRunner.ReportOutputValue(args, "mode", modeArg);
            UCL_AgentCommandRunner.ReportOutputValue(args, "repos", targets.Count.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "candidate_files", scannedFiles.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "ephemeral_skipped", ephemeral.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "commits", committed.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "blocked_repos", skippedRepos.ToString());
            // 這個數字要**一直印**（0 也印）：只在非零時才出現的欄位，讀者分不出「乾淨」與「沒量」。
            UCL_AgentCommandRunner.ReportOutputValue(args, "prestaged_repos", preStagedRepos.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "disabled_repos", disabledRepos.ToString());
            // 這三個跟 prestaged_repos 同理：**0 也印**。它們合起來就是「commits 為什麼是這個數」的答案，
            // 而只在非零時才出現的欄位，讀者分不出「乾淨」與「沒量」。
            UCL_AgentCommandRunner.ReportOutputValue(args, "failed_groups", failedGroups.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "empty_groups", emptyGroups.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "other_files", otherFiles.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "subptr_files", subPtrFiles.ToString());
            if (shas.Count > 0)
                UCL_AgentCommandRunner.ReportOutputValue(args, "shas", string.Join(" ", shas.ToArray()));

            // ⚠ 值先報完再丟 —— 呼叫端要的是「幾群失敗、為什麼」，而那些欄位不能被例外吃掉。
            //   丟例外的理由：git 操作失敗過的一輪**不該被判 Success**。
            //   已經成功的那幾群是真的（SHA 在 shas 欄），所以這不是回滾，是**拒絕把部分成功說成完成**。
            if (failedGroups > 0)
            {
                string aMsg = $"[AutoCommit] {failedGroups} 個群的 git 操作失敗（commit {committed} 群成功）"
                    + " —— 原因逐群印在上面那段 Editor log（`✗ … git add/commit 失敗 —— <stderr>`）。"
                    + " 常見一種是 `index.lock: File exists`：另一個 git process 正握著這個 repo 的 index"
                    + "（本 Cmd 刻意**不重試、不刪 lock** —— 刪別人的 lock 會讓那個 process 寫壞 index）。";
                Debug.LogError(aMsg);
                throw new Exception(aMsg);
            }
        }

        // 區塊職責：要做哪幾群 —— 已拆成兩支（2026-08-21 設定檔化）。
        // 物理意義：原本一支 ParseGroups 同時做「解析參數」與「取預設」，而設定檔化之後
        //          「預設」變成 **per-repo**（各 repo 的群 key 不同），兩件事不能再共用一個回傳值。
        //          ⇒ ParseExplicitGroups（顯式參數，沒給回 null）＋ DefaultOnGroups（單一 repo 的預設）。
        //          `__other`／`__subptr` 兩者都不含 ⇒ 仍然只有顯式列出才會被收（見檔頭硬擋①）。

        static string MessageOf(string iKey, UCL_AutoCommitRules.GroupDef[] iDefs, int iCount)
        {
            if (iKey == UCL_AutoCommitRules.KEY_SUBPTR)
                return $"chore(submodule): bump nested submodule pointers (auto) [{iCount} files]";
            if (iKey == UCL_AutoCommitRules.KEY_OTHER)
                return $"chore: sync unclassified generated files (auto) [{iCount} files]";
            foreach (var d in iDefs)
                if (d.Key == iKey) return $"{d.Message} [{iCount} files]";
            return $"chore: sync {iKey} (auto) [{iCount} files]";
        }

        // 區塊職責：掃 DataRoot 的 .gitmodules，收「自己帶 .ucl_autocommit.json」的 submodule。
        // 物理意義：設定檔是**加入的唯一憑據** —— 沒有設定檔就不收（不猜規則）。
        //          ⚠ 判準刻意是「有沒有設定檔」而不是「是不是 submodule」：後者會把所有 persona
        //            信件庫與別人的資料庫一起掃進來，而那些 repo 的分群規則不在這裡。
        static List<RepoTarget> CollectConfiguredSubmodules()
        {
            var list = new List<RepoTarget>();
            // 發現邏輯**不在這裡重寫**（見 UCL_AutoCommitConfig.DiscoverRepoPaths 的區塊註解）：
            // 頁面與本 Cmd 共用同一支，否則兩邊遲早對「有哪些 repo」給出不同答案而都不報錯。
            string root = UCL_AgentCommandsPath.DataRoot;
            if (string.IsNullOrEmpty(root)) return list;
            foreach (string dir in UCL_AutoCommitConfig.DiscoverRepoPaths(root))
            {
                string rel = Path.GetFileName(dir.TrimEnd('/'));

                UCL_AutoCommitConfig config;
                try { config = UCL_AutoCommitConfig.Load(dir); }
                catch (Exception e)
                {
                    // 壞掉的設定檔要**擋下這個 repo 並說為什麼**，不可靜默跳過 ——
                    // 「設定寫錯」與「這個 repo 沒設定」必須是兩種可分辨的結果。
                    list.Add(new RepoTarget
                    {
                        Root = dir,
                        Name = rel,
                        ConfigPath = UCL_AutoCommitConfig.PathOf(dir),
                        Blocked = $"設定檔讀取失敗：{e.Message}",
                    });
                    continue;
                }

                var errors = config.Validate();
                var target = new RepoTarget
                {
                    Root = dir,
                    Name = string.IsNullOrEmpty(config.m_Name) ? rel : config.m_Name,
                    ConfigPath = UCL_AutoCommitConfig.PathOf(dir),
                    Defs = config.ToGroupDefs(),
                    Disabled = !config.m_Enabled,
                };
                if (target.Disabled)
                {
                    // 停用**不是**錯誤 ⇒ 不進 blocked（那個數字的語意是「設定壞了」）。
                    // 但也不可靜默消失：自動創建的設定預設停用，若不回報就會變成
                    //「我明明加了設定檔，為什麼什麼都沒發生」——而那跟「沒被發現」同形。
                    target.Blocked = "設定為停用（Enabled=false）—— 到後台頁或設定檔開啟";
                }
                if (errors.Count > 0)
                    target.Blocked = "設定不合法：" + string.Join("；", errors);
                list.Add(target);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        static List<RepoTarget> CollectTargets(bool iLetters, bool iSubmodules, string iPersona, bool iIncludeOnline)
        {
            if (iSubmodules) return CollectConfiguredSubmodules();
            var list = new List<RepoTarget>();
            if (!iLetters)
            {
                string root = UCL_AgentCommandsPath.DataRoot;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    list.Add(new RepoTarget { Root = root.Replace('\\', '/'), Name = "AgentCommands" });
                return list;
            }

            string lettersRoot = UCL_LettersPath.Root;
            if (string.IsNullOrEmpty(lettersRoot) || !Directory.Exists(lettersRoot)) return list;
            var online = new HashSet<string>();
            foreach (var l in UCL_ActivePersonaLocks.ListOnline()) online.Add(l.Persona);

            foreach (string dir in Directory.GetDirectories(lettersRoot))
            {
                // submodule 的 .git 是**檔案**（gitdir: 指標），獨立 clone 才是目錄 —— 兩種都收
                string gitPath = Path.Combine(dir, ".git");
                if (!File.Exists(gitPath) && !Directory.Exists(gitPath)) continue;
                string name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(iPersona)
                    && !string.Equals(name, iPersona, StringComparison.Ordinal)) continue;
                bool isOnline = online.Contains(name);
                var t = new RepoTarget
                {
                    Root = dir.Replace('\\', '/'),
                    Name = name,
                    Online = isOnline,
                };
                if (isOnline && !iIncludeOnline)
                    t.Blocked = "persona 在線（可能正在寫；要收請帶 include_online=1）";
                list.Add(t);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        // 區塊職責：一個 repo 的 git status → 分群。
        // 物理意義：分類一律委派 UCL_AutoCommitRules.Classify（規則單一真相源）。
        //          `--porcelain` 對非 ASCII 檔名會加引號，要剝掉再比對前綴。
        int ScanOne(RepoTarget iRepo, UCL_AutoCommitRules.GroupDef[] iDefs, ref int oFiles)
        {
            int ephemeral = 0;
            var branch = Git(iRepo.Root, "rev-parse --abbrev-ref HEAD");
            iRepo.Branch = branch.stdout.Trim();
            if (branch.exit != 0)
            {
                iRepo.Blocked = $"git rev-parse 失敗（{branch.stderr.Trim()}）";
                return 0;
            }
            if (iRepo.Branch == "HEAD")
            {
                // detached：擋下不擋整批（見檔頭硬擋②）
                iRepo.Blocked = "detached HEAD —— commit 會落在游離節點，先 switch 回追蹤分支";
                return 0;
            }

            // 呼叫前的 index 快照 —— 在 stage 任何東西**之前**問，之後就分不出「誰放的」了。
            var staged = Git(iRepo.Root, "diff --cached --name-only");
            if (staged.exit == 0)
                foreach (string line in staged.stdout.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length > 0) iRepo.PreStaged.Add(s);
                }

            var st = Git(iRepo.Root, "status --porcelain=v1 --untracked-files=all");
            if (st.exit != 0)
            {
                iRepo.Blocked = $"git status 失敗（{st.stderr.Trim()}）";
                return 0;
            }
            var subPaths = SubmodulePaths(iRepo.Root);
            foreach (string line in st.stdout.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.Length < 4) continue;
                string path = l.Substring(3).Trim();
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) path = path.Substring(arrow + 4);      // rename：取新名
                if (path.Length >= 2 && path.StartsWith("\"") && path.EndsWith("\""))
                    path = path.Substring(1, path.Length - 2);
                if (string.IsNullOrEmpty(path)) continue;

                string key = UCL_AutoCommitRules.Classify(path, iDefs, subPaths.Contains(path));
                if (key == null) { ephemeral++; continue; }
                if (!iRepo.Groups.TryGetValue(key, out var list))
                    iRepo.Groups[key] = list = new List<string>();
                list.Add(path);
                oFiles++;
            }
            return ephemeral;
        }

        static HashSet<string> SubmodulePaths(string iRoot)
        {
            var set = new HashSet<string>();
            var r = Git(iRoot, "config --file .gitmodules --get-regexp path");
            if (r.exit != 0) return set;
            foreach (string line in r.stdout.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0) continue;
                int sp = l.IndexOf(' ');
                if (sp > 0 && sp + 1 < l.Length) set.Add(l.Substring(sp + 1).Trim());
            }
            return set;
        }

        // 區塊職責：一群一筆 commit —— 具名 stage（分批）→ **pathspec 提交** → 提交後對帳。
        // 物理意義：stage 用 `git add -- <files>` 逐批餵，**絕不 git add -A**
        //          （別人正在寫的檔會被一起帶走，而那不會有錯誤訊息）。
        //          訊息走 `-F <檔>` —— 長文一律走檔案，不賭引號（判準不是「含不含特殊字元」）。
        //          提交走 `--pathspec-from-file`（BUG-30 修法之二）：只提交這一群的路徑，
        //          index 裡的其他東西**在物理上不可能**被順手帶走 —— 擋 index 那條是「請你先處理」，
        //          這條是「就算擋漏了也帶不走」。⚠ 路徑清單走檔案而非命令列：一群可能上千檔，
        //          而 32k 命令列上限砍下來的形狀是「這筆少了幾個檔」，不是報錯。
        //          （`--pathspec-from-file` 需 git ≥ 2.25 —— 2020 年的版本；本機 2.39.2。）
        string CommitGroup(RepoTarget iRepo, List<string> iFiles, string iMessage, StringBuilder oLog)
        {
            for (int i = 0; i < iFiles.Count; i += CHUNK)
            {
                var sb = new StringBuilder("add --");
                for (int j = i; j < iFiles.Count && j < i + CHUNK; j++)
                    sb.Append(" \"").Append(iFiles[j]).Append('"');
                var add = Git(iRepo.Root, sb.ToString());
                if (add.exit != 0)
                {
                    oLog.AppendLine($"  ✗ {iRepo.Name}：git add 失敗 —— {add.stderr.Trim()}");
                    // 🩸 2026-08-31（summit）：**擋去路的守衛不擋歸路。**
                    //   分段 add 是逐 CHUNK 送的 ⇒ 前幾段可能已經進 index，而這裡直接 return ""
                    //   把那批**留在 index 裡**。而 index 非空正好命中檔頭硬擋④（`op=commit` 直接跳過
                    //   該 repo，**沒有繞法**）⇒ **失敗會把自己鎖在門外**：下一次、下下一次都被擋，
                    //   而擋下的理由（prestaged）跟真因（撞 index.lock）長得完全不一樣。
                    //   現場讀數：13:29 那次失敗留下 80 個 staged 檔（messages seq 15022–15092 ＝
                    //   排序後的第一個 CHUNK ＋ inbox），此後每一次 op=commit 都被自己的殘留擋著。
                    //   ⇒ 失敗要**把 index 還原成呼叫前的樣子**。這裡可以安全地 reset 這幾個路徑，
                    //     因為走到 CommitGroup 的前提就是 `PreStaged.Count == 0`（index 本來是空的）
                    //     —— 所以 unstage 這批不可能動到別人放進去的東西。
                    RollbackStaged(iRepo, iFiles, oLog);
                    return "";
                }
            }
            string tmp = Path.Combine(Path.GetTempPath(), $"ucl_autocommit_{Guid.NewGuid():N}.txt");
            string spec = Path.Combine(Path.GetTempPath(), $"ucl_autocommit_spec_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(tmp, iMessage + "\n", new UTF8Encoding(false));
                // 一行一路徑，**不加引號、不留尾行**：尾行的空字串會被 git 當成空 pathspec 而整筆拒絕。
                File.WriteAllText(spec, string.Join("\n", iFiles.ToArray()), new UTF8Encoding(false));
                var c = Git(iRepo.Root, $"commit -F \"{tmp}\" --pathspec-from-file=\"{spec}\"");
                if (c.exit != 0)
                {
                    oLog.AppendLine($"  ✗ {iRepo.Name}：git commit 失敗 —— "
                        + $"{(string.IsNullOrEmpty(c.stderr.Trim()) ? c.stdout.Trim() : c.stderr.Trim())}");
                    // 同上：commit 失敗時那批檔還在 index 裡，留著會擋住之後每一次 op=commit。
                    RollbackStaged(iRepo, iFiles, oLog);
                    return "";
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(spec)) File.Delete(spec); } catch { }
            }
            // 印 ✓ 不算數，讀回來才算：SHA 從 git 自己撈，不從 commit 的 stdout 猜。
            var head = Git(iRepo.Root, "rev-parse --short HEAD");
            string sha = head.exit == 0 ? head.stdout.Trim() : "?";
            oLog.AppendLine($"  ✓ {iRepo.Name} [{sha}] {iMessage}");
            ReconcileCommit(iRepo, iFiles, sha, oLog);
            return sha;
        }

        // 區塊職責：失敗時把 index 還原成呼叫前的樣子（unstage 這一群挑到的路徑）。
        // 物理意義：**只 unstage，不碰工作區** —— `reset --` 是 mixed reset 的 pathspec 形式，
        //          它把 index 拉回 HEAD 而檔案內容一個位元組都不動。⛔ 這裡永遠不用 `--hard`
        //          / `checkout --`：那會刪掉別人剛落盤的資料，而那是回不來的。
        // 數值影響：index 回到空；工作區不變 ⇒ 下一次 op=commit 可以重試（歸路存在）。
        // ⚠ 還原本身也會失敗（例如仍然握不到 index.lock）—— 那要說出來，因為那時
        //   「殘留還在」而下一次會被 prestaged 擋，人得知道去手動 unstage。
        static void RollbackStaged(RepoTarget iRepo, List<string> iFiles, StringBuilder oLog)
        {
            for (int i = 0; i < iFiles.Count; i += CHUNK)
            {
                var sb = new StringBuilder("reset --quiet --");
                for (int j = i; j < iFiles.Count && j < i + CHUNK; j++)
                    sb.Append(" \"").Append(iFiles[j]).Append('"');
                var r = Git(iRepo.Root, sb.ToString());
                if (r.exit == 0) continue;
                oLog.AppendLine($"  ⚠ {iRepo.Name}：失敗後還原 index 也失敗 —— {r.stderr.Trim()}"
                    + "　⇒ **index 裡還留著這一群的殘留**，下一次 op=commit 會被 prestaged 擋下；"
                    + "請手動 `git -C <repo> reset` 之後再跑（工作區沒有被動過）");
                return;
            }
            oLog.AppendLine($"  ↩ {iRepo.Name}：已把這一群從 index 還原（工作區未動）—— 可直接重試");
        }

        // 區塊職責：提交後對帳 —— 這一筆**實際**含哪些路徑，跟我挑的那份清單並排。
        // 物理意義：BUG-30 修法之三。前兩條是預防，這條是**讀數**：
        //          「我挑了哪些檔」與「這一筆實際提交了哪些檔」是兩件事，而工具原本只認得前者
        //          （訊息尾巴那個 `[N files]` 是分群自己算的，從不回頭問 git）。
        // ⚠ 只報「多出來的」不報「少了的」：少了通常是合法的（挑到的檔內容其實沒變 ⇒ 不進 diff），
        //   而誤報的代價跟漏報一樣真 —— 它會讓下一個人開始不信這條對帳。
        static void ReconcileCommit(RepoTarget iRepo, List<string> iFiles, string iSha, StringBuilder oLog)
        {
            var r = Git(iRepo.Root, "show --pretty=format: --name-only HEAD");
            if (r.exit != 0)
            {
                // 對帳跑不起來本身要說出來：靜默的「沒有多出來的檔」跟「沒量過」同形。
                oLog.AppendLine($"  ⚠ {iRepo.Name} [{iSha}] 提交後對帳失敗（git show：{r.stderr.Trim()}）—— 這筆沒有對帳讀數");
                return;
            }
            var picked = new HashSet<string>(iFiles);
            var extra = new List<string>();
            foreach (string line in r.stdout.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length == 0 || picked.Contains(s)) continue;
                extra.Add(s);
            }
            if (extra.Count == 0) return;
            oLog.AppendLine($"  ✗ {iRepo.Name} [{iSha}] **對帳不符**：這筆多帶了 {extra.Count} 個不在分群清單裡的檔");
            foreach (string f in PreviewPaths(extra)) oLog.AppendLine($"      {f}");
            Debug.LogError($"[AutoCommit] {iRepo.Name} [{iSha}] 提交內容與分群清單不符："
                + $"多出 {extra.Count} 檔（{string.Join(", ", PreviewPaths(extra).ToArray())}）"
                + " —— 分群訊息與實際內容已經脫鉤，這筆要人看（BUG-30 同族）");
        }

        /// <summary>列路徑用的節流：最多 20 筆，其餘只報數。洗版會讓人不讀，而不讀＝這些字等於沒寫。</summary>
        static List<string> PreviewPaths(List<string> iPaths)
        {
            const int LIMIT = 20;
            var list = new List<string>();
            for (int i = 0; i < iPaths.Count && i < LIMIT; i++) list.Add(iPaths[i]);
            if (iPaths.Count > LIMIT) list.Add($"…另有 {iPaths.Count - LIMIT} 筆未列");
            return list;
        }

        // 區塊職責：本 Cmd 的所有 git 呼叫都經這裡 —— 順手把 `core.quotepath=false` 釘在每一次呼叫上。
        // 物理意義：預設 quotepath=true 會把非 ASCII 路徑印成 C 風格的八進位轉義（一個中文字＝三段反斜線碼），
        //          而本檔剝的只是外層引號、不解轉義 ⇒ 兩處會安靜地壞：
        //          ① `git add -- <那串轉義>` 找不到檔（報成「git add 失敗」，看起來像 git 的錯）
        //          ② 提交後對帳把 `git show` 的轉義路徑跟自己的原始路徑比 ⇒ **每個中文檔名都誤報成「多帶」**
        //             —— 而誤報會讓下一個人開始不信這條對帳，代價跟漏報一樣真。
        //          🩸 實測（basecamp wake#68，scratch repo）：`git show --name-only` 把「c 空白 檔.txt」印成轉義串，
        //          帶 `-c core.quotepath=false` 才印回原字。含空白的路徑仍會被加外層引號（那層本檔本來就剝）。
        static (int exit, string stdout, string stderr) Git(string iWorkDir, string iArgs)
            => UCL_ProcessCli.Run("git", "-c core.quotepath=false " + iArgs, iWorkDir, PROC_TAG, nameof(Cmd_AutoCommit),
                GIT_TIMEOUT_MS,
                // git 在非互動環境不該彈認證視窗 —— 彈了就是卡到 timeout 才有人發現
                new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
    }
}
#endif
