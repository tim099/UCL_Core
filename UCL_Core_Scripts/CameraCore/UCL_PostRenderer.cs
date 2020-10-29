using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib
{
    public class RenderDatas
    {
        public void Clear() {
            m_Lines.Clear();
        }
        public List<UCL.Core.Physic.UCL_Line3D> m_Lines = new List<Physic.UCL_Line3D>();
    }
    public class UCL_PostRenderer : MonoBehaviour
    {
        public RenderDatas m_CurData = new RenderDatas();
        public RenderDatas m_NextData = new RenderDatas();
        Camera m_Camera;
        Matrix4x4 m_ProjectMatrix = Matrix4x4.Ortho(0, Screen.width, 0, Screen.height, 0, 1);
        public Material m_Mat;

        public static UCL_PostRenderer ins = null;
        void Awake() {
            ins = this;
            if(m_Mat == null) m_Mat = new Material(Shader.Find("Unlit/Color"));
            m_Camera = GetComponent<Camera>();
        }
        public void DrawLine(UCL.Core.Physic.UCL_Line3D line) {
            m_NextData.m_Lines.Add(line);//.Clone()
        }
        void WorldSpaceDrawLine(UCL.Core.Physic.UCL_Line3D line) {
            if(line == null || line.m_Width <= 0 || line.m_Points.Count == 0) {
                return;
            }

            m_Mat.SetPass(0);
            m_Mat.SetColor("_Color", line.m_Col);//_Unlit
            //GL.LoadOrtho();
            GL.LoadIdentity();
            GL.LoadProjectionMatrix(m_ProjectMatrix);

            System.Action<Vector3, Vector3> draw = delegate (Vector3 a, Vector3 b) {
                if(a.z < m_Camera.nearClipPlane || b.z < m_Camera.nearClipPlane) return;
                float z_dis = m_Camera.farClipPlane - m_Camera.nearClipPlane;
                a.z = -(a.z - m_Camera.nearClipPlane) / a.z; //avp.z / avp.w;
                b.z = -(b.z - m_Camera.nearClipPlane) / b.z;// bvp.z / bvp.w;

                GL.Begin(GL.LINES);
                GL.Color(line.m_Col);
                GL.Vertex(a);
                GL.Vertex(b);
                GL.End();
            };
            Vector3 PrevPoint = m_Camera.WorldToScreenPoint(line.m_Points[0]);



            Vector3 Point = PrevPoint;
            var StartP = Point;
            int count = line.m_Points.Count;
            for(int i = 1; i < count; i++) {
                Point = m_Camera.WorldToScreenPoint(line.m_Points[i]);

                draw(PrevPoint, Point);

                PrevPoint = Point;
            }
            //if(line.f_Loop) {
            //    draw(PrevPoint, StartP);
            //}
        }
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            foreach(var line in m_CurData.m_Lines) {
                if(line!=null && line.m_Points.Count > 0) {
                    int count = line.m_Points.Count;
                    Vector3 prev = line.m_Points[0];
                    for(int j = 1; j < count; j++) {
                        var cur = line.m_Points[j];
                        UCL.Core.UCL_DrawGizmos.DrawLine(prev, cur, line.m_Width, line.m_Col);
                        prev = cur;
                    }
                }

            }
#endif
        }

        void OnPostRender() {
            if(m_Camera == null) return;

            foreach(var line in m_CurData.m_Lines) {
                WorldSpaceDrawLine(line);
            }
            m_CurData.Clear();
            UCL.Core.GameObjectLib.Swap(ref m_CurData, ref m_NextData);
        }
    }
}