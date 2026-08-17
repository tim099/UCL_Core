// 區塊職責：UCL_Core 內 C# 端「repo root（git root）」唯一解析點
// 物理意義：歷史上多處（UCL_AgentCommandQueue / UCL_CompileErrorTracker / UCL_DocCatalogScanner）
//          各自寫 `Application.dataPath/../..`，這條路徑解析假設 layout 是
//          `<gitRoot>/<UnityProjectFolder>/Assets/...`（EOV 的 CardGame/ wrapper 結構）。
//          對「Unity project root 本身就是 git root」的扁平結構（如 TEVI），
//          兩個 `..` 會跳到 <gitRoot> 的上一層 → Python 寫 <gitRoot>/AgentCommands/，
//          C# 卻監聽 <parent>/AgentCommands/，pending.trigger 永遠落單。
// 修法：對齊 Python `run_cmd.py` 的 `_find_git_root_by_walk` —— 從 Application.dataPath
//          往上 walk 找第一個含 `.git` 目錄的 ancestor（submodule 的 .git 是檔案 redirect，
//          應跳過繼續往上）。Cache 結果避免每次重算。所有 git-root 需求集中走本 getter。
// 2026-05-13 (Zeta): 去掉 #if UNITY_EDITOR guard — 純 path 解析無 Editor 依賴, runtime 可用.
using System.IO;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// Repo / Unity project 路徑的單一解析點。所有需要 git-root 或 Unity-project-root 的
    /// C# 端程式都應該透過本類，避免 layout 假設散落各檔。
    /// </summary>
    /// <remarks>
    /// <para><b>RepoRoot</b>：含 <c>.git</c> 的 ancestor — 對應 Python <c>run_cmd.py</c> 的 <c>GIT_ROOT</c>。
    /// AgentCommands/ / .compile_status.json / ChatTavern/ 等都掛在這之下。</para>
    /// <para><b>UnityProjectRoot</b>：<c>Assets/</c> 的 parent —— Unity 工程的根，含 <c>ProjectSettings/</c>。
    /// 在扁平 layout（TEVI）下 == RepoRoot；在 nested layout（EOV）下會差一層。</para>
    /// </remarks>
    public static class UCL_RepoPath
    {
        static string s_CachedRepoRoot;
        static string s_CachedUnityProjectRoot;

        /// <summary>git-root（含 .git 的 ancestor）。Cache 後不重算 — 同一個 editor session 內穩定。</summary>
        public static string RepoRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_CachedRepoRoot))
                {
                    s_CachedRepoRoot = ResolveRepoRoot();
                }
                return s_CachedRepoRoot;
            }
        }

        /// <summary>Unity project root（Assets 的 parent）。Cache 後不重算。</summary>
        public static string UnityProjectRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_CachedUnityProjectRoot))
                {
                    s_CachedUnityProjectRoot = Path.GetDirectoryName(
                        Path.GetFullPath(Application.dataPath))
                        .Replace('\\', '/');
                }
                return s_CachedUnityProjectRoot;
            }
        }

        /// <summary>git-root/AgentCommands 絕對路徑（不保證資料夾存在）。</summary>
        public static string AgentCommandsDir => Path.Combine(RepoRoot, "AgentCommands").Replace('\\', '/');

        static string s_CachedUCLCoreDir;

        // 區塊職責：UCL_Core 根的**絕對路徑**單一解析點（取代 UCL_EditorPath.CorePath 的定位職責）。
        // 物理意義：UCL_EditorPath.CorePath 走 AssetDatabase.FindAssets ——
        //          ① **main-thread only**（UCL_AwakeningService.ResolveAwakeningScriptPath 的
        //            「⚠ 只能在主執行緒呼叫」就是它逼出來的）
        //          ② 靠「找特定腳本檔名 + 路徑含 UCL_Core」的啟發式，撞名不會叫
        //          ③ 回**專案相對**路徑，與其他解析器的絕對路徑混用要手拼
        //          本 getter 純檔案系統搜尋 ⇒ 三個問題一起消失，且與 Python
        //          `_lib/ucl_paths.py::_find_ucl_core_dir` **同演算法**（都是找目錄名）。
        // 數值影響：cache 後不重算；找不到 → raise，不回專案外的另一份 checkout。
        // ⚠ 唯一來源是**相對**那份，絕對由它組出來（Tim 2026-08-17 拍板）。
        //   🩸 為什麼不是兩個各自解析：那就是兩個會各自漂移的真相源 ——
        //     本檔今天稍早正是這樣（UCL_EditorPath.CorePath 走 AssetDatabase 算相對、
        //     本類走資料夾搜尋算絕對），兩者哪天不一致時**兩邊都不會報錯**。
        //   為什麼「相對」是源而不是「絕對」：
        //     ① Unity 資產側（AssetDatabase / Resources 路徑）只吃相對，反向剝前綴會在
        //        core 不在 Assets 底下時失敗；
        //     ② 絕對可由 UnityProjectRoot + 相對**無損**組出。
        //   ⚠ 相對那份**不再走 AssetDatabase** —— 否則 main-thread only 的限制會沿著
        //     「絕對由相對組出」傳染回絕對側，等於把剛拿掉的限制又裝回去。
        public static string UCLCoreRelative
        {
            get
            {
                if (string.IsNullOrEmpty(s_CachedUCLCoreDir))
                {
                    string aAbs = FindUCLCoreDir();
                    if (string.IsNullOrEmpty(aAbs))
                        throw new System.IO.DirectoryNotFoundException(
                            "[UCL_RepoPath] Unity 專案樹內找不到 UCL_Core 資料夾。" +
                            "⚠ 刻意不往專案外搜 —— 專案外可能存在另一份 checkout，撿到它不會報錯但全錯。");
                    string aProj = UnityProjectRoot.TrimEnd('/') + "/";
                    s_CachedUCLCoreDir = aAbs.StartsWith(aProj, System.StringComparison.OrdinalIgnoreCase)
                        ? aAbs.Substring(aProj.Length)      // 例：Assets/Plugins/UCL_Core
                        : aAbs;                              // 專案外（理論上到不了，FindUCLCoreDir 只搜 Assets/）
                }
                return s_CachedUCLCoreDir;
            }
        }

        /// <summary>UCL_Core 根的絕對路徑 —— 由 <see cref="UCLCoreRelative"/> 組出，兩者不可能不一致。</summary>
        public static string UCLCoreDir =>
            Path.Combine(UnityProjectRoot, UCLCoreRelative).Replace('\\', '/');

        /// <summary>&lt;UCL_Core&gt;/Tools~/AgentCommands 絕對路徑 —— python 工具都住這裡。</summary>
        /// <remarks>
        /// 幾乎所有 CorePath 的用途其實都是「找 Tools~ 底下那支腳本」（persona_ocr_locate.py /
        /// awakening.py / run_cmd.py / sculpt.py …）。各處自己 <c>Path.Combine(…, "Tools~",
        /// "AgentCommands", x)</c> 拼一次 = 又一組會漂移的平行路徑。走 <see cref="CoreTool"/>。
        /// </remarks>
        public static string CoreToolsDir =>
            Path.Combine(UCLCoreDir, "Tools~", "AgentCommands").Replace('\\', '/');

        /// <summary>&lt;UCL_Core&gt;/Tools~/AgentCommands/&lt;iName&gt;（可含子路徑）。不檢查存在性。</summary>
        public static string CoreTool(string iName) =>
            Path.Combine(CoreToolsDir, iName).Replace('\\', '/');

        // 區塊職責：EditorPrefs 的 per-project 命名空間後綴
        // 物理意義：**EditorPrefs 是 per-machine 不是 per-project** —— 同一台機器上所有 Unity 專案
        //          共用 `HKCU\Software\Unity Technologies\Unity Editor 5.x` 一份。所以任何
        //          「屬於這個專案」的設定直接存 EditorPrefs，都會靜默漂到別的專案去，
        //          而且漂過去的值長得完全正常（血證：2026-08-11 GitSubmoduleSync 的
        //          `Root=D:/Unity/LY` 漂進 Bar 專案，於是在 Bar 按 pull 是誠實地 pull 了 LY，
        //          回報一整排 ✓ 而 Bar 一個位元組都沒動）。
        //          key 加上本後綴即可 per-project 隔離。
        // 數值影響：純字串，cache 後不重算。
        // ⚠ 演算法**刻意保持與 UCL_WelcomePage / UCL_AgentSkillManagerPage 原本各自的私有副本
        //   逐字一致**（`Application.dataPath.GetHashCode().ToString("X")`，不做正規化）——
        //   換演算法會讓既有使用者已存的 key 全部變孤兒（歡迎頁重彈一次、skill 快照重算）。
        //   dataPath 在專案存活期間穩定；搬動專案資料夾視為新安裝，設定重來（可接受）。
        // ⚠ 依賴 string.GetHashCode 在 Editor（Mono）內跨 process 穩定。若 Unity 換 CoreCLR
        //   （其 GetHashCode 預設隨機化）本值會每次不同 —— 屆時的失效方式是**設定退回預設**，
        //   而本頁的預設是「Root = 本專案」，失效方向安全，不會變成再次操作到別的 repo。
        static string s_CachedProjectFingerprint;

        /// <summary>
        /// 專案指紋 —— <c>Application.dataPath</c> 的 hash，給 EditorPrefs key 加綴用。
        /// **任何 per-project 的 EditorPrefs key 都該加它**，否則設定會跨專案汙染。
        /// </summary>
        public static string ProjectFingerprint =>
            s_CachedProjectFingerprint ??= Application.dataPath.GetHashCode().ToString("X");

        // 區塊職責：實際的 git-root walk 邏輯
        // 物理意義：與 Python `run_cmd.py:_find_git_root_by_walk` 等價 —
        //          只接受 `.git` 為**資料夾**的 ancestor。submodule 的 `.git` 是檔案
        //          (gitdir: redirect) → 跳過繼續往上 walk，直到找到「真實」repo 根。
        //          與 Python 用 `is_dir()` 對齊；UCL_DocCatalogScanner 舊版也是只接受 dir。
        // 數值影響：至多 walk 幾層 + 一次 Directory.Exists check；走完 cache 後永不重算
        // fallback：完全找不到（無 .git 環境）→ 退回舊行為 `dataPath/../..`，不破壞既有 nested layout
        static string ResolveRepoRoot()
        {
            // tier-1：從 dataPath 往上找 .git〖資料夾〗（現行行為，維持不變）
            try
            {
                var p = new DirectoryInfo(Application.dataPath);
                while (p != null && p.Parent != null)
                {
                    if (Directory.Exists(Path.Combine(p.FullName, ".git")))
                        return p.FullName.Replace('\\', '/');
                    p = p.Parent;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_RepoPath] git-walk 失敗，改走 UCL_Core 搜尋: {e.Message}");
            }

            // tier-2：找不到 .git ⇒ 搜尋名為 UCL_Core 的資料夾，讀它的 gitlink 精確上溯（Tim 2026-08-17）
            //   物理意義：submodule 的 `.git` 是檔案，內容形如 `gitdir: ../../../.git/modules/<path>`。
            //             那串 `../` **不是提示，是 git 自己寫下的精確層數** —— 數幾個就上溯幾層，
            //             不是「找第一個 .git 就停」的啟發式，也完全不吃 cwd。
            string aCore = FindUCLCoreDir();
            if (!string.IsNullOrEmpty(aCore))
            {
                string aFromGitlink = ResolveSuperprojectFromGitlink(aCore);
                if (!string.IsNullOrEmpty(aFromGitlink)) return aFromGitlink;
            }
            // tier-3：UCL_Core 不是 submodule（企劃專案 / vendored 拷貝）⇒ 退到資料夾名搜尋。
            //   AgentCommands 一定直接掛在 repo 根底下 ⇒ 找到它，它的 parent 就是 repo 根。
            //   ⚠ 與 tier-2 平行、不巢狀 —— 巢狀的話「沒有 UCL_Core 但有 AgentCommands」的專案會漏掉。
            string aAgentCmd = FindAgentCommandsDir();
            if (!string.IsNullOrEmpty(aAgentCmd))
                return Directory.GetParent(aAgentCmd).FullName.Replace('\\', '/');

            // tier-4：全部失敗 ⇒ **不猜**。
            //   舊版這裡是 `dataPath/../..`，那是一條「一定會回一個看起來合理的路徑」的路 ——
            //   而它回錯的時候，狀態檔會被寫進另一個目錄且不報錯（pending.trigger 落單那一族）。
            throw new System.IO.DirectoryNotFoundException(
                "[UCL_RepoPath] 解析不到 repo 根：dataPath 之上沒有 .git 資料夾，" +
                "也找不到 UCL_Core / AgentCommands 資料夾。\n" +
                "  處置：確認專案結構，或在 Unity 控制台顯式設定 AgentCommands 資料根。\n" +
                "  ⚠ 這裡刻意不 fallback 到 dataPath/../.. —— 猜一個看起來合理的根，" +
                "會讓狀態檔安靜地寫到別的地方。");
        }

        // 區塊職責：往上找、再往下找名為 iName 的資料夾（企劃專案防呆用的最後手段）。
        // 物理意義：先上溯（UCL_Core / AgentCommands 通常是 dataPath 的 ancestor 或其 sibling 子樹），
        //          上溯無果再從 Unity 專案根做一次有限深度的下潛。
        // 數值影響：只在 tier-1 失敗時才跑；下潛限制深度避免掃整顆磁碟。
        // 🩸 2026-08-17 血證：第一版是「先往上 walk、每層探 <parent>/<iName>」。
        //   從 <proj>/Assets 上溯到 D:/Unity 時，它探到 **專案外另一份 UCL_Core checkout**
        //   （D:/Unity/UCL_Core）就回傳了 —— 完全正常的字串、完全錯的目錄，而且不會報錯。
        //   ⇒ 只往下搜，且**上界鎖死在 Unity 專案根**：UCL_Core / AgentCommands 依定義都在
        //     專案樹內，沒有任何理由讓搜尋逃出去。逃得出去 = 遲早撿到別的 checkout。
        // 🩸 2026-08-17 血證其三（同一天同一個函式的第三個錯路徑）：
        //   把它寫成通用的 FindDirByName(name) 之後，「Assets 優先」修好了 UCL_Core，
        //   卻讓 AgentCommands 命中 `<UCL_Core>/Tools~/AgentCommands` —— 那是**同名但完全不同**的東西。
        //   ⇒ **通用化本身就是那隻 bug**：名字不決定位置，而通用搜尋一定會在某處找到「一個」同名的，
        //     然後回一個長得完全正常的錯路徑。兩個名字要兩套規則，各自寫死自己的搜尋範圍。
        static string FindUCLCoreDir(int iMaxDepth = 6)
        {
            try
            {
                // UCL_Core 依定義住在 Assets/ 底下（Unity 專案的 package/plugin 位置）。
                // 只搜 Assets/ ⇒ 同時避開 .git/modules 內的同名目錄與專案外的另一份 checkout。
                string aAssets = Path.Combine(UnityProjectRoot, "Assets");
                if (!Directory.Exists(aAssets)) return null;
                return FindDirByNameDown(new DirectoryInfo(aAssets), "UCL_Core", iMaxDepth);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_RepoPath] 搜尋 UCL_Core 失敗: {e.Message}");
                return null;
            }
        }

        // AgentCommands 依定義**直接掛在 repo 根底下**，永遠不巢狀 ⇒ 直接探兩個位置，不做搜尋。
        // （`<UCL_Core>/Tools~/AgentCommands` 是同名的**程式碼**目錄，不是狀態根 —— 搜尋會撿到它。）
        static string FindAgentCommandsDir()
        {
            try
            {
                string aProj = UnityProjectRoot;
                foreach (string aCand in new[] {
                    Path.Combine(aProj, "AgentCommands"),                      // 扁平 layout
                    Path.GetFullPath(Path.Combine(aProj, "..", "AgentCommands")) })  // nested layout
                {
                    if (Directory.Exists(aCand)) return aCand.Replace('\\', '/');
                }
                return null;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_RepoPath] 搜尋 AgentCommands 失敗: {e.Message}");
                return null;
            }
        }

        // 🩸 2026-08-17 血證其二：下潛沒有排除 `.git` 時，`<repo>/.git/modules/…/UCL_Core`
        //   會**排在 `Assets` 前面**被撿到 —— 回傳的是 git 內部的 modules 目錄，不是工作區。
        //   同一個函式連續兩版回錯路徑，兩次的回傳值都長得完全正常。
        //   ⇒ 目錄搜尋一定要有排除清單，而清單本身就是會漏的東西（所以 Assets/ 優先才是主防線）。
        static readonly string[] s_SkipDirs =
            { "Library", "Temp", "Logs", "obj", "bin", "Build", "Builds", "node_modules" };

        static string FindDirByNameDown(DirectoryInfo iDir, string iName, int iDepth)
        {
            if (iDir == null || iDepth < 0) return null;
            foreach (var aSub in iDir.GetDirectories())
            {
                if (aSub.Name.StartsWith(".")) continue;                    // .git / .vs / .idea …
                if (System.Array.IndexOf(s_SkipDirs, aSub.Name) >= 0) continue;
                if (aSub.Name == iName) return aSub.FullName.Replace('\\', '/');
                string aHit = FindDirByNameDown(aSub, iName, iDepth - 1);
                if (!string.IsNullOrEmpty(aHit)) return aHit;
            }
            return null;
        }

        // 區塊職責：讀 submodule 的 `.git` gitlink，數 `../` 精確上溯到 superproject 根。
        // 物理意義：`gitdir: ../../../.git/modules/Assets/Plugins/UCL_Core` ⇒ 上溯 3 層 = repo 根。
        // 失敗處置：`.git` 不是檔案（非 submodule）／內容是絕對路徑（worktree）／格式不符 → 回 null，
        //          交由呼叫端走下一 tier。**不猜**。
        static string ResolveSuperprojectFromGitlink(string iSubmoduleDir)
        {
            try
            {
                string aGitLink = Path.Combine(iSubmoduleDir, ".git");
                if (!File.Exists(aGitLink)) return null;          // 資料夾 = 獨立 repo；不存在 = 非 git
                string aLine = File.ReadAllText(aGitLink).Trim();
                const string kPrefix = "gitdir:";
                if (!aLine.StartsWith(kPrefix)) return null;
                string aRel = aLine.Substring(kPrefix.Length).Trim().Replace('\\', '/');
                if (!aRel.StartsWith("../")) return null;          // 絕對路徑（worktree）→ 不處理
                int aUp = 0;
                while (aRel.StartsWith("../")) { aUp++; aRel = aRel.Substring(3); }
                var p = new DirectoryInfo(iSubmoduleDir);
                for (int i = 0; i < aUp && p != null; i++) p = p.Parent;
                return p?.FullName.Replace('\\', '/');
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_RepoPath] gitlink 解析失敗（{iSubmoduleDir}）: {e.Message}");
                return null;
            }
        }
    }
}
