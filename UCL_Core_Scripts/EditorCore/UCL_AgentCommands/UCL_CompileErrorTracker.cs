
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：把 Unity 每次編譯的錯誤 / 警告持久化成 JSON，讓 agent 可以離線讀取詳細內容。
// 物理意義：[InitializeOnLoad] + CompilationPipeline 事件是 Editor 內偵測編譯狀態的唯一可靠管道。
//          一旦其他 assembly 編譯失敗，連帶 Cmd handler 也載不進來 → 沒辦法靠 Cmd 查詢，
//          所以本 Tracker 故意放在 UCL_Core/Editor/ 獨立 assembly，在收到事件當下立刻
//          序列化成 JSON 寫進 git-root/AgentCommands/.compile_status.json，
//          配套 Python 工具 (check_compile.py) 直接讀檔即可，不需要 Editor 還能跑 Cmd。
// 數值影響：每次編譯結束會覆寫 .compile_status.json；不修改任何遊戲資料。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// 編譯狀態追蹤器：訂閱 <see cref="CompilationPipeline"/> 三大事件
    /// （compilationStarted / assemblyCompilationFinished / compilationFinished），
    /// 把每次編譯的所有錯誤 / 警告聚合後寫入 JSON。
    ///
    /// 輸出檔案：<c>&lt;gitRoot&gt;/AgentCommands/.compile_status.json</c>（隱藏檔但 git status 看得到）
    ///
    /// JSON Schema：
    /// <code>
    /// {
    ///   "tracker": "UCL_CompileErrorTracker",
    ///   "timestamp": "2026-05-06T...",
    ///   "duration_seconds": 5.2,
    ///   "in_progress": false,
    ///   "total_errors": 3,
    ///   "total_warnings": 12,
    ///   "messages": [
    ///     {
    ///       "assembly": "Assembly-CSharp",
    ///       "file": "Assets/Scripts/Foo.cs",
    ///       "line": 42,
    ///       "column": 15,
    ///       "type": "Error",
    ///       "message": "CS0103: ..."
    ///     }
    ///   ]
    /// }
    /// </code>
    /// </summary>
    [InitializeOnLoad]
    internal static class UCL_CompileErrorTracker
    {
        // 區塊職責：累積本次編譯的訊息（橫跨多個 assemblyCompilationFinished 事件）
        // 物理意義：Unity 是 per-assembly 編譯，所有 assembly 跑完才會觸發 compilationFinished
        // 數值影響：static 欄位；compilationStarted 時清空
        static readonly List<CompileMessage> s_Messages = new List<CompileMessage>();
        static DateTime s_StartTime;
        static bool s_InProgress;

        static UCL_CompileErrorTracker()
        {
            // 訂閱事件 — InitializeOnLoad 確保 Editor 啟動 / domain reload 時都會註冊上
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // 區塊職責：首次安裝 / domain reload 後若還沒有 status 檔，寫一份 placeholder
            // 物理意義：[InitializeOnLoad] 是在「上一次成功編譯」之後才跑的 → 那次 compile 的事件早就錯過。
            //          沒 placeholder 的話 check_compile.py / Cmd_GetCompileErrors 會誤以為 Tracker 沒運作。
            //          有 placeholder 至少能告訴使用者「Tracker 已載入但還沒看到任何編譯」。
            // 數值影響：只在檔案不存在時寫，不會覆蓋既有資料
            try
            {
                string outPath = GetOutputPath();
                if (!File.Exists(outPath))
                {
                    string outDir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                    }
                    File.WriteAllText(outPath,
                        "{\n" +
                        "  \"tracker\": \"UCL_CompileErrorTracker\",\n" +
                        "  \"timestamp\": \"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\",\n" +
                        "  \"duration_seconds\": 0,\n" +
                        "  \"in_progress\": false,\n" +
                        "  \"total_errors\": 0,\n" +
                        "  \"total_warnings\": 0,\n" +
                        "  \"total_messages\": 0,\n" +
                        "  \"messages\": [],\n" +
                        "  \"_note\": \"Placeholder — Tracker just loaded, no compile event captured yet. Edit any .cs to trigger one.\"\n" +
                        "}\n",
                        new UTF8Encoding(false));
                }
            }
            catch { /* placeholder 寫失敗不擋 Editor 啟動 */ }
        }

        // ===========================================================
        // 事件處理
        // ===========================================================

        // 區塊職責：新一輪編譯開始 — 清空 messages、記下開始時間、寫一次「in_progress」狀態
        static void OnCompilationStarted(object _)
        {
            s_Messages.Clear();
            s_StartTime = DateTime.Now;
            s_InProgress = true;
            // 立刻寫一次 in_progress=true 的標記，方便 Python 端「等待編譯完成」的輪詢
            WriteResult(durationSeconds: 0);
        }

        // 區塊職責：單一 assembly 編譯結束 — 收集其所有 message
        // 物理意義：Unity 是 per-assembly 編譯，每個 assembly 結束就會收到一次回調，
        //          含該 assembly 的所有 error / warning。
        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;
            string asmName = Path.GetFileNameWithoutExtension(assemblyPath ?? "");
            foreach (var m in messages)
            {
                s_Messages.Add(new CompileMessage
                {
                    Assembly = asmName,
                    File = NormalizePath(m.file),
                    Line = m.line,
                    Column = m.column,
                    Type = m.type.ToString(),
                    Message = m.message ?? "",
                });
            }
        }

        // 區塊職責：所有 assembly 跑完 — 寫最終結果 JSON
        // 物理意義：此時 s_Messages 內含本輪所有 message，可以做總計並落地
        static void OnCompilationFinished(object _)
        {
            s_InProgress = false;
            double dur = (DateTime.Now - s_StartTime).TotalSeconds;
            WriteResult(durationSeconds: dur);
        }

        // ===========================================================
        // 序列化 + 寫檔
        // 物理意義：手刻 JSON 避免依賴第三方 lib；schema 簡單足以 Python 端用 json.load 讀
        // 數值影響：覆寫 .compile_status.json
        // ===========================================================
        static void WriteResult(double durationSeconds)
        {
            int errors = 0, warnings = 0;
            foreach (var m in s_Messages)
            {
                if (m.Type == "Error") errors++;
                else if (m.Type == "Warning") warnings++;
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"tracker\": \"UCL_CompileErrorTracker\",");
            sb.AppendLine($"  \"timestamp\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\",");
            sb.AppendLine($"  \"duration_seconds\": {durationSeconds:0.000},");
            sb.AppendLine($"  \"in_progress\": {(s_InProgress ? "true" : "false")},");
            sb.AppendLine($"  \"total_errors\": {errors},");
            sb.AppendLine($"  \"total_warnings\": {warnings},");
            sb.AppendLine($"  \"total_messages\": {s_Messages.Count},");
            sb.AppendLine($"  \"messages\": [");
            for (int i = 0; i < s_Messages.Count; i++)
            {
                var m = s_Messages[i];
                sb.Append("    {");
                sb.Append($"\"assembly\":\"{Esc(m.Assembly)}\",");
                sb.Append($"\"file\":\"{Esc(m.File)}\",");
                sb.Append($"\"line\":{m.Line},");
                sb.Append($"\"column\":{m.Column},");
                sb.Append($"\"type\":\"{Esc(m.Type)}\",");
                sb.Append($"\"message\":\"{Esc(m.Message)}\"");
                sb.Append("}");
                if (i < s_Messages.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            try
            {
                string outPath = GetOutputPath();
                string outDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_CompileErrorTracker] write failed: {e.Message}");
            }
        }

        // 區塊職責：取得輸出 JSON 的絕對路徑
        // 物理意義：放 git-root/AgentCommands/.compile_status.json — 與 queue.json / pending.trigger 同層
        //          repo root 集中由 UCL_RepoPath 解析（git-walk，與 Python run_cmd.py 對齊）
        // 數值影響：純路徑計算
        public static string GetOutputPath()
        {
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, ".compile_status.json").Replace('\\', '/');
        }

        // 區塊職責：把絕對路徑壓成 git-root 相對路徑（讓 JSON 內檔案 path 跨機器穩定）
        // 物理意義：Unity CompilerMessage.file 可能是絕對路徑或 Assets/ 相對；統一成 git-root 相對
        // 數值影響：純字串轉換
        static string NormalizePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string p = raw.Replace('\\', '/');
            string gitRoot = UCL_RepoPath.RepoRoot.TrimEnd('/');
            if (p.StartsWith(gitRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return p.Substring(gitRoot.Length + 1);
            }
            // Assets/ 開頭的轉成 CardGame/Assets/...（git-root 相對）
            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "CardGame/" + p;
            }
            return p;
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        // ===========================================================
        // 內部資料結構
        // ===========================================================
        class CompileMessage
        {
            public string Assembly;
            public string File;
            public int Line;
            public int Column;
            public string Type;     // "Error" / "Warning" / "Info"
            public string Message;
        }
    }
}
#endif
