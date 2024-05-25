
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
        public static void OnPreprocessBuild()
        {
            ZipAllModules();
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
        #endregion

    }
}

