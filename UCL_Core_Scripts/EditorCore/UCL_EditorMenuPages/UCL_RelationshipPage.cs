// 區塊職責：relationship 後台頁 —— 看某位 persona 對某個對象的關係。
// 物理意義：新系統（letters/<persona>/relationship/）的 UI；舊 UCL_AffinitySystemPage 的接班人。
// 數值影響：唯讀 —— 只讀 relationship 資料，不寫檔（舊 affinity 一次性遷移入口已於 2026-08-19 移除）。
// 設計沿革：Plan_Relationship_System.md（Tim 2026-08-18 拍板）。
// 2026-08-18 calli
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.Relationship;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 關係管理頁 —— persona / 對象兩層下拉。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md")]
    public class UCL_RelationshipPage : UCL_CommonEditorPage
    {
        public override string WindowName => "關係（Relationship）";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // PopupSearchCache 的快取容器。
        // ⚠ 折疊狀態不共用這一個 —— 資料重載路徑上的 Clear() 會把折疊值一併清掉
        //   （症狀是「收不起來」，看起來像 key 撞名）。
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        readonly List<string> m_Personas = new();
        readonly List<string> m_Targets = new();
        int m_PersonaIdx = 0, m_TargetIdx = 0;
        bool m_Loaded = false;

        GUIStyle m_SmallStyle;
        GUIStyle SmallStyle
        {
            get
            {
                if (m_SmallStyle == null)
                    m_SmallStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return m_SmallStyle;
            }
        }

        public static UCL_RelationshipPage Create()
        {
            var page = new UCL_RelationshipPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        // ===========================================================
        // 區塊職責：persona 選擇器放 TopBar（照 UCL_BankAdminPage，Tim 指定）。
        // 物理意義：persona 是**整頁的視角**不是某一區的參數 —— 放內容區的話，
        //          捲到下面就看不到「現在在看誰」，而那正是最容易看錯資料的時候。
        // ===========================================================
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                LoadData();
            if (m_Personas.Count > 0)
            {
                int aNew = UCL_GUILayout.PopupSearchCache(m_PersonaIdx, m_Personas, m_Dic,
                    "RelationshipPersonaPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                if (aNew != m_PersonaIdx && aNew >= 0 && aNew < m_Personas.Count)
                {
                    m_PersonaIdx = aNew;
                    LoadTargets();
                    GUI.FocusControl(null);
                }
            }
        }

        void LoadData()
        {
            m_Personas.Clear();
            string aLetters = UCL_LettersPath.Root;      // letters 的正式入口，見 UCL_RelationshipIO 註解
            if (Directory.Exists(aLetters))
            {
                foreach (var d in Directory.GetDirectories(aLetters))
                {
                    // 只列真的有 relationship 資料的 —— 列出 30 個空資料夾等於把選單變成雜訊
                    if (Directory.Exists(Path.Combine(d, UCL_RelationshipIO.DIR_NAME)))
                        m_Personas.Add(Path.GetFileName(d));
                }
            }
            m_Personas.Sort(string.CompareOrdinal);
            if (m_PersonaIdx >= m_Personas.Count) m_PersonaIdx = 0;
            LoadTargets();
            m_Loaded = true;
        }

        void LoadTargets()
        {
            m_Targets.Clear();
            if (m_PersonaIdx < 0 || m_PersonaIdx >= m_Personas.Count) return;
            string d = UCL_RelationshipIO.PersonaDir(m_Personas[m_PersonaIdx]);
            if (!Directory.Exists(d)) return;
            foreach (var t in Directory.GetDirectories(d)) m_Targets.Add(Path.GetFileName(t));
            m_Targets.Sort(string.CompareOrdinal);
            if (m_TargetIdx >= m_Targets.Count) m_TargetIdx = 0;
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            if (m_Personas.Count == 0)
            {
                GUILayout.Label("（還沒有任何 relationship 資料 —— 用 run_cmd run Relationship 寫第一筆事件）", SmallStyle);
                return;
            }

            // ── 對象下拉（Tim 2026-08-18：原本全部攤開，改成選一個看一個）──────
            // 一位 persona 可能有十幾個對象，全攤開的話要捲很久才看得到想看的那個，
            // 而「捲很久」實務上等於「只看得到最上面那幾個」。
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("對象：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (m_Targets.Count > 0)
                {
                    // ⚠ PopupSearchCache 選項為 0 時會 LogError ⇒ 這裡先擋掉才畫
                    int aNew = UCL_GUILayout.PopupSearchCache(m_TargetIdx, m_Targets, m_Dic,
                        "RelationshipTargetPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    if (aNew != m_TargetIdx && aNew >= 0 && aNew < m_Targets.Count)
                    {
                        m_TargetIdx = aNew;
                        GUI.FocusControl(null);
                    }
                }
                else GUILayout.Label("（這位還沒有任何對象）", SmallStyle);
                GUILayout.FlexibleSpace();
            }

            if (m_Targets.Count == 0) return;
            DrawTarget(m_Personas[m_PersonaIdx], m_Targets[m_TargetIdx]);
        }

        void DrawTarget(string iPersona, string iTarget)
        {
            string aCur = UCL_RelationshipIO.CurrentPath(iPersona, iTarget);
            if (!File.Exists(aCur))
            {
                GUILayout.Label($"⚠ 缺 `_current.md`：{aCur}", SmallStyle);
                return;
            }
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                GUILayout.Label($"{iPersona} → {iTarget}", UCL_GUIStyle.LabelStyle);
                GUILayout.Label(File.ReadAllText(aCur), SmallStyle);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("開啟 _current.md", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        UCL_MarkdownViewerPage.Create(aCur, aCur);
                    if (GUILayout.Button("開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        UnityEditor.EditorUtility.RevealInFinder(UCL_RelationshipIO.TargetDir(iPersona, iTarget));
                    GUILayout.Label($"事件 {CountMd(UCL_RelationshipIO.EventsDir(iPersona, iTarget))} 筆　"
                        + $"看法 {CountMd(UCL_RelationshipIO.OpinionsDir(iPersona, iTarget))} 則", SmallStyle);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        static int CountMd(string d) => Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;

    }
}
#endif
