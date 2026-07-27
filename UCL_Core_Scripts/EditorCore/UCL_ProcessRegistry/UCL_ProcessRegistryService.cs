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
