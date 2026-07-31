// 區塊職責：聊天酒館「渲染筆數」參數的唯一真相源 — _last_op.md / _last_view.md 一次串幾筆訊息。
// 物理意義：這些筆數直接決定 agent 讀回結果時的 context 成本。原本四處硬編（op=read tail 預設 100、
//          op=post / op=join 重渲染 _last_view 各 100、search 100 / since_seq 200），
//          agent 端 `--arg limit=` 又只在 search / since 分支生效 → 打 limit=12 實際拿 100 筆，
//          實測一次早安 catch-up 花掉 66k token（calli 2026-07-31 盤點）。改為集中參數 + 後台可調。
// 數值影響：值走 PlayerPrefs（per-machine 持久，跟 UCL_ChatTavernSystemControl 同源，Tim 指定不用 EditorPrefs）；
//          一律經 Clamp 收在 [MinCount, MaxCount]，避免手打 0 / 負數 / 十萬筆炸 context。
// 設計取捨：不落 notify_config.json — 那份是 Python mirror 的設定；本組是 C# 渲染層行為，
//          兩者消費者不同，混在一起會讓「誰是真相源」再糊掉一次。
#if UNITY_EDITOR
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 酒館渲染筆數設定。Cmd_Tavern 各 op 未帶顯式參數時取用；後台「⚙ 參數設定」群組可調。
    /// </summary>
    public static class UCL_ChatTavernSettings
    {
        /// <summary>筆數下限 — 低於 1 等於什麼都不渲染，直接視為手滑。</summary>
        public const int MinCount = 1;
        /// <summary>筆數上限 — 500 筆已遠超任何 agent 單回合吃得下的量，再高只會炸 context。</summary>
        public const int MaxCount = 500;

        // 預設值：維持改動前的既有行為（100 / 100 / 100 / 200），本次只是把硬編搬成可調參數，不偷改預設。
        public const int DefaultReadTailCount = 100;
        public const int DefaultLastViewTailCount = 100;
        public const int DefaultSearchLimit = 100;
        public const int DefaultSinceLimit = 200;

        const string ReadTailKey = "UCL.ChatTavern.Render.ReadTailCount";
        const string LastViewKey = "UCL.ChatTavern.Render.LastViewTailCount";
        const string SearchLimitKey = "UCL.ChatTavern.Render.SearchLimit";
        const string SinceLimitKey = "UCL.ChatTavern.Render.SinceLimit";

        /// <summary>op=read 未帶 tail / from / to / search / since_seq 時，_last_op.md 串幾筆。</summary>
        public static int ReadTailCount
        {
            get => Get(ReadTailKey, DefaultReadTailCount);
            set => Set(ReadTailKey, value);
        }

        /// <summary>op=post / op=join 後重渲染 _last_view.md（同時寫進 _last_op.md）串幾筆。</summary>
        public static int LastViewTailCount
        {
            get => Get(LastViewKey, DefaultLastViewTailCount);
            set => Set(LastViewKey, value);
        }

        /// <summary>op=read search=... 未帶 limit 時的命中上限。</summary>
        public static int SearchLimit
        {
            get => Get(SearchLimitKey, DefaultSearchLimit);
            set => Set(SearchLimitKey, value);
        }

        /// <summary>op=read since_seq=... 未帶 limit 時的回補上限。</summary>
        public static int SinceLimit
        {
            get => Get(SinceLimitKey, DefaultSinceLimit);
            set => Set(SinceLimitKey, value);
        }

        /// <summary>四個參數全部回預設（刪 key，讓 getter 落回 Default*）。</summary>
        public static void ResetAll()
        {
            PlayerPrefs.DeleteKey(ReadTailKey);
            PlayerPrefs.DeleteKey(LastViewKey);
            PlayerPrefs.DeleteKey(SearchLimitKey);
            PlayerPrefs.DeleteKey(SinceLimitKey);
            PlayerPrefs.Save();
        }

        /// <summary>把任意輸入收進合法區間 — UI 與 setter 共用同一條規則，避免兩處各夾一次夾出不同結果。</summary>
        public static int Clamp(int value) => Mathf.Clamp(value, MinCount, MaxCount);

        static int Get(string key, int defaultValue) => Clamp(PlayerPrefs.GetInt(key, defaultValue));

        static void Set(string key, int value)
        {
            PlayerPrefs.SetInt(key, Clamp(value));
            PlayerPrefs.Save();   // 立即落盤：Editor 異常退出不該吃掉設定
        }
    }
}
#endif
