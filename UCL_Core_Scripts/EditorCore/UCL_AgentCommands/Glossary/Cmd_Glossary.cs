// 區塊職責: Proposal #25 NeologismGlossary — 自造新詞 + 對應解釋文件 + auto-attach 機制
// 物理意義: 跟 vector offset 機制相反 — 不發明連續向量, 直接造離散詞 + 對應 .md 解釋,
//          用詞時 detect/attach 自動補 refs。對齊「自然語言已是 embedding 高效採樣」哲學。
// 數值影響: 純檔案操作 (docs/Glossary/ 寫入); detect/attach 走 substring match (longest-wins);
//          不動 Treasury / messages / quest。
// 設計取捨:
//   - 一檔一詞 (docs/Glossary/<slug>.md) git diff/merge 友善
//   - frontmatter 含 aliases[] 統一指向 canonical term (避免「basecamp 大小姐」/「basecamp」分裂)
//   - longest-match-wins (per lessons L10) 避免子字串先勝事故
//   - aliases 都當 detect trigger; 命中時 link 到 canonical term .md
//   - 不做 LLM embedding fuzzy match (Phase 2 留 Proposal #25 Backlog)
//   - attach 預設 cap=5 防 ref pollution
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.Glossary
{
    // 區塊職責: 區域 helper — 對齊 Cmd_Tavern / Cmd_Treasury 用 _last_op.md 通報結果
    // 物理意義: Reject = ⚠ 預期失敗 (throw); Resolve = ✅ 成功 (WriteLastOp); FailLastOp = ❌ 例外失敗
    internal static class Cmd_Glossary_Helpers
    {
        public static void ResolveLastOp(string md)
        {
            UCL_ChatTavernRender.WriteLastOp(md);
        }

        // ===========================================================
        // 區塊職責：成功結果同時落一份**帶輪替的 payload**，並回報給呼叫端。
        //
        // 物理意義：`_last_op.md` 是**全 Cmd 共用的單一格** —— 下一支 Cmd 一寫就整份蓋掉。
        //   🩸 gura 2026-08-18 實測：register 完幾秒後一筆 Treasury 查詢就蓋掉它，
        //      而那份報告尾端正掛著「你在自由時間中，下一步是…」的指路。
        //      **掛了但沒人看得到，效果等於沒掛。**
        // ⇒ Tim 拍板補 per-op payload（保留最近 10 筆、收在資料夾裡）。
        //   `_last_op.md` **仍然照寫**：那是既有契約（python 端與酒館頁都讀它），
        //   拿掉它等於為了修耐久度去砍掉一個還有人用的入口。
        //
        // 數值影響：多一次寫檔 ＋ 一次目錄列舉；輪替只發生在超過 10 筆時。
        //   iArgs 為 null（內部呼叫）時仍會落檔，只是不回報路徑。
        // ===========================================================
        public static void ResolveLastOpWithPayload(
            System.Collections.Generic.IDictionary<string, string> iArgs, string iOp, string md, string iScope = null)
        {
            UCL_ChatTavernRender.WriteLastOp(md);
            string aPath = UCL_CmdPayloadStore.Write("Glossary", iOp, md, iScope);
            if (!string.IsNullOrEmpty(aPath)) UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPath);
        }

        public static void RejectLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ Glossary Cmd Rejected\n\n{msg}\n");
            Debug.LogWarning($"[Glossary] {msg}");
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Proposal #25 NeologismGlossary — Cmd_Glossary op-dispatch 入口
    /// 對應 Skill: ucl-glossary
    /// </summary>
    public class Cmd_Glossary : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Glossary";

        public override string ShortDescription =>
            "Neologism Glossary — register 新詞 + detect/attach refs (對應 docs/Glossary/<slug>.md)";

        public override string ArgsSchema =>
            "op=register|lookup|detect|attach|list\n" +
            "register: term=詞 slug=檔名slug [aliases=csv] [category=persona|concept|mechanism|tool|protocol] one_line=一句解說 [body=完整markdown] [created_by=agent_id] [overwrite=true|false]\n" +
            "lookup: term=詞或alias — 回 frontmatter + path (resolve aliases → canonical)\n" +
            "detect: text=要掃的文字 [cap=N(default 10)] — 列命中的 glossary terms (longest-match-wins)\n" +
            "attach: text=要掃的文字 [cap=N(default 5)] — 回原 text + append refs block\n" +
            "list: [category=...] — 列所有 glossary terms";

        public override string ExampleArgs =>
            "op=register;term=basecamp 大小姐;slug=basecamp;aliases=basecamp,Layer 0;category=persona;one_line=Layer 0 alive baseline persona";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Glossary.md";

        // ===========================================================
        // ExecuteAsync — op-dispatch
        // ===========================================================
        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                Cmd_Glossary_Helpers.RejectLastOp("缺少 op 參數 (register|lookup|detect|attach|list)");
                return;
            }

            switch (op)
            {
                case "register": Op_Register(args); break;
                case "lookup": Op_Lookup(args); break;
                case "detect": Op_Detect(args); break;
                case "attach": Op_Attach(args); break;
                case "list": Op_List(args); break;
                default:
                    Cmd_Glossary_Helpers.RejectLastOp($"未知 op: {op} (支援 register|lookup|detect|attach|list)");
                    break;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 路徑常量 — glossary 寫進 git-root/docs/Glossary/<slug>.md
        // ===========================================================
        private static string GlossaryDir
        {
            get
            {
                // 區塊職責: 解析 glossary 根目錄 <git-root>/docs/Glossary
                // 物理意義: 走 UCL_RepoPath.RepoRoot(git-walk 找 .git, 與 Python run_cmd.py 的
                //          GIT_ROOT 對齊), 不再用 Application.dataPath 反推兩層。
                // 數值影響: 舊寫法 Directory.GetParent(dataPath).Parent 假設 EOV nested layout
                //          (<git-root>/CardGame/Assets); 在扁平 layout(Bar/TEVI, <git-root>/Assets)
                //          會多跳一層、落到 git-root 的 parent, 讀到錯的 glossary → auto-attach 命中 0。
                return Path.Combine(UCL.Core.EditorLib.UCL_RepoPath.RepoRoot, "docs", "Glossary");
            }
        }

        // ===========================================================
        // 區塊職責: op=register — 新增/覆寫 glossary 詞
        // 物理意義: 寫 docs/Glossary/<slug>.md, frontmatter + body
        // 數值影響: 存在且 overwrite!=true → reject; 否則寫檔
        // ===========================================================
        private void Op_Register(Dictionary<string, string> args)
        {
            string term = GetArg(args, "term", "");
            string slug = GetArg(args, "slug", "");
            string aliasesCsv = GetArg(args, "aliases", "");
            string category = GetArg(args, "category", "concept");
            string oneLine = GetArg(args, "one_line", "");
            string body = GetArg(args, "body", "");
            string createdBy = GetArg(args, "created_by", "unknown");
            string overwriteRaw = GetArg(args, "overwrite", "false");
            bool overwrite = overwriteRaw == "true" || overwriteRaw == "1";

            if (string.IsNullOrEmpty(term)) { Cmd_Glossary_Helpers.RejectLastOp("register 缺少 term"); return; }
            if (string.IsNullOrEmpty(slug)) { Cmd_Glossary_Helpers.RejectLastOp("register 缺少 slug"); return; }
            if (string.IsNullOrEmpty(oneLine)) { Cmd_Glossary_Helpers.RejectLastOp("register 缺少 one_line (建議 < 80 字 給 ref block 顯示用)"); return; }

            Directory.CreateDirectory(GlossaryDir);

            // 區塊: 跨子資料夾查重 — 防同 slug 在 personas/ 已存在卻在 root 又建一份
            // 物理意義: 遞迴掃所有現存 entry, 比對 slug; 命中既有檔案直接複用該路徑覆寫
            // 數值影響: 若既有檔在子目錄, overwrite=true 時寫回原位置, 不在 root 另建分裂副本
            string fullPath = null;
            var existingEntries = LoadAllEntries();
            var existingHit = existingEntries.FirstOrDefault(e => e.slug == slug);
            if (existingHit != null)
            {
                if (!overwrite)
                {
                    Cmd_Glossary_Helpers.RejectLastOp($"glossary 已存在: docs/Glossary/{existingHit.relativeSubPath} (要覆寫加 overwrite=true)");
                    return;
                }
                fullPath = existingHit.filePath;   // 覆寫原檔, 不論在哪層
            }
            else
            {
                // 新建 — 預設寫 root (向後相容); 想寫子資料夾走檔案系統手動搬
                fullPath = Path.Combine(GlossaryDir, slug + ".md");
            }

            // 區塊職責: frontmatter 組裝
            // 數值影響: aliases CSV → YAML list; 空值 omit
            var aliases = string.IsNullOrEmpty(aliasesCsv)
                ? new List<string>()
                : aliasesCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"term: {EscapeYamlInline(term)}");
            sb.AppendLine($"slug: {slug}");
            if (aliases.Count > 0)
            {
                sb.AppendLine("aliases:");
                foreach (var a in aliases) sb.AppendLine($"  - {EscapeYamlInline(a)}");
            }
            sb.AppendLine($"category: {category}");
            sb.AppendLine($"created_at: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine($"created_by: {createdBy}");
            sb.AppendLine($"one_line: {EscapeYamlInline(oneLine)}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {term}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(body))
            {
                sb.AppendLine(body);
            }
            else
            {
                sb.AppendLine($"> {oneLine}");
                sb.AppendLine();
                sb.AppendLine("_(detailed explanation TBD)_");
            }

            File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(false));

            var report = new StringBuilder();
            report.AppendLine($"✅ glossary registered: **{term}** (slug: {slug})");
            report.AppendLine($"- category: {category}");
            report.AppendLine($"- aliases: {(aliases.Count > 0 ? string.Join(", ", aliases) : "(none)")}");
            // 計算 register 後的相對路徑顯示 (可能在 subdir, 用 existingHit 路徑反推)
            string reportRel = existingHit != null
                ? existingHit.relativeSubPath
                : slug + ".md";
            report.AppendLine($"- path: docs/Glossary/{reportRel}");
            report.AppendLine($"- created_by: {createdBy}");
            // 區塊職責：本人若正在自由時間中，回傳值尾端多附一段流程提示（Tim 2026-08-18）。
            // 物理意義：`glossary-entry` 是自由時間「知識沉澱」組的活動，入口是本 Cmd 而非 python 腳本，
            //          所以 `op=step` 代跑不到它。走 helper 讓 **Cmd 自己回報進流程**，
            //          而不是在活動層長出第二種 tool 形式去呼叫 Cmd（那會多一條流程，兩條漂移時都不報錯）。
            // 數值影響：不在自由時間時一個字都不加。
            // 邊界：createdBy 是自由字串（可能不是 persona）—— 查不到 session 就等於不在，不會誤印。
            UCL.Core.EditorLib.AgentCommands.UCL_FreeTimeHint.Append(report, createdBy);
            // 走帶輪替的 payload —— register 是**有產物**的 op，它的報告值得活過下一支 Cmd
            // （`_last_op.md` 共用一格，會被蓋掉；見 ResolveLastOpWithPayload 的血證）。
            Cmd_Glossary_Helpers.ResolveLastOpWithPayload(args, "register", report.ToString(), createdBy);
        }

        // ===========================================================
        // 區塊職責: op=lookup — alias 解析 → canonical
        // 物理意義: 給定詞或 alias, 找 canonical .md → 回 frontmatter + path
        // ===========================================================
        private void Op_Lookup(Dictionary<string, string> args)
        {
            string term = GetArg(args, "term", "");
            if (string.IsNullOrEmpty(term)) { Cmd_Glossary_Helpers.RejectLastOp("lookup 缺少 term"); return; }

            var entries = LoadAllEntries();
            var hit = ResolveCanonical(term, entries);
            if (hit == null)
            {
                Cmd_Glossary_Helpers.ResolveLastOp($"❌ glossary not found: `{term}` (沒有 term/alias 命中)");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"✅ glossary hit: **{hit.term}**");
            sb.AppendLine($"- slug: {hit.slug}");
            sb.AppendLine($"- category: {hit.category}");
            sb.AppendLine($"- aliases: {(hit.aliases.Count > 0 ? string.Join(", ", hit.aliases) : "(none)")}");
            sb.AppendLine($"- one_line: {hit.oneLine}");
            sb.AppendLine($"- path: docs/Glossary/{hit.relativeSubPath}");
            Cmd_Glossary_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: op=detect — 掃文字命中 glossary terms
        // 物理意義: substring match (case-insensitive), longest-match-wins (per L10)
        //          一個 canonical term 不重複命中 (即使 alias 多次出現)
        // 數值影響: 回命中 terms 清單 (canonical 形式), 上限 cap
        // ===========================================================
        private void Op_Detect(Dictionary<string, string> args)
        {
            string text = GetArg(args, "text", "");
            string capStr = GetArg(args, "cap", "10");
            int cap = int.TryParse(capStr, out var c) ? Math.Max(1, c) : 10;
            if (string.IsNullOrEmpty(text)) { Cmd_Glossary_Helpers.RejectLastOp("detect 缺少 text"); return; }

            var entries = LoadAllEntries();
            var hits = DetectHits(text, entries, cap);

            var sb = new StringBuilder();
            sb.AppendLine($"📖 detect 結果 ({hits.Count} 命中, cap={cap}):");
            foreach (var h in hits)
            {
                sb.AppendLine($"- **{h.term}** (matched: `{h.matchedAlias}`) → docs/Glossary/{h.relativeSubPath}");
            }
            if (hits.Count == 0) sb.AppendLine("_(無命中)_");
            Cmd_Glossary_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: op=attach — 在 text 結尾 append refs block
        // 物理意義: detect 命中後格式化成 markdown refs block append
        // 數值影響: 預設 cap=5 防 ref 污染; 命中 0 不 append (保留原 text)
        // ===========================================================
        private void Op_Attach(Dictionary<string, string> args)
        {
            string text = GetArg(args, "text", "");
            string capStr = GetArg(args, "cap", "5");
            int cap = int.TryParse(capStr, out var c) ? Math.Max(1, c) : 5;
            if (string.IsNullOrEmpty(text)) { Cmd_Glossary_Helpers.RejectLastOp("attach 缺少 text"); return; }

            // 共用 static helper — 同邏輯給跨 Cmd 呼叫 (e.g. Cmd_Tavern Op_Post auto-attach)
            string attached = AppendRefsToText(text, cap, forceReattach: true);
            Cmd_Glossary_Helpers.ResolveLastOp(attached);
        }

        // ===========================================================
        // 區塊職責: 公開 static helper — text 內掃 glossary terms + append refs block
        // 物理意義: 給 Cmd_Tavern Op_Post 等其他 handler 跨呼叫做 write-time auto-attach;
        //          跟 op=attach 同邏輯, 但 idempotent (text 已含 marker 直接返回)
        // 數值影響: 命中 0 → 原樣返回; 命中 > 0 → 末尾 append refs block; null/empty → 原樣返回
        // 安全性: 任何例外 swallow → 回傳原 text (絕不擋上游 caller 流程)
        // ===========================================================
        public const string AUTO_ATTACH_MARKER = "📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):";

        public static string AppendRefsToText(string text, int cap = 5, bool forceReattach = false)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // idempotent guard — 已含 marker 直接 return 避免雙重 attach (Op_Attach 走 forceReattach=true 跳過)
            if (!forceReattach && text.Contains(AUTO_ATTACH_MARKER)) return text;

            try
            {
                var entries = LoadAllEntries();
                if (entries.Count == 0) return text;
                var hits = DetectHits(text, entries, Math.Max(1, cap));
                if (hits.Count == 0) return text;

                var sb = new StringBuilder();
                sb.Append(text.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(AUTO_ATTACH_MARKER);
                sb.AppendLine();
                foreach (var h in hits)
                {
                    sb.AppendLine($"- **{h.term}**: {h.oneLine}");
                    sb.AppendLine($"(docs/Glossary/{h.relativeSubPath})");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Glossary] AppendRefsToText 失敗 (返回原 text): {ex.Message}");
                return text;
            }
        }

        // ===========================================================
        // 區塊職責: op=list — 列所有 glossary entries
        // 物理意義: 給 audit / agent 探索 glossary 用
        // ===========================================================
        private void Op_List(Dictionary<string, string> args)
        {
            string categoryFilter = GetArg(args, "category", "");
            var entries = LoadAllEntries();
            if (!string.IsNullOrEmpty(categoryFilter))
            {
                entries = entries.Where(e => e.category == categoryFilter).ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"📚 glossary list ({entries.Count} entries{(string.IsNullOrEmpty(categoryFilter) ? "" : ", category=" + categoryFilter)}):");
            sb.AppendLine();
            sb.AppendLine("| Term | Category | Aliases | One-line |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in entries.OrderBy(x => x.category).ThenBy(x => x.slug))
            {
                string aliasesStr = e.aliases.Count > 0 ? string.Join(", ", e.aliases) : "—";
                sb.AppendLine($"| `{e.slug}` **{e.term}** | {e.category} | {aliasesStr} | {e.oneLine} |");
            }
            Cmd_Glossary_Helpers.ResolveLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: 內部 — glossary entry POCO + 載入 + lookup
        // ===========================================================
        private class GlossaryEntry
        {
            public string term;
            public string slug;
            public List<string> aliases = new List<string>();
            public string category;
            public string oneLine;
            public string filePath;
            // 區塊職責: 相對於 docs/Glossary/ 的子路徑 (例如 "basecamp.md" 或 "personas/basecamp.md")
            // 物理意義: 支援子資料夾組織 (per Proposal: persona/trigger/protocol 分類);
            //          顯示時拼 "docs/Glossary/" + relativeSubPath 給出完整 markdown link
            // 數值影響: forward-slash 統一 (跨平台 link friendly)
            public string relativeSubPath;
        }

        private class DetectHit
        {
            public string term;
            public string slug;
            public string oneLine;
            public string matchedAlias;
            public int startPos;   // 命中位置 (給 longest-match-wins 排序)
            public int length;     // 命中字串長度
            public string relativeSubPath;   // 跟 GlossaryEntry 對齊 — 給 attach output 用完整 link
        }

        // 區塊職責: 載入所有 glossary entries (遞迴掃子資料夾)
        // 物理意義: SearchOption.AllDirectories — 支援 personas/ / triggers/ 等子分類目錄;
        //          flat 結構仍正常運作 (backward compat)
        // 數值影響: 每筆 entry 計算 relativeSubPath = filePath 相對 GlossaryDir 的子路徑 (forward-slash 標準化)
        private static List<GlossaryEntry> LoadAllEntries()
        {
            var list = new List<GlossaryEntry>();
            if (!Directory.Exists(GlossaryDir)) return list;
            foreach (var f in Directory.GetFiles(GlossaryDir, "*.md", SearchOption.AllDirectories))
            {
                // 跳過 README / index (檔名比對, 不論在哪層目錄)
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Equals("README", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;

                var entry = ParseEntry(f);
                if (entry != null)
                {
                    // 區塊: 計算 relativeSubPath — 相對 GlossaryDir, forward-slash 標準化
                    // 例: GlossaryDir="D:\repo\docs\Glossary", f="D:\repo\docs\Glossary\personas\basecamp.md"
                    //     → relativeSubPath="personas/basecamp.md"
                    string rel = f.Substring(GlossaryDir.Length).TrimStart('\\', '/');
                    entry.relativeSubPath = rel.Replace('\\', '/');
                    list.Add(entry);
                }
            }
            return list;
        }

        // 區塊職責: 簡易 frontmatter parser (term/slug/aliases/category/one_line)
        // 物理意義: 只 parse 我們關心的 fields, 不做 generic YAML
        // 數值影響: 失敗 entry 直接 return null (容忍格式錯 → skip 該檔不 crash)
        private static GlossaryEntry ParseEntry(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                if (!content.StartsWith("---")) return null;
                int end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end < 0) return null;
                string fm = content.Substring(3, end - 3);

                var entry = new GlossaryEntry { filePath = filePath };
                string currentList = null;
                foreach (var rawLine in fm.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("  - ") && currentList == "aliases")
                    {
                        entry.aliases.Add(UnescapeYamlInline(line.Substring(4).Trim()));
                        continue;
                    }
                    if (line.StartsWith("- ") && currentList == "aliases")
                    {
                        entry.aliases.Add(UnescapeYamlInline(line.Substring(2).Trim()));
                        continue;
                    }
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) continue;
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = colonIdx < line.Length - 1 ? line.Substring(colonIdx + 1).Trim() : "";
                    if (string.IsNullOrEmpty(val))
                    {
                        currentList = key;
                        continue;
                    }
                    currentList = null;
                    switch (key)
                    {
                        case "term": entry.term = UnescapeYamlInline(val); break;
                        case "slug": entry.slug = val; break;
                        case "category": entry.category = val; break;
                        case "one_line": entry.oneLine = UnescapeYamlInline(val); break;
                    }
                }
                if (string.IsNullOrEmpty(entry.term) || string.IsNullOrEmpty(entry.slug)) return null;
                return entry;
            }
            catch { return null; }
        }

        // 區塊職責: alias-aware lookup (term/alias 都接受)
        private static GlossaryEntry ResolveCanonical(string query, List<GlossaryEntry> entries)
        {
            // 1. exact term match (case-insensitive)
            foreach (var e in entries)
            {
                if (string.Equals(e.term, query, StringComparison.OrdinalIgnoreCase)) return e;
            }
            // 2. slug match
            foreach (var e in entries)
            {
                if (string.Equals(e.slug, query, StringComparison.OrdinalIgnoreCase)) return e;
            }
            // 3. alias match
            foreach (var e in entries)
            {
                foreach (var a in e.aliases)
                {
                    if (string.Equals(a, query, StringComparison.OrdinalIgnoreCase)) return e;
                }
            }
            return null;
        }

        // 區塊職責: detect hits (substring scan, longest-match-wins, dedupe by slug)
        // 物理意義: text 內掃所有 term+aliases 命中位置; 多命中區間重疊 → 取最長的;
        //          同一 slug 多處命中 → 只算一次 (取第一個 hit 位置)
        // 數值影響: O(text_len × terms_total); terms 數 < 200 級別性能 OK
        private static List<DetectHit> DetectHits(string text, List<GlossaryEntry> entries, int cap)
        {
            // 收集所有 raw hit (term + 各 alias 都當 trigger)
            var rawHits = new List<DetectHit>();
            string lowerText = text.ToLowerInvariant();
            foreach (var e in entries)
            {
                AddHitsFor(e, e.term, lowerText, text, rawHits);
                foreach (var a in e.aliases)
                {
                    AddHitsFor(e, a, lowerText, text, rawHits);
                }
            }

            // longest-match-wins: 按 startPos sort, 同 startPos 取最長; 排除被其他 hit 完全包含的
            rawHits.Sort((a, b) =>
            {
                int c = a.startPos.CompareTo(b.startPos);
                return c != 0 ? c : b.length.CompareTo(a.length);   // 同 start, 長的優先
            });

            var selected = new List<DetectHit>();
            int lastEnd = -1;
            foreach (var h in rawHits)
            {
                if (h.startPos < lastEnd) continue;   // 跟前一個 selected 重疊 → skip (longer-wins already)
                selected.Add(h);
                lastEnd = h.startPos + h.length;
            }

            // dedupe by slug (一個 canonical term 只保留第一次 hit)
            var seen = new HashSet<string>();
            var result = new List<DetectHit>();
            foreach (var h in selected)
            {
                if (seen.Contains(h.slug)) continue;
                seen.Add(h.slug);
                result.Add(h);
                if (result.Count >= cap) break;
            }
            return result;
        }

        private static void AddHitsFor(GlossaryEntry e, string trigger, string lowerText, string originalText, List<DetectHit> rawHits)
        {
            if (string.IsNullOrEmpty(trigger)) return;
            string lowerTrigger = trigger.ToLowerInvariant();
            int idx = 0;
            while (idx < lowerText.Length)
            {
                int found = lowerText.IndexOf(lowerTrigger, idx, StringComparison.Ordinal);
                if (found < 0) break;
                rawHits.Add(new DetectHit
                {
                    term = e.term,
                    slug = e.slug,
                    oneLine = e.oneLine,
                    matchedAlias = trigger,
                    startPos = found,
                    length = lowerTrigger.Length,
                    relativeSubPath = e.relativeSubPath,
                });
                idx = found + lowerTrigger.Length;
            }
        }

        // ===========================================================
        // YAML inline escape — 用 double-quote 包含 : / # / 空格 / 中文; 簡單版
        // ===========================================================
        private static string EscapeYamlInline(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            // 需要 quote 的條件: 含 : / # / start with - / 含 quote
            if (s.Contains(":") || s.Contains("#") || s.StartsWith("-") || s.Contains("\""))
            {
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }
            return s;
        }

        private static string UnescapeYamlInline(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }
    }
}
#endif
