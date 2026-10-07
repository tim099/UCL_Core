using System.IO;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 將 UCL_Core 自身的 docs 模組（"ucl_core:" prefix）註冊到 <see cref="UCL_DocsModuleRegistry"/>。
    /// [物理意義] 取代過去 <c>UCL_URL</c> 內 static ctor 中針對 ucl_core 的硬編碼分支，改走通用 Registry 流程，
    ///           讓 UCL_Core 與其他下游模組走同一條路徑。
    /// [數值影響] 不直接影響遊戲狀態，但決定了 [HelpURL("ucl_core:...")] 的解析行為。
    /// [呼叫時機]
    ///   - Runtime：BeforeSceneLoad 階段觸發。
    ///   - Editor：載入 / 編譯後觸發，確保 Editor 模式下 [HelpURL] 點擊也能解析。
    /// </summary>
    public static class UCL_CoreDocsBootstrap
    {
        // [常數] UCL_Core 的 prefix、雲端 URL、manifest 名稱；集中此處避免散落於程式各處。
        private const string PREFIX = "ucl_core";
        private const string BUILD_BASE_URL = "https://github.com/tim099/UCL_Core/blob/Dev/";
        private const string MANIFEST_NAME = "UCL_LocalizedDocsManifest";
        private const string DOCS_SUBFOLDER = "Docs~";
        private const string DISPLAY_NAME = "UCL_Core";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        private static void Register()
        {
            // 區塊職責：建立 UCL_Core 的 docs 模組描述並註冊。
            // 物理意義：Editor 端透過 UCL_EditorPath.CorePath 自動定位 UCL_Core 根；Build 端拼接 GitHub Dev 分支 URL。
            // 數值影響：影響所有以 "ucl_core:" 起頭的 HelpURL 連結的最終目標。
            UCL_DocsModuleRegistry.Register(new UCL_DocsModule
            {
                Prefix = PREFIX,
                DisplayName = DISPLAY_NAME,
                DocsSubfolder = DOCS_SUBFOLDER,
                ManifestResourceName = MANIFEST_NAME,
                BuildBaseUrl = BUILD_BASE_URL,
#if UNITY_EDITOR
                ResolveBaseProvider = () => UCL_EditorPath.CorePath,
                // [Resources 寫入位置] UCL_Core 自家的 Resources 資料夾，與既有 manifest 路徑一致。
                ResourcesFolderProvider = () =>
                {
                    string aCore = UCL_EditorPath.CorePath;
                    return string.IsNullOrEmpty(aCore) ? null : Path.Combine(aCore, "Resources");
                },
#endif
            });

#if UNITY_EDITOR
            // 區塊職責：註冊 "repo:" prefix — 解析「相對於 git repo 根」的本地文件路徑 (e.g. repo:docs/... / repo:.claude/skills/...)。
            // 物理意義：不同於 ucl_core: (錨在 UCL_Core 根)，repo: 錨在「包含本 Unity 專案的 git repo 根」，
            //          base 走 UCL_URL.FindRepoRoot() (與 ".claude/" 相對路徑特例共用同一錨點，避免雙套 root-finding 漂移)。
            //          Editor-only — repo 內原始文件 (docs / .claude skills) 不會打包進 player build，故 Build 端不註冊。
            // 數值影響：決定 frontmatter related: 內 "repo:..." 連結在 Editor MarkdownViewer 內的開啟目標。
            // [idempotent] RuntimeInitializeOnLoadMethod 與 InitializeOnLoadMethod 會雙觸發 Register；已註冊則跳過避免 override warning。
            if (!UCL_URL.HasResolver("repo"))
            {
                UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
                    prefix: "repo",
                    // [Resolve] repo 根 + 相對路徑 → 絕對路徑；找不到 repo 根時回傳 null (由 UCL_URL 維持原 URL)。
                    resolver: (aRel) =>
                    {
                        string aRoot = UCL_URL.FindRepoRoot();
                        return string.IsNullOrEmpty(aRoot) ? null : Path.GetFullPath(Path.Combine(aRoot, aRel));
                    },
                    // [Exists] 驅動 lang→en fallback；repo 根缺失或檔案不存在皆回 false。
                    existsChecker: (aRel) =>
                    {
                        string aRoot = UCL_URL.FindRepoRoot();
                        return !string.IsNullOrEmpty(aRoot) && File.Exists(Path.GetFullPath(Path.Combine(aRoot, aRel)));
                    }));
            }

            // 區塊職責：註冊 "scp_core:" prefix — 解析「相對於 SCP_Core 根」的本地文件路徑 (e.g. scp_core:Docs~/Commit.md / scp_core:Skills~/scp-commit/SKILL.md)。
            // 物理意義：SCP_Core 是跨專案共用的 agent 機制（指令／skill／文件），UCL_Core 的文件只引用、不複製。
            //          SCP_Core 掛載位置因專案而異，所以不寫死路徑：先找 UCL_Core 的同層資料夾，再找 repo 根底下的常見位置。
            //          Editor-only —— 與 repo: 同理，原始文件不打包進 player build。
            // 數值影響：決定 frontmatter related: 內 "scp_core:..." 連結在 Editor MarkdownViewer 內的開啟目標；找不到 SCP_Core 時回傳 null，由 UCL_URL 維持原 URL。
            if (!UCL_URL.HasResolver("scp_core"))
            {
                UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
                    prefix: "scp_core",
                    resolver: (aRel) =>
                    {
                        string aRoot = FindScpCoreRoot();
                        return string.IsNullOrEmpty(aRoot) ? null : Path.GetFullPath(Path.Combine(aRoot, aRel));
                    },
                    existsChecker: (aRel) =>
                    {
                        string aRoot = FindScpCoreRoot();
                        return !string.IsNullOrEmpty(aRoot) && File.Exists(Path.GetFullPath(Path.Combine(aRoot, aRel)));
                    }));
            }
#endif
        }

#if UNITY_EDITOR
        // 區塊職責：定位 SCP_Core 根（含 Docs~ 的那一層）。
        // 物理意義：候選順序 = UCL_Core 同層 → repo 根底下 Assets/Plugins／Assets／根目錄；每個候選都要有 Docs~ 才算數。
        // 數值影響：找不到回傳 null（不猜），呼叫端據此保留原 URL。
        private static string FindScpCoreRoot()
        {
            string aCore = UCL_EditorPath.CorePath;
            string aRepo = UCL_URL.FindRepoRoot();
            string[] aCandidates =
            {
                string.IsNullOrEmpty(aCore) ? null : Path.Combine(aCore, "..", "SCP_Core"),
                string.IsNullOrEmpty(aRepo) ? null : Path.Combine(aRepo, "Assets", "Plugins", "SCP_Core"),
                string.IsNullOrEmpty(aRepo) ? null : Path.Combine(aRepo, "Assets", "SCP_Core"),
                string.IsNullOrEmpty(aRepo) ? null : Path.Combine(aRepo, "SCP_Core"),
            };
            foreach (string aPath in aCandidates)
            {
                if (string.IsNullOrEmpty(aPath)) continue;
                string aFull = Path.GetFullPath(aPath);
                if (Directory.Exists(Path.Combine(aFull, DOCS_SUBFOLDER))) return aFull;
            }
            return null;
        }
#endif
    }
}
