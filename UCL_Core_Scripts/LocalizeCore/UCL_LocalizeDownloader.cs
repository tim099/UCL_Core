using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    [System.Serializable]
    public struct KeyPair
    {
        public KeyPair(string iKey, string iLocalize)
        {
            m_Key = ParseString(iKey);
            m_Localize = ParseString(iLocalize);
            //Debug.LogError(m_Key + "," + m_Localize);
        }
        static public string ParseString(string iStr)
        {
            if (string.IsNullOrEmpty(iStr))
            {
                return string.Empty;
            }
            if(iStr[0] != '"') {
                return iStr;
            }
            StringBuilder aSB = new StringBuilder();
            for(int i = 1; i < iStr.Length - 1; i++)
            {
                char aC = iStr[i];
                switch (aC) {
                    case '"':
                        {
                            i++;
                            aSB.Append('\\');
                            aSB.Append('"');
                            break;
                        }
                    case '\r':
                        {
                            i++;
                            aSB.Append('\n');
                            break;
                        }
                    default:
                        {
                            aSB.Append(aC);
                            break;
                        }
                }
            }
            return aSB.ToString();
        }
        public void SaveToString(StringBuilder aSB)
        {
            aSB.Append('\"');
            aSB.Append(m_Key);
            aSB.Append('\"');
            aSB.Append(':');
            aSB.Append('\"');
            aSB.Append(m_Localize);
            aSB.Append('\"');
        }
        public string m_Key;
        public string m_Localize;
    }
    [Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New LocalizeDownloader", menuName = "UCL/Downloader/LocalizeDownloader")]
    public class UCL_LocalizeDownloader : ScriptableObject
    {
        public UCL_LocalizeSetting m_LocalizeSetting = new UCL_LocalizeSetting();
        /// <summary>
        /// true if download success
        /// </summary>
        protected System.Action<bool> m_DownloadEndAct = null;
        protected Regex m_SplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);
        //#if UNITY_EDITOR
        //        [UCL.Core.ATTR.UCL_FunctionButton]
        //        public void ExploreSaveFolder()
        //        {
        //            m_LocalizeSetting.m_SaveFolder = UCL.Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_LocalizeSetting.m_SaveFolder);
        //        }
        //#endif
        [UCL.Core.ATTR.UCL_DrawOnGUI]
        protected void DrawOnGUI()
        {
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Copy")))
                {
                    m_LocalizeSetting.Copy();
                }
                if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Paste")))
                {
                    m_LocalizeSetting = m_LocalizeSetting.Paste();
                }
            }
        }
        public void StartDownload(System.Action<bool> iEndAct = null)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(m_LocalizeSetting.m_SaveFolder))
            {
                var aPath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_LocalizeSetting.m_SaveFolder = Core.FileLib.Lib.RemoveFolderPath(aPath, 1);
            }
#endif
            m_LocalizeSetting.StartDownload(iEndAct);
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void StartDownload()
        {
            StartDownload(null);
        }
    }
}

