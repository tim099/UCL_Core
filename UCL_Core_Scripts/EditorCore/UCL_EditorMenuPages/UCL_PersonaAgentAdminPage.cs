// 區塊職責：Persona & Agent 後台管理頁 — agent 開帳（含對應 bank）/ persona 建立（可選 fork 來源）/
//            persona 換綁 agent（Tim 2026-07-29 拍板，參考 UCL_ChatTavernAdminPage 與 UCL_BankAdminPage）。
// 物理意義：agent 與 persona 是兩層身分 —
//          (1) agent = 帳號層（claude-code / Zeta / Altair…），對應一個 bank（token 帳戶）；
//              權威表 = AgentCommands/AwakenInit/_registry_meta.json 的 agent_banks。
//          (2) persona = 人格層（summit / crest-001…），一檔一 persona，
//              存 AgentCommands/AwakenInit/personas/<name>.json，內含 agent 欄指向所屬 agent。
//          以前這些只能手改 JSON 或走 awakening.py CLI；本頁把三個高頻操作搬進 Editor。
// 數值影響：寫兩處檔案 —
//          _registry_meta.json 的 agent_banks（開 agent）、personas/<name>.json（建 persona / 換綁）；
//          可選種子額度走 UCL_TreasuryLedger.Credit（append-only，與 BankAdminPage 開戶同一路徑）。
// 設計取捨：
//   - persona 檔 schema **鏡像 awakening.py 的 fork_persona / 新建路徑**（identity_vector 64 維 [-1,1]、
//     vector_history 首筆、fork_lineage 鏈、wake_count 0、status/availability offline）。兩端共讀同一批檔，
//     schema 漂移會讓 morning ritual 讀不到 → 改這裡時務必同步看 awakening.py。
//   - 不 spawn python：C# 直接讀寫 json（同 awakening.py ↔ UCL_LoginStatusPage 的雙實作先例）。
//   - UI 字串硬編 zh-Hant（內部管理頁慣例，不走 CodeLocalize）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UCL.Core.EditorLib.AgentCommands.Treasury;    // 開 agent 時的可選種子額度
using UCL.Core.EditorLib.AgentCommands.ChatTavern;  // 操作通知發酒館主頻道
using UnityEditor;                                  // EditorApplication.timeSinceStartup（二段確認倒數）
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Persona & Agent 後台管理頁 — 開 agent（含 bank）/ 建 persona（可 fork）/ persona 換綁 agent。
    /// 入口：控制台 (UCL_ControlPanelPage) 的「🧬 Persona & Agent 管理」按鈕。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Workflows/Awakening_Ritual_Workflow.md")]
    public class UCL_PersonaAgentAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Persona & Agent 管理";
        public override bool ShowInPageMenu => true;

        public static UCL_PersonaAgentAdminPage Create() => UCL_EditorPage.Create<UCL_PersonaAgentAdminPage>();

        // ==== 路徑（與 BankAdminPage 同一套解析根：DataRoot = <RepoRoot>/AgentCommands）====
        static string DataRoot => UCL_AgentCommandsPath.DataRoot;
        static string RegistryMetaPath => Path.Combine(DataRoot, "AwakenInit", "_registry_meta.json");
        static string PersonasDir => Path.Combine(DataRoot, "AwakenInit", "personas");
        static string SessionLockDir => Path.Combine(DataRoot, "_session");

        // ==== awakening.py 的常數（改動前先確認兩端一致）====
        const int VECTOR_DIM = 64;              // _constants.vector_dim
        const int FORK_CHAIN_CAP = 5;           // _constants.fork_chain_cap
        const string NO_FORK_OPTION = "(不 fork — 全新 persona)";

        // ==== 顯示用快取（開頁 / Refresh / 操作後才重讀檔，不每幀掃磁碟）====
        readonly List<string> m_AgentKeys = new List<string>();                                  // agent_banks keys
        readonly Dictionary<string, string> m_AgentToBank = new Dictionary<string, string>();    // agent → bank
        readonly List<PersonaRow> m_Personas = new List<PersonaRow>();                           // persona 一覽
        readonly Dictionary<string, int> m_AgentPersonaCount = new Dictionary<string, int>();    // agent → persona 數
        readonly HashSet<string> m_LockedPersonas = new HashSet<string>();                       // 有 session lock（線上）
        bool m_Loaded = false;

        class PersonaRow
        {
            public string name;
            public string agent;
            public string model;
            public int wakeCount;
            public string status;
            public string forkedFrom;
            public int lineageDepth;
        }

        // ==== 折疊狀態與 Popup 快取分開存（workflow §5.1 血證：混用會讓 LoadData 清空折疊）====
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();      // Popup cache（LoadData 會 Clear）
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();  // 折疊狀態（永不隨資料重載清）

        // ==== 建 agent draft ====
        string m_NewAgentDraft = "";
        string m_NewAgentBankDraft = "";
        string m_NewAgentSeedDraft = "0";

        // ==== 建 persona draft ====
        string m_NewPersonaDraft = "";
        int m_NewPersonaAgentIdx = 0;
        string m_NewPersonaModelDraft = "";
        string m_NewPersonaRoleDraft = "";
        int m_ForkSourceIdx = 0;          // 0 = 不 fork

        // ==== 換綁 draft ====
        int m_RebindPersonaIdx = 0;
        int m_RebindAgentIdx = 0;
        string m_RebindArmedPersona = null;   // 二段確認：第一次點 arm，第二次才執行
        double m_RebindArmedAt = 0;
        const double REBIND_ARM_WINDOW_SEC = 5.0;

        string m_LastResultMsg = "";

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapLabelStyle;
            }
        }

        // ===========================================================
        // 區塊：資料載入 — registry meta + personas 目錄 + session lock 一次讀齊
        // 物理意義：persona 檔數量級是數十，開頁 / 操作後重讀成本可忽略；重點是**不要每幀讀**。
        // 數值影響：純讀取；單檔壞不擋整體（catch 後跳過該檔）。
        // ===========================================================
        void LoadData()
        {
            m_Loaded = true;
            m_AgentKeys.Clear();
            m_AgentToBank.Clear();
            m_Personas.Clear();
            m_AgentPersonaCount.Clear();
            m_LockedPersonas.Clear();
            m_Dic.Clear();

            try
            {
                if (File.Exists(RegistryMetaPath))
                {
                    var reg = JsonData.ParseJson(File.ReadAllText(RegistryMetaPath));
                    if (reg != null && reg.Contains("agent_banks"))
                    {
                        var ab = reg["agent_banks"];
                        if (ab.IsObject && ab.Dic != null)
                        {
                            foreach (var agent in ab.Dic.Keys.Where(k => !k.StartsWith("_"))
                                                             .OrderBy(k => k, StringComparer.Ordinal))
                            {
                                string bank = ab.GetString(agent, "");
                                if (string.IsNullOrEmpty(bank)) continue;
                                m_AgentKeys.Add(agent);
                                m_AgentToBank[agent] = bank;
                                m_AgentPersonaCount[agent] = 0;
                            }
                        }
                    }
                }

                if (Directory.Exists(PersonasDir))
                {
                    foreach (var pf in Directory.GetFiles(PersonasDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                    {
                        try
                        {
                            var pj = JsonData.ParseJson(File.ReadAllText(pf));
                            if (pj == null) continue;
                            var row = new PersonaRow
                            {
                                name = Path.GetFileNameWithoutExtension(pf),
                                agent = pj.GetString("agent", ""),
                                model = pj.GetString("model", ""),
                                wakeCount = pj.GetInt("wake_count", 0),
                                status = pj.GetString("status", "offline"),
                                forkedFrom = pj.GetString("forked_from", ""),
                                lineageDepth = (pj.Contains("fork_lineage") && pj["fork_lineage"].IsArray)
                                    ? pj["fork_lineage"].Count : 0,
                            };
                            m_Personas.Add(row);
                            if (!string.IsNullOrEmpty(row.agent) && m_AgentPersonaCount.ContainsKey(row.agent))
                                m_AgentPersonaCount[row.agent]++;
                        }
                        catch { /* 單檔壞不擋整體載入 */ }
                    }
                }

                // session lock = persona 目前是否被某 session 持有（換綁前要警告）
                if (Directory.Exists(SessionLockDir))
                {
                    foreach (var lf in Directory.GetFiles(SessionLockDir, "_persona_*.json"))
                    {
                        string n = Path.GetFileNameWithoutExtension(lf);
                        if (n.StartsWith("_persona_")) m_LockedPersonas.Add(n.Substring("_persona_".Length));
                    }
                }
            }
            catch (Exception ex)
            {
                SetResult($"❌ 載入失敗：{ex.Message}");
            }
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            DrawOverviewPanel();
            GUILayout.Space(8);
            DrawCreateAgentPanel();
            GUILayout.Space(8);
            DrawCreatePersonaPanel();
            GUILayout.Space(8);
            DrawRebindPanel();
            GUILayout.Space(8);
            DrawResultPanel();
        }

        // ===========================================================
        // 區塊：一覽 — agent（含 bank / persona 數）與 persona（含 agent / wake# / 血統）
        // ===========================================================
        void DrawOverviewPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "OverviewFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>🧬 身分一覽</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        LoadData();
                        SetResult("✓ 已重新載入 registry 與 persona 檔");
                    }
                    GUILayout.Label($"agent {m_AgentKeys.Count} 個 / persona {m_Personas.Count} 個",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label("<b>Agent（帳號層）</b>", WrapLabelStyle);
                foreach (var agent in m_AgentKeys)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"　• <b>{agent}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        GUILayout.Label($"bank: <b>{m_AgentToBank[agent]}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                        GUILayout.Label($"persona × {m_AgentPersonaCount[agent]}", WrapLabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                }

                GUILayout.Space(4);
                GUILayout.Label("<b>Persona（人格層）</b>　★ = 目前有 session lock（線上）", WrapLabelStyle);
                foreach (var p in m_Personas.OrderByDescending(x => x.wakeCount))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool locked = m_LockedPersonas.Contains(p.name);
                        GUILayout.Label($"　{(locked ? "★" : "　")} <b>{p.name}</b>", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        GUILayout.Label($"@{(string.IsNullOrEmpty(p.agent) ? "(未綁)" : p.agent)}", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label($"wake#{p.wakeCount}", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUILayout.Label(p.status, WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUILayout.Label(string.IsNullOrEmpty(p.forkedFrom)
                                ? "（原生）" : $"fork←{p.forkedFrom}（鏈深 {p.lineageDepth}）",
                            WrapLabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：建立新 agent（同時登記對應 bank）
        // 物理意義：agent 進 agent_banks 才算存在 — 沒登記的 agent 麾下 persona 收發不了 token。
        //          種子額度可選：>0 時對該 bank 寫第一筆 system_init credit（帳戶隱式建立，同 BankAdminPage 開戶）。
        // ===========================================================
        void DrawCreateAgentPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "CreateAgentFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>➕ 建立 Agent（含 bank）</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("建立", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.6f)), GUILayout.ExpandWidth(false)))
                        DoCreateAgent();
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("agent 名", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    string newAgent = GUILayout.TextField(m_NewAgentDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    // agent 名一改就同步建議 bank id（使用者仍可自行覆寫）
                    if (newAgent != m_NewAgentDraft)
                    {
                        m_NewAgentDraft = newAgent;
                        m_NewAgentBankDraft = newAgent;
                    }
                    GUILayout.Label("bank id", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_NewAgentBankDraft = GUILayout.TextField(m_NewAgentBankDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label("種子額度", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_NewAgentSeedDraft = GUILayout.TextField(m_NewAgentSeedDraft ?? "0", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Label("bank id 是 token 帳戶名（現有慣例：claude-code→cc / antigravity→a / Zeta→zeta）。"
                    + "種子額度 0 = 只登記不注入 token。agent 已存在時會**覆蓋** bank 映射並提示。", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：建立新 persona（可選 fork 來源）
        // 物理意義：fork = 從來源 persona 複製 identity_vector 與血統鏈（git branch 隱喻）；
        //          不 fork = 全新隨機 vector。兩條路徑都鏡像 awakening.py 的欄位，讓 morning ritual 讀得懂。
        // 數值影響：只寫一個新檔（已存在則拒絕，不覆蓋）；fork 鏈深超過 cap 只警告不阻擋（同 python 行為）。
        // ===========================================================
        void DrawCreatePersonaPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "CreatePersonaFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>🌱 建立 Persona</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("建立", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                        DoCreatePersona();
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("persona 名", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_NewPersonaDraft = GUILayout.TextField(m_NewPersonaDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label("所屬 agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (m_AgentKeys.Count > 0)
                    {
                        int idx = UCL_GUILayout.Popup(m_NewPersonaAgentIdx, m_AgentKeys, m_Dic, "NewPersonaAgent",
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                        if (idx >= 0 && idx < m_AgentKeys.Count) m_NewPersonaAgentIdx = idx;
                    }
                    else
                    {
                        GUILayout.Label("(尚無 agent — 請先建立)", WrapLabelStyle, GUILayout.ExpandWidth(false));
                    }
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("fork 來源", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    var forkOptions = ForkOptions();
                    int fidx = UCL_GUILayout.Popup(m_ForkSourceIdx, forkOptions, m_Dic, "ForkSource",
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    if (fidx >= 0 && fidx < forkOptions.Count) m_ForkSourceIdx = fidx;
                    GUILayout.Label("model", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                    m_NewPersonaModelDraft = GUILayout.TextField(m_NewPersonaModelDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("layer_role", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_NewPersonaRoleDraft = GUILayout.TextField(m_NewPersonaRoleDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                }
                GUILayout.Label($"fork 來源選「{NO_FORK_OPTION}」= 全新隨機 identity_vector；"
                    + "選既有 persona = 複製其 vector 並接上血統鏈（morning ritual 的 fork 語意）。"
                    + $"model / layer_role 留空會自動填（model 沿用來源或標 unset；layer_role 標建立來源與時間）。"
                    + $"血統鏈深度超過 {FORK_CHAIN_CAP} 只警告不阻擋，與 awakening.py 行為一致。", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：persona 換綁 agent
        // 物理意義：只改 persona 檔的 agent 欄 — vector / wake_count / 血統全部保留（換的是歸屬不是身分）。
        // 數值影響：換綁後該 persona 的 token 收付會走新 agent 的 bank；舊帳不追溯（append-only ledger 不改歷史）。
        // 邊界：persona 有 session lock（線上）時警告 + 二段確認 —— 線上換綁會讓該 session 的 bank 認知與檔案不一致。
        // ===========================================================
        void DrawRebindPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "RebindFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>🔗 Persona 換綁 Agent</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    string armLabel = IsRebindArmed(SelectedRebindPersona) ? "確認換綁（再按一次）" : "換綁";
                    var armColor = IsRebindArmed(SelectedRebindPersona)
                        ? new Color(1f, 0.5f, 0.4f) : new Color(1f, 0.85f, 0.3f);
                    if (GUILayout.Button(armLabel, UCL_GUIStyle.GetButtonStyle(armColor), GUILayout.ExpandWidth(false)))
                        DoRebindClicked();
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_Personas.Count == 0)
                {
                    GUILayout.Label("尚無 persona 可換綁。", WrapLabelStyle);
                    return;
                }

                var personaNames = m_Personas.Select(p => p.name).ToList();
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    int pidx = UCL_GUILayout.Popup(m_RebindPersonaIdx, personaNames, m_Dic, "RebindPersona",
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    if (pidx >= 0 && pidx < personaNames.Count && pidx != m_RebindPersonaIdx)
                    {
                        m_RebindPersonaIdx = pidx;
                        m_RebindArmedPersona = null;   // 換選擇即解除 arm，避免誤按確認到別人
                    }
                    var cur = SelectedRebindRow();
                    GUILayout.Label($"目前 → <b>{(cur != null && !string.IsNullOrEmpty(cur.agent) ? cur.agent : "(未綁)")}</b>",
                        WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label("換成", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    if (m_AgentKeys.Count > 0)
                    {
                        int aidx = UCL_GUILayout.Popup(m_RebindAgentIdx, m_AgentKeys, m_Dic, "RebindAgent",
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                        if (aidx >= 0 && aidx < m_AgentKeys.Count) m_RebindAgentIdx = aidx;
                    }
                    GUILayout.FlexibleSpace();
                }

                if (SelectedRebindPersona != null && m_LockedPersonas.Contains(SelectedRebindPersona))
                {
                    GUILayout.Label($"⚠ <b>{SelectedRebindPersona}</b> 目前有 session lock（線上）—— "
                        + "換綁會讓那個 session 記憶中的 bank 與檔案不一致（薪資可能記到舊 bank）。"
                        + "建議等它下線（走晚安協議）再換。", UCL_GUIStyle.GetLabelStyle(Color.yellow));
                }
                GUILayout.Label("換綁只改 agent 歸屬；identity_vector / wake_count / 血統鏈全部保留。"
                    + "既有 ledger 帳目不追溯（append-only 不改歷史），換綁後的收付才走新 bank。", WrapLabelStyle);
            }
        }

        void DrawResultPanel()
        {
            if (string.IsNullOrEmpty(m_LastResultMsg)) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>最近一次操作結果</b>", UCL_GUIStyle.LabelStyle);
                GUILayout.Label(m_LastResultMsg, WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：操作實作
        // ===========================================================
        void DoCreateAgent()
        {
            string agent = (m_NewAgentDraft ?? "").Trim();
            string bank = (m_NewAgentBankDraft ?? "").Trim();
            if (string.IsNullOrEmpty(agent) || string.IsNullOrEmpty(bank))
            { SetResult("❌ 建立 agent 失敗：agent 名 / bank id 不可為空"); return; }
            if (!int.TryParse((m_NewAgentSeedDraft ?? "0").Trim(), out int seed) || seed < 0)
            { SetResult($"❌ 建立 agent 失敗：種子額度需為非負整數（收到 '{m_NewAgentSeedDraft}'）"); return; }

            try
            {
                bool existed = m_AgentToBank.ContainsKey(agent);
                if (!File.Exists(RegistryMetaPath)) { SetResult($"❌ 找不到 {RegistryMetaPath}"); return; }
                var reg = JsonData.ParseJson(File.ReadAllText(RegistryMetaPath));
                if (!reg.Contains("agent_banks")) reg["agent_banks"] = JsonData.ParseJson("{}");
                reg["agent_banks"][agent] = bank;
                AtomicWrite(RegistryMetaPath, reg.ToJsonBeautify());

                string seedMsg = "";
                if (seed > 0)
                {
                    var e = UCL_TreasuryLedger.Credit(bank, seed, "system_init", "persona_agent_admin_create",
                        $"建立 agent 初始額度（PersonaAgentAdminPage）agent={agent}", "system", null);
                    seedMsg = $"，種子 {seed} → 餘額 {e.balance_after}";
                }

                SetResult($"✅ 建立 agent：`{agent}` → bank `{bank}`{(existed ? "（覆蓋既有映射）" : "")}{seedMsg}");
                Debug.Log($"[PersonaAgentAdmin] create agent {agent}→{bank} seed={seed}");
                NotifyTavern(
                    $"🧬 **身分後台｜建立 Agent**\n" +
                    $"agent **{agent}** → bank **{bank}**{(existed ? "（覆蓋既有映射）" : "")}{seedMsg}。\n" +
                    $"📝 說明：agent 是帳號層身分，登記進 agent_banks 後其麾下 persona 才能收發 token。",
                    "persona-agent-create-agent");
                m_NewAgentDraft = ""; m_NewAgentBankDraft = ""; m_NewAgentSeedDraft = "0";
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 建立 agent 失敗：{ex.Message}"); }
        }

        void DoCreatePersona()
        {
            string name = (m_NewPersonaDraft ?? "").Trim();
            if (string.IsNullOrEmpty(name)) { SetResult("❌ 建立 persona 失敗：名稱不可為空"); return; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { SetResult("❌ 建立 persona 失敗：名稱含不可用於檔名的字元"); return; }
            if (m_AgentKeys.Count == 0) { SetResult("❌ 建立 persona 失敗：尚無 agent，請先建立 agent"); return; }

            string agent = m_AgentKeys[Mathf.Clamp(m_NewPersonaAgentIdx, 0, m_AgentKeys.Count - 1)];
            string targetPath = Path.Combine(PersonasDir, name + ".json");
            if (File.Exists(targetPath))
            { SetResult($"❌ 建立 persona 失敗：`{name}` 已存在（不覆蓋既有人格）"); return; }

            var forkOptions = ForkOptions();
            string forkSource = (m_ForkSourceIdx > 0 && m_ForkSourceIdx < forkOptions.Count)
                ? forkOptions[m_ForkSourceIdx] : null;

            try
            {
                string now = UtcNowIso();
                var pj = JsonData.ParseJson("{}");
                List<double> vector;
                string trigger;
                var lineage = new List<string>();
                string warn = "";

                if (string.IsNullOrEmpty(forkSource))
                {
                    vector = GenVector();
                    trigger = "new_via_admin_page";
                }
                else
                {
                    string srcPath = Path.Combine(PersonasDir, forkSource + ".json");
                    if (!File.Exists(srcPath))
                    { SetResult($"❌ 建立 persona 失敗：fork 來源 `{forkSource}` 檔案不存在"); return; }
                    var src = JsonData.ParseJson(File.ReadAllText(srcPath));
                    vector = new List<double>();
                    if (src.Contains("identity_vector") && src["identity_vector"].IsArray)
                    {
                        var sv = src["identity_vector"];
                        for (int i = 0; i < sv.Count; i++) vector.Add(sv[i].GetDouble());
                    }
                    if (vector.Count == 0) vector = GenVector();   // 來源沒 vector（異常資料）→ 退回隨機，不讓新 persona 沒身分
                    if (src.Contains("fork_lineage") && src["fork_lineage"].IsArray)
                    {
                        var sl = src["fork_lineage"];
                        for (int i = 0; i < sl.Count; i++) lineage.Add(sl[i].GetString());
                    }
                    lineage.Add(forkSource);
                    trigger = "fork";
                    if (lineage.Count > FORK_CHAIN_CAP)
                        warn = $"（⚠ 血統鏈深度 {lineage.Count} > cap {FORK_CHAIN_CAP}，建議改開獨立人格）";
                }

                string model = (m_NewPersonaModelDraft ?? "").Trim();
                if (string.IsNullOrEmpty(model)) model = string.IsNullOrEmpty(forkSource) ? "unset" : SourceModel(forkSource);
                string role = (m_NewPersonaRoleDraft ?? "").Trim();
                if (string.IsNullOrEmpty(role))
                    role = string.IsNullOrEmpty(forkSource)
                        ? $"newly created via PersonaAgentAdminPage @ {now}"
                        : $"fork of {forkSource} @ {now}";

                pj["agent"] = new JsonData(agent);
                pj["model"] = new JsonData(model);
                pj["layer_role"] = new JsonData(role);
                pj["wake_count"] = new JsonData(0);
                pj["status"] = new JsonData("offline");
                pj["availability"] = new JsonData("offline");
                pj["last_active"] = JsonData.ParseJson("null");

                var vecJson = JsonData.ParseJson("[]");
                foreach (var x in vector) vecJson.Add(x);
                pj["identity_vector"] = vecJson;

                var hist = JsonData.ParseJson("[]");
                var h0 = JsonData.ParseJson("{}");
                h0["at"] = new JsonData(now);
                h0["hash"] = new JsonData(HashVector(vector));
                h0["delta_mag"] = new JsonData(0.0);
                h0["trigger"] = new JsonData(trigger);
                if (!string.IsNullOrEmpty(forkSource)) h0["source"] = new JsonData(forkSource);
                hist.Add(h0);
                pj["vector_history"] = hist;

                var lin = JsonData.ParseJson("[]");
                foreach (var s in lineage) lin.Add(s);
                pj["fork_lineage"] = lin;
                pj["forked_from"] = string.IsNullOrEmpty(forkSource) ? JsonData.ParseJson("null") : new JsonData(forkSource);
                pj["forked_at"] = string.IsNullOrEmpty(forkSource) ? JsonData.ParseJson("null") : new JsonData(now);
                pj["created_at"] = new JsonData(now);

                AtomicWrite(targetPath, pj.ToJsonBeautify());

                string lineageStr = lineage.Count > 0 ? string.Join(" → ", lineage) + " → " + name : "（原生，無血統）";
                SetResult($"✅ 建立 persona：`{name}` @ {agent}"
                    + (string.IsNullOrEmpty(forkSource) ? "（全新 vector）" : $"（fork ← {forkSource}）")
                    + $" 血統：{lineageStr}{warn}");
                Debug.Log($"[PersonaAgentAdmin] create persona {name} agent={agent} fork={forkSource ?? "(none)"}");
                NotifyTavern(
                    $"🌱 **身分後台｜建立 Persona**\n" +
                    $"persona **{name}** 誕生，歸屬 agent **{agent}**"
                    + (string.IsNullOrEmpty(forkSource) ? "，全新 identity_vector。\n" : $"，fork 自 **{forkSource}**（複製 vector 與血統）。\n")
                    + $"🧬 血統：{lineageStr}\n"
                    + $"📝 說明：persona 是人格層身分，wake_count 從 0 起算；下次有人喊早安並指定這個名字就會第一次醒來。{warn}",
                    "persona-agent-create-persona");
                m_NewPersonaDraft = ""; m_NewPersonaModelDraft = ""; m_NewPersonaRoleDraft = ""; m_ForkSourceIdx = 0;
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 建立 persona 失敗：{ex.Message}"); }
        }

        void DoRebindClicked()
        {
            string persona = SelectedRebindPersona;
            if (string.IsNullOrEmpty(persona)) { SetResult("❌ 換綁失敗：未選 persona"); return; }
            if (m_AgentKeys.Count == 0) { SetResult("❌ 換綁失敗：尚無 agent 可選"); return; }
            string newAgent = m_AgentKeys[Mathf.Clamp(m_RebindAgentIdx, 0, m_AgentKeys.Count - 1)];
            var row = SelectedRebindRow();
            if (row != null && row.agent == newAgent)
            { SetResult($"ℹ `{persona}` 已經綁在 `{newAgent}`，不需換綁"); return; }

            // 二段確認：第一次點 arm（5 秒內再按才執行）—— 換綁會影響薪資歸屬，不該一鍵誤觸
            if (!IsRebindArmed(persona))
            {
                m_RebindArmedPersona = persona;
                m_RebindArmedAt = EditorApplication.timeSinceStartup;
                SetResult($"⏳ 已待確認：`{persona}` → `{newAgent}`（5 秒內再按一次「確認換綁」生效）");
                return;
            }

            m_RebindArmedPersona = null;
            try
            {
                string path = Path.Combine(PersonasDir, persona + ".json");
                if (!File.Exists(path)) { SetResult($"❌ 換綁失敗：找不到 {persona}.json"); return; }
                var pj = JsonData.ParseJson(File.ReadAllText(path));
                string oldAgent = pj.GetString("agent", "");
                pj["agent"] = new JsonData(newAgent);
                AtomicWrite(path, pj.ToJsonBeautify());

                string lockWarn = m_LockedPersonas.Contains(persona)
                    ? "（⚠ 該 persona 目前線上，建議請它重新登入以同步 bank 認知）" : "";
                SetResult($"✅ 換綁：`{persona}` {(string.IsNullOrEmpty(oldAgent) ? "(未綁)" : oldAgent)} → `{newAgent}`{lockWarn}");
                Debug.Log($"[PersonaAgentAdmin] rebind {persona}: {oldAgent} → {newAgent}");
                NotifyTavern(
                    $"🔗 **身分後台｜Persona 換綁**\n" +
                    $"persona **{persona}** 的歸屬 agent：{(string.IsNullOrEmpty(oldAgent) ? "(未綁)" : oldAgent)} → **{newAgent}**{lockWarn}\n" +
                    $"📝 說明：只改歸屬，identity_vector / wake_count / 血統全部保留；換綁後的 token 收付走新 agent 的 bank，既有帳目不追溯。",
                    "persona-agent-rebind");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 換綁失敗：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：小工具
        // ===========================================================
        string SelectedRebindPersona =>
            (m_Personas.Count > 0 && m_RebindPersonaIdx >= 0 && m_RebindPersonaIdx < m_Personas.Count)
                ? m_Personas[m_RebindPersonaIdx].name : null;

        PersonaRow SelectedRebindRow() =>
            (m_Personas.Count > 0 && m_RebindPersonaIdx >= 0 && m_RebindPersonaIdx < m_Personas.Count)
                ? m_Personas[m_RebindPersonaIdx] : null;

        bool IsRebindArmed(string persona) =>
            !string.IsNullOrEmpty(persona) && m_RebindArmedPersona == persona
            && (EditorApplication.timeSinceStartup - m_RebindArmedAt) <= REBIND_ARM_WINDOW_SEC;

        // fork 下拉選項：第 0 項固定是「不 fork」，其餘為既有 persona
        List<string> ForkOptions()
        {
            var list = new List<string> { NO_FORK_OPTION };
            list.AddRange(m_Personas.Select(p => p.name));
            return list;
        }

        string SourceModel(string persona)
        {
            var row = m_Personas.FirstOrDefault(p => p.name == persona);
            return row != null && !string.IsNullOrEmpty(row.model) ? row.model : "unset";
        }

        // identity_vector：uniform [-1,1]^64，四位小數（鏡像 awakening.gen_vector）
        static List<double> GenVector()
        {
            var v = new List<double>(VECTOR_DIM);
            for (int i = 0; i < VECTOR_DIM; i++)
                v.Add(Math.Round(UnityEngine.Random.Range(-1f, 1f), 4));
            return v;
        }

        // vector hash：sha256("x.xxxx,x.xxxx,…") 前 8 hex（鏡像 awakening.hash_vector — 兩端算出同值才能互相驗證）
        static string HashVector(List<double> v)
        {
            string s = string.Join(",", v.Select(x => x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // 時戳格式鏡像 awakening.utcnow_iso（"...THH:mm:ss.fffZ"）
        static string UtcNowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";

        static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        void SetResult(string msg) { m_LastResultMsg = msg; }

        // 操作通知：同 BankAdminPage — 以酒保身分發酒館主頻道，C# mirror daemon 自動鏡到 Discord
        static void NotifyTavern(string body, string tag)
        {
            try
            {
                UCL_ChatTavernIO.AppendMessage("tavern", new UCL_ChatMessage
                {
                    sender_id = "tavern-keeper",
                    sender_name = "酒保",
                    sender_persona = "tavern-keeper",
                    body = body,
                    kind = "chat",
                    meta = new Dictionary<string, string>
                    {
                        { "tag", tag },
                        { "category", "meta" },
                        { "source", "PersonaAgentAdminPage" },
                    },
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaAgentAdmin] tavern 通知發送失敗（silent，不擋主操作）: {e.Message}");
            }
        }
    }
}
#endif
