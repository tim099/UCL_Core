// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 07/29 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：把 Registry 內所有 handler 的機器可讀參數規格反射匯出成 commands_schema.json，
//          供 Python client 端預檢使用 —— 取代 Python 手抄的 TAVERN_OP_SCHEMA。
// 物理意義：本檔是「同步」這個動作的**唯一實作**。三個入口（CMD 管理面板按鈕 /
//          Cmd_ExportCmdSchema / 日後任何自動觸發）全部呼叫本類別的同一個 static 方法 ——
//          各寫一份就是本設計正在治的病的下一個實例。
//          設計依據：Docs~/zh-Hant/Plan/Plan_AgentCmd_Schema_Reflection_Export.md
// 數值影響：只寫一個檔（<RepoRoot>/AgentCommands/commands_schema.json，入 git）。
//          **內容未變則不落筆**（不動 mtime、不製造 git 噪音、不觸發 asset import）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Cmd 參數規格匯出器 —— 面板按鈕與 <c>Cmd_ExportCmdSchema</c> 共用的唯一實作。
    /// </summary>
    public static class UCL_CmdSchemaExporter
    {
        /// <summary>產物檔名（落在 <see cref="UCL_RepoPath.AgentCommandsDir"/> 下）。</summary>
        public const string SchemaFileName = "commands_schema.json";

        /// <summary>
        /// 產物格式版本。**Python loader 會比對這個值**：
        /// 讀到未知（較新）的版本 → 放棄使用本檔並退回無預檢（fail-open），不猜格式。
        /// 破壞性改格式時 +1。
        /// </summary>
        public const int SchemaVersion = 1;

        /// <summary>產物絕對路徑。</summary>
        public static string SchemaPath =>
            Path.Combine(UCL_RepoPath.AgentCommandsDir, SchemaFileName).Replace('\\', '/');

        // ===========================================================
        // 區塊職責：來源檔雜湊 —— 判斷「產物是否對應當前的 Cmd 原始碼」。
        // 物理意義：**刻意不用 mtime**。git 不儲存 mtime（`git ls-tree` 只有 mode/type/blob/name），
        //          clone 或 checkout 後所有檔案的 mtime 都是「當下寫檔時間」，先後只取決於寫檔次序 ——
        //          而「clone 下來直接用」正是本產物入 git 的主要理由，用 mtime 等於在主場景擲骰子
        //          （gura QA 2026-07-29 推翻原案）。內容雜湊與檔案時間、clone 順序、時區全部無關。
        // 數值影響：純讀檔計算，不寫任何東西。
        //
        // ⚠ 跨語言契約 —— Python 端 (tavern_cmd.py) 重算時必須得到相同結果：
        //   ① 檔案集合 = <UnityProjectRoot>/Assets 底下所有檔名符合 `Cmd_*.cs` 者
        //                 ＋ UCL_AgentCommandRegistry.cs（type_aliases 的來源，檔名不符 Cmd_* 故顯式加入）
        //   ② 以「repo 相對路徑、正斜線、序數排序」決定順序
        //   ③ 逐檔餵入：相對路徑的 UTF-8 bytes → 一個 0 byte → 檔案原始 bytes（**不做換行正規化**）
        //   ④ SHA-256，輸出小寫 hex
        //   改動任一條規則 = 破壞契約，必須同步兩端並升 SchemaVersion。
        //
        // 🔑 ①（集合定義）**不再由兩端各自實作** —— 產物內以 `source_files` 明列參與雜湊的相對路徑，
        //   Python 照清單讀檔驗算即可，不自己去猜哪些檔算數。
        //   原本兩端各寫一份 glob 規則（C# 錨 UnityProjectRoot/Assets；Python 用 repo 下所有 Assets），
        //   gura QA 2026-07-29 實測：Python 那份**已經在撈 Library/PackageCache/*/Assets 與 .git/modules/**，
        //   目前對得上只因 Unity 官方 package 剛好沒人用 Cmd_ 開頭命名；且 UCL_Core 是跨專案 submodule，
        //   多 Unity 專案的 repo 一掛上就永久不符 → 預檢永久降級且沉默（同碼失聲）。
        //   把「兩份規則要逐字相同」這個維持不住的契約，換成「**一份規則（本檔）＋一份驗算（Python）**」；
        //   清單本身進 diff，誰多誰少一眼看得到。
        // ===========================================================
        public static string ComputeSourceHash()
        {
            var files = CollectSourceFiles();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = new MemoryStream())
            {
                foreach (var rel in files.Keys)
                {
                    // ③ 相對路徑 + 0 byte 分隔 + 原始檔案 bytes（分隔符防「路徑尾接檔頭」的邊界歧義）
                    byte[] pathBytes = Encoding.UTF8.GetBytes(rel);
                    stream.Write(pathBytes, 0, pathBytes.Length);
                    stream.WriteByte(0);
                    byte[] content = File.ReadAllBytes(files[rel]);
                    stream.Write(content, 0, content.Length);
                }
                stream.Position = 0;
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // 區塊職責：收集參與雜湊的來源檔 —— 回「repo 相對路徑（正斜線）→ 絕對路徑」，已依序數排序。
        // 物理意義：見上方跨語言契約 ①②。用 SortedDictionary + Ordinal 比較，確保與 Python 的
        //          `sorted(list)` 逐字一致（Ordinal = 按碼位比較，跟 Python 預設字串排序同語意）。
        // 數值影響：純檔案系統列舉；找不到 registry 檔不視為錯誤（跨專案結構可能不同）。
        static SortedDictionary<string, string> CollectSourceFiles()
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string repoRoot = UCL_RepoPath.RepoRoot.Replace('\\', '/').TrimEnd('/');
            string assetsDir = Path.Combine(UCL_RepoPath.UnityProjectRoot, "Assets");
            if (!Directory.Exists(assetsDir)) return result;

            // ① 所有 Cmd_*.cs
            foreach (var abs in Directory.GetFiles(assetsDir, "Cmd_*.cs", SearchOption.AllDirectories))
            {
                AddRelative(result, repoRoot, abs);
            }
            // ① 附加 registry（type_aliases 來源；檔名不符 Cmd_* 但改它會改變產物內容）
            foreach (var abs in Directory.GetFiles(assetsDir, "UCL_AgentCommandRegistry.cs", SearchOption.AllDirectories))
            {
                AddRelative(result, repoRoot, abs);
            }
            return result;
        }

        // 區塊職責：把絕對路徑換算成 repo 相對路徑（正斜線）後放進表。
        // 數值影響：不在 repo 底下的檔案（理論上不該發生）直接跳過，不讓它污染雜湊。
        static void AddRelative(SortedDictionary<string, string> dict, string repoRoot, string abs)
        {
            string norm = Path.GetFullPath(abs).Replace('\\', '/');
            if (!norm.StartsWith(repoRoot + "/", StringComparison.OrdinalIgnoreCase)) return;
            string rel = norm.Substring(repoRoot.Length + 1);
            dict[rel] = norm;
        }

        // ===========================================================
        // 區塊職責：組出產物 JSON 字串（不寫檔）。面板要顯示「將要寫入什麼」時也用它。
        // 物理意義：手搓 JSON 而非 JsonUtility —— 後者不支援 Dictionary，且我們需要**穩定序**
        //          （key 一律排序）才能讓「內容沒變 = 零 diff」成立。
        // 數值影響：純字串組合。**不含任何 wall-clock 欄位**（generated_at 之類）——
        //          時間戳會讓每次生成都產生 diff，且跨機器不可複現；要判新舊看 source_hash 就夠。
        // ===========================================================
        public static string BuildSchemaJson()
        {
            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema_version\": ").Append(SchemaVersion).Append(",\n");
            sb.Append("  \"source_hash\": \"").Append(ComputeSourceHash()).Append("\",\n");
            sb.Append("  \"generator\": \"UCL_CmdSchemaExporter\",\n");

            // source_files —— 參與雜湊的檔案清單（repo 相對路徑，已序數排序）。
            // 這是「集合定義的唯一來源」：Python 照這份讀檔驗算，不自己 glob（見 ComputeSourceHash 上方契約說明）。
            // 已排序 ⇒ 穩定序，內容沒變時零 diff；清單本身可被 review，多一個少一個看得出來。
            sb.Append("  \"source_files\": [");
            var srcFiles = CollectSourceFiles().Keys.ToList();
            for (int i = 0; i < srcFiles.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(Quote(srcFiles[i]));
            }
            sb.Append(srcFiles.Count > 0 ? "\n  ],\n" : "],\n");

            // type_aliases —— 消滅第四處鏡像（run_cmd.TYPE_ALIASES 與 Registry.s_TypeAliases 原本各一份）
            sb.Append("  \"type_aliases\": {");
            var aliases = UCL_AgentCommandRegistry.ListTypeAliases();
            var aliasKeys = aliases.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            for (int i = 0; i < aliasKeys.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(Quote(aliasKeys[i])).Append(": ").Append(Quote(aliases[aliasKeys[i]]));
            }
            sb.Append(aliasKeys.Count > 0 ? "\n  },\n" : "},\n");

            // commands
            sb.Append("  \"commands\": {");
            var ordered = handlers.OrderBy(h => h.CommandType, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                AppendCommand(sb, ordered[i]);
            }
            sb.Append(ordered.Count > 0 ? "\n  }\n" : "}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // 區塊職責：單一 handler 的 JSON 片段。
        // 物理意義：沒覆寫 ArgsSpec 的 handler 只出 `{}` —— 代表「有這個 cmd type，但不做參數預檢」。
        //          這是合法狀態不是缺漏（見 UCL_AgentCommandHandlerBase.ArgsSpec 註解）。
        static void AppendCommand(StringBuilder sb, UCL_AgentCommandHandlerBase h)
        {
            sb.Append("    ").Append(Quote(h.CommandType)).Append(": ");
            UCL_CmdArgsSpec spec = null;
            try
            {
                spec = h.ArgsSpec;   // handler 自訂 property 可能拋例外 —— 不讓一顆壞蘋果毀掉整份產物
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CmdSchema] '{h.CommandType}' 的 ArgsSpec 取值失敗，視為未宣告：{e.Message}");
            }
            if (spec == null) { sb.Append("{}"); return; }

            sb.Append("{\n");
            bool wroteAny = false;
            if (spec.Required != null && spec.Required.Length > 0)
            {
                sb.Append("      \"required\": ").Append(JsonArray(spec.Required));
                wroteAny = true;
            }
            if (spec.Aliases != null && spec.Aliases.Count > 0)
            {
                if (wroteAny) sb.Append(",\n");
                sb.Append("      \"aliases\": ").Append(JsonOrderedMap(spec.Aliases));
                wroteAny = true;
            }
            if (spec.Ops != null && spec.Ops.Count > 0)
            {
                if (wroteAny) sb.Append(",\n");
                sb.Append("      \"ops\": {");
                var opKeys = spec.Ops.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                for (int i = 0; i < opKeys.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n");
                    var op = spec.Ops[opKeys[i]];
                    sb.Append("        ").Append(Quote(opKeys[i])).Append(": {");
                    bool opWrote = false;
                    if (op.Required != null && op.Required.Length > 0)
                    {
                        sb.Append("\"required\": ").Append(JsonArray(op.Required));
                        opWrote = true;
                    }
                    if (op.Aliases != null && op.Aliases.Count > 0)
                    {
                        if (opWrote) sb.Append(", ");
                        sb.Append("\"aliases\": ").Append(JsonOrderedMap(op.Aliases));
                    }
                    sb.Append("}");
                }
                sb.Append("\n      }");
            }
            sb.Append("\n    }");
        }

        // 區塊職責：JSON 陣列序列化。**保留宣告順序**（required 順序不影響語意，但穩定輸出才有零 diff）。
        static string JsonArray(IEnumerable<string> items)
        {
            return "[" + string.Join(", ", items.Select(Quote)) + "]";
        }

        // 區塊職責：JSON 物件序列化，**保留 Dictionary 的插入順序**。
        // 物理意義：alias 表的**順序即優先序**（見 UCL_CmdOpSpec.Aliases）——
        //          這裡若照 key 排序會**改變語意**，讓 client 端選到錯的別名值。
        //          所以 alias 是全檔唯一刻意不排序的地方；C# 的 Dictionary 在只增不刪的
        //          初始化式下會維持插入順序，Python 的 dict 亦然，兩端行為一致。
        static string JsonOrderedMap(Dictionary<string, string> map)
        {
            var parts = map.Select(kv => Quote(kv.Key) + ": " + Quote(kv.Value));
            return "{" + string.Join(", ", parts) + "}";
        }

        // 區塊職責：JSON 字串跳脫。手搓 JSON 唯一容易出錯的地方，集中一處。
        static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>匯出結果 —— 給面板顯示與 Cmd 回報用。</summary>
        public struct ExportResult
        {
            /// <summary>是否真的寫了檔（內容未變 → false）。</summary>
            public bool Written;
            /// <summary>產物絕對路徑。</summary>
            public string Path;
            /// <summary>本次計算出的來源雜湊。</summary>
            public string SourceHash;
            /// <summary>納入產物的 cmd 數。</summary>
            public int CommandCount;
            /// <summary>其中有宣告 ArgsSpec 的 cmd 數。</summary>
            public int SpecCount;
        }

        // ===========================================================
        // 區塊職責：生成並落檔 —— **三個入口共用的唯一實作**。
        // 物理意義：面板按鈕、Cmd_ExportCmdSchema 都呼叫這一個方法。
        // 數值影響：**內容與現有檔逐字相同 → 不寫檔**（Written=false）。
        //          產物入 git，若每次都落筆會讓 git status 天天髒、並在別人 build 時偷改共用檔。
        // ===========================================================
        public static ExportResult Export()
        {
            string json = BuildSchemaJson();
            string path = SchemaPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            bool needWrite = true;
            if (File.Exists(path))
            {
                try
                {
                    // 逐字比對；相同就不動檔案（連 mtime 都不動）
                    needWrite = File.ReadAllText(path, Encoding.UTF8) != json;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CmdSchema] 讀取現有產物失敗，改為直接覆寫：{e.Message}");
                }
            }
            if (needWrite) File.WriteAllText(path, json, new UTF8Encoding(false));

            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            int specCount = 0;
            foreach (var h in handlers)
            {
                try { if (h.ArgsSpec != null) specCount++; } catch { /* 取值失敗視為未宣告，上面已警告 */ }
            }
            return new ExportResult
            {
                Written = needWrite,
                Path = path,
                SourceHash = ComputeSourceHash(),
                CommandCount = handlers.Count,
                SpecCount = specCount,
            };
        }

        // ===========================================================
        // 區塊職責：同步狀態查詢（面板用）—— 產物內的 source_hash 是否等於當前來源雜湊。
        // 物理意義：面板要能在**不寫檔**的前提下回答「現在同步了嗎」。
        // 數值影響：純讀取。產物不存在 / 解析不出 hash → 回 false（視為未同步）。
        // ===========================================================
        /// <summary>每日自動同步的「上次檢查時間」EditorPrefs key（per-machine，見 <see cref="UCL_CmdSchemaAutoSync"/>）。</summary>
        public const string AutoSyncPrefKey = "UCL_CmdSchema_LastAutoSyncTicks";

        /// <summary>上次自動檢查時間（本機）；從未檢查過回 <see cref="DateTime.MinValue"/>。</summary>
        public static DateTime LastAutoSyncUtc
        {
            get
            {
                string raw = UnityEditor.EditorPrefs.GetString(AutoSyncPrefKey, "");
                return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
            set => UnityEditor.EditorPrefs.SetString(AutoSyncPrefKey, value.Ticks.ToString());
        }

        public static bool IsInSync(out string artifactHash, out string currentHash)
        {
            currentHash = ComputeSourceHash();
            artifactHash = null;
            try
            {
                if (!File.Exists(SchemaPath)) return false;
                string text = File.ReadAllText(SchemaPath, Encoding.UTF8);
                var m = System.Text.RegularExpressions.Regex.Match(text, "\"source_hash\"\\s*:\\s*\"([0-9a-f]+)\"");
                if (!m.Success) return false;
                artifactHash = m.Groups[1].Value;
                return artifactHash == currentHash;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CmdSchema] 讀取產物 hash 失敗：{e.Message}");
                return false;
            }
        }
    }

    // ===========================================================
    // 區塊職責：每日一次的自動同步 — 編譯完成時檢查，距上次檢查超過一天才真的動手（Tim 2026-07-29 拍板）。
    // 物理意義：手動入口（面板按鈕 / Cmd_ExportCmdSchema）仍是主要管道，本類別只是**兜底**：
    //          人忘了按時，最多一天內會自己補上。設計上刻意不是「每次編譯都生成」——
    //          原始顧慮是效能（每次編譯都要讀完所有 Cmd_*.cs 算雜湊），一天一次讓成本可忽略。
    // 數值影響：
    //   - 節流未到期 → **完全不做事**（連雜湊都不算），編譯零額外成本。
    //   - 到期且雜湊相符 → 只更新節流時間戳，不寫產物。
    //   - 到期且雜湊不符 → 呼叫 Export()（內容未變仍不落筆），並印一行說明。
    //
    // ⚠ 節流時間戳存 **EditorPrefs（per-machine）**，刻意不寫進產物：
    //   產物入 git 且我們特意移除了所有 wall-clock 欄位以達成「內容沒變 = 零 diff」；
    //   把「上次檢查時間」寫回產物等於親手把剛消滅的 diff 噪音請回來，而且會讓每台機器
    //   互相覆寫對方的時間戳 —— 那是 per-machine 狀態，本來就不該進版控。
    // ===========================================================
    [UnityEditor.InitializeOnLoad]
    public static class UCL_CmdSchemaAutoSync
    {
        /// <summary>節流間隔 — 每台機器每天最多自動觸發一次。</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

        static UCL_CmdSchemaAutoSync()
        {
            // InitializeOnLoad 確保 Editor 啟動 / domain reload 時都會掛上（與 UCL_CompileErrorTracker 同慣例）
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        static void OnCompilationFinished(object _)
        {
            try
            {
                DateTime last = UCL_CmdSchemaExporter.LastAutoSyncUtc;
                DateTime now = DateTime.UtcNow;

                // 區塊職責：產物不存在 → **無視每日節流，立刻生成**（Tim 2026-07-30 拍板）
                // 物理意義：產物是 per-machine 衍生物、不入 git（跨機器 source_hash 必然不同，
                //          入 git 只會製造永久 diff 與假過期）。於是新 clone／新機器上它一定缺席，
                //          而缺席時 Python 端會**整個跳過參數預檢**（fail-open）——
                //          若還要再等最多 24 小時的節流才生成，等於白白讓預檢空窗一天。
                //          「缺檔」與「檔舊了」是兩種不同狀況：後者可以慢慢來，前者要立刻補。
                // 數值影響：只在檔案不存在時繞過節流；生成後仍會記錄時間戳，之後回到每日節奏。
                bool missing = !File.Exists(UCL_CmdSchemaExporter.SchemaPath);
                // 節流：未到期且產物已存在 → 直接返回（「不做事」的分支，不算雜湊、不碰檔案）
                if (!missing && last != DateTime.MinValue && now - last < Interval) return;

                UCL_CmdSchemaExporter.LastAutoSyncUtc = now;   // 先記時間，避免失敗時每次編譯都重試
                if (missing)
                {
                    var rm = UCL_CmdSchemaExporter.Export();
                    Debug.Log($"[CmdSchema] 產物不存在 → 已自動生成（不受每日節流限制）"
                            + $"— {rm.CommandCount} 個 cmd（{rm.SpecCount} 個有 ArgsSpec）→ {rm.Path}");
                    return;
                }
                // 已同步 → 什麼都不必做（out 需具名：本專案 C# 版本不接受無型別的 `out _`）
                string artifactHash, currentHash;
                if (UCL_CmdSchemaExporter.IsInSync(out artifactHash, out currentHash)) return;

                var r = UCL_CmdSchemaExporter.Export();
                Debug.Log($"[CmdSchema] 每日自動同步：{(r.Written ? "已更新" : "內容未變")} "
                        + $"— {r.CommandCount} 個 cmd（{r.SpecCount} 個有 ArgsSpec）→ {r.Path}\n"
                        + "（手動同步：控制台 → Cmd 後台管理頁，或 run_cmd.py run ExportCmdSchema）");
            }
            catch (Exception e)
            {
                // 自動同步是加值機制，失敗絕不可影響編譯流程
                Debug.LogWarning($"[CmdSchema] 每日自動同步失敗（不影響編譯）：{e.Message}");
            }
        }
    }
}
#endif
