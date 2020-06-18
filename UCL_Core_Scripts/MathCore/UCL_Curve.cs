using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    [ATTR.EnableUCLEditor]
    public class UCL_Curve : MonoBehaviour {
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

        #region ReadOnly
        [Header("ReadOnly")]
        [PA.UCL_ReadOnly] [SerializeField] protected Vector3[] m_WorldSpacePoints;
        [PA.UCL_ReadOnly] [SerializeField] protected Vector3[] m_PathPoints;
        [PA.UCL_ReadOnly] [SerializeField] protected float[] m_PathSegLength;
        [PA.UCL_ReadOnly] [SerializeField] protected float m_PathLength;
        [HideInInspector] [SerializeField] protected Vector3 m_GenStartPos;
        [HideInInspector] [SerializeField] protected Quaternion m_GenStartRot;
        [HideInInspector] [SerializeField] protected Vector3 m_GenStartScale;
        #endregion
#if UNITY_EDITOR
        private void OnValidate() {
            UpdatePathPoint();
        }
#endif
        public void UpdatePathPoint() {
            //Debug.LogWarning("UpdatePathPoint");
            if(m_Points.Count == 0) return;
            m_GenStartPos = transform.position;
            m_GenStartRot = transform.rotation;
            m_GenStartScale = transform.lossyScale;
            m_WorldSpacePoints = m_Points.ToArray();
            for(int i = 0; i < m_WorldSpacePoints.Length; i++) {
                m_WorldSpacePoints[i] = transform.TransformPoint(m_WorldSpacePoints[i]);
            }
            Vector3[] path = m_WorldSpacePoints;
            if(m_Points.Count > 1) {
                //m_PathSegLength
                m_PathSegLength = new float[m_Points.Count - 1];
                Vector3 prev = GetPoint(0);
                Vector3 cur = prev;
                float total_len = 0;
                for(int i = 0; i < m_Points.Count - 1; i++) {
                    float len = 0;
                    for(int j = 0; j < m_SmoothSeg; j++) {
                        cur = GetPoint((i * m_SmoothSeg + j) / (float)(m_SmoothSeg * m_Points.Count));
                        len += (cur - prev).magnitude;
                        prev = cur;
                    }
                    m_PathSegLength[i] = len;
                    total_len += len;
                }
                m_PathLength = total_len;
            } else {
                m_PathSegLength = null;
                m_PathLength = 0;
            }

            m_PathPoints = new Vector3[path.Length + 2];
            Array.Copy(path, 0, m_PathPoints, 1, path.Length);

            m_PathPoints[0] = m_PathPoints[1] + (m_PathPoints[1] - m_PathPoints[2]);
            m_PathPoints[m_PathPoints.Length - 1] = m_PathPoints[m_PathPoints.Length - 2] +
                (m_PathPoints[m_PathPoints.Length - 2] - m_PathPoints[m_PathPoints.Length - 3]);

            /*
            //is this a closed, continuous loop? yes? well then so let's make a continuous Catmull-Rom spline!
            if(m_PathPoints[1] == m_PathPoints[m_PathPoints.Length - 2]) {
                Vector3[] tmpLoopSpline = new Vector3[m_PathPoints.Length];
                Array.Copy(m_PathPoints, tmpLoopSpline, m_PathPoints.Length);
                tmpLoopSpline[0] = tmpLoopSpline[tmpLoopSpline.Length - 3];
                tmpLoopSpline[tmpLoopSpline.Length - 1] = tmpLoopSpline[2];
                m_PathPoints = new Vector3[tmpLoopSpline.Length];
                Array.Copy(tmpLoopSpline, m_PathPoints, tmpLoopSpline.Length);
            }
            */
        }

        public void SetPoint(int at, Vector3 pos) {
            if(at >= m_Points.Count) {
                return;
            }
            m_Points[at] = pos;
            UpdatePathPoint();
        }
        #region OnDrawGizmos

        protected void DrawGizmos() {
#if UNITY_EDITOR
            if(m_PathPoints.Length < 4) return;
            if(m_GenStartPos != transform.position ||
                m_GenStartRot != transform.rotation ||
                m_GenStartScale != transform.lossyScale) {

                UpdatePathPoint();
            }
            var tmp = Gizmos.color;
            Vector3 prev = Interpolation(m_PathPoints, 0);
            Gizmos.color = m_PathCol;
            int SmoothAmount = m_WorldSpacePoints.Length * m_SmoothSeg;
            for(int i = 1; i <= SmoothAmount; i++) {
                float pm = (float)i / SmoothAmount;
                Vector3 currPt = GetPoint(pm);
                Gizmos.DrawLine(currPt, prev);
                prev = currPt;
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
        public Vector3 GetPoint(float percent) {
            return Interpolation(m_PathPoints, percent);
        }
        /// <summary>
        /// Get postion on the path by length
        /// </summary>
        /// <param name="length"></param>
        /// <returns></returns>
        public Vector3 GetPosByLength(float length) {
            if(m_PathPoints.Length < 4) return Vector3.zero;
            float cur_len = 0;
            float seg_len = 1;
            int cur = 0;
            for(int i = 0; i < m_PathSegLength.Length; i++) {
                seg_len = m_PathSegLength[i];
                //Debug.LogWarning("seg_len:" + seg_len);
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
            //Debug.LogWarning("u:" + u + ",seg_len:" + seg_len+ ",cur_len:"+ cur_len+ ",at:"+ at+ ",(cur_len - at):"+ (cur_len - at));
            if(u < 0) u = 0;
            if(u > 1.0f) u = 1.0f;
            Vector3 a = m_PathPoints[cur];
            Vector3 b = m_PathPoints[cur + 1];
            Vector3 c = m_PathPoints[cur + 2];
            Vector3 d = m_PathPoints[cur + 3];
            //Debug.LogWarning("a:" + a + ",b:" + b + ",c:" + c + ",d:" + d);
            return 0.5f * ((-a + 3f * (b - c) + d) * (u * u * u)
                + (2f * a - 5f * b + 4f * c - d) * (u * u)
                + (-a + c) * u + 2f * b
            );
        }
        /// <summary>
        /// Get position base on lenght of path
        /// </summary>
        /// <param name="percent"></param>
        /// <returns></returns>
        public Vector3 GetPos(float percent) {
            return GetPosByLength(percent * m_PathLength);
        }
        public int GetPathPointsLength() { return m_PathPoints.Length; }


        private void OnDrawGizmosSelected() {
            if(f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        private void OnDrawGizmos() {
            if(!f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        private static Vector3 Interpolation(Vector3[] points, float percent) {
            if(points.Length < 4) return Vector3.zero;

            int numSections = points.Length - 3;
            int cur = Mathf.Min(Mathf.FloorToInt(percent * numSections), numSections - 1);
            if(cur < 0) cur = 0;
            float u = percent * numSections - cur;

            Vector3 a = points[cur];
            Vector3 b = points[cur + 1];
            Vector3 c = points[cur + 2];
            Vector3 d = points[cur + 3];

            return 0.5f * ((-a + 3f * (b - c) + d) * (u * u * u)
                + (2f * a - 5f * b + 4f * c - d) * (u * u)
                + (-a + c) * u + 2f * b
            );
        }
        #endregion
    }
}