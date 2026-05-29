// 文件關聯：對應的多語系說明文件
// English: Docs~/en/API/UCL_AgentCommand/Cmd_ReadHierarchy.md
// 日本語: Docs~/ja/API/UCL_AgentCommand/Cmd_ReadHierarchy.md
// 简体中文: Docs~/zh-Hans/API/UCL_AgentCommand/Cmd_ReadHierarchy.md
// 繁體中文: Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ReadHierarchy.md
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UCL.Core;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.ReadHierarchy
{
    /// <summary>
    /// Cmd_ReadHierarchy — 讀取當前 Unity Scene 的 Hierarchy 結構並以 markdown 輸出至 _last_op.md。
    /// 職責：把當前場景（或指定場景）的 GameObject 樹序列化成可讀文字，可選帶 component 摘要。
    /// 物理意義：給 agent 知道「Editor 此刻場景裡到底長什麼樣」—— 純讀取、絕不修改 transform。
    /// 數值影響：無；只讀場景物件圖，不觸發任何 Awake / Start / 序列化變更。
    /// 擴充彈性：args 預留 prefab / search / searchType / componentDetail 等鍵，後續無痛擴充。
    /// </summary>
    public class Cmd_ReadHierarchy : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ReadHierarchy";

        public override string ShortDescription => "Read the current Unity scene Hierarchy and dump it to _last_op.md as markdown.";

        /// <summary>
        /// 支援的參數說明。所有參數皆可選；無參數時讀 active scene、不含 component、不限深度。
        /// 兩個 mode：scene（預設）讀 SceneManager 的 GameObject；prefab 用 PrefabUtility.LoadPrefabContents 讀 asset。
        /// </summary>
        public override string ArgsSchema =>
            "mode=scene|prefab source 切換 (optional, default=scene); " +
            "[scene mode] scene=Scene name filter (optional, default=active scene); " +
            "[scene mode] root=Root GameObject name filter (optional, default=all roots); " +
            "[prefab mode] prefab=Prefab asset path, e.g. Assets/Foo.prefab (REQUIRED when mode=prefab); " +
            "depth=Max recursion depth, -1=unlimited (optional, default=-1); " +
            "includeInactive=true|false include inactive GO (optional, default=true); " +
            "includeComponents=true|false list components on each GO (optional, default=false); " +
            "componentDetail=name|fields component detail level (optional, default=name; 'fields' reserved for future); " +
            "search=Substring filter on GO name, case-insensitive (optional); " +
            "searchType=name|tag|layer|component (optional, default=name; only 'name' currently implemented)";

        public override string ExampleArgs => "includeComponents=true";

        public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_ReadHierarchy.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析 args + 委派給 BuildReport，最後以 markdown 寫入 _last_op.md
            // 物理意義：本方法只做 dispatch；真正建構 markdown 的邏輯放 BuildReport 便於測試與擴充
            // 數值影響：無；純讀取場景結構

            string aMode = GetArg(args, "mode", "scene").ToLowerInvariant();
            string aSceneName = GetArg(args, "scene", string.Empty);
            string aRootFilter = GetArg(args, "root", string.Empty);
            int aMaxDepth = ParseInt(GetArg(args, "depth", "-1"), -1);
            bool aIncludeInactive = ParseBool(GetArg(args, "includeInactive", "true"), true);
            bool aIncludeComponents = ParseBool(GetArg(args, "includeComponents", "false"), false);
            string aComponentDetail = GetArg(args, "componentDetail", "name");
            string aSearch = GetArg(args, "search", string.Empty);
            string aSearchType = GetArg(args, "searchType", "name");
            string aPrefabPath = GetArg(args, "prefab", string.Empty);

            // 向後相容：caller 沒帶 mode 但給了 prefab → 自動視為 mode=prefab
            // 物理意義：保留舊 caller 既有用法不破壞；只是現在會走真實現實作而非 placeholder
            // 數值影響：無；只是 dispatch 路徑決定
            if (string.Equals(aMode, "scene", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(aPrefabPath))
            {
                aMode = "prefab";
            }

            if (!string.Equals(aMode, "scene", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(aMode, "prefab", StringComparison.OrdinalIgnoreCase))
            {
                Cmd_ReadHierarchy_Helpers.RejectLastOp(
                    $"mode='{aMode}' 不認得。合法值: scene | prefab。");
                return;
            }

            // searchType 目前只實作 name，其餘回 Reject 明確化未實作
            // 物理意義：避免 caller 用了未實作的 searchType 還以為命中
            if (!string.IsNullOrEmpty(aSearch) && !string.Equals(aSearchType, "name", StringComparison.OrdinalIgnoreCase))
            {
                Cmd_ReadHierarchy_Helpers.RejectLastOp(
                    $"searchType='{aSearchType}' 尚未實作（目前只支援 name）。RESERVED for future: tag / layer / component。");
                return;
            }

            // componentDetail 目前只實作 name，'fields' 預留
            if (aIncludeComponents && !string.Equals(aComponentDetail, "name", StringComparison.OrdinalIgnoreCase))
            {
                Cmd_ReadHierarchy_Helpers.RejectLastOp(
                    $"componentDetail='{aComponentDetail}' 尚未實作（目前只支援 name）。RESERVED for future: fields（列出 SerializedField 與值）。");
                return;
            }

            // Mode dispatch：scene → BuildSceneReport / prefab → BuildPrefabReport
            // 物理意義：兩條路徑共用底層 WalkHierarchy + AppendNodeLine，只在 source 解析分支
            // 數值影響：無
            string aMarkdown;
            if (string.Equals(aMode, "prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(aPrefabPath))
                {
                    Cmd_ReadHierarchy_Helpers.RejectLastOp(
                        "mode=prefab 必須提供 prefab=<asset path> (e.g. prefab=Assets/Foo.prefab)。");
                    return;
                }
                aMarkdown = BuildPrefabReport(
                    iPrefabPath: aPrefabPath,
                    iMaxDepth: aMaxDepth,
                    iIncludeInactive: aIncludeInactive,
                    iIncludeComponents: aIncludeComponents,
                    iSearch: aSearch);
            }
            else
            {
                aMarkdown = BuildReport(
                    iSceneName: aSceneName,
                    iRootFilter: aRootFilter,
                    iMaxDepth: aMaxDepth,
                    iIncludeInactive: aIncludeInactive,
                    iIncludeComponents: aIncludeComponents,
                    iSearch: aSearch);
            }

            Cmd_ReadHierarchy_Helpers.ResolveLastOp(aMarkdown);
            await UniTask.CompletedTask;
        }

        /// <summary>
        /// 真正組 markdown 報告的方法 — 拆開以便未來測試 / 擴充 prefab 路徑共用。
        /// </summary>
        private static string BuildReport(
            string iSceneName,
            string iRootFilter,
            int iMaxDepth,
            bool iIncludeInactive,
            bool iIncludeComponents,
            string iSearch)
        {
            // 區塊職責：決定目標 Scene、收集 roots、套 root 濾鏡、走訪每棵子樹累積 markdown
            // 物理意義：把 Scene API 結果轉成人類可讀的階層樹文字
            // 數值影響：無，純讀取

            Scene aScene;
            if (string.IsNullOrEmpty(iSceneName))
            {
                aScene = SceneManager.GetActiveScene();
            }
            else
            {
                aScene = SceneManager.GetSceneByName(iSceneName);
                if (!aScene.IsValid())
                {
                    return BuildErrorReport($"Scene '{iSceneName}' not found or not loaded.");
                }
            }

            List<GameObject> aRoots = GameObjectLib.GetRootGameObjects(aScene, iIncludeInactive);

            // root 濾鏡：以 GO name 完全比對為主，未來可擴展 glob/path
            // 數值影響：濾掉不符合條件的根 GO 後繼續走訪
            if (!string.IsNullOrEmpty(iRootFilter))
            {
                aRoots.RemoveAll(go => go == null || !string.Equals(go.name, iRootFilter, StringComparison.Ordinal));
            }

            var aSb = new StringBuilder();
            aSb.AppendLine("# 🌲 Scene Hierarchy");
            aSb.AppendLine();
            aSb.Append("**Scene:** `").Append(aScene.name).Append('`');
            if (!string.IsNullOrEmpty(aScene.path)) aSb.Append("  (path: `").Append(aScene.path).Append("`)");
            aSb.AppendLine();
            aSb.Append("**Args:** depth=")
                .Append(iMaxDepth < 0 ? "∞" : iMaxDepth.ToString())
                .Append(" | includeInactive=").Append(iIncludeInactive)
                .Append(" | includeComponents=").Append(iIncludeComponents);
            if (!string.IsNullOrEmpty(iRootFilter)) aSb.Append(" | root=").Append(iRootFilter);
            if (!string.IsNullOrEmpty(iSearch)) aSb.Append(" | search=").Append(iSearch);
            aSb.AppendLine();

            int aTotalCount = 0;
            int aMatchedCount = 0;
            string aSearchLower = string.IsNullOrEmpty(iSearch) ? null : iSearch.ToLowerInvariant();

            aSb.AppendLine();
            aSb.AppendLine("## Hierarchy");
            aSb.AppendLine();

            if (aRoots.Count == 0)
            {
                aSb.AppendLine("_(no root GameObjects matched)_");
            }
            else
            {
                foreach (var aRoot in aRoots)
                {
                    if (aRoot == null) continue;
                    GameObjectLib.WalkHierarchy(aRoot.transform, (t, depth) =>
                    {
                        aTotalCount++;
                        // 套 search 濾鏡：未命中也走訪（為了 count 正確）但只 emit 命中行
                        // 物理意義：search miss 不寫進 md，但 hierarchy 統計仍計入便於了解 scene 規模
                        if (aSearchLower != null && !t.name.ToLowerInvariant().Contains(aSearchLower))
                        {
                            return;
                        }
                        aMatchedCount++;
                        AppendNodeLine(aSb, t, depth, iIncludeComponents);
                    }, iMaxDepth, iIncludeInactive);
                }
            }

            // 統計 footer 放最後：總/命中數 + 搜尋說明
            aSb.AppendLine();
            aSb.AppendLine("---");
            aSb.Append("**Stats:** ").Append(aTotalCount).Append(" GameObject(s) walked");
            if (aSearchLower != null) aSb.Append(", ").Append(aMatchedCount).Append(" matched search");
            aSb.AppendLine();

            return aSb.ToString();
        }

        /// <summary>
        /// 把單一節點寫成一行 markdown bullet。
        /// </summary>
        private static void AppendNodeLine(StringBuilder iSb, Transform iTransform, int iDepth, bool iIncludeComponents)
        {
            // 區塊職責：依 depth 做 indent，依 active 狀態加 ⊘ 灰標，可選 [Components] 摘要
            // 物理意義：產出 markdown bullet line，讓階層用縮排視覺呈現
            // 數值影響：無

            for (int i = 0; i < iDepth; ++i) iSb.Append("  ");
            iSb.Append("- ");
            bool aActive = iTransform.gameObject.activeSelf;
            if (!aActive) iSb.Append("⊘ ");
            iSb.Append('`').Append(iTransform.name).Append('`');
            if (iIncludeComponents)
            {
                var aComps = iTransform.gameObject.GetComponentTypeNames();
                if (aComps != null && aComps.Count > 0)
                {
                    iSb.Append("  · [");
                    for (int i = 0; i < aComps.Count; ++i)
                    {
                        if (i > 0) iSb.Append(", ");
                        iSb.Append(aComps[i]);
                    }
                    iSb.Append(']');
                }
            }
            iSb.AppendLine();
        }

        /// <summary>
        /// Prefab 模式：用 PrefabUtility.LoadPrefabContents 把 prefab 載入記憶體（不掛場景），
        /// 走訪後以 try-finally 確保 UnloadPrefabContents 釋放，避免 Editor 記憶體洩漏。
        /// </summary>
        /// <param name="iPrefabPath">Prefab asset 完整路徑 (Assets/.../X.prefab)</param>
        private static string BuildPrefabReport(
            string iPrefabPath,
            int iMaxDepth,
            bool iIncludeInactive,
            bool iIncludeComponents,
            string iSearch)
        {
            // 區塊職責：驗證 path → 載 prefab → 走訪輸出 markdown → finally Unload
            // 物理意義：把 prefab asset 的 GameObject 樹序列化成 markdown，與 scene 模式輸出格式對齊
            // 數值影響：載入暫存物件，try-finally 保證釋放；不修改 asset

            // Step 1：驗證 path 存在且可載為 GameObject asset
            // 物理意義：AssetDatabase 端先檢，比直接呼 LoadPrefabContents 失敗訊息友善
            var aAsset = AssetDatabase.LoadAssetAtPath<GameObject>(iPrefabPath);
            if (aAsset == null)
            {
                return BuildErrorReport(
                    $"Prefab not found at '{iPrefabPath}'. 預期 Assets/.../X.prefab 完整路徑（含 Assets/ 前綴與 .prefab 副檔名）。");
            }

            GameObject aRoot = null;
            try
            {
                // Step 2：載入到暫存記憶體（不掛任何 scene）
                aRoot = PrefabUtility.LoadPrefabContents(iPrefabPath);
                if (aRoot == null)
                {
                    return BuildErrorReport(
                        $"PrefabUtility.LoadPrefabContents 對 '{iPrefabPath}' 回 null（asset 損毀或非合法 prefab）。");
                }

                // Step 3：取 asset 類型 / variant base 資訊用於 markdown header
                // 物理意義：caller 可從 header 立刻看出這是 Regular / Variant / Model，variant 還會列 base path
                var aAssetType = PrefabUtility.GetPrefabAssetType(aAsset);
                string aVariantBase = string.Empty;
                if (aAssetType == PrefabAssetType.Variant)
                {
                    var aSource = PrefabUtility.GetCorrespondingObjectFromSource(aAsset);
                    if (aSource != null) aVariantBase = AssetDatabase.GetAssetPath(aSource);
                }

                // Step 4：組 markdown header + 共用走訪 body
                var aSb = new StringBuilder();
                aSb.AppendLine("# 🧩 Prefab Hierarchy");
                aSb.AppendLine();
                aSb.Append("**Asset path:** `").Append(iPrefabPath).Append('`').AppendLine();
                aSb.Append("**Asset type:** ").Append(aAssetType).AppendLine();
                if (!string.IsNullOrEmpty(aVariantBase))
                {
                    aSb.Append("**Variant base:** `").Append(aVariantBase).Append('`').AppendLine();
                }
                aSb.Append("**Args:** depth=")
                    .Append(iMaxDepth < 0 ? "∞" : iMaxDepth.ToString())
                    .Append(" | includeInactive=").Append(iIncludeInactive)
                    .Append(" | includeComponents=").Append(iIncludeComponents);
                if (!string.IsNullOrEmpty(iSearch)) aSb.Append(" | search=").Append(iSearch);
                aSb.AppendLine();
                aSb.AppendLine();
                aSb.AppendLine("## Hierarchy");
                aSb.AppendLine();

                // Step 5：共用走訪 + 搜尋濾鏡 + 累計統計
                // 物理意義：跟 BuildReport 同套邏輯（單一 root 版本），AppendNodeLine 完全共用
                int aTotalCount = 0;
                int aMatchedCount = 0;
                string aSearchLower = string.IsNullOrEmpty(iSearch) ? null : iSearch.ToLowerInvariant();

                GameObjectLib.WalkHierarchy(aRoot.transform, (t, depth) =>
                {
                    aTotalCount++;
                    if (aSearchLower != null && !t.name.ToLowerInvariant().Contains(aSearchLower))
                    {
                        return;
                    }
                    aMatchedCount++;
                    AppendNodeLine(aSb, t, depth, iIncludeComponents);
                }, iMaxDepth, iIncludeInactive);

                aSb.AppendLine();
                aSb.AppendLine("---");
                aSb.Append("**Stats:** ").Append(aTotalCount).Append(" GameObject(s) walked");
                if (aSearchLower != null) aSb.Append(", ").Append(aMatchedCount).Append(" matched search");
                aSb.AppendLine();

                return aSb.ToString();
            }
            finally
            {
                // CRITICAL：必須 Unload 釋放 Editor 記憶體
                // 物理意義：LoadPrefabContents 載入的暫存物件不自動清，漏 Unload = Editor 記憶體洩漏
                // 數值影響：釋放 prefab 暫存記憶體；try 路徑無論成功或例外都會跑這
                if (aRoot != null) PrefabUtility.UnloadPrefabContents(aRoot);
            }
        }

        private static string BuildErrorReport(string iMessage)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("# ⚠ ReadHierarchy Error");
            aSb.AppendLine();
            aSb.AppendLine(iMessage);
            return aSb.ToString();
        }

        // ── 小工具 ───────────────────────────────────────────
        /// <summary>從 args 取值，缺鍵或空值回 iDefault。</summary>
        private static string GetArg(Dictionary<string, string> args, string iKey, string iDefault)
        {
            if (args == null) return iDefault;
            if (!args.TryGetValue(iKey, out var v)) return iDefault;
            return string.IsNullOrEmpty(v) ? iDefault : v;
        }
        private static int ParseInt(string iValue, int iDefault)
        {
            return int.TryParse(iValue, out var v) ? v : iDefault;
        }
        private static bool ParseBool(string iValue, bool iDefault)
        {
            if (string.IsNullOrEmpty(iValue)) return iDefault;
            if (bool.TryParse(iValue, out var v)) return v;
            // 容錯：1/0 / yes/no
            if (iValue == "1") return true;
            if (iValue == "0") return false;
            return iDefault;
        }
    }

    /// <summary>
    /// Cmd_ReadHierarchy 專屬的 _last_op.md 寫入 helper（對齊 Cmd_TypeInspect / Cmd_Tavern pattern）。
    /// </summary>
    internal static class Cmd_ReadHierarchy_Helpers
    {
        public static void ResolveLastOp(string iMarkdown) => UCL_ChatTavernRender.WriteLastOp(iMarkdown);

        public static void RejectLastOp(string iMessage)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ ReadHierarchy Rejected\n\n{iMessage}\n");
            Debug.LogWarning($"[ReadHierarchy] {iMessage}");
            throw new InvalidOperationException(iMessage);
        }
    }
}
#endif
