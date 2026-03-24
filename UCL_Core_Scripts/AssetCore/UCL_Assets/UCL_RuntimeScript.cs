
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.JsonLib;
using UCL.Core.PA;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupID(UCL_AssetGroup.Assembly)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.AssemblyDataType.UCL_RuntimeScript)]
    public class UCL_RuntimeScript : UCL_Asset<UCL_RuntimeScript>
    {

        public UCL_Scope m_Scope = new();
        public override void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            base.OnGUI(iDataDic);

            if (GUILayout.Button("Trigger", UCL_GUIStyle.ButtonStyle))
            {
                m_Scope.Trigger();
            }
            //UCL_GUILayout.DrawObjectData(m_Scope, iDataDic, "Scope");

        }
    }

    [System.Serializable]
    public class UCL_StaticTypeSelector : UCLI_ShortName
    {
        [UCL.Core.PA.UCL_ValueDropdown(typeof(AssemblyExtensions), nameof(AssemblyExtensions.GetAllStaticTypesFullName))]
        public string m_TypeName;

        public string GetShortName() => ToString();

        public override string ToString()
        {
            return m_TypeName;
        }

        public Type TargetType
        {
            get
            {
                return AssemblyExtensions.GetTypeByFullName(m_TypeName);
            }
        }

        public UCL_StaticTypeSelector() { }
    }
}
