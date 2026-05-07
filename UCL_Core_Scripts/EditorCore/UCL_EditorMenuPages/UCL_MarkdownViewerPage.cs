// 區塊職責：在 Editor 內以 IMGUI 直接渲染 markdown 文件（**只負責渲染**），免切到 OS 預設 viewer
//          被 UCL_DocSearchPage 的「📄 預覽」按鈕呼叫；TopBar 仍保留 Reveal / OS Open 入口。
// 物理意義：解析交給 UCL_MarkdownParser（同 namespace，純邏輯、無 IMGUI 依賴）；本頁拿到
//          UCL_MarkdownDocument 後依 block 類型 switch 渲染 — heading 字級分層、code 用無 richText
//          的 box、table 用 HorizontalScope 平均分欄、mermaid 以樹狀縮排顯示節點 shape 與邊 label。
//          inline 語法（** / * / ` / [text](url) / ![alt](path)）由本頁的 InlineFormat 轉成
//          IMGUI rich-text tag（與 parser 解耦：parser 不產生 UI tag）。
// 數值影響：純讀取 + 字串處理；單檔載入後 m_Blocks 快取，重繪不重 parse。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 內嵌式 markdown 檢視頁。透過 <see cref="Create"/> 從外部傳入檔案路徑開啟。
    /// 與 OS 預設 viewer 並存：TopBar 提供 Reveal / OS Open / Copy raw 三顆按鈕，
    /// 這頁的存在價值是「不離開 Unity 視窗就能看 .md」。
    /// </summary>
    /// <remarks>
    /// 怎麼開新 EditorPage 見 <c>Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md</c>；
    /// markdown 渲染流程細節見 §inline format 的順序註解。
    /// </remarks>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MarkdownViewerPage.md")]
    public class UCL_MarkdownViewerPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_MarkdownViewer";

        // ==== 載入狀態 ====
        // 物理意義：m_RelativePath / m_AbsolutePath 一組 — 前者用於 UCL_URL prefix 解析，後者用於 File IO / Reveal
        string m_RelativePath;
        string m_AbsolutePath;
        string m_RawContent;
        string m_Frontmatter;
        bool m_ShowFrontmatter;
        bool m_LoadFailed;
        string m_LoadError;
        // 區塊職責：parse 結果 cache 在 page 實例 — 重繪不重 parse
        // 物理意義：型別與 parser 解耦，全部來自 UCL_MarkdownParser；本頁只負責渲染
        List<UCL_MdBlock> m_Blocks;

        // ==== styles（lazy 建立、頁內共用） ====
        GUIStyle[] m_HeadingStyles;
        GUIStyle m_BodyStyle;
        GUIStyle m_CodeBlockStyle;
        GUIStyle m_BulletStyle;
        GUIStyle m_TableHeaderStyle;

        // 區塊與 mermaid 的所有資料型別已抽到 UCL_MarkdownParser.cs（同 namespace），請見：
        //   UCL_MdBlockType / UCL_MdBlock / UCL_MermaidNode / UCL_MermaidEdge / UCL_MermaidGraph / UCL_MarkdownDocument
        // 本頁只負責「拿到 parse 結果後怎麼渲染」。

        /// <summary>
        /// 由外部入口（搜尋結果、welcome page 等）呼叫。
        /// </summary>
        /// <param name="relativePath">git-root 相對路徑（用於顯示 / OS open prefix 解析）</param>
        /// <param name="absolutePath">絕對路徑（用於 File.ReadAllText / RevealInFinder）</param>
        public static UCL_MarkdownViewerPage Create(string relativePath, string absolutePath)
        {
            var page = UCL_EditorPage.Create<UCL_MarkdownViewerPage>();
            page.LoadFile(relativePath, absolutePath);
            return page;
        }

        // ===========================================================
        // 載入：讀檔 → 委由 UCL_MarkdownParser 拆 block list
        // 物理意義：本頁不持有任何 parse 邏輯，純做 IO + 把結果塞進渲染 cache
        // ===========================================================
        void LoadFile(string relPath, string absPath)
        {
            m_RelativePath = relPath;
            m_AbsolutePath = absPath;
            m_LoadFailed = false; m_LoadError = null;
            m_RawContent = null; m_Frontmatter = null;
            m_Blocks = new List<UCL_MdBlock>();
            try
            {
                if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
                {
                    m_LoadFailed = true;
                    m_LoadError = "File not found: " + (absPath ?? "(null)");
                    return;
                }
                m_RawContent = File.ReadAllText(absPath);
                var doc = UCL_MarkdownParser.Parse(m_RawContent);
                m_Frontmatter = doc.Frontmatter;
                m_Blocks = doc.Blocks;
            }
            catch (System.Exception e)
            {
                m_LoadFailed = true;
                m_LoadError = e.Message;
            }
        }

        // ===========================================================
        // styles
        // ===========================================================
        void EnsureStyles()
        {
            if (m_HeadingStyles != null) return;
            int[] sizes = { 22, 19, 17, 15, 14, 13 };
            m_HeadingStyles = new GUIStyle[6];
            for (int i = 0; i < 6; i++)
            {
                m_HeadingStyles[i] = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    fontSize = sizes[i],
                    fontStyle = FontStyle.Bold,
                    richText = true,
                    wordWrap = true,
                };
            }
            m_BodyStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, wordWrap = true };
            m_BulletStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, wordWrap = true };
            // 表格 header：粗體 + 微亮黃，視覺把 header 列從資料列分出來
            m_TableHeaderStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
            {
                richText = true,
                wordWrap = true,
                fontStyle = FontStyle.Bold,
            };
            // 區塊職責：code block 專用樣式 — 關掉 rich-text（避免 <T> 等被解析）、開 wordWrap 防破版
            // 物理意義：code 內容是純文字，不該再吃 rich-text；fontSize 略小讓螢幕能塞更多字
            m_CodeBlockStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
            {
                richText = false,
                wordWrap = true,
                fontSize = Mathf.Max(10, UCL_GUIStyle.LabelStyle.fontSize - 1),
            };
        }

        // ===========================================================
        // TopBar 額外按鈕：📂 Reveal / 📖 OS Open / Copy raw
        // 物理意義：保留與搜尋結果列同一組外部入口；本頁是「另一條路」而非「取代」
        // ===========================================================
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (string.IsNullOrEmpty(m_RelativePath)) return;

            if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Reveal"),
                UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (!string.IsNullOrEmpty(m_AbsolutePath))
                {
                    EditorUtility.RevealInFinder(m_AbsolutePath);
                }
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.Search.OpenButton"),
                UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
            {
                UCL_DocSearchPage.OpenDocByUrl(m_RelativePath, m_AbsolutePath);
            }
            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"),
                UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (!string.IsNullOrEmpty(m_RawContent))
                {
                    GUIUtility.systemCopyBuffer = m_RawContent;
                }
            }

            // 頂部：相對路徑
            // 物理意義：統一走 GUILayout.Label + UCL_GUIStyle.LabelStyle 模式，
            //          避免 UCL_GUILayout.Label 多一層包裝（該 API 已標示廢棄）
            GUILayout.Label(m_RelativePath ?? "", UCL_GUIStyle.LabelStyle);
        }

        // ===========================================================
        // ContentOnGUI：path 標頭 + frontmatter toggle + block 渲染
        // ===========================================================
        protected override void ContentOnGUI()
        {
            EnsureStyles();
            if (m_LoadFailed)
            {
                GUILayout.Label("⚠ Load failed: " + (m_LoadError ?? ""), UCL_GUIStyle.LabelStyle);
                return;
            }
            if (m_Blocks == null) return;

            // frontmatter（折疊）：保留原樣 YAML 顯示供 debug / 校稿
            // 區塊職責：用 UCL_GUILayout.Toggle(bool, int size) — 顯示 ▼/► 折疊圖示，
            //          比一般 checkbox 更貼近「展開 / 收起」語意
            // 物理意義：見 Overview.md §3.1（基礎欄位 → Toggle(value, size)）
            if (!string.IsNullOrEmpty(m_Frontmatter))
            {
                using (new GUILayout.HorizontalScope())
                {
                    m_ShowFrontmatter = UCL_GUILayout.Toggle(m_ShowFrontmatter, 16);
                    GUILayout.Label("Frontmatter");
                    GUILayout.FlexibleSpace();
                }
                if (m_ShowFrontmatter)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label(m_Frontmatter, m_CodeBlockStyle);
                    }
                }
            }

            for (int i = 0; i < m_Blocks.Count; i++)
            {
                DrawBlock(m_Blocks[i]);
            }
        }

        void DrawBlock(UCL_MdBlock b)
        {
            switch (b.Type)
            {
                case UCL_MdBlockType.Heading:
                {
                    // 區塊職責：H1/H2 加多一點上方間距，視覺把章節分開
                    GUILayout.Space(b.HeadingLevel <= 2 ? 8 : 4);
                    int idx = Mathf.Clamp(b.HeadingLevel - 1, 0, 5);
                    GUILayout.Label(InlineFormat(b.Text), m_HeadingStyles[idx]);
                    break;
                }
                case UCL_MdBlockType.Paragraph:
                    GUILayout.Label(InlineFormat(b.Text), m_BodyStyle);
                    break;
                case UCL_MdBlockType.Bullet:
                    GUILayout.Label("  " + InlineFormat(b.Text), m_BulletStyle);
                    break;
                case UCL_MdBlockType.Quote:
                    // 視覺：左側藍條 + 斜體
                    GUILayout.Label("<color=#9BD0FF>▎</color> <i>" + InlineFormat(b.Text) + "</i>", m_BodyStyle);
                    break;
                case UCL_MdBlockType.CodeFence:
                    using (new GUILayout.VerticalScope("box"))
                    {
                        if (!string.IsNullOrEmpty(b.CodeLang))
                        {
                            GUILayout.Label("<color=#888888>" + b.CodeLang + "</color>", m_BodyStyle);
                        }
                        GUILayout.Label(b.CodeBody ?? "", m_CodeBlockStyle);
                    }
                    break;
                case UCL_MdBlockType.HorizontalRule:
                    GUILayout.Space(2);
                    GUILayout.Box(GUIContent.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
                    GUILayout.Space(2);
                    break;
                case UCL_MdBlockType.Empty:
                    GUILayout.Space(4);
                    break;
                case UCL_MdBlockType.Table:
                    DrawTable(b.TableRows);
                    break;
                case UCL_MdBlockType.Mermaid:
                    DrawMermaid(b.Graph, b.CodeBody);
                    break;
            }
        }

        // 區塊職責：渲染 markdown 表格 — 每列一個 HorizontalScope，cell 用 ExpandWidth 平均分配欄寬
        // 物理意義：第 0 列為 header（粗體 style）；header 下方畫一條 1px 分隔線；之後是資料列
        // 數值影響：colCount 取自 header；資料列若 cell 數不足，缺的位置補空字串避免 IndexOutOfRange
        void DrawTable(List<string[]> rows)
        {
            if (rows == null || rows.Count == 0) return;
            int colCount = rows[0].Length;
            using (new GUILayout.VerticalScope("box"))
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    GUIStyle style = (r == 0) ? m_TableHeaderStyle : m_BodyStyle;
                    using (new GUILayout.HorizontalScope())
                    {
                        for (int c = 0; c < colCount; c++)
                        {
                            string cell = c < row.Length ? row[c] : "";
                            GUILayout.Label(InlineFormat(cell), style,
                                GUILayout.ExpandWidth(true), GUILayout.MinWidth(60));
                        }
                    }
                    if (r == 0)
                    {
                        // header 與資料列之間畫細線分隔（取代 |---|---| 在原 markdown 的視覺）
                        GUILayout.Box(GUIContent.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
                    }
                }
            }
        }

        // ===========================================================
        // mermaid 渲染
        // 物理意義：v1 不畫真正的 graph layout；改用「樹狀縮排 + 節點 shape brackets + 邊 label」
        //          呈現節點間關係。從入度為 0 的節點（roots）DFS，已訪問節點重訪時標 (↩)。
        // 數值影響：每節點一行；分支會直接列在父節點之下、用 indent 表示層級；節點 shape：
        //          [rect] / (round) / ◇ diamond ◇；邊 label 包在 ─label→ 中。
        // ===========================================================
        void DrawMermaid(UCL_MermaidGraph g, string rawBody)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                if (g == null || g.Nodes.Count == 0)
                {
                    GUILayout.Label("<color=#888888>(mermaid: empty / parse failed)</color>", m_BodyStyle);
                    if (!string.IsNullOrEmpty(rawBody))
                    {
                        GUILayout.Label(rawBody, m_CodeBlockStyle);
                    }
                    return;
                }
                GUILayout.Label($"<color=#888888>graph {g.Direction}</color>", m_BodyStyle);

                // 入度統計 — 入度為 0 的節點為樹根
                var inDeg = new Dictionary<string, int>();
                foreach (var id in g.Nodes.Keys) inDeg[id] = 0;
                foreach (var e in g.Edges)
                {
                    if (e.To != null && inDeg.ContainsKey(e.To)) inDeg[e.To]++;
                }
                var visited = new HashSet<string>();
                foreach (var kv in inDeg)
                {
                    if (kv.Value == 0 && !visited.Contains(kv.Key))
                    {
                        DrawMermaidNode(g, kv.Key, 0, visited, null);
                    }
                }
                // 環狀殘餘 — 任選未訪節點當起點
                foreach (var id in g.Nodes.Keys)
                {
                    if (!visited.Contains(id))
                    {
                        DrawMermaidNode(g, id, 0, visited, null);
                    }
                }
            }
        }

        void DrawMermaidNode(UCL_MermaidGraph g, string id, int depth, HashSet<string> visited, string edgeLabel)
        {
            if (!g.Nodes.TryGetValue(id, out var node)) return;
            bool alreadySeen = visited.Contains(id);
            string indent = depth > 0 ? new string(' ', depth * 2) : "";
            string label = string.IsNullOrEmpty(node.Label) ? id : node.Label;
            string nodeText;
            switch (node.Shape)
            {
                case "round":   nodeText = $"<color=#9BD0FF>(</color> {label} <color=#9BD0FF>)</color>"; break;
                case "diamond": nodeText = $"<color=#FFE066>◇</color> <b>{label}</b> <color=#FFE066>◇</color>"; break;
                default:        nodeText = $"<color=#9BD0FF>[</color> {label} <color=#9BD0FF>]</color>"; break;
            }
            string arrow;
            if (depth == 0) arrow = "";
            else if (string.IsNullOrEmpty(edgeLabel)) arrow = "<color=#888888>→</color> ";
            else arrow = $"<color=#888888>─</color><color=#FFE066>{edgeLabel}</color><color=#888888>→</color> ";
            string suffix = alreadySeen ? " <color=#888888>(↩ ref)</color>" : "";
            GUILayout.Label(indent + arrow + nodeText + suffix, m_BodyStyle);

            if (alreadySeen) return;
            visited.Add(id);

            // 走訪所有由本節點出發的邊；同層多分支會直接 stack 顯示
            for (int i = 0; i < g.Edges.Count; i++)
            {
                var e = g.Edges[i];
                if (e.From == id && e.To != null)
                {
                    DrawMermaidNode(g, e.To, depth + 1, visited, e.Label);
                }
            }
        }

        // ===========================================================
        // inline format → IMGUI rich-text
        // 物理意義：依序處理 image → code → bold → italic → link
        //          — 順序非常重要：先抓 image（語法 ![alt](url) 是 link 的超集），
        //          再抓 code（避免 ** 等在 backtick 內被誤解），最後 bold/italic/link。
        // 數值影響：純字串替換；輸出含 <b><i><color> 等 IMGUI rich-text tag。
        // ===========================================================
        static readonly Regex s_RxImage  = new Regex(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);
        static readonly Regex s_RxCode   = new Regex(@"`([^`\n]+)`", RegexOptions.Compiled);
        static readonly Regex s_RxBold   = new Regex(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled);
        static readonly Regex s_RxItalic = new Regex(@"(?<!\*)\*([^*\n]+)\*(?!\*)|_([^_\n]+)_", RegexOptions.Compiled);
        static readonly Regex s_RxLink   = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        static string InlineFormat(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // image → 灰色 placeholder（v1：不渲染圖片）
            s = s_RxImage.Replace(s, m => "<color=#888888>🖼 " + m.Groups[1].Value + "</color>");
            // inline code → 黃色粗體
            s = s_RxCode.Replace(s, m => "<color=#FFE066><b>" + m.Groups[1].Value + "</b></color>");
            // bold
            s = s_RxBold.Replace(s, m => "<b>" + m.Groups[1].Value + "</b>");
            // italic（避開 ** 與單獨的 *）
            s = s_RxItalic.Replace(s, m =>
            {
                string text = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                return "<i>" + text + "</i>";
            });
            // link → 著色（v1：不可點）
            s = s_RxLink.Replace(s, m => "<color=#9BD0FF>" + m.Groups[1].Value + "</color>");
            return s;
        }
    }
}
#endif
