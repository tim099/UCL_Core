using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public interface UCLI_Path {
        Vector3 GetPos(float percent);
        float GetPathLength();
        Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy);
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
    }
}