// UCL Chat Tavern — IMGUI 頁面（prototype v1）
// 職責：人類可在 Editor 內直接以某個 identity 加入房間 / 看訊息 / 發言。
// 物理意義：跟 agent 走的 Cmd 接同一份檔案 → 人 vs agent 同一個聊天室。
// prototype 取捨：UI 字串先硬編；refs/meta 表單最簡（單行 paths + key=val）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Chat Tavern IMGUI 頁面 — 房間下拉 + 訊息檢視 + 發言。
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernPage.md")]
    public class UCL_ChatTavernPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Chat Tavern";

        // opt-in 進 UCL_EditorMenuPage 的 Page 選擇器下拉 — 
        // 物理意義：已提升為 EditorMenu 外部主要按鈕，此處關閉以避免下拉選單重複出現
        public override bool ShowInPageMenu => false;

        public static UCL_ChatTavernPage Create() => UCL_EditorPage.Create<UCL_ChatTavernPage>();

        // ===== 選擇狀態 =====
        // 區塊職責：使用者目前選中的房間 / 身分 + 暫存的輸入框內容
        // 物理意義：純 UI 狀態；按下 Send 才會落檔
        string m_SelectedRoomId = "";
        string m_SelectedIdentityId = "";
        string m_Input = "";
        string m_MetaInput = "";        // "k1=v1;k2=v2"
        string m_RefsInput = "";        // "path1|path2"
        int? m_ReplyTo = null;
        Vector2 m_MessagesScroll = Vector2.zero;
        // 區塊職責：自動捲到底的觸發旗標 + 訊息數快照
        // 物理意義：使用者習慣聊天室「最新訊息在底部」 — 進房 / 收到新訊息時自動捲到底；
        //          滾上去看歷史訊息時不要被自動拉回（只在 count 變大時觸發）
        // 數值影響：m_PendingScrollToBottom = true 會在下一次 BeginScrollView 後把 m_MessagesScroll.y
        //          設為 float.MaxValue（IMGUI 自動 clamp 到實際底部）
        bool m_PendingScrollToBottom = false;
        int m_LastSeenMsgCount = 0;
        string m_LastSeenRoomId = ""; // 跨房間切換偵測用：room id 變了 → 強制捲底

        // ===== 新建表單 =====
        bool m_ShowCreateRoom = false;
        bool m_ShowCharMapping = false;
        // ===== Deferred-apply（避免 button click 在 MouseUp 改 layout-affecting state，造成 Layout vs Repaint 結構不一致）=====
        // 物理意義：button 在 MouseUp event return true，若直接 mutate 會讓同一 frame 的 Repaint event 看到不同 state；
        //          延後到下個 frame 的 Layout event 才套用，可保證單一 frame 內 layout 結構恆定。
        bool? m_PendingShowCreateRoom = null;
        bool? m_PendingShowCreateIdentity = null;
        // 暫存「+ 新 alias」表單輸入；空 = 還沒填
        string m_NewAliasFrom = "";
        string m_NewAliasTo = "";
        string m_NewRoomId = "";
        string m_NewRoomName = "";
        string m_NewRoomDesc = "";

        bool m_ShowCreateIdentity = false;
        // 區塊職責：「+ 新身分」表單預設值
        // 物理意義：刻意留空 — agent-neutral；別讓人以為這個系統是 Claude 專屬
        // 數值影響：使用者每次點「+ 新身分」要主動填 id / name；建議格式見表單上方 hint
        string m_NewIdentityId = "";
        string m_NewIdentityName = "";
        string m_NewIdentityKind = "agent";

        // ===== 顯示快取 =====
        // 物理意義：每幀重讀 jsonl 太重；只有 Refresh / Send / 進房間時重抓
        // rooms / identities dropdown 來源都從 lightweight roster (rooms.json / identities.json) 改成 UCL_Asset.GetAllIDs()；
        // roster 仍保留給 Cmd_Tavern (Op_Post / Op_CreateRoom) 用 — 對齊既有行為
        UCL_ChatRoomList m_RoomsCache;               // rooms.json 的 roster — 用來查 name / description（顯示用）
        List<string> m_RoomIds;                      // UCL_ChatTavernRoomAsset asset id 清單（dropdown 來源）
        List<string> m_IdentityIds;                  // UCL_ChatTavernIdentityAsset asset id 清單（dropdown 來源）
        UCL_ChatIdentityList m_RosterCache;          // identities.json 的 roster — 用來查 display_name / kind（顯示用）
        List<UCL_ChatMessage> m_MessagesCache;
        UCL_ChatRoomMembers m_MembersCache;

        // PopupSearchCache 內部狀態容器（per-page；同一 page 多個 popup 用 key 區分）
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        // ===== 自動 polling =====
        // 物理意義：勾選後每 N 秒重抓 messages — 模擬即時聊天感
        bool m_AutoPoll = true;
        double m_LastPollTime = 0;
        const double PollIntervalSec = 2.0;

        // 區塊職責：頁面首次繪製時的一次性自動初始化旗標
        // 物理意義：第一次 ContentOnGUI 時：若沒有任何房間 → 建一間 default；若沒選中房間 → 自動選第一間
        // 數值影響：旗標確保只跑一次；Refresh 不重置（避免人為清掉 m_SelectedRoomId 後又被自動拉回）
        bool m_AutoInitDone = false;

        // 區塊職責：握手活躍狀態快取 — 給「中止握手」按鈕變色用
        // 物理意義：Python 端 wait_for_tavern_reply 每 poll touch _handshake_active.flag；
        //          mtime 距今 < HandshakeStaleSec 視為活躍。RequiresConstantRepaint 會每幀進來，
        //          所以加 throttle 避免每幀做 File IO（cf. 主訊息 polling 的 PollIntervalSec=2.0）
        // 數值影響：m_HandshakeActive 控制按鈕顏色 + DisabledScope；
        //          HandshakeStaleSec=2.0 = 4× Python poll interval (0.5s)，足夠抗 jitter 但不至於
        //          Python crash 後還顯示活躍超久
        bool m_HandshakeActive = false;
        double m_HandshakeStartUnix = 0;          // _handshake_start.txt 內容（Python time.time() float）
        double m_BartenderLastDrinkUnix = 0;      // _bartender_state.json sessions 中最近一筆 last_drink_at（unix）
        int m_BartenderConsecutiveDrinks = 0;
        double m_LastHandshakeCheckTime = 0;
        const double HandshakeCheckIntervalSec = 0.5;
        const double HandshakeStaleSec = 2.0;
        // 跟 Python 端常數對齊（測試 10s / production 480s；cooldown 90s）— 純顯示用，不影響觸發邏輯
        const double BartenderTriggerSec = 10.0;
        const double BartenderCooldownSec = 90.0;
        const int BartenderRestHintDrinks = 3;

        // Avatar 快取：sender_id → Sprite（lazy 載入 UCL_ChatTavernIdentityAsset.m_AvatarSprite）
        // 物理意義：訊息列表對每筆 sender_id 顯示頭像；同 sender 多筆訊息共用一張 Sprite，避免每幀重複載入
        // 數值影響：Sprite null 視為「沒配置 / Asset 不存在」，UI 顯示佔位 box
        readonly Dictionary<string, Sprite> m_AvatarCache = new Dictionary<string, Sprite>();

        // ===== Quest Panel =====
        // 物理意義：當房間有 events.jsonl → 顯示 task tree 面板（reducer 重放出當前 task states）
        // 數值影響：cache 每 QuestRefreshIntervalSec 重算一次；點選展開狀態 per-task 持有
        bool m_ShowQuestPanel = true;
        Dictionary<string, UCL_QuestTaskState> m_QuestStatesCache;
        string m_QuestExpandedTaskId = "";          // 點某 task → 展開 timeline；空 = 無展開
        string m_QuestStatusFilter = "all";          // all / pending / claimed / in_progress / review / done / ready / stale
        bool m_QuestFilterByMe = false;              // 只顯示 owner = m_SelectedIdentityId
        Vector2 m_QuestScroll = Vector2.zero;
        double m_LastQuestRefreshTime = 0;
        const double QuestRefreshIntervalSec = 2.0;

        // F3 — Quest 統計 7 計數 cache（跟 ComputeTaskStates 同 throttle）
        // 物理意義：每幀 foreach states.Values 算 done/claimed/in_progress/... 等於 Cache 之上再每幀重做一次 7 條件分類；
        //          反正 m_QuestStatesCache 自身有 throttle，計數陣列同步 cache 即可
        // 數值影響：跟 m_QuestStatesCache 同生命週期；refresh 觸發時一起更新
        int m_QuestCachedTotal, m_QuestCachedDone, m_QuestCachedClaimed, m_QuestCachedInProg,
            m_QuestCachedReview, m_QuestCachedReady, m_QuestCachedBlocked, m_QuestCachedStale;

        // F2 — DrawMessagesView 標題列 ReadCurrentSeq 結果 cache
        // 物理意義：標題每幀讀檔 _seq.txt 顯示最新 seq → File.ReadAllText 60Hz IO 純為了畫個數字
        //          改成 throttle 0.5s 取一次，跟 PollIntervalSec=2.0 / HandshakeCheckIntervalSec=0.5 同精神
        // 數值影響：使用者最多落後 0.5s 看到新 seq，沒人在意
        string m_CachedSeqRoom = null;
        int m_CachedSeq = 0;
        double m_LastSeqRefreshTime = 0;
        const double SeqRefreshIntervalSec = 0.5;

        // F1 — 訊息行用的 GUIStyle static cache（每幀 N 個 new GUIStyle 是 GC 主因）
        // 物理意義：DrawMessageRow 對每筆訊息每幀 new 3 個 GUIStyle (name/meta/body)，N=200 訊息×60fps=36k 物件/秒
        //          靜態複用 — textColor 隨 senderColor 變動就 mutate 同一份 instance（IMGUI 同 frame 立即繪製，安全）
        // 數值影響：GC 壓力預期 -80%，視覺輸出完全一致
        static GUIStyle s_MetaStyle;
        static GUIStyle s_NameStyleBold;
        static GUIStyle s_BodyStyleWrap;
        static GUIStyle s_BodyStyleNonChat;          // wrap + italic（join/leave/system 用）
        static System.Text.StringBuilder s_MetaBuilder;  // DrawMessageRow meta 拼接 reuse；Clear() 不釋放 capacity

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                RefreshAll();
            }
            m_AutoPoll = GUILayout.Toggle(m_AutoPoll, "Auto-Poll", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
            if (GUILayout.Button("Open Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_ChatTavernIO.EnsureTavernDir();
                UnityEditor.EditorUtility.RevealInFinder(UCL_ChatTavernIO.GetTavernDir());
            }

            // 區塊職責：「中止 Python 握手等待」按鈕（活躍時亮橘紅色 + 可按；無握手時灰色 + Disabled）
            // 物理意義：run_cmd.py 的 Tavern op=post --wait-reply 會 client-side polling
            //          messages.jsonl 等對方回覆；本按鈕 touch _handshake_cancel.flag，那邊偵測到
            //          mtime > wait_start 就會提前退出（exit code=2）。
            //          活躍偵測：Python 端 wait loop 每 poll touch _handshake_active.flag，
            //          C# 端讀其 mtime — < 2s 視為活躍。
            // 數值影響：active = 橘紅 (1, 0.6, 0.4)；inactive = 暗灰 (0.45) + DisabledScope 防誤點。
            //          連點兩次也安全（cancel flag mtime 會推進 → 後續 wait 仍能用）。
            UpdateHandshakeActive();
            Color btnColor = m_HandshakeActive ? new Color(1f, 0.6f, 0.4f) : new Color(0.45f, 0.45f, 0.45f);
            string btnLabel = m_HandshakeActive ? "🛑 中止握手" : "🛑 中止握手 (無)";
            using (new EditorGUI.DisabledScope(!m_HandshakeActive))
            {
                if (GUILayout.Button(btnLabel, UCL_GUIStyle.GetButtonStyle(btnColor), GUILayout.ExpandWidth(false)))
                {
                    UCL_ChatTavernIO.EnsureTavernDir();
                    string flagPath = Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "_handshake_cancel.flag");
                    File.WriteAllText(flagPath, System.DateTime.UtcNow.ToString("o"));
                    Debug.Log($"[Tavern] Wrote handshake cancel flag → {flagPath}");
                }
            }
        }

        // 區塊職責：throttle 過的握手活躍 + bartender 狀態檢查
        // 物理意義：每 HandshakeCheckIntervalSec 秒做一次 File IO 抓三個檔的狀態 — handshake_active
        //          (mtime → 活躍)、handshake_start (content → wait_start unix)、bartender_state (last_drink_at)
        // 數值影響：寫 m_HandshakeActive / m_HandshakeStartUnix / m_BartenderLastDrinkUnix / m_BartenderConsecutiveDrinks
        void UpdateHandshakeActive()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - m_LastHandshakeCheckTime < HandshakeCheckIntervalSec) return;
            m_LastHandshakeCheckTime = now;

            string tavernDir = UCL_ChatTavernIO.GetTavernDir();
            string activePath = Path.Combine(tavernDir, "_handshake_active.flag");
            if (!File.Exists(activePath))
            {
                m_HandshakeActive = false;
                m_HandshakeStartUnix = 0;
            }
            else
            {
                try
                {
                    double age = (System.DateTime.UtcNow - File.GetLastWriteTimeUtc(activePath)).TotalSeconds;
                    m_HandshakeActive = age < HandshakeStaleSec;
                }
                catch (System.IO.IOException) { m_HandshakeActive = false; }
            }

            // wait_start (Python time.time() float) — content of _handshake_start.txt
            string startPath = Path.Combine(tavernDir, "_handshake_start.txt");
            m_HandshakeStartUnix = 0;
            if (File.Exists(startPath))
            {
                try
                {
                    string s = File.ReadAllText(startPath).Trim();
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        m_HandshakeStartUnix = v;
                }
                catch { /* race ok */ }
            }

            // bartender state — pick MAX last_drink_at across sessions（顯示用，誰都行）
            string statePath = Path.Combine(tavernDir, "_bartender_state.json");
            m_BartenderLastDrinkUnix = 0;
            m_BartenderConsecutiveDrinks = 0;
            if (File.Exists(statePath))
            {
                try
                {
                    string json = File.ReadAllText(statePath);
                    // 簡易解析（避免引入 JsonUtility/Newtonsoft 麻煩 — 只抓兩欄）
                    var lastDrinks = System.Text.RegularExpressions.Regex.Matches(json, "\"last_drink_at\"\\s*:\\s*\"([^\"]+)\"");
                    foreach (System.Text.RegularExpressions.Match mt in lastDrinks)
                    {
                        if (System.DateTime.TryParse(mt.Groups[1].Value, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                        {
                            double unix = (dt - new System.DateTime(1970,1,1, 0,0,0, System.DateTimeKind.Utc)).TotalSeconds;
                            if (unix > m_BartenderLastDrinkUnix) m_BartenderLastDrinkUnix = unix;
                        }
                    }
                    var drinks = System.Text.RegularExpressions.Regex.Matches(json, "\"consecutive_drinks\"\\s*:\\s*(\\d+)");
                    foreach (System.Text.RegularExpressions.Match mt in drinks)
                    {
                        if (int.TryParse(mt.Groups[1].Value, out var v) && v > m_BartenderConsecutiveDrinks)
                            m_BartenderConsecutiveDrinks = v;
                    }
                }
                catch { /* race ok */ }
            }
        }

        // 區塊職責：頂部酒保資訊條 — 顯示活躍 / 倒數 / 連喝計數 + 「催促酒保 -30s」按鈕
        // 物理意義：給使用者一目了然「酒保下次插話還要多久」 — 數值用 Python 端 wait_start + bartender_state 算
        // 數值影響：點催促按鈕 → 寫 _handshake_hurry.flag（Python 端 poll loop 偵測到 → wait_start / cooldown 各 -30s）
        void DrawBartenderInfoBar()
        {
            UpdateHandshakeActive();
            using (new GUILayout.HorizontalScope("box"))
            {
                if (!m_HandshakeActive)
                {
                    GUILayout.Label("🍺 酒保：沉睡中（沒進行中的 wait）", UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                    return;
                }

                double nowUnix = (System.DateTime.UtcNow - new System.DateTime(1970,1,1, 0,0,0, System.DateTimeKind.Utc)).TotalSeconds;
                double countdown = -1;
                string statusText;
                if (m_HandshakeStartUnix > 0)
                {
                    double elapsed = nowUnix - m_HandshakeStartUnix;
                    if (elapsed < BartenderTriggerSec)
                    {
                        countdown = BartenderTriggerSec - elapsed;
                        statusText = $"🍺 酒保：首杯倒數 {countdown:F0}s";
                    }
                    else
                    {
                        // 已過 trigger 門檻，算 cooldown
                        double cooldownLeft = m_BartenderLastDrinkUnix > 0
                            ? BartenderCooldownSec - (nowUnix - m_BartenderLastDrinkUnix)
                            : 0;
                        if (cooldownLeft <= 0)
                        {
                            statusText = "🍺 酒保：隨時可插話";
                        }
                        else
                        {
                            countdown = cooldownLeft;
                            statusText = $"🍺 酒保：下杯倒數 {countdown:F0}s";
                        }
                    }
                }
                else
                {
                    statusText = "🍺 酒保：握手活躍中（無 start 資料）";
                }

                GUILayout.Label(statusText, UCL_GUIStyle.LabelStyle);

                // 連喝計數
                if (m_BartenderConsecutiveDrinks > 0)
                {
                    string drinkLabel = $"  已喝 {m_BartenderConsecutiveDrinks} 杯";
                    if (m_BartenderConsecutiveDrinks >= BartenderRestHintDrinks)
                        drinkLabel += " (達休息門檻)";
                    var style = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    if (m_BartenderConsecutiveDrinks >= BartenderRestHintDrinks)
                        style.normal.textColor = new Color(1f, 0.5f, 0.3f);
                    GUILayout.Label(drinkLabel, style);
                }

                GUILayout.FlexibleSpace();

                // 催促按鈕 — 寫 hurry flag，Python 偵測後 wait_start / cooldown 各 -30s
                using (new EditorGUI.DisabledScope(countdown <= 0))
                {
                    if (GUILayout.Button("⏩ 催促酒保 -30s", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.3f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_ChatTavernIO.EnsureTavernDir();
                        string flagPath = Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "_handshake_hurry.flag");
                        File.WriteAllText(flagPath, System.DateTime.UtcNow.ToString("o"));
                        Debug.Log($"[Tavern] 催促酒保 → wrote {flagPath}");
                    }
                }
            }
        }

        // 區塊職責：畫指定 sender 的 avatar（含 cache 載入 + 灰色佔位 fallback）
        // 物理意義：訊息列表 / input bar 共用此 helper；GUILayoutUtility.GetRect 取 size×size 區塊，
        //          有 Sprite 用 DrawTextureWithTexCoords（處理 sprite atlas rect / texture uv）
        // 數值影響：純繪製，不寫檔；占用 layout 一個方塊
        void DrawAvatar(string senderId, float size)
        {
            var avatar = GetAvatarSprite(senderId);
            var rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            if (avatar != null && avatar.texture != null)
            {
                var r = avatar.textureRect;
                var tex = avatar.texture;
                var uv = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
                GUI.DrawTextureWithTexCoords(rect, tex, uv);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f, 0.4f));
            }
        }

        // 區塊職責：Lookup 並 cache sender 對應的 Sprite（透過 UCL_ChatTavernCharacterMapping 三段查找）
        // 物理意義：sender_id 可能對不上任何 Asset → mapping 表把它導向「真正有 Asset 的」identity_id；
        //          再不行就用預設 fallback identity（character_mapping.json 的 default_identity_id）。
        // 數值影響：cache 命中 = 0 IO；miss → mapping load（自帶 mtime cache）+ 一次 ContainsAsset/GetData
        Sprite GetAvatarSprite(string senderId)
        {
            if (string.IsNullOrEmpty(senderId)) return null;
            if (m_AvatarCache.TryGetValue(senderId, out var cached)) return cached;
            Sprite sp = null;
            try
            {
                string resolvedId = UCL_ChatTavernCharacterMapping.ResolveIdentityId(senderId);
                if (!string.IsNullOrEmpty(resolvedId))
                {
                    var asset = new UCL_ChatTavernIdentityAsset();
                    if (asset.ContainsAsset(resolvedId))
                    {
                        var ident = asset.GetData(resolvedId);
                        // 區塊職責：守衛 m_AvatarSprite 為「未設定」狀態
                        // 物理意義：UCL_SpriteAssetEntry 預設 m_ID = "Default"，沒指定 sprite 時直接讀 .Sprite
                        //          會去 UCL_Asset 系統查 ID="Default" 的 sprite asset → 不存在 → 噴 exception。
                        //          每個沒設 avatar 的 identity 都會噴一次（且 OnGUI 每幀重試）
                        // 數值影響：跳過已知為「空」的 entry，僅在 ID 真的是使用者填過的值才 fetch
                        var sprEntry = ident?.m_AvatarSprite;
                        if (sprEntry != null
                            && !string.IsNullOrEmpty(sprEntry.ID)
                            && sprEntry.ID != UCL_SpriteAssetEntry.DefaultID)
                        {
                            sp = sprEntry.Sprite;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UCL_ChatTavernPage] GetAvatarSprite({senderId}) 失敗：{ex.Message}");
            }
            m_AvatarCache[senderId] = sp;   // null 也 cache 避免每次都試載
            return sp;
        }

        protected override void ContentOnGUI()
        {
            using (UCL_ChatTavernPerfOverlay.Sample("ContentOnGUI/total"))
            {
                // 延後套用上一 frame button click 觸發的 layout-affecting state mutation。
                // 必須在 Layout event 套用：Repaint event 套用會讓同一 frame 的 Layout 跟 Repaint 看到不一樣的 state。
                if (Event.current.type == EventType.Layout)
                {
                    if (m_PendingShowCreateRoom.HasValue) { m_ShowCreateRoom = m_PendingShowCreateRoom.Value; m_PendingShowCreateRoom = null; }
                    if (m_PendingShowCreateIdentity.HasValue) { m_ShowCreateIdentity = m_PendingShowCreateIdentity.Value; m_PendingShowCreateIdentity = null; }
                }

                if (m_RoomsCache == null || m_RoomIds == null) RefreshRooms();
                if (m_IdentityIds == null) RefreshIdentities();
                EnsureAutoInit();
                HandleAutoPoll();

                // Perf overlay 區塊：toggle bar 永遠顯示（小體積）；overlay 詳細表只在 ShowOverlay=true 時開
                using (new GUILayout.HorizontalScope())
                {
                    bool show = GUILayout.Toggle(UCL_ChatTavernPerfOverlay.ShowOverlay, "⏱ Perf", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (show != UCL_ChatTavernPerfOverlay.ShowOverlay)
                    {
                        UCL_ChatTavernPerfOverlay.ShowOverlay = show;
                        // 開 overlay 自動把 sample 也開（沒取樣 overlay 沒東西看）
                        if (show) UCL_ChatTavernPerfOverlay.Enabled = true;
                    }
                    if (UCL_ChatTavernPerfOverlay.ShowOverlay && GUILayout.Button("Dump Log", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_ChatTavernPerfOverlay.DumpToLog();
                    }
                    GUILayout.FlexibleSpace();
                }
                UCL_ChatTavernPerfOverlay.DrawOverlay();

                using (UCL_ChatTavernPerfOverlay.Sample("DrawBartenderInfoBar")) DrawBartenderInfoBar();
                GUILayout.Space(4);
                using (UCL_ChatTavernPerfOverlay.Sample("DrawRoomPicker")) DrawRoomPicker();
                GUILayout.Space(4);
                using (UCL_ChatTavernPerfOverlay.Sample("DrawIdentityPicker")) DrawIdentityPicker();
                GUILayout.Space(4);
                using (UCL_ChatTavernPerfOverlay.Sample("DrawCharacterMappingFoldout")) DrawCharacterMappingFoldout();
                GUILayout.Space(8);

                if (string.IsNullOrEmpty(m_SelectedRoomId))
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label("請先選擇或建立一個房間。", UCL_GUIStyle.LabelStyle);
                    }
                    return;
                }
                using (UCL_ChatTavernPerfOverlay.Sample("DrawQuestPanel")) DrawQuestPanel();
                GUILayout.Space(4);
                using (UCL_ChatTavernPerfOverlay.Sample("DrawMessagesView")) DrawMessagesView();
                GUILayout.Space(4);
                using (UCL_ChatTavernPerfOverlay.Sample("DrawInputBar")) DrawInputBar();
            }
        }

        // ===========================================================
        // 區塊：Quest Panel — task tree 視覺化
        // 物理意義：當房間有 events.jsonl → 顯示 reducer 算出的 task 狀態
        //          沒 events.jsonl → 整個 panel 摺疊（不是 quest 房不展示）
        // 數值影響：每 QuestRefreshIntervalSec 重算 ComputeTaskStates；點 task 展開 timeline
        // ===========================================================
        void DrawQuestPanel()
        {
            string roomDir = UCL_ChatTavernIO.GetRoomDir(m_SelectedRoomId);
            string eventsPath = Path.Combine(roomDir, "events.jsonl");
            if (!File.Exists(eventsPath))
            {
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label("🏛 Quest: 此房間尚無 events.jsonl（非 quest 房）。用 task_create 建第一個任務即會啟用。",
                        UCL_GUIStyle.LabelStyle);
                }
                return;
            }

            // throttle 重算
            double now = EditorApplication.timeSinceStartup;
            if (m_QuestStatesCache == null || now - m_LastQuestRefreshTime > QuestRefreshIntervalSec)
            {
                try
                {
                    m_QuestStatesCache = UCL_ChatTavernQuestIO.ComputeTaskStates(m_SelectedRoomId);
                    m_LastQuestRefreshTime = now;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[ChatTavernPage] Quest reducer 失敗: {ex.Message}");
                    m_QuestStatesCache = new Dictionary<string, UCL_QuestTaskState>();
                }
                // F3 — 計數同步 cache（每幀 foreach 7 條件改成 refresh 觸發一次）
                m_QuestCachedTotal = m_QuestStatesCache.Count;
                m_QuestCachedDone = m_QuestCachedClaimed = m_QuestCachedInProg = 0;
                m_QuestCachedReview = m_QuestCachedReady = m_QuestCachedBlocked = m_QuestCachedStale = 0;
                foreach (var s in m_QuestStatesCache.Values)
                {
                    if (s.is_stale) m_QuestCachedStale++;
                    switch (s.status)
                    {
                        case "done": m_QuestCachedDone++; break;
                        case "claimed": m_QuestCachedClaimed++; break;
                        case "in_progress": m_QuestCachedInProg++; break;
                        case "review": m_QuestCachedReview++; break;
                        case "pending":
                            if (UCL_ChatTavernQuestIO.IsReady(s, m_QuestStatesCache)) m_QuestCachedReady++;
                            else m_QuestCachedBlocked++;
                            break;
                    }
                }
            }

            using (new GUILayout.VerticalScope("box"))
            {
                // 標題列 + 折疊按鈕
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(m_ShowQuestPanel ? "▼" : "▶", UCL_GUIStyle.ButtonStyle, GUILayout.Width(28)))
                    {
                        m_ShowQuestPanel = !m_ShowQuestPanel;
                    }
                    // F3 — 直接用 cached 計數，每幀不再 foreach 7 條件分類
                    GUILayout.Label($"🏛 <b>Quest Tasks</b>  total={m_QuestCachedTotal}  ✅{m_QuestCachedDone}  🚧{m_QuestCachedInProg}  🔍{m_QuestCachedReview}  🔒{m_QuestCachedClaimed}  🟢{m_QuestCachedReady}  ⏳{m_QuestCachedBlocked}" + (m_QuestCachedStale > 0 ? $"  🔴{m_QuestCachedStale}" : ""),
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🔄", UCL_GUIStyle.ButtonStyle, GUILayout.Width(32)))
                    {
                        m_QuestStatesCache = null; // 強制下幀重算
                    }
                }
                if (!m_ShowQuestPanel) return;

                // 我的 inbox 提示
                DrawMyInboxHint();

                // filter row
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Filter status:", UCL_GUIStyle.LabelStyle, GUILayout.Width(90));
                    string[] statusOpts = { "all", "ready", "claimed", "in_progress", "review", "done", "pending", "stale" };
                    int curIdx = System.Array.IndexOf(statusOpts, m_QuestStatusFilter);
                    if (curIdx < 0) curIdx = 0;
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, statusOpts, m_PickerDic, "QuestStatusFilter", GUILayout.Width(120));
                    if (newIdx != curIdx) m_QuestStatusFilter = statusOpts[newIdx];
                    GUILayout.Space(8);
                    bool by = GUILayout.Toggle(m_QuestFilterByMe, "只看我認領的", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (by != m_QuestFilterByMe) m_QuestFilterByMe = by;
                    GUILayout.FlexibleSpace();
                }

                // task table（scroll）
                m_QuestScroll = GUILayout.BeginScrollView(m_QuestScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(360));
                if (m_QuestStatesCache != null && m_QuestStatesCache.Count > 0)
                {
                    var sorted = new List<UCL_QuestTaskState>(m_QuestStatesCache.Values);
                    sorted.Sort((a, b) =>
                    {
                        int sa = QuestStatusSortKey(a), sb = QuestStatusSortKey(b);
                        if (sa != sb) return sa - sb;
                        int pa = UCL_ChatTavernQuestIO.PriorityScore(a);
                        int pb = UCL_ChatTavernQuestIO.PriorityScore(b);
                        if (pa != pb) return pb - pa;
                        return b.downstream_weight - a.downstream_weight;
                    });
                    foreach (var st in sorted)
                    {
                        if (!QuestPassesFilter(st)) continue;
                        DrawQuestTaskRow(st);
                    }
                }
                else
                {
                    GUILayout.Label("(尚無 task；用 task_create 建第一個)", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.EndScrollView();
            }
        }

        bool QuestPassesFilter(UCL_QuestTaskState st)
        {
            // 我的 filter
            if (m_QuestFilterByMe && st.owner != m_SelectedIdentityId) return false;
            // status filter
            if (m_QuestStatusFilter == "all") return true;
            if (m_QuestStatusFilter == "stale") return st.is_stale;
            if (m_QuestStatusFilter == "ready")
                return st.status == "pending" && UCL_ChatTavernQuestIO.IsReady(st, m_QuestStatesCache);
            if (m_QuestStatusFilter == "pending")
                return st.status == "pending" && !UCL_ChatTavernQuestIO.IsReady(st, m_QuestStatesCache);
            return st.status == m_QuestStatusFilter;
        }

        int QuestStatusSortKey(UCL_QuestTaskState s)
        {
            switch (s.status)
            {
                case "review": return 0;
                case "in_progress": return 1;
                case "claimed": return 2;
                case "pending": return UCL_ChatTavernQuestIO.IsReady(s, m_QuestStatesCache) ? 3 : 4;
                case "done": return 5;
                default: return 6;
            }
        }

        void DrawQuestTaskRow(UCL_QuestTaskState st)
        {
            bool expanded = m_QuestExpandedTaskId == st.id;
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    string mark = QuestStatusEmoji(st);
                    if (st.is_stale) mark = "🔴" + mark;
                    GUILayout.Label(mark, UCL_GUIStyle.LabelStyle, GUILayout.Width(28));
                    if (GUILayout.Button(st.id, UCL_GUIStyle.ButtonStyle, GUILayout.Width(150)))
                    {
                        m_QuestExpandedTaskId = expanded ? "" : st.id;
                    }
                    GUILayout.Label(string.IsNullOrEmpty(st.title) ? "(no title)" : st.title,
                        UCL_GUIStyle.LabelStyle, GUILayout.MinWidth(120));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"P:{st.priority}", UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    GUILayout.Label($"D:{st.downstream_weight}", UCL_GUIStyle.LabelStyle, GUILayout.Width(40));
                    GUILayout.Label(string.IsNullOrEmpty(st.owner) ? "-" : st.owner,
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                    GUILayout.Label(string.IsNullOrEmpty(st.role) ? "-" : st.role,
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                }
                if (!expanded) return;

                // 展開：spec 摘要 + lifecycle timeline + 操作 hint
                GUILayout.Space(2);
                if (st.depends_on != null && st.depends_on.Count > 0)
                {
                    GUILayout.Label($"Depends on: {string.Join(", ", st.depends_on)}", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Label($"Created: {st.created_at}  ({st.age_days:F1}d ago)  reject_count={st.reject_count}",
                    UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(st.lease_until))
                    GUILayout.Label($"Lease until: {st.lease_until}" + (st.is_stale ? "  ⚠ STALE" : ""), UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(st.last_progress_summary))
                    GUILayout.Label($"Last progress: {st.last_progress_summary}", UCL_GUIStyle.LabelStyle);

                // spec 預覽 — 走內嵌 UCL_MarkdownViewerPage（不離開 Unity 視窗）
                string specPath = UCL_ChatTavernQuestIO.GetTaskSpecPath(m_SelectedRoomId, st.id);
                if (File.Exists(specPath))
                {
                    if (GUILayout.Button("📄 開啟 spec", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        OpenInMarkdownViewer(specPath);
                    }
                }

                // timeline
                GUILayout.Space(2);
                GUILayout.Label("<b>Lifecycle Timeline:</b>", UCL_GUIStyle.LabelStyle);
                foreach (var ev in st.lifecycle)
                {
                    string detail = "";
                    if (ev.data != null && ev.data.TryGetValue("summary", out var sm) && !string.IsNullOrEmpty(sm))
                        detail = $" — {sm}";
                    else if (ev.data != null && ev.data.TryGetValue("reason", out var rs) && !string.IsNullOrEmpty(rs))
                        detail = $" — reason: {rs}";
                    GUILayout.Label($"  • <b>seq={ev.seq}</b> [{ev.ts}] <i>{ev.type}</i> by {ev.actor}{detail}",
                        UCL_GUIStyle.LabelStyle);
                }

                // hint
                GUILayout.Space(2);
                string hint = QuestActionHint(st);
                if (!string.IsNullOrEmpty(hint))
                    GUILayout.Label($"💡 {hint}", UCL_GUIStyle.LabelStyle);
            }
        }

        string QuestStatusEmoji(UCL_QuestTaskState s)
        {
            switch (s.status)
            {
                case "done": return "✅";
                case "review": return "🔍";
                case "in_progress": return "🚧";
                case "claimed": return "🔒";
                case "pending":
                    return UCL_ChatTavernQuestIO.IsReady(s, m_QuestStatesCache) ? "🟢" : "⏳";
                default: return "⚪";
            }
        }

        string QuestActionHint(UCL_QuestTaskState s)
        {
            switch (s.status)
            {
                case "pending":
                    if (UCL_ChatTavernQuestIO.IsReady(s, m_QuestStatesCache))
                        return $"task_claim task_id={s.id} claimer=<你>";
                    return "等 deps 完成（pending blocked）";
                case "claimed":
                case "in_progress":
                    return $"task_progress / task_review_request / task_done task_id={s.id}";
                case "review":
                    return $"reviewer 動作：task_done 通過 / task_reject reason=...";
                case "done":
                    return $"task_reopen reason=... 可重開（找到問題時）";
                default: return "";
            }
        }

        void DrawMyInboxHint()
        {
            if (string.IsNullOrEmpty(m_SelectedIdentityId)) return;
            string inboxPath = UCL_ChatTavernQuestIO.GetInboxPath(m_SelectedRoomId, m_SelectedIdentityId);
            if (!File.Exists(inboxPath)) return;
            // 簡單算行數（## 開頭 = 一筆 inbox entry）
            int entries = 0;
            try
            {
                foreach (var line in File.ReadAllLines(inboxPath))
                {
                    if (line.StartsWith("## ")) entries++;
                }
            }
            catch { }
            if (entries == 0) return;
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label($"📬 你的 inbox ({m_SelectedIdentityId}) 有 {entries} 筆通知", UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button("📬 開啟 inbox", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    OpenInMarkdownViewer(inboxPath);
                }
            }
        }

        // ===========================================================
        // 區塊：房間選擇 + 建立
        // ===========================================================
        void DrawRoomPicker()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("房間：", UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_RoomIds == null || m_RoomIds.Count == 0)
                {
                    GUILayout.Label("(尚無房間 — 跑 Cmd_SeedTavernRoomAssets 或下面 + 新房間)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    // 區塊職責：房間下拉 — 來源 UCL_ChatTavernRoomAsset.GetAllIDs()
                    // 物理意義：display 用 roster (rooms.json) 的 name 對照；缺 roster 條目就 fallback 顯示 ID
                    // 數值影響：選定 → m_SelectedRoomId 更新 + RefreshMessages/Members
                    var labels = new List<string>(m_RoomIds.Count);
                    int curIdx = 0;
                    for (int i = 0; i < m_RoomIds.Count; i++)
                    {
                        string id = m_RoomIds[i];
                        var r = m_RoomsCache?.rooms?.Find(x => x != null && x.id == id);
                        string display = r != null && !string.IsNullOrEmpty(r.name) ? r.name : id;
                        labels.Add($"{display}  [{id}]");
                        if (id == m_SelectedRoomId) curIdx = i;
                    }
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, labels, m_PickerDic, "RoomPicker", GUILayout.Width(420));
                    if (newIdx != curIdx && newIdx >= 0 && newIdx < m_RoomIds.Count)
                    {
                        m_SelectedRoomId = m_RoomIds[newIdx];
                        RefreshMessages();
                        RefreshMembers();
                    }
                }
                if (GUILayout.Button(m_ShowCreateRoom ? "− Cancel" : "+ 新房間", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_PendingShowCreateRoom = !m_ShowCreateRoom;
                }

                // 加入 / 離開房間按鈕 — 必須無條件開啟 inner scope，並維持穩定控制項數。
                // 條件式 (if !empty(roomId)&&!empty(identityId)) 開 scope 會在 popup 改 m_SelectedRoomId
                // 後讓 Layout / Repaint 看到不同的 layout 結構，Unity 2021 IMGUI 拋 ExitGUIException: Mismatched LayoutGroup。
                using (new GUILayout.HorizontalScope())
                {
                    bool canAct = !string.IsNullOrEmpty(m_SelectedRoomId) && !string.IsNullOrEmpty(m_SelectedIdentityId);
                    bool isMember = canAct && m_MembersCache != null && m_MembersCache.member_ids.Contains(m_SelectedIdentityId);

                    string btnText = !canAct ? "(選房間+身分後可加入/離開)"
                                   : isMember ? $"離開「{m_SelectedRoomId}」"
                                              : $"加入「{m_SelectedRoomId}」";
                    var btnStyle = !canAct ? UCL_GUIStyle.ButtonStyle
                                 : isMember ? UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.4f))
                                            : UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 1f, 0.4f));

                    bool oldEnabled = GUI.enabled;
                    GUI.enabled = canAct;
                    if (GUILayout.Button(btnText, btnStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (isMember) DoLeave();
                        else DoJoin();
                    }
                    GUI.enabled = oldEnabled;

                    GUILayout.FlexibleSpace();

                    // 「登錄人數」而非「在場人數」 — agent turn-based 不會主動 leave，
                    // 顯示的是「曾經 join 過的累計成員」，不代表現在活躍。
                    // 真實活躍偵測需 last_active_at（Phase B；見 Quest_Workflow.md §12.6）
                    string memberText = m_MembersCache != null
                        ? $"登錄 {m_MembersCache.member_ids.Count} 人"
                        : "(尚未進房)";
                    var memberContent = new GUIContent(memberText, "曾經 join 過的累計人數，不代表當前活躍\n（agent 是 turn-based，turn 結束 ≠ 離房）");
                    GUILayout.Label(memberContent, UCL_GUIStyle.LabelStyle);
                }
                GUILayout.FlexibleSpace();

            }
            if (m_ShowCreateRoom)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    m_NewRoomId = LabeledTextField("id", m_NewRoomId, 60);
                    m_NewRoomName = LabeledTextField("name", m_NewRoomName, 60);
                    m_NewRoomDesc = LabeledTextField("desc", m_NewRoomDesc, 60);
                    if (GUILayout.Button("Create", UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewRoomId))
                        {
                            Debug.LogError("Room id 不能為空");
                        }
                        else
                        {
                            // 雙寫：roster (rooms.json) + UCL_ChatTavernRoomAsset .json shell
                            UCL_ChatTavernIO.CreateRoom(m_NewRoomId, m_NewRoomName, m_NewRoomDesc);
                            try
                            {
                                var asset = new UCL_ChatTavernRoomAsset(m_NewRoomId);
                                if (!asset.ContainsAsset(m_NewRoomId)) asset.Save();
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"[UCL_ChatTavernPage] 創建 UCL_ChatTavernRoomAsset shell 失敗（roster 已建好，可手動跑 Cmd_SeedTavernRoomAssets 補）：{ex.Message}");
                            }
                            m_SelectedRoomId = m_NewRoomId;
                            m_NewRoomId = m_NewRoomName = m_NewRoomDesc = "";
                            m_PendingShowCreateRoom = false; // 延後到下個 Layout event 才把表單收起來，避免本 frame Repaint 看到不同 layout 結構
                            RefreshAll();
                        }
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：身分選擇 + 建立
        // ===========================================================
        void DrawIdentityPicker()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("身分：", UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_IdentityIds == null || m_IdentityIds.Count == 0)
                {
                    GUILayout.Label("(尚無身分 — 跑 Cmd_SeedTavernIdentityAssets 或下面 + 新身分)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    // 區塊職責：身分下拉選單 — 來源是 UCL_ChatTavernIdentityAsset.GetAllIDs()
                    // 物理意義：display 用 roster (identities.json) 的 display_name 對照；缺 roster 條目就 fallback 顯示 ID
                    // 數值影響：選定 → m_SelectedIdentityId 更新；不寫任何檔
                    var labels = new List<string>(m_IdentityIds.Count);
                    int curIdx = 0;
                    for (int i = 0; i < m_IdentityIds.Count; i++)
                    {
                        string id = m_IdentityIds[i];
                        var ros = m_RosterCache?.identities?.Find(x => x != null && x.id == id);
                        string display = ros != null && !string.IsNullOrEmpty(ros.display_name) ? ros.display_name : id;
                        string kind = ros != null && !string.IsNullOrEmpty(ros.kind) ? ros.kind : "?";
                        labels.Add($"{display} ({kind})  [{id}]");
                        if (id == m_SelectedIdentityId) curIdx = i;
                    }
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, labels, m_PickerDic, "IdentityPicker", GUILayout.Width(420));
                    if (newIdx >= 0 && newIdx < m_IdentityIds.Count) m_SelectedIdentityId = m_IdentityIds[newIdx];
                }
                if (GUILayout.Button(m_ShowCreateIdentity ? "− Cancel" : "+ 新身分", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_PendingShowCreateIdentity = !m_ShowCreateIdentity;
                }
                GUILayout.FlexibleSpace();

            }
            if (m_ShowCreateIdentity)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    // 區塊職責：身分命名約定 hint — 提醒使用者本系統是 agent-neutral
                    // 物理意義：避免有人以為這只給 Claude 用 → 列出三家 agent 的範例 id
                    GUILayout.Label("命名約定：id 用 <model>-<persona>，例：claude-da-xiaojie / gemini-da-xiaojie / gpt-shifu",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("display_name 用 agent 自家稱呼，例：Claude大小姐 / Gemini大小姐 / GPT師傅",
                        UCL_GUIStyle.LabelStyle);
                    m_NewIdentityId = LabeledTextField("id", m_NewIdentityId, 60);
                    m_NewIdentityName = LabeledTextField("name", m_NewIdentityName, 60);
                    m_NewIdentityKind = LabeledTextField("kind", m_NewIdentityKind, 60);
                    if (GUILayout.Button("Create", UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewIdentityId))
                        {
                            Debug.LogError("Identity id 不能為空");
                        }
                        else
                        {
                            // 雙寫：roster (identities.json) + UCL_ChatTavernIdentityAsset .json shell
                            // 物理意義：roster 給 Cmd_Tavern.Op_Post 查 display_name；Asset 是 dropdown 來源 + rich data 載體
                            UCL_ChatTavernIO.GetOrCreateIdentity(m_NewIdentityId, m_NewIdentityName, m_NewIdentityKind);
                            try
                            {
                                var asset = new UCL_ChatTavernIdentityAsset(m_NewIdentityId);
                                if (!asset.ContainsAsset(m_NewIdentityId))
                                {
                                    asset.m_Tags = new List<string> { string.IsNullOrEmpty(m_NewIdentityKind) ? "agent" : m_NewIdentityKind };
                                    asset.Save();
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"[UCL_ChatTavernPage] 創建 UCL_ChatTavernIdentityAsset shell 失敗（roster 已建好，可手動跑 Cmd_SeedTavernIdentityAssets 補）：{ex.Message}");
                            }
                            m_SelectedIdentityId = m_NewIdentityId;
                            m_PendingShowCreateIdentity = false; // 延後到下個 Layout event 才收表單，避免本 frame Repaint 看到不同 layout 結構
                            RefreshIdentities();
                        }
                    }
                }
            }

        }

        // ===========================================================
        // 區塊：角色 Mapping foldout — 編輯 character_mapping.json
        // 物理意義：sender_id 對不上 Asset 時，UI 走三段查找：
        //   (1) 直接 match → (2) alias 表 → (3) default_identity_id
        //   本 foldout 提供圖形編輯介面；底層走 UCL_ChatTavernCharacterMapping
        // ===========================================================
        void DrawCharacterMappingFoldout()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    string label = m_ShowCharMapping ? "▼ 角色 Mapping" : "▶ 角色 Mapping";
                    if (GUILayout.Button(label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_ShowCharMapping = !m_ShowCharMapping;
                    }
                    GUILayout.Label("（sender_id 沒對應角色時的 fallback 鏈）", UCL_GUIStyle.LabelStyle);
                }

                if (!m_ShowCharMapping) return;

                var data = UCL_ChatTavernCharacterMapping.Load();
                bool dirty = false;

                // default
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Default", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    string newDefault = GUILayout.TextField(data.default_identity_id ?? "", GUILayout.MinWidth(220));
                    if (newDefault != (data.default_identity_id ?? ""))
                    {
                        data.default_identity_id = newDefault;
                        dirty = true;
                    }
                    GUILayout.Label("（建議用已建 Identity Asset 的 id）", UCL_GUIStyle.LabelStyle);
                }

                GUILayout.Space(4);
                GUILayout.Label("Aliases（sender_id → identity_id）：", UCL_GUIStyle.LabelStyle);

                if (data.aliases != null)
                {
                    int removeIndex = -1;
                    for (int i = 0; i < data.aliases.Count; i++)
                    {
                        var entry = data.aliases[i];
                        if (entry == null) continue;
                        using (new GUILayout.HorizontalScope())
                        {
                            string newFrom = GUILayout.TextField(entry.from ?? "", GUILayout.MinWidth(180));
                            GUILayout.Label("→", UCL_GUIStyle.LabelStyle, GUILayout.Width(20));
                            string newTo = GUILayout.TextField(entry.to ?? "", GUILayout.MinWidth(180));
                            if (newFrom != (entry.from ?? "") || newTo != (entry.to ?? ""))
                            {
                                entry.from = newFrom;
                                entry.to = newTo;
                                dirty = true;
                            }
                            if (GUILayout.Button("✕", UCL_GUIStyle.ButtonStyle, GUILayout.Width(28)))
                            {
                                removeIndex = i;
                            }
                        }
                    }
                    if (removeIndex >= 0)
                    {
                        data.aliases.RemoveAt(removeIndex);
                        dirty = true;
                    }
                }

                // 新增 alias 表單
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("+ 新 alias", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    m_NewAliasFrom = GUILayout.TextField(m_NewAliasFrom ?? "", GUILayout.MinWidth(180));
                    GUILayout.Label("→", UCL_GUIStyle.LabelStyle, GUILayout.Width(20));
                    m_NewAliasTo = GUILayout.TextField(m_NewAliasTo ?? "", GUILayout.MinWidth(180));
                    bool canAdd = !string.IsNullOrWhiteSpace(m_NewAliasFrom) && !string.IsNullOrWhiteSpace(m_NewAliasTo);
                    using (new EditorGUI.DisabledScope(!canAdd))
                    {
                        if (GUILayout.Button("Add", UCL_GUIStyle.GetButtonStyle(canAdd ? Color.green : Color.gray), GUILayout.Width(60)))
                        {
                            UCL_ChatTavernCharacterMapping.AddOrUpdateAlias(m_NewAliasFrom.Trim(), m_NewAliasTo.Trim());
                            m_NewAliasFrom = "";
                            m_NewAliasTo = "";
                            m_AvatarCache.Clear();
                        }
                    }
                }

                if (dirty)
                {
                    UCL_ChatTavernCharacterMapping.Save(data);
                    m_AvatarCache.Clear();
                }
            }
        }

        // ===========================================================
        // 區塊：訊息檢視
        // ===========================================================
        void DrawMessagesView()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // F2 — ReadCurrentSeq 走 cache（throttle 0.5s 或 room 切換時刷新），避免每幀 File IO
                double nowSeq = EditorApplication.timeSinceStartup;
                if (m_CachedSeqRoom != m_SelectedRoomId || nowSeq - m_LastSeqRefreshTime > SeqRefreshIntervalSec)
                {
                    m_CachedSeq = UCL_ChatTavernIO.ReadCurrentSeq(m_SelectedRoomId);
                    m_CachedSeqRoom = m_SelectedRoomId;
                    m_LastSeqRefreshTime = nowSeq;
                }
                GUILayout.Label($"# 🍺 {m_SelectedRoomId} (seq={m_CachedSeq})", UCL_GUIStyle.LabelStyle);
                // 區塊職責：BeginScrollView 之前若 m_PendingScrollToBottom == true → 強制把 y 設大值
                // 物理意義：IMGUI 會自動 clamp 到 contentHeight - viewportHeight；下一幀就在底部
                //          設完清旗標，避免使用者滾上去看歷史時被自動拉回
                if (m_PendingScrollToBottom)
                {
                    m_MessagesScroll.y = float.MaxValue;
                    m_PendingScrollToBottom = false;
                }
                m_MessagesScroll = GUILayout.BeginScrollView(m_MessagesScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(560)));
                if (m_MessagesCache == null || m_MessagesCache.Count == 0)
                {
                    GUILayout.Label("_(尚無訊息)_", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    // Sample name 必須 stable（dict key）；訊息數量在 overlay 看 calls 與 m_MessagesCache.Count 心算即可
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawMessagesView/rows"))
                    {
                        foreach (var m in m_MessagesCache)
                        {
                            DrawMessageRow(m);
                        }
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        void DrawMessageRow(UCL_ChatMessage m)
        {
            // 區塊職責：Discord 風格訊息排版 — 左側 48×48 大頭像，右側兩行（header + body）
            // 物理意義：頭像從 UCL_ChatTavernIdentityAsset.m_AvatarSprite 反查；缺則灰色佔位
            // 數值影響：純顯示；GUI.DrawTextureWithTexCoords 用 Sprite rect/texture
            using (new GUILayout.HorizontalScope())
            {
                // ===== 左側 48×48 頭像 =====
                using (new GUILayout.VerticalScope(GUILayout.Width(52)))
                {
                    GUILayout.Space(2);
                    DrawAvatar(m.sender_id, 48);
                }

                // ===== 右側 header + body =====
                using (new GUILayout.VerticalScope())
                {
                    Color senderColor = m.kind switch
                    {
                        "join" => new Color(0.5f, 1f, 0.5f),
                        "leave" => new Color(1f, 0.7f, 0.5f),
                        "system" => Color.gray,
                        _ => new Color(0.85f, 0.95f, 1f),
                    };
                    string time = "??:??:??";
                    if (!string.IsNullOrEmpty(m.ts))
                    {
                        int t = m.ts.IndexOf('T');
                        if (t >= 0 && t + 9 <= m.ts.Length) time = m.ts.Substring(t + 1, 8);
                    }

                    // F1 — 改用 static cached GUIStyle，避免每筆每幀 new 3 個 GUIStyle
                    EnsureMessageStyles();

                    // header 行：sender name (粗) + time + seq + reply 按鈕
                    using (new GUILayout.HorizontalScope())
                    {
                        // textColor 隨 senderColor 變動 → mutate 共用 instance（IMGUI 同 frame 立即繪製，安全）
                        s_NameStyleBold.normal.textColor = senderColor;
                        GUILayout.Label(m.sender_name ?? m.sender_id ?? "?", s_NameStyleBold, GUILayout.ExpandWidth(false));
                        GUILayout.Label($"  {time}  · seq {m.seq}", s_MetaStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("↩", UCL_GUIStyle.ButtonStyle, GUILayout.Width(30)))
                            m_ReplyTo = m.seq;
                    }

                    // body 行：chat = wrap-only；非 chat (join/leave/system) = wrap + italic + senderColor
                    GUIStyle bodyStyle;
                    if (m.kind != "chat")
                    {
                        s_BodyStyleNonChat.normal.textColor = senderColor;
                        bodyStyle = s_BodyStyleNonChat;
                    }
                    else
                    {
                        bodyStyle = s_BodyStyleWrap;
                    }
                    GUILayout.Label(m.body ?? "", bodyStyle);


                    if (m.refs != null && m.refs.Count > 0)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Space(56);   // 對齊新的 48px avatar + 4px padding
                            foreach (var r in m.refs)
                            {
                                if (GUILayout.Button($"📎 {r.path}", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                {
                                    // 嘗試在 Project 視窗 ping 該 asset（路徑是相對 repo root，要轉成 Assets/...）
                                    TryPingAsset(r.path);
                                }
                            }
                        }
                    }
                    if (m.meta != null && m.meta.Count > 0)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Space(56);   // 對齊新的 48px avatar + 4px padding
                            // F1 — StringBuilder 共用 instance，Clear() 不釋放 capacity → 後續訊息 reuse buffer
                            if (s_MetaBuilder == null) s_MetaBuilder = new System.Text.StringBuilder(64);
                            s_MetaBuilder.Clear();
                            foreach (var kv in m.meta) s_MetaBuilder.Append("[").Append(kv.Key).Append("=").Append(kv.Value).Append("] ");
                            GUILayout.Label(s_MetaBuilder.ToString(), UCL_GUIStyle.LabelStyle);
                        }
                    }
                }
            }

        }

        // ===========================================================
        // 區塊：輸入區（送訊息）
        // ===========================================================
        void DrawInputBar()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_ReplyTo.HasValue)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"↩ 回覆 seq={m_ReplyTo.Value}", UCL_GUIStyle.LabelStyle, GUILayout.Width(120));
                        if (GUILayout.Button("取消", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) m_ReplyTo = null;
                    }
                }

                // 區塊職責：input bar 左側顯示當前發言者的頭像 + 名字提示「妳以 X 身分發言」
                // 物理意義：跟訊息列表頭像同模式 — 確認使用者要送出這則時知道 sender persona
                // 數值影響：純顯示；avatar cache 命中率高（同 sender 多次發言）
                using (new GUILayout.HorizontalScope())
                {
                    using (new GUILayout.VerticalScope(GUILayout.Width(52)))
                    {
                        GUILayout.Space(2);
                        DrawAvatar(m_SelectedIdentityId, 48);
                    }
                    using (new GUILayout.VerticalScope())
                    {
                        var (idntId, idntName, idntKind) = ResolveSelectedIdentity();
                        var hintStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { normal = { textColor = new Color(0.7f, 0.85f, 1f) } };
                        string asWho = string.IsNullOrEmpty(idntId)
                            ? "(尚未選擇身分)"
                            : $"以「{idntName}」({idntKind}) 身分發言";
                        GUILayout.Label(asWho, hintStyle);
                        m_Input = GUILayout.TextArea(m_Input ?? "", GUILayout.MinHeight(60));
                    }
                }
                m_MetaInput = LabeledTextField("meta (k=v;k=v)", m_MetaInput, 130);
                m_RefsInput = LabeledTextField("refs (path|path)", m_RefsInput, 130);
                using (new GUILayout.HorizontalScope())
                {
                    bool canSend = !string.IsNullOrEmpty(m_SelectedIdentityId) && !string.IsNullOrEmpty(m_Input);
                    GUI.enabled = canSend;
                    if (GUILayout.Button("Send", UCL_GUIStyle.GetButtonStyle(canSend ? Color.cyan : Color.gray), GUILayout.ExpandWidth(false)))
                    {
                        DoSend();
                    }
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Clear", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Input = m_MetaInput = m_RefsInput = "";
                        m_ReplyTo = null;
                    }
                }
            }
        }

        // ===========================================================
        // 動作
        // ===========================================================
        // 區塊職責：解析 selected identity → 取 sender_id + display_name
        // 物理意義：dropdown 選的是 UCL_ChatTavernIdentityAsset 的 ID；display_name 從 roster (identities.json) 查
        //          缺 roster 條目（純粹 Asset 沒對應 roster）→ display_name fallback 用 ID 本身
        // 數值影響：純讀取，回傳 (id, displayName, kind) tuple；id 為空表示沒選
        (string id, string displayName, string kind) ResolveSelectedIdentity()
        {
            string id = m_SelectedIdentityId;
            if (string.IsNullOrEmpty(id)) return (null, null, null);
            var ros = m_RosterCache?.identities?.Find(x => x != null && x.id == id);
            string display = ros != null && !string.IsNullOrEmpty(ros.display_name) ? ros.display_name : id;
            string kind = ros != null && !string.IsNullOrEmpty(ros.kind) ? ros.kind : "agent";
            return (id, display, kind);
        }

        void DoSend()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) { Debug.LogError("身分不存在"); return; }
            var room = UCL_ChatTavernIO.GetRoom(m_SelectedRoomId);
            if (room == null) { Debug.LogError("房間不存在"); return; }

            var msg = new UCL_ChatMessage
            {
                sender_id = idntId,
                sender_name = idntName,
                kind = "chat",
                body = m_Input,
                reply_to = m_ReplyTo,
                meta = ParseMetaSimple(m_MetaInput),
                refs = ParseRefsSimple(m_RefsInput),
            };
            int seq = UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, msg);
            UCL_ChatTavernRender.WriteLastView(m_SelectedRoomId, room.name,
                UCL_ChatTavernIO.Tail(m_SelectedRoomId, 100), seq,
                $"> 你 ({idntName}) 剛 post：seq={seq}");
            m_Input = "";
            m_ReplyTo = null;
            RefreshMessages();
        }

        void DoJoin()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) return;
            UCL_ChatTavernIO.AddMember(m_SelectedRoomId, idntId);
            UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = idntId, sender_name = idntName, kind = "join",
                body = $"{idntName} 進入了酒館",
            });
            RefreshMessages();
            RefreshMembers();
        }

        void DoLeave()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) return;
            UCL_ChatTavernIO.RemoveMember(m_SelectedRoomId, idntId);
            UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = idntId, sender_name = idntName, kind = "leave",
                body = $"{idntName} 離開了酒館",
            });
            RefreshMessages();
            RefreshMembers();
        }

        // ===========================================================
        // helper
        // ===========================================================
        void RefreshAll()
        {
            RefreshRooms();
            RefreshIdentities();
            // 清 avatar cache — 別人改了 character mapping 或 identity asset sprite 後，不必重啟頁就生效
            m_AvatarCache.Clear();
            UCL_ChatTavernCharacterMapping.InvalidateCache();
            if (!string.IsNullOrEmpty(m_SelectedRoomId))
            {
                RefreshMessages();
                RefreshMembers();
            }
        }
        // 區塊職責：房間清單來源 — filesystem-as-truth（rooms/<id>/ 子目錄列舉）
        // 物理意義：以 rooms/ 目錄結構為 single source-of-truth；metadata 跟資料同地（rooms/<id>/meta.json）
        //          → 無漂移風險。Asset 不再是 dropdown source，純粹是 Inspector view layer（rich data）。
        // 數值影響：m_RoomIds 來自 IO.EnumerateRoomIds()；m_RoomsCache 從各房間的 meta.json 組合而成
        void RefreshRooms()
        {
            try
            {
                m_RoomIds = UCL_ChatTavernIO.EnumerateRoomIds();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UCL_ChatTavernPage] RefreshRooms EnumerateRoomIds 失敗：{ex.Message}");
                m_RoomIds = new List<string>();
            }
            m_RoomsCache = UCL_ChatTavernIO.LoadRooms();
        }
        // 區塊職責：身分清單來源從 identities.json 改為 UCL_ChatTavernIdentityAsset
        // 物理意義：以 UCL_Asset 為 single source-of-truth — dropdown 列出所有已建立的角色卡 ID；
        //          identities.json 仍保留作為 Cmd_Tavern Op_Post 寫訊息時的 display_name lookup
        // 數值影響：m_IdentityIds 來自 GetAllIDs(false) 遍歷模組路徑；m_RosterCache 仍從 identities.json 讀
        void RefreshIdentities()
        {
            // GetAllIDs 透過 UCL_ModuleService 遍歷當前 edit module 內 UCL_Assets/UCL_ChatTavernIdentityAsset/ 下的 .json 檔
            try
            {
                m_IdentityIds = new UCL_ChatTavernIdentityAsset().GetAllIDs(false) ?? new List<string>();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UCL_ChatTavernPage] RefreshIdentities GetAllIDs 失敗：{ex.Message}");
                m_IdentityIds = new List<string>();
            }
            m_IdentityIds.Sort(System.StringComparer.OrdinalIgnoreCase);
            m_RosterCache = UCL_ChatTavernIO.LoadIdentities();
        }
        // 區塊職責：重抓訊息 + 自動觸發 scroll-to-bottom 的單一進入點
        // 物理意義：兩種情境會把 m_PendingScrollToBottom 設 true：
        //   1) 切換房間（m_LastSeenRoomId 變了）— 進新房就在最底
        //   2) 同房內 count 增加 — 新訊息進來自動跟上
        //   兩者都不會在使用者滾上去看歷史時搶捲（count 持平 / 同房 → 不觸發）
        // 數值影響：每次 RefreshMessages 同步 m_LastSeenRoomId / m_LastSeenMsgCount
        void RefreshMessages()
        {
            m_MessagesCache = UCL_ChatTavernIO.Tail(m_SelectedRoomId, 100);
            int now = m_MessagesCache?.Count ?? 0;
            bool roomChanged = m_LastSeenRoomId != m_SelectedRoomId;
            bool countGrew = !roomChanged && now > m_LastSeenMsgCount;
            if (roomChanged || countGrew)
            {
                m_PendingScrollToBottom = true;
            }
            m_LastSeenRoomId = m_SelectedRoomId;
            m_LastSeenMsgCount = now;
        }
        void RefreshMembers() { m_MembersCache = UCL_ChatTavernIO.LoadMembers(m_SelectedRoomId); }

        // 區塊職責：頁面首次 ContentOnGUI 時的自動初始化
        // 物理意義：UX 起手式 — 第一次打開酒館頁就有可用房間，使用者不必先去建房；
        //          沒選中房間 → 自動選第一間，省一次點擊
        // 數值影響：
        //   1) 沒任何房間 → 自動建一間 id=`default` / name="Default 酒館" 並 RefreshRooms
        //   2) 沒選中房間 + 有可選 → 設 m_SelectedRoomId = 第一間 + 載 messages / members
        //   3) m_AutoInitDone 確保整個流程在頁面生命週期內只跑一次（避免人為清空後又被搶選）
        void EnsureAutoInit()
        {
            if (m_AutoInitDone) return;
            m_AutoInitDone = true;

            // (1) 沒任何房間 → 建一間 default（雙寫 roster + Asset shell）
            if (m_RoomIds == null || m_RoomIds.Count == 0)
            {
                UCL_ChatTavernIO.CreateRoom("default", "Default 酒館", "首次開啟自動建立的預設房間");
                try
                {
                    var asset = new UCL_ChatTavernRoomAsset("default");
                    if (!asset.ContainsAsset("default")) asset.Save();
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[UCL_ChatTavernPage] EnsureAutoInit 建 default RoomAsset 失敗：{ex.Message}");
                }
                RefreshRooms();
            }

            // (2) 沒選中 → 選第一間
            if (string.IsNullOrEmpty(m_SelectedRoomId) && m_RoomIds != null && m_RoomIds.Count > 0)
            {
                m_SelectedRoomId = m_RoomIds[0];
                RefreshMessages();
                RefreshMembers();
            }
        }

        // 區塊職責：Auto-Poll 主迴圈 — 每 PollIntervalSec 重抓 rooms / messages / members
        // 物理意義：別 agent 透過 Cmd_Tavern 建房 / 改 identities 時，本 Page 不知情；
        //          靠 polling 偵測 jsonl mtime 變動。三項都 cheap（< 1ms IO）。
        // 數值影響：rooms 永遠 poll（讓使用者能看到別人剛建的房）；
        //          messages / members 只在已選中房間才 poll（沒選中沒意義）。
        void HandleAutoPoll()
        {
            if (!m_AutoPoll) return;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - m_LastPollTime < PollIntervalSec) return;
            m_LastPollTime = now;

            RefreshRooms();
            if (!string.IsNullOrEmpty(m_SelectedRoomId))
            {
                RefreshMessages();
                RefreshMembers();
            }
        }

        static string LabeledTextField(string label, string value, float labelWidth)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label, UCL_GUIStyle.LabelStyle, GUILayout.Width(labelWidth));
                return GUILayout.TextField(value ?? "");
            }
        }

        static Dictionary<string, string> ParseMetaSimple(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var d = new Dictionary<string, string>();
            foreach (var pair in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                int idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                d[pair.Substring(0, idx).Trim()] = pair.Substring(idx + 1).Trim();
            }
            return d.Count > 0 ? d : null;
        }
        static List<UCL_ChatRef> ParseRefsSimple(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<UCL_ChatRef>();
            foreach (var p in raw.Split('|'))
            {
                string path = p.Trim();
                if (!string.IsNullOrEmpty(path)) list.Add(new UCL_ChatRef { path = path });
            }
            return list.Count > 0 ? list : null;
        }

        static void TryPingAsset(string repoRelativePath)
        {
            // 區塊職責：把 repo 相對路徑轉成 Assets/ 路徑後 ping 出來
            // 物理意義：refs 紀錄為 repo root 相對；Unity AssetDatabase 只認 Assets/ 路徑
            // 數值影響：純讀；找不到 asset 印 warning 但不報錯
            const string assetsPrefix = "CardGame/Assets/";
            if (repoRelativePath.StartsWith(assetsPrefix))
            {
                string assetPath = "Assets/" + repoRelativePath.Substring(assetsPrefix.Length);
                var obj = UnityEditor.AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj != null)
                {
                    UnityEditor.EditorGUIUtility.PingObject(obj);
                    return;
                }
            }
            Debug.LogWarning($"[ChatTavern] 無法 ping asset：{repoRelativePath}");
        }

        // F1 helper — lazy 建 static GUIStyle cache（首次 DrawMessageRow 觸發；之後共用）
        // 物理意義：UCL_GUIStyle.LabelStyle 也是 lazy-init，所以 EnsureMessageStyles 不能在 ctor / OnEnable 跑（會拿到 null）；放 OnGUI path 第一次跑安全
        // 數值影響：4 個 GUIStyle + 1 個 StringBuilder 一次性配置，後續零 alloc
        static void EnsureMessageStyles()
        {
            if (s_MetaStyle != null) return;
            s_MetaStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
            s_NameStyleBold = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
            s_BodyStyleWrap = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
            s_BodyStyleNonChat = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, fontStyle = FontStyle.Italic };
        }

        // 區塊職責：在 Editor 內以 UCL_MarkdownViewerPage 開啟 .md 檔
        // 物理意義：取代 EditorUtility.OpenWithDefaultApp — 不離開 Unity 視窗，而且能走多語 fallback /
        //          related 連結 / 內嵌 mermaid 等 viewer 既有功能
        // 數值影響：純 push 一頁；relative 路徑只給 viewer 顯示用，內容讀絕對路徑
        // 邊界：檔案不存在 → 印 warning 不開頁；GitRoot 算不出來退而求其次傳 abs 當 rel
        static void OpenInMarkdownViewer(string absPath)
        {
            if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
            {
                Debug.LogWarning($"[ChatTavern] 檔案不存在，無法開啟 viewer：{absPath}");
                return;
            }
            string gitRoot = UCL.Core.EditorLib.AgentCommands.UCL_DocCatalogScanner.GetGitRoot();
            string rel = !string.IsNullOrEmpty(gitRoot)
                ? Path.GetRelativePath(gitRoot, absPath).Replace('\\', '/')
                : absPath;
            UCL_MarkdownViewerPage.Create(rel, absPath);
        }
    }
}
#endif
