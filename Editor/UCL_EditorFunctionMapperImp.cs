using UnityEditor;

namespace UCL.Core.EditorLib
{
    [UnityEditor.InitializeOnLoad]
    public static class EditorFunctionMapperImp
    {
        static EditorFunctionMapperImp()
        {
            #region EditorApplication
            EditorApplication.update += EditorApplicationMapper.Update;
            EditorApplication.playModeStateChanged += (iPlayMode) =>
            {
                switch (iPlayMode)
                {
                    case PlayModeStateChange.EnteredEditMode:
                        {
                            EditorApplicationMapper.PlayModeStateChanged(PlayModeStateChangeMapper.EnteredEditMode);
                            break;
                        }
                    case PlayModeStateChange.EnteredPlayMode:
                        {
                            EditorApplicationMapper.PlayModeStateChanged(PlayModeStateChangeMapper.EnteredPlayMode);
                            break;
                        }
                    case PlayModeStateChange.ExitingEditMode:
                        {
                            EditorApplicationMapper.PlayModeStateChanged(PlayModeStateChangeMapper.ExitingEditMode);
                            break;
                        }
                    case PlayModeStateChange.ExitingPlayMode:
                        {
                            EditorApplicationMapper.PlayModeStateChanged(PlayModeStateChangeMapper.ExitingPlayMode);
                            break;
                        }
                }

            };
            EditorApplicationMapper.InitIsPlaying(() => EditorApplication.isPlaying, (iVal)=> { EditorApplication.isPlaying = iVal; });
            EditorApplicationMapper.InitIsPaused(() => EditorApplication.isPaused, (iVal) => { EditorApplication.isPaused = iVal; });
            EditorApplicationMapper.InitIsCompiling(() => EditorApplication.isCompiling);
            EditorApplicationMapper.InitIsPlayingOrWillChangePlaymode(() => EditorApplication.isPlayingOrWillChangePlaymode);
            EditorApplicationMapper.InitIsUpdating(() => EditorApplication.isUpdating);
            #endregion

            #region AssetDatabaseMapper
            AssetDatabaseMapper.InitLoadAssetAtPath(AssetDatabase.LoadAssetAtPath);
            AssetDatabaseMapper.InitGetBuiltinExtraResource(AssetDatabase.GetBuiltinExtraResource);
            AssetDatabaseMapper.InitGetAssetPath(AssetDatabase.GetAssetPath);
            AssetDatabaseMapper.InitLoadMainAssetAtPath(AssetDatabase.LoadMainAssetAtPath);
            AssetDatabaseMapper.InitRefresh(AssetDatabase.Refresh);
            AssetDatabaseMapper.InitContains(AssetDatabase.Contains);
            AssetDatabaseMapper.InitCreateAsset(AssetDatabase.CreateAsset);
            #endregion

            #region EditorUtility
            EditorUtilityMapper.InitOpenFilePanel(EditorUtility.OpenFilePanel);
            EditorUtilityMapper.InitOpenFolderPanel(EditorUtility.OpenFolderPanel);
            EditorUtilityMapper.InitCopySerialized(EditorUtility.CopySerialized);
            EditorUtilityMapper.InitSetDirty(EditorUtility.SetDirty);
            EditorUtilityMapper.InitDisplayProgressBar(EditorUtility.DisplayProgressBar);
            EditorUtilityMapper.InitClearProgressBar(EditorUtility.ClearProgressBar);
            #endregion

            #region SelectionMapper
            SelectionMapper.InitActiveObject(() => { return UnityEditor.Selection.activeObject; },
                (iObj) => { UnityEditor.Selection.activeObject = iObj; });
            SelectionMapper.InitActiveGameObject(() => { return UnityEditor.Selection.activeGameObject; },
                (iObj) => { UnityEditor.Selection.activeGameObject = iObj; });
            #endregion

            #region MonoScriptMapper
            MonoScriptMapper.InitFromMonoBehaviour(MonoScript.FromMonoBehaviour);
            MonoScriptMapper.InitFromScriptableObject(MonoScript.FromScriptableObject);
            #endregion

            #region SceneViewMapper
            SceneViewMapper.InitGetCurrentDrawingSceneViewCamera(() => SceneView.currentDrawingSceneView.camera);
            SceneViewMapper.InitGetCurrentDrawingSceneViewPosition(() => SceneView.currentDrawingSceneView.position);
            #endregion

            #region HandlesMapper
            HandlesMapper.InitLabel(Handles.Label);
            HandlesMapper.InitBeginGUI(Handles.BeginGUI);
            HandlesMapper.InitEndGUI(Handles.EndGUI);
            #endregion

            #region EditorGUIMapper
            EditorGUIMapper.InitPopup(EditorGUI.Popup);
            #endregion

            #region Init
            UCL_EditorUpdateManager.Init();
            UCL_EditorCoroutineManager.Init();
            UCL_EditorPlayStateNotifier.Init();
            #endregion
        }
    }
}