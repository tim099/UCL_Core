
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/05 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：本檔提供「批次解析 UCL_Asset 連動關係」的 Agent Command 實作。
// 物理意義：給定起點 Asset（例：RCG_StoryData/AbandonedTemple），透過反射遞迴
//          掃描其欄位內所有 UCLI_AssetEntry 引用，沿邊呼叫 UCL_ModuleService 取得
//          被引用 Asset 的實際 JSON 路徑；依 maxDepth 限制 BFS 跳數、用 visited set
//          去重，最後輸出全部 (AssetType, ID, 相對路徑) 清單給 AI agent 一次取用。
// 數值影響：純讀取，不修改任何 Asset；只在指定 outputPath 寫入一份結果檔。
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：批次解析 UCL_Asset 的連動 Asset 鏈。
    ///
    /// 給定起點 Asset（type + id），用反射遞迴掃描所有 <see cref="UCLI_AssetEntry"/> 欄位 / 集合，
    /// 沿邊呼叫 <see cref="UCL_ModuleService"/> 取得被引用 Asset 的實際 JSON 路徑；
    /// 以 BFS + visited set 防止重複與無限遞迴；依 maxDepth 限制資產層級跳數。
    ///
    /// 典型用途：AI agent 想理解一個 Story 的完整資源依賴 — 包括它觸發的 BattleSet、
    /// BattleSet 引用的 Unit / Item / DropPool — 透過此指令一次拿到所有 JSON 路徑。
    ///
    /// 參數：
    /// - <c>assetType</c>（必填）：起點 Asset 的 C# Type 名稱，例 <c>RCG_StoryData</c>
    /// - <c>assetIds</c>（必填）：起點 Asset ID（CSV 多筆），例 <c>AbandonedTemple,MysteriousCave</c>
    /// - <c>maxDepth</c>（選填，預設 3）：BFS 最大層級（0 = 只列起點本身）
    /// - <c>outputPath</c>（選填）：結果檔案路徑（預設 <c>AgentCommands/asset_refs_&lt;timestamp&gt;.json</c>）
    /// - <c>format</c>（選填，<c>json</c> / <c>md</c>，預設 <c>json</c>）：輸出格式
    /// - <c>module</c>（選填）：限定模組（預設掃所有模組）
    /// </summary>
    public class Cmd_ResolveAssetReferences : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ResolveAssetReferences";

        public override string ShortDescription =>
            "Recursively walk UCL_Asset references with depth limit; output JSON paths for AI agents.";

        public override string ArgsSchema =>
            "assetType=Starting asset C# type name (e.g. RCG_StoryData)\n" +
            "assetIds=CSV asset IDs (e.g. AbandonedTemple,MysteriousCave)\n" +
            "maxDepth=BFS max depth in asset hops (default 3, 0 = only seeds)\n" +
            "outputPath=Output file path relative to project root (default AgentCommands/asset_refs_<ts>.json)\n" +
            "format=Output format: json (default) or md\n" +
            "module=Optional module name to restrict lookup (default: all modules)";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md";

        // ===== Internal records =====

        /// <summary>單一被解析到的 Asset 紀錄。</summary>
        private class AssetRef
        {
            public string AssetType;     // C# Type name
            public string Id;            // Asset ID
            public string Path;          // Project-root-relative path; empty if not found
            public int Depth;            // BFS depth from root seeds (0 = seed)
            public string ParentKey;     // Who pointed to me; "<root>" for seeds
            public bool Exists;          // Whether the JSON actually exists on disk
            public List<string> ChildKeys = new List<string>();  // outgoing references (Type:ID strings)
        }

        // 區塊職責：取得 Unity 專案根目錄（CardGame/）
        // 物理意義：Application.dataPath 為 CardGame/Assets，往上 1 層即 Unity project root。
        //          Cmd 輸出 / 路徑相對基準都以這裡為準 — 確保產物落在 CardGame/AgentCommands/，
        //          避免污染外層 git root，同時跟 submodule（CardGame/Assets/UCL/）也清楚分離。
        // 數值影響：所有 outputPath 與相對路徑都以 Unity project root 為基準。
        private static string ProjectRoot
        {
            get
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            }
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析參數
            // 物理意義：將 args 轉成具體的 assetType Type 物件、ID 清單、深度限制、輸出格式
            // 數值影響：無副作用；僅校驗輸入
            string assetTypeName = GetArg(args, "assetType", null);
            string assetIdsCsv = GetArg(args, "assetIds", null);
            int maxDepth = TryParseInt(GetArg(args, "maxDepth", "3"), 3);
            string outputPath = GetArg(args, "outputPath", null);
            string format = GetArg(args, "format", "json").ToLowerInvariant();
            string moduleFilter = GetArg(args, "module", null);

            if (string.IsNullOrWhiteSpace(assetTypeName) || string.IsNullOrWhiteSpace(assetIdsCsv))
            {
                Debug.LogError("[Cmd:ResolveAssetReferences] Missing required args: assetType / assetIds");
                return;
            }

            Type assetType = ResolveTypeByName(assetTypeName);
            if (assetType == null)
            {
                Debug.LogError($"[Cmd:ResolveAssetReferences] Cannot resolve C# Type from name: '{assetTypeName}'");
                return;
            }

            var seedIds = assetIdsCsv.Split(',')
                .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            // 區塊職責：BFS 遍歷
            // 物理意義：從種子 Asset 出發，層級 0 → 1 → 2 → ...，每次擴張用反射找出當前 Asset 內
            //          所有 UCLI_AssetEntry 引用，把新出現的（type+id）入隊列，已 visited 的跳過。
            // 數值影響：填充 results dict（key = "Type:ID"），決定哪些 Asset 會被納入輸出。
            var results = new Dictionary<string, AssetRef>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(Type Type, string Id, int Depth, string ParentKey)>();

            foreach (var id in seedIds)
            {
                queue.Enqueue((assetType, id, 0, "<root>"));
            }

            while (queue.Count > 0)
            {
                if (token.IsCancellationRequested) return;

                var (curType, curId, curDepth, parentKey) = queue.Dequeue();
                string key = MakeKey(curType, curId);

                if (results.TryGetValue(key, out var existing))
                {
                    // Already visited. Append the additional parent so we don't lose the cross-link info.
                    if (!string.IsNullOrEmpty(parentKey) && existing.ParentKey != parentKey)
                    {
                        // Don't overwrite first parent; just leave the original. (Could store list of parents.)
                    }
                    continue;
                }

                var entry = new AssetRef
                {
                    AssetType = curType.Name,
                    Id = curId,
                    Depth = curDepth,
                    ParentKey = parentKey,
                };

                // 嘗試取得 AssetConfig + 路徑
                AbsolutePathInfo info = TryGetAssetPath(curType, curId, moduleFilter);
                entry.Path = info.RelPath ?? string.Empty;
                entry.Exists = info.Exists;

                results[key] = entry;

                // 不超過 depth 限制才繼續展開引用
                if (curDepth >= maxDepth) continue;

                // 載入 asset object，反射找子引用
                object asset = TryGetAssetData(curType, curId);
                if (asset == null) continue;

                var collected = new List<(Type RefType, string RefId)>();
                var fieldVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                CollectAssetEntries(asset, collected, fieldVisited);

                foreach (var (refType, refId) in collected)
                {
                    if (string.IsNullOrEmpty(refId)) continue;
                    string childKey = MakeKey(refType, refId);
                    entry.ChildKeys.Add(childKey);
                    if (!results.ContainsKey(childKey))
                    {
                        queue.Enqueue((refType, refId, curDepth + 1, key));
                    }
                }
            }

            // 區塊職責：寫出結果檔
            // 物理意義：將 results dict 序列化成 JSON 或 MD，放到 outputPath（預設 AgentCommands/asset_refs_<ts>.<ext>）
            // 數值影響：在指定路徑建立或覆寫一份檔案
            if (string.IsNullOrEmpty(outputPath))
            {
                string ext = format == "md" ? "md" : "json";
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = $"AgentCommands/asset_refs_{assetType.Name}_{ts}.{ext}";
            }

            string absOut = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(ProjectRoot, outputPath);

            string outDir = Path.GetDirectoryName(absOut);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string content = (format == "md")
                ? RenderMarkdown(results, assetType, seedIds, maxDepth)
                : RenderJson(results, assetType, seedIds, maxDepth);

            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            int total = results.Count;
            int found = results.Values.Count(r => r.Exists);
            int missing = total - found;
            Debug.Log($"[Cmd:ResolveAssetReferences] Resolved {total} asset(s) " +
                      $"({found} found, {missing} missing); maxDepth={maxDepth}; output: {outputPath}");

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        private struct AbsolutePathInfo
        {
            public string AbsPath;
            public string RelPath;   // Project-root-relative
            public bool Exists;
        }

        /// <summary>
        /// 嘗試取得 Asset 的 JSON 檔路徑（project-root-relative，方便 AI agent 直接 Read）。
        /// 若找不到（ID 不存在 / 模組不對）回傳空 RelPath。
        /// </summary>
        private AbsolutePathInfo TryGetAssetPath(Type assetType, string id, string moduleFilter)
        {
            var info = new AbsolutePathInfo();
            try
            {
                var config = UCL_ModuleService.Ins.GetAssetConfig(assetType, id);
                if (config != null && config.Exist)
                {
                    if (!string.IsNullOrEmpty(moduleFilter) && config.p_Module != null
                        && !string.Equals(config.p_Module.ID, moduleFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        // requested module mismatch
                        info.Exists = false;
                        return info;
                    }
                    string abs = config.AssetPath?.Replace('\\', '/') ?? string.Empty;
                    info.AbsPath = abs;
                    info.RelPath = ToProjectRelative(abs);
                    info.Exists = !string.IsNullOrEmpty(abs) && File.Exists(abs);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:ResolveAssetReferences] TryGetAssetPath({assetType.Name},{id}) ex: {e.Message}");
            }
            return info;
        }

        private static string ToProjectRelative(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return string.Empty;
            absPath = absPath.Replace('\\', '/');
            string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (absPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return absPath.Substring(root.Length);
            }
            return absPath;
        }

        /// <summary>
        /// 用反射呼叫 UCL_Asset&lt;T&gt;.GetData(string) 拿到 asset 物件實例。
        /// 我們不知道編譯期 T，但 UCL_ModuleService 也提供 GetAssetData，這裡用該管道更穩定。
        /// </summary>
        private object TryGetAssetData(Type assetType, string id)
        {
            try
            {
                // UCL_ModuleService.Ins 有 GetAssetConfig 但載入 cache 走 UCL_Asset<T>.Util
                // 反射呼叫 typeof(UCL_Asset<>).MakeGenericType(assetType).GetProperty("Util")?.GetValue(null)
                var utilGenericType = typeof(UCL_Util<>).MakeGenericType(assetType);
                var utilProp = utilGenericType.GetProperty("Util", BindingFlags.Public | BindingFlags.Static);
                object util = utilProp?.GetValue(null);
                if (util == null) return null;

                // util.GetAsset(id, true) → returns UCLI_Asset
                var getAsset = util.GetType().GetMethod("GetAsset",
                    new Type[] { typeof(string), typeof(bool) });
                if (getAsset == null) return null;

                return getAsset.Invoke(util, new object[] { id, true });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:ResolveAssetReferences] TryGetAssetData({assetType.Name},{id}) ex: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 反射遞迴：把 obj 內所有 UCLI_AssetEntry 收集起來（含巢狀結構、List、Array）。
        /// 這個遞迴只在「同一份 Asset 內」展開（不跳到別份 Asset），跳 Asset 由 BFS 主迴圈負責。
        /// fieldVisited 用 ReferenceEqualityComparer 防止 cycle / shared 子物件重複展開。
        /// </summary>
        private void CollectAssetEntries(object obj, List<(Type, string)> outList, HashSet<object> fieldVisited)
        {
            if (obj == null) return;
            if (obj is string) return;       // strings are leaves
            Type t = obj.GetType();
            if (t.IsPrimitive || t.IsEnum) return;
            if (!t.IsClass && !t.IsValueType) return;
            if (obj is UnityEngine.Object) return;   // skip Unity Object refs

            // Reference cycle guard (only for class instances; structs are cheap)
            if (t.IsClass)
            {
                if (fieldVisited.Contains(obj)) return;
                fieldVisited.Add(obj);
            }

            // Case 1: Self is a UCLI_AssetEntry → record and stop (don't dive into ID/GroupID strings)
            if (obj is UCLI_AssetEntry entry)
            {
                if (!entry.IsEmpty && entry.AssetType != null)
                {
                    outList.Add((entry.AssetType, entry.ID));
                }
                return;
            }

            // Case 2: Collections (IEnumerable but not string) → iterate items
            if (obj is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    CollectAssetEntries(item, outList, fieldVisited);
                }
                return;
            }

            // Case 3: Plain class / struct — walk fields
            // UCL JSON serializer uses fields (not properties). Walk public + private instance fields.
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                // skip compiler-generated / NonSerialized
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;

                Type ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                object val;
                try { val = f.GetValue(obj); }
                catch { continue; }
                if (val == null) continue;

                CollectAssetEntries(val, outList, fieldVisited);
            }
        }

        /// <summary>
        /// 嘗試用名稱在所有已載入的 Assembly 中找對應的 Type。
        /// 同名字會多回的話，優先取繼承自 UCL_Asset&lt;&gt; 的版本（資料 Asset）。
        /// </summary>
        private Type ResolveTypeByName(string name)
        {
            // First try exact full-name match across all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                Type best = null;
                foreach (var t in types)
                {
                    if (t.Name != name) continue;
                    if (IsUclAssetType(t))
                    {
                        return t;
                    }
                    if (best == null) best = t;
                }
                if (best != null) return best;
            }
            return null;
        }

        /// <summary>檢查 Type 是否繼承自 UCL_Asset&lt;T&gt;。</summary>
        private bool IsUclAssetType(Type t)
        {
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (cur.IsGenericType)
                {
                    var def = cur.GetGenericTypeDefinition();
                    if (def == typeof(UCL_Asset<>)) return true;
                }
            }
            return false;
        }

        // ===== Output renderers =====

        private string RenderJson(Dictionary<string, AssetRef> results, Type rootType, List<string> seeds, int maxDepth)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"command\": \"ResolveAssetReferences\",");
            sb.AppendLine($"  \"timestamp\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"rootType\": \"{Escape(rootType.Name)}\",");
            sb.AppendLine($"  \"seeds\": [{string.Join(", ", seeds.Select(s => $"\"{Escape(s)}\""))}],");
            sb.AppendLine($"  \"maxDepth\": {maxDepth},");
            sb.AppendLine($"  \"totalAssets\": {results.Count},");
            sb.AppendLine($"  \"foundOnDisk\": {results.Values.Count(r => r.Exists)},");
            sb.AppendLine($"  \"assets\": [");

            var ordered = results.Values
                .OrderBy(r => r.Depth)
                .ThenBy(r => r.AssetType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"key\": \"{Escape(MakeKey(r.AssetType, r.Id))}\",");
                sb.AppendLine($"      \"assetType\": \"{Escape(r.AssetType)}\",");
                sb.AppendLine($"      \"id\": \"{Escape(r.Id)}\",");
                sb.AppendLine($"      \"depth\": {r.Depth},");
                sb.AppendLine($"      \"path\": \"{Escape(r.Path)}\",");
                sb.AppendLine($"      \"exists\": {(r.Exists ? "true" : "false")},");
                sb.AppendLine($"      \"parent\": \"{Escape(r.ParentKey)}\",");
                sb.AppendLine($"      \"refs\": [{string.Join(", ", r.ChildKeys.Select(k => $"\"{Escape(k)}\""))}]");
                sb.Append(i < ordered.Count - 1 ? "    },\n" : "    }\n");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string RenderMarkdown(Dictionary<string, AssetRef> results, Type rootType, List<string> seeds, int maxDepth)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Asset References — {rootType.Name}");
            sb.AppendLine();
            sb.AppendLine($"- **Seeds**: {string.Join(", ", seeds)}");
            sb.AppendLine($"- **maxDepth**: {maxDepth}");
            sb.AppendLine($"- **Total Assets**: {results.Count}");
            sb.AppendLine($"- **Found On Disk**: {results.Values.Count(r => r.Exists)}");
            sb.AppendLine($"- **Generated**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## Flat Path List");
            sb.AppendLine();
            sb.AppendLine("| Depth | AssetType | ID | Exists | Path |");
            sb.AppendLine("|---|---|---|:-:|---|");

            var ordered = results.Values
                .OrderBy(r => r.Depth)
                .ThenBy(r => r.AssetType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var r in ordered)
            {
                string mark = r.Exists ? "✅" : "❌";
                sb.AppendLine($"| {r.Depth} | `{r.AssetType}` | `{r.Id}` | {mark} | `{r.Path}` |");
            }

            sb.AppendLine();
            sb.AppendLine("## Reference Tree");
            sb.AppendLine();
            sb.AppendLine("```");
            // Build a parent→children map
            var byParent = ordered.GroupBy(r => r.ParentKey)
                .ToDictionary(g => g.Key, g => g.ToList());
            void Render(string parent, int indent)
            {
                if (!byParent.TryGetValue(parent, out var kids)) return;
                foreach (var kid in kids)
                {
                    sb.Append(new string(' ', indent * 2));
                    sb.Append("- ");
                    sb.Append($"{kid.AssetType}/{kid.Id}");
                    sb.Append(kid.Exists ? "  " : " [MISSING]  ");
                    sb.AppendLine($"({kid.Path})");
                    Render(MakeKey(kid.AssetType, kid.Id), indent + 1);
                }
            }
            Render("<root>", 0);
            sb.AppendLine("```");
            return sb.ToString();
        }

        // ===== Tiny helpers =====

        private static string MakeKey(Type t, string id) => MakeKey(t.Name, id);
        private static string MakeKey(string typeName, string id) => $"{typeName}:{id}";

        private static string GetArg(Dictionary<string, string> args, string key, string fallback)
        {
            if (args == null) return fallback;
            return args.TryGetValue(key, out var v) ? v : fallback;
        }

        private static int TryParseInt(string s, int fallback)
        {
            return int.TryParse(s, out var v) ? v : fallback;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // .NET Standard 2.0 在 Unity 沒有 ReferenceEqualityComparer，自前實作
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif
