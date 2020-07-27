using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_DiamondSquareDemo : MonoBehaviour {
        public int m_Seed = 0;
        public bool m_RandBySeed = false;

        [HideInInspector] public List<float> m_Result;
        [Range(1,8)]public int m_SizeSlider = 1;
        [UCL.Core.PA.UCL_ReadOnly] public int m_Size = 5;
        [ATTR.UCL_FunctionButton]
        public void GenResult() {
            float[,] Arr = new float[m_Size, m_Size];
            var rnd = MathLib.UCL_Random.Instance;
            if(m_RandBySeed) {
                rnd = new MathLib.UCL_Random(m_Seed);
            }
            int len = m_Size - 1;
            Arr[0, 0] = rnd.Range(0, 1f);
            Arr[len, 0] = rnd.Range(0, 1f);
            Arr[len, len] = rnd.Range(0, 1f);
            Arr[0, len] = rnd.Range(0, 1f);
            MathLib.Lib.DiamondSquare(Arr, rnd);

            m_Result = new List<float>();
            for(int i = 0; i < m_Size; i++) {
                for(int j = 0; j < m_Size; j++) {
                    m_Result.Add(Arr[j,i]);
                }
            }
            //m_Texture = null;
        }
        private void OnValidate() {
            m_Size = Core.MathLib.Lib.PowTwo(m_SizeSlider)+1;
        }
        public int m_PixelSize = 8;
        Core.TextureLib.UCL_Texture2D m_Texture;
        [ATTR.UCL_DrawTexture2D]
        public Core.TextureLib.UCL_Texture2D ResultTexture() {
            //int m_PixelSize = 25;
            if(m_Texture == null || m_Texture.size.x != m_PixelSize * m_Size) {
                m_Texture = new TextureLib.UCL_Texture2D(new Vector2Int(m_PixelSize*m_Size, m_PixelSize*m_Size),TextureFormat.RGB24);
            }
            if(m_Result.Count >= m_Size * m_Size) {
                for(int i = 0; i < m_Size; i++) {
                    for(int j = 0; j < m_Size; j++) {
                        float val = m_Result[j + i * m_Size];
                        var col = new Color(val, val, val);
                        for(int x = 0; x < m_PixelSize; x++) {
                            for(int y = 0; y < m_PixelSize; y++) {
                                m_Texture.SetPixel(j*m_PixelSize+x, i*m_PixelSize+y, col);
                            }
                        }
                        
                    }
                }
            }

            return m_Texture;
        }
    }
}