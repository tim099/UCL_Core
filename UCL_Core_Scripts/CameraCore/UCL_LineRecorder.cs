using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib
{
    public class UCL_LineRecorder : MonoBehaviour
    {
        public int m_RecordLen = 25;
        public int m_RecordInterval = 5;
        public int m_LineWidth = 1;
        public Color m_LineCol = Color.red;
        public Color m_OutlineCol = Color.clear;
        int m_Timer = 0;
        UCL.Core.Physic.UCL_Line3D m_Line = null;
        // Start is called before the first frame update
        void Start() {
            m_Line = new Physic.UCL_Line3D(m_RecordLen);
        }

        // Update is called once per frame
        void Update() {
            m_Line.m_Col = m_LineCol;
            m_Line.m_OutLineCol = m_OutlineCol;
            m_Line.m_Width = m_LineWidth;
            if(m_Timer >= m_RecordInterval) {
                m_Timer = 0;
            }
            if(m_Timer == 0) {
                m_Line.Add(transform.position);
            }
            m_Timer++;
            UCL_PostRenderer.ins.DrawLine(m_Line);
        }
    }
}