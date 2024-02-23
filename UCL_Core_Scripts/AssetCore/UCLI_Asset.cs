
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 10:44
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
        private static List<Type> s_AllCommonDataTypes = null;
        /// <summary>
        /// 抓取所有UCLI_Asset的Type
        /// Get all Types of UCLI_Asset
        /// </summary>
        /// <returns></returns>
        public static List<Type> GetAllAssetTypes()
        {
            if (s_AllCommonDataTypes == null)
            {
                var aAllTypes = typeof(UCLI_Asset).GetAllITypesAssignableFrom();
                s_AllCommonDataTypes = new List<Type>();
                foreach (var aType in aAllTypes)
                {
                    if (aType.IsGenericType || aType.IsInterface)
                    {
                        continue;
                    }
                    s_AllCommonDataTypes.Add(aType);
                }
            }

            return s_AllCommonDataTypes;
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

