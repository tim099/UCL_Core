
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/25 2026
// 區塊職責：本檔提供「從 Agent Command 觸發 Addressables Build」的 Cmd，讓 agent (跑 run_cmd.py) 也能驗 addressable build，
//          不必每次都靠 Tim 手動點 Window > Asset Management > Addressables > Build。
// 物理意義：呼叫 Unity Addressables 官方 API — (可選) CleanPlayerContent 清舊 catalog → BuildPlayerContent(out result) 打 content。
//          BuildPlayerContent 走的就是 Tim 手動 build 的同一條路徑，所以能重現 / 驗證同樣的成功或失敗 (含 catalog 重複 key 等例外)。
// 數值影響：實際產出 ServerData / catalog (寫入磁碟)；本 Cmd 另把結果摘要寫一份 md 報告 + Debug.Log，失敗則 throw 讓 run_cmd 顯示失敗。
// 設計取捨：
//   - 參考 Cmd_DiagnoseAssetReflection 的 handler 樣板 + Cmd_BuildPlayerCheck 的 build-cmd 慣例 (per Tim task)。
//   - 用 AddressableAssetSettings.BuildPlayerContent(out result) 取得 result.Error；另 try/catch 包住，
//     捕捉「An item with the same key has already been added」這類在 catalog 建構期拋的例外 (非走 result.Error)。
//   - clean 預設 true：先 CleanPlayerContent 清舊 content，避免殘留 catalog 干擾 (對齊 UCL_PreBuildAddressableSetting)。
//   - *放置位置*：本檔放 UCL_Core/Editor (UCL_CoreEditor.asmdef)，因為 UnityEditor.AddressableAssets.* build API 在 Unity.Addressables.Editor
//     assembly，只有 Editor-only 的 UCL_CoreEditor.asmdef 有引用它；runtime 的 UCL_Core.asmdef 不能引用 editor-only assembly。
//     Cmd 自動發現走 GetAllSubclass→GetAllTypes (掃全 assembly)，故放 editor assembly 一樣會被 UCL_AgentCommandRegistry 註冊。
// ship 2026-05-25 gura (Tim task: 參考 Reflection CMD 做 build addressable CMD)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core;
using UCL.Core.EditorLib.AgentCommands;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：觸發 Addressables Build (BuildPlayerContent)。
    ///
    /// 讓 agent 透過 run_cmd.py 驗 addressable build (重現 Tim 手動 Build 的同一條路徑)，
    /// 抓得到 catalog 重複 key、duplicate address、缺資產等只有 build 時才暴露的問題。
    ///
    /// 參數：
    /// - <c>clean</c>（選填，預設 true）：build 前先 CleanPlayerContent 清舊 catalog。
    /// - <c>outputPath</c>（選填）：結果報告 md 路徑 (相對專案根)。
    /// </summary>
    public class Cmd_BuildAddressable : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "BuildAddressable";

        public override string ShortDescription =>
            "Build Addressables content (AddressableAssetSettings.BuildPlayerContent) so agent can verify addressable build / catch catalog errors.";

        public override string ArgsSchema =>
            "clean=true|false (default true — build 前先 CleanPlayerContent 清舊 catalog)\n" +
            "outputPath=結果報告 md 路徑 (相對專案根，預設 AgentCommands/addressable_build_<ts>.md)";

        /// <summary>Page「Fill Example」一鍵填入用。</summary>
        public override string ExampleArgs => "clean=true";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_BuildAddressable.md";

        // 專案根路徑 (與 Cmd_DiagnoseAssetReflection 一致，用於解析相對輸出路徑)
        private static string ProjectRoot => UCL_RepoPath.UnityProjectRoot;

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 讓 Cmd 切到下一影格再跑，避免在 trigger 回呼同步堆疊上直接做重活
            await UniTask.Yield();

            // 區塊職責：解析參數
            // 物理意義：clean 決定是否先清舊 catalog；outputPath 決定報告落點
            // 數值影響：影響 build 行為與報告位置
            bool clean = string.Equals(GetArg(args, "clean", "true"), "true", StringComparison.OrdinalIgnoreCase);
            string outputPath = GetArg(args, "outputPath", null);

            // 區塊職責：取得 Addressables 設定 (Default Object)
            // 物理意義：BuildPlayerContent / CleanPlayerContent 都需要 active settings；缺則無法 build
            // 數值影響：settings 為 null → 直接 fail-loud (throw)，避免後續 NRE
            AddressableAssetSettings aSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (aSettings == null)
            {
                throw new Exception("[Cmd:BuildAddressable] AddressableAssetSettingsDefaultObject.Settings == null — 專案尚未設定 Addressables (Window > Asset Management > Addressables > Groups 建立設定)。");
            }

            // 區塊職責：(可選) 先清舊 content
            // 物理意義：CleanPlayerContent 移除上次 build 產出的 catalog / bundle，避免殘留干擾 (對齊 UCL_PreBuildAddressableSetting)
            // 數值影響：clean=false 時跳過 (增量 / 除錯場景)
            if (clean)
            {
                try
                {
                    AddressableAssetSettings.CleanPlayerContent(aSettings.ActivePlayerDataBuilder);
                    Debug.Log("[Cmd:BuildAddressable] CleanPlayerContent done.");
                }
                catch (Exception ex)
                {
                    // 清理失敗不致命 (可能本來就沒舊 content)；記 warning 後續續跑 build
                    Debug.LogWarning($"[Cmd:BuildAddressable] CleanPlayerContent fail (續跑 build): {ex.Message}");
                }
            }

            // 區塊職責：實際 build content
            // 物理意義：BuildPlayerContent(out result) 走 Tim 手動 Build 的同一條路徑；
            //          result.Error 非空 = build 端回報失敗；另 try/catch 捕捉 catalog 建構期拋的例外 (e.g. 重複 key)。
            // 數值影響：成功 → 報告 OK + duration；失敗 → 報告 error 並 throw 讓 run_cmd 顯示失敗
            string aError = null;
            double aDuration = 0;
            string aBuildOutputPath = null;
            int aLocationCount = -1;
            string aExceptionDump = null;

            System.DateTime aStart = System.DateTime.Now;
            try
            {
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult aResult);
                if (aResult != null)
                {
                    aError = aResult.Error;                       // 空字串 = 成功
                    aDuration = aResult.Duration;                 // 秒
                    aBuildOutputPath = aResult.OutputPath;        // catalog 輸出目錄
                    aLocationCount = aResult.LocationCount;       // 產出的 location 數
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 區塊職責：捕捉 catalog 建構期拋出的例外 (非走 result.Error 的那種，如 duplicate key)
                // 物理意義：把完整型別 + 訊息 + stack head 收進報告，給 agent 一眼定位
                // 數值影響：aExceptionDump 非空 → 視為失敗
                aExceptionDump = $"{ex.GetType().Name}: {ex.Message}\n{HeadStack(ex)}";
                Debug.LogException(ex);
            }

            aDuration = aDuration > 0 ? aDuration : (System.DateTime.Now - aStart).TotalSeconds;

            // 區塊職責：判定成功與否
            // 物理意義：result.Error 非空 或 拋了例外 → 失敗
            // 數值影響：決定報告標題 + 是否 throw
            bool aFailed = !string.IsNullOrEmpty(aError) || !string.IsNullOrEmpty(aExceptionDump);

            // 區塊職責：寫報告
            if (string.IsNullOrEmpty(outputPath))
            {
                string ts = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = $"AgentCommands/addressable_build_{ts}.md";
            }
            string absOut = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(ProjectRoot, outputPath);
            string outDir = Path.GetDirectoryName(absOut);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string content = RenderMarkdown(aFailed, clean, aError, aExceptionDump, aDuration, aBuildOutputPath, aLocationCount);
            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            // 區塊職責：終局 log + (失敗時) throw
            // 物理意義：成功印 summary；失敗 throw 讓 run_cmd.py 端顯示 Cmd 失敗 (對齊跨層次驗證：不要只印 stdout OK)
            // 數值影響：throw 會被 runner 捕捉並標記此 Cmd 失敗
            if (aFailed)
            {
                string aReason = !string.IsNullOrEmpty(aError) ? aError : aExceptionDump;
                Debug.LogError($"[Cmd:BuildAddressable] FAILED ({aDuration:0.00}s) → {outputPath}\n{aReason}");
                throw new Exception($"[Cmd:BuildAddressable] Addressables build FAILED: {aReason}");
            }

            Debug.Log($"[Cmd:BuildAddressable] OK ({aDuration:0.00}s, locations={aLocationCount}) → {outputPath}");
            await UniTask.CompletedTask;
        }

        // 區塊職責：把 build 結果渲染成 markdown 報告
        // 物理意義：成功 / 失敗一目了然 + 關鍵數據 (duration / output / location / error)
        // 數值影響：純輸出，不影響 build
        private static string RenderMarkdown(bool iFailed, bool iCleaned, string iError, string iExceptionDump,
                                             double iDuration, string iOutputPath, int iLocationCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Addressable Build Result");
            sb.AppendLine();
            sb.AppendLine($"- **Generated**: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **Result**: {(iFailed ? "❌ FAILED" : "✅ SUCCESS")}");
            sb.AppendLine($"- **Cleaned first**: {iCleaned}");
            sb.AppendLine($"- **Duration**: {iDuration:0.00}s");
            if (iLocationCount >= 0) sb.AppendLine($"- **Location count**: {iLocationCount}");
            if (!string.IsNullOrEmpty(iOutputPath)) sb.AppendLine($"- **Output path**: `{iOutputPath}`");
            sb.AppendLine();

            if (iFailed)
            {
                sb.AppendLine("## Failure");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(iError))
                {
                    sb.AppendLine("`AddressablesPlayerBuildResult.Error`:");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(iError);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(iExceptionDump))
                {
                    sb.AppendLine("Exception (catalog build 期拋出):");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(iExceptionDump);
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.AppendLine("> Addressables content built successfully. ✅");
            }
            return sb.ToString();
        }

        // 取例外 stack 前 8 行 (與 Cmd_DiagnoseAssetReflection 一致)
        private static string HeadStack(Exception ex)
        {
            string s = ex.StackTrace ?? "";
            var lines = s.Split('\n');
            int n = Math.Min(8, lines.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.AppendLine(lines[i]);
            return sb.ToString();
        }
    }
}
#endif
