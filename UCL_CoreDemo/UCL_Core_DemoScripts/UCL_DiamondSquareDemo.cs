﻿using System.Collections;
using System.Collections.Generic;
using UCL.MeshLib;
using UnityEngine;


namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_DiamondSquareDemo : MonoBehaviour {
        public int m_Seed = 0;
        public bool m_RandBySeed = false;
        public bool m_ClearResultListAfterGen = false;
        [HideInInspector] public List<float> m_Result;
        [Range(1, 10)] public int m_SizeSlider = 1;
        [Range(1, 10)] public int m_Seg = 1;
        [UCL.Core.PA.UCL_ReadOnly] public int m_Size = 5;
        [Range(0.01f, 1f)] public float m_NoiseScale = 0.1f;
        public UCL_MeshTerrainCreator m_Terrain;
        public bool m_DoDiamond = true;
        /*
        [ATTR.UCL_FunctionButton]
        public void test() {
            ulong val = 0b_00011111_11111111_00001111_11111111_11111111_11111111_11111111_11111101;
            Debug.LogWarning(val.UCL_ToBitString());
            uint val2 = 0b_11111110_11110011_11111111_11111111;
            Debug.LogWarning(val2.UCL_ToBitString());
        }
        */

        [ATTR.UCL_FunctionButton]
        public void GenResult() {
            if(m_Seg > m_SizeSlider + 1) m_Seg = m_SizeSlider + 1;
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


            if(m_DoDiamond) MathLib.Lib.DiamondSquare(Arr, size, seg_len, rnd);

            m_Result = new List<float>();
            for(int i = 0; i < size; i++) {
                for(int j = 0; j < size; j++) {
                    m_Result.Add(Arr[j,i]);
                }
            }

            if(m_Terrain) {
                m_Terrain.SetTerrain(Arr);
            }
            ResultTexture();
            if(m_ClearResultListAfterGen) m_Result.Clear();
            //m_Texture = null;
        }
        private void OnValidate() {
            m_Size = Core.MathLib.Lib.PowTwo(m_SizeSlider)+1;
        }
        public int m_PixelSize = 8;
        Core.TextureLib.UCL_Texture2D m_Texture;
        public bool m_HideResultTexture = false;
        [ATTR.UCL_DrawTexture2D]
        public Core.TextureLib.UCL_Texture2D ResultTexture() {
            if(m_HideResultTexture) return null;
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