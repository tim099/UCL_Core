// 區塊職責：ScreenStream daemon 子程序生命週期管理 (UCL_Core 版) — [InitializeOnLoad] static class。
//            自 EOV 專案的 RCG_ScreenStreamDaemon 遷移 (Tim 2026-07-26 拍板「相關功能遷移到 UCL_Core」)。
// 物理意義：daemon 存活與 _config.json 的 enabled toggle 同步 (Tim 2026-07-28 拍板「停止錄影 daemon 同步停止」):
//          enabled=true → 每 CHECK_INTERVAL 秒 tick 確認子程序活著, 沒活就 spawn;
//          enabled=false → 有活的就 kill (不再常駐 idle);
//          EditorApplication.quitting 時 graceful kill, 避免 zombie process。
// 設計取捨：
//   - script 遷入 <UCL_Core>/Tools~/AgentCommands/ (跨專案共用)，路徑走 UCL_EditorPath.CorePath 動態解析
//   - daemon 存活綁 config.enabled (2026-07-28 前是常駐 idle 設計) — 停止錄影即收掉, 不留 idle process
//   - 防快速 crash loop: 連續 spawn fail >= MAX_FAST_FAILS → 進 backoff
//   - ⚠ 過渡期守門：偵測到 legacy RCG_ScreenStreamDaemon (EOV 專案端) 仍在 → 本 daemon 讓位不 spawn,
//     避免新舊兩支 python daemon 併寫同一個 frames ring buffer (互蓋 index)。Tim 移除 RCG 版後自動接管。
// 2026-07-26 kaguya (Luna) — 自 RCG_ScreenStreamDaemon (T11 basecamp 2026-05-16) 遷移
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UCL.Core.EditorLib;   // UCL_ProcessRegistryService (Process 註冊中心)
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    /// <summary>
    /// ScreenStream daemon 子程序生命週期管理 (UCL_Core 版) — daemon 存活與 _config.json 的
    /// enabled toggle 同步: 開始錄影 spawn、停止錄影 kill (Tim 2026-07-28, 不再常駐 idle)。
    /// 偵測到 legacy RCG_ScreenStreamDaemon 存在時自動讓位 (防雙 daemon 併寫)。
    /// </summary>
    public static class UCL_ScreenStreamDaemon
    {
        const double CHECK_INTERVAL_SECONDS = 5.0;
        const int MAX_FAST_FAILS = 3;
        const double BACKOFF_SECONDS = 60.0;
        // legacy 專案端 daemon 的 type 全名 — 反射探測用 (EOV: RCG.Editor.RCG_ScreenStreamDaemon)
        const string LEGACY_DAEMON_TYPE = "RCG.Editor.RCG_ScreenStreamDaemon";

        static Process s_DaemonProcess;
        static double s_LastCheckTime = 0;
        static int s_ConsecutiveFails = 0;
        static double s_BackoffUntil = 0;
        static bool s_HookedUp = false;
        static bool s_LegacyDetected = false;   // 讓位旗標 (init 時掃一次, domain reload 會重掃)
        static bool s_LegacyLogged = false;     // 讓位訊息只印一次, 不洗 console

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            if (s_HookedUp) return;
            try
            {
                // 過渡期守門：任一 loaded assembly 內存在 legacy daemon type → 本 daemon 讓位。
                // Tim 確認新版可用、刪除 RCG_ScreenStreamDaemon.cs 後, 下次 domain reload 這裡掃不到 → 自動接管。
                s_LegacyDetected = DetectLegacyDaemon();
                EditorApplication.update += Tick;
                EditorApplication.quitting += OnEditorQuitting;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                s_HookedUp = true;
                Debug.Log(s_LegacyDetected
                    ? "[UCL_ScreenStream] daemon manager loaded — 偵測到 legacy RCG_ScreenStreamDaemon, 本 daemon 讓位待命 (移除 RCG 版後自動接管)"
                    : "[UCL_ScreenStream] daemon manager loaded (waiting for first tick)");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] init fail: {e.Message}");
            }
        }

        // 區塊職責：legacy daemon 反射探測 — 掃 AppDomain 內全部 assembly 找 RCG 版 type
        // 物理意義：RCG 版還在專案裡 = 它的 [InitializeOnLoad] 也會 spawn 一支 python daemon;
        //          兩支同寫 frames ring buffer 會互蓋 index → 本版偵測到就整輪停用 (讓位)。
        static bool DetectLegacyDaemon()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { if (asm.GetType(LEGACY_DAEMON_TYPE) != null) return true; }
                    catch { /* 個別 assembly 掃不了就跳過 */ }
                }
            }
            catch { /* 掃描失敗視為無 legacy (fail-open: 寧可跑也不永久停用) */ }
            return false;
        }

        static void Tick()
        {
            try
            {
                if (s_LegacyDetected)
                {
                    if (!s_LegacyLogged)
                    {
                        s_LegacyLogged = true;   // 只印一次, 之後每 tick 靜默略過
                    }
                    return;   // 讓位中 — 不 spawn、不管理
                }
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastCheckTime < CHECK_INTERVAL_SECONDS) return;
                s_LastCheckTime = now;
                TickInternal(now);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] tick exception: {e.Message}");
            }
        }

        static void TickInternal(double now)
        {
            // (0) 錄影開關同步 (Tim 2026-07-28): enabled=false → daemon 不該活著 — 有活的收掉, 不 spawn。
            //     舊行為是 daemon 常駐 idle 等 toggle; 改為存活直接綁 config.enabled, 停止錄影即同步停止。
            if (!ReadConfigEnabled())
            {
                if (IsProcessAlive(s_DaemonProcess))
                {
                    Debug.Log("[UCL_ScreenStream] 錄影已停止 → 同步收掉 daemon");
                    KillDaemon();
                }
                else if (IsPidFileProcessAlive())
                {
                    // 孤兒場景: domain reload 後 s_DaemonProcess 參考遺失, 但 _daemon.pid 指的 process 還活著
                    // → 走註冊中心按 tag 收掉 (Service 內建身分驗證, PID 易主不誤殺現任持有者)
                    int killed = UCL_ProcessRegistryService.KillAllByTag("screenstream_daemon");
                    if (killed > 0)
                        Debug.Log($"[UCL_ScreenStream] 錄影已停止 → 收掉 {killed} 顆孤兒 daemon (registry sweep)");
                }
                // 未錄影 → 清掉宣稱「正在直播」的殘留檔（Tim 2026-07-30 回報）
                ClearLiveStateFiles();
                return;
            }

            // (1) 子程序仍活 → 啥都不做
            if (IsProcessAlive(s_DaemonProcess)) return;

            // (2) backoff 中 → 跳過本輪
            if (now < s_BackoffUntil) return;

            // (3) try spawn
            bool ok = SpawnDaemon();
            if (ok)
            {
                s_ConsecutiveFails = 0;
            }
            else
            {
                s_ConsecutiveFails++;
                if (s_ConsecutiveFails >= MAX_FAST_FAILS)
                {
                    s_BackoffUntil = now + BACKOFF_SECONDS;
                    Debug.LogWarning($"[UCL_ScreenStream] 連續 spawn 失敗 {s_ConsecutiveFails} 次, 進入 {BACKOFF_SECONDS}s backoff.");
                }
            }
        }

        /// <summary>
        /// screenstream_daemon.py 絕對路徑 — 走 UCL_EditorPath.CorePath 動態解析 UCL_Core 掛載點,
        /// 跨專案安全 (不硬編 AgentCommands/Tools 專案相對路徑; ucl-core-paths 慣例)。
        /// </summary>
        public static string ScriptPath =>
            Path.Combine(UCL_RepoPath.UnityProjectRoot, UCL_EditorPath.CorePath,
                         "Tools~", "AgentCommands", "screenstream_daemon.py").Replace('\\', '/');

        // ===========================================================
        // Spawn — 啟動 python screenstream_daemon.py (UCL_Core Tools~ 版)
        // 物理意義：daemon 啟動後自己 idle 等 _config.json enabled=true 才開始 capture;
        //          所以 spawn 行為跟 toggle 解耦 — Editor 只管 process 存活
        // ===========================================================
        static bool SpawnDaemon()
        {
            string scriptPath = ScriptPath;
            if (!File.Exists(scriptPath))
            {
                Debug.LogWarning($"[UCL_ScreenStream] daemon script not found: {scriptPath}");
                return false;
            }

            string pythonExe = ResolvePython();
            if (string.IsNullOrEmpty(pythonExe))
            {
                Debug.LogWarning("[UCL_ScreenStream] python executable not found in PATH.");
                return false;
            }

            // Singleton guard (Tim 2026-07-27): spawn 前先 kill 之前註冊的所有同 tag process —
            // 防 domain reload kill 失敗殘留 / 任何原因造成的雙 daemon 併寫 frames ring buffer。
            // 身分驗證在 Service 內: PID 已易主 (PidReused) 只清記錄不誤殺現任持有者。
            int prevKilled = UCL_ProcessRegistryService.KillAllByTag("screenstream_daemon");
            if (prevKilled > 0)
                Debug.LogWarning($"[UCL_ScreenStream] singleton guard: 收掉 {prevKilled} 顆殘留 daemon (防併寫)");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,   // daemon 自 repo-walk 算 data root, 這裡對齊 repo 根
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.Log($"[UCL_ScreenStream] {e.Data}");
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.LogWarning($"[UCL_ScreenStream] {e.Data}");
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                s_DaemonProcess = proc;
                // Process 註冊中心 (2026-07-27): 記 PID+name+start_time 身分, 供 UCL_ProcessAdminPage
                // 檢視/防誤殺處置 — recompile 掉 Process 物件後仍可經檔案記錄接管
                UCL_ProcessRegistryService.Register(proc, "screenstream_daemon",
                    "ScreenStream 錄影/STT/OCR daemon (screenstream_daemon.py)", nameof(UCL_ScreenStreamDaemon));
                Debug.Log($"[UCL_ScreenStream] daemon spawned, PID={proc.Id}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] spawn fail: {e.Message}");
                return false;
            }
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        /// <summary>
        /// Page 端 toggle 錄影後呼叫 — 跳過 CHECK_INTERVAL 節流立即同步一次 daemon 存活,
        /// 讓「開始/停止錄影」按下去即刻 spawn/kill, 不必等最多 5 秒的下一 tick。
        /// </summary>
        public static void RequestSyncNow()
        {
            try
            {
                if (s_LegacyDetected) return;   // 讓位中不管理
                s_LastCheckTime = EditorApplication.timeSinceStartup;
                TickInternal(EditorApplication.timeSinceStartup);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] sync-now fail: {e.Message}");
            }
        }

        /// <summary>
        /// 螢幕清單一次性刷新 (Tim 2026-07-28) — spawn `python screenstream_daemon.py --enum-monitors`,
        /// 枚舉螢幕寫 _monitors.json 即退 (不進 main loop、不寫 PID、不註冊 registry)。
        /// 呼叫時機: 開啟 UCL_ScreenStreamPage 時 (daemon 未運行 → 清單可能缺/舊) + 頁面「🔄」鈕 (熱插拔外接螢幕)。
        /// fire-and-forget: 短命 process (~1s), 頁面的 2s reload tick 會自動撿到新 _monitors.json。
        /// </summary>
        public static void EnumerateMonitorsOneShot()
        {
            try
            {
                string scriptPath = ScriptPath;
                if (!File.Exists(scriptPath)) return;
                string pythonExe = ResolvePython();
                if (string.IsNullOrEmpty(pythonExe)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" --enum-monitors",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,
                };
                using var proc = Process.Start(psi);   // 不追蹤、不等待 — 寫完 _monitors.json 自然退出
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] enum-monitors one-shot fail: {e.Message}");
            }
        }

        // 讀 _config.json 的 enabled — daemon 存活的 SOT (檔缺 / 壞檔視為 false: 沒 config 就不該有 daemon)
        static bool ReadConfigEnabled()
        {
            try
            {
                string path = Path.Combine(UCL_RepoPath.RepoRoot, "AgentCommands", "_screenstream", "_config.json");
                if (!File.Exists(path)) return false;
                var data = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path));
                return data != null && data.GetBool("enabled", false);
            }
            catch { return false; }
        }

        // _daemon.pid 指的 process 是否存活 — 偵測 domain reload 後遺失參考的孤兒 daemon
        static bool IsPidFileProcessAlive()
        {
            try
            {
                string pidPath = Path.Combine(UCL_RepoPath.RepoRoot, "AgentCommands", "_screenstream", "_daemon.pid");
                if (!File.Exists(pidPath)) return false;
                if (!int.TryParse(File.ReadAllText(pidPath).Trim(), out int pid) || pid <= 0) return false;
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }   // 無此 process / 讀檔失敗 = 沒有孤兒
        }

        static bool IsProcessAlive(Process p)
        {
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }

        // ===========================================================
        // 區塊職責：清掉「宣稱正在直播」的殘留狀態檔（Tim 2026-07-30 回報）。
        // 物理意義：`_live_info.json` 的契約是**「檔案存在 = 直播中」**，維護者原本只有 daemon 自己
        //          （transition 到 enabled=false 時呼叫 clear_live_info）。但停止錄影的實作是
        //          **立刻 Process.Kill() 收掉 daemon** —— 那不是 graceful shutdown，daemon 根本沒機會
        //          觀察到 transition。等於**每一次停止錄影都會漏一個孤兒旗標**，不是偶發是結構性：
        //          我們在清潔工打掃之前就把他殺了。
        //          實證：`_config.json.enabled=false`、pid 指的 process 早已不存在，
        //          而 `_live_info.json` 還停在兩天前那場（`started_at 2026-07-28T13:29`）——
        //          於是 `freetime.py` 的骰面一直把「觀看直播」鎖第 1 位，三個 persona 同時被誤導。
        // 數值影響：純刪檔，且**只在 enabled=false 時**呼叫 —— 此時這些檔按定義就不該存在。
        //          冪等（檔不在就跳過），失敗只記 warning：清不掉殘留不該讓 daemon 管理本身出錯。
        //   - `_live_info.json`：直播中旗標，freetime 骰面的唯一判準 → 未錄影時一律清
        //   - `_daemon.pid`：daemon 只在自己乾淨退出時刪它，被 Kill() 時同樣留下 —— 同一族的謊。
        //     ⚠ **但只在它指向的 process 已不存在時才清**：上面的孤兒偵測（IsPidFileProcessAlive）
        //     正是靠這個檔找到「domain reload 後失聯、卻還活著」的 daemon。若無條件刪掉，
        //     registry sweep 萬一沒收乾淨，下一輪就再也偵測不到那顆孤兒 —— 為了掃掉一個謊
        //     而弄瞎唯一的偵測器，划不來。
        // ===========================================================
        static void ClearLiveStateFiles()
        {
            string dir = Path.Combine(UCL_RepoPath.RepoRoot, "AgentCommands", "_screenstream");
            TryDeleteStaleFile(Path.Combine(dir, "_live_info.json"), "_live_info.json");
            // pid 指的 process 還活著 → 那不是殘留，是孤兒的線索，留著讓下一輪 tick 收拾
            if (!IsPidFileProcessAlive())
            {
                TryDeleteStaleFile(Path.Combine(dir, "_daemon.pid"), "_daemon.pid");
            }
        }

        static void TryDeleteStaleFile(string path, string label)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                Debug.Log($"[UCL_ScreenStream] 未錄影 → 清掉殘留的 {label}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] 清 {label} 失敗（不影響 daemon 管理）：{e.Message}");
            }
        }

        static string ResolvePython()
        {
            // 優先順序: PATH 內 python → python3 (跑 --version 驗證可用)
            string[] candidates = { "python", "python3" };
            foreach (var c in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = c,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi);
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0) return c;
                }
                catch { /* try next */ }
            }
            return null;
        }

        static void OnBeforeAssemblyReload()
        {
            // domain reload 前 graceful kill daemon, 避免 reload 後孤兒
            KillDaemon();
        }

        static void OnEditorQuitting()
        {
            KillDaemon();
        }

        static void KillDaemon()
        {
            if (s_DaemonProcess == null) return;
            int pid = -1;
            try
            {
                try { pid = s_DaemonProcess.Id; } catch { }
                if (!s_DaemonProcess.HasExited)
                {
                    s_DaemonProcess.Kill();
                    s_DaemonProcess.WaitForExit(2000);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ScreenStream] kill fail: {e.Message}");
            }
            finally
            {
                try { s_DaemonProcess.Dispose(); } catch { }
                s_DaemonProcess = null;
                // Process 註冊中心: 正常收掉 → 移除記錄檔 (kill 失敗的殘留交給管理頁 Dead 清理)
                if (pid > 0) UCL_ProcessRegistryService.Unregister(pid, "screenstream_daemon");
            }
        }
    }
}
#endif
