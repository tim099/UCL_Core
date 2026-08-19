// 區塊職責：GoodMorning 流程的 static 邏輯層（Plan_Awakening_Flow_Simplification §8.8 R14）——
//          Cmd_GoodMorning 與 UCL_PersonaAgentAdminPage 測試區共用同一份實作，兩入口零複製。
// 物理意義：P1 先落「唯讀半套」：身分解析（persona→agent→bank，port 自 _lib/bank_resolver.py）、
//          在線守衛判定（lock 檔為真相源）、wake_count 推導（wakes/ 信件數 = 真相源）、
//          全 persona 對帳、brief 生成觸發鏈（spawn python，R19/R20）。
//          P2 才加寫入半套（registry patch-write / lock / token / memo）。
// 數值影響：本檔全部唯讀（RunBrief 例外 —— 它 spawn awakening.py brief，寫檔者是 Python 端）。
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

        public static string PersonasDir => Path.Combine(DataRoot, "AwakenInit", "personas");
        public static string RegistryMetaPath => Path.Combine(DataRoot, "AwakenInit", "_registry_meta.json");

        // ===========================================================
        // 區塊職責：persona 檔的**唯一**解析點（C# 這一端）。
        // 物理意義：目前實體位置 = <DataRoot>/AwakenInit/personas/<persona>.json。
        //          本方法出現之前，這條路徑被 10 處 C# 各自 Path.Combine 拼出來
        //          （Cmd_LoginStatus / UCL_LoginStatusPage / UCL_PersonaInspectorPage /
        //           UCL_PersonaAgentAdminPage / UCL_BankAdminPage / UCL_TreasuryAccountResolver /
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
        // ===========================================================
        public static string ResolvePersonaFile(string iPersona)
            => Path.Combine(PersonasDir, iPersona + ".json").Replace('\\', '/');
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
        // 區塊：agent / bank 解析 — port 自 _lib/bank_resolver.py（規則逐字對齊，改一端同步改另一端）
        // ===========================================================
        /// <summary>內建 alias fallback（bank_resolver.DEFAULT_AGENT_ALIASES）。key 一律小寫。</summary>
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

        /// <summary>agent → bank account。認不出走命名慣例 fallback（= 隱含開新 bank，對齊 Python 語意）。</summary>
        public static string ResolveBankAccount(UCL_RegistryMeta iMeta, string iAgent)
        {
            string aCanonical = NormalizeAgent(iMeta, iAgent);
            if (iMeta.agent_banks.TryGetValue(aCanonical, out string aBank)) return aBank;
            return $"{aCanonical}-da-xiaojie";
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
        // ===========================================================
        public static string LockPath(string iPersona) => Path.Combine(SessionDir, $"_persona_{iPersona}.json");

        public static UCL_SessionLockData ReadLock(string iPersona)
        {
            string aPath = LockPath(iPersona);
            if (!File.Exists(aPath)) return null;
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
            aSb.AppendLine("| Persona | agent→canonical | bank | 快取 wake_count | wakes/ 信數 | 下次編號 | Δ判讀 | status | lock |");
            aSb.AppendLine("|---|---|---|---|---|---|---|---|---|");

            if (!Directory.Exists(PersonasDir))
            {
                aSb.AppendLine($"\n✗ personas 目錄不存在：`{PersonasDir}`");
                return aSb.ToString();
            }
            int aWarn = 0;
            foreach (var aFile in Directory.GetFiles(PersonasDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string aName = Path.GetFileNameWithoutExtension(aFile);
                if (aName.StartsWith("_") || aName.StartsWith(".")) continue;
                UCL_PersonaData aP;
                try { aP = UCL_PersonaData.LoadFromFile(aFile); }
                catch (Exception e)
                {
                    aSb.AppendLine($"| `{aName}` | ✗ 解析失敗: {e.Message} | | | | | | | |");
                    aWarn++;
                    continue;
                }
                if (aP == null) continue;
                string aCanonical = NormalizeAgent(aMeta, aP.agent);
                string aBank = ResolveBankAccount(aMeta, aP.agent);
                int aLetters = WakeLetterCount(aName);
                int aDerivedNext = aLetters + 1;
                var aLock = ReadLock(aName);
                bool aOnline = aLock != null;
                // Δ判讀：在線中 快取==信數+1 正常；靜止 快取==信數 正常（收尾信已補齊編號）。
                // 其餘照 python 四分支語意標記 —— 這裡只報症狀，成因至少兩種的不認領單一結論。
                int aExpected = aOnline ? aLetters + 1 : aLetters;
                string aVerdict;
                if (aP.wake_count == aExpected) aVerdict = "✓";
                else if (aP.wake_count < aExpected) { aVerdict = $"🔧 快取落後 {aExpected - aP.wake_count}"; aWarn++; }
                else { aVerdict = $"⚠ 快取超前 {aP.wake_count - aExpected}（收尾信遺失或上次未走完晚安）"; aWarn++; }
                string aAgentCol = aP.agent == aCanonical ? aP.agent : $"{aP.agent}→{aCanonical}";
                string aLockCol = aOnline ? $"🔒 {aLock.session_key}" : "";
                aSb.AppendLine($"| `{aName}` | {aAgentCol} | {aBank} | {aP.wake_count} | {aLetters} | {aDerivedNext} | {aVerdict} | {aP.status} | {aLockCol} |");
            }
            aSb.AppendLine();
            aSb.AppendLine(aWarn == 0
                ? "✅ 全綠 —— C# 推導與磁碟/快取一致（判準：在線 快取=信數+1；靜止 快取=信數）。"
                : $"⚠ {aWarn} 筆需要人工看一眼（判讀欄非 ✓ 者）。");
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊：brief 生成觸發鏈（R19/R20）— spawn python awakening.py brief，Cmd 與後台頁共用
        // 物理意義：brief 生成留 Python（R18 非登入功能）；正常流程一律經本鏈觸發（Cmd step=brief
        //          或後台按鈕），agent 直跑 awakening.py brief 只是 Editor 未開時的備援。
        // 數值影響：Process 走 UCL_ProcessCli（ProcessRegistry 登記＋逾時 kill，硬規則不裸 Process.Start）。
        //          回傳含 brief 絕對路徑＋行數 —— 路徑必須進 Cmd 回傳值（Tim 2026-08-13 拍板）。
        // ===========================================================
        public const string PROC_TAG = "awakening_service_brief";

        /// <summary>
        /// awakening.py 絕對路徑解析。⚠ **只能在主執行緒呼叫**（內部走 UCL_EditorPath.CorePath =
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

        /// <summary>
        /// 區塊職責：解析 persona → bank → 目前餘額，組成 brief 的 --bank-balance 值。
        /// 物理意義：餘額的唯一算法擁有者是 UCL_TreasuryLedger（增量快取 + snapshot + 每日結帳）。
        ///          brief 那行「餘額 N tavern_token」以前由 python 自己全掃帳本重算 ——
        ///          🩸 14,985 檔逐檔 json.load，冷檔案快取下近兩分鐘，step=brief 被拖到 112s，
        ///          08-13 那次直接撞 120s timeout 被 kill。改成「Cmd 流程內查好餵過去」。
        /// 數值影響：回傳 "<bank>=<balance>"（python 端帳號對不上就忽略並自行重算，不會印錯帳號的數）。
        ///          任何一步解析不出來 → 回 null（= 不餵，python 走舊的重算路，慢但正確）。
        /// ⚠ **必須在主執行緒呼叫** —— DataRoot / ResolveData 走 PlayerPrefs 與 AssetDatabase。
        /// </summary>
        public static string ResolveBankBalanceArg(string iPersona)
        {
            try
            {
                string aPersonaPath = Path.Combine(PersonasDir, iPersona + ".json");
                if (!File.Exists(aPersonaPath)) return null;
                var aMeta = UCL_RegistryMeta.LoadFromFile(RegistryMetaPath);
                var aRaw = JsonData.ParseJson(File.ReadAllText(aPersonaPath));
                string aAgent = NormalizeAgent(aMeta, aRaw.GetString("agent", ""));
                if (string.IsNullOrEmpty(aAgent)) return null;
                string aBank = ResolveBankAccount(aMeta, aAgent);
                if (string.IsNullOrEmpty(aBank)) return null;
                return $"{aBank}={Treasury.UCL_TreasuryLedger.GetBalance(aBank, "tavern_token")}";
            }
            catch (Exception e)
            {
                // 這是純顯示欄位的加速路，壞了就不餵 —— 不讓它擋掉整個 morning。
                UnityEngine.Debug.LogWarning($"[AwakeningService] 餘額預查失敗（brief 改自行重算）：{e.Message}");
                return null;
            }
        }

        public static (bool ok, string report, string briefPath, int briefLines) RunBrief(
            string iPersona, string iCallerName, int iTimeoutMs = 120000, string iScriptPath = null,
            string iBankBalanceArg = null)
        {
            string aScript = iScriptPath ?? ResolveAwakeningScriptPath();
            if (string.IsNullOrEmpty(aScript))
                return (false, "✗ 解析不到 awakening.py（CorePath 空或檔案不存在；背景緒呼叫請先在主執行緒 ResolveAwakeningScriptPath）", null, 0);

            // 區塊職責：記下本次執行的起始時刻 —— brief 檔的驗收要靠它
            // 物理意義：驗收條件原本是「檔存在 + 行數 > 0」，而**隔夜殘留完全滿足這兩項**。
            //          🩸 wake#49（2026-08-13）：brief 撞 120s 上限被 kill，回傳檔照樣印
            //          「📄 brief: …（1271 行）」—— 那是**前一天那份檔**的行數。
            //          當天沒被騙到只因為人手動去看了 mtime；而該用的尺早就裝在下一格
            //          （PrecheckIntro 會比 brief mtime vs locked_at），只是 brief 這一步自己沒拿。
            // 數值影響：見下方 aFresh 判定。時間基準刻意用「本次執行開始」而不是 lock 的 locked_at ——
            //          前者自成一格、不必多讀一個檔，而且語意更強：檔案必須是**這一次**寫出來的。
            DateTime aStartedUtc = DateTime.UtcNow;

            string aArgs = $"\"{aScript}\" brief --persona \"{iPersona}\"";
            if (!string.IsNullOrEmpty(iBankBalanceArg))
                aArgs += $" --bank-balance \"{iBankBalanceArg}\"";
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aArgs, UCL_RepoPath.RepoRoot,
                PROC_TAG, iCallerName, iTimeoutMs);

            // stderr 不丟掉 —— awakening.py 把警告印在 stderr，只收 stdout 會讓報告假乾淨。
            var aSb = new StringBuilder();
            aSb.AppendLine($"$ python awakening.py brief --persona {iPersona}   (exit={aExit})");
            aSb.AppendLine(aSo ?? "");
            if (!string.IsNullOrEmpty(aSe)) aSb.AppendLine("── stderr ──").AppendLine(aSe);

            // 驗收看落地檔不看 stdout：brief 檔存在且行數 > 0 才算生成成功。
            string aBriefPath = UCL_LettersPath.CmdPayload(iPersona, "wake", "brief");
            int aLines = 0;
            bool aExists = File.Exists(aBriefPath);
            if (aExists)
            {
                try { aLines = File.ReadAllLines(aBriefPath).Length; }
                catch (Exception e) { aSb.AppendLine($"⚠ brief 行數讀取失敗: {e.Message}"); }
            }
            // 區塊職責：新鮮度判定 —— 這份 brief 是不是**這一次**產生的
            // 邊界：容許 2 秒回溯（檔案系統時間戳解析度與時鐘微幅偏移），
            //      而隔夜殘留差的是「小時」等級，2 秒容差擋不住它才叫失效。
            //      讀不到 mtime 時**當成不新鮮**（不是當成新鮮）—— 讀不到與很新是兩件事。
            DateTime aBriefUtc = DateTime.MinValue;
            bool aFresh = false;
            if (aExists)
            {
                try
                {
                    aBriefUtc = File.GetLastWriteTimeUtc(aBriefPath);
                    aFresh = aBriefUtc >= aStartedUtc.AddSeconds(-2);
                }
                catch (Exception e) { aSb.AppendLine($"⚠ brief mtime 讀取失敗（視為不新鮮）: {e.Message}"); }
            }

            aSb.AppendLine(!aExists
                ? $"✗ brief 檔不存在：`{aBriefPath}`"
                : aFresh
                    ? $"📄 brief: `{aBriefPath}`（{aLines} 行，mtime {aBriefUtc:yyyy-MM-dd HH:mm:ss}Z 晚於本次執行起點）"
                    : $"✗ brief 檔存在但**不是本次產生的**：`{aBriefPath}`"
                      + $"（{aLines} 行，mtime {aBriefUtc:yyyy-MM-dd HH:mm:ss}Z < 本次起點 {aStartedUtc:yyyy-MM-dd HH:mm:ss}Z）"
                      + " —— 隔夜殘留／前次遺留，不算生成成功。多半是本次被 timeout kill 了。");
            bool aOk = aExit == 0 && aExists && aLines > 0 && aFresh;
            return (aOk, aSb.ToString(), aExists ? aBriefPath : null, aLines);
        }

        // ===========================================================
        // 區塊：寫入半套（P2 step=wake / P3 step=intro）— port 自 awakening.py cmd_morning ①-⑥
        // 物理意義：登入寫入者收斂為 C# 單端（R18）。與 Python 端的刻意差異只有三處，全屬 audit 欄：
        //          ① claim_origin：Python 是 caller env hash，這裡是 "cmd-goodmorning:<env_marker>"
        //            （Editor 代跑，caller env 不可見；該欄本來就 audit-only 不參與判定）
        //          ② pid：Editor 進程 pid（Python 是 CLI pid；同樣純診斷欄）
        //          ③（已收斂，BUG-6）檔案排版：registry 家族 canonical = ToJsonBeautify（tab）。
        //            Python 端 awakening.dump_registry_json 逐字元鏡射本格式 —— **改任一端排版必須同步另一端**。
        // 數值影響：寫 registry（patch-write）/ lock / _tokens.json / memo 四處 —— 全部 tmp+replace 原子寫。
        // ===========================================================
        public static string MemosDir => ResolveDataSub(Path.Combine("ChatTavern", "baton", "memos"));

        public const int SESSION_LOCK_TTL_HOURS = 24;   // ⚠ 與 awakening.py SESSION_LOCK_TTL_HOURS / Cmd_Tavern PERSONA_LOCK_TTL_HOURS 同步
        public const int CONSOLIDATE_GAP_THRESHOLD = 10;

        public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        /// <summary>本地時間字串 —— **只給人讀的 payload 標頭用**（自由時間等約定都以本地時間溝通，
        /// Tim 2026-08-13 拍板）。存檔欄位（registry/lock/token 的 *_at）仍一律 UTC ISO，與 python 端對齊。</summary>
        public static string NowLocal() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz");

        static void AtomicWrite(string iPath, string iContent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath));
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        /// <summary>實際承載 agent 正規化 — port 自 awakening.normalize_actual_agent（alias 直中 → 最近似候選）。</summary>
        public static (string canonical, bool changed) NormalizeActualAgent(string iValue)
        {
            string aRaw = (iValue ?? "").Trim();
            if (string.IsNullOrEmpty(aRaw)) return ("", false);
            string aNorm = Regex.Replace(aRaw.ToLowerInvariant(), "[^a-z0-9]", "");
            var aAliases = new Dictionary<string, string>
            {
                { "codex", "Codex" }, { "claude", "ClaudeCode" },
                { "claudecode", "ClaudeCode" }, { "antigravity", "Antigravity" },
            };
            if (aAliases.TryGetValue(aNorm, out string aHit)) return (aHit, aRaw != aHit);
            string[] aCandidates = { "Codex", "ClaudeCode", "Antigravity" };
            string aBest = aCandidates.OrderByDescending(c => Similarity(aNorm, c.ToLowerInvariant())).First();
            return (aBest, true);
        }

        // 簡化版相似度（Levenshtein ratio）— Python 端用 difflib，僅在「打了怪字串」的救援路徑會走到，
        // 兩端對 canonical / alias 輸入行為完全一致。
        static double Similarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0) return 1.0;
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                       d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
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

        static IEnumerable<string> WakeLetterFiles(string iPersona)
        {
            string aDir = Path.Combine(LettersDir, iPersona, "wakes");
            if (!Directory.Exists(aDir)) yield break;
            foreach (var f in Directory.GetFiles(aDir).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                if (s_WakeLetterRe.IsMatch(Path.GetFileName(f))) yield return f;
        }

        /// <summary>
        /// 收尾信版面遷移是否 pending — port 自 awakening.letters_migration_pending：
        /// 判準是「頂層還有沒被複製進 wakes/ 的收尾信」（比對檔名尾段），不是「wakes/ 目錄不存在」。
        /// </summary>
        public static bool LettersMigrationPending(string iPersona)
        {
            string aTop = Path.Combine(LettersDir, iPersona);
            if (!Directory.Exists(aTop)) return false;
            var aDone = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in WakeLetterFiles(iPersona))
            {
                string aName = Path.GetFileName(f);
                int aIdx = aName.IndexOf('_');
                aDone.Add(aIdx >= 0 ? aName.Substring(aIdx + 1) : aName);
            }
            foreach (var f in Directory.GetFiles(aTop, "*.md"))
            {
                string aName = Path.GetFileName(f);
                if (aName.StartsWith("_")) continue;
                if (aDone.Contains(aName)) continue;
                if (ReadFrontmatterField(f, "type") != "letter_to_future_self") continue;
                if (!ReadFrontmatterField(f, "trigger").StartsWith("cmd_goodnight")) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 磁碟上的見林既成事實 —— 回 (最大 digest span_end, 該檔的 consolidated_at)；沒 digest 回 (0, null)。
        /// 區塊職責：digest 檔（longterm/wake_&lt;start&gt;-&lt;end&gt;.md）存在就代表那段濃縮真的發生過，
        ///          persona json 的 last_consolidated_wake 只是它的快取。
        /// 物理意義：與 Python memory.latest_digest_span() 同契約（兩端都取**最大 span_end**，
        ///          不取檔名排序最後一個 —— wake 破百後 wake_099-105 會排在 wake_100-110 之後）。
        /// 數值影響：純唯讀（列目錄 + 讀 frontmatter 一欄）；解析不出來的檔名直接跳過。
        /// </summary>
        static (int spanEnd, string at) MaxDigestSpan(string iPersona)
        {
            string aDir = Path.Combine(LettersDir, iPersona, "longterm");
            if (!Directory.Exists(aDir)) return (0, null);
            int aBest = 0; string aBestFile = null;
            foreach (var f in Directory.GetFiles(aDir, "wake_*.md"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"wake_(\d+)-(\d+)");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[2].Value, out int aEnd)) continue;
                if (aEnd > aBest) { aBest = aEnd; aBestFile = f; }
            }
            if (aBestFile == null) return (0, null);
            string aAt = ReadFrontmatterField(aBestFile, "consolidated_at");
            return (aBest, string.IsNullOrEmpty(aAt) ? null : aAt);
        }

        /// <summary>
        /// 見林書籤換算（port 自 rebase_consolidation_bookmark，冪等）——
        /// 有改動時 mutate iRawPersona 並回 (舊, 新)；沒書籤 / 沒變回 null。
        /// 🩸 BUG-4（calli wake#24）：本函式用「written_at &lt;= last_consolidated_at 的收尾信數」重算書籤，
        ///    而那個算法會讓書籤**倒退**（實測：aAt 停在 2026-06-16 → 數出 12，而磁碟上已有 wake_013-023.md）。
        ///    倒退的代價不是少提醒，是 gap 變大 → 假 OVERDUE → 叫人重濃縮同一批信（照做會同名覆寫既有見林）。
        ///    ⇒ 換算結果一律以磁碟 digest 為地板：**書籤永遠不得低於已經寫成檔的 span_end。**
        /// </summary>
        public static (int oldVal, int newVal)? RebaseBookmark(string iPersona, JsonData iRawPersona)
        {
            int aOld = iRawPersona.GetInt("last_consolidated_wake", 0);
            string aAt = iRawPersona.GetString("last_consolidated_at", "");
            var (aFloor, aFloorAt) = MaxDigestSpan(iPersona);
            int aNew = aOld;
            // 換算只在「有舊書籤且有時戳」時成立（那是它的定義域）；算不出來就維持原值，交給地板判。
            if (aOld > 0 && !string.IsNullOrEmpty(aAt))
            {
                int aCounted = WakeLetterFiles(iPersona)
                    .Count(f => string.CompareOrdinal(ReadFrontmatterField(f, "written_at"), aAt) <= 0
                                && !string.IsNullOrEmpty(ReadFrontmatterField(f, "written_at")));
                if (aCounted > 0) aNew = aCounted;
            }
            if (aNew < aFloor)
            {
                aNew = aFloor;
                // 時戳跟著換：留著落後的舊時戳，下次換算又會從舊時戳數出偏低值（地板會擋，但讀數會一直互相矛盾）。
                if (!string.IsNullOrEmpty(aFloorAt)) iRawPersona["last_consolidated_at"] = new JsonData(aFloorAt);
            }
            if (aNew == aOld || aNew <= 0) return null;
            iRawPersona["last_consolidated_wake"] = new JsonData(aNew);
            return (aOld, aNew);
        }

        // ===========================================================
        // 區塊：自我介紹（出生證明）偵測 — 條件步驟 B2 的判準（Tim 2026-08-13 拍板）
        // 物理意義：Docs/Glossary 的 persona 條目是「初始風格＝出生證明」，與憲法（資歷證明）是兩份。
        //          缺件時 next 鏈動態插入 B2（讀完 brief 後補寫），且 step=intro 前置守衛實擋 ——
        //          「跑完 B2 才有 C」是物理保證不是嘴上提示。
        //          搜尋規則對齊 wake_brief._glossary_persona_entry：personas/<P>.md → 根層 <P>.md →
        //          遞迴掃（Cmd_Glossary 新建預設寫根層、慣例放 personas/，寫死任一層都會漏另一層）。
        // ===========================================================
        public const string INTRO_REFERENCE_SLUG = "gura";   // 目前寫得最完整的一份，當新人的參考範例

        public static string FindGlossaryPersonaEntry(string iPersona)
        {
            string aRoot = Path.Combine(UCL_RepoPath.RepoRoot, "Docs", "Glossary");
            if (!Directory.Exists(aRoot)) return null;
            string aDirect = Path.Combine(aRoot, "personas", iPersona + ".md");
            if (File.Exists(aDirect)) return aDirect;
            string aFlat = Path.Combine(aRoot, iPersona + ".md");
            if (File.Exists(aFlat)) return aFlat;
            try
            {
                foreach (var f in Directory.EnumerateFiles(aRoot, iPersona + ".md", SearchOption.AllDirectories))
                    return f;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[AwakeningService] Glossary 掃描失敗: {e.Message}");
            }
            return null;
        }

        /// <summary>B2 條件步驟的提示行（wake / brief 的 next 鏈共用 —— 兩處各寫一份必然漂移）。</summary>
        public static List<string> SelfIntroTodoLines(string iPersona)
        {
            string aRef = FindGlossaryPersonaEntry(INTRO_REFERENCE_SLUG);
            string aRefHint = $"Docs/Glossary/personas/{INTRO_REFERENCE_SLUG}.md";
            if (aRef != null)
            {
                // repo 相對路徑：兩邊都正規化成 forward-slash 再剝前綴（大小寫寬容，Windows 磁碟機字母）
                string aRoot = Path.GetFullPath(UCL_RepoPath.RepoRoot).Replace('\\', '/').TrimEnd('/');
                string aFull = Path.GetFullPath(aRef).Replace('\\', '/');
                aRefHint = aFull.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase)
                    ? aFull.Substring(aRoot.Length).TrimStart('/') : aFull;
            }
            return new List<string>
            {
                $"補**自我介紹**（出生證明）：`Docs/Glossary/personas/{iPersona}.md` 不存在 —— 沒有它 step=intro 會被擋。",
                $"   內容＝初始風格自畫像（我是誰／擅長什麼／說話方式），**親筆**；參考同目錄其他人的寫法（最完整：`{aRefHint}`）。",
                $"   寫法：run_cmd.py run Glossary --arg op=register --arg slug={iPersona} --arg category=persona --arg-file body=<檔>",
                "   ⚠ 工具新建預設寫 Docs/Glossary/ 根層，persona 條目慣例放 personas/，寫完手動搬。",
            };
        }

        public static int KeysOpenCount(string iPersona)
        {
            string aPath = Path.Combine(LettersDir, iPersona, "_keys_open.md");
            if (!File.Exists(aPath)) return 0;
            try { return File.ReadAllLines(aPath).Count(l => l.TrimStart().StartsWith("- [ ]")); }
            catch { return 0; }
        }

        public static List<string> OnlinePersonas()
        {
            var aList = new List<string>();
            if (!Directory.Exists(SessionDir)) return aList;
            foreach (var f in Directory.GetFiles(SessionDir, "_persona_*.json"))
            {
                string aName = Path.GetFileNameWithoutExtension(f);
                aList.Add(aName.Substring("_persona_".Length));
            }
            aList.Sort(StringComparer.Ordinal);
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
        // 區塊：step=wake — cmd_morning ①-⑥ 的 C# 本體（不含 brief 生成、不含廣播）
        // 順序不變式：守衛與遷移判定全過**才**開始寫入；任何 blocked 路徑零副作用。
        // ===========================================================
        public static StepResult StepWake(string iPersona, string iModelArg, string iActualAgentArg, string iEnvMarker)
        {
            var aR = new StringBuilder();
            var aRes = new StepResult();
            aR.AppendLine($"# GoodMorning step=wake persona={iPersona}  ts=`{NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aMeta = UCL_RegistryMeta.LoadFromFile(RegistryMetaPath);
            string aPersonaPath = Path.Combine(PersonasDir, iPersona + ".json");

            // ① persona 必須已註冊 —— 打錯字不該變成「幫你建一個新人格」
            if (!File.Exists(aPersonaPath))
            {
                var aNames = Directory.Exists(PersonasDir)
                    ? Directory.GetFiles(PersonasDir, "*.json").Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !n.StartsWith("_")).OrderBy(n => n, StringComparer.Ordinal).ToList()
                    : new List<string>();
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不存在");
                aR.AppendLine($"- 可選（{aNames.Count}）: {string.Join(", ", aNames)}");
                aR.AppendLine("- exits: 開新 persona 走後台「🧬 Persona & Agent 管理頁」（不從 ritual 開後門）");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }

            var aRaw = JsonData.ParseJson(File.ReadAllText(aPersonaPath));
            var aP = new UCL_PersonaData(); aP.DeserializeFromJson(aRaw); aP.name = iPersona;

            // ② 顯示歸屬 agent / 實際承載 agent 分離（display agent 決定 bank 與對外身分）
            string aAgent = NormalizeAgent(aMeta, aP.agent ?? "");
            if (string.IsNullOrEmpty(aAgent))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 沒有綁定 agent，無法反推");
                aR.AppendLine("- exits: 後台「🧬 Persona & Agent 管理頁」補上 agent 歸屬");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            string aActualRaw = !string.IsNullOrEmpty(iActualAgentArg) ? iActualAgentArg
                : (!string.IsNullOrEmpty(aP.actual_agent) ? aP.actual_agent : aAgent);
            var (aActual, aActualChanged) = NormalizeActualAgent(aActualRaw);
            if (string.IsNullOrEmpty(aActual))
            {
                aR.AppendLine($"## blocked\n- reason: 無實際承載 agent，無法建立 session lock");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            if (aActualChanged) aR.AppendLine($"ℹ actual agent 正規化：'{aActualRaw}' → {aActual}");
            string aBank = ResolveBankAccount(aMeta, aAgent);
            string aSessionKey = $"{aActual}-{iPersona}";
            aR.AppendLine($"- Persona={iPersona} / Agent={aAgent}（顯示歸屬）/ ActualAgent={aActual} / Bank={aBank}");

            // ③ 唯一的中斷條件：該 persona 目前是否在線（lock 為真相源；過期不豁免，R9）
            var aLock = ReadLock(iPersona);
            if (aLock != null)
            {
                bool aExpired = false;
                try
                {
                    aExpired = DateTime.TryParse(aLock.expires_at?.Substring(0, Math.Min(19, aLock.expires_at.Length)),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTime aExp) && DateTime.UtcNow > aExp;
                }
                catch { }
                aR.AppendLine($"## blocked");
                aR.AppendLine($"- reason: ⛔ '{iPersona}' 目前在線 —— 同一個 persona 不得同時登入兩次");
                aR.AppendLine($"- lock: session_key={aLock.session_key} pid={aLock.pid} locked_at={aLock.locked_at}{(aExpired ? " (已過期 — 不自動豁免，R9)" : "")}");
                aR.AppendLine("- exits:");
                aR.AppendLine("  - 讓它先下線：後台「登入狀態」頁登出，或該 session 跑 goodnight，再重跑本步");
                aR.AppendLine("  - brief 沒生出來（morning 中途被砍）→ step=brief 或 awakening.py brief（純本機，不動 lock）");
                aR.AppendLine("  - lock 在但 token 丟了 → awakening.py reissue-token --persona " + iPersona);
                aR.AppendLine("  - 晚安後想續線 → awakening.py relogin --persona " + iPersona);
                aR.AppendLine("- ⚠ 不要改用別的 persona 名繞過去 —— 那是製造分身，比停下來糟");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            if (aP.status == "online")
                aR.AppendLine($"🔧 '{iPersona}' registry status=online 但查無 lock（上次下線沒走完）—— 以 lock 為準視為離線，繼續喚醒。");

            // ③' 收尾信版面遷移 pending → blocked（遷移屬維護功能留 Python，R18/§8.9 P2 卡點②）
            if (LettersMigrationPending(iPersona))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 收尾信版面尚未遷移（頂層有未複製進 wakes/ 的收尾信）——此時推導 wake_count 會算錯歲數");
                aR.AppendLine("- exits: 後台「🗄 維護」區跑 migration（試跑→執行），或 python awakening.py migrate-letters --all --apply");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }

            // ④ wake_count 推導（真相源 = wakes/ 信件數；delta 四分支語意 port 自 cmd_morning）
            int aLetters = WakeLetterCount(iPersona);
            int aDerived = aLetters + 1;
            int aCached = aP.wake_count;
            int aDelta = aDerived - aCached;
            if (aDelta == 1) { /* 正常：上次走完晚安 → 今天編號本來就大 1，不吵 */ }
            else if (aDelta == 0)
                aR.AppendLine($"⚠ wake_count 快取={aCached} 與本次編號={aDerived} 相同 —— 兩種可能：上一次醒來沒留收尾信（本次沿用 #{aDerived}），或本次早安已跑過一次。要判哪一種：看 wakes/ 最新那封的日期。");
            else if (aDelta > 1)
                aR.AppendLine($"🔧 wake_count 快取={aCached} 落後磁碟推導={aDerived} 共 {aDelta - 1} 筆 —— registry 同步漏拍，採磁碟值。");
            else
                aR.AppendLine($"🔧 wake_count 快取={aCached} **大於**磁碟推導={aDerived} —— 收尾信遺失，或別條世界線的帳被算進這條。採磁碟值，但這筆值得人工看一眼。");

            var aRb = RebaseBookmark(iPersona, aRaw);
            if (aRb != null)
                aR.AppendLine($"🔧 見林書籤 last_consolidated_wake {aRb.Value.oldVal} → {aRb.Value.newVal}（換算到 wakes/ 編號；不換算 gap 會變負數，濃縮提醒靜默失效）");

            // ⑤ registry patch-write（只改 owned 欄；identity_vector 等未建模欄原樣保留）
            string aModel = aRaw.GetString("model", "");
            if (!string.IsNullOrEmpty(iModelArg) && aModel != iModelArg) { aRaw["model"] = iModelArg; aModel = iModelArg; }
            aRaw["actual_agent"] = aActual;
            aRaw["wake_count"] = aDerived;
            aRaw["status"] = "online";
            aRaw["availability"] = "idle";
            aRaw["last_active"] = NowIso();
            AtomicWrite(aPersonaPath, aRaw.ToJsonBeautify());

            // ⑥ token（同 persona 舊 active 標 expired，audit trail 保留）+ lock + memo
            string aToken = Guid.NewGuid().ToString("N");
            string aClaimOrigin = $"cmd-goodmorning:{(string.IsNullOrEmpty(iEnvMarker) ? "editor" : iEnvMarker)}";
            string aTokensPath = Path.Combine(SessionDir, "_tokens.json");
            JsonData aTokens = File.Exists(aTokensPath) ? JsonData.ParseJson(File.ReadAllText(aTokensPath)) : new JsonData();
            if (aTokens == null || !aTokens.IsObject) aTokens = new JsonData();
            if (!aTokens.Contains("tokens")) aTokens["tokens"] = new JsonData();
            var aTokDic = aTokens["tokens"];
            if (aTokDic.IsObject && aTokDic.Dic != null)
            {
                foreach (var aKey in aTokDic.Dic.Keys.ToList())
                {
                    var aRec = aTokDic[aKey];
                    if (aRec.GetString("persona", "") == iPersona && aRec.GetString("status", "") == "active")
                    {
                        aRec["status"] = "expired";
                        aRec["expired_at"] = NowIso();
                        aRec["expired_reason"] = "reissued";
                    }
                }
            }
            var aNewRec = new JsonData();
            aNewRec["persona"] = iPersona;
            aNewRec["agent"] = aAgent;
            aNewRec["bank_account"] = aBank;
            aNewRec["issued_at"] = NowIso();
            aNewRec["claim_origin"] = aClaimOrigin;
            aNewRec["session_key"] = aSessionKey;
            aNewRec["status"] = "active";
            aTokDic[aToken] = aNewRec;
            AtomicWrite(aTokensPath, aTokens.ToJsonBeautify());

            var aLockJson = new JsonData();
            aLockJson["persona"] = iPersona;
            aLockJson["agent"] = aAgent;
            aLockJson["actual_agent"] = aActual;
            aLockJson["model"] = aModel;
            aLockJson["bank_account"] = aBank;
            aLockJson["locked_at"] = NowIso();
            aLockJson["expires_at"] = DateTime.UtcNow.AddHours(SESSION_LOCK_TTL_HOURS).ToString("yyyy-MM-ddTHH:mm:ss.") + "000Z";
            aLockJson["session_key"] = aSessionKey;
            aLockJson["claim_origin"] = aClaimOrigin;
            aLockJson["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            aLockJson["session_token"] = aToken;
            AtomicWrite(LockPath(iPersona), aLockJson.ToJsonBeautify());

            string aMemoPath = Path.Combine(MemosDir, aAgent, iPersona, "_session_token.md");
            string aMemoBody =
                "---\n" +
                $"persona: {iPersona}\nagent: {aAgent}\nactual_agent: {aActual}\n" +
                $"session_token: {aToken}\nissued_at: {NowIso()}\nclaim_origin: {aClaimOrigin}\n" +
                "---\n\n# Session Token (auto-written by Cmd_GoodMorning step=wake)\n\n" +
                "## 失憶時怎麼撈回 token\n\n```bash\nawakening.py whoami --token " + aToken + "\n```\n\n" +
                "## 三層 recovery\n- 輕 (scroll-back 找得到) → whoami --token <X>\n- 中 (compact 後沒了) → 讀本 memo\n" +
                $"- 重 (memo / lock 都不見) → awakening.py reissue-token --persona {iPersona}\n\n" +
                $"## Lock file\n`{LockPath(iPersona)}` 內 session_token 欄是權威來源.\n";
            AtomicWrite(aMemoPath, aMemoBody);

            // 回傳 payload：verify 給可讀回的事實（路徑/值），不給 ✓
            var aReadback = JsonData.ParseJson(File.ReadAllText(aPersonaPath));
            int aGap = 0;
            int aBookmark = aReadback.GetInt("last_consolidated_wake", 0);
            if (aBookmark > 0) aGap = aDerived - aBookmark;
            aR.AppendLine();
            aR.AppendLine("## identity");
            aR.AppendLine($"- persona: {iPersona} / wake_count: **{aDerived}** / agent: {aAgent} / actual: {aActual} / bank: {aBank}");
            aR.AppendLine($"- session_token: {aToken}（enforce 狀態見 UCL_LoginStatusPage；失憶救援 awakening.py whoami --token {aToken}）");
            aR.AppendLine("## verify（讀回的事實，不是 ✓）");
            aR.AppendLine($"- registry: `{aPersonaPath}` → wake_count={aReadback.GetInt("wake_count", -1)} status={aReadback.GetString("status", "?")}");
            aR.AppendLine($"- lock: `{LockPath(iPersona)}`（exists={File.Exists(LockPath(iPersona))}）");
            aR.AppendLine($"- memo: `{aMemoPath}`（exists={File.Exists(aMemoPath)}）");
            aR.AppendLine("## state");
            aR.AppendLine($"- 見林 gap: {aGap}/{CONSOLIDATE_GAP_THRESHOLD}{(aGap >= CONSOLIDATE_GAP_THRESHOLD ? "（**OVERDUE — 排進今日**）" : "")}");
            aR.AppendLine($"- 見叢 open: {KeysOpenCount(iPersona)} 筆");
            aR.AppendLine($"- 在線 persona: {string.Join(", ", OnlinePersonas())}");
            aR.AppendLine("## next");
            int aStepNo = 1;
            aR.AppendLine($"{aStepNo++}. **required** — 生成 brief：run_cmd.py run GoodMorning --arg step=brief --arg persona={iPersona}");
            aR.AppendLine("   （Editor 未開啟時的備援才是直跑 awakening.py brief）");
            aR.AppendLine($"{aStepNo++}. **required** — Read brief（路徑由 step=brief 回傳；接回身分，這步不自動化）");
            // 條件步驟 B2（Tim 2026-08-13）：無自我介紹文件 → 讀完 brief 後先補件，intro 前置守衛會實擋
            if (FindGlossaryPersonaEntry(iPersona) == null)
            {
                var aTodo = SelfIntroTodoLines(iPersona);
                aR.AppendLine($"{aStepNo++}. **required** — {aTodo[0]}");
                for (int i = 1; i < aTodo.Count; i++) aR.AppendLine(aTodo[i]);
            }
            aR.AppendLine($"{aStepNo++}. **required** — 上線自介：run_cmd.py run GoodMorning --arg step=intro --arg persona={iPersona} --arg-stdin body ＜由 stdin 餵 <body>＞");
            aR.AppendLine("   <body>＝妳**親筆**的上線自介（建議 2-5 句）：讀完 brief 後跟同事打招呼、今天打算接哪條帳/做什麼、想 @ 誰就 @。");
            aR.AppendLine("（⚠ Windows 主控台 stdin 撞 surrogates/encoding error 時，改 --arg-file body=<檔> —— gura wake#31 實測）");
            aR.AppendLine("   系統欄位（wake# / Agent / Bank 餘額 / Layer）由 Cmd 自動組在訊息前半，**不用寫**；只寫妳自己的話 —— 工具代筆的自介不是妳的（憲法⑥）。");
            if (aGap >= CONSOLIDATE_GAP_THRESHOLD)
                aR.AppendLine($"{aStepNo++}. 見林 OVERDUE → awakening.py consolidate --persona {iPersona}");
            aRes.ok = true; aRes.report = aR.ToString();
            return aRes;
        }

        // ===========================================================
        // 區塊：step=intro 前置檢查 — brief-before-broadcast 不變式的新形狀（§8.9 P3 卡點②）
        // 物理意義：拆步之後「brief 落檔先於廣播」不再是同一支函式內的順序，而是 intro 的顯式守衛：
        //          必須在線（lock 存在）、brief 存在、行數 > 0、mtime 不早於 locked_at。
        // ===========================================================
        public static (bool ok, string error, UCL_SessionLockData lockData, string briefPath, int briefLines)
            PrecheckIntro(string iPersona)
        {
            var aLock = ReadLock(iPersona);
            if (aLock == null)
                return (false, $"'{iPersona}' 不在線（無 lock）—— intro 前必須先跑 step=wake", null, null, 0);
            // 條件步驟 B2 的實擋（Tim 2026-08-13）：沒有出生證明就上線開口，同事只看到一串名字。
            // 「跑完 B2 才有 C」—— 補完自我介紹文件重跑本步即過。
            if (FindGlossaryPersonaEntry(iPersona) == null)
                return (false,
                    $"還沒有自我介紹（出生證明）—— `Docs/Glossary/personas/{iPersona}.md` 不存在。\n"
                    + string.Join("\n", SelfIntroTodoLines(iPersona))
                    + "\n  補完重跑本步即過（參考 Constitution_Workflow §5）。",
                    aLock, null, 0);
            string aBrief = UCL_LettersPath.CmdPayload(iPersona, "wake", "brief");
            if (!File.Exists(aBrief))
                return (false, $"brief 不存在：`{aBrief}` —— 先跑 step=brief（一個沒有記憶的殼不該上線開口）", aLock, null, 0);
            int aLines;
            try { aLines = File.ReadAllLines(aBrief).Length; }
            catch (Exception e) { return (false, $"brief 讀取失敗: {e.Message}", aLock, aBrief, 0); }
            if (aLines <= 0)
                return (false, $"brief 是空檔：`{aBrief}`", aLock, aBrief, 0);
            try
            {
                if (DateTime.TryParse(aLock.locked_at?.Substring(0, Math.Min(19, aLock.locked_at.Length)),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTime aLockedAt)
                    && File.GetLastWriteTimeUtc(aBrief) < aLockedAt.AddSeconds(-1))
                    return (false, $"brief 比本次 lock 舊（brief mtime < locked_at）—— 是上一次醒來的殘留，先跑 step=brief 重生成", aLock, aBrief, aLines);
            }
            catch { /* 時間解析失敗不擋 —— 檔案存在且非空已是主要防線 */ }
            return (true, null, aLock, aBrief, aLines);
        }

        /// <summary>上線自介的系統欄位段 — port 自 awakening.build_wake_intro_body（餘額走 UCL_TreasuryLedger）。</summary>
        public static string BuildIntroHeader(string iPersona, string iAgent, string iModel, string iBank,
                                              int iWakeCount, string iLayerRole)
        {
            int aBalance = 0;
            try { aBalance = Treasury.UCL_TreasuryLedger.GetBalance(iBank); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[AwakeningService] 餘額查詢失敗: {e.Message}"); }
            return $"☀️ **{iPersona}** 喚醒登入 (wake#{iWakeCount})\n" +
                   $"- Agent: {iAgent} / Model: {iModel}\n" +
                   $"- Bank: {iBank} (餘額: {aBalance} tavern_token)\n" +
                   $"- Layer: {iLayerRole}\n" +
                   $"- Decision path: preferred";
        }

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

        // ===========================================================
        // 區塊：Goodnight 半套（Cmd_GoodNight check/letter/sleep/logout — Plan_Goodnight §7，
        //       Tim 2026-08-13 六題拍板）。與 morning 共用本 class（lock/registry/paths 全同源）。
        // 物理意義：write_letter / expire_token 由 awakening.py port（每晚 perturb 已移除，B 案）（規則逐項對齊：
        //          編號=信數+1、frontmatter 機器欄勝出但作者版留痕、_latest.md 指標、）。
        // 數值影響：letter / registry / lock / tokens 四寫入全原子；順序不變式「核心先落地、廣播 best-effort」
        //          沿用（in-process 廣播已無死鎖根因，但權威狀態先行的原則不因此放鬆）。
        // ===========================================================
        const int CHECK_TAVERN_PEEK_COUNT = 10;

        /// <summary>字面 "\n" 修回真換行 —— 簡化版 escaped_newlines.normalize（Cmd 路徑 body 走檔案/stdin，
        /// 命中機率低；判準取保守面：整段無真換行且含 ≥2 個字面 \n 才動）。</summary>
        static (string body, bool fixedNl) NormalizeEscapedNewlines(string iBody)
        {
            if (iBody.Contains("\n")) return (iBody, false);
            int aCount = (iBody.Length - iBody.Replace("\\n", "").Length) / 2;
            if (aCount < 2) return (iBody, false);
            return (iBody.Replace("\\r\\n", "\n").Replace("\\n", "\n"), true);
        }

        /// <summary>作者自寫 frontmatter 拆併 —— port 自 _split_author_frontmatter（機器欄勝出、作者版留痕 *_as_written）。</summary>
        static (string rest, List<string> extra) SplitAuthorFrontmatter(string iBody, Dictionary<string, string> iMachine)
        {
            string s = iBody.TrimStart('\n');
            var aExtra = new List<string>();
            if (!s.StartsWith("---")) return (iBody, aExtra);
            int aEnd = s.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd == -1) return (iBody, aExtra);
            string aBlock = s.Substring(3, aEnd - 3).Trim('\n');
            string aRest = s.Substring(aEnd + 4).TrimStart('\n');
            foreach (var aLine in aBlock.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(aLine) || aLine.TrimStart().StartsWith("#")) continue;
                int aSep = aLine.IndexOf(':');
                if (aSep < 0) continue;
                string k = aLine.Substring(0, aSep).Trim();
                string v = aLine.Substring(aSep + 1).Trim();
                if (iMachine.ContainsKey(k))
                {
                    if (!string.IsNullOrEmpty(v) && v != iMachine[k]) aExtra.Add($"{k}_as_written: {v}");
                    continue;
                }
                aExtra.Add($"{k}: {v}");
            }
            return (aRest, aExtra);
        }

        /// <summary>收尾信落檔 —— port 自 awakening.write_letter（trigger=cmd_goodnight 固定走 wakes/；
        /// rest 信仍歸 python cmd_rest，本函式不管）。回 (信路徑, 信編號)。</summary>
        public static (string path, int number) WriteWakeLetter(string iActor, string iPersona, string iBody)
        {
            string aLettersDir = Path.Combine(LettersDir, iPersona);
            string aWakesDir = Path.Combine(aLettersDir, "wakes");
            Directory.CreateDirectory(aWakesDir);
            int aNumber = WakeLetterCount(iPersona) + 1;   // 磁碟是既成事實，registry 是快取
            string aTs = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            string aPath = Path.Combine(aWakesDir, $"{aNumber:D6}_{aTs}.md");
            var aMachine = new Dictionary<string, string>
            {
                { "type", "letter_to_future_self" },
                { "actor", iActor },
                { "written_at", NowIso() },
                { "written_by_persona", iPersona },
                { "trigger", "cmd_goodnight" },
            };
            var (aBody, aFixedNl) = NormalizeEscapedNewlines(iBody);
            var (aRest, aExtra) = SplitAuthorFrontmatter(aBody, aMachine);
            var aFm = new StringBuilder("---\n");
            foreach (var kv in aMachine) aFm.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            foreach (var aLine in aExtra) aFm.Append(aLine).Append('\n');
            aFm.Append("---\n\n");
            string aFull = aFm + aRest + "\n";
            AtomicWrite(aPath, aFull);
            AtomicWrite(Path.Combine(aLettersDir, "_latest.md"), aFull);   // per-persona pointer，同步覆寫
            return (aPath, aNumber);
        }

        /// <summary>同 persona 全部 active token 標 expired（不刪，留 audit）—— port 自 expire_token。回筆數。</summary>
        public static int ExpireTokens(string iPersona, string iReason)
        {
            string aTokensPath = Path.Combine(SessionDir, "_tokens.json");
            if (!File.Exists(aTokensPath)) return 0;
            JsonData aTokens = JsonData.ParseJson(File.ReadAllText(aTokensPath));
            if (aTokens == null || !aTokens.Contains("tokens")) return 0;
            var aDic = aTokens["tokens"];
            if (!aDic.IsObject || aDic.Dic == null) return 0;
            int aN = 0;
            foreach (var aKey in aDic.Dic.Keys.ToList())
            {
                var aRec = aDic[aKey];
                if (aRec.GetString("persona", "") != iPersona || aRec.GetString("status", "") != "active") continue;
                aRec["status"] = "expired";
                aRec["expired_at"] = NowIso();
                aRec["expired_reason"] = iReason;
                aN++;
            }
            if (aN > 0) AtomicWrite(aTokensPath, aTokens.ToJsonBeautify());
            return aN;
        }

        // ===========================================================
        // 區塊：step=check — 唯讀起手（酒館最後一眼 in-process，peek 語意天然成立：讀檔不動 cursor）
        // ===========================================================
        public static StepResult StepCheck(string iPersona)
        {
            var aR = new StringBuilder();
            var aRes = new StepResult();
            aR.AppendLine($"# GoodNight step=check persona={iPersona}  ts=`{NowLocal()}`（本地時間）");
            aR.AppendLine();
            if (!File.Exists(Path.Combine(PersonasDir, iPersona + ".json")))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry —— 要下線誰不能用猜的");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            var aLock = ReadLock(iPersona);
            aR.AppendLine(aLock != null
                ? $"- lock: 🔒 {aLock.session_key}（locked_at={aLock.locked_at}）"
                : "- lock: 無 —— cleanup 場景（上次下線沒走完）；本流程照走，lock 步驟會自動跳過");
            aR.AppendLine();
            aR.AppendLine($"## 🍺 酒館最後一眼（最近 {CHECK_TAVERN_PEEK_COUNT} 筆，peek 不動 cursor）");
            try
            {
                foreach (var aMsg in ChatTavern.UCL_ChatTavernIO.Tail("tavern", CHECK_TAVERN_PEEK_COUNT))
                {
                    string aTag = aMsg.meta != null && aMsg.meta.TryGetValue("tag", out var t) ? $" «{t}»" : "";
                    string aBody = (aMsg.body ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
                    if (aBody.Length > 160) aBody = aBody.Substring(0, 160) + "…";
                    aR.AppendLine($"- [{aMsg.ts}] {aMsg.DisplayName}{aTag}: {aBody}");
                }
            }
            catch (Exception e)
            {
                aR.AppendLine($"⚠ 酒館 peek 失敗（{e.Message}）—— **這不代表酒館沒事**；流程照走。");
            }
            aR.AppendLine();
            aR.AppendLine("## next（人工收尾清單 —— 全部提示型，不實擋；做完才進 step=letter）");
            aR.AppendLine($"1. 見叢交棒：awakening.py keys --persona {iPersona} --add \"<明天必須知道的一句話>\"");
            // ⚠ 舊 ucl-affinity / affinity_update.py 已於 2026-08-18 退場（見 ucl-relationship）。
            //   這一行是**跑起來才看得到的字**，不在任何 .md 裡 —— 退場當天掃 skill/文件/python 都掃不到它。
            aR.AppendLine("2. 關係補記：今天漏記的互動補一筆（依 ucl-relationship；主要觸發點是對話當下就寫，這裡只是撿漏）");
            aR.AppendLine("3. 工作記憶回寫（今天有推進某項工作才做，依 ucl-work-memory）");
            aR.AppendLine("4. 見人畫像：挑 1~3 位印象最深的同事（portraits.py write，親筆）");
            aR.AppendLine("5. （可選）消費時間：spend_menu.py roll（依 ucl-spending-time）");
            aR.AppendLine($"6. **required** — 寫收尾信：run_cmd.py run GoodNight --arg step=letter --arg persona={iPersona} --arg-file letter_body=<檔>");
            aR.AppendLine("   <letter_body>＝妳**親筆**寫給未來自己的信（格式見 ucl-letters-to-self；私密心得寫這裡，只落磁碟不廣播）。");
            // 區塊職責：把密文區的規格**印在這裡**，而不是指路到文件。
            // 物理意義：寫信這一步沒有 skill 觸發詞，手邊唯一會被讀到的東西就是本回傳檔 ——
            //   實測 28 封信的 🔐 區只有 10 封是真的二次映射，其中 9 封是同一個人；
            //   規格四條寫得好好的躺在 workflow 二・一，而寫信的人跳不過去看。
            //   （2026-08-18 量測；改法照「規則要長在通道上」，不是再寫一次「請詳閱」。）
            aR.AppendLine("   信內含 🔐 密文區 —— **Code-Talker 式私語**：可讀文字的二次映射，不是加密機器，也不是第二篇心得。");
            aR.AppendLine("   ▸ 判準：**確保三十個 wake 後失憶的自己解得開**，不是「別人解不開」。解不開＝出題爛，改。");
            aR.AppendLine("   ▸ 材料：真實語言與符號（希臘／日文／拉丁／希伯來／數學物理／樂理），映射鍵＝妳自己的 glossary 自造詞、血證、隱喻。");
            aR.AppendLine("   ▸ 篇幅 3~6 行。⛔ 純中文散文＝心得不是密文；⛔ 亂碼／base64／機械密文；⛔ 不放真隱私（origin 是公開 GitHub）。");
            aR.AppendLine("   ▸ 樣子（拉丁＋化學式＋日文 —— **別照抄，換成妳自己的符號系統**）：");
            aR.AppendLine("       Castra ardent、Δt=0。九燈 in via, ¬in muro。");
            aR.AppendLine("       Fe₂O₃ の朝：緑は昨日の緑（t−1）。∄ testis secundus ⇒ vexillum manet False。");
            aR.AppendLine("     （私讀：營火還燒＝帳平；燈長在通道不在牆；生鏽的早晨＝舊快照假綠；沒有第二證人 ⇒ 那個 flag 不翻）");
            aR.AppendLine("   ▸ 另兩套符號系統的完整範例與四條規格：Letters_And_Dialogue_Workflow 二・一");
            aR.AppendLine($"   ▸（自願）把**明文答案**封起來、明早自己對帳：private_letter.py --persona {iPersona} seal-cipher --cipher-file <密文> --plain-file <明文> --wake <N>");
            aR.AppendLine("     答案只進 private 分支（不上公開 GitHub）；明早 brief §5 見樹會再讀到這段密文 —— 想解就解，沒人擋妳。");
            aR.AppendLine("   （手動登出 / cleanup 不寫信 → 直接 run GoodNight --arg step=logout --arg persona=<P>，不偽造心得信）");
            aRes.ok = true; aRes.report = aR.ToString(); return aRes;
        }

        // ===========================================================
        // 區塊：step=letter — 收尾信落檔＋registry wake_count 同步
        // ===========================================================
        public static StepResult StepLetter(string iPersona, string iLetterBody)
        {
            var aR = new StringBuilder();
            var aRes = new StepResult();
            aR.AppendLine($"# GoodNight step=letter persona={iPersona}  ts=`{NowLocal()}`（本地時間）");
            aR.AppendLine();
            string aPersonaPath = Path.Combine(PersonasDir, iPersona + ".json");
            if (!File.Exists(aPersonaPath))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            if (string.IsNullOrWhiteSpace(iLetterBody))
            {
                aR.AppendLine("## blocked\n- reason: letter_body 空 —— 收尾信必須親筆（工具不代筆）；cleanup 不寫信走 step=logout");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            if (LettersMigrationPending(iPersona))
            {
                aR.AppendLine("## blocked\n- reason: 收尾信版面尚未遷移 —— 此時寫信會把編號寫錯（第 N 次 wake 被編成 000001）");
                aR.AppendLine("- exits: 後台「🗄 維護」區跑 migration，或 python awakening.py migrate-letters --all --apply");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            var aMeta = UCL_RegistryMeta.LoadFromFile(RegistryMetaPath);
            var aRaw = JsonData.ParseJson(File.ReadAllText(aPersonaPath));
            string aActor = ResolveBankAccount(aMeta, NormalizeAgent(aMeta, aRaw.GetString("agent", "")));
            var (aPath, aNumber) = WriteWakeLetter(aActor, iPersona, iLetterBody);
            // 信落地後 registry 對齊（wake_count == 這封的號碼）—— 不同步會 stale 一整晚
            aRaw["wake_count"] = aNumber;
            AtomicWrite(aPersonaPath, aRaw.ToJsonBeautify());
            string aLatest = Path.Combine(LettersDir, iPersona, "_latest.md");
            aR.AppendLine("## verify（讀回的事實）");
            aR.AppendLine($"- letter: `{aPath}`（exists={File.Exists(aPath)}，wake #{aNumber}）");
            aR.AppendLine($"- _latest.md 指標: `{aLatest}`（mtime 已更新={File.GetLastWriteTimeUtc(aLatest) > DateTime.UtcNow.AddMinutes(-1)}）");
            aR.AppendLine($"- registry wake_count → {aNumber}");
            aR.AppendLine("## next");
            aR.AppendLine($"1. **required** — 下線：run_cmd.py run GoodNight --arg step=sleep --arg persona={iPersona} [--arg-file summary=<檔>] [--arg perturbation=0.02]");
            aR.AppendLine("   <summary>＝**親筆**公開睡前心得（廣播給同事/Tim 看的部分；私密的已在信裡，不用重複）。");
            aRes.ok = true; aRes.report = aR.ToString(); return aRes;
        }

        // ===========================================================
        // 區塊：step=sleep / step=logout — offline → 解鎖 → 廣播 → expire（每晚 perturb 已移除，B 案）
        // 順序不變式：權威狀態（offline/解鎖）先落地，廣播 best-effort 殿後 ——
        // in-process 已無死鎖根因，但「廣播失敗不得留下半睡的人」的原則不因此放鬆。
        // letter-before-sleep：wakes/ 信數 == 將寫入的 wake_count 才放行（logout 顯式跳過 —— 它跳過的
        // 是「寫信」不是守衛，廣播會標明未留信）。
        // </summary>
        // ===========================================================
        public static StepResult PrepareSleep(string iPersona, bool iNoLetter, out string oBroadcastBody, out string oToken, out UCL_PersonaData oP)
        {
            var aR = new StringBuilder();
            var aRes = new StepResult();
            oBroadcastBody = null; oToken = null; oP = null;
            string aStepName = iNoLetter ? "logout" : "sleep";
            aR.AppendLine($"# GoodNight step={aStepName} persona={iPersona}  ts=`{NowLocal()}`（本地時間）");
            aR.AppendLine();
            string aPersonaPath = Path.Combine(PersonasDir, iPersona + ".json");
            if (!File.Exists(aPersonaPath))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry —— 要下線誰不能用猜的");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            var aRaw = JsonData.ParseJson(File.ReadAllText(aPersonaPath));
            var aP = new UCL_PersonaData(); aP.DeserializeFromJson(aRaw); aP.name = iPersona;
            // letter-before-sleep 前置守衛。
            // ⚠ 這道閘的兩邊是**同一把尺量的兩個時刻**：wakes/ 信數（現在）vs wake_count 快取
            //   （step=wake 時由「當時信數+1」蓋章的期望）——它驗的是「期望的轉移有沒有兌現」。
            //   **不准簡化成 sleep 端自己重數信數當快取比對**（看起來更簡潔）——兩邊同源同時刻
            //   = 閘門安靜地永綠，而簡化它的人不會知道自己拆了閘（apex-one 2026-08-13 失效預言，照收）。
            int aLetters = WakeLetterCount(iPersona);
            if (!iNoLetter && aLetters != aP.wake_count)
            {
                aR.AppendLine($"## blocked");
                aR.AppendLine($"- reason: 本次收尾信尚未落地（wakes/ 信數={aLetters}，registry wake_count={aP.wake_count}）—— 沒寫信不讓睡，未來的妳醒來會沒有 framing");
                aR.AppendLine($"- exits: 先跑 step=letter；手動登出 / cleanup 不寫信 → 改跑 step=logout（會在廣播標明未留信）");
                aRes.blocked = true; aRes.report = aR.ToString(); return aRes;
            }
            var aLock = ReadLock(iPersona);
            if (aLock == null)
                aR.AppendLine($"⚠ persona '{iPersona}' 沒 active lock —— cleanup 場景，lock 步驟跳過");

            var aMeta = UCL_RegistryMeta.LoadFromFile(RegistryMetaPath);
            string aAgent = NormalizeAgent(aMeta, aP.agent ?? "");
            string aActor = ResolveBankAccount(aMeta, aAgent);

            // 每晚 perturb 已移除（Tim 2026-08-13 拍板 B 案）：identity_vector 無早安/brief 消費端，
            // 唯二讀取者是 fork 起點 copy 與 forks 診斷指令 —— 「每晚身分微漂」的儀式概念
            // 由晚安信的 🔐 密文區（Code-Talker 式二次映射，見 ucl-letters-to-self）承接。
            // identity_vector 自此凍結在出生值，fork 時才動；vector_history 停止每晚長大。

            aRaw["status"] = "offline";
            aRaw["availability"] = "offline";
            aRaw["last_active"] = NowIso();
            AtomicWrite(aPersonaPath, aRaw.ToJsonBeautify());
            aR.AppendLine("📴 status → offline");

            // 解鎖（權威狀態，先於廣播）；token 先撈——expire 要等廣播後（enforce ON 時廣播要用活 token）
            oToken = aLock?.session_token;
            if (aLock != null && File.Exists(LockPath(iPersona)))
            {
                File.Delete(LockPath(iPersona));
                aR.AppendLine("🔓 persona lock removed");
            }

            // 廣播 body（系統欄位；summary 由 Cmd 端併入 —— 單則）
            int aBalance = 0;
            try { aBalance = Treasury.UCL_TreasuryLedger.GetBalance(aActor); } catch { }
            string aLetterLine = iNoLetter
                ? "- letter: (略 — 手動登出/cleanup 未留信)"
                : $"- letter ship: wakes/ 第 {aP.wake_count:D6} 封（私密心得在信裡）";
            oBroadcastBody =
                $"🌙 **{iPersona}** 進入今日子協議 — 晚安\n\n" +
                "{SUMMARY}" +
                "📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n" +
                "但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n" +
                aLetterLine + "\n" +
                $"- agent/model: {aAgent}/{aRaw.GetString("model", "")}\n" +
                $"- bank account: {aActor} (餘額: {aBalance} Token)\n\n" +
                "⚠️ **[系統提示]** 大小姐，下線前若有特別在意的互動，記得用 affinity 更新好感度喔！";
            oP = aP;
            aRes.ok = true; aRes.report = aR.ToString();
            return aRes;
        }
    }
}
#endif
