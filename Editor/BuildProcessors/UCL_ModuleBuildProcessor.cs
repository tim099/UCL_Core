
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 05/07 2024 10:03
using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace UCL.Core
{
    
    public class UCL_ModuleBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;
        /// <summary>
        /// https://learn.microsoft.com/zh-tw/dotnet/api/system.io.compression.zipfile?view=net-8.0
        /// </summary>
        /// <param name="report"></param>
        public void OnPreprocessBuild(BuildReport report)
        {
            var summary = report.summary;
            // 區塊職責：判定本次 build target 是否為 Standalone(PC)，決定 m_PCDirectStreaming 是否生效。
            // 物理意義：只有 PC build 的 StreamingAssets 是真實磁碟資料夾、可同步直讀；其他平台一律走原 zip+install。
            // 數值影響：把 BuildTarget→bool 的判定留在 Editor 組件 (UCL_ModulePath.cs 是 runtime 組件不能引 UnityEditor)。
            bool aIsStandalone = IsStandaloneBuildTarget(summary.platform);
            Debug.LogWarning($"UCL_ModuleBuildPostprocessor OnPreprocessBuild report:{report.AllFieldToString()},platform:{summary.platform},outputPath:{summary.outputPath},IsStandalone:{aIsStandalone}");
            UCL_ModulePath.OnPreprocessBuild(aIsStandalone);
            //System.IO.Compression.ZipFile.CreateFromDirectory("zipdir", "todir");

        }

        // 判定 BuildTarget 是否屬於 Standalone(PC) 家族 (Windows/OSX/Linux)。
        static bool IsStandaloneBuildTarget(BuildTarget iTarget)
        {
            switch (iTarget)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return true;
            }
            return false;
        }
        public void OnPostprocessBuild(BuildReport report)
        {
            var summary = report.summary;
            Debug.LogWarning($"UCL_ModuleBuildPostprocessor OnPostprocessBuild report:{report.AllFieldToString()},platform:{summary.platform},outputPath:{summary.outputPath}");
            UCL_ModulePath.RemoveAllZipAllModules();
            UCL_ModulePath.RemoveDirectStreamingRawModules();//清理 PC 免安裝模組複製進 StreamingAssets 的原始檔副本
        }

        //[PostProcessBuild(1)]
        //public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
        //{
        //    Debug.Log($"UCL_ModuleBuildPostprocessor target:{target},pathToBuiltProject:{pathToBuiltProject}");
        //}
    }
}