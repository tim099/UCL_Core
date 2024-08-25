
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 10:44
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core.ATTR;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core
{
    public interface UCLI_ID
    {
        /// <summary>
        /// Unique ID of this Data
        /// </summary>
        string ID { get; set; }
    }
    public class UCL_AssetTypeInfo
    {
        public System.Type m_Type;
        public string m_Name;
        public string m_LocalizedName;
        public string m_Group;
        public int m_SortOrder;

        public UCL_AssetTypeInfo()
        {

        }

        public UCL_AssetTypeInfo(System.Type type)
        {
            m_Type = type;
            m_Name = m_Type.Name;

            string aLocalizeName = UCL_LocalizeManager.Get(m_Name);
            if (aLocalizeName != m_Name)
            {
                m_LocalizedName = $"{aLocalizeName}({m_Name})";
            }
            else
            {
                m_LocalizedName = m_Name;
            }
            {
                var attr = type.GetCustomAttribute<System.Configuration.SettingsGroupNameAttribute>();
                if (attr != null)
                {
                    m_Group = attr.GroupName;
                }
            }
            {
                var attr = type.GetCustomAttribute<UCL.Core.ATTR.UCL_SortAttribute>();
                if (attr != null)
                {
                    m_SortOrder = attr.m_SortOrder;
                }
                else
                {
                    m_SortOrder = int.MaxValue;
                }
                //[UCL.Core.ATTR.UCL_Sort(1)]
            }

        }
    }
    public interface UCLI_Asset : UCLI_CommonEditable, UCLI_Preview
    {
        string RelativeFolderPath { get; }
        UCLI_Asset CreateCommonData(string iID);
        List<string> GetAllIDs();
        /// <summary>
        /// 根據ID抓取物品設定
        /// </summary>
        /// <param name="iID">ID</param>
        /// <param name="iUseCache">使否使用緩存的資料</param>
        /// <returns></returns>
        UCLI_Asset GetCommonData(string iID, bool iUseCache = true);
        void RefreshAllDatas();
        /// <summary>
        /// 生成一個編輯選單頁面(用來選取要編輯的物品)
        /// </summary>
        void CreateSelectPage();
        /// <summary>
        /// 記錄這個Asset的分組
        /// </summary>
        string GroupID { get; set; }

        #region static
        /// <summary>
        /// 當前正在CreateData的RCGI_CommonData
        /// </summary>
        public static UCLI_Asset s_CurCreateData = null;
        private static Dictionary<Type, UCLI_Asset> s_TypeToUtilDic = null;
        public static UCLI_Asset GetUtilByType(System.Type iType)
        {
            if(s_TypeToUtilDic == null)
            {
                s_TypeToUtilDic = new Dictionary<Type, UCLI_Asset>();
            }
            if (!s_TypeToUtilDic.ContainsKey(iType))//Get Util with reflection
            {
                UCLI_Asset aAsset = null;
                try
                {
                    var aPropInfoUtil = iType.GetProperty("Util", BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                    if (aPropInfoUtil != null)
                    {
                        MethodInfo[] aMethodInfos = aPropInfoUtil.GetAccessors();
                        //aMethInfosStr = $",MethodInfos:{aMethodInfos.ConcatString(iMethod => iMethod.Name)}";
                        if (aMethodInfos.Length > 0)
                        {
                            try
                            {
                                MethodInfo aMethodInfo = aMethodInfos[0];
                                var aUtil = aMethodInfo.Invoke(null, null);//Get Util
                                aAsset = aUtil as UCLI_Asset;
                            }
                            catch (Exception iE)
                            {
                                Debug.LogError($"UCLI_Asset.GetUtilByType aType:{iType.FullName},Exception:{iE}");
                                Debug.LogException(iE);
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    Debug.LogException(e);
                    Debug.LogError($"UCLI_Asset.GetUtilByType aType:{iType.FullName},Exception:{e}");
                }
                finally
                {
                    s_TypeToUtilDic[iType] = aAsset;
                }
            }

            
            return s_TypeToUtilDic[iType];
        }
        private static List<Type> s_AllAssetTypes = null;
        /// <summary>
        /// 抓取所有UCLI_Asset的Type
        /// Get all types of UCLI_Asset
        /// </summary>
        /// <returns></returns>
        public static List<Type> GetAllAssetTypes()
        {
            if (s_AllAssetTypes == null)
            {
                var aAllTypes = typeof(UCLI_Asset).GetAllITypesAssignableFrom();
                s_AllAssetTypes = new List<Type>();
                foreach (var aType in aAllTypes)
                {
                    if (aType.IsGenericType || aType.IsInterface)
                    {
                        continue;
                    }
                    if (aType.GetCustomAttribute<UCL_IgnoreAssetAttribute>() != null)
                    {
                        continue;
                    }
                    s_AllAssetTypes.Add(aType);
                }
            }

            return s_AllAssetTypes;
        }


        private static Dictionary<string, UCL_AssetTypeInfo> s_AllAssetTypesInfo = null;
        public static Dictionary<string, UCL_AssetTypeInfo> GetAllAssetTypesInfo()
        {
            if(s_AllAssetTypesInfo == null)
            {
                s_AllAssetTypesInfo = new Dictionary<string, UCL_AssetTypeInfo>();
                foreach(var type in GetAllAssetTypes())
                {
                    var info = new UCL_AssetTypeInfo(type);
                    s_AllAssetTypesInfo.Add(info.m_Name, info);
                }
            }
            return s_AllAssetTypesInfo;
        }

        private static List<string> s_AssetGroups = null;
        public static List<string> GetAssetGroups()
        {
            if(s_AssetGroups == null)
            {
                HashSet<string> groups = new HashSet<string>();
                foreach(var info in GetAllAssetTypesInfo().Values)
                {
                    if (!string.IsNullOrEmpty(info.m_Group))
                    {
                        groups.Add(info.m_Group);
                    }
                }
                s_AssetGroups = groups.ToList();
                s_AssetGroups.Insert(0, string.Empty);//string.Empty == Any
            }
            return s_AssetGroups;
        }
        private static List<string> s_LocalizedAssetGroups = null;
        public static List<string> GetLocalizedAssetGroups()
        {
            if (s_LocalizedAssetGroups == null)
            {
                s_LocalizedAssetGroups = new List<string>();
                foreach (var group in GetAssetGroups())
                {
                    string localizedName = UCL_LocalizeManager.Get(group);
                    if(localizedName != group)
                    {
                        localizedName = $"{localizedName}({group})";
                    }
                    s_LocalizedAssetGroups.Add(localizedName);
                }
            }
            return s_LocalizedAssetGroups;
        }

        private static List<string> s_AllAssetTypeNames = null;
        /// <summary>
        /// Get all types name of UCLI_Asset
        /// </summary>
        /// <returns></returns>
        public static List<string> GetAllAssetTypeNames()
        {
            if (s_AllAssetTypeNames == null)
            {
                s_AllAssetTypeNames = new List<string>();
                foreach(var aType in GetAllAssetTypesInfo().Values)
                {
                    s_AllAssetTypeNames.Add(aType.m_Name);
                }
            }

            return s_AllAssetTypeNames;
        }



        public static void RefreshAllAssetsWithReflection()
        {
            //RCG_GameInitData.Ins.Save();
            //HashSet<Type> aIgnoreTypes = new HashSet<Type>() { typeof(RCG_CommonTag)};
            foreach (var aType in GetAllAssetTypes())
            {
                try
                {
                    string aPropInfosStr = string.Empty;
                    try
                    {
                        UCLI_Asset aUtil = GetUtilByType(aType);//Get Util
                        if (aUtil != null)
                        {
                            //aMethInfosStr += $"Result:{aUtil.GetType().FullName}";
                            aUtil.RefreshAllDatas();
                            Debug.LogWarning($"Util:{aUtil.GetType().FullName}.RefreshAllDatas()");
                        }
                    }
                    catch (Exception iE)
                    {
                        Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
                        Debug.LogException(iE);
                    }
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                }

            }
        }


        #endregion
    }
}

