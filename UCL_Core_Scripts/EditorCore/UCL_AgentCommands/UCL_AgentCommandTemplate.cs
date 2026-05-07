
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/07 2026
// Agent Commands — Template 子系統 (Page 之外的管理層)
// 設計目標：
//   1. 將「常用指令組合」存成獨立 JSON 檔，供使用者快速一鍵載入到 Add Command 表單。
//   2. 一個 Template = 一個 .json 檔，存於 AgentCommands/Templates/<Name>.json，
//      格式刻意與 History entry 高度相似，agent 可同時 grep 兩邊作為使用範本。
//   3. 與 UI 解耦：所有 IO / 命名規則都集中在這裡，UCL_AgentCommandsPage 只呼叫公開方法。
//   4. 序列化改用 UCL_Json (JsonConvert.SaveDataToJson / LoadDataFromJson)，
//      不再手寫 parser，省掉 ~150 行樣板碼且自動支援 Dictionary<string,string>。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：Template 資料模型
    // 物理意義：Template 不會被自動執行，只是「指令模子」 — 載入到表單後使用者再決定送進 queue。
    // 數值影響：純資料容器；UCL_Json 透過反射讀寫 public 欄位，因此這裡的 public 宣告即為序列化 schema。
    // ===========================================================
    /// <summary>
    /// 一筆指令模板（對應 Templates/&lt;Name&gt;.json）。
    /// </summary>
    [Serializable]
    public class UCL_AgentCommandTemplate
    {
        /// <summary>模板顯示名稱（同時也是檔名，需可作為合法檔名）</summary>
        public string Name;
        /// <summary>Command Type（同 UCL_AgentCommand.Type）</summary>
        public string Type;
        /// <summary>OneShot / Repeatable（建立模板時的預設值）</summary>
        public UCL_AgentCommandMode Mode = UCL_AgentCommandMode.OneShot;
        /// <summary>預設 args</summary>
        public Dictionary<string, string> Args = new();
        /// <summary>預設 description（可被使用者覆寫）</summary>
        public string Description;
        /// <summary>給人類讀的補充筆記（為什麼要建這個模板、適用情境等）</summary>
        public string Notes;
        /// <summary>建立時間（ISO 8601）</summary>
        public string CreatedAt;
        /// <summary>最近一次套用時間（ISO 8601；新建時 = CreatedAt）</summary>
        public string LastUsedAt;
    }

    /// <summary>
    /// Template 的讀寫管理。<br/>
    /// 路徑：&lt;repoRoot&gt;/AgentCommands/Templates/&lt;Name&gt;.json（一檔一筆）
    /// </summary>
    public static class UCL_AgentCommandTemplateStore
    {
        public const string TemplatesDirRelative = "Templates";

        /// <summary>取得 Templates 資料夾的絕對路徑（不保證存在）。</summary>
        public static string GetTemplatesDir()
        {
            string queueDir = UCL_AgentCommandQueue.GetQueueDir();
            return Path.Combine(queueDir, TemplatesDirRelative);
        }

        /// <summary>取得指定 Name 對應的模板檔絕對路徑。</summary>
        public static string GetTemplatePath(string name)
        {
            return Path.Combine(GetTemplatesDir(), SanitizeFileName(name) + ".json");
        }

        /// <summary>確保 Templates 資料夾存在。</summary>
        public static void EnsureDir()
        {
            string dir = GetTemplatesDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // ===========================================================
        // 區塊職責：寫入 / 讀取 / 刪除（UCL_Json 路徑）
        // 物理意義：相同 Name 直接覆寫（使用者明確指定要這個名字 = 願意覆寫，
        //          這點刻意與 History 的 dedup 行為不同）。
        // 數值影響：直接動 .json 檔；JsonConvert.SaveDataToJson 透過反射處理欄位，
        //          欄位順序 = 反射順序（不保證），但 Dict / Enum / 字串都正確處理。
        // ===========================================================

        /// <summary>儲存（或覆寫）一筆模板。Name 為空會被拒絕。</summary>
        public static bool Save(UCL_AgentCommandTemplate t)
        {
            if (t == null || string.IsNullOrWhiteSpace(t.Name))
            {
                Debug.LogWarning("[UCL_AgentCommandTemplateStore] Save aborted: template or Name is empty.");
                return false;
            }
            EnsureDir();
            if (string.IsNullOrEmpty(t.CreatedAt)) t.CreatedAt = DateTime.UtcNow.ToString("o");
            if (string.IsNullOrEmpty(t.LastUsedAt)) t.LastUsedAt = t.CreatedAt;
            try
            {
                // 區塊職責：物件 → JsonData → 美化字串 → 寫檔
                // 物理意義：ToJsonBeautify 會做縮排換行，方便 agent / 人類直接 cat 檢視
                // 數值影響：寫一個 UTF-8 (no BOM) 的 .json 檔；overwrites 既有同名檔
                JsonData data = JsonConvert.SaveDataToJson(t);
                File.WriteAllText(GetTemplatePath(t.Name), data.ToJsonBeautify(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandTemplateStore] Failed to save '{t.Name}': {ex}");
                return false;
            }
        }

        /// <summary>讀取單筆模板；找不到 / 解析失敗 → 回傳 null。</summary>
        public static UCL_AgentCommandTemplate Load(string name)
        {
            string path = GetTemplatePath(name);
            if (!File.Exists(path)) return null;
            try
            {
                // 區塊職責：檔 → 字串 → JsonData → 物件
                // 物理意義：LoadDataFromJson<T>() 反射填入 public 欄位；缺漏欄位會保留型別預設值（兼容舊檔）
                // 數值影響：純讀取；不寫檔
                string raw = File.ReadAllText(path, Encoding.UTF8);
                JsonData data = JsonData.ParseJson(raw);
                var t = JsonConvert.LoadDataFromJson<UCL_AgentCommandTemplate>(data);
                if (t != null && string.IsNullOrEmpty(t.LastUsedAt)) t.LastUsedAt = t.CreatedAt; // 兼容舊檔
                return t;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandTemplateStore] Failed to load '{name}': {ex}");
                return null;
            }
        }

        /// <summary>列出所有模板，依 LastUsedAt 由新到舊排序。</summary>
        public static List<UCL_AgentCommandTemplate> LoadAll()
        {
            var list = new List<UCL_AgentCommandTemplate>();
            string dir = GetTemplatesDir();
            if (!Directory.Exists(dir)) return list;
            foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    string raw = File.ReadAllText(path, Encoding.UTF8);
                    var t = JsonConvert.LoadDataFromJson<UCL_AgentCommandTemplate>(JsonData.ParseJson(raw));
                    if (t == null) continue;
                    if (string.IsNullOrEmpty(t.LastUsedAt)) t.LastUsedAt = t.CreatedAt;
                    list.Add(t);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UCL_AgentCommandTemplateStore] Skip corrupted template {path}: {ex.Message}");
                }
            }
            list.Sort((a, b) => string.Compare(b.LastUsedAt ?? "", a.LastUsedAt ?? "", StringComparison.Ordinal));
            return list;
        }

        /// <summary>刪除指定 Name 模板。</summary>
        public static bool Delete(string name)
        {
            string path = GetTemplatePath(name);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); return true; }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandTemplateStore] Failed to delete '{name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>更新「最近一次使用時間」。在使用者把模板套用到表單時呼叫。</summary>
        public static void TouchLastUsed(string name)
        {
            var t = Load(name);
            if (t == null) return;
            t.LastUsedAt = DateTime.UtcNow.ToString("o");
            Save(t);
        }

        /// <summary>對模板做關鍵字搜尋（Name / Type / Description / Notes / Args 全文比對）。</summary>
        public static List<UCL_AgentCommandTemplate> Search(string keyword)
        {
            var all = LoadAll();
            if (string.IsNullOrWhiteSpace(keyword)) return all;
            string k = keyword.Trim().ToLowerInvariant();
            return all.Where(t => Matches(t, k)).ToList();
        }

        static bool Matches(UCL_AgentCommandTemplate t, string k)
        {
            if (Contains(t.Name, k)) return true;
            if (Contains(t.Type, k)) return true;
            if (Contains(t.Description, k)) return true;
            if (Contains(t.Notes, k)) return true;
            if (t.Args != null)
            {
                foreach (var kv in t.Args)
                {
                    if (Contains(kv.Key, k)) return true;
                    if (Contains(kv.Value, k)) return true;
                }
            }
            return false;
        }
        static bool Contains(string s, string lowerKeyword)
        {
            return !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(lowerKeyword);
        }

        static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }
    }
}
#endif
