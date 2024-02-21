using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// Responsible for all operations related to Module
    /// </summary>
    public class UCL_ModuleService//: UCLI_FieldOnGUI
    {
        public const string RootRelativePath = ".ModuleService";
        public class ModuleEntry
        {
            public string m_ID;
            /// <summary>
            /// relative path in file system
            /// </summary>
            public string m_Path;

        }
        public class Config : UCL.Core.JsonLib.UnityJsonSerializable
        {
            public const string CoreModuleID = "Core";

            public string m_CurrentEditModule = string.Empty;

            /// <summary>
            /// All BuiltinModules are in StreammingAssets
            /// </summary>
            public List<string> m_BuiltinModules = new List<string>();


            public string m_Version = "1.0.0";


            virtual protected string BuiltinModulesPath => GetBuiltinModulesPath(UCL_AssetType.StreamingAssets);
            virtual public string SaveRelativePath => Path.Combine(RootRelativePath, "BuiltinModules");
            virtual public string GetBuiltinModulesPath(UCL_AssetType iAssetType)
            {
                var aPath = UCL_AssetPath.GetPath(iAssetType);
                return Path.Combine(aPath, SaveRelativePath);
            }
            public void OnGUI(UCL_ObjectDictionary iDataDic)
            {
                //GUILayout.Label("Config", UCL_GUIStyle.LabelStyle);
                UCL_GUILayout.DrawField(this, iDataDic, "Config");
            }
            public override JsonData SerializeToJson()
            {
//#if UNITY_EDITOR
//#endif
                if (Application.isEditor)//Check streamming assets for BuiltinModules
                {
                    var aBuiltinModulesPath = BuiltinModulesPath;
                    if (!Directory.Exists(aBuiltinModulesPath))//Init
                    {
                        InitBuiltinModules();
                    }
                    //Debug.LogError($"aBuiltinModulesPath:{aBuiltinModulesPath}");
                    var aDirs = UCL.Core.FileLib.Lib.GetDirectories(aBuiltinModulesPath, iRemoveRootPath : true);
                    //Debug.LogError($"aDirs:{aDirs.ConcatString()}");
                    m_BuiltinModules = aDirs.ToList();
                    if (!m_BuiltinModules.Contains(CoreModuleID))//Create Core Module!!
                    {
                        CreateModule(CoreModuleID);
                        m_BuiltinModules.Add(CoreModuleID);
                    }
                }
                return base.SerializeToJson();
            }
            private void InitBuiltinModules()
            {
                var aBuiltinModulesPath = BuiltinModulesPath;
                if (!Directory.Exists(aBuiltinModulesPath))
                {
                    Directory.CreateDirectory(aBuiltinModulesPath);
                }
                
            }
            public UCL_Module CreateModule(string iID)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.Init(iID);
                //var aPath = GetBuiltinModulesPath(aModule.m_AssetType);
                aModule.Save();//aPath
                return aModule;
            }
        }

        public static UCL_ModuleService Ins
        {
            get
            {
                if(s_Ins == null)
                {
                    s_Ins = new UCL_ModuleService();
                    s_Ins.Init();
                }
                return s_Ins;
            }
        }
        protected static UCL_ModuleService s_Ins = null;
        public static bool Initialized
        {
            get
            {
                return Ins.m_Initialized;
            }
        }

        public Config ModuleConfig => m_Config;

        protected bool m_Initialized = false;
        protected bool m_LoadingConfig = false;
        protected Config m_Config = new Config();


        /// <summary>
        /// 暫定固定會有一個核心設定模組 放在StreammingAssets
        /// 只有在Editor內能夠編輯 Build出來的版本只能夠讀取(用UnityWebRequest讀取StreammingAssets 避免跨平台出問題)
        /// </summary>
        virtual protected void Init()
        {
            InitAsync().Forget();
        }

        virtual protected async UniTask InitAsync()
        {
            //Debug.LogError("InitAsync()");
            //string aStr = await UCL_StreamingAssets.LoadString(".Install/CommonData/ATS_IconSprite/Icon_Heal.json");
            //Debug.LogError($"InitAsync() End aStr:{aStr}");
            await LoadConfig();
            m_Initialized = true;
        }
        virtual protected string SavePath
        {
            get
            {
                string aPath = string.Empty;
                if (Application.isEditor)//Save to streamming asset if in Editor
                {
                    return GetSavePath(UCL_AssetType.StreamingAssets);
                }

                return GetSavePath(UCL_AssetType.PersistentDatas);//save to Application.persistentDataPath path
            }
        }
        virtual protected string SaveRelativePath => Path.Combine(RootRelativePath, "Config.json");
        virtual protected string GetSavePath(UCL_AssetType iAssetType)
        {
            var aPath = UCL_AssetPath.GetPath(iAssetType);
            return Path.Combine(aPath, SaveRelativePath);
        }
        virtual protected void SaveConfig()
        {
            var aJson = m_Config.SerializeToJson();
            //Debug.LogError($"Application.isEditor:{Application.isEditor}");
            string aPath = SavePath;
            UCL.Core.FileLib.Lib.WriteAllText(aPath, aJson.ToJsonBeautify());
        }
        virtual protected async UniTask LoadConfig()
        {
            if (m_LoadingConfig)
            {
                return;
            }
            m_LoadingConfig = true;

            try
            {
                if (Application.isEditor)//Load from streamming asset if in Editor
                {
                    var aPath = GetSavePath(UCL_AssetType.StreamingAssets);
                    if (File.Exists(aPath))
                    {
                        LoadConfig(File.ReadAllText(aPath));
                    }
                }
                else //Try to load from Application.persistentDataPath
                {
                    var aPath = GetSavePath(UCL_AssetType.PersistentDatas);
                    if (File.Exists(aPath))
                    {
                        LoadConfig(File.ReadAllText(aPath));
                    }
                    else //config not exist!! try to load from streamming asset
                    {
                        try
                        {
                            string aJson = await UCL_StreamingAssets.LoadString(SaveRelativePath);
                            if (!string.IsNullOrEmpty(aJson))
                            {
                                LoadConfig(aJson);
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                        //aPath = GetSavePath(UCL_AssetType.StreamingAssets);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_LoadingConfig = false;
            }
        }
        virtual protected void LoadConfig(string iJson)
        {
            JsonData aJsonData = JsonData.ParseJson(iJson);
            m_Config.DeserializeFromJson(aJsonData);
        }
        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            using(var aScope = new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("UCL_ModuleService", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if(GUILayout.Button("Save Config", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    SaveConfig();
                }
                if (GUILayout.Button("Load Config", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    LoadConfig().Forget();
                }
            }
            
            m_Config.OnGUI(iDataDic.GetSubDic("Config"));
        }
    }
}
