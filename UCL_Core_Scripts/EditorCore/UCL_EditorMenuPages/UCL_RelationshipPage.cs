// 區塊職責：relationship 後台頁 —— 看某位 persona 對某個對象的關係，以及一次性的遷移入口。
// 物理意義：新系統（letters/<persona>/relationship/）的 UI；舊 UCL_AffinitySystemPage 的接班人。
// 數值影響：讀 relationship 資料；遷移區塊會寫檔（但**乾跑先於執行**，且執行要二段確認）。
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
    /// 關係管理頁 —— persona / 對象兩層下拉，加上舊 affinity 的遷移入口。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md")]
    public class UCL_RelationshipPage : UCL_CommonEditorPage
    {
        public override string WindowName => "關係（Relationship）";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        const string PrefKey_Sources = "UCL_Relationship_MigrateSources";

        // PopupSearchCache 的快取容器。
        // ⚠ 折疊狀態不共用這一個 —— 資料重載路徑上的 Clear() 會把折疊值一併清掉
        //   （症狀是「收不起來」，看起來像 key 撞名）。
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        readonly List<string> m_Personas = new();
        readonly List<string> m_Targets = new();
        int m_PersonaIdx = 0, m_TargetIdx = 0;
        bool m_Loaded = false;

        // 遷移區塊的狀態
        string m_Sources = "";
        string m_LastReport = "";
        bool m_DryRunDone = false;      // ⇐ 執行遷移的前提（見 DrawMigrate 的註解）
        double m_ArmedTime = -1.0;
        const double ARM_WINDOW_SEC = 6.0;

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
            string aLetters = UCL.Core.EditorLib.AgentCommands.Awakening.UCL_AwakeningService.LettersDir;
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
            m_Sources = UCL_ProjectEditorPrefs.GetString(PrefKey_Sources, "");
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

            DrawMigrate();
            GUILayout.Space(10);

            if (m_Personas.Count == 0)
            {
                GUILayout.Label("（還沒有任何 relationship 資料 —— 先跑上面的遷移）", SmallStyle);
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

        // ===========================================================
        // 區塊職責：遷移區塊 —— 舊 affinity → 新 relationship。
        //
        // 物理意義：兩道閘刻意疊在一起，各防一件事：
        //   ① **乾跑先於執行**（`m_DryRunDone`）—— 遷移只該發生一次，
        //      而「跑之前先知道會寫幾檔」是唯一能在事前發現不對勁的機會。
        //   ② **二段確認**（arm）—— 防的是手滑，跟①防的不是同一件事。
        //
        // ⚠ 來源路徑走輸入欄位 ＋ EditorPrefs，**不寫死另一個專案的位置** ——
        //   寫死的路徑跨機器/跨專案必壞，而且通常是靜默壞（fail-soft 之後什麼都不說）。
        // ⚠ C 案（把剩下的 persona 也升成獨立 repo）完成後，多來源就只在遷移那一次用到 ——
        //   所以它是**一次性的遷移輸入，不是常態設定**（Plan §4.0）。
        // ===========================================================
        void DrawMigrate()
        {
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                GUILayout.Label("🚚 從舊 affinity 遷移", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("來源專案根，**一行一個**，格式 `<路徑>|<標籤>`；留空 = 只讀本專案。",
                    SmallStyle);
                string aNew = GUILayout.TextArea(m_Sources ?? "", UCL_GUIStyle.TextFieldStyle,
                    GUILayout.Height(UCL_GUIStyle.GetScaledSize(46)));
                if (aNew != m_Sources)
                {
                    m_Sources = aNew;
                    UCL_ProjectEditorPrefs.SetString(PrefKey_Sources, m_Sources ?? "");
                    m_DryRunDone = false;      // 來源改了 ⇒ 之前那次乾跑的數字不再對應現在要跑的東西
                    m_ArmedTime = -1.0;
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("① 乾跑（什麼都不寫）", UCL_GUIStyle.ButtonStyle,
                            GUILayout.ExpandWidth(false)))
                    {
                        m_LastReport = UCL_RelationshipIO.Run(m_Sources, true);
                        m_DryRunDone = true;
                        m_ArmedTime = -1.0;
                    }

                    // 乾跑過才給按 —— 不是提醒，是真的擋住（提醒會被略過，擋住不會）
                    using (new UnityEditor.EditorGUI.DisabledScope(!m_DryRunDone))
                    {
                        double aNow = UnityEditor.EditorApplication.timeSinceStartup;
                        bool aArmed = m_ArmedTime > 0 && aNow - m_ArmedTime < ARM_WINDOW_SEC;
                        var c = GUI.color;
                        if (aArmed) GUI.color = new Color(1f, 0.5f, 0.5f);
                        if (GUILayout.Button(aArmed ? "再點一次確認：執行遷移" : "② 執行遷移",
                                UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            if (aArmed)
                            {
                                m_LastReport = UCL_RelationshipIO.Run(m_Sources, false);
                                m_ArmedTime = -1.0;
                                LoadData();
                            }
                            else m_ArmedTime = aNow;
                        }
                        GUI.color = c;
                    }

                    if (GUILayout.Button("開啟報告", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        string p = UCL_RelationshipIO.ReportPath;
                        if (File.Exists(p)) UCL_MarkdownViewerPage.Create(p, p);
                        else Debug.LogError($"[Relationship] 還沒有報告檔：{p}");
                    }
                    GUILayout.FlexibleSpace();
                }

                if (!m_DryRunDone)
                    GUILayout.Label("⚠ 先按①乾跑；②在乾跑之前是鎖住的（遷移只該發生一次，"
                        + "事前看數字是唯一能發現不對勁的機會）。", SmallStyle);

                if (!string.IsNullOrEmpty(m_LastReport))
                {
                    bool aShow = UCL_GUILayout.Toggle(m_FoldDic, "RelMigrateReportFold", 18, iDefaultValue: true);
                    GUILayout.Label("最近一次結果", SmallStyle);
                    if (aShow) GUILayout.Label(m_LastReport, SmallStyle);
                }
            }
        }
    }
}
#endif
