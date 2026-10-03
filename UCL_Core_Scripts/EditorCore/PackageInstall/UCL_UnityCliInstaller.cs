// 區塊職責：Unity 官方 CLI（`unity` 指令）的偵測／安裝／更新 —— 套件安裝頁（UCL_PackageInstallPage）的邏輯層
// 物理意義：CLI 是**系統層**安裝的獨立執行檔（不是 Unity 專案的 package），裝在使用者帳號底下、靠 PATH 被找到。
//          本檔只做三件事：① 找到它在哪、讀版本 ② 開一個使用者看得到的 PowerShell 視窗跑官方安裝指令
//          ③ 同樣方式跑 `unity self-update`。**安裝過程本身不在 Editor 裡跑**（見下方設計取捨）。
// 數值影響：偵測是唯讀（stat 檔案＋跑一次 `--version`）；安裝／更新會從網路下載並改使用者的 PATH，
//          只在使用者於頁面上按下確認之後才會發生。
// 設計取捨：
//   · **安裝走可見的 PowerShell 視窗，不在背景跑** —— winget 第一次用會要求同意來源條款、腳本可能跳 UAC，
//     這些提示在背景 process 裡看不見，等於永遠卡住。視窗讓人看到進度、自己按同意（本檔⛔不替人帶 accept 旗標）。
//   · **PATH 讀登錄檔的最新值，不讀 Editor 行程的** —— Editor 啟動時拿到的 PATH 是快照，
//     裝完之後它看不到新路徑（本地 LLM 頁的 ollama 安裝就因此要求「重開 Unity 再按重新整理」）。
//     讀 User＋Machine 兩份最新值，裝完按一次「重新偵測」就找得到，不必重開。
//   · **排除 Unity Editor 本體** —— Editor 的執行檔也叫 `Unity.exe`，Windows 檔名不分大小寫；
//     如果哪天 Editor 目錄進了 PATH，`unity --version` 會**開一個 Editor**。旁邊有 `Data/` 資料夾的那個一律跳過。
//   · 官方出處：https://docs.unity.com/en-us/unity-cli/use-unity-cli（2026-10-03 讀，CLI 當時是 beta）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.PackageInstall
{
    /// <summary>一次偵測的結果。</summary>
    public struct UnityCliProbeResult
    {
        public bool Found;          // 有沒有找到 CLI 執行檔
        public string ExePath;      // 找到的完整路徑（沒找到＝空）
        public string Version;      // `--version` 的第一行（跑不起來＝空）
        public string Error;        // 找到了但讀不到版本時的原因
        public List<string> SkippedEditors;   // 被排除的 Unity Editor 本體（給畫面誠實列出）
        public bool WingetFound;    // 這台機器有沒有 winget（決定 winget 安裝鈕能不能按）
    }

    /// <summary>Unity 官方 CLI 的偵測與安裝（Editor 專用）。</summary>
    public static class UCL_UnityCliInstaller
    {
        // Process 註冊中心的 tag —— 偵測用的短命 process 與安裝視窗分開，ProcessAdmin 頁才分得出是哪一種
        const string PROC_TAG_PROBE = "unity_cli_probe";
        const string PROC_TAG_INSTALL = "unity_cli_install";

        // `--version` 的逾時：正常不到一秒；第一次跑可能做背景更新檢查，給寬一點但不能無上限
        const int PROBE_TIMEOUT_MS = 20000;

        /// <summary>官方文件（頁面「📖 官方文件」鈕用）。</summary>
        public const string DocUrl = "https://docs.unity.com/en-us/unity-cli/use-unity-cli";

        /// <summary>winget 安裝指令（官方文件列出的套件 id；⛔ 不帶 --accept-* —— 條款由人在視窗裡自己同意）。</summary>
        public const string WingetInstallCommand = "winget install --id Unity.CLI --exact";

        /// <summary>官方安裝腳本（文件標為推薦方式；**下載並執行遠端腳本**，內容由 unity.com 當下提供）。</summary>
        public const string ScriptInstallCommand = "irm https://unity.com/install.ps1 | iex";

        /// <summary>這台機器能不能用本功能（CLI 支援三平台，但本檔的安裝與 PATH 讀法只寫了 Windows）。</summary>
        public static bool IsSupportedPlatform =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        // ===========================================================
        // 區塊：偵測
        // ===========================================================

        /// <summary>背景執行緒找 CLI＋讀版本，完成後切回主執行緒回傳（呼叫端拿到就會動 GUI 狀態）。</summary>
        public static async UniTask<UnityCliProbeResult> ProbeAsync()
        {
            var aResult = await UniTask.RunOnThreadPool(Probe);
            await UniTask.SwitchToMainThread();
            return aResult;
        }

        // 區塊職責：同步版偵測（只在背景執行緒呼叫 —— 會跑一次外部 process）
        // 物理意義：先找檔、再跑 `--version`。找到檔但版本讀不到時 Found 仍為 true、Error 帶原因 ——
        //          「檔案在」與「它跑得起來」是兩件事，畫面要分開講，不壓成一個「沒裝」。
        static UnityCliProbeResult Probe()
        {
            var aSkipped = new List<string>();
            var aResult = new UnityCliProbeResult
            {
                SkippedEditors = aSkipped,
                WingetFound = FindOnFreshPath("winget.exe", null) != null,
            };
            string aExe = FindOnFreshPath("unity.exe", aSkipped);
            if (aExe == null) return aResult;

            aResult.Found = true;
            aResult.ExePath = aExe;
            var (aExit, aOut, aErr) = UCL_ProcessCli.Run(aExe, "--version", null, PROC_TAG_PROBE,
                nameof(UCL_UnityCliInstaller), PROBE_TIMEOUT_MS, displayName: "unity --version");
            string aFirstLine = FirstLine(aOut);
            if (aExit == 0 && !string.IsNullOrEmpty(aFirstLine)) aResult.Version = aFirstLine;
            else aResult.Error = $"`--version` exit {aExit}：{FirstLine(string.IsNullOrEmpty(aErr) ? aOut : aErr)}";
            return aResult;
        }

        // 區塊職責：在「最新的」PATH 上找一支執行檔，回傳第一個命中的完整路徑（找不到＝null）
        // 物理意義：搜尋順序＝ UNITY_CLI_HOME\bin（官方腳本的自訂安裝位置）→ 使用者 PATH → 系統 PATH
        //          → Editor 行程自己的 PATH（舊快照，墊底）→ WindowsApps（winget 的 MSIX 別名住這裡）。
        // 數值影響：ioSkippedEditors 非 null 時，跳過的 Unity Editor 本體會記進去讓畫面列出。
        static string FindOnFreshPath(string iFileName, List<string> ioSkippedEditors)
        {
            var aDirs = new List<string>();
            string aCliHome = Environment.GetEnvironmentVariable("UNITY_CLI_HOME", EnvironmentVariableTarget.User)
                              ?? Environment.GetEnvironmentVariable("UNITY_CLI_HOME");
            if (!string.IsNullOrEmpty(aCliHome)) aDirs.Add(Path.Combine(aCliHome, "bin"));
            AddPathDirs(aDirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPathDirs(aDirs, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            AddPathDirs(aDirs, Environment.GetEnvironmentVariable("PATH"));
            string aLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(aLocalAppData)) aDirs.Add(Path.Combine(aLocalAppData, "Microsoft", "WindowsApps"));

            var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string aDir in aDirs)
            {
                if (string.IsNullOrWhiteSpace(aDir) || !aSeen.Add(aDir)) continue;
                string aPath;
                try { aPath = Path.Combine(aDir, iFileName); }
                catch (ArgumentException) { continue; }   // PATH 裡有非法字元的條目 —— 跳過那一格，不讓它擋掉後面的
                if (!File.Exists(aPath)) continue;
                if (IsUnityEditorExe(aPath))
                {
                    ioSkippedEditors?.Add(aPath);
                    continue;
                }
                return aPath;
            }
            return null;
        }

        // PATH 字串拆成目錄（展開 %VAR% —— 登錄檔裡的 PATH 常是未展開的 REG_EXPAND_SZ）
        static void AddPathDirs(List<string> ioDirs, string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return;
            foreach (string aPart in iPath.Split(Path.PathSeparator))
            {
                string aDir = Environment.ExpandEnvironmentVariables(aPart.Trim().Trim('"'));
                if (aDir.Length > 0) ioDirs.Add(aDir);
            }
        }

        // 區塊職責：判斷一支 `Unity.exe` 是不是 Editor 本體
        // 物理意義：Editor 安裝目錄的 `Unity.exe` 旁邊一定有 `Data/`（引擎資源）；CLI 是單一執行檔，旁邊沒有。
        //          判錯成 Editor 的代價只是「找不到 CLI」（畫面會列出被跳過的路徑）；
        //          反過來判錯的代價是按一次偵測就**開一個 Editor** —— 所以寧可往排除那邊偏。
        static bool IsUnityEditorExe(string iExePath)
        {
            string aDir = Path.GetDirectoryName(iExePath);
            return !string.IsNullOrEmpty(aDir) && Directory.Exists(Path.Combine(aDir, "Data"));
        }

        static string FirstLine(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            int aIdx = iText.IndexOfAny(new[] { '\r', '\n' });
            return (aIdx < 0 ? iText : iText.Substring(0, aIdx)).Trim();
        }

        // ===========================================================
        // 區塊：安裝／更新（開可見的 PowerShell 視窗）
        // ===========================================================

        /// <summary>用 winget 安裝（MSIX 套件，之後 `unity self-update` 照常可用）。</summary>
        public static string LaunchWingetInstall() => LaunchInConsole(WingetInstallCommand, "安裝 Unity CLI（winget）");

        /// <summary>用官方腳本安裝。</summary>
        public static string LaunchScriptInstall() => LaunchInConsole(ScriptInstallCommand, "安裝 Unity CLI（官方腳本）");

        /// <summary>用已安裝的 CLI 自我更新（路徑來自最近一次偵測）。</summary>
        public static string LaunchSelfUpdate(string iExePath)
        {
            if (string.IsNullOrEmpty(iExePath) || !File.Exists(iExePath))
                return "❌ 找不到 CLI 執行檔 —— 先按「重新偵測」";
            // PowerShell 裡執行帶空白路徑的程式要用呼叫運算子 &；單引號內的單引號要寫兩次
            return LaunchInConsole($"& '{iExePath.Replace("'", "''")}' self-update", "更新 Unity CLI");
        }

        // 區塊職責：開一個 PowerShell 視窗跑指令，視窗**不會自己關**（-NoExit），讓人讀完結果
        // 物理意義：UseShellExecute=true ⇒ 獨立的主控台視窗，跟 Editor 沒有 stdin／stdout 關係；
        //          登記進 ProcessRegistry（ProcessAdmin 頁看得到、domain reload 後不變成無主孤兒）。
        // 數值影響：回傳一行狀態文字給頁面顯示；⛔ 不等它跑完 —— 安裝要幾分鐘，進度在那個視窗裡。
        static string LaunchInConsole(string iCommand, string iLabel)
        {
            if (!IsSupportedPlatform) return "❌ 目前只支援 Windows";
            // 整段指令包在命令列的一對雙引號裡交給 -Command ⇒ 指令本身不能再含雙引號
            // （命令列層與 PowerShell 層的跳脫規則不同，混著用會靜默切錯參數）。本檔的三條指令都只用單引號。
            if (iCommand.IndexOf('"') >= 0) return $"❌ 指令含雙引號，拒絕組命令列：{iCommand}";
            try
            {
                // 先印出要跑的指令再執行 —— 視窗裡看得到「這一窗在跑什麼」（單引號字串內的單引號寫兩次）
                string aEcho = $"[{iLabel}] {iCommand}".Replace("'", "''");
                var aPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // -NoProfile：不讓使用者的 profile 腳本影響安裝；-NoExit：跑完視窗留著讓人讀結果
                    Arguments = $"-NoExit -NoProfile -Command \"Write-Host '{aEcho}'; {iCommand}\"",
                    UseShellExecute = true,
                };
                var aRecord = UCL_ProcessRegistryService.StartAndRegister(aPsi, PROC_TAG_INSTALL,
                    iLabel, nameof(UCL_UnityCliInstaller));
                return aRecord != null
                    ? $"▶ 已開啟 PowerShell 視窗：{iLabel} —— 進度看那個視窗；跑完回來按「🔄 重新偵測」"
                    : $"⚠ 已送出啟動，但沒拿到 process 可登記：{iLabel}（視窗若有出現就照常進行）";
            }
            catch (Exception e)
            {
                return $"❌ 開不了 PowerShell：{e.Message}";
            }
        }
    }
}
#endif
