using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Misc
{
    /// <summary>
    /// Follow target's position
    /// </summary>
    public class UCL_PositionFollower : MonoBehaviour
    {
        public Transform m_Target = null;

        public void FollowPosition()
        {
            if (m_Target == null) return;
            transform.position = m_Target.position;
        }
        private void Awake()
        {
            FollowPosition();
        }
        private void Update()
        {
            FollowPosition();
        }
    }
}

