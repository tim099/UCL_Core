using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.PA
{
    public class UCL_PA_DrawTexture2DAttribute : PropertyAttribute, ITexture2D
    {
        public Type m_Type = null;
        public string m_FuncName = string.Empty;
        public UCL_PA_DrawTexture2DAttribute() { }
        public UCL_PA_DrawTexture2DAttribute(string iFuncName)
        {
            m_FuncName = iFuncName;
        }

        public Texture2D GetTexture(object iObj, object iValue)
        {
            if (iValue is Texture2D && string.IsNullOrEmpty(m_FuncName)) return iValue as Texture2D;
            if (m_Type == null)
            {
                m_Type = iObj.GetType();
            }
            var aMethod = m_Type.GetMethod(m_FuncName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                 | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
            //, types: new Type[1] { typeof(Texture2D)}
            if (aMethod != null)
            {
                var aResult = aMethod.Invoke(iObj, null);
                return aResult as Texture2D;
            }
            return null;
        }
    }
}