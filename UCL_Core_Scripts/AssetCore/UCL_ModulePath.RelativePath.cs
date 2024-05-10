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
        #region RelativePath
        public static class RelativePath
        {
            public const string ModuleServicePath = ".ModuleService";
            public const string ModulesRootRelativePath = "ModulesRoot";
            public const string BuiltinModulesZipFolder = "ZipModules";
            public const string ModulesFolderName = "Modules";

            /// <summary>
            /// path of Config file(StreamingAssets\.ModuleService\Config.json)
            /// </summary>
            public static string BuiltinConfigPath => Path.Combine(ModuleServicePath, ConfigFileName);
            public static string BuiltinModulesFolder => ModulesFolderName;



            public static string ModulesFolder => Path.Combine(ModulesRootRelativePath, ModulesFolderName);

            /// <summary>
            /// .ModuleService\Modules\{iID}
            /// </summary>
            /// <param name="iID"></param>
            /// <returns></returns>
            public static string GetBuiltinModulePath(string iID) => Path.Combine(BuiltinModulesFolder, iID);
            /// <summary>
            /// ModulesRoot\Modules\{iID}
            /// </summary>
            /// <param name="iID"></param>
            /// <returns></returns>
            public static string GetModulePath(string iID) => Path.Combine(ModulesFolderName, iID);
        }

        #endregion

        #region ModuleRelativePath
        /// <summary>
        /// RelativePath under each module
        /// </summary>
        public static class ModuleRelativePath
        {
            public static string GetAssetRelativePath(System.Type iType) => $"UCL_Assets/{iType.Name}";
        }

        #endregion
    }
}
