
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:52
using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.Page
{
    public class UCL_EditCommonDataPage : UCL_CommonEditorPage
    {

        public override string WindowName => UCL_LocalizeManager.Get("EditCommonDataPage");

        static public UCL_EditCommonDataPage Create() => UCL_EditorPage.Create<UCL_EditCommonDataPage>();
        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public UCL_EditCommonDataPage()
        {

        }
        ~UCL_EditCommonDataPage()
        {

        }

        protected override void ContentOnGUI()
        {
            if (!UCL_ModuleService.Initialized)
            {
                return;
            }
            UCL_ModuleService.Ins.OnGUI(m_DataDic.GetSubDic("ModuleService"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);
            var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
            var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);
            foreach (var aType in UCLI_Asset.GetAllAssetTypes())
            {
                try
                {
                    string aPropInfosStr = string.Empty;
                    try
                    {
                        UCLI_Asset aUtil = UCLI_Asset.GetUtilByType(aType);//Get Util
                        if (aUtil != null)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(aType.Name, aLabelStyle, GUILayout.ExpandWidth(false));
                            if(GUILayout.Button($"Edit", aButtonStyle, GUILayout.Width(100)))
                            {
                                aUtil.CreateSelectPage();
                            }
                            //GUILayout.Label($"{aType.FullName}");
                            //aUtil.RefreshAllDatas();
                            //Debug.LogWarning($"Util:{aUtil.GetType().FullName}.RefreshAllDatas()");
                            GUILayout.EndHorizontal();
                        }
                    }
                    catch (Exception iE)
                    {
                        Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
                        Debug.LogException(iE);
                    }
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                }

            }
        }
    }
}
