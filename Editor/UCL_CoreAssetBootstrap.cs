
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/07 2026
//
// 區塊職責：UCL_Core 預設 Asset 自動 bootstrap — 全新專案 clone UCL_Core 後自動補齊
//          .BuiltinModules/ModulesRoot/Modules/Core/ 下必要的 JSON Asset。
// 物理意義：[InitializeOnLoadMethod] 在 domain reload 時跑一次；對 Templates~ 整顆樹做遞迴掃描，
//          把目前還沒在使用者專案出現過的檔案列為 pending；create_if_missing 語意 — 絕不覆寫。
//          Templates~ 內檔案隨時可以新增 / 刪除，不必維護額外的 manifest 檔；
//          想觸發「再問一次」的時機由 TemplatesContentVersion 常數控制（bump 後使用者 marker 落後就會再掃）。
// 數值影響：寫入 Assets/.BuiltinModules/... 下的 JSON 檔；寫 ProjectSettings/UCL_CoreBootstrap.version
//          記錄 marker；首次套用結束後呼叫 AssetDatabase.Refresh 讓 Unity 接收。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// UCL_Core 預設 Asset 引導器。<br/>
    /// <list type="bullet">
    ///   <item>InitializeOnLoadMethod 自動執行：偵測缺漏並提示套用</item>
    ///   <item>Tools/UCL/Bootstrap 選單提供「補缺 / Diff / 強制覆寫」三顆手動入口</item>
    /// </list>
    /// 範本來源：<c>UCL_Core/Templates~/Assets/...</c>（整顆子樹遞迴掃描，無 manifest 檔）。
    /// </summary>
    public static class UCL_CoreAssetBootstrap
    {
        // ===========================================================
        // 區塊職責：版本常數
        // 物理意義：當 UCL_Core 維護者新增 / 修改了 Templates~ 內容、想讓既有專案被「再問一次」時，
        //          就把這個常數 +1。使用者 marker 比常數低 → 進掃描流程；相同 → 早期 return。
        //          採用 const 而非檔案，避免在範本資料夾內塞入「不算範本」的 metadata 檔案。
        // 數值影響：marker 比較唯一依據；不影響檔案複製邏輯本身（那是 file-by-file scan）。
        // ===========================================================
        public const int TemplatesContentVersion = 2;

        const string MarkerFileRelative = "ProjectSettings/UCL_CoreBootstrap.version";
        const string TemplatesDirName = "Templates~";
        const string TemplatesAssetsSubdir = "Assets"; // 只掃此子樹，避免誤抓 Templates~ 根層的 metadata
        const string CoreEditorAsmdef = "UCL_CoreEditor"; // 用此檔定位 UCL_Core/Editor 路徑

        // ===========================================================
        // 區塊職責：[InitializeOnLoadMethod] 入口
        // 物理意義：Editor 啟動 / domain reload 時自動跑；使用 delayCall 推遲一拍以避開 Editor
        //          初始化未完成的時點（與 UCL_WelcomeAutoOpen 同款手法）
        // 數值影響：在 missing 情境下會 schedule 一次 EditorUtility.DisplayDialog；
        //          其餘時間 0 副作用（marker 已最新就 early-return）
        // ===========================================================
        [InitializeOnLoadMethod]
        static void OnEditorLoad()
        {
            EditorApplication.delayCall += AutoApplyIfNeeded;
        }

        static void AutoApplyIfNeeded()
        {
            try
            {
                int applied = ReadMarker();
                if (applied >= TemplatesContentVersion)
                {
                    return; // marker 已是最新版 → 不掃 / 不彈窗，這是熱路徑
                }

                string templatesDir = GetTemplatesDir();
                if (templatesDir == null) return;

                var pending = ScanPending(templatesDir, GetProjectRoot());
                if (pending.Count == 0)
                {
                    // 沒有缺漏 → 直接寫 marker，下次重啟不再進這段
                    WriteMarker(TemplatesContentVersion);
                    return;
                }

                // 區塊職責：首次安裝 (applied==0) 直接套用，不打擾使用者
                // 物理意義：版本升級 (applied>0 && version 更高) 才彈 dialog 詢問，
                //          因為使用者可能已經有自定的版本，要 opt-in
                // 數值影響：dialog 回 No 時不寫 marker → 下次仍會詢問
                if (applied == 0)
                {
                    int n = ApplyEntries(templatesDir, pending, force: false);
                    WriteMarker(TemplatesContentVersion);
                    Debug.Log($"[UCL_Core Bootstrap] First-time install — applied {n} default asset(s).");
                    AssetDatabase.Refresh();
                }
                else
                {
                    string msg = $"UCL_Core 偵測到 {pending.Count} 個新增的預設 Asset (Templates v{applied} → v{TemplatesContentVersion})。\n\n是否套用？\n（不會覆蓋已存在的檔；只補缺漏。）";
                    int choice = EditorUtility.DisplayDialogComplex(
                        "UCL_Core: Apply New Defaults?",
                        msg,
                        "套用", "稍後再問", "不再提示");
                    if (choice == 0) // 套用
                    {
                        int n = ApplyEntries(templatesDir, pending, force: false);
                        WriteMarker(TemplatesContentVersion);
                        Debug.Log($"[UCL_Core Bootstrap] Applied {n} default asset(s) (Templates v{TemplatesContentVersion}).");
                        AssetDatabase.Refresh();
                    }
                    else if (choice == 2) // 不再提示 → 寫 marker 跳過
                    {
                        WriteMarker(TemplatesContentVersion);
                        Debug.Log("[UCL_Core Bootstrap] User skipped Templates update.");
                    }
                    // choice == 1 (稍後): 什麼都不做，下次 reload 再問
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_Core Bootstrap] Auto-apply failed: {e}");
            }
        }

        // ===========================================================
        // 區塊職責：Tools/UCL/Bootstrap 三顆手動入口
        // 物理意義：給開發者用：明確補缺 / 看差異 / 強制蓋（極少用）
        // 數值影響：Apply / Force 會寫檔；Diff 純讀
        // ===========================================================

        [MenuItem("Tools/UCL/Bootstrap/Apply Missing Defaults", priority = 200)]
        public static void Menu_ApplyMissing()
        {
            string templatesDir = GetTemplatesDir();
            if (templatesDir == null) return;
            var pending = ScanPending(templatesDir, GetProjectRoot());
            if (pending.Count == 0)
            {
                EditorUtility.DisplayDialog("UCL_Core Bootstrap", "沒有缺漏 — 所有預設 Asset 都已存在。", "OK");
                return;
            }
            int n = ApplyEntries(templatesDir, pending, force: false);
            WriteMarker(TemplatesContentVersion);
            Debug.Log($"[UCL_Core Bootstrap] Applied {n} missing asset(s) (Templates v{TemplatesContentVersion}).");
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/UCL/Bootstrap/Diff Against Templates", priority = 201)]
        public static void Menu_Diff()
        {
            string templatesDir = GetTemplatesDir();
            if (templatesDir == null) return;
            string projectRoot = GetProjectRoot();
            var allFiles = ScanAllTemplateFiles(templatesDir);

            var sb = new StringBuilder();
            sb.AppendLine($"# UCL_Core Bootstrap Diff (Templates v{TemplatesContentVersion})\n");
            sb.AppendLine($"Templates dir: {templatesDir}\n");
            int missing = 0, identical = 0, modified = 0;
            foreach (var rel in allFiles)
            {
                string srcPath = Path.Combine(templatesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string dstPath = Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dstPath))
                {
                    sb.AppendLine($"- ❌ MISSING in project: `{rel}`");
                    missing++;
                    continue;
                }
                if (FileBytesEqual(srcPath, dstPath)) { identical++; }
                else
                {
                    sb.AppendLine($"- ✎ MODIFIED locally: `{rel}`");
                    modified++;
                }
            }
            sb.AppendLine($"\nSummary: {missing} missing / {modified} modified / {identical} identical (total {allFiles.Count}).");
            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/UCL/Bootstrap/Force Re-Apply (Overwrite!)", priority = 202)]
        public static void Menu_ForceReapply()
        {
            if (!EditorUtility.DisplayDialog(
                "Force Re-Apply Defaults?",
                "此操作會用 Templates~ 內的範本【覆寫】所有對應 Asset，使用者本地修改會遺失。\n\n確定要繼續嗎？",
                "強制覆寫", "取消")) return;

            string templatesDir = GetTemplatesDir();
            if (templatesDir == null) return;
            var allFiles = ScanAllTemplateFiles(templatesDir);
            int n = ApplyEntries(templatesDir, allFiles, force: true);
            WriteMarker(TemplatesContentVersion);
            Debug.Log($"[UCL_Core Bootstrap] Force-applied {n} asset(s) (Templates v{TemplatesContentVersion}).");
            AssetDatabase.Refresh();
        }

        // ===========================================================
        // 區塊職責：套用核心邏輯
        // 物理意義：把 Templates~ 內的單檔複製到專案 .BuiltinModules 下；
        //          force=false（create_if_missing）時若目的檔已存在就略過；force=true 時無視全部覆寫
        // 數值影響：寫入若干 .json 檔；不刪除任何檔
        // ===========================================================
        static int ApplyEntries(string templatesDir, List<string> relativePaths, bool force)
        {
            if (string.IsNullOrEmpty(templatesDir) || relativePaths == null) return 0;
            string projectRoot = GetProjectRoot();
            int applied = 0;
            foreach (var rel in relativePaths)
            {
                string srcPath = Path.Combine(templatesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string dstPath = Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(srcPath))
                {
                    Debug.LogWarning($"[UCL_Core Bootstrap] template gone missing during apply — skip: {rel}");
                    continue;
                }

                bool dstExists = File.Exists(dstPath);
                if (dstExists && !force) continue; // create_if_missing 且已存在 → 不動

                try
                {
                    string dstDir = Path.GetDirectoryName(dstPath);
                    if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
                    File.Copy(srcPath, dstPath, overwrite: true);
                    applied++;
                    Debug.Log($"[UCL_Core Bootstrap] {(dstExists ? "OVERWRITE" : "CREATE")} {rel}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UCL_Core Bootstrap] copy failed for {rel}: {ex.Message}");
                }
            }
            return applied;
        }

        // ===========================================================
        // 區塊職責：掃描 Templates~ 整顆 Assets/ 子樹
        // 物理意義：相對路徑以 "Assets/" 開頭（即專案根相對路徑），與 .BuiltinModules 對應位置一一對應
        //          只走 Assets/ 子樹，避免誤抓 Templates~ 根層任何維護用 metadata
        // 數值影響：純讀檔系統；O(N) 隨檔數線性
        // ===========================================================
        static List<string> ScanAllTemplateFiles(string templatesDir)
        {
            var list = new List<string>();
            string assetsRoot = Path.Combine(templatesDir, TemplatesAssetsSubdir);
            if (!Directory.Exists(assetsRoot)) return list;

            int prefixLen = templatesDir.Length;
            // 確保 prefix 含結尾分隔符，substring 後是純相對路徑
            if (prefixLen > 0 && templatesDir[prefixLen - 1] != Path.DirectorySeparatorChar
                              && templatesDir[prefixLen - 1] != Path.AltDirectorySeparatorChar)
            {
                prefixLen += 1;
            }

            foreach (string abs in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                if (abs.Length <= prefixLen) continue;
                string rel = abs.Substring(prefixLen).Replace(Path.DirectorySeparatorChar, '/');
                list.Add(rel);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // ===========================================================
        // 區塊職責：找出「目前缺漏」的範本檔
        // 物理意義：對 Templates~ 內每一檔判定 dest 是否存在；不存在 → 列為 pending
        //          create_if_missing 唯一語意 — 沒有 force_overwrite 概念，那條路徑由 Force Re-Apply 選單處理
        // 數值影響：純讀取，不寫
        // ===========================================================
        static List<string> ScanPending(string templatesDir, string projectRoot)
        {
            var pending = new List<string>();
            foreach (var rel in ScanAllTemplateFiles(templatesDir))
            {
                string dstPath = Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dstPath)) pending.Add(rel);
            }
            return pending;
        }

        // ===========================================================
        // 區塊職責：路徑解析
        // 物理意義：透過 UCL_CoreEditor.asmdef 的位置反推 UCL_Core 根目錄，再接 Templates~。
        //          使用者把 UCL_Core 放在哪個資料夾都能找到。
        // 數值影響：純讀 AssetDatabase；找不到時印 LogError 並回 null
        // ===========================================================
        static string GetTemplatesDir()
        {
            string[] guids = AssetDatabase.FindAssets($"{CoreEditorAsmdef} t:AssemblyDefinitionAsset");
            if (guids == null || guids.Length == 0)
            {
                Debug.LogError($"[UCL_Core Bootstrap] Cannot locate {CoreEditorAsmdef}.asmdef — UCL_Core/Editor/ 找不到了？");
                return null;
            }
            string asmdefAssetPath = AssetDatabase.GUIDToAssetPath(guids[0]);   // "Assets/.../UCL_Core/Editor/UCL_CoreEditor.asmdef"
            string editorDir = Path.GetDirectoryName(asmdefAssetPath);          // "Assets/.../UCL_Core/Editor"
            string coreDirAssetPath = Path.GetDirectoryName(editorDir);         // "Assets/.../UCL_Core"
            string projectRoot = GetProjectRoot();
            string templatesDir = Path.Combine(projectRoot, coreDirAssetPath, TemplatesDirName);
            if (!Directory.Exists(templatesDir))
            {
                Debug.LogWarning($"[UCL_Core Bootstrap] Templates dir not found: {templatesDir}");
                return null;
            }
            return templatesDir;
        }

        static string GetProjectRoot() => Path.GetDirectoryName(Application.dataPath);

        // ===========================================================
        // 區塊職責：marker 讀寫
        // 物理意義：marker 內容只是一個整數版本號。放 ProjectSettings/ 不污染 Asset 樹
        // 數值影響：讀檔不存在時回 0（=從未套用）；寫檔時 ensure 資料夾存在
        // ===========================================================
        static int ReadMarker()
        {
            string path = Path.Combine(GetProjectRoot(), MarkerFileRelative);
            if (!File.Exists(path)) return 0;
            try
            {
                string s = File.ReadAllText(path, Encoding.UTF8).Trim();
                return int.TryParse(s, out int v) ? v : 0;
            }
            catch { return 0; }
        }

        static void WriteMarker(int version)
        {
            string path = Path.Combine(GetProjectRoot(), MarkerFileRelative);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, version.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_Core Bootstrap] Failed to write marker: {e}");
            }
        }

        // ===========================================================
        // 區塊職責：byte-level 比對（給 Diff 用）
        // 物理意義：純位元比對；template 與 dest 一致 = identical，否則 modified
        // 數值影響：只讀檔，不寫
        // ===========================================================
        static bool FileBytesEqual(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (fa.Length != fb.Length) return false;
                using var sa = fa.OpenRead();
                using var sb = fb.OpenRead();
                int x;
                while ((x = sa.ReadByte()) != -1)
                {
                    if (x != sb.ReadByte()) return false;
                }
                return true;
            }
            catch { return false; }
        }
    }
}
#endif
