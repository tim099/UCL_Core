
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
        // Template 自動覆蓋同步用 — 記錄使用者「跳過某衝突時的 Template 端 hash」，避免下次 reload 又 spam dialog
        // 行為：rel_path → sha1(當時 Templates~ 端內容)。Templates~ 內容未變 → 視為使用者已決定不接受此版本，silent skip；
        //       Templates~ 端被 bump 改動 → 視為新版本，重新 prompt
        const string TemplatePushSkipMarkerFile = "ProjectSettings/UCL_CoreTemplatePush.skipped.json";
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
            EditorApplication.delayCall += AutoTemplatePushIfNeeded;
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

        // ===========================================================
        // 區塊職責：Template 自動覆蓋同步（Templates~ → 專案 .BuiltinModules）入口
        // 物理意義：跨專案分發 Template 改動 — UCL_Core 維護者改了某 Template 檔 → 別的專案 pull UCL_Core
        //          後 Editor 啟動時自動把新版 Templates~ 內容覆蓋專案 .BuiltinModules 對應檔。
        //          這跟既有 `Apply Missing Defaults`（只補缺，不覆寫）互補。
        // 數值影響：寫入專案 .BuiltinModules 內若干 .json；不刪除任何檔
        // 觸發條件分流：
        //   - 新檔（Templates~ 有，專案沒有）→ 直接 silent 複製，無對話框（這跟 forward 的「補缺」behaviour 重疊但無害）
        //   - 衝突（兩邊都有但內容不同）→ 跳 Windows Explorer 風格 per-file 對話框（保護專案本地修改不被無聲覆蓋）
        //   - Templates~ 沒有 / 專案有 → 不處理（專案本地自訂，超出 scope）
        // ===========================================================

        enum TemplatePushStatus { NewInTemplate, Conflict }

        struct TemplatePushEntry
        {
            public string RelPath;             // 相對路徑（從 templates dir 起算，如 "Assets/.BuiltinModules/.../foo.json"）
            public string TemplatePath;        // Templates~ 端絕對路徑（src — 上游）
            public string ProjectPath;         // 專案端絕對路徑（dst — 下游）
            public TemplatePushStatus Status;
            public long TemplateSize;          // src
            public long ProjectSize;           // dst — 0 if NewInTemplate
        }

        // ===========================================================
        // 區塊職責：[InitializeOnLoadMethod] 自動觸發 Template push（Templates~ → 專案 .BuiltinModules）
        // 物理意義：跟前 Bootstrap 自動安裝同款（OnEditorLoad → delayCall）— 不需要使用者手動點 menu
        //          UCL_Core 維護者改了 Templates → 別的專案 pull UCL_Core 後 Editor 啟動就自動把新版推進專案
        // 數值影響：
        //   - 沒任何 entries → 0 副作用 silent return（熱路徑）
        //   - 只有新檔（無衝突）→ silent 自動複製，Console 印 log
        //   - 有衝突 → 過濾掉「上次 user 已 skip 且 Template hash 沒變」→ 剩下才彈 dialog
        //   - 過濾後仍 0 → silent return（使用者 skip 過後不重複 prompt 直到 Templates 又被 bump）
        // ===========================================================
        static void AutoTemplatePushIfNeeded()
        {
            try
            {
                string templatesDir = GetTemplatesDir();
                if (templatesDir == null) return;
                string projectRoot = GetProjectRoot();
                var entries = ScanTemplatePush(templatesDir, projectRoot);
                if (entries.Count == 0) return;

                // 過濾衝突：上次已 skip 且 Template hash 沒變的就不再問
                var skipMarker = LoadTemplatePushSkipMarker();
                var actionable = new List<TemplatePushEntry>();
                foreach (var e in entries)
                {
                    if (e.Status == TemplatePushStatus.Conflict)
                    {
                        string curHash = ComputeFileSha1(e.TemplatePath);  // ← 對 Templates 端 hash
                        if (skipMarker.TryGetValue(e.RelPath, out var prev) && prev == curHash)
                        {
                            continue; // 已 skip 過此 Template 版本 → 不再 prompt 直到 Templates 再被改
                        }
                    }
                    actionable.Add(e);
                }
                if (actionable.Count == 0) return;

                int newCount = 0, conflictCount = 0;
                foreach (var e in actionable)
                {
                    if (e.Status == TemplatePushStatus.NewInTemplate) newCount++;
                    else conflictCount++;
                }

                // 只有新檔（無衝突）→ silent auto，無 dialog
                if (conflictCount == 0)
                {
                    int written = ApplyTemplatePush(actionable, allowOverwriteAll: true, skipMarker: null);
                    Debug.Log($"[UCL_Core Bootstrap] Template 自動覆蓋寫入 {written} 個新檔（Templates~ → 專案 .BuiltinModules）。");
                    AssetDatabase.Refresh();
                    return;
                }

                // 有衝突 → 走 menu 版同樣的 per-file dialog 流程，但跳過已決定 skip 的
                RunTemplatePushWithDialogs(actionable, isAuto: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_Core Bootstrap] AutoTemplatePush 失敗: {ex.Message}");
            }
        }

        [MenuItem("Tools/UCL/Bootstrap/Push Templates → Modules (Force)", priority = 220)]
        public static void Menu_PushTemplates()
        {
            string templatesDir = GetTemplatesDir();
            if (templatesDir == null) return;
            string projectRoot = GetProjectRoot();

            var entries = ScanTemplatePush(templatesDir, projectRoot);
            if (entries.Count == 0)
            {
                EditorUtility.DisplayDialog("Push Templates → Modules",
                    "沒有可推送的變動 — 專案 .BuiltinModules 已對齊 Templates~。", "OK");
                return;
            }
            // Menu 入口：忽略 skip marker（使用者主動觸發 → 強迫重問所有衝突）
            RunTemplatePushWithDialogs(entries, isAuto: false);
        }

        // ===========================================================
        // 區塊職責：實際跑 dialog + 寫檔流程（auto 與 menu 共用）
        // 物理意義：summary dialog → 逐筆處理（新檔 silent，衝突 per-file dialog）→ 完成 dialog
        // 數值影響：寫專案 .BuiltinModules 內若干 .json；衝突被「跳過」者 → 寫 skip marker
        // ===========================================================
        static void RunTemplatePushWithDialogs(List<TemplatePushEntry> entries, bool isAuto)
        {
            int newCount = 0, conflictCount = 0;
            foreach (var e in entries)
            {
                if (e.Status == TemplatePushStatus.NewInTemplate) newCount++;
                else conflictCount++;
            }

            string summaryTitle = isAuto
                ? "Push Templates → Modules (自動偵測)"
                : "Push Templates → Modules";
            string summaryPrefix = isAuto
                ? "Editor 啟動時偵測到 Templates~ 與專案 .BuiltinModules 有差異（Templates 已被上游更新）：\n"
                : "";
            string summary =
                summaryPrefix +
                $"檢測到 {entries.Count} 個 Template 變動將推進專案 .BuiltinModules：\n" +
                $"  - 新檔（自動複製）：{newCount}\n" +
                $"  - 衝突（專案本地已修改，per-file 確認）：{conflictCount}\n\n" +
                $"是否繼續？";
            if (!EditorUtility.DisplayDialog(summaryTitle, summary, "繼續", "取消"))
            {
                if (isAuto)
                {
                    // 使用者按取消 → 把所有衝突項以「當前 Templates hash」當作 skip 寫進 marker
                    // 下次 reload 同樣 Templates 內容不再 spam；Templates 再被 bump 才會重問
                    var marker = LoadTemplatePushSkipMarker();
                    foreach (var e in entries)
                    {
                        if (e.Status == TemplatePushStatus.Conflict)
                            marker[e.RelPath] = ComputeFileSha1(e.TemplatePath);
                    }
                    SaveTemplatePushSkipMarker(marker);
                }
                return;
            }

            var skipMarker = LoadTemplatePushSkipMarker();
            int written = ApplyTemplatePush(entries, allowOverwriteAll: false, skipMarker: skipMarker);
            int skipped = entries.Count - written;
            SaveTemplatePushSkipMarker(skipMarker);

            string done = $"完成：寫入 {written} 檔 / 跳過 {skipped} 檔。";
            EditorUtility.DisplayDialog(summaryTitle, done, "OK");
            AssetDatabase.Refresh();
        }

        // ===========================================================
        // 區塊職責：跑寫檔 loop — 新檔 silent / 衝突 per-file dialog（Win Explorer 風格）
        // 物理意義：allowOverwriteAll=true → 全部寫不問（auto silent path 用）；
        //          allowOverwriteAll=false → 衝突逐筆問
        // 數值影響：寫專案 .BuiltinModules；skipMarker 不為 null 時，「跳過」會 record 當下 Templates hash
        // ===========================================================
        static int ApplyTemplatePush(List<TemplatePushEntry> entries, bool allowOverwriteAll, Dictionary<string, string> skipMarker)
        {
            int written = 0;
            bool overwriteAllRemaining = allowOverwriteAll;

            foreach (var e in entries)
            {
                if (e.Status == TemplatePushStatus.NewInTemplate)
                {
                    if (CopyTemplateToProject(e)) written++;
                    continue;
                }
                // 衝突
                if (overwriteAllRemaining)
                {
                    if (CopyTemplateToProject(e)) written++;
                    continue;
                }
                string msg =
                    $"路徑：{e.RelPath}\n\n" +
                    $"Templates~ (上游 / src)：{e.TemplateSize} bytes\n" +
                    $"專案 .BuiltinModules (本地 / dst)：{e.ProjectSize} bytes\n\n" +
                    $"是否用上游 Template 覆蓋本地？\n（本地已修改，覆蓋會遺失自訂內容）";
                int choice = EditorUtility.DisplayDialogComplex(
                    $"Template 衝突: {Path.GetFileName(e.RelPath)}",
                    msg,
                    "覆蓋",                       // 0 = ok
                    "全部覆蓋(剩餘衝突)",         // 1 = cancel slot
                    "保留本地(跳過)"              // 2 = alt
                );
                if (choice == 0)
                {
                    if (CopyTemplateToProject(e)) written++;
                    skipMarker?.Remove(e.RelPath);
                }
                else if (choice == 1)
                {
                    overwriteAllRemaining = true;
                    if (CopyTemplateToProject(e)) written++;
                    skipMarker?.Remove(e.RelPath);
                }
                else // 2 = 保留本地 → record 當下 Templates hash
                {
                    if (skipMarker != null)
                        skipMarker[e.RelPath] = ComputeFileSha1(e.TemplatePath);
                }
            }
            return written;
        }

        // ===========================================================
        // 區塊職責：Template push skip marker 讀寫
        // 物理意義：JSON — { rel_path: sha1_of_template_when_skipped }；
        //          使用者「保留本地」過此版本 Template → 此 hash 不變期間都 silent skip；
        //          Templates~ 又被改 → hash 變動 → 重新 prompt
        // 數值影響：每次 sync 結束時刷新；不存在 / 損毀 → 視為空 dict
        // ===========================================================
        static Dictionary<string, string> LoadTemplatePushSkipMarker()
        {
            string path = Path.Combine(GetProjectRoot(), TemplatePushSkipMarkerFile);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return dict;
            try
            {
                string txt = File.ReadAllText(path, Encoding.UTF8);
                // 簡易 JSON 解析（避免引外部依賴）— 假設格式 { "key": "val", ... }
                int i = 0;
                while (i < txt.Length)
                {
                    int kStart = txt.IndexOf('"', i); if (kStart < 0) break;
                    int kEnd = txt.IndexOf('"', kStart + 1); if (kEnd < 0) break;
                    string key = txt.Substring(kStart + 1, kEnd - kStart - 1);
                    int colon = txt.IndexOf(':', kEnd); if (colon < 0) break;
                    int vStart = txt.IndexOf('"', colon); if (vStart < 0) break;
                    int vEnd = txt.IndexOf('"', vStart + 1); if (vEnd < 0) break;
                    string val = txt.Substring(vStart + 1, vEnd - vStart - 1);
                    dict[key] = val;
                    i = vEnd + 1;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_Core Bootstrap] 讀 reverse-sync skip marker 失敗：{e.Message}");
            }
            return dict;
        }

        static void SaveTemplatePushSkipMarker(Dictionary<string, string> dict)
        {
            string path = Path.Combine(GetProjectRoot(), TemplatePushSkipMarkerFile);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("{\n");
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  \"").Append(EscapeJsonString(kv.Key)).Append("\": \"").Append(EscapeJsonString(kv.Value)).Append("\"");
                }
                sb.Append("\n}\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_Core Bootstrap] 寫 reverse-sync skip marker 失敗：{e.Message}");
            }
        }

        static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string ComputeFileSha1(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA1.Create();
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return ""; }
        }

        static bool CopyTemplateToProject(TemplatePushEntry e)
        {
            try
            {
                string dstDir = Path.GetDirectoryName(e.ProjectPath);
                if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
                File.Copy(e.TemplatePath, e.ProjectPath, overwrite: true);
                Debug.Log($"[UCL_Core Bootstrap] TEMPLATE-PUSH {e.Status}: {e.RelPath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_Core Bootstrap] template push copy failed for {e.RelPath}: {ex.Message}");
                return false;
            }
        }

        // ===========================================================
        // 區塊職責：掃描 Templates~ 與專案 .BuiltinModules 的差異（用於 Template 自動推送）
        // 物理意義：兩種要 push 過去的情境：
        //   1. Templates~ 有 / 專案沒有 → NewInTemplate（Templates 加了新檔，專案要拿）
        //   2. 兩邊都有但 byte 不同 → Conflict（Templates 上游改了 + 專案本地也改了）
        // 不處理：專案有 / Templates~ 沒有 — 那是專案本地自訂，超出本工具 scope
        // 數值影響：純讀；O(N) 一輪 Templates~ 掃描
        // ===========================================================
        static List<TemplatePushEntry> ScanTemplatePush(string templatesDir, string projectRoot)
        {
            var entries = new List<TemplatePushEntry>();
            foreach (var rel in ScanAllTemplateFiles(templatesDir))
            {
                // .meta 不 sync — Unity 自動產生
                if (rel.EndsWith(".meta", StringComparison.Ordinal)) continue;

                string templateAbs = Path.Combine(templatesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string projectAbs = Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(templateAbs)) continue;  // template 端被刪了？skip
                long templateSize = new FileInfo(templateAbs).Length;

                if (!File.Exists(projectAbs))
                {
                    entries.Add(new TemplatePushEntry
                    {
                        RelPath = rel,
                        TemplatePath = templateAbs,
                        ProjectPath = projectAbs,
                        Status = TemplatePushStatus.NewInTemplate,
                        TemplateSize = templateSize,
                        ProjectSize = 0,
                    });
                }
                else if (!FileBytesEqual(templateAbs, projectAbs))
                {
                    entries.Add(new TemplatePushEntry
                    {
                        RelPath = rel,
                        TemplatePath = templateAbs,
                        ProjectPath = projectAbs,
                        Status = TemplatePushStatus.Conflict,
                        TemplateSize = templateSize,
                        ProjectSize = new FileInfo(projectAbs).Length,
                    });
                }
            }
            entries.Sort((a, b) =>
            {
                // 新檔排前面（先看到無衝突的，再進衝突確認）
                int cs = a.Status.CompareTo(b.Status);
                if (cs != 0) return cs;
                return string.Compare(a.RelPath, b.RelPath, StringComparison.Ordinal);
            });
            return entries;
        }
    }
}
#endif
