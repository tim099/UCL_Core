// 區塊職責：讀 <DataRoot>/_session/_persona_*.json —— 「誰有 lock／誰在線」的**唯一**掃描實作。
// 物理意義：**有 lock ＝ 在線**，直到 goodnight/logout 顯式刪檔 —— 過期機制已於 2026-08-19 移除
//          （Tim 拍板：R9「過期不自動豁免」讓 expires_at 不再閘任何行為之後，它只剩顯示在讀，
//          整套 TTL／續期／過期標記是在餵一個沒有消費端的欄位）。
//          在線判準不是 persona registry 的 status 欄 —— 登出流程沒走完時 status 會停在 online
//          （實測 zenith-one），拿它當在線來源會挑到一個沒人在的 session。
// 數值影響：純讀檔，無快取；呼叫端每次拿到的是當下磁碟狀態，不會用到上一次 GUI repaint 的舊快照。
// 🩸 收斂前的散裝代價（2026-08-19 實掃）：C# 5 檔繞過本類直掃、python 端另有 7 處 ——
//    同一份 lock 資料，run_cmd 的身分推論當天兩次把 summit 誤判成 basecamp。
//    新增「在線」相關欄位（如 now_status）只准加在這裡，不准在呼叫端各加一次。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>單一 persona session lock 的視圖 —— lock 檔全部消費端會用到的欄位。</summary>
    public class UCL_PersonaLockInfo
    {
        public string Persona = "";
        public string Agent = "";
        public string Model = "";
        public string BankAccount = "";
        public UCL_ActualAgent ActualAgent = UCL_ActualAgent.None;
        public string ActualAgentRaw = "";
        public string LockedAt = "";
        public string SessionKey = "";
        public string ClaimOrigin = "";
        public int Pid = 0;
        /// <summary>lock 檔內 session_token 欄的原始值（失憶救援用）—— 別跟 <see cref="SessionToken"/> 混淆。</summary>
        public string RawSessionToken = "";
        /// <summary>lock 檔的絕對路徑（admin 頁手動清 lock 用）。</summary>
        public string FilePath = "";
        /// <summary>一句話「我現在在做什麼」（§8.5 now_status）—— 由 Cmd_Tavern post 的 status 參數順手更新。</summary>
        public string NowStatus = "";
        /// <summary>now_status 的更新時刻（UTC ISO）—— 顯示端要換算成「多久前」，過舊的狀態比沒有狀態更誤導。</summary>
        public string StatusUpdatedAt = "";

        /// <summary>token 形狀由 awakening 端的命名慣例決定：session 標題就是 <c>##persona##</c>。</summary>
        public string SessionToken => $"##{Persona}##";

        // ===========================================================
        // 區塊職責：now_status 的「多久前」文字（顯示端唯一換算實作）。
        // 物理意義：§8.5 明寫「staleness 要顯示 —— 過舊的狀態比沒有狀態更會誤導」。
        //          換算住在這裡而不住在各個顯示頁：**「在線」相關的東西只准加在本類**
        //          （本檔檔頭的規矩），否則後台頁與 catchup 會對同一個時間戳給出兩種說法。
        // ⚠ 對側契約：python 端的同一份換算在 `AgentCommands/Tools/tavern_catchup.py`
        //   （剛剛／N 分鐘前／N 小時前／N 天前）—— **用字要一致**，兩邊講同一件事用不同字
        //   會讓人以為是兩種狀態。改一邊要改另一邊。
        // 數值影響：時間戳解析失敗或空值回空字串（呼叫端自己決定要不要顯示「沒設定」）；
        //          一律以 UTC 比對（lock 裡存的是 UTC ISO）。
        // ===========================================================
        public string StatusAgeText
        {
            get
            {
                if (string.IsNullOrEmpty(StatusUpdatedAt)) return "";
                if (!DateTime.TryParse(StatusUpdatedAt, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var aAt)) return "";
                double aSec = (DateTime.UtcNow - aAt).TotalSeconds;
                if (aSec < 0) aSec = 0;                     // 時鐘微幅倒退不該印成負數
                if (aSec < 60) return "剛剛";
                if (aSec < 3600) return $"{(int)(aSec / 60)} 分鐘前";
                if (aSec < 86400) return $"{(int)(aSec / 3600)} 小時前";
                return $"{(int)(aSec / 86400)} 天前";
            }
        }

        /// <summary>狀態是不是舊到會誤導（超過一天）—— 顯示端據此加警示，§8.5 的「過舊比沒有更糟」。</summary>
        public bool IsStatusStale
        {
            get
            {
                if (string.IsNullOrEmpty(StatusUpdatedAt)) return false;
                if (!DateTime.TryParse(StatusUpdatedAt, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var aAt)) return false;
                return (DateTime.UtcNow - aAt).TotalSeconds >= 86400;
            }
        }
    }

    public static class UCL_ActivePersonaLocks
    {
        // ⚠ 資料根走 UCL_AgentCommandsPath.DataRoot（可 override 的**資料**根，與 UCL_AwakeningService
        //   同源），不是 UCL_RepoPath.AgentCommandsDir（canonical code 位置）——
        //   2026-08-19 收斂前本類走後者，資料根被 override 時會與其他消費端各看各的目錄。
        public static string SessionDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "_session").Replace('\\', '/');

        /// <summary>
        /// 列出全部 persona lock（＝在線名單），依 persona 名稱排序。
        /// 讀不到目錄或壞檔一律略過該筆並警告，不讓一個壞掉的 lock 檔擋住整份清單。
        /// </summary>
        public static List<UCL_PersonaLockInfo> ListLocks()
        {
            var list = new List<UCL_PersonaLockInfo>();
            try
            {
                string dir = SessionDir;
                if (!Directory.Exists(dir)) return list;
                foreach (string file in Directory.GetFiles(dir, "_persona_*.json"))
                {
                    try
                    {
                        var data = JsonData.ParseJson(File.ReadAllText(file));
                        if (data == null) continue;
                        string persona = data.GetString("persona", "");
                        if (string.IsNullOrEmpty(persona)) continue;
                        string actualRaw = data.GetString("actual_agent", "");
                        list.Add(new UCL_PersonaLockInfo
                        {
                            Persona = persona,
                            Agent = data.GetString("agent", ""),
                            Model = data.GetString("model", ""),
                            BankAccount = data.GetString("bank_account", ""),
                            ActualAgent = UCL_ActualAgentUtility.ParseOrNone(actualRaw),
                            ActualAgentRaw = actualRaw,
                            LockedAt = data.GetString("locked_at", ""),
                            SessionKey = data.GetString("session_key", ""),
                            ClaimOrigin = data.GetString("claim_origin", ""),
                            Pid = data.GetInt("pid", 0),
                            RawSessionToken = data.GetString("session_token", ""),
                            NowStatus = data.GetString("now_status", ""),
                            StatusUpdatedAt = data.GetString("status_updated_at", ""),
                            FilePath = file.Replace('\\', '/'),
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

        /// <summary>在線清單 —— 過期機制移除後即 <see cref="ListLocks"/> 的語意別名（有 lock ＝ 在線）。</summary>
        public static List<UCL_PersonaLockInfo> ListOnline() => ListLocks();

        /// <summary>只要名字集合的呼叫端用這支。</summary>
        public static HashSet<string> LockedNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in ListLocks()) set.Add(l.Persona);
            return set;
        }

        public static UCL_PersonaLockInfo Find(string persona)
        {
            if (string.IsNullOrEmpty(persona)) return null;
            return ListLocks().Find(l => string.Equals(l.Persona, persona, StringComparison.OrdinalIgnoreCase));
        }
    }
}
#endif
