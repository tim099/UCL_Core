// 區塊職責：聊天酒館「渲染筆數」參數的唯一真相源 — _last_op.md / _last_view.md 一次串幾筆訊息，
//            以及 wake brief §8 酒館 catch-up 撈幾筆。
// 物理意義：這些筆數直接決定 agent 讀回結果時的 context 成本。原本四處硬編（op=read tail 預設 100、
//          op=post / op=join 重渲染各 100、search 100 / since_seq 200），
//          agent 端 `--arg limit=` 又只在 search / since 分支生效 → 打 limit=12 實際拿 100 筆，
//          實測一次早安 catch-up 花掉 66k token（calli 2026-07-31 盤點）。
// 數值影響：**落 JSON 檔而非 PlayerPrefs**（Tim 2026-07-31 要求 catch-up 筆數也進後台）——
//          catch-up 那筆的消費者是 Python（wake_brief.py），PlayerPrefs 在 Windows 存在登錄檔、
//          Python 讀不到。兩邊要看同一個數字，就不能各存各的。
//          舊 PlayerPrefs 值在 JSON 不存在時仍會被讀出來當種子（一次性遷移，不丟 Tim 已調過的設定）。
// 設計取捨：不塞進 notify_config.json —— 那份是 Python mirror 的設定，混進來會讓「誰是真相源」再糊一次。
#if UNITY_EDITOR
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 酒館渲染筆數設定。Cmd_Tavern 各 op 未帶顯式參數時取用；後台「⚙ 參數設定」群組可調；
    /// Python 端（wake_brief.py）讀同一份 JSON。
    /// </summary>
    public static class UCL_ChatTavernSettings
    {
        /// <summary>筆數下限 — 低於 1 等於什麼都不渲染，直接視為手滑。</summary>
        public const int MinCount = 1;
        /// <summary>筆數上限 — 500 筆已遠超任何 agent 單回合吃得下的量，再高只會炸 context。</summary>
        public const int MaxCount = 500;

        // 預設值：維持改動前的既有行為，本次只是把硬編搬成可調參數，不偷改預設。
        public const int DefaultReadTailCount = 100;
        public const int DefaultLastViewTailCount = 100;
        public const int DefaultSearchLimit = 100;
        public const int DefaultSinceLimit = 200;
        public const int DefaultBriefCatchupCount = 10;   // wake brief §8：撈 10 筆他人訊息

        // 檔名固定；目錄走可 override 的 AgentCommands 資料根（跨專案不寫死安裝路徑）
        public const string SettingsFileName = "render_settings.json";

        static string SettingsPath =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", SettingsFileName);

        // legacy PlayerPrefs key（2026-07-31 上午版本用過）— 只在 JSON 缺該欄時當種子讀一次
        const string LegacyReadTailKey = "UCL.ChatTavern.Render.ReadTailCount";
        const string LegacyLastViewKey = "UCL.ChatTavern.Render.LastViewTailCount";
        const string LegacySearchLimitKey = "UCL.ChatTavern.Render.SearchLimit";
        const string LegacySinceLimitKey = "UCL.ChatTavern.Render.SinceLimit";

        /// <summary>op=read 未帶 tail / from / to / search / since_seq 時，_last_op.md 串幾筆。</summary>
        public static int ReadTailCount
        {
            get => Get("read_tail_count", DefaultReadTailCount, LegacyReadTailKey);
            set => Set("read_tail_count", value);
        }

        /// <summary>op=post / op=join 後重渲染 _last_view.md（同時寫進 _last_op.md）串幾筆。</summary>
        public static int LastViewTailCount
        {
            get => Get("last_view_tail_count", DefaultLastViewTailCount, LegacyLastViewKey);
            set => Set("last_view_tail_count", value);
        }

        /// <summary>op=read search=... 未帶 limit 時的命中上限。</summary>
        public static int SearchLimit
        {
            get => Get("search_limit", DefaultSearchLimit, LegacySearchLimitKey);
            set => Set("search_limit", value);
        }

        /// <summary>op=read since_seq=... 未帶 limit 時的回補上限。</summary>
        public static int SinceLimit
        {
            get => Get("since_limit", DefaultSinceLimit, LegacySinceLimitKey);
            set => Set("since_limit", value);
        }

        /// <summary>wake brief §8 酒館 catch-up 撈幾筆（消費者是 Python 端 wake_brief.py）。</summary>
        public static int BriefCatchupCount
        {
            get => Get("brief_catchup_count", DefaultBriefCatchupCount, null);
            set => Set("brief_catchup_count", value);
        }

        /// <summary>全部回預設 —— 直接刪檔，讓每個 getter 落回 Default*。</summary>
        public static void ResetAll()
        {
            try { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); }
            catch (System.Exception e) { Debug.LogWarning($"[TavernSettings] reset 失敗: {e.Message}"); }
        }

        /// <summary>把任意輸入收進合法區間 — UI 與 setter 共用同一條規則，避免兩處各夾一次夾出不同結果。</summary>
        public static int Clamp(int value) => Mathf.Clamp(value, MinCount, MaxCount);

        static JsonData Load()
        {
            try
            {
                if (File.Exists(SettingsPath)) return JsonData.ParseJson(File.ReadAllText(SettingsPath));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[TavernSettings] 讀取失敗（改用預設）: {e.Message}");
            }
            return JsonData.ParseJson("{}");
        }

        static int Get(string key, int defaultValue, string legacyPrefKey)
        {
            var jd = Load();
            if (jd != null && jd.Contains(key)) return Clamp(jd.GetInt(key, defaultValue));
            // JSON 沒這欄 → 讀 legacy PlayerPrefs 當種子（Tim 上午在舊版調過的值不該無聲消失）
            if (!string.IsNullOrEmpty(legacyPrefKey) && PlayerPrefs.HasKey(legacyPrefKey))
                return Clamp(PlayerPrefs.GetInt(legacyPrefKey, defaultValue));
            return defaultValue;
        }

        static void Set(string key, int value)
        {
            int v = Clamp(value);
            var jd = Load();
            jd[key] = new JsonData(v);
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 原子寫：tmp + replace，避免 Python 端剛好讀到寫一半的檔
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, jd.ToJsonBeautify());
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(tmp, SettingsPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[TavernSettings] 寫入失敗: {e.Message}");
            }
        }
    }
}
#endif
