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
        /// <summary>
        /// Get FolderRoot by Type and FuncName
        /// ex. [UCL.Core.PA.UCL_FolderExplorer(typeof(UCL_ModuleService), nameof(UCL_ModuleService.ModResourcesPath))]
        /// </summary>
        FuncName,
        /// <summary>
        /// Refresh when OnGUI
        /// ex. [PA.UCL_FolderExplorer(PA.ExplorerType.FuncNameWithRefresh, typeof(UCL_ModuleService), nameof(UCL_ModuleService.ModResourcesPath))]
        /// </summary>
        FuncNameWithRefresh,
    }
    public class UCL_FolderExplorerAttribute : PropertyAttribute
    {
        public ExplorerType m_ExplorerType = ExplorerType.None;
        public string m_FolderRoot = string.Empty;
        public UCL.Core.UCLI_FileExplorer m_FileExplorer = null;
        private System.Action m_RefreshAct = null;
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
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iType">Type of the Target Class</param>
        /// <param name="iFuncName">Static function of iType that return the folder root(string) or UCL.Core.UCLI_FileExplorer</param>
        public UCL_FolderExplorerAttribute(Type iType, string iFuncName)
        {
            InitFuncName(ExplorerType.FuncName, iType, iFuncName);
        }

        public UCL_FolderExplorerAttribute(Type iType, string iFuncName, string iFolderRoot)
        {
            InitFuncName(ExplorerType.FuncName, iType, iFuncName);
            m_FolderRoot = iFolderRoot;
        }
        public UCL_FolderExplorerAttribute(ExplorerType iExplorerType, Type iType, string iFuncName)
        {
            InitFuncName(iExplorerType, iType, iFuncName);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iType">Type of the Target Class</param>
        /// <param name="iFuncName">Static function of iType that return the folder root(string) or UCL.Core.UCLI_FileExplorer</param>
        public void InitFuncName(ExplorerType iExplorerType, Type iType, string iFuncName)
        {
            m_ExplorerType = iExplorerType;
            var aMethod = iType.GetMethod(iFuncName);
            if (aMethod != null)
            {
                try
                {
                    void Refresh()
                    {
                        var aResult = aMethod.Invoke(null, null);
                        if (aResult is string)
                        {
                            m_FolderRoot = aResult as string;
                        }
                        else if (aResult is UCL.Core.UCLI_FileExplorer)
                        {
                            m_FileExplorer = aResult as UCL.Core.UCLI_FileExplorer;
                        }
                    }
                    if (iExplorerType == ExplorerType.FuncNameWithRefresh)
                    {
                        m_RefreshAct = Refresh;
                    }
                    else
                    {
                        m_RefreshAct = null;
                    }
                    
                    Refresh();
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
            m_RefreshAct?.Invoke();

            var aResult = UCL.Core.UI.UCL_GUILayout.FolderExplorer(iDataDic, iPath, m_FolderRoot, iDisplayName,
                iIsShowFiles: false, iFileExplorer : m_FileExplorer);
            return aResult;
        }
    }
}