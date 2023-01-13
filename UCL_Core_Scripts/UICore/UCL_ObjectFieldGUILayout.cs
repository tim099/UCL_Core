using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// The short name you want to display in UCL_ObjectFieldGUILayout
    /// 實作此interface的class在UCL_ObjectFieldGUILayout會優先顯示GetShortName()回傳的名稱
    /// </summary>
    public interface UCLI_ShortName
    {
        string GetShortName();
    }
    /// <summary>
    /// class with this interface will show Icon in UCL.Core.UI.UCL_GUILayout.DrawField
    /// </summary>
    public interface UCLI_Icon
    {
        Texture2D IconTexture { get; }
    }
}

namespace UCL.Core.UI
{
    /// <summary>
    /// Render field of object using GUILayout
    /// </summary>
    public class UCL_ObjectFieldGUILayout
    {
        protected UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        //Vector2 m_ScrollPos = Vector2.zero;
        public void ClearData()
        {
            m_DataDic.Clear();
        }
        virtual public void DrawObject(object iObj, string iDisplayName = "", bool iWithScroll = true, Func<string, string> iFieldNameFunc = null)
        {
            GUILayout.BeginVertical();
            if (iWithScroll)
            {
                Vector2 aPos = GUILayout.BeginScrollView(m_DataDic.GetData<Vector2>(iDisplayName + "_ScrollPos", Vector2.zero));
                m_DataDic.SetData(iDisplayName + "_ScrollPos", aPos);
            }

            UCL.Core.UI.UCL_GUILayout.DrawObjectData(iObj, m_DataDic.GetSubDic(iDisplayName), iDisplayName, iFieldNameFunc : iFieldNameFunc);

            if (iWithScroll) GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }
    }
}