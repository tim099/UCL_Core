using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib
{
    [RequireComponent(typeof(Camera))]
    public class UCL_CameraRaycaster : UCL.Core.Physic.UCL_Raycaster
    {
        public Transform m_HitPosTarget = null;//Will move target to hit position when ray hit!!
        public Camera m_Camera = null;
        private void Reset() {
#if UNITY_EDITOR
            if(m_Camera == null) {
                m_Camera = GetComponent<Camera>();
            }
#endif
        }
        public override bool Raycast() {
            if(m_Camera == null) return false;

            var start_pos = transform.position;
            var ray = m_Camera.ScreenPointToRay(Input.mousePosition);
            m_RayDir = ray.direction;
            bool result = Physics.Raycast(ray, out m_HitInfo, m_MaxDistance, (int)m_LayerMask);

            if(result) {
#if UNITY_EDITOR
                Debug.DrawRay(ray.origin, ray.direction * Vector3.Distance(ray.origin, m_HitInfo.point), Color.yellow);
                Debug.DrawRay(m_HitInfo.point, 10.0f * m_HitInfo.normal, Color.green);
#endif
                if(m_HitPosTarget) {
                    m_HitPosTarget.position = m_HitInfo.point;
                }
            } else {
#if UNITY_EDITOR
                Debug.DrawRay(ray.origin, m_MaxDistance * ray.direction, Color.red);
#endif
                m_HitInfo.point = new Vector3(-1, -1, -1);
            }

            return result;
        }
    }
}