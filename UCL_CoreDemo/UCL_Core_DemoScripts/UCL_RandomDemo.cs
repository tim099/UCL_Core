using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.MathLib.Demo {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_RandomDemo : MonoBehaviour {
        public int m_RandTimes = 100;
        public int m_Seed = 0;

        public int m_IntRangeMin = 0;
        public int m_IntRangeMax = 100;

        public float m_FloatRangeMin = 0f;
        public float m_FloatRangeMax = 1f;

        UCL_Random m_Rnd;
        public RandomState m_RndState;
        public int[] m_HitTimes;
        [ATTR.UCL_FunctionButton("Rand Int(log_value = true)" , true)]
        [ATTR.UCL_FunctionButton("Rand Int(log_value = false)",false)]
        public void Rand_Int(bool log_value) {
            if(m_Rnd == null) InitSeed();

            int len = m_IntRangeMax - m_IntRangeMin;
            m_HitTimes = new int[len];
            var min = m_IntRangeMin;
            var max = m_IntRangeMax;
            for(int i = 0; i < m_RandTimes; i++) {
                var val = m_Rnd.Range(min, max);
                m_HitTimes[val - min]++;
                if(log_value) Debug.Log(val);
            }
            for(int i = 0; i < len; i++) {
                Debug.Log("val:" + (i + min) + ",Hit Times:" + m_HitTimes[i]);
            }
        }
        [ATTR.UCL_FunctionButton("Rand Float(log_value = true)", true)]
        [ATTR.UCL_FunctionButton("Rand Float(log_value = false)", false)]
        public void Rand_Float(bool log_value) {
            if(m_Rnd == null) InitSeed();
            MathLib.RangeChecker<float> rangeChecker = new RangeChecker<float>();
            var min = m_FloatRangeMin;
            var max = m_FloatRangeMax;
            for(int i = 0; i < m_RandTimes; i++) {
                var val = m_Rnd.Range(min, max);
                rangeChecker.AddValue(val);
                if(log_value) Debug.Log(val);
            }
            Debug.LogWarning("min:" + rangeChecker.Min.ToString("N5") + ",max:" + rangeChecker.Max.ToString("N5"));
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_OnUnitCircle() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.OnUnitCircle();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_InUnitSquare() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.InUnitSquare() * 2.0f - Vector2.one;
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }
        [ATTR.UCL_FunctionButton]
        public void Rand_OnUnitSquare() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.OnUnitSquare()*2.0f - Vector2.one;
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }
        [Range(0, 1f)] public float m_RectWidth = 1f;
        [Range(0, 1f)] public float m_RectHeight = 1f;

        [ATTR.UCL_FunctionButton]
        public void Rand_InRect() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.InRect(m_RectWidth, m_RectHeight) * 2.0f - Vector2.one;
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }
        [ATTR.UCL_FunctionButton]
        public void Rand_OnRect() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.OnRect(m_RectWidth, m_RectHeight) * 2.0f - Vector2.one;
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_InUnitCircle() {
            if(m_Rnd == null) InitSeed();
            m_RandomPoints.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.InUnitCircle();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_OnUnitSphere() {
            if(m_Rnd == null) InitSeed();
            m_UpdateVal = !m_UpdateVal;
            m_RandomPoints3D.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.OnUnitSphere();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints3D.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_InUnitSphere() {
            if(m_Rnd == null) InitSeed();
            m_UpdateVal = !m_UpdateVal;
            m_RandomPoints3D.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.InUnitSphere();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints3D.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Rand_OnUnitCube() {
            if(m_Rnd == null) InitSeed();
            m_UpdateVal = !m_UpdateVal;
            m_RandomPoints3D.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.OnUnitCube();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints3D.Add(point);
            }
        }
        [ATTR.UCL_FunctionButton]
        public void Rand_InUnitCube() {
            if(m_Rnd == null) InitSeed();
            m_UpdateVal = !m_UpdateVal;
            m_RandomPoints3D.Clear();
            for(int i = 0; i < m_RandTimes; i++) {
                var point = m_Rnd.InUnitCube();
                //Debug.LogWarning("point:" + point);
                m_RandomPoints3D.Add(point);
            }
        }

        [ATTR.UCL_FunctionButton]
        public void InitSeedByTime() {
            Debug.LogWarning("Time:" + System.DateTime.Now.ToLongTimeString());
            m_Seed = MathLib.Crc32.Sum(System.DateTime.Now);
            m_Rnd = new UCL_Random(m_Seed);
        }
        public List<Vector2> m_RandomPoints = new List<Vector2>();
        Core.TextureLib.UCL_Texture2D m_Texture = null;
        public Color m_DotColor = new Color(0, 0, 0, 0.3f);
        [ATTR.UCL_DrawTexture2D]
        public Core.TextureLib.UCL_Texture2D DrawRandomPoints() {
            if(m_Texture == null) {
                m_Texture = new TextureLib.UCL_Texture2D(new Vector2Int(256, 256), TextureFormat.RGB24);
            }
            m_Texture.SetColor(Color.white);
            if(m_RandomPoints != null) {
                foreach(var point in m_RandomPoints) {
                    m_Texture.DrawDot((0.98f*point).ToTextureSpace(), m_DotColor, 1);
                }
            }
            
            return m_Texture;
        }
        public Color m_PointColor3D = Color.green;
        public List<Vector3> m_RandomPoints3D = new List<Vector3>();
        public bool m_UpdateVal;
        [Range(0f,1f)]public float m_Points3D_ShowRange = 1.0f;
        [Range(-1f, 1f)] public float m_Points3D_ShowPos = 0.5f;
        /// <summary>
        /// x=0,y=1,z=2
        /// </summary>
        [Range(0, 2)] public int m_RangeAxis = 1;
        [PA.UCL_ReadOnly] [SerializeField] int m_Points3DShowCount = 0;
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            var prev = Gizmos.color;
            Gizmos.color = m_PointColor3D;
            m_Points3DShowCount = 0;
            if(m_Points3D_ShowRange < 1.0f) {
                float min = m_Points3D_ShowPos - m_Points3D_ShowRange;
                if(min < -1) min = -1;
                float max = m_Points3D_ShowPos + m_Points3D_ShowRange;
                if(max > 1) max = 1;
                foreach(var point in m_RandomPoints3D) {

                    var val = point.GetValue(m_RangeAxis);
                    if(val >= min && val <= max) {
                        m_Points3DShowCount++;
                        Core.UCL_DrawGizmos.DrawSphere(transform.TransformPoint(point), 1f);
                    }
                }
            } else {
                m_Points3DShowCount = m_RandomPoints3D.Count;
                foreach(var point in m_RandomPoints3D) {
                    //Core.UCL_DrawGizmos.DrawConstSizeSphere(transform.TransformPoint(point), 1f);
                    //UCL_DrawGizmos.DrawCube
                    //float pos = 10 * point.y;
                    //pos = pos - Mathf.FloorToInt(pos);
                    //Gizmos.color = pos > 0.5f ? m_PointColor3D : Color.black;//Color.Lerp(m_PointColor3D, Color.black, pos);
                    Core.UCL_DrawGizmos.DrawSphere(transform.TransformPoint(point), 1f);
                    //Gizmos.DrawCube(transform.TransformPoint(point), Vector3.one);
                }
            }


            Gizmos.color = prev;
#endif
        }

        [ATTR.UCL_FunctionButton]
        public void InitSeed() {
            m_Rnd = new UCL_Random(m_Seed);
        }
        [ATTR.UCL_FunctionButton]
        public void SaveState() {
            m_RndState = m_Rnd.GetState();
        }

        [ATTR.UCL_FunctionButton]
        public void LoadState() {
            m_Rnd.SetState(m_RndState);
        }
        private void Awake() {
            
        }
        // Start is called before the first frame update
        void Start() {

        }

        // Update is called once per frame
        void Update() {
            //Debug.LogWarning("Test:" + MathLib.Crc32.Sum(System.DateTime.Now));
        }
    }
}