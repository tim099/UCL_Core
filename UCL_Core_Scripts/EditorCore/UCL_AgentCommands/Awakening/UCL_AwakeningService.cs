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
        public static string SessionDir => ResolveOverridablePath("session_dir", "_session");
        public static string LettersDir => ResolveOverridablePath("letters_dir", Path.Combine("ChatTavern", "baton", "letters"));

        static string ResolveOverridablePath(string iConfigKey, string iDefaultSub)
        {
            try
            {
                string aCfgPath = Path.Combine(DataRoot, "_config", "tavern_paths.json");
                if (File.Exists(aCfgPath))
                {
                    var aCfg = JsonData.ParseJson(File.ReadAllText(aCfgPath));
                    string aOverride = aCfg?.GetString(iConfigKey, "")?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(aOverride))
                    {
                        // 對齊 Python：~ / $VAR 展開、相對路徑以 RepoRoot 為基準
                        aOverride = Environment.ExpandEnvironmentVariables(aOverride);
                        if (aOverride.StartsWith("~"))
                            aOverride = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                        + aOverride.Substring(1);
                        if (!Path.IsPathRooted(aOverride))
                            aOverride = Path.Combine(UCL_RepoPath.RepoRoot, aOverride);
                        return Path.GetFullPath(aOverride);
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[AwakeningService] tavern_paths.json 讀取失敗（fallback 預設）: {e.Message}");
            }
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
            aSb.AppendLine($"# 🧪 Awakening 對帳（C# 唯讀掃描） ts=`{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}`");
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

        public static (bool ok, string report, string briefPath, int briefLines) RunBrief(
            string iPersona, string iCallerName, int iTimeoutMs = 120000)
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel))
                return (false, "✗ 解析不到 UCL_Core 路徑（UCL_EditorPath.CorePath 為空）", null, 0);
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/awakening.py"));
            if (!File.Exists(aScript))
                return (false, $"✗ 找不到 awakening.py：{aScript}", null, 0);

            string aArgs = $"\"{aScript}\" brief --persona \"{iPersona}\"";
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aArgs, UCL_RepoPath.RepoRoot,
                PROC_TAG, iCallerName, iTimeoutMs);

            // stderr 不丟掉 —— awakening.py 把警告印在 stderr，只收 stdout 會讓報告假乾淨。
            var aSb = new StringBuilder();
            aSb.AppendLine($"$ python awakening.py brief --persona {iPersona}   (exit={aExit})");
            aSb.AppendLine(aSo ?? "");
            if (!string.IsNullOrEmpty(aSe)) aSb.AppendLine("── stderr ──").AppendLine(aSe);

            // 驗收看落地檔不看 stdout：brief 檔存在且行數 > 0 才算生成成功。
            string aBriefPath = Path.Combine(LettersDir, iPersona, "_wake_brief.md");
            int aLines = 0;
            bool aExists = File.Exists(aBriefPath);
            if (aExists)
            {
                try { aLines = File.ReadAllLines(aBriefPath).Length; }
                catch (Exception e) { aSb.AppendLine($"⚠ brief 行數讀取失敗: {e.Message}"); }
            }
            aSb.AppendLine(aExists
                ? $"📄 brief: `{aBriefPath}`（{aLines} 行）"
                : $"✗ brief 檔不存在：`{aBriefPath}`");
            bool aOk = aExit == 0 && aExists && aLines > 0;
            return (aOk, aSb.ToString(), aExists ? aBriefPath : null, aLines);
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
    }
}
#endif
