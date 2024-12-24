
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 03/02 2024 11:01
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;




namespace UCL.Core
{
    public class UCL_ModuleEntry : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ShortName, UCLI_FieldOnGUI
    {
        public const string CoreModuleID = "Core";
        public static UCL_ModuleEntry CoreModule => new UCL_ModuleEntry(CoreModuleID);



        /// <summary>
        /// Get all modules ID
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllIDs()
        {
            return UCL_ModuleService.Ins.GetAllModuleIDs();
        }
        [UCL.Core.PA.UCL_List(nameof(GetAllIDs))]
        public string m_ID = CoreModuleID;


        public UCL_ModuleEntry() { }
        public UCL_ModuleEntry(string iID)
        {
            m_ID = iID;
        }
        public string GetShortName() => ID;//$"UCL_ModuleEntry({ID})";
        virtual public string ID { get => m_ID; set => m_ID = value; }
        /// <summary>
        /// 抓取對應的模組
        /// </summary>
        virtual public UCL_Module Module => UCL_ModuleService.Ins.GetModule(m_ID);

        /// <summary>
        /// 在編輯器中繪製
        /// </summary>
        virtual public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDic)
        {
            GUILayout.BeginHorizontal();
            bool aIsPreview = UCL.Core.UI.UCL_GUILayout.Toggle(iDic, nameof(aIsPreview));
            GUILayout.Label(iFieldName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            
            var aIDs = GetAllIDs();
            var allModuleNames = UCL_ModuleService.Ins.GetAllModuleNames();

            var index = UCL_GUILayout.PopupAuto(aIDs.IndexOf(ID), allModuleNames, iDic, nameof(ID));
            ID = aIDs[index];

            //UCL.Core.UI.UCL_GUILayout.DrawField(this, iDic, iFieldName, true, UCL.Core.UCL_StaticFunctions.LocalizeFieldName);
            GUILayout.EndHorizontal();

            
            //GUILayout.BeginHorizontal();
            //bool aIsPreview = UCL.Core.UI.UCL_GUILayout.Toggle(iDic, "PreviewToggle");
            //Debug.LogError($"aIsPreview:{aIsPreview}");
            //GUILayout.Label(UCL_LocalizeManager.Get("Preview"), UCL_GUIStyle.LabelStyle);
            //GUILayout.EndHorizontal();
            if (aIsPreview)
            {
                UCL_Module module = Module;
                if (module != null)
                {
                    module.OnGUI(m_ID, iDic.GetSubDic("ModuleContentOnGUI"));
                }
            }
            return this;
        }
        #region PA
        public override int GetHashCode()
        {
            return ID.GetHashCode();
        }
        public override bool Equals(object iObj)
        {
            return Equals(iObj as UCL_ModuleEntry);
        }

        public virtual bool Equals(UCL_ModuleEntry iObj)
        {
            if (iObj == null) return false;
            return iObj.ID == ID;
        }
        #endregion
    }
}