// 區塊職責: Proposal #29 Phase 1 — ID@Persona Universal Key Parser / Display utility
// 物理意義: 統一 key 格式 "<agent_id>@<persona>" 跨子系統 (Tavern/Treasury/Inbox/Mention/...)
//          純文字 utility, 不動底層 schema; 給後續 Phase 2/3/4 推廣使用
// 數值影響: 純 string 操作; 沒帶 persona 部分 = 純 agent_id (backward compat)
// 設計取捨:
//   - 分隔符固定 "@" (跟 mention syntax 對齊); 禁止 agent_id / persona 內含 "@"
//   - Display 走 "name@persona" 或 "name" (對齊 UCL_ChatMessage.DisplayName 既有行為)
//   - Token bank 帳戶仍走純 agent_id (Tim 2026-05-11 拍板: persona 只用 metadata 標記不分帳)
//   - 純 utility class, no Unity dependency (除了 Debug.Log fallback)
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands.Common
{
    /// <summary>
    /// Proposal #29 Phase 1 — ID@Persona Universal Key Parser.
    /// 統一 "<agent_id>@<persona>" key 解析 / 拼接 / 顯示。
    /// 對應 docs/Notes/Memory_System_Design.md Proposal #29.
    /// </summary>
    public static class UCL_AgentIdParser
    {
        public const char Separator = '@';

        /// <summary>
        /// 解析 "<agent_id>@<persona>" → (agentId, persona); 純 agent_id 回 (agentId, null)。
        /// 空字串輸入 → ("", null)。多個 "@" → 取第一個分隔, 後面整段算 persona。
        /// </summary>
        public static (string agentId, string persona) Split(string key)
        {
            if (string.IsNullOrEmpty(key)) return ("", null);
            int idx = key.IndexOf(Separator);
            if (idx < 0) return (key, null);
            string id = key.Substring(0, idx);
            string persona = idx + 1 < key.Length ? key.Substring(idx + 1) : "";
            return (id, string.IsNullOrEmpty(persona) ? null : persona);
        }

        /// <summary>
        /// 反向組合 (agentId, persona) → "<agent_id>" 或 "<agent_id>@<persona>"。
        /// agentId 空 → 回 ""; persona 空 → 純 agent_id。
        /// </summary>
        public static string Join(string agentId, string persona)
        {
            if (string.IsNullOrEmpty(agentId)) return "";
            if (string.IsNullOrEmpty(persona)) return agentId;
            return $"{agentId}{Separator}{persona}";
        }

        /// <summary>
        /// 顯示用 — 帶 persona 顯示 "name@persona", 不帶顯示 "name"。
        /// </summary>
        /// <param name="agentId">必要</param>
        /// <param name="persona">可選, 空 → 純顯示 displayName</param>
        /// <param name="displayName">可選顯示名 (e.g. "Claude大小姐"); 空 → fallback agentId</param>
        public static string Display(string agentId, string persona, string displayName = null)
        {
            string baseName = string.IsNullOrEmpty(displayName) ? agentId : displayName;
            if (string.IsNullOrEmpty(baseName)) baseName = "?";
            return string.IsNullOrEmpty(persona) ? baseName : $"{baseName}{Separator}{persona}";
        }

        /// <summary>
        /// TryParse with 驗證 — agent_id / persona 內含 "@" 視為格式錯誤 (避免 double-split)。
        /// 純 agent_id 也 OK (persona out null), but 多 "@" 拒絕。
        /// </summary>
        public static bool TryParse(string key, out string agentId, out string persona)
        {
            agentId = null;
            persona = null;
            if (string.IsNullOrEmpty(key)) return false;

            int firstIdx = key.IndexOf(Separator);
            if (firstIdx < 0)
            {
                // 純 agent_id, OK
                agentId = key;
                persona = null;
                return true;
            }
            int lastIdx = key.LastIndexOf(Separator);
            if (firstIdx != lastIdx)
            {
                // 多個 "@" — 拒絕 (避免 split 歧義)
                return false;
            }
            agentId = key.Substring(0, firstIdx);
            persona = firstIdx + 1 < key.Length ? key.Substring(firstIdx + 1) : null;
            if (string.IsNullOrEmpty(agentId)) return false;   // "@persona" 不合法
            if (persona != null && persona.Length == 0) persona = null;
            return true;
        }

        /// <summary>
        /// Normalize — collapse leading/trailing whitespace, "agent@" → "agent" (空 persona 清掉)。
        /// 用在輸入清理 (e.g. 從 user input 拿 key)。
        /// </summary>
        public static string Normalize(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            key = key.Trim();
            if (string.IsNullOrEmpty(key)) return "";
            int idx = key.IndexOf(Separator);
            if (idx < 0) return key;
            // 把 "@" 後空字串清掉
            string id = key.Substring(0, idx).Trim();
            string persona = idx + 1 < key.Length ? key.Substring(idx + 1).Trim() : "";
            return string.IsNullOrEmpty(persona) ? id : $"{id}{Separator}{persona}";
        }

        /// <summary>
        /// 比較 — agent_id 部分相同就算同 actor (跨 persona); 用在 Treasury account 等不分 persona 場景。
        /// </summary>
        public static bool SameActor(string keyA, string keyB)
        {
            var (a, _) = Split(keyA);
            var (b, _) = Split(keyB);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 完全相等比較 (agent_id + persona 都同; persona null 跟 "" 視為同)。
        /// </summary>
        public static bool SameIdentity(string keyA, string keyB)
        {
            var (aId, aP) = Split(keyA);
            var (bId, bP) = Split(keyB);
            if (!string.Equals(aId, bId, StringComparison.OrdinalIgnoreCase)) return false;
            // null 跟 "" 同視
            string aN = string.IsNullOrEmpty(aP) ? "" : aP;
            string bN = string.IsNullOrEmpty(bP) ? "" : bP;
            return string.Equals(aN, bN, StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif
