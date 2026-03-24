
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core.JsonLib;
using UCL.Core.PA;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    public interface UCLI_Scope : UCLI_TypeListable
    {
        UCLI_Scope Parent { get; set; }
        void Trigger();
        object GetVariable(string name);
        void DeclareVariable(string name, object val);
    }

    public abstract class UCL_ScopeBase : JsonLib.UnityJsonSerializable, UCLI_Scope, UCLI_ShortName, UCLI_FieldOnGUI
    {
        public UCLI_Scope Parent { get; set; }

        virtual public string GetShortName() => this.ToString();
        virtual public object GetVariable(string name)
        {
            if (Parent != null)
            {
                return Parent.GetVariable(name);
            }
            return null;
        }
        virtual public void DeclareVariable(string name, object val)
        {

        }
        virtual public void Trigger() { }

        virtual public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            return UCL_GUILayout.DrawField(this, iParams);
        }
    }

    public class UCL_Scope : UCL_ScopeBase
    {
        [SerializeReference] public List<UCLI_Scope> m_Scopes = new();

        private Dictionary<string, object> m_Variables = new();

        public override object GetVariable(string name)
        {
            if (m_Variables.TryGetValue(name, out object value))
            {
                return value;
            }
            if (Parent != null)
            {
                return Parent.GetVariable(name);
            }
            return null;
        }
        public override void DeclareVariable(string name, object val)
        {
            m_Variables[name] = val;
        }
        public override string ToString()
        {
            return "{" + m_Scopes.ConcatToString() + "}";
        }
        public override void Trigger()
        {
            for (int i = 0; i < m_Scopes.Count; i++)
            {
                var scope = m_Scopes[i];
                scope.Parent = this;
                scope.Trigger();
            }
        }

    }


    public class UCL_CallStaticFunction : UCL_ScopeBase
    {
        public UCL_TypeSelector m_TypeSelector = new();

        public List<UCLI_ValueSource> m_Parameters = new();

        public IEnumerable<string> MethodNames
        {
            get
            {
                Refresh();
                return m_MethodInfoDic.Keys;
            }
        }


        /// <summary>
        /// 要呼叫的Function
        /// </summary>
        [UCL_ValueDropdown(nameof(MethodNames))]
        public string m_Function;

        private Type m_Type = null;
        private Dictionary<string, MethodInfo> m_MethodInfoDic = new();
        private string m_CachedFunction = null;
        public override string ToString()
        {
            return $"{m_TypeSelector}({m_Parameters.ConcatToString()})";
        }
        private void Refresh()
        {
            var type = m_TypeSelector.TargetType;
            if (m_Type == type)
            {
                return;
            }
            m_Type = type;
            //m_Parameters.Clear();
            m_MethodInfoDic.Clear();
            if (m_Type != null)
            {
                var methodInfos = m_Type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (var method in methodInfos)
                {
                    var methodSignature = method.GetSignature();
                    //m_MethodNames.Add(methodSignature);
                    m_MethodInfoDic[methodSignature] = method;
                }
                //Debug.LogError($"{m_Type.FullName} get MethodNames:{m_MethodNames.ConcatToString()}");
            }
        }



        public override object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            Refresh();
            return base.OnGUI(iFieldName, iDataDic, iParams);
        }

        public override void Trigger()
        {
            Refresh();
            if (m_Type == null) return;
            if (m_MethodInfoDic.TryGetValue(m_Function, out var method))
            {
                //var method = m_MethodInfoDic[m_Function];
                
                if (m_Parameters.IsNullOrEmpty())
                {
                    Debug.LogWarning($"CallStaticFunction:{m_Function}");
                    method.Invoke(null, null);
                }
                else
                {
                    var aParams = m_Parameters.Select(a => a.GetValue(Parent)).ToArray();
                    Debug.LogWarning($"CallStaticFunction:{m_Function}, Params:{aParams.ConcatToString()}");
                    method.Invoke(null, aParams);
                }
                
            }


        }

    }

    public class UCL_DeclareVariable : UCL_ScopeBase
    {
        public UCL_TypeSelector m_Type = new();
        public string m_Name;

        const string KeyDefaultValue = "DefaultValue";
        private object m_DefaultValue;
        public override string ToString()
        {
            return $"{m_Type} {m_Name} = {m_DefaultValue}";
        }
        public override JsonData SerializeToJson()
        {

            var result = base.SerializeToJson();
            result[KeyDefaultValue] = new JsonData(m_DefaultValue);
            return result;
        }
        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            m_DefaultValue = iJson.Get(KeyDefaultValue).GetValue(m_Type.TargetType);
        }
        public override object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            iParams.m_DrawObjExSetting = new();
            iParams.m_DrawObjExSetting.OnShowField = () =>
            {
                var type = m_Type.TargetType;
                if (type != null)
                {
                    if (m_DefaultValue != null && m_DefaultValue.GetType() != type)
                    {
                        m_DefaultValue = null;
                    }

                    if (m_DefaultValue == null)
                    {
                        m_DefaultValue = type.CreateInstance();
                    }
                }
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.String:
                        {
                            m_DefaultValue = UCL_GUILayout.TextArea("Default Value", m_DefaultValue as string);
                            break;
                        }
                    case TypeCode.Int32:
                        {
                            m_DefaultValue = UCL_GUILayout.IntField("Default Value", (int)m_DefaultValue);
                            break;
                        }
                    case TypeCode.Single:
                        {
                            m_DefaultValue = UCL_GUILayout.FloatField("Default Value", (float)m_DefaultValue);
                            break;
                        }
                    default:
                        {
                            m_DefaultValue = UCL_GUILayout.DrawObjectData(m_DefaultValue, iParams.CreateChild());
                            break;
                        }
                        //case TypeCode.Int32: return 0;
                        //case TypeCode.UInt32: return (uint)0;
                        //case TypeCode.Int64: return (long)0;
                        //case TypeCode.UInt64: return (ulong)0;
                        //case TypeCode.Int16: return (short)0;
                        //case TypeCode.UInt16: return (ushort)0;
                        //case TypeCode.Byte: return (byte)0;
                        //case TypeCode.SByte: return (sbyte)0;
                        //case TypeCode.Single: return 0f;
                        //case TypeCode.Double: return 0d;
                }
            };
            return base.OnGUI(iFieldName, iDataDic, iParams);
        }

        public override void Trigger()
        {
            var type = m_Type.TargetType;
            object val = m_DefaultValue;
            if (val == null)
            {
                val = type.CreateInstance();
            }
            //Debug.LogError($"DeclareVariable type:{type}, val:{val}");
            Parent.DeclareVariable(m_Name, val);
        }

    }
}
