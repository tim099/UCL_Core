// 區塊職責：銀行後台管理頁 (Bank Admin) — Treasury token 帳戶 / 繪圖券 / 酒館券 的可視化管理入口
//            （Tim 2026-07-21 拍板，參考 UCL_ChatTavernAdminPage）。
// 物理意義：三種內部貨幣分屬兩套系統 —
//          (1) tavern_token 綁 bank(agent)，走 UCL_TreasuryLedger（append-only ledger，餘額 replay）；
//          (2) 繪圖券綁 persona，存 AgentCommands/Canvas/vouchers/<persona>.json；
//          (3) 酒館券綁 persona 但分桶在 bank 下，存 AgentCommands/ChatTavern/agent_bonus_quota.json 的
//              agents.<bank>.personas.<persona>。
//          本頁讓 Tim 不必手改 JSON / 手打 CLI 就能：查餘額 / 給 agent 開戶 / 打款(薪酬 token 入戶) /
//          跨 bank 轉帳(守恆，A 扣 B 增) / 券查詢與發放。
// 數值影響：token 寫操作走 UCL_TreasuryLedger.Credit/Debit（自帶簽章 + Discord treasury_mirror 廣播）；
//          券寫操作走 JsonData round-trip + 原子 tmp+replace（照 canvas.py / work_session.py 的 schema 補 history）。
// 設計取捨：所有後台代操作一律用 caller="system" 繞過 Treasury 帳戶隔離鐵律（後台本就是代所有帳戶操作）；
//          UI 字串仿 UCL_ControlPanelPage / UCL_ChatTavernAdminPage 慣例硬編 zh-Hant（內部管理頁，不走 CodeLocalize）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UCL.Core.EditorLib.AgentCommands.Treasury;   // UCL_TreasuryLedger / TreasuryLedgerEntry
using UCL.Core.EditorLib.AgentCommands.CanvasVoucher; // UCL_CanvasVoucherLedger（繪圖券 canonical，C# 直呼不 spawn python）
using UCL.Core.EditorLib.AgentCommands.Voucher;       // UCL_TavernVoucherLedger（酒館券 canonical）+ 券共用底層
using UCL.Core.EditorLib.AgentCommands.ChatTavern; // UCL_ChatTavernIO / UCL_ChatMessage（操作通知發酒館主頻道）
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 銀行後台管理頁 — Treasury token 帳戶 / 繪圖券 / 酒館券 的查詢與管理。
    /// 入口：控制台 (UCL_ControlPanelPage) 的「🏦 銀行後台管理」按鈕。
    /// </summary>
    public class UCL_BankAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "銀行後台管理";
        public override bool ShowInPageMenu => true;

        public static UCL_BankAdminPage Create() => UCL_EditorPage.Create<UCL_BankAdminPage>();

        // ==== 路徑（跟 UCL_ChatTavernAdminPage 同一套解析根：DataRoot = <RepoRoot>/AgentCommands）====
        static string DataRoot => UCL_AgentCommandsPath.DataRoot;
        static string RegistryMetaPath => Path.Combine(DataRoot, "AwakenInit", "_registry_meta.json");
        static string PersonasDir => Path.Combine(DataRoot, "AwakenInit", "personas");
        static string CanvasVouchersDir => Path.Combine(DataRoot, "Canvas", "vouchers");
        static string TavernQuotaPath => Path.Combine(DataRoot, "ChatTavern", "agent_bonus_quota.json");

        // ==== 顯示用快取（開頁 / 按 Refresh 才重讀檔，不每幀掃磁碟）====
        JsonData m_RegistryMeta;                                    // _registry_meta.json 整份
        readonly List<string> m_BankIds = new List<string>();      // 帳號宇宙 = agent_banks values ∪ system_accounts keys
        readonly List<string> m_AgentKeys = new List<string>();    // agent_banks 的 keys（開戶下拉用）
        readonly Dictionary<string, string> m_AgentToBank = new Dictionary<string, string>();  // agent → bank（agent_banks 原表）
        readonly List<string> m_PersonaNames = new List<string>(); // PersonaCard asset 全 ID
        readonly Dictionary<string, string> m_PersonaToAgent = new Dictionary<string, string>(); // persona → agent（讀 persona 檔 agent 欄）
        bool m_Loaded = false;

        // ==== 餘額快取（P0 效能修）====
        // 物理意義：IMGUI ContentOnGUI 每 repaint frame 都跑。UCL_TreasuryLedger.GetBalance 會 **replay
        //          整個 ledger**（某 bank 數千筆 entry = 每幀數千次檔案讀取）、券餘額也每幀讀檔，還在
        //          overview + voucher 兩 panel 各算一次 → repaint 嚴重卡頓。
        // 數值影響：只在「選擇改變 / 操作後 / Refresh」重算，steady-state repaint 走快取零磁碟 I/O。
        int m_CacheTokenBal = 0;
        int m_CacheCanvasBal = 0;
        int m_CacheTavernBal = -1;        // -1 = 無法解析 persona 的 bank
        string m_CacheForBank = "\0";     // 快取對應的選擇（sentinel 初值保證首幀必算）
        string m_CacheForPersona = "\0";
        bool m_BalancesDirty = true;      // LoadData / 操作後 / Refresh 設 true → 下輪強制重算

        // ==== 下拉 / 輸入 draft ====
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();  // PopupSearchCache cache 容器
        int m_SelectedPersonaIdx = 0;      // 上方 persona 下拉
        int m_SelectedBankIdx = 0;         // 上方 bank 下拉
        int m_TransferToBankIdx = 0;       // 轉帳的 to_account 下拉

        // 開戶
        string m_NewAgentDraft = "";       // 要開戶的 agent key
        string m_NewBankDraft = "";        // 對應 bank id（預設命名慣例 {agent}-da-xiaojie）
        string m_OpenInitAmountDraft = "0";// 開戶初始種子額度（0 = 只註冊不種子）

        // 打款 / 轉帳 / 發券的金額 + 描述 draft
        string m_DepositAmountDraft = "0";
        string m_DepositSourceKind = "tim_grant";
        string m_DepositDescDraft = "";
        string m_TransferAmountDraft = "0";
        string m_TransferDescDraft = "";
        string m_CanvasGrantAmountDraft = "0";
        string m_TavernGrantAmountDraft = "0";   // 酒館券發放金額（Tim 2026-07-24：接上 UCL_TavernVoucherLedger canonical grant）
        string m_VoucherDescDraft = "";   // 繪圖券／酒館券發放共用的說明欄（Tim 2026-07-21：發券同步說明到酒館通知，仿打款）

        // 操作結果訊息（持久顯示直到下次操作，取代 Editor-only DisplayDialog）
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

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
        }

        // ===========================================================
        // 區塊：資料載入
        // 物理意義：registry meta（agent_banks / system_accounts）+ persona 清單 + persona→agent 對照
        //          一次讀齊進快取；draft / 下拉索引維持不動（避免 Refresh 打斷正在填的操作）。
        // 數值影響：純讀，不寫檔。
        // ===========================================================
        void LoadData()
        {
            m_Loaded = true;
            m_RegistryMeta = null;
            m_BankIds.Clear(); m_AgentKeys.Clear(); m_AgentToBank.Clear();
            m_PersonaNames.Clear(); m_PersonaToAgent.Clear();
            try
            {
                // ---- registry meta：帳號宇宙 = agent_banks values ∪ system_accounts keys ----
                var bankSet = new SortedSet<string>(StringComparer.Ordinal);
                if (File.Exists(RegistryMetaPath))
                {
                    m_RegistryMeta = JsonData.ParseJson(File.ReadAllText(RegistryMetaPath));
                    // agent_banks：agent → bank 權威映射
                    if (m_RegistryMeta != null && m_RegistryMeta.Contains("agent_banks"))
                    {
                        var ab = m_RegistryMeta["agent_banks"];
                        if (ab.IsObject && ab.Dic != null)
                        {
                            foreach (var agent in ab.Dic.Keys.Where(k => !k.StartsWith("_")).OrderBy(k => k, StringComparer.Ordinal))
                            {
                                string bank = ab.GetString(agent, "");
                                if (string.IsNullOrEmpty(bank)) continue;
                                m_AgentKeys.Add(agent);
                                m_AgentToBank[agent] = bank;
                                bankSet.Add(bank);
                            }
                        }
                    }
                    // system_accounts：系統 NPC 帳號（tavern-keeper 等），也是合法帳號宇宙成員
                    if (m_RegistryMeta != null && m_RegistryMeta.Contains("system_accounts"))
                    {
                        var sa = m_RegistryMeta["system_accounts"];
                        if (sa.IsObject && sa.Dic != null)
                            foreach (var acc in sa.Dic.Keys.Where(k => !k.StartsWith("_")))
                                bankSet.Add(acc);
                    }
                }
                m_BankIds.AddRange(bankSet);

                // ---- persona 清單（PersonaCard asset 全 ID，跟 AdminPage 同來源）----
                try
                {
                    foreach (var id in UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernPersonaCardAsset.Util.GetAllIDs())
                        if (!string.IsNullOrEmpty(id) && !id.StartsWith("_")) m_PersonaNames.Add(id);
                    m_PersonaNames.Sort(StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BankAdmin] PersonaCard GetAllIDs fail: {ex.Message}");
                }

                // ---- persona → agent（讀各 persona 檔的 agent 欄；SOT，不另存一份）----
                if (Directory.Exists(PersonasDir))
                {
                    foreach (var pf in Directory.GetFiles(PersonasDir, "*.json"))
                    {
                        try
                        {
                            var pj = JsonData.ParseJson(File.ReadAllText(pf));
                            string name = Path.GetFileNameWithoutExtension(pf);
                            string agent = pj != null ? pj.GetString("agent", "") : "";
                            if (!string.IsNullOrEmpty(agent)) m_PersonaToAgent[name] = agent;
                        }
                        catch { /* 單檔壞不擋整體載入 */ }
                    }
                }

                // draft 預設：開戶 bank 命名慣例跟著 agent draft 走（若使用者還沒改）
                SyncNewBankDraftFromAgent();
                // 資料重載 → 餘額快取失效，下輪重算
                m_BalancesDirty = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BankAdmin] load fail: {e.Message}");
            }
        }

        // ===========================================================
        // 區塊：agent → bank 解析（C# 端最小複刻 _lib/bank_resolver.py 的 agent→bank 規則）
        // 物理意義：Windows 大小寫不敏感 + agent_aliases override + 內建 claude/anthropic→claude-code；
        //          認不出 → 命名慣例 fallback {agent}-da-xiaojie（跟 Python resolver 一致，避免 split-brain）。
        // 數值影響：純查表，不寫檔。
        // ===========================================================
        string ResolveAgentToBank(string agent)
        {
            if (string.IsNullOrEmpty(agent)) return agent;
            // Step 1: direct hit
            if (m_AgentToBank.TryGetValue(agent, out var direct)) return direct;
            // Step 2: case-insensitive 比對
            foreach (var kv in m_AgentToBank)
                if (string.Equals(kv.Key, agent, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            // Step 3: alias（registry agent_aliases 優先，其次內建 claude/anthropic→claude-code）
            string canonical = ResolveAgentAlias(agent);
            if (!string.Equals(canonical, agent, StringComparison.Ordinal))
            {
                if (m_AgentToBank.TryGetValue(canonical, out var viaAlias)) return viaAlias;
                foreach (var kv in m_AgentToBank)
                    if (string.Equals(kv.Key, canonical, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            // Step 4: 認不出 → fail-loud 回空字串（**絕不 auto-mint bank 名**）
            // basecamp 2026-07-21 加碼：admin 頁會打款/轉帳/開戶動錢，對未知 target 憑空造
            // 「{agent}-da-xiaojie」＝錢可能飛進打錯字的幽靈 bank。後台工具權限最高、寫入最權威，
            // 該被更嚴格要求 — 未知一律拒絕攤給人看，不 derive、不 mint。
            // （對比 Python runtime resolver 保留 {canonical}-da-xiaojie 作「刻意開新 bank」的 derive，
            //   那是背景自動流程的可接受語意；admin 代操作 UI 不吃這套。）
            return "";
        }

        // agent alias 解析：純讀 registry agent_aliases（小寫 key），零硬編邏輯。
        // Bank 整合 2026-07-21 data-down：原本硬編的 claude/anthropic→claude-code 已下沉成
        // _registry_meta.json agent_aliases 的明文資料，本函式只「笨查表」— 沒有 code 邏輯可 split-brain。
        string ResolveAgentAlias(string agent)
        {
            string lower = agent.ToLowerInvariant();
            if (m_RegistryMeta != null && m_RegistryMeta.Contains("agent_aliases"))
            {
                var al = m_RegistryMeta["agent_aliases"];
                if (al.IsObject && al.Dic != null)
                    foreach (var k in al.Dic.Keys)
                        if (k.ToLowerInvariant() == lower) return al.GetString(k, agent);
            }
            return agent;   // 查無 alias → 原樣回（不再硬編任何 vendor 特例）
        }

        // persona → bank（兩跳：persona→agent→bank）；查不到 agent 回空字串（caller 顯示 fail-loud 提示）
        string ResolvePersonaToBank(string persona)
        {
            if (string.IsNullOrEmpty(persona)) return "";
            if (!m_PersonaToAgent.TryGetValue(persona, out var agent) || string.IsNullOrEmpty(agent)) return "";
            return ResolveAgentToBank(agent);
        }

        // 開戶 bank draft 跟著 agent draft 走（使用者沒手動改過才自動帶）
        void SyncNewBankDraftFromAgent()
        {
            string a = (m_NewAgentDraft ?? "").Trim();
            if (!string.IsNullOrEmpty(a)) m_NewBankDraft = $"{a}-da-xiaojie";
        }

        string SelectedPersona =>
            (m_PersonaNames.Count > 0 && m_SelectedPersonaIdx >= 0 && m_SelectedPersonaIdx < m_PersonaNames.Count)
                ? m_PersonaNames[m_SelectedPersonaIdx] : null;

        string SelectedBank =>
            (m_BankIds.Count > 0 && m_SelectedBankIdx >= 0 && m_SelectedBankIdx < m_BankIds.Count)
                ? m_BankIds[m_SelectedBankIdx] : null;

        string TransferToBank =>
            (m_BankIds.Count > 0 && m_TransferToBankIdx >= 0 && m_TransferToBankIdx < m_BankIds.Count)
                ? m_BankIds[m_TransferToBankIdx] : null;

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            DrawSelectorPanel();
            GUILayout.Space(6);
            DrawOverviewPanel();
            GUILayout.Space(6);
            DrawTokenOpsPanel();      // 開戶 / 打款 / 轉帳
            GUILayout.Space(6);
            DrawVoucherPanel();       // 繪圖券 + 酒館券 查詢 / 發放
            GUILayout.Space(6);
            DrawResultPanel();
        }

        // ===========================================================
        // 區塊：頂部選擇器 — persona + bank 兩個下拉
        // 物理意義：券綁 persona → persona 下拉；token 綁 bank → bank 下拉。選 persona 自動同步 bank
        //          （persona→agent→bank 是決定性映射），但 bank 仍可獨立覆寫（查系統帳號 / 別的 bank）。
        // ===========================================================
        void DrawSelectorPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>🏦 銀行後台管理</b> — 選 persona / bank 查看與操作（券綁 persona、token 綁 bank）", WrapLabelStyle);

                // Persona 下拉
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (m_PersonaNames.Count == 0)
                        GUILayout.Label("(無 persona — 檢查 PersonaCard asset)", UCL_GUIStyle.LabelStyle);
                    else
                    {
                        int newIdx = UCL_GUILayout.PopupSearchCache(m_SelectedPersonaIdx, m_PersonaNames, m_Dic, "BankPersonaPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                        if (newIdx != m_SelectedPersonaIdx && newIdx >= 0 && newIdx < m_PersonaNames.Count)
                        {
                            m_SelectedPersonaIdx = newIdx;
                            // 選 persona → 自動把 bank 下拉同步到它解析出的 bank（找得到才同步）
                            string b = ResolvePersonaToBank(m_PersonaNames[newIdx]);
                            int bi = m_BankIds.IndexOf(b);
                            if (bi >= 0) m_SelectedBankIdx = bi;
                            GUI.FocusControl(null);
                        }
                        // persona→agent→bank 解析顯示（fail-loud：查不到明示）
                        string p = m_PersonaNames[m_SelectedPersonaIdx];
                        string ag = m_PersonaToAgent.TryGetValue(p, out var a) ? a : null;
                        string bk = ResolvePersonaToBank(p);
                        GUILayout.Label(
                            string.IsNullOrEmpty(ag)
                                ? "<color=#ff8866>⚠ 此 persona 檔缺 agent 欄，無法解析 bank（fail-loud，不 mint）</color>"
                                : string.IsNullOrEmpty(bk)
                                    ? $"→ agent <b>{ag}</b> → <color=#ff8866>⚠ agent 未註冊於 agent_banks，拒絕 auto-mint（先開戶）</color>"
                                    : $"→ agent <b>{ag}</b> → bank <b>{bk}</b>",
                            WrapLabelStyle);
                    }
                }

                // Bank 下拉
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Bank", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (m_BankIds.Count == 0)
                        GUILayout.Label("(無 bank — 檢查 _registry_meta.json 的 agent_banks)", UCL_GUIStyle.LabelStyle);
                    else
                    {
                        int newIdx = UCL_GUILayout.PopupSearchCache(m_SelectedBankIdx, m_BankIds, m_Dic, "BankPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                        if (newIdx >= 0 && newIdx < m_BankIds.Count) m_SelectedBankIdx = newIdx;
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：餘額快取重算（P0 效能修的核心）
        // 物理意義：只在「選擇改變 or dirty」時才真的去 replay ledger / 讀券檔；否則直接 return 走快取。
        //          在 DrawOverviewPanel 開頭呼叫（此時 DrawSelectorPanel 已於本幀更新選擇索引，無 1-frame 延遲）。
        // 數值影響：steady-state repaint（選擇沒動）＝零磁碟 I/O；選擇一變 / 操作後才付一次重算成本。
        // ===========================================================
        void EnsureBalances()
        {
            string bank = SelectedBank;
            string persona = SelectedPersona;
            // 快取命中：選擇沒變且非 dirty → 直接用上輪算好的值，不碰磁碟
            if (!m_BalancesDirty && bank == m_CacheForBank && persona == m_CacheForPersona) return;
            // 快取失效：重算一次（token replay ledger + 兩種券讀檔）
            m_CacheTokenBal = string.IsNullOrEmpty(bank) ? 0 : SafeGetTokenBalance(bank);
            m_CacheCanvasBal = string.IsNullOrEmpty(persona) ? 0 : GetCanvasVoucherBalance(persona);
            string pbank = string.IsNullOrEmpty(persona) ? "" : ResolvePersonaToBank(persona);
            m_CacheTavernBal = string.IsNullOrEmpty(pbank) ? -1 : GetTavernVoucherBalance(pbank, persona);
            m_CacheForBank = bank;
            m_CacheForPersona = persona;
            m_BalancesDirty = false;
        }

        // ===========================================================
        // 區塊：帳戶總覽（唯讀）— 選定 bank 的 token 餘額 + 選定 persona 的兩種券餘額（全走快取）
        // ===========================================================
        void DrawOverviewPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>💰 帳戶總覽</b>（唯讀）", WrapLabelStyle);

                EnsureBalances();   // 只在選擇變/dirty 時重算；steady-state 零 I/O（避免每幀 replay ledger 卡頓）
                string bank = SelectedBank;
                string persona = SelectedPersona;

                // token 餘額（綁 bank，快取值）
                GUILayout.Label(
                    string.IsNullOrEmpty(bank)
                        ? "  💵 tavern_token: (未選 bank)"
                        : $"  💵 tavern_token（bank <b>{bank}</b>）: <b>{m_CacheTokenBal}</b>",
                    WrapLabelStyle);

                // 券（綁 persona，快取值）
                if (!string.IsNullOrEmpty(persona))
                {
                    GUILayout.Label($"  🎨 繪圖券（persona <b>{persona}</b>）: <b>{m_CacheCanvasBal}</b>", WrapLabelStyle);
                    string pbank = ResolvePersonaToBank(persona);   // 純 dict 查表，非 I/O
                    GUILayout.Label(
                        m_CacheTavernBal < 0
                            ? "  🍺 酒館券: <color=#ff8866>(無法解析 persona 的 bank)</color>"
                            : $"  🍺 酒館券／自由時間（{pbank}.personas.{persona}）: <b>{m_CacheTavernBal}</b>",
                        WrapLabelStyle);
                }
                else GUILayout.Label("  🎨 繪圖券 / 🍺 酒館券: (未選 persona)", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：token 操作 — 開戶 / 打款 / 轉帳
        // 數值影響：全部走 UCL_TreasuryLedger，caller="system" 繞帳戶隔離（後台代操作）；金額正整數驗證。
        // ===========================================================
        void DrawTokenOpsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>💵 Token 操作</b>（tavern_token，走 Treasury ledger）", WrapLabelStyle);

                // ---- 開戶 ----
                GUILayout.Label("<b>🏦 開戶</b>：註冊新 agent→bank 映射（寫 _registry_meta.json）＋可選種子額度（system_init credit）", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                    string newAgent = GUILayout.TextField(m_NewAgentDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    if (newAgent != m_NewAgentDraft) { m_NewAgentDraft = newAgent; SyncNewBankDraftFromAgent(); }
                    GUILayout.Label("bank", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_NewBankDraft = GUILayout.TextField(m_NewBankDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    GUILayout.Label("種子", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_OpenInitAmountDraft = GUILayout.TextField(m_OpenInitAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    if (GUILayout.Button("開戶", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                        DoOpenAccount();
                }

                GUILayout.Space(4);

                // ---- 打款（薪酬 token 入戶）----
                GUILayout.Label("<b>💵 打款</b>：直接把 token 入選定 bank（薪酬 / 獎金 / Tim grant）", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"→ bank: <b>{SelectedBank ?? "(未選)"}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    GUILayout.Label("金額", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_DepositAmountDraft = GUILayout.TextField(m_DepositAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    GUILayout.Label("source_kind", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_DepositSourceKind = GUILayout.TextField(m_DepositSourceKind ?? "tim_grant", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                    if (GUILayout.Button("打款", UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                        DoDeposit();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("說明", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_DepositDescDraft = GUILayout.TextField(m_DepositDescDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                GUILayout.Space(4);

                // ---- 轉帳（守恆，跨 bank）----
                GUILayout.Label("<b>🔁 轉帳</b>：從上方選定 bank → 目標 bank（A 扣 N、B 增 N，共用 tx_id，credit 失敗自動 rollback）", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"from: <b>{SelectedBank ?? "(未選)"}</b>  →  to", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    if (m_BankIds.Count > 0)
                    {
                        int newIdx = UCL_GUILayout.PopupSearchCache(m_TransferToBankIdx, m_BankIds, m_Dic, "TransferToPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        if (newIdx >= 0 && newIdx < m_BankIds.Count) m_TransferToBankIdx = newIdx;
                    }
                    GUILayout.Label("金額", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_TransferAmountDraft = GUILayout.TextField(m_TransferAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    if (GUILayout.Button("轉帳", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                        DoTransfer();
                }
                GUILayout.Label("  ⚠ 打款 / 轉帳 / 開戶種子皆為寫帳操作（append-only ledger，可再開反向操作修正但不會消失）。", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：券操作 — 繪圖券（Canvas）+ 酒館券（agent_bonus_quota）查詢與發放
        // 數值影響：券寫操作走 JsonData round-trip + 原子寫；補 history 保持與 canvas.py / work_session.py 同 schema。
        // ===========================================================
        void DrawVoucherPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>🎫 券操作</b>（綁 persona；上方選定 persona = <b>" + (SelectedPersona ?? "(未選)") + "</b>）", WrapLabelStyle);

                bool hasPersona = !string.IsNullOrEmpty(SelectedPersona);

                // ---- 共用說明欄（繪圖券／酒館券發放共用；仿打款，發券時同步進酒館通知的 📌 本次備註）----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("說明", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_VoucherDescDraft = GUILayout.TextField(m_VoucherDescDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                // ---- 繪圖券（餘額用快取值，避免重複讀檔）----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"🎨 繪圖券 餘額: <b>{(hasPersona ? m_CacheCanvasBal.ToString() : "-")}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("發放", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_CanvasGrantAmountDraft = GUILayout.TextField(m_CanvasGrantAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    using (new EditorGUI.DisabledScope(!hasPersona))
                        if (GUILayout.Button("發繪圖券", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                            DoGrantCanvasVoucher();
                }

                // ---- 酒館券（餘額用快取值；發放走 UCL_TavernVoucherLedger canonical grant）----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"🍺 酒館券 餘額: <b>{(m_CacheTavernBal < 0 ? "-" : m_CacheTavernBal.ToString())}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("發放", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_TavernGrantAmountDraft = GUILayout.TextField(m_TavernGrantAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    using (new EditorGUI.DisabledScope(!hasPersona || m_CacheTavernBal < 0))
                        if (GUILayout.Button("發酒館券", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.6f)), GUILayout.ExpandWidth(false)))
                            DoGrantTavernVoucher();
                }
                GUILayout.Label("  查詢直讀；發放走各券 canonical C# ledger（繪圖券 UCL_CanvasVoucherLedger／酒館券 UCL_TavernVoucherLedger），正規路徑、含 history 審計。", WrapLabelStyle);
            }
        }

        void DrawResultPanel()
        {
            if (string.IsNullOrEmpty(m_LastResultMsg)) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>📋 最近一次操作結果</b>", WrapLabelStyle);
                GUILayout.Label(m_LastResultMsg, WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：操作實作
        // ===========================================================

        // 開戶：寫 agent_banks[agent]=bank（若不存在）＋可選種子額度（system_init credit, caller=system）
        void DoOpenAccount()
        {
            string agent = (m_NewAgentDraft ?? "").Trim();
            string bank = (m_NewBankDraft ?? "").Trim();
            if (string.IsNullOrEmpty(agent) || string.IsNullOrEmpty(bank))
            { SetResult("❌ 開戶失敗：agent / bank 不可為空"); return; }
            if (!int.TryParse((m_OpenInitAmountDraft ?? "0").Trim(), out int initAmount) || initAmount < 0)
            { SetResult($"❌ 開戶失敗：種子額度需為非負整數（收到 '{m_OpenInitAmountDraft}'）"); return; }

            try
            {
                // 寫 registry（agent_banks 已有同 agent → 只更新 bank 映射；提示避免誤覆蓋）
                bool existed = m_AgentToBank.ContainsKey(agent);
                WriteRegistry(reg =>
                {
                    if (!reg.Contains("agent_banks")) reg["agent_banks"] = JsonData.ParseJson("{}");
                    reg["agent_banks"][agent] = bank;
                });

                // 可選種子額度：對新 bank 寫第一筆 system_init credit（帳戶隱式建立）
                string seedMsg = "";
                if (initAmount > 0)
                {
                    var e = UCL_TreasuryLedger.Credit(bank, initAmount, "system_init", "bank_admin_open",
                        $"開戶初始額度（BankAdminPage）agent={agent}", "system", null);
                    seedMsg = $"，種子 {initAmount} → 餘額 {e.balance_after}";
                }
                SetResult($"✅ 開戶：agent `{agent}` → bank `{bank}`{(existed ? "（覆蓋既有映射）" : "")}{seedMsg}");
                Debug.Log($"[BankAdmin] 開戶 {agent}→{bank} seed={initAmount}");
                NotifyTavern(
                    $"🏦 **銀行後台｜開戶**\n" +
                    $"agent **{agent}** → bank **{bank}**{(existed ? "（覆蓋既有映射）" : "")}{seedMsg}。\n" +
                    $"📝 說明：把一個 agent 註冊進帳號宇宙（agent_banks），之後它麾下 persona 才能收發 token；帶種子額度則注入開戶初始 tavern_token。",
                    "bank-open");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 開戶失敗：{ex.Message}"); }
        }

        // 打款：Credit 選定 bank
        void DoDeposit()
        {
            string bank = SelectedBank;
            if (string.IsNullOrEmpty(bank)) { SetResult("❌ 打款失敗：未選 bank"); return; }
            if (!int.TryParse((m_DepositAmountDraft ?? "0").Trim(), out int amount) || amount <= 0)
            { SetResult($"❌ 打款失敗：金額需為正整數（收到 '{m_DepositAmountDraft}'）"); return; }
            string sourceKind = string.IsNullOrEmpty((m_DepositSourceKind ?? "").Trim()) ? "tim_grant" : m_DepositSourceKind.Trim();
            try
            {
                string desc = string.IsNullOrEmpty(m_DepositDescDraft) ? "後台打款（BankAdminPage）" : m_DepositDescDraft.Trim();
                var e = UCL_TreasuryLedger.Credit(bank, amount, sourceKind, "bank_admin_deposit", desc, "system", null);
                SetResult($"✅ 打款：`{bank}` +{amount}（{sourceKind}）餘額 {e.balance_before} → {e.balance_after}");
                Debug.Log($"[BankAdmin] 打款 {bank} +{amount} ({sourceKind})");
                NotifyTavern(
                    $"💵 **銀行後台｜打款**\n" +
                    $"bank **{bank}** 入帳 +{amount} tavern_token（來源 {sourceKind}），餘額 {e.balance_before} → **{e.balance_after}**。\n" +
                    $"📝 說明：把 token 直接發進某帳戶（薪酬／獎金／Tim grant），token 綁 bank(agent)。\n" +
                    $"📌 本次備註：{desc}",
                    "bank-deposit");
                m_BalancesDirty = true;   // 餘額變動 → 快取失效
                m_DepositAmountDraft = "0";
            }
            catch (Exception ex) { SetResult($"❌ 打款失敗：{ex.Message}"); }
        }

        // 轉帳：Debit(from) → Credit(to)，共用 tx_id，credit 失敗 rollback（照 Cmd_Treasury.Op_Transfer）
        void DoTransfer()
        {
            string from = SelectedBank;
            string to = TransferToBank;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) { SetResult("❌ 轉帳失敗：from / to bank 未選"); return; }
            if (from == to) { SetResult("❌ 轉帳失敗：from == to 自轉禁止"); return; }
            if (!int.TryParse((m_TransferAmountDraft ?? "0").Trim(), out int amount) || amount <= 0)
            { SetResult($"❌ 轉帳失敗：金額需為正整數（收到 '{m_TransferAmountDraft}'）"); return; }

            string txId = "tx_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string desc = string.IsNullOrEmpty(m_TransferDescDraft) ? "後台轉帳（BankAdminPage）" : m_TransferDescDraft.Trim();

            // Step 1: Debit from（不足 / 隔離違規會 throw；caller=system 繞隔離）
            TreasuryLedgerEntry debitEntry;
            try { debitEntry = UCL_TreasuryLedger.Debit(from, amount, "trade", txId, desc, "system", txId); }
            catch (Exception ex) { SetResult($"❌ 轉帳 Debit 失敗：{ex.Message}"); return; }

            // Step 2: Credit to（罕見失敗 → rollback 退錢給 from）
            try { UCL_TreasuryLedger.Credit(to, amount, "trade", txId, desc, "system", txId); }
            catch (Exception ex)
            {
                try
                {
                    UCL_TreasuryLedger.Credit(from, amount, "transfer_rollback", txId + "|rollback",
                        "Rollback failed transfer: " + ex.Message, "system", txId + "_rollback");
                    SetResult($"❌ 轉帳 Credit 失敗，已 rollback 退還 `{from}`：{ex.Message}");
                }
                catch (Exception rbEx)
                {
                    SetResult($"❌ 轉帳 Credit 失敗且 rollback 也失敗！DANGLING debit uuid={debitEntry.uuid} — orig={ex.Message} / rollback={rbEx.Message}");
                }
                return;
            }
            SetResult($"✅ 轉帳：`{from}` → `{to}` {amount}（tx `{txId}`），from 餘額 → {debitEntry.balance_after}");
            Debug.Log($"[BankAdmin] 轉帳 {from}→{to} {amount} tx={txId}");
            NotifyTavern(
                $"🔁 **銀行後台｜轉帳**\n" +
                $"**{from}** → **{to}** {amount} tavern_token（tx `{txId}`）；{from} 扣 {amount}、{to} 增 {amount}（守恆），{from} 餘額 → **{debitEntry.balance_after}**。\n" +
                $"📝 說明：跨 bank 帳戶間搬 token，總量守恆（一方扣、一方增），credit 失敗會自動 rollback 退回。\n" +
                $"📌 本次備註：{desc}",
                "bank-transfer");
            m_BalancesDirty = true;   // 雙邊餘額變動 → 快取失效
            m_TransferAmountDraft = "0";
        }

        // 發繪圖券（爭點二 canonical，2026-07-21 全室拍板）：**不 C# 直寫**，走 canvas.py voucher grant
        // — canvas 是繪圖券的 canonical owner（內部 lock + append-only audit）。後台工具權限最高、寫入最權威，
        //   繞 owner 直寫＝無審計發券（錢憑空出現查不了帳），比 config drift 毒得多 → 一律走 owner。
        //   BankAdminPage 是 Tim 手動 Editor 工具，Editor 在場，同步 spawn python 成本可接受（一次性、刻意、有 audit）。
        void DoGrantCanvasVoucher()
        {
            string persona = SelectedPersona;
            if (string.IsNullOrEmpty(persona)) { SetResult("❌ 發繪圖券失敗：未選 persona"); return; }
            if (!int.TryParse((m_CanvasGrantAmountDraft ?? "0").Trim(), out int amount) || amount <= 0)
            { SetResult($"❌ 發繪圖券失敗：金額需為正整數（收到 '{m_CanvasGrantAmountDraft}'）"); return; }

            string desc = string.IsNullOrEmpty(m_VoucherDescDraft) ? "後台發券（BankAdminPage）" : m_VoucherDescDraft.Trim();
            try
            {
                // Tim 2026-07-22 拍板：走 C# canonical ledger 直呼（不再 spawn python canvas.py）——
                // 從源頭消滅「canvas.py cwd 相對路徑寫到錯的 AgentCommands(stray)」那一整類 split bug。
                var (before, after) = UCL_CanvasVoucherLedger.Grant(persona, amount, "admin_grant", desc);
                SetResult($"✅ 發繪圖券（C# canonical ledger）：`{persona}` +{amount}，餘額 {before} → {after}");
                Debug.Log($"[BankAdmin] 發繪圖券 {persona} +{amount} via UCL_CanvasVoucherLedger");
                NotifyTavern(
                    $"🎨 **銀行後台｜發繪圖券** @{persona}\n" +
                    $"persona **{persona}** 發放 +{amount} 張繪圖券，餘額 {before} → **{after}**。\n" +
                    $"📝 說明：繪圖券綁 persona，用於共用像素畫布繪圖（1 券 ≈ 1 像素）；本次走 C# canonical ledger 寫入。\n" +
                    $"📌 本次備註：{desc}",
                    "voucher-grant-canvas");
                m_BalancesDirty = true;   // 繪圖券餘額變動 → 快取失效
                m_CanvasGrantAmountDraft = "0";
                m_VoucherDescDraft = "";
            }
            catch (Exception ex) { SetResult($"❌ 發繪圖券失敗（C# ledger）：{ex.Message}"); }
        }

        // 發酒館券（Tim 2026-07-24 canonical，比照繪圖券走 C# ledger）：UCL_TavernVoucherLedger 是正規 owner，
        // 以 DataRoot 錨定寫 agent_bonus_quota.json（與 work_session accrual 同源、含 history 審計）——這是有審計、
        // 正規路徑的 canonical 寫入者，非「繞 owner 直寫」。原本『缺 grant CLI 故暫停』的禁令，已被繪圖券
        // 改走 C# canonical ledger 的先例推翻（C# static owner 本身就是 canonical，非直寫繞審計）。
        void DoGrantTavernVoucher()
        {
            string persona = SelectedPersona;
            if (string.IsNullOrEmpty(persona)) { SetResult("❌ 發酒館券失敗：未選 persona"); return; }
            string bank = ResolvePersonaToBank(persona);
            if (string.IsNullOrEmpty(bank))
            { SetResult($"❌ 發酒館券失敗：persona `{persona}` 無法解析 bank（persona 檔缺 agent 欄，或 agent 未開戶；fail-loud 不 mint）"); return; }
            if (!int.TryParse((m_TavernGrantAmountDraft ?? "0").Trim(), out int amount) || amount <= 0)
            { SetResult($"❌ 發酒館券失敗：金額需為正整數（收到 '{m_TavernGrantAmountDraft}'）"); return; }

            string desc = string.IsNullOrEmpty(m_VoucherDescDraft) ? "後台發券（BankAdminPage）" : m_VoucherDescDraft.Trim();
            try
            {
                var (before, after) = UCL_TavernVoucherLedger.Grant(bank, persona, amount, "admin_grant", desc);
                SetResult($"✅ 發酒館券（C# canonical ledger）：`{bank}`.personas.`{persona}` +{amount}，餘額 {before} → {after}");
                Debug.Log($"[BankAdmin] 發酒館券 {bank}.{persona} +{amount} via UCL_TavernVoucherLedger");
                NotifyTavern(
                    $"🍺 **銀行後台｜發酒館券** @{persona}\n" +
                    $"persona **{persona}**（bank {bank}）發放 +{amount} 張酒館券／自由時間券，餘額 {before} → **{after}**。\n" +
                    $"📝 說明：酒館券綁 persona（分桶在 bank 下的 personas），用於自由時間 / 招待等；本次走 C# canonical ledger 寫入。\n" +
                    $"📌 本次備註：{desc}",
                    "voucher-grant-tavern");
                m_BalancesDirty = true;   // 酒館券餘額變動 → 快取失效
                m_TavernGrantAmountDraft = "0";
                m_VoucherDescDraft = "";
            }
            catch (Exception ex) { SetResult($"❌ 發酒館券失敗（C# ledger）：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：讀取 helpers（券餘額 / token 餘額，全部防呆回退）
        // ===========================================================
        static int SafeGetTokenBalance(string bank)
        {
            try { return UCL_TreasuryLedger.GetBalance(bank, "tavern_token"); }
            catch { return 0; }
        }

        // 委派 C# canonical ledger（跟發券同源、同路徑解析，不再自己讀檔避免路徑漂移）
        int GetCanvasVoucherBalance(string persona) => UCL_CanvasVoucherLedger.GetBalance(persona);

        // 委派酒館券 canonical ledger（跟發券同源、同路徑解析，不再自己讀檔避免 schema/路徑漂移）
        int GetTavernVoucherBalance(string bank, string persona) => UCL_TavernVoucherLedger.GetBalance(bank, persona);

        // ===========================================================
        // 區塊：受控寫入 helpers
        // ===========================================================
        void WriteRegistry(Action<JsonData> mutate)
        {
            if (!File.Exists(RegistryMetaPath)) { SetResult("❌ _registry_meta.json 不存在"); return; }
            var reg = JsonData.ParseJson(File.ReadAllText(RegistryMetaPath));
            mutate(reg);
            AtomicWrite(RegistryMetaPath, reg.ToJsonBeautify());
        }

        void SetResult(string msg) { m_LastResultMsg = msg; }

        // ===========================================================
        // 區塊：操作通知 — 每筆 bank 寫操作發一則到聊天酒館主頻道（Tim 2026-07-21 追加需求）
        // 物理意義：仿系統 NPC（酒保 tavern-keeper）廣播 — 走 UCL_ChatTavernIO.AppendMessage("tavern", ...)，
        //          fireDiscordMirror=true 讓通知自動鏡射到 Discord（Tim 手機即時收到後台動了什麼）。
        //          參考 ScreenStream 開播通知 / work_session start announce 的同一套系統廣播慣例。
        // 數值影響：純發訊息，不動任何帳；失敗只記 warning 不擋主操作（通知是輔助，不該讓已成功的寫帳回滾）。
        // ===========================================================
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
                        { "source", "BankAdminPage" },
                    },
                }, fireDiscordMirror: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BankAdmin] tavern 通知發送失敗（silent，不擋主操作）: {e.Message}");
            }
        }

        // ISO 8601 UTC + ms，對齊 Treasury / canvas 的 ts 格式 "yyyy-MM-ddTHH:mm:ss.fffZ"
        static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        // 6-char hex uuid，對齊 Treasury / canvas 的 uuid 格式
        static string ShortUuid() => Guid.NewGuid().ToString("N").Substring(0, 6);

        static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
#endif
