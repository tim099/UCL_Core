// 區塊職責：遠端協作視窗控制 — 在遠端模式下，以 Win32 將已開啟的指定 IDE 帶到前景。
// 物理意義：此類別只處理「找視窗 / 切換前景」；不傳送鍵盤文字、不按 Enter，也不保存啟用狀態。
// 數值影響：使用者最後一次實體輸入後的 m_UserIdlePauseSeconds 內，一般切換一律拒絕；預設 60 秒。
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 遠端模式的最小權限視窗協作控制器。static runtime state 在 domain reload / Editor 重啟後回到關閉，
    /// 所以使用者必須每次從 Bartender Admin 明確啟動，避免背景程序意外搶走本機操作權。
    /// </summary>
    public static class UCL_RemoteWindowControl
    {
        const int DefaultUserIdlePauseSeconds = 60;
        const string DiagnosticFileName = "remote_window_last_test.md";
        static bool s_Enabled;
        static bool s_PauseOnUserInput = true;
        static int s_UserIdlePauseSeconds = DefaultUserIdlePauseSeconds;
        static string s_LastResult = "尚未測試";

        /// <summary>本次 Editor session 是否允許一般前景切換；故意不寫 PlayerPrefs / 檔案。</summary>
        public static bool Enabled => s_Enabled;

        /// <summary>一般自動切換是否在偵測到使用者鍵鼠操作後暫停；runtime-only，預設安全開啟。</summary>
        public static bool PauseOnUserInput
        {
            get => s_PauseOnUserInput;
            set => s_PauseOnUserInput = value;
        }

        /// <summary>使用者輸入後的保護暫停秒數；runtime-only，範圍限制避免誤填造成永久停止。</summary>
        public static int UserIdlePauseSeconds
        {
            get => s_UserIdlePauseSeconds;
            set => s_UserIdlePauseSeconds = Mathf.Clamp(value, 5, 3600);
        }

        public static string LastResult => s_LastResult;
        public static string DiagnosticPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), DiagnosticFileName);

        /// <summary>最後一筆 OS 實體輸入距今秒數；讀取失敗時回 0，採 fail-closed。</summary>
        public static double UserIdleSeconds
        {
            get
            {
                LASTINPUTINFO info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return 0;
                uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
                return elapsedMs / 1000.0;
            }
        }

        /// <summary>設定 runtime 開關；關閉時也中止進行中的輪循，確保不殘留延後切換。</summary>
        public static void SetEnabled(bool enabled)
        {
            s_Enabled = enabled;
        }

        /// <summary>
        /// 一般自動切換入口。使用者最近有操作時拒絕，以免遠端自動化與真人爭奪焦點。
        /// </summary>
        public static bool TryActivate(string targetName, out string result)
        {
            if (!s_Enabled)
            {
                result = "系統尚未啟動（每次 Editor session 都需在酒保後台手動開啟）";
                return false;
            }
            if (s_PauseOnUserInput && UserIdleSeconds < s_UserIdlePauseSeconds)
            {
                result = $"偵測到使用者操作，暫停切換；尚需靜置 {Math.Ceiling(s_UserIdlePauseSeconds - UserIdleSeconds)} 秒";
                return false;
            }
            return TryActivateWindow(targetName, out result);
        }

        /// <summary>
        /// 後台測試按鈕的單次、明示授權切換。它仍需 runtime 開關已開，但不受「剛點下按鈕」造成的 idle
        /// 保護阻擋；一般自動流程則必須走 <see cref="TryActivate"/> 並完整遵守 idle 保護。
        /// </summary>
        public static bool TryActivateExplicitly(string targetName, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                s_LastResult = result;
                return false;
            }
            bool succeeded = TryActivateWindow(targetName, out result);
            s_LastResult = $"{targetName}：{result}";
            return succeeded;
        }

        // 區塊職責：列舉可見 top-level window，以標題或程序名稱比對指定工具後呼叫 SetForegroundWindow。
        // 物理意義：Win32 視窗與 process metadata 比圖像可靠，無 DPI / icon badge / 深淺主題相依性。
        // 數值影響：只接受可見且非空標題的視窗；沒有唯一命中時保留目前焦點、不嘗試猜測點擊座標。
        static bool TryActivateWindow(string targetName, out string result)
        {
            IntPtr processMatched = IntPtr.Zero;
            string processMatchedTitle = "";
            IntPtr titleFallback = IntPtr.Zero;
            string titleFallbackTitle = "";
            var diagnostics = new List<string>();
            var visibleWindows = new List<string>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetWindowThreadProcessId(window, out uint processId);
                string processName = GetProcessName(processId);
                string title = GetWindowTitle(window);
                visibleWindows.Add(DescribeWindow(window, processId, processName, title));
                bool processHit = IsTargetProcess(processName, targetName);
                bool titleHit = ContainsIgnoreCase(title, targetName);
                if (processHit || titleHit)
                    diagnostics.Add($"- hwnd: `0x{window.ToInt64():X}` | pid: `{processId}` | process: `{processName}` | process-hit: `{processHit}` | title-hit: `{titleHit}` | title: `{title}`");
                if (processHit)
                {
                    processMatched = window;
                    processMatchedTitle = title;
                    return false; // process identity is definitive; don't let a later title match replace it.
                }
                if (titleFallback == IntPtr.Zero && titleHit)
                {
                    titleFallback = window;
                    titleFallbackTitle = title;
                }
                return true;
            }, IntPtr.Zero);

            IntPtr matched = processMatched != IntPtr.Zero ? processMatched : titleFallback;
            string matchedTitle = processMatched != IntPtr.Zero ? processMatchedTitle : titleFallbackTitle;

            if (matched == IntPtr.Zero)
            {
                result = "找不到可見視窗";
                WriteDiagnostic(targetName, diagnostics, visibleWindows, result, IntPtr.Zero, false, GetForegroundWindow(), GetForegroundWindow());
                return false;
            }
            IntPtr foregroundBefore = GetForegroundWindow();
            bool requestAccepted = TryBringToForeground(matched, foregroundBefore);
            IntPtr foregroundAfter = GetForegroundWindow();
            bool success = foregroundAfter == matched;
            result = success
                ? $"已切換到「{matchedTitle}」"
                : $"找到「{matchedTitle}」，但前景仍是 {DescribeWindow(foregroundAfter)}（Win32 request={requestAccepted}）";
            WriteDiagnostic(targetName, diagnostics, visibleWindows, result, matched, success, foregroundBefore, foregroundAfter);
            return success;
        }

        // 區塊職責：用 restore + bring-to-top + 暫時 AttachThreadInput 完成 Windows 前景切換，並由 caller 做真實前景驗證。
        // 物理意義：SetForegroundWindow 回 true 只代表 request 被接受，未保證視窗實際蓋到目前焦點；attach 可處理同桌面同權限程序。
        // 數值影響：任何 Win32 呼叫失敗都只回 false，沒有座標點擊或鍵盤 fallback；跨權限桌面仍安全地失敗。
        static bool TryBringToForeground(IntPtr target, IntPtr foregroundBefore)
        {
            uint targetThread = GetWindowThreadProcessId(target, out _);
            uint foregroundThread = foregroundBefore == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundBefore, out _);
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != targetThread)
                    attached = AttachThreadInput(foregroundThread, targetThread, true);
                ShowWindowAsync(target, ShowRestore);
                bool broughtToTop = BringWindowToTop(target);
                bool foregroundRequested = SetForegroundWindow(target);
                SetFocus(target);
                return broughtToTop || foregroundRequested;
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, targetThread, false);
            }
        }

        // 區塊職責：將每次明示測試的 Win32 中介資料落成可直接開啟的 UTF-8 Markdown。
        // 物理意義：UI 顯示的一行結果不足以判斷錯配是標題、process、PID 或 Windows 前景政策造成；檔案保留候選真相。
        // 數值影響：每次覆寫一份最新測試，不累積含本機視窗標題的歷史資料；寫檔失敗不影響切換結果。
        static void WriteDiagnostic(string targetName, List<string> candidates, List<string> visibleWindows, string result, IntPtr selectedWindow, bool foregroundSucceeded, IntPtr foregroundBefore, IntPtr foregroundAfter)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var text = new StringBuilder();
                text.AppendLine("# 遠端視窗協作測試診斷");
                text.AppendLine();
                text.AppendLine($"- 時間（UTC）：`{DateTime.UtcNow:O}`");
                text.AppendLine($"- 指定 agent：`{targetName}`");
                text.AppendLine($"- 使用者靜置秒數：`{UserIdleSeconds:0.000}`");
                text.AppendLine($"- 切換前 foreground：{DescribeWindow(foregroundBefore)}");
                text.AppendLine($"- 切換後 foreground：{DescribeWindow(foregroundAfter)}");
                text.AppendLine($"- 選定 hwnd：`{(selectedWindow == IntPtr.Zero ? "none" : $"0x{selectedWindow.ToInt64():X}")}`");
                text.AppendLine($"- SetForegroundWindow：`{foregroundSucceeded}`");
                text.AppendLine($"- 結果：{result}");
                text.AppendLine();
                text.AppendLine("## 可見候選視窗");
                text.AppendLine(candidates.Count == 0 ? "（無 process / title 命中候選）" : string.Join("\n", candidates));
                text.AppendLine();
                text.AppendLine("## 全部可見 top-level 視窗");
                text.AppendLine(visibleWindows.Count == 0 ? "（無法列舉）" : string.Join("\n", visibleWindows));
                File.WriteAllText(DiagnosticPath, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"[RemoteWindowControl] 診斷檔寫入失敗: {exception.Message}");
            }
        }

        static string GetWindowTitle(IntPtr window)
        {
            int length = GetWindowTextLength(window);
            if (length <= 0) return "";
            var buffer = new StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        static string GetProcessName(uint processId)
        {
            try { return Process.GetProcessById((int)processId).ProcessName ?? ""; }
            catch { return ""; }
        }

        static string DescribeWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return "`none`";
            GetWindowThreadProcessId(window, out uint processId);
            return DescribeWindow(window, processId, GetProcessName(processId), GetWindowTitle(window));
        }

        static string DescribeWindow(IntPtr window, uint processId, string processName, string title) =>
            $"`0x{window.ToInt64():X}` | pid=`{processId}` | process=`{processName}` | title=`{title}`";

        static bool ContainsIgnoreCase(string source, string value) =>
            !string.IsNullOrEmpty(source) && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        // 區塊職責：將人類顯示名稱對齊實際 process basename，作為比視窗標題更可靠的第一優先識別。
        // 物理意義：Claude Code 的 Windows process 通常叫 claude.exe；反過來，工作階段標題可含其他 agent 名稱。
        // 數值影響：只有完全等於既定 basename 才當 definitive hit；標題僅在找不到該程序時才作 fallback。
        static bool IsTargetProcess(string processName, string targetName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            if (targetName == "Antigravity") return string.Equals(processName, "Antigravity", StringComparison.OrdinalIgnoreCase);
            // Codex Desktop 在本機可見主視窗由 ChatGPT.exe 擁有；codex.exe 則是無視窗的 command / host process。
            if (targetName == "Codex") return string.Equals(processName, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase);
            if (targetName == "Claude Code") return string.Equals(processName, "claude", StringComparison.OrdinalIgnoreCase);
            return string.Equals(processName, targetName, StringComparison.OrdinalIgnoreCase);
        }

        delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        const int ShowRestore = 9;
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    }
}
#endif
