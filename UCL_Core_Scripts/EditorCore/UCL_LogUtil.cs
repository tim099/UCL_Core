// UCL_LogUtil — runtime LogError / LogWarning / LogException 自動寫盤工具（Editor-only）
// 物理意義：hook Application.logMessageReceived → 雙寫主 log（含 Warning）+ 錯誤獨立 log（Error/Exception）
// 數值影響：每次 Editor session 起手清空 Errors_latest.log，per-session 備份 Errors_HH_MM_SS.log
// 遷移歷史（T31 Round 32）：
//   - 原 RCG_Utils/LogUtil.cs（namespace Util）遷入 UCL_Core
//   - rename LogUtil → UCL_LogUtil 對齊 UCL_ 命名慣例
//   - rename ExtraInfoScope → UCL_LogExtraInfoScope
//   - EnableLog default 改成 true（解 T25 「沒人設 PlayerPrefs key 全 session 沒落盤」根因）
//   - 加 Editor menu Tools/UCL/Toggle UCL_LogUtil.EnableLog 給 Tim 開關
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// Provides a scope for temporarily setting additional logging information. When disposed, restores the previous
    /// logging context if it has not changed.
    /// </summary>
    /// <remarks>Use this class to associate extra information with log entries within a specific scope. The
    /// extra information is automatically cleared when the scope is disposed, ensuring that subsequent log entries are
    /// not affected. This class is intended to be used with a using statement to ensure proper cleanup.</remarks>
    public class UCL_LogExtraInfoScope : System.IDisposable
    {
        private readonly string _info;
        private static Stack<string> s_infoStack = new();

        public UCL_LogExtraInfoScope(string info)
        {
            _info = info;
            s_infoStack.Push(info);
            UCL_LogUtil.ExtraInfo = s_infoStack.ConcatToString();
        }
        public void Dispose()
        {
            s_infoStack.Pop();
            UCL_LogUtil.ExtraInfo = s_infoStack.ConcatToString();
        }
    }

    /// <summary>
    /// 靜態日誌工具，把 runtime LogError / LogWarning / Exception 持久化到本地檔案。
    /// 跟 UCL_Core 的 .compile_status.json（compile-time errors）形成 runtime 對應 — agent diagnose 真 bug 看 Errors_latest.log。
    /// </summary>
    public static class UCL_LogUtil
    {
        const string PlayerPrefsKey = "UCL_LogUtil";   // 跟舊版 PlayerPrefs key "LogUtil" 不重複，乾淨遷移
        const string MenuPath = "Tools/UCL/Toggle UCL_LogUtil.EnableLog";

        /// <summary>
        /// 控制是否啟用日誌輸出（PlayerPrefs 持久化）；T31 改為 default=true 確保首次運行就落盤。
        /// </summary>
        public static bool EnableLog
        {
            // T31 — default "True" 解 T25 觀察到的「沒人設 key 全 session 沒落盤」根因
            get => PlayerPrefs.GetString(PlayerPrefsKey, "True") == "True";
            set => PlayerPrefs.SetString(PlayerPrefsKey, value ? "True" : "False");
        }

        /// <summary>
        /// 用來記錄觸發時的額外資訊（可附加在每條日誌內方便追蹤上下文，目前不會自動加上）。
        /// </summary>
        public static string ExtraInfo;

        public static string AppendExtraInfo(string message)
        {
            if (string.IsNullOrEmpty(ExtraInfo)) return message;
            return $"[{ExtraInfo}]\n {message}";
        }

        private static ulong s_LogIndex = 0;
        private static string s_LogPath;
        // 區塊職責：Error 獨立輸出檔（agent 讀錯方便用）
        // 物理意義：Simulation_<ts>.log 混雜 Warning/Error/Exception 三種；agent 想單獨拉錯要 grep。
        //          多寫一份 Errors_<ts>.log（per-session）+ Errors_latest.log（固定檔名，每次 Init 清空）
        //          → agent 直接讀 Errors_latest.log 就能拿到「當前 session 的所有錯誤」，與
        //          UCL_Core 的 .compile_status.json（編譯期錯誤）形成 runtime 對應檔案。
        // 數值影響：只在 type==Error 或 Exception 時雙寫；不影響主 log 內容。
        private static string s_ErrorLogPath;
        private static string s_ErrorLatestPath;
        public const string ErrorLatestFileName = "Errors_latest.log";
        private static bool s_IsInitialized = false;

        [InitializeOnLoadMethod]
        public static void Init()
        {
            if (s_IsInitialized) return;
            s_IsInitialized = true;
            // T31 — 不再強制把 default 寫成 "False"；EnableLog property 已 default true（PlayerPrefs.GetString 第二參數）
            InitLog();
            try
            {
                Debug.Log($"[UCL_LogUtil] 初始化完成，Log 路徑: {s_LogPath} / EnableLog={EnableLog}");
                // 註冊日誌回調事件，自動擷取 Error、Exception 與 Warning
                Application.logMessageReceived += HandleUnityLog;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[UCL_LogUtil] 初始化失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新創建輸出檔案。
        /// </summary>
        public static void InitLog()
        {
            // 區塊職責：log 輸出夾 —— 名稱結尾的 `~` 是**功能性的，不是命名風格**。
            // 物理意義：Unity 對 Assets/ 底下結尾為 `~` 的資料夾完全不做 asset import ——
            //          不掃描、不建 .meta、不進 AssetDatabase。
            // 為什麼要這樣（2026-08-01 Tim 回報 Editor 全域卡頓，實測根因之一）：
            //          原本輸出到 Assets/DebugLogs/，累積到 **876 個檔 / 27MB**，
            //          而且每寫一次 log，Editor.log 就出現一次
            //          `Start importing Assets/DebugLogs/Simulation_*.log (DefaultImporter)` ——
            //          純文字 log 對遊戲毫無用處，卻讓每次 asset refresh 都得掃過它們、
            //          還各自產生一個 .meta 要追蹤。低 CPU + 高磁碟的卡頓有一份是它貢獻的。
            // ⚠ 改名時必須同步的消費端：AgentCommands/Tools/debuglog_query.py 的候選路徑表。
            //   那支是 agent 讀 log 的唯一入口，漏改就會變成「查不到 = 以為沒有錯誤」——
            //   最糟的失效方式（安靜且看起來像好消息）。
            string folder = Path.Combine(Application.dataPath, "DebugLogs~");
            Directory.CreateDirectory(folder);
            s_LogPath = Path.Combine(folder, $"Simulation_{System.DateTime.Now:HH_mm_ss}.log");

            // 區塊職責：建立 Error 獨立輸出檔
            // 物理意義：每次 Init（每個 Editor session 起手）都重建一份 per-session Errors_<ts>.log
            //          + 一份固定檔名 Errors_latest.log（先清空 → 之後 append）。
            //          agent 永遠讀 Errors_latest.log 即可拿到「當前 session 的錯誤」。
            // 數值影響：兩個檔案在此被建立 / 清空；之後 HandleUnityLog / Log(Error) 會雙寫進去。
            s_ErrorLogPath = Path.Combine(folder, $"Errors_{System.DateTime.Now:HH_mm_ss}.log");
            s_ErrorLatestPath = Path.Combine(folder, ErrorLatestFileName);
            try
            {
                string header = $"--- [Errors_latest.log — session started {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] ---\n" +
                                $"--- [per-session backup at: {Path.GetFileName(s_ErrorLogPath)}] ---\n\n";
                File.WriteAllText(s_ErrorLatestPath, header, new System.Text.UTF8Encoding(false));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UCL_LogUtil] 初始化 Errors_latest.log 失敗：{ex.Message}");
            }
        }

        private static void HandleUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Warning)
            {
                string tag = type == LogType.Exception ? "EXCEPTION" : type.ToString().ToUpper();
                string message = $"{++s_LogIndex}.[{tag}][{System.DateTime.Now:HH:mm:ss}] {condition}";

                // 只有 Error 和 Exception 才附加 StackTrace，節省空間
                if (type == LogType.Error || type == LogType.Exception)
                {
                    if (!string.IsNullOrEmpty(stackTrace))
                    {
                        message += $"\n[StackTrace]\n{stackTrace}";
                    }
                }

                AppendToFile(message);

                // 區塊職責：Error / Exception 雙寫到獨立 Error log
                // 物理意義：跟主 log 同一筆訊息（含 StackTrace），但只挑 Error 和 Exception；
                //          Warning 不雙寫（避免 noise，agent 主要關心 Error/Exception）
                if (type == LogType.Error || type == LogType.Exception)
                {
                    AppendToErrorFile(message);
                }
            }
        }

        public static void Log(string message)
        {
            Log(message, LogType.Log);
        }

        public static void LogWarning(string message)
        {
            Log(message, LogType.Warning);
        }

        public static void LogError(string message)
        {
            Log(message, LogType.Error);
        }

        /// <summary>
        /// 根據指定的 LogType 調用對應的日誌記錄方法。
        /// </summary>
        /// <param name="message">日誌訊息 (string): 要記錄的文字內容</param>
        /// <param name="level">日誌層級 (LogType): 決定要使用哪種 Unity Log 方法</param>
        public static void Log(string message, LogType level)
        {
            string formatted = $"{++s_LogIndex}.[{level}][{System.DateTime.Now:HH:mm:ss}] {message}";
            AppendToFile(formatted);

            // 區塊職責：手動 LogError 也雙寫到 Error log（與 HandleUnityLog 路徑一致）
            // 物理意義：手動呼叫 UCL_LogUtil.LogError 不走 Application.logMessageReceived → 不會自動觸發
            //           HandleUnityLog 的雙寫；這裡補一份，讓兩個入口收斂到同樣行為
            // 數值影響：手動 LogError 內容不附 StackTrace（手動 caller 沒 stack 資訊），這是預期取捨
            if (level == LogType.Error || level == LogType.Exception)
            {
                AppendToErrorFile(formatted);
            }
        }

        private static void AppendToFile(string content)
        {
            if (!EnableLog) return;
            if (string.IsNullOrEmpty(s_LogPath)) return;
            try
            {
                File.AppendAllText(s_LogPath, content + "\n");
            }
            catch { }
        }

        // 區塊職責：把訊息雙寫到 Errors_<ts>.log 與 Errors_latest.log
        // 物理意義：per-session 永久檔（HH_mm_ss 為 session 起手時間）+ 固定檔名（每次 Init 清空）
        //           兩份內容一致，差異只在保留週期：per-session 跟著 Simulation_*.log 一起保留以供事後查；
        //           latest 給 agent 即時讀
        // 數值影響：兩個檔案各 append 一次；EnableLog 為 false 時整體不寫
        private static void AppendToErrorFile(string content)
        {
            if (!EnableLog) return;
            if (!string.IsNullOrEmpty(s_ErrorLogPath))
            {
                try { File.AppendAllText(s_ErrorLogPath, content + "\n\n"); } catch { }
            }
            if (!string.IsNullOrEmpty(s_ErrorLatestPath))
            {
                try { File.AppendAllText(s_ErrorLatestPath, content + "\n\n"); } catch { }
            }
        }

        /// <summary>
        /// 將訊息記錄到指定路徑的檔案中，並自動處理目錄創建。
        /// </summary>
        /// <param name="path">目標檔案路徑 (string): 檔案在硬碟中的儲存位置</param>
        /// <param name="message">日誌訊息 (string): 要寫入檔案的文字內容</param>
        /// <param name="level">日誌層級 (LogType): 標記這條訊息的嚴重程度</param>
        public static void LogToPath(string path, string message, LogType level)
        {
            if (!EnableLog) return;
            // [區塊定義]：日誌路徑預處理與資料夾初始化
            // [職責說明]：檢查並確保目標檔案所在的目錄路徑完整存在。
            // [物理意義]：透過檔案系統 API 驗證路徑合法性，防止因目錄缺失導致的寫入 Stream 崩潰。
            // [數值影響]：若目錄不存在，則會在硬碟中創建對應的節點，涉及 I/O 操作。

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // [區塊定義]：訊息內容封裝與格式化
            string timeStamp = System.DateTime.Now.ToString("HH:mm:ss");
            string levelTag = level.ToString().ToUpper();
            string formattedMessage = $"{s_LogIndex}.[{levelTag}][{timeStamp}] {message}";

            try
            {
                File.AppendAllText(path, formattedMessage + "\n");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[UCL_LogUtil] LogToPath 寫入失敗! 目標路徑: {path}, 錯誤訊息: {ex.Message}");
            }
        }

        // ===========================================================
        // T31 — Editor menu toggle（per Tim Round 32 拍板）
        // 物理意義：給 Tim Editor 內一鍵切 EnableLog；checked 狀態反映當前值
        // 數值影響：呼叫 EnableLog setter → 寫 PlayerPrefs；下次 Editor session 起手沿用
        // ===========================================================
        [MenuItem(MenuPath, priority = 200)]
        private static void ToggleEnableLog()
        {
            EnableLog = !EnableLog;
            Debug.Log($"[UCL_LogUtil] EnableLog → {EnableLog}");
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleEnableLogValidate()
        {
            Menu.SetChecked(MenuPath, EnableLog);
            return true;   // 永遠可點
        }
    }
}
#endif
