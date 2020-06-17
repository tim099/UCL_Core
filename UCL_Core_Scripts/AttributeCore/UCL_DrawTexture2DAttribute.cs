using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawTexture2DAttribute : Attribute {
        Vector2Int m_Size;
        Core.TextureLib.UCL_Texture2D m_Texture;
        TextureFormat m_TextureFormat;
        string m_Type;
        public UCL_DrawTexture2DAttribute(int size_x, int size_y, TextureFormat _TextureFormat = TextureFormat.ARGB32, string type = "") {
            //Debug.LogWarning("UCL_DrawTexture2DAttribute:" + size_x + "," + size_y);
            m_Size = new Vector2Int(size_x, size_y);
            m_TextureFormat = _TextureFormat;
            m_Type = type;
        }
        public TextureLib.UCL_Texture2D GetTexture() {
            if(m_Texture == null) {
                m_Texture = TextureLib.UCL_Texture2D.CreateTexture(m_Size, m_TextureFormat, m_Type);
            }
            return m_Texture;
        }
        ~UCL_DrawTexture2DAttribute() {
            //Debug.LogWarning("Destruct UCL_DrawTexture2DAttribute");
        }
    }
}