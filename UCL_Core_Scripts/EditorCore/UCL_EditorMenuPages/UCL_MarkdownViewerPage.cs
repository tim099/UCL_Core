// 區塊職責：在 Editor 內以 IMGUI 直接渲染 markdown 文件，免切到 OS 預設 viewer
//          被 UCL_DocSearchPage 的「📄 預覽」按鈕呼叫；TopBar 仍保留 Reveal / OS Open 入口。
// 物理意義：把 .md 拆成 block list（heading / paragraph / code fence / bullet / quote / hr / empty / table），
//          再對 paragraph/heading/bullet/cell 內的 inline 語法（** / * / ` / [text](url) / ![alt](path)）
//          做 rich-text 替換。表格偵測 header + 分隔列（|---|---|）後消費連續資料列，
//          渲染成 HorizontalScope 平均分欄；連結 v1 僅著色不點擊、巢狀清單不處理。
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
        List<MdBlock> m_Blocks;

        // ==== styles（lazy 建立、頁內共用） ====
        GUIStyle[] m_HeadingStyles;
        GUIStyle m_BodyStyle;
        GUIStyle m_CodeBlockStyle;
        GUIStyle m_BulletStyle;
        GUIStyle m_TableHeaderStyle;

        // ==== block model ====
        enum MdBlockType { Heading, Paragraph, CodeFence, Bullet, Quote, HorizontalRule, Empty, Table }
        class MdBlock
        {
            public MdBlockType Type;
            public int HeadingLevel;        // Heading 用：1~6
            public string Text;             // Heading / Paragraph / Bullet / Quote 用
            public string CodeLang;         // CodeFence 用：```lang
            public string CodeBody;         // CodeFence 用
            public List<string[]> TableRows;// Table 用：第 0 列為 header；分隔列（|---|---|）已剃除
        }

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
        // 載入 + parse
        // ===========================================================
        void LoadFile(string relPath, string absPath)
        {
            m_RelativePath = relPath;
            m_AbsolutePath = absPath;
            m_LoadFailed = false; m_LoadError = null;
            m_RawContent = null; m_Frontmatter = null;
            m_Blocks = new List<MdBlock>();
            try
            {
                if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
                {
                    m_LoadFailed = true;
                    m_LoadError = "File not found: " + (absPath ?? "(null)");
                    return;
                }
                m_RawContent = File.ReadAllText(absPath);
                Parse(m_RawContent);
            }
            catch (System.Exception e)
            {
                m_LoadFailed = true;
                m_LoadError = e.Message;
            }
        }

        // 區塊職責：把 raw text 切成 MdBlock list
        // 物理意義：line-based 掃描 + 簡易狀態機（in-code-fence 開關）
        // 數值影響：只在 LoadFile 時跑一次；m_Blocks 之後當 cache 用
        void Parse(string content)
        {
            m_Blocks.Clear();
            m_Frontmatter = null;
            if (string.IsNullOrEmpty(content)) return;

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
                    m_Frontmatter = fm.ToString().TrimEnd();
                    idx = j;
                }
            }

            var paraBuf = new StringBuilder();

            for (int i = idx; i < lines.Length; i++)
            {
                string line = lines[i];

                // code fence：累積到下一個 ``` 為止；中間任何語法都不解析
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```"))
                {
                    FlushParagraph(paraBuf);
                    string lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                    var body = new StringBuilder();
                    i++;
                    for (; i < lines.Length; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("```")) break;
                        body.Append(lines[i]).Append('\n');
                    }
                    m_Blocks.Add(new MdBlock
                    {
                        Type = MdBlockType.CodeFence,
                        CodeLang = lang,
                        CodeBody = body.ToString().TrimEnd('\n'),
                    });
                    continue;
                }

                // heading
                if (TryParseHeading(line, out int lv, out string ht))
                {
                    FlushParagraph(paraBuf);
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.Heading, HeadingLevel = lv, Text = ht });
                    continue;
                }

                // horizontal rule（---、***、___ 各 ≥3 個）
                if (Regex.IsMatch(line, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$"))
                {
                    FlushParagraph(paraBuf);
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.HorizontalRule });
                    continue;
                }

                // empty line：作為 paragraph 邊界 + 視覺空白
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(paraBuf);
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.Empty });
                    continue;
                }

                // table：偵測 `|...|` 開頭結尾的 row + 緊接著的 `|---|---|` 分隔列
                // 物理意義：標準 GitHub markdown 表格 — 第一列 header、第二列 ---/--- 分隔、之後是資料列
                // 數值影響：消費連續 |...| 列直到非表格行；不支援對齊符號（:--/--:/:-:）— v1 一律左對齊
                if (IsTableLine(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    FlushParagraph(paraBuf);
                    var rows = new List<string[]> { SplitTableRow(line) };
                    i += 2; // 跳過 header + separator
                    while (i < lines.Length && IsTableLine(lines[i]))
                    {
                        rows.Add(SplitTableRow(lines[i]));
                        i++;
                    }
                    i--; // 抵銷 outer for 的 i++（讓非表格行下一輪重新處理）
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.Table, TableRows = rows });
                    continue;
                }

                // bullet（- / * / + / 1.）
                var bulletMatch = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s+(.*)$");
                if (bulletMatch.Success)
                {
                    FlushParagraph(paraBuf);
                    string indent = bulletMatch.Groups[1].Value;
                    string marker = bulletMatch.Groups[2].Value;
                    string rest = bulletMatch.Groups[3].Value;
                    // 視覺上 - / * / + 一律換成 •；數字保留原樣
                    string display = marker.Length == 1 ? "•" : marker;
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.Bullet, Text = indent + display + " " + rest });
                    continue;
                }

                // blockquote
                if (trimmed.StartsWith("> "))
                {
                    FlushParagraph(paraBuf);
                    m_Blocks.Add(new MdBlock { Type = MdBlockType.Quote, Text = trimmed.Substring(2) });
                    continue;
                }

                // 一般段落：累積直到下一個非段落 block 或空行
                paraBuf.Append(line).Append('\n');
            }
            FlushParagraph(paraBuf);
        }

        void FlushParagraph(StringBuilder buf)
        {
            if (buf.Length == 0) return;
            m_Blocks.Add(new MdBlock
            {
                Type = MdBlockType.Paragraph,
                Text = buf.ToString().TrimEnd('\n', ' ', '\t'),
            });
            buf.Clear();
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
        // 物理意義：用來確認上一行 `|...|` 是 table header 而不是普通 inline 引文
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

        // 區塊職責：把 `| a | b | c |` 切成 ["a", "b", "c"] 並 trim
        static string[] SplitTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            var cells = t.Split('|');
            for (int k = 0; k < cells.Length; k++) cells[k] = cells[k].Trim();
            return cells;
        }

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

        void DrawBlock(MdBlock b)
        {
            switch (b.Type)
            {
                case MdBlockType.Heading:
                {
                    // 區塊職責：H1/H2 加多一點上方間距，視覺把章節分開
                    GUILayout.Space(b.HeadingLevel <= 2 ? 8 : 4);
                    int idx = Mathf.Clamp(b.HeadingLevel - 1, 0, 5);
                    GUILayout.Label(InlineFormat(b.Text), m_HeadingStyles[idx]);
                    break;
                }
                case MdBlockType.Paragraph:
                    GUILayout.Label(InlineFormat(b.Text), m_BodyStyle);
                    break;
                case MdBlockType.Bullet:
                    GUILayout.Label("  " + InlineFormat(b.Text), m_BulletStyle);
                    break;
                case MdBlockType.Quote:
                    // 視覺：左側藍條 + 斜體
                    GUILayout.Label("<color=#9BD0FF>▎</color> <i>" + InlineFormat(b.Text) + "</i>", m_BodyStyle);
                    break;
                case MdBlockType.CodeFence:
                    using (new GUILayout.VerticalScope("box"))
                    {
                        if (!string.IsNullOrEmpty(b.CodeLang))
                        {
                            GUILayout.Label("<color=#888888>" + b.CodeLang + "</color>", m_BodyStyle);
                        }
                        GUILayout.Label(b.CodeBody ?? "", m_CodeBlockStyle);
                    }
                    break;
                case MdBlockType.HorizontalRule:
                    GUILayout.Space(2);
                    GUILayout.Box(GUIContent.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
                    GUILayout.Space(2);
                    break;
                case MdBlockType.Empty:
                    GUILayout.Space(4);
                    break;
                case MdBlockType.Table:
                    DrawTable(b.TableRows);
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
