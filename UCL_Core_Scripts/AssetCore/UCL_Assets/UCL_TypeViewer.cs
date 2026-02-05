
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core.PA;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Assembly)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.AssemblyDataType.UCL_TypeViewer)]
    public class UCL_TypeViewer : UCL_Asset<UCL_TypeViewer>
    {
        public UCL_TypeSelector m_TypeSelector = new();


        private UCL_TypeInfoCache cache = null;
        public override void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            base.OnGUI(iDataDic);
            var type = m_TypeSelector.TargetType;
            
            if(type == null) 
            {
                GUILayout.Label("type == null", UCL_GUIStyle.LabelStyle);
                return;
            }
            if(cache != null && cache.m_TargetType != type)
            {
                cache = null;
            }
            if(cache == null)
            {
                cache = new UCL_TypeInfoCache(type);
            }

            GUILayout.Label($"Type Viewer:{type.FullName}", UCL_GUIStyle.LabelStyle);

            

            cache.m_MethodInfoCache.OnGUI(iDataDic, "Method");
            cache.m_StaticMethodInfoCache.OnGUI(iDataDic, "StaticMethod");

            if (cache.m_Target != null)
            {
                UCL_GUILayout.DrawObjectData(cache.m_Target, iDataDic.GetSubDic("Instance"), "Instance");
            }
        }
        int test = 0;
        public string Test()
        {
            ++test;
            string result = $"Test:{test}";
            //Debug.LogError(result);
            return result;
        }
        public static string Test2()
        {
            string result = $"Test 2";
            //Debug.LogError(result);
            return result;
        }
        public static string Test3(string input)
        {
            string result = $"Test 3 input:{input}";
            //Debug.LogError(result);
            return result;
        }
    }

    [System.Serializable]
    public class UCL_TypeSelector : UCLI_ShortName
    {
        public enum SelectionMode
        {
            UserDefined,
            /// <summary>
            /// int, string, float, double, bool, object, decimal...
            /// </summary>
            Primitive,
        }

        public enum PrimitiveType
        {
            Int,
            String,
            Float,
            Double,
            Bool,
            Object,
            Decimal,
        }

        public SelectionMode m_Mode = SelectionMode.UserDefined;
        [ShowIfField(nameof(m_Mode), SelectionMode.Primitive)]
        public PrimitiveType m_SelectedPrimitive = PrimitiveType.Int;

        [ShowIfField(nameof(m_Mode), SelectionMode.UserDefined)]
        [UCL.Core.PA.UCL_ValueDropdown(typeof(AssemblyExtensions), nameof(AssemblyExtensions.GetAllTypeFullNames))]
        public string m_TypeName;

        public string GetShortName() => ToString();

        public override string ToString()
        {
            return m_Mode == SelectionMode.Primitive
                ? m_SelectedPrimitive.ToString()
                : m_TypeName;
        }

        public Type TargetType
        {
            get
            {
                if (m_Mode == SelectionMode.Primitive)
                {
                    switch (m_SelectedPrimitive)
                    {
                        case PrimitiveType.Int: return typeof(int);
                        case PrimitiveType.String: return typeof(string);
                        case PrimitiveType.Float: return typeof(float);
                        case PrimitiveType.Double: return typeof(double);
                        case PrimitiveType.Bool: return typeof(bool);
                        case PrimitiveType.Object: return typeof(object);
                        case PrimitiveType.Decimal: return typeof(decimal);
                        default: return null;
                    }
                }
                else
                {
                    return AssemblyExtensions.GetTypeByFullName(m_TypeName);
                }
            }
        }

        public UCL_TypeSelector() { }

        public UCL_TypeSelector(PrimitiveType primitiveType)
        {
            m_Mode = SelectionMode.Primitive;
            m_SelectedPrimitive = primitiveType;
        }
    }
    public class UCL_MethodInfoCache
    {
        public MethodInfo[] m_MethodInfos;
        public Dictionary<string, MethodInfo> m_MethodInfoDic = new Dictionary<string, MethodInfo>();
        public List<string> m_MethodNames = new List<string>();


        public UCL_TypeInfoCache Cache { get;private set; }
        public string SelectedMethodName
        {
            get => m_SelectedMethodName;
            set
            {
                if (m_SelectedMethodName != value)
                {
                    Debug.LogWarning($"Refresh m_SelectedMethodName:{m_SelectedMethodName},value:{value}");
                    m_SelectedMethodName = value;
                    SelectedMethod = m_MethodInfoDic.TryGetValue(m_SelectedMethodName, out var methodInfo) ? methodInfo : null;

                    ParameterValues.Clear();
                    if (SelectedMethod != null)
                    {
                        Parameters = SelectedMethod.GetParameters();
                        if (!Parameters.IsNullOrEmpty())
                        {
                            foreach (var param in Parameters)
                            {
                                ParameterValues[param] = param.ParameterType.CreateInstance();
                            }
                        }
                    }
                }

            }
        }
        public MethodInfo SelectedMethod { get; private set; }
        public ParameterInfo[] Parameters { get; private set; }
        public Dictionary<ParameterInfo, object> ParameterValues = new();

        private string m_SelectedMethodName;

        public UCL_MethodInfoCache(Type targetType, UCL_TypeInfoCache cache, BindingFlags flags)
        {
            Cache = cache;
            m_MethodInfos = targetType.GetMethods(flags);
            foreach (var method in m_MethodInfos)
            {
                var methodSignature = method.GetSignature();
                m_MethodNames.Add(methodSignature);
                m_MethodInfoDic[methodSignature] = method;
            }
        }

        public void OnGUI(UCL_ObjectDictionary iDataDic, string name)
        {
            var dic = iDataDic.GetSubDic(name);
            GUILayout.BeginHorizontal();
            bool show = UCL_GUILayout.Toggle(dic, "Show");
            using (new GUILayout.VerticalScope())
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(name, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    SelectedMethodName = UCL_GUILayout.PopupAuto(SelectedMethodName, m_MethodNames, dic, "MethodListPopup");
                }
                if (show)
                {
                    var methodInfo = SelectedMethod;
                    if (methodInfo != null)
                        using (new GUILayout.VerticalScope())
                        {
                            var parameters = Parameters;

                            if (parameters.Length > 0)
                            {
                                int index = 0;
                                foreach (var param in parameters)
                                {
                                    var paramDic = dic.GetSubDic($"{++index}.{SelectedMethodName}.{param.Name}");
                                    //GUILayout.BeginHorizontal();
                                    var currentValue = ParameterValues[param];

                                    GUILayout.Label($"{index}. {param.ParameterType.Name} {param.Name}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                                    ParameterValues[param] = UCL_GUILayout.DrawObjectData(currentValue, paramDic,
                                        $"{currentValue.GetType().Name} {param.Name}");
                                    //GUILayout.EndHorizontal();
                                }
                            }

                            if (GUILayout.Button("Invoke", UCL_GUIStyle.ButtonStyle))
                            {
                                object result = null;
                                if (parameters.Length > 0)
                                {
                                    if (parameters.Length != ParameterValues.Count)
                                    {
                                        Debug.LogError($"parameters.Length:{parameters.Length},ParameterValues.Count:{ParameterValues.Count}");
                                    }
                                    else
                                    {
                                        result = methodInfo.Invoke(Cache.m_Target, ParameterValues.Values.ToArray());
                                    }


                                }
                                else
                                {
                                    result = methodInfo.Invoke(Cache.m_Target, null);
                                }
                                if (result != null)
                                {
                                    Debug.Log($"Result:{result}");
                                }
                            }
                        }
                }
            }
            GUILayout.EndHorizontal();
        }
    }
    public class UCL_TypeInfoCache
    {
        public Type m_TargetType;
        public object m_Target;

        public UCL_MethodInfoCache m_MethodInfoCache;
        public UCL_MethodInfoCache m_StaticMethodInfoCache;

        public UCL_TypeInfoCache() { }
        public UCL_TypeInfoCache(Type targetType) 
        {
            m_TargetType = targetType;
            if (!m_TargetType.IsAbstract)
            {
                try
                {
                    m_Target = Activator.CreateInstance(targetType);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Debug.LogError($"UCL_TypeInfoCache Activator.CreateInstance Exception:{e.ToString()}");
                }
            }
            m_MethodInfoCache = new UCL_MethodInfoCache(targetType, this, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            m_StaticMethodInfoCache = new UCL_MethodInfoCache(targetType, this, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }
    }
    public static partial class MethodExtensions
    {
        public static string GetSignature(this MethodInfo methodInfo)
        {
            var parameters = methodInfo.GetParameters();
            var parameterSignatures = parameters.Select(p => $"{p.ParameterType.GetTypeName()} {p.Name}");
            return $"{methodInfo.ReturnType.GetTypeName()} {methodInfo.Name} ({string.Join(", ", parameterSignatures)})";
        }
    }
}
