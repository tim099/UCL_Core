
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
        /// <summary>
        /// ÀÉ®×¦WºÙ
        /// </summary>
        [UCL.Core.PA.UCL_List("GetAllFileNames")]
        public string m_FileName = string.Empty;
        public string FileSystemFolderPath => Path.Combine(UCL_ModuleService.GetModResourcesPath(m_ModuleID), m_FolderPath);
        public string FilePath => Path.Combine(FileSystemFolderPath, m_FileName);
        public List<string> GetAllFileNames()
        {
            m_ModuleID = UCL_ModuleService.CurEditModuleID;

            string aPath = FileSystemFolderPath;
            var aFileDatas = UCL.Core.FileLib.Lib.GetFilesName(aPath, "*", System.IO.SearchOption.TopDirectoryOnly);
            List<string> aFileNames = new List<string>() { string.Empty };//Can select null(Empty)
            aFileNames.Append(aFileDatas);
            return aFileNames;
        }

        override public UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return default;
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

        public byte[] ReadAllBytes()
        {
            string aPath = FilePath;
            if (!File.Exists(aPath))
            {
                return System.Array.Empty<byte>();
            }
            return File.ReadAllBytes(aPath);
        }
    }
}