// 區塊職責：酒館 CLI 的設定（白名單）與待確認狀態，以及兩者的讀寫。
// 物理意義：這條通道讓**酒館裡的一句話**去動 Editor 的狀態（目前是遠端視窗協作，
//          而那個能力會移動游標、按 Enter）。所以本檔的重點不是功能，是**誰可以**。
// 數值影響：只讀寫 `ChatTavern/bartender/cli_settings.json` 與 `cli_state.json`；
//          指令的實際效果由 UCL_BartenderCliService 的 handler 決定。
//
// 設計取捨：
//   · **授權比對一律精確（case-insensitive 全等），不沿用 keyword trigger 的 liberal substring。**
//     那邊用 substring 是刻意的（`IsTargetMatch` 讓 "Zeta" 同時命中 sender_id 與 name）——
//     猜錯的代價只是多發一則罐頭。這裡猜錯的代價是**把遙控權給錯人**：
//     substring 之下白名單填 `Tim` 會連 `Tim2` / `not-Tim` 一起放行，而它不會報錯。
//   · 白名單比對三個欄位（sender_id / sender_name / sender_persona）的**任一個全等**即通過 ——
//     因為同一個真人在不同路徑進來時填的是不同欄位（Discord 鏡像填 sender_id、
//     agent 走 persona），只認一個欄位會變成「明明是他卻被擋」。
//   · 白名單**空的時候一律擋光**（不是「空＝全部放行」）。空清單最可能的成因是
//     設定檔剛生成或被清掉，那時 fail-open 等於整條通道對所有人敞開。
//   · 待確認狀態落磁碟而不是靜態欄位 —— domain reload 每次編譯都發生，
//     記憶體裡的 pending 會無聲消失，使用者回了 Y 卻沒有反應，且看不出原因。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>白名單一筆 —— 一個可以下指令的人。</summary>
    [Serializable]
    public class UCL_BartenderCliUser : UnityJsonSerializable
    {
        /// <summary>比對鍵：與 sender_id / sender_name / sender_persona **任一全等**（忽略大小寫）即通過。</summary>
        public string id = "";
        /// <summary>顯示用；不參與比對（改暱稱不該影響授權）。</summary>
        public string display_name = "";
        /// <summary>為什麼放行這個人 —— 給日後看設定檔的人，不給程式讀。</summary>
        public string note = "";
    }

    /// <summary>酒館 CLI 設定。</summary>
    [Serializable]
    public class UCL_BartenderCliSettings : UnityJsonSerializable
    {
        /// <summary>總開關。關掉 ＝ 完全不理 `cmd` 開頭的訊息（連「你沒有權限」都不回）。</summary>
        public bool enabled = true;

        /// <summary>
        /// 指令前綴。預設 `cmd`；比對時整句轉小寫（Tim 2026-08-19 指定不分大小寫）。
        /// ⚠ 改這個會讓既有的使用習慣失效，而失效的樣子是「我打了指令但沒反應」。
        /// </summary>
        public string prefix = "cmd";

        /// <summary>
        /// 二次確認的有效秒數。逾期的 pending 不會被 Y 觸發。
        /// 物理意義：沒有逾期的話，三個月後有人回一句「y」會啟動一個他早忘了的指令。
        /// </summary>
        public int confirm_timeout_seconds = 180;

        /// <summary>可下指令的人。**空清單 ＝ 全部擋掉**（不是全部放行）。</summary>
        public List<UCL_BartenderCliUser> users = new List<UCL_BartenderCliUser>();

        // JsonConvert 的 Unity 模式會把 bool 寫成 "True"/"False" 字串，
        // 而 python 讀到 "False" 是 truthy —— 本檔目前只有 C# 讀，但先把 wire format 修對
        // （同 UCL_BartenderLLMSettings 的既有處置）。
        public override JsonData SerializeToJson()
        {
            var aJson = base.SerializeToJson();
            aJson["enabled"] = enabled;
            return aJson;
        }

        /// <summary>這個發話者能不能下指令 —— 精確比對，空清單擋光。</summary>
        public bool IsAuthorized(string iSenderId, string iSenderName, string iPersona)
        {
            if (users == null || users.Count == 0) return false;
            for (int i = 0; i < users.Count; i++)
            {
                string aKey = users[i] != null ? (users[i].id ?? "").Trim() : "";
                if (aKey.Length == 0) continue;                       // 空 id 不放行任何人
                if (Same(aKey, iSenderId) || Same(aKey, iSenderName) || Same(aKey, iPersona)) return true;
            }
            return false;
        }

        static bool Same(string iA, string iB)
            => !string.IsNullOrEmpty(iB) && string.Equals(iA, iB.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一筆待二次確認的指令。</summary>
    [Serializable]
    public class UCL_BartenderCliPending : UnityJsonSerializable
    {
        public string command_line = "";     // 使用者原本打的那一行（已 trim，未轉小寫 —— 回顯要顯示他打的樣子）
        public string requester_key = "";    // 誰要求的（取 sender_persona → sender_name → sender_id 第一個非空）
        public string requester_id = "";
        public string room_id = "";
        public int source_seq = 0;
        public string created_at = "";       // ISO 8601 UTC
        public string summary = "";          // 這個指令會做什麼 —— 問句裡要原樣出現，不要只問「確定嗎」

        public bool IsExpired(int iTimeoutSeconds)
        {
            if (string.IsNullOrEmpty(created_at)) return true;        // 沒有時間戳一律當過期（fail-closed）
            if (!DateTime.TryParse(created_at, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var aWhen)) return true;
            return (DateTime.UtcNow - aWhen).TotalSeconds > Mathf.Max(10, iTimeoutSeconds);
        }
    }

    /// <summary>CLI 的執行期狀態（目前只有待確認佇列，一個 requester 最多一筆）。</summary>
    [Serializable]
    public class UCL_BartenderCliState : UnityJsonSerializable
    {
        public List<UCL_BartenderCliPending> pending = new List<UCL_BartenderCliPending>();

        /// <summary>空清單時序列化出來的形狀（見 UCL_BartenderCliIO.SaveState 的區塊註解）。</summary>
        public const string EmptyJson = "{\n\t\"pending\":[]\n}";
    }

    /// <summary>CLI 設定與狀態的讀寫（與 llm_settings 同層、同一套原子寫入）。</summary>
    public static class UCL_BartenderCliIO
    {
        public const string SettingsFile = "cli_settings.json";
        public const string StateFile = "cli_state.json";

        public static string GetSettingsPath() => Path.Combine(UCL_BartenderIO.GetBartenderDir(), SettingsFile);
        public static string GetStatePath() => Path.Combine(UCL_BartenderIO.GetBartenderDir(), StateFile);

        // 區塊職責：讀設定；檔案不存在時**產生一份預設檔並寫回**。
        // 物理意義：這條通道沒有 UI 以外的入口，而「檔案不存在」與「白名單是空的」在行為上
        //          都是全部擋掉 —— 但兩者的處置不同（前者要生檔，後者要加人）。
        //          先生檔，使用者才看得到那份清單長什麼樣、該往哪加。
        // ⚠ 預設清單刻意**只放 Tim**（repo 擁有者、也是提出這條通道的人）。
        //   不預設全開，也不預設空到不能用 —— 空到不能用會讓人去手改 json，
        //   而「只能手改 json」正是今天剛被抱怨過的那件事。
        public static UCL_BartenderCliSettings Load()
        {
            string aPath = GetSettingsPath();
            if (!File.Exists(aPath))
            {
                var aDefault = CreateDefault();
                try { Save(aDefault); } catch (Exception e)
                {
                    Debug.LogWarning($"[BartenderCli] 預設設定寫入失敗（本次仍用記憶體預設）：{e.Message}");
                }
                return aDefault;
            }
            try
            {
                string aJson = File.ReadAllText(aPath);
                var aData = new UCL_BartenderCliSettings();
                if (!string.IsNullOrEmpty(aJson)) aData.DeserializeFromJson(JsonData.ParseJson(aJson));
                if (aData.users == null) aData.users = new List<UCL_BartenderCliUser>();
                if (string.IsNullOrWhiteSpace(aData.prefix)) aData.prefix = "cmd";
                return aData;
            }
            catch (Exception e)
            {
                // 壞檔 → 退成「總開關開著但白名單空的」＝ 全部擋掉。
                // 出聲是必要的：靜默退預設會讓「設定壞了」跟「我不在白名單」長得一樣。
                Debug.LogWarning($"[BartenderCli] 設定讀取失敗，本次一律擋下（fail-closed）：{e.Message}");
                return new UCL_BartenderCliSettings();
            }
        }

        public static UCL_BartenderCliSettings CreateDefault()
        {
            var aData = new UCL_BartenderCliSettings();
            aData.users.Add(new UCL_BartenderCliUser
            {
                id = "Tim",
                display_name = "Tim",
                note = "repo 擁有者；本通道的預設唯一授權者（2026-08-19 建立時寫入）",
            });
            return aData;
        }

        public static void Save(UCL_BartenderCliSettings iData)
            => WriteAtomic(GetSettingsPath(), (iData ?? new UCL_BartenderCliSettings()).SerializeToJson().ToJsonBeautify());

        public static UCL_BartenderCliState LoadState()
        {
            string aPath = GetStatePath();
            if (!File.Exists(aPath)) return new UCL_BartenderCliState();
            try
            {
                string aJson = File.ReadAllText(aPath);
                var aState = new UCL_BartenderCliState();
                if (!string.IsNullOrEmpty(aJson)) aState.DeserializeFromJson(JsonData.ParseJson(aJson));
                if (aState.pending == null) aState.pending = new List<UCL_BartenderCliPending>();
                return aState;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BartenderCli] 狀態讀取失敗，視同沒有待確認：{e.Message}");
                return new UCL_BartenderCliState();
            }
        }

        // 區塊職責：寫狀態檔，且**空清單也要寫成 `{"pending":[]}`**。
        // 🩸 實測（2026-08-19）：清單清空之後序列化出來的檔案內容是字面的 `null`。
        //   讀取端我有 null 防護所以功能正常 —— 但一個內容是 `null` 的狀態檔會讓下一個讀它的人
        //   （人、或別的語言的工具）以為系統壞了，而它其實只是「現在沒有待確認」。
        //   **「壞掉」與「空的」在檔案上長得一樣**，所以在寫入端就把形狀固定下來。
        public static void SaveState(UCL_BartenderCliState iState)
        {
            var aState = iState ?? new UCL_BartenderCliState();
            string aText;
            if (aState.pending == null || aState.pending.Count == 0)
            {
                aText = UCL_BartenderCliState.EmptyJson;
            }
            else
            {
                var aJson = aState.SerializeToJson();
                aText = aJson != null ? aJson.ToJsonBeautify() : UCL_BartenderCliState.EmptyJson;
                if (string.IsNullOrWhiteSpace(aText) || aText.Trim() == "null")
                {
                    aText = UCL_BartenderCliState.EmptyJson;
                }
            }
            WriteAtomic(GetStatePath(), aText);
        }

        // 換檔用 File.Replace 而不是 Delete→Move：後者兩行之間有一個**檔案不存在的真空窗**，
        // 窗裡遇上 domain reload 就整份消失（2026-08-19 已在 llm_settings.json 上實地撞到一次）。
        static void WriteAtomic(string iPath, string iText)
        {
            UCL_BartenderIO.EnsureBartenderDir();
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iText, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Replace(aTmp, iPath, null);
            else File.Move(aTmp, iPath);
        }
    }
}
#endif
