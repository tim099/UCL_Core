
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:46
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public enum UCL_AssetType
    {
        StreamingAssets = 0,
        PersistentDatas,

        Addressables,
        /// <summary>
        /// Path xxx\SteamLibrary\steamapps\workshop\content\xxxx
        /// Example D:\SteamLibrary\steamapps\workshop\content\1158310\2973143830
        /// </summary>
        SteamMods,
    }
    /// <summary>
    /// UCL_Module contains info about how to load assets in this module
    /// </summary>
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID
    {
        /// <summary>
        /// StreamingAssets for BuiltinModules
        /// and PersistentDatas for Runtime
        /// </summary>
        public UCL_AssetType m_AssetType = UCL_AssetType.StreamingAssets;

        [SerializeField] protected string m_ID;

        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { get => m_ID; set => m_ID = value; }
        #endregion

        public void Init(string iID)
        {
            m_ID = iID;
            if (Application.isEditor)//Create BuiltinModule in streamming assets
            {
                m_AssetType = UCL_AssetType.StreamingAssets;
            }
            else//Create Module in PersistentDatas
            {
                m_AssetType = UCL_AssetType.PersistentDatas;
            }
        }
        protected string GetConfigPath(string iFolderPath) => Path.Combine(iFolderPath, "Config.json");
        protected string GetResourcePath(string iFolderPath) => Path.Combine(iFolderPath, "ModResources");
        /// <summary>
        /// Save Module Config
        /// </summary>
        public void Save()
        {
            string aFolderPath = UCL_ModuleService.Ins.ModuleConfig.GetBuiltinModulesPath(m_AssetType);
            //Debug.LogError($"aFolderPath:{aFolderPath}");
            string aPath = Path.Combine(aFolderPath, ID);
            if(!Directory.Exists(aPath))
            {
                Directory.CreateDirectory(aPath);
            }
            var aConfigPath = GetConfigPath(aPath);
            File.WriteAllText(aConfigPath, SerializeToJson().ToJsonBeautify());//SaveConfig
            if (Application.isEditor)
            {
                var aResFolder = GetResourcePath(aPath);
                if (!Directory.Exists(aResFolder))
                {
                    Directory.CreateDirectory(aResFolder);
                    string aResReadMe = "Please put Mod resources in this folder";
                    File.WriteAllText(Path.Combine(aResFolder,"Readme.txt"), aResReadMe);
                }
            }
        }
        public async UniTask Load(string iFolderPath)
        {
            switch (m_AssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        string aPath = Path.Combine(iFolderPath, ID);
                        if (Directory.Exists(aPath))
                        {
                            var aConfigPath = GetConfigPath(aPath);
                            string aJson = File.ReadAllText(aConfigPath);
                            JsonData aJsonData = JsonData.ParseJson(aJson);
                            DeserializeFromJson(aJsonData);
                        }
                        break;
                    }
            }



        }
        public override JsonData SerializeToJson()
        {
            return base.SerializeToJson();
        }
    }
}