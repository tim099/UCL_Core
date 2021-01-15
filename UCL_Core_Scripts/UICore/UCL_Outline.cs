using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core.UI
{
    public class UCL_Outline : Shadow
    {
#if UNITY_EDITOR
        protected override void Reset() {
            Color color = base.effectColor;
            color.a = 1f;
            base.effectColor = color;
        }
#endif
        [Range(0, 15)]
        public float m_Size = 3.0f;

        public override void ModifyMesh(VertexHelper vh) {
            if(!IsActive()) return;
            var verts = new List<UIVertex>();
            vh.GetUIVertexStream(verts);

            var min_size = verts.Count * 5;
            if(verts.Capacity < min_size)
                verts.Capacity = min_size;

            Vector2 m_effectDistance = new Vector2(m_Size, m_Size);
            var start = 0;
            var end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, m_effectDistance.x, m_effectDistance.y);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, m_effectDistance.x, -m_effectDistance.y);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, -m_effectDistance.x, m_effectDistance.y);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, -m_effectDistance.x, -m_effectDistance.y);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, 0, m_effectDistance.y * 1.5f);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, m_effectDistance.x * 1.5f, 0);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, -m_effectDistance.x * 1.5f, 0);

            start = end;
            end = verts.Count;
            ApplyShadowZeroAlloc(verts, effectColor, start, verts.Count, 0, -m_effectDistance.y * 1.5f);

            vh.Clear();
            vh.AddUIVertexTriangleStream(verts);

        }
    }
}