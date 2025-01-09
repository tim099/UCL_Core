using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core.EditorLib.Page
{
    public class UCL_EditorMenuPage : UCL_EditorPage
    {
        public override string WindowName => "UCL_EditorMenu";
        protected override bool ShowCloseButton => false;
        protected override bool ShowBackButton => false;

        //UCL.Core.UCL_ObjectDictionary m_Dic = new UCL.Core.UCL_ObjectDictionary();

        /// <summary>
        /// Draw Editor Munu
        /// </summary>
        protected override void ContentOnGUI()
        {
            using (var aScope = new GUILayout.VerticalScope("box"))//, GUILayout.MaxWidth(320)
            {
                using (var aScopeH = new GUILayout.HorizontalScope("box"))
                {
                    var aStyleData = UCL_GUIStyle.CurStyleData;
                    UCL_GUIStyle.SetSizeOnGUI();
                }


                if (GUILayout.Button(UCL_LocalizeManager.Get("Edit Modules"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_ModuleServiceEditPage.Create();
                }

                if (GUILayout.Button(UCL_LocalizeManager.Get("UCL_LocalizeEditPage"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_EditorPage.Create<UCL_LocalizeEditPage>();
                }
            }
        }
    }
}