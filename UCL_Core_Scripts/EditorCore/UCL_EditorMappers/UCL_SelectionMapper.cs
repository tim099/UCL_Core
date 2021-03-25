namespace UCL.Core.EditorLib
{
    static public class SelectionMapper
    {
        #region activeObject
        private static System.Func<UnityEngine.Object> m_GetActiveObject = null;
        private static System.Action<UnityEngine.Object> m_SetActiveObject = null;
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

        #region activeGameObject
        private static System.Func<UnityEngine.GameObject> m_GetActiveGameObject = null;
        private static System.Action<UnityEngine.GameObject> m_SetActiveGameObject = null;
        public static void InitActiveGameObject(System.Func<UnityEngine.GameObject> iGetActiveGameObject,
            System.Action<UnityEngine.GameObject> iSetActiveGameObject)
        {
            m_GetActiveGameObject = iGetActiveGameObject;
            m_SetActiveGameObject = iSetActiveGameObject;
        }
        /// <summary>
        /// Mapper to UnityEditor.Selection.activeGameObject, Only work in Editor
        /// </summary>
        public static UnityEngine.GameObject activeGameObject
        {
            get
            {
                if (m_GetActiveGameObject == null) return null;
                return m_GetActiveGameObject.Invoke();
            }
            set
            {
                if (m_SetActiveGameObject == null) return;
                m_SetActiveGameObject.Invoke(value);
            }
        }
        #endregion
    }
}