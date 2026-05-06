
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：本檔提供「即時模糊搜尋全 Markdown 文件」的 Agent Command。
// 物理意義：給定 query → 遞迴 live scan markdown roots → 解析 frontmatter →
//          對 query 做 synonym expansion（每篇 doc 的 aliases + 可選的中央
//          _synonyms 檔）→ 對每個 entry 計分（title/aliases/tags/description/
//          filename 各權重不同）→ top-N 排序輸出。**不依賴 _catalog.md**，
//          每次都 cold scan，永遠新鮮（200 篇 SSD 上 <200ms）。
// 數值影響：純讀取；只有當 outputPath 提供時才寫檔，否則僅 Debug.Log。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：以關鍵字（含同義詞展開）即時搜尋全 Markdown 文件，輸出 ranked top-N。
    ///
    /// 與 <see cref="Cmd_ExportDocsCatalog"/> 的差異：
    /// <list type="table">
    ///   <item><term>ExportDocsCatalog</term><description>產靜態索引給 Ctrl+F 用</description></item>
    ///   <item><term>SearchDocs</term><description>每次 live scan 並做 ranking，**不讀 catalog**</description></item>
    /// </list>
    ///
    /// 計分權重：title=10 / aliases=8 / tags=6 / description=5 / filename=4。
    /// AND 語義（預設）要求 query 所有 term 都至少在某個欄位命中；OR 語義任一即可。
    /// Synonym expansion 兩層來源：
    /// <list type="bullet">
    ///   <item><b>每篇 doc 的 frontmatter aliases</b> — 文件作者貼標</item>
    ///   <item><b>中央 _synonyms 檔（選用）</b> — query 端的同義詞展開</item>
    /// </list>
    ///
    /// 參數：
    /// <list type="bullet">
    ///   <item><c>query</c>（必填）：以空白分隔的關鍵字（AND）</item>
    ///   <item><c>roots</c>（選填）：分號分隔資料夾（git-root 相對；預設 <c>Docs;CardGame/Assets/UCL/UCL_Core/Docs~</c>）</item>
    ///   <item><c>limit</c>（選填，預設 <c>20</c>）：top-N 命中</item>
    ///   <item><c>format</c>（選填）：<c>md</c>（預設）/ <c>json</c></item>
    ///   <item><c>excludeDirs</c>（選填）：路徑片段命中即略過</item>
    ///   <item><c>synonymsPath</c>（選填）：同義詞檔（git-root 相對；每行一組 CSV，<c>#</c> 為註解）</item>
    ///   <item><c>outputPath</c>（選填）：輸出檔；不指定則只印 Console</item>
    ///   <item><c>searchMode</c>（選填，預設 <c>and</c>）：<c>and</c> / <c>or</c></item>
    /// </list>
    /// </summary>
    public class Cmd_SearchDocs : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SearchDocs";

        public override string ShortDescription =>
            "Live fuzzy search across all markdown docs (independent of _catalog.md); ranks by frontmatter field weights and supports synonym expansion.";

        public override string ArgsSchema =>
            "query=Search query — terms separated by space (AND semantics by default). REQUIRED.\n" +
            "roots=Semicolon-separated dirs (git-root-relative; default Docs;CardGame/Assets/UCL/UCL_Core/Docs~)\n" +
            "limit=Max hits to return (default 20)\n" +
            "format=md|json (default md)\n" +
            "excludeDirs=Path substrings to skip (default node_modules;.git;_Drafts)\n" +
            "synonymsPath=Optional synonym file (git-root-relative). Format: one CSV group per line, # for comments\n" +
            "outputPath=Output file (git-root-relative; if omitted, prints to Unity Console)\n" +
            "searchMode=and|or (default and)";

        /// <summary>Page「Fill Example」按鈕一鍵填入用 — 搜「物品」展示同義詞展開效果。</summary>
        public override string ExampleArgs =>
            "query=物品;limit=10;format=md;synonymsPath=Docs/_synonyms.txt";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_SearchDocs.md";

        // ===========================================================
        // 主流程：解析 args → live scan → query expansion → score → top-N → 渲染
        // ===========================================================
        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string query = GetArg(args, "query", null);
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("missing args[query]");
            }

            string rootsArg = GetArg(args, "roots", "Docs;CardGame/Assets/UCL/UCL_Core/Docs~");
            int limit = int.TryParse(GetArg(args, "limit", "20"), out var lim) && lim > 0 ? lim : 20;
            string format = (GetArg(args, "format", "md") ?? "md").ToLowerInvariant();
            string excludeArg = GetArg(args, "excludeDirs", "node_modules;.git;_Drafts");
            string synonymsPath = GetArg(args, "synonymsPath", null);
            string outputPath = GetArg(args, "outputPath", null);
            string searchMode = (GetArg(args, "searchMode", "and") ?? "and").ToLowerInvariant();
            bool orMode = searchMode == "or";

            string gitRoot = UCL_DocCatalogScanner.GetGitRoot();
            var roots = UCL_DocCatalogScanner.SplitList(rootsArg);
            var excludes = UCL_DocCatalogScanner.SplitList(excludeArg);

            // 1) live scan — 每次都 cold；catalog 不被讀也不被寫
            var entries = UCL_DocCatalogScanner.ScanRoots(roots, gitRoot, excludes, includeArchived: false, token);

            // 2) 切詞 + 同義詞展開
            var rawTerms = query.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (rawTerms.Count == 0)
            {
                throw new ArgumentException("query is empty after tokenization");
            }
            var synonymGroups = LoadSynonyms(gitRoot, synonymsPath);
            var expandedSets = rawTerms.Select(t => ExpandTerm(t, synonymGroups)).ToList();

            // 3) 計分 — 對每個 entry 算 score
            var hits = new List<ScoredHit>();
            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();
                var (score, matched) = ScoreEntry(e, expandedSets, orMode);
                if (score > 0)
                {
                    hits.Add(new ScoredHit { Entry = e, Score = score, MatchedFields = matched });
                }
            }

            // 4) 排序 → top-N
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (hits.Count > limit) hits = hits.Take(limit).ToList();

            // 5) 渲染 → 輸出
            string content = format == "json"
                ? RenderJson(hits, query, expandedSets, entries.Count)
                : RenderMarkdown(hits, query, expandedSets, entries.Count, orMode);

            if (!string.IsNullOrEmpty(outputPath))
            {
                string absOut = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(gitRoot, outputPath);
                string outDir = Path.GetDirectoryName(absOut);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }
                File.WriteAllText(absOut, content, new UTF8Encoding(false));
                Debug.Log($"[Cmd:SearchDocs] query=\"{query}\" → {hits.Count} hit(s) (scanned {entries.Count}) → {outputPath}");
            }
            else
            {
                Debug.Log($"[Cmd:SearchDocs] query=\"{query}\" → {hits.Count} hit(s) (scanned {entries.Count})\n{content}");
            }

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 計分：對每個 entry 計算分數
        // 物理意義：title 命中最有信心、aliases 是模糊搜尋的主軸、filename 兜底
        // 數值影響：score=0 表不命中；AND 模式下任一 term miss 即 0
        // ===========================================================
        static (int score, List<string> matched) ScoreEntry(
            UCL_DocCatalogEntry e, List<HashSet<string>> termSets, bool orMode)
        {
            int totalScore = 0;
            int termsHit = 0;
            var matchedFields = new HashSet<string>();

            // 各欄位轉小寫一次（搜尋大小寫無視）
            string filename = (Path.GetFileNameWithoutExtension(e.RelativePath) ?? "").ToLowerInvariant();
            string title = (e.Title ?? "").ToLowerInvariant();
            string description = (e.Description ?? "").ToLowerInvariant();
            string tagsConcat = string.Join(",", e.Tags ?? new List<string>()).ToLowerInvariant();
            string aliasesConcat = string.Join(",", e.Aliases ?? new List<string>()).ToLowerInvariant();

            foreach (var termSet in termSets)
            {
                int termScore = 0;
                foreach (var v in termSet)
                {
                    string vlow = v.ToLowerInvariant();
                    if (string.IsNullOrEmpty(vlow)) continue;
                    if (title.Contains(vlow))         { termScore = Math.Max(termScore, 10); matchedFields.Add("title"); }
                    if (aliasesConcat.Contains(vlow)) { termScore = Math.Max(termScore, 8);  matchedFields.Add("aliases"); }
                    if (tagsConcat.Contains(vlow))    { termScore = Math.Max(termScore, 6);  matchedFields.Add("tags"); }
                    if (description.Contains(vlow))   { termScore = Math.Max(termScore, 5);  matchedFields.Add("description"); }
                    if (filename.Contains(vlow))      { termScore = Math.Max(termScore, 4);  matchedFields.Add("filename"); }
                }
                if (termScore > 0)
                {
                    termsHit++;
                    totalScore += termScore;
                }
            }

            // AND 語義：要求所有 term 都命中
            if (!orMode && termsHit < termSets.Count) return (0, null);
            // OR 語義：任一命中即可
            if (orMode && termsHit == 0) return (0, null);

            // bonus：命中 term 數越多分越高
            totalScore += termsHit * 2;
            return (totalScore, matchedFields.OrderBy(s => s).ToList());
        }

        // ===========================================================
        // 同義詞展開
        // 物理意義：query 端的展開（中央詞表），與 doc 端的 aliases（個別文件）互補
        // ===========================================================
        static HashSet<string> ExpandTerm(string term, List<List<string>> synonymGroups)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { term };
            if (synonymGroups == null) return set;
            foreach (var grp in synonymGroups)
            {
                if (grp.Any(g => string.Equals(g, term, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var g in grp) set.Add(g);
                }
            }
            return set;
        }

        // 同義詞檔格式（純文本，避免依賴 YAML lib）：
        //   # 註解行以 # 起始
        //   物品, 道具, item, items, 消耗品
        //   狀態, status, buff, debuff
        // 每非空非註解行 = 一組同義詞集合
        static List<List<string>> LoadSynonyms(string gitRoot, string path)
        {
            var groups = new List<List<string>>();
            if (string.IsNullOrEmpty(path)) return groups;
            string abs = Path.IsPathRooted(path) ? path : Path.Combine(gitRoot, path);
            if (!File.Exists(abs))
            {
                Debug.LogWarning($"[Cmd:SearchDocs] synonyms file not found, skipped: {path}");
                return groups;
            }
            try
            {
                foreach (var line in File.ReadAllLines(abs))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    var parts = l.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    if (parts.Count >= 2) groups.Add(parts);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:SearchDocs] synonyms read failed: {e.Message}");
            }
            return groups;
        }

        class ScoredHit
        {
            public UCL_DocCatalogEntry Entry;
            public int Score;
            public List<string> MatchedFields;
        }

        // ===========================================================
        // Markdown 渲染
        // ===========================================================
        static string RenderMarkdown(List<ScoredHit> hits, string query,
            List<HashSet<string>> termSets, int totalScanned, bool orMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 🔍 SearchDocs — \"{query}\"");
            sb.AppendLine();
            sb.AppendLine($"> Mode: **{(orMode ? "OR" : "AND")}** · Scanned **{totalScanned}** docs · Found **{hits.Count}** hit(s)");
            // 列出展開過的同義詞群（讓使用者知道實際搜了哪些變體）
            if (termSets.Any(s => s.Count > 1))
            {
                sb.AppendLine($">");
                sb.AppendLine($"> Terms expanded via synonyms:");
                foreach (var ts in termSets)
                {
                    if (ts.Count > 1)
                    {
                        sb.AppendLine($">  - `{string.Join(" | ", ts)}`");
                    }
                }
            }
            sb.AppendLine();
            if (hits.Count == 0)
            {
                sb.AppendLine("> No matches.");
                return sb.ToString();
            }
            sb.AppendLine("| # | Score | Path | Title | Matched | Description |");
            sb.AppendLine("|---|---|---|---|---|---|");
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                sb.Append("| ").Append(i + 1);
                sb.Append(" | ").Append(h.Score);
                sb.Append(" | ").Append($"[`{Esc(h.Entry.RelativePath)}`]({h.Entry.RelativePath})");
                sb.Append(" | ").Append(Esc(h.Entry.Title));
                sb.Append(" | ").Append(Esc(string.Join(", ", h.MatchedFields ?? new List<string>())));
                sb.Append(" | ").Append(Esc(h.Entry.Description));
                sb.AppendLine(" |");
            }
            return sb.ToString();
        }

        // ===========================================================
        // JSON 渲染
        // ===========================================================
        static string RenderJson(List<ScoredHit> hits, string query,
            List<HashSet<string>> termSets, int totalScanned)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"command\": \"SearchDocs\",");
            sb.AppendLine($"  \"query\": \"{JsonEsc(query)}\",");
            sb.AppendLine($"  \"scanned\": {totalScanned},");
            sb.AppendLine($"  \"hitCount\": {hits.Count},");
            sb.AppendLine("  \"expandedTerms\": [");
            for (int i = 0; i < termSets.Count; i++)
            {
                sb.Append("    [");
                sb.Append(string.Join(",", termSets[i].Select(t => "\"" + JsonEsc(t) + "\"")));
                sb.Append("]");
                if (i < termSets.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"hits\": [");
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                sb.Append("    {");
                sb.Append($"\"rank\":{i + 1},");
                sb.Append($"\"score\":{h.Score},");
                sb.Append($"\"path\":\"{JsonEsc(h.Entry.RelativePath)}\",");
                sb.Append($"\"title\":\"{JsonEsc(h.Entry.Title)}\",");
                sb.Append($"\"description\":\"{JsonEsc(h.Entry.Description)}\",");
                sb.Append("\"matched_fields\":[");
                sb.Append(string.Join(",", (h.MatchedFields ?? new List<string>()).Select(f => "\"" + JsonEsc(f) + "\"")));
                sb.Append("],");
                sb.Append($"\"tags\":[{string.Join(",", h.Entry.Tags.Select(t => "\"" + JsonEsc(t) + "\""))}],");
                sb.Append($"\"aliases\":[{string.Join(",", h.Entry.Aliases.Select(t => "\"" + JsonEsc(t) + "\""))}]");
                sb.Append("}");
                if (i < hits.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
        }

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
#endif
