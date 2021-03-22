using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Physic
{
    [System.Serializable]
    public class UCL_Line3D
    {
        public int Count { get { return m_Points.Count; } }
        public float Length { get; protected set; }
        public List<Vector3> Points { get { return m_Points; } }
        public UCL_Line3D Clone() {
            var aLine3D = new UCL_Line3D(m_MaxLen);
            aLine3D.m_Width = m_Width;
            aLine3D.m_Col = m_Col;
            aLine3D.m_OutLineCol = m_OutLineCol;
            aLine3D.m_Loop = m_Loop;
            aLine3D.m_Points = m_Points.Clone();
            return aLine3D;
        }
        public UCL_Line3D(int _MaxLen = -1) {
            m_MaxLen = _MaxLen;
        }
        protected List<Vector3> m_Points = new List<Vector3>();
        public Color m_Col = Color.red;
        /// <summary>
        /// if clear then no outline!!
        /// </summary>
        public Color m_OutLineCol = Color.clear;
        /// <summary>
        /// One pixel default
        /// </summary>
        public int m_Width = 1;
        /// <summary>
        /// //-1 is No Limit
        /// </summary>
        public int m_MaxLen = -1;
        /// <summary>
        /// Is the line Loop?
        /// </summary>
        public bool m_Loop = false;
        public void Add(Vector3 p) {
            if(m_MaxLen > 0) {
                if(m_Points.Count >= m_MaxLen) {
                    m_Points.RemoveFirst();
                }
            }
            m_Points.Add(p);
        }
        public void Clear()
        {
            m_Points.Clear();
        }
        public Vector3 GetPoint(int at) {
            return m_Points[at];
        }
    }
}