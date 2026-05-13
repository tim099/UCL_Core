// 區塊職責：酒保 (Bartender) 系統的資料模型 — Trigger / TimeRule / State
// 物理意義：酒保是駐留 Unity Editor 內的背景小程序, 監看 tavern 訊息 + 時間, 依規則自動發言.
//          資料分三類:
//            (1) UCL_BartenderTrigger — 留言觸發 (keyword-based, token-budgeted)
//            (2) UCL_BartenderTimeRule — 時間規則 (HH:mm cron-lite, daily one-shot)
//            (3) UCL_BartenderState — 跨 tick 狀態 (last_seen_seq + fired_today set)
// 設計取捨：用 [Serializable] + JsonUtility, 不引入 Newtonsoft (對齊 UCL_ChatTavernIO 慣例).
//          Dict 無法序列化 → fired_today 用 List<string> "YYYY-MM-DD::rule_id" key.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 留言觸發資料 — 留言者寫一條 "當 target 發言含 keyword 時, bartender 廣播 message".
    /// 觸發次數由 token 預算決定 (1 token = 1 trigger), 耗盡自動移除.
    /// </summary>
    [Serializable]
    public class UCL_BartenderTrigger
    {
        /// <summary>uuid (8-hex) — 留言唯一識別.</summary>
        public string id;

        /// <summary>建立留言的 sender_id (e.g. "Zeta-da-xiaojie") — 用於 [{creator}的留言(N)] 顯示前綴.</summary>
        public string creator_id;

        /// <summary>display name (creator 對應的 sender_name, e.g. "Zeta大小姐"). 空 → 用 creator_id.</summary>
        public string creator_name;

        /// <summary>
        /// 目標清單 — match 規則: 訊息的 sender_id / sender_name / sender_persona 任一含 substring (case-insensitive).
        /// 空 list = match 任何 sender (廣域 trigger). 設計上鼓勵明確指定避免 noise.
        /// </summary>
        public List<string> targets = new List<string>();

        /// <summary>觸發關鍵字 — case-insensitive substring match on message body.</summary>
        public string keyword;

        /// <summary>留言內容 — bartender 觸發時會以此為 body 發到 tavern (走 AppendMessage → 自動 Discord mirror).</summary>
        public string message;

        /// <summary>剩餘觸發次數 — 初始 = tokens (or default 1). 每 fire 一次 -1, 歸 0 移除.</summary>
        public int remaining_triggers;

        /// <summary>初始 token 預算 (用於 audit / display).</summary>
        public int initial_tokens;

        /// <summary>建立時間 (UTC ISO).</summary>
        public string created_at;

        /// <summary>目標 room id (空 = "tavern" 主廳).</summary>
        public string target_room = "tavern";
    }

    /// <summary>
    /// 時間規則 — 每天指定 HH:mm 觸發一次, daily one-shot (state 內 fired_today key 防同日重觸).
    /// e.g. "23:50 提醒 Tim 該睡覺 + grace 過後 HP penalty".
    /// </summary>
    [Serializable]
    public class UCL_BartenderTimeRule
    {
        /// <summary>規則 id (人類可讀, e.g. "default-sleep-2350").</summary>
        public string id;

        /// <summary>觸發時間 HH:mm (local time, 24-hour, e.g. "23:50").</summary>
        public string time_hhmm;

        /// <summary>提醒對象顯示名 (e.g. "Tim") — 訊息開頭 @target 用.</summary>
        public string target_id;

        /// <summary>提醒訊息內容 (主 reminder body).</summary>
        public string reminder_msg;

        /// <summary>寬限期分鐘數 — 過了 time_hhmm + grace_minutes 才會觸發 penalty warning.</summary>
        public int grace_minutes = 10;

        /// <summary>penalty 啟用? 啟用後 bartender 在寬限期過後每隔 penalty_interval_minutes 廣播一次 HP 扣血公式提醒.</summary>
        public bool penalty_enabled = false;

        /// <summary>penalty 重複間隔 (e.g. 每 5 分鐘廣播一次扣血累積).</summary>
        public int penalty_interval_minutes = 5;

        /// <summary>penalty 對象 (e.g. "Tim") — bartender 訊息會 @ 此 id.</summary>
        public string penalty_target;

        /// <summary>規則啟用? 設 false 可暫停而不刪除.</summary>
        public bool enabled = true;

        /// <summary>規則目標 room (空 = "tavern").</summary>
        public string target_room = "tavern";
    }

    /// <summary>
    /// Bartender 跨 tick 狀態 — last_seen_seq 防止重看舊訊息, fired_today_keys 防同日重觸發時間規則.
    /// </summary>
    [Serializable]
    public class UCL_BartenderState
    {
        /// <summary>上次掃描到的最大 seq (per room) — 避免重複掃舊訊息. 用 List of Pair 因 JsonUtility 不吃 Dict.</summary>
        public List<UCL_BartenderRoomSeq> room_last_seq = new List<UCL_BartenderRoomSeq>();

        /// <summary>當日已觸發的時間規則 key — 格式 "YYYY-MM-DD::rule_id" (reminder) or "YYYY-MM-DD::rule_id::penalty::N" (第 N 次 penalty 廣播).</summary>
        public List<string> fired_today_keys = new List<string>();

        /// <summary>上次跑 overnight deposit 保管費檢查的日期 (local YYYY-MM-DD). 跨日時 daemon 觸發新一輪檢查.
        /// 空字串 = daemon 首次啟動, 不收費直接 init 成 today (避免新裝立刻課稅).</summary>
        public string last_overnight_check_date;

        /// <summary>state 上次更新時間 (UTC ISO) — debug 用.</summary>
        public string last_updated;
    }

    /// <summary>per-room seq 紀錄 (取代 Dict 因 JsonUtility 限制).</summary>
    [Serializable]
    public class UCL_BartenderRoomSeq
    {
        public string room_id;
        public int last_seq;
    }

    /// <summary>triggers.json 頂層 container (List<T> 不能直接 serialize, 用 wrapper).</summary>
    [Serializable]
    public class UCL_BartenderTriggerList
    {
        public List<UCL_BartenderTrigger> triggers = new List<UCL_BartenderTrigger>();
    }

    /// <summary>time_rules.json 頂層 container.</summary>
    [Serializable]
    public class UCL_BartenderTimeRuleList
    {
        public List<UCL_BartenderTimeRule> rules = new List<UCL_BartenderTimeRule>();
    }
}
#endif
