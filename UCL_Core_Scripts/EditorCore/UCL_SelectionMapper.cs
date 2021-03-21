using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    static public class SelectionMapper
    {
        #region activeObject
        public static System.Func<UnityEngine.Object> m_GetActiveObject = null;
        public static System.Action<UnityEngine.Object> m_SetActiveObject = null;
        public static void InitActiveObject(System.Func<UnityEngine.Object> iGetActiveObject,
            System.Action<UnityEngine.Object> iSetActiveObject)
        {
            m_GetActiveObject = iGetActiveObject;
            m_SetActiveObject = iSetActiveObject;
        }
        /// <summary>
        /// Mapper to UnityEditor.Selection.activeObject, Only work in Editor
        /// </summary>
        public static UnityEngine.Object activeObject
        {
            get
            {
                if (m_GetActiveObject == null) return null;
                return m_GetActiveObject.Invoke();
            }
            set
            {
                if (m_SetActiveObject == null) return;
                m_SetActiveObject.Invoke(value);
            }
        }
        #endregion
    }
}