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

        // ⛔ `PersonaPath` 已退場（2026-08-21）：persona 欄位改走 UCL_PersonaProfile 接縫
        //    （中央 json 退場、model / actual_agent 住 letters/<p>/profile/）。

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

        /// <summary>actual_agent → 廠牌名。vendor 是可驗的必填身分，由 actual_agent 推導不靠人填。</summary>
        public static Dictionary<string, string> LoadVendors()
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
                if (data == null || !data.Contains("vendors")) return map;
                var vendors = data["vendors"];
                foreach (var key in new List<string>(map.Keys))
                    if (vendors.Contains(key)) map[key] = vendors.GetString(key, "");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentModel] 讀廠牌表失敗（視為全空）：{e.Message}");
            }
            return map;
        }

        /// <summary>
        /// trailer 的型號欄字串。規則（2026-08-03 三票拍板）：
        /// vendor 推不出來 → 整段沿用原值（不印假精確的 `?`）；version 等於 vendor → 只印 vendor；
        /// **不剝 version 開頭的 vendor 前綴** —— 冗餘只是難看，剝字串是猜測。
        /// </summary>
        public static string FormatTrailerModel(string persona)
        {
            var resolved = Resolve(persona);
            string raw = resolved.Model ?? "";
            string actualAgent = "";
            try
            {
                // 走接縫（2026-08-21：中央 persona json 退場，actual_agent 住 profile/）
                var data = UCL_PersonaProfile.GetRaw(persona);
                if (data != null) actualAgent = data.GetString("actual_agent", "");
            }
            catch { /* 讀不到就當沒有 vendor */ }
            if (string.IsNullOrEmpty(actualAgent)) return raw;
            var vendors = LoadVendors();
            if (!vendors.TryGetValue(actualAgent, out string vendor) || string.IsNullOrWhiteSpace(vendor))
                return raw;
            vendor = vendor.Trim();
            if (string.IsNullOrEmpty(raw) || raw == "?" || Normalize(raw) == Normalize(vendor)) return vendor;
            return $"{vendor} / {raw}";
        }

        public static bool SaveModels(Dictionary<string, string> models, out string error)
            => SaveAll(models, LoadVendors(), out error);

        /// <summary>
        /// 整檔覆寫 models + vendors。**兩張表必須一起寫** —— 只寫一張會把另一張洗掉
        /// （同檔整檔覆寫的典型陷阱，而且它不會報錯）。
        /// </summary>
        public static bool SaveAll(Dictionary<string, string> models, Dictionary<string, string> vendors, out string error)
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
                    sb.AppendLine($"    \"{kv.Key}\": \"{Esc(kv.Value)}\"{comma}");
                }
                sb.AppendLine("  },");
                sb.AppendLine("  \"vendors\": {");
                int j = 0;
                foreach (var kv in vendors)
                {
                    string comma = (++j < vendors.Count) ? "," : "";
                    sb.AppendLine($"    \"{kv.Key}\": \"{Esc(kv.Value)}\"{comma}");
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

        static string Esc(string v) => (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

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
                var data = UCL_PersonaProfile.GetRaw(persona);
                if (data != null)
                {
                    raw = (data.GetString("model", "") ?? "").Trim();
                    actualAgent = data.GetString("actual_agent", "");
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
