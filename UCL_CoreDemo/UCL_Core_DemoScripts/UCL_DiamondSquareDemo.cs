using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_DiamondSquareDemo : MonoBehaviour {
        public int m_Seed = 0;
        public bool m_RandBySeed = false;

        [HideInInspector] public List<float> m_Result;
        [Range(1, 8)] public int m_SizeSlider = 1;
        [Range(1, 8)] public int m_Seg = 1;
        [UCL.Core.PA.UCL_ReadOnly] public int m_Size = 5;
        [Range(0.01f, 1f)] public float m_NoiseScale = 0.1f;
        public bool m_DoDiamond = true;
        [ATTR.UCL_FunctionButton]
        public void GenResult() {
            int len = m_Size - 1;
            int seg = MathLib.Lib.PowTwo(m_Seg-1);

            int size = m_Size;//(len * m_Seg) + 1;
            float[,] Arr = new float[size, size];
            var rnd = MathLib.UCL_Random.Instance;
            if(m_RandBySeed) {
                rnd = new MathLib.UCL_Random(m_Seed);
            }
            int seg_len = len / seg;
            float sx = rnd.Range(-99f, 99f);
            float sy = rnd.Range(-99f, 99f);
            for(int i = 0; i <= seg; i++) {
                for(int j = 0; j <= seg; j++) {
                    Arr[j * seg_len, i * seg_len] = 0.5f + 0.5f*MathLib.Noise.PerlinNoise(sx+ m_NoiseScale * j, sy+ m_NoiseScale * i);//rnd.Range(0, 1f);
                        //rnd.Range(0, 1f);
                }
            }

            //for(int i = 0; i <= m_Seg; i++) {
            //    for(int j = 0; j <= m_Seg; j++) {
            //        Arr[j * len, i * len] = rnd.Range(0, 1f);
            //        //0.5f + 0.5f*MathLib.Noise.PerlinNoise(sx+0.1f * j, sy+0.1f * i);//rnd.Range(0, 1f);
            //    }
            //}
            //for(int i = 0; i < m_Seg; i++) {
            //    for(int j = 0; j < m_Seg; j++) {
            //        //MathLib.Lib.DiamondSquare(Arr, j * len, i * len, len+1, rnd);
            //    }
            //}


            if(m_DoDiamond) MathLib.Lib.DiamondSquare(Arr, size, seg_len, rnd);
            //int len = size - 1;
            //Arr[0, 0] = rnd.Range(0, 1f);
            //Arr[len, 0] = rnd.Range(0, 1f);
            //Arr[len, len] = rnd.Range(0, 1f);
            //Arr[0, len] = rnd.Range(0, 1f);
            //MathLib.Lib.DiamondSquare(Arr, 0, 0, size, rnd);

            m_Result = new List<float>();
            for(int i = 0; i < size; i++) {
                for(int j = 0; j < size; j++) {
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
            int size = m_Size;//((m_Size - 1) * m_Seg) + 1;
            if(m_Texture == null || m_Texture.size.x != m_PixelSize * size) {
                m_Texture = new TextureLib.UCL_Texture2D(new Vector2Int(m_PixelSize* size, m_PixelSize* size),TextureFormat.RGB24);
            }
            if(m_Result.Count >= size * size) {
                for(int i = 0; i < size; i++) {
                    for(int j = 0; j < size; j++) {
                        float val = m_Result[j + i * size];
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