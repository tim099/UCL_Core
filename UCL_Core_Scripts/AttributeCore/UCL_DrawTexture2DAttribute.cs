using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UCL.Core.ATTR {
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawTexture2DAttribute : UCL_Attribute
    {
        Vector2Int m_Size;
        Core.TextureLib.UCL_Texture2D m_Texture;
        TextureFormat m_TextureFormat;
        Type m_Type;
        //static int m_Count = 0;
        public UCL_DrawTexture2DAttribute() {
            m_Size = Vector2Int.one;
        }
        public UCL_DrawTexture2DAttribute(int iSizeX, int iSizeY, TextureFormat iTextureFormat = TextureFormat.ARGB32, Type iType = null) {
            //m_Count++;
            //Debug.LogWarning("UCL_DrawTexture2DAttribute:" + size_x + "," + size_y);
            m_Size = new Vector2Int(iSizeX, iSizeY);
            m_TextureFormat = iTextureFormat;
            if(iType == null) {
                iType = typeof(TextureLib.UCL_Texture2D);
            }
            m_Type = iType;
        }
        public TextureLib.UCL_Texture2D GetTexture() {
            if(m_Texture == null) {
                m_Texture = TextureLib.UCL_Texture2D.CreateTexture(m_Size, m_TextureFormat, m_Type);
            }
            return m_Texture;
        }
        public override void DrawAttribute(UnityEngine.Object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic)
        {
            GUILayout.Box(iMethodInfo.Name);
            var aReturnType = iMethodInfo.ReturnType;
            TextureLib.UCL_Texture2D aTex = null;
            if (aReturnType.IsAssignableFrom(typeof(TextureLib.UCL_Texture2D)))
            {//IsSubclassOf
                aTex = iMethodInfo.Invoke(iTarget, null) as TextureLib.UCL_Texture2D; 
            }
            else
            {
                aTex = GetTexture();
                iMethodInfo.Invoke(iTarget, new object[] { aTex });
            }
            if (aTex != null) GUILayout.Box(aTex.texture);
        }
        ~UCL_DrawTexture2DAttribute() {
            //m_Count--;
            //Debug.LogWarning("m_Count:"+ m_Count);
            if(m_Texture != null) {
                m_Texture.ClearTexture();
            }
            //Debug.LogWarning("Destruct UCL_DrawTexture2DAttribute");
        }
    }
}