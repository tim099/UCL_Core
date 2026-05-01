
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/10 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// 要保存到ModResources的哪個位置
        /// </summary>
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();
//#if UNITY_EDITOR
//        private static IList<string> GetBundlesName() => UnityEditor.AssetDatabase.GetAllAssetBundleNames();
//        [UCL.Core.PA.UCL_List(nameof(GetBundlesName))]
//#endif
        public string m_BundleName = "BundleName";

        /// <summary>
        /// 輸出的資料夾
        /// </summary>
        public string FolderPath => m_ModResourcesData.FileSystemFolderPath;

        /// <summary>
        /// Bundle路徑
        /// </summary>
        public string BundlePath => Path.Combine(FolderPath, m_BundleName);

        /// <summary>
        /// Manifest路徑
        /// </summary>
        public string ManifestPath => Path.Combine(FolderPath, $"{m_BundleName}.manifest");

        /// <summary>
        /// 載入的Bundle
        /// </summary>
        //private AssetBundle m_AssetBundle = null;
        /// <summary>
        /// 所有在這個Bundle內的Asset的名稱(路徑)
        /// </summary>
        public List<string> m_AllAssetNames = new List<string>();

        //private ELoadingState m_LoadingState = ELoadingState.None;
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

        public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
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


            if (GUILayout.Button(UCL_LocalizeManager.Get("Load Bundle"), UCL_GUIStyle.ButtonStyle))
            {
                LoadBundle().Forget();
            }
            if (GUILayout.Button(UCL_LocalizeManager.Get("UnloadAllAssetBundles"), UCL_GUIStyle.ButtonStyle))
            {
                UCL_BundleService.UnloadAllAssetBundles(true);
                //AssetBundle.UnloadAllAssetBundles(true);
            }
            return this;
        }

        /// <summary>
        /// Load AssetBundle
        /// </summary>
        /// <returns></returns>
        public async UniTask<AssetBundle> LoadBundle()
        {
            //"file://path/to/your/AssetBundles";
            var bundle = await UCL_BundleService.LoadBundle(BundlePath);
            if (bundle != null)
            {
                m_AllAssetNames = bundle.GetAllAssetNames().ToList();
                //Debug.LogError($"name:{m_AssetBundle.name},AllAssetNames:{m_AllAssetNames.ConcatToString()}");
            }
            return bundle;

            //string path = BundlePath;
            //if (!File.Exists(path))
            //{
            //    Debug.LogError($"LoadManifest path:{path}, !File.Exists(path)");
            //    return null;
            //}
            ////Debug.LogError($"LoadBundle path:{path}");
            ////if (m_LoadingState == ELoadingState.Loading)//還在載入中 等待載入結束
            ////{
            ////    await UniTask.WaitUntil(() => m_LoadingState != ELoadingState.Loading);
            ////}
            
            //if (m_LoadingState != ELoadingState.Complete)
            //{
            //    m_LoadingState = ELoadingState.Loading;
            //    m_AssetBundle = await AssetBundle.LoadFromFileAsync(path);
            //    if (m_AssetBundle != null)
            //    {
            //        m_AllAssetNames = m_AssetBundle.GetAllAssetNames().ToList();
            //        //Debug.LogError($"name:{m_AssetBundle.name},AllAssetNames:{m_AllAssetNames.ConcatToString()}");
            //    }
            //    else
            //    {
            //        Debug.LogError($"LoadBundle path:{path}, Fail");
            //    }
            //    m_LoadingState = ELoadingState.Complete;
            //}


            //return m_AssetBundle;


            //AssetBundle.UnloadAllAssetBundles(true);

            //string manifestBundleURL = path;//$"file://{path}";  
            //AssetBundle manifestBundle = await AssetBundle.LoadFromFileAsync(manifestBundleURL);
            //var allAssetNames = manifestBundle.GetAllAssetNames();
            //Debug.LogError($"name:{manifestBundle.name},bundles:{allAssetNames.ConcatToString()}");
            //return manifestBundle;
            //foreach(var assetName in allAssetNames)
            //{
            //    var asset = manifestBundle.LoadAsset(assetName);
            //    Debug.LogError($"assetName:{assetName},asset:{asset.name}, Type:{asset.GetType().Name}");
            //    if (Application.isPlaying)
            //    {
            //        if(asset is GameObject obj)
            //        {
            //            GameObject.Instantiate(obj);//Test
            //        }
            //    }
            //}
            //AssetBundleManifest manifest = manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");

            //Debug.LogError($"manifest:{manifest.name},bundles:{manifest.GetAllAssetBundles().ConcatToString()}");
        }
        public async UniTask<T> LoadAsset<T>(string assetName) where T : UnityEngine.Object
        {
            T asset = null;
            try
            {
                var bundle = await LoadBundle();
                if (bundle == null)
                {
                    return null;
                }
                asset = bundle.LoadAsset<T>(assetName);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            return asset;
        }
        #region Editor
#if UNITY_EDITOR
        private void BuildBundle(UnityEditor.BuildTarget buildTarget)
        {
            string outputPath = FolderPath;

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

        public override UCL.Core.JsonLib.JsonData SerializeToJson()
        {
            return UCL.Core.JsonLib.JsonConvert.SaveFieldsToJsonUnityVer(this);
        }
        public override void DeserializeFromJson(UCL.Core.JsonLib.JsonData iJson)
        {
            UCL.Core.JsonLib.JsonConvert.LoadFieldFromJsonUnityVer(this, iJson);
            UCLI_AssetEntry.s_DeserializeAction?.Invoke(this);
        }

        [UCL.Core.PA.UCL_List(nameof(GetAllAssetNames))]
        public string m_AssetName = string.Empty;

        public override bool IsEmpty
        {
            get
            {
                if (base.IsEmpty) return true;
                return string.IsNullOrEmpty(m_AssetName);
                //try
                //{
                //    var data = GetData();
                //    return data.IsEmpty;
                //}
                //catch //Data not exist!!
                //{
                //    return true;
                //}
            }
        }

        public IList<string> GetAllAssetNames()
        {
            try
            {
                var data = GetData();
                return data.m_AllAssetNames;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return Array.Empty<string>();
        }

        public async UniTask<T> LoadAsset<T>() where T : UnityEngine.Object 
        {
            if (IsEmpty) return null;
            try
            {
                var data = GetData();
                return await data.LoadAsset<T>(m_AssetName);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            return null;
        }
    }


    public static class UCL_BundleService
    {
        public class UCL_LoadBundleSetting
        {
            public ELoadingState m_LoadingState = ELoadingState.None;
            /// <summary>
            /// 載入的Bundle
            /// </summary>
            public AssetBundle m_AssetBundle = null;

            /// <summary>
            /// Load AssetBundle
            /// </summary>
            /// <returns></returns>
            public async UniTask<AssetBundle> LoadBundle(string bundlePath)
            {
                if (!File.Exists(bundlePath))
                {
                    Debug.LogError($"LoadBundle bundlePath:{bundlePath}, !File.Exists(path)");
                    return null;
                }

                if (m_LoadingState != ELoadingState.Complete)
                {
                    m_LoadingState = ELoadingState.Loading;
                    m_AssetBundle = await AssetBundle.LoadFromFileAsync(bundlePath);
                    m_LoadingState = ELoadingState.Complete;
                }

                return m_AssetBundle;
            }
            public async UniTask Dispose()
            {
                if (m_LoadingState == ELoadingState.Loading)
                {
                    await UniTask.WaitUntil(() => m_LoadingState == ELoadingState.Complete);
                }

                if (m_AssetBundle != null)
                {
                    m_AssetBundle.Unload(true);
                }
                m_AssetBundle = null;
                m_LoadingState = ELoadingState.Disposed;
            }
        }
        private static Dictionary<string, UCL_LoadBundleSetting> s_LoadedBundles = new();

        /// <summary>
        /// Unloads all currently loaded AssetBundles.
        /// </summary>
        /// <param name="unloadAllObjects">Determines whether the current instances of 
        /// objects loaded from AssetBundles will also be unloaded.</param>
        public static void UnloadAllAssetBundles(bool unloadAllObjects)
        {
            foreach(var bundle in s_LoadedBundles.Values)
            {
                bundle.Dispose().Forget();
            }
            s_LoadedBundles.Clear();
            //AssetBundle.UnloadAllAssetBundles(unloadAllObjects);
        }

        /// <summary>
        /// Load AssetBundle
        /// </summary>
        /// <returns></returns>
        public static async UniTask<AssetBundle> LoadBundle(string bundlePath)
        {
            if (!s_LoadedBundles.ContainsKey(bundlePath))//Load bundle
            {
                var setting = s_LoadedBundles[bundlePath] = new UCL_LoadBundleSetting();
                await setting.LoadBundle(bundlePath);
            }
            {
                var setting = s_LoadedBundles[bundlePath];
                if (setting.m_LoadingState == ELoadingState.Loading)
                {
                    await UniTask.WaitUntil(() => setting.m_LoadingState != ELoadingState.Loading);//等待載入完成
                }
                return setting.m_AssetBundle;
            }
        }

    }
}