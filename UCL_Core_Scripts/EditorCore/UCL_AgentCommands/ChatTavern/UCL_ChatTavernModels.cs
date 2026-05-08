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
    }

    /// <summary>active wait 清單（JsonUtility 序列化用包裝）。</summary>
    [Serializable]
    public class UCL_ChatActiveWaitList
    {
        public List<UCL_ChatActiveWait> waits = new List<UCL_ChatActiveWait>();
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
