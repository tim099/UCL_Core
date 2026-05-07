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
        public string kind;          // "agent" | "human" | "system"
        public string created_at;    // ISO 8601 UTC
        public string last_seen_at;  // ISO 8601 UTC
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
    /// 一筆訊息（對應 messages.jsonl 一行）。
    /// kind 開放：chat / join / leave / system / note_ref / tool_call / tool_result。
    /// </summary>
    public class UCL_ChatMessage
    {
        public int seq;
        public string ts;             // ISO 8601 UTC
        public string sender_id;
        public string sender_name;    // 寫入時 snapshot；事後若 identity 改名也不影響歷史
        public string kind;           // "chat" 為預設
        public string body;
        public int? reply_to;         // 可選：回覆某 seq
        public Dictionary<string, string> meta;  // 自由 key-value
        public List<UCL_ChatRef> refs;            // 檔案引用列表
    }
}
#endif
