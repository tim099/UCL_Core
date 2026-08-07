// 區塊職責：酒保 (Bartender) 系統的資料模型 — Trigger / TimeRule / State
// 物理意義：酒保是駐留 Unity Editor 內的背景小程序, 監看 tavern 訊息 + 時間, 依規則自動發言.
//          資料分三類:
//            (1) UCL_BartenderTrigger — 留言觸發 (keyword-based, token-budgeted)
//            (2) UCL_BartenderTimeRule — 時間規則 (HH:mm cron-lite, daily one-shot)
//            (3) UCL_BartenderState — 跨 tick 狀態 (last_seen_seq + fired_today set)
// 設計取捨：用 [Serializable] + JsonUtility, 不引入 Newtonsoft (對齊 UCL_ChatTavernIO 慣例).
//          Dict 無法序列化 → fired_today 用 List<string> "YYYY-MM-DD::rule_id" key.
//          ⚠ 例外：TimeRule 因為要存多型的 reminder_lines，改走 UCL.Core.JsonLib 的
//          JsonData / JsonConvert（Tim 2026-08-07 指示）—— 見 UCL_BartenderIO.LoadTimeRules。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 留言觸發資料 — 留言者寫一條 "當 target 發言含 keyword 時, bartender 廣播 message".
    /// 觸發次數由 token 預算決定 (1 token = 1 trigger), 耗盡自動移除.
    /// </summary>
    [Serializable]
    public class UCL_BartenderTrigger : UnityJsonSerializable
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
    /// e.g. "23:50 廣播一則該睡覺的提醒".
    /// </summary>
    /// <remarks>
    /// 繼承 <see cref="UCL.Core.JsonLib.UnityJsonSerializable"/> —— 因為 reminder_lines 是多型清單，
    /// 需要走 UCL.Core.JsonLib 的 SaveFields/LoadField（會依 [SerializeReference] 存還原用的 ClassName）。
    /// 本型別因此**不再**經 JsonUtility 存取；同檔其他 model（Trigger / State）維持原樣。
    /// </remarks>
    [Serializable]
    public class UCL_BartenderTimeRule : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_IsEnable, UCLI_ShortName
    {
        public bool IsEnable { get => enabled; set => enabled = value; }
        public string GetShortName() => this.ToString();
        public override string ToString() => $"[{time_hhmm}]:{id}";
        /// <summary>規則 id (人類可讀, e.g. "default-sleep-2350").</summary>
        public string id = "ID";

        /// <summary>觸發時間 HH:mm (local time, 24-hour, e.g. "23:50").</summary>
        public string time_hhmm = "23:50";//給予初始值範例

        /// <summary>
        /// 提醒訊息內容 (主 reminder body) —— 每條規則一組 provider，廣播時以換行串接。
        /// </summary>
        /// <remarks>
        /// 區塊職責：把「提醒內文」從單一固定字串升級成可組裝的多段內容。
        /// 物理意義：一個元素 = 一行。用 <see cref="UCL_StringProvider"/> 而非 string，
        ///          是為了讓某幾行可以是「求值出來的」（之後可加時間 / 查表 / 隨機等子類），
        ///          而不必為此再改一次 schema。預設子類 <see cref="UCL_StringValueProvider"/>
        ///          就是原本的固定字串行為。
        /// 數值影響：<c>[SerializeReference]</c> 是多型的**唯一觸發訊號**
        ///          （見 <see cref="UCL_PolymorphicHelper.IsPolymorphicField"/>）——
        ///          少了它，JSON 存檔只會存下宣告型 <c>UCL_StringProvider</c> 而丟掉子類資料，
        ///          而且**不會報錯**。要動這個欄位的人請不要順手拿掉。
        /// ⚠ 舊欄位 <c>reminder_msg</c>（string）已淘汰：讀檔時由本類別的
        ///   <see cref="DeserializeFromJson"/> override 就地遷移進本欄位（見該處說明）。
        /// </remarks>
        [SerializeReference] public List<UCL_StringProvider> reminder_lines = new List<UCL_StringProvider>();


        /// <summary>
        /// 反序列化 —— 順便做舊格式（<c>reminder_msg</c> 單一字串）→ 新格式（<c>reminder_lines</c>）的就地遷移。
        /// </summary>
        /// <remarks>
        /// 區塊職責：讓「讀進來的物件」永遠是新形狀，呼叫端不必知道這個檔是哪一代寫的。
        /// 物理意義：**遷移放這裡而不是 IO 層** —— 本 override 的參數就是「這一條規則的原始 JSON」，
        ///          舊欄位當場看得到、內部欄位當場寫得進去；放在 IO 層則要把反序列化結果與來源陣列
        ///          按索引配對回去，而那個配對是一條沒有防護的隱含假設（順序一致），
        ///          schema 一動就會靜默錯位。
        /// 數值影響：純記憶體內轉換，**不回寫檔案** —— 遷移結果等下一次 SaveTimeRules 才落盤，
        ///          避免「開個頁面就默默改寫使用者的資料檔」。
        ///          舊字串**不做拆行**：拆了就改變作者原本的分行意圖，而且拆錯沒人看得出來。
        ///          一行舊內文 = 一個 <see cref="UCL_StringValueProvider"/>。
        /// </remarks>
        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            if (iJson == null || !iJson.IsObject) return;

            // 觸發條件看「新欄位在不在」而不是「舊欄位在不在」——
            // 新欄位缺席才是「這是舊檔」的充分訊號；反過來判會漏掉兩欄並存的中間態檔案。
            if (iJson.Contains(NewReminderLinesKey)) return;
            if (!iJson.Contains(LegacyReminderMsgKey)) return;   // 兩邊都沒有 = 空規則，沒東西可遷

            string aMsg = iJson.GetString(LegacyReminderMsgKey, string.Empty);
            if (string.IsNullOrEmpty(aMsg)) return;              // 空字串沒東西可遷，留空清單

            if (reminder_lines == null) reminder_lines = new List<UCL_StringProvider>();
            reminder_lines.Add(new UCL_StringValueProvider(aMsg));
            Debug.Log($"[Bartender] time rule '{id}' 已自舊欄位 {LegacyReminderMsgKey} 遷移為 {NewReminderLinesKey}（1 行）");
        }

        /// <summary>舊欄位名 —— 只在讀檔遷移時用到，寫檔一律不再產生。</summary>
        const string LegacyReminderMsgKey = "reminder_msg";

        /// <summary>新欄位名 —— 遷移的觸發判準是「這個 key 在不在」。</summary>
        const string NewReminderLinesKey = "reminder_lines";

        /// <summary>
        /// 取得廣播用的完整內文 —— 逐行求值後以換行串接。
        /// </summary>
        /// <remarks>
        /// 物理意義：這是 daemon / 頁面預覽的**唯一**組裝入口，兩邊不各自 join，
        ///          免得「預覽看到的」跟「真的廣播出去的」有一天長得不一樣。
        /// 數值影響：null 元素直接跳過（不生成空行）；求值為空字串的元素**保留**成空行 ——
        ///          那是作者刻意寫的段落間隔，跟「這一行壞掉了」不同，不該被吞掉。
        /// </remarks>
        public string GetReminderBody()
        {
            if (reminder_lines == null || reminder_lines.Count == 0) return string.Empty;
            var aParts = new List<string>(reminder_lines.Count);
            foreach (var aLine in reminder_lines)
            {
                if (aLine == null) continue;   // 沒有 provider 的空槽：跳過，不留空行
                aParts.Add(aLine.GetString());
            }
            return string.Join("\n", aParts);
        }

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

        /// <summary>當日已觸發的時間規則 key — 格式 "YYYY-MM-DD::rule_id"（daily one-shot 去重）。</summary>
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
    public class UCL_BartenderTriggerList : UnityJsonSerializable
    {
        public List<UCL_BartenderTrigger> triggers = new List<UCL_BartenderTrigger>();
    }

    /// <summary>time_rules.json 頂層 container.</summary>
    /// <remarks>
    /// 繼承 <see cref="UCL.Core.JsonLib.UnityJsonSerializable"/>：整份清單的存讀交給
    /// <c>JsonConvert.SaveFieldsToJsonUnityVer / LoadFieldFromJsonUnityVer</c>，
    /// 呼叫端不必自己走訪 rules 陣列（少一份手寫的走訪，就少一處會跟 schema 脫節的地方）。
    /// </remarks>
    [Serializable]
    public class UCL_BartenderTimeRuleList : UCL.Core.JsonLib.UnityJsonSerializable
    {
        public List<UCL_BartenderTimeRule> rules = new List<UCL_BartenderTimeRule>();

        public override void DeserializeFromJson(JsonData aJson)
        {
            base.DeserializeFromJson(aJson);
        }
    }

    // ===========================================================
    // T06.2 — Plan_Standby_Dispatch_Bartender Phase 1 MVP
    // ===========================================================

    /// <summary>
    /// Task assignment — supervisor 委派給 target_persona 的 task.
    /// MVP Pull model: supervisor 寫進 assignments.json (op=assign_add),
    /// target_persona 醒來透過 awakening.py morning ritual 結尾 (T06.4) 讀取.
    /// Phase 2 將加 Push daemon scan (sender_persona match 自動 fire tavern post).
    /// </summary>
    [Serializable]
    public class UCL_BartenderAssignment
    {
        /// <summary>唯一 id (auto uuid8) — 用於 ack / remove 反查.</summary>
        public string assignment_id;

        /// <summary>派給誰 (persona codename, e.g. "summit" / "basecamp").</summary>
        public string target_persona;

        /// <summary>Task 內容 — 自由 text.</summary>
        public string task_body;

        /// <summary>Supervisor 識別 (e.g. "Tim" / "claude-da-xiaojie").</summary>
        public string supervisor;

        /// <summary>建立時間 (UTC ISO).</summary>
        public string created_at;

        /// <summary>狀態: pending / delivered / acked / declined / deferred.</summary>
        public string status = "pending";

        /// <summary>ack action (若 status=acked|declined|deferred): accept / decline / defer.</summary>
        public string ack_action;

        /// <summary>ack 時間 (UTC ISO).</summary>
        public string ack_at;

        /// <summary>Reward (tavern_token) — agent accept 後可由 supervisor / Tim grant.</summary>
        public int reward_tokens = 0;

        /// <summary>Deadline (UTC ISO) — agent 必須在此前 ack (空 = 無 deadline).</summary>
        public string deadline;
    }

    /// <summary>assignments.json 頂層 container.</summary>
    [Serializable]
    public class UCL_BartenderAssignmentList
    {
        public List<UCL_BartenderAssignment> pending = new List<UCL_BartenderAssignment>();
    }
}
#endif
