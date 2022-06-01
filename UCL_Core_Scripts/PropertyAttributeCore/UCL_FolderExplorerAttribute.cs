using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
        public UCL_FolderExplorerAttribute()
        {
            Init(ExplorerType.AssetsRoot);
        }
        public UCL_FolderExplorerAttribute(ExplorerType iExplorerType) {
            Init(iExplorerType);
        }
        public UCL_FolderExplorerAttribute(string iFolderRoot)
        {
            m_FolderRoot = iFolderRoot;
        }
        public UCL_FolderExplorerAttribute(Type iType, string iFuncName)
        {
            var aMethod = iType.GetMethod(iFuncName);
            if (aMethod != null)
            {
                try
                {
                    m_FolderRoot = aMethod.Invoke(null, null) as string;
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                    Debug.LogError("UCL_FolderExplorerAttribute method.Invoke iFuncName:" + iFuncName + " Exception:" + iE.ToString());
                }
            }
            else
            { //might be accessor
                PropertyInfo aPropInfo = iType.GetProperty(iFuncName);
                if (aPropInfo == null)
                { // not accessor!!
                    Debug.LogError("UCL_FolderExplorerAttribute:" + iType.Name + ",func_name == null :" + iFuncName);
                    return;
                }
                MethodInfo[] aAccessors = aPropInfo.GetAccessors();
                for (int i = 0; i < aAccessors.Length; i++)
                {
                    MethodInfo aMethodInfo = aAccessors[i];
                    // Determine if this is the property getter or setter.
                    if (aMethodInfo.ReturnType != typeof(void))
                    {//getter
                        m_FolderRoot = aMethodInfo.Invoke(null, new object[] { }) as string;
                        if (!string.IsNullOrEmpty(m_FolderRoot)) break;
                    }
                }
            }
        }
        public void Init(ExplorerType iExplorerType)
        {
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
        public string OnGUI(UCL.Core.UCL_ObjectDictionary iDataDic, string iPath, string iDisplayName = "Folder Explorer")
        {
            var aResult = UCL.Core.UI.UCL_GUILayout.FolderExplorer(iDataDic, iPath, m_FolderRoot, iDisplayName,
                iIsShowFiles: false);
            return aResult;
        }
    }
}