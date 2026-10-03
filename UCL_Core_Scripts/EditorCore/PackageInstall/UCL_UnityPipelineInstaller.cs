// 區塊職責：Unity Pipeline 套件（`com.unity.pipeline`）的偵測／安裝／升級／連線測試 —— 套件安裝頁的邏輯層
// 物理意義：Pipeline 是**專案層**的 Unity package，裝進本專案的 `Packages/manifest.json`；
//          它在 Editor 內開 7800 埠，Unity 官方 CLI 必須靠它才連得進正在跑的 Editor（`status`／`command …`）。
//          沒裝它，`unity status` 回 STATUS_NO_INSTANCES（2026-10-03 實測）。
// 數值影響：偵測是唯讀（Package Manager 已解析的資訊）；安裝／升級**會改 manifest.json 與 packages-lock.json**（進版控），
//          只在使用者按下確認後才發生。
// 設計取捨：
//   · **安裝走官方 CLI 的 `unity pipeline install`**，不用 `PackageManager.Client.Add` —— 套件是實驗版
//     （2026-10-03 為 0.8.0-exp.1），該裝哪一版由 CLI 解析；我們寫死名稱或版本，CLI 一更新就會漂。
//     代價：要先裝 CLI（頁面上 CLI 區塊沒找到時，本區按鈕停用）。
//   · **跑在背景、不開視窗** —— 這兩支是非互動指令（帶 --non-interactive），輸出直接收回頁面顯示。
//   · **跑完呼叫 `Client.Resolve()`** —— Editor 失焦時不一定會自己去讀新的 manifest；
//     2026-10-03 裝完後是靠一次重編才讓它解析到套件的。
//   · 連線測試用 `unity command editor_status`：它經過 Editor 主執行緒（實測主執行緒凍 10 秒時它卡 10.39 秒），
//     所以「回得來」才代表 Editor 真的在處理事情；⚠ `unity status` 量不到這件事（凍住時照回 ready）。
#if UNITY_EDITOR
using System;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.PackageInstall
{
    /// <summary>一次 CLI 動作的結果（安裝／升級／連線測試共用）。</summary>
    public struct UnityPipelineRunResult
    {
        public bool Ok;          // exit 0 才算
        public int ExitCode;     // 程序 exit code（逾時／沒跑起來＝-1）
        public string Output;    // stdout（沒有就退 stderr）—— 原文給畫面顯示，本類別不解讀
        public double Seconds;   // 耗時（連線測試用它判斷「主執行緒有沒有卡」）
    }

    /// <summary>Unity Pipeline 套件的偵測與安裝（Editor 專用）。</summary>
    public static class UCL_UnityPipelineInstaller
    {
        /// <summary>套件名稱（官方文件 `com.unity.pipeline@latest`）。</summary>
        public const string PackageName = "com.unity.pipeline";

        /// <summary>官方文件。</summary>
        public const string DocUrl = "https://docs.unity.com/en-us/unity-cli/unity-pipeline/unity-pipeline-package";

        const string PROC_TAG = "unity_pipeline_cli";

        // 安裝／升級要經過網路與 Package Manager 解析 —— 給寬；逾時會強制 kill（不留孤兒）
        const int INSTALL_TIMEOUT_MS = 300000;
        // 連線測試：正常約 1.6 秒（大半是 CLI 啟動）；給 15 秒，超過就當「沒回應」
        const int PING_TIMEOUT_MS = 15000;

        /// <summary>本專案目前解析到的 Pipeline 版本；沒裝＝null。⚠ 主執行緒呼叫（Package Manager API）。</summary>
        public static string InstalledVersion()
        {
            var aInfo = UnityEditor.PackageManager.PackageInfo.FindForPackageName(PackageName);
            return aInfo?.version;
        }

        /// <summary>`unity pipeline install`（已裝則什麼都不改）。</summary>
        public static UniTask<UnityPipelineRunResult> InstallAsync(string iCliExe)
            => RunAsync(iCliExe, $"pipeline install --project-path \"{UCL_RepoPath.UnityProjectRoot}\" --non-interactive --no-banner",
                INSTALL_TIMEOUT_MS, iResolveAfter: true);

        /// <summary>`unity pipeline upgrade`（有新版才升）。</summary>
        public static UniTask<UnityPipelineRunResult> UpgradeAsync(string iCliExe)
            => RunAsync(iCliExe, $"pipeline upgrade --project-path \"{UCL_RepoPath.UnityProjectRoot}\" --non-interactive --no-banner",
                INSTALL_TIMEOUT_MS, iResolveAfter: true);

        /// <summary>`unity command editor_status`：CLI 經 Pipeline 問一次 Editor。</summary>
        public static UniTask<UnityPipelineRunResult> PingAsync(string iCliExe)
            => RunAsync(iCliExe, $"command editor_status --project-path \"{UCL_RepoPath.UnityProjectRoot}\" --no-banner",
                PING_TIMEOUT_MS, iResolveAfter: false);

        // 區塊職責：背景跑一次 CLI，切回主執行緒後（需要時）叫 Package Manager 重新解析
        // 物理意義：⚠ 連線測試**必須在背景執行緒等** —— editor_status 要 Editor 主執行緒來回答，
        //          在主執行緒上同步等它就是自己等自己（死結到逾時）。
        static async UniTask<UnityPipelineRunResult> RunAsync(string iCliExe, string iArgs, int iTimeoutMs, bool iResolveAfter)
        {
            if (string.IsNullOrEmpty(iCliExe))
                return new UnityPipelineRunResult { ExitCode = -1, Output = "❌ 找不到 Unity CLI —— 先在上方 CLI 區塊安裝並重新偵測" };

            var aResult = await UniTask.RunOnThreadPool(() =>
            {
                var aStart = DateTime.UtcNow;
                var (aExit, aOut, aErr) = UCL_ProcessCli.Run(iCliExe, iArgs, UCL_RepoPath.UnityProjectRoot, PROC_TAG,
                    nameof(UCL_UnityPipelineInstaller), iTimeoutMs, displayName: "unity " + iArgs.Split(' ')[0] + " " + iArgs.Split(' ')[1]);
                return new UnityPipelineRunResult
                {
                    Ok = aExit == 0,
                    ExitCode = aExit,
                    Output = string.IsNullOrWhiteSpace(aOut) ? aErr : aOut,
                    Seconds = (DateTime.UtcNow - aStart).TotalSeconds,
                };
            });
            await UniTask.SwitchToMainThread();
            if (iResolveAfter && aResult.Ok) UnityEditor.PackageManager.Client.Resolve();
            return aResult;
        }
    }
}
#endif
