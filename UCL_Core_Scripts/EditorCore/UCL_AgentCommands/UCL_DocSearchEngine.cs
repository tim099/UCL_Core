// 區塊職責：本檔提供「對 markdown 文件做模糊搜尋」的共用引擎，
//          被 Cmd_SearchDocs（CLI / agent batch）與 UCL_WelcomePage（內嵌搜尋列）共用。
// 物理意義：把 query expansion + scoring + ranking 從 Cmd 邏輯獨立出來，
//          上層元件只需負責「資料來源」與「結果呈現」，搜尋核心一致。
// 數值影響：純函式集合，不寫檔不修改 entries。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 單筆搜尋命中：被命中的文件 + 分數 + 命中的欄位列表。
    /// P1 新增：最佳命中 section + 上下文片段（rich-text 高亮），給 UI 端做 snippet preview。
    /// </summary>
    public class UCL_DocSearchHit
    {
        public UCL_DocCatalogEntry Entry;
        public int Score;
        public List<string> MatchedFields;

        /// <summary>命中分數最高的 section 標題（H1~H6 文字）；null = intro / 無標題段</summary>
        public string SectionTitle;
        /// <summary>該 section 在原檔案中的起始行號（1-based）；0 = 未取得</summary>
        public int SectionStartLine;
        /// <summary>圍繞最早命中位置的上下文片段；含 IMGUI rich-text 高亮（&lt;color&gt;&lt;b&gt;）；可為 null</summary>
        public string Snippet;
    }

    /// <summary>
    /// .md 切割後的章節單元：標題（null=intro）+ 起始行 + body 文字（不含標題行本身）。
    /// 由 <see cref="UCL_DocSearchEngine.LoadSections"/> 產生，給 SearchSimpleWithBody 做章節級計分用。
    /// </summary>
    public class UCL_DocSection
    {
        public string Heading;
        public int StartLine;
        public string Body;
    }

    /// <summary>
    /// 文件搜尋引擎（純 static 工具集）。流程：
    /// <list type="number">
    ///   <item>caller 先用 <see cref="UCL_DocCatalogScanner.ScanRoots"/> 取得 entries</item>
    ///   <item>用 <see cref="LoadSynonyms"/>（或自行構造）取得同義詞群</item>
    ///   <item>用 <see cref="Search"/>（或 <see cref="SearchSimple"/>）對 entries 計分排序</item>
    /// </list>
    ///
    /// 計分權重：title=10 / aliases=8 / tags=6 / description=5 / filename=4。
    /// 每 term 取最高分相加，最後加 termsHit×2 bonus。
    /// </summary>
    public static class UCL_DocSearchEngine
    {
        // ===========================================================
        // 公開 API
        // ===========================================================

        /// <summary>
        /// 一般用法：給定 query 字串 + entries → 回傳 ranked hits（top-N，預設 unlimited）。
        /// 若提供 <paramref name="preferredLang"/>（如 "zh-Hant"），路徑含該 lang 段的文件會額外加分排前。
        /// </summary>
        public static List<UCL_DocSearchHit> SearchSimple(
            string query,
            IEnumerable<UCL_DocCatalogEntry> entries,
            List<List<string>> synonymGroups = null,
            bool orMode = false,
            int limit = 0,
            string preferredLang = null)
        {
            if (string.IsNullOrWhiteSpace(query) || entries == null)
            {
                return new List<UCL_DocSearchHit>();
            }
            var rawTerms = query.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (rawTerms.Count == 0) return new List<UCL_DocSearchHit>();
            var expandedSets = rawTerms.Select(t => ExpandTerm(t, synonymGroups)).ToList();
            return Search(entries, expandedSets, orMode, limit, preferredLang);
        }

        /// <summary>
        /// 進階用法：caller 自行準備好展開後的 term sets（每個 set = 一個原始 term + 其同義詞）。
        /// </summary>
        public static List<UCL_DocSearchHit> Search(
            IEnumerable<UCL_DocCatalogEntry> entries,
            List<HashSet<string>> expandedTermSets,
            bool orMode = false,
            int limit = 0,
            string preferredLang = null)
        {
            var hits = new List<UCL_DocSearchHit>();
            if (entries == null || expandedTermSets == null || expandedTermSets.Count == 0) return hits;
            foreach (var e in entries)
            {
                if (e == null) continue;
                var (score, matched) = ScoreEntry(e, expandedTermSets, orMode);
                if (score > 0)
                {
                    // 區塊職責：path 含 preferredLang 段 → 加 lang bonus（讓當前語系版本排前）
                    // 物理意義：UCL_Core 多語系文件結構為 Docs~/<lang>/... 共 4 份；同一份內容用同
                    //          query 命中時應優先給使用者當前語系。EOV 端的 Docs/ 沒 lang 段，不受影響。
                    // 數值影響：只加分不減分；其他語系版本仍會出現在結果中（用更多上下文 cover），
                    //          只是排序往後。
                    int bonus = ComputeLangBonus(e.RelativePath, preferredLang);
                    if (bonus > 0)
                    {
                        score += bonus;
                        if (matched != null && !matched.Contains("lang")) matched.Add("lang");
                    }
                    hits.Add(new UCL_DocSearchHit
                    {
                        Entry = e, Score = score, MatchedFields = matched,
                    });
                }
            }
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (limit > 0 && hits.Count > limit) hits = hits.Take(limit).ToList();
            return hits;
        }

        // 區塊職責：依 preferredLang 對某 entry 路徑算「語系加權」
        // 物理意義：路徑含 "/<preferredLang>/" → +5；含其他已知語系段（en/ja/zh-Hans/zh-Hant）→ 0
        //          無語系段（單一語言 doc）→ 0，不影響原排序
        // 數值影響：純整數計算，不修改 entry
        static int ComputeLangBonus(string relPath, string preferredLang)
        {
            if (string.IsNullOrEmpty(preferredLang) || string.IsNullOrEmpty(relPath)) return 0;
            string p = "/" + relPath.Replace('\\', '/') + "/";
            return p.IndexOf("/" + preferredLang + "/", StringComparison.OrdinalIgnoreCase) >= 0 ? 5 : 0;
        }

        // ===========================================================
        // 計分：對每個 entry 計算分數
        // 物理意義：title 命中最有信心、aliases 是模糊搜尋主軸、filename 兜底
        // 數值影響：score=0 表不命中；AND 模式下任一 term miss 即 0
        // ===========================================================
        public static (int score, List<string> matched) ScoreEntry(
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

            totalScore += termsHit * 2;  // bonus：命中 term 數越多分越高
            return (totalScore, matchedFields.OrderBy(s => s).ToList());
        }

        // ===========================================================
        // 同義詞展開
        // 物理意義：query 端展開（中央詞表），與 doc 端 aliases（個別文件）互補
        // ===========================================================
        public static HashSet<string> ExpandTerm(string term, List<List<string>> synonymGroups)
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

        // ===========================================================
        // P1：章節級搜尋 + snippet preview（給 UCL_DocSearchPage 用）
        // 物理意義：在 metadata 計分之外讀取檔案 body，依 markdown 標題切 section 各自計分；
        //          選分數最高的 section 提取 ±N 字元 context、把 query 變體用 rich-text 高亮。
        //          Cmd_SearchDocs 維持走 Search/SearchSimple（純 metadata），不受影響。
        // 數值影響：每個 entry 多一次 ReadAllLines（200 篇 .md SSD 上 cold scan 仍可控）。
        //          score = metaScore + bestSectionBodyScore + termsHitCount*2 + langBonus；
        //          相較 SearchSimple 多了 body 加成，排序會略有差異。
        // ===========================================================

        /// <summary>
        /// SearchSimple 的「body-aware」變體：
        /// 額外讀取每個 entry 的檔案內容、依 H1~H6 切 section、章節級計分，
        /// 並產出最佳 section 的 snippet（含 IMGUI rich-text 高亮）。
        /// AND/OR 語義以「metadata 命中 ∪ body 命中」為準。
        /// </summary>
        public static List<UCL_DocSearchHit> SearchSimpleWithBody(
            string query,
            IEnumerable<UCL_DocCatalogEntry> entries,
            string gitRoot,
            List<List<string>> synonymGroups = null,
            bool orMode = false,
            int limit = 0,
            string preferredLang = null)
        {
            if (string.IsNullOrWhiteSpace(query) || entries == null)
            {
                return new List<UCL_DocSearchHit>();
            }
            var rawTerms = query.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (rawTerms.Count == 0) return new List<UCL_DocSearchHit>();
            var expandedSets = rawTerms.Select(t => ExpandTerm(t, synonymGroups)).ToList();

            var hits = new List<UCL_DocSearchHit>();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.RelativePath)) continue;

                // 1) 既有 metadata 計分（不含 termsHit*2 bonus，bonus 留到合併後一次加）
                var (metaScore, matched, metaHit) = ScoreEntryPerTerm(e, expandedSets);

                // 2) 讀檔切 section + body 計分
                string abs = Path.IsPathRooted(e.RelativePath)
                    ? e.RelativePath
                    : Path.Combine(gitRoot ?? "", e.RelativePath);
                var sections = LoadSections(abs);

                int bestSectionScore = 0;
                UCL_DocSection bestSection = null;
                bool[] anyBodyHit = new bool[expandedSets.Count];
                foreach (var s in sections)
                {
                    var (sScore, sHit) = ScoreSectionBody(s.Body, s.Heading, expandedSets);
                    for (int i = 0; i < sHit.Length; i++) if (sHit[i]) anyBodyHit[i] = true;
                    if (sScore > bestSectionScore) { bestSectionScore = sScore; bestSection = s; }
                }

                // 3) AND/OR gate：以 metadata ∪ body 為準
                int termsHitCount = 0;
                for (int i = 0; i < expandedSets.Count; i++)
                {
                    if (metaHit[i] || anyBodyHit[i]) termsHitCount++;
                }
                if (!orMode && termsHitCount < expandedSets.Count) continue;
                if (orMode && termsHitCount == 0) continue;

                int total = metaScore + bestSectionScore + termsHitCount * 2;

                // lang bonus（與 Search 路徑一致）
                int langBonus = ComputeLangBonus(e.RelativePath, preferredLang);
                if (langBonus > 0)
                {
                    total += langBonus;
                    if (!matched.Contains("lang")) matched.Add("lang");
                }
                if (bestSectionScore > 0 && !matched.Contains("body")) matched.Add("body");

                // 4) snippet：優先用最佳命中 section；無 body 命中時退回 intro
                string snippet = null;
                string secTitle = null;
                int secLine = 0;
                if (bestSection != null)
                {
                    snippet = BuildSnippet(bestSection.Body, expandedSets);
                    secTitle = bestSection.Heading;
                    secLine = bestSection.StartLine;
                }
                else if (sections.Count > 0)
                {
                    snippet = BuildSnippet(sections[0].Body, expandedSets);
                    secTitle = sections[0].Heading;
                    secLine = sections[0].StartLine;
                }

                hits.Add(new UCL_DocSearchHit
                {
                    Entry = e,
                    Score = total,
                    MatchedFields = matched.OrderBy(s => s).ToList(),
                    SectionTitle = secTitle,
                    SectionStartLine = secLine,
                    Snippet = snippet,
                });
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            // 區塊職責：同篇文件多語系變體收斂 — 若 preferredLang 版有命中，藏掉其他語系版
            // 物理意義：UCL_Core/Docs~/<lang>/... 4 份同內容文件，在搜尋結果裡只該出現使用者
            //          當前語系那一份；preferredLang 版沒命中時才保留其他語系版作 fallback
            // 數值影響：在 sort 之後、limit 截斷之前執行 — 確保 limit 名額不會被「會被收斂掉的
            //          其他語系版」吃光
            hits = CollapseLangVariants(hits, preferredLang);
            if (limit > 0 && hits.Count > limit) hits = hits.Take(limit).ToList();
            return hits;
        }

        // 已知多語系段（與 ComputeLangBonus 同步）— UCL_Core 多語系 Docs 路徑段為這 4 個
        static readonly string[] s_KnownLangs = { "en", "ja", "zh-Hans", "zh-Hant" };

        // ===========================================================
        // 同篇文件多語系變體收斂
        // 物理意義：把路徑裡的 "/<lang>/" 段當 lang token、其餘部分當 docKey；
        //          同 docKey 群組內若含 preferredLang 版本則只保留它，否則整組保留。
        // 數值影響：保持 hits 原排序（穩定刪除）；不在這裡重排，由 caller 已 sort 過。
        // ===========================================================
        static List<UCL_DocSearchHit> CollapseLangVariants(
            List<UCL_DocSearchHit> hits, string preferredLang)
        {
            if (hits == null || hits.Count == 0 || string.IsNullOrEmpty(preferredLang)) return hits;

            // 第一遍：收集每個 docKey 是否存在 preferredLang 版
            var hasPreferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits)
            {
                var (key, lang) = ExtractLangKey(h.Entry?.RelativePath);
                if (lang != null && string.Equals(lang, preferredLang, StringComparison.OrdinalIgnoreCase))
                {
                    hasPreferred.Add(key);
                }
            }

            // 第二遍：保留規則
            //   - 路徑無 lang 段（單一語言文件）→ 保留
            //   - 群組含 preferredLang 版 → 只保留 preferredLang 版
            //   - 群組無 preferredLang 版 → 全保留作 fallback
            var result = new List<UCL_DocSearchHit>(hits.Count);
            foreach (var h in hits)
            {
                var (key, lang) = ExtractLangKey(h.Entry?.RelativePath);
                if (lang == null) { result.Add(h); continue; }
                bool isPreferred = string.Equals(lang, preferredLang, StringComparison.OrdinalIgnoreCase);
                if (hasPreferred.Contains(key))
                {
                    if (isPreferred) result.Add(h);
                    // else: 同篇文件 preferredLang 版已在，丟棄此語系版
                }
                else
                {
                    result.Add(h);
                }
            }
            return result;
        }

        // 從 git-root 相對路徑切出 (docKey, lang)：
        //   "CardGame/Assets/UCL/UCL_Core/Docs~/zh-Hant/UCL_EditorPage/UCL_DocSearchPage.md"
        //   → key = "CardGame/Assets/UCL/UCL_Core/Docs~/UCL_EditorPage/UCL_DocSearchPage.md"
        //     lang = "zh-Hant"
        // 路徑沒有任何已知 lang 段時，lang = null（caller 視為「不收斂」）。
        static (string docKey, string lang) ExtractLangKey(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return (relPath, null);
            string p = relPath.Replace('\\', '/');
            foreach (var lang in s_KnownLangs)
            {
                string seg = "/" + lang + "/";
                int idx = p.IndexOf(seg, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string key = p.Substring(0, idx) + "/" + p.Substring(idx + seg.Length);
                    return (key, lang);
                }
            }
            return (p, null);
        }

        // ===========================================================
        // metadata 計分（per-term 版本）
        // 物理意義：對外暴露「哪個 term 在 metadata 命中」的位元向量，
        //          給 SearchSimpleWithBody 做 metadata ∪ body 的 AND/OR 判斷。
        // 數值影響：與 ScoreEntry 同權重（title=10 / aliases=8 / tags=6 / desc=5 / filename=4），
        //          但**不**加 termsHit*2 bonus（bonus 留到合併 body 後一次加，避免雙計）。
        // ===========================================================
        public static (int score, HashSet<string> matched, bool[] termHit) ScoreEntryPerTerm(
            UCL_DocCatalogEntry e, List<HashSet<string>> termSets)
        {
            var matched = new HashSet<string>();
            var hit = new bool[termSets.Count];
            int total = 0;
            if (e == null) return (0, matched, hit);

            string filename = (Path.GetFileNameWithoutExtension(e.RelativePath) ?? "").ToLowerInvariant();
            string title = (e.Title ?? "").ToLowerInvariant();
            string description = (e.Description ?? "").ToLowerInvariant();
            string tagsConcat = string.Join(",", e.Tags ?? new List<string>()).ToLowerInvariant();
            string aliasesConcat = string.Join(",", e.Aliases ?? new List<string>()).ToLowerInvariant();

            for (int i = 0; i < termSets.Count; i++)
            {
                int termScore = 0;
                foreach (var v in termSets[i])
                {
                    string vlow = v.ToLowerInvariant();
                    if (string.IsNullOrEmpty(vlow)) continue;
                    if (title.Contains(vlow))         { termScore = Math.Max(termScore, 10); matched.Add("title");       hit[i] = true; }
                    if (aliasesConcat.Contains(vlow)) { termScore = Math.Max(termScore, 8);  matched.Add("aliases");     hit[i] = true; }
                    if (tagsConcat.Contains(vlow))    { termScore = Math.Max(termScore, 6);  matched.Add("tags");        hit[i] = true; }
                    if (description.Contains(vlow))   { termScore = Math.Max(termScore, 5);  matched.Add("description"); hit[i] = true; }
                    if (filename.Contains(vlow))      { termScore = Math.Max(termScore, 4);  matched.Add("filename");    hit[i] = true; }
                }
                total += termScore;
            }
            return (total, matched, hit);
        }

        // ===========================================================
        // 章節 body 計分
        // 物理意義：每個 term 在 section body 命中得 3 分；在 heading 命中再加到 4。
        //          相對 metadata（最低 4 分）刻意壓低，避免 body 噪音蓋過 frontmatter 訊號。
        // 數值影響：單 section 每 term 上限 4；多個 term 在同 section 各取最高加總。
        // ===========================================================
        public static (int score, bool[] termHit) ScoreSectionBody(
            string body, string heading, List<HashSet<string>> termSets)
        {
            var hit = new bool[termSets.Count];
            int total = 0;
            string blow = (body ?? "").ToLowerInvariant();
            string hlow = (heading ?? "").ToLowerInvariant();
            for (int i = 0; i < termSets.Count; i++)
            {
                int termScore = 0;
                foreach (var v in termSets[i])
                {
                    string vlow = v.ToLowerInvariant();
                    if (string.IsNullOrEmpty(vlow)) continue;
                    if (blow.Length > 0 && blow.Contains(vlow))
                    {
                        termScore = Math.Max(termScore, 3);
                        hit[i] = true;
                    }
                    if (hlow.Length > 0 && hlow.Contains(vlow))
                    {
                        termScore = Math.Max(termScore, 4);
                        hit[i] = true;
                    }
                }
                total += termScore;
            }
            return (total, hit);
        }

        // ===========================================================
        // markdown 切 section
        // 物理意義：跳過開頭 YAML frontmatter（--- 包夾），逐行掃描，
        //          以 ^#{1,6}<space> 作為新 section 的分界。第一個標題之前的內容歸入 intro section（Heading=null）。
        // 數值影響：純讀取；caller 端 cache 由各自決定（page 內 m_Hits 重繪不重掃）。
        // ===========================================================
        public static List<UCL_DocSection> LoadSections(string absPath)
        {
            var result = new List<UCL_DocSection>();
            if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath)) return result;
            string[] lines;
            try { lines = File.ReadAllLines(absPath); }
            catch { return result; }

            int startIdx = 0;
            // 跳過 frontmatter（必須以 --- 起首才算）
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == "---") { startIdx = i + 1; break; }
                }
            }

            string curHeading = null;
            int curStartLine = startIdx + 1;
            var buf = new StringBuilder();
            for (int i = startIdx; i < lines.Length; i++)
            {
                string line = lines[i];
                if (TryParseHeading(line, out string heading))
                {
                    if (buf.Length > 0 || curHeading != null)
                    {
                        result.Add(new UCL_DocSection
                        {
                            Heading = curHeading,
                            StartLine = curStartLine,
                            Body = buf.ToString(),
                        });
                    }
                    curHeading = heading;
                    curStartLine = i + 1;
                    buf.Clear();
                }
                else
                {
                    buf.Append(line);
                    buf.Append('\n');
                }
            }
            if (buf.Length > 0 || curHeading != null)
            {
                result.Add(new UCL_DocSection
                {
                    Heading = curHeading,
                    StartLine = curStartLine,
                    Body = buf.ToString(),
                });
            }
            return result;
        }

        // ATX heading 偵測：開頭 1~6 個 #，後接單一空白，剩下視為 heading 文字
        static bool TryParseHeading(string line, out string heading)
        {
            heading = null;
            if (string.IsNullOrEmpty(line)) return false;
            int n = 0;
            while (n < line.Length && line[n] == '#') n++;
            if (n == 0 || n > 6) return false;
            if (n >= line.Length || line[n] != ' ') return false;
            heading = line.Substring(n + 1).Trim();
            return true;
        }

        // ===========================================================
        // snippet 產生
        // 物理意義：從 body 找最早命中的位置，截 [-leftCtx, +rightCtx] 字元視窗，
        //          壓掉換行/多重空白，最後對所有 term 變體做 case-insensitive 高亮。
        // 數值影響：純字串處理；輸出含 IMGUI rich-text tag，呼叫端 GUIStyle 必須開 richText。
        // ===========================================================
        public static string BuildSnippet(
            string body, List<HashSet<string>> termSets,
            int leftCtx = 80, int rightCtx = 160)
        {
            if (string.IsNullOrEmpty(body) || termSets == null || termSets.Count == 0) return null;

            int firstIdx = -1;
            foreach (var ts in termSets)
            {
                foreach (var v in ts)
                {
                    if (string.IsNullOrEmpty(v)) continue;
                    int idx = body.IndexOf(v, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && (firstIdx < 0 || idx < firstIdx)) firstIdx = idx;
                }
            }

            int start, end;
            if (firstIdx < 0)
            {
                start = 0;
                end = Math.Min(body.Length, leftCtx + rightCtx);
            }
            else
            {
                start = Math.Max(0, firstIdx - leftCtx);
                end = Math.Min(body.Length, firstIdx + rightCtx);
            }
            string raw = body.Substring(start, end - start)
                             .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            raw = Regex.Replace(raw, @"\s{2,}", " ").Trim();
            if (raw.Length == 0) return null;

            string highlighted = HighlightMatches(raw, termSets);
            string prefix = start > 0 ? "…" : "";
            string suffix = end < body.Length ? "…" : "";
            return prefix + highlighted + suffix;
        }

        // 對 raw 內所有 term 變體做不分大小寫高亮，回傳含 <color><b> 標記的字串。
        // 演算法：收集所有命中區間 → 排序 → 合併重疊 → 分段重組。
        static string HighlightMatches(string raw, List<HashSet<string>> termSets)
        {
            if (string.IsNullOrEmpty(raw) || termSets == null) return raw;
            var ranges = new List<(int start, int end)>();
            foreach (var ts in termSets)
            {
                foreach (var v in ts)
                {
                    if (string.IsNullOrEmpty(v)) continue;
                    int idx = 0;
                    while (idx < raw.Length)
                    {
                        int found = raw.IndexOf(v, idx, StringComparison.OrdinalIgnoreCase);
                        if (found < 0) break;
                        ranges.Add((found, found + v.Length));
                        idx = found + Math.Max(1, v.Length);
                    }
                }
            }
            if (ranges.Count == 0) return raw;
            ranges.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)>();
            foreach (var r in ranges)
            {
                if (merged.Count > 0 && r.start <= merged[merged.Count - 1].end)
                {
                    var last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = (last.start, Math.Max(last.end, r.end));
                }
                else
                {
                    merged.Add(r);
                }
            }
            var sb = new StringBuilder();
            int cur = 0;
            foreach (var m in merged)
            {
                if (m.start > cur) sb.Append(raw, cur, m.start - cur);
                sb.Append("<color=#FFE066><b>");
                sb.Append(raw, m.start, m.end - m.start);
                sb.Append("</b></color>");
                cur = m.end;
            }
            if (cur < raw.Length) sb.Append(raw, cur, raw.Length - cur);
            return sb.ToString();
        }

        // 同義詞檔格式（純文本，避免依賴 YAML lib）：
        //   # 註解行以 # 起始
        //   物品, 道具, item, items, 消耗品
        // 每非空非註解行 = 一組同義詞集合。
        public static List<List<string>> LoadSynonyms(string gitRoot, string path)
        {
            var groups = new List<List<string>>();
            if (string.IsNullOrEmpty(path)) return groups;
            string abs = Path.IsPathRooted(path) ? path : Path.Combine(gitRoot, path);
            if (!File.Exists(abs))
            {
                Debug.LogWarning($"[UCL_DocSearchEngine] synonyms file not found, skipped: {path}");
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
                Debug.LogWarning($"[UCL_DocSearchEngine] synonyms read failed: {e.Message}");
            }
            return groups;
        }
    }
}
#endif
