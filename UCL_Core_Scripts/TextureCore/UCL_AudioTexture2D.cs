using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    public class UCL_AudioTexture2D : UCL_Texture2D {
        public UCL_AudioTexture2D(Vector2Int _size, TextureFormat _TextureFormat = TextureFormat.ARGB32) 
            : base(_size, _TextureFormat) {
            Init();
        }
        virtual public void SetData(float[] data) {
            int m_DataLen = data.Length;
            int seg = data.Length / width;
            for(int i = 0; i < width; i++) {
                float val = 0;
                int end = i * (seg + 1);
                if(end > m_DataLen) end = m_DataLen;
                for(int j = i * seg; j < end; j++) {
                    val += Mathf.Abs(data[j]);
                }
                int h = Mathf.RoundToInt((val * height) / seg);
            }
            /*
            for(int j = 0; j < m_PlayTexture.width; j++) {
                for(int i = 0; i < m_PlayTexture.height; i++) {
                    m_PlayTexture.SetPixel(j, i, m_DrawData[j] < i ? Color.black : Color.yellow);
                }
            }
            */
        }
    }
}