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
        /// <summary>
        /// instance member 呼叫的目標物件。null/empty 時走 static；設定字串時從 <see cref="UCL_ReflectionInvoker.Variables"/>
        /// 查同名變數（前綴 <c>$</c> 由 ParseRequest 自動剃除）。target 設定後 BindingFlags 自動切到 Instance。
        /// </summary>
        public string Target;
        /// <summary>呼叫成功時把回傳值寫入 <see cref="UCL_ReflectionInvoker.Variables"/>[StoreAs]，供後續 invoke 引用</summary>
        public string StoreAs;
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
    /// 支援：
    /// <list type="bullet">
    ///   <item>static / instance method / property / field（instance 透過 <see cref="UCL_ReflectionInvokeRequest.Target"/> 從 <see cref="Variables"/> 取目標）</item>
    ///   <item>public / nonPublic（<see cref="UCL_ReflectionInvokeRequest.IncludeNonPublic"/>）</item>
    ///   <item>static 成員的 BaseType hierarchy walk（generic base class 的 static 如 <c>UCL_Util&lt;T&gt;.Util</c> 也找得到）</item>
    ///   <item>method 多載（<see cref="UCL_ReflectionInvokeRequest.ParamTypes"/> 嚴格匹配；空時偏好無參版本）</item>
    ///   <item>method 參數的 default value 自動補齊（<see cref="ParameterInfo.HasDefaultValue"/>）</item>
    ///   <item>跨 invoke 變數鏈（<see cref="Variables"/> + <c>$varname</c> 語法）</item>
    /// </list>
    /// 限制：
    /// <list type="bullet">
    ///   <item>參數型別自動轉換僅涵蓋 primitive / string / enum / 顯式 "null"（見 <see cref="AssemblyExtensions.TryConvertFromString"/>）；複雜物件須先 storeAs 再用 <c>$varname</c> 引用</item>
    ///   <item>generic method 需要在 ParamTypes 一併展開（不主動推 type args）</item>
    ///   <item>args 內的字面值 <c>$abc</c> 會被視為變數引用，無 escape 機制（v1）</item>
    /// </list>
    /// </remarks>
    public static class UCL_ReflectionInvoker
    {
        // ===========================================================
        // 跨 invoke 變數儲存
        // ===========================================================

        /// <summary>
        /// 跨 invoke 共用的變數字典 — agent 可在一支 invoke 用 <c>storeAs=foo</c> 把回傳值塞進來，
        /// 下一支 invoke 用 <c>target=$foo</c> 或 <c>args=$foo;...</c> 引用。Editor 內全域 / 跨 Cmd 有效；
        /// domain reload（含 Cmd_Recompile）會清空 — 設計上不持久化，避免狀態污染。
        /// </summary>
        public static readonly Dictionary<string, object> Variables = new Dictionary<string, object>();

        /// <summary>清空 <see cref="Variables"/>。提供給工具呼叫；正常使用不必手動清。</summary>
        public static void ClearVariables() => Variables.Clear();

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
            // type 在「有 target 時」可省略（會用 target.GetType()），所以這裡不強制；Invoke 內再判
            if (rawArgs.TryGetValue("type", out var typeName) && !string.IsNullOrWhiteSpace(typeName))
            {
                req.TypeName = typeName.Trim();
            }
            if (!rawArgs.TryGetValue("member", out var memberName) || string.IsNullOrWhiteSpace(memberName))
                throw new ArgumentException("missing args[member] — method / property / field name required");

            req.MemberName = memberName.Trim();
            req.Kind = (rawArgs.TryGetValue("kind", out var k) && !string.IsNullOrWhiteSpace(k))
                ? k.Trim().ToLowerInvariant() : "method";
            req.IsGetter = !rawArgs.TryGetValue("getter", out var g)
                           || !string.Equals(g, "false", StringComparison.OrdinalIgnoreCase);
            req.ParamTypes = SplitSemicolon(rawArgs.TryGetValue("paramTypes", out var pt) ? pt : null);
            req.Args = SplitSemicolon(rawArgs.TryGetValue("args", out var a) ? a : null);
            req.IncludeNonPublic = rawArgs.TryGetValue("nonPublic", out var np)
                                   && string.Equals(np, "true", StringComparison.OrdinalIgnoreCase);
            // 區塊職責：target 與 storeAs — instance method + 變數儲存
            // 物理意義：target=$varname 或 target=varname 都接受（前綴 $ 純為清晰，內部一律剃除）
            //          storeAs=varname 不允許 $（變數名必須是純識別子）
            if (rawArgs.TryGetValue("target", out var tg) && !string.IsNullOrWhiteSpace(tg))
            {
                req.Target = tg.Trim().TrimStart('$');
            }
            if (rawArgs.TryGetValue("storeAs", out var sa) && !string.IsNullOrWhiteSpace(sa))
            {
                req.StoreAs = sa.Trim();
            }
            return req;
        }

        // ---------- 執行 ----------

        public static UCL_ReflectionInvokeResult Invoke(UCL_ReflectionInvokeRequest req)
        {
            if (req == null) return Fail("request is null");

            // 1. 取 target instance（如果有）— 影響後續 binding flags 與 invoke 第一參
            object target = null;
            bool isInstance = !string.IsNullOrEmpty(req.Target);
            if (isInstance)
            {
                if (!Variables.TryGetValue(req.Target, out target) || target == null)
                {
                    return Fail($"target variable '${req.Target}' not found in Variables (use storeAs=... in a previous invoke first)");
                }
            }

            // 2. 解析 type — 走 AssemblyExtensions 的共用 cache（FQN → Type）**嚴格匹配**
            // 物理意義：刻意不做大小寫 fallback — agent 應該餵原汁原味的 Type.FullName。
            //          有 target 時 type 可省略 → 直接用 target.GetType()（讓 caller 不必重複指定）
            Type type;
            if (!string.IsNullOrEmpty(req.TypeName))
            {
                type = AssemblyExtensions.GetTypeByFullName(req.TypeName);
                if (type == null) return Fail($"type not found: {req.TypeName} (use exact Type.FullName, case-sensitive)");
            }
            else if (isInstance)
            {
                type = target.GetType();
            }
            else
            {
                return Fail("missing args[type] — required when no target instance is provided");
            }

            // 3. 依 kind 分流
            BindingFlags flags = BuildBindingFlags(req, isInstance);
            try
            {
                switch (req.Kind)
                {
                    case "method":   return WithStore(req, InvokeMethod(type, target, req, flags));
                    case "property": return WithStore(req, InvokeProperty(type, target, req, flags));
                    case "field":    return WithStore(req, InvokeField(type, target, req, flags));
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
        // 物理意義：依 isInstance 切 Static / Instance；Public 永遠開；IncludeNonPublic=true 時加 NonPublic。
        //          靜態查詢還會加 FlattenHierarchy — 讓繼承來的 static 成員（如 UCL_Util<T>.Util）可被
        //          子類別（RCG_StoryData）反射查到。Instance 則不需要（GetType.GetProperty 會自動沿 hierarchy 查）
        // 數值影響：純位元 OR
        static BindingFlags BuildBindingFlags(UCL_ReflectionInvokeRequest req, bool isInstance)
        {
            var flags = BindingFlags.Public | (isInstance ? BindingFlags.Instance : (BindingFlags.Static | BindingFlags.FlattenHierarchy));
            if (req.IncludeNonPublic) flags |= BindingFlags.NonPublic;
            return flags;
        }

        // 區塊職責：呼叫成功時把 Value 寫入 Variables[StoreAs]
        // 物理意義：給後續 invoke 用 $varname 引用（target / args 皆可）
        // 數值影響：失敗結果不寫入；無 StoreAs 時跳過
        static UCL_ReflectionInvokeResult WithStore(UCL_ReflectionInvokeRequest req, UCL_ReflectionInvokeResult result)
        {
            if (result.Success && !string.IsNullOrEmpty(req.StoreAs))
            {
                Variables[req.StoreAs] = result.Value;
            }
            return result;
        }

        // 區塊職責：把 args 字串轉成參數 object[] — 多了 $varname 從 Variables 取的支援
        // 物理意義：$ 前綴 → 直接拿 Variables[name] 物件（不走 TryConvertFromString）；
        //          其餘 → Type.TryConvertFromString 走 primitive/string/enum/null 轉換
        // 數值影響：任何失敗 return false + err
        static bool TryBuildArgValues(ParameterInfo[] ps, List<string> rawArgs, object[] outValues, out string err)
        {
            err = null;
            // 只處理 caller 提供的部分；tail 缺的位置由 DefaultValue 補（在外層處理）
            int n = Math.Min(rawArgs.Count, ps.Length);
            for (int i = 0; i < n; i++)
            {
                string raw = rawArgs[i];
                if (!string.IsNullOrEmpty(raw) && raw[0] == '$')
                {
                    string varName = raw.Substring(1);
                    if (!Variables.TryGetValue(varName, out var v))
                    {
                        err = $"arg[{i}] '${varName}' not found in Variables";
                        return false;
                    }
                    // 型別檢查：null 對 value type 不行；object 不需檢查（讓 Invoke 自己 throw）
                    if (v == null && ps[i].ParameterType.IsValueType
                        && Nullable.GetUnderlyingType(ps[i].ParameterType) == null)
                    {
                        err = $"arg[{i}] '${varName}' is null but parameter type {ps[i].ParameterType.FullName} is non-nullable value type";
                        return false;
                    }
                    outValues[i] = v;
                    continue;
                }
                if (!ps[i].ParameterType.TryConvertFromString(raw, out outValues[i], out var convErr))
                {
                    err = $"arg[{i}] '{raw}' → {ps[i].ParameterType.FullName}: {convErr}";
                    return false;
                }
            }
            return true;
        }

        // ---------- 子流程：method / property / field ----------

        static UCL_ReflectionInvokeResult InvokeMethod(Type type, object target, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            string scope = target != null ? "method" : "static method";
            MethodInfo method;
            if (req.ParamTypes.Count > 0)
            {
                var paramTypes = req.ParamTypes.Select(AssemblyExtensions.GetTypeByFullName).ToArray();
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    if (paramTypes[i] == null)
                        return Fail($"paramTypes[{i}] type not found: {req.ParamTypes[i]}");
                }
                // 物理意義：static method 走 hierarchy walk（generic base class 的 static 不一定吃 FlattenHierarchy）；
                //          instance method 不必（GetMethod 自動沿 hierarchy）
                method = FindMember(type, req.MemberName, flags,
                    (t, n, f) => t.GetMethod(n, f, binder: null, types: paramTypes, modifiers: null),
                    walkHierarchy: target == null);
                if (method == null)
                    return Fail($"{scope} not found: {type.FullName}.{req.MemberName}({string.Join(",", req.ParamTypes)})" +
                                (req.IncludeNonPublic ? "" : " — try nonPublic=true"));
            }
            else
            {
                // 無 paramTypes：先試無參數多載（最常見），找不到再走 name 唯一匹配
                // 同樣對 static 走 hierarchy walk 蒐集所有層的 candidates
                var candidates = new List<MethodInfo>();
                {
                    var t = type;
                    while (t != null && t != typeof(object))
                    {
                        candidates.AddRange(t.GetMethods(flags).Where(m => m.Name == req.MemberName));
                        if (target != null) break;
                        t = t.BaseType;
                    }
                }
                if (candidates.Count == 0)
                    return Fail($"{scope} not found: {type.FullName}.{req.MemberName}" +
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

            // 轉參數（含 $varname 從 Variables 引用 + default value 補齊）
            // 物理意義：req.Args.Count 可少於 ps.Length — 缺的 tail 參數若有 DefaultValue 就自動補
            //          （例 RCG_StoryData.GetData(string iID, bool iUseCache=true) 只給 iID 即可）
            //          多了則直接 fail
            var ps = method.GetParameters();
            if (req.Args.Count > ps.Length)
                return Fail($"too many args: method expects up to {ps.Length}, got {req.Args.Count}");
            object[] argv = new object[ps.Length];
            // 先處理使用者提供的部分
            if (!TryBuildArgValues(ps, req.Args, argv, out var argErr))
                return Fail(argErr);
            // 再用 DefaultValue 補齊缺的
            for (int i = req.Args.Count; i < ps.Length; i++)
            {
                if (!ps[i].HasDefaultValue)
                    return Fail($"arg[{i}] '{ps[i].Name}' has no default and no value provided " +
                                $"(method expects {ps.Length} args, got {req.Args.Count})");
                argv[i] = ps[i].DefaultValue;
            }

            object ret = method.Invoke(target, argv);
            return Ok(ret, method.ReturnType);
        }

        static UCL_ReflectionInvokeResult InvokeProperty(Type type, object target, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            string scope = target != null ? "property" : "static property";
            // 物理意義：generic base class 的 static 成員（如 UCL_Util<T>.Util）有時 FlattenHierarchy 也找不到
            //          需要手動沿 BaseType 走鏈逐層 GetProperty
            var prop = FindMember(type, req.MemberName, flags, (t, n, f) => t.GetProperty(n, f), target == null);
            if (prop == null) return Fail($"{scope} not found: {type.FullName}.{req.MemberName}" +
                                          (req.IncludeNonPublic ? "" : " — try nonPublic=true"));

            if (req.IsGetter)
            {
                if (prop.GetGetMethod(req.IncludeNonPublic) == null)
                    return Fail($"property has no accessible getter: {prop.Name}");
                object v = prop.GetValue(target);
                return Ok(v, prop.PropertyType);
            }
            if (prop.GetSetMethod(req.IncludeNonPublic) == null)
                return Fail($"property has no accessible setter: {prop.Name}");
            if (req.Args.Count != 1) return Fail($"setter expects 1 arg, got {req.Args.Count}");
            // setter 也支援 $varname
            object sv;
            if (!string.IsNullOrEmpty(req.Args[0]) && req.Args[0][0] == '$')
            {
                string varName = req.Args[0].Substring(1);
                if (!Variables.TryGetValue(varName, out sv))
                    return Fail($"setter arg '${varName}' not found in Variables");
            }
            else if (!prop.PropertyType.TryConvertFromString(req.Args[0], out sv, out var err))
            {
                return Fail($"arg '{req.Args[0]}' → {prop.PropertyType.FullName}: {err}");
            }
            prop.SetValue(target, sv);
            return Ok(null, typeof(void));
        }

        static UCL_ReflectionInvokeResult InvokeField(Type type, object target, UCL_ReflectionInvokeRequest req, BindingFlags flags)
        {
            string scope = target != null ? "field" : "static field";
            var field = FindMember(type, req.MemberName, flags, (t, n, f) => t.GetField(n, f), target == null);
            if (field == null) return Fail($"{scope} not found: {type.FullName}.{req.MemberName}" +
                                           (req.IncludeNonPublic ? "" : " — try nonPublic=true"));

            if (req.IsGetter)
            {
                object v = field.GetValue(target);
                return Ok(v, field.FieldType);
            }
            if (req.Args.Count != 1) return Fail($"field set expects 1 arg, got {req.Args.Count}");
            object sv;
            if (!string.IsNullOrEmpty(req.Args[0]) && req.Args[0][0] == '$')
            {
                string varName = req.Args[0].Substring(1);
                if (!Variables.TryGetValue(varName, out sv))
                    return Fail($"setter arg '${varName}' not found in Variables");
            }
            else if (!field.FieldType.TryConvertFromString(req.Args[0], out sv, out var err))
            {
                return Fail($"arg '{req.Args[0]}' → {field.FieldType.FullName}: {err}");
            }
            field.SetValue(target, sv);
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

        // 區塊職責：先在 type 直接找成員；找不到時「手動沿 BaseType 走鏈」逐層找 static 成員
        // 物理意義：generic base class 的 static 成員（如 UCL_Util<T>.Util 被 RCG_StoryData 繼承）
        //          BindingFlags.FlattenHierarchy 在某些 .NET runtime / 某些泛型情境不一定有效，
        //          手動 walk BaseType 是最可靠的兜底
        // 數值影響：純查詢；找到立刻 return；走到 typeof(object) 為止
        static T FindMember<T>(Type type, string name, BindingFlags flags,
            Func<Type, string, BindingFlags, T> getter, bool walkHierarchy) where T : MemberInfo
        {
            var t = type;
            while (t != null && t != typeof(object))
            {
                var m = getter(t, name, flags);
                if (m != null) return m;
                if (!walkHierarchy) return null;
                t = t.BaseType;
            }
            return null;
        }
    }
}
