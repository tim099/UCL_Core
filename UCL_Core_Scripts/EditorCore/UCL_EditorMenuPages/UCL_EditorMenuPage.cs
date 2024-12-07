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
                    float aScale = aStyleData.Scale;
                    if (aScale < 0.1f)
                    {
                        aScale = 0.1f;
                    }
                    int aSize = Mathf.RoundToInt(30f / aScale);
                    var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, aSize);
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Small"), aButtonStyle))
                    {
                        aStyleData.SetScale(1f);
                    }
                    GUILayout.Space(30);
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Medium"), aButtonStyle))
                    {
                        aStyleData.SetScale(1.5f);
                    }
                    GUILayout.Space(30);
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Big"), aButtonStyle))
                    {
                        aStyleData.SetScale(2.5f);
                    }
                    GUILayout.Space(30);
                    if (GUILayout.Button(UCL_LocalizeManager.Get("XL"), aButtonStyle))
                    {
                        aStyleData.SetScale(4f);
                    }
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