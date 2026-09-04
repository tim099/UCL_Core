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
    /// senate ucmd run NoteLesson \
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
            "actor=Agent id 來源標記 (default: --persona，兩者都沒有才 'unknown') | " +
            "category=Lesson 分類 bug/design/workflow/debug/test 等 (default: 'general') | " +
            "title=一行標題 (optional，給的話進 jsonl) | " +
            "tags=逗號分隔標籤 (optional，給的話以陣列進 jsonl)";

        public override string ExampleArgs =>
            "body=UCL_Json bool 用 True/False 字串非 JSON bool;actor=claude-da-xiaojie;category=bug";

        public override string HelpURL =>
            "ucl_core:Skills~/agent-lessons-log/SKILL.md";

        // ===========================================================
        // 區塊職責：本 Cmd **真的會消化**的參數名（TASK-0078／BUG-42）。
        // 物理意義：拿它反過來擋「傳了、回 Success、但沒有任何一層讀過」的欄位。
        //          🩸 那正是 BUG-42 的形狀：`--arg title=… --arg tags=…` 全被靜默丟棄，
        //          而回傳檔印得完整、jsonl 也真的多一行 ⇒ **沒有任何一格會說它掉了東西**。
        // ⚠ 這裡刻意不走 ArgsSpec：那個型別只表達得出 Required 與 Aliases
        //   （它自己的檔頭寫明「刻意不收 optional，沒人用的欄位一定會爛」），
        //   表達不了「完整字彙表」。⇒ 字彙表由**唯一會用它的人**（本 handler）自己持有。
        // 邊界：`_` 開頭是框架注入的內部鍵（`_cmd_id` / `_timeout_sec` / `_caller_client`…），不歸本表管。
        // ===========================================================
        static readonly HashSet<string> kKnownArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "body", "actor", "category", "title", "tags",
            "persona",   // --persona 旗標注入；WriteConfirm 的 per-persona 鏡寫在用
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            // 區塊職責：先擋沒人讀的參數 —— **在 append 之前**（BUG-42／TASK-0078）。
            // 物理意義：一旦 append 成功，「欄位掉了」就沒有任何一層會喊 ⇒ 拒收必須發生在寫入前。
            var aUnknown = new List<string>();
            if (args != null)
            {
                foreach (var k in args.Keys)
                {
                    if (string.IsNullOrEmpty(k) || k[0] == '_') continue;   // 框架注入的內部鍵
                    if (!kKnownArgs.Contains(k)) aUnknown.Add(k);
                }
            }
            if (aUnknown.Count > 0)
            {
                aUnknown.Sort(StringComparer.Ordinal);
                throw new Exception(
                    $"[NoteLesson] 不認得的參數：{string.Join(", ", aUnknown)}" +
                    $"（本 Cmd 只消化：body, actor, category, title, tags）。" +
                    "⛔ 刻意擋下而不是忽略 —— 靜默丟欄位時回傳檔跟成功長得一模一樣（BUG-42）。");
            }

            string body = GetArg(args, "body", "").Trim();
            // actor 沒給就退回 --persona（旗標注入，WriteConfirm 一直在用它）——
            // 🩸 BUG-42：舊版直接落 "unknown"，於是 `--persona summit` 記的 lesson 掛在 unknown 名下。
            string actor = GetArg(args, "actor", "").Trim();
            if (actor.Length == 0) actor = GetArg(args, "persona", "").Trim();
            if (actor.Length == 0) actor = "unknown";
            string category = GetArg(args, "category", "general").Trim();
            string title = GetArg(args, "title", "").Trim();
            var aTags = new List<string>();
            foreach (var t in GetArg(args, "tags", "").Split(','))
            {
                string tt = t.Trim();
                if (tt.Length > 0 && !aTags.Contains(tt)) aTags.Add(tt);
            }

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
                WriteConfirm(args, confirmMdPath, dupMd);
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
            // 選填欄位：**沒給就不寫這個鍵**（不寫 "" / []）——
            // 「沒給標題」與「標題是空字串」是兩件事，壓成一件的話舊行讀起來像有人清空過它。
            if (title.Length > 0) sb.Append(',').Append("\"title\":").Append(ToJsonString(title));
            if (aTags.Count > 0)
            {
                sb.Append(',').Append("\"tags\":[");
                for (int i = 0; i < aTags.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ToJsonString(aTags[i]));
                }
                sb.Append(']');
            }
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
                (title.Length > 0 ? $"- **title**: {title}\n" : "") +
                (aTags.Count > 0 ? $"- **tags**: `{string.Join("`, `", aTags)}`\n" : "") +
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
                WriteConfirm(args, confirmMdPath, confirmMd);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NoteLesson] confirm md 寫入失敗（jsonl 已 append 不受影響）：{ex.Message}");
            }

            Debug.Log($"[NoteLesson] +1 lesson by {actor} ({category}): {Truncate(body, 50)}");
        }

        // ===========================================================
        // 區塊職責：確認檔落地 —— 全域 `_last_lesson.md` ＋ per-persona 鏡寫（TASK-0059 第五宿主）。
        // 物理意義：全域檔**保留、內容不變** —— run_cmd 的 fail-detection（CMD_OUTPUT_FILES
        //   的 "notelesson" 項）讀它的 mtime＋首行 marker，stub 化＝拆掉活的偵測（同 _last_op 的偏離）。
        //   閱讀通道遷 per-persona：兩人先後記 lesson，各自的回傳檔互不覆蓋，
        //   run_cmd 印的「📄 回傳檔」指向本次這個人（ReportOutputFile）。
        // ⚠ persona 從 args 拿（--persona 旗標注入）；拿不到（後台頁等非 queue 路徑）⇒ 只寫全域，與舊版全等。
        // ===========================================================
        static void WriteConfirm(Dictionary<string, string> iArgs, string iGlobalPath, string iContent)
        {
            File.WriteAllText(iGlobalPath, iContent, new UTF8Encoding(false));
            try
            {
                string aPersona = iArgs != null && iArgs.TryGetValue("persona", out var p) ? p.Trim() : "";
                if (aPersona.Length == 0) return;
                string aPayload = UCL_LettersPath.CmdPayload(aPersona, "notelesson", "last_op");
                Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
                File.WriteAllText(aPayload, iContent, new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPayload);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NoteLesson] per-persona 鏡寫失敗（全域 _last_lesson.md 已寫）：{e.Message}");
            }
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
