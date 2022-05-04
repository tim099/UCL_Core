using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class UCL_EditorLib
    {
        /// <summary>
        /// Removes all MonoBehaviours with MissingScript
        /// script from the given Transform and all childs.
        /// </summary>
        /// <param name="iTarget"></param>
        static public void RemoveMonoBehavioursWithMissingScript(Transform iTarget)
        {
#if UNITY_EDITOR
            int aCount = UnityEditor.GameObjectUtility.RemoveMonoBehavioursWithMissingScript(iTarget.gameObject);
            if (aCount > 0)
            {
                Debug.LogError("RemoveMissingScript Count:" + aCount + ",iTarget:" + iTarget.name);
            }
            foreach (Transform aChild in iTarget)
            {
                RemoveMonoBehavioursWithMissingScript(aChild);
            }
#endif
        }
    }
}