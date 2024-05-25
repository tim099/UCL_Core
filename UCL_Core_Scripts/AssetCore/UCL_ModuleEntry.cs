
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 03/02 2024 11:01
using System.Collections;
using System.Collections.Generic;
using UnityEngine;




namespace UCL.Core
{
    public class UCL_ModuleEntry : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ShortName
    {
        public const string CoreModuleID = "Core";

        protected const string FuncKeyGetAllIDs = "GetAllIDs";
        /// <summary>
        /// Get all modules ID
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllIDs()
        {
            return UCL_ModuleService.Ins.GetAllModulesID();
        }
        [UCL.Core.PA.UCL_List(FuncKeyGetAllIDs)]
        public string m_ID = CoreModuleID;


        public UCL_ModuleEntry() { }
        public UCL_ModuleEntry(string iID)
        {
            m_ID = iID;
        }
        public string GetShortName() => $"UCL_ModuleEntry({ID})";
        virtual public string ID { get => m_ID; set => m_ID = value; }


    }
}