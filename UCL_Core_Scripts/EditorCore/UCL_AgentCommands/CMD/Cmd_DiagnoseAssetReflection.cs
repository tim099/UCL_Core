
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
// 區塊職責：本檔提供「逐步測試 UCL_Asset 反射存取」的診斷 Cmd。
// 物理意義：列舉所有 UCL_Asset<> 子類，對每個 (type, id) 依序執行：
//          1) GetAllIDs() — 看能否拿 ID 清單
//          2) GetAsset(id, true) — 看能否載入物件
//          3) 淺反射欄位（不下鑽）— 看能否讀第一層 fields
//          每一步都個別 try/catch 並記錄例外型別 / 訊息，最後輸出 per-step 報告
//          給 agent 一眼看出 NRE 來自哪個 (type, id, step)。
// 數值影響：純讀取，不修改任何 Asset；只在指定 outputPath 寫入一份 md 檔。
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
    /// Agent Command：診斷 UCL_Asset 反射管線 — 找 NRE 元兇。
    ///
    /// 配對 <see cref="Cmd_FindAssetUsages"/> / <see cref="Cmd_ResolveAssetReferences"/>
    /// 在「掃所有 UCL_Asset 子類」時偶爾踩到的 NullReferenceException：runner 預設只記
    /// e.Message（無 stack trace），本 Cmd 拆成三步個別 try/catch，把哪一步、哪個型別、
    /// 哪個 id 出包都印出來給 agent 看。
    ///
    /// 三步驟：
    /// - Step 1: <c>UCL_Util&lt;T&gt;.Util.GetAllIDs(false)</c>
    /// - Step 2: <c>UCL_Util&lt;T&gt;.Util.GetAsset(id, true)</c>
    /// - Step 3: 對載入物件淺反射（只讀第一層 instance fields，不下鑽）
    ///
    /// 參數：
    /// - <c>searchTypes</c>（選填）：限定型別 CSV；預設掃所有 UCL_Asset 子類
    /// - <c>maxIdsPerType</c>（選填，預設 0=全跑）：每個型別最多測幾個 ID（除錯時可設小）
    /// - <c>outputPath</c>（選填）：輸出 md 路徑
    /// </summary>
    public class Cmd_DiagnoseAssetReflection : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "DiagnoseAssetReflection";

        public override string ShortDescription =>
            "Probe every UCL_Asset type/id through GetAllIDs → GetAsset → shallow reflection; report per-step failures.";

        public override string ArgsSchema =>
            "searchTypes=CSV asset types to probe (default: all UCL_Asset subclasses)\n" +
            "maxIdsPerType=Cap on IDs probed per type (default 0 = no cap)\n" +
            "deep=true to also perform full recursive reflection (mirrors FindAssetUsages); default false (shallow only)\n" +
            "maxFieldDepth=Recursion depth cap for deep mode (default 16)\n" +
            "outputPath=Output md path relative to project root (default AgentCommands/asset_reflect_diagnose_<ts>.md)";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md";

        private class StepFailure
        {
            public string TypeName;
            public string Id;            // null when failure is at GetAllIDs step
            public string Step;          // "GetAllIDs" / "GetAsset" / "ShallowReflect" / "DeepReflect" / "EnumerateTypes"
            public string FieldPath;     // 在 DeepReflect 階段，記錄踩雷的欄位路徑（dotted）
            public string ExType;
            public string ExMessage;
            public string StackHead;     // first 6 stack frames
        }

        private class TypeStat
        {
            public string TypeName;
            public int IdCount;          // -1 if GetAllIDs failed
            public int Loaded;
            public int LoadFailed;
            public int Reflected;
            public int ReflectFailed;
        }

        private static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/'); }
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            try
            {
                await ExecuteAsyncCore(args, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                throw new Exception($"[Cmd:DiagnoseAssetReflection] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            }
        }

        private async UniTask ExecuteAsyncCore(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析參數
            // 物理意義：決定要掃哪些型別、每型別最多幾個 id、結果落哪
            // 數值影響：影響掃描成本與輸出體積
            string searchTypesCsv = GetArg(args, "searchTypes", null);
            int maxIdsPerType = TryParseInt(GetArg(args, "maxIdsPerType", "0"), 0);
            bool deep = string.Equals(GetArg(args, "deep", "false"), "true", StringComparison.OrdinalIgnoreCase);
            int maxFieldDepth = TryParseInt(GetArg(args, "maxFieldDepth", "16"), 16);
            string outputPath = GetArg(args, "outputPath", null);

            // 區塊職責：收集要掃的型別清單
            // 物理意義：使用者指定 → 那些；否則枚舉全部 UCL_Asset<> 子類
            // 數值影響：scannedTypes 數
            var failures = new List<StepFailure>();
            var stats = new List<TypeStat>();

            List<Type> searchTypes;
            try
            {
                if (!string.IsNullOrWhiteSpace(searchTypesCsv))
                {
                    searchTypes = new List<Type>();
                    foreach (var name in searchTypesCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                    {
                        var t = ResolveTypeByName(name);
                        if (t != null && IsUclAssetType(t)) searchTypes.Add(t);
                    }
                }
                else
                {
                    searchTypes = EnumerateAllUclAssetTypes();
                }
            }
            catch (Exception ex)
            {
                failures.Add(new StepFailure
                {
                    Step = "EnumerateTypes",
                    TypeName = "(global)",
                    ExType = ex.GetType().Name,
                    ExMessage = ex.Message,
                    StackHead = HeadStack(ex),
                });
                searchTypes = new List<Type>();
            }

            // 區塊職責：對每個型別跑三步診斷
            // 物理意義：先試 GetAllIDs；失敗則型別本身或 Util 構造有問題（記下後跳過）。
            //          成功則對每 id 試 GetAsset → 試淺反射；任何步驟失敗都個別記錄。
            // 數值影響：填充 failures + stats
            foreach (var t in searchTypes)
            {
                if (token.IsCancellationRequested) return;

                var stat = new TypeStat { TypeName = t.Name, IdCount = -1 };
                stats.Add(stat);

                List<string> ids = null;
                try
                {
                    ids = TryGetAllIDsRaw(t);
                    stat.IdCount = ids?.Count ?? 0;
                }
                catch (Exception ex)
                {
                    failures.Add(new StepFailure
                    {
                        Step = "GetAllIDs",
                        TypeName = t.Name,
                        ExType = ex.GetType().Name,
                        ExMessage = ex.Message,
                        StackHead = HeadStack(ex),
                    });
                    continue;
                }
                if (ids == null) continue;

                // 取樣
                IEnumerable<string> idsToTry = ids;
                if (maxIdsPerType > 0) idsToTry = ids.Take(maxIdsPerType);

                foreach (var id in idsToTry)
                {
                    if (token.IsCancellationRequested) return;
                    if (string.IsNullOrEmpty(id)) continue;

                    object asset;
                    try
                    {
                        asset = TryGetAssetDataRaw(t, id);
                        if (asset != null) stat.Loaded++;
                    }
                    catch (Exception ex)
                    {
                        stat.LoadFailed++;
                        // 區塊職責：Method.Invoke 會把目標方法的例外包成 TargetInvocationException
                        // 物理意義：unwrap inner 才看得到真正的 NRE / 訊息
                        // 數值影響：失敗紀錄帶上原始例外型別，方便定位
                        var inner = Unwrap(ex);
                        failures.Add(new StepFailure
                        {
                            Step = "GetAsset",
                            TypeName = t.Name,
                            Id = id,
                            ExType = inner.GetType().Name + (ReferenceEquals(inner, ex) ? "" : $" (inner of {ex.GetType().Name})"),
                            ExMessage = inner.Message,
                            StackHead = HeadStack(inner),
                        });
                        continue;
                    }
                    if (asset == null) continue;

                    try
                    {
                        ShallowReflect(asset);
                        stat.Reflected++;
                    }
                    catch (Exception ex)
                    {
                        stat.ReflectFailed++;
                        failures.Add(new StepFailure
                        {
                            Step = "ShallowReflect",
                            TypeName = t.Name,
                            Id = id,
                            ExType = ex.GetType().Name,
                            ExMessage = ex.Message,
                            StackHead = HeadStack(ex),
                        });
                    }

                    // 區塊職責：deep 模式 — 模擬 FindAssetUsages 的完整遞迴反射
                    // 物理意義：在每一層欄位 / 集合 iteration 個別 try/catch；
                    //          命中例外時把 dotted field path 記下來，定位 NRE 元兇。
                    // 數值影響：填充 stat.DeepFailed + failures(Step=DeepReflect, FieldPath=...)
                    if (deep)
                    {
                        try
                        {
                            var stack = new List<string>();
                            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                            DeepReflectProbe(asset, "$", stack, visited, maxFieldDepth, t.Name, id, failures);
                        }
                        catch (Exception ex)
                        {
                            // 應該被內層全部吃掉；此 catch 只是最後保險
                            failures.Add(new StepFailure
                            {
                                Step = "DeepReflect",
                                TypeName = t.Name,
                                Id = id,
                                FieldPath = "(outer)",
                                ExType = ex.GetType().Name,
                                ExMessage = ex.Message,
                                StackHead = HeadStack(ex),
                            });
                        }
                    }
                }
            }

            // 區塊職責：寫出報告
            if (string.IsNullOrEmpty(outputPath))
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = $"AgentCommands/asset_reflect_diagnose_{ts}.md";
            }
            string absOut = Path.IsPathRooted(outputPath)
                ? outputPath : Path.Combine(ProjectRoot, outputPath);
            string outDir = Path.GetDirectoryName(absOut);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            string content = RenderMarkdown(stats, failures);
            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            Debug.Log($"[Cmd:DiagnoseAssetReflection] types={stats.Count} failures={failures.Count} → {outputPath}");
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // Probes（不吞例外，由呼叫端 try/catch；與 FindAssetUsages 的 Try* 版本邏輯相同）
        // ===========================================================

        private List<string> TryGetAllIDsRaw(Type assetType)
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
            foreach (var o in raw) if (o is string s) list.Add(s);
            return list;
        }

        private object TryGetAssetDataRaw(Type assetType, string id)
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

        // 淺反射：只讀第一層 instance fields，不下鑽 — 用來偵測「能不能讀屬性 / getter 是否 NRE」
        private void ShallowReflect(object obj)
        {
            if (obj == null) return;
            var t = obj.GetType();
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                _ = f.GetValue(obj); // 觸發 getter（若有），不使用回傳值
            }
        }

        /// <summary>
        /// 深反射探針：模擬 FindAssetUsages 的遞迴邏輯，但每一步個別 try/catch，
        /// 一抓到例外就把 (type, id, dotted-field-path, ex) 寫入 failures，**繼續**遞迴。
        /// 這樣同一個 asset 內多個壞點都能被捕捉到。
        /// </summary>
        private void DeepReflectProbe(
            object obj, string root, List<string> stack, HashSet<object> visited,
            int depthRemaining, string typeName, string id, List<StepFailure> failures)
        {
            if (obj == null) return;
            if (depthRemaining <= 0) return;
            if (obj is string) return;
            Type t;
            try { t = obj.GetType(); }
            catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack), "GetType", ex); return; }

            if (t.IsPrimitive || t.IsEnum) return;
            if (!t.IsClass && !t.IsValueType) return;
            if (obj is UnityEngine.Object) return;

            if (t.IsClass)
            {
                if (visited.Contains(obj)) return;
                visited.Add(obj);
            }

            // AssetEntry — 探查屬性 getter
            if (obj is UCLI_AssetEntry entry)
            {
                try
                {
                    var _ = entry.IsEmpty;
                    var __ = entry.AssetType;
                    var ___ = entry.ID;
                }
                catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack), "Entry.GetterNRE", ex); }
                return;
            }

            // Collection
            if (obj is IEnumerable enumerable)
            {
                IEnumerator it = null;
                try { it = enumerable.GetEnumerator(); }
                catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack), "Iter.GetEnumerator", ex); return; }
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
                    catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack) + $"[{idx}]", "Iter.MoveNext/Current", ex); break; }

                    stack.Add($"[{idx}]");
                    DeepReflectProbe(item, root, stack, visited, depthRemaining - 1, typeName, id, failures);
                    stack.RemoveAt(stack.Count - 1);
                    idx++;
                }
                return;
            }

            // Class / struct — fields
            FieldInfo[] fields;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
            catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack), "GetFields", ex); return; }

            foreach (var f in fields)
            {
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                Type ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                object val;
                try { val = f.GetValue(obj); }
                catch (Exception ex) { Record(failures, typeName, id, JoinPath(root, stack) + "." + f.Name, "GetValue", ex); continue; }
                if (val == null) continue;

                stack.Add("." + f.Name);
                DeepReflectProbe(val, root, stack, visited, depthRemaining - 1, typeName, id, failures);
                stack.RemoveAt(stack.Count - 1);
            }
        }

        private static void Record(List<StepFailure> failures, string typeName, string id,
                                   string fieldPath, string subStep, Exception ex)
        {
            failures.Add(new StepFailure
            {
                Step = "DeepReflect:" + subStep,
                TypeName = typeName,
                Id = id,
                FieldPath = fieldPath,
                ExType = ex.GetType().Name,
                ExMessage = ex.Message,
                StackHead = HeadStack(ex),
            });
        }

        private static string JoinPath(string root, List<string> stack)
        {
            if (stack == null || stack.Count == 0) return root;
            var sb = new StringBuilder(root);
            foreach (var seg in stack) sb.Append(seg);
            return sb.ToString();
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // ===========================================================
        // Type enumeration
        // ===========================================================

        private List<Type> EnumerateAllUclAssetTypes()
        {
            var result = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types?.Where(x => x != null).ToArray() ?? Array.Empty<Type>();
                }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                    if (!IsUclAssetType(t)) continue;
                    result.Add(t);
                }
            }
            return result;
        }

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
                    if (t == null || t.Name != name) continue;
                    if (IsUclAssetType(t)) return t;
                    if (best == null) best = t;
                }
                if (best != null) return best;
            }
            return null;
        }

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
        // Output
        // ===========================================================

        private string RenderMarkdown(List<TypeStat> stats, List<StepFailure> failures)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UCL_Asset Reflection Diagnose");
            sb.AppendLine();
            sb.AppendLine($"- **Generated**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **Types probed**: {stats.Count}");
            sb.AppendLine($"- **Total failures**: {failures.Count}");
            sb.AppendLine();

            // Failures by step
            sb.AppendLine("## Failures by Step");
            sb.AppendLine();
            var byStep = failures.GroupBy(f => f.Step).OrderBy(g => g.Key);
            if (!byStep.Any())
            {
                sb.AppendLine("> No failures detected. ✅");
            }
            else
            {
                foreach (var g in byStep)
                {
                    sb.AppendLine($"### `{g.Key}` ({g.Count()} failure(s))");
                    sb.AppendLine();
                    sb.AppendLine("| Type | ID | FieldPath | ExType | Message |");
                    sb.AppendLine("|---|---|---|---|---|");
                    foreach (var f in g.OrderBy(x => x.TypeName).ThenBy(x => x.Id ?? "").ThenBy(x => x.FieldPath ?? ""))
                    {
                        string msg = (f.ExMessage ?? "").Replace("|", "\\|").Replace("\n", " ");
                        string fp = (f.FieldPath ?? "—").Replace("|", "\\|");
                        sb.AppendLine($"| `{f.TypeName}` | `{f.Id ?? "—"}` | `{fp}` | `{f.ExType}` | {msg} |");
                    }
                    sb.AppendLine();

                    // 第一筆 failure 印 stack trace 摘要 — 最常見的 NRE 用這個就能定位
                    var first = g.First();
                    sb.AppendLine("First-failure stack head:");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(first.StackHead ?? "(none)");
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            // Per-type stats
            sb.AppendLine("## Per-Type Stats");
            sb.AppendLine();
            sb.AppendLine("| Type | IDs | Loaded | LoadFail | Reflected | ReflectFail |");
            sb.AppendLine("|---|--:|--:|--:|--:|--:|");
            foreach (var s in stats.OrderBy(s => s.TypeName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"| `{s.TypeName}` | {s.IdCount} | {s.Loaded} | {s.LoadFailed} | {s.Reflected} | {s.ReflectFailed} |");
            }
            return sb.ToString();
        }

        // ===== helpers =====
        // GetArg 已上移至 UCL_AgentCommandHandlerBase（從原本寬鬆版改用嚴格版：value 為空字串視同未提供）
        private static int TryParseInt(string s, int fallback)
        {
            return int.TryParse(s, out var v) ? v : fallback;
        }
        private static string HeadStack(Exception ex)
        {
            string s = ex.StackTrace ?? "";
            var lines = s.Split('\n').Take(8);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 剝開 TargetInvocationException / TypeInitializationException 之類的 wrapper，
        /// 拿到真正觸發例外的內層異常。最多剝 5 層避免 cycle。
        /// </summary>
        private static Exception Unwrap(Exception ex)
        {
            for (int i = 0; i < 5 && ex != null; i++)
            {
                if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                { ex = tie.InnerException; continue; }
                if (ex is TypeInitializationException tx && tx.InnerException != null)
                { ex = tx.InnerException; continue; }
                break;
            }
            return ex;
        }
    }
}
#endif
