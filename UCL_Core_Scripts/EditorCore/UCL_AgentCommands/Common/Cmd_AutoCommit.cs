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
            "op=commit — 逐群 commit（純 git commit，無 trailer／無公告／不領薪；不 push、不 bump 父層） | " +
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

            int committed = 0, skippedRepos = 0, emptyGroups = 0;
            var shas = new List<string>();
            foreach (var t in targets)
            {
                if (!string.IsNullOrEmpty(t.Blocked))
                {
                    skippedRepos++;
                    sb.AppendLine($"  ⛔ {t.Name}：{t.Blocked} —— 跳過");
                    continue;
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
                }
                if (!any) sb.AppendLine($"  ・{t.Name}：這幾群都沒有候選檔");
            }

            sb.AppendLine($"  ⇒ {(op == "scan" ? "掃描" : "提交")}完成："
                + $"候選檔 {scannedFiles}／ephemeral 略過 {ephemeral}／"
                + $"commit {committed}／擋下的 repo {skippedRepos}");
            Debug.Log(sb.ToString());

            UCL_AgentCommandRunner.ReportOutputValue(args, "op", op);
            UCL_AgentCommandRunner.ReportOutputValue(args, "mode", modeArg);
            UCL_AgentCommandRunner.ReportOutputValue(args, "repos", targets.Count.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "candidate_files", scannedFiles.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "ephemeral_skipped", ephemeral.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "commits", committed.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "blocked_repos", skippedRepos.ToString());
            if (shas.Count > 0)
                UCL_AgentCommandRunner.ReportOutputValue(args, "shas", string.Join(" ", shas.ToArray()));
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
                };
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

        // 區塊職責：一群一筆 commit —— 具名 stage（分批）→ commit。
        // 物理意義：stage 用 `git add -- <files>` 逐批餵，**絕不 git add -A**
        //          （別人正在寫的檔會被一起帶走，而那不會有錯誤訊息）。
        //          訊息走 `-F <檔>` —— 長文一律走檔案，不賭引號（判準不是「含不含特殊字元」）。
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
                    return "";
                }
            }
            string tmp = Path.Combine(Path.GetTempPath(), $"ucl_autocommit_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(tmp, iMessage + "\n", new UTF8Encoding(false));
                var c = Git(iRepo.Root, $"commit -F \"{tmp}\"");
                if (c.exit != 0)
                {
                    oLog.AppendLine($"  ✗ {iRepo.Name}：git commit 失敗 —— "
                        + $"{(string.IsNullOrEmpty(c.stderr.Trim()) ? c.stdout.Trim() : c.stderr.Trim())}");
                    return "";
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            // 印 ✓ 不算數，讀回來才算：SHA 從 git 自己撈，不從 commit 的 stdout 猜。
            var head = Git(iRepo.Root, "rev-parse --short HEAD");
            string sha = head.exit == 0 ? head.stdout.Trim() : "?";
            oLog.AppendLine($"  ✓ {iRepo.Name} [{sha}] {iMessage}");
            return sha;
        }

        static (int exit, string stdout, string stderr) Git(string iWorkDir, string iArgs)
            => UCL_ProcessCli.Run("git", iArgs, iWorkDir, PROC_TAG, nameof(Cmd_AutoCommit),
                GIT_TIMEOUT_MS,
                // git 在非互動環境不該彈認證視窗 —— 彈了就是卡到 timeout 才有人發現
                new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
    }
}
#endif
