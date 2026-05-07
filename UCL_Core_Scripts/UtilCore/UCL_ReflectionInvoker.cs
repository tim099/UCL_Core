// 區塊職責：純邏輯反射調用器 — 把字串描述（type / member / args）解析成 .NET MemberInfo 並 Invoke。
// 物理意義：上層觸發來源解耦（不限定 Cmd_Invoke）— 任何 runtime / editor 程式碼都可建構
//          UCL_ReflectionInvokeRequest 並呼叫 UCL_ReflectionInvoker.Invoke，免為每支 API 寫專用包裝。
// 數值影響：純資料轉換 + 反射 Invoke；副作用視被呼叫的 API 而定。
//          解析失敗 / 找不到 type / member / 參數型別不符等 → 回 Result.Success=false。
//          Type 解析委派給 AssemblyExtensions（共用 cache）；字串轉型委派給 Type.TryConvertFromString 擴充。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UCL.Core
{
    // ===========================================================
    // 資料模型
    // ===========================================================

    /// <summary>反射調用的請求 — 由 <see cref="UCL_ReflectionInvoker.ParseRequest"/> 從字典解析，或 caller 直接 new。</summary>
    public class UCL_ReflectionInvokeRequest
    {
        /// <summary>完整型別名（如 <c>UnityEditor.Compilation.CompilationPipeline</c>）</summary>
        public string TypeName;
        /// <summary>成員名（method / property / field 名）</summary>
        public string MemberName;
        /// <summary>"method" / "property" / "field"；預設 "method"</summary>
        public string Kind = "method";
        /// <summary>method 多載消歧 — 完整型別名清單；空則以 MemberName 唯一匹配</summary>
        public List<string> ParamTypes = new List<string>();
        /// <summary>method 參數 / property setter 值 — 字串清單，按 ParamTypes 順序轉型</summary>
        public List<string> Args = new List<string>();
        /// <summary>property 是 get（true，預設）還是 set（false，此時 Args[0] 為值）</summary>
        public bool IsGetter = true;
        /// <summary>是否搜尋 internal / private static member（預設 false 只搜 public）— Unity 內建 API 大量 internal，常需開啟</summary>
        public bool IncludeNonPublic = false;
    }

    /// <summary>反射調用的結果。</summary>
    public class UCL_ReflectionInvokeResult
    {
        public bool Success;
        public string Error;
        public object Value;            // void method → null；getter / 一般 method → 實際回傳
        public string ValueAsString;    // ToString() 後備字串
        public Type ValueType;
    }

    // ===========================================================
    // 主邏輯
    // ===========================================================

    /// <summary>
    /// 字串描述 → MemberInfo → Invoke 的純邏輯轉換層。觸發來源不限 Cmd（runtime 工具 / Editor button /
    /// 外部 script 都可直接呼叫）。
    /// </summary>
    /// <remarks>
    /// 限制（v1）：
    /// <list type="bullet">
    ///   <item>只支援 public + static method / property / field（instance 需要 target，未實作）</item>
    ///   <item>參數型別自動轉換僅涵蓋 primitive / string / enum / 顯式 "null"（見 <see cref="AssemblyExtensions.TryConvertFromString"/>）</item>
    ///   <item>generic method 需要在 ParamTypes 一併展開（v1 不主動推 type args）</item>
    /// </list>
    /// </remarks>
    public static class UCL_ReflectionInvoker
    {
        // ---------- 解析 ----------

        /// <summary>
        /// 從字典（如 Cmd args）解析成 <see cref="UCL_ReflectionInvokeRequest"/>。
        /// 期待 keys：<c>type</c>（必填）/ <c>member</c>（必填）/ <c>kind</c> / <c>paramTypes</c> / <c>args</c> / <c>getter</c>。
        /// <c>paramTypes</c> 與 <c>args</c> 用分號 <c>;</c> 分隔。
        /// </summary>
        public static UCL_ReflectionInvokeRequest ParseRequest(IDictionary<string, string> rawArgs)
        {
            if (rawArgs == null) throw new ArgumentNullException(nameof(rawArgs));
            var req = new UCL_ReflectionInvokeRequest();
            if (!rawArgs.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("missing args[type] — fully qualified type name required (e.g. UnityEditor.Compilation.CompilationPipeline)");
            if (!rawArgs.TryGetValue("member", out var memberName) || string.IsNullOrWhiteSpace(memberName))
                throw new ArgumentException("missing args[member] — method / property / field name required");

            req.TypeName = typeName.Trim();
            req.MemberName = memberName.Trim();
            req.Kind = (rawArgs.TryGetValue("kind", out var k) && !string.IsNullOrWhiteSpace(k))
                ? k.Trim().ToLowerInvariant() : "method";
            req.IsGetter = !rawArgs.TryGetValue("getter", out var g)
                           || !string.Equals(g, "false", StringComparison.OrdinalIgnoreCase);
            req.ParamTypes = SplitSemicolon(rawArgs.TryGetValue("paramTypes", out var pt) ? pt : null);
            req.Args = SplitSemicolon(rawArgs.TryGetValue("args", out var a) ? a : null);
            req.IncludeNonPublic = rawArgs.TryGetValue("nonPublic", out var np)
                                   && string.Equals(np, "true", StringComparison.OrdinalIgnoreCase);
            return req;
        }

        // ---------- 執行 ----------

        public static UCL_ReflectionInvokeResult Invoke(UCL_ReflectionInvokeRequest req)
        {
            if (req == null) return Fail("request is null");

            // 1. 解析 type — 走 AssemblyExtensions 的共用 cache（FQN → Type）**嚴格匹配**
            // 物理意義：刻意不做大小寫 fallback — agent 應該餵原汁原味的 Type.FullName，
            //          錯一個字母就該被攔下，避免「明明拼錯卻撞到別的同名 type」這種隱性 bug
            Type type = AssemblyExtensions.GetTypeByFullName(req.TypeName);
            if (type == null) return Fail($"type not found: {req.TypeName} (use exact Type.FullName, case-sensitive)");

            // 2. 依 kind 分流
            BindingFlags flags = BuildBindingFlags(req);
            try
            {
                switch (req.Kind)
                {
                    case "method":   return InvokeMethod(type, req, flags);
                    case "property": return InvokeProperty(type, req, flags);
                    case "field":    return InvokeField(type, req, flags);
                    default:         return Fail($"unknown kind: {req.Kind} (expected method/property/field)");
                }
            }
            catch (TargetInvocationException ti)
            {
                // method body 內部丟例外 — 把原 InnerException 攤平回報，避免被反射層遮住
                var inner = ti.InnerException ?? ti;
                return Fail($"target threw {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            }
            catch (Exception e)
            {
                return Fail($"{e.GetType().Name}: {e.Message}");
            }
        }

        // 區塊職責：依 request flag 拼出 BindingFlags
        // 物理意義：Static + Public 永遠開；IncludeNonPublic=true 時加 NonPublic（含 internal / private / protected）
        // 數值影響：純位元 OR；不影響 instance（v1 不支援）
        static BindingFlags BuildBindingFlags(UCL_ReflectionInvokeRequest req)
        {
            var flags = BindingFlags.Static | BindingFlags.Public;
            if (req.IncludeNonPublic) flags |= BindingFlags.NonPublic;
            return flags;
        }

        // ---------- 子流程：method / property / field ----------

        static UCL_ReflectionInvokeResult InvokeMethod(Type type, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            MethodInfo method;
            if (req.ParamTypes.Count > 0)
            {
                // 帶 paramTypes：精確匹配多載 — 一樣走 strict FullName lookup
                var paramTypes = req.ParamTypes.Select(AssemblyExtensions.GetTypeByFullName).ToArray();
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    if (paramTypes[i] == null)
                        return Fail($"paramTypes[{i}] type not found: {req.ParamTypes[i]}");
                }
                method = type.GetMethod(req.MemberName, flags, binder: null,
                    types: paramTypes, modifiers: null);
                if (method == null)
                    return Fail($"method not found: {type.FullName}.{req.MemberName}({string.Join(",", req.ParamTypes)})" +
                                (req.IncludeNonPublic ? "" : " — try nonPublic=true"));
            }
            else
            {
                // 無 paramTypes：先試無參數多載（最常見），找不到再走 name 唯一匹配
                // 物理意義：CompilationPipeline.RequestScriptCompilation 這類有多載又常用無參版本的場景，
                //          empty paramTypes 直接對到 () 多載，免使用者每次寫 paramTypes=
                var candidates = type.GetMethods(flags).Where(m => m.Name == req.MemberName).ToList();
                if (candidates.Count == 0)
                    return Fail($"static method not found: {type.FullName}.{req.MemberName}" +
                                (req.IncludeNonPublic ? "" : " — try nonPublic=true"));

                var noArg = candidates.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (noArg != null)
                {
                    method = noArg;
                }
                else if (candidates.Count == 1)
                {
                    method = candidates[0];
                }
                else
                {
                    var sigs = string.Join("\n  ", candidates.Select(FormatMethodSignature));
                    return Fail($"ambiguous method (need paramTypes): {type.FullName}.{req.MemberName}\n  candidates:\n  {sigs}");
                }
            }

            // 轉參數
            var ps = method.GetParameters();
            if (req.Args.Count != ps.Length)
                return Fail($"arg count mismatch: method expects {ps.Length}, got {req.Args.Count}");
            object[] argv = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (!ps[i].ParameterType.TryConvertFromString(req.Args[i], out argv[i], out var err))
                    return Fail($"arg[{i}] '{req.Args[i]}' → {ps[i].ParameterType.FullName}: {err}");
            }

            object ret = method.Invoke(null, argv);
            return Ok(ret, method.ReturnType);
        }

        static UCL_ReflectionInvokeResult InvokeProperty(Type type, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            var prop = type.GetProperty(req.MemberName, flags);
            if (prop == null) return Fail($"static property not found: {type.FullName}.{req.MemberName}" +
                                          (req.IncludeNonPublic ? "" : " — try nonPublic=true"));

            // 開 nonPublic 時 GetGetMethod / GetSetMethod 也要傳 nonPublic=true 才看得到 internal accessor
            if (req.IsGetter)
            {
                if (prop.GetGetMethod(req.IncludeNonPublic) == null)
                    return Fail($"property has no accessible getter: {prop.Name}");
                object v = prop.GetValue(null);
                return Ok(v, prop.PropertyType);
            }
            // setter
            if (prop.GetSetMethod(req.IncludeNonPublic) == null)
                return Fail($"property has no accessible setter: {prop.Name}");
            if (req.Args.Count != 1) return Fail($"setter expects 1 arg, got {req.Args.Count}");
            if (!prop.PropertyType.TryConvertFromString(req.Args[0], out var sv, out var err))
                return Fail($"arg '{req.Args[0]}' → {prop.PropertyType.FullName}: {err}");
            prop.SetValue(null, sv);
            return Ok(null, typeof(void));
        }

        static UCL_ReflectionInvokeResult InvokeField(Type type, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            var field = type.GetField(req.MemberName, flags);
            if (field == null) return Fail($"static field not found: {type.FullName}.{req.MemberName}" +
                                           (req.IncludeNonPublic ? "" : " — try nonPublic=true"));

            if (req.IsGetter)
            {
                object v = field.GetValue(null);
                return Ok(v, field.FieldType);
            }
            if (req.Args.Count != 1) return Fail($"field set expects 1 arg, got {req.Args.Count}");
            if (!field.FieldType.TryConvertFromString(req.Args[0], out var sv, out var err))
                return Fail($"arg '{req.Args[0]}' → {field.FieldType.FullName}: {err}");
            field.SetValue(null, sv);
            return Ok(null, typeof(void));
        }

        // ---------- 結果 / 工具 ----------

        static UCL_ReflectionInvokeResult Ok(object value, Type valueType)
        {
            return new UCL_ReflectionInvokeResult
            {
                Success = true,
                Value = value,
                ValueType = valueType,
                ValueAsString = ValueToString(value),
            };
        }

        static UCL_ReflectionInvokeResult Fail(string err)
        {
            return new UCL_ReflectionInvokeResult { Success = false, Error = err };
        }

        static string ValueToString(object v)
        {
            if (v == null) return "null";
            if (v is string s) return s;
            // 簡單 enumerable 列出 — 不深層展開避免無窮遞迴
            if (v is IEnumerable en && !(v is string))
            {
                var sb = new StringBuilder("[");
                bool first = true;
                int n = 0;
                foreach (var item in en)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(item == null ? "null" : item.ToString());
                    first = false;
                    if (++n >= 32) { sb.Append(", ..."); break; }
                }
                sb.Append("]");
                return sb.ToString();
            }
            return v.ToString();
        }

        static List<string> SplitSemicolon(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            return raw.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        static string FormatMethodSignature(MethodInfo m)
        {
            return $"{m.ReturnType.Name} {m.Name}(" +
                   string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) +
                   ")";
        }
    }
}
