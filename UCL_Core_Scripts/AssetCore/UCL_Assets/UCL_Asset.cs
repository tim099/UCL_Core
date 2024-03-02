
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
        virtual public string RelativeFolderPath
        {
            get
            {
                if(s_RelativeFolderPath == null)
                {
                    s_RelativeFolderPath = UCL_ModulePath.GetAssetRelativePath(GetType()); //$"UCL_Assets/{GetType().Name}";
                }
                return s_RelativeFolderPath;
                //return UCL_ModuleService.Ins.GetFolderPath(GetType());
                //return ATS_FileData.GetFolderPath(GetType());
            }
        }
        static string s_RelativeFolderPath = null;
        virtual public string SaveFolderPath => UCL_ModuleService.Ins.GetFolderPath(RelativeFolderPath);
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
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                if (iIsShowEditButton)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle))
                    {
                        UCL_CommonEditPage.Create(this);
                    }
                }
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
        virtual public UCL_Module Module => UCL_ModuleService.CurEditModule;
        public const string CommonDataMetaName = ".CommonDataMeta";
        public string CommonDataMetaPath => System.IO.Path.Combine(SaveFolderPath, CommonDataMetaName);
        /// <summary>
        /// 存檔路徑
        /// </summary>
        public string SavePath //=> FileDatas.GetFileSystemPath(ID);
        {
            get
            {
                return GetSavePath(ID);
            }
        }
        virtual public string GetSavePath(string iID)
        {
            return Path.Combine(SaveFolderPath, $"{iID}.json");
        }
        /// <summary>
        /// Check if asset exist
        /// </summary>
        /// <param name="iID">ID of asset</param>
        /// <returns>true if asset exist</returns>
        public bool ContainsAsset(string iID)// => FileDatas.FileExists(iID);
        {
            string aPath = GetSavePath(iID);
            //Debug.LogError($"ContainsAsset aPath:{aPath}");
            return File.Exists(aPath);
        }
        public string ReadAssetJson(string iID)
        {
            string aPath = GetSavePath(iID);
            if (!File.Exists(aPath))
            {
                return string.Empty;
            }
            return File.ReadAllText(aPath);
        }
        public void SaveAssetJson(string iID, string iJson)
        {
            string aPath = GetSavePath(iID);
            File.WriteAllText(aPath, iJson);
        }
        #endregion


        #region FakeStatic

        /// <summary>
        /// cached data
        /// </summary>
        /// <returns></returns>
        private Dictionary<string, T> s_DataDic = null;


        virtual public T CreateData(string iID)
        {
            if (!ContainsAsset(iID))
            {
                string aLog = $"Create {GetType().FullName} ID: {iID},Not Exist!!";
                Debug.LogError(aLog);
                //throw new System.Exception(aLog);
                return null;
            }
            var aData = new T();
            UCLI_Asset.s_CurCreateData = aData;
            var aTmp = aData as UCL_Asset<T>;
            aTmp.Init(iID);
            UCLI_Asset.s_CurCreateData = null;
            return aData;
        }

        /// <summary>
        /// 根據ID抓取物品設定
        /// </summary>
        /// <param name="iID">ID</param>
        /// <param name="iUseCache">使否使用緩存的資料</param>
        /// <returns></returns>
        virtual public T GetData(string iID, bool iUseCache = true)
        {
            if (string.IsNullOrEmpty(iID))
            {
                Debug.LogError($"GetData, string.IsNullOrEmpty(iID)");
                return default;
            }
            if (!iUseCache)
            {
                return CreateData(iID);
            }
            if (s_DataDic == null) s_DataDic = new Dictionary<string, T>();
            if (!s_DataDic.ContainsKey(iID))
            {
                s_DataDic[iID] = CreateData(iID);
            }
            return s_DataDic[iID];
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
            //Debug.LogError($"TODO {GetType().Name}.Delete {iID}");
            string aPath = GetSavePath(iID);
            if (File.Exists(aPath))
            {
                File.Delete(aPath);
            }
            //FileDatas.DeleteFile(iID);
            ClearCache();
        }
        /// <summary>
        /// Create a page to select UCL_Asset to edit
        /// 生成一個編輯選單頁面(用來選取要編輯的物品)
        /// </summary>
        virtual public void CreateSelectPage()
        {
            CreateCommonSelectPage();
        }
        /// <summary>
        /// Create a page to select UCL_Asset to edit
        /// 生成一個編輯選單頁面(用來選取要編輯的物品)
        /// </summary>
        /// <returns></returns>
        virtual public Page.UCL_SelectAssetPage<T> CreateCommonSelectPage()
        {
            return Page.UCL_SelectAssetPage<T>.Create();
        }
        #endregion


        private UCL.Core.UCL_ObjectDictionary m_PreviewDic = null;
        protected UCL.Core.UCL_ObjectDictionary PreviewDic
        {
            get
            {
                if (m_PreviewDic == null) m_PreviewDic = new UCL.Core.UCL_ObjectDictionary();
                return m_PreviewDic;
            }
        }


        virtual public void Init(string iID)
        {
            ID = iID;
            try
            {
                if (ContainsAsset(ID))
                {
                    string aData = ReadAssetJson(ID);
                    DeserializeFromJson(JsonData.ParseJson(aData));
                }
                else
                {
                    Debug.LogError("Init ID:" + iID + ",file not exist!!");
                }
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }

        }

        virtual public JsonData Save()
        {
            var aJson = SerializeToJson();
            SaveAssetJson(ID, aJson.ToJsonBeautify());
            //FileDatas.WriteAllText(ID, aJson.ToJsonBeautify());
            //Debug.LogError("Save:" + ID);
            ClearCache();
            return aJson;
        }

        /// <summary>
        /// 清除緩存
        /// </summary>
        virtual public void ClearCache()
        {
            if (this != Util) Util.ClearCache();
            if (s_DataDic != null) s_DataDic.Clear();
            //s_DataDic = null;
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
            
            aCommonDataMeta.Init(GetType().FullName, SaveCommonDataMetaJson);

            string aJson = GetCommonDataMetaJson();
            if (!string.IsNullOrEmpty(aJson))
            {
                JsonData aData = JsonData.ParseJson(aJson);
                aCommonDataMeta.DeserializeFromJson(aData);
                aCommonDataMeta.CheckFileMetas(GetEditableIDs());
            }

            return aCommonDataMeta;
        }
        public string GetCommonDataMetaJson()
        {
            string aSaveFolderPath = SaveFolderPath;
            if (!Directory.Exists(aSaveFolderPath))
            {
                Directory.CreateDirectory(aSaveFolderPath);
            }
            string aCommonDataMetaPath = CommonDataMetaPath;
            if(!File.Exists(aCommonDataMetaPath))
            {
                return string.Empty;
            }

            return File.ReadAllText(aCommonDataMetaPath);
        }
        public void SaveCommonDataMetaJson(string iJson)
        {
            string aSaveFolderPath = SaveFolderPath;
            if (!Directory.Exists(aSaveFolderPath))
            {
                Directory.CreateDirectory(aSaveFolderPath);
            }
            File.WriteAllText(CommonDataMetaPath, iJson);
        }
        /// <summary>
        /// 抓取此資料所屬的分組
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
        /// get all assets ID of this AssetType
        /// </summary>
        /// <returns></returns>
        virtual public List<string> GetAllIDs()
        {
            //this should base on current module and dependencies modules of current module

            return UCL_ModuleService.Ins.GetAllAssetsID(this.GetType());

            //return UCL.Core.FileLib.Lib.GetFilesName(SaveFolderPath, "*.json", SearchOption.TopDirectoryOnly, true).ToList();

            //return FileDatas.GetFileIDs();
        }

        /// <summary>
        /// 抓取所有可編輯資料的ID
        /// </summary>
        /// <returns></returns>
        virtual public List<string> GetEditableIDs()
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
