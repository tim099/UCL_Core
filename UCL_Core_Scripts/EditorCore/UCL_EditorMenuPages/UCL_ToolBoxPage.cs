// 區塊職責：工具集入口頁 — 把散落在 EditorMenu 外層的工具型頁面收攏成一個入口
// 物理意義：EditorMenu 外層按鈕會隨功能增加無上限成長（Welcome / 文件搜尋 / 模組 / 多語系 /
//          Agent 指令 / 酒館 / 控制台 / 登入狀態…）。工具型的頁面收進這裡一層，
//          外層只留一顆「工具集」，之後新增工具改這一個檔即可，不必再動 EditorMenu。
// 數值影響：純導覽 —— 每顆按鈕 push 一個既有頁到當前 controller，本頁不持有任何狀態、不寫任何檔。
//
// 設計取捨（Tim 2026-08-05 指派，仿 UCL_ControlPanelPage）：
//   · ShowInPageMenu => false：本頁由 EditorMenu 外層按鈕進入，不重複出現在下拉頁選單
//   · 每個工具一行：按鈕 + 一句「它解什麼問題」。**只寫按鈕名的入口頁等於沒有資訊** ——
//     人要的是「我現在該點哪一個」，那需要一句話說明，不是一個名字。
//   · 不在這裡放任何工具的實際功能 —— 入口頁一旦開始長功能，就會變成第二個控制台。
#if UNITY_EDITOR
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 工具集 — 開發用工具頁面的集合入口（Git 攤平同步 / 文件搜尋 / 多語系編輯）。
    /// </summary>
    public class UCL_ToolBoxPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("ToolBox.Title");

        // 已是 EditorMenu 外部按鈕（見 UCL_EditorMenuPage），關閉下拉避免重複出現
        public override bool ShowInPageMenu => false;

        public static UCL_ToolBoxPage Create() => UCL_EditorPage.Create<UCL_ToolBoxPage>();

        protected override void ContentOnGUI()
        {
            DrawTool(UCL_CodeLocalize.Get("ToolBox.GitFlattenSync"),
                UCL_CodeLocalize.Get("ToolBox.GitFlattenSync.Desc"),
                () => UCL_GitFlattenSyncPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.GitSubmoduleSync"),
                UCL_CodeLocalize.Get("ToolBox.GitSubmoduleSync.Desc"),
                () => UCL_GitSubmoduleSyncPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.AutoCommit"),
                UCL_CodeLocalize.Get("ToolBox.AutoCommit.Desc"),
                () => UCL_AutoCommitPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.DocSearch"),
                UCL_CodeLocalize.Get("ToolBox.DocSearch.Desc"),
                () => UCL_DocSearchPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.LocalizeEdit"),
                UCL_CodeLocalize.Get("ToolBox.LocalizeEdit.Desc"),
                () => UCL_EditorPage.Create<UCL_LocalizeEditPage>());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.ScreenStream"),
                UCL_CodeLocalize.Get("ToolBox.ScreenStream.Desc"),
                () => UCL_ScreenStreamPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.ProcessAdmin"),
                UCL_CodeLocalize.Get("ToolBox.ProcessAdmin.Desc"),
                () => UCL_ProcessAdminPage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.LibraryManage"),
                UCL_CodeLocalize.Get("ToolBox.LibraryManage.Desc"),
                () => UCL_LibraryManagePage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.ReadingNotesManage"),
                UCL_CodeLocalize.Get("ToolBox.ReadingNotesManage.Desc"),
                () => UCL_ReadingNotesManagePage.Create());

            DrawTool(UCL_CodeLocalize.Get("ToolBox.SculptureViewer"),
                UCL_CodeLocalize.Get("ToolBox.SculptureViewer.Desc"),
                () => UCL_SculptureViewerPage.Create());
        }

        // 區塊職責：畫一個工具列（按鈕 + 說明）
        // 物理意義：按鈕寬度固定，說明用 wrap label 佔剩餘寬度 —— 視窗變窄時說明換行，
        //          按鈕不會被壓成看不見（IMGUI 下 ExpandWidth(false) 的按鈕會被 label 擠掉）。
        void DrawTool(string label, string desc, System.Action open)
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                if (GUILayout.Button(label, UCL_GUIStyle.GetButtonStyle(Color.cyan),
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(240))))
                {
                    open?.Invoke();
                }
                GUILayout.Label(desc, WrapLabelStyle);
            }
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
        };
    }
}
#endif
