
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Editor)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditEditorType.UCL_TypeViewer)]
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

            GUILayout.BeginHorizontal();

            bool show = UCL_GUILayout.Toggle(iDataDic, "Methods");
            using (new GUILayout.VerticalScope())
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Method", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    cache.SelectedMethodName = UCL_GUILayout.PopupAuto(cache.SelectedMethodName, cache.m_MethodNames, iDataDic, "MethodListPopup");
                }
                if (show)
                {
                    var methodInfo = cache.SelectedMethod;
                    if (methodInfo != null)
                        using (new GUILayout.VerticalScope())
                        {
                            var parameters = cache.Parameters;

                            if(parameters.Length > 0)
                            {
                                foreach(var param in parameters)
                                {
                                    var dic = iDataDic.GetSubDic($"{cache.SelectedMethodName}.{param.Name}");
                                    GUILayout.BeginHorizontal();
                                    var currentValue = cache.ParameterValues[param];
                                    GUILayout.Label($"{param.ParameterType.Name} {param.Name}:", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                    
                                    cache.ParameterValues[param] = UCL_GUILayout.DrawObjectData(currentValue, dic, 
                                        $"{currentValue.GetType().Name} {param.Name}");
                                    GUILayout.EndHorizontal();
                                }
                            }

                            if (GUILayout.Button("Invoke", UCL_GUIStyle.ButtonStyle))
                            {
                                object result = null;
                                if (parameters.Length > 0)
                                {
                                    if(parameters.Length != cache.ParameterValues.Count)
                                    {
                                        Debug.LogError($"parameters.Length:{parameters.Length},ParameterValues.Count:{cache.ParameterValues.Count}");
                                    }
                                    else
                                    {
                                        result = methodInfo.Invoke(cache.m_Target, cache.ParameterValues.Values.ToArray());
                                    }
                                    
                                    
                                }
                                else
                                {
                                    result = methodInfo.Invoke(cache.m_Target, null);
                                }
                                if(result != null)
                                {
                                    Debug.Log($"Result:{result}");
                                }
                            }
                        }
                }
            }


            GUILayout.EndHorizontal();

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
        /// <summary>
        /// 
        /// </summary>
        [UCL.Core.PA.UCL_ValueDropdown(typeof(AssemblyExtensions),nameof(AssemblyExtensions.GetAllTypeFullNames))]
        public string m_TypeName;

        public string GetShortName() => ToString();
        public override string ToString() => m_TypeName;

        public Type TargetType => AssemblyExtensions.GetTypeByFullName(m_TypeName);//System.Type.GetType(m_TypeName);
    }

    public class UCL_TypeInfoCache
    {
        public Type m_TargetType;
        public object m_Target;
        public MethodInfo[] m_MethodInfos;
        public Dictionary<string, MethodInfo> m_MethodInfoDic = new Dictionary<string, MethodInfo>();
        public List<string> m_MethodNames = new List<string>();



        public string SelectedMethodName
        {
            get => m_SelectedMethodName;
            set {
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


            m_MethodInfos = targetType.GetMethods();
            foreach (var method in m_MethodInfos)
            {
                var methodSignature = method.GetSignature();
                m_MethodNames.Add(methodSignature);
                m_MethodInfoDic[methodSignature] = method;
            }
        }
    }
    public static partial class MethodExtensions
    {
        public static string GetSignature(this MethodInfo methodInfo)
        {
            var parameters = methodInfo.GetParameters();
            var parameterSignatures = parameters.Select(p => $"{p.ParameterType.Name} {p.Name}");
            return $"{methodInfo.ReturnType.Name} {methodInfo.Name} ({string.Join(", ", parameterSignatures)})";
        }
    }
}
