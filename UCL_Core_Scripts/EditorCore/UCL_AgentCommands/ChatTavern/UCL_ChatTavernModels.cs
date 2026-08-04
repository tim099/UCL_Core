// UCL Chat Tavern — 資料模型（prototype v1）
// 多 agent 聊天室的最小資料結構：身分 / 房間 / 訊息 / 引用。
// 設計取捨：訊息採 jsonl append-only，本檔僅定義模型；IO 與序列化在 UCL_ChatTavernIO。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 身分（持久化）— 跨房間共用。id 為穩定鍵（agent 後續 Post 帶入）；display_name 顯示用。
    /// 物理意義：避免不同 agent 撞名 + 讓人類在 IMGUI 看到統一稱呼。
    ///           agent-neutral 設計 — 各 agent 自選 id / display_name（Claude / Gemini / GPT 都自報名號）。
    /// </summary>
    [Serializable]
    public class UCL_ChatIdentity
    {
        public string id;            // 穩定鍵；建議 "<model>-<persona>" 例 "claude-da-xiaojie" / "gemini-da-xiaojie" / "gpt-shifu"
        public string display_name;  // 顯示名稱，例 "Claude大小姐" / "Gemini大小姐" / "GPT師傅"
        public string kind;          // "agent" | "human" | "system" | "npc"
        public string created_at;    // ISO 8601 UTC
        public string last_seen_at;  // ISO 8601 UTC

        // 註：rich persona 資料（avatar / role_settings / color / catchphrases / tags）走獨立的
        //     UCL_ChatTavernIdentityAsset (繼承 UCL_Asset<T>) per-file 儲存，不污染本 lightweight roster；
        //     Python / Cmd_Tavern 仍只看上面三欄。
    }

    /// <summary>身分清單（JsonUtility 序列化用包裝）。</summary>
    [Serializable]
    public class UCL_ChatIdentityList
    {
        public List<UCL_ChatIdentity> identities = new List<UCL_ChatIdentity>();
    }

    /// <summary>
    /// 房間中介資料。messages.jsonl / members.json / notes 子目錄都掛在 rooms/&lt;id&gt;/ 之下。
    /// </summary>
    [Serializable]
    public class UCL_ChatRoom
    {
        public string id;
        public string name;
        public string description;
        public string created_at;

        // R6 — Quest task lifecycle 鏡像到聊天室開關
        // 物理意義：true = 此房 task_create / task_claim / task_done 等事件不寫 system message 進 messages.jsonl
        // 數值影響：JsonUtility 對缺欄位 deserialize 成 default(bool)=false → 既有 meta.json 不必動，預設行為 = 鏡像 on
        public bool disable_quest_mirror;

        // R7 (T04 chat-flow-robust) — 房 owner agent
        // 物理意義：模糊「大小姐」routing 給此 agent；null/空 = 任何 agent 可接（broadcast）
        // 數值影響：純 routing hint；agent 看 SKILL Routing Rules 自律解析
        // 適用：quest 房（owner = quest-lead）/ brainstorm 主題房（owner = 主導 agent）；tavern 默認房不設
        public string owner_agent;

        // R7 (Q20260508-180358 — Quest task → Discord 推送修法 C)
        // 物理意義：per-room override 哪些 message kind 要鏡像到 Discord（tavern_mirror stream）
        // 範例 ["chat", "system"] = 對話 + R6 task lifecycle 都推；["chat"] = 只推對話
        // 數值影響：null/空 list = fallback config.tavern_mirror.kinds（向下相容）
        // 慣例：
        //   - quest 房（chat-flow-robust 等）→ ["chat", "system"]（Tim 要看 task 進度）
        //   - tavern 默認 brainstorm → ["chat"]（不要被 R6 mirror 噴爆）
        //   - agent-prompt-queue → ["chat"]（queue-idle stream 已涵蓋；避免雙重通知）
        public List<string> mirror_kinds;
    }

    /// <summary>房間清單。</summary>
    [Serializable]
    public class UCL_ChatRoomList
    {
        public List<UCL_ChatRoom> rooms = new List<UCL_ChatRoom>();
    }

    /// <summary>
    /// 房間 in-room 成員（僅存 id；display_name 從 identities.json 查）。
    /// 物理意義：標記「目前在房內」的身分；非歷史紀錄（join/leave 系統訊息留在 jsonl）。
    /// </summary>
    [Serializable]
    public class UCL_ChatRoomMembers
    {
        public string room_id;
        public List<string> member_ids = new List<string>();
    }

    /// <summary>
    /// 訊息檔案引用 — 指向專案內某個檔案（相對 repo root）。
    /// anchor 與 label 為 prototype 預留，現階段可空。
    /// </summary>
    public class UCL_ChatRef
    {
        public string path;    // 相對 repo root，例如 "CardGame/Assets/Scripts/.../X.cs"
        public string anchor;  // 例如 "line=84" 或 "guid=abc..."（v2）
        public string label;   // 顯示用短描述（v2）
    }

    /// <summary>
    /// 一筆 active wait — 對應 _active_waits.json 的一個條目。
    /// 物理意義：op=wait 改成 fire-and-forget 後，handler 立刻寫一筆 pending 進來、返回；
    ///           背景 UniTask 監看 _seq.txt，命中 / timeout 後改 status 並寫 _wait_&lt;id&gt;.md。
    ///           agent 之後用 op=wait_check 查狀態。
    /// </summary>
    [Serializable]
    public class UCL_ChatActiveWait
    {
        public string wait_id;        // 例 "20260508-005000-a1b2c3"
        public string room_id;
        public int since_seq;          // 等待 seq > since_seq 的新訊息
        public int timeout_sec;
        public string started_at;      // ISO 8601 UTC
        public string expires_at;      // ISO 8601 UTC（started_at + timeout_sec）
        public string status;          // "pending" | "fulfilled" | "timeout" | "cancelled"
        public int result_first_seq;   // 0 = N/A；fulfilled 時為命中的第一個新 seq
        public int result_count;       // 0 = N/A；fulfilled 時為新訊息數
        public string finished_at;     // ISO 8601 UTC；status 進入終態時填
        public string owner;           // 可選，發起 wait 的 identity_id

        // ── 以下三欄為 2026-08-04 加入（Tim：系統性功能固化到 C# server 端）──
        // 物理意義：wait 的完整語意本來散在 python 的 client-side polling（tavern_handshake.py）——
        //          「等誰」「誰在等」只存在那個 process 的區域變數裡，磁碟上沒有任何紀錄。
        //          於是 Editor 端想知道「現在誰被 blocking 等著」時無從得知，
        //          酒保自動通知也就無法把「被等的人」加權（Tim 2026-08-04 要的權重 100）。
        //          把它們寫進這裡＝wait 的意圖成為 server 端的一等公民，不再是 client 的私有狀態。
        public string expect_from;     // 可選，只認這個 sender_id 的回覆（對應舊 --wait-reply-from）
        public string waiter;          // 可選，發起 wait 的 persona（owner 的 persona 層，給「誰在等」顯示用）
        public bool exclude_bartender; // 酒保的氛圍插話不算數（預設 true；等的就是酒保時自動關掉）

        // 區塊職責：酒保插話的**可見性** —— 不打斷 wait，但要讓等待方知道發生過。
        // 物理意義：python client 版把酒保插話當 weak reply 直接結束 wait，agent 才看得到它。
        //          那是「為了讓人看見而砍掉正在做的事」。搬進 server 後改成：wait 照等，
        //          插話次數記在這裡，等待方輪詢時看到計數變動就印出來 —— 兩件事都拿到。
        // 數值影響：npc_cups 累加；達 UCL_TavernWaitNpc.RestHintDrinks 時等待方該自決收 turn。
        public int npc_cups;           // 本次 wait 期間酒保插話累計杯數
        public int npc_after_sec;      // 幾秒後才開始插話（0 = 用預設；測試/調校可調小）
    }

    /// <summary>active wait 清單（JsonUtility 序列化用包裝）。</summary>
    [Serializable]
    public class UCL_ChatActiveWaitList
    {
        public List<UCL_ChatActiveWait> waits = new List<UCL_ChatActiveWait>();
    }

    /// <summary>
    /// 一筆訊息。T38 後對應「per-message file」格式 — 每筆訊息一獨立 .json 檔。
    /// 檔名約定：rooms/<room>/messages/<YYYY-MM-DD>/<HHMMSS>_<MMM>_<UUID6>.json
    /// kind 開放：chat / join / leave / system / note_ref / tool_call / tool_result。
    ///
    /// 新欄位（T38）：
    ///   - uuid：6-char hex，跟檔名 UUID 對齊；用於去重 + reply_to_uuid 跨檔引用
    ///   - reply_to_uuid：取代舊 reply_to (int seq)，因 seq 是 derived 不再 stable
    ///
    /// 舊欄位 seq 變 derived（reader 動態算）— 寫入時不寫進檔；讀取時根據 walk + ts sort 補上。
    /// 舊欄位 reply_to (int) 仍保留 backward-compat 但 deprecated。
    /// </summary>
    public class UCL_ChatMessage
    {
        public int seq;                          // T38: derived by reader (not persisted in per-msg file)
        public string ts;                        // ISO 8601 UTC + millisecond ("2026-05-09T08:47:52.312Z")
        public string uuid;                      // T38 NEW: 6-char hex; 跟檔名 UUID 對齊
        public string sender_id;
        public string sender_name;
        public string sender_persona;            // Phase 1 (Tim 2026-05-11 拍板) — 同 actor 不同 persona (basecamp / ridge-001 etc.) 的時間分層 first-class 標記；空字串/null = legacy/未走 persona 機制
        public string sender_avatar_sprite;      // T28 (Tim 2026-05-14 拍板) — 渲染端用 sprite_id (e.g. "Avatars_basecamp"); Cmd_Tavern Op_Post lookup PersonaCard.AvatarSprite 命中 → 用 persona avatar; 否則 fallback IdentityAsset.AvatarSprite; 兩個都沒 → 空字串 (UI 端 render 預設頭像).
        public string kind;                      // "chat" 為預設
        public string body;
        public int? reply_to;                    // T38 deprecated: 改用 reply_to_uuid（保留欄位讓舊 record load 不爆）
        public string reply_to_uuid;             // T38 NEW: 取代 reply_to int seq 的跨檔引用方式
        public Dictionary<string, string> meta;  // 自由 key-value
        public List<UCL_ChatRef> refs;           // 檔案引用列表

        /// <summary>
        /// Phase 1 (Tim 2026-05-11 拍板) — 渲染用 display name。
        /// Proposal #29 Phase 1: delegate 到 UCL_AgentIdParser.Display 集中 logic。
        /// 物理意義: 統一 render 點 — 帶 persona = "name@persona" / 不帶 = "name"。
        /// 使用點: UCL_ChatTavernRender / UCL_ChatTavernPage / Discord notify 都該走本 helper。
        /// </summary>
        public string DisplayName =>
            UCL.Core.EditorLib.AgentCommands.Common.UCL_AgentIdParser.Display(
                sender_id, sender_persona, sender_name);
    }

}
#endif
