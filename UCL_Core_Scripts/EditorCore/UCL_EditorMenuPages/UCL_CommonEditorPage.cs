
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

    /// <summary>
    /// 文件關聯：對應的多語系說明文件位於 Docs~/{lang}/UCL_EditorPage/UCL_CommonEditorPage.md
    /// 物理意義：透過 [HelpURL] 將編輯器頁面與本地化文檔綁定，讓編輯器內的 ? 按鈕能依當前語系跳轉到對應 md。
    /// 數值影響：無執行期影響，僅影響 Inspector / 編輯器內的說明連結指向。
    /// Docs~\en\UCL_EditorPage\UCL_CommonEditorPage.md
    /// Docs~\zh-Hant\UCL_EditorPage\UCL_CommonEditorPage.md
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_CommonEditorPage.md")]
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
