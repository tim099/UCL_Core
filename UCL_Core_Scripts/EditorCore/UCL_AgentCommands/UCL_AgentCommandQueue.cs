
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// Queue persistence — read/write AgentCommands/queue.json at repository root.
// 與 RCG 版相容（相同檔案路徑 + 相同 JSON 格式），可平行運作。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// queue.json 的讀寫管理。
    /// 路徑：&lt;repoRoot&gt;/AgentCommands/queue.json
    /// （repoRoot = Application.dataPath/../.. — 即 Assets 的上兩層）
    /// </summary>
    public static class UCL_AgentCommandQueue
    {
        public const string QueueDirRelative = "AgentCommands";
        public const string QueueFileName = "queue.json";

        /// <summary>取得 queue.json 的絕對路徑（不保證檔案存在）。</summary>
        public static string GetQueuePath()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string dir = Path.Combine(projectRoot, QueueDirRelative);
            return Path.Combine(dir, QueueFileName);
        }

        /// <summary>取得 AgentCommands 資料夾的絕對路徑。</summary>
        public static string GetQueueDir()
        {
            return Path.GetDirectoryName(GetQueuePath());
        }

        /// <summary>確保 AgentCommands 資料夾存在。</summary>
        public static void EnsureDir()
        {
            string dir = GetQueueDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>讀取 queue.json — 不存在或解析失敗時回傳空 queue。</summary>
        public static UCL_AgentCommandQueueData Load()
        {
            string path = GetQueuePath();
            if (!File.Exists(path))
            {
                return new UCL_AgentCommandQueueData();
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                // Unity JsonUtility 不支援 Dictionary，因此採手寫 JSON parse（極簡）
                return ParseJson(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandQueue] Failed to load queue: {e}");
                return new UCL_AgentCommandQueueData();
            }
        }

        /// <summary>寫入 queue.json（會覆寫整個檔案）。</summary>
        public static void Save(UCL_AgentCommandQueueData data)
        {
            EnsureDir();
            string path = GetQueuePath();
            try
            {
                string json = SerializeJson(data);
                File.WriteAllText(path, json, new UTF8Encoding(false)); // no BOM
                Debug.Log($"[UCL_AgentCommandQueue] Saved queue → {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandQueue] Failed to save queue: {e}");
            }
        }

        // ===========================================================
        // 簡易 JSON 序列化（不依賴 Unity JsonUtility，因為要支援 Dictionary）
        // ===========================================================

        static string SerializeJson(UCL_AgentCommandQueueData data)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"Commands\": [");
            bool firstCmd = true;
            foreach (var c in data.Commands ?? new List<UCL_AgentCommand>())
            {
                if (!firstCmd) sb.Append(",");
                firstCmd = false;
                sb.Append("\n    {");
                AppendField(sb, "Id", c.Id, true);
                AppendField(sb, "Type", c.Type, false);
                AppendField(sb, "Mode", c.Mode.ToString(), false);
                AppendInt(sb, "RunCount", c.RunCount);
                sb.Append(",\n      \"Args\": {");
                if (c.Args != null && c.Args.Count > 0)
                {
                    bool firstArg = true;
                    foreach (var kv in c.Args)
                    {
                        if (!firstArg) sb.Append(",");
                        firstArg = false;
                        sb.Append("\n        \"").Append(EscapeStr(kv.Key)).Append("\": \"").Append(EscapeStr(kv.Value)).Append("\"");
                    }
                    sb.Append("\n      ");
                }
                sb.Append("}");
                AppendField(sb, "CreatedAt", c.CreatedAt, false);
                AppendField(sb, "LastRunAt", c.LastRunAt, false);
                AppendField(sb, "LastRunResult", c.LastRunResult, false);
                AppendField(sb, "LastRunError", c.LastRunError, false);
                AppendField(sb, "Description", c.Description, false);
                sb.Append("\n    }");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static void AppendField(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\n      \"").Append(key).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append("\"").Append(EscapeStr(value)).Append("\"");
        }
        static void AppendBool(StringBuilder sb, string key, bool value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value ? "true" : "false");
        }
        static void AppendInt(StringBuilder sb, string key, int value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value);
        }
        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ===========================================================
        // 簡易 JSON 解析（支援我們自寫的格式 + agent 手寫的標準 JSON）
        // ===========================================================

        static UCL_AgentCommandQueueData ParseJson(string json)
        {
            int pos = 0;
            SkipWS(json, ref pos);
            ExpectChar(json, ref pos, '{');
            var result = new UCL_AgentCommandQueueData();
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                if (key == "Commands")
                {
                    result.Commands = ParseCommandArray(json, ref pos);
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return result;
        }

        static List<UCL_AgentCommand> ParseCommandArray(string json, ref int pos)
        {
            var list = new List<UCL_AgentCommand>();
            ExpectChar(json, ref pos, '[');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == ']') { pos++; break; }
                list.Add(ParseCommand(json, ref pos));
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return list;
        }

        static UCL_AgentCommand ParseCommand(string json, ref int pos)
        {
            var c = new UCL_AgentCommand();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);

                switch (key)
                {
                    case "Id":            c.Id = ParseStringOrNull(json, ref pos); break;
                    case "Type":          c.Type = ParseStringOrNull(json, ref pos); break;
                    case "Mode":
                        {
                            string s = ParseStringOrNull(json, ref pos);
                            if (Enum.TryParse<UCL_AgentCommandMode>(s, out var m)) c.Mode = m;
                            break;
                        }
                    case "RunCount":      c.RunCount = ParseInt(json, ref pos); break;
                    case "Executed":      // 向後相容：舊版 bool true 視為 RunCount=1
                        c.RunCount = ParseBool(json, ref pos) ? 1 : 0;
                        break;
                    case "Args":          c.Args = ParseStringDict(json, ref pos); break;
                    case "CreatedAt":     c.CreatedAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunAt":     c.LastRunAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunResult": c.LastRunResult = ParseStringOrNull(json, ref pos); break;
                    case "LastRunError":  c.LastRunError = ParseStringOrNull(json, ref pos); break;
                    case "Description":   c.Description = ParseStringOrNull(json, ref pos); break;
                    default:              SkipValue(json, ref pos); break;
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return c;
        }

        static Dictionary<string, string> ParseStringDict(string json, ref int pos)
        {
            var d = new Dictionary<string, string>();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string k = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                string v = ParseStringOrNull(json, ref pos) ?? "";
                d[k] = v;
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return d;
        }

        static string ParseString(string json, ref int pos)
        {
            ExpectChar(json, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char ch = json[pos++];
                if (ch == '"') break;
                if (ch == '\\' && pos < json.Length)
                {
                    char esc = json[pos++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        static string ParseStringOrNull(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos < json.Length && json[pos] == 'n')
            {
                if (pos + 4 <= json.Length && json.Substring(pos, 4) == "null")
                {
                    pos += 4;
                    return null;
                }
            }
            return ParseString(json, ref pos);
        }

        static bool ParseBool(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos + 4 <= json.Length && json.Substring(pos, 4) == "true")  { pos += 4; return true; }
            if (pos + 5 <= json.Length && json.Substring(pos, 5) == "false") { pos += 5; return false; }
            return false;
        }

        static int ParseInt(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            int start = pos;
            if (pos < json.Length && (json[pos] == '-' || json[pos] == '+')) pos++;
            while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            if (pos == start) return 0;
            return int.TryParse(json.Substring(start, pos - start), out var v) ? v : 0;
        }

        static void SkipValue(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length) return;
            char ch = json[pos];
            if (ch == '"') { ParseString(json, ref pos); return; }
            if (ch == '{' || ch == '[')
            {
                char open = ch, close = (ch == '{') ? '}' : ']';
                int depth = 0;
                while (pos < json.Length)
                {
                    char c = json[pos];
                    if (c == '"') { ParseString(json, ref pos); continue; }
                    if (c == open) depth++;
                    else if (c == close) { depth--; pos++; if (depth == 0) return; continue; }
                    pos++;
                }
                return;
            }
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) return;
                pos++;
            }
        }

        static void SkipWS(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }
        static void ExpectChar(string json, ref int pos, char ch)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length || json[pos] != ch)
                throw new Exception($"Expected '{ch}' at pos {pos}");
            pos++;
        }
    }
}
#endif
