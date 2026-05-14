using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Linq;
public static partial class AssemblyExtensions {

    private static Assembly[] s_Assemblys = null;
    public static Assembly[] GetAssemblies()
    {
        if(s_Assemblys == null)
        {
            s_Assemblys = AppDomain.CurrentDomain.GetAssemblies();
        }
        return s_Assemblys;
    }
    private static List<Type> s_Types = null;
    public static List<Type> GetAllTypes()
    {
        if(s_Types == null)
        {
            var aAssemblies = GetAssemblies();
            s_Types = aAssemblies.SelectMany(iAssembly => iAssembly.GetTypes()).ToList();
        }

        return s_Types;
    }

    private static List<Type> s_StaticTypes = null;
    public static List<Type> GetAllStaticTypes()
    {
        if (s_StaticTypes == null)
        {
            var types = GetAllTypes();
            s_StaticTypes = types.Where(type => type.IsAbstract && type.IsSealed).ToList();
        }
        return s_StaticTypes;
    }
    private static List<string> s_StaticTypeFullName = null;
    public static List<string> GetAllStaticTypesFullName()
    {
        if (s_StaticTypeFullName == null)
        {
            var types = GetAllStaticTypes();
            s_StaticTypeFullName = types.Select(type => type.FullName).ToList();
        }
        return s_StaticTypeFullName;
    }


    private static Dictionary<string, Type> s_TypeDic = null;
    public static Dictionary<string, Type> TypeDic
    {
        get
        {
            if(s_TypeDic == null)
            {
                var types = GetAllTypes();
                s_TypeDic = new();
                foreach(var type in types)
                {
                    s_TypeDic[type.FullName] = type;
                }
            }
            return s_TypeDic;
        }
    }
    public static Type GetTypeByFullName(string iTypeFullName)
    {
        if(string.IsNullOrEmpty(iTypeFullName)) return null;
        var typeDic = TypeDic;
        if(typeDic.TryGetValue(iTypeFullName, out var type))
        {
            return type;
        }
        return null;
    }

    // 區塊職責：「短名（Type.Name）→ 全部同名 Type」的反向 lookup cache
    // 物理意義：agent / Cmd 收到的 type 引用通常只有短名（無 namespace），需在所有 assembly 內找對應 Type；
    //          短名可能多型別共用（不同 namespace 同名），故存 List 不存單一 Type。
    // 數值影響：lazy build；第一次呼叫掃 GetAllTypes() 建表，之後 O(1) lookup。
    private static Dictionary<string, List<Type>> s_TypesByShortName = null;
    private static Dictionary<string, List<Type>> TypesByShortName
    {
        get
        {
            if (s_TypesByShortName == null)
            {
                var types = GetAllTypes();
                s_TypesByShortName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
                foreach (var t in types)
                {
                    if (t == null) continue;
                    var name = t.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!s_TypesByShortName.TryGetValue(name, out var list))
                    {
                        list = new List<Type>();
                        s_TypesByShortName[name] = list;
                    }
                    list.Add(t);
                }
            }
            return s_TypesByShortName;
        }
    }

    /// <summary>
    /// 區塊職責：依名稱（短名 Type.Name 或 FullName）找全部對應的 Type。
    /// 物理意義：先試 FullName 精準命中（TypeDic），找不到再退到短名 lookup（TypesByShortName）。
    /// 數值影響：cache O(1) hit；空字串 / null → 空 list。
    /// </summary>
    /// <param name="iName">短名或 FullName</param>
    /// <returns>命中 Type 的 list（可能多筆；找不到回空 list，不回 null）</returns>
    public static IReadOnlyList<Type> ResolveTypesByName(string iName)
    {
        if (string.IsNullOrEmpty(iName)) return Array.Empty<Type>();

        // FullName 精準命中（最常見：agent 給完整 namespace 名）
        if (TypeDic.TryGetValue(iName, out var fullHit) && fullHit != null)
        {
            return new[] { fullHit };
        }
        // 短名 fallback
        if (TypesByShortName.TryGetValue(iName, out var list))
        {
            return list;
        }
        return Array.Empty<Type>();
    }

    /// <summary>
    /// 區塊職責：依名稱找對應 Type — 命中多筆時取第一筆。
    /// 物理意義：給「八成不重名」的 caller 用；要做 disambiguation（e.g. 偏好某 base class）的 caller 該用 ResolveTypesByName 或 ResolveTypeByNamePreferSubclassOfGenericDefinition。
    /// 數值影響：找不到回 null。
    /// </summary>
    public static Type ResolveTypeByName(string iName)
    {
        var hits = ResolveTypesByName(iName);
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>
    /// 區塊職責：依名稱找 Type — 命中多筆時優先取「繼承自指定開放泛型 base」的版本。
    /// 物理意義：典型用途 — agent 收到短名 "RCG_ItemData"，跨 assembly 可能撞名；
    ///          若有一個是 UCL_Asset&lt;&gt; 子類、另一個不是 → 優先回 UCL_Asset&lt;&gt; 子類（更貼近 caller 意圖）。
    /// 數值影響：iOpenGenericBase==null 或非 generic def → 等價 ResolveTypeByName；找不到回 null。
    /// </summary>
    /// <param name="iName">短名或 FullName</param>
    /// <param name="iOpenGenericBase">優先偏好的開放泛型 base（typeof(Foo&lt;&gt;)）</param>
    public static Type ResolveTypeByNamePreferSubclassOfGenericDefinition(string iName, Type iOpenGenericBase)
    {
        var hits = ResolveTypesByName(iName);
        if (hits.Count == 0) return null;
        if (iOpenGenericBase != null && iOpenGenericBase.IsGenericTypeDefinition)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].IsSubclassOfGenericTypeDefinition(iOpenGenericBase)) return hits[i];
            }
        }
        return hits[0];
    }

    /// <summary>
    /// 區塊職責：判斷 iType 是否繼承自一個開放泛型型別定義（e.g. UCL_Asset&lt;&gt;）。
    /// 物理意義：標準 IsSubclassOf 不認開放泛型；本方法沿 BaseType chain 比對 GetGenericTypeDefinition。
    /// 數值影響：iType==null 或 iOpenGenericDef==null 或 iOpenGenericDef 不是開放泛型 → 回 false。
    /// </summary>
    /// <param name="iType">待測型別</param>
    /// <param name="iOpenGenericDef">開放泛型定義（typeof(Foo&lt;&gt;)）</param>
    public static bool IsSubclassOfGenericTypeDefinition(this Type iType, Type iOpenGenericDef)
    {
        if (iType == null || iOpenGenericDef == null) return false;
        if (!iOpenGenericDef.IsGenericTypeDefinition) return false;
        for (Type cur = iType; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            if (cur.IsGenericType && cur.GetGenericTypeDefinition() == iOpenGenericDef) return true;
        }
        return false;
    }

    // 區塊職責：「開放泛型 base → 全部具現子型別」cache（abstract / 開放泛型本身排除）
    // 物理意義：UCL_Asset<> 之類的 base，常需列舉所有「可實例化、可載入」的具體子型別。
    // 數值影響：lazy build per iOpenGenericDef；第一次呼叫掃 GetAllTypes() 建子型別清單，之後 O(1) hit。
    private static Dictionary<Type, List<Type>> s_ConcreteSubclassesOfGenericDef = null;

    /// <summary>
    /// 區塊職責：列舉繼承自指定開放泛型 base 的「具現可實例化子型別」(abstract / generic-def 排除)。
    /// 物理意義：常用於 UCL_Asset&lt;&gt; 等 base 的全域 type 掃描；cache 後跨 Cmd 共用。
    /// 數值影響：iOpenGenericDef==null 或非 generic def → 回空 list。回傳是內部 cache 的 reference, 不可修改。
    /// </summary>
    public static IReadOnlyList<Type> GetConcreteSubclassesOfGenericDefinition(Type iOpenGenericDef)
    {
        if (iOpenGenericDef == null || !iOpenGenericDef.IsGenericTypeDefinition)
        {
            return Array.Empty<Type>();
        }
        if (s_ConcreteSubclassesOfGenericDef == null)
        {
            s_ConcreteSubclassesOfGenericDef = new Dictionary<Type, List<Type>>();
        }
        if (!s_ConcreteSubclassesOfGenericDef.TryGetValue(iOpenGenericDef, out var cached))
        {
            cached = new List<Type>();
            var types = GetAllTypes();
            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                if (!t.IsSubclassOfGenericTypeDefinition(iOpenGenericDef)) continue;
                cached.Add(t);
            }
            s_ConcreteSubclassesOfGenericDef[iOpenGenericDef] = cached;
        }
        return cached;
    }
    /// <summary>
    /// 區塊職責：通用「字串 → 目標型別物件」轉換器 — primitive / string / enum / "null" 字面值都吃。
    /// 物理意義：給 Cmd / config / 反射調用等場景把 string args 轉成可餵給 method 的 object。
    /// 數值影響：純轉換；任何失敗 → return false + iErr 描述（不丟例外，呼叫端決定要不要 throw）。
    /// </summary>
    /// <param name="iType">目標型別（method 參數型別 / property type 等）</param>
    /// <param name="iRaw">字串原值；"null"（不分大小寫）視為 null 字面值</param>
    /// <param name="iValue">轉成功後填這裡</param>
    /// <param name="iErr">失敗時填錯誤敘述</param>
    public static bool TryConvertFromString(this Type iType, string iRaw, out object iValue, out string iErr)
    {
        iValue = null;
        iErr = null;
        if (iType == null) { iErr = "target type is null"; return false; }

        // null 字面值 — reference type 與 Nullable<T> 才允許
        if (string.Equals(iRaw, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (iType.IsValueType && Nullable.GetUnderlyingType(iType) == null)
            {
                iErr = $"cannot pass null to value type {iType.FullName}";
                return false;
            }
            iValue = null;
            return true;
        }

        if (iType == typeof(string)) { iValue = iRaw; return true; }

        if (iType.IsEnum)
        {
            try { iValue = Enum.Parse(iType, iRaw, ignoreCase: true); return true; }
            catch (Exception e) { iErr = $"enum parse failed: {e.Message}"; return false; }
        }

        if (iType.IsBool())
        {
            if (bool.TryParse(iRaw, out var b)) { iValue = b; return true; }
            iErr = $"bool parse failed: '{iRaw}'";
            return false;
        }

        // 其餘 primitive / Decimal / DateTime → Convert.ChangeType
        // 物理意義：含 Nullable<T> 時須對 underlying type 做 Convert，再裝箱成 Nullable
        try
        {
            Type underlying = Nullable.GetUnderlyingType(iType) ?? iType;
            iValue = Convert.ChangeType(iRaw, underlying, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e)
        {
            iErr = $"Convert.ChangeType to {iType.FullName} failed: {e.Message}";
            return false;
        }
    }
    private static List<string> s_TypeFullNames = null;
    public static List<string> GetAllTypeFullNames()
    {
        if (s_TypeFullNames == null)
        {
            var types = GetAllTypes();
            s_TypeFullNames = types.Select(type => type.FullName).ToList();
        }
        return s_TypeFullNames;
    }
    private static List<string> s_AllNameSpaces = null;
    public static List<string> GetAllNameSpaces()
    {
        if (s_AllNameSpaces == null)
        {
            var types = GetAllTypes();
            HashSet<string> nameSpaces = new HashSet<string>();
            nameSpaces.Add(string.Empty);//Any namespace
            foreach (var type in types)
            {
                nameSpaces.Add(type.Namespace);
            }
            nameSpaces.Remove(null);
            s_AllNameSpaces = nameSpaces.ToList();
        }
        return s_AllNameSpaces;
    }

    public static IEnumerable<Type> GetAllSubclass(this Type iType)
    {
        var aTypes = GetAllTypes();
        var aSubclassTypes = aTypes.Where(iSubclassType => iSubclassType.IsSubclassOf(iType));
        return aSubclassTypes;
    }
    public static IEnumerable<Type> GetAllITypesAssignableFrom(this Type iType)
    {
        var aTypes = GetAllTypes();
        var aSubclassTypes = aTypes.Where(iSubclassType => iType.IsAssignableFrom(iSubclassType));
        return aSubclassTypes;
    }
    public static MethodInfo[] GetAllMethods(this Type type) {
        var aMethods = new List<MethodInfo>();
        aMethods.AddRange(type.GetMethods());

        var aBaseType = type.BaseType;
        while(aBaseType != null) {
            aMethods.AddRange(aBaseType.GetMethods());
            aBaseType = aBaseType.BaseType;
        }

        return aMethods.ToArray();
    }
    //Primitive Type、Reference Type、Value Type
    //https://dotblogs.com.tw/Mystic_Pieces/2017/10/15/020448
    //All Primitive Type: Boolean, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, IntPtr, UIntPtr, Char, Double, Single
    public static bool IsGenericList(this Type iType)
    {
        return (iType.IsGenericType && (iType.GetGenericTypeDefinition() == typeof(List<>)));
    }
    public static bool IsGenericDictionary(this Type iType)
    {
        return (iType.IsGenericType && (iType.GetGenericTypeDefinition() == typeof(Dictionary<,>)));
    }
    public static bool IsStruct(this Type iType) {
        return iType.IsValueType && !iType.IsPrimitive;
    }
    public static bool IsStructOrClass(this Type iType) {
        return iType.IsStruct() | iType.IsClass;
    }
    public static bool IsString(this Type iType)
    {
        return iType == typeof(string);
    }
    public static bool IsBool(this Type iType)
    {
        return iType == typeof(bool);
    }
    public static bool IsNumericType(this Type iType)
    {
        switch (Type.GetTypeCode(iType))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.Decimal:
            case TypeCode.Double:
            case TypeCode.Single:
                return true;            
        }
        switch (Type.GetTypeCode(Nullable.GetUnderlyingType(iType)))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.Decimal:
            case TypeCode.Double:
            case TypeCode.Single:
                return true;
        }
        return false;
    }
    public static HashSet<Type> NumberTypes
    {
        get
        {
            if (s_NumberTypes == null)
            {
                s_NumberTypes = new HashSet<Type>()
                {
                    typeof(int),
                    typeof(float),
                    typeof(byte),
                    typeof(short),
                    typeof(ushort),
                    typeof(sbyte),
                    typeof(uint),
                    typeof(long),
                    typeof(ulong),
                    typeof(double),
                    typeof(decimal),
                };
            }
            return s_NumberTypes;
        }
    }
    private static HashSet<Type> s_NumberTypes = null;
    //https://stackoverflow.com/questions/4478464/c-sharp-switch-on-type
    /// <summary>
    /// return true if iType is type of number
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static bool IsNumber(this Type iType)
    {
        return NumberTypes.Contains(iType);

        //return iType == typeof(int)
        //    || iType == typeof(float)
        //        || iType == typeof(byte)
        //        || iType == typeof(short)
        //        || iType == typeof(ushort)
        //        || iType == typeof(sbyte)
        //        || iType == typeof(uint)
        //        || iType == typeof(long)
        //        || iType == typeof(ulong)
        //        || iType == typeof(double)
        //        || iType == typeof(decimal);
    }
    /// <summary>
    /// Convert the string to number of type
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iString"></param>
    /// <returns></returns>
    public static object TryParseToNumber(this Type iType, string iString)
    {
        if (iType == typeof(int))
        {
            int aResult;
            if (!int.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(float))
        {
            float aResult;
            if (!float.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(sbyte))
        {
            sbyte aResult;
            if (!sbyte.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(byte))
        {
            byte aResult;
            if (!byte.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(short))
        {
            short aResult;
            if (!short.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(ushort))
        {
            ushort aResult;
            if (!ushort.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(uint))
        {
            uint aResult;
            if (!uint.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(long))
        {
            long aResult;
            if (!long.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(ulong))
        {
            ulong aResult;
            if (!ulong.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(double))
        {
            double aResult;
            if (!double.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(decimal))
        {
            decimal aResult;
            if (!decimal.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }

        return 0;
    }

    public static string GetTypeName(this Type iType)
    {
        if (iType.IsGenericType)
        {
            if (typeof(IList).IsAssignableFrom(iType))
            {
                var aGenericArguments = iType.GetGenericArguments();
                if (!aGenericArguments.IsNullOrEmpty())
                {
                    return $"IList<{iType.GetGenericArguments()[0].Name}>";
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(iType))
            {
                var aGenericArguments = iType.GetGenericArguments();
                if (aGenericArguments.Length >= 2)
                {
                    return $"IDictionary<{aGenericArguments[0].Name},{aGenericArguments[1].Name}>";
                }
            }
            else if (iType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(iType))
            {
                var aGenericType = iType.GetGenericTypeDefinition();
                var aGenericArguments = iType.GetGenericArguments();
                if (aGenericArguments.Length >= 1)
                {
                    return $"{aGenericType.Name}<{aGenericArguments[0].Name}>";
                }
            }
        }
        return iType.Name;
    }
    /// <summary>
    /// return type of a GenericType(List,Array,Dic)
    /// etc. List<T> will return type of T
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static Type GetGenericKeyType(this Type iType)
    {
        var aGenericTypeArguments = iType.GetGenericArguments();
        return aGenericTypeArguments[0];
    }
    /// <summary>
    /// return type of a GenericType(List,Array,Dic)
    /// etc. List<T> will return type of T
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static Type GetGenericValueType(this Type iType)
    {
        if (iType.IsArray)
        {
            return iType.GetElementType();
        }
        var aGenericTypeArguments = iType.GetGenericArguments();//GetTypeInfo().
        if (aGenericTypeArguments.IsNullOrEmpty())
        {
            Debug.LogError($"GetGenericValueType aGenericTypeArguments.IsNullOrEmpty() iType:{iType.FullName}");
            return iType;
        }
        if (typeof(IDictionary).IsAssignableFrom(iType))
        {
            return aGenericTypeArguments[1];//[0] is Key type [1] is Value type!!
        }
        return aGenericTypeArguments[0];
    }
    /// <summary>
    /// Create Instance of input type
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static object CreateInstance(this Type iType)
    {
        if (iType == null) return null;
        if (iType.IsArray) return Array.CreateInstance(iType.GetElementType(), 0);// 陣列型別：建立長度 0 的陣列

        // 基本型別與字串
        switch (Type.GetTypeCode(iType))
        {
            case TypeCode.String: return string.Empty;
            case TypeCode.Int32: return 0;
            case TypeCode.UInt32: return (uint)0;
            case TypeCode.Int64: return (long)0;
            case TypeCode.UInt64: return (ulong)0;
            case TypeCode.Int16: return (short)0;
            case TypeCode.UInt16: return (ushort)0;
            case TypeCode.Byte: return (byte)0;
            case TypeCode.SByte: return (sbyte)0;
            case TypeCode.Single: return 0f;
            case TypeCode.Double: return 0d;
        }

        //if (iType == typeof(System.Type)) return System.Type.Missing;

        if (typeof(UnityEngine.Object).IsAssignableFrom(iType))
        {
            if (typeof(Component).IsAssignableFrom(iType)) return null;
            if (iType == typeof(Sprite)) return null;
            
            Debug.LogError($"CreateInstance iType:{iType.FullName},is UnityEngine.Object!!");
            //return null;
        }
        if (iType.IsTuple())
        {
            //Debug.LogError("iType:" + iType.Name + "iType.IsTuple():" + iType.IsTuple());
            Type[] aTypeArray = iType.GetGenericArguments();

            var aConstructer = iType.GetConstructor(aTypeArray);
            if (aConstructer != null)
            {
                object[] aValues = new object[aTypeArray.Length];
                for (int i = 0; i < aTypeArray.Length; i++)
                {
                    aValues[i] = aTypeArray[i].CreateInstance();
                }
                return aConstructer.Invoke(aValues);
            }
        }
        object aObj = null;
        try
        {
            aObj = Activator.CreateInstance(iType);
        }catch(System.Exception iE)
        {
            Debug.LogException(iE);
            Debug.LogError($"CreateInstance Type:{iType.FullName},Exception:{iE}");
        }
        return aObj;
    }
    private static HashSet<Type> s_TupleTypes = null;
    /// <summary>
    /// return true if iType is tuple
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static bool IsTuple(this Type iType)
    {
        if (iType == null) return false;
        if (!iType.IsGenericType) return false;
        if(s_TupleTypes == null)
        {
            s_TupleTypes = new HashSet<Type>(){ 
                 //typeof(ValueTuple<>), typeof(ValueTuple<,>),
                 //typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
                 //typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>),
                 //typeof(ValueTuple<,,,,,,>), typeof(ValueTuple<,,,,,,,>),
                 typeof(Tuple<>), typeof(Tuple<,>),
                 typeof(Tuple<,,>), typeof(Tuple<,,,>),
                 typeof(Tuple<,,,,>), typeof(Tuple<,,,,,>),
                 typeof(Tuple<,,,,,,>), typeof(Tuple<,,,,,,,>)

            };
        }
        var aGenType = iType.GetGenericTypeDefinition();
        //Debug.LogError("aGenType:" + aGenType.Name+ ",iType:"+ iType.Name);
        return s_TupleTypes.Contains(aGenType);
    }
    public static bool IsHashSet(this Type iType)
    {
        if (iType == null) return false;

        return iType.IsGenericType && iType.GetGenericTypeDefinition() == typeof(HashSet<>);
    }

    private static Dictionary<(Type, BindingFlags), List<FieldInfo>> s_FieldsCache = new();

    public static List<FieldInfo> GetAllFieldsWithCache(this Type iType, BindingFlags iBindingAttr) 
    {
        var key = (iType, iBindingAttr);
        if (!s_FieldsCache.ContainsKey(key))
        {
            s_FieldsCache[key] = GetAllFields(iType, iBindingAttr);
        }
        return s_FieldsCache[key];
    }
    /// <summary>
    /// Get Fields Include parent
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iBindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFields(this Type iType, BindingFlags iBindingAttr) {
        FieldInfo[] aFields = iType.GetFields(iBindingAttr);
        List<FieldInfo> aList = new List<FieldInfo>();//aFields.ToList();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        for (int i = 0; i < aFields.Length; i++)
        {
            var aField = aFields[i];
            aFieldNameHash.Add(aField.Name);
            aList.Add(aField);
        }
        
        var aBaseType = iType.BaseType;
        
        if(aBaseType != null) {
            if((iBindingAttr & BindingFlags.Public) != 0) {
                iBindingAttr ^= BindingFlags.Public;
            }
            var aList2 = aBaseType.GetAllFields(iBindingAttr);
            for(int i = 0; i < aList2.Count; i++) {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }
    /// <summary>
    /// Get Fields Include parent, until parent is iEndType
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iEndType"></param>
    /// <param name="iBindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFieldsUntil(this Type iType, Type iEndType, BindingFlags iBindingAttr) {
        FieldInfo[] aFields = iType.GetFields(iBindingAttr);
        List<FieldInfo> aList = new List<FieldInfo>();//aFields.ToList();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        for (int i = 0; i < aFields.Length; i++)
        {
            var aField = aFields[i];
            aFieldNameHash.Add(aField.Name);
            aList.Add(aField);
        }

        var aBaseType = iType.BaseType;

        if (aBaseType != null && aBaseType != iEndType)
        {
            if ((iBindingAttr & BindingFlags.Public) != 0)
            {
                iBindingAttr ^= BindingFlags.Public;
            }
            var aList2 = aBaseType.GetAllFieldsUntil(iEndType, iBindingAttr);
            for (int i = 0; i < aList2.Count; i++)
            {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }

    /// <summary>
    /// Get Fields Include parent, until parent is iEndType
    /// Ignore Fields with [HideInInspector] 
    /// Ignore NonPublic Fields whithout[SerializeField]
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iEndType"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFieldsUnityVer(this Type iType, Type iEndType = null, int aLayer = 0)
    {
        List<FieldInfo> aList = new List<FieldInfo>();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        if (aLayer == 0)//get Public field of root type
        {
            FieldInfo[] aPublicFields = iType.GetFields(BindingFlags.Instance | BindingFlags.Public);
            
            for (int i = 0; i < aPublicFields.Length; i++)
            {
                var aField = aPublicFields[i];
                if (aField.GetCustomAttribute<HideInInspector>() == null)
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        {//get NonPublicFields
            FieldInfo[] aNonPublicFields = iType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < aNonPublicFields.Length; i++)
            {
                var aField = aNonPublicFields[i];
                if (aField.GetCustomAttribute<SerializeField>() != null)//SerializeField Only!!
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }

        var aBaseType = iType.BaseType;

        if (aBaseType != null && aBaseType != iEndType)
        {
            var aList2 = aBaseType.GetAllFieldsUnityVer(iEndType, aLayer + 1);
            for (int i = 0; i < aList2.Count; i++)
            {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }
}
