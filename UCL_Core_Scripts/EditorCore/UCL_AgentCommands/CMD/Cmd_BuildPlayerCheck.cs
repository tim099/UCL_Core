// 區塊職責: 用當前 Build Profile (Unity 6 BuildProfile API) 跑 Player Script Compile-only build,
//          給 agent 驗 CS error / Mono preprocessor bug / #if UNITY_EDITOR guard 不一致等
//          「Editor 過但 Player Build 不過」family 問題, 避免 Tim 手動點 Addressables Build 才暴露.
// 物理意義: BuildPipeline.BuildPlayer + BuildOptions.BuildScriptsOnly → 只跑 player script compile,
//          不打 asset bundle / addressables / app packaging, ~5-15s vs full build 30-60s.
// 數值影響: 出 BuildReport, parse summary.result + steps[].messages → 寫 markdown to _last_op.md
//          (對齊 check_compile.py 格式給 agent 讀)。
// 設計取捨:
//   - 走 BuildOptions.BuildScriptsOnly 而非完整 BuildPlayer — 10x 快, 對 CS 驗證足夠
//   - 用 BuildProfile.GetActiveBuildProfile() 取當前 (per Tim 指定); 沒設 profile 退 EditorUserBuildSettings
//   - 預設 target 從 active profile 或 activeBuildTarget 拿, agent 不必傳 target
//   - 輸出到 Library/AgentBuildCheck/ (Unity 自己會清 Library)
// T19 ship 2026-05-18 gura (Tim task: 績效獎金 30 token)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 用當前 Build Profile 跑 Player Script Compile-only build, 驗 CS error / preprocessor bug。
    /// 比 check_compile.py 強之處: 走 Player 編譯路徑, 抓得到 #if UNITY_EDITOR 排除後的 missing type
    /// + Mono preprocessor verbatim-string bug 等 Editor compile 看不出來的問題。
    /// </summary>
    public class Cmd_BuildPlayerCheck : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "BuildPlayerCheck";

        public override string ShortDescription =>
            "Build Player scripts-only using active BuildProfile to catch CS errors that Editor compile misses.";

        public override string ArgsSchema =>
            "mode=scripts_only|full (default scripts_only — fast, 不打 addressables/asset bundle)\n" +
            "broadcast=true|false (default false — 完成後是否 tavern post; QA mode 預設 false 不洗版)";

        public override string ExampleArgs => "mode=scripts_only";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string mode = GetArg(args, "mode", "scripts_only").ToLowerInvariant();
            bool broadcast = GetArg(args, "broadcast", "false").ToLowerInvariant() == "true";

            // Step 1: 取當前 Build Profile (per Tim 指定 — Unity 6 API)
            BuildProfile activeProfile = null;
            try
            {
                activeProfile = BuildProfile.GetActiveBuildProfile();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildPlayerCheck] GetActiveBuildProfile fail: {ex.Message}");
            }

            string profileName = activeProfile != null ? activeProfile.name : "(no active profile — fallback to EditorUserBuildSettings)";

            // Step 2: 取 scenes list (priority: profile.scenes → EditorBuildSettings 已啟用 scenes)
            string[] scenePaths;
            if (activeProfile != null && activeProfile.scenes != null && activeProfile.scenes.Length > 0)
            {
                scenePaths = activeProfile.scenes.Select(s => s.path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            }
            else
            {
                scenePaths = EditorBuildSettings.scenes
                    .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                    .Select(s => s.path)
                    .ToArray();
            }

            if (scenePaths.Length == 0)
            {
                WriteResult($"# ❌ BuildPlayerCheck — No Scenes\n\n" +
                            $"- Active profile: `{profileName}`\n" +
                            $"- Profile scenes: 0; EditorBuildSettings enabled scenes: 0\n" +
                            $"- 修法: 在 Build Profile 加入 scenes, 或在 EditorBuildSettings.scenes 啟用至少 1 個 scene。");
                return;
            }

            // Step 3: 組 BuildPlayerOptions
            // 輸出到系統 temp folder (Unity reject Library/Temp/ 等內部目錄為 build output);
            // 用 system temp 避免污染專案目錄 + 跨 platform 通用。
            string outputDir = Path.Combine(Path.GetTempPath(), "UCL_AgentBuildCheck",
                System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            try { Directory.CreateDirectory(outputDir); } catch { /* ignore */ }

            BuildTarget target = activeProfile != null
                ? ResolveProfileBuildTarget(activeProfile)
                : EditorUserBuildSettings.activeBuildTarget;

            string outputExt = GetExtensionForTarget(target);
            string outputPath = Path.Combine(outputDir, $"PlayerCheck{outputExt}");

            BuildOptions buildOptions = BuildOptions.None;
            if (mode == "scripts_only")
            {
                buildOptions |= BuildOptions.BuildScriptsOnly;
            }
            // mode=full → 不加 BuildScriptsOnly, 跑完整 Player Build (慢)
            // 不支援 mode=addressables (不在本 Cmd scope)
            if (mode == "addressables")
            {
                WriteResult("# ❌ BuildPlayerCheck — mode=addressables not supported\n\n" +
                            "本 Cmd 只支援 scripts_only / full。Addressables 走 AddressableAssetSettings.BuildPlayerContent(), " +
                            "需要另一個 Cmd 包裝 (future work)。");
                return;
            }

            var opts = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outputPath,
                target = target,
                options = buildOptions,
            };

            Debug.Log($"[BuildPlayerCheck] start — profile=`{profileName}`, mode={mode}, target={target}, scenes={scenePaths.Length}, output={outputPath}");
            double startSeconds = EditorApplication.timeSinceStartup;

            BuildReport report = null;
            string fatalException = null;
            try
            {
                report = BuildPipeline.BuildPlayer(opts);
            }
            catch (Exception ex)
            {
                fatalException = $"{ex.GetType().Name}: {ex.Message}";
                Debug.LogException(ex);
            }

            double durSeconds = EditorApplication.timeSinceStartup - startSeconds;

            // Step 4: handle fatal exception (BuildPlayer throw → report=null)
            if (report == null)
            {
                WriteResult($"# ❌ BuildPlayerCheck — Fatal Exception\n\n" +
                            $"- Active profile: `{profileName}`\n" +
                            $"- Mode: `{mode}`\n" +
                            $"- Target: `{target}`\n" +
                            $"- Scenes: {scenePaths.Length}\n" +
                            $"- Output (attempted): `{outputPath}`\n" +
                            $"- Duration: {durSeconds:F1}s\n\n" +
                            $"## Exception\n\n```\n{fatalException ?? "(unknown — report null without exception)"}\n```\n\n" +
                            $"BuildPipeline.BuildPlayer 拋例外, report=null。常見原因: 輸出路徑非法 (Unity 拒 Library/Temp) / scenes 無效 / target 不支援。");
                return;
            }

            var summary = report.summary;
            BuildResult result = summary.result;

            // 收集 Error / Exception 訊息 (Warning 不列, 避免噪音)
            var errors = new List<(string step, string content)>();
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Exception || msg.type == LogType.Assert)
                    {
                        errors.Add((step.name ?? "(unnamed step)", msg.content ?? ""));
                    }
                }
            }

            // Step 5: 寫 markdown 報告
            var sb = new System.Text.StringBuilder();
            string resultIcon = result == BuildResult.Succeeded ? "✅" : (result == BuildResult.Failed ? "❌" : "⚠️");
            sb.AppendLine($"# {resultIcon} BuildPlayerCheck — `{result}`");
            sb.AppendLine();
            sb.AppendLine($"- Active Build Profile: `{profileName}`");
            sb.AppendLine($"- Mode: `{mode}` ({(mode == "scripts_only" ? "BuildOptions.BuildScriptsOnly" : "full BuildPlayer")})");
            sb.AppendLine($"- Target: `{target}`");
            sb.AppendLine($"- Scenes: {scenePaths.Length}");
            sb.AppendLine($"- Duration: {durSeconds:F1}s");
            sb.AppendLine($"- Output: `{outputPath}`");
            sb.AppendLine($"- Errors: **{errors.Count}** | Total steps: {report.steps.Length}");
            sb.AppendLine();

            if (errors.Count > 0)
            {
                sb.AppendLine("## Errors");
                sb.AppendLine();
                sb.AppendLine("| # | Step | Message |");
                sb.AppendLine("|---|---|---|");
                int i = 1;
                foreach (var (step, content) in errors.Take(30))
                {
                    string oneLine = (content ?? "").Replace("\n", " ").Replace("|", "\\|");
                    if (oneLine.Length > 300) oneLine = oneLine.Substring(0, 300) + "…";
                    sb.AppendLine($"| {i} | `{step}` | {oneLine} |");
                    i++;
                }
                if (errors.Count > 30)
                {
                    sb.AppendLine();
                    sb.AppendLine($"_(... {errors.Count - 30} more errors suppressed)_");
                }
            }
            else
            {
                sb.AppendLine("✨ **No errors** — Player script compile pass.");
            }

            WriteResult(sb.ToString());

            // Optional broadcast (default off — QA mode 不洗版)
            if (broadcast)
            {
                try
                {
                    Debug.Log($"[BuildPlayerCheck] result={result}, errors={errors.Count}, dur={durSeconds:F1}s, broadcast=true (handled by caller via tavern post)");
                }
                catch { /* ignore */ }
            }

            Debug.Log($"[BuildPlayerCheck] done — result={result}, errors={errors.Count}, dur={durSeconds:F1}s");
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        static BuildTarget ResolveProfileBuildTarget(BuildProfile profile)
        {
            // BuildProfile 對應的 NamedBuildTarget / BuildTarget 不一定直接 expose;
            // 走 reflection 或 fallback 到 activeBuildTarget. Unity 6 內部欄位 m_BuildTarget.
            try
            {
                var type = typeof(BuildProfile);
                var field = type.GetField("m_BuildTarget",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    var val = field.GetValue(profile);
                    if (val is BuildTarget bt) return bt;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildPlayerCheck] reflect profile target fail: {ex.Message}, fallback to activeBuildTarget");
            }
            return EditorUserBuildSettings.activeBuildTarget;
        }

        static string GetExtensionForTarget(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return ".exe";
                case BuildTarget.StandaloneOSX:
                    return ".app";
                case BuildTarget.StandaloneLinux64:
                    return ".x86_64";
                default:
                    return "";
            }
        }

        static void WriteResult(string markdown)
        {
            try
            {
                // 對齊 Cmd_Bartender / 別人寫 _last_op.md 的路徑
                string tavernDir = Path.Combine(
                    Directory.GetParent(Application.dataPath).Parent.FullName,
                    "AgentCommands", "ChatTavern");
                Directory.CreateDirectory(tavernDir);
                string path = Path.Combine(tavernDir, "_last_op.md");
                File.WriteAllText(path, markdown);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildPlayerCheck] WriteResult fail: {ex.Message}");
            }
        }
    }
}
#endif
