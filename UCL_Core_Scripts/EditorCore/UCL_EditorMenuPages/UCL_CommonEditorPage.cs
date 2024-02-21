
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/21 2024 10:13
using System.Collections;
using System.Collections.Generic;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    public class UCL_CommonEditorPage : UCL_EditorPage
    {
        protected string m_TypeName;

        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            base.Init(iGUIPageController);
            m_TypeName = this.GetType().Name;
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            GUILayout.Label(m_TypeName, UCL_GUIStyle.LabelStyle);
            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                GUIUtility.systemCopyBuffer = m_TypeName;
            }
        }
    }
}
