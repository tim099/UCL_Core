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

        #region 分組資料

        // 區塊職責：一個工具入口的資料（localize key 前綴 ＋ 開頁動作）
        // 物理意義：KeyPrefix 同時決定名稱（`<prefix>`）與說明（`<prefix>.Desc`）兩個 localize key ——
        //          兩者共用同一個前綴，才不會出現「名稱翻了、說明沒翻」這種各自漂移的狀態。
        readonly struct ToolEntry
        {
            public readonly string KeyPrefix;
            public readonly System.Action Open;

            public ToolEntry(string iKeyPrefix, System.Action iOpen)
            {
                KeyPrefix = iKeyPrefix;
                Open = iOpen;
            }
        }

        // 區塊職責：一組工具（折疊狀態 key ＋ 組標題 localize key ＋ 成員清單）
        // 物理意義：**組員數量由 Tools.Length 現算**，不是寫死在標題字串裡 ——
        //          寫死的數字在有人加工具時不會報錯，只會讓標題列講一個比實作小的數。
        readonly struct ToolGroup
        {
            public readonly string FoldKey;
            public readonly string TitleKey;
            public readonly ToolEntry[] Tools;

            public ToolGroup(string iFoldKey, string iTitleKey, params ToolEntry[] iTools)
            {
                FoldKey = iFoldKey;
                TitleKey = iTitleKey;
                Tools = iTools;
            }
        }

        // 區塊職責：本頁的完整工具清單（唯一事實來源）
        // 物理意義：static readonly ⇒ 只在型別初始化時建一次，`ContentOnGUI` 每幀不配置陣列
        //          （IMGUI 每秒重畫數十次，Draw 裡 new 陣列＝穩定的 GC 壓力）。
        // 數值影響：新增工具 = 在對應組的陣列裡加一行；折疊 key 與組員數量都不必手動維護。
        // 設計取捨（Tim 2026-08-21 指派「依分組加折疊」）：分組的判準是**使用者這一刻在做哪件事**
        //          （在推 commit / 在管 agent 營運 / 在查文件…），不是「這支工具的實作住在哪個模組」——
        //          後者對點按鈕的人沒有意義。
        static readonly ToolGroup[] s_Groups =
        {
            new ToolGroup("Fold.Git", "ToolBox.Group.Git",
                new ToolEntry("ToolBox.GitFlattenSync", () => UCL_GitFlattenSyncPage.Create()),
                new ToolEntry("ToolBox.GitSubmoduleSync", () => UCL_GitSubmoduleSyncPage.Create()),
                new ToolEntry("ToolBox.AutoCommit", () => UCL_AutoCommitPage.Create())),

            new ToolGroup("Fold.AgentOps", "ToolBox.Group.AgentOps",
                new ToolEntry("ToolBox.TaskManager", () => UCL_TaskManagerPage.Create()),
                new ToolEntry("ToolBox.Relationship", () => UCL_RelationshipPage.Create()),
                new ToolEntry("ToolBox.SessionAdmin", () => UCL_SessionAdminPage.Create()),
                new ToolEntry("ToolBox.FreeTimeAdmin", () => UCL_FreeTimeAdminPage.Create())),

            new ToolGroup("Fold.Runtime", "ToolBox.Group.Runtime",
                new ToolEntry("ToolBox.LLMModelAdmin", () => UCL_LLMModelAdminPage.Create()),
                new ToolEntry("ToolBox.ScreenStream", () => UCL_ScreenStreamPage.Create()),
                new ToolEntry("ToolBox.ProcessAdmin", () => UCL_ProcessAdminPage.Create())),

            new ToolGroup("Fold.Docs", "ToolBox.Group.Docs",
                new ToolEntry("ToolBox.DocSearch", () => UCL_DocSearchPage.Create()),
                new ToolEntry("ToolBox.LocalizeEdit", () => UCL_EditorPage.Create<UCL_LocalizeEditPage>())),

            new ToolGroup("Fold.Content", "ToolBox.Group.Content",
                new ToolEntry("ToolBox.LibraryManage", () => UCL_LibraryManagePage.Create()),
                new ToolEntry("ToolBox.ReadingNotesManage", () => UCL_ReadingNotesManagePage.Create()),
                new ToolEntry("ToolBox.SculptureViewer", () => UCL_SculptureViewerPage.Create())),
        };

        #endregion

        // 區塊職責：保存各組的展開偏好，供 UCL_GUILayout.Toggle 持久化讀寫
        // 物理意義：折疊狀態專用容器，**不與 PopupSearchCache 共用** ——
        //          共用時資料重載路徑上的 Clear() 會把折疊值一併清掉（症狀是「收不起來」）。
        // 數值影響：每組一個 bool，生命週期＝頁面 instance（離開再進來回到預設值）。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        protected override void ContentOnGUI()
        {
            foreach (var aGroup in s_Groups)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    if (!FoldHeader(aGroup)) continue;   // 收合時只留標題列（組名 + 組員數）

                    foreach (var aTool in aGroup.Tools)
                    {
                        DrawTool(aTool);
                    }
                }
            }
        }

        // 區塊職責：畫一組的標題列（折疊鈕 + 組名 + 組員數），回傳是否展開
        // 物理意義：**預設收合**（iDefaultValue: false，Tim 2026-08-21 版面微調）—— 15 支工具全展開
        //          要捲很久，收合後一頁看得完 5 組；標題列帶組員數，所以「收著」不等於「藏起來」。
        // 數值影響：組員數現算自 Tools.Length，收合狀態下也看得到「這組有幾支工具」。
        bool FoldHeader(ToolGroup iGroup)
        {
            using (new GUILayout.HorizontalScope())
            {
                bool aShow = UCL_GUILayout.Toggle(m_FoldDic, iGroup.FoldKey, 21, iDefaultValue: false);
                GUILayout.Label($"<b>{UCL_CodeLocalize.Get(iGroup.TitleKey)}</b>" +
                        $"　<color=grey>({iGroup.Tools.Length})</color>",
                    RichLabelStyle, GUILayout.ExpandWidth(false));
                return aShow;
            }
        }

        // 區塊職責：畫一個工具列（按鈕 + 說明）
        // 物理意義：按鈕寬度固定，說明用 wrap label 佔剩餘寬度 —— 視窗變窄時說明換行，
        //          按鈕不會被壓成看不見（IMGUI 下 ExpandWidth(false) 的按鈕會被 label 擠掉）。
        void DrawTool(ToolEntry iTool)
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                if (GUILayout.Button(UCL_CodeLocalize.Get(iTool.KeyPrefix), UCL_GUIStyle.GetButtonStyle(Color.cyan),
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(240))))
                {
                    iTool.Open?.Invoke();
                }
                GUILayout.Label(UCL_CodeLocalize.Get(iTool.KeyPrefix + ".Desc"), WrapLabelStyle);
            }
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
        };

        // 組標題用 —— richText 讓 <b> / <color> 生效（不開的話標籤會直接印出 tag）
        GUIStyle m_RichLabelStyle;
        GUIStyle RichLabelStyle => m_RichLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            richText = true,
        };
    }
}
#endif
