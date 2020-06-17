using System.Collections;
using System.Collections.Generic;
using UCL.Core.TextureLib;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class UCL_EaseTexture : UCL_Texture2D {
        public UCL_Ease m_Ease;
        public UCL.Core.MathLib.RangeChecker<float> m_RangeCheck = new MathLib.RangeChecker<float>();
        public Color m_EaseCol = Color.green;

        public float m_Min;
        public float m_Max;
        public float m_Range;
        public UCL_EaseTexture(Vector2Int _size, TextureFormat _TextureFormat = TextureFormat.ARGB32)
            : base(_size, _TextureFormat) {
            Init();
        }
        public void SetEase(UCL_Ease ease) {
            m_Ease = ease;
            if(m_Ease == null) return;
            m_RangeCheck.Init(0, 1);

            for(int i = 0; i < m_Size.x; i++) {
                float at = (i / (float)(m_Size.x - 1));
                float val = m_Ease.GetEase(at);
                m_RangeCheck.AddValue(val);
            }
            SetColor(Color.black);
            m_Min = m_RangeCheck.Min;
            m_Max = m_RangeCheck.Max;
            m_Range = m_Max - m_Min;

            if(m_Min < 0) {
                float z_pos = -m_Min / m_Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                },Color.white);
            }

            if(m_Max > 1.0f) {
                float z_pos = (1.0f-m_Min) / m_Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                }, Color.blue);
            }

            float range = ((Mathf.CeilToInt(m_Range * (m_Size.y + 1))) / (float)m_Size.y);
            DrawLine(
                delegate (float x) {
                    float val = m_Ease.GetEase(x);
                    float y = ((val - m_Min) / range);
                    return y;
                }, m_EaseCol);
        }
    }
}