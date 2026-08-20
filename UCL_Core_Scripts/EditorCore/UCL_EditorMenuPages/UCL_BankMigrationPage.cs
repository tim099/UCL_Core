// 區塊職責：agent id ↔ 帳號 id 合一遷移的操作頁（Tim 2026-08-20 拍板）。
// 物理意義：系統裡「同一個身分」目前有兩個名字 —— agent id（綁定值）與 agent id（錢實際存放的 key）。
//          本頁把兩者收斂成一個。收斂方向由**成本**決定而不是由美觀決定：
//          實測 5 組待合併中，agent 名那側的帳戶餘額**全部是 0**（錢都在 bank 名那側）
//          ⇒ 預設把 agent 改名成 agent id，**零 ledger 異動**。
//          反方向（保留 agent 名）要搬 11,338 token，只有明確指定 rename 的那些帳戶才走。
// 數值影響：兩段各自獨立 ——
//          ① agent 改名：只動綁定檔與 registry.agent，**不碰 ledger、不動一分錢**
//          ② 帳戶改名（rename 欄位有值時）：**真的搬錢**，走 ledger transfer（debit + credit 同 tx_id）
//          兩段刻意不合併成一顆按鈕：一顆會動錢一顆不會，混在一起的話「按了什麼」就說不清楚。
// ⚠ 本頁的計畫計算與 UI **走同一支** BuildPlan()，且它是 static、吃字串參數 ——
//   目的是讓 Cmd_Invoke 能在不開 Editor 的情況下驗同一條路徑
//   （`kind=field` 不能寫 private draft 欄位，所以邏輯層若綁死 UI 狀態就沒有任何自動驗證手段）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UCL.Core.EditorLib.AgentCommands;            // UCL_PersonaProfile
using UCL.Core.EditorLib.AgentCommands.Treasury;   // UCL_TreasuryLedger / Resolver / CentralBankSettings
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// agent id ↔ 帳號 id 合一遷移頁。入口：銀行後台（UCL_BankAdminPage）。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Workflows/Agent_Bank_Unification_Migration_Workflow.md")]
    public class UCL_BankMigrationPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_BankMigration";
        // 非衍生頁（不需參數就有意義）⇒ 依建頁守則 §6.1 掛進 Page Picker，
        // 使用者才有「再次打開」的途徑；高頻入口另由 UCL_BankAdminPage 的按鈕提供。
        public override bool ShowInPageMenu => true;

        public static UCL_BankMigrationPage Create() => UCL_EditorPage.Create<UCL_BankMigrationPage>();

        // ==========================================================
        // 資料模型：一列 ＝ 一個 agent 的遷移計畫
        // ==========================================================
        public class MigrationRow
        {
            public string Agent;          // 現行 agent id（綁定檔與 registry.agent 存的值）
            public string CurrentBank;    // 現行帳號 id（agent_banks[Agent]，錢在這裡）
            public string RenameTo;       // rename 欄位：把「帳號」改名成這個（空＝不改名）
            public string FinalBank;      // 遷移後的最終帳號 id ＝ RenameTo 非空 ? RenameTo : CurrentBank
            public int BankBalance;       // CurrentBank 餘額
            public int TargetBalance;     // FinalBank 餘額（rename 時是目標帳戶的現有餘額）
            public List<string> Personas = new List<string>();
            public bool NeedsTransfer;    // 要不要搬錢（CurrentBank != FinalBank 且餘額 > 0）
            public bool NeedsRename;      // 要不要改 agent id（Agent != FinalBank）
            // 目標是不是**全新帳戶**（ledger 從未有過任何一筆）。
            // ⚠ 存在的理由：「餘額 0 的既有帳戶」與「還不存在的帳戶」在畫面上都顯示 `(0)`，
            //   而兩者的意思完全不同 —— 前者是有歷史的空戶，後者是這次才生出來的名字。
            //   不標的話，打錯一個字母（`FRS` 打成 `FSR`）看起來跟填對完全一樣。
            public bool TargetIsNew;
            public string Blocker;        // 非空＝這一列不可執行，理由在此
        }

        // ==========================================================
        // 區塊職責：把 renameSpec 解析成「帳號 → 新帳號」的對照表
        // 物理意義：rename 欄位掛在**帳號**上不是 agent 上 —— 因為要改名的是錢所在的那個 key。
        //          格式 `舊帳號=新帳號;舊帳號2=新帳號2`，空字串＝全部不改名。
        // ⚠ 帳號名可能含空白（`Federal Reserve System`），所以只用 `;` 與第一個 `=` 切，不 trim 掉內部空白。
        // ==========================================================
        public static Dictionary<string, string> ParseRenameSpec(string iSpec)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(iSpec)) return map;
            foreach (var seg in iSpec.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(seg)) continue;
                int eq = seg.IndexOf('=');
                if (eq <= 0) continue;
                string k = seg.Substring(0, eq).Trim();
                string v = seg.Substring(eq + 1).Trim();
                if (k.Length > 0 && v.Length > 0) map[k] = v;
            }
            return map;
        }

        // ==========================================================
        // 區塊職責：大小寫同名帳號的偵測與合併（Tim 2026-08-20 拍板）
        // 物理意義：帳號 id 的唯一性**以全小寫比對** —— 兩個只差大小寫的帳號在系統裡活不下去：
        //   `Treasury/accounts/<id>.json` 在 Windows/macOS 上會撞成同一個檔，
        //   而症狀是「查 A 拿到 B 的資料」，全程零報錯。
        //   🩸 2026-08-20 實例：`zeta`（2792、有綁定）與 `Zeta`（空戶）的資料檔互相覆蓋。
        // 數值影響：**可能搬錢**（把被併方的餘額轉到保留方）。本專案 4 組實測全部有一邊為 0，
        //   所以實際 transfer 筆數是 0 —— 但程式不假設這件事，餘額 > 0 就照樣走 ledger transfer。
        // ==========================================================
        public class CaseCollisionGroup
        {
            public string LowerKey;
            public List<string> Members = new List<string>();
            public string Keeper;                 // 保留下來的那個
            public Dictionary<string, int> Balances = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> BoundCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public int MoveAmount;                // 需要搬過去的總額
        }

        /// <summary>
        /// 偵測所有「lower 之後相同」的帳號組。純讀。
        /// </summary>
        /// <remarks>
        /// 保留者的選法（順序即優先級，全部可解釋，不用「看起來比較對」）：
        /// ① 有 persona 綁定的（那是活的身分）→ ② 系統帳戶（已登記在案）→
        /// ③ 餘額多的 → ④ ledger 筆數多的 → ⑤ 字典序（決定性 tie-break，避免每次跑結果不同）。
        /// </remarks>
        public static List<CaseCollisionGroup> DetectCaseCollisions()
        {
            var byLower = new Dictionary<string, CaseCollisionGroup>(StringComparer.Ordinal);
            foreach (var acc in AccountUniverse().Keys)
            {
                string k = acc.ToLowerInvariant();
                if (!byLower.TryGetValue(k, out var g))
                { g = new CaseCollisionGroup { LowerKey = k }; byLower[k] = g; }
                if (!g.Members.Contains(acc)) g.Members.Add(acc);
            }
            var bound = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in UCL_PersonaProfile.PoolNamesSorted())
            {
                string a = UCL_PersonaProfile.GetString(p, "agent", "").Trim();
                if (string.IsNullOrEmpty(a)) continue;
                bound.TryGetValue(a, out int c); bound[a] = c + 1;
            }
            var result = new List<CaseCollisionGroup>();
            foreach (var g in byLower.Values)
            {
                if (g.Members.Count < 2) continue;
                foreach (var m in g.Members)
                {
                    g.Balances[m] = SafeBal(m);
                    bound.TryGetValue(m, out int bc); g.BoundCounts[m] = bc;
                }
                g.Members.Sort(StringComparer.Ordinal);
                g.Keeper = g.Members
                    .OrderByDescending(m => g.BoundCounts[m])
                    .ThenByDescending(m => UCL_TreasuryAccountResolver.IsCanonicalAccount(m) ? 1 : 0)
                    .ThenByDescending(m => g.Balances[m])
                    .ThenByDescending(m => LedgerEntryCount(m))
                    .ThenBy(m => m, StringComparer.Ordinal)
                    .First();
                g.MoveAmount = g.Members.Where(m => m != g.Keeper).Sum(m => Math.Max(0, g.Balances[m]));
                result.Add(g);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.LowerKey, b.LowerKey));
            return result;
        }

        /// <summary>同名合併的試跑報告（Cmd_Invoke 可直接讀）。</summary>
        public static string CaseCollisionReport()
        {
            var groups = DetectCaseCollisions();
            var sb = new StringBuilder();
            sb.AppendLine($"# 大小寫同名帳號　{groups.Count} 組");
            if (groups.Count == 0)
            {
                sb.AppendLine("✓ 沒有任何一組 —— 帳號 id 在全小寫比對下已經是唯一的。");
                return sb.ToString();
            }
            foreach (var g in groups)
            {
                sb.AppendLine($"## 【{g.LowerKey}】保留 **`{g.Keeper}`**");
                foreach (var m in g.Members)
                {
                    string role = m == g.Keeper ? "← 保留" : "→ 併入並銷戶";
                    sb.AppendLine($"  - `{m}`　餘額 {g.Balances[m]}　persona {g.BoundCounts[m]} 位　{role}");
                }
                sb.AppendLine($"  ⇒ 需搬 {g.MoveAmount} token" + (g.MoveAmount == 0 ? "（零成本）" : ""));
            }
            sb.AppendLine();
            sb.AppendLine($"⇒ 合計需搬 {groups.Sum(x => x.MoveAmount)} token；"
                + "被併方會進 closed_accounts 並記 renamed_to。");
            return sb.ToString();
        }

        // ==========================================================
        // 區塊職責：算出完整遷移計畫（**純讀**，不寫任何東西）
        // 物理意義：這是試跑與實際執行**共用的唯一計算** —— 試跑看到的就是執行會做的。
        //          兩份各算一次的話，「試跑通過」與「執行正確」之間就沒有邏輯關係了。
        // 數值影響：零。純查表 + 讀餘額。
        // ==========================================================
        public static List<MigrationRow> BuildPlan(string iRenameSpec, string iCurrencyId)
        {
            var renames = ParseRenameSpec(iRenameSpec);
            var rows = new List<MigrationRow>();
            var meta = LoadRegistryMeta();
            // 計畫的來源要跟「現在誰是真相源」一致：
            //   · 遷移前 → `agent_banks`（那時它就是路由表）
            //   · 已合一 → **persona 的實際 agent**（agent_banks 已退出解析，留著的是過時殘留）
            // 🩸 不分模式一律讀 agent_banks 的話，遷移**跑完之後**這一頁會繼續顯示
            //   「需改名 5 組」與已經不存在的舊帳號名 —— 一個已完成的流程持續宣稱自己沒做，
            //   而那比沒有畫面更糟：它會讓人再跑一次。
            // 開關拔除後只剩一種答案：現行身分一律由 persona 的實際 agent 導出。
            var agentBanks = BuildIdentityMapFromPersonas();

            // persona → agent（用來顯示「每個 persona 遷移後會用哪個帳號」）
            var personaAgent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in UCL_PersonaProfile.PoolNamesSorted())
            {
                string a = UCL_PersonaProfile.GetString(p, "agent", "").Trim();
                if (!string.IsNullOrEmpty(a)) personaAgent[p] = a;
            }

            foreach (var kv in agentBanks.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var row = new MigrationRow { Agent = kv.Key, CurrentBank = kv.Value };
                renames.TryGetValue(row.CurrentBank, out row.RenameTo);
                row.RenameTo = row.RenameTo ?? "";
                row.FinalBank = string.IsNullOrEmpty(row.RenameTo) ? row.CurrentBank : row.RenameTo;
                row.BankBalance = SafeBal(row.CurrentBank);
                row.TargetBalance = SafeBal(row.FinalBank);
                row.NeedsTransfer = row.CurrentBank != row.FinalBank && row.BankBalance > 0;
                row.NeedsRename = row.Agent != row.FinalBank;
                // 「從未有過任何一筆 ledger」才算新帳戶 —— 用餘額判會把「花光的舊帳戶」誤判成新的。
                row.TargetIsNew = row.CurrentBank != row.FinalBank && LedgerEntryCount(row.FinalBank) == 0;
                foreach (var pa in personaAgent) if (pa.Value == row.Agent) row.Personas.Add(pa.Key);

                // 阻擋條件 —— 寧可整列不可執行，也不要跑到一半才發現。
                if (UCL_TreasuryAccountResolver.IsClosed(row.FinalBank, out string closedWhy))
                    row.Blocker = $"目標帳號 `{row.FinalBank}` 已銷戶（{closedWhy}）—— 銷戶帳號禁止金流";
                else if (row.FinalBank.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    row.Blocker = $"目標帳號 `{row.FinalBank}` 含不能當檔名的字元（綁定檔以它為內容、審計以它為 key）";
                rows.Add(row);
            }
            return rows;
        }

        // ==========================================================
        // 區塊職責：把計畫算成人可讀報告（Cmd_Invoke 直接回傳這個字串）
        // 物理意義：**UI 與 CLI 看到的是同一份文字** —— 不做兩套呈現，
        //          否則「我在 CLI 驗過了」證明不了使用者在畫面上看到什麼。
        // ==========================================================
        /// <summary>
        /// 預設情境（不指定任何 rename）的試跑報告。
        /// ⚠ 存在理由不只是方便：`Cmd_Invoke` 的 `args=` 給空字串會被判成「沒給值」
        /// （實測：`arg[0] 'iRenameSpec' has no default and no value provided`），
        /// 所以「不 rename」這個最常用的情境**必須有一個無參入口**，否則 CLI 驗不到它。
        /// </summary>
        public static string BuildPlanReport() => BuildPlanReport("");

        public static string BuildPlanReport(string iRenameSpec)
        {
            string currency = UCL_CentralBankSettings.CurrencyId;
            var rows = BuildPlan(iRenameSpec, currency);
            var sb = new StringBuilder();
            sb.AppendLine($"# agent↔帳號 合一遷移試跑　currency={currency}　agent {rows.Count} 組");
            sb.AppendLine($"rename 指定：{(string.IsNullOrWhiteSpace(iRenameSpec) ? "(無 —— 全部沿用現行帳號 id)" : iRenameSpec)}");
            sb.AppendLine();
            int transferCnt = 0, transferSum = 0, renameCnt = 0, blocked = 0;
            foreach (var r in rows)
            {
                string act = r.NeedsRename ? $"agent 改名 `{r.Agent}`→`{r.FinalBank}`" : "agent 不變";
                string mv = r.NeedsTransfer
                    ? $"　💰 搬錢 `{r.CurrentBank}`({r.BankBalance}) → `{r.FinalBank}`"
                      + (r.TargetIsNew ? "（🆕 **全新帳戶**，ledger 從無紀錄）" : $"（既有，餘額 {r.TargetBalance}）")
                    : "";
                sb.AppendLine($"- `{r.Agent}` → 最終帳號 **`{r.FinalBank}`**　{act}{mv}"
                    + $"　persona {r.Personas.Count} 位"
                    + (string.IsNullOrEmpty(r.Blocker) ? "" : $"　⛔ {r.Blocker}"));
                if (r.NeedsTransfer) { transferCnt++; transferSum += r.BankBalance; }
                if (r.NeedsRename) renameCnt++;
                if (!string.IsNullOrEmpty(r.Blocker)) blocked++;
            }
            sb.AppendLine();
            sb.AppendLine("## 每個 persona 遷移後使用的帳號");
            foreach (var r in rows)
                foreach (var p in r.Personas)
                    // 「（原 X）」的判準是**帳號有沒有變**，不是 agent 有沒有改名 ——
                    // 用 agent 當判準的話，`Sirius → Federal Reserve System（原 Federal Reserve System）`
                    // 這種「原＝新」的括號會冒出來，讓沒有變動的那一列看起來像動過。
                    sb.AppendLine($"- {p} → `{r.FinalBank}`" + (r.CurrentBank == r.FinalBank ? "" : $"（原 `{r.CurrentBank}`）"));
            sb.AppendLine();
            var finals = rows.Select(x => x.FinalBank).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            sb.AppendLine($"## 遷移後總共會有這些帳號 id（{finals.Count} 個）");
            sb.AppendLine("　" + string.Join("、", finals.Select(x => "`" + x + "`")));
            sb.AppendLine();
            // 顯示名稱狀態：合一後署名由這份資料供給，所以它是遷移計畫的一部分而不是附註。
            int noProf = finals.Count(x => UCL_BankAccountProfileIO.Load(x) == null);
            sb.AppendLine($"## 帳戶資料（一帳一檔）：已建檔 {finals.Count - noProf}／未建檔 {noProf}");
            if (noProf > 0)
                sb.AppendLine($"　⇒ 執行「② 改名＋切換」時會**自動為未建檔者建檔**（顯示名稱先套 id）。");
            sb.AppendLine();
            sb.AppendLine($"⇒ 需改名 {renameCnt} 組｜需搬錢 {transferCnt} 組（合計 {transferSum} token）｜阻擋 {blocked} 組");
            if (blocked > 0) sb.AppendLine("⛔ 有阻擋項 —— 執行鈕不會開放，先處理上面標 ⛔ 的那幾列。");
            return sb.ToString();
        }

        static JsonData LoadRegistryMeta()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_registry_meta.json");
                if (!System.IO.File.Exists(path)) return null;
                return JsonData.ParseJson(System.IO.File.ReadAllText(path));
            }
            catch { return null; }
        }

        // 合一模式下的「agent → 帳號」映射：agent id 就是帳號 id，所以是恆等映射，
        // 而清單來源是**實際在用的 agent**（從 persona 的 registry 欄收集），不是任何一張表。
        static Dictionary<string, string> BuildIdentityMapFromPersonas()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in UCL_PersonaProfile.PoolNamesSorted())
            {
                string a = UCL_PersonaProfile.GetString(p, "agent", "").Trim();
                if (!string.IsNullOrEmpty(a)) d[a] = a;
            }
            return d;
        }

        static Dictionary<string, string> ReadAgentBanks(JsonData iMeta)
        {
            // 走跟 UCL_BankAdminPage 完全相同的讀法（`Dic.Keys` + `GetString`，過濾 `_` 開頭的註解鍵）——
            // 同一份資料兩種讀法會在某個邊界值上分岔，而那種分岔不會報錯。
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (iMeta == null || !iMeta.Contains("agent_banks")) return d;
            var ab = iMeta["agent_banks"];
            if (!ab.IsObject || ab.Dic == null) return d;
            foreach (var k in ab.Dic.Keys.Where(x => !x.StartsWith("_")))
            {
                string v = ab.GetString(k, "");
                if (!string.IsNullOrEmpty(v)) d[k] = v;
            }
            return d;
        }

        // ==========================================================
        // 區塊職責：ledger「每個帳戶有幾筆」的一次性快取。
        // 🩸 為什麼一定要快取（2026-08-20 summit 當場害 Tim 卡住）：
        //   本頁原本對每一列各叫一次 `UCL_TreasuryLedger.Audit(account)`，而那支是
        //   **全帳本 read+parse**（實測 14,000+ 檔）—— 9 列就是 9 次全掃，開頁必卡；
        //   同名偵測還放在 Draw 裡每幀跑一次，等於每幀重放整本帳。
        //   ⚠ 這個坑 `UCL_TreasuryLedger` 的區塊註解**早就寫過**
        //     （「ChatTavernPage 每 2 秒刷餘額 ⇒ 初開 40 秒 + 嚴重卡頓」），我照樣踩。
        //   ⇒ 判準：**Draw* 裡不准出現任何一次全帳本掃描**；要用就先進快取，
        //     並在 LoadData／執行動作之後顯式失效。
        // ==========================================================
        static Dictionary<string, int> s_AccountUniverse;

        static void InvalidateLedgerCounts() => s_AccountUniverse = null;

        // ==========================================================
        // 區塊職責：帳戶宇宙（帳戶 → 餘額）—— **不掃 ledger**。
        // 物理意義：來源是 `accounts/_balances.snapshot.txt`（**單檔**，Treasury 自己維護的餘額快照）
        //   ∪ registry 的三張表 ∪ persona 的現行 agent。
        //   🩸 第一版用 `LoadAllEntries()`（全帳本 14,000+ 檔）：改成「只掃一次」之後仍要 51 秒，
        //     因為問題不是掃幾次，是**根本不該掃**。Treasury 早就把餘額增量維護在那張快照裡了。
        // ⚠ 快照是衍生檔、可能落後 watermark ⇒ 用它判「有沒有歷史」會把剛建的帳戶判成新的。
        //   那個誤判方向是**安全的**（多提醒一次「這是新帳戶」，不會漏掉），所以可接受；
        //   但**不可**拿它當餘額真相 —— 餘額一律走 `UCL_TreasuryLedger.GetBalance`（它有自己的增量快取）。
        // ==========================================================
        static Dictionary<string, int> AccountUniverse()
        {
            if (s_AccountUniverse != null) return s_AccountUniverse;
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                string snap = System.IO.Path.Combine(UCL_BankAccountProfileIO.AccountsRoot, "_balances.snapshot.txt");
                if (System.IO.File.Exists(snap))
                {
                    foreach (var line in System.IO.File.ReadAllLines(snap))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("watermark=") || line.StartsWith("count=")) continue;
                        var parts = line.Split('	');
                        if (parts.Length < 1 || string.IsNullOrEmpty(parts[0])) continue;
                        d[parts[0]] = 1;
                    }
                }
                var meta = LoadRegistryMeta();
                foreach (var key in new[] { "agent_banks", "system_accounts", "closed_accounts" })
                {
                    if (meta == null || !meta.Contains(key)) continue;
                    var node = meta[key];
                    if (!node.IsObject || node.Dic == null) continue;
                    foreach (var k in node.Dic.Keys)
                    {
                        if (k.StartsWith("_")) continue;
                        d[k] = 1;
                        if (key == "agent_banks")
                        {
                            string v = node.GetString(k, "");
                            if (!string.IsNullOrEmpty(v)) d[v] = 1;
                        }
                    }
                }
                foreach (var p in UCL_PersonaProfile.PoolNamesSorted())
                {
                    string a = UCL_PersonaProfile.GetString(p, "agent", "").Trim();
                    if (!string.IsNullOrEmpty(a)) d[a] = 1;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[BankMigration] 帳戶宇宙建立失敗：{ex.Message}"); }
            s_AccountUniverse = d;
            return d;
        }

        // 這個名字在系統裡出現過嗎（0 ＝ 從未出現，是全新帳戶）。
        static int LedgerEntryCount(string account)
            => AccountUniverse().ContainsKey(account) ? 1 : 0;

        static int SafeBal(string account)
        {
            try { return UCL_TreasuryLedger.GetBalance(account, "tavern_token"); }
            catch { return 0; }
        }

        // ==========================================================
        // UI 狀態（draft 一律不直接落盤 —— 打到一半的字不該生效）
        // ==========================================================
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        readonly Dictionary<string, string> m_RenameDrafts = new Dictionary<string, string>(StringComparer.Ordinal);
        List<MigrationRow> m_Rows;
        List<CaseCollisionGroup> m_CacheCollisions;   // 同名偵測結果快取（Draw 不重算）
        string m_Report = "";
        string m_LastResult = "";
        bool m_TransferArmed = false;
        double m_TransferArmedAt = 0;
        bool m_RenameArmed = false;
        double m_RenameArmedAt = 0;
        bool m_MergeArmed = false;          // 同名合併的二段確認（可能搬錢＋銷戶）
        double m_MergeArmedAt = 0;
        const double ARM_WINDOW_SEC = 5.0;

        GUIStyle m_WrapStyle;
        GUIStyle WrapStyle
        {
            get
            {
                if (m_WrapStyle == null)
                    m_WrapStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapStyle;
            }
        }

        // 把 UI 的 rename 草稿組成 renameSpec —— UI 與 CLI 因此共用同一個入口參數。
        public string ComposeRenameSpec()
        {
            var parts = new List<string>();
            foreach (var kv in m_RenameDrafts)
                if (!string.IsNullOrWhiteSpace(kv.Value)) parts.Add($"{kv.Key}={kv.Value.Trim()}");
            return string.Join(";", parts);
        }

        protected override void ContentOnGUI()
        {
            DrawHeaderPanel();
            DrawPlanPanel();
            DrawCaseMergePanel();
            DrawExecutePanel();
            DrawResultPanel();
        }

        void DrawHeaderPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>🔀 agent ↔ 帳號 合一遷移</b>　"
                    + "把「agent id」與「帳號 id」收斂成同一個名字。", WrapStyle);
                GUILayout.Label("　預設方向是<b>把 agent 改名成現行帳號 id</b> —— 那條路**不動任何一分錢**。"
                    + "要反過來（保留 agent 名、把錢搬過去）就在該列的 <b>rename</b> 欄填新帳號 id。", WrapStyle);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("🔄 重新試跑", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)),
                            GUILayout.ExpandWidth(false)))
                        RefreshPlan();
                    if (GUILayout.Button("🏦 回銀行後台", UCL_GUIStyle.GetButtonStyle(Color.cyan),
                            GUILayout.ExpandWidth(false)))
                        UCL_BankAdminPage.Create();
                    GUILayout.FlexibleSpace();
                }
            }
        }

        public void RefreshPlan()
        {
            // 資料可能被別的路徑改過（Cmd／別的頁／git）⇒ 重算前先丟快取，
            // 但**只在這裡丟**，不在 Draw 裡 —— 那正是卡頓的來源。
            InvalidateLedgerCounts();
            m_CacheCollisions = null;
            m_Rows = BuildPlan(ComposeRenameSpec(), UCL_CentralBankSettings.CurrencyId);
            m_Report = BuildPlanReport(ComposeRenameSpec());
        }

        void DrawPlanPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "MigPlanFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>📋 遷移計畫</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                if (m_Rows == null) RefreshPlan();

                foreach (var r in m_Rows)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"　<b>{r.Agent}</b> → 帳號 <b>{r.CurrentBank}</b>（{r.BankBalance}）",
                            WrapStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(330)));
                        GUILayout.Label("rename→", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        m_RenameDrafts.TryGetValue(r.CurrentBank, out string draft);
                        string nd = GUILayout.TextField(draft ?? "", UCL_GUIStyle.TextFieldStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(170)));
                        if (nd != (draft ?? "")) { m_RenameDrafts[r.CurrentBank] = nd; RefreshPlan(); }
                        GUILayout.Label($"⇒ <b>{r.FinalBank}</b>"
                            + (r.NeedsTransfer ? $"　<color=#ffaa55>💰搬 {r.BankBalance}</color>" : "")
                            + (r.NeedsRename ? "　<color=#88ccff>改名</color>" : "")
                            + (string.IsNullOrEmpty(r.Blocker) ? "" : $"　<color=red>⛔ {r.Blocker}</color>"),
                            WrapStyle);
                        GUILayout.FlexibleSpace();
                    }
                }
                GUILayout.Space(4);
                GUILayout.Label("<b>試跑結果</b>（這份文字與 CLI `Cmd_Invoke BuildPlanReport` 完全相同）", WrapStyle);
                GUILayout.Label(m_Report ?? "", WrapStyle);
            }
        }

        // ==========================================================
        // 區塊職責：大小寫同名合併的 UI（試跑 → 執行）
        // 物理意義：這一步讓「帳號 id 在全小寫下唯一」成為系統的事實，而不只是一條約定。
        //          約定會被下一個開戶的人打破；把它做成資料狀態才擋得住。
        // ==========================================================
        void DrawCaseMergePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // ⚠ 不在 Draw 裡偵測 —— 那會每幀重算。改用 RefreshPlan 時算好的快取。
                var groups = m_CacheCollisions ?? (m_CacheCollisions = DetectCaseCollisions());
                GUILayout.Label($"<b>🔠 大小寫同名帳號</b>　{groups.Count} 組"
                    + (groups.Count == 0 ? "　<color=#88dd88>✓ 已唯一</color>" : ""), WrapStyle);
                if (groups.Count == 0)
                {
                    GUILayout.Label("　帳號 id 在全小寫比對下已經是唯一的 —— 開戶那一格也擋著同名，"
                        + "所以這個狀態不會自己壞掉。", WrapStyle);
                    return;
                }
                GUILayout.Label("　兩個只差大小寫的帳號在檔案系統上是**同一個檔**（`Treasury/accounts/&lt;id&gt;.json`），"
                    + "查 A 會拿到 B 的資料且零報錯。合併＝把被併方的錢搬給保留方，再把它銷戶並記 `renamed_to`。", WrapStyle);
                foreach (var g in groups)
                {
                    GUILayout.Label($"　【{g.LowerKey}】保留 <b>{g.Keeper}</b>"
                        + "　" + string.Join("　", g.Members.Select(m =>
                            $"{(m == g.Keeper ? "◎" : "→")}`{m}`({g.Balances[m]}"
                            + (g.BoundCounts[m] > 0 ? $"，persona {g.BoundCounts[m]}" : "") + ")")),
                        WrapStyle);
                }
                using (new GUILayout.HorizontalScope())
                {
                    bool armed = m_MergeArmed
                        && (EditorApplication.timeSinceStartup - m_MergeArmedAt) <= ARM_WINDOW_SEC;
                    int total = groups.Sum(x => x.MoveAmount);
                    if (GUILayout.Button(armed ? "⚠ 確認合併" : $"🔠 合併同名（{groups.Count} 組）",
                            UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.55f, 0.4f) : new Color(1f, 0.85f, 0.6f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        if (armed) DoMergeCaseCollisions(groups);
                        else
                        {
                            m_MergeArmed = true;
                            m_MergeArmedAt = EditorApplication.timeSinceStartup;
                            m_LastResult = $"⚠ 待確認：合併 {groups.Count} 組同名帳號，需搬 {total} token，"
                                + "被併方會銷戶並記 renamed_to。5 秒內再按一次生效。";
                        }
                    }
                    GUILayout.Label($"　需搬 {total} token" + (total == 0 ? "（零成本 —— 每組都有一邊是空的）" : ""), WrapStyle);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 合併：搬錢（若有）→ 銷戶被併方並記 renamed_to → 刪掉被併方的帳戶資料檔。
        // ⚠ 順序不可換：先銷戶再搬錢的話，搬錢會被「銷戶帳號禁止金流」擋住而卡在中間。
        void DoMergeCaseCollisions(List<CaseCollisionGroup> iGroups)
        {
            m_MergeArmed = false;
            var sb = new StringBuilder();
            int moved = 0, closed = 0, failed = 0;
            foreach (var g in iGroups)
            {
                foreach (var m in g.Members)
                {
                    if (m == g.Keeper) continue;
                    int bal = SafeBal(m);
                    if (bal > 0)
                    {
                        string txId = "tx_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        string desc = $"大小寫同名合併 {m} → {g.Keeper}";
                        try
                        {
                            UCL_TreasuryLedger.Debit(m, bal, "account-rename", txId, desc,
                                "system", txId, idempotencyKey: null, resolveAccount: false);
                            UCL_TreasuryLedger.Credit(g.Keeper, bal, "account-rename", txId, desc,
                                "system", txId, idempotencyKey: null, resolveAccount: false);
                            moved++;
                            sb.AppendLine($"  💰 {m} → {g.Keeper}：{bal}");
                        }
                        catch (Exception ex)
                        { failed++; sb.AppendLine($"  ✗ {m} 搬錢失敗，**未銷戶**：{ex.Message}"); continue; }
                    }
                    if (!UCL_TreasuryAccountResolver.CloseAccount(m,
                            $"大小寫同名合併，併入 {g.Keeper}", g.Keeper, "BankMigrationPage", out string cErr))
                    { failed++; sb.AppendLine($"  ✗ {m} 銷戶失敗：{cErr}"); continue; }
                    closed++;
                    // 被併方的帳戶資料檔要刪掉，否則它仍佔著保留方的檔名（撞名的根源沒消失）。
                    try
                    {
                        string pf = System.IO.Path.Combine(UCL_BankAccountProfileIO.AccountsRoot, m + ".json");
                        if (System.IO.File.Exists(pf))
                        {
                            var probe = UCL_BankAccountProfileIO.Load(m);
                            if (probe != null) { System.IO.File.Delete(pf); sb.AppendLine($"  🗑 移除帳戶資料檔 {m}.json"); }
                            else sb.AppendLine($"  ・{m}.json 內的 id 不是它自己（撞名佔位），保留給保留方，不刪");
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"  ⚠ {m} 資料檔處理失敗（不影響合併）：{ex.Message}"); }
                    sb.AppendLine($"  ✓ {m} 已併入 {g.Keeper} 並銷戶（renamed_to={g.Keeper}）");
                }
            }
            UCL_TreasuryAccountResolver.Invalidate();
            m_LastResult = $"🔠 同名合併：搬錢 {moved} 筆／銷戶 {closed} 個／失敗 {failed}"
                + System.Environment.NewLine + sb;
            Debug.Log("[BankMigration] " + m_LastResult);
            RefreshPlan();
        }

        void DrawExecutePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>▶ 執行</b>　兩段分開按 —— <b>第一段會動錢，第二段不會</b>。", WrapStyle);
                if (m_Rows == null) { GUILayout.Label("（尚未試跑）", WrapStyle); return; }
                bool anyBlocked = m_Rows.Any(x => !string.IsNullOrEmpty(x.Blocker));
                var transfers = m_Rows.Where(x => x.NeedsTransfer).ToList();
                var renames = m_Rows.Where(x => x.NeedsRename).ToList();

                if (anyBlocked)
                {
                    GUILayout.Label("<color=red>⛔ 計畫中有阻擋項，執行已停用。</color>", WrapStyle);
                    return;
                }

                // ── 第一段：搬錢（ledger transfer）──
                using (new GUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(transfers.Count == 0))
                    {
                        bool armed = m_TransferArmed
                            && (EditorApplication.timeSinceStartup - m_TransferArmedAt) <= ARM_WINDOW_SEC;
                        if (GUILayout.Button(armed ? "⚠ 確認搬錢" : $"① 搬錢（{transfers.Count} 組）",
                                UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.55f, 0.4f) : new Color(1f, 0.85f, 0.6f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            if (armed) DoTransfers(transfers);
                            else
                            {
                                m_TransferArmed = true;
                                m_TransferArmedAt = EditorApplication.timeSinceStartup;
                                m_LastResult = "⚠ 待確認：" + string.Join("／", transfers.Select(
                                    x => $"{x.CurrentBank}({x.BankBalance})→{x.FinalBank}"))
                                    + "　5 秒內再按一次才會真的搬錢。";
                            }
                        }
                    }
                    GUILayout.Label(transfers.Count == 0
                        ? "　（沒有需要搬錢的組 —— 這是好事，代表遷移零 ledger 異動）"
                        : $"　合計 {transfers.Sum(x => x.BankBalance)} token，走 ledger transfer（同 tx_id，credit 失敗自動退回）", WrapStyle);
                    GUILayout.FlexibleSpace();
                }

                // ── 第二段：agent 改名（不動錢）──
                using (new GUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(renames.Count == 0))
                    {
                        bool armed = m_RenameArmed
                            && (EditorApplication.timeSinceStartup - m_RenameArmedAt) <= ARM_WINDOW_SEC;
                        if (GUILayout.Button(armed ? "⚠ 確認改名並切換" : $"② agent 改名 ＋ 切換解析模式（{renames.Count} 組）",
                                UCL_GUIStyle.GetButtonStyle(armed ? new Color(1f, 0.55f, 0.4f) : new Color(0.7f, 0.9f, 1f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            if (armed) DoRenames(renames);
                            else
                            {
                                m_RenameArmed = true;
                                m_RenameArmedAt = EditorApplication.timeSinceStartup;
                                m_LastResult = "⚠ 待確認：" + string.Join("／", renames.Select(
                                    x => $"{x.Agent}→{x.FinalBank}({x.Personas.Count}人)"))
                                    + "　接著會把解析模式切成「已合一」。"
                                    + "　5 秒內再按一次生效。**這一段不動任何一分錢；中途失敗會整批回滾且不切換。**";
                            }
                        }
                    }
                    GUILayout.Label(renames.Count == 0
                        ? "　（沒有需要改名的組）"
                        : "　改綁定檔與 registry.agent 兩邊 → 逐筆讀回複驗 → 切換解析模式 → 逐人複驗；"
                          + "**中途失敗整批回滾、開關不動**（改名與切換之間不留縫）", WrapStyle);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 動錢：照 UCL_BankAdminPage.DoTransfer 的既有形狀（debit → credit，同 tx_id，credit 失敗退回）。
        // ⚠ resolveAccount:false —— from/to 都是**帳號字面**（從計畫算出來的），不可讓身分歸一介入，
        //   否則會扣到「解析後的那個帳號」而畫面顯示成功。
        void DoTransfers(List<MigrationRow> iRows)
        {
            m_TransferArmed = false;
            var sb = new StringBuilder();
            int ok = 0, fail = 0;
            foreach (var r in iRows)
            {
                string txId = "tx_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string desc = $"帳號改名歸併 {r.CurrentBank} → {r.FinalBank}（agent↔帳號合一遷移）";
                try
                {
                    UCL_TreasuryLedger.Debit(r.CurrentBank, r.BankBalance, "account-rename", txId, desc,
                        "system", txId, idempotencyKey: null, resolveAccount: false);
                }
                catch (Exception ex) { fail++; sb.AppendLine($"✗ {r.CurrentBank} Debit 失敗：{ex.Message}"); continue; }
                try
                {
                    UCL_TreasuryLedger.Credit(r.FinalBank, r.BankBalance, "account-rename", txId, desc,
                        "system", txId, idempotencyKey: null, resolveAccount: false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        UCL_TreasuryLedger.Credit(r.CurrentBank, r.BankBalance, "account-rename", txId + "-rollback",
                            "credit 失敗自動退回", "system", txId, idempotencyKey: null, resolveAccount: false);
                        sb.AppendLine($"✗ {r.FinalBank} Credit 失敗（已退回 {r.CurrentBank}）：{ex.Message}");
                    }
                    catch (Exception ex2)
                    { sb.AppendLine($"🔴 {r.CurrentBank}→{r.FinalBank} Credit 失敗且退回也失敗：{ex.Message} / {ex2.Message}"); }
                    fail++; continue;
                }
                ok++;
                sb.AppendLine($"✓ {r.CurrentBank} → {r.FinalBank}：{r.BankBalance}"
                    + $"（現餘 {SafeBal(r.CurrentBank)} / {SafeBal(r.FinalBank)}）");
            }
            m_LastResult = $"💰 搬錢完成 {ok} 組、失敗 {fail} 組\n{sb}";
            Debug.Log("[BankMigration] " + m_LastResult);
            UCL_TreasuryAccountResolver.Invalidate();
            RefreshPlan();
        }

        // ==========================================================
        // 區塊職責：agent 改名 ＋ 切換解析模式 —— **一顆按鈕的原子操作**（Tim 2026-08-20 拍板 (b)）。
        // 物理意義：為什麼必須綁在一起 ——
        //   兩條解析鏈對「不同狀態的資料」各自成立：
        //     · 開關 false（舊鏈）要求 agent 名在 `agent_banks` 的 key 裡 ⇒ 改名**後**它就查不到了
        //     · 開關 true（新鏈）要求 agent 名**就是**帳號 ⇒ 改名**前**它還不是
        //   ⇒ 「已改名但還沒切開關」是一段兩邊都不對的狀態。改名與切換之間不能有人看得到的縫。
        //   （已經一致的組 —— Myth／Altair／Template —— 在兩條鏈都對，不受影響。）
        // 數值影響：不動 ledger。但**改變之後每一筆錢的去向**，所以：
        //   ⚠ **失敗即整批回滾**。若改名跑到一半失敗，已改的那幾組需要新鏈、沒改的需要舊鏈 ——
        //     此時開關無論設哪一邊都有人是壞的，所以唯一正確的收尾是把已改的改回去、開關不動。
        //   回滾本身也可能失敗（例如檔案被鎖），那種情況**必須大聲說**並列出待人工收尾的清單：
        //   靜默的半完成狀態，比一個明確的失敗貴得多。
        // ==========================================================
        void DoRenames(List<MigrationRow> iRows)
        {
            m_RenameArmed = false;
            string currency = UCL_CentralBankSettings.CurrencyId;
            const string ACTOR = "BankMigrationPage";
            const string REASON = "agent↔帳號合一遷移（Tim 2026-08-20 拍板）";
            var sb = new StringBuilder();
            var done = new List<MigrationRow>();   // 已成功改名的，回滾時要按相反方向改回去

            foreach (var r in iRows)
            {
                bool allOk = UCL_PersonaProfile.RenameAgent(r.Agent, r.FinalBank, currency,
                    ACTOR, REASON, false,
                    out int hit, out int renamed, out int failed, out string report);
                sb.AppendLine(report);
                if (allOk) { done.Add(r); continue; }

                // ── 失敗 ⇒ 整批回滾，開關不動 ──
                sb.AppendLine($"🔴 `{r.Agent}` → `{r.FinalBank}` 失敗 —— 開始回滾已完成的 {done.Count} 組，"
                    + "**解析模式維持不變**。");
                int rbOk = 0; var rbFail = new List<string>();
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    var d = done[i];
                    bool back = UCL_PersonaProfile.RenameAgent(d.FinalBank, d.Agent, currency,
                        ACTOR, "回滾：合一遷移中途失敗", false,
                        out _, out _, out _, out string rbReport);
                    sb.AppendLine(rbReport);
                    if (back) rbOk++; else rbFail.Add($"{d.FinalBank}→{d.Agent}");
                }
                sb.AppendLine(rbFail.Count == 0
                    ? $"  ↩ 回滾完成 {rbOk} 組 —— 系統回到遷移前狀態，可以查清原因後重跑。"
                    : $"  🔴 **回滾未竟**：{string.Join("、", rbFail)} —— 這幾組現在停在中間狀態，"
                      + "需要人工收尾（用本頁 rename 欄位或 Cmd `op=rename_agent` 改回去）。");
                m_LastResult = $"❌ 合一遷移失敗並已回滾\n{sb}";
                Debug.LogError("[BankMigration] " + m_LastResult);
                UCL_TreasuryAccountResolver.Invalidate();
                RefreshPlan();
                return;
            }

            // ── 全數成功 ⇒ 讓解析器重新認識磁碟 ──
            // ⚠ 這裡以前還會把 `account_resolve_unified` 切成 1；那個開關已於 2026-08-20 移除
            //   （合一是唯一模式）⇒ 改名成功之後不必再切什麼，只要讓快取失效。
            UCL_TreasuryAccountResolver.Invalidate();
            sb.AppendLine("✅ 改名全數成功（解析一律一跳；`agent_banks` 不參與）。");
            // 切換後逐人複驗：這才是「遷移成功」的讀數，不是「改了幾個檔」。
            int bad = 0;
            foreach (var r in iRows)
                foreach (var p in r.Personas)
                {
                    string got = UCL_TreasuryAccountResolver.Resolve(p).AccountId;
                    if (got != r.FinalBank) { bad++; sb.AppendLine($"  ⚠ {p}：解析成 `{got}`，期望 `{r.FinalBank}`"); }
                }
            sb.AppendLine(bad == 0
                ? "  ✓ 切換後逐人複驗：全部解析到預期帳號。"
                : $"  🔴 切換後有 {bad} 位解析結果不如預期 —— 請立刻切回「遷移前」並查原因。");

            // 帳戶資料一帳一檔：遷移後**所有最終帳號**都建檔（不只改名的那幾組）。
            // 物理意義：合一之後顯示名稱改由 `Treasury/accounts/<id>.json` 供給，
            //   而遷移正是「帳號 id 換了一批」的那一刻 —— 不在這裡補，舊 roster 的鍵就對不上，
            //   症狀是所有人顯示成 id、或更糟：命中舊表裡某個同名的壞資料。
            // ⚠ 已建檔的一律跳過 —— 遷移沒有資格覆蓋人已經取好的名字。
            // 顯示名稱先一律套 id（Tim 2026-08-20：之後再逐個改）。
            int profMade = 0, profSkip = 0, profFail = 0;
            foreach (var fb in m_Rows.Select(x => x.FinalBank).Distinct())
            {
                if (!UCL_BankAccountProfileIO.IsValidAccountId(fb)) { profFail++; continue; }
                if (UCL_BankAccountProfileIO.Load(fb) != null) { profSkip++; continue; }
                if (UCL_BankAccountProfileIO.Save(fb, fb, "合一遷移自動建檔", "BankMigrationPage", out string pErr))
                    profMade++;
                else { profFail++; sb.AppendLine($"  ✗ 帳戶資料建檔失敗 {fb}：{pErr}"); }
            }
            sb.AppendLine($"  🏷 帳戶資料（一帳一檔）：新建 {profMade}／已存在跳過 {profSkip}／失敗 {profFail}"
                + "　顯示名稱先套 id，之後到銀行後台「🏷 帳戶資料」逐個改。");
            m_LastResult = $"🔀 合一遷移完成（改名 {done.Count} 組 ＋ 已切換解析模式）\n{sb}";
            Debug.Log("[BankMigration] " + m_LastResult);
            RefreshPlan();
        }

        void DrawResultPanel()
        {
            if (string.IsNullOrEmpty(m_LastResult)) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>📋 最近一次操作結果</b>", WrapStyle);
                GUILayout.Label(m_LastResult, WrapStyle);
            }
        }
    }
}
#endif
