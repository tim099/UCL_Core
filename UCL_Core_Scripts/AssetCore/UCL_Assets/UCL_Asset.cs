
// ATS_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 02/20 2024 19:50
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UCL.Core;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{


    public class UCL_Asset<T> : UCL_Util<T>, UCLI_Asset where T : class, UCLI_Asset, UCLI_CommonEditable, new()
    {
        #region must override 一定要override的部份
        //public static string GetFolderPath(System.Type iType) => $"Install/.CommonData/{iType.Name.Replace("RCG_", string.Empty)}";

        /// <summary>
        /// 檔案路徑
        /// </summary>
        virtual public string RelativeFolderPath => UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath(GetType());

        virtual public string SaveFolderPath => UCL_ModuleService.Ins.GetCurEditModuleFolder(RelativeFolderPath);
        virtual public UCLI_Asset CreateCommonData(string iID) => CreateData(iID) as UCLI_Asset;

        /// <summary>
        /// 預覽
        /// </summary>
        /// <param name="iIsShowEditButton"></param>
        virtual public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            //GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))//, GUILayout.MinWidth(130)
            {
                if (iIsShowEditButton)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle))
                    {
                        UCL_CommonEditPage.Create(this);
                    }
                }
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                
                //GUILayout.Label($"{this.UCL_ToString()}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                UCL_GUILayout.Preview.OnGUI(this, iDataDic.GetSubDic("DrawPreview"));
                //UCL_GUILayout.DrawObjectData(this, iDataDic.GetSubDic("Preview Data"), string.Empty, true);

            }
            //GUILayout.EndHorizontal();
        }
        public static string LocalizeFieldName(string iDisplayName)
        {
            if (iDisplayName[0] == 'm' && iDisplayName[1] == '_')
            {
                iDisplayName = iDisplayName.Substring(2, iDisplayName.Length - 2);
            }
            iDisplayName = UCL_LocalizeManager.Get(iDisplayName);
            return iDisplayName;
        }
        /// <summary>
        /// 繪製編輯介面
        /// </summary>
        /// <param name="iDataDic"></param>
        virtual public void OnGUI(UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            GUILayout.BeginVertical();

            using (var scope = new GUILayout.VerticalScope("box"))//, GUILayout.Width(500)
            {
                UCL.Core.UI.UCL_GUILayout.DrawObjectData(this, iDataDic, string.Empty, true, LocalizeFieldName);
            }
            using (new GUILayout.VerticalScope("box"))//Preview
            {
                bool aIsShow = false;
                using (new GUILayout.HorizontalScope())
                {
                    aIsShow = UCL.Core.UI.UCL_GUILayout.Toggle(iDataDic, "ShowPreview", iDefaultValue: true);
                    UCL.Core.UI.UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Preview"));
                }

                if (aIsShow)
                {
                    //using (new GUILayout.VerticalScope(GUILayout.Width(200)))
                    {
                        Preview(iDataDic.GetSubDic("Preview"), false);
                    }
                }
            }

            GUILayout.EndVertical();
        }
        #endregion

        #region FileSystem
        public static UCL_ModuleService.AssetConfig GetAssetConfig(string iID) => UCL_ModuleService.Ins.GetAssetConfig(typeof(T), iID);

        virtual public UCL_ModuleService.AssetConfig AssetConfig => GetAssetConfig(ID);
        virtual public UCL_Module Module => AssetConfig.p_Module;

        public const string CommonDataMetaName = ".CommonDataMeta";
        public string CommonDataMetaPath => System.IO.Path.Combine(SaveFolderPath, CommonDataMetaName);

        /// <summary>
        /// 存檔路徑
        /// </summary>
        public string AssetPath => AssetConfig.AssetPath;

        virtual public string GetAssetPath(string iID)
        {
            return GetAssetConfig(iID).AssetPath;
            //return Path.Combine(SaveFolderPath, $"{iID}.json");
        }
        /// <summary>
        /// Check if asset exist
        /// </summary>
        /// <param name="iID">ID of asset</param>
        /// <returns>true if asset exist</returns>
        public bool ContainsAsset(string iID)// => FileDatas.FileExists(iID);
        {
            string aPath = GetAssetPath(iID);
            //Debug.LogError($"ContainsAsset aPath:{aPath}");
            return File.Exists(aPath);
        }
        #endregion


        #region FakeStatic
        virtual public T CreateData(string iID)
        {
            var aType = typeof(T);
            var aConfig = GetAssetConfig(iID);
            if (!aConfig.Exist)
            {
                Debug.LogError($"CreateData Type:{aType}, ID:{iID}, !Config.Exist");
                return null;
            }

            var aData = new T();
            UCLI_Asset.s_CurCreateData = aData;

            try
            {
                aData.ID = iID;
                aData.DeserializeFromJson(aConfig.GetJsonData());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            UCLI_Asset.s_CurCreateData = null;
            return aData;
        }

        /// <summary>
        /// Get Asset by ID
        /// </summary>
        /// <param name="iID">ID</param>
        /// <param name="iUseCache">Use cache data?</param>
        /// <returns></returns>
        virtual public T GetData(string iID, bool iUseCache = true)
        {
            if (!iUseCache)//Create new asset
            {
                return CreateData(iID);
            }

            var aConfig = GetAssetConfig(iID);
            if (aConfig.AssetCache == null)
            {
                aConfig.AssetCache = CreateData(iID);
            }

            return aConfig.AssetCache as T;
        }
        /// <summary>
        /// 根據ID抓取物品設定
        /// </summary>
        /// <param name="iID">ID</param>
        /// <param name="iUseCache">使否使用緩存的資料</param>
        /// <returns></returns>
        virtual public UCLI_Asset GetCommonData(string iID, bool iUseCache = true) => GetData(iID, iUseCache);
        /// <summary>
        /// 刪除
        /// </summary>
        /// <param name="iID"></param>
        public void Delete(string iID)
        {
            var aAssetConfig = GetAssetConfig(iID);
            aAssetConfig.DeleteAsset();

            //FileDatas.DeleteFile(iID);
            ClearCache(iID);
        }
        /// <summary>
        /// Create a page to select UCL_Asset to edit
        /// 生成一個編輯選單頁面(用來選取要編輯的物品)
        /// </summary>
        virtual public void CreateSelectPage()
        {
            CreateSelectAssetPage();
        }
        /// <summary>
        /// Create a page to select UCL_Asset to edit
        /// 生成一個編輯選單頁面(用來選取要編輯的物品)
        /// </summary>
        /// <returns></returns>
        virtual public Page.UCL_SelectAssetPage<T> CreateSelectAssetPage()
        {
            return Page.UCL_SelectAssetPage<T>.Create();
        }
        #endregion

        virtual public JsonData Save()
        {
            var aJson = SerializeToJson();
            AssetConfig.SaveAsset(aJson);
            //Debug.LogError("Save:" + ID);
            ClearCache(ID);
            return aJson;
        }

        /// <summary>
        /// 清除緩存
        /// </summary>
        virtual public void ClearAllCache()
        {
            UCL_ModuleService.Ins.ClearAssetsCache(typeof(T));
            //Debug.LogError("ClearCache:" + ID);
        }
        /// <summary>
        /// 清除緩存
        /// </summary>
        virtual public void ClearCache(string iID)
        {
            UCL_ModuleService.Ins.ClearAssetsCache(typeof(T), iID);
            //Debug.LogError("ClearCache:" + ID);
        }
        virtual public string ID { get; set; }


        private UCL_AssetMeta m_AssetMeta = null;
        public UCL_AssetMeta AssetMetaIns
        {
            get
            {
                if (m_AssetMeta == null)
                {
                    m_AssetMeta = CreateCommonDataMeta();
                }
                return m_AssetMeta;
            }
        }
        virtual public UCL_AssetMeta CreateCommonDataMeta()
        {
            UCL_AssetMeta aCommonDataMeta = new UCL_AssetMeta();
            
            aCommonDataMeta.Init(GetType().FullName, SaveAssetMetaJson);

            string aJson = GetAssetMetaJson();
            if (!string.IsNullOrEmpty(aJson))
            {
                JsonData aData = JsonData.ParseJson(aJson);
                aCommonDataMeta.DeserializeFromJson(aData);
                aCommonDataMeta.CheckFileMetas(GetEditableIDs());
            }

            return aCommonDataMeta;
        }
        public string GetAssetMetaJson()
        {
            //if (!Directory.Exists(SaveFolderPath)) https://stackoverflow.com/questions/27575384/c-sharp-directory-createdirectory-path-should-i-check-if-path-exists-first
            Directory.CreateDirectory(SaveFolderPath);
            string aCommonDataMetaPath = CommonDataMetaPath;
            if(!File.Exists(aCommonDataMetaPath))
            {
                return string.Empty;
            }

            return File.ReadAllText(aCommonDataMetaPath);
        }
        public void SaveAssetMetaJson(string iJson)
        {
            //if (!Directory.Exists(SaveFolderPath)) https://stackoverflow.com/questions/27575384/c-sharp-directory-createdirectory-path-should-i-check-if-path-exists-first
            Directory.CreateDirectory(SaveFolderPath);
            File.WriteAllText(CommonDataMetaPath, iJson);
        }
        /// <summary>
        /// GroupID of this asset
        /// </summary>
        /// <returns></returns>
        public string GroupID
        {
            get
            {
                var aMeta = AssetMetaIns.GetFileMeta(ID);
                return aMeta.m_Group;
            }
        }


        /// <summary>
        /// get all assets ID
        /// </summary>
        /// <returns></returns>
        virtual public List<string> GetAllIDs()
        {
            //this should base on current module and dependencies modules of current module
            var aIDs = UCL_ModuleService.Ins.GetAllAssetsID(this.GetType());
            //Debug.LogError($"GetAllIDs(),aIDs:{aIDs}");
            return aIDs;
        }

        /// <summary>
        /// get all editable assets ID
        /// 抓取所有可編輯資料的ID
        /// </summary>
        /// <returns></returns>
        virtual public IList<string> GetEditableIDs()
        {
            return UCL_ModuleService.Ins.GetAllEditableAssetsID(this.GetType());

            //return GetAllIDs();
        }
        /// <summary>
        /// 通過讀檔 再存檔更新所有資料
        /// (除了檔案格式變更外請勿使用此Function)
        /// </summary>
        virtual public void RefreshAllDatas()
        {
            var aIDs = GetEditableIDs();
            for (int i = 0; i < aIDs.Count; i++)
            {
                string aID = aIDs[i];
                var aData = CreateData(aID);
                aData.Save();
            }
        }
        /// <summary>
        /// 複製一份
        /// </summary>
        /// <returns></returns>
        virtual public UCLI_CommonEditable CloneInstance()
        {
            var aClone = this.CloneObject();
            aClone.ID = this.ID;
            return aClone;
        }
        virtual public JsonData SerializeToJson()
        {
            return JsonConvert.SaveFieldsToJsonUnityVer(this);
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadFieldFromJsonUnityVer(this, iJson);
        }
    }
}
