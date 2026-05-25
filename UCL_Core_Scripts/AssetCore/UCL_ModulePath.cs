
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_ModulePath
    {
        public const string ConfigFileName = "Config.json";
        //public const string FileInfosFileName = "FileInfos.json";
        //public const string ModResourcesName = "ModResources";


        #region Common
        /// <summary>
        /// Build 前置：把 Builtin 模組打包進 StreamingAssets。
        /// 一般模組 → zip 進 ZipModules/ (runtime 解壓安裝)；
        /// 標記 m_PCDirectStreaming 且本次為 Standalone(PC) build 的模組 → 原始檔直接複製進 .ModuleService/Modules/{id} (runtime 免安裝直讀)。
        /// </summary>
        /// <param name="iIsStandaloneBuild">本次 build target 是否為 Standalone(PC)；非 PC 一律忽略 m_PCDirectStreaming，全部走 zip (其他平台 100% 原邏輯)</param>
        public static void OnPreprocessBuild(bool iIsStandaloneBuild)
        {
            //UCL_ModulePath.PersistantPath.GetModulesEntry(m_ModuleEditType).ConfigPath;
            var entry = UCL_ModulePath.PersistantPath.GetModulesEntry(UCL_ModuleEditType.Builtin);
            var config =  entry.LoadConfig();

            // 區塊職責：收集「PC 免安裝直讀」模組 ID (僅 Standalone build 才生效)。
            // 物理意義：m_ExportModules 提供「哪些模組要輸出 (m_ExportModule)」；免安裝旗標則讀模組自身 Config (UCL_Module.Config.m_PCDirectStreaming)。
            //          這些模組不 zip，改原始檔複製進 StreamingAssets；其餘走原本 zip 流程。
            // 數值影響：非 Standalone build → aDirectStreamingIDs 為空 → 行為與今天逐位元一致。
            var aDirectStreamingIDs = new List<string>();
            if (iIsStandaloneBuild && config != null && config.m_ExportModules != null)
            {
                foreach (var aKV in config.m_ExportModules)
                {
                    if (aKV.Value != null && aKV.Value.m_ExportModule)
                    {
                        // 免安裝旗標現在在模組自身 Config，讀 Builtin 模組的 Config.json
                        var aModuleConfig = entry.GetModuleEntry(aKV.Key).GetConfig();
                        if (aModuleConfig != null && aModuleConfig.m_PCDirectStreaming)
                        {
                            aDirectStreamingIDs.Add(aKV.Key);
                        }
                    }
                }
            }

            // 一般模組 zip (排除免安裝模組避免重複出貨)
            entry.ZipAllModules(config, aDirectStreamingIDs);

            // 免安裝模組：原始資料夾直接複製進 StreamingAssets/.ModuleService/Modules/{id}
            foreach (var aID in aDirectStreamingIDs)
            {
                CopyBuiltinModuleToStreaming(entry, aID);
            }

            //Save Builtin config to StreamingAssets
            string aConfigPath = entry.ConfigPath;
            if (File.Exists(aConfigPath))//Copy config to streaming asset
            {
                File.Copy(aConfigPath, UCL_ModulePath.PersistantPath.ConfigInstallPath, true);
            }
            else
            {
                Debug.LogError($"OnPreprocessBuild, !File.Exists(aConfigPath) aConfigPath:{aConfigPath}");
            }
        }

        /// <summary>
        /// 把單一 Builtin 模組的原始資料夾複製進 StreamingAssets 的免安裝直讀位置 (.ModuleService/Modules/{id})。
        /// 來源：Builtin 模組 RootFolder (dataPath/.BuiltinModules/ModulesRoot/Modules/{id})；
        /// 目標：StreamingReadOnly ModulesEntry 解析出的 streamingAssetsPath/.ModuleService/Modules/{id}。
        /// </summary>
        static void CopyBuiltinModuleToStreaming(PersistantPath.ModulesEntry iBuiltinEntry, string iID)
        {
            string aSrc = iBuiltinEntry.GetModuleEntry(iID).RootFolder;
            string aDst = PersistantPath.StreamingReadOnly.GetModulePath(iID);
            if (!Directory.Exists(aSrc))
            {
                Debug.LogError($"CopyBuiltinModuleToStreaming ID:{iID}, 來源不存在 aSrc:{aSrc}");
                return;
            }
            if (Directory.Exists(aDst))//清掉舊副本避免殘留
            {
                Directory.Delete(aDst, true);
            }
            UCL.Core.FileLib.Lib.CopyDirectory(aSrc, aDst);
            Debug.LogWarning($"CopyBuiltinModuleToStreaming ID:{iID} 原始檔複製進 StreamingAssets, aDst:{aDst}");
        }
        /// <summary>
        /// zip all Builtin modules to Streamimg assets folder
        /// </summary>
        public static void ZipAllModules()
        {
            var aModulePath = UCL_ModulePath.PersistantPath.GetModulesEntry(UCL_ModuleEditType.Builtin);
            aModulePath.ZipAllModules();
        }
        /// <summary>
        /// remove all zip modules from Streamimg assets folder
        /// </summary>
        public static void RemoveAllZipAllModules()
        {
            string aFolderPath = PersistantPath.ModulesZipFolder;
            if (!Directory.Exists(aFolderPath))
            {
                return;
            }

            Directory.Delete(aFolderPath, true);
        }

        /// <summary>
        /// Build 後置清理：移除 OnPreprocessBuild 為「PC 免安裝模組」複製進 StreamingAssets 的原始檔副本 (.ModuleService/Modules)。
        /// 物理意義：這些原始檔已被 build 烘進輸出，源專案 StreamingAssets 不該殘留 (保持 git 乾淨、避免 Editor 誤讀)。
        /// 數值影響：只刪 OnPreprocessBuild 自己建的 Modules 資料夾；不存在則 no-op。
        /// </summary>
        public static void RemoveDirectStreamingRawModules()
        {
            string aFolderPath = PersistantPath.StreamingReadOnly.ModulesPath;//streamingAssetsPath/.ModuleService/Modules
            if (!Directory.Exists(aFolderPath))
            {
                return;
            }
            Directory.Delete(aFolderPath, true);
        }
        #endregion

    }
}

