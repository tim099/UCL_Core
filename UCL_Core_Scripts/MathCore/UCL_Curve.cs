using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
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
        public bool m_Loop = false;
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


#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton("Reverse")]
#endif
        public void Reverse() {

            for(int i = 0,len = (m_Points.Count/2); i < len ; i++) {
                //m_PathPoints.Swap(0, 1);
                m_Points.Swap(i, m_Points.Count - i - 1);
                //UCL.Core.GameObjectLib.swap(ref m_Points[i], ref m_Points[m_Points.Count - i-1]);
            }
            UpdatePathPoint();
        }
#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
#endif
        public void CreateLoop() {
            if(m_Points.Count < 2) return;
            if(m_Points.LastElement() == m_Points.FirstElement()) {
                Debug.LogWarning("CreateLoop() Fail,start point == end point!!");
                return;
            }
            m_Loop = true;
            m_Points.Add(m_Points[0]);
            UpdatePathPoint();
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
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
        [System.Serializable]
        public class LengthData {
            public LengthData() {
                m_Data = new List<float>();
            }
            public List<float> m_Data;
        }
        public List<LengthData> m_SegLengthDatas = new List<LengthData>();
        public void UpdatePathPoint() {
            //m_PathLengthPos.Clear();
            //m_PathLengthCol.Clear();
            //Debug.LogWarning("UpdatePathPoint");
            if(m_Points.Count == 0) return;
            m_SegLengthDatas.Clear();
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
                //m_PathLengthPos.Add(cur);
                //m_PathLengthCol.Add(Color.red);
                float total_len = 0;
                float cur_len = 0;
                
                for(int i = 0; i < m_Points.Count - 1; i++) {
                    float len = 0;
                    float p = (i / (float)(m_Points.Count-1));
                    LengthData ld = new LengthData();
                    ld.m_Data.Add(0);
                    //List<float> cur_len_data = new List<float>();
                    for(int j = 0; j < m_SmoothSeg; j++) {
                        float p2 = ((j+1) / (float)m_SmoothSeg);
                        float pos = p + (p2 / (m_Points.Count-1));
                        cur = GetPoint(pos);
                        float dis = (cur - prev).magnitude;
                        len += dis;
                        cur_len += dis;
                        ld.m_Data.Add(len);
                        prev = cur;
                        //m_PathLengthPos.Add(cur);
                        //m_PathLengthCol.Add(Color.Lerp(Color.red, Color.green, p2));
                    }
                    for(int j = 1; j < ld.m_Data.Count; j++) {
                        ld.m_Data[j] = ld.m_Data[j] / len;
                    }
                    m_SegLengthDatas.Add(ld);
                    m_PathSegLength[i] = len;
                    total_len += len;
                }
                m_PathLength = total_len;
            } else {
                m_PathSegLength = null;
                m_PathLength = 0;
            }
            if(!m_Loop) {
                m_PathPoints = new Vector3[path.Length + 2];
                Array.Copy(path, 0, m_PathPoints, 1, path.Length);

                m_PathPoints[0] = (2 * m_PathPoints[1] - m_PathPoints[2]);
                m_PathPoints[m_PathPoints.Length - 1] = (2 * m_PathPoints[m_PathPoints.Length - 2] - m_PathPoints[m_PathPoints.Length - 3]);
            } else {
                m_PathPoints = new Vector3[path.Length + 2];
                Array.Copy(path, 0, m_PathPoints, 1, path.Length);

                m_PathPoints[0] = path[path.Length-2];
                m_PathPoints[m_PathPoints.Length - 1] = path[1];
                //*/
            }
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

#if UNITY_EDITOR
        [Header("Editor DrawGizmos Setting")]//m_DisPointCount
        [SerializeField] [Range(0,1)] float m_DemoPoint = 0f;
        [SerializeField] Color m_DemoPointColor = Color.black;
        [SerializeField] int m_DisPointCount = 0;
        [SerializeField] bool m_GetPoint = false;
#endif
        protected void DrawGizmos() {
#if UNITY_EDITOR
            if(m_PathPoints.Length < 4) return;
            if(m_GenStartPos != transform.position ||
                m_GenStartRot != transform.rotation ||
                m_GenStartScale != transform.lossyScale) {

                UpdatePathPoint();
            }
            var tmp = Gizmos.color;
            {
                Vector3 prev = Lerp(m_PathPoints, 0);
                Gizmos.color = m_PathCol;
                int SmoothAmount = m_WorldSpacePoints.Length * m_SmoothSeg;
                for(int i = 1; i <= SmoothAmount; i++) {
                    float pm = (float)i / SmoothAmount;
                    Vector3 currPt = GetPoint(pm);
                    Gizmos.DrawLine(currPt, prev);
                    prev = currPt;
                }
            }
            /*
            {
                Gizmos.color = Color.yellow;
                var prev = m_PathLengthPos[0];
                for(int i = 1; i < m_PathLengthPos.Count; i++) {
                    var cur = m_PathLengthPos[i];
                    Gizmos.color = m_PathLengthCol[i];
                    Gizmos.DrawLine(cur, prev);
                    prev = cur;
                }
            }
            */

            if(m_DisPointCount > 0) {
                float seg = 1.0f / m_DisPointCount;
                for(int i = 0; i < m_DisPointCount; i++) {
                    var a = m_GetPoint ? GetPoint(i * seg) : GetPos(i * seg);
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
            ///*
            m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                float u2 = u * u;
                float u3 = u * u2;
                return mult * (2f - 5f * u2 + 3f * u3);
            }, Color.green, min, max, false, false);
            m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                float u2 = u * u;
                float u3 = u * u2;
                return mult * (u + 4f * u2 - 3f * u3);
            }, Color.blue, min, max, false, false);
            m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                float u2 = u * u;
                float u3 = u * u2;
                return mult * (-u2 + u3);
            }, Color.cyan, min, max, false, false);
            //*/
            //Ease.UCL_EaseTexture.DrawEase(m_Ease, m_EaseTexture);
            //GUILayout.Box(m_CurveTexture.texture);
        }
#endif
        #endregion

        public Vector3 Lerp(Vector3[] m_PathPoints ,int cur, float u) {
            /*
            int at = cur + 1;
            if(at >= 0 && at < m_SegLengthDatas.Count) {
                var n = MathLib.LinearMapping.GetPosition(m_SegLengthDatas[at].m_Data, u);
                //Debug.LogWarning("n:" + n + ",u:" + u);
                u = n;
            }
            */
            Vector3 a = m_PathPoints[cur];
            Vector3 b = m_PathPoints[cur + 1];
            Vector3 c = m_PathPoints[cur + 2];
            Vector3 d = m_PathPoints[cur + 3];

            float u2 = u * u;
            float u3 = u * u2;
            var aa = a * (-u + 2f * u2 - u3);
            var bb = b * (2f - 5f * u2 + 3f * u3);
            var cc = c * (u + 4f * u2 - 3f * u3);
            var dd = d * (-u2 + u3);

            return 0.5f * (aa + bb + cc + dd);
            
            //return aa + bb + cc + dd;
            /*
            return 0.5f * (
                (-a + 3f * (b - c) + d) * (u * u * u)
                + (2f * a - 5f * b + 4f * c - d) * (u * u)
                + (-a + c) * u 
                + 2f * b
            );
            */
        }
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

            return Lerp(m_PathPoints, cur, u);
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
        /// <param name="percent"></param>
        /// <returns></returns>
        public Vector3 GetPos(float percent) {
            return GetPosByLength(percent * m_PathLength);
        }
        /// <summary>
        /// Get position base on Segment(ignore segment length)
        /// </summary>
        /// <param name="percent"></param>
        /// <returns></returns>
        public Vector3 GetPoint(float percent) {
            return Lerp(m_PathPoints, percent);
        }
        public Vector3 GetPosLinear(float percent) {
            return GetPosByLengthLinear(percent * m_PathLength);
        }
        public int GetPathPointsLength() { return m_PathPoints.Length; }


        private void OnDrawGizmosSelected() {
            if(f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        private void OnDrawGizmos() {
            if(!f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        protected Vector3 Lerp(Vector3[] points, float percent) {
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
        #endregion
    }
}