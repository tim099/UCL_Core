// 區塊職責：T40 Treasury Ledger schema models
// 物理意義：每筆 ledger entry 對應一個獨立 .json 檔；schema 含 actor_signature 防盜用
// 數值影響：純資料容器；序列化 / 反序列化由 UCL_TreasuryLedger 自己處理（同 ChatTavern 慣例）
//
// 2026-05-18 (gura): 去掉 #if UNITY_EDITOR guard — 純 data class, 無 Editor 依賴.
// 對齊 UCL_TreasuryLedger.cs 2026-05-13 (Zeta) 已 strip 同 guard 的決策.
// 之前 guard 殘留導致 Player Build 撞 CS0246 (consumer 找不到 TreasuryLedgerEntry / TreasuryEntryType,
// 因為 Models 被 #if 排除但 Ledger 不被排除).

using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>Ledger entry type — 進帳 / 出帳。</summary>
    public enum TreasuryEntryType
    {
        Credit,    // 進帳（system → account）
        Debit,     // 出帳（account → system）
    }

    /// <summary>
    /// Ledger entry — 對應一個 .json 檔.
    /// Schema 跟 Plan_Tavern_Treasury_Ledger.md §2.3 對齊.
    /// </summary>
    public class TreasuryLedgerEntry
    {
        public string ts;                               // ISO 8601 UTC + ms
        public string uuid;                             // 6-char hex；對齊檔名 UUID
        public string type;                             // "credit" / "debit"
        public int amount;                              // 正整數
        public string currency = "tavern_token";        // v1 只支援 tavern_token

        public string account_id;                       // 受款 / 扣款帳戶

        // 資金來源（credit）or 用途（debit）— 對應 rules.json income_sources / spending_uses key
        public string source_kind;                      // "tim_grant" / "task_completion" / "tavern_post" / etc.
        public string source_ref;                       // 引用 ID（task_id / message_uuid / bonus_id）
        public string source_description;               // 自由文字描述

        public int balance_before;                      // 寫入時的當下 balance（給 audit）
        public int balance_after;                       // = balance_before ± amount

        // actor_signature — 防盜用核心；caller 自報 + server 偵測雙紀錄
        public string sig_agent_id_claimed;             // caller 自報的 agent_id
        public string sig_process_id;                   // Editor process ID
        public string sig_env_marker;                   // server detect: "claude-code" / "antigravity" / "unknown"
        public string sig_cmd_id;                       // 觸發本 entry 的 cmd_id（給 audit trace）

        public bool signature_mismatch;                 // sig_agent_id_claimed 跟 sig_env_marker 不一致 → true

        // 冪等鍵（2026-08-01 雙扣事故對策）— caller 顯式帶才啟用判重；空 = 不判重（照舊）。
        // 物理意義：同 (type, account_id, idempotency_key) 在同一 UTC 日已有 entry → 第二次寫入被
        //          抑制並回傳既有 entry。「這筆金流要不要防重」是呼叫端的顯式宣告，工具不猜 —
        //          同一天打賞同一本書兩次是合法的（不帶 key），酒館 post 自動扣款重跑不該扣兩次（帶 key）。
        public string idempotency_key;
    }

    // ===========================================================
    // 區塊職責：請款單（payout request）— agent 主張「我該收一筆錢」，等 Tim 從後台批款
    // 物理意義：跟 ledger entry 是**兩種不同的東西**：
    //          ledger entry = 已成事實的收付（帳面餘額的組成部分）
    //          請款單       = 尚未發生的主張（核准後才生出對應 ledger entry）
    //          分開存的理由：混進 ledger 會讓餘額包含未核准的主張 —— 那是假帳。
    // 數值影響：本類別純資料；金額只在**核准時**才透過 UCL_TreasuryLedger.Credit 變成真錢。
    //          status 是唯一會被就地改寫的欄位（pending → approved / rejected / cancelled），
    //          其餘欄位建立後不可變 —— 改了就對不上核准當時所依據的內容。
    // 設計取捨：**target_bank 由請款者顯式宣告，不從聊天 sender 推斷。**
    //          2026-07-31 血證：commit 薪資 hook 拿貼文 sender 當帳戶，summit 帶了 persona 名
    //          （`summit` 而非 bank `zeta`）→ 錢進了影子帳戶。身分層 routing 靠推斷必出事，
    //          所以請款單要求顯式寫明收款 bank，並在核准時由人眼二次確認。
    // ===========================================================
    public class TreasuryPayoutRequest
    {
        public string request_id;          // <UUID6> — 對齊檔名，核准 / 駁回 / 取消都用它定位
        public string requested_at;        // ISO 8601 UTC + ms
        public string status;              // "pending" / "approved" / "rejected" / "cancelled"

        // ---- 請款內容（建立後不可變）----
        public string target_bank;         // 收款帳戶（bank id，例 cc / zeta / Myth）— 顯式宣告，不推斷
        public int amount;                 // 正整數
        public string currency = "tavern_token";
        public string reason;              // 為什麼該付這筆（給人看的，核准與否靠它判斷）
        public string source_kind;         // 核准後寫進 ledger 的 source_kind（例 commit / tim_grant）
        public string source_ref;          // 憑證引用（SHA / task_id / message uuid…），可為空

        // ---- 請款者身分（雙欄：agent 層 + persona 層，跟酒館訊息同慣例）----
        public string requester_agent;     // agent id（例 claude-code / Zeta / Myth）
        public string requester_persona;   // persona codename（例 gura / summit），可為空

        // ---- 審批結果（pending 時全空）----
        public string decided_at;          // ISO 8601 UTC + ms
        public string decided_by;          // 核准者（後台批款一律 "Tim"）
        public string decision_note;       // 駁回理由 / 核准備註
        public string ledger_entry_uuid;   // 核准後對應的 ledger entry uuid（對帳用）
    }
}
