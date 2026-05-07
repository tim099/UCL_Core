
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:48
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_AssetPath
    {
        // 區塊職責：TemplateModules 路徑快取 — 解析一次後重用
        // 物理意義：AssetDatabase.FindAssets 不便宜，且 UCL_Core 不會在 runtime 移動位置，
        //          所以 first-call 解出後可以一直 cache 直到 domain reload
        // 數值影響：runtime build 中此欄位永遠不被讀（GetPath 直接回 empty），所以 #if UNITY_EDITOR 包起來
#if UNITY_EDITOR
        private static string s_TemplateModulesPath;
#endif

        /// <summary>
        /// Return the corresponding root directory path based on the enum
        /// notice the difference in slash directions on different operating systems according to Path.DirectorySeparatorChar
        /// </summary>
        /// <param name="iAssetType"></param>
        /// <returns></returns>
        public static string GetPath(UCL_AssetType iAssetType)
        {
            switch (iAssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        return Application.streamingAssetsPath;
                    }
                case UCL_AssetType.BuiltinModules:
                    {
                        return Path.Combine(Application.dataPath, ".BuiltinModules");
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        return Application.persistentDataPath;
                    }
                case UCL_AssetType.Addressables:
                    {
                        return string.Empty;
                    }
                case UCL_AssetType.TemplateModules:
                    {
                        return GetTemplateModulesPath();
                    }
            }
            return string.Empty;
        }

        // ===========================================================
        // 區塊職責：TemplateModules 路徑解析 — 找出 UCL_Core/Templates~/Assets/.BuiltinModules
        // 物理意義：透過 AssetDatabase 反推 UCL_CoreEditor.asmdef 位置 → 上溯 UCL_Core 根 → 接 Templates~/Assets/.BuiltinModules
        //          這樣使用者把 UCL_Core 放在 Assets/ 下任何路徑都能正確找到
        // 數值影響：純讀 AssetDatabase；runtime build 直接回 empty（Template 模式僅 Editor 可用）
        // ===========================================================
        static string GetTemplateModulesPath()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(s_TemplateModulesPath)) return s_TemplateModulesPath;
            string[] guids = UnityEditor.AssetDatabase.FindAssets("UCL_CoreEditor t:AssemblyDefinitionAsset");
            if (guids == null || guids.Length == 0)
            {
                Debug.LogWarning("[UCL_AssetPath] TemplateModules: UCL_CoreEditor.asmdef not found — Template mode will be unavailable.");
                return string.Empty;
            }
            string asmdefAssetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);  // "Assets/.../UCL_Core/Editor/UCL_CoreEditor.asmdef"
            string editorDir = Path.GetDirectoryName(asmdefAssetPath);                     // "Assets/.../UCL_Core/Editor"
            string coreDirAssetPath = Path.GetDirectoryName(editorDir);                    // "Assets/.../UCL_Core"
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            s_TemplateModulesPath = Path.Combine(projectRoot, coreDirAssetPath, "Templates~", "Assets", ".BuiltinModules");
            return s_TemplateModulesPath;
#else
            return string.Empty;
#endif
        }
    }
}
