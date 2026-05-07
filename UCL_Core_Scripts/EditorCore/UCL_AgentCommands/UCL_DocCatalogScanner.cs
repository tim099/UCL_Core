
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：本檔提供「掃描 markdown 資料夾並解析 YAML frontmatter」的共用 helper，
//          被 Cmd_ExportDocsCatalog（產靜態索引）與 Cmd_SearchDocs（live 搜尋）兩支 Cmd 共用。
// 物理意義：把「path → DocEntry」的轉換從 Cmd 邏輯獨立出來，避免重複實作 frontmatter parser；
//          新 Cmd 只要呼叫 ScanRoots 就能拿到結構化的 entry list，再各自決定如何渲染 / 過濾。
// 數值影響：純讀取，不修改任何檔案；caller 自行決定後續輸出位置。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 單一 markdown 文件的索引列：路徑 + frontmatter 解析結果。
    /// 由 <see cref="UCL_DocCatalogScanner"/> 產生並回傳給上層 Cmd 使用。
    /// </summary>
    public class UCL_DocCatalogEntry
    {
        /// <summary>git-root 相對的檔案路徑（forward slashes）</summary>
        public string RelativePath;
        /// <summary>frontmatter.title；缺則 fallback 至檔名 / 第一個 H1</summary>
        public string Title;
        /// <summary>frontmatter.description（可空）</summary>
        public string Description;
        /// <summary>frontmatter.tags（受控詞彙做分類）</summary>
        public List<string> Tags = new List<string>();
        /// <summary>frontmatter.aliases（自由同義詞做模糊搜尋）</summary>
        public List<string> Aliases = new List<string>();
        /// <summary>frontmatter.target_audience</summary>
        public List<string> TargetAudience = new List<string>();
        /// <summary>frontmatter.last_updated（YYYY-MM-DD）</summary>
        public string LastUpdated;
        /// <summary>frontmatter.archived: true → 預設不列入索引</summary>
        public bool Archived;
        /// <summary>檔案是否含 frontmatter 區塊（給統計 / 規範稽核用）</summary>
        public bool HasFrontmatter;
    }

    /// <summary>
    /// 掃描 markdown 資料夾並解析 YAML frontmatter 的共用工具。
    /// 全 static — 沒有狀態，每次 ScanRoots 都是冷啟。caller 想 cache 自行加。
    /// </summary>
    public static class UCL_DocCatalogScanner
    {
        // ===========================================================
        // 主入口：遞迴掃 roots，每個 .md 解 frontmatter，回傳 entry list
        // 物理意義：給上層 Cmd「結構化的 200 篇文件視圖」一次拿到位
        // 數值影響：純讀取；caller 用 token 控制中斷
        // ===========================================================
        /// <summary>
        /// 掃描 roots 列出的所有 .md 並解析 frontmatter。
        /// </summary>
        /// <param name="roots">要掃的資料夾清單（git-root 相對；絕對路徑也可）</param>
        /// <param name="gitRoot">git root 絕對路徑（用於計算 RelativePath）</param>
        /// <param name="excludeDirs">路徑片段命中即略過（如 node_modules / .git）</param>
        /// <param name="includeArchived">是否包含 frontmatter 標 archived: true 的檔案</param>
        /// <param name="token">取消權杖</param>
        /// <returns>依路徑排序的 DocEntry list</returns>
        public static List<UCL_DocCatalogEntry> ScanRoots(
            IEnumerable<string> roots,
            string gitRoot,
            IEnumerable<string> excludeDirs,
            bool includeArchived,
            CancellationToken token)
        {
            var entries = new List<UCL_DocCatalogEntry>();
            var excludes = excludeDirs?.ToList() ?? new List<string>();

            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root)) continue;
                string absRoot = Path.IsPathRooted(root) ? root : Path.Combine(gitRoot, root);
                if (!Directory.Exists(absRoot))
                {
                    Debug.LogWarning($"[UCL_DocCatalogScanner] root not found, skipped: {root} → {absRoot}");
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(absRoot, "*.md", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = file.Replace('\\', '/');
                    // 排除規則：路徑含任一 excludeDirs 片段（前後加 / 比對避免子字串誤判）
                    bool excluded = false;
                    foreach (var ex in excludes)
                    {
                        if (normalized.IndexOf("/" + ex + "/", StringComparison.OrdinalIgnoreCase) >= 0
                            || normalized.EndsWith("/" + ex, StringComparison.OrdinalIgnoreCase))
                        {
                            excluded = true; break;
                        }
                    }
                    if (excluded) continue;
                    var entry = ParseDoc(file, gitRoot);
                    if (!includeArchived && entry.Archived) continue;
                    entries.Add(entry);
                }
            }

            entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        // ===========================================================
        // Frontmatter parser：手刻簡易 YAML（只支援 scalar / `[a, b]` 行內 list）
        // 物理意義：避免引入完整 YAML lib；專案 frontmatter 都符合此 subset
        // 數值影響：純讀檔
        // ===========================================================
        /// <summary>
        /// 讀單一 .md 檔，解析其 YAML frontmatter，回傳 DocEntry。
        /// 沒有 frontmatter 的檔案仍會被列入（title 取自檔名 / 第一個 H1）。
        /// </summary>
        public static UCL_DocCatalogEntry ParseDoc(string absPath, string gitRoot)
        {
            var entry = new UCL_DocCatalogEntry
            {
                RelativePath = MakeRelative(absPath, gitRoot),
            };

            string[] lines;
            try { lines = File.ReadAllLines(absPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_DocCatalogScanner] read failed: {absPath} ({e.Message})");
                return entry;
            }

            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                // 沒有 frontmatter — 從第一個 H1 取 title
                foreach (var line in lines)
                {
                    if (line.StartsWith("# "))
                    {
                        entry.Title = line.Substring(2).Trim();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(entry.Title))
                {
                    entry.Title = Path.GetFileNameWithoutExtension(absPath);
                }
                return entry;
            }

            entry.HasFrontmatter = true;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim() == "---") break;
                int sep = line.IndexOf(':');
                if (sep < 0) continue;
                string key = line.Substring(0, sep).Trim().ToLowerInvariant();
                string val = line.Substring(sep + 1).Trim();
                switch (key)
                {
                    case "title": entry.Title = StripQuotes(val); break;
                    case "description": entry.Description = StripQuotes(val); break;
                    case "last_updated": entry.LastUpdated = StripQuotes(val); break;
                    case "tags": entry.Tags = ParseInlineList(val); break;
                    case "aliases": entry.Aliases = ParseInlineList(val); break;
                    case "target_audience": entry.TargetAudience = ParseInlineList(val); break;
                    case "archived":
                        entry.Archived = string.Equals(StripQuotes(val), "true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            if (string.IsNullOrEmpty(entry.Title))
            {
                entry.Title = Path.GetFileNameWithoutExtension(absPath);
            }
            return entry;
        }

        // ===== 共用小工具 =====

        public static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length >= 2 &&
                ((s[0] == '"' && s[s.Length - 1] == '"') ||
                 (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        public static List<string> ParseInlineList(string val)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(val)) return list;
            val = val.Trim();
            if (val.StartsWith("[") && val.EndsWith("]"))
            {
                val = val.Substring(1, val.Length - 2);
            }
            foreach (var part in val.Split(','))
            {
                string p = StripQuotes(part.Trim());
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
            }
            return list;
        }

        public static List<string> SplitList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();
        }

        public static string MakeRelative(string abs, string root)
        {
            string a = abs.Replace('\\', '/');
            string r = root.Replace('\\', '/').TrimEnd('/');
            return a.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)
                ? a.Substring(r.Length + 1)
                : a;
        }

        // 區塊職責：取得 git-root（從 Application.dataPath 往上 walk 找含 `.git` 目錄的 ancestor）
        // 物理意義：原寫法 `dataPath/../..` 假設專案結構為 `<gitRoot>/<UnityProject>/Assets`（如 CardGame layout），
        //          對 git-root 即 Unity project root 的扁平結構（如 TEVI）會落點偏差 1 層。
        //          改成主動 walk 比對 `.git` 直接資料夾（不是 file — submodule 的 .git 是檔案 redirect，
        //          應跳過繼續往上）兩種結構都能正確命中。
        // 數值影響：純路徑計算 + 至多走幾層 Directory.Exists
        public static string GetGitRoot()
        {
            string p = Path.GetFullPath(Application.dataPath); // <project>/Assets
            string cur = Path.GetDirectoryName(p);
            while (!string.IsNullOrEmpty(cur))
            {
                // submodule 的 .git 是檔案（gitdir: redirect），只接受真正的 .git 目錄
                if (Directory.Exists(Path.Combine(cur, ".git")))
                    return cur.Replace('\\', '/');
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || parent == cur) break;
                cur = parent;
            }
            // fallback：保留舊行為，避免無 .git 的特殊環境完全失效
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../..")).Replace('\\', '/');
        }
    }
}
#endif
