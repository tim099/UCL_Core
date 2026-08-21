// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：失敗 Cmd 的**可補跑紀錄**（Tim 2026-08-21 派單）
// 物理意義：失敗的 OneShot 自 2026-08-07 起會即時出隊（queue 不堵塞），而既有的兩份失敗痕跡
//          都不足以「補跑」：
//            · `_cmd_results/<id>.json` —— 機器可讀 verdict，但**不含 Args**，且 3 天後被 Purge
//            · `_cmd_errors/<id>.md`    —— 有 Args，但那是**給人讀的視圖**（markdown）
//          ⇒ 要重跑就得有 Type + Mode + Args 的結構化紀錄，所以在失敗當下多寫一份本檔。
//          ⛔ 刻意**不去 parse `_cmd_errors` 的 md** —— 對人類視圖寫 parser 是第二份真相源，
//             格式一改就靜默壞掉（而失敗紀錄壞掉的樣子跟「沒有失敗」一模一樣）。
// 數值影響：`<DataRoot>/_cmd_failed/<cmdId>.json`，一檔一筆，**不自動清除** ——
//          它是待處理清單（有人補跑或刪掉才消失），不是快取。
// 設計取捨：
//   · 序列化沿用 `JsonConvert.SaveDataToJson` / `LoadDataFromJson<T>`（同 UCL_AgentCommandTemplateStore）——
//     Queue / History 各自手寫了一份 parser，這裡不再長出第四份。
//   · 補跑**不在本層發生**：本層只記錄與查詢。重跑會重放副作用（酒館公告重發、轉帳重轉），
//     那必須是人按下去的動作 —— 見 `UCL_AgentCommandRunner` 失敗分支的「不自動重試」註解。
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
    // 區塊職責：一筆失敗紀錄的資料模型
    // 物理意義：public 欄位即序列化 schema（UCL_Json 走反射讀寫）。
    //          Args 保存**當時真正送進 handler 的參數**（含 `_cmd_id` 這種系統注入鍵）——
    //          紀錄要忠於現場；要不要在補跑時剔除某些鍵，是補跑那一端的決定。
    // 數值影響：純資料容器，不做 IO。
    // ===========================================================
    /// <summary>
    /// 一筆失敗 Cmd 的可補跑紀錄（對應 _cmd_failed/&lt;Id&gt;.json）。
    /// </summary>
    [Serializable]
    public class UCL_AgentCommandFailedEntry
    {
        /// <summary>失敗當時的 cmd id（＝檔名，也是 `_cmd_errors/&lt;Id&gt;.md` 的鍵）</summary>
        public string Id;
        /// <summary>Command Type（對應 UCL_AgentCommandRegistry 的 key）</summary>
        public string Type;
        /// <summary>OneShot / Repeatable</summary>
        public UCL_AgentCommandMode Mode = UCL_AgentCommandMode.OneShot;
        /// <summary>失敗當時的完整 args</summary>
        public Dictionary<string, string> Args = new();
        /// <summary>當時的描述（agent / 使用者留的備註）</summary>
        public string Description;
        /// <summary>失敗時間（ISO 8601, UTC）</summary>
        public string FailedAt;
        /// <summary>例外訊息（一行；完整 stack 在 ErrorReportPath）</summary>
        public string Error;
        /// <summary>跑在哪條 queue（"&lt;persona&gt;" / "anonymous"）。空 = 未記錄。</summary>
        public string QueueId;
        /// <summary>詳細錯誤報告路徑（`_cmd_errors/&lt;Id&gt;.md`）—— 只是指路，不保證檔還在</summary>
        public string ErrorReportPath;

        // ===========================================================
        // 區塊職責：補跑痕跡
        // 物理意義：補跑後**不刪掉本紀錄** —— 補跑本身可能又失敗，刪掉的話「試過了」這件事就消失，
        //          而「沒人補過」跟「補過但又壞了」在畫面上會長得一樣。
        // 數值影響：RetryCount 每次補跑 +1；RetryCmdId 指向最後一次補跑產生的新 cmd id。
        // ===========================================================
        /// <summary>最後一次補跑的時間（ISO 8601）。空 = 還沒補跑過。</summary>
        public string RetriedAt;
        /// <summary>最後一次補跑產生的新 cmd id（可據此去 `_cmd_results/` 查它的下場）</summary>
        public string RetryCmdId;
        /// <summary>累計補跑次數</summary>
        public int RetryCount = 0;
    }

    /// <summary>
    /// 失敗 Cmd 紀錄的讀寫管理。<br/>
    /// 路徑：&lt;DataRoot&gt;/_cmd_failed/&lt;cmdId&gt;.json（一檔一筆）<br/>
    /// 與 `_cmd_results`（3 天後 Purge）／`_cmd_errors`（人可讀 stack）並存，用途不同：
    /// 本store 是**待處理的補跑清單**。
    /// </summary>
    public static class UCL_AgentCommandFailedStore
    {
        public const string FailedDirRelative = "_cmd_failed";

        /// <summary>取得 _cmd_failed 資料夾絕對路徑（不保證存在）。</summary>
        public static string GetFailedDir()
            => Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, FailedDirRelative);

        /// <summary>取得指定 cmd id 對應的紀錄檔路徑。</summary>
        public static string GetEntryPath(string iId)
            => Path.Combine(GetFailedDir(), SanitizeFileName(iId) + ".json");

        /// <summary>確保資料夾存在。</summary>
        public static void EnsureDir()
        {
            string aDir = GetFailedDir();
            if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
        }

        // ===========================================================
        // 區塊職責：失敗當下寫入一筆紀錄（由 Runner 的失敗分支呼叫）
        // 物理意義：同一個 cmd id 只會有一筆（覆寫）—— id 本身就唯一。
        // 數值影響：寫 1 個 json；IO 失敗只印 warning **不 throw** ——
        //          紀錄寫不出來不該讓 Cmd 的失敗處理再爆第二次（那會蓋掉原始例外）。
        // ===========================================================
        /// <summary>把一筆失敗的 cmd 記成可補跑紀錄。回傳寫入的 entry（失敗回 null）。</summary>
        /// <param name="iCmd">失敗的 cmd（Args 會被複製一份，之後 queue 端的變動不影響紀錄）</param>
        /// <param name="iError">例外訊息（一行）</param>
        /// <param name="iQueueId">跑在哪條 queue；空字串／null = 未記錄（**不會被填成 anonymous**）</param>
        public static UCL_AgentCommandFailedEntry Record(UCL_AgentCommand iCmd, string iError, string iQueueId)
        {
            if (iCmd == null || string.IsNullOrEmpty(iCmd.Id)) return null;
            try
            {
                EnsureDir();
                var aEntry = new UCL_AgentCommandFailedEntry
                {
                    Id = iCmd.Id,
                    Type = iCmd.Type,
                    Mode = iCmd.Mode,
                    Args = iCmd.Args != null
                        ? new Dictionary<string, string>(iCmd.Args)
                        : new Dictionary<string, string>(),
                    Description = iCmd.Description,
                    FailedAt = DateTime.UtcNow.ToString("o"),
                    Error = iError,
                    QueueId = string.IsNullOrEmpty(iQueueId) ? null : iQueueId,
                    ErrorReportPath = Path.Combine(
                        UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "_cmd_errors", $"{iCmd.Id}.md"),
                };
                Write(aEntry);
                return aEntry;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_AgentCommandFailedStore] 失敗紀錄寫不出來（不影響原本的失敗處理）：{ex.Message}");
                return null;
            }
        }

        /// <summary>讀取單筆；找不到 / 壞檔 → null。</summary>
        public static UCL_AgentCommandFailedEntry Load(string iId)
        {
            string aPath = GetEntryPath(iId);
            if (!File.Exists(aPath)) return null;
            return LoadFile(aPath);
        }

        /// <summary>載入全部，依 FailedAt 由新到舊。壞檔跳過並印 warning，不中斷整批。</summary>
        public static List<UCL_AgentCommandFailedEntry> LoadAll()
        {
            var aList = new List<UCL_AgentCommandFailedEntry>();
            string aDir = GetFailedDir();
            if (!Directory.Exists(aDir)) return aList;
            foreach (string aPath in Directory.EnumerateFiles(aDir, "*.json"))
            {
                var aEntry = LoadFile(aPath);
                if (aEntry != null) aList.Add(aEntry);
            }
            aList.Sort((a, b) => string.Compare(b.FailedAt ?? "", a.FailedAt ?? "", StringComparison.Ordinal));
            return aList;
        }

        /// <summary>刪掉一筆（＝這筆處理完了 / 不打算補）。</summary>
        public static bool Delete(string iId)
        {
            string aPath = GetEntryPath(iId);
            if (!File.Exists(aPath)) return false;
            try { File.Delete(aPath); return true; }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandFailedStore] 刪除 '{iId}' 失敗：{ex.Message}");
                return false;
            }
        }

        /// <summary>清空整個清單，回傳實際刪掉幾筆。</summary>
        public static int DeleteAll()
        {
            string aDir = GetFailedDir();
            if (!Directory.Exists(aDir)) return 0;
            int aCount = 0;
            foreach (string aPath in Directory.EnumerateFiles(aDir, "*.json").ToList())
            {
                try { File.Delete(aPath); aCount++; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UCL_AgentCommandFailedStore] 跳過刪不掉的 {aPath}：{ex.Message}");
                }
            }
            return aCount;
        }

        // ===========================================================
        // 區塊職責：記下「這筆被補跑過」
        // 物理意義：補跑產生的是**新的 cmd id**，它的成敗會落在新 id 的 result / failed 紀錄上。
        //          本欄位是兩者之間唯一的連線 —— 沒有它就無法回答「這筆到底補成了沒」。
        // 數值影響：更新既有檔（RetriedAt / RetryCmdId / RetryCount）；找不到原紀錄則 no-op。
        // ===========================================================
        public static bool MarkRetried(string iId, string iNewCmdId)
        {
            var aEntry = Load(iId);
            if (aEntry == null) return false;
            aEntry.RetriedAt = DateTime.UtcNow.ToString("o");
            aEntry.RetryCmdId = iNewCmdId;
            aEntry.RetryCount += 1;
            Write(aEntry);
            return true;
        }

        // ===========================================================
        // 區塊職責：數出「有失敗報告但沒有結構化紀錄」的舊筆數
        // 物理意義：`_cmd_errors/<id>.md` 是永久保存的失敗痕跡（不像 `_cmd_results` 會被 Purge），
        //          所以它的檔數＝歷史上失敗過幾次。本 store 是 2026-08-21 才加的 ⇒
        //          之前的失敗**沒有 Args 的結構化紀錄，補跑不了**。
        //          ⛔ 這個數字存在的理由是**別把「不能補」畫成「沒有失敗」** ——
        //          畫面上少一筆跟不存在長得一樣，那正是這個系統最貴的失敗形狀。
        // 數值影響：只列目錄 + 檔案存在檢查（不讀檔內容）；呼叫端請自行 throttle（別放進 Draw）。
        // ===========================================================
        /// <summary>數出 `_cmd_errors/` 裡沒有對應結構化紀錄的失敗（＝無法補跑的舊筆數）。</summary>
        public static int CountReportsWithoutRecord()
        {
            try
            {
                string aErrDir = Path.Combine(
                    UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "_cmd_errors");
                if (!Directory.Exists(aErrDir)) return 0;
                int aCount = 0;
                foreach (string aPath in Directory.EnumerateFiles(aErrDir, "*.md"))
                {
                    string aId = Path.GetFileNameWithoutExtension(aPath);
                    if (!File.Exists(GetEntryPath(aId))) aCount++;
                }
                return aCount;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_AgentCommandFailedStore] 數舊失敗報告時出錯：{ex.Message}");
                return 0;
            }
        }

        // ===========================================================
        // 區塊職責：JSON 讀寫（沿用 JsonConvert，不自寫 parser）
        // 物理意義：`SaveDataToJson` 支援 Dictionary 與 enum-as-string（Queue / History 當年自寫
        //          parser 的理由是 Unity JsonUtility 不支援 Dictionary —— 而 UCL 自己的 JsonLib 支援）。
        // 數值影響：UTF-8 無 BOM（python 端直接 json.load 不會被 BOM 咬）。
        // ===========================================================
        static void Write(UCL_AgentCommandFailedEntry iEntry)
        {
            EnsureDir();
            string aPath = GetEntryPath(iEntry.Id);
            try
            {
                JsonData aData = JsonConvert.SaveDataToJson(iEntry);
                File.WriteAllText(aPath, aData.ToJsonBeautify(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UCL_AgentCommandFailedStore] 寫入 {aPath} 失敗：{ex}");
            }
        }

        static UCL_AgentCommandFailedEntry LoadFile(string iPath)
        {
            try
            {
                string aRaw = File.ReadAllText(iPath, Encoding.UTF8);
                var aEntry = JsonConvert.LoadDataFromJson<UCL_AgentCommandFailedEntry>(JsonData.ParseJson(aRaw));
                if (aEntry == null) return null;
                // 檔名即 id —— 內容缺 Id 的壞檔用檔名補回來，否則刪除／補跑都定位不到它
                if (string.IsNullOrEmpty(aEntry.Id)) aEntry.Id = Path.GetFileNameWithoutExtension(iPath);
                return aEntry;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UCL_AgentCommandFailedStore] 跳過壞檔 {iPath}：{ex.Message}");
                return null;
            }
        }

        static string SanitizeFileName(string iName)
        {
            if (string.IsNullOrEmpty(iName)) return "unnamed";
            var aInvalid = Path.GetInvalidFileNameChars();
            var aSb = new StringBuilder(iName.Length);
            foreach (char c in iName)
            {
                aSb.Append(Array.IndexOf(aInvalid, c) >= 0 ? '_' : c);
            }
            return aSb.ToString();
        }
    }
}
#endif
