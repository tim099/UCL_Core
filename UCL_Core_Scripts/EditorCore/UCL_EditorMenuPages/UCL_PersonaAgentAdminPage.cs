// 區塊職責：Persona & Agent 後台管理頁 — agent 開帳（含對應 bank）/ persona 建立（可選 fork 來源）/
//            persona 換綁 agent / persona 角色卡（PersonaCard）檢視與補建
//            （Tim 2026-07-29 拍板，參考 UCL_ChatTavernAdminPage 與 UCL_BankAdminPage）。
// 物理意義：agent 與 persona 是兩層身分 —
//          (1) agent = 帳號層（claude-code / Zeta / Altair…），對應一個 bank（token 帳戶）；
//              權威表 = AgentCommands/AwakenInit/_registry_meta.json 的 agent_banks。
//          (2) persona = 人格層（summit / crest-001…），一檔一 persona，
//              存 AgentCommands/AwakenInit/personas/<name>.json，內含 agent 欄指向所屬 agent。
//          再往上還有一層純展示資產：UCL_ChatTavernPersonaCardAsset（同 ID 對齊 persona），
//          存頭像 sprite / 顏色 / 口頭禪 / 擅長清單。persona 有檔但沒卡 = 「有名字沒臉」，
//          會讓 UCL_ChatTavernAdminPage 的頭像 Override 下拉選不到它（gura 之前正是此狀態）。
//          以前這些只能手改 JSON 或走 awakening.py CLI；本頁把四個高頻操作搬進 Editor。
// 數值影響：寫三處 —
//          _registry_meta.json 的 agent_banks（開 agent）、personas/<name>.json（建 persona / 換綁）、
//          <當前編輯模組>/UCL_Assets/UCL_ChatTavernPersonaCardAsset/<persona>.json（一鍵建卡）；
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
using UCL.Core.Page;                                // UCL_CommonEditPage / UCL_SelectAssetPage（角色卡跳轉編輯）
using UCL.Core.UI;
using UCL.Core.EditorLib.AgentCommands;             // UCL_AgentEmailRegistry（信箱解析）/ UCL_ActualAgent
using UCL.Core.EditorLib.AgentCommands.Awakening;   // UCL_PersonaData / UCL_AwakeningService（GoodMorning Cmd 遷移）
using UCL.Core.EditorLib.AgentCommands.Treasury;    // 開 agent 時的可選種子額度
using UCL.Core.EditorLib.AgentCommands.ChatTavern;  // 操作通知發酒館主頻道 + UCL_ChatTavernPersonaCardAsset
using UnityEditor;                                  // EditorApplication.timeSinceStartup（二段確認倒數）/ AssetDatabase.Refresh
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

        // 區塊職責：persona 一覽列 — typed model 本體在 UCL_PersonaData（Awakening/UCL_AwakeningData.cs），
        //          本類只補 UI 衍生值。
        // ⚠ 血證（2026-08-13 wake#47 修）：舊版自帶 camelCase 欄位（wakeCount/layerRole/forkedFrom），
        //   而 UnityJsonSerializable 的欄名匹配是 exact（僅剝 m_ 前綴）—— JSON key 是 snake_case，
        //   對不上**不報錯、靜默留預設值**，於是總覽表的 wake# 全顯示 0、fork 欄全顯示（原生）。
        //   欄位名必須與 JSON key 逐字相同；衍生值一律用 property（serializer 只走 field）。
        class PersonaRow : UCL_PersonaData
        {
            /// <summary>fork 鏈深 — 由 fork_lineage 推導（UI 顯示用）。</summary>
            public int lineageDepth => fork_lineage != null ? fork_lineage.Count : 0;
        }

        // ==== Persona 角色卡（PersonaCard）====
        // 區塊職責：persona 檔（AwakenInit/personas/*.json）↔ UCL_ChatTavernPersonaCardAsset 的對應狀態。
        // 物理意義：兩者以**同 ID** 對齊 — persona 檔是 awakening state 真相源（誰存在 / 歸屬 / 醒幾次），
        //          角色卡是同一 persona 的展示層（頭像 sprite / 顏色 / 口頭禪 / 擅長）。
        //          m_CardIds 走 UCL_ChatTavernPersonaCardAsset.Util.GetAllIDs()，與
        //          UCL_ChatTavernAdminPage 頭像 Override 下拉**同一來源** —— 這裡列得到的，那邊才選得到。
        // 數值影響：本區塊預設純讀；唯一寫入是「一鍵建立角色卡」（Save() 寫一個新 .json）。
        readonly HashSet<string> m_CardIds = new HashSet<string>(StringComparer.Ordinal);   // 已存在角色卡的 ID
        readonly List<string> m_CardPersonaOptions = new List<string>();                    // 下拉顯示（● 有卡 / ○ 無卡）
        int m_CardPersonaIdx = 0;
        // 快照 + 對應 ID：避免每幀 CreateData 重讀磁碟；讀取失敗時 id 仍記住（不無限重試）
        UCL_ChatTavernPersonaCardAsset m_CardPreview = null;
        string m_CardPreviewId = null;

        // ==== 折疊狀態與 Popup 快取分開存（workflow §5.1 血證：混用會讓 LoadData 清空折疊）====
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();      // Popup cache（LoadData 會 Clear）
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();  // 折疊狀態（永不隨資料重載清）

        // ==== 信箱設定（agent 預設表 + persona override）====
        // 區塊職責：本頁是信箱的**唯一設定入口**（Tim 2026-08-03 拍板），draft 與磁碟值分開存，
        //          按「儲存」才寫檔 —— 邊打字邊寫檔會跟 awakening.py 同時寫 persona 檔打架。
        readonly Dictionary<string, string> m_EmailDefaultDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> m_EmailOverrideDrafts = new Dictionary<string, string>(StringComparer.Ordinal);
        string m_EmailFallbackDraft = "";
        bool m_EmailLoaded = false;
        // 下拉選中的 persona + 該員的解析快取。
        // 物理意義：Resolve() 每呼叫一次就讀一次 persona 檔與 registry；OnGUI 每幀重繪，
        //          原本「逐列 Resolve 全部 persona」等於每幀掃 19 個檔。改成只算選中那一位，
        //          並在切換 / 儲存時才重算 —— 面板顯示的仍是磁碟真值，只是不再每幀去問。
        readonly UCL_ObjectDictionary m_EmailPersonaPopupDic = new UCL_ObjectDictionary();
        // agent 預設型號：下拉選 agent，改該 agent 一格。與信箱分開存（檔名各自對應內容，不混一包）。
        readonly Dictionary<string, string> m_ModelDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> m_VendorDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly UCL_ObjectDictionary m_ModelAgentPopupDic = new UCL_ObjectDictionary();
        UCL_ActualAgent m_ModelAgentSel = UCL_ActualAgent.Codex;
        bool m_ModelLoaded = false;
        string m_EmailSelectedPersona = "";
        UCL_AgentEmailResolution m_EmailSelectedResolved = null;
        int m_EmailFallbackCount = -1;

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
            // 記住重載前選中的 persona（角色卡面板）— 建完卡會 LoadData，選擇不該被彈回第一項
            string prevCardPersona = SelectedCardPersona;
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
                            var row = new PersonaRow();
                            row.name = Path.GetFileNameWithoutExtension(pf);
                            row.DeserializeFromJson(pj);
                            //{
                            //    name = Path.GetFileNameWithoutExtension(pf),
                            //    agent = pj.GetString("agent", ""),
                            //    model = pj.GetString("model", ""),
                            //    layerRole = pj.GetString("layer_role", ""),
                            //    wakeCount = pj.GetInt("wake_count", 0),
                            //    status = pj.GetString("status", "offline"),
                            //    forkedFrom = pj.GetString("forked_from", ""),
                            //    lineageDepth = (pj.Contains("fork_lineage") && pj["fork_lineage"].IsArray)
                            //        ? pj["fork_lineage"].Count : 0,
                            //};
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

                // 區塊職責：掃 PersonaCard asset ID → 標記每個 persona 有無對應角色卡
                // 物理意義：GetAllIDs() 走 UCL_ModuleService（當前編輯模組 + 依賴模組），與
                //          UCL_ChatTavernAdminPage 頭像 Override 下拉同一支來源，兩頁看到的集合一致。
                // 數值影響：純讀取；掃失敗只警告（UI 全顯示為「無卡」），不擋 persona / agent 一覽。
                m_CardIds.Clear();
                try
                {
                    foreach (var id in UCL_ChatTavernPersonaCardAsset.Util.GetAllIDs())
                    {
                        if (!string.IsNullOrEmpty(id)) m_CardIds.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PersonaAgentAdmin] PersonaCard GetAllIDs 失敗（角色卡面板將顯示為全無卡）：{ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SetResult($"❌ 載入失敗：{ex.Message}");
            }
            // 放在 try 外：載入中途拋錯時 m_Personas 可能只填了一半，選項仍必須跟它同步重建，
            // 否則下拉標籤與實際 persona 清單長度分歧（顯示 A 實際選到 B）。
            RebuildCardOptions(prevCardPersona);
        }

        // 區塊職責：重建角色卡下拉選項並還原選中項
        // 物理意義：選項字串前綴是**有無角色卡**的視覺標記（● 有 / ○ 無），值本體仍是 persona 名。
        // 數值影響：純 UI 狀態；順帶作廢 card 快照（資料重載後舊快照可能已過期）。
        void RebuildCardOptions(string iRestoreSelection)
        {
            m_CardPersonaOptions.Clear();
            foreach (var p in m_Personas)
            {
                m_CardPersonaOptions.Add($"{(m_CardIds.Contains(p.name) ? "●" : "○")} {p.name}");
            }
            m_CardPersonaIdx = 0;
            if (!string.IsNullOrEmpty(iRestoreSelection))
            {
                int idx = m_Personas.FindIndex(p => p.name == iRestoreSelection);
                if (idx >= 0) m_CardPersonaIdx = idx;
            }
            m_CardPreview = null;
            m_CardPreviewId = null;
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            DrawOverviewPanel();
            GUILayout.Space(8);
            DrawEmailPanel();
            GUILayout.Space(8);
            DrawModelPanel();
            GUILayout.Space(8);
            DrawPersonaCardPanel();
            GUILayout.Space(8);
            DrawCreateAgentPanel();
            GUILayout.Space(8);
            DrawCreatePersonaPanel();
            GUILayout.Space(8);
            DrawRebindPanel();
            GUILayout.Space(8);
            DrawMaintenancePanel();
            GUILayout.Space(8);
            DrawAwakeningTestPanel();
            GUILayout.Space(8);
            DrawResultPanel();
        }

        // ===========================================================
        // 區塊：🗄 維護 — 收尾信版面 migration（頂層舊格式 → wakes/<序號>_<ts>.md）
        // 區塊職責：把「早安時才會自動跑」的那件事，做成後台可以對全體觸發的入口。
        // 物理意義：遷移**本來就是自動的**，但只對「正在醒來的那一位」跑
        //          （awakening.py cmd_morning 內 letters_migration_pending → migrate_letters_to_wakes）。
        //          於是很久沒上線的 persona（apex-two 實例）會一直停在舊格式 ——
        //          它不是壞掉，是那條路徑只有醒來才會經過。本區塊補的是那個缺口。
        // 數值影響：試跑完全唯讀。執行會**複製**頂層收尾信進 wakes/（原檔保留不動，
        //          `shutil.copy2`），並把 registry.wake_count 改成 wakes/ 的信件數。
        // 實作分工（Tim 2026-08-11 拍板走 A 案）：**判斷與改檔全在 awakening.py**，
        //          本區塊只負責畫按鈕、跑 process、把 stdout 貼回來，**不含任何遷移邏輯**。
        //          理由：那支的規則有連號、wake_count 推導、見林書籤 rebase、兩段式改號四層，
        //          C# 重寫一份就是第二個實作 —— 而它們漂移時錯的是「誰幾歲」，沒有人會當場發現。
        // ⚠ 在線者一律不動：進行中的 wake 還沒寫收尾信，磁碟信件數天生比 registry 少 1，
        //   此時改寫 wake_count 會把人當場減一歲（實測 summit：待複製 0 封，仍 43 → 42）。
        //   這道守衛在 **awakening.py 那一端**，不在這裡 —— 擋線跟被擋的邏輯放同一處，
        //   才不會出現「CLI 擋、後台沒擋」這種只有走某條路才踩得到的洞。
        // ===========================================================
        const string PROC_TAG_AWAKENING = "persona_admin_awakening";
        const int MIGRATE_TIMEOUT_MS = 5 * 60 * 1000;

        string m_MigrateReport = "";
        Vector2 m_MigrateScroll;
        bool m_MigrateRunning;

        void DrawMaintenancePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "MaintenanceFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🗄 維護 — 收尾信版面 migration（舊格式 → wakes/）</b>", WrapLabelStyle);
                }
                if (!aShow) return;

                GUILayout.Label(
                    "把頂層的 <b>&lt;ts&gt;.md</b> 收尾信複製成 <b>wakes/&lt;6位序號&gt;_&lt;ts&gt;.md</b>，"
                    + "序號＝第幾次 wake。<b>原檔保留不動</b>（複製不是搬移）。\n"
                    + "遷移**本來就會在早安時自動跑**，但只對正在醒來的那一位 —— "
                    + "很久沒上線的 persona 會一直停在舊格式。本欄是補那個缺口。\n"
                    + "<b>範圍是 registry 裡的 persona</b>，不是磁碟上所有 letters 目錄"
                    + "（兩者目前不一致，試跑報表以 awakening.py 的輸出為準）。",
                    WrapLabelStyle);
                GUILayout.Label(
                    "✅ <b>已經是新格式的 persona 一律不動</b>（wakes/ 內已經有信者）——\n"
                    + "　 本欄的守備範圍是「<b>還沒開始遷移的人</b>」。已遷移者若頂層還有零星沒收進去的信，"
                    + "補收會把它們插在中間 → 後面全部重編號 → 信件內文自稱的編號、見林 digest 檔名、"
                    + "見林書籤三者同時對不上。\n"
                    + "　 更關鍵：那些零星的信<b>只存在於某些 checkout</b>（同一個 repo，"
                    + "不同專案的工作樹內容會分岔）—— 所以「該不該補收」取決於你站在哪一份，"
                    + "而<b>那種決定不該由批次替人做</b>。交給本人下次醒來時在她自己的工作樹上判。",
                    WrapLabelStyle);
                GUILayout.Label(
                    "🔒 <b>在線的 persona 一律不動</b> —— 進行中的 wake 還沒寫收尾信，"
                    + "磁碟信件數天生比 registry 少 1；此時同步 wake_count 會把人當場減一歲，"
                    + "而且對「沒有任何檔案要遷移」的人照樣發生。試跑報表會逐筆標出被鎖定的人。",
                    WrapLabelStyle);

                using (new EditorGUI.DisabledScope(m_MigrateRunning))
                using (new GUILayout.HorizontalScope())
                {
                    // 試跑永遠可按（唯讀）—— 先看清單再決定，慣例同 ChatTavernAdmin / SubmoduleSync
                    if (GUILayout.Button("試跑（唯讀，只列計畫）",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        RunMigrateLetters(false);
                    }
                    if (GUILayout.Button("執行 migration（會寫檔）",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.35f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        UCL_OptionPage.Create("確認執行收尾信 migration？",
                            "會對 registry 裡**未在線**的 persona：\n"
                            + "　· 把頂層收尾信<b>複製</b>進 wakes/（原檔保留不動）\n"
                            + "　· 把 registry.wake_count 改成 wakes/ 的信件數\n"
                            + "　· 把見林書籤（last_consolidated_wake）換算到新編號\n\n"
                            + "<b>在線的 persona 一律跳過。</b>\n"
                            + "wake_count 的改寫**不限於有檔案要搬的人** —— "
                            + "請先按「試跑」看清楚哪些人的歲數會被動到。",
                            new ButtonData("執行", () => RunMigrateLetters(true),
                                UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                            new ButtonData("取消"));
                    }
                    if (m_MigrateRunning) GUILayout.Label("⏳ 執行中…", WrapLabelStyle);
                }

                if (!string.IsNullOrEmpty(m_MigrateReport))
                {
                    using (var sv = new GUILayout.ScrollViewScope(m_MigrateScroll,
                               GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(200))))
                    {
                        m_MigrateScroll = sv.scrollPosition;
                        EditorGUILayout.TextArea(m_MigrateReport, UCL_GUIStyle.LabelStyle);
                    }
                }
            }
        }

        // 區塊職責：跑 awakening.py migrate-letters 並把輸出貼回報告區
        // 物理意義：Process 走 UCL_ProcessCli（內含 ProcessRegistry 登記 / 雙 stream 非阻塞讀 /
        //          逾時 kill）—— 硬規則：C# 開的每顆外部 Process 都要登記，不自己刻 Process.Start。
        //          背景執行緒跑（WaitForExit 會擋 UI），回主執行緒才寫 m_MigrateReport。
        // 數值影響：apply=false 唯讀；apply=true 由 awakening.py 決定寫什麼。
        void RunMigrateLetters(bool apply)
        {
            if (m_MigrateRunning) return;
            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel))
            {
                m_MigrateReport = "✗ 解析不到 UCL_Core 路徑（UCL_EditorPath.CorePath 為空）";
                return;
            }
            // 不寫死 install path（見 ucl-core-paths）—— 各專案掛載位置不同，寫死的跨專案必壞，
            // 而且通常是靜默壞（File.Exists 失敗後 fail-soft，連 warning 都沒有）。
            string script = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, corePathRel, "Tools~/AgentCommands/awakening.py"));
            if (!File.Exists(script))
            {
                m_MigrateReport = $"✗ 找不到 awakening.py：{script}";
                return;
            }
            m_MigrateRunning = true;
            m_MigrateReport = apply ? "⏳ 執行 migration…" : "⏳ 試跑…";
            string repoRoot = UCL_RepoPath.RepoRoot;
            System.Threading.Tasks.Task.Run(() =>
            {
                string text;
                try
                {
                    // 路徑含空白時參數會被切斷 —— 引號是呼叫端的責任（UCL_ProcessCli 明寫）
                    string args = $"\"{script}\" migrate-letters --all" + (apply ? " --apply" : "");
                    var (exit, so, se) = UCL_ProcessCli.Run("python", args, repoRoot,
                        PROC_TAG_AWAKENING, nameof(UCL_PersonaAgentAdminPage), MIGRATE_TIMEOUT_MS);
                    // stderr 不丟掉：awakening.py 把「跳過幾封」「wake_count 異常」印在 stderr，
                    // 只收 stdout 會讓報告看起來乾淨而其實漏了警告（那正是這頁在治的病）。
                    text = so;
                    if (!string.IsNullOrEmpty(se)) text += "\n\n── stderr ──\n" + se;
                    if (exit != 0) text = $"✗ awakening.py 結束碼 {exit}\n\n" + text;
                }
                catch (Exception e)
                {
                    text = "🚨 例外：" + e;
                }
                EditorApplication.delayCall += () =>
                {
                    m_MigrateRunning = false;
                    m_MigrateReport = text;
                    if (apply) LoadData();   // 歲數變了，總覽表要重讀 —— 報告說改了不算數
                };
            });
        }

        // ===========================================================
        // 區塊：🌅 Awakening 測試 — GoodMorning Cmd 遷移的 QA 入口（Plan_Awakening_Flow_Simplification §8.8 R14/R19）
        // 區塊職責：用 Template 測試殼實測 UCL_AwakeningService 的每一半套 —— 對帳（唯讀掃描）與
        //          brief 生成（經與 Cmd step=brief 同一條觸發鏈 spawn python），result 貼回本頁供 QA 欄位/格式。
        // 物理意義：邏輯全在 UCL_AwakeningService（static，與 Cmd_GoodMorning 共用零複製）；
        //          本區塊只畫按鈕、跑背景 Task、貼報告 —— 同 DrawMaintenancePanel 的分工原則。
        // 數值影響：對帳純唯讀；brief 生成寫檔者是 Python 端（awakening.py brief，只重生成機械產物）。
        //          測試殼廣播/錢類規矩見 letters/Template/README.md —— brief 不廣播、不動錢，安全。
        // ===========================================================
        const string AWAKEN_TEST_PERSONA = "Template";
        const int BRIEF_TIMEOUT_MS = 2 * 60 * 1000;

        string m_AwakenTestReport = "";
        Vector2 m_AwakenTestScroll;
        bool m_AwakenTestRunning;

        void DrawAwakeningTestPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "AwakenTestFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🌅 Awakening 測試（GoodMorning Cmd 遷移 QA）</b>", WrapLabelStyle);
                }
                if (!aShow) return;

                GUILayout.Label(
                    "邏輯層 = UCL_AwakeningService（與 Cmd_GoodMorning 共用）。"
                    + $"brief 測試固定跑測試殼 `{AWAKEN_TEST_PERSONA}`（規矩見 letters/{AWAKEN_TEST_PERSONA}/README.md）。",
                    WrapLabelStyle);

                using (new GUILayout.HorizontalScope())
                {
                    using (new UnityEditor.EditorGUI.DisabledScope(m_AwakenTestRunning))
                    {
                        if (GUILayout.Button("🧪 對帳（全 persona，唯讀）", UCL_GUIStyle.ButtonStyle))
                        {
                            // 純檔案掃描（數十檔），主執行緒直接跑；出錯貼報告不炸頁
                            try { m_AwakenTestReport = UCL_AwakeningService.AuditReport(); }
                            catch (Exception e) { m_AwakenTestReport = "🚨 對帳例外：" + e; }
                        }
                        if (GUILayout.Button($"📄 生成 brief（{AWAKEN_TEST_PERSONA}）", UCL_GUIStyle.ButtonStyle))
                        {
                            RunAwakenBriefTest();
                        }
                    }
                    if (m_AwakenTestRunning) GUILayout.Label("⏳ 執行中…", WrapLabelStyle);
                }

                if (!string.IsNullOrEmpty(m_AwakenTestReport))
                {
                    using (var sv = new GUILayout.ScrollViewScope(m_AwakenTestScroll,
                               GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(240))))
                    {
                        m_AwakenTestScroll = sv.scrollPosition;
                        EditorGUILayout.TextArea(m_AwakenTestReport, UCL_GUIStyle.LabelStyle);
                    }
                }
            }
        }

        // 區塊職責：brief 生成測試 — 背景執行緒 spawn python（WaitForExit 會擋 UI），回主執行緒貼報告。
        // 物理意義：走 UCL_AwakeningService.RunBrief —— 與 Cmd_GoodMorning step=brief **同一條觸發鏈**（R20），
        //          此處測通 = Cmd 那條也通（同一份實作，差別只在入口）。
        void RunAwakenBriefTest()
        {
            if (m_AwakenTestRunning) return;
            m_AwakenTestRunning = true;
            m_AwakenTestReport = "⏳ 生成 brief…";
            System.Threading.Tasks.Task.Run(() =>
            {
                string aText;
                try
                {
                    var aResult = UCL_AwakeningService.RunBrief(
                        AWAKEN_TEST_PERSONA, nameof(UCL_PersonaAgentAdminPage), BRIEF_TIMEOUT_MS);
                    var aSb = new StringBuilder();
                    aSb.AppendLine(aResult.ok ? "✅ brief 生成（判定依據＝落地檔存在且行數 > 0，非 stdout）" : "✗ brief 生成失敗");
                    aSb.AppendLine(aResult.report);
                    if (aResult.ok)
                    {
                        aSb.AppendLine("── brief 摘要（frontmatter 全文＋段落標題，QA 欄位/格式用）──");
                        aSb.AppendLine(UCL_AwakeningService.SummarizeBrief(aResult.briefPath));
                    }
                    aText = aSb.ToString();
                }
                catch (Exception e)
                {
                    aText = "🚨 例外：" + e;
                }
                EditorApplication.delayCall += () =>
                {
                    m_AwakenTestRunning = false;
                    m_AwakenTestReport = aText;
                };
            });
        }

        // ===========================================================
        // 區塊：型號 — agent 預設型號 + 「model 欄填成 agent 名」的自動翻譯
        // 物理意義：提示使用者「該填什麼型號」實測會**讓人填錯**（apex-one 的 system prompt 第一句是
        //          "You are Antigravity" 所以填了 Antigravity；kaguya 填了 Codex —— 兩人都誠實作答）。
        //          所以不再靠提示，改成底層辨識：model 欄是 agent 名就翻成這裡設的預設型號。
        // 數值影響：Claude / Gemini 這種單獨廠牌名**當型號不翻** —— 它們也可能是某人誠實給的模糊答案，
        //          翻掉等於擦掉資訊。只翻明確的 agent 名（Codex / ClaudeCode / Antigravity / 別名）。
        // ===========================================================
        void DrawModelPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "ModelFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🏷 型號設定</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                if (!m_ModelLoaded)
                {
                    m_ModelDrafts.Clear();
                    foreach (var aKv in UCL_AgentModelRegistry.LoadModels()) m_ModelDrafts[aKv.Key] = aKv.Value;
                    m_VendorDrafts.Clear();
                    foreach (var aKv in UCL_AgentModelRegistry.LoadVendors()) m_VendorDrafts[aKv.Key] = aKv.Value;
                    m_ModelLoaded = true;
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    m_ModelAgentSel = UCL_GUILayout.PopupAuto(m_ModelAgentSel, m_ModelAgentPopupDic, "ModelAgent", 6,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    GUILayout.FlexibleSpace();
                }
                if (m_ModelAgentSel == UCL_ActualAgent.None)
                {
                    GUILayout.Label("（選一個 agent）", WrapLabelStyle);
                    return;
                }
                string aKey = m_ModelAgentSel.ToString();
                if (!m_ModelDrafts.ContainsKey(aKey)) m_ModelDrafts[aKey] = "";
                if (!m_VendorDrafts.ContainsKey(aKey)) m_VendorDrafts[aKey] = "";
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("廠牌 vendor", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_VendorDrafts[aKey] = GUILayout.TextField(m_VendorDrafts[aKey] ?? "");
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("預設型號", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_ModelDrafts[aKey] = GUILayout.TextField(m_ModelDrafts[aKey] ?? "");
                    if (GUILayout.Button("💾 儲存", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.Width(UCL_GUIStyle.GetScaledSize(72))))
                    {
                        // 兩張表一起寫 —— 只寫一張會把另一張洗掉，而且不會報錯。
                        if (UCL_AgentModelRegistry.SaveAll(m_ModelDrafts, m_VendorDrafts, out string aErr))
                            SetResult($"✓ {aKey} 廠牌／預設型號已存 → {UCL_AgentModelRegistry.RegistryPath}");
                        else
                            SetResult($"❌ 儲存失敗：{aErr}");
                    }
                }
                GUILayout.Label("vendor 是 trailer 必印的身分（由 actual_agent 推導）；預設型號只在 model 欄被填成 agent 名時拿來翻譯。",
                    WrapLabelStyle);
                GUILayout.Label($"檔案：{UCL_AgentModelRegistry.RegistryPath}", WrapLabelStyle);

                // 攤開「誰會被這格影響」—— 只看設定值看不出效果，看得到受影響的人才知道改了什麼。
                GUILayout.Space(4);
                GUILayout.Label("<b>受本表翻譯影響的 persona</b>（model 欄填成 agent 名者）", WrapLabelStyle);
                int aHit = 0;
                foreach (var aRow in m_Personas)
                {
                    var aRes = UCL_AgentModelRegistry.Resolve(aRow.name);
                    if (aRes.Source != "agent-translated" && aRes.Source != "agent-unmapped") continue;
                    aHit++;
                    string aNote = aRes.Source == "agent-translated"
                        ? $"<b>{aRes.Model}</b>"
                        : $"<color=#ffcc66>{aRes.Model}（{aRes.AgentKey} 尚未設預設型號，保留原值）</color>";
                    GUILayout.Label($"  {aRow.name}：填了「{aRes.Raw}」→ {aNote}　trailer 會印 <b>({UCL_AgentModelRegistry.FormatTrailerModel(aRow.name)})</b>",
                        WrapLabelStyle);
                }
                if (aHit == 0)
                    GUILayout.Label("  （目前沒有人把 agent 名填進 model 欄）", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：信箱 — agent 預設表（key = actual_agent）+ persona override
        // 物理意義：預設表 key 用 actual_agent 而非顯示 agent —— 前者是封閉集合（三個值），後者每多一位
        //          同事就多一格要填。但**沒有 actual_agent 的 persona 吃不到預設**（實測 19 位裡有 17 位
        //          缺這欄），那些人必須逐一填 override；面板把他們標出來，不讓它靜默 fallback。
        // 數值影響：override 空字串＝清除（回頭吃預設），不是寫入空信箱；按儲存才落檔。
        // ===========================================================
        void DrawEmailPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "EmailFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>📧 信箱設定</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (m_EmailFallbackCount > 0)
                        GUILayout.Label($"<color=#ffcc66>⚠ {m_EmailFallbackCount} 位吃 fallback／未設定</color>",
                            UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                if (!m_EmailLoaded) LoadEmailDrafts();

                GUILayout.Label("<b>Agent 預設（key = actual_agent，封閉集合）</b>", WrapLabelStyle);
                var aKeys = new List<string>(m_EmailDefaultDrafts.Keys);
                aKeys.Sort(StringComparer.Ordinal);
                foreach (var aKey in aKeys)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(aKey, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        m_EmailDefaultDrafts[aKey] = GUILayout.TextField(m_EmailDefaultDrafts[aKey] ?? "");
                        DrawEmailMark(m_EmailDefaultDrafts[aKey]);
                    }
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("fallback", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    m_EmailFallbackDraft = GUILayout.TextField(m_EmailFallbackDraft ?? "");
                    DrawEmailMark(m_EmailFallbackDraft);
                }
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("💾 儲存 Agent 預設", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false)))
                        SaveEmailDefaults();
                    if (GUILayout.Button("↻ 捨棄未存編輯", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_EmailLoaded = false;
                        SetResult("↻ 已重讀磁碟值（未儲存的編輯已捨棄）");
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Label($"檔案：{UCL_AgentEmailRegistry.RegistryPath}", WrapLabelStyle);

                GUILayout.Space(6);
                GUILayout.Label("<b>Persona override（空白＝沿用 agent 預設）</b>", WrapLabelStyle);
                if (m_Personas.Count == 0)
                {
                    GUILayout.Label("（沒有 persona 檔可設定）", WrapLabelStyle);
                    return;
                }

                var aOptions = new List<string>();
                foreach (var aRow in m_Personas)
                {
                    // 下拉標籤直接標出「這位有沒有自己的信箱」—— 選之前就看得到，不必逐個點開。
                    string aOwn = m_EmailOverrideDrafts.TryGetValue(aRow.name, out var aDraft)
                        ? aDraft : UCL_AgentEmailRegistry.LoadPersonaOverride(aRow.name);
                    if (!m_EmailOverrideDrafts.ContainsKey(aRow.name)) m_EmailOverrideDrafts[aRow.name] = aOwn;
                    aOptions.Add((string.IsNullOrWhiteSpace(aOwn) ? "○ " : "● ") + aRow.name);
                }
                int aCur = m_Personas.FindIndex(r => r.name == m_EmailSelectedPersona);
                if (aCur < 0) aCur = 0;
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    int aNext = UCL_GUILayout.PopupAuto(aCur, aOptions, m_EmailPersonaPopupDic, "EmailPersona", 10,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(260)));
                    if (aNext < 0 || aNext >= m_Personas.Count) aNext = aCur;
                    if (aNext != aCur || m_EmailSelectedResolved == null
                        || m_EmailSelectedPersona != m_Personas[aNext].name)
                    {
                        m_EmailSelectedPersona = m_Personas[aNext].name;
                        RefreshEmailSelection();
                    }
                    GUILayout.FlexibleSpace();
                }

                string aPersona = m_EmailSelectedPersona;
                var aRes = m_EmailSelectedResolved;
                if (string.IsNullOrEmpty(aPersona) || aRes == null) return;
                if (!m_EmailOverrideDrafts.ContainsKey(aPersona))
                    m_EmailOverrideDrafts[aPersona] = UCL_AgentEmailRegistry.LoadPersonaOverride(aPersona);

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("actual_agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    GUILayout.Label(string.IsNullOrEmpty(aRes.ActualAgent)
                        ? "<color=#ff9966>(無 — 這位吃不到 agent 預設，只能靠 override 或 fallback)</color>"
                        : aRes.ActualAgent, WrapLabelStyle);
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("override", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_EmailOverrideDrafts[aPersona] = GUILayout.TextField(m_EmailOverrideDrafts[aPersona] ?? "");
                    DrawEmailMark(m_EmailOverrideDrafts[aPersona]);
                    if (GUILayout.Button("💾 儲存", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.Width(UCL_GUIStyle.GetScaledSize(72))))
                    {
                        SavePersonaEmail(aPersona, m_EmailOverrideDrafts[aPersona]);
                        RefreshEmailSelection();
                    }
                    if (GUILayout.Button("清除", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(56))))
                    {
                        m_EmailOverrideDrafts[aPersona] = "";
                        SavePersonaEmail(aPersona, "");
                        RefreshEmailSelection();
                    }
                }
                // 輸入框空白不代表「沒有信箱」—— 它可能正吃著 agent 預設或 fallback。
                // 把 resolve 結果與來源攤開，才不會有人以為空白就是沒設定。
                string aSrc = aRes.Source == "persona-override" ? "persona 自訂"
                    : aRes.Source == "agent-default" ? $"{aRes.ActualAgent} 預設"
                    : aRes.Source == "fallback" ? "全域 fallback" : "<color=#ff6666>未設定</color>";
                GUILayout.Label($"→ 生效：<b>{aRes.Email}</b>（{aSrc}）", WrapLabelStyle);
            }
        }

        // 空白一律放行（空白＝沿用上層），只標「有填但不像位址」。
        void DrawEmailMark(string iValue)
        {
            bool aOk = string.IsNullOrWhiteSpace(iValue) || UCL_AgentEmailRegistry.LooksLikeEmail(iValue);
            GUILayout.Label(aOk ? "" : "<color=#ff6666>格式?</color>",
                UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(48)));
        }

        void LoadEmailDrafts()
        {
            m_EmailDefaultDrafts.Clear();
            foreach (var aKv in UCL_AgentEmailRegistry.LoadDefaults()) m_EmailDefaultDrafts[aKv.Key] = aKv.Value;
            m_EmailFallbackDraft = UCL_AgentEmailRegistry.LoadFallback();
            m_EmailOverrideDrafts.Clear();
            m_EmailSelectedResolved = null;
            m_EmailLoaded = true;
            RecountEmailFallback();
        }

        /// <summary>重算選中 persona 的生效值（切換 / 儲存後呼叫）—— 不在 OnGUI 每幀做。</summary>
        void RefreshEmailSelection()
        {
            m_EmailSelectedResolved = string.IsNullOrEmpty(m_EmailSelectedPersona)
                ? null : UCL_AgentEmailRegistry.Resolve(m_EmailSelectedPersona);
            if (!string.IsNullOrEmpty(m_EmailSelectedPersona))
                m_EmailOverrideDrafts[m_EmailSelectedPersona] =
                    UCL_AgentEmailRegistry.LoadPersonaOverride(m_EmailSelectedPersona);
            RecountEmailFallback();
        }

        /// <summary>標題列那個「幾位吃 fallback」的數字 —— 全掃一次很貴，只在載入與儲存後算。</summary>
        void RecountEmailFallback()
        {
            int aCount = 0;
            foreach (var aRow in m_Personas)
                if (UCL_AgentEmailRegistry.Resolve(aRow.name).IsFallback) aCount++;
            m_EmailFallbackCount = aCount;
        }

        // 任何一格格式不對就整批不存 —— 部分寫入會留下「一半新一半舊」的狀態，比不存難查。
        void SaveEmailDefaults()
        {
            foreach (var aKv in m_EmailDefaultDrafts)
            {
                if (!string.IsNullOrWhiteSpace(aKv.Value) && !UCL_AgentEmailRegistry.LooksLikeEmail(aKv.Value))
                {
                    SetResult($"❌ {aKv.Key} 的值不像 email：{aKv.Value}（未儲存任何變更）");
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(m_EmailFallbackDraft) && !UCL_AgentEmailRegistry.LooksLikeEmail(m_EmailFallbackDraft))
            {
                SetResult($"❌ fallback 的值不像 email：{m_EmailFallbackDraft}（未儲存任何變更）");
                return;
            }
            if (UCL_AgentEmailRegistry.SaveDefaults(m_EmailDefaultDrafts, m_EmailFallbackDraft, out string aErr))
            {
                RefreshEmailSelection();   // 預設一改，很多人的生效值跟著變，顯示要跟上
                SetResult($"✓ agent 預設已存 → {UCL_AgentEmailRegistry.RegistryPath}");
            }
            else
                SetResult($"❌ 儲存失敗：{aErr}");
        }

        void SavePersonaEmail(string iPersona, string iEmail)
        {
            string aTrimmed = (iEmail ?? "").Trim();
            if (!string.IsNullOrEmpty(aTrimmed) && !UCL_AgentEmailRegistry.LooksLikeEmail(aTrimmed))
            {
                SetResult($"❌ {iPersona} 的值不像 email：{aTrimmed}（未儲存）");
                return;
            }
            if (UCL_AgentEmailRegistry.SavePersonaOverride(iPersona, aTrimmed, out string aErr))
                SetResult(string.IsNullOrEmpty(aTrimmed)
                    ? $"✓ {iPersona} 的 override 已清除（回頭吃 agent 預設）"
                    : $"✓ {iPersona} → {aTrimmed}");
            else
                SetResult($"❌ {iPersona} 儲存失敗：{aErr}");
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
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "OverviewFold", 21);
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
                foreach (var p in m_Personas.OrderByDescending(x => x.wake_count))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool locked = m_LockedPersonas.Contains(p.name);
                        GUILayout.Label($"　{(locked ? "★" : "　")} <b>{p.name}</b>", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        GUILayout.Label($"@{(string.IsNullOrEmpty(p.agent) ? "(未綁)" : p.agent)}", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label($"wake#{p.wake_count}", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUILayout.Label(p.status, WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUILayout.Label(string.IsNullOrEmpty(p.forked_from)
                                ? "（原生）" : $"fork←{p.forked_from}（鏈深 {p.lineageDepth}）",
                            WrapLabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：Persona 角色卡（PersonaCard）— 檢視對應狀態 / 缺卡一鍵補建 / 有卡跳轉編輯
        // 物理意義：下拉列出 personas/ 目錄全體（真相源），前綴 ● / ○ 直接標示有無同 ID 的
        //          UCL_ChatTavernPersonaCardAsset。缺卡 → 一鍵建（預填歸屬 agent / layer_role / tag）；
        //          有卡 → 顯示基本資訊並可跳 UCL_CommonEditPage 細編。
        // 數值影響：讀取不寫；「一鍵建立」寫一個新 asset .json（已存在一律不覆寫，避免蓋掉手工調過的卡）。
        // 設計取捨：不自動批次補全所有缺卡 —— 建卡等於宣告「這個 persona 要有臉」，該由人逐個決定；
        //          批次需求走既有 Cmd_SeedTavernIdentityAssets 的同類做法，不在管理頁塞隱式大動作。
        // ===========================================================
        void DrawPersonaCardPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                int missing = m_Personas.Count(p => !m_CardIds.Contains(p.name));
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "PersonaCardFold", 21);
                    GUILayout.Label("<b>🎭 Persona 角色卡</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(missing > 0
                            ? $"卡 {m_CardIds.Count} 張 / persona {m_Personas.Count} 個　<color=yellow>尚缺 {missing}</color>"
                            : $"卡 {m_CardIds.Count} 張 / persona {m_Personas.Count} 個　✓ 全員有卡",
                        WrapLabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("📋 角色卡清單頁", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        DoOpenCardSelectPage();
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_Personas.Count == 0)
                {
                    GUILayout.Label("尚無 persona —— 請先在下方「建立 Persona」開一個。", WrapLabelStyle);
                    return;
                }

                // persona 下拉（● 有卡 / ○ 無卡）+ 該 persona 的歸屬與 wake 次數（一眼確認選對人）
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    int idx = UCL_GUILayout.PopupSearchCache(m_CardPersonaIdx, m_CardPersonaOptions, m_Dic, "CardPersonaPicker",
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                    if (idx != m_CardPersonaIdx && idx >= 0 && idx < m_CardPersonaOptions.Count)
                    {
                        m_CardPersonaIdx = idx;
                        m_CardPreview = null;      // 換選擇 → 快照失效，下次繪製重讀
                        m_CardPreviewId = null;
                    }
                    var row = SelectedCardRow();
                    if (row != null)
                    {
                        GUILayout.Label($"@{(string.IsNullOrEmpty(row.agent) ? "(未綁)" : row.agent)}", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label($"wake#{row.wake_count}", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUILayout.Label(m_LockedPersonas.Contains(row.name) ? "★ 線上" : "　", WrapLabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    }
                    GUILayout.FlexibleSpace();
                }

                string persona = SelectedCardPersona;
                if (string.IsNullOrEmpty(persona)) return;

                if (!m_CardIds.Contains(persona)) DrawCardMissingBlock(persona);
                else DrawCardExistBlock(persona);

                GUILayout.Space(4);
                if (missing > 0)
                {
                    GUILayout.Label($"○ 尚缺角色卡：{string.Join(" / ", m_Personas.Where(p => !m_CardIds.Contains(p.name)).Select(p => p.name))}",
                        WrapLabelStyle);
                }
                // 孤兒卡 = 有角色卡但 personas/ 沒同名檔（persona 改名殘留、或純展示用的非 awakening 角色）
                var orphans = m_CardIds.Where(id => !m_Personas.Any(p => p.name == id))
                                       .OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (orphans.Count > 0)
                {
                    GUILayout.Label($"🃏 孤兒卡（有卡但 personas/ 無同名檔）：{string.Join(" / ", orphans)}"
                        + "　—— 改名殘留或非 awakening 角色，不影響喚醒流程。", WrapLabelStyle);
                }
                GUILayout.Label($"角色卡寫入當前編輯模組 <b>[{UCL_ModuleService.CurEditModuleID}]</b> 的 "
                    + "UCL_Assets/UCL_ChatTavernPersonaCardAsset/。有卡之後 UCL_ChatTavernAdminPage 的"
                    + "「Persona 頭像 Override」下拉才選得到這個 persona。", WrapLabelStyle);
            }
        }

        // 區塊職責：選中 persona 沒有角色卡時的區塊 — 說明後果 + 一鍵建立 + 預填內容公告
        // 物理意義：缺卡的實際症狀是「酒館頭像 Override 選不到它」+ Discord 頭像只能退 agent 層 fallback。
        // 數值影響：按下按鈕才寫檔；本區塊本身純顯示。
        void DrawCardMissingBlock(string iPersona)
        {
            var row = SelectedCardRow();
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"⚠ <b>{iPersona}</b> 尚無角色卡（頭像 Override 下拉選不到它）",
                    UCL_GUIStyle.GetLabelStyle(Color.yellow), GUILayout.ExpandWidth(false));
                if (GUILayout.Button("✨ 一鍵建立角色卡", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 1f)),
                        GUILayout.ExpandWidth(false)))
                    DoCreateCard(iPersona);
                GUILayout.FlexibleSpace();
            }
            GUILayout.Label($"建立後預填：歸屬 agent = <b>{(row != null && !string.IsNullOrEmpty(row.agent) ? row.agent : "(未綁)")}</b>、"
                + $"角色設定 = persona 檔的 layer_role（{Ellipsis(row?.layer_role, 40)}）、tag = 該 agent 名。"
                + "頭像 sprite / 顏色 / 口頭禪 / 擅長清單一律留空，之後用「編輯角色卡」慢慢填。", WrapLabelStyle);
        }

        // 區塊職責：選中 persona 已有角色卡時的區塊 — 基本資訊摘要 + 跳轉編輯頁
        // 物理意義：只讀展示層欄位；歸屬 agent 與 persona 檔不一致時明確警示
        //          （persona 檔的 agent 才是權威 —— 換綁只改 persona 檔，卡上的 OwnerAgentId 會留舊值）。
        // 數值影響：不寫檔；「編輯角色卡」開的是 clone 編輯頁，要在那頁按存檔才落地。
        void DrawCardExistBlock(string iPersona)
        {
            EnsureCardPreview(iPersona);
            var card = m_CardPreview;
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"✅ <b>{iPersona}</b> 已有角色卡", WrapLabelStyle, GUILayout.ExpandWidth(false));
                if (GUILayout.Button("✏ 編輯角色卡", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.3f)),
                        GUILayout.ExpandWidth(false)))
                    DoOpenCardEdit(iPersona);
                if (GUILayout.Button("🔄 重讀", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_CardPreviewId = null;
                    m_CardPreview = null;
                }
                GUILayout.FlexibleSpace();
            }
            if (card == null)
            {
                GUILayout.Label($"⚠ 角色卡 `{iPersona}` 存在但讀取失敗（詳見 Console）— 檔案可能格式壞了。",
                    UCL_GUIStyle.GetLabelStyle(Color.yellow));
                return;
            }

            var row = SelectedCardRow();
            using (new GUILayout.HorizontalScope())
            {
                // 區塊職責：歸屬 agent 一律 **derive 自 persona 檔**，不顯示卡上存的值
                // 物理意義：「這個 persona 歸誰」是歸屬事實，事實源只有 personas/<name>.json 的 agent 欄
                //          （換綁只改那裡）。展示層不該有自己版本的「你歸誰」。
                // 數值影響：卡上的 m_OwnerAgentId 不再參與顯示 → 兩處不一致在物理上無從發生，
                //          原本的黃字警示因此整條移除。警示是把一致性外包給人類注意力，
                //          而同一天的 wait-reply 事件已證明「有警示 ≠ 會有人修」（見 glossary 同碼失聲）。
                GUILayout.Label("歸屬 agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                GUILayout.Label(row != null && !string.IsNullOrEmpty(row.agent) ? $"<b>{row.agent}</b>" : "(未綁)",
                    WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                GUILayout.Label("頭像 sprite", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                string spriteId = card.m_AvatarSprite != null ? card.m_AvatarSprite.ID : null;
                GUILayout.Label(string.IsNullOrEmpty(spriteId) ? "(未指定)" : spriteId, WrapLabelStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                GUILayout.Label("顏色", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                // 色塊：hex 解析成功就用該色畫字，失敗 / 空值顯示 (空)（不猜色，避免誤導）
                if (!string.IsNullOrEmpty(card.m_ColorHex) && ColorUtility.TryParseHtmlString(card.m_ColorHex, out var c))
                    GUILayout.Label($"██ {card.m_ColorHex}", UCL_GUIStyle.GetLabelStyle(c), GUILayout.ExpandWidth(false));
                else
                    GUILayout.Label(string.IsNullOrEmpty(card.m_ColorHex) ? "(空)" : $"{card.m_ColorHex}(解析失敗)",
                        WrapLabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("清單欄位", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                GUILayout.Label($"口頭禪 × {Count(card.m_Catchphrases)}　擅長 × {Count(card.m_Skills)}　"
                    + $"不擅長 × {Count(card.m_AntiSkills)}　外觀 prompt {(string.IsNullOrEmpty(card.m_AppearancePrompt) ? "無" : $"{card.m_AppearancePrompt.Length} 字")}",
                    WrapLabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            GUILayout.Label($"角色設定：{Ellipsis(card.m_RoleSettings, 120)}", WrapLabelStyle);
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
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "CreateAgentFold", 21);
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
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "CreatePersonaFold", 21);
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
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "RebindFold", 21);
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
        // 區塊：角色卡操作實作
        // 物理意義：建卡 = 對這個 persona 宣告「它要有臉」；卡與 persona 檔以同 ID 對齊，此處不改 persona 檔。
        // 數值影響：Save() 寫一個新 .json 到當前編輯模組；已存在則一律跳過（絕不覆寫手工調過的卡）。
        // ===========================================================
        void DoCreateCard(string iPersona)
        {
            var row = m_Personas.FirstOrDefault(p => p.name == iPersona);
            if (row == null) { SetResult($"❌ 建立角色卡失敗：找不到 persona `{iPersona}`（請按「重新載入」）"); return; }

            try
            {
                var asset = new UCL_ChatTavernPersonaCardAsset(iPersona);   // ctor → Init(iID)，設 ID
                // 檔案已在磁碟但 GetAllIDs 沒列到（模組快取未刷新）→ 寧可漏建也不覆寫，改叫使用者重掃
                if (asset.ContainsAsset(iPersona))
                {
                    SetResult($"ℹ 角色卡 `{iPersona}` 的檔案其實已存在（模組快取沒刷新）— 已跳過建立，不覆寫既有內容");
                    asset.ClearAllCache();
                    LoadData();
                    return;
                }

                // 預填兩欄（其餘留空給人細編）：角色設定沿用 layer_role / tag 標所屬 agent
                // ⚠ 刻意**不寫** m_OwnerAgentId：歸屬事實只該有一份，住在 personas/<name>.json 的 agent 欄。
                //   卡上再存一份就是雙寫來源，換綁後必然漂移（全 repo 無任何 consumer 讀它，存了純製造漂移）。
                asset.m_RoleSettings = row.layer_role ?? "";
                asset.m_Tags = new List<string>();
                if (!string.IsNullOrEmpty(row.agent)) asset.m_Tags.Add(row.agent);

                asset.Save();
                asset.ClearAllCache();          // 清型別快取 → 下次 GetAllIDs 掃得到新卡
                AssetDatabase.Refresh();        // 讓 Unity 看到新 .json（同 Cmd_SeedTavernIdentityAssets 收尾）

                SetResult($"✅ 建立角色卡：`{iPersona}`（歸屬 {(string.IsNullOrEmpty(row.agent) ? "(未綁)" : row.agent)}）"
                    + $" → 模組 [{UCL_ModuleService.CurEditModuleID}]。接著可按「✏ 編輯角色卡」填頭像 / 顏色 / 口頭禪。");
                Debug.Log($"[PersonaAgentAdmin] create persona card {iPersona} owner={row.agent}");
                NotifyTavern(
                    $"🎭 **身分後台｜建立 Persona 角色卡**\n" +
                    $"persona **{iPersona}** 有臉了 —— 角色卡建立完成，歸屬 agent **{(string.IsNullOrEmpty(row.agent) ? "(未綁)" : row.agent)}**。\n" +
                    $"📝 說明：角色卡是展示層（頭像 sprite / 顏色 / 口頭禪 / 擅長清單），跟 persona 檔以同名對齊。" +
                    $"有卡之後酒館後台的「Persona 頭像 Override」下拉才選得到它，Discord 頭像也才能釘到自己那張。",
                    "persona-agent-create-card");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 建立角色卡失敗：{ex.Message}"); }
        }

        // 跳轉編輯：對齊 UCL_SelectAssetPage 的「進頁前先 OnEdit() 建資料夾 → CommonEditPage.Create(clone)」流程
        void DoOpenCardEdit(string iPersona)
        {
            try
            {
                var util = UCL_ChatTavernPersonaCardAsset.Util;
                util.OnEdit();                                  // 確保 SaveFolderPath 存在（否則編輯頁存檔會找不到目錄）
                var asset = util.GetData(iPersona, false);       // false = 不吃快取，讀當前磁碟內容
                if (asset == null) { SetResult($"❌ 開啟編輯頁失敗：讀不到角色卡 `{iPersona}`"); return; }
                UCL_CommonEditPage.Create(asset);               // Create = clone 一份編輯，要在該頁按存檔才寫回
                SetResult($"✏ 已開啟 `{iPersona}` 的角色卡編輯頁 —— 改完記得在那頁存檔（本頁的顯示按「🔄 重讀」更新）");
            }
            catch (Exception ex) { SetResult($"❌ 開啟編輯頁失敗：{ex.Message}"); }
        }

        // 開角色卡型別的標準選取頁（要批次瀏覽 / 刪卡 / 用內建 Create 流程時走這裡）
        void DoOpenCardSelectPage()
        {
            try { UCL_SelectAssetPage.Create<UCL_ChatTavernPersonaCardAsset>(); }
            catch (Exception ex) { SetResult($"❌ 開啟角色卡清單頁失敗：{ex.Message}"); }
        }

        // 讀取當前選中 persona 的角色卡快照；讀失敗也記住 id（避免每幀重試打磁碟）
        void EnsureCardPreview(string iPersona)
        {
            if (m_CardPreviewId == iPersona) return;
            m_CardPreviewId = iPersona;
            m_CardPreview = null;
            try { m_CardPreview = UCL_ChatTavernPersonaCardAsset.Util.GetData(iPersona, false); }
            catch (Exception ex) { Debug.LogWarning($"[PersonaAgentAdmin] 讀角色卡 `{iPersona}` 失敗：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：小工具
        // ===========================================================
        string SelectedCardPersona =>
            (m_Personas.Count > 0 && m_CardPersonaIdx >= 0 && m_CardPersonaIdx < m_Personas.Count)
                ? m_Personas[m_CardPersonaIdx].name : null;

        PersonaRow SelectedCardRow() =>
            (m_Personas.Count > 0 && m_CardPersonaIdx >= 0 && m_CardPersonaIdx < m_Personas.Count)
                ? m_Personas[m_CardPersonaIdx] : null;

        static int Count(List<string> iList) => iList != null ? iList.Count : 0;

        // 自由文字顯示用：截斷 + 把 '<' 換成全角（label 開了 richText，裸 '<' 會被吃成標籤導致整行消失）
        static string Ellipsis(string iText, int iMax)
        {
            if (string.IsNullOrEmpty(iText)) return "(空)";
            string s = iText.Replace('<', '＜').Replace('\n', ' ');
            return s.Length <= iMax ? s : s.Substring(0, iMax) + "…";
        }

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
