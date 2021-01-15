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
            public int m_RecordInterval = 5;
            public int m_StartRandomAt = 80;
            public float m_Inertia = 0.85f;
            public float m_VelDec = 0.95f;
            public int m_SwingAngle = 30;
            public int m_SwingLoop = 100;
        }
        [Range(0, 1000)] public int m_Seed = 0;
        public MoveData m_MoveData;

        [Header("Start Position Range")]
        public bool m_UpdateSizeOnValidate = true;
        public Transform m_StartPosMin;
        public Transform m_StartPosMax;
        public Vector3 m_Size;
        [Space(10)]

        public bool m_DrawGizmos = true;
        public int m_MaxMoveTimes = 3000;
        UCLI_Path m_Path;
        [UCL.Core.PA.UCL_ReadOnly] public float m_PathLength;
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
#if UNITY_EDITOR
            if(m_UpdateSizeOnValidate && !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode
                && !UnityEditor.EditorApplication.isUpdating && transform.lossyScale != Vector3.zero) {
                UpdateSize();
            }
            UpdatePath();
#endif
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


        [ATTR.UCL_FunctionButton]
        public void UpdateSize() {
            if(m_StartPosMax == null || m_StartPosMin == null) return;
            m_Size = m_StartPosMin.InverseTransformPoint(m_StartPosMax.transform.position);
        }

        [ATTR.UCL_DrawString]
        virtual protected string RecordSeedCount() {
            if(m_RecordSeeds == null) return "RecordSeedCount: 0";
            return "RecordSeedCount: " + m_RecordSeeds.Count;
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RecordSeed() {
            UpdateSize();
            m_RecordSeeds = new List<int>();
            int end_at = m_RecordSeedStartAt + m_RecordSeedCount;
            for(int i = m_RecordSeedStartAt; i < end_at; i++) {
                var path = GenRandomPath(i);
                if(path != null) {
                    float del = path.GetPathLength() - m_RecordPathMinLength;
                    if(del >= 0 && del <= m_RecordPathRange) {
                        m_RecordSeeds.Add(i);
                        if(m_RecordSeeds.Count >= m_MaxRecordCount) return;
                    }
                }
            }
        }
        [ATTR.UCL_FunctionButton]
        public void ClearRecordSeeds() {
            if(m_RecordSeeds == null) return;
            m_RecordSeeds.Clear();
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
            var size = m_Size;
            bool inv_x = false;
            bool inv_y = false;
            if(size.x < 0) {
                inv_x = true;
                size.x = -size.x;
            }
            if(size.y < 0) {
                inv_y = true;
                size.y = -size.y;
            }
            float hx = 0.5f * size.x;
            float hy = 0.5f * size.y;
            var sp = rnd.OnRect(size.x, size.y);

            Vector2 vel = Vector2.zero;
            if(sp.x == 0) {
                if(sp.y > hy) {
                    vel = rnd.OnUnitCircle(-0.2f * Mathf.PI, 0);
                } else {
                    vel = rnd.OnUnitCircle(0, 0.2f * Mathf.PI);
                }
            } else if(sp.x == size.x) {
                if(sp.y > hy) {
                    vel = rnd.OnUnitCircle(1f * Mathf.PI, 1.2f * Mathf.PI);
                } else {
                    vel = rnd.OnUnitCircle(0.8f * Mathf.PI, 1f * Mathf.PI);
                }
            } else if(sp.y == 0) {
                if(sp.x > hx) {
                    vel = rnd.OnUnitCircle(0.5f * Mathf.PI, 0.7f * Mathf.PI);
                } else {
                    vel = rnd.OnUnitCircle(0.3f * Mathf.PI, 0.5f * Mathf.PI);
                }
            } else {
                if(sp.x > hx) {
                    vel = rnd.OnUnitCircle(1.3f * Mathf.PI, 1.5f * Mathf.PI);
                } else {
                    vel = rnd.OnUnitCircle(1.5f * Mathf.PI, 1.7f * Mathf.PI);
                }
            }
            Vector2 pos = sp;
            System.Func<Vector2, Vector3> ToWorldSpace = null;
            if(inv_x && inv_y) {
                ToWorldSpace =
                    (o) => {
                        o.x = -o.x;
                        o.y = -o.y;
                        return m_StartPosMin.TransformPoint(o);
                    };
            } else if(inv_x) {
                ToWorldSpace =
                    (o) => {
                        o.x = -o.x;
                        return m_StartPosMin.TransformPoint(o);
                    };
            } else if(inv_y) {
                ToWorldSpace =
                    (o) => {
                        o.y = -o.y;
                        return m_StartPosMin.TransformPoint(o);
                    };
            } else {
                ToWorldSpace =
                    (o) => {
                        return m_StartPosMin.TransformPoint(o);
                    };
            }

            float angle = m_StartPosMin.rotation.eulerAngles.z;

            Vector3 PrevPos = ToWorldSpace(pos);
            float acc = m_MoveData.m_Acc;// * (1f/m_StartPosMin.lossyScale.x);
            vel *= acc;
#if PathDebug
            Debug.LogWarning("m_Size:" + this.m_Size + ",sp:" + sp + ",vel:" + vel);
#endif
            points.Add(PrevPos);
            int swing_loop = m_MoveData.m_SwingLoop;
            int loop = swing_loop * 2 + 1;
            float rr = ((m_MoveData.m_SwingAngle * Mathf.Deg2Rad) / swing_loop);
            float start_r = vel.Radius();
            int start_random_at = m_MoveData.m_StartRandomAt;
            int record_interval = m_MoveData.m_RecordInterval;
            var vel_dec = m_MoveData.m_VelDec;
            for(int i = 0; i < m_MaxMoveTimes; i++) {
                if(i > start_random_at) {
                    vel *= vel_dec;
                    float r = vel.Radius();
                    float dr = r - start_r;
                    r += (i % loop - swing_loop) * rr;
                    Vector2 acc_vec = Vector2.zero;
                    if(dr > 0 && dr < Mathf.PI) {
                        acc_vec = rnd.OnUnitCircle(r - m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                            r + m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad);
                    } else {
                        acc_vec = rnd.OnUnitCircle(r - m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                            r + m_MoveData.m_AngelDel * Mathf.Deg2Rad);
                    }
                    vel += acc_vec * acc;
                }
                pos += vel;

                if(pos.x < 0 || pos.x > size.x
                    || pos.y < 0 || pos.y > size.y) {
                    break;
                }
                if(i % record_interval == 0) {
                    var cur_pos = ToWorldSpace(pos);
                    float len = (cur_pos - PrevPos).magnitude;
                    path_len += len;
                    points.Add(cur_pos);
                    PrevPos = cur_pos;
                }
            }
            {
                var cur_pos = ToWorldSpace(pos);
                path_len += (cur_pos - PrevPos).magnitude;
                points.Add(cur_pos);
            }
#if PathDebug
            for(int i = 0; i < points.Count; i++) {
                Debug.LogWarning("" + i + ":" + points[i]);
            }
#endif
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
            if(!m_DrawGizmos) return;

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