using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.PA
{
    public enum ExplorerType
    {
        None = 0,
        StreamingAssets,
        AssetsRoot,
    }
    public class UCL_FolderExplorerAttribute : PropertyAttribute
    {
        public ExplorerType m_ExplorerType = ExplorerType.None;
        public string m_FolderRoot = string.Empty;
        public UCL_FolderExplorerAttribute(ExplorerType iExplorerType) {
            m_ExplorerType = iExplorerType;
            switch (m_ExplorerType)
            {
                case ExplorerType.StreamingAssets:
                    {
                        m_FolderRoot = Application.streamingAssetsPath;
                        break;
                    }
                case ExplorerType.AssetsRoot:
                    {
                        m_FolderRoot = FileLib.Lib.AssetsRoot;
                        break;
                    }
            }
        }
        public UCL_FolderExplorerAttribute(string iFolderRoot)
        {
            m_FolderRoot = iFolderRoot;
        }
    }
}