// 區塊職責：T48 — agent 自律記錄 lesson（撞坑 / debug 教訓 / workflow a-ha）的指令入口
// 物理意義：append 一行 JSONL entry 到 AgentCommands/Lessons/lessons.jsonl；跨 agent 共享 raw audit log
// 數值影響：純檔案 append 操作；body 重複偵測 skip（防重複污染）；不動 Treasury / messages
// 設計取捨：
//   - jsonl 格式（一行一 lesson）→ git diff / merge 友善 + tail 命令容易
//   - 不自動 promote 進 SKILL.md curated section（必須人工 review，避免膨脹）
//   - dedupe 走 body 字串完全一致檢查；不做 fuzzy match（v2 backlog）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Lessons
{
    /// <summary>
    /// T48 — Agent Lesson 紀錄指令。配合 agent-lessons-log skill 用。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// python AgentCommands/run_cmd.py run NoteLesson \
    ///   --arg body="UCL_Json bool 用 True/False 字串非 JSON bool" \
    ///   --arg actor="claude-da-xiaojie" \
    ///   --arg category="bug"
    /// </code>
    ///
    /// <para>磁碟結構：</para>
    /// <code>
    /// AgentCommands/Lessons/
    ///   ├── lessons.jsonl       (audit log; 一行一 lesson; ts/actor/category/body 欄位)
    ///   └── _last_lesson.md     (最後一筆 confirm; 給 caller 看寫入結果)
    /// </code>
    /// </summary>
    public class Cmd_NoteLesson : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "NoteLesson";

        public override string ShortDescription =>
            "Append agent lesson to AgentCommands/Lessons/lessons.jsonl (跨 agent 共享 audit log).";

        public override string ArgsSchema =>
            "body=Lesson 短句精華，建議 < 30 字 (required) | " +
            "actor=Agent id 來源標記 (default: 'unknown') | " +
            "category=Lesson 分類 bug/design/workflow/debug/test 等 (default: 'general')";

        public override string ExampleArgs =>
            "body=UCL_Json bool 用 True/False 字串非 JSON bool;actor=claude-da-xiaojie;category=bug";

        public override string HelpURL =>
            "ucl_core:Skills~/agent-lessons-log/SKILL.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string body = GetArg(args, "body", "").Trim();
            string actor = GetArg(args, "actor", "unknown").Trim();
            string category = GetArg(args, "category", "general").Trim();

            if (string.IsNullOrWhiteSpace(body))
            {
                throw new Exception("[NoteLesson] body 必填（傳 --arg body=\"<短句精華>\"）");
                return;
            }

            // Path: <DataRoot>/Lessons/ (走可 override 資料根;預設 = RepoRoot/AgentCommands/Lessons)
            string lessonsDir = Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "Lessons");
            try
            {
                Directory.CreateDirectory(lessonsDir);
            }
            catch (Exception ex)
            {
                throw new Exception($"[NoteLesson] 建立 Lessons 目錄失敗：{ex.Message}");
                return;
            }

            string jsonlPath = Path.Combine(lessonsDir, "lessons.jsonl");
            string confirmMdPath = Path.Combine(lessonsDir, "_last_lesson.md");

            // 區塊職責：dedupe 檢查 — body 完全一致已存在則 skip
            // 物理意義：避免 agent 重複呼叫產生噪音；簡單字串比對不做 fuzzy
            // 數值影響：每次 append 前 read all lines（lessons 通常 < 1000 筆，cost OK）
            string trimmedBody = body;
            string escapedBodyForCheck = "\"body\":" + ToJsonString(trimmedBody);
            bool isDup = false;
            if (File.Exists(jsonlPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(jsonlPath, Encoding.UTF8))
                    {
                        if (line.Contains(escapedBodyForCheck))
                        {
                            isDup = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NoteLesson] dedupe check fail (繼續 append)：{ex.Message}");
                }
            }

            if (isDup)
            {
                string dupMd =
                    $"# 🔁 Lesson 重複，skip\n\n" +
                    $"- **body**: {body}\n" +
                    $"- **actor**: `{actor}`\n" +
                    $"- **category**: `{category}`\n\n" +
                    $"已存在於 `{Rel(jsonlPath)}`，未重複 append（dedupe 防噪音）。\n" +
                    $"如要 force append，請改寫 body 內容。\n";
                File.WriteAllText(confirmMdPath, dupMd, new UTF8Encoding(false));
                Debug.Log($"[NoteLesson] dup skip: '{Truncate(body, 40)}' (actor={actor})");
                return;
            }

            // 構造 JSONL entry — 走 UTC ISO 8601 時戳 + manual JSON build（避免引依賴）
            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ts\":").Append(ToJsonString(ts)).Append(',');
            sb.Append("\"actor\":").Append(ToJsonString(actor)).Append(',');
            sb.Append("\"category\":").Append(ToJsonString(category)).Append(',');
            sb.Append("\"body\":").Append(ToJsonString(body));
            sb.Append('}');
            sb.Append('\n');

            try
            {
                File.AppendAllText(jsonlPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new Exception($"[NoteLesson] append jsonl 失敗：{ex.Message}");
                return;
            }

            // 寫 _last_lesson.md 給 caller 視覺確認
            string confirmMd =
                $"# 📝 Lesson noted ({category})\n\n" +
                $"- **ts**: `{ts}`\n" +
                $"- **actor**: `{actor}`\n" +
                $"- **category**: `{category}`\n" +
                $"- **body**: {body}\n\n" +
                $"appended → `{Rel(jsonlPath)}`\n\n" +
                $"---\n\n" +
                $"後續：定期 review jsonl tail，將高價值 lesson promote 進 `Skills~/agent-lessons-log/SKILL.md` curated list（手動 edit）。\n";
            // 區塊職責：本人若正在自由時間中，回傳值多附一段流程提示（Tim 2026-08-18）。
            // 物理意義：知識沉澱是自由時間活動之一，而**記完一筆 lesson 正是最容易斷線的位置** ——
            //          產物剛落地、注意力在產物上，而換骰指令在上一份回傳檔裡。
            //          走 helper 自己查 session，而不是把本 Cmd 的流程抽離讓自由時間層重跑 ——
            //          後者會產生第二條流程，而兩條漂移時兩邊都不報錯。
            // 數值影響：不在自由時間時**一個字都不加**（本 Cmd 平常也會被工作流程用到，不該多噪音）。
            // 邊界：actor 是自由字串（可能是 agent id 不是 persona）—— 查不到 session 就等於不在，
            //      不會誤印。要精準對上請帶 persona 形式的 actor。
            var aConfirmSb = new System.Text.StringBuilder(confirmMd);
            UCL_FreeTimeHint.Append(aConfirmSb, actor);
            confirmMd = aConfirmSb.ToString();
            try
            {
                File.WriteAllText(confirmMdPath, confirmMd, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NoteLesson] confirm md 寫入失敗（jsonl 已 append 不受影響）：{ex.Message}");
            }

            Debug.Log($"[NoteLesson] +1 lesson by {actor} ({category}): {Truncate(body, 50)}");
        }

        // ===========================================================
        // Helper: minimal JSON string escape（手寫避免 Newtonsoft.Json 依賴）
        // 物理意義：把 string 包成 JSON 合法 quoted string，escape \" \\ \n \r \t
        // 數值影響：純 string transformation；Unicode 不額外 escape
        // ===========================================================
        static string ToJsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }

        static string Rel(string absPath)
        {
            try
            {
                string root = UCL_RepoPath.RepoRoot.Replace('\\', '/');
                string p = absPath.Replace('\\', '/');
                if (p.StartsWith(root)) return p.Substring(root.Length).TrimStart('/');
                return p;
            }
            catch { return absPath; }
        }
    }
}
#endif
