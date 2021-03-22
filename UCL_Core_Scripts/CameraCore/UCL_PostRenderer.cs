using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        void Reset() {
#if UNITY_EDITOR
            if(m_Mat == null) {
                m_Mat = UCL_CoreSetting.LoadMaterial("UCL_VertexColMat");
                //var path = Path.Combine(Path.Combine(UCL_CoreSetting.GetFolderPath(), "UCL_Core_Materials"), "UCL_VertexColMat.mat");
                //Debug.LogError("Reset() path>:"+ path);
                //m_Mat = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) as Material;
            }
#endif
        }
        public RenderDatas m_CurData = new RenderDatas();
        public RenderDatas m_NextData = new RenderDatas();
        Camera m_Camera;
        Matrix4x4 m_ProjectMatrix = Matrix4x4.Ortho(0, Screen.width, 0, Screen.height, 0, 1);
        public Material m_Mat;

        public static UCL_PostRenderer ins = null;
        void Awake() {
            ins = this;
            if(m_Mat == null) m_Mat = new Material(Shader.Find("Unlit/Color"));
            m_Mat = Instantiate(m_Mat);
            m_Camera = GetComponent<Camera>();
        }
        public void DrawLine(UCL.Core.Physic.UCL_Line3D line) {
            m_NextData.m_Lines.Add(line);//.Clone()
        }
        void WorldSpaceDrawLine(UCL.Core.Physic.UCL_Line3D line) {
            if(line == null || line.m_Width <= 0 || line.Count <= 1) {
                return;
            }

            m_Mat.SetPass(0);
            m_Mat.SetColor("_Color", line.m_Col);
            //GL.LoadOrtho();
            GL.LoadIdentity();
            GL.LoadProjectionMatrix(m_ProjectMatrix);

            System.Action<Vector3, Vector3> draw = delegate (Vector3 a, Vector3 b) {
                if(a.z < m_Camera.nearClipPlane || b.z < m_Camera.nearClipPlane) return;
                float z_dis = m_Camera.farClipPlane - m_Camera.nearClipPlane;
                a.z = -(a.z - m_Camera.nearClipPlane) / a.z;
                b.z = -(b.z - m_Camera.nearClipPlane) / b.z;

                GL.Begin(GL.LINES);
                GL.Color(line.m_Col);
                GL.Vertex(a);
                GL.Vertex(b);
                GL.End();
            };
            Vector3 PrevPoint = m_Camera.WorldToScreenPoint(line.Points[0]);

            Vector3 Point = PrevPoint;
            var StartP = Point;
            int count = line.Count;
            for(int i = 1; i < count; i++) {
                Point = m_Camera.WorldToScreenPoint(line.Points[i]);

                draw(PrevPoint, Point);

                PrevPoint = Point;
            }
            if(line.m_Loop) {
                draw(PrevPoint, StartP);
            }
        }
        void WorldSpace_DrawRect(UCL.Core.Physic.UCL_Line3D line) {
            if(line == null || line.m_Width <= 0 || line.Count <= 1) {
                return;
            }
            bool draw_out_line = line.m_OutLineCol != Color.clear;
            m_Mat.SetPass(0);
            m_Mat.SetColor("_Color", line.m_Col);
            //GL.LoadOrtho();
            GL.LoadIdentity();
            GL.LoadProjectionMatrix(m_ProjectMatrix);

            Vector3 pa = Vector3.negativeInfinity;
            Vector3 pb = Vector3.negativeInfinity;
            Vector3 prev_pa = Vector3.negativeInfinity;
            Vector3 prev_pb = Vector3.negativeInfinity;
            Vector3 start_pa = Vector3.negativeInfinity;
            System.Func<Vector3, Vector3, bool> draw = delegate (Vector3 a, Vector3 b) {
                if(a.z < 0 || b.z < 0) return false;
                float z_dis = m_Camera.farClipPlane - m_Camera.nearClipPlane;
                a.z = -(a.z - m_Camera.nearClipPlane) / a.z;
                b.z = -(b.z - m_Camera.nearClipPlane) / b.z;

                var del = b - a;
                var n = Vector3.Cross(del, new Vector3(0, 0, 1));
                n.Normalize();
                n *= line.m_Width;
                prev_pa = pa;
                prev_pb = pb;
                pa = a + n;
                pb = b + n;
                if(start_pa.Equals(Vector3.negativeInfinity)) {
                    start_pa = a + n;
                }
                if(!prev_pb.Equals(Vector3.negativeInfinity)) {
                    GL.Begin(GL.TRIANGLES);
                    GL.Color(line.m_Col);
                    GL.Vertex(prev_pb);

                    GL.Vertex(a);
                    GL.Vertex(pa);
                    GL.End();
                    if(draw_out_line) {
                        GL.Begin(GL.LINES);
                        GL.Color(line.m_OutLineCol);
                        GL.Vertex(prev_pb);
                        GL.Vertex(pa);
                        GL.End();
                    }
                }

                GL.Begin(GL.QUADS);
                GL.Color(line.m_Col);
                GL.Vertex(a);
                GL.Vertex(b);
                GL.Vertex(pb);
                GL.Vertex(pa);
                GL.End();

                if(draw_out_line) {
                    GL.Begin(GL.LINES);
                    GL.Color(line.m_OutLineCol);
                    GL.Vertex(a);
                    GL.Vertex(b);
                    GL.End();

                    GL.Begin(GL.LINES);
                    GL.Color(line.m_OutLineCol);
                    GL.Vertex(a + n);
                    GL.Vertex(b + n);
                    GL.End();
                }
                return true;
            };

            Vector3 PrevPoint = m_Camera.WorldToScreenPoint(line.GetPoint(0));
            Vector3 Point = PrevPoint;
            var StartP = Point;
            int count = line.Count;
            bool draw_flag = false;
            for(int i = 1; i < count; i++) {
                Point = m_Camera.WorldToScreenPoint(line.GetPoint(i));

                draw_flag = draw(PrevPoint, Point);

                PrevPoint = Point;
            }

            if(line.m_Loop) {
                draw(PrevPoint, StartP);
                if(draw_flag) {
                    StartP.z = -(StartP.z - m_Camera.nearClipPlane) / StartP.z;
                    if(StartP.z >= 0 && start_pa.z >= 0 && pb.z >= 0) {
                        GL.Begin(GL.TRIANGLES);
                        GL.Color(line.m_Col);//line.m_Col
                        GL.Vertex(pb);
                        GL.Vertex(StartP);
                        GL.Vertex(start_pa);
                        GL.End();
                        if(draw_out_line) {
                            GL.Begin(GL.LINES);
                            GL.Color(line.m_OutLineCol);
                            GL.Vertex(pb);
                            GL.Vertex(start_pa);
                            GL.End();
                        }
                    }
                }
            }
        }
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            foreach(var line in m_CurData.m_Lines) {
                if(line!=null && line.Count > 0) {
                    int count = line.Count;
                    Vector3 prev = line.Points[0];
                    for(int j = 1; j < count; j++) {
                        var cur = line.Points[j];
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
                if(line.m_Width > 1 || !line.m_OutLineCol.Equals(Color.clear)) {
                    WorldSpace_DrawRect(line);
                } else {
                    WorldSpaceDrawLine(line);
                }
            }
            m_CurData.Clear();
            UCL.Core.GameObjectLib.Swap(ref m_CurData, ref m_NextData);
        }
    }
}