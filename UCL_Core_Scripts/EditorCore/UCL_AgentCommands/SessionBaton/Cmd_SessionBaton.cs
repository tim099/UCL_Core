// 區塊職責：T83 — agent session 結束前 dump baton 給下次 session 接力
// 物理意義：寫一份 markdown 進 AgentCommands/ChatTavern/baton/<actor>_<ts>.md
//          + _latest_<actor>.md 覆寫 pointer（下次 session 載 SKILL 後直接 grep 自己 latest）
// 數值影響：純檔案操作；不動 Treasury / messages.jsonl / lessons.jsonl
// 設計取捨：
//   - 兩份檔案：timestamped audit (不覆寫) + _latest_<actor>.md (覆寫，給下次 session 快查)
//   - markdown 而非 jsonl，body 自由格式（鼓勵敘事 thread summary 而非結構化 entry）
//   - 不限制 body 長度（cascade marathon 累積到 5KB+ 也合理）
//   - 不繞 Cmd_Tavern — baton 不是 chat 訊息是 session 接力 metadata
//
// 解決問題：Tim 2026-05-11 拍板「session 失憶不該被 agent 美化成 mono no aware
//          詛咒，UCL_Core 完全可以實做跨 session 機制」— 取代 Antigravity 的 Phantom
//          Daemon 走「物理 IO 繞過」反模式（違反 P0 鐵律）。
//
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.SessionBaton
{
    /// <summary>
    /// T83 — Agent session baton 接力指令。Session 結束前 dump 主軸 / 未完議題 /
    /// 待 Tim 拍板的鉤子 / 重要學習，給下次 session 開機載入後重建 thread context。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// python AgentCommands/run_cmd.py run SessionBaton \
    ///   --arg actor="claude-da-xiaojie" \
    ///   --arg title="Marathon 21 cascade + T82 三輪 ship 收尾" \
    ///   --arg body="# 主軸\n...\n# 未完議題\n...\n# 鉤子\n..."
    /// </code>
    ///
    /// <para>磁碟結構 (kyouko-persona-binding T03, 2026-05-13 拍板 Agent@Persona keyed)：</para>
    /// <code>
    /// AgentCommands/ChatTavern/baton/
    ///   ├── claude-da-xiaojie/
    ///   │   ├── basecamp/
    ///   │   │   ├── 20260511T001234Z.md   (timestamped audit; 不覆寫)
    ///   │   │   └── _latest.md            (覆寫 pointer; 下次 session 直接看)
    ///   │   ├── crest-001/
    ///   │   │   └── _latest.md
    ///   │   └── _unassigned/             (沒傳 persona arg 的 legacy 落點)
    ///   ├── antigravity-da-xiaojie/
    ///   │   └── apex-one/
    ///   │       └── _latest.md
    ///   └── _last_op.md                  (給當次 caller confirm, 共用一份)
    /// </code>
    ///
    /// <para>下次 session 重建 SOP：</para>
    /// <code>
    /// 1. 開機載 SKILL.md → 看到本 cmd reference
    /// 2. cat AgentCommands/ChatTavern/baton/&lt;my_id&gt;/&lt;my_persona&gt;/_latest.md
    /// 3. 重建 thread context 後正式進工作
    /// </code>
    /// </summary>
    public class Cmd_SessionBaton : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SessionBaton";

        public override string ShortDescription =>
            "T83 Agent session baton — dump thread summary 給下次 session 接力 (跨 session 記憶機制)";

        public override string ArgsSchema =>
            "actor=Agent id 來源 (required) | " +
            "persona=Persona codename (optional, default '_unassigned' — kyouko-persona-binding T03) | " +
            "title=Baton 主題短句 (optional, default 'Session Baton') | " +
            "body=Markdown 內容 - 主軸/未完議題/鉤子/重要學習 (required) | " +
            "summary=1-2 句 header summary (optional, 自動從 body 第一段截取)";

        public override string ExampleArgs =>
            "actor=claude-da-xiaojie;persona=basecamp;title=T82 三輪 ship 收尾;body=## 主軸\\n...";

        public override string HelpURL =>
            "ucl_core:Skills~/ucl-chat-tavern/SKILL.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string actor = GetArg(args, "actor", "").Trim();
            string persona = GetArg(args, "persona", "_unassigned").Trim();
            string title = GetArg(args, "title", "Session Baton").Trim();
            string body = GetArg(args, "body", "");
            string summary = GetArg(args, "summary", "").Trim();

            if (string.IsNullOrWhiteSpace(actor))
            {
                throw new Exception("[SessionBaton] actor 必填（傳 --arg actor=\"<agent_id>\"）");
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new Exception("[SessionBaton] body 必填（傳 --arg body=\"<markdown 內容>\"）");
            }

            // 安全：actor / persona 不能含路徑分隔（防 path traversal）
            if (actor.Contains("/") || actor.Contains("\\") || actor.Contains(".."))
            {
                throw new Exception($"[SessionBaton] actor 含非法字元: {actor}");
            }
            if (string.IsNullOrWhiteSpace(persona) || persona.Contains("/") || persona.Contains("\\") || persona.Contains(".."))
            {
                throw new Exception($"[SessionBaton] persona 含非法字元或空字串: {persona}");
            }

            // Path: <repoRoot>/AgentCommands/ChatTavern/baton/<actor>/<persona>/
            // 區塊職責：T03 kyouko-persona-binding refactor — baton 從 actor-keyed 改 Agent@Persona-keyed
            // 物理意義：basecamp 跟 crest-001 的 baton 不再共用 _latest pointer，各自 persona-bounded
            // 數值影響：legacy caller 沒傳 persona → 落 _unassigned/（backward compat，goodnight 後可手動歸位）
            string batonDir = Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "ChatTavern", "baton", actor, persona);
            try
            {
                Directory.CreateDirectory(batonDir);
            }
            catch (Exception ex)
            {
                throw new Exception($"[SessionBaton] 建立 baton 目錄失敗：{ex.Message}");
            }

            // Timestamp: UTC compact ISO（檔名友善）
            string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string isoTs = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            string timestampedPath = Path.Combine(batonDir, $"{ts}.md");
            string latestPath = Path.Combine(batonDir, "_latest.md");
            // confirm md 留在 baton 根（caller 一律 cat 同一處）
            string confirmPath = Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "ChatTavern", "baton", "_last_op.md");

            // 自動 summary（如果 caller 沒給）：從 body 第一段非空行截取
            if (string.IsNullOrEmpty(summary))
            {
                summary = ExtractFirstParagraph(body, 200);
            }

            // 構造 baton markdown — 含 frontmatter metadata + body
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"actor: {actor}");
            sb.AppendLine($"persona: {persona}");
            sb.AppendLine($"title: {title}");
            sb.AppendLine($"ts_utc: {isoTs}");
            sb.AppendLine($"summary: {EscapeYaml(summary)}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# 🪃 {title}");
            sb.AppendLine();
            sb.AppendLine($"> **Baton from**: `{actor}` @ `{isoTs}`");
            sb.AppendLine($"> **Summary**: {summary}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(body);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"_baton 接力下次 session 重建 thread context 用 — 載入 SKILL 後 cat `baton/{actor}/{persona}/_latest.md` 即可看本筆_");

            string content = sb.ToString();

            // 寫 timestamped audit 檔（永不覆寫）
            try
            {
                File.WriteAllText(timestampedPath, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new Exception($"[SessionBaton] 寫 timestamped baton 失敗：{ex.Message}");
            }

            // 覆寫 _latest_<actor>.md pointer
            try
            {
                File.WriteAllText(latestPath, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionBaton] 寫 latest pointer 失敗（timestamped 已寫不受影響）：{ex.Message}");
            }

            // confirm md 給 caller
            string confirmMd =
                $"# 🪃 Baton 接力 dump 完成\n\n" +
                $"- **actor**: `{actor}`\n" +
                $"- **persona**: `{persona}`\n" +
                $"- **title**: {title}\n" +
                $"- **ts**: `{isoTs}`\n" +
                $"- **summary**: {summary}\n\n" +
                $"📦 寫入：\n" +
                $"- `{Rel(timestampedPath)}` (timestamped audit)\n" +
                $"- `{Rel(latestPath)}` (覆寫 pointer)\n\n" +
                $"---\n\n" +
                $"下次 session 載入 SKILL 後快速重建：\n" +
                $"```\ncat AgentCommands/ChatTavern/baton/{actor}/{persona}/_latest.md\n```\n";
            try
            {
                File.WriteAllText(confirmPath, confirmMd, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionBaton] confirm md 寫入失敗（baton 已寫不受影響）：{ex.Message}");
            }

            Debug.Log($"[SessionBaton] +1 baton by {actor}@{persona} ({title}) → {Rel(timestampedPath)}");
        }

        // ===========================================================
        // Helper: 從 body 抓第一段非空行作為 fallback summary
        // 物理意義：caller 沒給 summary 時自動截一段給 latest pointer header 用
        // 數值影響：截取 max 字元（避免 frontmatter 過長）；多行接成單行
        // ===========================================================
        static string ExtractFirstParagraph(string body, int maxLen)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var lines = body.Split('\n');
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (sb.Length > 0) break;   // 第一段結束
                    continue;
                }
                // 跳過 markdown header 開頭的 # 符號自身（保留文字）
                if (line.StartsWith("#"))
                {
                    line = line.TrimStart('#').Trim();
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
                if (sb.Length >= maxLen) break;
            }
            string result = sb.ToString();
            if (result.Length > maxLen) result = result.Substring(0, maxLen - 1) + "…";
            return result;
        }

        // 區塊職責：YAML frontmatter 簡單 escape — 含特殊字元時包雙引號
        // 物理意義：summary 含 : / # / [ / ] 等 YAML 保留字會破壞 frontmatter 解析
        static string EscapeYaml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool needsQuote = s.Contains(":") || s.Contains("#") || s.Contains("[") ||
                              s.Contains("]") || s.Contains("{") || s.Contains("}") ||
                              s.Contains("\n") || s.Contains("\"");
            if (!needsQuote) return s;
            string escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
            return "\"" + escaped + "\"";
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
