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
        }
    }
}
