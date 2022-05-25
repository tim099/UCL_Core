using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UCL.Core.ATTR
{
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawOnGUIAttribute : UCL_Attribute
    {
        public UCL_DrawOnGUIAttribute() { m_Params = null; }
        public UCL_DrawOnGUIAttribute(params object[] iParams)
        {
            m_Params = iParams;
        }
        object[] m_Params = null;
        public override void Draw(object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic)
        {
            try
            {
                var aParams = iMethodInfo.GetParameters();

                if (m_Params != null)
                {
                    if (aParams.Length == m_Params.Length)
                    {
                        iMethodInfo.Invoke(iTarget, m_Params);
                    }
                }
                else
                {
                    if (aParams.Length == 0)
                    {
                        iMethodInfo.Invoke(iTarget, null);
                    }else if(aParams.Length == 1 && aParams[0].ParameterType == typeof(UCL_ObjectDictionary))
                    {
                        iMethodInfo.Invoke(iTarget, new object[] { iDic });
                    }
                }

            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }
        }
    }
}