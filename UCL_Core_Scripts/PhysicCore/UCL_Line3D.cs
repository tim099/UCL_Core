using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Physic
{
    [System.Serializable]
    public class UCL_Line3D
    {
        public UCL_Line3D Clone() {
            var data = new UCL_Line3D(m_MaxLen);
            data.m_Width = m_Width;
            data.m_Points = new List<Vector3>(m_Points.Count);
            for(int i=0;i< data.m_Points.Count; i++) {
                data.m_Points[i] = m_Points[i];
            }
            return data;
        }
        public UCL_Line3D(int _MaxLen = -1) {
            m_MaxLen = _MaxLen;
        }
        public List<Vector3> m_Points = new List<Vector3>();
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
        public Vector3 GetPoint(int at) {
            return m_Points[at];
        }
        public int Count { get { return m_Points.Count; } }
    }
}