
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
    public static class UCL_ModResourcesService
    {
        public class LoadedData : IDisposable
        {
            public string m_FilePath;

            public List<UnityEngine.Object> m_CreatedAssets = new List<UnityEngine.Object>();
            public void Dispose()
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
            public Sprite Sprite
            {
                get
                {
                    if(m_CreatedAssets.Count > 1)
                    {
                        return m_CreatedAssets[1] as Sprite;
                    }
                    return null;
                }
            }
            public Texture2D Texture2D
            {
                get
                {
                    if (m_CreatedAssets.Count > 0)
                    {
                        return m_CreatedAssets[0] as Texture2D;
                    }
                    return null;
                }
            }
        }
        public static Sprite LoadSprite(string iPath)
        {
            //Debug.LogError("LoadSpriteAsync");
            if (s_LoadedDatas.ContainsKey(iPath))
            {
                var aData = s_LoadedDatas[iPath];
                return aData.Sprite;
            }
            if(!File.Exists(iPath))
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite !File.Exists,iPath:{iPath}");
                return null;
            }
            var aBytes = File.ReadAllBytes(iPath);
            if (aBytes == null)
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite File.ReadAllBytes() == null,iPath:{iPath}");
                return null;
            }
            LoadedData aLoadedData = new LoadedData();
            s_LoadedDatas[iPath] = aLoadedData;

            Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
            if (aTexture == null)
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite CreateTexture() == null,iPath:{iPath}");
                return null;
            }
            aLoadedData.m_CreatedAssets.Add(aTexture);

            Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
            if (aSprite == null)
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite CreateSprite() == null,iPath:{iPath}");
                return null;
            }
            aLoadedData.m_CreatedAssets.Add(aSprite);
            return aSprite;
        }
        public static void ReleaseAll()
        {
            foreach(var aData in s_LoadedDatas.Values)
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
        private static Dictionary<string, LoadedData> s_LoadedDatas = new Dictionary<string, LoadedData>();
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
        public string FileSystemFolderPath => Path.Combine(UCL_ModuleService.GetModResourcesPath(m_ModuleID), m_FolderPath);
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