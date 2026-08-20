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
            "mode=agent（預設，掃 AgentCommands 本層）｜letters（掃 letters/<persona>/ 每個 repo） | " +
            "[groups=<key1,key2>] 只做這幾群（預設＝該模式所有 DefaultOn 的群；" +
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
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string op = GetArg(args, "op", "scan").Trim().ToLowerInvariant();
            if (op != "scan" && op != "commit")
                throw new Exception($"[AutoCommit] 未知 op '{op}'（scan / commit）");

            string modeArg = GetArg(args, "mode", "agent").Trim().ToLowerInvariant();
            bool letters = modeArg == "letters";
            if (modeArg != "letters" && modeArg != "agent")
                throw new Exception($"[AutoCommit] 未知 mode '{modeArg}'（agent / letters）");

            var defs = UCL_AutoCommitRules.Defs(letters);
            var wanted = ParseGroups(GetArg(args, "groups", ""), defs);
            // ⚠ 參數名刻意**不叫** `persona`：`run_cmd.py --persona <me>` 會把 persona 戳進 args
            //   （那是「這筆是誰派的」宣告，見 ucl-coding）⇒ 叫 persona 就會被那個宣告當成篩選條件。
            // 🩸 實測踩過：`--persona kiara` 讓 letters 模式的掃描範圍從 9 個 repo 縮成 1 個，
            //   而輸出是「repos=1」—— 看起來像「找不到其他 repo」的探索 bug，不像參數撞名。
            string personaFilter = GetArg(args, "only_persona", "").Trim();
            bool includeOnline = GetArg(args, "include_online", "0").Trim() == "1";

            var targets = CollectTargets(letters, personaFilter, includeOnline);
            if (targets.Count == 0)
                throw new Exception("[AutoCommit] 沒有可處理的 repo —— 檢查 mode / persona 參數");

            var sb = new StringBuilder();
            sb.AppendLine($"[AutoCommit] op={op} mode={(letters ? "letters" : "agent")} "
                + $"repos={targets.Count} groups={string.Join(",", new List<string>(wanted).ToArray())}");

            int ephemeral = 0, scannedFiles = 0;
            foreach (var t in targets) ephemeral += ScanOne(t, defs, ref scannedFiles);

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
                foreach (var g in wanted)
                {
                    if (!t.Groups.TryGetValue(g, out var files) || files.Count == 0) { emptyGroups++; continue; }
                    any = true;
                    string label = MessageOf(g, defs, files.Count);
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
            UCL_AgentCommandRunner.ReportOutputValue(args, "mode", letters ? "letters" : "agent");
            UCL_AgentCommandRunner.ReportOutputValue(args, "repos", targets.Count.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "candidate_files", scannedFiles.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "ephemeral_skipped", ephemeral.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "commits", committed.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "blocked_repos", skippedRepos.ToString());
            if (shas.Count > 0)
                UCL_AgentCommandRunner.ReportOutputValue(args, "shas", string.Join(" ", shas.ToArray()));
        }

        // 區塊職責：要做哪幾群。
        // 物理意義：預設＝該模式所有 DefaultOn 的群。`__other`／`__subptr` **不在預設裡**，
        //          只有顯式列出才會被收（見檔頭硬擋①）。
        static HashSet<string> ParseGroups(string iArg, UCL_AutoCommitRules.GroupDef[] iDefs)
        {
            var set = new HashSet<string>();
            string a = (iArg ?? "").Trim();
            if (string.IsNullOrEmpty(a))
            {
                foreach (var d in iDefs) if (d.DefaultOn) set.Add(d.Key);
                return set;
            }
            foreach (var raw in a.Split(','))
            {
                string k = raw.Trim();
                if (k.Length == 0) continue;
                set.Add(k);
            }
            return set;
        }

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

        static List<RepoTarget> CollectTargets(bool iLetters, string iPersona, bool iIncludeOnline)
        {
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
