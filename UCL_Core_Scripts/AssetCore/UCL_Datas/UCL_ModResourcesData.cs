
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 20:05
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class UCL_ModResourcesService
    {
        public enum DataType
        {
            Default = 0,
            Sprite,
        }

        private static Dictionary<string, LoadedData> s_LoadedDatas = new Dictionary<string, LoadedData>();

        #region LoadedData
        public class LoadedData : IDisposable
        {
            public string m_FilePath;

            public List<UnityEngine.Object> m_CreatedAssets = new List<UnityEngine.Object>();

            virtual public DataType DataType => DataType.Default;

            virtual public void Init(string iPath)
            {
                m_FilePath = iPath;
            }
            virtual public void Dispose()
            {
                foreach(var aAsset in m_CreatedAssets)
                {
                    if(aAsset != null)
                    {
                        UnityEngine.Object.Destroy(aAsset);
                    }
                }
                m_CreatedAssets.Clear();
            }
        }
        public class LoadedSpriteData : LoadedData
        {
            const int TextureIndex = 0;
            const int SpriteIndex = TextureIndex + 1;

            override public DataType DataType => DataType.Sprite;

            override public void Init(string iPath)
            {
                base.Init(iPath);
                if (!File.Exists(iPath))
                {
                    Debug.LogError($"LoadedSpriteData.Init !File.Exists,iPath:{iPath}");
                    return;
                }

                var aBytes = File.ReadAllBytes(iPath);
                Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
                m_CreatedAssets.Add(aTexture);
                Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
                m_CreatedAssets.Add(aSprite);
            }
            public Sprite Sprite
            {
                get
                {
                    if (m_CreatedAssets.Count > SpriteIndex)
                    {
                        return m_CreatedAssets[SpriteIndex] as Sprite;
                    }
                    return null;
                }
            }
            public Texture2D Texture2D
            {
                get
                {
                    if (m_CreatedAssets.Count > TextureIndex)
                    {
                        return m_CreatedAssets[TextureIndex] as Texture2D;
                    }
                    return null;
                }
            }
        }
        #endregion


        public static Sprite LoadSprite(string iPath)
        {
            //Debug.LogError($"LoadSprite iPath:{iPath}");
            if (!s_LoadedDatas.ContainsKey(iPath))
            {
                try
                {
                    LoadedSpriteData aLoadedData = new LoadedSpriteData();
                    s_LoadedDatas[iPath] = aLoadedData;
                    aLoadedData.Init(iPath);
                }
                catch(System.Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogError($"LoadSprite iPath:{iPath},Exception:{ex}");
                    return null;
                }
            }


            var aData = s_LoadedDatas[iPath];
            if (aData is LoadedSpriteData aSpriteData)
            {
                return aSpriteData.Sprite;
            }
            else
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite aData.GetType().FullName:{aData.GetType().FullName}");
                return null;
            }
        }

        static UCL_ModResourcesService()
        {
            //Debug.LogError("UCL_ModResourcesService.InitializeOnLoad");
            EditorLib.EditorApplicationMapper.playModeStateChanged += (iPlayModeStateChangeMapper) =>
            {
                //Debug.LogError($"playModeStateChanged iPlayModeStateChangeMapper:{iPlayModeStateChangeMapper}");
                switch (iPlayModeStateChangeMapper)
                {
                    case EditorLib.PlayModeStateChangeMapper.ExitingPlayMode:
                        {
                            ReleaseAll();
                            break;
                        }
                }
            };
        }

        public static void ReleaseAll()
        {
            //UnityEditor.EditorApplication.playModeStateChanged
            foreach (var aData in s_LoadedDatas.Values)
            {
                aData.Dispose();
            }
            s_LoadedDatas.Clear();
        }
        public static void Release(string iKey)
        {
            if (s_LoadedDatas.ContainsKey(iKey))
            {
                s_LoadedDatas[iKey].Dispose();
                s_LoadedDatas.Remove(iKey);
            }
        }

    }

    /// <summary>
    /// All resources in ModResources can be loaded by UCL_ModResourcesData
    /// </summary>
    [System.Serializable]
    public class UCL_ModResourcesData : UCL_Data, UCL.Core.UCLI_NameOnGUI
    {
        [ReadOnly(true)]
        public string m_ModuleID;

        [UCL.Core.PA.UCL_FolderExplorer(typeof(UCL_ModuleService), UCL_ModuleService.ReflectKeyModResourcesPath)]
        public string m_FolderPath;
        #region ReflectionGetAllFileNames
        const string ReflectionID_GetAllFileNames = "GetAllFileNames";
        public List<string> GetAllFileNames()
        {
            m_ModuleID = UCL_ModuleService.CurEditModuleID;

            string aPath = FileSystemFolderPath;
            var aFileDatas = UCL.Core.FileLib.Lib.GetFilesName(aPath, "*", System.IO.SearchOption.TopDirectoryOnly);
            List<string> aFileNames = new List<string>() { string.Empty };//Can select null(Empty)
            aFileNames.Append(aFileDatas);
            return aFileNames;
        }
        #endregion
        /// <summary>
        /// 檔案名稱
        /// </summary>
        [UCL.Core.PA.UCL_List(ReflectionID_GetAllFileNames)]
        public string m_FileName = string.Empty;


        public override string Key => FilePath;
        public override bool IsEmpty => string.IsNullOrEmpty(m_FileName);
        public string FileSystemFolderPath
        {
            get
            {
                if (string.IsNullOrEmpty(m_ModuleID))
                {
                    m_ModuleID = UCL_ModuleService.CurEditModuleID;
                }
                string aPath = UCL_ModuleService.GetModResourcesPath(m_ModuleID);
                if(string.IsNullOrEmpty(m_FolderPath))
                {
                    return aPath;
                }
                return Path.Combine(aPath, m_FolderPath);
            }
        }
        public string FilePath => Path.Combine(FileSystemFolderPath, m_FileName);

        //~UCL_ModResourcesData()
        //{
        //    Release();
        //}

        /// <summary>
        /// Release Object load from UCL_Data
        /// </summary>
        /// <param name=""></param>
        public override void Release()
        {
            UCL_ModResourcesService.Release(FilePath);
        }
        override public Sprite GetSprite()
        {
            if (IsEmpty)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSprite IsEmpty!,FileSystemFolderPath:{FileSystemFolderPath}");
                return null;
            }
            return UCL_ModResourcesService.LoadSprite(FilePath);
        }
        override public UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return default;
        }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override async UniTask<Sprite> LoadSpriteAsync(CancellationToken iToken)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            return GetSprite();
        }
        virtual public void NameOnGUI(UCL.Core.UCL_ObjectDictionary iDataDic, string iDisplayName)
        {
            {
                GUILayout.Label(iDisplayName, UCL.Core.UI.UCL_GUIStyle.LabelStyle);
            }
#if UNITY_STANDALONE_WIN

            var aPath = FileSystemFolderPath;
            if (Directory.Exists(aPath))
            {
                if (GUILayout.Button(UCL_LocalizeManager.Get("OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL.Core.FileLib.WindowsLib.OpenExplorer(aPath);
                }
            }
#endif
        }
        public async UniTask<byte[]> ReadAllBytesAsync()
        {
            string aPath = FilePath;
            if (!File.Exists(aPath))
            {
                return null;//System.Array.Empty<byte>()
            }
            return await File.ReadAllBytesAsync(aPath);
        }
        public byte[] ReadAllBytes()
        {
            string aPath = FilePath;
            if (!File.Exists(aPath))
            {
                return null;//System.Array.Empty<byte>()
            }
            return File.ReadAllBytes(aPath);
        }
    }
}