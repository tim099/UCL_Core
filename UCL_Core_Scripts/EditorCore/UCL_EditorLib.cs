using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class UCL_EditorLib
    {
        /// <summary>
        /// Removes all MonoBehaviours with a missing script from the given Transform and all childs.
        /// </summary>
        /// <param name="iTarget"></param>
        static public void RemoveMonoBehavioursWithMissingScript(Transform iTarget)
        {
#if UNITY_EDITOR
            UnityEditor.GameObjectUtility.RemoveMonoBehavioursWithMissingScript(iTarget.gameObject);
            foreach (Transform aChild in iTarget)
            {
                RemoveMonoBehavioursWithMissingScript(aChild);

            }
#endif
        }
    }
}