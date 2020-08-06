using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    [ATTR.EnableUCLEditor]
    public class UCL_TextureEditor : MonoBehaviour {
        public Texture2D m_Texture;
        public string m_OutputFolder = "Assets/Textures";
        public string m_SaveName = "NewTexture";
        public Vector2Int m_Split = Vector2Int.one;
        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;
        protected string GetOutputPath() {
            return System.IO.Path.Combine(m_OutputFolder, m_SaveName);
        }
        [ATTR.UCL_FunctionButton]
        public void SplitTexture() {
            if(m_Split.x == 0 || m_Split.y == 0) return;
            if(m_Split == Vector2Int.one) {
                SavePNG();
                return;
            }
            int width = m_Texture.width / m_Split.x;
            int height = m_Texture.height / m_Split.y;
            var texture = new Texture2D(width, height, m_TextureFormat, false);
            for(int i = 0; i < m_Split.y; i++) {
                //Debug.LogWarning("SplitTexture:" + i);
                for(int j = 0; j < m_Split.x; j++) {
                    //Debug.LogWarning("SplitTexture:" + j+","+i);
                    int sx = (m_Texture.width * j) / m_Split.x;
                    int sy = (m_Texture.height * i) / m_Split.y;
                    //Debug.LogWarning("sy:" + sy);
                    for(int y = 0; y < height; y++) {
                        for(int x = 0; x < width; x++) {
                            var col = m_Texture.GetPixel(sx + x, sy + y);
                            texture.SetPixel(x, y, col);
                        }
                    }
                    TextureLib.Lib.SavePNG(GetOutputPath() + "_" + j + "_" + i, texture);
                }
            }
        }



        public void SavePNG(Texture2D _Texture) {
            TextureLib.Lib.SavePNG(GetOutputPath(), _Texture);
        }
        public void SavePNG() {
            SavePNG(m_Texture);
        }
    }
}