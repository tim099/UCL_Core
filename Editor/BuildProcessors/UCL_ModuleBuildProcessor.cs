
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
            Debug.LogWarning($"UCL_ModuleBuildPostprocessor OnPreprocessBuild report:{report.UCL_ToString()},platform:{summary.platform},outputPath:{summary.outputPath}");
            UCL_ModulePath.ZipAllModules();
            //System.IO.Compression.ZipFile.CreateFromDirectory("zipdir", "todir");

        }
        public void OnPostprocessBuild(BuildReport report)
        {
            var summary = report.summary;
            Debug.LogWarning($"UCL_ModuleBuildPostprocessor OnPostprocessBuild report:{report.UCL_ToString()},platform:{summary.platform},outputPath:{summary.outputPath}");
            UCL_ModulePath.RemoveAllZipAllModules();
        }

        //[PostProcessBuild(1)]
        //public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
        //{
        //    Debug.Log($"UCL_ModuleBuildPostprocessor target:{target},pathToBuiltProject:{pathToBuiltProject}");
        //}
    }
}