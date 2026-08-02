// 區塊職責：agent 預設型號表，以及「model 欄被填成 agent 名」時的底層翻譯（C# 端）。
// 物理意義：實測發現**提示反而讓人填錯** —— apex-one 的 system prompt 第一句是 "You are Antigravity"
//          所以他把 Antigravity 填進 model；kaguya 填 Codex。兩人都是誠實作答，錯的是我們要求他們
//          回答一個他們讀起來意思不同的問題。所以不靠提示，改在底層辨識並翻譯（Tim 2026-08-03 拍板）。
// 數值影響：辨識無視大小寫／空白／連字號／底線；翻不出來**保留原值**而不是清空 ——
//          原值至少是某人真的寫下的資訊，空白什麼都不是。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    public class UCL_AgentModelResolution
    {
        public string Model = "";
        public string Raw = "";
        public string Source = "";      // as-written / agent-translated / agent-unmapped / empty
        public string AgentKey = "";
        public bool WasTranslated => Source == "agent-translated";
    }

    public static class UCL_AgentModelRegistry
    {
        public static string RegistryPath =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "agent_models.json").Replace('\\', '/');

        static string PersonaPath(string persona) =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "personas", persona + ".json").Replace('\\', '/');

        // 已知會被填進 model 欄的 agent 別名 → 正規 actual_agent。
        // 收的是**人真的會寫出來的字**，不是理論上的正確值；漏一個就翻不出來，多一個沒有代價。
        // value 為 None(空字串) = 有歧義：Claude / Gemini 也可能是誠實給的模糊型號，一律當型號不翻。
        static readonly Dictionary<string, string> s_Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "codex", "Codex" },
            { "openai", "Codex" },
            { "chatgpt", "Codex" },
            { "claudecode", "ClaudeCode" },
            { "anthropic", "ClaudeCode" },
            { "antigravity", "Antigravity" },
            { "claude", "" },
            { "gemini", "" },
        };

        /// <summary>辨識用正規化 —— 無視大小寫、空白、連字號、底線。</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static Dictionary<string, string> LoadModels()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (UCL_ActualAgent agent in Enum.GetValues(typeof(UCL_ActualAgent)))
            {
                if (agent == UCL_ActualAgent.None) continue;
                map[agent.ToString()] = "";
            }
            try
            {
                if (!File.Exists(RegistryPath)) return map;
                var data = JsonData.ParseJson(File.ReadAllText(RegistryPath));
                if (data == null || !data.Contains("models")) return map;
                var models = data["models"];
                foreach (var key in new List<string>(map.Keys))
                    if (models.Contains(key)) map[key] = models.GetString(key, "");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentModel] 讀預設型號失敗（視為全空）：{e.Message}");
            }
            return map;
        }

        public static bool SaveModels(Dictionary<string, string> models, out string error)
        {
            error = "";
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_schema_version\": 1,");
                sb.AppendLine("  \"_description\": \"agent 預設型號（key = actual_agent）。persona 的 model 欄若被填成 agent 名，解析時自動翻成這裡的值。唯一設定入口是 UCL_PersonaAgentAdminPage。\",");
                sb.AppendLine("  \"models\": {");
                int i = 0;
                foreach (var kv in models)
                {
                    string comma = (++i < models.Count) ? "," : "";
                    sb.AppendLine($"    \"{kv.Key}\": \"{(kv.Value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")}\"{comma}");
                }
                sb.AppendLine("  }");
                sb.AppendLine("}");
                string dir = Path.GetDirectoryName(RegistryPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(RegistryPath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>這個字串是不是 agent 名？是的話回正規 actual_agent，不是（或有歧義）回空字串。</summary>
        public static string IdentifyAgent(string value)
        {
            string n = Normalize(value);
            if (string.IsNullOrEmpty(n)) return "";
            foreach (UCL_ActualAgent agent in Enum.GetValues(typeof(UCL_ActualAgent)))
            {
                if (agent == UCL_ActualAgent.None) continue;
                if (n == Normalize(agent.ToString())) return agent.ToString();
            }
            return s_Aliases.TryGetValue(n, out string mapped) ? mapped : "";
        }

        /// <summary>persona.model → 是 agent 名就翻成該 agent 預設型號；翻不出來保留原值。</summary>
        public static UCL_AgentModelResolution Resolve(string persona)
        {
            var result = new UCL_AgentModelResolution();
            string raw = "", actualAgent = "";
            try
            {
                string path = PersonaPath(persona);
                if (File.Exists(path))
                {
                    var data = JsonData.ParseJson(File.ReadAllText(path));
                    if (data != null)
                    {
                        raw = (data.GetString("model", "") ?? "").Trim();
                        actualAgent = data.GetString("actual_agent", "");
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[AgentModel] 讀 persona {persona} 失敗：{e.Message}"); }

            result.Raw = raw;
            result.AgentKey = actualAgent;
            if (string.IsNullOrEmpty(raw)) { result.Model = "?"; result.Source = "empty"; return result; }

            string key = IdentifyAgent(raw);
            if (string.IsNullOrEmpty(key)) { result.Model = raw; result.Source = "as-written"; return result; }

            result.AgentKey = key;
            var models = LoadModels();
            if (models.TryGetValue(key, out string mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                result.Model = mapped.Trim();
                result.Source = "agent-translated";
                return result;
            }
            result.Model = raw;                 // 認得出是 agent 名但後台沒設 → 別把資訊擦掉
            result.Source = "agent-unmapped";
            return result;
        }
    }
}
#endif
