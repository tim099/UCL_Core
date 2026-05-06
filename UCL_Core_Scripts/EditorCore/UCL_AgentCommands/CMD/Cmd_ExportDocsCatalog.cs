
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：本檔提供「掃描所有 Markdown 文件並彙整 frontmatter 成單一索引檔」的 Agent Command。
// 物理意義：給定 roots（資料夾清單，git-root-relative）→ 遞迴搜尋 *.md → 解析 YAML
//          frontmatter（title / description / tags / aliases / target_audience / last_updated /
//          archived）→ 將 (path, title, description, tags, aliases) 整合成一張可 Ctrl+F
//          搜尋的 Markdown 表格（或 JSON）。aliases 欄位是「模糊搜尋」的關鍵 — 例如
//          一份名為「道具系統」的文件在 frontmatter 加上 `aliases: [物品, item]`，
//          之後不論 agent 搜「物品」「item」都能在 catalog 表裡命中該行。
// 數值影響：純讀取 .md 檔；只在指定 outputPath 寫入一份索引檔，不修改原 docs。
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
    /// Agent Command：掃描 markdown roots，把每份 .md 的 frontmatter 整合成單一索引檔。
    ///
    /// 設計重點：解決「專案 docs 太多、不知從何找起」的問題。catalog 行內含
    /// path / title / description / tags / aliases / target_audience / last_updated；
    /// agent / 人類在 IDE 內 Ctrl+F 一次掃過 200 餘篇文件就能定位到候選清單。
    ///
    /// 模糊搜尋（同義詞）：透過每份文件 frontmatter 的 <c>aliases:</c> 欄位實現 —
    /// 文件作者把可能的搜尋詞（例：物品 / item / 道具）寫進 aliases，catalog 行就會
    /// 同時含這些詞，於是 grep 任一變體都能命中。<b>不依賴 embedding</b>，僅靠 keyword + alias。
    /// 後續若要更語意化的搜尋，可疊加 <c>Cmd_SearchDocs</c> + 同義詞表（_synonyms.yaml）。
    ///
    /// 參數：
    /// <list type="bullet">
    ///   <item><c>roots</c>（選填）：分號分隔的資料夾清單（git-root 相對；預設
    ///         <c>Docs;CardGame/Assets/UCL/UCL_Core/Docs~</c>）</item>
    ///   <item><c>outputPath</c>（選填）：輸出路徑（git-root 相對；預設
    ///         <c>Docs/_catalog.md</c>）</item>
    ///   <item><c>format</c>（選填）：<c>md</c>（預設）或 <c>json</c></item>
    ///   <item><c>excludeDirs</c>（選填）：分號分隔的路徑片段，命中即略過（預設
    ///         <c>node_modules;.git;_Drafts</c>）</item>
    ///   <item><c>includeArchived</c>（選填，預設 <c>false</c>）：是否列出
    ///         frontmatter 含 <c>archived: true</c> 的文件</item>
    /// </list>
    /// </summary>
    public class Cmd_ExportDocsCatalog : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ExportDocsCatalog";

        public override string ShortDescription =>
            "Scan markdown roots and emit a single fuzzy-searchable catalog (path/title/description/tags/aliases) — solves 'too many docs to grep blindly'.";

        public override string ArgsSchema =>
            "roots=Semicolon-separated dirs to scan (git-root-relative; default Docs;CardGame/Assets/UCL/UCL_Core/Docs~)\n" +
            "outputPath=Output path (git-root-relative; default Docs/_catalog.md)\n" +
            "format=md|json (default md)\n" +
            "excludeDirs=Semicolon-separated path substrings to skip (default node_modules;.git;_Drafts)\n" +
            "includeArchived=true|false (default false) — also list docs whose frontmatter has archived: true";

        /// <summary>Page「Fill Example」按鈕一鍵填入用 — EOV 預設掃 Docs/ 與 UCL_Core 的 Docs~/。</summary>
        public override string ExampleArgs =>
            "roots=Docs;CardGame/Assets/UCL/UCL_Core/Docs~;outputPath=Docs/_catalog.md;format=md";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md";

        // ===========================================================
        // 主流程：解析 args → 列舉 *.md → 解析 frontmatter → 寫檔
        // 物理意義：catalog 是「靜態快照」— 每次跑這支 Cmd 就重新生成
        // 數值影響：只在 outputPath 建立 / 覆寫單一索引檔
        // ===========================================================
        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 1) 解析 args
            string rootsArg = GetArg(args, "roots", "Docs;CardGame/Assets/UCL/UCL_Core/Docs~");
            string outputPath = GetArg(args, "outputPath", "Docs/_catalog.md");
            string format = (GetArg(args, "format", "md") ?? "md").ToLowerInvariant();
            string excludeArg = GetArg(args, "excludeDirs", "node_modules;.git;_Drafts");
            bool includeArchived = string.Equals(
                GetArg(args, "includeArchived", "false"), "true", StringComparison.OrdinalIgnoreCase);

            string gitRoot = UCL_DocCatalogScanner.GetGitRoot();

            var roots = UCL_DocCatalogScanner.SplitList(rootsArg);
            var excludes = UCL_DocCatalogScanner.SplitList(excludeArg);

            // 2) 委由共用 scanner 完成「遞迴掃 *.md + 解 frontmatter + 排序」
            //    (與 Cmd_SearchDocs 共用同一份解析邏輯，避免重複實作)
            var entries = UCL_DocCatalogScanner.ScanRoots(roots, gitRoot, excludes, includeArchived, token);

            // 3) 寫檔
            string absOut = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(gitRoot, outputPath);
            string outDir = Path.GetDirectoryName(absOut);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string content = format == "json"
                ? RenderJson(entries, roots)
                : RenderMarkdown(entries, roots, outputPath);
            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            Debug.Log($"[Cmd:ExportDocsCatalog] Indexed {entries.Count} doc(s) from " +
                      $"{roots.Count} root(s) → {outputPath} (format={format})");

            await UniTask.CompletedTask;
        }

        // 區塊備註：DocEntry / ParseDoc / StripQuotes / ParseInlineList / SplitList / MakeRelative
        //          已抽出至 UCL_DocCatalogScanner（同 namespace），由本 Cmd 與 Cmd_SearchDocs 共用。

        // ===========================================================
        // Markdown 渲染
        // 物理意義：catalog 主檔，人類 Ctrl+F + agent grep 都用此檔
        // ===========================================================
        static string RenderMarkdown(List<UCL_DocCatalogEntry> entries, List<string> roots, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("title: 文件總目錄 (Auto-Generated Docs Catalog)");
            sb.AppendLine("description: 由 Cmd_ExportDocsCatalog 掃整個 Docs/ 與 UCL_Core/Docs~/ 的 frontmatter 整合成單一索引；用 Ctrl+F 在 Aliases / Title 欄位做模糊搜尋（同義詞透過每篇文件的 frontmatter aliases 提供）");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"total_docs: {entries.Count}");
            sb.AppendLine($"scan_roots: [{string.Join(", ", roots)}]");
            sb.AppendLine("source: Cmd_ExportDocsCatalog");
            sb.AppendLine("aliases: [文件目錄, docs catalog, 文件索引, document index]");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# 📚 文件總目錄 (Auto-Generated)");
            sb.AppendLine();
            sb.AppendLine($"> **共 {entries.Count} 篇 Markdown** — 此檔自動由 [`Cmd_ExportDocsCatalog`](../CardGame/Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs) 產生，**請勿手改**。");
            sb.AppendLine($">");
            sb.AppendLine($"> **用法**：在 IDE 內 `Ctrl+F` 搜尋你想查的關鍵字 — 標題 / 描述 / Tags / **Aliases** 欄位都會被一起命中。要讓「物品」也命中「道具系統」，請在該 doc frontmatter 加 `aliases: [物品, item]` 後重跑此 Cmd。");
            sb.AppendLine($">");
            sb.AppendLine($"> 詳細模糊搜尋規範見 [DocsCatalog_Workflow](Workflows/DocsCatalog_Workflow.md)。");
            sb.AppendLine();
            sb.AppendLine($"> Scan roots: {string.Join(" / ", roots.Select(r => "`" + r + "`"))}");
            sb.AppendLine();

            // 依 top-dir 分群（讓人類更易瀏覽）
            var byGroup = entries
                .GroupBy(e => GetTopGroup(e.RelativePath))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in byGroup)
            {
                sb.AppendLine($"## `{grp.Key}` ({grp.Count()})");
                sb.AppendLine();
                sb.AppendLine("| Path | Title | Description | Tags | Aliases | Audience | Updated |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var e in grp)
                {
                    string pathLink = MakeMarkdownLink(outputPath, e.RelativePath);
                    sb.Append("| ").Append(pathLink);
                    sb.Append(" | ").Append(Esc(e.Title));
                    sb.Append(" | ").Append(Esc(e.Description));
                    sb.Append(" | ").Append(Esc(string.Join(", ", e.Tags)));
                    sb.Append(" | ").Append(Esc(string.Join(", ", e.Aliases)));
                    sb.Append(" | ").Append(Esc(string.Join(", ", e.TargetAudience)));
                    sb.Append(" | ").Append(Esc(e.LastUpdated));
                    sb.AppendLine(" |");
                }
                sb.AppendLine();
            }

            // 統計尾部
            int withFm = entries.Count(e => e.HasFrontmatter);
            int withAliases = entries.Count(e => e.Aliases != null && e.Aliases.Count > 0);
            int withTags = entries.Count(e => e.Tags != null && e.Tags.Count > 0);
            sb.AppendLine("## 統計");
            sb.AppendLine();
            sb.AppendLine($"- 總文件數：**{entries.Count}**");
            sb.AppendLine($"- 含 frontmatter：{withFm} / {entries.Count}");
            sb.AppendLine($"- 含 `tags`：{withTags}");
            sb.AppendLine($"- 含 `aliases`（模糊搜尋友好）：{withAliases}");
            sb.AppendLine();
            sb.AppendLine("> **建議**：把搜尋率高的常用同義詞補進文件 frontmatter 的 `aliases:` 欄位，每跑一次本 Cmd 都會即時反映進 catalog。");
            return sb.ToString();
        }

        // 取得文件「分組鍵」：跨 root 的文件用第一/第二層作為群名
        // 例：Docs/Architecture/Battle.md → "Docs/Architecture"
        //     Docs/INDEX.md → "Docs"
        //     CardGame/Assets/UCL/UCL_Core/Docs~/zh-Hant/Workflows/X.md → "CardGame/Assets/UCL/UCL_Core/Docs~/zh-Hant"
        static string GetTopGroup(string relPath)
        {
            var parts = relPath.Replace('\\', '/').Split('/');
            // 取倒數第二層（檔名前一段）作為分群鍵 — 多數 Architecture / Workflows / API 子資料夾就是一群
            if (parts.Length >= 2)
            {
                return string.Join("/", parts.Take(parts.Length - 1));
            }
            return ".";
        }

        // 從 catalog 檔案位置算到 target 文件的相對路徑（給 Markdown link 用）
        static string MakeMarkdownLink(string catalogRel, string targetRel)
        {
            string fromDir = Path.GetDirectoryName(catalogRel ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(fromDir)) fromDir = ".";
            string href = MakeRelativeLink(fromDir, targetRel.Replace('\\', '/'));
            return $"[`{Esc(targetRel)}`]({href})";
        }

        static string MakeRelativeLink(string fromDir, string toPath)
        {
            var fromParts = (fromDir == "." ? Array.Empty<string>() : fromDir.Split('/'));
            var toParts = toPath.Split('/');
            int common = 0;
            while (common < fromParts.Length && common < toParts.Length &&
                   string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
            {
                common++;
            }
            var sb = new StringBuilder();
            for (int i = common; i < fromParts.Length; i++) sb.Append("../");
            for (int i = common; i < toParts.Length; i++)
            {
                sb.Append(toParts[i]);
                if (i < toParts.Length - 1) sb.Append('/');
            }
            return sb.Length == 0 ? "." : sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
        }

        // ===========================================================
        // JSON 渲染（給程式用）
        // ===========================================================
        static string RenderJson(List<UCL_DocCatalogEntry> entries, List<string> roots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"command\": \"ExportDocsCatalog\",");
            sb.AppendLine($"  \"generated\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"scan_roots\": [{string.Join(", ", roots.Select(r => "\"" + JsonEsc(r) + "\""))}],");
            sb.AppendLine($"  \"total\": {entries.Count},");
            sb.AppendLine("  \"docs\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("    {");
                sb.Append($"\"path\":\"{JsonEsc(e.RelativePath)}\",");
                sb.Append($"\"title\":\"{JsonEsc(e.Title)}\",");
                sb.Append($"\"description\":\"{JsonEsc(e.Description)}\",");
                sb.Append($"\"tags\":[{string.Join(",", e.Tags.Select(t => "\"" + JsonEsc(t) + "\""))}],");
                sb.Append($"\"aliases\":[{string.Join(",", e.Aliases.Select(t => "\"" + JsonEsc(t) + "\""))}],");
                sb.Append($"\"target_audience\":[{string.Join(",", e.TargetAudience.Select(t => "\"" + JsonEsc(t) + "\""))}],");
                sb.Append($"\"last_updated\":\"{JsonEsc(e.LastUpdated)}\",");
                sb.Append($"\"has_frontmatter\":{(e.HasFrontmatter ? "true" : "false")}");
                sb.Append("}");
                if (i < entries.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        // GetGitRoot 已搬移到 UCL_DocCatalogScanner — 本檔以 UCL_DocCatalogScanner.GetGitRoot() 取代
    }
}
#endif
