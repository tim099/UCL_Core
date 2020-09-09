using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public class UCL_Path : MonoBehaviour {
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
        /// Get the Rect cover hole path
        /// </summary>
        /// <param name="dir">the direction of rect(etc. xy</param>
        /// <returns></returns>
        virtual public Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return Rect.zero;
        }

    }
}