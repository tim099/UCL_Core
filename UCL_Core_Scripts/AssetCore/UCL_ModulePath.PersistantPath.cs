
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_ModulePath
    {
        public static partial class PersistantPath
        {
            public static ModulesEntry Builtin
            {
                get
                {
                    if (s_Builtin == null)
                    {
                        s_Builtin = new ModulesEntry(UCL_ModuleEditType.Builtin);
                    }
                    return s_Builtin;
                }
            }
            private static ModulesEntry s_Builtin = null;

            public static ModulesEntry Runtime
            {
                get
                {
                    if (s_Runtime == null)
                    {
                        s_Runtime = new ModulesEntry(UCL_ModuleEditType.Runtime);
                    }
                    return s_Runtime;
                }
            }
            private static ModulesEntry s_Runtime = null;

            public static ModulesEntry GetModulesEntry(UCL_ModuleEditType iModuleEditType)
            {
                switch (iModuleEditType)
                {
                    case UCL_ModuleEditType.Builtin:
                        {
                            return PersistantPath.Builtin;
                        }
                    case UCL_ModuleEditType.Runtime:
                        {
                            return PersistantPath.Runtime;
                        }
                }
                return PersistantPath.Runtime;
            }

            /// <summary>
            /// ModulesZipFolder always in Streamming assets!!
            /// </summary>
            public static string ModulesZipFolder => Path.Combine(Application.streamingAssetsPath, RelativePath.ModuleServicePath, RelativePath.BuiltinModulesZipFolder);

            /// <summary>
            /// Config直接放在StreamingAssets中
            /// </summary>
            public static string ConfigInstallPath => Path.Combine(Application.streamingAssetsPath, RelativePath.ModuleServicePath, UCL_ModulePath.ConfigFileName);
            #region ModulePathConfig
            /// <summary>
            /// Path config of all modules base on UCL_ModuleEditType(Builtin or Runtime)
            /// </summary>
            public class ModulesEntry
            {

                public UCL_ModuleEditType ModuleEditType;

                /// <summary>
                /// Builtin is (Path.Combine(Application.dataPath, ".BuiltinModules"))
                /// </summary>
                public string RootFolder;

                /// <summary>
                /// Path.Combine(RootFolder, RelativePath.ModulesRootRelativePath, RelativePath.ModulesFolderName);
                /// </summary>
                public string ModulesPath;
                /// <summary>
                /// Path.Combine(RootFolder, UCL_ModulePath.ConfigFileName)
                /// </summary>
                public string ConfigPath;

                private Dictionary<string, ModuleEntry> m_ModuleConfigDic = new Dictionary<string, ModuleEntry>();

                public ModulesEntry(UCL_ModuleEditType iModuleEditType)
                {
                    ModuleEditType = iModuleEditType;
                    switch (ModuleEditType)
                    {
                        case UCL_ModuleEditType.Builtin:
                            {
                                RootFolder = Path.Combine(UCL_AssetPath.GetPath(UCL_AssetType.BuiltinModules), RelativePath.ModulesRootRelativePath);
                                break;
                            }
                        case UCL_ModuleEditType.Runtime:
                            {
                                RootFolder = Path.Combine(UCL_AssetPath.GetPath(UCL_AssetType.PersistentDatas), RelativePath.ModulesRootRelativePath);
                                break;
                            }
                    }
                    ModulesPath = Path.Combine(RootFolder, RelativePath.ModulesFolderName);

                    ConfigPath = Path.Combine(RootFolder, UCL_ModulePath.ConfigFileName);
                    //Debug.LogError($"ModulePathConfig ModuleEditType:{ModuleEditType},RootFolder:{RootFolder}" +
                    //    $"\nModulesZipFolder:{PersistantPath.ModulesZipFolder}" +
                    //    $"\nModulesPath:{ModulesPath}");
                }
                /// <summary>
                /// return root path of module
                /// </summary>
                /// <param name="iID"></param>
                /// <returns></returns>
                public string GetModulePath(string iID)
                {
                    string aPath = Path.Combine(RootFolder, RelativePath.GetModulePath(iID));
                    //Debug.LogError($"GetModulePath iID:{iID} aPath:{aPath}" +
                    //    $"\nRootFolder:{RootFolder}");
                    return aPath;
                }
                /// <summary>
                /// return ModuleConfig of module
                /// </summary>
                /// <param name="iID">id of module</param>
                /// <returns></returns>
                public ModuleEntry GetModuleEntry(string iID)
                {
                    try
                    {
                        if (!m_ModuleConfigDic.ContainsKey(iID))
                        {
                            m_ModuleConfigDic[iID] = new ModuleEntry(this, iID);
                        }
                    }
                    catch(System.Exception ex)
                    {
                        Debug.LogException(ex);
                        Debug.LogError($"GetModuleConfig iID:{iID},Exception:{ex}");
                        return null;
                    }

                    return m_ModuleConfigDic[iID];
                }
                public IList<string> GetAllModulesID()
                {
                    string aPath = ModulesPath;

                    var aIDs = UCL.Core.FileLib.Lib.GetDirectories(aPath, iSearchOption: SearchOption.TopDirectoryOnly, iRemoveRootPath: true);

                    //Debug.LogError($"ModulePath.GetAllModulesID ModuleEditType:{ModuleEditType} ,aPath:{aPath},aIDs:{aIDs.ConcatString()}");
                    return aIDs;
                }
                public static string GetModulesZipPath(string iID)
                {
                    return $"{PersistantPath.ModulesZipFolder}/{iID}.zip";
                }
                public static string GetModulesZipConfigPath(string iID)
                {
                    return $"{PersistantPath.ModulesZipFolder}/{iID}.json";
                }
                /// <summary>
                /// zip all Builtin modules to Streamimg assets folder
                /// </summary>
                public void ZipAllModules()
                {
                    var aIDs = GetAllModulesID();

                    Debug.LogWarning($"ZipAllModules aIDs:{aIDs.ConcatString()}");
                    if (aIDs.IsNullOrEmpty())//No modules exist
                    {
                        return;
                    }
                    string aZipFolderPath = PersistantPath.ModulesZipFolder;
                    if (Directory.Exists(aZipFolderPath))
                    {
                        Directory.Delete(aZipFolderPath, true);
                    }
                    Directory.CreateDirectory(aZipFolderPath);//Create root folder

                    foreach (var aID in aIDs)
                    {
                        ModuleEntry aConfig = GetModuleEntry(aID);
                        aConfig.ZipModule(aZipFolderPath);

                        //string aPath = GetModulePath(aID);

                        //string aZipPath = GetModulesZipPath(aID);
                        //string aZipConfigPath = GetModulesZipConfigPath(aID);
                        //System.IO.Compression.ZipFile.CreateFromDirectory(aPath, aZipPath);

                        //using (ZipArchive aZip = ZipFile.Open(aZipPath, ZipArchiveMode.Read))//try to read config
                        //{
                        //    string aConfig = aZip.ReadAllTextFromEntry("Config.json");
                        //    Debug.LogError($"aID:{aID},aConfig:{aConfig}");
                        //}
                    }
                    //System.IO.Compression.ZipFile.CreateFromDirectory("zipdir", "todir");

                    //foreach (ZipArchiveEntry aEntry in aZip.Entries)
                    //{
                    //    Debug.LogError($"entry.Name:{aEntry.Name}");
                    //    if (aEntry.Name == "Config.json")
                    //    {
                    //        //entry.ExtractToFile("myfile");
                    //        using (Stream aStream = aEntry.Open())
                    //        {
                    //            // convert stream to string
                    //            using (StreamReader aReader = new StreamReader(aStream))
                    //            {
                    //                string text = aReader.ReadToEnd();
                    //                Debug.LogError($"entry.Name:{aEntry.Name},text:{text}");
                    //            }
                    //        }
                    //    }
                    //}

                }
            }
            #endregion
        }






    }
}

