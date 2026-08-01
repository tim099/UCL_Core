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
        int m_CacheCentralBankBal = -1;   // 🏦 央行餘額；-1 = 讀取失敗
                                          // ⚠ 2026-08-01 回歸事故：央行面板初版每幀直接呼叫
                                          //   UCL_TreasuryLedger.GetBalance(央行) —— 對一個上萬筆的 ledger
                                          //   做 per-frame replay，Tim 回報「開頁嚴重卡頓無法操作」。
                                          //   本區塊上方的註解**就是在警告這件事**，而我照樣踩了。
                                          //   教訓：這頁任何新面板要顯示餘額，一律走快取，
                                          //   不准在 Draw* 裡直接呼叫 GetBalance。
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

        // 🏦 央行政策參數草稿（Tim 2026-08-01）—— 進頁時由 LoadCentralBankDrafts() 從落盤值填入。
        // 存草稿而非直接綁 property：TextField 每幀寫回會讓「打到一半的字」直接落盤
        // （打 "50" 的過程中會先經過 "5"，那一瞬間費率就變成 0.5% 並生效）。
        string m_ThresholdDraft = "";
        string m_FeeRateDraft = "";
        string m_MailFeeDraft = "";
        bool m_ExemptCentralDraft = true;

        // 操作結果訊息（持久顯示直到下次操作，取代 Editor-only DisplayDialog）
        string m_LastResultMsg = "";

        // ==== 📨 請款審批 ====
        // 區塊職責：待審請款單快取 + 每張單的備註 draft + 核准二段確認狀態
        // 物理意義：請款單存在磁碟（Treasury/requests/），列表只在 LoadData / 操作後 / 按重新載入時重讀 ——
        //          跟本頁餘額快取同一個理由：IMGUI 每 repaint frame 都跑，每幀掃目錄會卡頓。
        // 數值影響：純顯示狀態；真正的錢在 OnApproveClicked → UCL_TreasuryRequestStore.Approve 才動。
        readonly List<UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryPayoutRequest> m_PendingRequests
            = new List<UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryPayoutRequest>();
        readonly Dictionary<string, string> m_RequestNoteDrafts = new Dictionary<string, string>();
        string m_ApproveArmedId = null;      // 二段確認：第一次點 arm，第二次才真的打款
        double m_ApproveArmedAt = 0;
        const double APPROVE_ARM_WINDOW_SEC = 5.0;

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
            // Treasury 餘額快取自 2026-08-01 起「初始化掃一遍之後純記憶體」（Tim 拍板）——
            // 外部改動（git pull / 手動改檔 / 另一個 Editor）不會自動被看到。
            // Refresh 是使用者唯一能表達「重新認識磁碟」的入口，所以在這裡強制重掃。
            UCL_TreasuryLedger.InvalidateBalanceCache();
            m_BalancesDirty = true;
            LoadCentralBankDrafts();   // 央行政策草稿一併回讀（Refresh 也要跟著更新，不留舊值）
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
            // 待審請款單一併讀進來（放 try 外：上面掛掉不該讓審批面板變成空的假象 ——
            // 「沒有待審單」與「載入失敗」是兩件事，不能長得一樣）
            ReloadPayoutRequests();
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
            DrawPayoutRequestPanel(); // 📨 agent 請款審批（核准 = 央行撥款）
            GUILayout.Space(6);
            DrawCentralBankPanel();   // 🏦 央行 / 保管費 / 掛號信費用
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
            // 央行餘額跟選擇無關，但重算時機一致（選擇改變 / 操作後 / Refresh）——
            // 它同樣是 full-ledger replay，絕不能放進 Draw* 每幀跑。
            try { m_CacheCentralBankBal = UCL_TreasuryLedger.GetBalance(UCL_CentralBankSettings.CentralBankAccount); }
            catch { m_CacheCentralBankBal = -1; }
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

        // ===========================================================
        // 區塊：🏦 央行 / 保管費 —— Tim 2026-08-01「銀行後台管理頁可以調整保管費%數」
        // 物理意義：這三個數字原本寫死在 UCL_BartenderDaemon 的 const 裡，要動就得改 code + 重編。
        //          它們是**經濟政策參數**不是實作細節 —— 決定權該在後台，不在 C# 檔裡。
        // 數值影響：改完立刻生效（daemon 每輪重讀），不必重啟 Editor 也不必重編。
        //          費率以千分比存（50 = 5.0%），UI 收 % 字串再換算 —— 讓 Tim 打「5」或「2.5」都行。
        // 邊界：**不在這裡放「立刻結算一次」按鈕** —— 保管費是每日一次的跨日事件，
        //      手動觸發會讓同一天扣兩次（idempotency 靠的是 useRef per-day-per-account，
        //      按鈕不會繞過它，但會讓人以為扣了兩次而去查一個不存在的 bug）。
        // ===========================================================
        void DrawCentralBankPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EnsureBalances();      // 走快取；**絕不在 Draw* 裡直接 GetBalance**（見餘額快取區塊註解）
                string cb = UCL_CentralBankSettings.CentralBankAccount;
                int cbBalance = m_CacheCentralBankBal;

                GUILayout.Label($"<b>🏦 {UCL_CentralBankSettings.CentralBankDisplayName}</b> — 公庫餘額 <b>{(cbBalance < 0 ? "讀取失敗" : cbBalance.ToString())}</b> tavern_token", WrapLabelStyle);
                GUILayout.Label($"  帳號 <b>{cb}</b>｜跨日保管費全數存入此處（不再蒸發）；請款核准由此撥款（不足即拒絕，不憑空增發）。", WrapLabelStyle);

                // ---- 保管費門檻 ----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("保管費門檻", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_ThresholdDraft = GUILayout.TextField(m_ThresholdDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label("token 以下不收費", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));

                    GUILayout.Label("費率", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_FeeRateDraft = GUILayout.TextField(m_FeeRateDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label($"%（超額部分；上限 {UCL_CentralBankSettings.MaxFeePermille / 10}%）", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                }

                // ---- 掛號信費用 + 央行豁免 ----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("掛號信", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_MailFeeDraft = GUILayout.TextField(m_MailFeeDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label("token / 封（0 = 免費）", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    m_ExemptCentralDraft = GUILayout.Toggle(m_ExemptCentralDraft, " 央行豁免自己的保管費", UCL_GUIStyle.LabelStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("💾 儲存政策參數", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                        DoSaveCentralBankSettings();
                    if (GUILayout.Button("↩ 重讀", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        LoadCentralBankDrafts();
                }
                GUILayout.Label("  改完即生效（daemon 每輪重讀設定，不必重編）。參數落 <b>AgentCommands/Treasury/bank_settings.json</b>，Python 端讀同一份。", WrapLabelStyle);
            }
        }

        void LoadCentralBankDrafts()
        {
            m_ThresholdDraft = UCL_CentralBankSettings.OvernightThreshold.ToString();
            m_FeeRateDraft = UCL_CentralBankSettings.FeeRateDisplay;
            m_MailFeeDraft = UCL_CentralBankSettings.RegisteredMailFee.ToString();
            m_ExemptCentralDraft = UCL_CentralBankSettings.ExemptCentralBank;
        }

        void DoSaveCentralBankSettings()
        {
            // 三個欄位分別驗；**任何一個不合法就整批不存** —— 半套寫入會讓 Tim 以為改了兩項
            // 實際只進一項，而畫面上看不出差別。
            if (!int.TryParse((m_ThresholdDraft ?? "").Trim(), out int threshold) || threshold < 0)
            { SetResult($"❌ 保管費門檻需為非負整數（收到 '{m_ThresholdDraft}'）"); return; }
            if (!double.TryParse((m_FeeRateDraft ?? "").Trim(), out double ratePercent) || ratePercent < 0)
            { SetResult($"❌ 費率需為非負數（收到 '{m_FeeRateDraft}'）"); return; }
            if (!int.TryParse((m_MailFeeDraft ?? "").Trim(), out int mailFee) || mailFee < 0)
            { SetResult($"❌ 掛號信費用需為非負整數（收到 '{m_MailFeeDraft}'）"); return; }

            int permille = UCL_CentralBankSettings.ClampPermille((int)System.Math.Round(ratePercent * 10));
            bool clamped = permille != (int)System.Math.Round(ratePercent * 10);

            int oldThreshold = UCL_CentralBankSettings.OvernightThreshold;
            string oldRate = UCL_CentralBankSettings.FeeRateDisplay;
            int oldMailFee = UCL_CentralBankSettings.RegisteredMailFee;

            UCL_CentralBankSettings.OvernightThreshold = threshold;
            UCL_CentralBankSettings.OvernightFeePermille = permille;
            UCL_CentralBankSettings.RegisteredMailFee = mailFee;
            UCL_CentralBankSettings.ExemptCentralBank = m_ExemptCentralDraft;
            LoadCentralBankDrafts();   // 回讀落盤值 —— 顯示的是實際生效值，不是我剛打的字

            string clampNote = clamped ? $"（費率被夾到上限 {UCL_CentralBankSettings.MaxFeePermille / 10}%）" : "";
            SetResult($"✅ 政策參數已存：門檻 {oldThreshold}→{threshold}｜費率 {oldRate}%→{UCL_CentralBankSettings.FeeRateDisplay}%｜掛號信 {oldMailFee}→{mailFee}｜央行豁免 {(m_ExemptCentralDraft ? "開" : "關")}{clampNote}");
            NotifyTavern(
                $"🏦 **銀行後台｜央行政策調整**\n" +
                $"跨日保管費門檻 **{oldThreshold} → {threshold}**、費率 **{oldRate}% → {UCL_CentralBankSettings.FeeRateDisplay}%**、" +
                $"掛號信 **{oldMailFee} → {mailFee}** token/封、央行豁免 **{(m_ExemptCentralDraft ? "開" : "關")}**。{clampNote}\n" +
                $"📝 說明：保管費是全系統最大的一條資金流，它的參數變動會改變每個人每晚的餘額，所以改了要公告 —— 靜默調整等於偷偷改稅率。",
                "bank-policy");
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

                // ── 打款改由央行出帳（Tim 2026-08-01「績效獎金等也從央行打款」）──
                // 物理意義：績效獎金走的就是這個入口。它原本憑空 Credit，改成從央行扣之後
                //          「發獎金」會實際消耗公庫 —— 獎金池有上限，而那個上限看得見。
                // 邊界：**收款方就是央行時不扣**（那是往公庫注資，也是唯一的合法增發入口）。
                //      這條保留是刻意的：Tim 仍需要一個把新錢放進系統的方法，
                //      而讓它只有一個入口、且該入口就叫「打款給央行」，比散落各處的憑空 credit 好查。
                string centralBank = UCL_CentralBankSettings.CentralBankAccount;
                bool drawFromCB = !string.IsNullOrEmpty(centralBank) && bank != centralBank;
                TreasuryLedgerEntry cbDebit = null;
                if (drawFromCB)
                {
                    int cbBal = -1;
                    try { cbBal = UCL_TreasuryLedger.GetBalance(centralBank); } catch { }
                    if (cbBal >= 0 && cbBal < amount)
                    {
                        SetResult($"❌ 打款失敗：央行 `{centralBank}` 餘額 {cbBal} < 本次 {amount}。" +
                                  $"要發這筆請先「打款給央行本身」補足公庫（那是唯一的合法增發入口），不要繞過閉環。");
                        return;
                    }
                    cbDebit = UCL_TreasuryLedger.Debit(centralBank, amount, "bank_admin_disbursement",
                        "bank_admin_deposit", $"後台打款撥出給 @{bank}：{desc}", "system", null);
                }

                TreasuryLedgerEntry e;
                try
                {
                    e = UCL_TreasuryLedger.Credit(bank, amount, sourceKind, "bank_admin_deposit",
                        desc + (drawFromCB ? $"（由 {centralBank} 撥款）" : "（注資央行，合法增發）"), "system", null);
                }
                catch (Exception creditEx)
                {
                    if (cbDebit != null)
                    {
                        // 央行已扣但沒發出去 → 退回，否則錢憑空消失且無聲
                        try
                        {
                            UCL_TreasuryLedger.Credit(centralBank, amount, "bank_admin_rollback",
                                "bank_admin_deposit|rollback", $"打款失敗回滾: {creditEx.Message}", "system", null);
                        }
                        catch (Exception rbEx)
                        {
                            SetResult($"❌ 打款失敗且央行回滾也失敗 —— 央行有懸空 debit（uuid={cbDebit.uuid}, {amount}）需人工處理。orig={creditEx.Message} / rollback={rbEx.Message}");
                            return;
                        }
                    }
                    SetResult($"❌ 打款失敗{(cbDebit != null ? "（央行已回滾，帳目平）" : "")}：{creditEx.Message}");
                    return;
                }
                SetResult($"✅ 打款：`{bank}` +{amount}（{sourceKind}）餘額 {e.balance_before} → {e.balance_after}"
                          + (drawFromCB ? $"｜央行出帳 -{amount}" : "｜注資央行（增發）"));
                Debug.Log($"[BankAdmin] 打款 {bank} +{amount} ({sourceKind})");
                NotifyTavern(
                    $"💵 **銀行後台｜{(drawFromCB ? "打款（央行撥出）" : "注資央行（增發）")}**\n" +
                    $"bank **{bank}** 入帳 +{amount} tavern_token（來源 {sourceKind}），餘額 {e.balance_before} → **{e.balance_after}**。\n" +
                    (drawFromCB
                        ? $"🏦 由 **{centralBank}** 撥出 -{amount}，公庫餘額 → **{SafeBalance(centralBank)}**。\n"
                        : $"🆕 本筆是**注資央行**（唯一的合法增發入口）—— 貨幣總量增加 {amount}。\n") +
                    $"📝 說明：把 token 發進某帳戶（薪酬／績效獎金／Tim grant）。2026-08-01 起獎金由央行撥款，公庫不足即拒發。\n" +
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

        // ===========================================================
        // 區塊：📨 請款審批 — agent 走 Cmd_Treasury op=request 開單，這裡核准即實際打款
        // 物理意義：補上「agent 主張該收錢」的正規管道。此前只有兩極：自動 hook（規則寫死，
        //          超出規則的勞動無處可請）或口頭請 Tim credit（無單據、無稽核、講過就忘）。
        // 數值影響：**核准是本頁唯一會依他人主張動錢的入口** → 金額 / 收款 bank / 理由三者
        //          必須同屏可見才按得下去；核准後由 UCL_TreasuryRequestStore.Approve 寫 ledger。
        // 邊界：
        //   - 只列 pending（已決的單子留在檔案裡供稽核，不佔畫面）
        //   - 核准走**二段確認**（先 arm 再確認，5 秒窗口）—— 後台是單人操作，沒有旁人可看，
        //     誤觸就是真的付錢出去。commit 公告那條刻意不防重複是因為酒館公開、肉眼可見；
        //     這裡沒有那層社會約束，所以必須由機制擋。
        //   - 收款 bank 不在合法帳號宇宙 → 標紅警告但**不阻擋**（可能是刻意開新帳戶），
        //     由人決定。靜默擋掉會讓請款者不知道為什麼沒下文。
        // ===========================================================
        void DrawPayoutRequestPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"<b>📨 請款審批</b>（pending {m_PendingRequests.Count} 筆；agent 走 <b>Cmd_Treasury op=request</b> 開單）",
                        WrapLabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("🔄 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        ReloadPayoutRequests();
                        SetResult($"✓ 已重新載入請款單（pending {m_PendingRequests.Count} 筆）");
                    }
                    GUILayout.FlexibleSpace();
                }

                if (m_PendingRequests.Count == 0)
                {
                    GUILayout.Label("（目前沒有待審請款單）", WrapLabelStyle);
                    return;
                }

                foreach (var req in m_PendingRequests)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        bool armed = IsApproveArmed(req.request_id);
                        bool bankKnown = m_BankIds.Contains(req.target_bank);
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<b>{req.amount} {req.currency}</b> → bank <b>{req.target_bank}</b>"
                                    + (bankKnown ? "" : "　<color=yellow>⚠ 非既有帳號</color>"),
                                WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                            GUILayout.Label($"{req.requester_agent}@{req.requester_persona}", WrapLabelStyle,
                                GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                            GUILayout.Label($"`{req.request_id}`  {req.requested_at}", WrapLabelStyle, GUILayout.ExpandWidth(false));
                            GUILayout.FlexibleSpace();
                        }
                        GUILayout.Label($"理由：{req.reason}", WrapLabelStyle);
                        GUILayout.Label($"source_kind / ref：{req.source_kind} / {(string.IsNullOrEmpty(req.source_ref) ? "(無)" : req.source_ref)}",
                            WrapLabelStyle);
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label("備註", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                            string prev = m_RequestNoteDrafts.TryGetValue(req.request_id, out var d) ? d : "";
                            m_RequestNoteDrafts[req.request_id] = GUILayout.TextField(prev, UCL_GUIStyle.TextFieldStyle,
                                GUILayout.Width(UCL_GUIStyle.GetScaledSize(260)));

                            var armColor = armed ? new Color(1f, 0.5f, 0.4f) : new Color(0.5f, 1f, 0.6f);
                            if (GUILayout.Button(armed ? "✔ 確認打款（再按一次）" : "✔ 核准打款",
                                    UCL_GUIStyle.GetButtonStyle(armColor), GUILayout.ExpandWidth(false)))
                                OnApproveClicked(req);
                            if (GUILayout.Button("✘ 駁回", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                                OnRejectClicked(req);
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
                GUILayout.Label("核准 = 立即寫入 ledger（等同打款）；駁回只改狀態不動錢。"
                    + "已決的單子留在 <b>AgentCommands/Treasury/requests/</b> 供稽核，不再列於此。", WrapLabelStyle);
            }
        }

        void ReloadPayoutRequests()
        {
            m_PendingRequests.Clear();
            try
            {
                m_PendingRequests.AddRange(
                    UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryRequestStore.List(pendingOnly: true));
            }
            catch (Exception ex)
            {
                SetResult($"❌ 載入請款單失敗：{ex.Message}");
            }
        }

        bool IsApproveArmed(string requestId) =>
            !string.IsNullOrEmpty(requestId) && m_ApproveArmedId == requestId
            && (EditorApplication.timeSinceStartup - m_ApproveArmedAt) <= APPROVE_ARM_WINDOW_SEC;

        void OnApproveClicked(UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryPayoutRequest req)
        {
            // 二段確認：第一次點只 arm（5 秒內再按才真的付錢）
            if (!IsApproveArmed(req.request_id))
            {
                m_ApproveArmedId = req.request_id;
                m_ApproveArmedAt = EditorApplication.timeSinceStartup;
                SetResult($"⏳ 待確認：核准 `{req.request_id}` → 付 {req.amount} {req.currency} 給 `{req.target_bank}`"
                    + "（5 秒內再按一次「確認打款」生效）");
                return;
            }
            m_ApproveArmedId = null;
            try
            {
                string note = m_RequestNoteDrafts.TryGetValue(req.request_id, out var n) ? n : "";
                var done = UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryRequestStore.Approve(
                    req.request_id, decidedBy: "Tim", note: note);
                SetResult($"✅ 已打款：`{done.request_id}` +{done.amount} {done.currency} → `{done.target_bank}`"
                    + $"（ledger entry {done.ledger_entry_uuid}）");
                NotifyTavern(
                    $"💰 **銀行後台｜請款核准**\n" +
                    $"請款單 `{done.request_id}` 核准 —— **+{done.amount} {done.currency}** 已打入 bank **{done.target_bank}**。\n" +
                    $"🏦 由 **{UCL_CentralBankSettings.CentralBankAccount}** 撥款，公庫餘額 → **{SafeBalance(UCL_CentralBankSettings.CentralBankAccount)}**。\n" +
                    $"📝 原請款理由：{done.reason}\n" +
                    (string.IsNullOrEmpty(note) ? "" : $"📌 審批備註：{note}\n") +
                    $"🧾 請款者：{done.requester_agent}@{done.requester_persona}",
                    "payout-request-approved");
                m_BalancesDirty = true;
                ReloadPayoutRequests();
            }
            catch (Exception ex) { SetResult($"❌ 核准失敗：{ex.Message}"); }
        }

        void OnRejectClicked(UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryPayoutRequest req)
        {
            try
            {
                string note = m_RequestNoteDrafts.TryGetValue(req.request_id, out var n) ? n : "";
                var done = UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryRequestStore.Close(
                    req.request_id, UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryRequestStore.StatusRejected,
                    decidedBy: "Tim", note: note);
                SetResult($"✘ 已駁回：`{done.request_id}`（{done.amount} {done.currency} → `{done.target_bank}`）"
                    + (string.IsNullOrEmpty(note) ? "　※ 建議填備註讓請款者知道為什麼" : $"　理由：{note}"));
                NotifyTavern(
                    $"🚫 **銀行後台｜請款駁回**\n" +
                    $"請款單 `{done.request_id}`（{done.amount} {done.currency} → bank {done.target_bank}）被駁回，**未打款**。\n" +
                    (string.IsNullOrEmpty(note) ? "📝 未附理由。\n" : $"📝 理由：{note}\n") +
                    $"🧾 請款者：{done.requester_agent}@{done.requester_persona}",
                    "payout-request-rejected");
                ReloadPayoutRequests();
            }
            catch (Exception ex) { SetResult($"❌ 駁回失敗：{ex.Message}"); }
        }

        /// <summary>讀餘額給公告用；讀不到回 "?" 而不是丟例外（公告失敗不該讓已完成的金流看起來像失敗）。</summary>
        static string SafeBalance(string account)
        {
            try { return UCL_TreasuryLedger.GetBalance(account).ToString(); }
            catch { return "?"; }
        }

        void SetResult(string msg) { m_LastResultMsg = msg; }

        // ===========================================================
        // 區塊：操作通知 — 每筆 bank 寫操作發一則到聊天酒館主頻道（Tim 2026-07-21 追加需求）
        // 物理意義：仿系統 NPC（酒保 tavern-keeper）廣播 — 走 UCL_ChatTavernIO.AppendMessage("tavern", ...)，
        //          訊息寫入酒館後由 C# mirror daemon 自動鏡射到 Discord（Tim 手機即時收到後台動了什麼）。
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
                });
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
