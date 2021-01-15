using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Physic {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_Raycaster : MonoBehaviour
    {
        public LayerMask m_LayerMask;
        /// <summary>
        /// true then do Raycast every Update
        /// </summary>
        public bool m_AutoRaycast = false;

        public bool m_RayDirByTransform = true;
        public float m_MaxDistance = 10.0f;
        public RaycastHit m_HitInfo = default;
        public Vector3 m_RayDir = default;

        [UCL.Core.ATTR.UCL_FunctionButton]
        virtual public bool Raycast() {
            if(m_RayDirByTransform) {
                m_RayDir = transform.forward;
            }

            var start_pos = transform.position;
            var ray = new Ray(start_pos, m_RayDir);
            bool result = Physics.Raycast(ray, out m_HitInfo, m_MaxDistance, m_LayerMask);
#if UNITY_EDITOR
            Debug.DrawRay(start_pos, m_RayDir.normalized * m_MaxDistance, Color.red);
#endif
            return result;
        }
        private void Update() {
            if(m_AutoRaycast) Raycast();
        }
    }
}


