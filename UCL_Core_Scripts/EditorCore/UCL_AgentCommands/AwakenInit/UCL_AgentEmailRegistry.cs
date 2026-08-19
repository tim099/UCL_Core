// 區塊職責：agent 預設信箱 + persona override 的唯一解析點（C# 端）。
// 物理意義：預設表以 **actual_agent**（Codex / ClaudeCode / Antigravity）為 key —— 那是封閉集合，
//          設一次就不必再管；顯示 agent（Sirius / Myth / 月讀大小姐…）是開放的，每多一位同事就多一格要填，
//          而漏填會靜默 fallback。override 跟著 persona 檔走，因為它本來就是「這個人的」屬性。
// 數值影響：解析順序 persona.email → defaults[actual_agent] → fallback；查不到回哨兵值而不是空字串，
//          因為空字串在 trailer 裡長得像「還沒填」，哨兵值長得像「壞了」—— 後者才會被人看見。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>一筆解析結果 —— 值本身與「它從哪來」一起回，UI 才能顯示 override / 預設 / 沒設定。</summary>
    public class UCL_AgentEmailResolution
    {
        public string Email = "";
        public string Source = "";        // persona-override / agent-default / fallback / unset
        public string ActualAgent = "";
        public bool IsFallback => Source == "fallback" || Source == "unset";
    }

    public static class UCL_AgentEmailRegistry
    {
        /// <summary>查不到時回這個 —— 刻意長得不像正常位址，讓它在第一次 commit 就被看見。</summary>
        public const string UnsetSentinel = "unset@invalid";

        public static string RegistryPath =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "agent_emails.json").Replace('\\', '/');

        // persona 檔一律走單一解析點（見 UCL_AwakeningService.ResolvePersonaFile 的區塊註解）
        static string PersonaPath(string persona) =>
            Awakening.UCL_AwakeningService.ResolvePersonaFile(persona);

        /// <summary>預設表（actual_agent → email）。檔案不存在時回三個空欄，讓後台有東西可填。</summary>
        public static Dictionary<string, string> LoadDefaults()
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
                if (data == null || !data.Contains("defaults")) return map;
                var defaults = data["defaults"];
                foreach (var key in new List<string>(map.Keys))
                {
                    if (defaults.Contains(key)) map[key] = defaults.GetString(key, "");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentEmail] 讀預設表失敗（視為全空）：{e.Message}");
            }
            return map;
        }

        public static string LoadFallback()
        {
            try
            {
                if (!File.Exists(RegistryPath)) return "";
                var data = JsonData.ParseJson(File.ReadAllText(RegistryPath));
                return data == null ? "" : data.GetString("fallback", "");
            }
            catch { return ""; }
        }

        /// <summary>整份覆寫預設表 + fallback。後台是唯一設定入口，所以這裡不做增量合併。</summary>
        public static bool SaveDefaults(Dictionary<string, string> defaults, string fallback, out string error)
        {
            error = "";
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_schema_version\": 1,");
                sb.AppendLine("  \"_description\": \"agent 預設信箱（key = actual_agent，封閉集合）。persona 層 override 寫在 AwakenInit/personas/<name>.json 的 email 欄。唯一設定入口是 UCL_PersonaAgentAdminPage。\",");
                sb.AppendLine("  \"defaults\": {");
                int i = 0;
                foreach (var kv in defaults)
                {
                    string comma = (++i < defaults.Count) ? "," : "";
                    sb.AppendLine($"    {Quote(kv.Key)}: {Quote(kv.Value ?? "")}{comma}");
                }
                sb.AppendLine("  },");
                sb.AppendLine($"  \"fallback\": {Quote(fallback ?? "")}");
                sb.AppendLine("}");
                string dir = Path.GetDirectoryName(RegistryPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(RegistryPath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>讀 persona 檔的 override（沒有欄位或空字串都算沒設）——
        /// 走 UCL_PersonaProfile 唯一讀取入口（Phase 0 接縫；email 屬 IDENTITY_FIELDS，§8.3）。</summary>
        public static string LoadPersonaOverride(string persona)
        {
            try { return UCL_PersonaProfile.GetString(persona, "email", ""); }
            catch { return ""; }
        }

        /// <summary>
        /// 寫 persona override。空字串＝清除 override（回頭吃 agent 預設），不是寫入空信箱。
        /// 走 §8.6 寫入接縫（patch 單欄＋actor/reason 必填＋審計＋快照刷新）。
        /// </summary>
        public static bool SavePersonaOverride(string persona, string email, string actor, string reason, out string error)
            => UCL_PersonaProfile.SetField(persona, "email", email ?? "", actor, reason, out error);

        /// <summary>
        /// 解析某 persona 該用的信箱。順序：persona.email → defaults[actual_agent] → fallback → 哨兵。
        /// </summary>
        public static UCL_AgentEmailResolution Resolve(string persona)
        {
            var result = new UCL_AgentEmailResolution();
            string actualAgent = "";
            try
            {
                // persona 欄位走 UCL_PersonaProfile 唯一讀取入口（Phase 0 接縫；壞檔接縫已警告）
                var data = UCL_PersonaProfile.GetRaw(persona);
                if (data != null)
                {
                    actualAgent = data.GetString("actual_agent", "");
                    string own = data.GetString("email", "");
                    if (!string.IsNullOrWhiteSpace(own))
                    {
                        result.Email = own.Trim();
                        result.Source = "persona-override";
                        result.ActualAgent = actualAgent;
                        return result;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentEmail] 讀 persona {persona} 失敗：{e.Message}");
            }
            result.ActualAgent = actualAgent;

            var defaults = LoadDefaults();
            if (!string.IsNullOrEmpty(actualAgent) && defaults.TryGetValue(actualAgent, out string byAgent)
                && !string.IsNullOrWhiteSpace(byAgent))
            {
                result.Email = byAgent.Trim();
                result.Source = "agent-default";
                return result;
            }

            string fallback = LoadFallback();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                result.Email = fallback.Trim();
                result.Source = "fallback";
                return result;
            }
            result.Email = UnsetSentinel;
            result.Source = "unset";
            return result;
        }

        /// <summary>粗篩：只擋明顯不是位址的東西（空白、缺 @、多個 @、頭尾點）。不做 RFC 級驗證。</summary>
        public static bool LooksLikeEmail(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            int at = v.IndexOf('@');
            if (at <= 0 || at != v.LastIndexOf('@') || at == v.Length - 1) return false;
            string domain = v.Substring(at + 1);
            return domain.Contains(".") && !domain.StartsWith(".") && !domain.EndsWith(".") && !v.Contains(" ");
        }

        static string Quote(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
#endif
