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
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 單筆搜尋命中：被命中的文件 + 分數 + 命中的欄位列表。
    /// </summary>
    public class UCL_DocSearchHit
    {
        public UCL_DocCatalogEntry Entry;
        public int Score;
        public List<string> MatchedFields;
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
