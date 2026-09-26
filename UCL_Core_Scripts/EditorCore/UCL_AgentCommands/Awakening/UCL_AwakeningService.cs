// 區塊職責：GoodMorning 流程的 static 邏輯層（Plan_Awakening_Flow_Simplification §8.8 R14）——
//          Cmd_GoodMorning 與 UCL_PersonaAgentAdminPage 測試區共用同一份實作，兩入口零複製。
// 物理意義：P1 先落「唯讀半套」：身分解析（persona→agent→bank）、
//          在線守衛判定（lock 檔為真相源）、wake_count 推導（wakes/ 信件數 = 真相源）、
//          全 persona 對帳、brief 生成觸發鏈（就地呼叫 SCP_WakeBrief，不 spawn 任何 process）。
//          P2 才加寫入半套（registry patch-write / lock / token / memo）。
// 數值影響：本檔全部唯讀（RunBrief 例外 —— 它就地呼叫 SCP_WakeBrief.Write，寫檔者是本 process）。
// 對帳義務：wake 信計數規則 ^(\d{6})_.*\.md$ 與 letters 路徑解析**逐字對齊 awakening.py**
//          （list_wake_letters / _resolve_data_path）——兩端規則漂移 = wake 編號分裂，
//          改任一端務必同步改另一端並跑後台「對帳」按鈕全綠。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Awakening
{
    public static class UCL_AwakeningService
    {
        // ===========================================================
        // 區塊：路徑解析 — 對齊 awakening.py 的 override 語意
        // 物理意義：資料根走 UCL_AgentCommandsPath.DataRoot（pointer 檔與 Python 同步）；
        //          其上還有一層 legacy override：_config/tavern_paths.json 的 letters_dir /
        //          session_dir key（deprecated 但 Python 仍 honor —— C# 不讀它就會在設了
        //          override 的機器上兩端各看各的目錄，靜默分裂）。
        // 數值影響：override 空/缺 → DataRoot 預設子路徑（與 Python fallback 逐字同構）。
        // ===========================================================
        static string DataRoot => UCL_AgentCommandsPath.DataRoot;

        public static string RegistryMetaPath => Path.Combine(DataRoot, "AwakenInit", "_registry_meta.json");

        // ===========================================================
        // 區塊職責：persona 檔的**唯一**解析點（C# 這一端）。
        // 物理意義：目前實體位置 = <DataRoot>/AwakenInit/personas/<persona>.json。
        //          本方法出現之前，這條路徑被 10 處 C# 各自 Path.Combine 拼出來
        //          （Cmd_LoginStatus / UCL_LoginStatusPage / UCL_PersonaInspectorPage /
        //           UCL_PersonaAgentAdminPage / UCL_BankResolve /
        //           UCL_ChatTavernIO / UCL_AgentEmailRegistry / UCL_AgentModelRegistry），
        //          Python 端另有 9 處。**多一條路徑的代價不是重複，是遷移時改不完的那幾處
        //          會靜默讀到舊檔** —— 舊檔還在、讀得到，兩邊各自成功、各自綠燈，沒有一格會紅。
        //          ⇒ 本方法存在的理由不是少打字，是讓第二條路徑**沒有地方存在**。
        // 數值影響：純字串組合，不檢查存在性；預設模式下與改動前**逐字相同**（本次收斂的驗收判準）。
        // ⚠ 為什麼家在這裡而不在 UCL_AgentCommandsPath：後者是通用 path helper，
        //   不認得 _config/tavern_paths.json 的 letters_dir override。而 persona 檔之後要
        //   改成「letters 優先」，那時需要 LettersDir 的 override 語意 —— 本類同時擁有兩者。
        // ⚠ 對側契約：Python 等價入口是 _lib/ucl_paths.py 的 personas_dir() / persona_file()。
        //   兩端要一起改 —— 只改一端的後果是兩邊各看各的目錄，而**兩邊都不會報錯**。
        // ⛔ `ResolvePersonaFile` / `PersonasDir` 已退場（2026-08-21，Tim 拍板）：
        //    persona 資料整合到 `letters/<persona>/`（身分欄 profile/、帳號 bank/<區域>.md），
        //    中央 `AwakenInit/personas/` 不再存在。名單走 `UCL_PersonaProfile.PoolNames()`
        //    （判準＝profile/ 目錄存在），欄位走 `UCL_PersonaProfile.GetRaw()`。
        //    ⚠ 留一支能組出「那個檔的路徑」的函式，就是留一個邀請下一個人去直讀的入口 ——
        //      而它會 `File.Exists` 失敗後 fail-soft，症狀是「查無此人」，不是「路徑過期」。
        /// <summary>session **token 表**（`_tokens.json` / `_token_enforce.json`）住的目錄。
        /// ⚠ persona lock **不在這裡**（TASK-0105 起走 <see cref="LockPath"/> → letters/&lt;p&gt;/profile/）。</summary>
        public static string SessionDir => ResolveDataSub("_session");
        public static string LettersDir => ResolveDataSub(Path.Combine("ChatTavern", "baton", "letters"));

        // ===========================================================
        // 區塊職責：資料根底下的子路徑解析（legacy 細粒度 override 已廢除，Tim 2026-08-17 拍板）。
        // 物理意義：原本這裡是 ResolveOverridablePath —— 讀 _config/tavern_paths.json 的
        //          letters_dir / session_dir 逐項覆寫。該機制自 2026-05-28 起被
        //          .agentcommands_root.local pointer 檔取代（整個資料根一次搬遷）。
        //          查證：`git log --all -- _config/tavern_paths.json` 為空 ——
        //          **所有分支、整段歷史都沒有提交過那個檔**，版控裡只有 .example.json 範本。
        // 🩸 為什麼「存在即 raise」而不是安靜移除支援：那個檔是 per-machine / gitignored，
        //   我證得到「從沒被提交」，證不到「沒有任何一台機器留著一份」。
        //   安靜移除支援 ⇒ 那台機器的路徑**無聲改成另一個目錄，兩邊都不報錯**。
        //   ⇒ 用一個吵的失敗換掉一個安靜的漂移。
        // 數值影響：純字串組合；有殘留設定檔時在第一次解析路徑處就炸，不會走到讀寫資料。
        // ⚠ 對側契約：Python 端等價處置在 _lib/ucl_paths.py（同樣 raise，訊息對齊）。
        // ===========================================================
        static string ResolveDataSub(string iDefaultSub)
        {
            string aLegacy = Path.Combine(DataRoot, "_config", "tavern_paths.json");
            if (File.Exists(aLegacy))
                throw new Exception(
                    $"[AwakeningService] 偵測到已廢除的細粒度路徑覆寫檔：{aLegacy}\n"
                    + "  該機制已被 <repo-root>/.agentcommands_root.local pointer 檔取代（整個資料根一次搬遷）。\n"
                    + "  處置：把 letters_dir / session_dir 的意圖改成資料根 override（控制台「AgentCommands 路徑」→ 套用），\n"
                    + "        然後刪除或改名該檔（例如加 .disabled 後綴）。\n"
                    + "  ⚠ 這裡刻意不 fallback —— 靜默改讀另一個目錄比停下來糟。");
            return Path.Combine(DataRoot, iDefaultSub);
        }

        // ===========================================================
        // 區塊：agent / bank 解析
        // ⛔ 規則本體在 `SCP_BankAccountResolver`（TASK-0269 起唯一一份）——
        //   這裡只剩 alias fallback；⚠ 想在這裡加解析邏輯之前，先問它是不是該加在那一份上。
        // ===========================================================
        /// <summary>內建 alias fallback。key 一律小寫。</summary>
        static readonly Dictionary<string, string> s_DefaultAgentAliases = new Dictionary<string, string>
        {
            { "claude", "claude-code" },
            { "anthropic", "claude-code" },
        };

        /// <summary>把輸入 agent 字串歸 canonical key：直中 → case-insensitive → alias → 原樣返回。</summary>
        public static string NormalizeAgent(UCL_RegistryMeta iMeta, string iAgent)
        {
            if (string.IsNullOrEmpty(iAgent)) return iAgent;
            var aBanks = iMeta.agent_banks;
            if (aBanks.ContainsKey(iAgent)) return iAgent;
            string aLower = iAgent.ToLowerInvariant();
            foreach (var aKey in aBanks.Keys)
                if (aKey.ToLowerInvariant() == aLower) return aKey;
            var aMerged = new Dictionary<string, string>();
            foreach (var kv in s_DefaultAgentAliases) aMerged[kv.Key] = kv.Value;
            foreach (var kv in iMeta.agent_aliases) aMerged[kv.Key.ToLowerInvariant()] = kv.Value;
            if (aMerged.TryGetValue(aLower, out string aCanonical))
            {
                if (aBanks.ContainsKey(aCanonical)) return aCanonical;
                foreach (var aKey in aBanks.Keys)
                    if (aKey.ToLowerInvariant() == aCanonical.ToLowerInvariant()) return aKey;
                return aCanonical;
            }
            return iAgent;
        }

        // ===========================================================
        // 區塊職責：agent → 帳號。**合一模式：agent id 就是帳號 id，一跳到底。**
        // 物理意義：解析端 2026-08-20 就改成這樣了，
        //          而這支 C# 對偶**沒跟上** —— 它還在走 `agent_banks` 兩跳。
        // 🩸 那個落差今天被量到（basecamp 2026-08-21）：它把 `claude-code` 解成
        //   `claude-da-xiaojie`，而該帳戶在 08-20 13:12 就已經改名歸併成 `claude-code`
        //   （帳本有 `account-rename` 那筆），`Treasury/accounts/` 裡根本沒有它。
        //   於是登入寫進 lock 的帳號、brief 印的餘額、晚安廣播、Sculpture 扣款**全指著一個不存在的帳戶**，
        //   而錢真的進得去（孤兒帳戶照樣入帳）—— 沒有任何一層會出聲。
        // ⚠ **刻意不走 `NormalizeAgent`**（照 python 那條血證）：大小寫歸一會把 `zeta` 歸成 `Zeta`，
        //   而合一之後那是**兩個不同帳戶**，其中一個已銷戶 ⇒ 錢流向合法但禁止金流的帳號，全程零報錯。
        //   alias 表同理不生效：它映射的是舊 agent 名，那些名字合一後已經不是任何人的帳號。
        // 數值影響：`agent_banks` 只在輸入為空時當備援，並**出聲** —— 那是「舊表還有沒有人在讀」的讀數。
        // ===========================================================
        public static string ResolveBankAccount(UCL_RegistryMeta iMeta, string iAgent)
        {
            string aUnified = (iAgent ?? "").Trim();
            if (!string.IsNullOrEmpty(aUnified)) return aUnified;

            UnityEngine.Debug.LogWarning("[Awakening] ResolveBankAccount 收到空 agent —— 退 legacy agent_banks 兩跳鏈。"
                           + "合一之後不該走到這裡，看到這行請查呼叫端為什麼沒有 agent。");
            string aCanonical = NormalizeAgent(iMeta, iAgent);
            if (iMeta != null && iMeta.agent_banks.TryGetValue(aCanonical, out string aBank)) return aBank;
            return aCanonical;   // ⛔ 不再 derive `<agent>-da-xiaojie`：那是孤兒帳戶製造機（summit 2026-08-14 同修）
        }

        // ===========================================================
        // 區塊：wake_count 推導 — 真相源 = wakes/ 信件數（Tim 2026-07-31 拍板）
        // 物理意義：檔名規則 ^(\d{6})_.*\.md$（awakening.py _WAKE_LETTER_RE 逐字對齊）。
        //          「本次 wake 編號」= 信件數 + 1（已完成的 wake 數 = 收尾信數，本次還沒寫信）。
        // ===========================================================
        static readonly Regex s_WakeLetterRe = new Regex(@"^\d{6}_.*\.md$");

        public static int WakeLetterCount(string iPersona)
        {
            string aDir = Path.Combine(LettersDir, iPersona, "wakes");
            if (!Directory.Exists(aDir)) return 0;
            return Directory.GetFiles(aDir).Count(f => s_WakeLetterRe.IsMatch(Path.GetFileName(f)));
        }

        // ===========================================================
        // 區塊：在線守衛 — lock 檔存在與否是唯一判準（registry status 只是快取，不得拿快取否決事實）
        // 物理意義：lock 住 letters/<p>/profile/_session.json（TASK-0105），版面唯一實作 UCL_LettersPath.SessionLock。
        // ===========================================================
        public static string LockPath(string iPersona) => UCL_LettersPath.SessionLock(iPersona);

        public static UCL_SessionLockData ReadLock(string iPersona)
        {
            string aPath = LockPath(iPersona);
            // 🔴 這裡原本是 `if (!File.Exists(aPath)) return null;`（TASK-0265）。
            //   ⛔ 那一行的代價不是「少讀一個檔」：`null` 在呼叫端的語意是**「這個 persona 沒有登入」**，
            //     而那正是早安「同一個 persona 不得同時登入兩次」那道守衛讀的東西。
            //   ⇒ lock 檔換檔那一瞬間（實測窗口 6.4~40.9%）讀到 false ⇒ **守衛放行**，而沒有任何一層會叫。
            //   ⚠ `Busy` 仍然回 `null`（呼叫端的型別只有「有/沒有」兩種），⛔ 但它**出聲**：
            //     不出聲的話，「真的沒登入」與「我這次沒讀到」在日誌上也同形。
            if (!UCL_AtomicFileRead.TryReadAllText(aPath, out _, out UCL_FileReadState aState))
            {
                if (aState == UCL_FileReadState.Busy)
                    UnityEngine.Debug.LogWarning(UCL_AtomicFileRead.DescribeBusy(aPath)
                        + $" ⇒ 本次把 `{iPersona}` 讀成「沒有 lock」，⚠ 那可能是錯的。");
                return null;
            }
            try { return UCL_SessionLockData.LoadFromFile(aPath); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[AwakeningService] lock 解析失敗 {aPath}: {e.Message}");
                return null;
            }
        }

        public static bool IsOnline(string iPersona) => File.Exists(LockPath(iPersona));

        // ===========================================================
        // 區塊：全 persona 對帳 — P1 驗收核心（C# 推導 vs registry 快取 vs lock 實況）
        // 物理意義：delta 四分支語意沿用 awakening.py cmd_morning（=1 正常 / =0 兩種可能 /
        //          >1 快取落後 / <0 快取超前要人工看）——這裡是唯讀對帳，只報症狀不改值。
        // ===========================================================
        public static string AuditReport()
        {
            var aSb = new StringBuilder();
            var aMeta = UCL_RegistryMeta.LoadFromFile(RegistryMetaPath);
            aSb.AppendLine($"# 🧪 Awakening 對帳（C# 唯讀掃描） ts=`{NowLocal()}`（本地時間）");
            aSb.AppendLine();
            aSb.AppendLine($"- DataRoot: `{DataRoot}`");
            aSb.AppendLine($"- LettersDir: `{LettersDir}`　SessionDir: `{SessionDir}`");
            aSb.AppendLine($"- agent_banks: {aMeta.agent_banks.Count} 筆");
            aSb.AppendLine();
            aSb.AppendLine("| Persona | 帳號（agent id） | 綁定來源 | wakes/ 信數 | 下次編號 | status | lock | profile 缺席欄 |");
            aSb.AppendLine("|---|---|---|---|---|---|---|---|");

            // 📌 2026-08-21 起「快取 wake_count vs 信數」那兩欄**沒有意義了** —— wake_count 已改成
            //    由 wakes/ 信數推導（中央 persona json 退場），兩邊同源 ⇒ 永遠相等的比對是裝飾。
            //    （這正是 summit 那條「恆亮警告」的鏡像：一個恆綠的對帳欄同樣不帶資訊。）
            //    改對帳**真的還會分岔的東西**：本區有沒有帳號綁定、profile 有哪些欄缺席。
            int aWarn = 0;
            string aRegion = Treasury.UCL_CentralBankSettings.CurrencyId;
            var aPool = UCL_PersonaProfile.PoolNamesSorted();
            aSb.AppendLine($"（pool 判準＝`letters/<persona>/profile/` 存在，共 {aPool.Count} 位；區域＝{aRegion}）\n");
            foreach (var aName in aPool)
            {
                var aJd = UCL_PersonaProfile.GetRaw(aName, false);
                if (aJd == null)
                {
                    aSb.AppendLine($"| `{aName}` | ✗ 讀不出來（profile/ 壞了？） | | | | | | |");
                    aWarn++; continue;
                }
                var aP = new UCL_PersonaData(); aP.DeserializeFromJson(aJd); aP.name = aName;
                string aAcc = aJd.GetString("agent", "");
                UCL_PersonaProfile.GetBankAccount(aName, aRegion, out string aBankSrc, out _);
                int aLetters = WakeLetterCount(aName);
                var aLock = ReadLock(aName);
                var aSrcs = UCL_PersonaProfile.GetFieldSources(aName);
                var aAbsent = new List<string>();
                if (aSrcs != null)
                    foreach (var kv in aSrcs) if (kv.Value == UCL_PersonaProfile.SRC_ABSENT) aAbsent.Add(kv.Key);
                if (string.IsNullOrEmpty(aAcc)) aWarn++;
                aSb.AppendLine($"| `{aName}` | {(string.IsNullOrEmpty(aAcc) ? "⚠ 無綁定" : aAcc)} | {aBankSrc} | "
                             + $"{aLetters} | {aLetters + 1} | {aJd.GetString("status", "?")} | "
                             + $"{(aLock != null ? "🔒 " + aLock.session_key : "")} | {aAbsent.Count} |");
            }
            aSb.AppendLine();
            aSb.AppendLine(aWarn == 0
                ? $"✅ {aPool.Count} 位都有本區（{aRegion}）帳號綁定，資料讀得出來。"
                : $"⚠ {aWarn} 筆需要人工看一眼（無綁定的人，錢會落央行）。");
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊：brief 生成觸發鏈 — **就地呼叫 SCP_WakeBrief（C#）**，Cmd 與後台頁共用
        // 物理意義：2026-09-01 起 brief 的生產端搬進 SCP_Core（TASK-0097）—— 不再 spawn python。
        //          ⇒ 少一個 process、少一組編碼／環境變數的坑，而且 §6.5 見人與 `cmd people`
        //            從此**是同一支邏輯**（兩處各組一次的症狀不是報錯，是兩邊都不紅的兩個答案）。
        //          Editor 未開時的備援仍是 `senate cmd wake-brief`（原生，不需要 Editor）。
        // 數值影響：回傳含 brief 絕對路徑＋行數 —— 路徑必須進 Cmd 回傳值（Tim 2026-08-13 拍板）。
        //          ⚠ 新鮮度判定**照舊保留**：它擋的是「檔在但不是這次產生的」，
        //            而那隻病與生產端是誰無關（wake#49 讀到前一天那份 1271 行的血證）。
        // ===========================================================
        public const string PROC_TAG = "awakening_service_brief";

        /// <summary>
        /// awakening.py 絕對路徑解析。
        /// <para>⚠ 2026-09-01 起 <see cref="RunBrief"/> **不再用它**（brief 生產端已搬進 SCP_Core）。
        /// 留著是因為還有別的呼叫端；哪天真的零呼叫端就直接刪，不留 stub。</para>
        /// ⚠ **只能在主執行緒呼叫**（內部走 UCL_EditorPath.CorePath =
        /// AssetDatabase.FindAssets）——背景緒要用時，先在主執行緒解析好再把結果傳進去
        /// （RunBrief 的 iScriptPath 參數就是為此存在；快取暖了之後背景緒僥倖能跑，冷啟動必炸）。
        /// </summary>
        public static string ResolveAwakeningScriptPath()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel)) return null;
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/awakening.py"));
            return File.Exists(aScript) ? aScript : null;
        }

        // ⛔ `ResolveBankBalanceArg` 已移除（Tim 2026-08-21）：帳號與餘額改由 Cmd_GoodMorning 的
        //    回傳檔印（C# 端＝真相源），python brief 不再複述它自己查不到的數。
        //    原本存在的理由是避開「python 全掃 14,985 檔帳本」的 112s（wake#49 撞 120s timeout）；
        //    現在連印都不印，那個成本從結構上消失，而不是被一層快取繞過。
        //    🩸 它同時是一隻 bug 的載體：它用**正向鏈** `ResolveBankAccount` 解帳號，
        //    解出 `claude-da-xiaojie`（`Treasury/accounts/` 裡**不存在**）並印「餘額 0」，
        //    而錢實際在 `claude-code`。查無此帳戶與沒錢印成同一個字，就沒有人會去追。
        /// <summary>
        /// 生成 brief（就地呼叫 <see cref="SCP.Core.Letters.SCP_WakeBrief"/>，不 spawn 任何 process）。
        /// </summary>
        /// <param name="iTimeoutMs">保留參數 —— 已無 process 可逾時，留著是為了不動呼叫端簽章。</param>
        /// <param name="iScriptPath">保留參數 —— 同上（python 腳本路徑已不再需要）。</param>
        public static (bool ok, string report, string briefPath, int briefLines) RunBrief(
            string iPersona, string iCallerName, int iTimeoutMs = 120000, string iScriptPath = null)
        {
            // TASK-0303：本體搬進 SCP_Core（`SCP_Morning.Brief`）—— Senate 的 morning-brief 與這裡呼叫同一份，
            //   新鮮度判定（mtime 晚於本次起點）也在那一份裡。iTimeoutMs／iScriptPath 只為不動呼叫端簽章。
            var aRes = SCP.Core.Letters.SCP_Morning.Brief(MorningRoots(), iPersona);
            return (aRes.Ok, aRes.Report, aRes.BriefPath, aRes.BriefLines);
        }

        // ===========================================================
        // 區塊：晚安／後台共用的路徑與時間工具。
        // ⚠ 登入的寫入（lock／_tokens.json／memo／profile）已經不在本檔 —— 在 SCP_Core `SCP_Morning.Wake`（TASK-0303）。
        //   本檔剩下的寫入是晚安那一側（StepCheck／ExpireTokens／WriteWakeLetter…）。
        // ===========================================================
        public static string MemosDir => ResolveDataSub(Path.Combine("ChatTavern", "baton", "memos"));

        public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        /// <summary>本地時間字串 —— **只給人讀的 payload 標頭用**（自由時間等約定都以本地時間溝通，
        /// Tim 2026-08-13 拍板）。存檔欄位（registry/lock/token 的 *_at）仍一律 UTC ISO，與 python 端對齊。</summary>
        public static string NowLocal() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz");

        // ===========================================================
        // 區塊職責：更新 persona 的 now_status（§8.5）—— 「我現在在做什麼」一句話＋時間戳。
        // 物理意義：now_status 是活體狀態，寫入通道只有本函式
        //          （呼叫端＝Cmd_Tavern post 的 status 參數、Cmd_Coding 開場／更新／收場）。
        //          TASK-0294（Tim 2026-09-25）：**只寫 `cmd/now_status.json`，⛔ 不再改寫 lock** ——
        //          lock 只在上線寫、下線刪（原本整檔重寫 lock，每次都開一個「lock 不存在」的窗口）。
        //          狀態檔帶 lock 的 session_key ＋ locked_at：讀取端兩格都對上才採用 ⇒ 上一場的狀態不會掛到新的一場。
        //          ⛔ 只帶 session_key 不夠：它是 `{actual_agent}-{persona}` 的常數，同 agent 重新登入完全相同
        //          （TASK-0294 QA @kotoko）；locked_at 只在登入時寫、每次重生。
        // 數值影響：lock 不存在或讀不了 ⇒ no-op 回 false（沒登入就沒有「現在狀態」可言）；lock 只讀不寫。
        // ===========================================================
        public static bool UpdateNowStatus(string iPersona, string iStatus)
        {
            if (!UCL_AtomicFileRead.TryReadAllText(LockPath(iPersona), out string aLockText, out _)) return false;
            try
            {
                var aLock = JsonData.ParseJson(aLockText);
                if (aLock == null) return false;
                var aStatus = new JsonData();
                aStatus["session_key"] = new JsonData(aLock.GetString("session_key", ""));
                aStatus["locked_at"] = new JsonData(aLock.GetString("locked_at", ""));
                aStatus["now_status"] = new JsonData(iStatus ?? "");
                aStatus["status_updated_at"] = new JsonData(NowIso());
                UCL_LettersPath.EnsureCmdDir(iPersona);
                AtomicWrite(UCL_LettersPath.NowStatus(iPersona), aStatus.ToJsonBeautify());
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Awakening] UpdateNowStatus({iPersona}) 失敗：{e.Message}");
                return false;
            }
        }


        static void AtomicWrite(string iPath, string iContent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath));
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        /// <summary>讀 md frontmatter 單欄（port 自 awakening._read_frontmatter_field；找不到回空字串）。</summary>
        public static string ReadFrontmatterField(string iPath, string iField)
        {
            try
            {
                using (var aReader = new StreamReader(iPath))
                {
                    string aLine = aReader.ReadLine();
                    if (aLine == null || aLine.Trim() != "---") return "";
                    string aPrefix = iField + ":";
                    for (int i = 0; i < 100; i++)
                    {
                        aLine = aReader.ReadLine();
                        if (aLine == null || aLine.Trim() == "---") return "";
                        if (aLine.StartsWith(aPrefix))
                            return aLine.Substring(aPrefix.Length).Trim().Trim('"', '\'');
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[AwakeningService] frontmatter 讀取失敗 {iPath}: {e.Message}");
            }
            return "";
        }

        /// <summary>
        /// 區塊職責：寫 md frontmatter 單欄（<see cref="ReadFrontmatterField"/> 的對偶，刻意放在它旁邊）。
        /// 物理意義：只動 frontmatter 那一段，**正文一個字都不碰** —— 活動 md 的正文是給人讀的說明文件，
        ///          用 GUI 改設定不該有機會改到它。欄位已存在→就地換值；不存在→附加在 frontmatter 尾端。
        /// 數值影響：值含 `:`／`#`／前後空白時自動加雙引號（否則 YAML 讀回來會截斷或變成註解）。
        ///          原子替換（.tmp → move）：半寫的 md 會讓下次掃描讀到殘缺 frontmatter。
        /// 失敗處置：檔案不存在／沒有 frontmatter 起始 `---` → 回 false 並留 log，**不代為新建**
        ///          （替沒有 frontmatter 的檔硬生一段，等於替使用者決定那個檔是什麼）。
        /// </summary>
        public static bool WriteFrontmatterField(string iPath, string iField, string iValue)
        {
            try
            {
                if (!File.Exists(iPath)) { UnityEngine.Debug.LogWarning($"[AwakeningService] frontmatter 寫入失敗，檔案不存在：{iPath}"); return false; }
                var aLines = new List<string>(File.ReadAllLines(iPath));
                if (aLines.Count == 0 || aLines[0].Trim() != "---")
                {
                    UnityEngine.Debug.LogWarning($"[AwakeningService] frontmatter 寫入失敗，缺起始 ---：{iPath}");
                    return false;
                }
                int aEnd = -1;
                for (int i = 1; i < aLines.Count; i++)
                    if (aLines[i].Trim() == "---") { aEnd = i; break; }
                if (aEnd < 0) { UnityEngine.Debug.LogWarning($"[AwakeningService] frontmatter 寫入失敗，缺結束 ---：{iPath}"); return false; }

                string aRaw = iValue ?? "";
                // 需要引號的情形：含分隔符/註解符、或前後有空白（YAML 會 trim 掉而使值悄悄變樣）
                bool aNeedQuote = aRaw.Contains(":") || aRaw.Contains("#") || aRaw != aRaw.Trim();
                string aOut = aNeedQuote ? $"\"{aRaw.Replace("\"", "\\\"")}\"" : aRaw;
                string aLine = $"{iField}: {aOut}";

                int aFound = -1;
                string aPrefix = iField + ":";
                for (int i = 1; i < aEnd; i++)
                    if (aLines[i].StartsWith(aPrefix)) { aFound = i; break; }
                if (aFound >= 0) aLines[aFound] = aLine;
                else aLines.Insert(aEnd, aLine);

                string aTmp = iPath + ".tmp";
                File.WriteAllLines(aTmp, aLines, new UTF8Encoding(false));
                if (File.Exists(iPath)) File.Delete(iPath);
                File.Move(aTmp, iPath);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[AwakeningService] frontmatter 寫入失敗 {iPath}: {e.Message}");
                return false;
            }
        }

        // 走 UCL_ActivePersonaLocks 唯一掃描實作。2026-08-19 收斂時語意順手修正：
        // 舊版只看檔案存在（過期 lock 也被列成「在線」），現在名副其實只列未過期的。
        public static List<string> OnlinePersonas()
        {
            var aList = new List<string>();
            foreach (var l in UCL_ActivePersonaLocks.ListOnline()) aList.Add(l.Persona);
            return aList;
        }

        /// <summary>step 回傳值落檔路徑 —— persona 步驟放 letters/&lt;persona&gt;/cmd/（與 wake brief 同層同慣例），
        /// 目錄本身即宣告「機器寫的、每次該步驟重跑即覆寫」（Tim 2026-08-13 拍板：每步回傳值落檔供 QA）。</summary>
        // 落點走 UCL_LettersPath（版面唯一實作，Plan_Letters_Dir_Layout §8.2 批次⑤）——
        // 原本在這裡自己 Combine 一次，那是「letters 底下版面」的第 N 種算法。
        // ⚠ 對側契約：python 端等價入口 = `_lib/ucl_paths.py::letters_cmd_payload()`。
        public static string StepPayloadPath(string iPersona, string iStep)
            => UCL_LettersPath.CmdPayload(iPersona, "goodmorning", iStep);

        public class StepResult
        {
            public bool ok;
            public bool blocked;      // 守衛/前置檢查擋下（非例外、狀態零副作用）
            public string report = "";
        }

        // ===========================================================
        // 區塊：step=wake —— 本體在 SCP_Core（`SCP_Morning.Wake`，TASK-0303）。
        // 物理意義：Senate 的 morning-wake 與 Editor 的 GoodMorning／後台頁呼叫**同一份**實作 ——
        //          兩個寫入端各寫一份 lock／_tokens.json 的日子結束（2026-09-26 Tim 拍板：Editor 版改呼叫 SCP_Core）。
        //          intro 前置檢查與標頭同樣在 SCP_Morning（PrecheckIntro／BuildIntroHeader）。
        // ===========================================================
        public static StepResult StepWake(string iPersona, string iModelArg, string iActualAgentArg, string iEnvMarker)
        {
            var aRes = SCP.Core.Letters.SCP_Morning.Wake(MorningRoots(), iPersona, iModelArg, iActualAgentArg,
                string.IsNullOrEmpty(iEnvMarker) ? "editor" : iEnvMarker);
            return new StepResult { ok = aRes.Ok, blocked = aRes.Blocked, report = aRes.Report };
        }

        /// <summary>早安邏輯層（SCP_Core）要的三個根 —— 由 Editor 端的路徑解析器給。</summary>
        public static SCP.Core.Letters.SCP_MorningRoots MorningRoots() => new SCP.Core.Letters.SCP_MorningRoots
        {
            DataRoot = UCL_AgentCommandsPath.DataRoot.Replace('\\', '/'),
            LettersRoot = LettersDir.Replace('\\', '/'),
            ProjectRoot = UCL_RepoPath.RepoRoot.Replace('\\', '/'),
        };

        /// <summary>brief 檔內容摘要（QA 欄位/格式用）：frontmatter 全文＋各段標題行。</summary>
        public static string SummarizeBrief(string iBriefPath, int iMaxLines = 80)
        {
            if (string.IsNullOrEmpty(iBriefPath) || !File.Exists(iBriefPath)) return "(brief 檔不存在)";
            var aOut = new StringBuilder();
            var aLines = File.ReadAllLines(iBriefPath);
            bool aInFrontmatter = false;
            int aEmitted = 0;
            for (int i = 0; i < aLines.Length && aEmitted < iMaxLines; i++)
            {
                string aLine = aLines[i];
                if (i == 0 && aLine.Trim() == "---") { aInFrontmatter = true; aOut.AppendLine(aLine); aEmitted++; continue; }
                if (aInFrontmatter)
                {
                    aOut.AppendLine(aLine); aEmitted++;
                    if (aLine.Trim() == "---") aInFrontmatter = false;
                    continue;
                }
                if (aLine.StartsWith("#")) { aOut.AppendLine($"[L{i + 1}] {aLine}"); aEmitted++; }
            }
            aOut.AppendLine($"（共 {aLines.Length} 行；上面是 frontmatter 全文＋段落標題索引）");
            return aOut.ToString();
        }
    }
}
#endif
