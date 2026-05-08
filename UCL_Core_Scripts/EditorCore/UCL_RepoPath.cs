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
#if UNITY_EDITOR
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
#endif
