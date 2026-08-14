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
// 數值影響：只寫一個檔（<RepoRoot>/AgentCommands/commands_schema.json，**不入 git** ——
//          per-project 衍生物，Tim 2026-08-14 拍板，見 ComputeSourceHash 上方的 📌）。
//          **內容未變則不落筆**（不動 mtime、不觸發 asset import）。
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
        // 物理意義：**判同步與否一律用內容雜湊，不用 mtime**。git 不儲存 mtime
        //          （`git ls-tree` 只有 mode/type/blob/name），clone 或 checkout 後所有檔案的 mtime
        //          都是「當下寫檔時間」，先後只取決於寫檔次序（gura QA 2026-07-29 推翻 mtime 原案）。
        //          內容雜湊與檔案時間、clone 順序、時區全部無關。
        //          ⚠ mtime 仍**可以**當本機快取鍵用（見 ComputeStatSignature）—— 那是兩件事：
        //          當快取鍵時猜錯只是白算一次；當同步判準時猜錯會把「已改」洗成「同步」。
        // 數值影響：純讀檔計算，不寫任何東西。
        //
        // 📌 產物**不入版控**（Tim 2026-08-14 拍板；同日移出追蹤並加進 AgentCommands/.gitignore）。
        //   理由：UCL_Core 與 Cmd 體系是跨專案共用 lib，各專案有自己的專屬 Cmd，
        //   於是 cmd 清單 / source_files / source_hash **每個專案都不同**。入 git 的後果不是衝突，
        //   是 A 專案 commit 的產物在 B 專案永遠顯示「過期」→ 預檢自動降級成不擋。
        //   （本段原本寫著相反的話「clone 下來直接用正是本產物入 git 的主要理由」，
        //    與下方 AutoSync 的註解互相矛盾了一段時間；2026-08-14 實查後以後者為準。）
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
        // 檔案清單快取 —— 見下方 CollectSourceFiles 的成本說明。
        static SortedDictionary<string, string> s_CachedSourceFiles;

        static SortedDictionary<string, string> CollectSourceFiles()
        {
            // 區塊職責：清單快取（Tim 2026-07-30 回報面板卡頓的**主因**）。
            // 物理意義：下面那兩個 GetFiles(AllDirectories) 是整棵 Assets 的遞迴掃描 ——
            //          本專案 Assets 底下有 46633 個項目，實測**一次 213ms**。
            //          IsInSync 走 ComputeSourceHash → 走這裡，而 IMGUI 每 frame 呼叫 IsInSync，
            //          等於每秒花數百 ms 在重複列舉同一批檔 → 面板整個卡住。
            // 數值影響：快取範圍是**一次 domain reload**（static 欄位）。這正好是安全邊界：
            //          Cmd 檔集合只會因「新增/刪除 .cs」而變，而那必然觸發編譯 → domain reload → 快取歸零。
            //          另有 InvalidateSyncCache() 可手動清（生成後呼叫）。
            if (s_CachedSourceFiles != null) return s_CachedSourceFiles;

            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string repoRoot = UCL_RepoPath.RepoRoot.Replace('\\', '/').TrimEnd('/');
            string assetsDir = Path.Combine(UCL_RepoPath.UnityProjectRoot, "Assets");
            if (!Directory.Exists(assetsDir)) return result;

            // ① Cmd_*.cs ＋ UCL_AgentCommandRegistry.cs（後者檔名不符 Cmd_* 但改它會改變產物內容）
            // **一次走訪、就地篩兩種檔名** —— 原本是兩次 GetFiles(AllDirectories)，等於把整棵
            // Assets（46633 項）走兩遍。合併後省掉一半，語意完全相同（結果照 SortedDictionary 排序）。
            foreach (var abs in Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(abs);
                if (name.StartsWith("Cmd_", StringComparison.Ordinal)
                    || string.Equals(name, "UCL_AgentCommandRegistry.cs", StringComparison.Ordinal))
                {
                    AddRelative(result, repoRoot, abs);
                }
            }
            s_CachedSourceFiles = result;
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
        // ===========================================================
        // 區塊職責：schema 預檢總開關（Tim 2026-07-30 追加）—— 關閉時等同「產物不存在」。
        // 物理意義：**用檔案旗標而不是 EditorPrefs**，因為這個開關要跨語言生效：
        //          C# 端據它停止更新產物，Python 端據它跳過預檢。EditorPrefs 只有 C# 讀得到，
        //          再叫 C# 把狀態鏡射進某處給 Python 讀，就又是一份雙端鏡像 —— 那正是本工作在治的病。
        //          旗標檔存在 = 停用（檔案存在與否本身就是狀態，不必解析內容，沒有格式可漂）。
        // 數值影響：停用時 → Export()/AutoSync 一律不寫檔（產物凍結在停用當下的版本）；
        //          Python 端不讀產物、不驗雜湊、不做參數預檢，行為與產物不存在時逐字相同。
        //          per-machine（gitignored）：這是「我這台機器現在不想要預檢」，不該傳染給別人。
        // ===========================================================
        public const string DisableFlagFileName = "_cmd_schema_disabled.local";

        public static string DisableFlagPath =>
            Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, DisableFlagFileName);

        /// <summary>schema 預檢是否已停用（旗標檔存在即停用）。</summary>
        public static bool PreflightDisabled
        {
            get { try { return File.Exists(DisableFlagPath); } catch { return false; } }
            set
            {
                try
                {
                    string path = DisableFlagPath;
                    if (value)
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        // 內容純粹給人看；判定只看檔案在不在
                        File.WriteAllText(path,
                            "schema 預檢已停用（本機）。\n"
                            + "效果：C# 不再更新 commands_schema.json；Python 端跳過參數預檢（等同產物不存在）。\n"
                            + "重新啟用：控制台 → Cmd 後台管理頁 → 勾回「啟用 schema 預檢」，或直接刪除本檔。\n",
                            new UTF8Encoding(false));
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CmdSchema] 切換預檢開關失敗：{e.Message}");
                }
            }
        }

        public struct ExportResult
        {
            /// <summary>是否真的寫了檔（內容未變 → false）。</summary>
            public bool Written;
            /// <summary>是否因為預檢已停用而整個跳過（此時 Written 必為 false）。</summary>
            public bool SkippedDisabled;
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
            // 停用中 → 不生成、不寫檔（產物凍結）。這條擋在最前面，連 BuildSchemaJson 的反射成本都不付。
            // 三個入口（面板 / Cmd_ExportCmdSchema / AutoSync）都走本方法，所以擋這裡就是全擋。
            if (PreflightDisabled)
            {
                return new ExportResult
                {
                    Written = false,
                    SkippedDisabled = true,
                    Path = SchemaPath,
                    SourceHash = "",
                    CommandCount = 0,
                    SpecCount = 0,
                };
            }
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
            // 在清快取**之前**取雜湊：此時清單快取仍熱，直接算即可；
            // 清了再算會讓 Export 自己多付一次整棵 Assets 的遞迴掃描（實測 213ms）。
            string sourceHash = ComputeSourceHash();
            // 產物剛變 → 同步狀態快取必須失效，否則面板會在節流窗內繼續顯示生成前的舊判定
            // （「已更新產物」卻還標著 ⚠ 未同步，看起來像沒生效）。放在最後一步。
            InvalidateSyncCache();
            return new ExportResult
            {
                Written = needWrite,
                Path = path,
                SourceHash = sourceHash,
                CommandCount = handlers.Count,
                SpecCount = specCount,
            };
        }

        // ===========================================================
        // 區塊職責：同步狀態查詢（面板用）—— 產物內的 source_hash 是否等於當前來源雜湊。
        // 物理意義：面板要能在**不寫檔**的前提下回答「現在同步了嗎」。
        // 數值影響：純讀取。產物不存在 / 解析不出 hash → 回 false（視為未同步）。
        // ===========================================================
        /// <summary>每日自動同步的「上次檢查時間」UCL_ProjectEditorPrefs key（per-project，見 <see cref="UCL_CmdSchemaAutoSync"/>）。</summary>
        public const string AutoSyncPrefKey = "UCL_CmdSchema_LastAutoSyncTicks";

        /// <summary>上次自動檢查時間（本機）；從未檢查過回 <see cref="DateTime.MinValue"/>。</summary>
        public static DateTime LastAutoSyncUtc
        {
            get
            {
                string raw = UCL_ProjectEditorPrefs.GetString(AutoSyncPrefKey, "");
                return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
            set => UCL_ProjectEditorPrefs.SetString(AutoSyncPrefKey, value.Ticks.ToString());
        }

        // ===========================================================
        // 區塊職責：IsInSync 的快取層 —— IMGUI 每個 frame 都會呼叫，不可每次都算完整雜湊。
        // 物理意義：ComputeSourceHash() 要讀 52 個檔的完整 bytes（實測 ~1.1s）。而 IMGUI 的
        //          OnGUI 每秒重繪多次（Layout + Repaint 各一輪），等於每秒讀上百個檔 ——
        //          面板一開就卡死（Tim 2026-07-30 回報）。
        // 數值影響：兩段防護，成本遞減：
        //   ① 時間節流：距上次檢查未滿 MinRecheckSeconds → 直接回傳上次結果（frame 內零 IO）。
        //   ② stat 簽章：滿了才 stat 52 次（每檔 mtime+size，~1ms）；簽章沒變 → 沿用上次算出的雜湊，
        //      仍不讀檔。簽章變了才真的重算。
        // ⚠ mtime 只是**快取失效提示**，不是正確性判準 —— 判同步與否的權威始終是內容 SHA-256。
        //   反過來用才安全：簽章一變就重算（白算一次無害），不會把「已改動」洗成「還同步」。
        //   這與 Python 端 tavern_cmd._stat_signature 是同一套設計，兩端同構。
        // ===========================================================
        const double MinRecheckSeconds = 1.0;

        static double s_LastCheckTime = -1;
        static string s_CachedStatSig;
        static string s_CachedSourceHash;
        static string s_CachedArtifactHash;
        static bool s_CachedInSync;

        // 區塊職責：把 ②stat 簽章那層快取**跨 domain reload 保留**（本機、per-project，不入 git）。
        // 物理意義：上面的 static 欄位每次 domain reload 就歸零，而 AutoSync 正好掛在編譯之後 ——
        //          於是每次編譯都必然落到「簽章沒有可比對的舊值」→ 付一次完整雜湊（~1.1s）。
        //          原本的日期節流就是為了迴避這個成本，代價是**改完最多 24 小時都是過期的**
        //          （2026-08-14：整個上午每一條 Cmd 都在印降級警告，根因即此）。
        //          把簽章與它對應的雜湊一起存進 prefs 之後，「來源沒動過」只要 52 次 stat（~1ms）
        //          就能判定，於是節流不再需要 —— 真的改了才付那 1.1s，而那正是該付的時候。
        // 數值影響：⚠ 兩個值**必須成對讀寫** —— 簽章對應的是「算那次雜湊時的來源狀態」，
        //          只存一個等於下次拿舊雜湊去配新簽章。存錯配對不會報錯，會安靜地宣稱已同步。
        //          ⚠ 也**只能**當快取鍵用：mtime 在 clone 後只反映寫檔次序（gura QA 2026-07-29 推翻過
        //          「拿 mtime 當同步判準」的方案，而本產物確實入 git）。這裡安全是因為
        //          新 clone 的機器 prefs 是空的 → 簽章對不上 → 照樣重算，永遠不會把「已改」洗成「同步」。
        const string StatSigPrefKey = "UCL_CmdSchema_LastStatSig";
        const string SourceHashPrefKey = "UCL_CmdSchema_LastSourceHash";

        /// <summary>把 stat 簽章與其對應的內容雜湊從 prefs 載回記憶體快取（只在記憶體快取為空時）。</summary>
        static void SeedCacheFromPrefs()
        {
            if (s_CachedStatSig != null) return;
            string sig = UCL_ProjectEditorPrefs.GetString(StatSigPrefKey, "");
            string hash = UCL_ProjectEditorPrefs.GetString(SourceHashPrefKey, "");
            // 成對才採用 —— 缺一個就當作沒有（寧可多算一次，不可拿舊雜湊配新簽章）
            if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(hash)) return;
            s_CachedStatSig = sig;
            s_CachedSourceHash = hash;
        }

        /// <summary>把當前的 stat 簽章與內容雜湊成對寫進 prefs。</summary>
        static void PersistCacheToPrefs()
        {
            if (string.IsNullOrEmpty(s_CachedStatSig) || string.IsNullOrEmpty(s_CachedSourceHash)) return;
            UCL_ProjectEditorPrefs.SetString(StatSigPrefKey, s_CachedStatSig);
            UCL_ProjectEditorPrefs.SetString(SourceHashPrefKey, s_CachedSourceHash);
        }

        /// <summary>清掉同步狀態快取 —— 生成後或使用者手動要求時呼叫，下次查詢會重算。</summary>
        public static void InvalidateSyncCache()
        {
            s_LastCheckTime = -1;
            s_CachedStatSig = null;
            s_CachedSourceFiles = null;     // 檔案清單也一併重掃（新增/刪除 Cmd 檔後才需要）
            // prefs 也要清，否則下次 SeedCacheFromPrefs 會把剛清掉的那份載回來（清了等於沒清）
            UCL_ProjectEditorPrefs.SetString(StatSigPrefKey, "");
            UCL_ProjectEditorPrefs.SetString(SourceHashPrefKey, "");
        }

        // 便宜的變更偵測：只 stat 不讀內容。格式為「相對路徑|mtime ticks|長度」串接後雜湊。
        static string ComputeStatSignature()
        {
            var files = CollectSourceFiles();
            var sb = new StringBuilder();
            foreach (var kv in files)
            {
                try
                {
                    var fi = new FileInfo(kv.Value);
                    sb.Append(kv.Key).Append('|').Append(fi.LastWriteTimeUtc.Ticks)
                      .Append('|').Append(fi.Length).Append('\n');
                }
                catch
                {
                    sb.Append(kv.Key).Append("|missing\n");
                }
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        public static bool IsInSync(out string artifactHash, out string currentHash)
        {
            // ① 時間節流 —— IMGUI 一個 frame 內可能呼叫多次，這層讓其餘呼叫零成本
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (s_LastCheckTime >= 0 && now - s_LastCheckTime < MinRecheckSeconds)
            {
                artifactHash = s_CachedArtifactHash;
                currentHash = s_CachedSourceHash;
                return s_CachedInSync;
            }
            s_LastCheckTime = now;

            // ② stat 簽章沒變 → 來源檔沒動過，沿用上次算出的內容雜湊（不讀檔）
            //    先從 prefs 載回上次的簽章/雜湊 —— 記憶體快取撐不過 domain reload，
            //    而 AutoSync 正好每次編譯後跑（見 SeedCacheFromPrefs 的區塊註解）。
            SeedCacheFromPrefs();
            string sig = ComputeStatSignature();
            currentHash = (sig == s_CachedStatSig && s_CachedSourceHash != null)
                ? s_CachedSourceHash
                : ComputeSourceHash();
            s_CachedStatSig = sig;
            s_CachedSourceHash = currentHash;
            PersistCacheToPrefs();

            bool result = IsInSyncUncached(currentHash, out artifactHash);
            s_CachedArtifactHash = artifactHash;
            s_CachedInSync = result;
            return result;
        }

        static bool IsInSyncUncached(string currentHash, out string artifactHash)
        {
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
    // ⚠ 節流時間戳存 **UCL_ProjectEditorPrefs（per-project）**，刻意不寫進產物：
    //   產物入 git 且我們特意移除了所有 wall-clock 欄位以達成「內容沒變 = 零 diff」；
    //   把「上次檢查時間」寫回產物等於親手把剛消滅的 diff 噪音請回來，而且會讓每台機器
    //   互相覆寫對方的時間戳 —— 那是 per-machine 狀態，本來就不該進版控。
    // ===========================================================
    // 區塊職責：編譯完成後讓產物跟上來源 —— 「新鮮」這件事由**內容**決定，不由時間決定。
    // 物理意義：2026-08-14 改版前這裡是**每日節流**：未到期且產物存在就直接 return，連 hash 都不比。
    //          於是改完 Cmd 的 C# 之後，產物最多 24 小時都是過期的，而過期時 Python 端預檢
    //          **自動降級成不擋** —— 那行降級警告因此天天出現，被讀成背景音（判準 1：假警報比沒有警報更糟）。
    //          節流的原因是真的：ComputeSourceHash 要讀 52 個檔完整 bytes（實測 ~1.1s）。
    //          但 IsInSync 早就有 stat 簽章那層便宜閘（~1ms），只是它的快取活不過 domain reload，
    //          所以每次編譯都會落到完整雜湊 —— 節流其實是在迴避「快取沒跨 reload」這件事。
    //          把簽章跨 reload 保存之後（見 SeedCacheFromPrefs），便宜閘真的變便宜，節流就不再需要。
    // 數值影響：未動過 Cmd 來源的編譯 ≈ 52 次 stat；動過才付 1.1s 並重寫產物。
    //          **不再有「過期但沒人補」的窗口** —— 最多過期一次編譯的時間。
    [UnityEditor.InitializeOnLoad]
    public static class UCL_CmdSchemaAutoSync
    {

        // 區塊職責：觸發點 —— **domain reload 之後**，不是 compilationFinished。
        // 物理意義：`compilationFinished` 跑在**舊 domain**（新 assembly 尚未載入）。在那裡匯出會產生
        //          一份**新鮮度戳記與內容來自不同時刻**的產物：
        //            · source_files / source_hash 來自檔案系統 → 已含新檔（新的）
        //            · commands 來自 UCL_AgentCommandRegistry.ListHandlers() 反射 → 舊 assembly（舊的）
        //          2026-08-14 實證：新增 Cmd_SchemaSelfTest.cs 後，產物的 source_files 有它、
        //          commands 沒有它，而 **hash 卻相符** —— 於是 IsInSync 從此回 true，
        //          往後每次編譯都早退，這份錯的產物永遠不會被自動修正（只能手動 ExportCmdSchema）。
        //          比「沒更新」更糟：沒更新會被 hash 抓到，這種錯**帶著一枚有效的新鮮度戳記**。
        // 數值影響：改掛 delayCall（InitializeOnLoad 靜態建構子在 reload 後執行，delayCall 再延到
        //          該幀結束，確保 registry 已就緒）。代價是每次 domain reload 都跑一次便宜閘，
        //          而那本來就是我們要的頻率。
        static UCL_CmdSchemaAutoSync()
        {
            // InitializeOnLoad 確保 Editor 啟動 / domain reload 時都會掛上（與 UCL_CompileErrorTracker 同慣例）
            UnityEditor.EditorApplication.delayCall += OnAfterDomainReload;
        }

        static void OnAfterDomainReload()
        {
            try
            {
                // 停用中 → 連檢查都不做（「停止更新產物」的字面意思）。擋在最前面，零成本返回。
                if (UCL_CmdSchemaExporter.PreflightDisabled) return;

                DateTime now = DateTime.UtcNow;

                // 區塊職責：產物不存在 → 立刻生成（Tim 2026-07-30 拍板）
                // 物理意義：缺席時 Python 端會**整個跳過參數預檢**（fail-open），所以一刻都不能等。
                //          「缺檔」與「檔舊了」是兩種不同狀況，但改用內容判定之後兩者都是立刻補 ——
                //          差別只剩「缺檔連 hash 都不必算」。
                // 📌 產物**不入版控**（per-project 衍生物）—— 2026-08-14 實查發現它當時仍被
                //   AgentCommands submodule 追蹤，且與 ComputeSourceHash 上方的註解互相矛盾。
                //   Tim 同日拍板：跨專案 lib ＋ 各專案專屬 Cmd ⇒ 產物內容天生不同，不該入 git。
                //   已移出追蹤並加進 .gitignore，本註解與上方均已對齊事實。
                bool missing = !File.Exists(UCL_CmdSchemaExporter.SchemaPath);

                UCL_CmdSchemaExporter.LastAutoSyncUtc = now;   // 純資訊（面板顯示「上次檢查」），不再當閘門
                if (missing)
                {
                    var rm = UCL_CmdSchemaExporter.Export();
                    Debug.Log($"[CmdSchema] 產物不存在 → 已自動生成（不受每日節流限制）"
                            + $"— {rm.CommandCount} 個 cmd（{rm.SpecCount} 個有 ArgsSpec）→ {rm.Path}");
                    return;
                }
                // 已同步 → 什麼都不必做（out 需具名：本專案 C# 版本不接受無型別的 `out _`）
                // IsInSync 內含 stat 簽章便宜閘：來源沒動過時只 stat 不讀檔（~1ms），
                // 這就是拿掉日期節流之後仍然不卡編譯的原因。
                string artifactHash, currentHash;
                if (UCL_CmdSchemaExporter.IsInSync(out artifactHash, out currentHash)) return;

                var r = UCL_CmdSchemaExporter.Export();
                Debug.Log($"[CmdSchema] 來源變動 → 自動同步：{(r.Written ? "已更新" : "內容未變")} "
                        + $"— {r.CommandCount} 個 cmd（{r.SpecCount} 個有 ArgsSpec）→ {r.Path}\n"
                        + "（手動同步：控制台 → Cmd 後台管理頁，或 run_cmd.py run ExportCmdSchema）");
            }
            catch (Exception e)
            {
                // 自動同步是加值機制，失敗絕不可影響編譯流程
                Debug.LogWarning($"[CmdSchema] domain reload 後自動同步失敗（不影響編譯）：{e.Message}");
            }
        }
    }
}
#endif
