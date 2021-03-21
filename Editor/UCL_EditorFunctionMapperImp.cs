using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;

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
            #endregion

            #region AssetDatabaseMapper
            AssetDatabaseMapper.InitLoadAssetAtPath(AssetDatabase.LoadAssetAtPath);
            AssetDatabaseMapper.InitGetBuiltinExtraResource(AssetDatabase.GetBuiltinExtraResource);
            AssetDatabaseMapper.InitGetAssetPath(AssetDatabase.GetAssetPath);
            AssetDatabaseMapper.InitLoadMainAssetAtPath(AssetDatabase.LoadMainAssetAtPath);
            #endregion

            #region EditorUtility
            EditorUtilityMapper.InitOpenFilePanel(EditorUtility.OpenFilePanel);
            EditorUtilityMapper.InitOpenFolderPanel(EditorUtility.OpenFolderPanel);
            #endregion

            #region SelectionMapper
            SelectionMapper.InitActiveObject(() => { return UnityEditor.Selection.activeObject; },
                (iObj) => { UnityEditor.Selection.activeObject = iObj; });
            #endregion

            #region Init
            UCL_EditorUpdateManager.Init();
            UCL_EditorCoroutineManager.Init();
            UCL_EditorPlayStateNotifier.Init();
            #endregion
        }
    }
}