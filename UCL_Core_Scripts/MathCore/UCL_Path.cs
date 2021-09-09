using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public interface UCLI_Path {
        /// <summary>
        /// Get position base on length of path
        /// </summary>
        /// <param name="percent">Range from 0.0f ~ 1.0f</param>
        /// <returns></returns>
        Vector3 GetPos(float percent);
        float GetPathLength();

        /// <summary>
        /// Get the Rect cover whole path
        /// </summary>
        /// <param name="dir">the direction of rect(etc. xy</param>
        /// <returns></returns>
        Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy);

        /// <summary>
        /// Get a random path generate by seed
        /// </summary>
        /// <param name="seed">seed of the path</param>
        /// <returns></returns>
        UCLI_Path GetRandomPath(int seed);//UCLI_Path GetRandomPath(int seed) { return this; }
    }
    public class Path : UCLI_Path {
        public Path() {
            //Init();
        }
        public Path(List<Vector3> _points = null, float _PathLength = 0) {
            Init(_points, _PathLength);
        }
        virtual public void Init(List<Vector3> _points = null, float _PathLength = 0) {
            m_Points = _points;
            m_PathLength = _PathLength;
            if(m_Points == null) {
                m_Points = new List<Vector3>();
            }
        }

        /// <summary>
        /// Get position base on length of path
        /// </summary>
        /// <param name="percent">Range from 0.0f ~ 1.0f</param>
        /// <returns></returns>
        virtual public Vector3 GetPos(float percent) {
            return Core.MathLib.LinearMapping.GetValue(m_Points, percent);
        }
        //virtual public rect3D

        /// <summary>
        /// Get the Rect cover whole path
        /// </summary>
        /// <param name="dir">the direction of rect(etc. xy</param>
        /// <returns></returns>
        virtual public Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return Rect.zero;
        }
        virtual public float GetPathLength() {
            return m_PathLength;
        }
        virtual public void UpdatePathPoint() { }
        virtual public UCLI_Path GetRandomPath(int seed) { return this; }
        virtual public void OnDrawGizmos(Color m_StartCol, Color m_EndCol) {
#if UNITY_EDITOR
            var tmp = Gizmos.color;

            Gizmos.color = m_StartCol;
            var prev = m_Points[0];
            UCL_DrawGizmos.DrawConstSizeSphere(prev, 1.5f);
            for(int i = 1; i < m_Points.Count; i++) {
                var cur = m_Points[i];
                //Gizmos.color = Color.Lerp(m_StartCol, m_EndCol, (i / (float)m_Points.Count));
                UCL_DrawGizmos.DrawLine(prev, cur);
                prev = cur;
            }
            Gizmos.color = m_EndCol;
            if(m_Points.Count > 1) {
                UCL_DrawGizmos.DrawConstSizeSphere(prev, 1.5f);
            }
            
            //m_Rnd.OnRect(width, height);

            Gizmos.color = tmp;
#endif
        }
        public float m_PathLength;
        public List<Vector3> m_Points;
    }
    public class CurvePath : Path {
        public CurvePath(List<Vector3> _points = null, float _PathLength = 0) {
            Init(_points, _PathLength);
        }
        public override void Init(List<Vector3> _points = null, float _PathLength = 0) {
            m_Points = _points;
            m_PathLength = _PathLength;
            if(m_Points == null) {
                m_Points = new List<Vector3>();
            }
            if(m_Points.Count >= 2) {
                var s = 2 * m_Points[0] - m_Points[1];
                var e = 2 * m_Points.LastElement<Vector3>() - m_Points[m_Points.Count - 2];
                m_Points.Insert(0, s);
                m_Points.Add(e);
            }
        }
        public override Vector3 GetPos(float percent) {
            if(m_Points.Count < 4) return Vector3.zero;

            int numSections = m_Points.Count - 3;
            int cur = Mathf.Min(Mathf.FloorToInt(percent * numSections), numSections - 1);
            if(cur < 0) cur = 0;
            float u = percent * numSections - cur;

            return Lerp(cur, u);
        }
        public Vector3 Lerp(int cur, float y) {
            Vector3 a = m_Points[cur];
            Vector3 b = m_Points[cur + 1];
            Vector3 c = m_Points[cur + 2];
            Vector3 d = m_Points[cur + 3];
            return 0.5f * (
                (-a + 3f * (b - c) + d) * (y * y * y)
                + (2f * a - 5f * b + 4f * c - d) * (y * y)
                + (-a + c) * y
                + 2f * b
            );
        }
        public override void OnDrawGizmos(Color m_StartCol, Color m_EndCol) {
#if UNITY_EDITOR
            var tmp = Gizmos.color;

            Gizmos.color = m_StartCol;
            var prev = m_Points[0];
            UCL_DrawGizmos.DrawConstSizeSphere(prev, 1.5f);
            //for(int i = 1; i < m_Points.Count; i++) {
            //    var cur = m_Points[i];
            //    //Gizmos.color = Color.Lerp(m_StartCol, m_EndCol, (i / (float)m_Points.Count));
            //    UCL_DrawGizmos.DrawLine(prev, cur);
            //    prev = cur;
            //}
            int pc = 500;
            for(int i = 1; i <= pc; i++) {
                var cur = GetPos((i / (float)pc));
                UCL_DrawGizmos.DrawLine(prev, cur);
                prev = cur;
            }
            Gizmos.color = m_EndCol;
            if(m_Points.Count > 1) {
                UCL_DrawGizmos.DrawConstSizeSphere(prev, 1.5f);
            }

            //m_Rnd.OnRect(width, height);

            Gizmos.color = tmp;
#endif
        }
    }
    public class UCL_Path : MonoBehaviour, UCLI_Path {
        /// <summary>
        /// Get position base on length of path
        /// </summary>
        /// <param name="percent">Range from 0.0f ~ 1.0f</param>
        /// <returns></returns>
        virtual public Vector3 GetPos(float percent) {
            return Vector3.zero;
        }
        //virtual public rect3D

        /// <summary>
        /// Get the Rect cover whole path
        /// </summary>
        /// <param name="dir">the direction of rect(etc. xy</param>
        /// <returns></returns>
        virtual public Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return Rect.zero;
        }
        virtual public float GetPathLength() {
            return 0;
        }
        virtual public void UpdatePathPoint() { }

        /// <summary>
        /// Get a random path generate by seed
        /// </summary>
        /// <param name="seed">seed of the path</param>
        /// <returns></returns>
        virtual public UCLI_Path GetRandomPath(int seed) { return this; }
    }


}