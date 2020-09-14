using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_RandomPath2D : UCL_Path {
        [System.Serializable]
        public class MoveData {
            public float m_Acc = 0.1f;
            public float m_AngelDel = 10f;
            public Vector2 m_Vel = Vector2.zero;
            public Vector3 m_Position;

            public int m_RecordInterval = 5;
            public int m_StartRandomAt = 80;
            public float m_Inertia = 0.85f;
            public float m_VelDec = 0.95f;
            public int m_SwingAngle = 30;
            public int m_SwingLoop = 100;
        }
        [Range(0, 1000)] public int m_Seed = 0;
        public MoveData m_MoveData;
        public Transform m_StartPosMin;
        public Transform m_StartPosMax;
        public int m_MaxMoveTimes = 3000;
        UCLI_Path m_Path;
        [UCL.Core.PA.UCL_ReadOnly]public float m_PathLength;
        UCL.Core.MathLib.UCL_Random m_Rnd;
        public override Vector3 GetPos(float percent) {
            if(m_Path == null) {
                UpdatePath();
            }
            return m_Path.GetPos(percent);
        }
        public override Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return base.GetRect(dir);
        }
        private void OnValidate() {
            UpdatePath();
        }
        public Color m_PathCol = Color.yellow;
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void UpdatePath() {
            if(m_StartPosMin == null || m_StartPosMax == null) return;
            m_Path = GetRandomPath(m_Seed);
            if(m_Path != null) {
                m_PathLength = m_Path.GetPathLength();
            }
        }

        public int m_RecordSeedStartAt = 0;
        public int m_RecordSeedCount = 10000;
        public int m_MaxRecordCount = 500;
        public float m_RecordPathMinLength = 500f;
        public float m_RecordPathRange = 100f;
        public List<int> m_RecordSeeds;
        //public float m_Length
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RecordSeed() {
            m_RecordSeeds = new List<int>();
            int end_at = m_RecordSeedStartAt + m_RecordSeedCount;
            for(int i = m_RecordSeedStartAt; i < end_at; i++) {
                var path = GenRandomPath(i);
                if(path != null) {
                    float del = path.GetPathLength() - m_RecordPathMinLength;
                    if(del >= 0 && del <= m_RecordPathRange) {
                        m_RecordSeeds.Add(i);
                        if(m_RecordSeeds.Count >= m_MaxRecordCount) return;
                    } else {
                        //Debug.LogWarning("path.GetPathLength():" + path.GetPathLength());
                    }
                }
            }
        }

        public void RandomSeed() {
            m_Seed = Core.MathLib.UCL_Random.Instance.Next();
            UpdatePath();
        }
        protected UCLI_Path GenRandomPath(int seed) {
            if(m_StartPosMin == null || m_StartPosMax == null) return null;
            List<Vector3> points = new List<Vector3>();

            points.Clear();
            float path_len = 0;
            var rnd = new UCL_Random(seed);

            Vector3 min = m_StartPosMin.position;
            Vector3 max = m_StartPosMax.position;
            Vector3 del = max - min;
            float hx = 0.5f * m_StartPosMax.position.x;
            float hy = 0.5f * m_StartPosMax.position.y;
            var sp = rnd.OnRect(del.x, del.y);
            if(sp.x == 0) {
                if(sp.y > hy) {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(-0.2f * Mathf.PI, 0);
                } else {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(0, 0.2f * Mathf.PI);
                }
                //m_MoveData.m_Vel = m_Rnd.OnUnitCircle(-0.2f*Mathf.PI, 0.2f * Mathf.PI);
            } else if(sp.x == del.x) {
                if(sp.y > hy) {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(1f * Mathf.PI, 1.2f * Mathf.PI);
                } else {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(0.8f * Mathf.PI, 1f * Mathf.PI);
                }
                //m_MoveData.m_Vel = m_Rnd.OnUnitCircle(0.8f * Mathf.PI, 1.2f * Mathf.PI);
            } else if(sp.y == 0) {
                if(sp.x > hx) {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(0.5f * Mathf.PI, 0.7f * Mathf.PI);
                } else {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(0.3f * Mathf.PI, 0.5f * Mathf.PI);
                }
                //m_MoveData.m_Vel = m_Rnd.OnUnitCircle(0.3f * Mathf.PI, 0.7f*Mathf.PI);
            } else {
                if(sp.x > hx) {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(1.3f * Mathf.PI, 1.5f * Mathf.PI);
                } else {
                    m_MoveData.m_Vel = rnd.OnUnitCircle(1.5f * Mathf.PI, 1.7f * Mathf.PI);
                }
                //m_MoveData.m_Vel = m_Rnd.OnUnitCircle(1.3f * Mathf.PI, 1.7f * Mathf.PI);
            }
            Vector3 PrevPos = min + sp.ToVec3();

            m_MoveData.m_Vel *= m_MoveData.m_Acc;

            m_MoveData.m_Position = PrevPos;

            points.Add(PrevPos);
            int swing_loop = m_MoveData.m_SwingLoop;//m_MoveData.m_SwingAngle * 2 + 1;
            int loop = swing_loop * 2 + 1;
            float rr = ((m_MoveData.m_SwingAngle * Mathf.Deg2Rad) / swing_loop);
            float start_r = m_MoveData.m_Vel.Radius();
            int start_random_at = m_MoveData.m_StartRandomAt;
            int record_interval = m_MoveData.m_RecordInterval;
            for(int i = 0; i < m_MaxMoveTimes; i++) {
                if(i > start_random_at) {
                    m_MoveData.m_Vel *= m_MoveData.m_VelDec;
                    float r = m_MoveData.m_Vel.Radius();
                    float dr = r - start_r;
                    r += (i % loop - swing_loop) * rr;
                    Vector2 acc_vec = Vector2.zero;
                    if(dr > 0 && dr < Mathf.PI) {
                        acc_vec = rnd.OnUnitCircle(r - m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                            r + m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad);//
                    } else {
                        acc_vec = rnd.OnUnitCircle(r - m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                            r + m_MoveData.m_AngelDel * Mathf.Deg2Rad);//- m_MoveData.m_AngelDel * Mathf.Deg2Rad

                    }
                    m_MoveData.m_Vel += acc_vec * m_MoveData.m_Acc;
                }
                m_MoveData.m_Position += m_MoveData.m_Vel.ToVec3();

                if(m_MoveData.m_Position.x < min.x || m_MoveData.m_Position.x > max.x
                    || m_MoveData.m_Position.y < min.y || m_MoveData.m_Position.y > max.y) {
                    break;
                }
                if(i % record_interval == 0) {
                    float len = (m_MoveData.m_Position - PrevPos).magnitude;
                    path_len += len;
                    points.Add(m_MoveData.m_Position);
                    PrevPos = m_MoveData.m_Position;
                }
            }

            path_len += (m_MoveData.m_Position - PrevPos).magnitude;
            points.Add(m_MoveData.m_Position);
            var path = new CurvePath(points, path_len);
            return path;
        }
        public override UCLI_Path GetRandomPath(int seed) {
            if(m_RecordSeeds == null || m_RecordSeeds.Count == 0) {
                return GenRandomPath(seed);
            }

            var rnd = new UCL_Random(seed);
            return GenRandomPath(m_RecordSeeds[rnd.Next(m_RecordSeeds.Count)]);
        }
        public override float GetPathLength() {
            if(m_RecordSeeds.Count > 0) {
                return m_RecordPathMinLength + 0.5f * m_RecordPathRange;
            }
            if(m_Path != null) return m_Path.GetPathLength();
            return 0;
        }
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            if(m_StartPosMin == null || m_StartPosMax == null) return;
            if(m_Path == null) {
                UpdatePath();
            }

            var path = m_Path as Path;
            if(path == null) return;

            Color inv = new Color(1f - m_PathCol.r, 1f - m_PathCol.g, 1f - m_PathCol.b, 1f);
            path.OnDrawGizmos(m_PathCol, inv);
#endif
        }

    }
}