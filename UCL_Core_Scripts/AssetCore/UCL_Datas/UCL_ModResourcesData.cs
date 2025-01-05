
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 20:05
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core
{

    /// <summary>
    /// All resources in ModResources can be loaded by UCL_ModResourcesData
    /// </summary>
    [System.Serializable]
    public class UCL_ModResourcesData : UCL_Data, UCL.Core.UCLI_NameOnGUI
    {
        [ReadOnly(true)]
        public string m_ModuleID;

        [UCL.Core.PA.UCL_FolderExplorer(typeof(UCL_ModuleService), nameof(UCL_ModuleService.ModResourcesPath))]
        public string m_FolderPath;

        #region ReflectionGetAllFileNames
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
        [UCL.Core.PA.UCL_List(nameof(GetAllFileNames))]
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
        /// <summary>
        /// 完整的檔案路徑
        /// </summary>
        public string FilePath => Path.Combine(FileSystemFolderPath, m_FileName);
        /// <summary>
        /// URL for UnityWebrequest
        /// </summary>
        public string UnityWebrequestURL => $"file://{FilePath}";


        public UCL_ModResourcesData() { }
        //public UCL_ModResourcesData(string folderPath)
        //{
        //    m_FolderPath = folderPath;
        //}
        //~UCL_ModResourcesData()
        //{
        //    Release();
        //}
        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            if(UCL_ModuleService.AssetConfig.s_CurCreateDataConfig != null)
            {
                var module = UCL_ModuleService.AssetConfig.s_CurCreateDataConfig.p_Module;
                if(module != null)
                {
                    m_ModuleID = module.ID;
                }
            }
        }
        //public override void DeserializeFromJson(JsonData iJson)
        //{
        //    base.DeserializeFromJson(iJson);
        //    if (string.IsNullOrEmpty(m_ModuleID))
        //    {
        //        m_ModuleID = UCL_ModuleService.CurEditModuleID;
        //    }
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
//#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override async UniTask<Sprite> LoadSpriteAsync(CancellationToken iToken, UCLI_LoadTextureConfig config)
//#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            if (IsEmpty)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSprite IsEmpty!,FileSystemFolderPath:{FileSystemFolderPath}");
                return null;
            }
            //var result = await UCL.Core.TextureLib.Lib.LoadTextureFromFile(FilePath);
            var result = await UCL_ModResourcesService.LoadTextureAsync(FilePath, config);
            return result.Sprite;

            //return GetSprite();
        }
        override public async UniTask<Texture2D> LoadTextureAsync(CancellationToken iToken, UCLI_LoadTextureConfig config)
        {
            var result = await UCL_ModResourcesService.LoadTextureAsync(FilePath, config);
            return result.Texture2D;

            //var sprite = await LoadSpriteAsync(iToken);
            //if (sprite == null)
            //{
            //    return null;
            //}
            //return sprite.texture;
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
            if (IsEmpty)
            {
                Debug.LogError($"{GetType().Name}.ReadAllBytes IsEmpty!,FileSystemFolderPath:{FileSystemFolderPath}");
                return null;
            }
            string aPath = FilePath;
            if (!File.Exists(aPath))
            {
                return null;//System.Array.Empty<byte>()
            }
            return File.ReadAllBytes(aPath);
        }
        public string ReadAllText()
        {
            if (IsEmpty)
            {
                Debug.LogError($"{GetType().Name}.ReadAllText IsEmpty!,FileSystemFolderPath:{FileSystemFolderPath}");
                return null;
            }
            string aPath = FilePath;
            if (!File.Exists(aPath))
            {
                return null;//System.Array.Empty<byte>()
            }
            return File.ReadAllText(aPath);
        }
    }
}