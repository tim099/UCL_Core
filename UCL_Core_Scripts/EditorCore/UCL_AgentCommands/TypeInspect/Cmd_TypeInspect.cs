// 區塊職責: Plan_Cmd_TypeInspect_Design MVP — agent 用的 Type Introspection 工具。
//          給 type 名 → 回完整 member list (signature/return/static/inherited)，解 agent API discovery friction。
// 物理意義: 純 reflection (System.Reflection) + 既有 AssemblyExtensions 的 type lookup cache;
//          零 runtime overhead (dev-time Cmd only)，輸出 markdown 走 _last_op.md。
// 數值影響: read-only — 不寫專案檔、不動 Treasury/messages，只讀型別 metadata 後 WriteLastOp。
// MVP scope: find / inspect / inspect_many 三 op (進階 op + 原始碼行號標註留 v2)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.TypeInspect
{
    // 區塊職責: 區域 helper — 對齊 Cmd_Glossary/Cmd_Tavern 用 _last_op.md 通報 (Reject=⚠ throw / Resolve=✅)
    internal static class Cmd_TypeInspect_Helpers
    {
        public static void ResolveLastOp(string md) => UCL_ChatTavernRender.WriteLastOp(md);

        public static void RejectLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ TypeInspect Cmd Rejected\n\n{msg}\n");
            Debug.LogWarning($"[TypeInspect] {msg}");
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Cmd_TypeInspect — Type introspection Cmd (對應 Plan_Cmd_TypeInspect_Design)。
    /// 解 agent「假設 .NET/Newtonsoft API、撞專案 native API」的反覆 friction。
    /// </summary>
    public class Cmd_TypeInspect : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "TypeInspect";

        public override string ShortDescription =>
            "Type introspection — 給 type 名回完整 member list (解 agent API discovery friction)";

        public override string ArgsSchema =>
            "op=find|inspect|inspect_many\n" +
            "find: type=短名或部分 full name — 列所有匹配 type 的 full name [max=N(default 60)]\n" +
            "inspect: type=full name 或短名 [include=public|all|non_inherited] [kinds=method,property,field,ctor,event,nested] — 回完整 member list\n" +
            "inspect_many: types=csv — 一次 inspect 多個 type";

        public override string ExampleArgs => "op=inspect;type=JsonData";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_TypeInspect.md";

        // 查詢類 cmd，下調 timeout 早期失敗
        public override int TimeoutSeconds => 120;

        // ===========================================================
        // ExecuteAsync — op-dispatch
        // ===========================================================
        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            switch (op)
            {
                case "find": Op_Find(args); break;
                case "inspect": Op_Inspect(args); break;
                case "inspect_many": Op_InspectMany(args); break;
                case "": Cmd_TypeInspect_Helpers.RejectLastOp("缺少 op 參數 (find|inspect|inspect_many)"); break;
                default: Cmd_TypeInspect_Helpers.RejectLastOp($"未知 op: {op} (支援 find|inspect|inspect_many)"); break;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 區塊職責: op=find — 短名/部分 full name 模糊搜尋, 列匹配 type
        // 物理意義: 掃 AssemblyExtensions.GetAllTypes() (已 cache), substring 比對 FullName/Name
        // ===========================================================
        private void Op_Find(Dictionary<string, string> args)
        {
            string q = GetArg(args, "type", "");
            if (string.IsNullOrEmpty(q)) { Cmd_TypeInspect_Helpers.RejectLastOp("find 缺少 type (短名或部分 full name)"); return; }
            int max = ParseInt(GetArg(args, "max", "60"), 60);

            var matches = AssemblyExtensions.GetAllTypes()
                .Where(t => t.FullName != null &&
                            (t.FullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"=== Cmd_TypeInspect (find, \"{q}\") ===");
            sb.AppendLine($"命中 {matches.Count} 個 type" + (matches.Count > max ? $" (顯示前 {max})" : ""));
            sb.AppendLine();
            foreach (var t in matches.Take(max))
                sb.AppendLine($"- {t.FullName}  [{t.Assembly.GetName().Name}]");
            if (matches.Count == 0)
                sb.AppendLine("(無匹配 — 試短名或檢查拼字)");
            Cmd_TypeInspect_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: op=inspect — 解析單一 type → 完整 member list
        // ===========================================================
        private void Op_Inspect(Dictionary<string, string> args)
        {
            string name = GetArg(args, "type", "");
            if (string.IsNullOrEmpty(name)) { Cmd_TypeInspect_Helpers.RejectLastOp("inspect 缺少 type"); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"=== Cmd_TypeInspect (inspect, {name}) ===\n");
            sb.Append(InspectOne(name, args));
            Cmd_TypeInspect_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: op=inspect_many — 一筆 Cmd 批次 inspect 多個 type (agent 一次吃多個 API)
        // ===========================================================
        private void Op_InspectMany(Dictionary<string, string> args)
        {
            string csv = GetArg(args, "types", "");
            if (string.IsNullOrEmpty(csv)) { Cmd_TypeInspect_Helpers.RejectLastOp("inspect_many 缺少 types (csv)"); return; }
            var names = csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"=== Cmd_TypeInspect (inspect_many, {names.Count} types) ===\n");
            foreach (var n in names)
            {
                sb.Append(InspectOne(n, args));
                sb.AppendLine("\n---\n");
            }
            Cmd_TypeInspect_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: 解析 type 名 + 格式化單一 type 的完整 member list (markdown)
        // 物理意義: 先 FullName 精準命中, 再退短名; 找不到列相近候選。
        //          member 依 kind 分組, inherited 標 (inherited from X), static/virtual 標 modifier。
        // ===========================================================
        private string InspectOne(string name, Dictionary<string, string> args)
        {
            Type t = AssemblyExtensions.GetTypeByFullName(name) ?? AssemblyExtensions.ResolveTypeByName(name);
            if (t == null)
            {
                var cand = AssemblyExtensions.GetAllTypes()
                    .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                (x.FullName != null && x.FullName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Take(8).Select(x => x.FullName).ToList();
                return $"## ❓ 找不到 type: {name}\n" +
                       (cand.Count > 0 ? "相近候選:\n" + string.Join("\n", cand.Select(c => "- " + c)) : "(無相近候選)") + "\n";
            }

            string include = GetArg(args, "include", "public").ToLowerInvariant();
            BindingFlags bf = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            if (include == "all") bf |= BindingFlags.NonPublic;
            if (include == "non_inherited") bf |= BindingFlags.DeclaredOnly;

            string kinds = GetArg(args, "kinds", "").ToLowerInvariant();
            bool Want(string k) => string.IsNullOrEmpty(kinds) || kinds.Contains(k);

            var sb = new StringBuilder();
            sb.AppendLine($"## {Friendly(t)}");
            sb.AppendLine($"- Full Name: {t.FullName}");
            sb.AppendLine($"- Assembly: {t.Assembly.GetName().Name}");
            sb.AppendLine($"- Kind: {KindOf(t)}");
            if (t.BaseType != null && t.BaseType != typeof(object))
                sb.AppendLine($"- Inherits: {Friendly(t.BaseType)}");
            var ifaces = t.GetInterfaces();
            if (ifaces.Length > 0)
                sb.AppendLine($"- Implements: {string.Join(", ", ifaces.Select(Friendly))}");
            sb.AppendLine();

            if (Want("ctor"))
            {
                var ctors = t.GetConstructors(bf & ~BindingFlags.Static);
                if (ctors.Length > 0)
                {
                    sb.AppendLine($"### Constructors ({ctors.Length})");
                    foreach (var c in ctors) sb.AppendLine($"- {t.Name}({Params(c.GetParameters())})");
                    sb.AppendLine();
                }
            }
            if (Want("method"))
            {
                var methods = t.GetMethods(bf).Where(m => !m.IsSpecialName).OrderBy(m => m.Name).ToList();
                if (methods.Count > 0)
                {
                    sb.AppendLine($"### Methods ({methods.Count})");
                    foreach (var m in methods) sb.AppendLine($"- {Mods(m)}{MethodSig(m)}{Inh(t, m.DeclaringType)}");
                    sb.AppendLine();
                }
            }
            if (Want("property"))
            {
                var props = t.GetProperties(bf).OrderBy(p => p.Name).ToList();
                if (props.Count > 0)
                {
                    sb.AppendLine($"### Properties ({props.Count})");
                    foreach (var p in props)
                        sb.AppendLine($"- {Friendly(p.PropertyType)} {p.Name} {{{(p.CanRead ? " get;" : "")}{(p.CanWrite ? " set;" : "")} }}{Inh(t, p.DeclaringType)}");
                    sb.AppendLine();
                }
            }
            if (Want("field"))
            {
                var fields = t.GetFields(bf).OrderBy(f => f.Name).ToList();
                if (fields.Count > 0)
                {
                    sb.AppendLine($"### Fields ({fields.Count})");
                    foreach (var f in fields)
                        sb.AppendLine($"- {(f.IsStatic ? "static " : "")}{Friendly(f.FieldType)} {f.Name}{Inh(t, f.DeclaringType)}");
                    sb.AppendLine();
                }
            }
            if (Want("event"))
            {
                var evs = t.GetEvents(bf).ToList();
                if (evs.Count > 0)
                {
                    sb.AppendLine($"### Events ({evs.Count})");
                    foreach (var e in evs) sb.AppendLine($"- {Friendly(e.EventHandlerType)} {e.Name}");
                    sb.AppendLine();
                }
            }
            if (Want("nested"))
            {
                BindingFlags nf = BindingFlags.Public;
                if (include == "all") nf |= BindingFlags.NonPublic;
                var nested = t.GetNestedTypes(nf).ToList();
                if (nested.Count > 0)
                {
                    sb.AppendLine($"### Nested Types ({nested.Count})");
                    foreach (var n in nested) sb.AppendLine($"- {n.Name}");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        // ===== formatting helpers =====

        private static string KindOf(Type t)
        {
            if (t.IsInterface) return "interface";
            if (t.IsEnum) return "enum";
            if (t.IsValueType) return "struct";
            if (t.IsAbstract && t.IsSealed) return "static class";
            if (t.IsAbstract) return "abstract class";
            return "class";
        }

        // inherited 標註 (member 的 DeclaringType != 被查 type → 來自 base)
        private static string Inh(Type owner, Type decl) => (decl != null && decl != owner) ? $"  (inherited from {decl.Name})" : "";

        private static string Mods(MethodInfo m) => (m.IsStatic ? "static " : "") + (m.IsVirtual && !m.IsFinal ? "virtual " : "");

        private static string MethodSig(MethodInfo m)
        {
            string gen = m.IsGenericMethod ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            return $"{Friendly(m.ReturnType)} {m.Name}{gen}({Params(m.GetParameters())})";
        }

        private static string Params(ParameterInfo[] ps) =>
            string.Join(", ", ps.Select(p =>
                Friendly(p.ParameterType) + " " + p.Name +
                (p.HasDefaultValue ? " = " + (p.DefaultValue?.ToString() ?? "null") : "")));

        // 友善型別名 (處理 void / generic / array / by-ref / 泛型參數)
        private static string Friendly(Type t)
        {
            if (t == null) return "void";
            if (t == typeof(void)) return "void";
            if (t.IsByRef) return Friendly(t.GetElementType());
            if (t.IsGenericParameter) return t.Name;
            if (t.IsArray) return Friendly(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                string n = t.Name;
                int tick = n.IndexOf('`');
                if (tick >= 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(", ", t.GetGenericArguments().Select(Friendly)) + ">";
            }
            return t.Name;
        }

        private static int ParseInt(string s, int def) => int.TryParse(s, out var v) ? v : def;
    }
}
#endif
