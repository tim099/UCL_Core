
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/10 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_BundleAsset)]
    public class UCL_BundleAsset : UCL_Asset<UCL_BundleAsset>, IDisposable, UCLI_FieldOnGUI
    {
        /// <summary>
        /// 要把哪個資料夾打包為bundle
        /// </summary>
        [UCL.Core.PA.UCL_FolderExplorer(UCL.Core.PA.ExplorerType.AssetsRoot)]
        public string m_SourceFolder;

        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();
//#if UNITY_EDITOR
//        private static IList<string> GetBundlesName() => UnityEditor.AssetDatabase.GetAllAssetBundleNames();
//        [UCL.Core.PA.UCL_List(nameof(GetBundlesName))]
//#endif
        public string m_BundleName = "BundleName";

        public bool IsEmpty => m_ModResourcesData.IsEmpty;


        /// <summary>
        /// Preview(OnGUI)
        /// </summary>
        /// <param name="iIsShowEditButton">Show edit button in preview window?</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            //GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            //GUILayout.EndHorizontal();
        }
        public UCL_BundleAsset()
        {
            ID = "New Bundle";
        }

        public void Dispose()
        {

        }

        public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            UCL_GUILayout.DrawField(this, iDataDic.GetSubDic("Data"), iFieldName);

#if UNITY_EDITOR

            UnityEditor.BuildTarget buildTarget;
            using (var scope = new GUILayout.HorizontalScope())
            {
                GUILayout.Label("BuildTarget", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                var buildDic = iDataDic.GetSubDic("SelectedBuildTarget");
                buildTarget = buildDic.GetData(nameof(buildTarget), UnityEditor.BuildTarget.StandaloneWindows);
                buildTarget = UCL_GUILayout.PopupAuto(buildTarget, buildDic, "PopupAutoBuildTarget");
                buildDic.SetData(nameof(buildTarget), buildTarget);
            }




            //只有在Editor內可以build
            if (GUILayout.Button(UCL_LocalizeManager.Get("Build Bundle"), UCL_GUIStyle.ButtonStyle))
            {
                //RefreshStatus();
                BuildBundle(buildTarget);
            }
#endif
            return this;
        }

        #region Editor
#if UNITY_EDITOR
        private void BuildBundle(UnityEditor.BuildTarget buildTarget)
        {
            string outputPath = m_ModResourcesData.FileSystemFolderPath;

            Debug.LogWarning($"BuildBundle m_SourceFolder:{m_SourceFolder},outputPath:{outputPath}");
            System.IO.Directory.CreateDirectory(outputPath);

            //string[] assetPaths = UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { m_SourceFolder });// 獲取指定資料夾內的所有Prefab
            string[] assetPaths = UnityEditor.AssetDatabase.FindAssets("", new[] { m_SourceFolder });//Select All

            UnityEditor.AssetBundleBuild[] buildMap = new UnityEditor.AssetBundleBuild[1];
            buildMap[0].assetBundleName = m_BundleName;
            buildMap[0].assetNames = new string[assetPaths.Length];

            for (int i = 0; i < assetPaths.Length; i++)
            {
                buildMap[0].assetNames[i] = UnityEditor.AssetDatabase.GUIDToAssetPath(assetPaths[i]);
            }


            //Debug.LogError($"map:{map.AllFieldToString()}");
            Debug.LogWarning($"buildMap:{buildMap.AllFieldToString()}");

            AssetBundleManifest manifest = UnityEditor.BuildPipeline.BuildAssetBundles(outputPath, buildMap,
                UnityEditor.BuildAssetBundleOptions.None, buildTarget);
            Debug.LogWarning($"assetBundleManifest:{manifest.name.AllFieldToString()}");
        }

        //static void BuildAllAssetBundles()
        //{
        //    string assetBundleDirectory = "Assets/AssetBundles";
        //    if (!System.IO.Directory.Exists(assetBundleDirectory))
        //    {
        //        System.IO.Directory.CreateDirectory(assetBundleDirectory);
        //    }

        //    // 獲取指定資料夾內的所有Prefab
        //    string[] assetPaths = UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/YourFolderName" });
        //    UnityEditor.AssetBundleBuild[] buildMap = new UnityEditor.AssetBundleBuild[1];
        //    buildMap[0].assetBundleName = "mybundle";
        //    buildMap[0].assetNames = new string[assetPaths.Length];

        //    for (int i = 0; i < assetPaths.Length; i++)
        //    {
        //        buildMap[0].assetNames[i] = UnityEditor.AssetDatabase.GUIDToAssetPath(assetPaths[i]);
        //    }

        //    UnityEditor.BuildPipeline.BuildAssetBundles(assetBundleDirectory, buildMap, UnityEditor.BuildAssetBundleOptions.None, UnityEditor.BuildTarget.StandaloneWindows);
        //}
#endif
        #endregion
    }

    [System.Serializable]
    public class UCL_BundleEntry : UCL_AssetEntryDefault<UCL_BundleAsset>
    {
        public const string DefaultID = "Default";
        public UCL_BundleEntry() { m_ID = DefaultID; }
        public UCL_BundleEntry(string iID) { m_ID = iID; }

        public UCL_ModResourcesData Data => GetData().m_ModResourcesData;
        public override bool IsEmpty
        {
            get
            {
                if (base.IsEmpty) return true;
                try
                {
                    var data = GetData();
                    return data.IsEmpty;
                }
                catch //Data not exist!!
                {
                    return true;
                }
            }
        }
    }
}