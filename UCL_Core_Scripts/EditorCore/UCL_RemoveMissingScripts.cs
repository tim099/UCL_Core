using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_RemoveMissingScripts : MonoBehaviour
    {
#if UNITY_EDITOR
        /// <summary>
        /// Removes all MonoBehaviours with MissingScript
        /// script from the given Transform and all childs.
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RemoveMonoBehavioursWithMissingScript()
        {
            UCL_EditorLib.RemoveMonoBehavioursWithMissingScript(transform);
        }
#endif
    }
}