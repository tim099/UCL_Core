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
using UCL.Core.EditorLib.AgentCommands.Mail;       // UCL_RegisteredMailIO（進帳類操作另寄一封免費系統掛號信）
using UCL.Core.EditorLib.AgentCommands;             // UCL_PersonaProfile（區域綁定讀寫接縫：換區重綁用）
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
        // persona 目錄走單一解析點（見 UCL_AwakeningService.ResolvePersonaFile 的區塊註解）
        static string PersonasDir => AgentCommands.Awakening.UCL_AwakeningService.PersonasDir;
        static string CanvasVouchersDir => Path.Combine(DataRoot, "Canvas", "vouchers");
        static string TavernQuotaPath => Path.Combine(DataRoot, "ChatTavern", "agent_bonus_quota.json");

        // ==== 顯示用快取（開頁 / 按 Refresh 才重讀檔，不每幀掃磁碟）====
        JsonData m_RegistryMeta;                                    // _registry_meta.json 整份
        readonly List<string> m_BankIds = new List<string>();      // 帳號宇宙 = agent_banks values ∪ system_accounts keys ∪ ledger 內的孤兒帳戶
        // 區塊職責：只存在於 ledger、卻沒有任何 agent_banks / system_accounts 對應的帳戶。
        // 物理意義：這種帳戶**裡面有真的錢，但沒有主人**，而且後台原本完全看不到它 ——
        //          帳號宇宙是從 registry 建的，ledger 裡冒出來的 account_id 不在其中。
        // 血證（2026-08-04）：summit 長期用 `--arg sender=summit` 發文，persona 名被 alias 歸一
        //          灌進 agent 欄，於是 2026-07-31 一筆 commit 領薪 +5 token 落進 `summit` 這個
        //          不存在的帳戶。錢在帳上、餘額表算得出來，但下拉選單找不到它，**也就無法轉出**。
        //          看不見的錢比不見的錢更難處理 —— 前者連「有問題」都不會被發現。
        readonly HashSet<string> m_OrphanBankIds = new HashSet<string>(StringComparer.Ordinal);
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
        int m_CacheCanvasExpiring;   // 未過期限時券（與永久券分開顯示 —— 混報會讓人不知道幾張明天沒了）
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

        // ==== 🧭 帳號解析規則（2026-08-14）====
        // 區塊職責：registry 裡「決定錢落到哪個帳戶」的三張表的顯示與編輯 draft。
        // 物理意義：agent_aliases / system_accounts / closed_accounts 此前**沒有任何頁面在管** ——
        //          2026-08-04 那次補登記 system_accounts 是直接手改 JSON 的。
        //          手改沒有閘門、沒有痕跡，而它改的是金流的路由表。
        // ⚠ agent_banks 刻意唯讀：它已經有兩個寫入者（本頁「開戶」＋ Persona & Agent 管理頁「開 agent」），
        //   再加第三個只會讓漂移更難查。要改去那兩處，本區只負責讓人看見全貌。
        readonly Dictionary<string, string> m_Aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<string> m_SystemAccounts = new List<string>();
        string m_ProbeDraft = "";          // 解析試算輸入
        string m_NewAliasFromDraft = "";   // 別名（來源字串）
        string m_NewAliasToDraft = "";     // 對應的 canonical agent

        // 開戶
        string m_NewAgentDraft = "";       // 要開戶的 agent key
        string m_NewBankDraft = "";        // 對應 agent id（預設命名慣例 {agent}-da-xiaojie）
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

        // 區塊職責：繪圖券的**有效期限**（分鐘）—— 0 ＝ 永久券（Tim 2026-08-18 期間限定券）。
        // 物理意義：用「幾分鐘後到期」而不是要人手打 ISO 時戳 —— 手打時戳會打錯，
        //          而打錯的後果是發出一批**已經過期**或**永遠不過期**的券，兩者都不會報錯。
        // 數值影響：>0 才換算成 expires_at；0 走原本的永久券路徑（行為與改動前逐值相同）。
        string m_CanvasExpireMinutesDraft = "0";

        // 🏦 央行政策參數草稿（Tim 2026-08-01）—— 進頁時由 LoadCentralBankDrafts() 從落盤值填入。
        // 存草稿而非直接綁 property：TextField 每幀寫回會讓「打到一半的字」直接落盤
        // （打 "50" 的過程中會先經過 "5"，那一瞬間費率就變成 0.5% 並生效）。
        string m_ThresholdDraft = "";
        string m_FeeRateDraft = "";
        string m_MailFeeDraft = "";
        bool m_ExemptCentralDraft = true;

        // 🪙 區域（貨幣）ID 草稿（Tim 2026-08-20）。二段確認狀態獨立一組 ——
        // 這個欄位改下去會讓全體 persona 的綁定檔一次對不上鍵，不能跟政策參數共用一顆 arm。
        string m_CurrencyDraft = "";
        bool m_CurrencyArmed = false;
        double m_CurrencyArmedAt = 0;

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
        // ==== 💸 轉帳審批（2026-08-04）====
        // 物理意義：跟請款審批同形狀但不同語意 —— 請款是央行撥款（消耗公庫），轉帳是 A→B（總量守恆）。
        //          主要用途是**歸戶**：把錢從孤兒 / 打錯字的帳戶搬回正主，且留下審批痕跡。
        readonly List<UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryTransferRequest> m_PendingTransfers
            = new List<UCL.Core.EditorLib.AgentCommands.Treasury.TreasuryTransferRequest>();
        readonly Dictionary<string, string> m_TransferNoteDrafts = new Dictionary<string, string>();
        string m_TransferArmedId = null;     // 二段確認，同請款
        double m_TransferArmedAt = 0;
        // 手動開單 draft（from / to 沿用上方 bank 下拉與轉帳目標下拉）
        string m_NewTransferAmountDraft = "0";
        string m_NewTransferReasonDraft = "";

        string m_ApproveArmedId = null;      // 二段確認：第一次點 arm，第二次才真的打款
        double m_ApproveArmedAt = 0;
        const double APPROVE_ARM_WINDOW_SEC = 5.0;

        // ==== 🗺 agent_banks 全表編輯（summit 2026-08-20）====
        // 區塊職責：agent→bank 路由表的**列出／刪除／覆蓋**三個此前缺席的操作。
        // 物理意義：`agent_banks` 決定「某個 agent 麾下所有 persona 的薪水流去哪個帳戶」。
        //          此前本頁只能**新增／覆蓋**（DoOpenAccount），而且：
        //            ① 沒有地方看得到全表　② 沒有刪除　③ 覆蓋既有映射**一按就生效**。
        // 數值影響：本身不動 ledger 一分錢，但它改的是「下一筆錢會流去哪」——
        //          比直接打款更難察覺，因為錯誤要等到下一次發薪才顯現，而且顯現時看起來像正常入帳。
        // 為什麼要二段確認：同一頁的其他寫入都比它嚴格（別名只准在 unresolved 時加、補登記明文不搬錢、
        //          改區域 ID／請款／轉帳皆有 arm）。唯獨改薪水去向沒有閘門 ——
        //          它至今沒出事是因為沒人常用它，不是因為它安全。
        string m_AgentBankRemoveArmed = null;   // 待確認刪除的 agent key
        double m_AgentBankRemoveArmedAt = 0;
        bool m_OpenAccountArmed = false;        // 覆蓋既有映射才需要 arm（純新增不擋）
        double m_OpenAccountArmedAt = 0;

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
            if (!m_PersonaNames.IsNullOrEmpty())
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
            }

            // 開設定檔所在資料夾（同 UCL_BartenderCliCommandsPage 的手勢；
            // 走 UCL_ExplorerUtil 而不是 Application.OpenURL —— 外部 Process 要登記）
            if (GUILayout.Button("📂 開啟設定檔位置", UCL_GUIStyle.ButtonStyle,
                GUILayout.Width(UCL_GUIStyle.GetScaledSize(150))))
            {
                UCL_ExplorerUtil.Open(UCL_CentralBankSettings.SettingsDir, "BankAdminPage");
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
            m_Aliases.Clear(); m_SystemAccounts.Clear();
            m_OrphanRowsDirty = true;   // 孤兒列（餘額／解析／綁定）跟著資料一起重算
            // registry 可能剛被本頁（開戶／銷戶／別名／補登記）或外部改過 —— 解析器也要跟著重新認識磁碟，
            // 否則畫面讀的是新表、解析走的是舊表，兩邊各自說得通而且都不報錯。
            UCL_TreasuryAccountResolver.Invalidate();
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
                            { bankSet.Add(acc); m_SystemAccounts.Add(acc); }
                    }
                    // agent_aliases：解析規則的一張表，本頁「🧭 帳號解析規則」區可增刪
                    if (m_RegistryMeta != null && m_RegistryMeta.Contains("agent_aliases"))
                    {
                        var al = m_RegistryMeta["agent_aliases"];
                        if (al.IsObject && al.Dic != null)
                            foreach (var k in al.Dic.Keys.Where(k => !k.StartsWith("_")))
                                m_Aliases[k] = al.GetString(k, "");
                    }
                }
                // ---- 孤兒帳戶：ledger 有、registry 沒有 ----
                // 加進帳號宇宙是為了**看得見 + 轉得出去**，不是承認它合法；UI 會另外標記。
                // 這裡不 auto-mint 任何 registry 條目 —— 補登記是人的決定，不是列表的副作用。
                m_OrphanBankIds.Clear();
                try
                {
                    // 已銷戶的帳號要退出孤兒名單與下拉選單。
                    // ⚠ 銷戶**不刪 ledger entry**（append-only），所以下面這個掃描一定會把它撈回來 ——
                    //   不在這裡濾掉的話，銷完戶按 Refresh 它照樣在，而且會同時出現在「孤兒」與
                    //   「已銷戶」兩處。症狀看起來像「刷新沒作用」，實際是掃描沒把銷戶算進去。
                    var closedSet = UCL_TreasuryAccountResolver.GetClosedAccounts();
                    foreach (var e in UCL_TreasuryLedger.LoadAllEntries())
                    {
                        if (e == null || string.IsNullOrEmpty(e.account_id)) continue;
                        if (bankSet.Contains(e.account_id)) continue;
                        m_OrphanBankIds.Add(e.account_id);
                    }
                    // 例外：已銷戶卻**還有餘額**的，絕不隱藏。
                    // 銷戶前置條件是餘額 0，所以這種情況代表閘門被繞過或事後又有錢進來 ——
                    // 那正是最該被看見的一種帳，藏起來就變成「看不見的錢」。
                    foreach (var closed in closedSet.Keys)
                        if (m_OrphanBankIds.Contains(closed) && SafeGetTokenBalance(closed) == 0)
                            m_OrphanBankIds.Remove(closed);

                    foreach (var orphan in m_OrphanBankIds) bankSet.Add(orphan);
                }
                catch (Exception ex)
                {
                    // 掃不到要出聲：靜默跳過會讓「沒有孤兒」與「沒掃成功」長得一樣
                    Debug.LogWarning($"[BankAdmin] 掃 ledger 孤兒帳戶失敗（本次列表可能漏帳戶）: {ex.Message}");
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
            ReloadTransferRequests();
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
            DrawOrphanPanel();        // 👻 孤兒帳戶：標記遷移 / 銷戶（獨立區，收合時仍顯示可銷戶數）
            GUILayout.Space(6);
            DrawResolveRulesPanel();  // 🧭 帳號解析規則：試算 / alias / 系統帳號 / 已銷戶
            GUILayout.Space(6);
            DrawTokenOpsPanel();      // 開戶 / 打款 / 轉帳
            GUILayout.Space(6);
            DrawVoucherPanel();       // 繪圖券 + 酒館券 查詢 / 發放
            GUILayout.Space(6);
            DrawPayoutRequestPanel(); // 📨 agent 請款審批（核准 = 央行撥款）
            GUILayout.Space(6);
            DrawTransferRequestPanel(); // 💸 轉帳審批（核准 = A→B 搬錢，總量守恆）
            GUILayout.Space(6);
            DrawCurrencyPanel();      // 🪙 區域（貨幣）ID —— letters/<persona>/bank/<ID>.md 的鍵
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
                // 按鈕放 Label 前面（建頁守則 L2）—— 後面那句說明會隨語系/縮放變長，
                // 按鈕排在它後面時視窗一窄就被擠出可見範圍。
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("🔀 agent↔帳號 合一遷移",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.6f)), GUILayout.ExpandWidth(false)))
                        UCL_BankMigrationPage.Create();
                    GUILayout.Label("<b>🏦 銀行後台管理</b> — 選 persona / bank 查看與操作（券綁 persona、token 綁 bank）",
                        WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }

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
            m_CacheCanvasBal = string.IsNullOrEmpty(persona) ? 0 : GetCanvasVoucherPermanent(persona);
            m_CacheCanvasExpiring = string.IsNullOrEmpty(persona) ? 0 : GetCanvasVoucherExpiring(persona);
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
        // 區塊：section 折疊（Tim 2026-08-04；形狀參考 UCL_ControlPanelPage）
        // 物理意義：本頁 section 越長越多（總覽 / 開戶打款轉帳 / 券 / 請款審批 / 轉帳審批 / 央行），
        //          全部展開時要捲很久才找得到目標。**預設收合**讓它回到「一眼看完標題列」的密度。
        // 數值影響：純 UI；折疊狀態存 m_FoldDic（頁面 instance 生命週期）。
        // 設計取捨：標題列即使收合也顯示關鍵摘要（餘額 / 待審筆數），
        //          否則使用者得先展開才知道「這裡有沒有事」—— 那等於把資訊藏起來。
        // ===========================================================
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        /// <summary>畫一個預設收合的 section；回傳是否展開。summary 收合時也看得到。</summary>
        bool FoldHeader(string key, string title, string summary)
        {
            bool show = false;
            using (new GUILayout.HorizontalScope())
            {
                show = UCL_GUILayout.Toggle(m_FoldDic, key, 21, iDefaultValue: false);
                GUILayout.Label(title, new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                if (!string.IsNullOrEmpty(summary))
                    GUILayout.Label(summary, new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            return show;
        }

        // ===========================================================
        // 區塊：帳戶總覽（唯讀）— 選定 bank 的 token 餘額 + 選定 persona 的兩種券餘額（全走快取）
        // ===========================================================
        void DrawOverviewPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EnsureBalances();   // 摘要列要用餘額，先算（只在 dirty 時真的重算）
                string orphanTag = m_OrphanBankIds.Count > 0
                    ? $"　<color=yellow>⚠ {m_OrphanBankIds.Count} 孤兒帳戶</color>" : "";
                bool showOv = FoldHeader("BankOverviewFold", "<b>💰 帳戶總覽</b>（唯讀）",
                    $"　bank <b>{SelectedBank ?? "-"}</b>：<b>{m_CacheTokenBal}</b> token{orphanTag}");
                if (!showOv) return;

                // 孤兒帳戶的清單與操作已移到獨立區塊「👻 孤兒帳戶」——
                // 它此前埋在本區的收合層裡，等於「要先猜到才找得到」。這裡只留一行指路。
                if (m_OrphanBankIds.Count > 0)
                    GUILayout.Label($"  ⚠ 另有 <b>{m_OrphanBankIds.Count}</b> 個孤兒帳戶 —— 見下方「👻 孤兒帳戶」區塊（標記遷移 / 銷戶）。",
                        WrapLabelStyle);

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
                    GUILayout.Label($"  🎨 繪圖券（persona <b>{persona}</b>）: 永久 <b>{m_CacheCanvasBal}</b>"
                        + (m_CacheCanvasExpiring > 0
                            ? $"　＋ 限時 <color=#ffcc66><b>{m_CacheCanvasExpiring}</b></color>（到期作廢）"
                            : "　（無限時券）"), WrapLabelStyle);
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
        // 區塊：孤兒帳戶清單 —— 從「唯讀告警」升級成「可操作」（Tim 2026-08-14）
        // 物理意義：孤兒＝ledger 裡有錢、registry 裡沒有主人的 account_id。此前這裡只印一行字，
        //          要處理得自己去下拉選單湊 from/to 手打金額 —— 35 個孤兒就是 35 次手打，
        //          於是實際上沒有人會做，清單變成一個「看得見但不會被處理」的告警。
        // 兩個動作，順序不可顛倒：
        //   ① 標記遷移 —— 開一張 orphan-consolidation 轉帳單（pending，**不動錢**），
        //      目標由帳號解析器建議；Tim 在下方「💸 轉帳審批」確認後按核准才真的搬。
        //      這正是 Tim 要的「人工標記 → 後台確認 → 按下遷移」，而流程本體是既有的轉帳審批鏈，
        //      不另造第二套。
        //   ② 銷戶 —— 只有餘額歸零後才會出現。三道閘全過才准（見 DoCloseGhostAccount）。
        // 數值影響：本區塊自身零金流 —— 標記只產生單據，銷戶只寫 registry 名單。
        //          真正動錢的只有轉帳審批的核准鍵。
        // ===========================================================
        // 區塊職責：獨立的孤兒帳戶區 —— 標題列（收合狀態下唯一看得到的東西）必須自己說完
        //          「有幾個」「其中幾個可以現在銷戶」。否則銷戶功能等於不存在：
        //          它藏在別區的收合層裡，而使用者要先猜到那裡有東西才會展開（Tim 2026-08-14 回報找不到）。
        void DrawOrphanPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EnsureOrphanRows();      // ⚠ 走快取。本檔案上方那條血證：Draw* 內不准直接算餘額
                int closable = 0, withMoney = 0;
                foreach (var row in m_OrphanRows)
                {
                    if (row.Balance > 0) withMoney++;
                    else if (row.Closable) closable++;
                }
                var closedNow = m_CacheClosedAccounts;
                string summary = m_OrphanBankIds.Count == 0
                    ? "　(無孤兒帳戶)"
                    : $"　共 <b>{m_OrphanBankIds.Count}</b>｜有餘額待遷移 <color=yellow><b>{withMoney}</b></color>"
                      + $"｜可銷戶 <color=#88ff88><b>{closable}</b></color>"
                      + (closedNow.Count > 0 ? $"｜已銷戶 {closedNow.Count}" : "");
                if (!FoldHeader("BankOrphanFold", "<b>👻 孤兒帳戶</b>（標記遷移 / 銷戶）", summary)) return;
                if (m_OrphanBankIds.Count == 0 && closedNow.Count == 0)
                {
                    GUILayout.Label("（沒有孤兒帳戶 —— 每一筆錢都有登記在案的主人）", WrapLabelStyle);
                    return;
                }
                DrawOrphanRows();
            }
        }

        // 區塊職責：孤兒列的顯示資料快取 —— 每列要算餘額、跑解析、掃 persona 綁定，
        //          35 列 × 每 repaint frame 是不可接受的（本檔案 2026-08-01 卡頓事故的同一形狀）。
        // 物理意義：這些值只在「資料重載 / 操作後 / Refresh」才會變，跟餘額快取同一個時機。
        struct OrphanRow
        {
            public string Id;
            public int Balance;
            public TreasuryAccountResolution Resolution;
            public List<string> Personas;
            public bool HasTarget;    // 解析得出且與自身不同 → 可標記遷移
            public bool Closable;     // 餘額 0 且無綁定且非正式帳號且未銷戶 → 可銷戶
        }
        readonly List<OrphanRow> m_OrphanRows = new List<OrphanRow>();
        Dictionary<string, string> m_CacheClosedAccounts = new Dictionary<string, string>(StringComparer.Ordinal);
        bool m_OrphanRowsDirty = true;

        void EnsureOrphanRows()
        {
            if (!m_OrphanRowsDirty && !m_BalancesDirty) return;
            m_OrphanRows.Clear();
            m_CacheClosedAccounts = UCL_TreasuryAccountResolver.GetClosedAccounts();
            foreach (var orphan in m_OrphanBankIds.OrderBy(x => x, StringComparer.Ordinal))
            {
                // ⚠ 用 SafeGetTokenBalance（int）不是 SafeBalance（顯示字串，讀不到時回 "?"）——
                //   餘額在這裡要參與「是否為 0」的判斷，那是閘門條件，不能拿顯示值來判。
                var res = UCL_TreasuryAccountResolver.Resolve(orphan);
                var personas = UCL_TreasuryAccountResolver.GetBoundPersonas(orphan);
                int bal = SafeGetTokenBalance(orphan);
                m_OrphanRows.Add(new OrphanRow
                {
                    Id = orphan,
                    Balance = bal,
                    Resolution = res,
                    Personas = personas,
                    HasTarget = !res.IsUnresolved && res.Changed,
                    Closable = bal == 0 && personas.Count == 0
                               && !UCL_TreasuryAccountResolver.IsCanonicalAccount(orphan)
                               && !m_CacheClosedAccounts.ContainsKey(orphan),
                });
            }
            m_OrphanRowsDirty = false;
        }

        void DrawOrphanRows()
        {
            GUILayout.Label($"  ⚠ <b>{m_OrphanBankIds.Count} 個孤兒帳戶</b>（只存在於 ledger，"
                + "沒有 agent_banks / system_accounts 對應）—— 多半是 agent 名大小寫、"
                + "agent 欄被填成 persona 名，或舊命名殘留：", WrapLabelStyle);

            foreach (var row in m_OrphanRows)
            {
                string orphan = row.Id;
                int bal = row.Balance;
                var res = row.Resolution;
                bool hasTarget = row.HasTarget;
                var personas = row.Personas;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"　　• <b>{orphan}</b>　餘額 <b>{bal}</b>", WrapLabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                    GUILayout.Label(
                        hasTarget
                            ? $"建議歸入 <b>{res.AccountId}</b>（{res.Trace}）"
                            : "<color=#ff8866>無法判定歸屬</color>（解析器查無對應 —— 需人工選目標，走下方手動開單）",
                        WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(420)));

                    if (bal > 0)
                    {
                        using (new EditorGUI.DisabledScope(!hasTarget))
                        {
                            if (GUILayout.Button("📝 標記遷移", UCL_GUIStyle.GetButtonStyle(new Color(0.8f, 0.9f, 1f)),
                                    GUILayout.ExpandWidth(false)))
                                DoMarkOrphanMigration(orphan, res.AccountId, bal, res.Trace);
                        }
                    }
                    else
                    {
                        // 餘額 0 才談銷戶 —— 有錢的帳戶銷掉等於把錢移出視野，比孤兒更難查
                        if (GUILayout.Button("🚫 銷戶", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.6f)),
                                GUILayout.ExpandWidth(false)))
                            DoCloseGhostAccount(orphan);
                    }
                    GUILayout.FlexibleSpace();
                }
                if (personas.Count > 0)
                    GUILayout.Label($"　　　　↳ <color=yellow>綁定 persona：{string.Join(", ", personas)}</color>"
                        + "（有綁定就不准銷戶 —— 那筆錢的來源是這個 persona 的勞動）", WrapLabelStyle);
            }

            GUILayout.Label("　　↳ 標記遷移只開單不動錢；核准在下方「💸 轉帳審批」。"
                + "錢搬走後帳戶餘額為 0，ledger 紀錄仍留著（append-only，本來就不該消失）。", WrapLabelStyle);

            var closed = m_CacheClosedAccounts;   // 走快取：GetClosedAccounts 每次都配一份新 dict
            if (closed.Count > 0)
            {
                GUILayout.Label($"  🚫 <b>已銷戶 {closed.Count} 個</b>（不再接受任何金流；歷史保留）：", WrapLabelStyle);
                foreach (var kv in closed.OrderBy(x => x.Key, StringComparer.Ordinal))
                    GUILayout.Label($"　　• <b>{kv.Key}</b>　{kv.Value}", WrapLabelStyle);
            }
        }

        // 標記遷移：開一張 pending 轉帳單，**不動錢**。核准權留在轉帳審批面板。
        void DoMarkOrphanMigration(string orphan, string target, int amount, string trace)
        {
            if (string.IsNullOrEmpty(target) || target == orphan)
            { SetResult($"❌ 標記失敗：`{orphan}` 沒有可判定的歸屬目標，請用下方手動開單指定"); return; }
            if (amount <= 0) { SetResult($"❌ 標記失敗：`{orphan}` 餘額為 {amount}，沒有東西可搬"); return; }
            try
            {
                // 同一個孤兒已有 pending 單就不要再開第二張 —— 兩張都核准會扣兩次，
                // 而第二次會因為餘額不足失敗，看起來像「系統壞了」而不是「我開重複了」。
                foreach (var p in m_PendingTransfers)
                    if (string.Equals(p.from_bank, orphan, StringComparison.Ordinal))
                    { SetResult($"⚠ `{orphan}` 已有待審轉帳單 `{p.request_id}`（{p.amount} → {p.to_bank}），未重複開單"); return; }

                var req = UCL_TreasuryTransferRequestStore.Create(
                    orphan, target, amount,
                    reason: $"孤兒帳戶歸戶：{trace}。全額 {amount} 搬回正主後此帳戶歸零，可再銷戶。",
                    kind: "orphan-consolidation",
                    requesterAgent: "BankAdminPage", requesterPersona: SelectedPersona);
                SetResult($"✅ 已標記待遷移 `{req.request_id}`：{amount} `{orphan}` → `{target}`"
                    + "（**尚未動錢** —— 到下方「💸 轉帳審批」確認後按核准才生效）");
                ReloadTransferRequests();
            }
            catch (Exception ex) { SetResult($"❌ 標記失敗：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：幽靈帳號銷戶（Tim 2026-08-14）
        // 物理意義：ledger 是 append-only，帳戶**不是實體物件** —— 它只是「曾經出現在 entry 裡的
        //          account_id」。所以銷戶不可能是刪除，只能是一份「已銷戶名單」：
        //          寫入端據此拒收（fail-loud），後台據此把它移出待處理視野，而歷史一個字不動。
        // 三道閘（任一不過就不准銷）：
        //   ① 餘額必須為 0 —— 有錢還銷戶＝錢從視野消失，比孤兒更難查
        //   ② 沒有 persona 綁定 —— 綁定不是欄位而是一條解析鏈（persona→agent→bank），
        //      所以問的是「誰會解析到這裡」，不是「它的 persona 欄寫誰」
        //   ③ 不是註冊在案的正式帳號 —— 銷掉 registry 裡的 bank 會讓該 agent 的薪水無處可去
        // 數值影響：零金流。只在 _registry_meta.json 的 closed_accounts 加一筆。
        // 可逆性：手動從 closed_accounts 移除即復戶 —— 所以這裡不做二段確認，
        //        擋錯誤的是三道閘，不是手速。
        // ===========================================================
        void DoCloseGhostAccount(string account)
        {
            if (string.IsNullOrEmpty(account)) { SetResult("❌ 銷戶失敗：帳號為空"); return; }

            // ⚠ 這一格刻意**不用** SafeGetTokenBalance —— 它讀失敗回 0，
            //   而「讀不到餘額」與「餘額是 0」在這裡會導致完全相反的決定（放行 vs 擋下）。
            //   閘門條件必須能分辨這兩者：讀不到就是讀不到，不准當成 0 放行。
            int bal;
            try { bal = UCL_TreasuryLedger.GetBalance(account); }
            catch (Exception ex)
            { SetResult($"❌ 銷戶失敗：`{account}` 餘額讀取失敗（{ex.Message}）—— 讀不到餘額不等於餘額為 0，不放行"); return; }
            if (bal != 0)
            { SetResult($"❌ 銷戶失敗：`{account}` 餘額為 {bal}，不是 0 —— 先標記遷移把錢搬回正主再銷"); return; }

            var personas = UCL_TreasuryAccountResolver.GetBoundPersonas(account);
            if (personas.Count > 0)
            { SetResult($"❌ 銷戶失敗：`{account}` 仍有 persona 綁定（{string.Join(", ", personas)}）—— 先解除綁定再銷"); return; }

            if (UCL_TreasuryAccountResolver.IsCanonicalAccount(account))
            { SetResult($"❌ 銷戶失敗：`{account}` 是註冊在案的正式帳號（agent_banks / system_accounts），不是幽靈帳號"); return; }

            if (UCL_TreasuryAccountResolver.IsClosed(account, out _))
            { SetResult($"⚠ `{account}` 已經是銷戶狀態，未重複寫入"); return; }

            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
                WriteRegistry(reg =>
                {
                    if (!reg.Contains(UCL_TreasuryAccountResolver.ClosedAccountsKey))
                        reg[UCL_TreasuryAccountResolver.ClosedAccountsKey] = JsonData.ParseJson("{}");
                    reg[UCL_TreasuryAccountResolver.ClosedAccountsKey][account] =
                        $"幽靈帳號銷戶 {stamp}（餘額 0、無 persona 綁定、非註冊帳號）。ledger 歷史保留，不再接受任何金流。";
                });
                UCL_TreasuryAccountResolver.Invalidate();   // registry 剛改，下一次解析要看得到
                SetResult($"🚫 已銷戶 `{account}`：往後任何 credit / debit 打進來都會被拒絕並點名來源。"
                    + "ledger 歷史保留不動；要復戶就從 _registry_meta.json 的 closed_accounts 移除。");
                NotifyTavern(
                    $"🚫 **銀行後台｜幽靈帳號銷戶**\n" +
                    $"帳號 **{account}** 已銷戶（餘額 0、無 persona 綁定、非註冊帳號，三道閘全過）。\n" +
                    $"📝 說明：ledger 是 append-only，銷戶不刪任何歷史 —— 它只是一份拒收名單。" +
                    $"往後有金流打進這個帳號會**當場拋錯並點名來源**，因為那代表還有呼叫路徑沒清乾淨。",
                    "bank-account-closed");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 銷戶失敗：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：🧭 帳號解析規則 —— 決定「錢落到哪個帳戶」的路由表（Tim 2026-08-14）
        // 物理意義：ledger 的 account_id 由 UCL_TreasuryAccountResolver 依四張表判定：
        //          agent_banks（agent→bank）／agent_aliases（別名→agent）／system_accounts（終點帳號）
        //          ／closed_accounts（拒收名單），外加 personas/<n>.json 的 agent 欄。
        //          後三張此前**沒有任何 UI**，2026-08-04 補登記是直接手改 JSON —— 手改沒有閘門也沒有痕跡，
        //          而它改的是金流的路由表。
        // 數值影響：本區塊自身零金流。但它改的規則會決定**下一筆錢往哪走**，所以每個寫入動作
        //          都先讓人看到「改完之後這個字串會解析成什麼」（試算欄），而不是改完等下一筆錢來驗。
        // 邊界：agent_banks 唯讀 —— 它已有兩個寫入者（本頁開戶／Persona & Agent 管理頁開 agent），
        //      這裡再加第三個只會讓漂移更難查。要改去那兩處。
        // ===========================================================
        void DrawResolveRulesPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // 本區用到的已銷戶快取由 EnsureOrphanRows 填 —— 不倚賴「孤兒區一定先畫過」這個假設，
                // 因為畫面順序日後被調換不會有任何錯誤訊息，只會顯示成空的。
                EnsureOrphanRows();
                if (!FoldHeader("BankResolveRulesFold", "<b>🧭 帳號解析規則</b>",
                        $"　agent {m_AgentToBank.Count}｜別名 {m_Aliases.Count}｜系統帳號 {m_SystemAccounts.Count}"
                        + $"｜已銷戶 {m_CacheClosedAccounts.Count}")) return;

                // ---- 試算：任何字串進來會變成哪個帳號 ----
                GUILayout.Label("<b>🔍 解析試算</b>：輸入任意 agent / persona / 帳號字串，看它現在會解析成什麼（純讀，不寫任何東西）", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("字串", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_ProbeDraft = GUILayout.TextField(m_ProbeDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                }
                if (!string.IsNullOrWhiteSpace(m_ProbeDraft))
                {
                    var pr = UCL_TreasuryAccountResolver.Resolve(m_ProbeDraft.Trim());
                    GUILayout.Label(
                        pr.IsUnresolved
                            ? $"　<color=#ff8866>✘ 查無對應</color> —— 錢會留在 <b>{pr.AccountId}</b> 這個孤兒帳戶。"
                              + "　修法：下面加一條別名，或去開戶／補登記成系統帳號。"
                            : $"　✅ <b>{pr.AccountId}</b>　<i>{pr.Trace}</i>"
                              + (UCL_TreasuryAccountResolver.IsClosed(pr.AccountId, out var cr)
                                 ? $"　<color=#ff8866>⚠ 但此帳號已銷戶，金流會被拒收（{cr}）</color>" : ""),
                        WrapLabelStyle);
                }

                GUILayout.Space(6);

                // ---- agent_aliases：把認不出的字串接到既有 agent ----
                GUILayout.Label($"<b>🔀 別名（agent_aliases）</b> {m_Aliases.Count} 筆"
                    + "：把「解析不出來的字串」對到既有 agent。大小寫已由解析器自動處理，<b>這裡只放真正不同的拼法</b>"
                    + "（舊命名如 <b>zeta-bank</b>、外部識別碼如 <b>discord:xxxx</b>）。", WrapLabelStyle);
                foreach (var kv in m_Aliases.OrderBy(x => x.Key, StringComparer.Ordinal).ToList())
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        string bank = m_AgentToBank.TryGetValue(kv.Value, out var b) ? b : "?";
                        GUILayout.Label($"　　<b>{kv.Key}</b> → agent <b>{kv.Value}</b> → bank <b>{bank}</b>",
                            WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(460)));
                        if (GUILayout.Button("刪除", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.7f, 0.7f)), GUILayout.ExpandWidth(false)))
                            DoRemoveAlias(kv.Key);
                        GUILayout.FlexibleSpace();
                    }
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("＋ 別名", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                    m_NewAliasFromDraft = GUILayout.TextField(m_NewAliasFromDraft ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("→ agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(55)));
                    if (m_AgentKeys.Count > 0)
                    {
                        int cur = Mathf.Max(0, m_AgentKeys.IndexOf(m_NewAliasToDraft));
                        int ni = UCL_GUILayout.PopupSearchCache(cur, m_AgentKeys, m_Dic, "AliasAgentPicker",
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                        if (ni >= 0 && ni < m_AgentKeys.Count) m_NewAliasToDraft = m_AgentKeys[ni];
                    }
                    if (GUILayout.Button("新增", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false)))
                        DoAddAlias();
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Space(6);

                // ---- agent_banks：路由表全表（列出／刪除）----
                // 這張表此前只能新增/覆蓋、看不到全貌 —— 而看不到的表，錯了也沒有人會發現。
                GUILayout.Label($"<b>🗺 agent → bank 路由表（agent_banks）</b> {m_AgentToBank.Count} 筆"
                    + "：決定**某 agent 麾下所有 persona 的薪水流向哪個帳戶**。"
                    + "<color=#ffcc66>改這裡不會動到任何一分現有的錢，但會改變下一筆錢的去向。</color>", WrapLabelStyle);
                foreach (var kv in m_AgentToBank.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // 綁在這個 agent 上的 persona 數：改/刪這一列會直接影響這些人。
                        int boundCnt = 0;
                        foreach (var pa in m_PersonaToAgent) if (pa.Value == kv.Key) boundCnt++;
                        bool identity = kv.Key == kv.Value;   // 自映射＝agent id 與帳號 id 已合一
                        bool closedTgt = UCL_TreasuryAccountResolver.IsClosed(kv.Value, out _);
                        // 按鈕放 Label 前面（守則 L2，同下方已銷戶清單的血證）。
                        bool armed = IsAgentBankRemoveArmed(kv.Key);
                        if (GUILayout.Button(armed ? "⚠ 確認刪除" : "刪除",
                                UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.55f, 0.4f) : new Color(1f, 0.7f, 0.7f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            if (armed) DoRemoveAgentBank(kv.Key);
                            else
                            {
                                m_AgentBankRemoveArmed = kv.Key;
                                m_AgentBankRemoveArmedAt = EditorApplication.timeSinceStartup;
                                SetResult($"⚠ 待確認：刪除映射 `{kv.Key}` → `{kv.Value}`（{boundCnt} 位 persona 受影響）"
                                    + " —— 刪除後這些人的薪水會走 fallback，5 秒內再按一次生效。");
                            }
                        }
                        GUILayout.Label($"　<b>{kv.Key}</b> → <b>{kv.Value}</b>"
                            + $"　餘額 {SafeBalance(kv.Value)}　persona {boundCnt} 位"
                            + (identity ? "　<color=#88dd88>◎ 已合一</color>" : "")
                            + (closedTgt ? "　<color=red>⚠ 目標已銷戶</color>" : ""),
                            WrapLabelStyle);
                        GUILayout.FlexibleSpace();
                    }
                }
                GUILayout.Label("　（要新增／改對應請用下方「🏦 開戶」；刪除只拿掉路由，"
                    + "**帳戶與裡面的錢都不會消失**，只是不再有 agent 指向它）", WrapLabelStyle);

                GUILayout.Space(6);

                // ---- system_accounts：把一個帳號本身認定為終點（不再往下解析）----
                GUILayout.Label($"<b>🏛 系統／舊世代帳號（system_accounts）</b> {m_SystemAccounts.Count} 筆"
                    + "：這些名字**就是帳戶本身**，解析到此為止。舊世代 bank 補登記在這裡 ——"
                    + "登記進 agent_banks 會把該 agent 現行薪水導去舊帳戶（2026-08-04 拍板）。", WrapLabelStyle);
                foreach (var acc in m_SystemAccounts.OrderBy(x => x, StringComparer.Ordinal))
                    GUILayout.Label($"　　• <b>{acc}</b>　餘額 {SafeBalance(acc)}", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"把目前選定的 bank <b>{SelectedBank ?? "(未選)"}</b> 補登記為系統帳號", WrapLabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(360)));
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(SelectedBank)
                                                       || !m_OrphanBankIds.Contains(SelectedBank)))
                    {
                        if (GUILayout.Button("🏛 補登記", UCL_GUIStyle.GetButtonStyle(new Color(0.8f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                            DoRegisterSystemAccount(SelectedBank);
                    }
                    GUILayout.Label("（只對孤兒帳戶開放 —— 已登記的不必再登記）", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Space(6);

                // ---- closed_accounts：復戶 ----
                var closed = m_CacheClosedAccounts;   // 同上：走快取，不在 Draw* 每幀重建
                GUILayout.Label($"<b>🚫 已銷戶（closed_accounts）</b> {closed.Count} 筆：拒收任何金流；歷史保留。", WrapLabelStyle);
                foreach (var kv in closed.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // 已銷戶帳號的餘額應恆為 0（銷戶前置條件）。不為 0 = 閘門被繞過或事後有錢進來，
                        // 那是必須當場看見的異常，不是一個可以安靜列在名單裡的狀態。
                        int cbal = SafeGetTokenBalance(kv.Key);
                        // 🩸 按鈕放 Label **前面**（建頁守則 L2）—— 這一列的說明文字是 closed_accounts
                        //   的整段理由（「幽靈帳號銷戶 2026-08-14（…）。ledger 歷史保留，不再接受任何金流。」），
                        //   排在它後面時按鈕會被推到 560px 之外**跑出可見範圍**：
                        //   Tim 2026-08-20 在畫面上找不到「↩ 復戶」，而版面爆掉不會編譯錯、也不會 log error。
                        if (GUILayout.Button("↩ 復戶", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            DoReopenAccount(kv.Key);
                        GUILayout.Label($"　• <b>{kv.Key}</b>"
                            + (cbal != 0 ? $"　<color=red>⚠ 餘額 {cbal} ≠ 0，請查金流來源</color>" : "")
                            + $"　{kv.Value}", WrapLabelStyle);
                        GUILayout.FlexibleSpace();
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label($"<b>🏦 agent → bank（agent_banks）</b> {m_AgentToBank.Count} 筆 —— <b>唯讀</b>。"
                    + "寫入端有兩處：本頁「🏦 帳號操作 → 開戶」與「Persona & Agent 管理」頁的開 agent。"
                    + "這裡不開第三個入口，因為同一張表三處可寫的漂移不會報錯。", WrapLabelStyle);
                foreach (var kv in m_AgentToBank.OrderBy(x => x.Key, StringComparer.Ordinal))
                    GUILayout.Label($"　　• <b>{kv.Key}</b> → <b>{kv.Value}</b>　餘額 {SafeBalance(kv.Value)}", WrapLabelStyle);
            }
        }

        // 新增別名 —— 寫 registry 前先擋掉三種會讓規則失效或打架的輸入
        void DoAddAlias()
        {
            string from = (m_NewAliasFromDraft ?? "").Trim();
            string to = (m_NewAliasToDraft ?? "").Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            { SetResult("❌ 新增別名失敗：來源字串與目標 agent 都必填"); return; }
            if (!m_AgentToBank.ContainsKey(to))
            { SetResult($"❌ 新增別名失敗：`{to}` 不在 agent_banks 裡 —— 先開戶，別讓別名指向不存在的 agent"); return; }

            // 已經解析得出來的字串不需要別名。硬加只會製造一條「看起來在生效、其實從沒被用到」的規則，
            // 而那種規則日後會被當成事實引用。
            var cur = UCL_TreasuryAccountResolver.Resolve(from);
            if (!cur.IsUnresolved)
            { SetResult($"⚠ 未新增：`{from}` 目前已能解析（{cur.Trace}）—— 不需要別名。要改變它的去向請改對應的表。"); return; }

            try
            {
                WriteRegistry(reg =>
                {
                    if (!reg.Contains("agent_aliases")) reg["agent_aliases"] = JsonData.ParseJson("{}");
                    reg["agent_aliases"][from.ToLowerInvariant()] = to;   // key 一律小寫（解析器以小寫比對）
                });
                UCL_TreasuryAccountResolver.Invalidate();
                var after = UCL_TreasuryAccountResolver.Resolve(from);
                SetResult($"✅ 已新增別名：`{from}` → agent `{to}`。試算結果：{after}");
                m_NewAliasFromDraft = "";
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 新增別名失敗：{ex.Message}"); }
        }

        bool IsAgentBankRemoveArmed(string agent) => m_AgentBankRemoveArmed == agent
            && (EditorApplication.timeSinceStartup - m_AgentBankRemoveArmedAt) <= APPROVE_ARM_WINDOW_SEC;

        // 刪一條路由。**不動帳戶、不動錢** —— 帳戶與餘額都留著，只是不再有 agent 指向它。
        // 為什麼要留一條後路而不是禁止刪：合一完成後整張表會變成恆等映射並退場，
        // 沒有刪除就只能手改 JSON —— 而手改 JSON 正是這一族坑的起點（2026-08-04 補登記同形）。
        void DoRemoveAgentBank(string agent)
        {
            m_AgentBankRemoveArmed = null;
            if (string.IsNullOrEmpty(agent)) { SetResult("❌ 刪除失敗：agent 為空"); return; }
            try
            {
                string oldBank = m_AgentToBank.TryGetValue(agent, out var ob) ? ob : "?";
                var affected = new List<string>();
                foreach (var pa in m_PersonaToAgent) if (pa.Value == agent) affected.Add(pa.Key);
                WriteRegistry(reg =>
                {
                    if (reg.Contains("agent_banks")) reg["agent_banks"].Remove(agent);
                });
                UCL_TreasuryAccountResolver.Invalidate();
                // 刪完立刻試算：讓「刪掉之後這些人會流去哪」當場可見，而不是等下次發薪才知道。
                var after = UCL_TreasuryAccountResolver.Resolve(agent);
                SetResult($"🗑 已刪除路由 `{agent}` → `{oldBank}`（帳戶與 {SafeBalance(oldBank)} 餘額原地不動）。"
                    + $"　現在 `{agent}` 的解析結果：{after}"
                    + (affected.Count > 0
                        ? $"　⚠ 受影響 persona {affected.Count} 位：{string.Join("、", affected)}"
                        : "　（沒有 persona 綁在它上面）"));
                NotifyTavern(
                    $"🗺 **銀行後台｜刪除路由**\n"
                    + $"`{agent}` → `{oldBank}` 已從 agent_banks 移除（帳戶與餘額**原地不動**，只是不再有 agent 指向它）。\n"
                    + (affected.Count > 0 ? $"⚠ 受影響 persona {affected.Count} 位：{string.Join("、", affected)}\n" : "")
                    + $"📝 解析現況：{after}",
                    "bank-route-remove");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 刪除路由失敗：{ex.Message}"); }
        }

        void DoRemoveAlias(string aliasKey)
        {
            try
            {
                WriteRegistry(reg =>
                {
                    if (reg.Contains("agent_aliases")) reg["agent_aliases"].Remove(aliasKey);
                });
                UCL_TreasuryAccountResolver.Invalidate();
                var after = UCL_TreasuryAccountResolver.Resolve(aliasKey);
                SetResult($"🗑 已刪除別名 `{aliasKey}`。該字串現在的解析結果：{after}"
                    + (after.IsUnresolved ? "　⚠ 往後打進這個名字的錢會變成孤兒。" : ""));
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 刪除別名失敗：{ex.Message}"); }
        }

        // 補登記系統帳號 —— 把孤兒認定為「它自己就是終點」，錢原地合法化，不搬動任何一分
        void DoRegisterSystemAccount(string account)
        {
            if (string.IsNullOrEmpty(account)) { SetResult("❌ 補登記失敗：未選 bank"); return; }
            if (UCL_TreasuryAccountResolver.IsCanonicalAccount(account))
            { SetResult($"⚠ `{account}` 已經是正式帳號，未重複登記"); return; }
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
                int bal = SafeGetTokenBalance(account);
                WriteRegistry(reg =>
                {
                    if (!reg.Contains("system_accounts")) reg["system_accounts"] = JsonData.ParseJson("{}");
                    reg["system_accounts"][account] =
                        $"{stamp} 由銀行後台補登記（補登記當下餘額 {bal}）。此名稱即帳戶本身，解析到此為止；"
                        + "刻意不進 agent_banks —— 那會把對應 agent 的現行薪水導向本帳戶。";
                });
                UCL_TreasuryAccountResolver.Invalidate();
                SetResult($"🏛 已補登記系統帳號 `{account}`（餘額 {bal} 原地不動）—— 它不再是孤兒，"
                    + "往後打進這個名字的錢合法留在這裡。");
                NotifyTavern(
                    $"🏛 **銀行後台｜補登記系統帳號**\n" +
                    $"帳號 **{account}**（餘額 **{bal}**）補登記為 system_account —— 它從「孤兒」變成「終點帳號」。\n" +
                    $"📝 說明：這是**承認**而非搬錢，一分錢都沒動。刻意不寫進 agent_banks：那會把該 agent 的現行薪水導來這個舊帳戶。",
                    "bank-system-account");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 補登記失敗：{ex.Message}"); }
        }

        void DoReopenAccount(string account)
        {
            try
            {
                WriteRegistry(reg =>
                {
                    if (reg.Contains(UCL_TreasuryAccountResolver.ClosedAccountsKey))
                        reg[UCL_TreasuryAccountResolver.ClosedAccountsKey].Remove(account);
                });
                UCL_TreasuryAccountResolver.Invalidate();
                SetResult($"↩ 已復戶 `{account}` —— 它重新接受金流（並回到孤兒清單，除非已另行登記）。");
                LoadData();
            }
            catch (Exception ex) { SetResult($"❌ 復戶失敗：{ex.Message}"); }
        }

        // ===========================================================
        // 區塊：token 操作 — 開戶 / 打款 / 轉帳
        // 數值影響：全部走 UCL_TreasuryLedger，caller="system" 繞帳戶隔離（後台代操作）；金額正整數驗證。
        // ===========================================================
        void DrawTokenOpsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                if (!FoldHeader("BankTokenOpsFold", "<b>🏦 帳號操作</b>", "")) return;
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
                if (!FoldHeader("BankVoucherFold", "<b>🎫 券</b>", "")) return;
                GUILayout.Label("<b>🎫 券操作</b>（綁 persona；上方選定 persona = <b>" + (SelectedPersona ?? "(未選)") + "</b>）", WrapLabelStyle);

                bool hasPersona = !string.IsNullOrEmpty(SelectedPersona);

                // ---- 共用說明欄（繪圖券／酒館券發放共用；仿打款，發券時同步進酒館通知的 📌 本次備註）----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("說明", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_VoucherDescDraft = GUILayout.TextField(m_VoucherDescDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                // ---- 兩種券的數量欄與整合發放按鈕 ----
                // 區塊職責：保留各券獨立數量輸入，但收束成單一操作與單一酒館公告。
                // 物理意義：繪圖券與酒館券仍各自走自己的 canonical ledger；UI 合併不會混淆資產所有權或 history。
                // 數值影響：填 0 的券種不寫帳；兩欄皆為 0 時不允許發放，避免產生空操作／空公告。
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"🎨 繪圖券: 永久 <b>{(hasPersona ? m_CacheCanvasBal.ToString() : "-")}</b>"
                        + (hasPersona && m_CacheCanvasExpiring > 0 ? $" ＋限時 <b>{m_CacheCanvasExpiring}</b>" : ""),
                        WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(230)));
                    GUILayout.Label("發放", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_CanvasGrantAmountDraft = GUILayout.TextField(m_CanvasGrantAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    // 期間限定券（Tim 2026-08-18）：用「幾分鐘後到期」而不是要人手打 ISO 時戳 ——
                    // 手打時戳會打錯，而打錯的後果是發出一批**已經過期**或**永遠不過期**的券，
                    // 兩者都不會報錯。0 ＝ 永久券（行為與改動前逐值相同）。
                    GUILayout.Label("期限(分, 0=永久)", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    m_CanvasExpireMinutesDraft = GUILayout.TextField(m_CanvasExpireMinutesDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"🍺 酒館券 餘額: <b>{(m_CacheTavernBal < 0 ? "-" : m_CacheTavernBal.ToString())}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("發放", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    m_TavernGrantAmountDraft = GUILayout.TextField(m_TavernGrantAmountDraft ?? "0", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                }
                using (new EditorGUI.DisabledScope(!hasPersona))
                {
                    if (GUILayout.Button("一次發放券", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f))))
                    {
                        DoGrantVouchers();
                    }
                }

                        
                GUILayout.Label("  兩種券各自走 canonical C# ledger、共用一次公告；數量填 0 即略過該券種。", WrapLabelStyle);
            }
        }

        // ===========================================================
        // ===========================================================
        // 區塊：🪙 區域（貨幣）ID —— Tim 2026-08-20「銀行 ID 要在 UCL_BankAdminPage 可以編輯」
        // 物理意義：本專案的區域 ID＝`letters/<persona>/bank/<ID>.md` 的**檔名**。
        //          persona 在各區域用哪個帳號（＝agent id）存在它自己的 letters 底下、一區一檔。
        //          🩸 一區一檔是硬需求：persona 的 letters 是同一個 git repo 被多個專案掛著
        //            （實測 LY 與 D:/Unity/Bar 的 letters/kiara root commit 與 HEAD 相同）
        //            ⇒ 存單一值的檔會被兩個專案互相覆寫，症狀是「另一個專案的帳號」，
        //            一個完全合法的字串，沒有任何一層會出聲。
        // 數值影響：改這個 ID **不會**自動改名既有的綁定檔 ⇒ 生效瞬間全體視為未綁定
        //          （落央行＋ErrorLog）。所以走二段確認，且提示裡明講要接著改名。
        // 邊界：**這裡不自動改名 letters 底下的檔** —— 那是跨 repo 的批次寫入（7 位 persona 各自
        //      獨立 git repo），該由遷移流程做並逐位驗，不該掛在一個後台按鈕的副作用裡。
        // ===========================================================
        void DrawCurrencyPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                string cur = UCL_CentralBankSettings.CurrencyId;
                if (!FoldHeader("BankCurrencyFold", "<b>🪙 區域（貨幣）ID</b>", cur)) return;

                GUILayout.Label($"本專案的區域 ID：<b>{cur}</b>（未設定時的預設 <b>{UCL_CentralBankSettings.DefaultCurrencyId}</b>）", WrapLabelStyle);
                GUILayout.Label("  persona 在本區使用的帳號（＝agent id）存在 <b>letters/&lt;persona&gt;/bank/&lt;本 ID&gt;.md</b> —— <b>一區一檔</b>。同一份 letters 被多個專案掛著，鍵不同才不會互相覆寫。", WrapLabelStyle);

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("區域 ID", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_CurrencyDraft = GUILayout.TextField(m_CurrencyDraft ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    bool armed = IsCurrencyArmed();
                    if (GUILayout.Button(armed ? "⚠ 確認變更" : "💾 儲存區域 ID",
                            UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.65f, 0.4f) : new Color(0.5f, 1f, 0.5f)),
                            GUILayout.ExpandWidth(false)))
                        DoSaveCurrencyId();
                    if (GUILayout.Button("↩ 重讀", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    { m_CurrencyDraft = UCL_CentralBankSettings.CurrencyId; m_CurrencyArmed = false; }

                }
                GUILayout.Label("  ⚠ 改這個 ID 會<b>自動把全體 persona 的綁定從舊區搬到新區</b>（複製 → 翻設定 → 刪舊檔，每段都有審計）。<b>若有人在新區已經有不同的綁定 ⇒ 整批中止、ID 不變</b>（那狀況要避免，不是要挑一個）。二段確認 —— 按一次待確認，5 秒內再按才生效。", WrapLabelStyle);
                GUILayout.Label("  值落 <b>AgentCommands/Treasury/bank_settings.json</b> 的 <b>currency_id</b>，Python 端讀同一份。", WrapLabelStyle);
            }
        }

        bool IsCurrencyArmed() => m_CurrencyArmed
            && (EditorApplication.timeSinceStartup - m_CurrencyArmedAt) <= APPROVE_ARM_WINDOW_SEC;

        void DoSaveCurrencyId()
        {
            string v = (m_CurrencyDraft ?? "").Trim();
            if (!UCL_CentralBankSettings.IsValidCurrencyId(v))
            {
                m_CurrencyArmed = false;
                SetResult($"❌ 區域 ID 不合法（要能當檔名：不可空白、不可含路徑分隔或檔名非法字元）：'{m_CurrencyDraft}'");
                return;
            }
            string old = UCL_CentralBankSettings.CurrencyId;
            if (v == old) { m_CurrencyArmed = false; SetResult($"ℹ 區域 ID 已經是 `{v}`，不需變更"); return; }

            if (!IsCurrencyArmed())
            {
                m_CurrencyArmed = true;
                m_CurrencyArmedAt = EditorApplication.timeSinceStartup;
                // 待確認時先跑一次**預檢**（dry run）—— 把「按下去會發生什麼」講在按之前，
                // 而不是按完才發現有人衝突。這是本頁唯一會在 arm 階段做 IO 的地方，值得：
                // 衝突清單就是使用者要拿去處理的東西。
                string aPre = UCL_PersonaProfile.CopyBankRegionAll(old, v,
                    "Tim@BankAdminPage", $"預檢：區域 ID {old} → {v}", true,
                    out int aPc, out int aPs, out int aPconf, out int aPf);
                Debug.Log(aPre);
                m_CurrencyArmed = true;
                m_CurrencyArmedAt = EditorApplication.timeSinceStartup;
                if (aPconf > 0 || aPf > 0)
                {
                    m_CurrencyArmed = false;
                    SetResult($"⛔ 不能改：`{old}` → `{v}` 有 **{aPconf} 筆衝突**（新區已有不同綁定）"
                        + $"{(aPf > 0 ? $"、{aPf} 筆預檢失敗" : "")} —— **ID 未變更**。"
                        + "清單見 Console；先把那幾位處理掉（改名或刪掉新區那個檔）再回來。");
                    return;
                }
                SetResult($"⏳ 待確認：區域 ID `{old}` → `{v}`。預檢：會搬 **{aPc}** 位、跳過 {aPs} 位、衝突 0。"
                    + "確認後自動執行「複製到新區 → 翻設定 → 刪舊區」三段（每段有審計）。5 秒內再按一次生效。");
                return;
            }

            m_CurrencyArmed = false;
            string aActor = "Tim@BankAdminPage";
            string aReason = $"區域 ID 改名 {old} → {v}（後台自動換區重綁）";

            // ── 段① 複製到新區（舊檔還在 ⇒ 此刻兩邊都有，而新區還沒生效 ⇒ 安全的中間狀態）
            string aCopyRep = UCL_PersonaProfile.CopyBankRegionAll(old, v, aActor, aReason, false,
                out int aCopied, out int aSkipped, out int aConflicts, out int aFailed);
            Debug.Log(aCopyRep);
            if (aConflicts > 0 || aFailed > 0)
            {
                // **ID 不變** —— 寧可停在舊區完整可用，也不要翻了設定卻只搬了一半。
                SetResult($"⛔ 換區中止：衝突 {aConflicts} 筆、失敗 {aFailed} 筆 —— **ID 仍是 `{old}`**，"
                    + "已複製的檔留著（新區未生效，不影響現況）。清單見 Console。");
                return;
            }

            // ── 段② 翻設定（最後才動的那一格）
            UCL_CentralBankSettings.CurrencyId = v;
            // 印 ✓ 不算數，讀回來才算：setter 對不合法值是「出聲後不寫」，
            // 只看按鈕有沒有被按會把「拒寫」讀成「已存」。
            string now = UCL_CentralBankSettings.CurrencyId;
            if (now != v)
            {
                SetResult($"❌ 寫入後讀回不符（期望 `{v}`、實際 `{now}`）—— **ID 未生效**。"
                    + $"新區的綁定檔已寫好 {aCopied} 份（無害，未生效）；原因看 Console。");
                return;
            }

            // ── 段③ 刪舊區（失敗不致命：殘留的舊檔對別人只是「另一個區域的檔」）
            string aDelRep = UCL_PersonaProfile.DeleteBankRegionAll(old, aActor, aReason,
                out int aDeleted, out int aDelFailed);
            Debug.Log(aDelRep);

            m_CurrencyDraft = now;
            SetResult($"✅ 區域 ID：`{old}` → `{now}`；自動換區重綁完成 —— "
                + $"搬 {aCopied} 位、跳過 {aSkipped} 位、刪舊檔 {aDeleted} 份"
                + $"{(aDelFailed > 0 ? $"（{aDelFailed} 份刪除失敗 —— 殘留舊檔，不致命，見 Console）" : "")}。"
                + "⚠ 這些檔要 commit（自動提交頁的 bank/ 群）。");
            Debug.Log($"[BankAdmin] currency_id {old} → {now}；copied={aCopied} skipped={aSkipped} deleted={aDeleted} delFailed={aDelFailed}");
        }

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
                if (!FoldHeader("BankCentralFold", "<b>🏦 央行 / 政策參數</b>", "")) return;
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
            // 區域 ID 一併回讀 —— 它不是政策參數，但同樣「顯示的必須是落盤值」；
            // 不回讀的話 Refresh 之後畫面留著上次打到一半的字，而那看起來像已生效。
            m_CurrencyDraft = UCL_CentralBankSettings.CurrencyId;
            m_CurrencyArmed = false;
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

            // 覆蓋既有映射 ＝ 改薪水去向，走二段確認（純新增不擋 —— 新增不會改變任何現有的人）。
            // 🩸 此前這裡一按就生效，只在事後的結果字串補一句「（覆蓋既有映射）」——
            //    而那句話出現時，路由已經改完了。閘門要長在動作之前，不是結果訊息裡。
            bool existedPre = m_AgentToBank.TryGetValue(agent, out var prevBank);
            if (existedPre && prevBank != bank)
            {
                bool armed = m_OpenAccountArmed
                    && (EditorApplication.timeSinceStartup - m_OpenAccountArmedAt) <= APPROVE_ARM_WINDOW_SEC;
                if (!armed)
                {
                    m_OpenAccountArmed = true;
                    m_OpenAccountArmedAt = EditorApplication.timeSinceStartup;
                    int affected = 0;
                    foreach (var pa in m_PersonaToAgent) if (pa.Value == agent) affected++;
                    SetResult($"⚠ 待確認：`{agent}` 的路由要從 `{prevBank}`（餘額 {SafeBalance(prevBank)}）"
                        + $"改成 `{bank}`（餘額 {SafeBalance(bank)}）。"
                        + $"　**{affected} 位 persona 的下一筆薪水會改流向**。"
                        + "　現有的錢不會搬動。5 秒內再按一次生效。");
                    return;
                }
            }
            m_OpenAccountArmed = false;

            try
            {
                // 寫 registry（agent_banks 已有同 agent → 只更新 bank 映射；提示避免誤覆蓋）
                bool existed = m_AgentToBank.ContainsKey(agent);
                WriteRegistry(reg =>
                {
                    if (!reg.Contains("agent_banks")) reg["agent_banks"] = JsonData.ParseJson("{}");
                    reg["agent_banks"][agent] = bank;
                });
                // registry 剛變 → 讓帳號解析器立刻看到新戶，否則下一行的種子 credit
                // 會把這個剛開的 bank 判成「查無對應」而警告成孤兒。
                UCL_TreasuryAccountResolver.Invalidate();

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
                // 區塊職責：讓 token 打款公告能沿用目前 persona 下拉，直接通知實際使用者而非只顯示 bank。
                // 物理意義：token 帳本仍以 bank 為主體；persona mention 只是酒館通知的收件人定位，不改 ledger 歸屬。
                // 數值影響：未選 persona 時維持舊格式；已選時多一個 @mention，讓 ChatTavern inbox／Discord 提醒自動命中。
                string personaMention = string.IsNullOrEmpty(SelectedPersona) ? "" : $" @{SelectedPersona}";
                NotifyTavern(
                    $"💵 **銀行後台｜{(drawFromCB ? "打款（央行撥出）" : "注資央行（增發）")}**\n" +
                    $"bank **{bank}**{personaMention} 入帳 +{amount} tavern_token（來源 {sourceKind}），餘額 {e.balance_before} → **{e.balance_after}**。\n" +
                    (drawFromCB
                        ? $"🏦 由 **{centralBank}** 撥出 -{amount}，公庫餘額 → **{SafeBalance(centralBank)}**。\n"
                        : $"🆕 本筆是**注資央行**（唯一的合法增發入口）—— 貨幣總量增加 {amount}。\n") +
                    $"📝 說明：把 token 發進某帳戶（薪酬／績效獎金／Tim grant）。2026-08-01 起獎金由央行撥款，公庫不足即拒發。\n" +
                    $"📌 本次備註：{desc}",
                    "bank-deposit");
                // 績效獎金 / 薪酬 / Tim grant 都走這個入口 —— 收款人不在線時，掛號信是唯一會被讀到的通道
                NotifyMail(SelectedPersona,
                    $"入帳通知 — +{amount} tavern_token（{sourceKind}）",
                    $"銀行後台打款：bank `{bank}` 入帳 **+{amount} tavern_token**。\n\n" +
                    $"- **來源**：{sourceKind}\n" +
                    $"- **餘額**：{e.balance_before} → **{e.balance_after}**\n" +
                    (drawFromCB
                        ? $"- **撥款來源**：央行 `{centralBank}`（公庫 → {SafeBalance(centralBank)}）\n"
                        : "- **撥款來源**：注資央行（本筆為合法增發）\n") +
                    $"\n**本次備註**：{desc}\n" +
                    "\n---\n\n確認讀過後跑 `registered_mail.py ack --persona <你>` 除名。",
                    "bank_admin_deposit");
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

            // ⚠ 本頁的轉帳三處一律 resolveAccount:false ——
            //   from / to 是從**既有帳號下拉**選出來的（清單本身就含孤兒帳戶，那是歸戶的操作對象）。
            //   判準：「從既有帳號清單選出來的」＝認字面；「從身分推導出來的」＝歸一。
            //   若讓歸一介入，選 `summit` 會變成扣 `zeta` 的錢，而畫面顯示轉帳成功。
            // Step 1: Debit from（不足 / 隔離違規會 throw；caller=system 繞隔離）
            TreasuryLedgerEntry debitEntry;
            try { debitEntry = UCL_TreasuryLedger.Debit(from, amount, "trade", txId, desc, "system", txId, idempotencyKey: null, resolveAccount: false); }
            catch (Exception ex) { SetResult($"❌ 轉帳 Debit 失敗：{ex.Message}"); return; }

            // Step 2: Credit to（罕見失敗 → rollback 退錢給 from）
            try { UCL_TreasuryLedger.Credit(to, amount, "trade", txId, desc, "system", txId, idempotencyKey: null, resolveAccount: false); }
            catch (Exception ex)
            {
                try
                {
                    UCL_TreasuryLedger.Credit(from, amount, "transfer_rollback", txId + "|rollback",
                        "Rollback failed transfer: " + ex.Message, "system", txId + "_rollback",
                        idempotencyKey: null, resolveAccount: false);
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

        // 區塊職責：一次性發放繪圖券與酒館券，並以同一則通知告知選定 persona。
        // 物理意義：兩種券仍分別委派其 canonical ledger；這個方法只收束 UI 操作、驗證與對外公告，避免兩則通知漂移。
        // 數值影響：任一數量為 0 即不寫對應 ledger；兩者皆為 0 時零寫入、零公告；任一非零則各 append 一筆 history。
        void DoGrantVouchers()
        {
            string persona = SelectedPersona;
            if (string.IsNullOrEmpty(persona)) { SetResult("❌ 發券失敗：未選 persona"); return; }
            if (!int.TryParse((m_CanvasGrantAmountDraft ?? "0").Trim(), out int canvasAmount) || canvasAmount < 0)
            { SetResult($"❌ 發券失敗：繪圖券需為非負整數（收到 '{m_CanvasGrantAmountDraft}'）"); return; }
            if (!int.TryParse((m_TavernGrantAmountDraft ?? "0").Trim(), out int tavernAmount) || tavernAmount < 0)
            { SetResult($"❌ 發券失敗：酒館券需為非負整數（收到 '{m_TavernGrantAmountDraft}'）"); return; }
            if (canvasAmount == 0 && tavernAmount == 0) { SetResult("❌ 發券失敗：至少填一種券的大於 0 數量"); return; }

            string bank = tavernAmount > 0 ? ResolvePersonaToBank(persona) : "";
            if (tavernAmount > 0 && string.IsNullOrEmpty(bank))
            { SetResult($"❌ 發券失敗：persona '{persona}' 無法解析 bank，酒館券不能 mint；繪圖券亦未寫入。"); return; }

            string desc = string.IsNullOrEmpty(m_VoucherDescDraft) ? "後台發券（BankAdminPage）" : m_VoucherDescDraft.Trim();
            try
            {
                int canvasBefore = 0, canvasAfter = 0, tavernBefore = 0, tavernAfter = 0;
                if (canvasAmount > 0)
                {
                    // 期限欄 → ISO 到期時刻。解析不出來或 <=0 ⇒ 空字串 ＝ 永久券（**不猜**：
                    // 把打錯的字當成某個天數，等於替發券的人決定了一批券的壽命）。
                    string aExpiresAt = "";
                    if (int.TryParse((m_CanvasExpireMinutesDraft ?? "0").Trim(), out int aMins) && aMins > 0)
                    {
                        aExpiresAt = DateTime.UtcNow.AddMinutes(aMins)
                            .ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "Z";
                    }
                    (canvasBefore, canvasAfter) = UCL_CanvasVoucherLedger.Grant(
                        persona, canvasAmount, "admin_grant", desc, aExpiresAt);
                }
                if (tavernAmount > 0)
                    (tavernBefore, tavernAfter) = UCL_TavernVoucherLedger.Grant(bank, persona, tavernAmount, "admin_grant", desc);

                var summary = new List<string>();
                var announcement = new StringBuilder($"🎫 **銀行後台｜發券** @{persona}\n");
                if (canvasAmount > 0)
                {
                    summary.Add($"繪圖券 +{canvasAmount}（{canvasBefore} → {canvasAfter}）");
                    announcement.AppendLine($"🎨 繪圖券 +{canvasAmount}，餘額 {canvasBefore} → **{canvasAfter}**。");
                }
                if (tavernAmount > 0)
                {
                    summary.Add($"酒館券 +{tavernAmount}（{tavernBefore} → {tavernAfter}）");
                    announcement.AppendLine($"🍺 酒館券／自由時間券 +{tavernAmount}（bank {bank}），餘額 {tavernBefore} → **{tavernAfter}**。");
                }
                //announcement.AppendLine("📝 兩種券各自走 canonical C# ledger；填 0 的券種已略過。");
                announcement.Append($"📌 本次備註：{desc}");
                SetResult($"✅ 發券：'{persona}' {string.Join("｜", summary)}");
                Debug.Log($"[BankAdmin] 發券 {persona} canvas={canvasAmount} tavern={tavernAmount}");
                NotifyTavern(announcement.ToString(), "voucher-grant");
                NotifyMail(persona,
                    $"發券通知 — {string.Join("／", summary)}",
                    $"銀行後台發券給 @{persona}：\n\n"
                    + (canvasAmount > 0 ? $"- 🎨 **繪圖券 +{canvasAmount}**，餘額 {canvasBefore} → **{canvasAfter}**\n" : "")
                    + (tavernAmount > 0 ? $"- 🍺 **酒館券／自由時間券 +{tavernAmount}**（bank `{bank}`），餘額 {tavernBefore} → **{tavernAfter}**\n" : "")
                    + $"\n**本次備註**：{desc}\n"
                    + "\n---\n\n確認讀過後跑 `registered_mail.py ack --persona <你>` 除名。",
                    "bank_admin_voucher_grant");
                m_BalancesDirty = true;
                m_CanvasGrantAmountDraft = "0";
                m_TavernGrantAmountDraft = "0";
                m_VoucherDescDraft = "";
            }
            catch (Exception ex) { SetResult($"❌ 發券失敗（canonical ledger）：{ex.Message}"); }
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
                NotifyMail(persona,
                    $"發券通知 — 繪圖券 +{amount}",
                    $"銀行後台發放 **繪圖券 +{amount}** 給 @{persona}，餘額 {before} → **{after}**。\n\n" +
                    "繪圖券綁 persona，用於共用像素畫布（1 券 ≈ 1 像素）。\n\n" +
                    $"**本次備註**：{desc}\n" +
                    "\n---\n\n確認讀過後跑 `registered_mail.py ack --persona <你>` 除名。",
                    "bank_admin_canvas_voucher");
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
                NotifyMail(persona,
                    $"發券通知 — 酒館券 +{amount}",
                    $"銀行後台發放 **酒館券／自由時間券 +{amount}** 給 @{persona}（bank `{bank}`），" +
                    $"餘額 {before} → **{after}**。\n\n" +
                    $"**本次備註**：{desc}\n" +
                    "\n---\n\n確認讀過後跑 `registered_mail.py ack --persona <你>` 除名。",
                    "bank_admin_tavern_voucher");
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
        // 區塊職責：繪圖券的**三種**餘額（2026-08-18 券改批次制）。
        // 物理意義：Tim 拍板「查永久券數量時不自動包含限時券」—— 所以後台**分開顯示**。
        //   一個欄位混報兩種的話，看到「160」的人不知道其中幾張明天就沒了。
        int GetCanvasVoucherPermanent(string persona) => UCL_CanvasVoucherLedger.GetPermanent(persona);
        int GetCanvasVoucherExpiring(string persona) => UCL_CanvasVoucherLedger.GetExpiring(persona);

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
                bool showPay = FoldHeader("BankPayoutFold", "<b>📨 請款審批</b>",
                    m_PendingRequests.Count > 0
                        ? $"　<color=yellow><b>pending {m_PendingRequests.Count} 筆</b></color>" : "　(無待審)");
                if (!showPay) return;
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"（agent 走 <b>Cmd_Treasury op=request</b> 開單）",
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

        // ===========================================================
        // 區塊：💸 轉帳審批 —— 「從 A 帳戶轉多少到 B」的待審清單 + 手動開單
        // 物理意義：跟請款審批同形狀、不同語意。請款＝央行撥款（消耗公庫）；轉帳＝A→B（總量守恆）。
        //          分成兩張單是刻意的：審批者最需要一眼看清「這筆會不會消耗公庫」。
        // 主要用途：**歸戶** —— 把錢從孤兒 / 打錯字的帳戶搬回正主，且留下「為什麼搬」的痕跡。
        //          後台的「直接轉帳」也做得到同樣的事，但事後沒有人知道那筆為什麼被搬。
        // 數值影響：pending 期間零影響；核准才 Debit→Credit（失敗回滾）。二段確認防連點。
        // ===========================================================
        void DrawTransferRequestPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show = FoldHeader("BankTransferReqFold", "<b>💸 轉帳審批</b>",
                    m_PendingTransfers.Count > 0
                        ? $"　<color=yellow><b>pending {m_PendingTransfers.Count} 筆</b></color>" : "　(無待審)");
                if (!show) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("（核准 = 從 A 扣、給 B，總量守恆，<b>不動央行</b>）", WrapLabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("🔄 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        ReloadTransferRequests();
                        SetResult($"✓ 已重新載入轉帳單（pending {m_PendingTransfers.Count} 筆）");
                    }
                    GUILayout.FlexibleSpace();
                }

                // ---- 手動開單（from = 上方 bank 下拉；to = 轉帳目標下拉）----
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label($"<b>＋ 開一張轉帳單</b>：<b>{SelectedBank ?? "(未選)"}</b> → <b>{TransferToBank ?? "(未選)"}</b>"
                        + "（用上方兩個 bank 下拉選來源與目標）", WrapLabelStyle);
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("金額", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        m_NewTransferAmountDraft = GUILayout.TextField(m_NewTransferAmountDraft, UCL_GUIStyle.TextFieldStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        GUILayout.Label("理由", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        m_NewTransferReasonDraft = GUILayout.TextField(m_NewTransferReasonDraft, UCL_GUIStyle.TextFieldStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)));
                        if (GUILayout.Button("📝 建立待審單", UCL_GUIStyle.GetButtonStyle(new Color(0.8f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                            DoCreateTransferRequest();
                    }
                }

                if (m_PendingTransfers.Count == 0)
                {
                    GUILayout.Label("（目前沒有待審轉帳單）", WrapLabelStyle);
                    return;
                }

                foreach (var req in m_PendingTransfers)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        bool armed = IsTransferArmed(req.request_id);
                        // 出款方餘額不足要在按下去之前就看得見，而不是核准失敗才知道
                        int fromBal = -1;
                        try { fromBal = UCL_TreasuryLedger.GetBalance(req.from_bank); } catch { }
                        bool enough = fromBal < 0 || fromBal >= req.amount;
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<b>{req.amount} {req.currency}</b>　<b>{req.from_bank}</b> → <b>{req.to_bank}</b>"
                                    + (enough ? "" : $"　<color=red>⚠ 出款方餘額 {fromBal} 不足</color>"),
                                WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(380)));
                            GUILayout.Label($"{req.kind}", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                            GUILayout.Label($"`{req.request_id}`  {req.requested_at}", WrapLabelStyle, GUILayout.ExpandWidth(false));
                            GUILayout.FlexibleSpace();
                        }
                        GUILayout.Label($"理由：{req.reason}", WrapLabelStyle);
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label("備註", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                            string prev = m_TransferNoteDrafts.TryGetValue(req.request_id, out var d) ? d : "";
                            m_TransferNoteDrafts[req.request_id] = GUILayout.TextField(prev, UCL_GUIStyle.TextFieldStyle,
                                GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                            GUI.enabled = enough;
                            if (GUILayout.Button(armed ? "✅ 確認轉帳" : "核准",
                                    UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.8f, 0.4f) : new Color(0.75f, 1f, 0.8f)),
                                    GUILayout.ExpandWidth(false)))
                                OnTransferApproveClicked(req);
                            GUI.enabled = true;
                            if (GUILayout.Button("駁回", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.7f, 0.7f)), GUILayout.ExpandWidth(false)))
                                OnTransferRejectClicked(req);
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        void ReloadTransferRequests()
        {
            m_PendingTransfers.Clear();
            try { m_PendingTransfers.AddRange(UCL_TreasuryTransferRequestStore.List(pendingOnly: true)); }
            catch (Exception e) { Debug.LogWarning($"[BankAdmin] 讀轉帳單失敗: {e.Message}"); }
        }

        bool IsTransferArmed(string requestId) =>
            !string.IsNullOrEmpty(requestId) && m_TransferArmedId == requestId
            && (EditorApplication.timeSinceStartup - m_TransferArmedAt) <= APPROVE_ARM_WINDOW_SEC;

        void DoCreateTransferRequest()
        {
            string from = SelectedBank, to = TransferToBank;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) { SetResult("❌ 開單失敗：from / to bank 未選"); return; }
            if (!int.TryParse((m_NewTransferAmountDraft ?? "0").Trim(), out int amount) || amount <= 0)
            { SetResult($"❌ 開單失敗：金額需為正整數（收到 '{m_NewTransferAmountDraft}'）"); return; }
            if (string.IsNullOrWhiteSpace(m_NewTransferReasonDraft)) { SetResult("❌ 開單失敗：理由必填 —— 審批者要有東西可判"); return; }
            try
            {
                var req = UCL_TreasuryTransferRequestStore.Create(
                    from, to, amount, m_NewTransferReasonDraft.Trim(),
                    kind: m_OrphanBankIds.Contains(from) ? "orphan-consolidation" : "manual_transfer",
                    requesterAgent: "BankAdminPage", requesterPersona: SelectedPersona);
                SetResult($"✅ 轉帳單已建立 `{req.request_id}`：{amount} `{from}` → `{to}`（待審，尚未動錢）");
                m_NewTransferAmountDraft = "0";
                m_NewTransferReasonDraft = "";
                ReloadTransferRequests();
            }
            catch (Exception ex) { SetResult($"❌ 開單失敗：{ex.Message}"); }
        }

        void OnTransferApproveClicked(TreasuryTransferRequest req)
        {
            if (!IsTransferArmed(req.request_id))
            {
                m_TransferArmedId = req.request_id;
                m_TransferArmedAt = EditorApplication.timeSinceStartup;
                SetResult($"⏳ 待確認：轉帳 `{req.request_id}` → {req.amount} 從 `{req.from_bank}` 到 `{req.to_bank}`"
                    + "（5 秒內再按一次「確認轉帳」生效）");
                return;
            }
            m_TransferArmedId = null;
            try
            {
                string note = m_TransferNoteDrafts.TryGetValue(req.request_id, out var n) ? n : "";
                var done = UCL_TreasuryTransferRequestStore.Approve(req.request_id, decidedBy: "Tim", note: note);
                SetResult($"✅ 已轉帳：`{done.request_id}` {done.amount} `{done.from_bank}` → `{done.to_bank}`");
                NotifyTavern(
                    $"💸 **銀行後台｜轉帳核准**\n" +
                    $"轉帳單 `{done.request_id}` 核准 —— **{done.amount} {done.currency}** 自 **{done.from_bank}** 轉入 **{done.to_bank}**。\n" +
                    $"📊 餘額：`{done.from_bank}` → **{SafeBalance(done.from_bank)}**｜`{done.to_bank}` → **{SafeBalance(done.to_bank)}**\n" +
                    $"📝 理由：{done.reason}\n" +
                    (string.IsNullOrEmpty(note) ? "" : $"📌 審批備註：{note}\n") +
                    $"🏷 分類：{done.kind}（總量守恆，未動央行）",
                    "transfer-request-approved");
                m_BalancesDirty = true;
                LoadData();          // 孤兒帳戶餘額歸零後要重掃，列表才會反映現況
                ReloadTransferRequests();
            }
            catch (Exception ex) { SetResult($"❌ 轉帳核准失敗：{ex.Message}"); }
        }

        void OnTransferRejectClicked(TreasuryTransferRequest req)
        {
            try
            {
                string note = m_TransferNoteDrafts.TryGetValue(req.request_id, out var n) ? n : "";
                var done = UCL_TreasuryTransferRequestStore.Close(
                    req.request_id, UCL_TreasuryTransferRequestStore.StatusRejected, decidedBy: "Tim", note: note);
                SetResult($"✘ 已駁回轉帳單：`{done.request_id}`（{done.amount} {done.from_bank} → {done.to_bank}）"
                    + (string.IsNullOrEmpty(note) ? "　※ 建議填備註說明為什麼" : $"　理由：{note}"));
                ReloadTransferRequests();
            }
            catch (Exception ex) { SetResult($"❌ 駁回失敗：{ex.Message}"); }
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
                // 請款者本人多半不在線 —— 酒館公告他醒來時看不到，掛號信才會端到 wake brief 上
                NotifyMail(done.requester_persona,
                    $"請款單 {done.request_id} 已核准 — +{done.amount} {done.currency}",
                    $"你的請款單 `{done.request_id}` 已由 **{done.decided_by}** 核准。\n\n" +
                    $"- **金額**：+{done.amount} {done.currency}\n" +
                    $"- **入帳 bank**：`{done.target_bank}`\n" +
                    $"- **撥款來源**：央行 `{UCL_CentralBankSettings.CentralBankAccount}`\n" +
                    $"- **核准時間**：{done.decided_at}\n" +
                    $"- **ledger entry**：`{done.ledger_entry_uuid}`\n\n" +
                    $"**原請款理由**：{done.reason}\n" +
                    (string.IsNullOrEmpty(note) ? "" : $"\n**審批備註**：{note}\n") +
                    "\n---\n\n錢已經在帳上了 —— 醒來時別再把這筆當成「待核准」。\n" +
                    "確認讀過後跑 `registered_mail.py ack --persona <你>` 除名，否則每次醒來都會再看到這封。",
                    $"payout_request_{done.request_id}");
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

        // ===========================================================
        // 區塊：進帳通知掛號信 — 每筆「有具體收款 persona」的核准 / 打款 / 發券另寄一封免費系統信
        //      （Tim 2026-08-04：「通過的請款、獎金等都額外寄一份掛號信給目標 persona，系統信件不收費」）
        // 物理意義：酒館公告是**廣播到現在** —— 收款人多半不在線，醒來時那則公告早被 catch-up 的
        //          數十筆訊息推到視線外（今早 summit 就把已核准的請款當成「待核准」報了一次，
        //          就是這條通道缺口的活體證據）。掛號信是**指名 + 定時 + 不 ack 不消失**，
        //          會出現在收件人下一次 wake brief 的 §7 最前面。兩者不重複：一個給在線的人看，
        //          一個給醒來的人看。
        // 數值影響：**零** —— 系統信 fee=0，不經 Treasury。寄信失敗只記 warning，
        //          絕不回滾已完成的金流（通知是輔助，錢是主體）。
        // 邊界：persona 為空（後台只選了 bank 沒選 persona）→ 不寄，並在 Debug 留一行 ——
        //      「沒有收件人」跟「寄失敗」是兩件事，不該混成同一句話。
        // ===========================================================
        static void NotifyMail(string persona, string subject, string body, string refId)
        {
            if (string.IsNullOrEmpty(persona))
            {
                Debug.Log($"[BankAdmin] 未指定收款 persona → 略過掛號信（{subject}）");
                return;
            }
            UCL_RegisteredMailIO.SendSystemMail(persona, subject, body, refId: refId);
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
