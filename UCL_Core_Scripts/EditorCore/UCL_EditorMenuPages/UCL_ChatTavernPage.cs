// UCL Chat Tavern — IMGUI 頁面（prototype v1）
// 職責：人類可在 Editor 內直接以某個 identity 加入房間 / 看訊息 / 發言。
// 物理意義：跟 agent 走的 Cmd 接同一份檔案 → 人 vs agent 同一個聊天室。
// prototype 取捨：UI 字串先硬編；refs/meta 表單最簡（單行 paths + key=val）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.LocalizeLib;
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
        public override string WindowName => UCL_CodeLocalize.Get("Tavern.Title");

        // opt-in 進 UCL_EditorMenuPage 的 Page 選擇器下拉 — 
        // 物理意義：已提升為 EditorMenu 外部主要按鈕，此處關閉以避免下拉選單重複出現
        public override bool ShowInPageMenu => false;

        public static UCL_ChatTavernPage Create() => UCL_EditorPage.Create<UCL_ChatTavernPage>();

        // ===== 選擇狀態 =====
        // 區塊職責：使用者目前選中的房間 / 身分 + 暫存的輸入框內容
        // 物理意義：純 UI 狀態；按下 Send 才會落檔
        string SelectedRoomId
        {
            get => PlayerPrefs.GetString(nameof(SelectedRoomId));
            set => PlayerPrefs.SetString(nameof(SelectedRoomId), value);
        }
        string SelectedIdentityId
        {
            get => PlayerPrefs.GetString(nameof(SelectedIdentityId));
            set => PlayerPrefs.SetString(nameof(SelectedIdentityId), value);
        }
        string m_Input = "";
        // 區塊職責: meta/refs 預設值 — 進入頁面就帶可運作預設, Tim 2026-05-12 拍板
        // 物理意義:
        //   - meta `tag:human-post` 標記訊息來自 IMGUI 頁面 (vs agent 走 run_cmd.py)
        //   - meta `alter-pacing-bypass:true` 跳過 alter-pair 300s 自動延遲 — 人類發言不該被 throttle
        //   - refs 空, 但 label 提示格式 (path|path) 引導使用者填入
        // 數值影響: 使用者可手動編輯 / 清空; 跟 agent post 走完全一樣 Cmd_Tavern.Op_Post 路徑
        string m_MetaInput = "tag:human-post;alter-pacing-bypass:true";
        string m_RefsInput = "";        // "path1|path2"
        int? m_ReplyTo = null;
        // 註：原「自動捲到底」旗標(m_PendingScrollToBottom) + 訊息數快照(m_LastSeenMsgCount) 已隨
        //     內層 scroll 移除 + 分頁改制一併移除（分頁後最新頁預設即最新 PageSize 筆，不需捲底/count 偵測）。
        string m_LastSeenRoomId = ""; // 跨房間切換偵測用：room id 變了 → 強制捲底

        // 區塊職責：訊息分頁 — 每頁固定 MessagePageSize 筆，避免一次繪製上百列造成 RequiresConstantRepaint 每幀 O(N) 卡頓（Tim 2026-07-20 拍板）
        // 物理意義：m_MessagePage=0 為「最新頁」，數字越大越舊；RefreshMessages 用 CountMessages 算總數 + clamp 頁碼，
        //          再 Tail((page+1)*PageSize) 取涵蓋當前頁的最少訊息、切出本頁那 PageSize 筆。
        // 數值影響：每幀只繪 ≤ PageSize 列（10），render 成本從「全 cache」降到固定 10 列。
        const int MessagePageSize = 10;
        int m_MessagePage = 0;              // 0 = 最新頁
        int m_TotalMessageCount = 0;        // 房間訊息總數快取（由 Tail 最新一筆的 seq 推得，零額外列舉）— 供分頁計算
        // button click 在 MouseUp 直接 mutate 頁碼 + 重抓會改 layout 結構 → 延後到下個 Layout event 套用（對齊既有 deferred-apply 模式）
        int? m_PendingSetPage = null;
        string m_JumpPageInput = "";        // 「跳轉到指定頁」輸入框暫存（UI 為 1-based 頁碼）

        // ===== 新建表單 =====
        bool m_ShowCreateRoom = false;
        bool m_ShowCharMapping = false;
        // 區塊職責：上方控制區塊（房間/身分/角色 Mapping/發言區）折疊旗標（Tim 2026-07-20 拍板）
        // 物理意義：折疊後把垂直空間全讓給下方訊息列表 — 訊息已改不自帶 scroll，靠外層頁面 scroll
        // 數值影響：true = 展開繪製四個子區塊；false = 只留 toggle 列，其餘收起
        bool m_ShowTopPanel = true;
        // ===== Deferred-apply（避免 button click 在 MouseUp 改 layout-affecting state，造成 Layout vs Repaint 結構不一致）=====
        // 物理意義：button 在 MouseUp event return true，若直接 mutate 會讓同一 frame 的 Repaint event 看到不同 state；
        //          延後到下個 frame 的 Layout event 才套用，可保證單一 frame 內 layout 結構恆定。
        bool? m_PendingShowCreateRoom = null;
        bool? m_PendingShowCreateIdentity = null;
        bool? m_PendingShowTopPanel = null;   // 上方控制區塊折疊 deferred-apply（避免 button click 在 MouseUp 改 layout 結構 → Mismatched LayoutGroup）
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
        // 跟 Python 端常數對齊（run_cmd.py BARTENDER_TRIGGER_SEC default=450；cooldown 90s；rest hint 3 杯）— 純顯示用，不影響觸發邏輯
        // 物理意義：IMGUI 端顯示酒保倒數要跟 Python 真實觸發時間吻合，否則使用者看到「10s」但實際 450s 才會 fire 會困惑
        // 數值影響：450s ≈ 7.5 min；慢速模式 wait=480s 內不會被酒保打斷（per SKILL.md 設計）
        // 想覆蓋：建議 Python 端設 UCL_BARTENDER_TRIGGER_SEC env var；本常數要動就要兩邊一起改
        const double BartenderTriggerSec = 450.0;
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
        static System.Text.StringBuilder s_MetaBuilder;  // DrawMessageRow meta拼接 reuse；Clear() 不釋放 capacity

        // ===== Treasury Balance Cache =====
        // 物理意義：每幀讀取 Treasury JSON 太重，快取目前選中身分的 Token 餘額。
        // 數值影響：供 DrawIdentityPicker 顯示用，不影響底層 Ledger。
        int m_CachedTokenBalance = 0;
        string m_CachedBalanceIdentityId = null;
        double m_LastBalanceRefreshTime = 0;
        const double BalanceRefreshIntervalSec = 2.0;

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                RefreshAll();
            }
            m_AutoPoll = GUILayout.Toggle(m_AutoPoll, UCL_CodeLocalize.Get("Tavern.Btn.AutoPoll"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
            if (GUILayout.Button(UCL_CodeLocalize.Get("Open Folder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
            string btnLabel = m_HandshakeActive ? UCL_CodeLocalize.Get("Tavern.Btn.CancelHandshake") : UCL_CodeLocalize.Get("Tavern.Btn.CancelHandshakeNone");
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
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Bartender.Asleep"), UCL_GUIStyle.LabelStyle);
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
                        statusText = string.Format(UCL_CodeLocalize.Get("Tavern.Bartender.FirstDrinkFmt"), countdown);
                    }
                    else
                    {
                        // 已過 trigger 門檻，算 cooldown
                        double cooldownLeft = m_BartenderLastDrinkUnix > 0
                            ? BartenderCooldownSec - (nowUnix - m_BartenderLastDrinkUnix)
                            : 0;
                        if (cooldownLeft <= 0)
                        {
                            statusText = UCL_CodeLocalize.Get("Tavern.Bartender.Ready");
                        }
                        else
                        {
                            countdown = cooldownLeft;
                            statusText = string.Format(UCL_CodeLocalize.Get("Tavern.Bartender.NextDrinkFmt"), countdown);
                        }
                    }
                }
                else
                {
                    statusText = UCL_CodeLocalize.Get("Tavern.Bartender.ActiveNoStart");
                }

                GUILayout.Label(statusText, UCL_GUIStyle.LabelStyle);

                // 連喝計數
                if (m_BartenderConsecutiveDrinks > 0)
                {
                    string drinkLabel = string.Format(UCL_CodeLocalize.Get("Tavern.Bartender.DrunkFmt"), m_BartenderConsecutiveDrinks);
                    if (m_BartenderConsecutiveDrinks >= BartenderRestHintDrinks)
                        drinkLabel += UCL_CodeLocalize.Get("Tavern.Bartender.RestHintSuffix");
                    var style = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    if (m_BartenderConsecutiveDrinks >= BartenderRestHintDrinks)
                        style.normal.textColor = new Color(1f, 0.5f, 0.3f);
                    GUILayout.Label(drinkLabel, style);
                }

                GUILayout.FlexibleSpace();

                // 催促按鈕 — 寫 hurry flag，Python 偵測後 wait_start / cooldown 各 -30s
                using (new EditorGUI.DisabledScope(countdown <= 0))
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Btn.HurryBartender"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.3f)), GUILayout.ExpandWidth(false)))
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
        // T28 (Tim 2026-05-14 拍板): 加 avatarSpriteId 參數 — message.sender_avatar_sprite 帶 sprite_id 時優先直 load,
        //                            否則 fallback 走 sender_id-based identity lookup (legacy compat).
        void DrawAvatar(string senderId, float size, string avatarSpriteId = null)
        {
            Sprite avatar = null;
            if (!string.IsNullOrEmpty(avatarSpriteId))
            {
                avatar = GetAvatarSpriteById(avatarSpriteId);
            }
            if (avatar == null)
            {
                avatar = GetAvatarSprite(senderId);  // legacy fallback
            }
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

        // 區塊職責：直接用 UCL_SpriteAsset ID lookup Sprite (T28, persona-level avatar 直 hit)
        // 物理意義：message.sender_avatar_sprite 已是 sprite_id (e.g. "Avatars_basecamp"), Cmd_Tavern Op_Post 已 resolve;
        //          render 端不必再走 identity → sprite reverse lookup, 直接 by id load
        // 數值影響：跟 m_AvatarCache 共用 cache (key 是 "spriteid:" + id 避免跟 sender_id-key 撞)
        Sprite GetAvatarSpriteById(string spriteId)
        {
            if (string.IsNullOrEmpty(spriteId)) return null;
            string cacheKey = "spriteid:" + spriteId;
            if (m_AvatarCache.TryGetValue(cacheKey, out var cached)) return cached;
            Sprite sp = null;
            try
            {
                var spriteAsset = new UCL_SpriteAsset();
                if (spriteAsset.ContainsAsset(spriteId))
                {
                    var data = spriteAsset.GetData(spriteId);
                    sp = data?.Sprite;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UCL_ChatTavernPage] GetAvatarSpriteById({spriteId}) 失敗：{ex.Message}");
            }
            m_AvatarCache[cacheKey] = sp;
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
                    if (m_PendingShowTopPanel.HasValue) { m_ShowTopPanel = m_PendingShowTopPanel.Value; m_PendingShowTopPanel = null; }
                    // 翻頁：在 Layout event 套用頁碼 + 重抓，確保本幀 Layout/Repaint 看到一致的訊息結構
                    if (m_PendingSetPage.HasValue)
                    {
                        m_MessagePage = m_PendingSetPage.Value;
                        m_PendingSetPage = null;
                        RefreshMessages();
                    }
                }

                if (m_RoomsCache == null || m_RoomIds == null) RefreshRooms();
                if (m_IdentityIds == null) RefreshIdentities();
                EnsureAutoInit();
                HandleAutoPoll();

                // Perf overlay 區塊：toggle bar 永遠顯示（小體積）；overlay 詳細表只在 ShowOverlay=true 時開 //加上BartenderInfoBar
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
                    

                    using (UCL_ChatTavernPerfOverlay.Sample("DrawBartenderInfoBar")) DrawBartenderInfoBar();
                    GUILayout.FlexibleSpace();
                }
                UCL_ChatTavernPerfOverlay.DrawOverlay();

                
                GUILayout.Space(4);
                // ===== 上方控制區塊（可 Toggle 折疊）— Tim 2026-07-20 拍板 =====
                // 區塊職責：房間/身分/角色 Mapping/發言區 收進一個可折疊的上方區塊；發言區移到角色 Mapping 下。
                // 物理意義：折疊後把垂直空間讓給下方訊息列表（訊息已改不自帶 scroll，靠外層頁面 scroll，避免雙 scroll）。
                // 數值影響：toggle 用 deferred-apply（m_PendingShowTopPanel）避免 button click 中途改 layout 結構 → Mismatched LayoutGroup。
                using (new GUILayout.HorizontalScope("box"))
                {
                    bool newShowTop = UCL_GUILayout.Toggle(m_ShowTopPanel);
                    if (newShowTop != m_ShowTopPanel) m_PendingShowTopPanel = newShowTop;
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.TopPanel.Title"), UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (m_ShowTopPanel)
                {
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawRoomPicker")) DrawRoomPicker();
                    GUILayout.Space(4);
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawIdentityPicker")) DrawIdentityPicker();
                    GUILayout.Space(4);
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawCharacterMappingFoldout")) DrawCharacterMappingFoldout();
                    GUILayout.Space(4);
                    // 發言區域移到角色 Mapping 下（Tim 2026-07-20 拍板）
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawInputBar")) DrawInputBar();
                }
                GUILayout.Space(8);

                if (string.IsNullOrEmpty(SelectedRoomId))
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label(UCL_CodeLocalize.Get("Tavern.SelectRoomFirst"), UCL_GUIStyle.LabelStyle);
                    }
                    return;
                }
                using (UCL_ChatTavernPerfOverlay.Sample("DrawQuestPanel")) DrawQuestPanel();
                GUILayout.Space(4);
                // 下方訊息區 — 不再自帶 scroll scope（外層頁面已有 scroll，雙 scroll 難操作）
                using (UCL_ChatTavernPerfOverlay.Sample("DrawMessagesView")) DrawMessagesView();
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
            string roomDir = UCL_ChatTavernIO.GetRoomDir(SelectedRoomId);
            string eventsPath = Path.Combine(roomDir, "events.jsonl");
            if (!File.Exists(eventsPath))
            {
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Quest.NoEvents"),
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
                    m_QuestStatesCache = UCL_ChatTavernQuestIO.ComputeTaskStates(SelectedRoomId);
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
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Quest.FilterStatus"), UCL_GUIStyle.LabelStyle, GUILayout.Width(90));
                    string[] statusOpts = { "all", "ready", "claimed", "in_progress", "review", "done", "pending", "stale" };
                    int curIdx = System.Array.IndexOf(statusOpts, m_QuestStatusFilter);
                    if (curIdx < 0) curIdx = 0;
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, statusOpts, m_PickerDic, "QuestStatusFilter", GUILayout.Width(120));
                    if (newIdx != curIdx) m_QuestStatusFilter = statusOpts[newIdx];
                    GUILayout.Space(8);
                    bool by = GUILayout.Toggle(m_QuestFilterByMe, UCL_CodeLocalize.Get("Tavern.Quest.OnlyMine"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (by != m_QuestFilterByMe) m_QuestFilterByMe = by;
                    GUILayout.FlexibleSpace();
                }

                // task table（scroll）
                m_QuestScroll = GUILayout.BeginScrollView(m_QuestScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
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
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Quest.NoTasks"), UCL_GUIStyle.LabelStyle);
                }
                GUILayout.EndScrollView();
            }
        }

        bool QuestPassesFilter(UCL_QuestTaskState st)
        {
            // 我的 filter
            if (m_QuestFilterByMe && st.owner != SelectedIdentityId) return false;
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
                    GUILayout.Label(string.IsNullOrEmpty(st.title) ? UCL_CodeLocalize.Get("Tavern.Quest.NoTitle") : st.title,
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
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Quest.DependsOnFmt"), string.Join(", ", st.depends_on)), UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Quest.CreatedFmt"), st.created_at, st.age_days, st.reject_count),
                    UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(st.lease_until))
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Quest.LeaseUntilFmt"), st.lease_until) + (st.is_stale ? UCL_CodeLocalize.Get("Tavern.Quest.StaleSuffix") : ""), UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(st.last_progress_summary))
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Quest.LastProgressFmt"), st.last_progress_summary), UCL_GUIStyle.LabelStyle);

                // spec 預覽 — 走內嵌 UCL_MarkdownViewerPage（不離開 Unity 視窗）
                string specPath = UCL_ChatTavernQuestIO.GetTaskSpecPath(SelectedRoomId, st.id);
                if (File.Exists(specPath))
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Quest.OpenSpec"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        OpenInMarkdownViewer(specPath);
                    }
                }

                // timeline
                GUILayout.Space(2);
                GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Quest.LifecycleTitle"), UCL_GUIStyle.LabelStyle);
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
                    return UCL_CodeLocalize.Get("Tavern.Quest.Hint.Blocked");
                case "claimed":
                case "in_progress":
                    return $"task_progress / task_review_request / task_done task_id={s.id}";
                case "review":
                    return UCL_CodeLocalize.Get("Tavern.Quest.Hint.Review");
                case "done":
                    return UCL_CodeLocalize.Get("Tavern.Quest.Hint.Done");
                default: return "";
            }
        }

        void DrawMyInboxHint()
        {
            if (string.IsNullOrEmpty(SelectedIdentityId)) return;
            string inboxPath = UCL_ChatTavernQuestIO.GetInboxPath(SelectedRoomId, SelectedIdentityId);
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
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Inbox.HintFmt"), SelectedIdentityId, entries), UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Inbox.OpenBtn"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
                GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Field.Room"), UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_RoomIds == null || m_RoomIds.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Room.Empty"), UCL_GUIStyle.LabelStyle);
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
                        if (id == SelectedRoomId) curIdx = i;
                    }
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, labels, m_PickerDic, "RoomPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(420)));
                    if (newIdx != curIdx && newIdx >= 0 && newIdx < m_RoomIds.Count)
                    {
                        SelectedRoomId = m_RoomIds[newIdx];
                        RefreshMessages();
                        RefreshMembers();
                    }
                }
                if (GUILayout.Button(m_ShowCreateRoom ? UCL_CodeLocalize.Get("Tavern.Btn.CancelCreate") : UCL_CodeLocalize.Get("Tavern.Btn.NewRoom"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_PendingShowCreateRoom = !m_ShowCreateRoom;
                }

                // 加入 / 離開房間按鈕 — 必須無條件開啟 inner scope，並維持穩定控制項數。
                // 條件式 (if !empty(roomId)&&!empty(identityId)) 開 scope 會在 popup 改 m_SelectedRoomId
                // 後讓 Layout / Repaint 看到不同的 layout 結構，Unity 2021 IMGUI 拋 ExitGUIException: Mismatched LayoutGroup。
                using (new GUILayout.HorizontalScope())
                {
                    bool canAct = !string.IsNullOrEmpty(SelectedRoomId) && !string.IsNullOrEmpty(SelectedIdentityId);
                    bool isMember = canAct && m_MembersCache != null && m_MembersCache.member_ids.Contains(SelectedIdentityId);

                    string btnText = !canAct ? UCL_CodeLocalize.Get("Tavern.Room.NeedSelect")
                                   : isMember ? string.Format(UCL_CodeLocalize.Get("Tavern.Btn.LeaveRoomFmt"), SelectedRoomId)
                                              : string.Format(UCL_CodeLocalize.Get("Tavern.Btn.JoinRoomFmt"), SelectedRoomId);
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
                        ? string.Format(UCL_CodeLocalize.Get("Tavern.Room.MemberCountFmt"), m_MembersCache.member_ids.Count)
                        : UCL_CodeLocalize.Get("Tavern.Room.NotJoined");
                    var memberContent = new GUIContent(memberText, UCL_CodeLocalize.Get("Tavern.Room.MemberTooltip"));
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
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Btn.Create"), UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewRoomId))
                        {
                            Debug.LogError(UCL_CodeLocalize.Get("Tavern.Err.RoomIdEmpty"));
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
                            SelectedRoomId = m_NewRoomId;
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
                GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Field.Identity"), UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_IdentityIds == null || m_IdentityIds.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Identity.Empty"), UCL_GUIStyle.LabelStyle);
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
                        if (id == SelectedIdentityId) curIdx = i;
                    }
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, labels, m_PickerDic, "IdentityPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(420)));
                    if (newIdx >= 0 && newIdx < m_IdentityIds.Count) SelectedIdentityId = m_IdentityIds[newIdx];
                }
                if (GUILayout.Button(m_ShowCreateIdentity ? UCL_CodeLocalize.Get("Tavern.Btn.CancelCreate") : UCL_CodeLocalize.Get("Tavern.Btn.NewIdentity"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_PendingShowCreateIdentity = !m_ShowCreateIdentity;
                }

                // 區塊職責：顯示當前角色的 Token 餘額
                // 物理意義：讓使用者在發言/切換身分時能即時知曉國庫水位，無縫對接跨維度經濟
                // 數值影響：純讀取，資料來自 UpdateTokenBalance() 節流快取
                UpdateTokenBalance();
                string tokenDisplay = string.IsNullOrEmpty(SelectedIdentityId) ? "---" : m_CachedTokenBalance.ToString();
                GUILayout.Space(10);
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Balance.Fmt"), tokenDisplay),
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));

                GUILayout.FlexibleSpace();

            }
            if (m_ShowCreateIdentity)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    // 區塊職責：身分命名約定 hint — 提醒使用者本系統是 agent-neutral
                    // 物理意義：避免有人以為這只給 Claude 用 → 列出三家 agent 的範例 id
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Identity.NamingHint1"),
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Identity.NamingHint2"),
                        UCL_GUIStyle.LabelStyle);
                    m_NewIdentityId = LabeledTextField("id", m_NewIdentityId, 60);
                    m_NewIdentityName = LabeledTextField("name", m_NewIdentityName, 60);
                    m_NewIdentityKind = LabeledTextField("kind", m_NewIdentityKind, 60);
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Btn.Create"), UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewIdentityId))
                        {
                            Debug.LogError(UCL_CodeLocalize.Get("Tavern.Err.IdentityIdEmpty"));
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
                            SelectedIdentityId = m_NewIdentityId;
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
                    string label = m_ShowCharMapping ? UCL_CodeLocalize.Get("Tavern.CharMapping.TitleOpen") : UCL_CodeLocalize.Get("Tavern.CharMapping.TitleClosed");
                    if (GUILayout.Button(label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_ShowCharMapping = !m_ShowCharMapping;
                    }
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.CharMapping.Subtitle"), UCL_GUIStyle.LabelStyle);
                }

                if (!m_ShowCharMapping) return;

                var data = UCL_ChatTavernCharacterMapping.Load();
                bool dirty = false;

                // default
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.CharMapping.Default"), UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    string newDefault = GUILayout.TextField(data.default_identity_id ?? "", GUILayout.MinWidth(220));
                    if (newDefault != (data.default_identity_id ?? ""))
                    {
                        data.default_identity_id = newDefault;
                        dirty = true;
                    }
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.CharMapping.DefaultHint"), UCL_GUIStyle.LabelStyle);
                }

                GUILayout.Space(4);
                GUILayout.Label(UCL_CodeLocalize.Get("Tavern.CharMapping.AliasesLabel"), UCL_GUIStyle.LabelStyle);

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
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.CharMapping.NewAlias"), UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    m_NewAliasFrom = GUILayout.TextField(m_NewAliasFrom ?? "", GUILayout.MinWidth(180));
                    GUILayout.Label("→", UCL_GUIStyle.LabelStyle, GUILayout.Width(20));
                    m_NewAliasTo = GUILayout.TextField(m_NewAliasTo ?? "", GUILayout.MinWidth(180));
                    bool canAdd = !string.IsNullOrWhiteSpace(m_NewAliasFrom) && !string.IsNullOrWhiteSpace(m_NewAliasTo);
                    using (new EditorGUI.DisabledScope(!canAdd))
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Add"), UCL_GUIStyle.GetButtonStyle(canAdd ? Color.green : Color.gray), GUILayout.Width(60)))
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
                if (m_CachedSeqRoom != SelectedRoomId || nowSeq - m_LastSeqRefreshTime > SeqRefreshIntervalSec)
                {
                    m_CachedSeq = UCL_ChatTavernIO.ReadCurrentSeq(SelectedRoomId);
                    m_CachedSeqRoom = SelectedRoomId;
                    m_LastSeqRefreshTime = nowSeq;
                }
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Messages.HeaderFmt"), SelectedRoomId, m_CachedSeq), UCL_GUIStyle.LabelStyle);
                // T-Layout (Tim 2026-07-20)：訊息區不再自帶 scroll scope — 外層頁面 (UCL_CommonEditorPage) 已有 scroll，
                // 雙 scroll 難操作。訊息直接 inline 繪製，溢出由外層 scroll 承載。分頁後每頁 ≤ PageSize 列，高度可控。

                // 分頁控制列（Tim 2026-07-20）— 每頁 MessagePageSize 筆，翻頁看更舊
                DrawMessagePagerBar();

                if (m_MessagesCache == null || m_MessagesCache.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Messages.Empty"), UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    // Sample name 必須 stable（dict key）；每頁最多 MessagePageSize 列 → 每幀 render 成本固定
                    using (UCL_ChatTavernPerfOverlay.Sample("DrawMessagesView/rows"))
                    {
                        // 反向渲染：m_MessagesCache 是 oldest-first (paging 的 GetRange 依賴此序不動)，
                        // 但顯示要「最新在最上面」(Tim 2026-07-27) → 倒著繪，最新 seq 在頂端。
                        for (int i = m_MessagesCache.Count - 1; i >= 0; i--)
                        {
                            DrawMessageRow(m_MessagesCache[i]);
                        }
                    }
                }
            }
        }

        // 區塊職責：訊息分頁控制列 — 最新 / 較新 / 頁碼指示 / 較舊（Tim 2026-07-20 拍板）
        // 物理意義：m_MessagePage=0 為最新頁；按鈕只設 m_PendingSetPage（deferred），真正翻頁 + 重抓延後到 Layout event，
        //          避免 button click 在 MouseUp 改 layout 結構造成本幀 Layout/Repaint 不一致。
        // 數值影響：maxPage 由 m_TotalMessageCount（RefreshMessages 以 CountMessages 純列舉取得）算；越界按鈕以 DisabledScope 擋。
        void DrawMessagePagerBar()
        {
            int total = m_TotalMessageCount;
            int maxPage = total <= 0 ? 0 : (total - 1) / MessagePageSize;
            using (new GUILayout.HorizontalScope("box"))
            {
                // 往「較新」方向（含跳到最新頁）— 已在最新頁則 disable
                using (new EditorGUI.DisabledScope(m_MessagePage <= 0))
                {
                    if (GUILayout.Button("⏮", UCL_GUIStyle.ButtonStyle, GUILayout.Width(32)))
                        m_PendingSetPage = 0;
                    if (GUILayout.Button("◀", UCL_GUIStyle.ButtonStyle, GUILayout.Width(32)))
                        m_PendingSetPage = m_MessagePage - 1;
                }
                // 頁碼指示：第 X/Y 頁 · 共 N 筆
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Messages.PagerFmt"), m_MessagePage + 1, maxPage + 1, total), UCL_GUIStyle.LabelStyle);
                // 往「較舊」方向 — 已在最舊頁則 disable
                using (new EditorGUI.DisabledScope(m_MessagePage >= maxPage))
                {
                    if (GUILayout.Button("▶", UCL_GUIStyle.ButtonStyle, GUILayout.Width(32)))
                        m_PendingSetPage = m_MessagePage + 1;
                }

                GUILayout.FlexibleSpace();

                // 輸入頁碼 → 跳轉（UI 為 1-based）。翻頁走 deferred m_PendingSetPage，同其他按鈕。
                GUILayout.Label(UCL_CodeLocalize.Get("Tavern.Messages.JumpTo"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_JumpPageInput = GUILayout.TextField(m_JumpPageInput ?? "", GUILayout.Width(48));
                if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Messages.JumpBtn"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    // 解析 1-based 輸入 → 0-based 頁碼，clamp 到 [0, maxPage]
                    if (int.TryParse((m_JumpPageInput ?? "").Trim(), out int p1) && p1 >= 1)
                    {
                        int target = p1 - 1;
                        if (target > maxPage) target = maxPage;
                        if (target < 0) target = 0;
                        m_PendingSetPage = target;
                    }
                }
            }
            GUILayout.Space(2);
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
                    // T28: persona avatar 優先 (msg.sender_avatar_sprite), fallback sender_id-based legacy
                    DrawAvatar(m.sender_id, 48, m.sender_avatar_sprite);
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
                        GUILayout.Label(m.DisplayName, s_NameStyleBold, GUILayout.ExpandWidth(false));
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
                        GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Tavern.Reply.IndicatorFmt"), m_ReplyTo.Value), UCL_GUIStyle.LabelStyle, GUILayout.Width(120));
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Cancel"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) m_ReplyTo = null;
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
                        DrawAvatar(SelectedIdentityId, 48);
                    }
                    using (new GUILayout.VerticalScope())
                    {
                        var (idntId, idntName, idntKind) = ResolveSelectedIdentity();
                        var hintStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { normal = { textColor = new Color(0.7f, 0.85f, 1f) } };
                        string asWho = string.IsNullOrEmpty(idntId)
                            ? UCL_CodeLocalize.Get("Tavern.Input.NoIdentity")
                            : string.Format(UCL_CodeLocalize.Get("Tavern.Input.SpeakingAsFmt"), idntName, idntKind);
                        GUILayout.Label(asWho, hintStyle);
                        m_Input = GUILayout.TextArea(m_Input ?? "", GUILayout.MinHeight(60));
                    }
                }
                m_MetaInput = LabeledTextField(UCL_CodeLocalize.Get("Tavern.Field.Meta"), m_MetaInput, 130);
                m_RefsInput = LabeledTextField(UCL_CodeLocalize.Get("Tavern.Field.Refs"), m_RefsInput, 130);
                using (new GUILayout.HorizontalScope())
                {
                    bool canSend = !string.IsNullOrEmpty(SelectedIdentityId) && !string.IsNullOrEmpty(m_Input);
                    GUI.enabled = canSend;
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Tavern.Btn.Send"), UCL_GUIStyle.GetButtonStyle(canSend ? Color.cyan : Color.gray), GUILayout.ExpandWidth(false)))
                    {
                        DoSend();
                    }
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
            string id = SelectedIdentityId;
            if (string.IsNullOrEmpty(id)) return (null, null, null);
            var ros = m_RosterCache?.identities?.Find(x => x != null && x.id == id);
            string display = ros != null && !string.IsNullOrEmpty(ros.display_name) ? ros.display_name : id;
            string kind = ros != null && !string.IsNullOrEmpty(ros.kind) ? ros.kind : "agent";
            return (id, display, kind);
        }

        // 區塊職責: 透過 Cmd_Tavern.Op_Post 統一發言 — 跟 agent 走 run_cmd.py 完全一樣的底層邏輯
        // 物理意義: Tim 2026-05-12 拍板 — Page Send 不再 bypass Op_Post 直接 AppendMessage,
        //          改建 args dict 走 Cmd_Tavern.ExecuteAsync, 自動繼承下列 7 道機制:
        //          (1) alter pacing — meta alter-pacing-bypass=true 已 preset 跳過
        //          (2) presence 更新 (sender 自動 status=active + current_room)
        //          (3) @mention parser → 對方 inbox auto-write
        //          (4) glossary auto-attach — body 命中 glossary 詞自動 append refs block
        //          (5) Discord mirror (notify_discord webhook)
        //          (6) WriteLastView _last_view.md 渲染
        //          (7) auto-credit/auto-debit hook (work_post / token_parse)
        // 數值影響: async void 適合 IMGUI button click context — 立刻 return GUI 繼續 redraw,
        //          post 寫入跟 RefreshMessages 在 await 後處理; m_Input 立刻清空給使用者乾淨輸入區。
        // Anti-pattern: 別在這裡再 AppendMessage / WriteLastView — Op_Post 全包了, 重複呼叫 = 雙重寫入
        async void DoSend()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) { Debug.LogError(UCL_CodeLocalize.Get("Tavern.Err.NoIdentity")); return; }
            if (string.IsNullOrEmpty(SelectedRoomId)) { Debug.LogError(UCL_CodeLocalize.Get("Tavern.Err.NoRoom")); return; }

            // 構造 args dict — schema 跟 run_cmd.py run Tavern --arg key=val 完全對齊
            var args = new Dictionary<string, string>
            {
                { "op", "post" },
                { "room", SelectedRoomId },
                { "sender", idntId },
                { "body", m_Input },
            };
            if (m_ReplyTo.HasValue) args["reply_to"] = m_ReplyTo.Value.ToString();
            if (!string.IsNullOrEmpty(m_MetaInput)) args["meta"] = m_MetaInput;
            if (!string.IsNullOrEmpty(m_RefsInput)) args["refs"] = m_RefsInput;

            // 立刻清空輸入區 — 使用者按 Send 後馬上看到乾淨 input, 不必等 await
            m_Input = "";
            m_ReplyTo = null;

            // 走 Cmd_Tavern.Op_Post 同一路徑 — 跟 agent 走 queue.json 觸發的完全一致
            // 不走 queue.json 因為 Page 就在 Editor 內, 直接 invoke handler 省一次 watcher tick
            try
            {
                var cmd = new UCL.Core.EditorLib.AgentCommands.ChatTavern.Cmd_Tavern();
                await cmd.ExecuteAsync(args, default);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ChatTavernPage] DoSend via Cmd_Tavern fail: {ex.Message}\n{ex.StackTrace}");
            }

            RefreshMessages();
        }

        void DoJoin()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) return;
            UCL_ChatTavernIO.AddMember(SelectedRoomId, idntId);
            UCL_ChatTavernIO.AppendMessage(SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = idntId, sender_name = idntName, kind = "join",
                body = string.Format(UCL_CodeLocalize.Get("Tavern.Join.MsgFmt"), idntName),
            });
            RefreshMessages();
            RefreshMembers();
        }

        void DoLeave()
        {
            var (idntId, idntName, _) = ResolveSelectedIdentity();
            if (string.IsNullOrEmpty(idntId)) return;
            UCL_ChatTavernIO.RemoveMember(SelectedRoomId, idntId);
            UCL_ChatTavernIO.AppendMessage(SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = idntId, sender_name = idntName, kind = "leave",
                body = string.Format(UCL_CodeLocalize.Get("Tavern.Leave.MsgFmt"), idntName),
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
            if (!string.IsNullOrEmpty(SelectedRoomId))
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
        // 區塊職責：重抓當前頁訊息（分頁模型）— 單一進入點
        // 物理意義：
        //   1) 切房 → 頁碼重置回 0（最新頁），不繼承上一房翻到的深度
        //   2) 用 CountMessages（純列舉零 parse）取總數 → 算 maxPage + clamp 當前頁（防新訊息進來後頁碼越界）
        //   3) Tail((page+1)*PageSize) 取「涵蓋當前頁的最少訊息」（oldest-first），切出本頁那 PageSize 筆
        // 數值影響：m_MessagesCache 只存本頁 ≤ PageSize 筆 → DrawMessagesView 每幀最多繪 PageSize 列。
        //          頁 p 對應全序 [T-(p+1)*PageSize, T-p*PageSize)，正好是 Tail((p+1)*PageSize) 的最前 sliceCount 筆。
        void RefreshMessages()
        {
            bool roomChanged = m_LastSeenRoomId != SelectedRoomId;
            if (roomChanged) m_MessagePage = 0;   // 切房回最新頁

            // #3 優化 (Tim 2026-07-20)：單次 Tail 同時取「本頁訊息 + 總數」— Tail 給每筆的 seq = 該訊息在全序的
            //   絕對位置(1-based)，且 Tail 一定含最新那筆 → 最新一筆的 seq == 房間總數。故不必再單獨呼叫
            //   CountMessages 掃一次目錄（每次 poll 從 2 次全房列舉降到 1 次），也不動共用 IO 層/不影響 Discord daemon。
            int fetch = (m_MessagePage + 1) * MessagePageSize;
            var tail = UCL_ChatTavernIO.Tail(SelectedRoomId, fetch);   // oldest-first, len = min(fetch, total)
            int total = (tail != null && tail.Count > 0) ? tail[tail.Count - 1].seq : 0;

            // clamp 頁碼（防新訊息湧入 / 刪檔後越界）；只有真的越界才重 Tail 一次（罕見）
            int maxPage = total <= 0 ? 0 : (total - 1) / MessagePageSize;
            if (m_MessagePage < 0) m_MessagePage = 0;
            if (m_MessagePage > maxPage)
            {
                m_MessagePage = maxPage;
                fetch = (m_MessagePage + 1) * MessagePageSize;
                tail = UCL_ChatTavernIO.Tail(SelectedRoomId, fetch);
            }
            m_TotalMessageCount = total;

            // 本頁 = tail 的最前 sliceCount 筆（頁 p 對應全序 [T-(p+1)*PageSize, T-p*PageSize)）
            int sliceCount = System.Math.Min(MessagePageSize, System.Math.Max(0, total - m_MessagePage * MessagePageSize));
            if (tail != null && tail.Count > sliceCount)
                m_MessagesCache = tail.GetRange(0, sliceCount);
            else
                m_MessagesCache = tail ?? new List<UCL_ChatMessage>();

            m_LastSeenRoomId = SelectedRoomId;
        }
        void RefreshMembers() { m_MembersCache = UCL_ChatTavernIO.LoadMembers(SelectedRoomId); }

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
            if (string.IsNullOrEmpty(SelectedRoomId) && m_RoomIds != null && m_RoomIds.Count > 0)
            {
                SelectedRoomId = m_RoomIds[0];
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
            if (!string.IsNullOrEmpty(SelectedRoomId))
            {
                RefreshMessages();
                RefreshMembers();
            }
        }

        // 區塊職責：節流抓取 Treasury 餘額
        // 物理意義：每 2 秒或身分切換時，才主動查一次 UCL_TreasuryLedger 防止 IO 暴衝
        // 數值影響：寫入 m_CachedTokenBalance
        void UpdateTokenBalance()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            bool idChanged = SelectedIdentityId != m_CachedBalanceIdentityId;
            if (!idChanged && now - m_LastBalanceRefreshTime < BalanceRefreshIntervalSec) return;

            m_CachedBalanceIdentityId = SelectedIdentityId;
            m_LastBalanceRefreshTime = now;

            if (string.IsNullOrEmpty(SelectedIdentityId))
            {
                m_CachedTokenBalance = 0;
                return;
            }

            try
            {
                // 呼叫靜態 API 取回 Token 數量
                m_CachedTokenBalance = UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.GetBalance(SelectedIdentityId, "tavern_token");
            }
            catch
            {
                m_CachedTokenBalance = 0;
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
