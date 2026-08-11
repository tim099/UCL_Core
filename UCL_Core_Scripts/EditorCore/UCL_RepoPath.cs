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
            try
            {
                var p = new DirectoryInfo(Application.dataPath);
                while (p != null && p.Parent != null)
                {
                    string gitPath = Path.Combine(p.FullName, ".git");
                    if (Directory.Exists(gitPath))
                    {
                        return p.FullName.Replace('\\', '/');
                    }
                    p = p.Parent;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_RepoPath] git-walk failed, falling back: {e.Message}");
            }
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")).Replace('\\', '/');
        }
    }
}
