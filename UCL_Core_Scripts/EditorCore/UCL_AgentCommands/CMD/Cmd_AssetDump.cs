// 區塊職責：本檔提供「dump 單一 UCL_Asset 的解析後欄位樹」診斷 Cmd。
// 物理意義：給 args (asset_type, id), GetAsset(id) → 走反射 walk fields → 印 markdown 樹。
//          用於排查 JSON schema drift (e.g. enum 值非法導致 default fallback 但難肉眼定位)。
// 數值影響：純讀取，不修改任何 Asset；只在指定 outputPath 寫入一份 md 檔。
// 對比 Cmd_DiagnoseAssetReflection: 那是 sweep 全型別找失敗點；本 Cmd 是 single asset 顯微鏡。
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command — Dump 單一 UCL_Asset deserialize 後的 field tree。
    ///
    /// 用例：排查 schema drift。例 — RCG_CharacterData AncientTreeSpirit JSON 寫 Pos:"Middle"
    /// 但 UnitPos enum 只有 Front/Back/All → Enum.Parse 拋, JsonConvert fail-soft 設 default.
    /// 想知道實際被 deserialize 進記憶體的 Pos 值是什麼 → 跑本 Cmd 看效實際 value 即可。
    ///
    /// 參數：
    /// - <c>asset_type</c>（必填）：UCL_Asset 子類短名 (e.g. RCG_CharacterData)
    /// - <c>id</c>（必填）：asset ID (e.g. AncientTreeSpirit)
    /// - <c>maxDepth</c>（選填，預設 8）：reflection recursion 上限
    /// - <c>outputPath</c>（選填）：輸出 md 路徑 (相對 project root); 省略則 AgentCommands/asset_dump_<type>_<id>.md
    /// </summary>
    public class Cmd_AssetDump : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "AssetDump";

        public override string ShortDescription =>
            "Dump deserialized field tree of a single UCL_Asset (schema-drift forensics).";

        public override string ArgsSchema =>
            "asset_type=UCL_Asset subclass short name, required (e.g. RCG_CharacterData)\n" +
            "id=Asset ID, required (e.g. AncientTreeSpirit)\n" +
            "maxDepth=Recursion depth cap (default 8)\n" +
            "outputPath=Output md path relative to project root (default AgentCommands/asset_dump_<type>_<id>.md)";

        public override string ExampleArgs => "asset_type=RCG_CharacterData id=AncientTreeSpirit";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_AssetDump.md";

        private static string ProjectRoot => UCL_RepoPath.UnityProjectRoot;

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.SwitchToMainThread();

            string typeName = GetArg(args, "asset_type", null);
            string id = GetArg(args, "id", null);
            int maxDepth = TryParseInt(GetArg(args, "maxDepth", "8"), 8);
            string outputPath = GetArg(args, "outputPath", null);

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(id))
            {
                throw new System.Exception("AssetDump requires --arg asset_type=<X> --arg id=<Y>");
                return;
            }

            // 解析型別
            Type assetType = null;
            try
            {
                assetType = AssemblyExtensions.ResolveTypeByNamePreferSubclassOfGenericDefinition(typeName, typeof(UCL_Asset<>));
            }
            catch (Exception ex)
            {
                throw new System.Exception($"AssetDump: failed to resolve type '{typeName}': {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (assetType == null || !assetType.IsSubclassOfGenericTypeDefinition(typeof(UCL_Asset<>)))
            {
                throw new System.Exception($"AssetDump: type '{typeName}' is not a UCL_Asset<> subclass");
                return;
            }

            // 取 Util.GetAsset(id, true) (走 reflection 拿 generic Util)
            object asset;
            try
            {
                // UCL_Asset<T> : UCL_Util<T>, Util static 在 generic base — 需 walk base hierarchy
                // (BindingFlags.FlattenHierarchy 對 static generic base 不可靠, walk 比較穩)
                Type walk = assetType;
                PropertyInfo utilProp = null;
                while (walk != null && walk != typeof(object))
                {
                    utilProp = walk.GetProperty("Util", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (utilProp != null) break;
                    walk = walk.BaseType;
                }
                var util = utilProp?.GetValue(null);
                if (util == null) { throw new System.Exception($"AssetDump: Util property null (walked hierarchy from {assetType.Name})"); return; }
                var getAssetMi = util.GetType().GetMethod("GetAsset", new[] { typeof(string), typeof(bool) });
                if (getAssetMi == null) { throw new System.Exception("AssetDump: GetAsset method not found"); return; }
                asset = getAssetMi.Invoke(util, new object[] { id, true });
            }
            catch (TargetInvocationException tie)
            {
                throw new System.Exception($"AssetDump: GetAsset threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                return;
            }
            catch (Exception ex)
            {
                throw new System.Exception($"AssetDump: GetAsset failed {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (asset == null)
            {
                throw new System.Exception($"AssetDump: asset '{id}' not found in type '{typeName}'");
                return;
            }

            // Dump field tree
            var sb = new StringBuilder();
            sb.AppendLine($"# Asset Dump — `{typeName}` / `{id}`");
            sb.AppendLine();
            sb.AppendLine($"- **Generated**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **Concrete type**: `{asset.GetType().FullName}`");
            sb.AppendLine($"- **Reflection max depth**: {maxDepth}");
            sb.AppendLine();
            sb.AppendLine("## Field Tree");
            sb.AppendLine();
            sb.AppendLine("```");
            var visited = new HashSet<object>(new RefEqComparer());
            DumpObject(asset, sb, 0, maxDepth, visited);
            sb.AppendLine("```");

            // 輸出
            string outRel = outputPath;
            if (string.IsNullOrWhiteSpace(outRel))
            {
                string safeId = id.Replace("/", "_").Replace("\\", "_");
                outRel = $"AgentCommands/asset_dump_{typeName}_{safeId}.md";
            }
            string outAbs = Path.IsPathRooted(outRel) ? outRel : Path.Combine(ProjectRoot, outRel);
            Directory.CreateDirectory(Path.GetDirectoryName(outAbs));
            File.WriteAllText(outAbs, sb.ToString(), Encoding.UTF8);

            Debug.Log($"[Cmd_AssetDump] {typeName}/{id} → {outRel} ({sb.Length} chars)");
            await UniTask.Yield();
        }

        // 區塊職責：recursive field dump
        // 物理意義：對 obj 走 instance fields (含 NonPublic), 印 name/type/value
        //          - 基本型 → 直接 ToString()
        //          - enum → ToString() (顯示實際 enum 值, 包括 fail-soft 後的 default)
        //          - string/UnityEngine.Object → 直接印
        //          - IEnumerable → 列舉 elements
        //          - 其他 class → 下鑽 (depth-1)
        // 數值影響：純讀取
        private static void DumpObject(object obj, StringBuilder sb, int depth, int maxDepth, HashSet<object> visited)
        {
            if (obj == null) { sb.AppendLine(Indent(depth) + "(null)"); return; }
            if (depth >= maxDepth) { sb.AppendLine(Indent(depth) + "... (max depth)"); return; }
            var t = obj.GetType();

            // 處理 cycle (only for reference types)
            if (!t.IsValueType && t != typeof(string))
            {
                if (visited.Contains(obj)) { sb.AppendLine(Indent(depth) + "(circular ref)"); return; }
                visited.Add(obj);
            }

            // 取 instance fields (含 private/inherited)
            var fields = new List<FieldInfo>();
            for (var ct = t; ct != null && ct != typeof(object); ct = ct.BaseType)
            {
                fields.AddRange(ct.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            }
            if (fields.Count == 0)
            {
                sb.AppendLine(Indent(depth) + FormatValue(obj));
                return;
            }

            foreach (var f in fields)
            {
                object val;
                try { val = f.GetValue(obj); }
                catch (Exception ex) { val = $"<get failed: {ex.GetType().Name}: {ex.Message}>"; continue; }
                sb.Append(Indent(depth));
                sb.Append(f.Name);
                sb.Append(" : ");
                sb.Append(SimpleTypeName(f.FieldType));
                sb.Append(" = ");
                if (val == null) { sb.AppendLine("(null)"); continue; }

                var ft = val.GetType();
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(decimal))
                {
                    sb.AppendLine(FormatValue(val));
                }
                else if (val is IEnumerable enumerable && ft != typeof(string))
                {
                    sb.AppendLine("[");
                    int i = 0;
                    foreach (var item in enumerable)
                    {
                        sb.Append(Indent(depth + 1) + $"[{i}] ");
                        if (item == null) sb.AppendLine("(null)");
                        else if (item.GetType().IsPrimitive || item.GetType().IsEnum || item is string) sb.AppendLine(FormatValue(item));
                        else
                        {
                            sb.AppendLine();
                            DumpObject(item, sb, depth + 2, maxDepth, visited);
                        }
                        i++;
                        if (i >= 100) { sb.AppendLine(Indent(depth + 1) + "... (truncated at 100 items)"); break; }
                    }
                    sb.AppendLine(Indent(depth) + "]");
                }
                else
                {
                    sb.AppendLine();
                    DumpObject(val, sb, depth + 1, maxDepth, visited);
                }
            }
        }

        private static string Indent(int n) => new string(' ', n * 2);

        private static string FormatValue(object v)
        {
            if (v == null) return "(null)";
            if (v is string s) return "\"" + s + "\"";
            if (v is Enum e) return $"{v} ({Convert.ToInt32(e)})";
            return v.ToString();
        }

        private static string SimpleTypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition().Name;
                var idx = def.IndexOf('`');
                if (idx > 0) def = def.Substring(0, idx);
                var args = string.Join(",", Array.ConvertAll(t.GetGenericArguments(), SimpleTypeName));
                return $"{def}<{args}>";
            }
            return t.Name;
        }

        private class RefEqComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static int TryParseInt(string s, int defaultValue) =>
            int.TryParse(s, out var v) ? v : defaultValue;
    }
}
#endif
