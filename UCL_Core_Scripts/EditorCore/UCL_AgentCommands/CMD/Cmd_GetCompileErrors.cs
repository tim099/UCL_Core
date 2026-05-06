
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：把 UCL_CompileErrorTracker 寫的 .compile_status.json 內容讀回並印出 / 寫 md 報告。
// 物理意義：給 healthy 狀態下的 batch 用 — 例如 agent 想跑「ExportNotes → 確認 compile 狀態 → 進行下一步」
//          這種 chained workflow，可以一次 submit 多個 cmd 得到完整資訊。
//          注意：當其他 assembly 編譯失敗時，本 Cmd handler 自己也載不進來（因為 Registry 反射發現失敗）→
//          那種情境請改用 Tools~/AgentCommands/check_compile.py（standalone Python，不依賴 Cmd 系統）。
// 數值影響：純讀檔；可選 outputPath 寫一份報告檔。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：讀 <see cref="UCL_CompileErrorTracker"/> 寫入的 JSON，輸出 markdown 報告。
    ///
    /// 注意「chicken-and-egg」：
    /// <list type="bullet">
    ///   <item>當其他 assembly 編譯失敗時，本 Cmd 也載不進 Registry → 無法觸發。</item>
    ///   <item>那種情境改用 <c>Tools~/AgentCommands/check_compile.py</c>（standalone Python，直接讀 JSON，不依賴 Cmd 系統）。</item>
    /// </list>
    ///
    /// 參數：
    /// <list type="bullet">
    ///   <item><c>errorsOnly</c>（選填，預設 <c>false</c>）：只列 Error</item>
    ///   <item><c>format</c>（選填，預設 <c>md</c>）：<c>md</c>（人類讀，含摘要 + 表格） / <c>json</c>（直接 passthrough 原始 JSON）</item>
    ///   <item><c>outputPath</c>（選填）：輸出檔（git-root 相對）；不指定則只印 Console</item>
    /// </list>
    /// </summary>
    // touched 2026-05-06: trigger fresh compile so UCL_CompileErrorTracker captures events
    public class Cmd_GetCompileErrors : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GetCompileErrors";

        public override string ShortDescription =>
            "Read UCL_CompileErrorTracker JSON and report Unity compile status. For broken-assembly cases, use Tools~/check_compile.py instead (standalone Python).";

        public override string ArgsSchema =>
            "errorsOnly=true|false (default false) — only list Error messages\n" +
            "format=md|json (default md)\n" +
            "outputPath=Output file (git-root-relative; if omitted, prints to Console)";

        public override string ExampleArgs => "errorsOnly=true;format=md";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_GetCompileErrors.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            bool errorsOnly = string.Equals(GetArg(args, "errorsOnly", "false"), "true",
                                            StringComparison.OrdinalIgnoreCase);
            string format = (GetArg(args, "format", "md") ?? "md").ToLowerInvariant();
            string outputPath = GetArg(args, "outputPath", null);

            string statusPath = UCL_CompileErrorTracker.GetOutputPath();
            if (!File.Exists(statusPath))
            {
                throw new FileNotFoundException(
                    $"Compile status file not found at {statusPath}. " +
                    "Tracker has not run yet — trigger any compile first.");
            }

            string raw = File.ReadAllText(statusPath);

            string content = format == "json"
                ? raw   // JSON 模式直接 passthrough（已是 valid JSON）
                : RenderMarkdown(raw, errorsOnly);

            if (!string.IsNullOrEmpty(outputPath))
            {
                string gitRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."))
                                     .Replace('\\', '/');
                string absOut = Path.IsPathRooted(outputPath)
                    ? outputPath : Path.Combine(gitRoot, outputPath);
                string outDir = Path.GetDirectoryName(absOut);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }
                File.WriteAllText(absOut, content, new UTF8Encoding(false));
                Debug.Log($"[Cmd:GetCompileErrors] → {outputPath}");
            }
            else
            {
                Debug.Log($"[Cmd:GetCompileErrors]\n{content}");
            }

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // Markdown 渲染（hand-parse JSON — schema 由 Tracker 控制，可預期）
        // 物理意義：避免引入第三方 JSON lib，且 schema 簡單足以 regex 抽取
        // 數值影響：純字串轉換
        // ===========================================================
        static string RenderMarkdown(string rawJson, bool errorsOnly)
        {
            int totalErrors = ExtractInt(rawJson, "total_errors");
            int totalWarnings = ExtractInt(rawJson, "total_warnings");
            int totalMsgs = ExtractInt(rawJson, "total_messages");
            string ts = ExtractString(rawJson, "timestamp");
            double duration = ExtractDouble(rawJson, "duration_seconds");
            bool inProg = ExtractBool(rawJson, "in_progress");

            var sb = new StringBuilder();
            sb.AppendLine("# 🔧 Unity Compile Status");
            sb.AppendLine();
            if (inProg)
            {
                sb.AppendLine("> ⏳ **Compile in progress** — 結果尚未定案，請稍後再查。");
                sb.AppendLine();
            }
            sb.AppendLine($"- Timestamp: `{ts}`");
            sb.AppendLine($"- Duration: {duration:0.00}s");
            sb.AppendLine($"- **Errors: {totalErrors}**");
            sb.AppendLine($"- Warnings: {totalWarnings}");
            sb.AppendLine($"- Total messages: {totalMsgs}");
            if (errorsOnly) sb.AppendLine("- (filter: errors only)");
            sb.AppendLine();

            if (totalMsgs == 0)
            {
                if (totalErrors == 0 && totalWarnings == 0)
                {
                    sb.AppendLine("✅ **Clean compile.**");
                }
                return sb.ToString();
            }

            // 解析 messages array — Tracker 寫的格式每筆是單行
            // {"assembly":"...","file":"...","line":N,"column":N,"type":"...","message":"..."}
            var msgs = ExtractMessages(rawJson);
            if (errorsOnly)
            {
                msgs = msgs.FindAll(m => m.Type == "Error");
            }
            // 排序：Error 在前
            msgs.Sort((a, b) =>
            {
                int ta = a.Type == "Error" ? 0 : 1;
                int tb = b.Type == "Error" ? 0 : 1;
                if (ta != tb) return ta - tb;
                return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            });

            sb.AppendLine("| # | Type | Assembly | File | Line | Message |");
            sb.AppendLine("|---|---|---|---|---|---|");
            for (int i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                string emoji = m.Type == "Error" ? "❌" : "⚠";
                string msg = m.Message ?? "";
                if (msg.Length > 200) msg = msg.Substring(0, 200) + "…";
                msg = msg.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
                sb.AppendLine($"| {i + 1} | {emoji} {m.Type} | `{m.Assembly}` | `{m.File}` | {m.Line} | {msg} |");
            }
            return sb.ToString();
        }

        // ===========================================================
        // 簡易 JSON 抽取（只支援本檔需要的 schema）
        // ===========================================================
        static int ExtractInt(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        }

        static double ExtractDouble(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            return m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : 0;
        }

        static bool ExtractBool(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }

        static string ExtractString(string json, string key)
        {
            // 抓 "key":"value"，value 內不含 \" escape 已足以覆蓋 Tracker 寫的 timestamp 格式
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        // 解析 messages array 內的每個 object
        static List<MsgEntry> ExtractMessages(string json)
        {
            var result = new List<MsgEntry>();
            // 找到 "messages": [ 之後的內容
            int idx = json.IndexOf("\"messages\"", StringComparison.Ordinal);
            if (idx < 0) return result;
            int start = json.IndexOf('[', idx);
            int end = json.LastIndexOf(']');
            if (start < 0 || end <= start) return result;
            string body = json.Substring(start + 1, end - start - 1);

            // Tracker 寫每筆 message 都是一行 — 用 regex 抓單行 object
            // 每行：{"assembly":"...","file":"...","line":N,"column":N,"type":"...","message":"..."}
            var rx = new Regex("\\{\"assembly\":\"([^\"]*)\",\"file\":\"([^\"]*)\",\"line\":(-?\\d+),\"column\":(-?\\d+),\"type\":\"([^\"]*)\",\"message\":\"((?:\\\\\"|[^\"])*)\"\\}");
            foreach (Match m in rx.Matches(body))
            {
                result.Add(new MsgEntry
                {
                    Assembly = m.Groups[1].Value,
                    File = m.Groups[2].Value,
                    Line = int.TryParse(m.Groups[3].Value, out var ln) ? ln : 0,
                    Column = int.TryParse(m.Groups[4].Value, out var col) ? col : 0,
                    Type = m.Groups[5].Value,
                    Message = UnescapeJsonString(m.Groups[6].Value),
                });
            }
            return result;
        }

        static string UnescapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\")
                    .Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        class MsgEntry
        {
            public string Assembly;
            public string File;
            public int Line;
            public int Column;
            public string Type;
            public string Message;
        }
    }
}
#endif
