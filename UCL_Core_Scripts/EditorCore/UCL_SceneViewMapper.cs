using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class SceneViewMapper
    {
        #region CurrentDrawingSceneViewCamera
        private static System.Func<Camera> m_GetCurrentDrawingSceneViewCamera = null;
        public static Camera CurrentDrawingSceneViewCamera
        {
            get
            {
                if (m_GetCurrentDrawingSceneViewCamera == null) return null;
                return m_GetCurrentDrawingSceneViewCamera.Invoke();
            }
        }
        public static void InitGetCurrentDrawingSceneViewCamera(System.Func<Camera> iGetCurrentDrawingSceneViewCamera)
        {
            m_GetCurrentDrawingSceneViewCamera = iGetCurrentDrawingSceneViewCamera;
        }
        #endregion
        #region CurrentDrawingSceneViewPosition
        private static System.Func<Rect> m_GetCurrentDrawingSceneViewPosition = null;
        public static Rect CurrentDrawingSceneViewPosition
        {
            get
            {
                if (m_GetCurrentDrawingSceneViewPosition == null) return default;
                return m_GetCurrentDrawingSceneViewPosition.Invoke();
            }
        }
        public static void InitGetCurrentDrawingSceneViewPosition(System.Func<Rect> iGetCurrentDrawingSceneViewPosition)
        {
            m_GetCurrentDrawingSceneViewPosition = iGetCurrentDrawingSceneViewPosition;
        }
        #endregion
    }
}