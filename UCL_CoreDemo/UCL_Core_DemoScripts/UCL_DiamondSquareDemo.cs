using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_DiamondSquareDemo : MonoBehaviour {
        public int m_Seed = 0;
        public bool m_RandBySeed = false;

        public List<float> m_Result;
        public int m_Size = 5;
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
            MathLib.Lib.DiamondSquare(Arr, m_Size, rnd);

            m_Result = new List<float>();
            for(int i = 0; i < m_Size; i++) {
                for(int j = 0; j < m_Size; j++) {
                    m_Result.Add(Arr[j,i]);
                }
            }
            m_Texture = null;
        }
        Core.TextureLib.UCL_Texture2D m_Texture;
        [ATTR.UCL_DrawTexture2D]
        public Core.TextureLib.UCL_Texture2D ResultTexture() {
            int xval = 25;
            if(m_Texture == null) {
                m_Texture = new TextureLib.UCL_Texture2D(new Vector2Int(xval*m_Size, xval*m_Size),TextureFormat.RGB24);
            }
            if(m_Result.Count >= m_Size * m_Size) {
                for(int i = 0; i < m_Size; i++) {
                    for(int j = 0; j < m_Size; j++) {
                        float val = m_Result[j + i * m_Size];
                        var col = new Color(val, val, val);
                        for(int x = 0; x < xval; x++) {
                            for(int y = 0; y < xval; y++) {
                                m_Texture.SetPixel(j*xval+x, i*xval+y, col);
                            }
                        }
                        
                    }
                }
            }

            return m_Texture;
        }
    }
}