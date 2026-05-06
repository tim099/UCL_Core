
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/API/UCL_AgentCommand/Cmd_FindAssetUsages.md
// 日本語: Docs~/ja/API/UCL_AgentCommand/Cmd_FindAssetUsages.md
// 简体中文: Docs~/zh-Hans/API/UCL_AgentCommand/Cmd_FindAssetUsages.md
// 繁體中文: Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_FindAssetUsages.md
//
// 區塊職責：本檔提供「反向查詢 UCL_Asset 被引用位置」的 Agent Command 實作。
// 物理意義：給定目標 Asset（例：RCG_CustomStatusData/Stun），掃描所有（或指定）
//          UCL_Asset 子型別的全部實例，透過反射深入每份 asset 的欄位樹，找出
//          指向目標的 UCLI_AssetEntry — 同時記錄發生位置（dotted field path），
//          最後輸出 (UsedBy_AssetType, UsedBy_ID, JSON 路徑, 欄位路徑) 清單給 AI agent。
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
    /// Agent Command：反向查詢被指定 Asset 引用的所有位置。
    ///
    /// 與 <see cref="Cmd_ResolveAssetReferences"/>（順向：起點 → 它引用了誰）相反，
    /// 本 Cmd 給定目標 Asset（例如 RCG_CustomStatusData/Stun），掃描 searchTypes 所列
    /// 的全部 UCL_Asset 子型別實例，透過反射深入欄位樹，找出指向目標的 UCLI_AssetEntry，
    /// 並記錄該引用發生於哪個 Asset 的哪個欄位路徑（如
    /// <c>m_Effects[2].m_Setting.m_StatusEntry</c>）。
    ///
    /// 典型用途：
    /// - 「<c>RCG_CustomStatusData/Stun</c> 被誰用？」 → 列出所有 RCG_CardData /
    ///   RCG_ItemData / RCG_EquipmentData / 怪物技能 等引用點
    /// - 重構安全網：刪 / 重命名 Asset 前先看影響範圍
    /// - 平衡分析：某 Status 被多少卡片同時上 buff / 反向 debuff？
    ///
    /// 參數：
    /// - <c>targetType</c>（必填）：被查詢 Asset 的 C# Type 名稱，例 <c>RCG_CustomStatusData</c>
    /// - <c>targetIds</c>（必填）：被查詢 Asset ID（CSV 多筆），例 <c>Stun,Burn</c>
    /// - <c>searchTypes</c>（選填）：限定掃描的 Asset Type CSV；預設掃所有 UCL_Asset 子類
    /// - <c>outputPath</c>（選填）：結果檔案路徑（預設 <c>AgentCommands/asset_usages_&lt;timestamp&gt;.json</c>）
    /// - <c>format</c>（選填，<c>json</c> / <c>md</c>，預設 <c>json</c>）：輸出格式
    /// - <c>module</c>（選填）：限定使用方所屬模組（預設掃所有模組）
    /// - <c>maxFieldDepth</c>（選填，預設 16）：反射欄位遞迴深度上限（防 cycle）
    /// </summary>
    public class Cmd_FindAssetUsages : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FindAssetUsages";

        public override string ShortDescription =>
            "Reverse lookup — list every asset that references the given target asset(s), with field path.";

        public override string ArgsSchema =>
            "targetType=Target asset C# type name (e.g. RCG_CustomStatusData)\n" +
            "targetIds=CSV asset IDs to look up (e.g. Stun,Burn)\n" +
            "searchTypes=CSV asset types to scan (default: all UCL_Asset subclasses)\n" +
            "outputPath=Output file path relative to project root (default AgentCommands/asset_usages_<ts>.json)\n" +
            "format=Output format: json (default) or md\n" +
            "module=Optional module name to restrict the using-side (default: all modules)\n" +
            "maxFieldDepth=Reflection recursion cap to guard cycles (default 16)";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_FindAssetUsages.md";

        // ===== Internal records =====

        /// <summary>單一引用點紀錄（誰、在哪份 asset 的哪個欄位路徑、引用了哪個目標）。</summary>
        private class UsageHit
        {
            public string TargetKey;        // "<TargetType>:<TargetID>"
            public string UsedByType;       // C# Type name of the referencing asset
            public string UsedById;          // ID of the referencing asset
            public string UsedByPath;        // project-root-relative JSON path of the referencing asset
            public bool   UsedByExists;      // 該檔案是否真的存在
            public string FieldPath;         // dotted field path inside the referencing asset
        }

        // 區塊職責：取得 Unity 專案根目錄（CardGame/）
        // 物理意義：Application.dataPath 為 CardGame/Assets，往上 1 層即 Unity project root。
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
            // 區塊職責：頂層 try/catch — 把 NRE 之類的 bare message 補上完整 stack trace
            // 物理意義：runner 只把 e.Message 寫到 LastRunError，stack trace 容易遺失；
            //          這裡先 Debug.LogException 印到 Console，再用 e.ToString() 重拋，
            //          讓 LastRunError 帶上型別 + 訊息 + stack 給 agent 自我修。
            // 數值影響：無；只是診斷品質提升。
            try
            {
                await ExecuteAsyncCore(args, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                throw new Exception($"[Cmd:FindAssetUsages] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            }
        }

        private async UniTask ExecuteAsyncCore(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析參數
            // 物理意義：將 args 轉成具體的 targetType Type 物件、targetId 集合、searchTypes 過濾清單、輸出格式
            // 數值影響：無副作用；僅校驗輸入
            string targetTypeName = GetArg(args, "targetType", null);
            string targetIdsCsv = GetArg(args, "targetIds", null);
            string searchTypesCsv = GetArg(args, "searchTypes", null);
            int maxFieldDepth = TryParseInt(GetArg(args, "maxFieldDepth", "16"), 16);
            string outputPath = GetArg(args, "outputPath", null);
            string format = GetArg(args, "format", "json").ToLowerInvariant();
            string moduleFilter = GetArg(args, "module", null);

            if (string.IsNullOrWhiteSpace(targetTypeName) || string.IsNullOrWhiteSpace(targetIdsCsv))
            {
                Debug.LogError("[Cmd:FindAssetUsages] Missing required args: targetType / targetIds");
                return;
            }

            Type targetType = ResolveTypeByName(targetTypeName);
            if (targetType == null)
            {
                Debug.LogError($"[Cmd:FindAssetUsages] Cannot resolve C# Type from name: '{targetTypeName}'");
                return;
            }

            // 目標集合（OrdinalIgnoreCase 比對 ID，因為 UCL_Asset ID 比對通常大小寫不敏感）
            var targetIds = new HashSet<string>(
                targetIdsCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
            if (targetIds.Count == 0)
            {
                Debug.LogError("[Cmd:FindAssetUsages] targetIds is empty after parsing.");
                return;
            }

            // 區塊職責：決定掃描範圍（searchTypes）
            // 物理意義：若使用者指定 → 只掃這些 type；否則枚舉全部 UCL_Asset<> 子類
            // 數值影響：影響掃描成本（O(types × assetsPerType × fieldsPerAsset)）
            List<Type> searchTypes;
            if (!string.IsNullOrWhiteSpace(searchTypesCsv))
            {
                searchTypes = new List<Type>();
                foreach (var name in searchTypesCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                {
                    var t = ResolveTypeByName(name);
                    if (t == null)
                    {
                        Debug.LogWarning($"[Cmd:FindAssetUsages] Unknown searchType '{name}' — skipped.");
                        continue;
                    }
                    if (!IsUclAssetType(t))
                    {
                        Debug.LogWarning($"[Cmd:FindAssetUsages] Type '{name}' is not a UCL_Asset subclass — skipped.");
                        continue;
                    }
                    searchTypes.Add(t);
                }
            }
            else
            {
                searchTypes = EnumerateAllUclAssetTypes();
            }

            // 區塊職責：對每個 searchType 列出所有 ID，逐份載入並反射搜尋
            // 物理意義：對每份 (searchType, id) asset 物件，遞迴走訪欄位樹；遇到 UCLI_AssetEntry
            //          就比對 (entry.AssetType, entry.ID) 是否命中 (targetType, targetIds)；
            //          命中即附帶 dotted field path 寫入 usages。
            // 數值影響：填充 usages 清單；不修改 asset 內容。
            var usages = new List<UsageHit>();
            int scannedTypes = 0;
            int scannedAssets = 0;

            foreach (var searchType in searchTypes)
            {
                if (token.IsCancellationRequested) return;

                List<string> ids = TryGetAllIDs(searchType);
                if (ids == null) continue;
                scannedTypes++;

                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) return;
                    if (string.IsNullOrEmpty(id)) continue;

                    object asset = TryGetAssetData(searchType, id);
                    scannedAssets++;
                    if (asset == null) continue;

                    // 模組過濾（檢查 asset 自身的 module，不是 target 的 module）
                    if (!string.IsNullOrEmpty(moduleFilter)
                        && !MatchesModule(searchType, id, moduleFilter))
                    {
                        continue;
                    }

                    var pathStack = new List<string>();
                    var fieldVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);

                    // 區塊職責：對單一 asset 安全反射 — 任何反射例外都吞掉並 LogWarning，
                    // 確保單一壞蛋（malformed asset / 自訂 getter NRE）不會中斷整批掃描。
                    // 物理意義：scannedAssets 仍計入，只是 hits 不會新增。
                    // 數值影響：可能少抓到該份 asset 內的引用點；其餘正常產出。
                    try
                    {
                        SearchForTargets(
                            obj: asset,
                            rootName: "$",
                            pathStack: pathStack,
                            fieldVisited: fieldVisited,
                            targetType: targetType,
                            targetIds: targetIds,
                            depthRemaining: maxFieldDepth,
                            onHit: (entry, fieldPath) =>
                            {
                                // 拿引用方 (using-side) 的 JSON 路徑
                                var info = TryGetAssetPath(searchType, id, null);
                                usages.Add(new UsageHit
                                {
                                    TargetKey = MakeKey(targetType, entry.ID),
                                    UsedByType = searchType.Name,
                                    UsedById = id,
                                    UsedByPath = info.RelPath ?? string.Empty,
                                    UsedByExists = info.Exists,
                                    FieldPath = fieldPath,
                                });
                            });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Cmd:FindAssetUsages] Skipped {searchType.Name}/{id} due to reflection error: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // 區塊職責：寫出結果檔
            // 物理意義：將 usages 序列化成 JSON 或 MD（依 target 分組顯示），落到 outputPath
            // 數值影響：在指定路徑建立或覆寫一份檔案
            if (string.IsNullOrEmpty(outputPath))
            {
                string ext = format == "md" ? "md" : "json";
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = $"AgentCommands/asset_usages_{targetType.Name}_{ts}.{ext}";
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
                ? RenderMarkdown(usages, targetType, targetIds, scannedTypes, scannedAssets)
                : RenderJson(usages, targetType, targetIds, scannedTypes, scannedAssets);

            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            Debug.Log($"[Cmd:FindAssetUsages] Target={targetType.Name}({string.Join(",", targetIds)}); " +
                      $"scanned {scannedAssets} asset(s) across {scannedTypes} type(s); " +
                      $"found {usages.Count} usage hit(s); output: {outputPath}");

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // Reflection search core
        // ===========================================================

        /// <summary>
        /// 反射遞迴：在 obj 內搜尋指向 (targetType, targetIds) 的 UCLI_AssetEntry。
        /// 命中時透過 onHit 回呼回報 (entry, dotted-field-path)。
        ///
        /// 與 Cmd_ResolveAssetReferences.CollectAssetEntries 不同：
        /// - 命中後**不**跨 asset 跳轉（我們只在當前 using-side asset 內找引用點）
        /// - 額外維護 pathStack 以建構 dotted field path
        /// </summary>
        private void SearchForTargets(
            object obj,
            string rootName,
            List<string> pathStack,
            HashSet<object> fieldVisited,
            Type targetType,
            HashSet<string> targetIds,
            int depthRemaining,
            Action<UCLI_AssetEntry, string> onHit)
        {
            if (obj == null) return;
            if (depthRemaining <= 0) return;
            if (obj is string) return;
            Type t = obj.GetType();
            if (t.IsPrimitive || t.IsEnum) return;
            if (!t.IsClass && !t.IsValueType) return;
            if (obj is UnityEngine.Object) return;

            // Reference cycle guard（only for class instances; structs cheap）
            if (t.IsClass)
            {
                if (fieldVisited.Contains(obj)) return;
                fieldVisited.Add(obj);
            }

            // Case 1: AssetEntry → 比對 target；命中即回報，不再下鑽
            // 區塊職責：保護 entry 屬性 getter（可能在 partial-init 狀態下 NRE）
            if (obj is UCLI_AssetEntry entry)
            {
                try
                {
                    if (!entry.IsEmpty
                        && entry.AssetType != null
                        && entry.AssetType == targetType
                        && !string.IsNullOrEmpty(entry.ID)
                        && targetIds.Contains(entry.ID))
                    {
                        onHit(entry, JoinPath(rootName, pathStack));
                    }
                }
                catch
                {
                    // 個別 entry 屬性壞掉就略過，不影響其他欄位掃描
                }
                return;
            }

            // Case 2: Collection → 用索引擴增 path，逐項展開
            // 區塊職責：保護迭代器 — 自訂 IEnumerable 可能在 MoveNext 時 NRE
            if (obj is IEnumerable enumerable)
            {
                IEnumerator it = null;
                try { it = enumerable.GetEnumerator(); }
                catch { return; }
                if (it == null) return;

                int idx = 0;
                while (true)
                {
                    bool moved;
                    object item;
                    try
                    {
                        moved = it.MoveNext();
                        if (!moved) break;
                        item = it.Current;
                    }
                    catch
                    {
                        // 迭代失敗 → 結束此集合的展開
                        break;
                    }

                    pathStack.Add($"[{idx}]");
                    SearchForTargets(item, rootName, pathStack, fieldVisited, targetType, targetIds, depthRemaining - 1, onHit);
                    pathStack.RemoveAt(pathStack.Count - 1);
                    idx++;
                }
                return;
            }

            // Case 3: Plain class / struct — 走 instance fields（含 private，與 UCL JSON serializer 一致）
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;

                Type ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                object val;
                try { val = f.GetValue(obj); }
                catch { continue; }
                if (val == null) continue;

                pathStack.Add("." + f.Name);
                SearchForTargets(val, rootName, pathStack, fieldVisited, targetType, targetIds, depthRemaining - 1, onHit);
                pathStack.RemoveAt(pathStack.Count - 1);
            }
        }

        // path 組裝：rootName + 各 segment（field 已含 "." 前綴；index 是 "[i]"）
        private static string JoinPath(string root, List<string> stack)
        {
            if (stack == null || stack.Count == 0) return root;
            var sb = new StringBuilder(root);
            foreach (var seg in stack) sb.Append(seg);
            return sb.ToString();
        }

        // ===========================================================
        // Asset enumeration / loading helpers
        // ===========================================================

        /// <summary>枚舉所有目前 AppDomain 內的 UCL_Asset&lt;T&gt; 具現子型別。</summary>
        private List<Type> EnumerateAllUclAssetTypes()
        {
            var result = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                    if (!IsUclAssetType(t)) continue;
                    result.Add(t);
                }
            }
            return result;
        }

        /// <summary>反射呼叫 <c>UCL_Util&lt;T&gt;.Util.GetAllIDs()</c> 取得 ID 清單。</summary>
        private List<string> TryGetAllIDs(Type assetType)
        {
            try
            {
                var utilGenericType = typeof(UCL_Util<>).MakeGenericType(assetType);
                var utilProp = utilGenericType.GetProperty("Util", BindingFlags.Public | BindingFlags.Static);
                object util = utilProp?.GetValue(null);
                if (util == null) return null;

                var getAllIds = util.GetType().GetMethod("GetAllIDs", new Type[] { typeof(bool) });
                if (getAllIds == null) return null;

                var raw = getAllIds.Invoke(util, new object[] { false }) as IEnumerable;
                if (raw == null) return null;

                var list = new List<string>();
                foreach (var o in raw)
                {
                    if (o is string s) list.Add(s);
                }
                return list;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:FindAssetUsages] TryGetAllIDs({assetType.Name}) ex: {e.Message}");
                return null;
            }
        }

        /// <summary>反射呼叫 <c>UCL_Util&lt;T&gt;.Util.GetAsset(id, true)</c> 載入 asset 物件。</summary>
        private object TryGetAssetData(Type assetType, string id)
        {
            try
            {
                var utilGenericType = typeof(UCL_Util<>).MakeGenericType(assetType);
                var utilProp = utilGenericType.GetProperty("Util", BindingFlags.Public | BindingFlags.Static);
                object util = utilProp?.GetValue(null);
                if (util == null) return null;

                var getAsset = util.GetType().GetMethod("GetAsset",
                    new Type[] { typeof(string), typeof(bool) });
                if (getAsset == null) return null;

                return getAsset.Invoke(util, new object[] { id, true });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:FindAssetUsages] TryGetAssetData({assetType.Name},{id}) ex: {e.Message}");
                return null;
            }
        }

        private struct AbsolutePathInfo
        {
            public string AbsPath;
            public string RelPath;   // Project-root-relative
            public bool Exists;
        }

        /// <summary>取得 asset JSON 路徑（project-root-relative）。失敗回傳空 RelPath。</summary>
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
                Debug.LogWarning($"[Cmd:FindAssetUsages] TryGetAssetPath({assetType.Name},{id}) ex: {e.Message}");
            }
            return info;
        }

        private bool MatchesModule(Type assetType, string id, string moduleId)
        {
            try
            {
                var config = UCL_ModuleService.Ins.GetAssetConfig(assetType, id);
                if (config?.p_Module == null) return false;
                return string.Equals(config.p_Module.ID, moduleId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
        /// 用名稱在所有已載入的 Assembly 中找對應的 Type；同名衝突優先取 UCL_Asset&lt;&gt; 子類。
        /// </summary>
        private Type ResolveTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                Type best = null;
                foreach (var t in types)
                {
                    if (t.Name != name) continue;
                    if (IsUclAssetType(t)) return t;
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

        // ===========================================================
        // Output renderers
        // ===========================================================

        private string RenderJson(List<UsageHit> usages, Type targetType, HashSet<string> targetIds,
                                  int scannedTypes, int scannedAssets)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"command\": \"FindAssetUsages\",");
            sb.AppendLine($"  \"timestamp\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"targetType\": \"{Escape(targetType.Name)}\",");
            sb.AppendLine($"  \"targetIds\": [{string.Join(", ", targetIds.Select(s => $"\"{Escape(s)}\""))}],");
            sb.AppendLine($"  \"scannedTypes\": {scannedTypes},");
            sb.AppendLine($"  \"scannedAssets\": {scannedAssets},");
            sb.AppendLine($"  \"totalHits\": {usages.Count},");

            // 依 targetId 分組（每個被查詢 ID 一個 group）
            sb.AppendLine($"  \"usagesByTarget\": [");
            var grouped = usages
                .GroupBy(u => u.TargetKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int gi = 0; gi < grouped.Count; gi++)
            {
                var g = grouped[gi];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"targetKey\": \"{Escape(g.Key)}\",");
                sb.AppendLine($"      \"hitCount\": {g.Count()},");
                sb.AppendLine($"      \"hits\": [");

                var ordered = g.OrderBy(u => u.UsedByType, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(u => u.UsedById, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(u => u.FieldPath, StringComparer.Ordinal)
                               .ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var u = ordered[i];
                    sb.AppendLine("        {");
                    sb.AppendLine($"          \"usedByType\": \"{Escape(u.UsedByType)}\",");
                    sb.AppendLine($"          \"usedById\": \"{Escape(u.UsedById)}\",");
                    sb.AppendLine($"          \"path\": \"{Escape(u.UsedByPath)}\",");
                    sb.AppendLine($"          \"exists\": {(u.UsedByExists ? "true" : "false")},");
                    sb.AppendLine($"          \"fieldPath\": \"{Escape(u.FieldPath)}\"");
                    sb.Append(i < ordered.Count - 1 ? "        },\n" : "        }\n");
                }
                sb.AppendLine("      ]");
                sb.Append(gi < grouped.Count - 1 ? "    },\n" : "    }\n");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string RenderMarkdown(List<UsageHit> usages, Type targetType, HashSet<string> targetIds,
                                      int scannedTypes, int scannedAssets)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Asset Usages — {targetType.Name}");
            sb.AppendLine();
            sb.AppendLine($"- **Targets**: {string.Join(", ", targetIds)}");
            sb.AppendLine($"- **Scanned**: {scannedAssets} asset(s) across {scannedTypes} type(s)");
            sb.AppendLine($"- **Total Hits**: {usages.Count}");
            sb.AppendLine($"- **Generated**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var grouped = usages
                .GroupBy(u => u.TargetKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 補上 0 命中的 target（讓使用者知道沒被引用，而非命令出錯）
            var hitTargets = new HashSet<string>(grouped.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var tid in targetIds)
            {
                string key = MakeKey(targetType.Name, tid);
                if (!hitTargets.Contains(key))
                {
                    sb.AppendLine($"## `{key}`");
                    sb.AppendLine();
                    sb.AppendLine("> No usages found.");
                    sb.AppendLine();
                }
            }

            foreach (var g in grouped)
            {
                sb.AppendLine($"## `{g.Key}` ({g.Count()} hit(s))");
                sb.AppendLine();
                sb.AppendLine("| UsedBy Type | UsedBy ID | Field Path | JSON Path |");
                sb.AppendLine("|---|---|---|---|");

                var ordered = g.OrderBy(u => u.UsedByType, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(u => u.UsedById, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(u => u.FieldPath, StringComparer.Ordinal)
                               .ToList();
                foreach (var u in ordered)
                {
                    string mark = u.UsedByExists ? "" : " ❌";
                    sb.AppendLine($"| `{u.UsedByType}` | `{u.UsedById}` | `{u.FieldPath}` | `{u.UsedByPath}`{mark} |");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ===== Tiny helpers =====

        private static string MakeKey(Type t, string id) => MakeKey(t.Name, id);
        private static string MakeKey(string typeName, string id) => $"{typeName}:{id}";

        // GetArg 已上移至 UCL_AgentCommandHandlerBase（從原本寬鬆版改用嚴格版：
        //   原：key 存在就回 value（包括空字串）
        //   新：key 存在且 value 非空字串才回 value，否則回 fallback
        // 行為差異僅在「使用者填了 key 但 value 為空字串」這個 corner case，新行為更安全）

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
