// 區塊職責：讀 AgentCommands/_session/_persona_*.json，回「現在真的在線」的 persona 清單。
// 物理意義：在線判準是 **lock 檔存在且未過期**，不是 persona registry 的 status 欄 —— 登出流程沒走完
//          時 status 會停在 online（實測 zenith-one），拿它當在線來源會挑到一個沒人在的 session。
// 數值影響：純讀檔，無快取；呼叫端每次拿到的是當下磁碟狀態，不會用到上一次 GUI repaint 的舊快照。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>單一 persona session lock 的精簡視圖 —— 只留 routing 需要的欄位。</summary>
    public class UCL_PersonaLockInfo
    {
        public string Persona = "";
        public string Agent = "";
        public string BankAccount = "";
        public UCL_ActualAgent ActualAgent = UCL_ActualAgent.None;
        public string ActualAgentRaw = "";
        public string LockedAt = "";
        public string ExpiresAt = "";

        /// <summary>token 形狀由 awakening 端的命名慣例決定：session 標題就是 <c>##persona##</c>。</summary>
        public string SessionToken => $"##{Persona}##";
    }

    public static class UCL_ActivePersonaLocks
    {
        public static string SessionDir => Path.Combine(UCL_RepoPath.AgentCommandsDir, "_session").Replace('\\', '/');

        /// <summary>
        /// 列出未過期的 persona lock，依 persona 名稱排序。讀不到目錄或壞檔一律略過該筆，
        /// 不讓一個壞掉的 lock 檔擋住整份清單。
        /// </summary>
        public static List<UCL_PersonaLockInfo> ListOnline()
        {
            var list = new List<UCL_PersonaLockInfo>();
            try
            {
                string dir = SessionDir;
                if (!Directory.Exists(dir)) return list;
                DateTime now = DateTime.UtcNow;
                foreach (string file in Directory.GetFiles(dir, "_persona_*.json"))
                {
                    try
                    {
                        var data = JsonData.ParseJson(File.ReadAllText(file));
                        if (data == null) continue;
                        string persona = data.GetString("persona", "");
                        if (string.IsNullOrEmpty(persona)) continue;
                        string expires = data.GetString("expires_at", "");
                        if (IsExpired(expires, now)) continue;
                        string actualRaw = data.GetString("actual_agent", "");
                        list.Add(new UCL_PersonaLockInfo
                        {
                            Persona = persona,
                            Agent = data.GetString("agent", ""),
                            BankAccount = data.GetString("bank_account", ""),
                            ActualAgent = UCL_ActualAgentUtility.ParseOrNone(actualRaw),
                            ActualAgentRaw = actualRaw,
                            LockedAt = data.GetString("locked_at", ""),
                            ExpiresAt = expires,
                        });
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ActivePersonaLocks] 略過壞掉的 lock 檔 {file}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ActivePersonaLocks] 掃描 lock 目錄失敗: {e.Message}");
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Persona, b.Persona));
            return list;
        }

        public static UCL_PersonaLockInfo Find(string persona)
        {
            if (string.IsNullOrEmpty(persona)) return null;
            return ListOnline().Find(l => string.Equals(l.Persona, persona, StringComparison.OrdinalIgnoreCase));
        }

        // 無 expires_at 的舊 lock 視為未過期 —— 那是欄位缺席，不是「已經過期」的證據；
        // 真的要下線仍由 goodnight 刪檔決定。
        static bool IsExpired(string expiresAt, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(expiresAt)) return false;
            if (!DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return false;
            return parsed < nowUtc;
        }
    }
}
#endif
