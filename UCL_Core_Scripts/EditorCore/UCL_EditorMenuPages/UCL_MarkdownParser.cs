// 區塊職責：純邏輯 markdown parser — 把 raw .md 字串切成結構化的 block list（含 frontmatter / heading /
//          paragraph / code fence / bullet / quote / hr / empty / table / mermaid）。
// 物理意義：不依賴 IMGUI / UnityEditor，純字串處理；上層（UCL_MarkdownViewerPage 或其他 viewer / exporter）
//          只需把 string 餵進來、拿到 List<UCL_MdBlock> 自行渲染或轉換。
// 數值影響：line-based 掃描；單檔通常 < 1ms（除非包含大型 mermaid 區塊）。
//          parser 與 viewer 解耦：未來可重用做 markdown → HTML / TextMesh / Console 等其他輸出。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;   // Render 端 Mathf.Clamp 用

namespace UCL.Core.EditorLib.Page
{
    // ===========================================================
    // 資料模型
    // ===========================================================

    /// <summary>
    /// markdown block 的種類。對應 <see cref="UCL_MdBlock"/> 的 <c>Type</c>，渲染端用 switch 分流。
    /// </summary>
    public enum UCL_MdBlockType
    {
        Heading,
        Paragraph,
        CodeFence,
        Bullet,
        Quote,
        HorizontalRule,
        Empty,
        Table,
        Mermaid,
    }

    /// <summary>
    /// 切出來的單一 markdown block。各欄位的有意義性看 <see cref="Type"/>：
    /// Heading 用 <see cref="HeadingLevel"/> + <see cref="Text"/>；
    /// Paragraph / Bullet / Quote 用 <see cref="Text"/>；
    /// CodeFence / Mermaid 用 <see cref="CodeLang"/> + <see cref="CodeBody"/>（Mermaid 額外有 <see cref="Graph"/>）；
    /// Table 用 <see cref="TableRows"/>；其他 type 全欄位皆可空。
    /// </summary>
    public class UCL_MdBlock
    {
        public UCL_MdBlockType Type;
        public int HeadingLevel;            // Heading 用：1~6
        public string Text;                 // Heading / Paragraph / Bullet / Quote 用
        public string CodeLang;             // CodeFence 用：```lang
        public string CodeBody;             // CodeFence 用；Mermaid 也保留原始字串供 fallback / Copy
        public List<string[]> TableRows;    // Table 用：第 0 列為 header；分隔列（|---|---|）已剃除
        public UCL_MermaidGraph Graph;      // Mermaid 用：parse 後的節點 / 邊資料
    }

    /// <summary>單一 mermaid 節點（id + label + 三種 shape 之一）</summary>
    public class UCL_MermaidNode
    {
        public string Id;
        public string Label;
        public string Shape;                // "rect" / "round" / "diamond"；未指定 → "rect"
    }

    /// <summary>單一 mermaid 邊；<see cref="Label"/> 為 <c>--&gt;|x|</c> 中的 x，可空</summary>
    public class UCL_MermaidEdge
    {
        public string From, To, Label;
    }

    /// <summary>parse 後的 mermaid 圖：方向 + 節點字典 + 邊清單</summary>
    public class UCL_MermaidGraph
    {
        public string Direction = "LR";
        public Dictionary<string, UCL_MermaidNode> Nodes = new Dictionary<string, UCL_MermaidNode>();
        public List<UCL_MermaidEdge> Edges = new List<UCL_MermaidEdge>();
    }

    /// <summary>
    /// <see cref="UCL_MarkdownParser.Parse"/> 的輸出 — 拆分後的 frontmatter（YAML 字串、不解析）+ block list。
    /// </summary>
    public class UCL_MarkdownDocument
    {
        /// <summary>frontmatter 區塊原文（不含包夾的 ---），無則為 null</summary>
        public string Frontmatter;
        public List<UCL_MdBlock> Blocks = new List<UCL_MdBlock>();
    }

    // ===========================================================
    // Parser 主體
    // ===========================================================

    /// <summary>
    /// 純函式 markdown 解析器。輸入 raw 字串、輸出 <see cref="UCL_MarkdownDocument"/>。
    /// 不依賴 IMGUI / UnityEditor / 檔案系統，可單元測試也可被任意 viewer / exporter 重用。
    /// </summary>
    /// <remarks>
    /// 支援：YAML frontmatter（包夾 <c>---</c>）/ ATX heading / 段落 / code fence（含 mermaid 分流）/
    /// bullet（- * + 與 1.）/ blockquote（&gt; ）/ horizontal rule（--- *** ___）/ 標準 GFM 表格 /
    /// mermaid（graph / flowchart 方向 + 三種 shape + --&gt;|label|）。
    /// 不支援：setext heading（=== / ---）、巢狀清單、HTML 區塊、定義清單、腳註、複雜 mermaid 語法等。
    /// </remarks>
    public static class UCL_MarkdownParser
    {
        public static UCL_MarkdownDocument Parse(string content)
        {
            var doc = new UCL_MarkdownDocument();
            if (string.IsNullOrEmpty(content)) return doc;

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int idx = 0;

            // frontmatter：必須從第一行 "---" 起、到下個 "---" 為止
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                var fm = new StringBuilder();
                int j = 1;
                bool closed = false;
                for (; j < lines.Length; j++)
                {
                    if (lines[j].Trim() == "---") { closed = true; j++; break; }
                    fm.AppendLine(lines[j]);
                }
                if (closed)
                {
                    doc.Frontmatter = fm.ToString().TrimEnd();
                    idx = j;
                }
            }

            var blocks = doc.Blocks;
            var paraBuf = new StringBuilder();

            for (int i = idx; i < lines.Length; i++)
            {
                string line = lines[i];

                // code fence：累積到下一個 ``` 為止；中間任何語法都不解析
                // mermaid 例外：lang == "mermaid" 改 parse 成 UCL_MermaidGraph
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```"))
                {
                    FlushParagraph(paraBuf, blocks);
                    string lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                    var body = new StringBuilder();
                    i++;
                    for (; i < lines.Length; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("```")) break;
                        body.Append(lines[i]).Append('\n');
                    }
                    string codeText = body.ToString().TrimEnd('\n');
                    if (string.Equals(lang, "mermaid", StringComparison.OrdinalIgnoreCase))
                    {
                        blocks.Add(new UCL_MdBlock
                        {
                            Type = UCL_MdBlockType.Mermaid,
                            CodeLang = lang,
                            CodeBody = codeText,
                            Graph = ParseMermaid(codeText),
                        });
                    }
                    else
                    {
                        blocks.Add(new UCL_MdBlock
                        {
                            Type = UCL_MdBlockType.CodeFence,
                            CodeLang = lang,
                            CodeBody = codeText,
                        });
                    }
                    continue;
                }

                // heading
                if (TryParseHeading(line, out int lv, out string ht))
                {
                    FlushParagraph(paraBuf, blocks);
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.Heading, HeadingLevel = lv, Text = ht });
                    continue;
                }

                // horizontal rule（---、***、___ 各 ≥3 個）
                if (Regex.IsMatch(line, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$"))
                {
                    FlushParagraph(paraBuf, blocks);
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.HorizontalRule });
                    continue;
                }

                // empty line：作為 paragraph 邊界 + 視覺空白
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(paraBuf, blocks);
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.Empty });
                    continue;
                }

                // table：偵測 `|...|` 開頭結尾的 row + 緊接著的 `|---|---|` 分隔列
                if (IsTableLine(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    FlushParagraph(paraBuf, blocks);
                    var rows = new List<string[]> { SplitTableRow(line) };
                    i += 2; // 跳過 header + separator
                    while (i < lines.Length && IsTableLine(lines[i]))
                    {
                        rows.Add(SplitTableRow(lines[i]));
                        i++;
                    }
                    i--; // 抵銷 outer for 的 i++（讓非表格行下一輪重新處理）
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.Table, TableRows = rows });
                    continue;
                }

                // bullet（- / * / + / 1.）
                var bulletMatch = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s+(.*)$");
                if (bulletMatch.Success)
                {
                    FlushParagraph(paraBuf, blocks);
                    string indent = bulletMatch.Groups[1].Value;
                    string marker = bulletMatch.Groups[2].Value;
                    string rest = bulletMatch.Groups[3].Value;
                    // 視覺上 - / * / + 一律換成 •；數字保留原樣
                    string display = marker.Length == 1 ? "•" : marker;
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.Bullet, Text = indent + display + " " + rest });
                    continue;
                }

                // blockquote
                if (trimmed.StartsWith("> "))
                {
                    FlushParagraph(paraBuf, blocks);
                    blocks.Add(new UCL_MdBlock { Type = UCL_MdBlockType.Quote, Text = trimmed.Substring(2) });
                    continue;
                }

                // 一般段落：累積直到下一個非段落 block 或空行
                paraBuf.Append(line).Append('\n');
            }
            FlushParagraph(paraBuf, blocks);
            return doc;
        }

        // ===========================================================
        // helpers — block 級
        // ===========================================================

        static void FlushParagraph(StringBuilder buf, List<UCL_MdBlock> blocks)
        {
            if (buf.Length == 0) return;
            blocks.Add(new UCL_MdBlock
            {
                Type = UCL_MdBlockType.Paragraph,
                Text = buf.ToString().TrimEnd('\n', ' ', '\t'),
            });
            buf.Clear();
        }

        // ATX heading：開頭 1~6 個 #，後接單一空白，剩下視為 heading 文字
        static bool TryParseHeading(string line, out int level, out string text)
        {
            level = 0; text = null;
            if (string.IsNullOrEmpty(line)) return false;
            int n = 0;
            while (n < line.Length && line[n] == '#') n++;
            if (n == 0 || n > 6) return false;
            if (n >= line.Length || line[n] != ' ') return false;
            level = n;
            text = line.Substring(n + 1).Trim();
            return true;
        }

        // 區塊職責：判斷 line 是否為表格資料列（首尾皆 `|` 且至少含一個內部 `|`）
        // 物理意義：標準 markdown 表格列必有兩端 `|`；只有一端會誤判 inline 引文
        static bool IsTableLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            if (t.Length < 3) return false;
            if (!t.StartsWith("|") || !t.EndsWith("|")) return false;
            // 至少要有兩個 `|`（首尾）+ 中間至少 1 個內部 `|`
            int count = 0;
            foreach (char c in t) if (c == '|') count++;
            return count >= 3;
        }

        // 區塊職責：判斷 line 是否為表格分隔列（|---|---|；可帶 :- / -: / :-: 對齊符號 + 空白）
        static bool IsTableSeparator(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            if (t.Length < 3 || !t.StartsWith("|") || !t.EndsWith("|")) return false;
            string inner = t.Substring(1, t.Length - 2);
            bool hasDash = false;
            foreach (char c in inner)
            {
                if (c == '-') hasDash = true;
                else if (c != '|' && c != ':' && c != ' ') return false;
            }
            return hasDash;
        }

        // 把 `| a | b | c |` 切成 ["a", "b", "c"] 並 trim
        static string[] SplitTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            var cells = t.Split('|');
            for (int k = 0; k < cells.Length; k++) cells[k] = cells[k].Trim();
            return cells;
        }

        // ===========================================================
        // mermaid parser（簡化版）
        // 物理意義：line-based 掃描；每行可能是 `graph LR` 方向宣告 / 註解（%%）/ 邊
        //          邊的形式為 `LeftRef --> [|EdgeLabel|] RightRef`，Ref 可帶 shape：
        //          A / A[Label] / A(Label) / A{Label}
        // 數值影響：v1 不支援其他箭頭語法（---、==>、-.->）、subgraph、class style、
        //          多重邊（A --> B & C）；遇到看不懂的行直接忽略
        // ===========================================================
        static readonly Regex s_RxMermaidDir = new Regex(@"^(?:graph|flowchart)\s+(\w+)", RegexOptions.Compiled);
        static readonly Regex s_RxMermaidEdge = new Regex(
            @"(\w+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?" +   // 左節點 + 可選 shape
            @"\s*-->\s*" +
            @"(?:\|([^|]+)\|\s*)?" +                              // 可選邊 label
            @"(\w+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?",    // 右節點 + 可選 shape
            RegexOptions.Compiled);

        public static UCL_MermaidGraph ParseMermaid(string body)
        {
            var g = new UCL_MermaidGraph();
            if (string.IsNullOrEmpty(body)) return g;
            var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("%%")) continue;

                var dirM = s_RxMermaidDir.Match(line);
                if (dirM.Success) { g.Direction = dirM.Groups[1].Value; continue; }

                var edgeM = s_RxMermaidEdge.Match(line);
                while (edgeM.Success)
                {
                    string fromId = edgeM.Groups[1].Value;
                    string fromLabel = null, fromShape = null;
                    if (edgeM.Groups[2].Success) { fromShape = "rect";    fromLabel = edgeM.Groups[2].Value; }
                    else if (edgeM.Groups[3].Success) { fromShape = "round";   fromLabel = edgeM.Groups[3].Value; }
                    else if (edgeM.Groups[4].Success) { fromShape = "diamond"; fromLabel = edgeM.Groups[4].Value; }

                    string edgeLabel = edgeM.Groups[5].Success ? edgeM.Groups[5].Value.Trim() : null;

                    string toId = edgeM.Groups[6].Value;
                    string toLabel = null, toShape = null;
                    if (edgeM.Groups[7].Success) { toShape = "rect";    toLabel = edgeM.Groups[7].Value; }
                    else if (edgeM.Groups[8].Success) { toShape = "round";   toLabel = edgeM.Groups[8].Value; }
                    else if (edgeM.Groups[9].Success) { toShape = "diamond"; toLabel = edgeM.Groups[9].Value; }

                    EnsureMermaidNode(g, fromId, fromLabel, fromShape);
                    EnsureMermaidNode(g, toId, toLabel, toShape);
                    g.Edges.Add(new UCL_MermaidEdge { From = fromId, To = toId, Label = edgeLabel });

                    edgeM = edgeM.NextMatch();
                }
            }
            return g;
        }

        // 註冊或補完節點：已存在節點但缺 label/shape 時補上；已有的不覆寫
        // 物理意義：避免後出現的 bare ref（`B --> C`）把先前定義過的 label/shape 洗掉
        static void EnsureMermaidNode(UCL_MermaidGraph g, string id, string label, string shape)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!g.Nodes.TryGetValue(id, out var n))
            {
                n = new UCL_MermaidNode { Id = id, Label = label ?? id, Shape = shape ?? "rect" };
                g.Nodes[id] = n;
            }
            else
            {
                if (!string.IsNullOrEmpty(label)) n.Label = label;
                if (!string.IsNullOrEmpty(shape)) n.Shape = shape;
            }
        }

        // ===========================================================
        // 反向輸出：UCL_MarkdownDocument → markdown 字串
        // 物理意義：與 Parse 形成 round-trip；呼叫端可以以結構化方式建立文件
        //          （e.g. RCG_StoryData export）然後呼叫 Render 生成 .md 寫入磁碟。
        // 數值影響：純字串輸出。bullet 一律規範化為 `-` 開頭（原本 parser 把 -/*/+ 一律換成 •，
        //          這裡再規範化回 `-` 讓輸出能被任何 markdown 渲染器接受 + 重新 parse）。
        // ===========================================================

        /// <summary>
        /// 把 <see cref="UCL_MarkdownDocument"/> 反向輸出為 markdown 字串。
        /// 與 <see cref="Parse"/> 形成 round-trip（重 parse 後 block 結構一致；marker 字符可能正規化）。
        /// </summary>
        public static string Render(UCL_MarkdownDocument doc)
        {
            if (doc == null) return "";
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(doc.Frontmatter))
            {
                sb.Append("---\n");
                sb.Append(doc.Frontmatter);
                if (!doc.Frontmatter.EndsWith("\n")) sb.Append('\n');
                sb.Append("---\n\n");
            }
            if (doc.Blocks != null)
            {
                foreach (var b in doc.Blocks)
                {
                    RenderBlock(sb, b);
                }
            }
            return sb.ToString();
        }

        static void RenderBlock(StringBuilder sb, UCL_MdBlock b)
        {
            if (b == null) return;
            switch (b.Type)
            {
                case UCL_MdBlockType.Heading:
                    int lv = Mathf.Clamp(b.HeadingLevel, 1, 6);
                    for (int i = 0; i < lv; i++) sb.Append('#');
                    sb.Append(' ').Append(b.Text ?? "").Append('\n');
                    break;
                case UCL_MdBlockType.Paragraph:
                    sb.Append(b.Text ?? "").Append('\n');
                    break;
                case UCL_MdBlockType.Bullet:
                    // parser 把所有 -/*/+ 統一成 •，這裡再規範化回 `-` 確保產出的 markdown
                    // 在任何渲染器（GitHub / Obsidian / 我們自己）都能被認回來
                    sb.Append(NormalizeBulletForRender(b.Text ?? "")).Append('\n');
                    break;
                case UCL_MdBlockType.Quote:
                    sb.Append("> ").Append(b.Text ?? "").Append('\n');
                    break;
                case UCL_MdBlockType.HorizontalRule:
                    sb.Append("---\n");
                    break;
                case UCL_MdBlockType.Empty:
                    sb.Append('\n');
                    break;
                case UCL_MdBlockType.CodeFence:
                    sb.Append("```").Append(b.CodeLang ?? "").Append('\n');
                    if (!string.IsNullOrEmpty(b.CodeBody))
                    {
                        sb.Append(b.CodeBody);
                        if (!b.CodeBody.EndsWith("\n")) sb.Append('\n');
                    }
                    sb.Append("```\n");
                    break;
                case UCL_MdBlockType.Mermaid:
                    sb.Append("```mermaid\n");
                    if (b.Graph != null && (b.Graph.Nodes.Count > 0 || b.Graph.Edges.Count > 0))
                    {
                        sb.Append(RenderMermaid(b.Graph));
                    }
                    else if (!string.IsNullOrEmpty(b.CodeBody))
                    {
                        sb.Append(b.CodeBody);
                        if (!b.CodeBody.EndsWith("\n")) sb.Append('\n');
                    }
                    sb.Append("```\n");
                    break;
                case UCL_MdBlockType.Table:
                    RenderTable(sb, b.TableRows);
                    break;
            }
        }

        // bullet 一律換頭：parse 階段把 `-` `*` `+` 都正規化成 `•`，這裡反向換回 `-`
        // 數值影響：indent 與 rest 保留；只動第一個 marker glyph
        static string NormalizeBulletForRender(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int idx = text.IndexOf('•');
            if (idx >= 0) return text.Substring(0, idx) + "-" + text.Substring(idx + 1);
            return text;
        }

        static void RenderTable(StringBuilder sb, List<string[]> rows)
        {
            if (rows == null || rows.Count == 0) return;
            int cols = rows[0].Length;
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append('|');
                for (int c = 0; c < cols; c++)
                {
                    string cell = c < rows[r].Length ? rows[r][c] : "";
                    sb.Append(' ').Append(EscapeTableCell(cell)).Append(" |");
                }
                sb.Append('\n');
                if (r == 0)
                {
                    sb.Append('|');
                    for (int c = 0; c < cols; c++) sb.Append("---|");
                    sb.Append('\n');
                }
            }
        }

        static string EscapeTableCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // pipe 會破壞 row 邊界、換行會把 cell 拆成多行 — 都要 escape
            return s.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");
        }

        /// <summary>
        /// 把 <see cref="UCL_MermaidGraph"/> 反向輸出為 mermaid 程式碼（不含外圍 ``` 圍欄）。
        /// 邊先寫，已渲染過的節點下次只用 bare ref；最後補不出現在任何邊裡的孤立節點。
        /// </summary>
        public static string RenderMermaid(UCL_MermaidGraph g)
        {
            if (g == null) return "";
            var sb = new StringBuilder();
            sb.Append("graph ").Append(string.IsNullOrEmpty(g.Direction) ? "LR" : g.Direction).Append('\n');

            var rendered = new HashSet<string>();
            if (g.Edges != null)
            {
                foreach (var e in g.Edges)
                {
                    if (e == null || string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To)) continue;
                    sb.Append("    ");
                    sb.Append(RenderMermaidNodeRef(g, e.From, rendered));
                    sb.Append(" -->");
                    if (!string.IsNullOrEmpty(e.Label))
                    {
                        // edge label 比 node label 寬鬆 — 在 |...| 之間只有 `|` 與換行會破語法
                        sb.Append('|').Append(SanitizeMermaidEdgeLabel(e.Label)).Append('|');
                    }
                    sb.Append(' ');
                    sb.Append(RenderMermaidNodeRef(g, e.To, rendered));
                    sb.Append('\n');
                }
            }
            // 孤立節點（沒出現在任何邊裡）— 至少保留它在輸出裡
            if (g.Nodes != null)
            {
                foreach (var kv in g.Nodes)
                {
                    if (!rendered.Contains(kv.Key))
                    {
                        sb.Append("    ").Append(RenderMermaidNodeRef(g, kv.Key, rendered)).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        // 第一次提到節點時帶完整 shape + label；之後同 id 直接用 bare ref（避免重複定義 label）
        static string RenderMermaidNodeRef(UCL_MermaidGraph g, string id, HashSet<string> rendered)
        {
            if (string.IsNullOrEmpty(id)) return "_unknown";
            if (rendered.Contains(id)) return id;
            rendered.Add(id);
            if (g.Nodes == null || !g.Nodes.TryGetValue(id, out var n)) return id;
            // label 與 id 相同（或無 label）→ 不需要 shape brackets，只輸出 id
            if (string.IsNullOrEmpty(n.Label) || n.Label == id) return id;
            string lbl = SanitizeMermaidLabel(n.Label);
            switch (n.Shape)
            {
                case "round":   return id + "(" + lbl + ")";
                case "diamond": return id + "{" + lbl + "}";
                default:        return id + "[" + lbl + "]";
            }
        }

        // 把可能破壞 mermaid 節點 shape 的字元（[](){}|"和換行）換成空白或 `-`
        // 物理意義：節點 label 在 [...] / (...) / {...} 圍欄之間，這些字元會提早終止 shape
        static string SanitizeMermaidLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\n': case '\r': sb.Append(' '); break;
                    case '[': case ']':
                    case '(': case ')':
                    case '{': case '}':
                    case '|': case '"':
                        sb.Append('-');
                        break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // edge label 的 sanitizer — 比 node label 寬鬆
        // 物理意義：在 `|...|` 之間，只有 `|` 與換行 / `"` 會真的破語法；()/[]/{} 都合法
        // 數值影響：保留 `(70%)` 這類常見標註不被吃掉
        static string SanitizeMermaidEdgeLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\n': case '\r': sb.Append(' '); break;
                    case '|': sb.Append('/'); break;
                    case '"': sb.Append('\''); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
