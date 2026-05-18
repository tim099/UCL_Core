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
    }
}
