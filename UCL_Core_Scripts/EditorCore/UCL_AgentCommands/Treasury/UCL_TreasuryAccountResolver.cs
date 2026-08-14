// 區塊職責：Treasury 帳號解析 —— 把「呼叫端給的 account_id 字串」歸一成註冊在案的正式帳號。
// 物理意義：ledger 的 account_id 此前是**純字串直寫**：給什麼就寫什麼。於是三種輸入各自生出
//          一個「有錢但沒有主人」的孤兒帳戶，而且沒有任何一層會出聲：
//            ① 大小寫不同     —— agent `Zeta` 寫成帳號 `Zeta`，正式 bank 是 `zeta`
//            ② persona 名當帳號 —— `summit` / `gura` / `apex-one`（錢認 agent，說話才認 persona）
//            ③ 打錯字 / 舊命名  —— `zeta-bank`、`zeta-da-xiaojie-bank`、`antigravity-da-xiaojie-da-xiaojie`
//          2026-08-14 實查 12,742 筆 ledger：35 個孤兒帳戶、合計 2,616 token，
//          其中 `Zeta`(310) 與 `Fed`(114) **當天仍在增加** —— 這不是歷史殘帳，是還在漏的洞。
// 數值影響：解析結果決定**錢落進哪個帳戶**。因此本 module 的鐵律是「只歸一、不發明」：
//          查不到對應 → 原樣回傳並標記 unresolved，絕不 derive、絕不 auto-mint 帳號名。
//          （對比：BankAdminPage 的 agent→bank 對未知 agent 直接拒絕，那是 admin 代操作的更嚴標準；
//            寫入端不能拒絕，因為拒絕會讓一筆真實勞動的薪水直接消失。標記比丟棄安全。）
// 事實來源：AgentCommands/AwakenInit/_registry_meta.json（agent_banks / agent_aliases /
//          system_accounts / closed_accounts）＋ AwakenInit/personas/<persona>.json 的 agent 欄。
//          **不另存一份對照表** —— 平行索引與事實不一致時兩邊都能各自運作、都不報錯。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>帳號解析的判定結果 —— 呼叫端要能分辨「原本就對」「被歸一」「查不到」。</summary>
    public enum TreasuryAccountResolveKind
    {
        /// <summary>輸入已是註冊在案的正式帳號（bank 值 / system_account），原樣採用。</summary>
        AlreadyCanonical,
        /// <summary>大小寫或拼寫差異，已歸一到註冊拼法。</summary>
        CaseNormalized,
        /// <summary>輸入是 agent 名（或其 alias），已換成該 agent 的 bank。</summary>
        ViaAgent,
        /// <summary>輸入是 persona 名，經 persona→agent→bank 兩跳換算。</summary>
        ViaPersona,
        /// <summary>查不到任何對應 —— 原樣通過，但標記出來讓人看得見。</summary>
        Unresolved,
    }

    public struct TreasuryAccountResolution
    {
        public string Input;
        public string AccountId;
        public TreasuryAccountResolveKind Kind;
        /// <summary>解析路徑的白話說明，寫進 ledger entry 的 description / log 用。</summary>
        public string Trace;

        public bool Changed => !string.Equals(Input, AccountId, StringComparison.Ordinal);
        public bool IsUnresolved => Kind == TreasuryAccountResolveKind.Unresolved;

        // 沒有這個 override，Cmd_Invoke 與 Debug.Log 印出來的是型別名 ——
        // 「有回傳值」與「回傳值是什麼」會長得一樣，等於白驗一場。
        public override string ToString()
            => $"`{Input}` → `{AccountId}` [{Kind}] {Trace}";
    }

    public static class UCL_TreasuryAccountResolver
    {
        // ==========================================================
        // 區塊職責：registry 快取 —— 以 _registry_meta.json 的 mtime 當失效訊號
        // 物理意義：Credit/Debit 是高頻路徑（每則酒館發文都走一次），每次重讀 + parse registry
        //          會把「解析」變成新的效能坑。而 registry 幾乎不變，mtime 比對是最便宜的正確性來源。
        // 數值影響：純讀快取；mtime 前進即整組重建（開戶 / 銷戶寫完立刻被下一次呼叫看到）。
        // ⚠ 刻意用 mtime 而非「載入一次就不再看」：開戶與銷戶都會改 registry，
        //   而那兩件事之後緊接著就是金流操作 —— 陳舊的 registry 會讓剛開的戶被判成 unresolved。
        // ==========================================================
        static readonly object s_Lock = new object();
        static DateTime s_RegistryStamp = DateTime.MinValue;
        static string s_PersonaDirStampKey = "";

        // bank 值 / system_account：正式帳號宇宙（key = 原拼法）
        static readonly HashSet<string> s_CanonicalAccounts = new HashSet<string>(StringComparer.Ordinal);
        // 小寫 → 原拼法（大小寫歸一用）
        static readonly Dictionary<string, string> s_AccountByLower = new Dictionary<string, string>(StringComparer.Ordinal);
        // agent 名（小寫）→ bank
        static readonly Dictionary<string, string> s_AgentToBankLower = new Dictionary<string, string>(StringComparer.Ordinal);
        // alias（小寫）→ agent canonical key
        static readonly Dictionary<string, string> s_AliasLower = new Dictionary<string, string>(StringComparer.Ordinal);
        // persona 名（小寫）→ agent 名
        static readonly Dictionary<string, string> s_PersonaToAgentLower = new Dictionary<string, string>(StringComparer.Ordinal);
        // 已銷戶帳號（原拼法）→ 銷戶理由
        static readonly Dictionary<string, string> s_ClosedAccounts = new Dictionary<string, string>(StringComparer.Ordinal);

        public const string ClosedAccountsKey = "closed_accounts";

        static string RegistryMetaPath
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_registry_meta.json");
        static string PersonasDir
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "personas");

        /// <summary>強制下次解析重讀 registry（開戶 / 銷戶 / 手改 JSON 後呼叫）。</summary>
        public static void Invalidate()
        {
            lock (s_Lock) { s_RegistryStamp = DateTime.MinValue; s_PersonaDirStampKey = ""; }
        }

        // 區塊職責：確保快取與磁碟同步（呼叫端必須已持有 s_Lock）
        // 物理意義：registry 用 mtime；persona 目錄用「檔數 + 最新 mtime」的複合鍵 ——
        //          新增一個 persona 檔不會改目錄本身的 mtime（某些檔案系統上），檔數變化補得到。
        static void EnsureLoaded_NoLock()
        {
            DateTime regStamp;
            try { regStamp = File.Exists(RegistryMetaPath) ? File.GetLastWriteTimeUtc(RegistryMetaPath) : DateTime.MinValue; }
            catch { regStamp = DateTime.MinValue; }

            string personaKey = "";
            try
            {
                if (Directory.Exists(PersonasDir))
                {
                    var files = Directory.GetFiles(PersonasDir, "*.json");
                    DateTime newest = DateTime.MinValue;
                    foreach (var f in files)
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > newest) newest = t;
                    }
                    personaKey = files.Length + "|" + newest.Ticks;
                }
            }
            catch { personaKey = "?"; }

            if (regStamp == s_RegistryStamp && personaKey == s_PersonaDirStampKey) return;

            s_CanonicalAccounts.Clear(); s_AccountByLower.Clear();
            s_AgentToBankLower.Clear(); s_AliasLower.Clear();
            s_PersonaToAgentLower.Clear(); s_ClosedAccounts.Clear();

            try
            {
                if (File.Exists(RegistryMetaPath))
                {
                    var reg = JsonData.ParseJson(File.ReadAllText(RegistryMetaPath, Encoding.UTF8));
                    if (reg != null && reg.IsObject)
                    {
                        // agent_banks: agent → bank（bank 值本身也是正式帳號）
                        if (reg.Contains("agent_banks"))
                        {
                            var ab = reg["agent_banks"];
                            if (ab.IsObject && ab.Dic != null)
                                foreach (var agent in ab.Dic.Keys)
                                {
                                    if (string.IsNullOrEmpty(agent) || agent.StartsWith("_")) continue;
                                    string bank = ab.GetString(agent, "");
                                    if (string.IsNullOrEmpty(bank)) continue;
                                    s_AgentToBankLower[agent.ToLowerInvariant()] = bank;
                                    AddCanonical_NoLock(bank);
                                }
                        }
                        // system_accounts: 系統 / 舊世代帳號，同樣是正式帳號（終點，不再往下解析）
                        if (reg.Contains("system_accounts"))
                        {
                            var sa = reg["system_accounts"];
                            if (sa.IsObject && sa.Dic != null)
                                foreach (var acc in sa.Dic.Keys)
                                    if (!string.IsNullOrEmpty(acc) && !acc.StartsWith("_")) AddCanonical_NoLock(acc);
                        }
                        // agent_aliases: 小寫 key → canonical agent
                        if (reg.Contains("agent_aliases"))
                        {
                            var al = reg["agent_aliases"];
                            if (al.IsObject && al.Dic != null)
                                foreach (var k in al.Dic.Keys)
                                {
                                    if (string.IsNullOrEmpty(k) || k.StartsWith("_")) continue;
                                    string v = al.GetString(k, "");
                                    if (!string.IsNullOrEmpty(v)) s_AliasLower[k.ToLowerInvariant()] = v;
                                }
                        }
                        // closed_accounts: 已銷戶（value = 銷戶理由）
                        if (reg.Contains(ClosedAccountsKey))
                        {
                            var ca = reg[ClosedAccountsKey];
                            if (ca.IsObject && ca.Dic != null)
                                foreach (var k in ca.Dic.Keys)
                                    if (!string.IsNullOrEmpty(k) && !k.StartsWith("_"))
                                        s_ClosedAccounts[k] = ca.GetString(k, "");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 讀不到 registry → 解析能力歸零，但**不能靜默** ——
                // 那會讓「沒有對應」與「沒讀到表」長得一模一樣（empty-is-a-question）。
                Debug.LogWarning($"[Treasury] 帳號解析表讀取失敗，本輪一律視為 unresolved（錢仍會入帳，只是不歸一）：{ex.Message}");
            }

            try
            {
                if (Directory.Exists(PersonasDir))
                    foreach (var pf in Directory.GetFiles(PersonasDir, "*.json"))
                    {
                        try
                        {
                            var pj = JsonData.ParseJson(File.ReadAllText(pf, Encoding.UTF8));
                            string name = Path.GetFileNameWithoutExtension(pf);
                            string agent = pj != null ? pj.GetString("agent", "") : "";
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(agent))
                                s_PersonaToAgentLower[name.ToLowerInvariant()] = agent;
                        }
                        catch { /* 單一 persona 檔壞不該讓整個金流解析停擺 */ }
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] persona 對照讀取失敗（persona 名將無法歸一）：{ex.Message}");
            }

            s_RegistryStamp = regStamp;
            s_PersonaDirStampKey = personaKey;
        }

        static void AddCanonical_NoLock(string account)
        {
            if (string.IsNullOrEmpty(account)) return;
            s_CanonicalAccounts.Add(account);
            string lower = account.ToLowerInvariant();
            // 先到先得：同一個小寫拼法對到兩個正式帳號時保留先登記的那個。
            // 實例：`zeta`（現行 bank）與 `Zeta-da-xiaojie`（舊世代）小寫不同，不會撞；
            //       真撞到代表 registry 本身有歧義，那要人去修，不該由解析器猜。
            if (!s_AccountByLower.ContainsKey(lower)) s_AccountByLower[lower] = account;
        }

        // ==========================================================
        // 區塊職責：解析主函式 —— 六段判定，先精確後模糊，查不到就承認查不到
        // 物理意義：順序即優先序。精確命中優先於大小寫歸一，是為了讓 registry 裡刻意並存的
        //          兩個拼法（`zeta` 與 `Zeta-da-xiaojie`）都能各自被指名，而不是互相吸收。
        // 數值影響：純查表無副作用；回傳值決定 ledger entry 的 account_id。
        // ==========================================================
        public static TreasuryAccountResolution Resolve(string accountId)
        {
            var r = new TreasuryAccountResolution { Input = accountId, AccountId = accountId };
            if (string.IsNullOrEmpty(accountId))
            {
                r.Kind = TreasuryAccountResolveKind.Unresolved;
                r.Trace = "空字串";
                return r;
            }

            lock (s_Lock)
            {
                EnsureLoaded_NoLock();
                string lower = accountId.ToLowerInvariant();

                // ① 已是正式帳號（精確拼法）
                if (s_CanonicalAccounts.Contains(accountId))
                {
                    r.Kind = TreasuryAccountResolveKind.AlreadyCanonical;
                    r.Trace = "已是註冊帳號";
                    return r;
                }

                // ② agent 名（精確小寫比對）→ bank
                //    先於大小寫歸一：`Zeta` 是 agent 名，要走 agent→bank 換成 `zeta`，
                //    而不是被當成某個帳號的大小寫變體。
                if (s_AgentToBankLower.TryGetValue(lower, out var bankOfAgent))
                {
                    r.AccountId = bankOfAgent;
                    r.Kind = r.Changed ? TreasuryAccountResolveKind.ViaAgent : TreasuryAccountResolveKind.AlreadyCanonical;
                    r.Trace = $"agent `{accountId}` → bank `{bankOfAgent}`";
                    return r;
                }

                // ③ 正式帳號的大小寫 / 拼法變體
                if (s_AccountByLower.TryGetValue(lower, out var canonicalSpelling))
                {
                    r.AccountId = canonicalSpelling;
                    r.Kind = TreasuryAccountResolveKind.CaseNormalized;
                    r.Trace = $"大小寫歸一 `{accountId}` → `{canonicalSpelling}`";
                    return r;
                }

                // ④ alias → agent → bank
                if (s_AliasLower.TryGetValue(lower, out var canonicalAgent)
                    && s_AgentToBankLower.TryGetValue(canonicalAgent.ToLowerInvariant(), out var bankViaAlias))
                {
                    r.AccountId = bankViaAlias;
                    r.Kind = TreasuryAccountResolveKind.ViaAgent;
                    r.Trace = $"alias `{accountId}` → agent `{canonicalAgent}` → bank `{bankViaAlias}`";
                    return r;
                }

                // ⑤ persona 名 → agent → bank（錢認 agent，說話才認 persona）
                if (s_PersonaToAgentLower.TryGetValue(lower, out var agentOfPersona)
                    && s_AgentToBankLower.TryGetValue(agentOfPersona.ToLowerInvariant(), out var bankViaPersona))
                {
                    r.AccountId = bankViaPersona;
                    r.Kind = TreasuryAccountResolveKind.ViaPersona;
                    r.Trace = $"persona `{accountId}` → agent `{agentOfPersona}` → bank `{bankViaPersona}`";
                    return r;
                }

                // ⑥ 查不到 —— 原樣通過並標記。**不 derive、不 mint。**
                r.Kind = TreasuryAccountResolveKind.Unresolved;
                r.Trace = "查無對應（未歸一，將產生／沿用孤兒帳戶）";
                return r;
            }
        }

        // ==========================================================
        // 區塊職責：自我檢查 —— 用「與資料無關的不變式」驗解析器，不是把答案抄一遍
        // 物理意義：斷言若寫成 `Resolve("Zeta") == "zeta"`，那只是把 registry 的內容複述一次；
        //          registry 改了它就假紅，而解析器真的壞掉時它未必會紅。
        //          所以這裡驗的是**性質**：對 registry 裡的每一個 agent、每一個 bank、每一個 persona
        //          都必須成立的關係。資料換一批，斷言照樣有鑑別力。
        // 數值影響：純讀，零金流。回傳報告字串（Cmd_Invoke 會把它印進 Editor log）。
        // ==========================================================
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            void Check(bool ok, string label)
            {
                if (ok) { pass++; sb.Append("  ✅ "); }
                else { fail++; sb.Append("  ❌ "); }
                sb.Append(label).Append('\n');
            }

            lock (s_Lock) { EnsureLoaded_NoLock(); }

            Dictionary<string, string> agents, personas;
            HashSet<string> canonical;
            lock (s_Lock)
            {
                agents = new Dictionary<string, string>(s_AgentToBankLower, StringComparer.Ordinal);
                personas = new Dictionary<string, string>(s_PersonaToAgentLower, StringComparer.Ordinal);
                canonical = new HashSet<string>(s_CanonicalAccounts, StringComparer.Ordinal);
            }

            sb.Append($"# 帳號解析 SelfTest（agents={agents.Count} / banks={canonical.Count} / personas={personas.Count}）\n\n");

            // 不變式 ①：每個正式帳號解析後必須是自己（歸一不可搬動已經正確的帳）
            sb.Append("## ① 正式帳號恆等\n");
            foreach (var acc in canonical)
            {
                var r = Resolve(acc);
                Check(string.Equals(r.AccountId, acc, StringComparison.Ordinal), $"{r}");
            }

            // 不變式 ②：每個 agent 名（原拼法與大小寫變體）都必須解析到它登記的 bank
            sb.Append("## ② agent → 登記的 bank（含大小寫變體）\n");
            foreach (var kv in agents)
            {
                var r = Resolve(kv.Key);
                Check(string.Equals(r.AccountId, kv.Value, StringComparison.Ordinal), $"{r}（期望 `{kv.Value}`）");
                var rUpper = Resolve(kv.Key.ToUpperInvariant());
                Check(string.Equals(rUpper.AccountId, kv.Value, StringComparison.Ordinal),
                      $"{rUpper}（大小寫變體，期望 `{kv.Value}`）");
            }

            // 不變式 ③：每個 persona 都必須解析到「它的 agent 所登記的 bank」
            // ⚠ 例外是**撞名**：某個 persona 的名字剛好也是註冊在案的帳號（見 ⑥）。
            //   那種情況下「正式帳號優先」是刻意的 —— 打進那個名字的錢屬於那個帳戶，
            //   不該因為有人同名而被搬去別處。這裡跳過它們**但不隱藏**，⑥ 會逐筆列出來。
            sb.Append("## ③ persona → agent → bank（撞名者見 ⑥）\n");
            var collisions = new List<string>();
            foreach (var kv in personas)
            {
                if (!agents.TryGetValue(kv.Value.ToLowerInvariant(), out var expect)) continue;
                var r = Resolve(kv.Key);
                if (canonical.Contains(r.AccountId) && !string.Equals(r.AccountId, expect, StringComparison.Ordinal)
                    && string.Equals(r.AccountId, kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    collisions.Add($"`{kv.Key}`：既是正式帳號、也是 persona（其 agent `{kv.Value}` 的 bank 是 `{expect}`）");
                    continue;
                }
                Check(string.Equals(r.AccountId, expect, StringComparison.Ordinal), $"{r}（期望 `{expect}`）");
            }

            // 不變式 ④：查無對應必須誠實回報 Unresolved 且**原樣通過**（不 mint、不丟棄）
            sb.Append("## ④ 查無對應時不發明帳號\n");
            foreach (var junk in new[] { "definitely-not-an-account-xyz", "zeta-bank-typo", "" })
            {
                var r = Resolve(junk);
                Check(r.IsUnresolved && string.Equals(r.AccountId, junk, StringComparison.Ordinal),
                      $"`{junk}` → Unresolved 且原樣通過（實際 `{r.AccountId}` / {r.Kind}）");
            }

            // 不變式 ⑤：已銷戶名單裡的帳號都不該同時是正式帳號（銷戶閘門的前提）
            sb.Append("## ⑤ 已銷戶 ∩ 正式帳號 = ∅\n");
            foreach (var kv in GetClosedAccounts())
                Check(!canonical.Contains(kv.Key), $"`{kv.Key}` 已銷戶且非正式帳號");
            if (GetClosedAccounts().Count == 0) sb.Append("  （目前沒有已銷戶帳號）\n");

            // ⑥ 撞名清單 —— 不是失敗，但是**必須被看見的資料狀態**。
            // 一個名字同時是「帳戶」與「persona」時，解析器讓帳戶贏；
            // 而那代表：以該 persona 名義產生的收入會留在同名帳戶，不會流向它 agent 的 bank。
            // 這在舊世代 bank 上通常正是想要的，但它必須是「有人看過並同意」的，不是靜默生效。
            sb.Append("## ⑥ 撞名（正式帳號優先，刻意行為）\n");
            if (collisions.Count == 0) sb.Append("  （無撞名）\n");
            else foreach (var c in collisions) sb.Append("  ⚠ ").Append(c).Append('\n');

            string verdict = fail == 0
                ? $"✅ 全數通過（{pass}）" + (collisions.Count > 0 ? $"，另有 {collisions.Count} 筆撞名待人看過（見 ⑥）" : "")
                : $"❌ 失敗 {fail} / 通過 {pass}";
            sb.Append($"\n**{verdict}**\n");
            string report = sb.ToString();
            if (fail > 0) Debug.LogError("[Treasury] 帳號解析 SelfTest 有失敗項：\n" + report);
            else Debug.Log("[Treasury] 帳號解析 SelfTest：\n" + report);
            return report;
        }

        /// <summary>該帳號是否已銷戶（以正式拼法比對；呼叫端請先 Resolve）。</summary>
        public static bool IsClosed(string accountId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(accountId)) return false;
            lock (s_Lock)
            {
                EnsureLoaded_NoLock();
                return s_ClosedAccounts.TryGetValue(accountId, out reason);
            }
        }

        /// <summary>已銷戶名單快照（原拼法 → 理由）。</summary>
        public static Dictionary<string, string> GetClosedAccounts()
        {
            lock (s_Lock)
            {
                EnsureLoaded_NoLock();
                return new Dictionary<string, string>(s_ClosedAccounts, StringComparer.Ordinal);
            }
        }

        /// <summary>該帳號是否為註冊在案的正式帳號（bank 值或 system_account）。</summary>
        public static bool IsCanonicalAccount(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return false;
            lock (s_Lock)
            {
                EnsureLoaded_NoLock();
                return s_CanonicalAccounts.Contains(accountId);
            }
        }

        // ==========================================================
        // 區塊職責：列出「解析後會落到這個帳號」的所有 persona —— 銷戶前置檢查用
        // 物理意義：銷戶的硬條件之一是「沒有 persona 綁在它上面」（Tim 2026-08-14）。
        //          綁定關係不是一個欄位，是一條解析鏈（persona → agent → bank），
        //          所以要問的不是「這個帳號的 persona 欄是誰」，是**「誰會解析到這裡」**。
        // 數值影響：純讀。回傳非空 = 不准銷戶。
        // ==========================================================
        public static List<string> GetBoundPersonas(string accountId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(accountId)) return result;
            lock (s_Lock)
            {
                EnsureLoaded_NoLock();
                foreach (var kv in s_PersonaToAgentLower)
                {
                    if (!s_AgentToBankLower.TryGetValue(kv.Value.ToLowerInvariant(), out var bank)) continue;
                    if (string.Equals(bank, accountId, StringComparison.Ordinal)) result.Add(kv.Key);
                }
                // persona 名本身就等於該帳號時（孤兒帳戶的典型成因）也算綁定 ——
                // 那筆錢的來源就是那個 persona 的勞動，銷掉等於把它的歷史抹掉。
                if (s_PersonaToAgentLower.ContainsKey(accountId.ToLowerInvariant())
                    && !result.Contains(accountId.ToLowerInvariant()))
                    result.Add(accountId.ToLowerInvariant());
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }
}
