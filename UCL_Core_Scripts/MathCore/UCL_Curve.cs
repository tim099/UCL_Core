using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_Curve : UCL_Path {
        public List<Vector3> m_Points;

        #region OnDrawGizmos Setting
        [Header("OnDrawGizmos Setting")]
        public Color m_PathCol = Color.green;
        public Color m_PointCol = Color.red;
        [Range(0.5f, 5.0f)] public float m_PointSize = 1.5f;
        [Range(1, 100)] public int m_SmoothSeg = 20;
        #endregion

        /// <summary>
        /// if true then path Gizmos will only draw when selected
        /// </summary>
        public bool f_OnlyDrawGizmosOnSelected = true;
        public bool m_Loop = false;

        /// <summary>
        /// Fix the length of each segment
        /// </summary>
        public bool m_FixLength = true;
        #region ReadOnly
        [Header("ReadOnly")]
        [PA.UCL_ReadOnly] [SerializeField] protected float[] m_PathSegLength;
        [PA.UCL_ReadOnly] [SerializeField] protected float m_PathLength;
        [HideInInspector] [SerializeField] protected Vector3[] m_WorldSpacePoints;
        [HideInInspector] [SerializeField] protected Vector3[] m_PathPoints;
        [HideInInspector] [SerializeField] protected Vector3 m_GenStartPos;
        [HideInInspector] [SerializeField] protected Quaternion m_GenStartRot;
        [HideInInspector] [SerializeField] protected Vector3 m_GenStartScale;
        [HideInInspector] [SerializeField] protected List<LengthData> m_SegLengthDatas = new List<LengthData>();
        #endregion
#if UNITY_EDITOR
        private void OnValidate() {
            if(!UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode
                && !UnityEditor.EditorApplication.isUpdating && transform.lossyScale != Vector3.zero) {
                UpdatePathPoint();
            }
        }
#endif
        private void Start() {
            //UpdatePathPoint();
        }

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton("Reverse")]
#endif
        public void Reverse() {
            m_Points.Reverse();
            UpdatePathPoint();
        }

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
#endif
        public void CreateLoop() {
            if(m_Points.Count < 2) return;
            m_Loop = true;
            if(m_Points.LastElement() == m_Points.FirstElement()) {
                Debug.LogWarning("CreateLoop() Fail,start point == end point!!");
                return;
            }
            m_Points.Add(m_Points[0]);
            UpdatePathPoint();
            //UpdatePathPoint();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
#endif
        public void RemoveLoop() {
            if(m_Points.Count < 2) return;
            if(m_Points.LastElement() != m_Points.FirstElement()) {
                Debug.LogWarning("RemoveLoop() Fail,start point != end point!!");
                return;
            }
            m_Loop = false;
            m_Points.RemoveAt(m_Points.Count-1);
            UpdatePathPoint();
        }
        [System.Serializable]
        public class LengthData {
            public LengthData() {
                m_Data = new List<float>();
            }
            public List<float> m_Data;
            //public List<float> m_Origin;
        }
        override public void UpdatePathPoint() {
            if(transform.lossyScale == Vector3.zero) {
                Debug.LogError("UCL_Curve UpdatePathPoint() Error, transform.lossyScale == Vector3.zero");
                return;
            }
            if(m_Points == null) {
                m_Points = new List<Vector3>();
                m_Points.Add(Vector3.zero);
                m_Points.Add(Vector3.right);
            }
            //Debug.LogWarning("UpdatePathPoint()");
            if(m_Points == null) m_Points = new List<Vector3>();
            if(m_Points.Count == 0) return;
            m_SegLengthDatas.Clear();
            if(m_Loop && m_Points.Count > 2) {
                m_Points[m_Points.Count - 1] = m_Points[0];
            }
            m_GenStartPos = transform.position;
            m_GenStartRot = transform.rotation;
            m_GenStartScale = transform.lossyScale;

            UpdateWorldSpacePoint();

            if(m_Points.Count > 1) {
                m_PathSegLength = new float[m_Points.Count - 1];
                Vector3 prev = GetPoint(0);
                Vector3 cur = prev;

                float total_len = 0;
                float cur_len = 0;
                
                for(int i = 0; i < m_Points.Count - 1; i++) {
                    float seg_len = 0;
                    float p = (i / (float)(m_Points.Count-1));

                    if(m_FixLength) {
                        LengthData ld = new LengthData();
                        ld.m_Data.Add(0);

                        for(int j = 0; j < m_SmoothSeg; j++) {
                            float p2 = ((j + 1) / (float)m_SmoothSeg);
                            float pos = p + (p2 / (m_Points.Count - 1));
                            cur = GetPoint(pos);
                            float dis = (cur - prev).magnitude;
                            seg_len += dis;
                            cur_len += dis;

                            prev = cur;
                            ld.m_Data.Add(seg_len);
                        }
                        for(int j = 1; j < ld.m_Data.Count; j++) {
                            ld.m_Data[j] = ld.m_Data[j] / seg_len;
                        }

                        m_SegLengthDatas.Add(ld);
                    } else {
                        for(int j = 0; j < m_SmoothSeg; j++) {
                            float p2 = ((j + 1) / (float)m_SmoothSeg);
                            float pos = p + (p2 / (m_Points.Count - 1));
                            cur = GetPoint(pos);
                            float dis = (cur - prev).magnitude;
                            seg_len += dis;
                            cur_len += dis;

                            prev = cur;
                        }
                    }

                    m_PathSegLength[i] = seg_len;
                    total_len += seg_len;
                }
                m_PathLength = total_len;
            } else {
                m_PathSegLength = null;
                m_PathLength = 0;
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(gameObject);
#endif
        }
        public void UpdateWorldSpacePoint() {
            if(m_Points == null || m_Points.Count == 0) return;

            if(m_WorldSpacePoints == null || m_WorldSpacePoints.Length != m_Points.Count) {
                m_WorldSpacePoints = m_Points.ToArray();
            }
            for(int i = 0; i < m_WorldSpacePoints.Length; i++) {
                m_WorldSpacePoints[i] = transform.TransformPoint(m_Points[i]);
            }

            if(m_PathPoints == null || m_PathPoints.Length != m_WorldSpacePoints.Length + 2) {
                m_PathPoints = new Vector3[m_WorldSpacePoints.Length + 2];
            }
            if(!m_Loop) {
                Array.Copy(m_WorldSpacePoints, 0, m_PathPoints, 1, m_WorldSpacePoints.Length);

                m_PathPoints[0] = (2 * m_PathPoints[1] - m_PathPoints[2]);
                m_PathPoints[m_PathPoints.Length - 1] = (2 * m_PathPoints[m_PathPoints.Length - 2] - m_PathPoints[m_PathPoints.Length - 3]);
            } else {
                Array.Copy(m_WorldSpacePoints, 0, m_PathPoints, 1, m_WorldSpacePoints.Length);

                m_PathPoints[0] = m_WorldSpacePoints[m_WorldSpacePoints.Length - 2];
                m_PathPoints[m_PathPoints.Length - 1] = m_WorldSpacePoints[1];
            }
        }
        protected float ConvertLinearPos(List<float> val, float target) {
            if(val.Count == 0) return 1f;
            float len = 0;
            int i = 0;
            float seg_len = val[0];
            for(; i < val.Count; i++) {
                seg_len = val[i];
                if(target <= len + seg_len) break;
                len += seg_len;
            }
            float seg_pos = (target - len) / seg_len;
            if(seg_pos < 0) seg_pos = 0;
            if(seg_pos > 1) seg_pos = 1;
            return ((i + seg_pos) / (float)val.Count);
        }
        protected float GetLinearVal(List<float> val, float target) {
            if(target > 1) target = 1;
            if(target < 0) target = 0;

            if(val == null || val.Count == 0) return target;
            int i = 0;
            for(; i < val.Count; i++) {
                if(target <= val[i]) {
                    break;
                }
            }
            float prev_val = 0;

            float pos = i / (float)val.Count;
            if(i > 0) {
                prev_val = val[i - 1];
            }
            float del = target - prev_val;
            float len = val[i] - prev_val;
            float dis = del / len;
            if(dis > 1) dis = 1;

            return (dis / val.Count) + pos;
        }
        public void SetPoint(int at, Vector3 pos) {
            if(at >= m_Points.Count) {
                return;
            }
            m_Points[at] = pos;
            UpdatePathPoint();
        }
        #region OnDrawGizmos

//#if UNITY_EDITOR
        [Header("Editor DrawGizmos Setting")]//m_DisPointCount
#pragma warning disable 0219
        [SerializeField] [Range(-0.2f,1.2f)] float m_DemoPoint = 0f;
#pragma warning restore 0219
        [SerializeField] Color m_DemoPointColor = Color.black;
        [SerializeField] int m_DisPointCount = 0;
        //[SerializeField] bool m_GetPoint = false;
//#endif
        protected void DrawGizmos() {
#if UNITY_EDITOR
            if(m_PathPoints == null || m_PathPoints.Length < 4) return;
            if(m_GenStartPos != transform.position ||
                m_GenStartRot != transform.rotation ||
                m_GenStartScale != transform.lossyScale) {

                UpdatePathPoint();
            }
            var tmp = Gizmos.color;
            {
                Vector3 prev = GetPoint(m_PathPoints, 0);
                Gizmos.color = m_PathCol;
                int SmoothAmount = m_WorldSpacePoints.Length * m_SmoothSeg;
                for(int i = 1; i <= SmoothAmount; i++) {
                    float pm = (float)i / SmoothAmount;
                    Vector3 currPt = GetPoint(pm);
                    Gizmos.DrawLine(currPt, prev);
                    prev = currPt;
                }
            }

            if(m_DisPointCount > 0) {
                float seg = 1.0f / m_DisPointCount;
                for(int i = 0; i < m_DisPointCount; i++) {
                    var a = GetPos(i * seg);
                    //var a = m_GetPoint ? GetPoint(i * seg) : GetPos(i * seg);
                    UCL_DrawGizmos.DrawConstSizeSphere(a, 0.5f * m_PointSize);
                    /*
                    var b = GetPosLinear(i * seg);
                    UCL_DrawGizmos.DrawConstSizeSphere(b, 0.5f * m_PointSize);
                    Gizmos.DrawLine(a, b);
                    */
                }
                /*
                for(int i = 0; i < m_WorldSpacePoints.Length - 1; i++) {
                    Gizmos.DrawLine(m_WorldSpacePoints[i], m_WorldSpacePoints[i + 1]);
                }
                */
            } else {
                Gizmos.color = m_DemoPointColor;
                UCL_DrawGizmos.DrawConstSizeSphere(GetPos(m_DemoPoint), 0.75f * m_PointSize);
            }
            Gizmos.color = m_PointCol;
            //UnityEditor.Handles.color = m_PointCol;
            

            for(int i = 0; i < m_WorldSpacePoints.Length; i++) {
                UCL_DrawGizmos.DrawConstSizeSphere(m_WorldSpacePoints[i], m_PointSize);
                //Gizmos.DrawSphere(m_WorldSpacePoints[i], 1f * GetGizmoSize(m_WorldSpacePoints[i]));
                //UnityEditor.Handles.dra(m_WorldSpacePoints[i], Vector3.one);
            }

            Gizmos.color = tmp;
#endif
        }

        private void OnDrawGizmosSelected() {
            if(f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        private void OnDrawGizmos() {
            if(!f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }

        #endregion
        public void RemoveAt(int at) {
            if(at >= m_Points.Count || at < 0) return;
            m_Points.RemoveAt(at);
            UpdatePathPoint();
        }

        /// <summary>
        /// if at smaller then zero, at will set to m_Points.Count - 1;
        /// </summary>
        /// <param name="at"></param>
        public void InsertPoint(int at) {
            if(m_Points.Count < 2) {
                m_Points.Add(Vector3.zero);
                return;
            }
            if(at >= m_Points.Count) at = m_Points.Count - 1;
            if(at < 0) at = m_Points.Count - 1;

            var p = ((float)(at - 0.5f) / (m_Points.Count - 1));

            Vector3 pos = GetPoint(p);
            m_Points.Insert(at, transform.InverseTransformPoint(pos));
            UpdatePathPoint();
        }


        [ATTR.UCL_FunctionButton("AddPoint")]
        public void AddPoint() {
            Vector3 pos = Vector3.zero;
            if(m_Points.Count == 1) {
                pos = m_Points[0] + Vector3.right;
            } else if(m_Points.Count > 1) {
                pos = 2 * m_Points[m_Points.Count - 1] - m_Points[m_Points.Count - 2];
            }
            m_Points.Add(pos);
            UpdatePathPoint();
        }

        public void AddPoint(Vector3 pos) {
            m_Points.Add(pos);
            UpdatePathPoint();
        }
        #region Editor

#if UNITY_EDITOR
        /*
        [Core.ATTR.UCL_DrawTexture2D(128, 128, TextureFormat.ARGB32, typeof(Core.TextureLib.UCL_Texture2D))]
        public void Editor_DebugCurve(Core.TextureLib.UCL_Texture2D m_CurveTexture) {
            m_CurveTexture.SetColor(Color.black);
            if(m_SegLengthDatas.Count > 1) {
                m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                    return u;
                    //return MathLib.LinearMapping.GetPosition(m_SegLengthDatas[0].m_Data, u);
                }, Color.red, 0, 1, false, false);

                m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                    return MathLib.LinearMapping.GetY(m_SegLengthDatas[0].m_Data, u);
                }, Color.green, 0, 1, false, false);
            }
        }

        //UCL.Core.TextureLib.UCL_Texture2D m_CurveTexture;
        [Core.ATTR.UCL_DrawTexture2D(128,128, TextureFormat.ARGB32, typeof(Core.TextureLib.UCL_Texture2D))]
        public void Editor_TestCurve(Core.TextureLib.UCL_Texture2D m_CurveTexture) {
            float min = -0.2f, max = 1f;
            float mult = 0.5f;
            m_CurveTexture.SetColor(Color.black);
            m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                float u2 = u * u;
                float u3 = u * u2;
                return mult*(-u + 2f * u2 - u3);
            },Color.red, min, max, true,true);
            //GUILayout.Box("Min:" + range.x + ",Max:" + range.y);
            //Ease.UCL_EaseTexture.DrawEase(m_Ease, m_EaseTexture);
            //GUILayout.Box(m_CurveTexture.texture);
        }
    */
#endif
        #endregion


        /// <summary>
        /// Get postion on the path by length
        /// </summary>
        /// <param name="length"></param>
        /// <returns></returns>
        public Vector3 GetPosByLength(float length) {
            if(m_PathPoints.Length < 4) return Vector3.zero;

            if(m_Loop) {
                if(length > m_PathLength || length < 0) {
                    float len = length / m_PathLength;
                    length = m_PathLength * (len - Mathf.FloorToInt(len));
                }
            } else {
                if(length < 0 || length > m_PathLength) {
                    float pos = length / m_PathLength;
                    return GetPoint(pos);
                }
                //if(length < 0) length = 0;
                //else if(length > m_PathLength) length = m_PathLength;
            }

            float cur_len = 0;
            float seg_len = 1;
            int cur = 0;
            for(int i = 0; i < m_PathSegLength.Length; i++) {
                seg_len = m_PathSegLength[i];
                cur_len += seg_len;
                if(cur_len >= length) {
                    cur = i;
                    break;
                }
            }

            if(seg_len <= 0.0001f) seg_len = 0.0001f;

            float u = 1.0f - (cur_len - length) / seg_len;
            if(u < 0) u = 0;
            if(u > 1.0f) u = 1.0f;

            if(cur > m_PathPoints.Length - 4) {
                cur = m_PathPoints.Length - 4;
                u = 1;
            }
            if(cur < 0) {
                cur = 0;
                u = 0;
            }

            return Lerp(cur, u);
        }
        override public float GetPathLength() {
            return m_PathLength;
        }
        public Vector3 GetPosByLengthLinear(float length) {
            if(m_PathPoints.Length < 4) return Vector3.zero;

            if(m_Loop) {
                if(length > m_PathLength || length < 0) {
                    float len = length / m_PathLength;
                    length = m_PathLength * (len - Mathf.FloorToInt(len));
                }
            }

            float cur_len = 0;
            float seg_len = 1;
            int cur = 0;
            for(int i = 0; i < m_PathSegLength.Length; i++) {
                seg_len = m_PathSegLength[i];
                cur_len += seg_len;
                if(cur_len >= length) {
                    cur = i;
                    break;
                }
            }
            if(cur > m_PathPoints.Length - 4) {
                cur = m_PathPoints.Length - 4;
            }

            if(seg_len <= 0.0001f) seg_len = 0.0001f;
            if(cur < 0) cur = 0;

            float u = 1.0f - (cur_len - length) / seg_len;
            if(u < 0) u = 0;
            if(u > 1.0f) u = 1.0f;

            return Vector3.Lerp(m_PathPoints[cur+1], m_PathPoints[cur+2], u);
        }


        /// <summary>
        /// Get position base on length of path
        /// </summary>
        /// <param name="percent">Range from 0.0f ~ 1.0f</param>
        /// <returns></returns>
        override public Vector3 GetPos(float percent) {
            return GetPosByLength(percent * m_PathLength);
        }

        /// <summary>
        /// Get the Rect cover hole path
        /// </summary>
        /// <param name="dir">the direction of rect(etc. xy</param>
        /// <returns></returns>
        override public Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            if(m_WorldSpacePoints == null || m_WorldSpacePoints.Length == 0) return Rect.zero;
            Vector3 min = m_WorldSpacePoints[0];
            Vector3 max = min;
            for(int i = 1; i < m_WorldSpacePoints.Length; i++) {
                var point = m_WorldSpacePoints[i];
                if(point.x < min.x) {
                    min.x = point.x;
                } else if(point.x > max.x){
                    max.x = point.x;
                }

                if(point.y < min.y) {
                    min.y = point.y;
                }else if(point.y > max.y) {
                    max.y = point.y;
                }

                if(point.z < min.z) {
                    min.z = point.z;
                } else if(point.z > max.z) {
                    max.z = point.z;
                }
            }
            Vector2 minn = min.ToVec2(dir);
            Vector2 maxx = max.ToVec2(dir);
            float width = maxx.x - minn.x;
            float height = maxx.y - minn.y;
            return Rect.MinMaxRect(minn.x - 0.1f*width, minn.y - 0.1f*height, maxx.x + 0.1f*width, maxx.y + 0.1f*height);
        }

        /// <summary>
        /// Get position base on Segment(ignore segment length)
        /// </summary>
        /// <param name="percent">Range from 0.0f ~ 1.0f</param>
        /// <returns></returns>
        public Vector3 GetPoint(float percent) {
            return GetPoint(m_PathPoints, percent);
        }
        public Vector3 GetPosLinear(float percent) {
            return GetPosByLengthLinear(percent * m_PathLength);
        }
        protected Vector3 GetPoint(Vector3[] points, float percent) {
            if(points.Length < 4) return Vector3.zero;

            if(m_Loop) {
                if(percent > 1 || percent < 0) percent -= Mathf.FloorToInt(percent);
            }
            int numSections = points.Length - 3;
            int cur = Mathf.Min(Mathf.FloorToInt(percent * numSections), numSections - 1);
            if(cur < 0) cur = 0;
            float u = percent * numSections - cur;

            return Lerp(points, cur, u);
        }
        
        public Vector3 Lerp(int cur, float y) {
            if(m_FixLength && m_SegLengthDatas != null && cur >= 0 && cur < m_SegLengthDatas.Count) {
                y = MathLib.LinearMapping.GetX(m_SegLengthDatas[cur].m_Data, y);//at
            }
            Vector3 a = m_PathPoints[cur];
            Vector3 b = m_PathPoints[cur + 1];
            Vector3 c = m_PathPoints[cur + 2];
            Vector3 d = m_PathPoints[cur + 3];
            /*
            float y2 = y * y;
            float y3 = y * y2;
            var aa = a * (-y + 2f * y2 - y3);
            var bb = b * (2f - 5f * y2 + 3f * y3);
            var cc = c * (y + 4f * y2 - 3f * y3);
            var dd = d * (-y2 + y3);

            return 0.5f * (aa + bb + cc + dd);
            */
            ///*
            return 0.5f * (
                (-a + 3f * (b - c) + d) * (y * y * y)
                + (2f * a - 5f * b + 4f * c - d) * (y * y)
                + (-a + c) * y 
                + 2f * b
            );
            //*/
        }
        public Vector3 Lerp(Vector3[] m_PathPoints, int cur, float y) {
            Vector3 a = m_PathPoints[cur];
            Vector3 b = m_PathPoints[cur + 1];
            Vector3 c = m_PathPoints[cur + 2];
            Vector3 d = m_PathPoints[cur + 3];
            ///*
            return 0.5f * (
                (-a + 3f * (b - c) + d) * (y * y * y)
                + (2f * a - 5f * b + 4f * c - d) * (y * y)
                + (-a + c) * y 
                + 2f * b
            );
            //*/
        }
        public int GetPathPointsLength() { return m_PathPoints.Length; }

        #region Editor
#if UNITY_EDITOR
        [NonSerialized] public VectorExtensionMethods.Vec3ToVec2 m_Dir = VectorExtensionMethods.Vec3ToVec2.xy;
        TextureLib.UCL_Texture2D m_Texture;
        [ATTR.UCL_DrawTexture2D]
        UCL.Core.TextureLib.UCL_Texture2D CurveTexture() {
            if(m_PathPoints == null || m_PathPoints.Length == 0) return null;
            if(m_Texture == null) {
                m_Texture = new TextureLib.UCL_Texture2D(256, 256);
            }
            m_Texture.SetColor(Color.black);
            m_Texture.DrawPath(this, GetRect(m_Dir), Color.green, m_Dir, 5 * m_PathPoints.Length);
            return m_Texture;
        }
#endif
        #endregion
    }
}