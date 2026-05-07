
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
    /// 編輯器頁面共通基底：在 <see cref="UCL_EditorPage"/> 之上於 TopBar 顯示 ClassName + Copy 按鈕。
    /// 子類只需 override <c>WindowName</c> 與 <c>ContentOnGUI()</c> 即可。
    /// </summary>
    /// <remarks>
    /// 怎麼建一頁新的 EditorPage（必/選 override / TopBarButtons 客製 / 入口點掛接 /
    /// UI 元件選用對照 / 8 大常見地雷）見：
    /// <c>Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md</c>。
    /// 內部畫 UI 時：
    /// <list type="bullet">
    ///   <item>樣式取自 <c>UCL_GUIStyle</c>（見 <c>API/UCL_GUIStyle/UCL_GUIStyle_Overview.md</c>）</item>
    ///   <item>欄位 / 集合 / 物件繪製用 <c>UCL_GUILayout</c>（見 <c>API/UCL_GUILayout/UCL_GUILayout_Overview.md</c>）</item>
    /// </list>
    /// </remarks>
    [HelpURL("ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md")]
    public class UCL_CommonEditorPage : UCL_EditorPage
    {
        protected string m_TypeName;

        // 區塊職責：Page 是否要列入 UCL_EditorMenuPage 的「Page 選擇器」下拉
        // 物理意義：opt-in 設計 — 預設不出現；想被人類從 EditorMenu 直接打開的子 Page 自行 override 為 true
        // 數值影響：純 metadata；UCL_EditorMenuPage 透過反射收集所有 ShowInPageMenu==true 的子類顯示
        /// <summary>
        /// 是否要列入 <see cref="UCL_EditorMenuPage"/> 的 Page 選擇器下拉。預設 false（opt-in）。
        /// 子類若希望被使用者從主選單直接打開，覆寫成 true。
        /// </summary>
        public virtual bool ShowInPageMenu => false;

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
