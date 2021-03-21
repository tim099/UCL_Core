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
        }
    }
}