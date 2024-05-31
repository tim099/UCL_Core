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
    public class UCL_ModResourcesServicePage : UCL_CommonEditorPage
    {
        public override string WindowName => GetType().Name;

        static public UCL_ModResourcesServicePage Create() => UCL_EditorPage.Create<UCL_ModResourcesServicePage>();

        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public UCL_ModResourcesServicePage()
        {

        }
        ~UCL_ModResourcesServicePage()
        {

        }

        protected override void ContentOnGUI()
        {
            UCL_ModResourcesService.OnGUI(m_DataDic);

        }
    }
}