using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
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
                //GUILayout.Box("OuO");
                //if (GUILayout.Button("Test"))
                //{
                //    Debug.LogError("Test!!");
                //}
                if (GUILayout.Button(UCL_LocalizeManager.Get("UCL_LocalizeEditPage")))
                {
                    UCL_EditorPage.Create<UCL_LocalizeEditPage>();
                }
            }
        }
    }
}