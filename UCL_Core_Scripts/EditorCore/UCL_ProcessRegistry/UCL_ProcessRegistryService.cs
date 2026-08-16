// 區塊職責: Editor 端 child process 註冊中心 (static Service) — C# 開的每顆外部 Process 都在此登記,
//            以「每 process 一個 json 檔」持久化, domain reload / recompile 後仍能接管既有 process。
// 物理意義: 解「多顆 .py daemon 併跑互踩 / recompile 後失去 Process 物件變孤兒 / 光憑 PID 誤殺別人」三族問題
//            (2026-07-27 Tim 拍板: 短時間重複 OCR 疑似多 daemon 事件的基建回應)。
// 設計取捨:
//   - 身分 = PID + process name + start time (UTC) 三重比對 — PID 會被 OS 回收再發,
//     光 PID 不足以斷定同一顆; start time 是 kernel 記的, 同 PID 不同世代必不同。
//   - 每 process 單檔 (<tag>_<pid>.json) 而非集中一檔 — 避免併發寫互蓋, 且單檔壞不連坐。
//   - 檔案落主專案 AgentCommands/_process_registry/ (runtime 狀態, per-project, 不入 UCL_Core repo)。
// 2026-07-27 summit — Tim 規格: 「不能單純只記錄PID 還要有能判斷Process在做什麼 ... 不能誤關」
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// 已註冊 process 的身分驗證結果。
    /// Alive = PID 活著且 name/start_time 都吻合 (確定是本尊);
    /// Dead = PID 不存在或已退出;
    /// PidReused = PID 活著但 name 或 start_time 不吻合 (被 OS 回收再發給別的 process — 絕不可 kill);
    /// Unknown = 無法取得對方資訊 (權限不足等) — 保守處理, 同樣不可 kill。
    /// </summary>
    public enum UCL_ProcessStatus { Alive, Dead, PidReused, Unknown }

    /// <summary>
    /// 單顆已註冊 process 的持久化記錄 (對應 _process_registry/&lt;tag&gt;_&lt;pid&gt;.json)。
    /// </summary>
    public class UCL_ProcessRecord
    {
        public int pid;
        public string process_name = "";
        /// <summary>process 啟動時間 (UTC, ISO-8601 round-trip) — PID 再利用判定的關鍵身分欄。</summary>
        public string start_time_utc = "";
        /// <summary>這顆 process 在做什麼 (穩定識別字, e.g. "screenstream_daemon") — 檔名前綴。</summary>
        public string tag = "";
        public string description = "";
        public string command_line = "";
        public string registered_by = "";
        public string registered_at_utc = "";
        /// <summary>來源記錄檔絕對路徑 (載入時回填, 不序列化)。</summary>
        [NonSerialized] public string source_file = "";

        public DateTime? StartTimeUtc
        {
            get
            {
                if (DateTime.TryParse(start_time_utc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var t))
                    return t.ToUniversalTime();
                return null;
            }
        }

        public JsonData ToJson()
        {
            var d = new JsonData();
            d["pid"] = new JsonData(pid);
            d["process_name"] = new JsonData(process_name);
            d["start_time_utc"] = new JsonData(start_time_utc);
            d["tag"] = new JsonData(tag);
            d["description"] = new JsonData(description);
            d["command_line"] = new JsonData(command_line);
            d["registered_by"] = new JsonData(registered_by);
            d["registered_at_utc"] = new JsonData(registered_at_utc);
            d["schema_version"] = new JsonData(1);
            return d;
        }

        public static UCL_ProcessRecord FromJson(JsonData d)
        {
            if (d == null) return null;
            return new UCL_ProcessRecord
            {
                pid = d.GetInt("pid", 0),
                process_name = d.GetString("process_name", ""),
                start_time_utc = d.GetString("start_time_utc", ""),
                tag = d.GetString("tag", ""),
                description = d.GetString("description", ""),
                command_line = d.GetString("command_line", ""),
                registered_by = d.GetString("registered_by", ""),
                registered_at_utc = d.GetString("registered_at_utc", ""),
            };
        }
    }

    /// <summary>
    /// Child process 註冊中心 (static Service) — C# 端開啟外部 Process 後必須經此註冊,
    /// 以檔案持久化撐過 domain reload; kill 前做 PID+name+start_time 三重身分驗證防誤殺。
    /// UI 入口: UCL_ProcessAdminPage (Process 管理頁)。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ProcessAdminPage.md")]
    // 區塊職責：Editor 載入時自動清一次 Dead / PidReused 殘留記錄。
    // 物理意義：**fire-and-forget 型的 spawn 沒有 `finally` 可以放 `Unregister`**
    //          （`Process.Start` 完就 return，沒人等它），所以它們的記錄只能靠事後清。
    //          2026-08-06 全面登記之前，這件事沒有自動觸發點 —— `CleanupStale` 只有
    //          `UCL_ProcessAdminPage` 的手動按鈕會呼叫。那樣的話「全部登記」會把
    //          「沒有屍潮」換成「殘檔堆積」，而堆積出來的畫面跟屍潮長得一樣，
    //          一樣會訓練人忽略那張表。
    // 數值影響：只刪 Dead / PidReused 的**記錄檔**，絕不碰任何活著的 process
    //          （PidReused 那顆 PID 已易主，是別人的，只清記錄）。
    // 設計取捨：掛 InitializeOnLoad 而不是掛在 `Register` 裡 —— 後者會讓每次 spawn 都多一次
    //          全表掃描（Tim 2026-08-06 拍板）。domain reload 每次編譯都發生，頻率已足夠。
    [UnityEditor.InitializeOnLoad]
    static class UCL_ProcessRegistryAutoCleanup
    {
        static UCL_ProcessRegistryAutoCleanup()
        {
            // 例外一律吞掉但留 log —— 清理失敗不該擋住 Editor 啟動，
            // 但**靜默失敗**會讓殘檔無聲累積，那正是這個機制要解的問題。
            try
            {
                int n = UCL_ProcessRegistryService.CleanupStale();
                if (n > 0) Debug.Log($"[ProcessRegistry] 啟動清理：移除 {n} 筆已死的 process 記錄");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ProcessRegistry] 啟動清理失敗（殘檔會留到下次或手動清）: {e.Message}");
            }
        }
    }

    public static class UCL_ProcessRegistryService
    {
        const string REGISTRY_DIR_RELATIVE = "AgentCommands/_process_registry";
        /// <summary>start_time 比對容差 (秒) — Process.StartTime 與記錄值間的時鐘/精度緩衝。</summary>
        const double START_TIME_TOLERANCE_SEC = 2.0;

        public static string RegistryDir => Path.Combine(UCL_RepoPath.RepoRoot, REGISTRY_DIR_RELATIVE);

        // ===========================================================
        // Register / Unregister
        // ===========================================================
        /// <summary>
        /// 註冊一顆剛由 C# 啟動的 process。擷取 PID / name / start time / cmdline 寫成單檔記錄。
        /// process 已退出或資訊取不到時回 null (fail-soft, 不炸 caller 的 spawn 流程)。
        /// allowMultiple=false (預設, Tim 2026-07-27 拍板) = singleton 模式: 註冊時先 kill 掉
        /// 所有既存同 tag process (身分驗證通過的才動手), 確保同功能同時只有一顆。
        /// 註: 新 process 此時尚未寫記錄檔, 不會誤殺自己; 若要「舊的先死新的才生」的嚴格順序,
        /// 請在 spawn 前自行呼叫 KillAllByTag (如 UCL_ScreenStreamDaemon 的 pre-spawn guard)。
        /// </summary>
        /// <summary>
        /// 區塊職責：fire-and-forget 型 spawn 的唯一入口（開檔案總管 / 用預設程式開檔 / 短命 CLI）。
        /// 物理意義：這類 spawn **沒有 `finally` 可以放 `Unregister`** —— 呼叫端不等它。
        ///          所以登記之後靠 <see cref="CleanupStale"/> 回收（Editor 載入時自動跑一次）。
        ///          `allowMultiple: true` 是刻意的：使用者可以同時開好幾個檔案總管，
        ///          這裡**不是** singleton 語意，套 KillAllByTag 會把上一個關掉。
        /// 數值影響：登記失敗（回 null）不影響 process 本身 —— 那顆已經在跑了。
        /// 邊界：`UseShellExecute = true` 時 `Process.Start` **可能回 null**（外殼把請求交給既存的
        ///      應用程式實例，沒有新 process 可回）。那不是錯誤，只是沒東西可登記。
        /// </summary>
        /// <summary>
        /// 區塊職責：等待型 spawn 的登記 + 自動反登記（`using` scope）。
        /// 物理意義：`Register` / `Unregister` 必須成對，而**成對最常見的破法是例外路徑**——
        ///          手寫 try/finally 時很容易只寫在正常路徑上，留下一筆已死的 PID 記錄。
        ///          包成 IDisposable 之後，正常結束與丟例外都會反登記，成對性由語言保證而不是靠人記得。
        /// 用法（C# 8 using 宣告，不必動既有括號結構）：
        /// <code>
        /// p.Start();
        /// using var _ = UCL_ProcessRegistryService.RegisterScope(p, TAG, "在做什麼", nameof(MyPage));
        /// // …既有的 ReadToEnd / WaitForExit(timeout) 照舊…
        /// </code>
        /// </summary>
        public static IDisposable RegisterScope(System.Diagnostics.Process proc, string tag,
            string description = "", string registeredBy = "", bool allowMultiple = false)
            => new ProcRegScope(proc, tag, description, registeredBy, allowMultiple);

        sealed class ProcRegScope : IDisposable
        {
            readonly int m_Pid;
            readonly string m_Tag;
            public ProcRegScope(System.Diagnostics.Process proc, string tag, string desc,
                string by, bool allowMultiple)
            {
                m_Tag = tag;
                m_Pid = -1;
                try
                {
                    if (Register(proc, tag, desc, by, allowMultiple) != null) m_Pid = proc.Id;
                }
                catch (Exception e)
                {
                    // 登記失敗不擋工作本身（process 已經在跑了），但**不可靜默** ——
                    // 沒登記成功卻沒人知道，那顆就是沒人管得到的孤兒。
                    Debug.LogWarning($"[ProcessRegistry] 登記失敗（tag={tag}，該顆將無法被接管）: {e.Message}");
                }
            }
            public void Dispose()
            {
                if (m_Pid > 0) Unregister(m_Pid, m_Tag);
            }
        }

        public static UCL_ProcessRecord StartAndRegister(System.Diagnostics.ProcessStartInfo psi,
            string tag, string description = "", string registeredBy = "")
        {
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;      // 外殼複用既有實例 — 正常情況，不是失敗
            return Register(proc, tag, description, registeredBy, allowMultiple: true);
        }

        public static UCL_ProcessRecord Register(System.Diagnostics.Process proc, string tag,
            string description = "", string registeredBy = "", bool allowMultiple = false)
        {
            if (proc == null || string.IsNullOrEmpty(tag)) return null;
            try
            {
                if (!allowMultiple)
                {
                    int n = KillAllByTag(tag);
                    if (n > 0)
                        Debug.LogWarning($"[UCL_ProcessRegistry] Register({tag}) singleton: 收掉 {n} 顆既存同 tag process");
                }
                var rec = new UCL_ProcessRecord
                {
                    pid = proc.Id,
                    process_name = SafeProcessName(proc),
                    start_time_utc = proc.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    tag = SanitizeTag(tag),
                    description = description ?? "",
                    command_line = BuildCommandLine(proc),
                    registered_by = registeredBy ?? "",
                    registered_at_utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
                Directory.CreateDirectory(RegistryDir);
                string path = RecordPath(rec.tag, rec.pid);
                // atomic 換檔 — 防半寫檔被別的 reload 讀到
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, rec.ToJson().ToJsonBeautify() + "\n");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                rec.source_file = path;
                return rec;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ProcessRegistry] register fail (tag={tag}): {e.Message}");
                return null;
            }
        }

        /// <summary>移除記錄檔 (process 已由 caller 正常收掉時呼叫)。tag 為 null 時只按 pid 找。</summary>
        public static void Unregister(int pid, string tag = null)
        {
            try
            {
                if (!Directory.Exists(RegistryDir)) return;
                if (!string.IsNullOrEmpty(tag))
                {
                    string path = RecordPath(SanitizeTag(tag), pid);
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                foreach (var f in Directory.GetFiles(RegistryDir, $"*_{pid}.json"))
                {
                    var rec = LoadRecord(f);
                    if (rec != null && rec.pid == pid) File.Delete(f);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ProcessRegistry] unregister fail (pid={pid}): {e.Message}");
            }
        }

        // ===========================================================
        // Query / Validate
        // ===========================================================
        /// <summary>載入全部記錄 (含 source_file 回填)。壞檔跳過不連坐。</summary>
        public static List<UCL_ProcessRecord> LoadAll()
        {
            var list = new List<UCL_ProcessRecord>();
            try
            {
                if (!Directory.Exists(RegistryDir)) return list;
                foreach (var f in Directory.GetFiles(RegistryDir, "*.json"))
                {
                    var rec = LoadRecord(f);
                    if (rec != null) list.Add(rec);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ProcessRegistry] load all fail: {e.Message}");
            }
            list.Sort((a, b) => string.CompareOrdinal(a.tag, b.tag) != 0
                ? string.CompareOrdinal(a.tag, b.tag) : a.pid.CompareTo(b.pid));
            return list;
        }

        /// <summary>
        /// 一次拿全部記錄 + 即時身分狀態 — 查詢端標準入口 (對偶 python load_all_with_status)。
        /// status 是「呼叫當下」的即時驗證結果, 每次查都重新對 OS 比身分。
        /// </summary>
        public static List<(UCL_ProcessRecord rec, UCL_ProcessStatus status)> LoadAllWithStatus()
        {
            var list = new List<(UCL_ProcessRecord, UCL_ProcessStatus)>();
            foreach (var rec in LoadAll()) list.Add((rec, Validate(rec)));
            return list;
        }

        /// <summary>
        /// 身分驗證 — 記錄 vs 當前 OS process 狀態。
        /// 物理意義: PID 會被 OS 回收再發; 只有 name + start_time 都吻合才認定是「當初註冊的那顆」。
        /// </summary>
        public static UCL_ProcessStatus Validate(UCL_ProcessRecord rec)
        {
            if (rec == null || rec.pid <= 0) return UCL_ProcessStatus.Unknown;
            System.Diagnostics.Process p;
            try { p = System.Diagnostics.Process.GetProcessById(rec.pid); }
            catch (ArgumentException) { return UCL_ProcessStatus.Dead; }   // 無此 PID
            catch (Exception) { return UCL_ProcessStatus.Unknown; }
            try
            {
                using (p)
                {
                    if (p.HasExited) return UCL_ProcessStatus.Dead;
                    if (!string.IsNullOrEmpty(rec.process_name) &&
                        !string.Equals(p.ProcessName, rec.process_name, StringComparison.OrdinalIgnoreCase))
                        return UCL_ProcessStatus.PidReused;
                    var recorded = rec.StartTimeUtc;
                    if (recorded.HasValue)
                    {
                        double diff = Math.Abs((p.StartTime.ToUniversalTime() - recorded.Value).TotalSeconds);
                        if (diff > START_TIME_TOLERANCE_SEC) return UCL_ProcessStatus.PidReused;
                    }
                    return UCL_ProcessStatus.Alive;
                }
            }
            catch (Exception)
            {
                // 拿不到對方資訊 (權限/剛退出 race) — 保守回 Unknown, kill 端會拒絕動手
                return UCL_ProcessStatus.Unknown;
            }
        }

        // ===========================================================
        // Kill / Cleanup
        // ===========================================================
        /// <summary>
        /// 身分驗證通過才 kill (Alive 以外一律拒絕 — PidReused 誤殺是本 Service 存在的理由)。
        /// kill 成功後順手移除記錄檔。
        /// </summary>
        public static bool KillRegistered(UCL_ProcessRecord rec, out string error)
        {
            error = null;
            var status = Validate(rec);
            if (status != UCL_ProcessStatus.Alive)
            {
                error = status switch
                {
                    UCL_ProcessStatus.Dead => "process 已不存在 (記錄可直接清除)",
                    UCL_ProcessStatus.PidReused => "PID 已被別的 process 佔用 — 拒絕 kill (防誤殺)",
                    _ => "無法驗證 process 身分 — 拒絕 kill (保守)",
                };
                return false;
            }
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(rec.pid);
                // kill 前最後一次身分複驗 (Validate 到 Kill 之間仍有極小 race 窗)
                double diff = rec.StartTimeUtc.HasValue
                    ? Math.Abs((p.StartTime.ToUniversalTime() - rec.StartTimeUtc.Value).TotalSeconds)
                    : 0.0;
                if (diff > START_TIME_TOLERANCE_SEC)
                {
                    error = "kill 前複驗失敗: start time 不吻合 (PID 已易主)";
                    return false;
                }
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (ArgumentException)
            {
                // 已自己退出 — 視為成功收掉
            }
            catch (Exception e)
            {
                error = $"kill fail: {e.Message}";
                return false;
            }
            if (!string.IsNullOrEmpty(rec.source_file))
            {
                try { File.Delete(rec.source_file); } catch { /* 殘檔交給 CleanupStale */ }
            }
            return true;
        }

        /// <summary>
        /// Kill 所有同 tag 的已註冊 process — singleton guard: spawn 前先呼叫,
        /// 確保同一種 daemon 永遠只有一顆 (Tim 2026-07-27: 「每次啟動時先 kill 之前註冊的所有同類」)。
        /// 物理意義: 逐筆走 Validate 身分驗證 — Alive 才 kill (含 kill 前複驗);
        ///          Dead / PidReused 只清記錄檔 (PID 已易主的那顆是別人, 絕不碰);
        ///          Unknown 不 kill 也不清 (保守), 進 skipped 讓 caller 決定要不要人工處理。
        /// 回傳: 實際 kill 掉的數量。skippedReasons: 沒動手的記錄與原因 (可為 null 不收集)。
        /// </summary>
        public static int KillAllByTag(string tag, List<string> skippedReasons = null)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            string wantTag = SanitizeTag(tag);
            int killed = 0;
            foreach (var rec in LoadAll())
            {
                if (!string.Equals(rec.tag, wantTag, StringComparison.OrdinalIgnoreCase)) continue;
                var status = Validate(rec);
                switch (status)
                {
                    case UCL_ProcessStatus.Alive:
                        if (KillRegistered(rec, out string err))
                        {
                            killed++;
                            Debug.Log($"[UCL_ProcessRegistry] KillAllByTag({wantTag}): killed PID {rec.pid}");
                        }
                        else
                        {
                            skippedReasons?.Add($"PID {rec.pid}: {err}");
                            Debug.LogWarning($"[UCL_ProcessRegistry] KillAllByTag({wantTag}) PID {rec.pid} 未動手: {err}");
                        }
                        break;
                    case UCL_ProcessStatus.Dead:
                    case UCL_ProcessStatus.PidReused:
                        // 本尊已不在 (或 PID 易主) — 只清記錄檔, 不碰現任 PID 持有者
                        try
                        {
                            if (!string.IsNullOrEmpty(rec.source_file) && File.Exists(rec.source_file))
                                File.Delete(rec.source_file);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[UCL_ProcessRegistry] KillAllByTag({wantTag}) 清記錄失敗 (PID {rec.pid}): {e.Message}");
                        }
                        break;
                    default:   // Unknown — 驗不了身分, 不 kill 不清, 回報給 caller
                        skippedReasons?.Add($"PID {rec.pid}: 身分無法驗證 (Unknown)");
                        break;
                }
            }
            return killed;
        }

        /// <summary>
        /// Kill 所有 tag **以 prefix 開頭**的已註冊 process —— 給「一族 tag」用的收攤入口。
        /// </summary>
        /// <remarks>
        /// 區塊職責: 當一種工作把 tag 拆成 <c>&lt;family&gt;_&lt;誰&gt;</c> 時，收攤需要掃整族而不是單一 tag。
        /// 物理意義: <see cref="KillAllByTag"/> 是**精確比對**（那是刻意的 —— 精確才不會誤殺別族）；
        ///          拆 tag 換來了隔離，代價就是「一次收乾淨」需要另一個入口。本函式補的正是那個代價。
        /// 🩸 真實案例（basecamp 2026-08-16）：`streamwatch_montage` 原本全場共用，於是後起跑的陪看者
        ///          會經由 Register 的 singleton 語意殺掉別人正在跑的那顆（症狀：exit=-1 且 stderr 全空）。
        ///          改成 <c>streamwatch_montage_&lt;persona&gt;</c> 之後互不干擾，
        ///          **但停播時就再也沒有一個 tag 掃得到全部** ⇒ 需要本函式。
        /// 數值影響: 逐筆走與 KillAllByTag 相同的身分驗證（Alive 才 kill、Dead/PidReused 只清記錄、
        ///          Unknown 保守不動），回傳實際 kill 掉的數量。prefix 為空 → 回 0（**不做全殺**：
        ///          「空字串等於全部」是最容易誤觸的那種預設）。
        /// </remarks>
        public static int KillAllByTagPrefix(string tagPrefix, List<string> skippedReasons = null)
        {
            if (string.IsNullOrEmpty(tagPrefix)) return 0;
            string wantPrefix = SanitizeTag(tagPrefix);
            var aTags = new List<string>();
            foreach (var rec in LoadAll())
            {
                if (rec.tag != null && rec.tag.StartsWith(wantPrefix, StringComparison.OrdinalIgnoreCase)
                    && !aTags.Contains(rec.tag))
                    aTags.Add(rec.tag);
            }
            int killed = 0;
            foreach (var t in aTags) killed += KillAllByTag(t, skippedReasons);
            if (killed > 0)
                Debug.Log($"[UCL_ProcessRegistry] KillAllByTagPrefix({wantPrefix}): 收掉 {killed} 顆（涵蓋 tag：{string.Join(", ", aTags)}）");
            return killed;
        }

        /// <summary>清掉 Dead / PidReused 的殘留記錄檔。回傳清除數。</summary>
        public static int CleanupStale()
        {
            int n = 0;
            foreach (var rec in LoadAll())
            {
                var status = Validate(rec);
                if (status != UCL_ProcessStatus.Dead && status != UCL_ProcessStatus.PidReused) continue;
                try
                {
                    if (!string.IsNullOrEmpty(rec.source_file) && File.Exists(rec.source_file))
                    {
                        File.Delete(rec.source_file);
                        n++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UCL_ProcessRegistry] cleanup fail ({rec.source_file}): {e.Message}");
                }
            }
            return n;
        }

        // ===========================================================
        // Helpers
        // ===========================================================
        static string RecordPath(string tag, int pid) => Path.Combine(RegistryDir, $"{tag}_{pid}.json");

        static UCL_ProcessRecord LoadRecord(string path)
        {
            try
            {
                var rec = UCL_ProcessRecord.FromJson(JsonData.ParseJson(File.ReadAllText(path)));
                if (rec != null) rec.source_file = path;
                return rec;
            }
            catch (Exception)
            {
                return null;   // 壞檔跳過 (可由管理頁手動清)
            }
        }

        static string SafeProcessName(System.Diagnostics.Process p)
        {
            try { return p.ProcessName; }
            catch (Exception) { return ""; }
        }

        static string BuildCommandLine(System.Diagnostics.Process p)
        {
            try
            {
                var si = p.StartInfo;
                if (!string.IsNullOrEmpty(si.FileName))
                    return $"{si.FileName} {si.Arguments}".Trim();
            }
            catch (Exception) { }
            return "";
        }

        static string SanitizeTag(string tag)
        {
            var sb = new System.Text.StringBuilder(tag.Length);
            foreach (var c in tag)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
#endif
