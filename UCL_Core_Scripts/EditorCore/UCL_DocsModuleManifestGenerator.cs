#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UCL.Core.EditorTools
{
    /// <summary>
    /// [職責] 為所有透過 <see cref="UCL_DocsModuleRegistry"/> 註冊的模組，掃描其 docs 資料夾並寫出 manifest 到對應 Resources。
    /// [物理意義] 取代原本 UCL_Core / RCG / 各模組各自一份的 generator，改由單一 generator 迭代註冊清單一次處理所有模組。
    /// [數值影響] 不影響遊戲狀態；僅產生資料檔。
    /// [觸發時機]
    ///   1. Build 前自動：透過 IPreprocessBuildWithReport 在每次 Build 開始前自動跑（<see cref="UCL_DocsModuleManifestBuildHook"/>）。
    ///   2. 手動：選單 "Tools / UCL / Generate All Localized Docs Manifests"。
    /// </summary>
    public static class UCL_DocsModuleManifestGenerator
    {
        /// <summary>
        /// [職責] 手動觸發所有已註冊模組的 manifest 重產，並彈出結果摘要對話框。
        /// </summary>
        [MenuItem("Tools/UCL/Generate All Localized Docs Manifests")]
        public static void GenerateAllMenuItem()
        {
            var aResults = GenerateAll();
            int aTotal = 0;
            var aSB = new System.Text.StringBuilder();
            foreach (var aPair in aResults)
            {
                aSB.AppendLine($"{aPair.Key}: {aPair.Value} entries");
                aTotal += aPair.Value;
            }
            EditorUtility.DisplayDialog(
                "Localized Docs Manifests",
                $"Generated {aResults.Count} manifest(s), {aTotal} entries total:\n\n{aSB}",
                "OK"
            );
        }

        /// <summary>
        /// [職責] 迭代 <see cref="UCL_DocsModuleRegistry.All"/>，對每個模組呼叫 <see cref="Generate"/>。
        /// [回傳] 每個 module DisplayName / Prefix → 條目數的對應；產生失敗者條目數為 -1。
        /// </summary>
        public static Dictionary<string, int> GenerateAll()
        {
            var aResults = new Dictionary<string, int>();
            foreach (var aModule in UCL_DocsModuleRegistry.All)
            {
                aResults[aModule.DisplayName ?? aModule.Prefix] = Generate(aModule);
            }
            return aResults;
        }

        /// <summary>
        /// [職責] 為單一模組掃描 docs 並寫入 manifest。
        /// [計算邏輯]
        ///   1. 透過 ResolveBaseProvider 取得模組根；空字串則跳過。
        ///   2. 掃描 base + DocsSubfolder 內所有 *.md（遞迴），路徑以「相對於 base」+ forward-slash 統一化。
        ///   3. 排序、加上 header 註解、寫入 ResourcesFolderProvider/<ManifestResourceName>.txt。
        ///   4. AssetDatabase.ImportAsset 同步匯入，避免 build 時序錯位。
        /// [數值影響] 影響該模組 manifest 內條目數；找不到 base / docs 資料夾時寫空 manifest。
        /// </summary>
        /// <returns>條目數；發生致命錯誤回 -1（不影響其他模組）。</returns>
        public static int Generate(UCL_DocsModule iModule)
        {
            // [輸入防護] 必填欄位缺漏時跳過，generator 應對 null / 缺漏寬容（避免單一模組失敗害全 build hook 中止）。
            if (iModule == null)
            {
                Debug.LogError("[UCL_DocsModuleManifestGenerator] Generate called with null module.");
                return -1;
            }
            string aDisplayName = iModule.DisplayName ?? iModule.Prefix;

            string aBase = iModule.ResolveBaseProvider?.Invoke();
            if (string.IsNullOrEmpty(aBase))
            {
                Debug.LogWarning($"[UCL_DocsModuleManifestGenerator] [{aDisplayName}] ResolveBaseProvider returned empty; manifest skipped.");
                return -1;
            }
            string aResourcesFolder = iModule.ResourcesFolderProvider?.Invoke();
            if (string.IsNullOrEmpty(aResourcesFolder))
            {
                Debug.LogWarning($"[UCL_DocsModuleManifestGenerator] [{aDisplayName}] ResourcesFolderProvider returned empty; manifest skipped.");
                return -1;
            }

            // 區塊職責：定位 scan root 並蒐集 .md 檔。
            // 物理意義：scan root = base + DocsSubfolder（後者為空時 = base 自身）；產生的條目相對於 base，故含 DocsSubfolder 前綴。
            // 數值影響：找不到 scan root 時寫空 manifest，runtime 失去 fallback 但不 crash。
            string aScanRoot = string.IsNullOrEmpty(iModule.DocsSubfolder)
                ? aBase
                : Path.Combine(aBase, iModule.DocsSubfolder);

            List<string> aRelativePaths = new();
            if (!Directory.Exists(aScanRoot))
            {
                Debug.LogWarning($"[UCL_DocsModuleManifestGenerator] [{aDisplayName}] scan root not found: {aScanRoot}; writing empty manifest.");
            }
            else
            {
                foreach (var aPath in Directory.EnumerateFiles(aScanRoot, "*.md", SearchOption.AllDirectories))
                {
                    // [.git 過濾] 雖然 .git 不太會有 .md，仍保險過濾。
                    if (aPath.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) continue;
                    if (aPath.Contains("/.git/")) continue;

                    string aRel = Path.GetRelativePath(aBase, aPath).Replace('\\', '/');
                    aRelativePaths.Add(aRel);
                }
                aRelativePaths.Sort(System.StringComparer.Ordinal);
            }

            // 區塊職責：組裝 manifest 文字內容。
            // 物理意義：行首 '#' 為註解；正式內容每行一條相對路徑。
            var aSB = new System.Text.StringBuilder();
            aSB.AppendLine($"# {iModule.ManifestResourceName}");
            aSB.AppendLine($"# Auto-generated by UCL_DocsModuleManifestGenerator at {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            aSB.AppendLine($"# DO NOT EDIT BY HAND - re-run \"Tools/UCL/Generate All Localized Docs Manifests\" instead.");
            aSB.AppendLine($"# Module: {aDisplayName} (prefix={iModule.Prefix})");
            aSB.AppendLine($"# Source: {aScanRoot}");
            aSB.AppendLine($"# Entries: {aRelativePaths.Count}");
            foreach (var aRel in aRelativePaths)
            {
                aSB.AppendLine(aRel);
            }
            string aContent = aSB.ToString();

            // 區塊職責：寫入檔案 + 通知 AssetDatabase。
            // 物理意義：File.WriteAllText 寫入 ResourcesFolder/<name>.txt；AssetDatabase.ImportAsset 同步匯入避免 build 時序錯位。
            string aManifestFullPath = Path.Combine(aResourcesFolder, iModule.ManifestResourceName + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(aManifestFullPath)!);
            File.WriteAllText(aManifestFullPath, aContent);

            // [Asset 重新匯入] 將絕對路徑轉成 Assets/ 開頭的相對路徑供 AssetDatabase 認得；轉不成功就只是省略 Refresh，不影響檔案寫入。
            string aAssetPath = AbsoluteToAssetPath(aManifestFullPath);
            if (!string.IsNullOrEmpty(aAssetPath))
            {
                AssetDatabase.ImportAsset(aAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            // [快取刷新] 通知 reader 下次查詢重新載入。
            UCL_DocsModuleManifest.Reload(iModule.ManifestResourceName);

            Debug.Log($"[UCL_DocsModuleManifestGenerator] [{aDisplayName}] wrote {aRelativePaths.Count} entries to {aManifestFullPath}");
            return aRelativePaths.Count;
        }

        // [輔助] 將絕對路徑轉為 Unity AssetDatabase 可識別的 "Assets/..." 相對路徑；不在 Assets/ 之下時回 null。
        private static string AbsoluteToAssetPath(string iAbs)
        {
            string aFull = Path.GetFullPath(iAbs).Replace('\\', '/');
            string aDataPath = Application.dataPath.Replace('\\', '/');
            if (!aFull.StartsWith(aDataPath, System.StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + aFull.Substring(aDataPath.Length);
        }
    }

    /// <summary>
    /// [職責] 在每次 Build 開始前重產所有已註冊模組的 manifest，避免人為遺忘。
    /// [物理意義] Unity 的 IPreprocessBuildWithReport hook，build 開始時呼叫 OnPreprocessBuild。
    /// </summary>
    public sealed class UCL_DocsModuleManifestBuildHook : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport iReport)
        {
            Debug.Log("[UCL_DocsModuleManifestBuildHook] Pre-build: regenerating all registered docs manifests...");
            UCL_DocsModuleManifestGenerator.GenerateAll();
        }
    }
}
#endif
