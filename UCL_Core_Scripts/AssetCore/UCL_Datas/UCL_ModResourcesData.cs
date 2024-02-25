
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 20:05
using Cysharp.Threading.Tasks;
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

        private List<UnityEngine.Object> m_CreatedAssets = new List<UnityEngine.Object>();


        public override string Key => FilePath;
        public override bool IsEmpty => string.IsNullOrEmpty(m_FileName);
        public string FileSystemFolderPath => Path.Combine(UCL_ModuleService.GetModResourcesPath(m_ModuleID), m_FolderPath);
        public string FilePath => Path.Combine(FileSystemFolderPath, m_FileName);

        ~UCL_ModResourcesData()
        {
            Release(null);
        }

        /// <summary>
        /// Release Object load from UCL_Data
        /// </summary>
        /// <param name=""></param>
        public override void Release(UnityEngine.Object iObject)
        {
            foreach(var aCreated in m_CreatedAssets)
            {
                UnityEngine.Object.Destroy(aCreated);
            }
            //if(iObject is Sprite aSprite)
            //{
            //    GameObject.Destroy(aSprite);
            //    if(m_Texture != null)
            //    {
            //        GameObject.Destroy(m_Texture);
            //    }
            //}
        }
        override public UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return default;
        }
        public override async UniTask<Sprite> LoadSpriteAsync(CancellationToken iToken)
        {
            //Debug.LogError("LoadSpriteAsync");
            if (IsEmpty)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSpriteAsync IsEmpty!,FileSystemFolderPath:{FileSystemFolderPath}");
                return null;
            }
            var aBytes = await ReadAllBytesAsync();
            if(aBytes == null)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSpriteAsync ReadAllBytesAsync() == null,FilePath:{FilePath}");
                return null;
            }

            Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
            if(aTexture == null)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSpriteAsync CreateTexture() == null,FilePath:{FilePath}");
                return null;
            }
            m_CreatedAssets.Add(aTexture);

            Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
            if(aSprite == null)
            {
                Debug.LogError($"UCL_ModResourcesData.LoadSpriteAsync CreateSprite() == null,FilePath:{FilePath}");
                return null;
            }

            return aSprite;
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