// 區塊職責：ScreenStream daemon 子程序生命週期管理 (UCL_Core 版) — [InitializeOnLoad] static class。
//            自 EOV 專案的 RCG_ScreenStreamDaemon 遷移 (Tim 2026-07-26 拍板「相關功能遷移到 UCL_Core」)。
// 物理意義：Editor 啟動 / domain reload 時自動 spawn python screenstream_daemon.py 為 child process,
//          每 CHECK_INTERVAL 秒 tick 確認子程序仍活著, 沒活就 respawn;
//          EditorApplication.quitting 時 graceful kill, 避免 zombie process。
// 設計取捨：
//   - script 遷入 <UCL_Core>/Tools~/AgentCommands/ (跨專案共用)，路徑走 UCL_EditorPath.CorePath 動態解析
//   - daemon 啟動後一直 alive, 自己讀 config toggle on/off — Editor 端不必管 toggle 邏輯
//   - 防快速 crash loop: 連續 spawn fail >= MAX_FAST_FAILS → 進 backoff
//   - ⚠ 過渡期守門：偵測到 legacy RCG_ScreenStreamDaemon (EOV 專案端) 仍在 → 本 daemon 讓位不 spawn,
//     避免新舊兩支 python daemon 併寫同一個 frames ring buffer (互蓋 index)。Tim 移除 RCG 版後自動接管。
// 2026-07-26 kaguya (Luna) — 自 RCG_ScreenStreamDaemon (T11 basecamp 2026-05-16) 遷移
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    /// <summary>
    /// ScreenStream daemon 子程序常駐管理 (UCL_Core 版) — Editor 開機自動 spawn python daemon,
    /// daemon 自己讀 _config.json enabled toggle 控制 capture; Editor 只管 process 存活。
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
        static bool IsProcessAlive(Process p)
        {
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
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
            try
            {
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
            }
        }
    }
}
#endif
