using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TestLib {
    [ATTR.EnableUCLEditor]
    public class UCL_TestScript : MonoBehaviour {
        [ATTR.UCL_FunctionButton]
        public void TestInt() {
            Debug.Log("IntMax:" + int.MaxValue);
            Debug.Log("IntMin:" + int.MinValue);
            Debug.Log("-IntMax:" + (-int.MaxValue));
            int val = int.MinValue;
            Debug.Log("-IntMin:" + (-val));
            Debug.Log("ABS(IntMin):" + MathLib.Lib.Abs(int.MinValue));
        }
        public RectTransform m_Range;
        public Vector3[] m_Corners;
        TextureLib.UCL_Texture2D m_Texture;
        Vector2Int line_s;
        Vector2Int line_e;
        [ATTR.UCL_DrawTexture2D]
        public TextureLib.UCL_Texture2D DrawLine() {
            if(m_Texture == null) {
                ReDraw();
            }
            
            return m_Texture;
        }
        public UCL.Core.MathLib.UCL_Path m_Path;
        public int m_PathSeg = 60;
        public int sx = 80;
        public int sy = 50;
        [Range(-360,360)]public int m_Angle;
        [Range(0,500f)]public float m_Len;
        public Vector2 m_PathSize = Vector2.one;
        private void OnValidate() {
            ReDraw();
        }

        [ATTR.UCL_FunctionButton]
        public void ReDraw() {
            if(m_Texture == null) {
                m_Texture = new TextureLib.UCL_Texture2D(256, 256);
            }
            
            float rad = m_Angle * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);
            int ex = Mathf.RoundToInt(sx + m_Len * cos);
            int ey = Mathf.RoundToInt(sy + m_Len * sin);
            //Debug.LogWarning("sin:" + sin + ",cos:" + cos + ",rad:" + rad+",sx:"+sx+",sy:"+sy);
            m_Texture.SetColor(Color.black);
            m_Texture.DrawLine(sx, sy, ex, ey, Color.yellow);
            //m_Texture.DrawLine(85, 10, 10, 50, Color.green);
            //m_Texture.DrawLine(0.1f, 0.88f, 0.9f, 0.6f, Color.red);
            if(m_Path) {
                m_Texture.DrawPathXY(m_Path, m_Range, Color.cyan, m_PathSeg);
            }
        }
    }
}

