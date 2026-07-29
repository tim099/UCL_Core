// 區塊職責：酒館後台管理頁 (Tavern Admin) — Discord mirror 同步狀態 / persona 頭像 override /
//            底層 config・state 檔案的可視化管理入口（Tim 2026-07-15 拍板，參考 UCL_LoginStatusPage）。
// 物理意義：notify_discord.py 的 tavern_mirror 機制全靠 notify_config.json + _tavern_state.json 兩份
//          Python 端檔案驅動；本頁是 C# 端唯讀+受控寫入的前端 — 讓 Tim 不必手改 JSON 就能：
//          看同步到哪一筆 (per-room last_seen_seq vs 房間當前 max seq)、改 persona 頭像 URL、
//          重置失敗計數、手動觸發一次同步。
// 數值影響：寫入走 JsonData round-trip (ToJsonBeautify)，Python json.loads 可正常讀回；
//          state/config 的寫入都即時 flush，下一次 mirror run 即生效。
// 設計取捨：UI 字串仿 UCL_ControlPanelPage 慣例用 zh-Hant 硬編（內部管理頁，不走 CodeLocalize）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 酒館後台管理頁 — Discord mirror 同步狀態 / persona 頭像 override / 底層檔案管理。
    /// 入口：控制台 (UCL_ControlPanelPage) 的「🍺 酒館後台管理」按鈕。
    /// </summary>
    public class UCL_ChatTavernAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "酒館後台管理";
        public override bool ShowInPageMenu => true;

        public static UCL_ChatTavernAdminPage Create() => UCL_EditorPage.Create<UCL_ChatTavernAdminPage>();

        // ==== 路徑（跟 notify_discord.py / UCL_ChatTavernIO 同一套解析根）====
        static string PromptQueueDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "PromptQueue");
        static string NotifyConfigPath => Path.Combine(PromptQueueDir, "notify_config.json");
        static string TavernStatePath => Path.Combine(PromptQueueDir, "_tavern_state.json");
        static string DrainLogPath => Path.Combine(PromptQueueDir, "_drain.log");

        // ==== 顯示用快取（開頁/按 Refresh 才重讀檔，不每幀掃磁碟）====
        JsonData m_Config;                       // notify_config.json 整份（寫回時 round-trip）
        JsonData m_State;                        // _tavern_state.json 整份
        List<string> m_WatchedRooms = new List<string>();
        Dictionary<string, int> m_RoomMaxSeq = new Dictionary<string, int>();      // 房間當前 max seq（_seq.txt）
        Dictionary<string, string> m_SeqDraft = new Dictionary<string, string>();  // 套用 seq 編輯 draft

        // ==== T6.5：native owner 下的同步進度快取（LoadData 時算好，DrawTavernRoomRows 讀快取，不每幀讀檔）====
        // 物理意義：native 游標是 per-webhook ts_high（無 seq）→ 由 UCL_DiscordMirrorDaemon.GetRoomNativeProgress
        //          反推「已同步到 seq / 待同步筆數」；python owner 下這些不填，顯示照舊走 last_seen_seq。
        Dictionary<string, int> m_RoomNativeSynced = new Dictionary<string, int>();  // native：已同步到 seq
        Dictionary<string, int> m_RoomNativePending = new Dictionary<string, int>(); // native：待同步筆數
        Dictionary<string, bool> m_RoomNativeCapped = new Dictionary<string, bool>();// native：待同步是否超反推上限（顯示標「≥」）

        // ==== 註冊新房下拉（Tim 2026-07-21）— 列出「所有房間 dir ∖ 已 watched」供選取加入同步 ====
        List<string> m_UnregisteredRooms = new List<string>();  // 未註冊房 id 清單（下拉值）
        int m_SelectedNewRoomIdx = 0;                            // 下拉選中索引
        List<KeyValuePair<string, string>> m_AvatarOverrides = new List<KeyValuePair<string, string>>();
        List<string> m_LogTail = new List<string>();
        bool m_Loaded = false;

        // ==== Inbound（Discord → 酒館）顯示快取（Tim 2026-07-28）====
        // 物理意義：三份外部真相各掃一次進快取 — 中繼器存活（Process 註冊中心）、頻道路由
        //          （ChatTavern/discord_channel_routing.json）、bot token secret（_secrets 掃描）。
        // 數值影響：只在 LoadData（開頁 / 按 Refresh）掃，不每幀碰磁碟與 process 列舉。
        string m_InboundRelayStatus = "(未載入)";
        string m_InboundRoutingSummary = "(未載入)";
        readonly List<string> m_InboundRoutingRows = new List<string>();
        string m_InboundTokenStatus = "(未載入)";

        // ==== Webhook 設定 — 下拉選取模式（Tim 2026-07-16 拍板，交互仿 Persona 頭像 panel）====
        // 區塊職責：六個 webhook 消費端（全走統一 schema：webhook_urls / webhook_file / webhook_env_var，
        //          優先序 ENV > FILE > CONFIG）的觀看與操作 — 下拉選 stream → 純文字顯示該 stream 的
        //          同步狀態（未同步筆數 / cursor / 失敗計數）+ URL 遮罩列表 + 驗證/刪除/新增。
        // 物理意義：webhook URL 是 secret（拿到即可對頻道發言）且 Tim 常截圖本頁 — 列表永遠遮罩
        //          （只露 webhook id，token 全隱），驗證用 GET 取回 Discord 端頻道名顯示健康度。
        // 數值影響：增刪走 WriteConfigRoot 原子落檔（照現行慣例寫 config，Q1 Tim 拍板行為不變）；
        //          新增一律先驗證通過才入庫（擋貼錯 URL）。
        // 區塊職責：stream 清單 — 從 config **動態發現**（Tim 2026-07-28 要求「新增 & 刪除 stream」）
        // 物理意義：原本是硬編 static 陣列 → 新增一條 stream 得改 code。改為掃 notify_config 根層：
        //          任何「物件且帶 webhook_urls / webhook_env_var / webhook_file 任一欄」的 key 就是一條 stream，
        //          外加兩個特殊位置：queue-idle（= config 根本身）與 quest_routing（nested 在 tavern_mirror 下）。
        // 數值影響：LoadData 時重建；新增 = 寫一個 {enabled:false, webhook_urls:[]} 空塊；
        //          刪除 = 移除該 key（core stream 擋刪，見 s_CoreStreamKeys）。
        // 邊界：core stream（有 C# 消費者）誤刪會靜默停掉鏡像 → UI 直接不給刪，只能改內容。
        static readonly HashSet<string> s_CoreStreamKeys = new HashSet<string>
            { "tavern_mirror", "treasury_mirror", "tavern_inbound", "quest_routing", "queue-idle" };
        // 已知 stream 的人話說明（未列者顯示「自訂」）
        static readonly Dictionary<string, string> s_StreamDesc = new Dictionary<string, string>
        {
            { "tavern_mirror",   "酒館訊息 → Discord" },
            { "treasury_mirror", "記帳 embed → Discord" },
            { "tavern_inbound",  "Discord → 酒館 inbound" },
            { "quest_routing",   "task lifecycle 分流（nested）" },
            { "wake_notify",     "⚠ 已退役 — 無消費者" },
            { "queue-idle",      "⚠ 已退役 — 無消費者（= config 根層）" },
        };

        List<string> m_StreamKeys = new List<string>();      // 動態發現的 stream key
        List<string> m_StreamLabels = new List<string>();    // 對應下拉顯示字串
        int m_SelectedStreamIdx = 0;
        string m_NewStreamKey = "";                          // 新增 stream 的 key 輸入
        string m_PendingDeleteStream = null;                 // 二段確認：待確認刪除的 stream key

        // 掃 config 建 stream 清單（LoadData 呼叫）
        void RebuildStreamList()
        {
            m_StreamKeys.Clear();
            m_StreamLabels.Clear();
            var found = new List<string>();
            try
            {
                if (m_Config != null && m_Config.IsObject && m_Config.Dic != null)
                {
                    foreach (var kv in m_Config.Dic)
                    {
                        var v = kv.Value;
                        if (v == null || !v.IsObject) continue;
                        bool isStream = v.Contains("webhook_urls") || v.Contains("webhook_env_var") || v.Contains("webhook_file");
                        if (isStream) found.Add(kv.Key);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[TavernAdmin] 掃 stream 失敗: {e.Message}"); }
            found.Sort(StringComparer.Ordinal);
            // 特殊位置：quest_routing（nested）與 queue-idle（根層）不在上面的掃描結果內，手動補
            if (!found.Contains("quest_routing")) found.Add("quest_routing");
            if (!found.Contains("queue-idle")) found.Add("queue-idle");
            foreach (var k in found)
            {
                m_StreamKeys.Add(k);
                string desc = s_StreamDesc.TryGetValue(k, out var d) ? d : "自訂";
                m_StreamLabels.Add($"{k}（{desc}）{(s_CoreStreamKeys.Contains(k) ? " 🔒" : "")}");
            }
            if (m_SelectedStreamIdx >= m_StreamKeys.Count) m_SelectedStreamIdx = 0;
        }
        string m_NewWebhookUrl = "";
        readonly Dictionary<string, string> m_WebhookProbe = new Dictionary<string, string>();   // url → 驗證結果快取

        // ==== Persona 頭像 Override — 下拉選取模式（Tim 2026-07-15 修訂）====
        // 區塊職責：PopupSearchCache 選 persona → URL 欄自動帶入該 persona 當前 override（無設定 = 空）
        //          → 修改後按套用寫回設定檔。persona 清單 = persona pool（AwakenInit/personas/*.json）
        //          ∪ 既有 override keys（config 裡有但 pool 沒有的也要能選到、能清）。
        List<string> m_PersonaNames = new List<string>();     // 下拉值清單
        List<string> m_PersonaOptions = new List<string>();   // 下拉顯示字串（有 override 的標 ●）
        int m_SelectedPersonaIdx = 0;
        string m_SelectedUrlDraft = "";                        // 當前選中 persona 的 URL 編輯 draft
        // 區塊職責：兩個 UCL_ObjectDictionary 刻意分開 — 生命週期不同，混用會互相連坐
        // 物理意義：m_Dic 存 PopupSearchCache（**衍生資料**：下拉選項變了就該失效 → LoadData 會 Clear 它）；
        //          m_FoldDic 存各區塊折疊狀態（**使用者 UI 偏好**：不該因為資料重載而被重置）。
        // 血證（2026-07-28 Tim QA）：折疊狀態原本也塞 m_Dic → 按「Discord 同步總開關」→ WriteConfigRoot
        //          → LoadData → m_Dic.Clear() → 折疊值消失 → 下一幀退回 iDefaultValue(true) = 強制展開。
        //          症狀是「按總開關就展開、而且收不起來」，看起來像 key 撞名，實際是共用快取被整個清掉。
        //          同理 WriteStateField / Refresh / 套用 seq / 新增刪除 stream 全都會觸發 LoadData，
        //          折疊只要跟 popup 共用字典就會被反覆重置。
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();      // PopupSearchCache（LoadData 會清）
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();  // 折疊狀態（永不隨 LoadData 清）

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                {
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                }
                return m_WrapLabelStyle;
            }
        }
        /// <summary>當前下拉選中的 persona 名（清單空回 null）。</summary>
        string SelectedPersona => (m_PersonaNames.Count > 0 && m_SelectedPersonaIdx >= 0 && m_SelectedPersonaIdx < m_PersonaNames.Count)
                ? m_PersonaNames[m_SelectedPersonaIdx] : null;
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
        }
        /// <summary>
        /// 註：舊的 GetLastSeen(room)（讀 rooms.<room>.last_seen_seq）已於 2026-07-28 移除 —
        /// 那是 python mirror 的 per-room 位置浮水印；native 游標走 rooms.<room>.webhooks 的
        /// ts_high + per-webhook seen-set，進度一律經 UCL_DiscordMirrorDaemon.GetRoomNativeProgress 取得。
        /// </summary>
        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            DrawMirrorStatePanel();
            GUILayout.Space(8);
            DrawInboundPanel();
            GUILayout.Space(8);
            DrawAvatarOverridePanel();
            GUILayout.Space(8);
            DrawWebhookPanel();
            GUILayout.Space(8);
            DrawFilesPanel();
        }
        // ===========================================================
        // 區塊：資料載入
        // 物理意義：config / state / 各房 _seq.txt / log tail 一次讀齊進快取；draft 欄位同步重置。
        // ===========================================================
        void LoadData()
        {
            m_Loaded = true;
            // 記住 reload 前選中的 persona — 寫回後重載清單順序可能變（● 標記），按名字對回索引
            string prevSelected = (m_PersonaNames.Count > 0 && m_SelectedPersonaIdx >= 0 && m_SelectedPersonaIdx < m_PersonaNames.Count)
                ? m_PersonaNames[m_SelectedPersonaIdx] : null;
            m_Config = null; m_State = null;
            m_WatchedRooms.Clear(); m_RoomMaxSeq.Clear(); m_SeqDraft.Clear();
            m_RoomNativeSynced.Clear(); m_RoomNativePending.Clear(); m_RoomNativeCapped.Clear();
            m_AvatarOverrides.Clear(); m_LogTail.Clear();
            // T6.5：本次載入的 mirror owner 快照 — native 下同步進度改由 daemon 反推（見下方 rooms 迴圈）
            try
            {
                if (File.Exists(NotifyConfigPath)) m_Config = JsonData.ParseJson(File.ReadAllText(NotifyConfigPath));
                if (File.Exists(TavernStatePath)) m_State = JsonData.ParseJson(File.ReadAllText(TavernStatePath));

                var tm = (m_Config != null && m_Config.Contains("tavern_mirror")) ? m_Config["tavern_mirror"] : null;
                if (tm != null)
                {
                    // watched rooms + 房間當前 max seq（rooms/<room>/_seq.txt，缺檔 = 0）
                    if (tm.Contains("rooms") && tm["rooms"].IsArray)
                    {
                        for (int i = 0; i < tm["rooms"].Count; i++)
                        {
                            // GetString() 無參 = 取字串節點本身的值；GetString("") 會把空字串當 key 查（bug 前科）
                            string room = tm["rooms"][i].GetString();
                            if (string.IsNullOrEmpty(room)) continue;
                            m_WatchedRooms.Add(room);
                            string seqPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "rooms", room, "_seq.txt");
                            int maxSeq = 0;
                            if (File.Exists(seqPath)) int.TryParse(File.ReadAllText(seqPath).Trim(), out maxSeq);
                            m_RoomMaxSeq[room] = maxSeq;
                            // 2026-07-28：native 為唯一 owner → 同步進度/draft 一律走 daemon 反推的 native 游標
                            {
                                UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.GetRoomNativeProgress(
                                    room, maxSeq, out int synced, out int pending, out bool capped);
                                m_RoomNativeSynced[room] = synced;
                                m_RoomNativePending[room] = pending;
                                m_RoomNativeCapped[room] = capped;
                                m_SeqDraft[room] = synced.ToString();
                            }
                        }
                    }
                    // persona 頭像 override（_ 開頭 key 是註解欄，不進列表）
                    if (tm.Contains("persona_avatar_overrides"))
                    {
                        var po = tm["persona_avatar_overrides"];
                        if (po.IsObject && po.Dic != null)
                        {
                            foreach (var key in po.Dic.Keys.Where(k => !k.StartsWith("_")).OrderBy(k => k, StringComparer.Ordinal))
                            {
                                m_AvatarOverrides.Add(new KeyValuePair<string, string>(key, po.GetString(key, "")));
                            }
                        }
                    }
                }

                // 區塊職責：建 persona 下拉清單 — PersonaCard asset 全 ID（Tim 2026-07-15 拍板改用
                //          UCL_ChatTavernPersonaCardAsset.Util.GetAllIDs()）∪ 既有 override keys
                // 物理意義：PersonaCard 是 persona 的正式 asset 真相源（不依賴 AwakenInit/personas 目錄
                //          存在與否）；config 裡有但 card 沒有的（改名/測試殘留）也要能選到才能清
                var names = new SortedSet<string>(StringComparer.Ordinal);
                try
                {
                    foreach (var id in UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernPersonaCardAsset.Util.GetAllIDs())
                    {
                        if (!string.IsNullOrEmpty(id) && !id.StartsWith("_")) names.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[TavernAdmin] PersonaCard GetAllIDs fail: {ex.Message}");
                }
                foreach (var kv in m_AvatarOverrides) names.Add(kv.Key);
                m_PersonaNames = names.ToList();
                var overrideKeys = new HashSet<string>(m_AvatarOverrides.Select(kv => kv.Key));
                m_PersonaOptions = m_PersonaNames.Select(n => overrideKeys.Contains(n) ? $"● {n}" : n).ToList();
                m_Dic.Clear();   // 下拉選項變了 → 清 PopupSearchCache（折疊狀態在 m_FoldDic，不受影響）

                // 還原選中項 + 同步 URL draft（找不到舊選中 → 回第 0 項）
                m_SelectedPersonaIdx = prevSelected != null ? Math.Max(0, m_PersonaNames.IndexOf(prevSelected)) : 0;
                SyncUrlDraftFromSelection();

                // log tail — 只留 [tavern] 相關的最後 5 行（同步結果一目了然）
                if (File.Exists(DrainLogPath))
                {
                    m_LogTail = File.ReadLines(DrainLogPath)
                        .Where(l => l.Contains("[tavern]"))
                        .Reverse().Take(5).Reverse().ToList();
                }

                // 註冊新房下拉的候選 = 所有房間 dir ∖ 已 watched（Tim 2026-07-21）
                // 物理意義：列舉 rooms 根目錄子資料夾（只取名、不讀內容）；扣掉已在 watched list 的 → 剩可註冊者
                m_UnregisteredRooms.Clear();
                m_SelectedNewRoomIdx = 0;
                try
                {
                    string roomsRoot = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernIO.GetRoomsRoot();
                    if (Directory.Exists(roomsRoot))
                    {
                        var watched = new HashSet<string>(m_WatchedRooms, StringComparer.Ordinal);
                        foreach (var dir in Directory.GetDirectories(roomsRoot))
                        {
                            string rid = Path.GetFileName(dir);
                            // _ 開頭 = 系統/暫存資料夾，不列；已 watched 不列
                            if (string.IsNullOrEmpty(rid) || rid.StartsWith("_") || watched.Contains(rid)) continue;
                            m_UnregisteredRooms.Add(rid);
                        }
                        m_UnregisteredRooms.Sort(StringComparer.Ordinal);
                    }
                }
                catch (Exception exRooms)
                {
                    Debug.LogWarning($"[TavernAdmin] 列舉房間目錄失敗: {exRooms.Message}");
                }

                RebuildStreamList();   // stream 下拉改動態發現（可增刪）

                // Inbound 三份外部真相各掃一次（Tim 2026-07-28）— 中繼器存活 / 頻道路由 / bot token secret
                m_InboundRelayStatus = ScanInboundRelayStatus();
                ScanInboundRouting();
                m_InboundTokenStatus = ScanInboundTokenStatus();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernAdmin] load fail: {e.Message}");
            }
        }
        // ===========================================================
        // 區塊1：Mirror 同步狀態
        // 物理意義：per-room「同步到哪一筆」可視化 — last_seen_seq（可編輯）vs 房間 max seq；
        //          差額 = 尚未推到 Discord 的筆數（0 = 已追平）。
        // 數值影響：套用 seq 直接改 _tavern_state.json；設小 = 重放區間訊息、設大 = 跳過區間訊息，
        //          屬管理員操作，前排顯示警語。
        // ===========================================================
        void DrawMirrorStatePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                bool enabled = false;
                try
                {
                    var tm = (m_Config != null && m_Config.Contains("tavern_mirror")) ? m_Config["tavern_mirror"] : null;
                    if (tm != null) enabled = tm.GetBool("enabled", false);
                }
                catch { /* 缺欄位視為 off */ }

                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "MirrorFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>📡 Discord Mirror 同步狀態</b>", WrapLabelStyle);

                    // Discord 同步總開關 一次寫「仍有消費者」的三條 stream 的 enabled：tavern_mirror（酒館訊息）/ treasury_mirror（記帳頻道，
                    // 之前漏網的 bank 訊息就是它）/ tavern_inbound（Discord→酒館 inbound）。
                    // 2026-07-28: wake_notify 與頂層 enabled（queue-idle）隨 python notify 一同退役
                    //   （無 C# 對應實作、實測長期零活動）→ 不再寫入，避免「開了卻沒人讀」的假象。
                    // 顯示狀態以 tavern_mirror.enabled 為代表（master 寫入後三者同步）。
                    // 語意：預設 off、缺欄位視為 off（C# GetBool("enabled", false)）。
                    // 邊界：tavern_inbound 目前無 C# 中繼器接管 — 開關只表意圖，見下方 Inbound 區塊實況。
                    bool newEnabled = GUILayout.Toggle(enabled,
                        enabled ? " <color=#66ff66>● Discord 同步啟用中（按一下全部關閉）</color>" : " <color=#ff8866>○ Discord 同步已關閉（按一下全部啟用）</color>",
                        new GUIStyle(UCL_GUIStyle.ButtonStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    if (newEnabled != enabled)
                    {
                        WriteConfigRoot(cfg =>
                        {
                            foreach (var block in new[] { "tavern_mirror", "treasury_mirror", "tavern_inbound" })
                            {
                                if (!cfg.Contains(block)) cfg[block] = JsonData.ParseJson("{}");
                                cfg[block]["enabled"] = newEnabled;
                            }
                        });
                        Debug.Log($"[TavernAdmin] Discord 同步總開關 → {newEnabled}（tavern_mirror / treasury_mirror / tavern_inbound 三 stream 同步寫入）");
                    }

                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;


                int failures = 0;
                try { if (m_State != null) failures = m_State.GetInt("consecutive_failures", 0); } catch { }

                using (new GUILayout.HorizontalScope())
                {

                    GUILayout.Space(8);
                    GUILayout.Label($"連續失敗計數：{failures}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (failures > 0 && GUILayout.Button("歸零", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        WriteStateField(s => s["consecutive_failures"] = 0);
                    }
                    GUILayout.FlexibleSpace();
                    // 手動觸發一次 mirror run — 2026-07-28 起只有 native 一條路：daemon.ForceTick 立即掃描送出
                    if (GUILayout.Button("▶ 立即觸發同步", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.ForceTick();
                        Debug.Log("[TavernAdmin] 手動觸發 mirror daemon ForceTick（掃描 + 送出立即跑一輪）");
                    }
                }

                // per-room 同步進度 + 套用 seq → 移至「🔗 Webhook 設定」panel 下拉的 tavern_mirror 分支
                // （Tim 2026-07-16 整合拍板 — 本 panel 收斂成全域資訊：總開關 / 失敗計數 / 觸發 / log）
                GUILayout.Label("per-room 同步進度與套用 seq → 下方「🔗 Webhook 設定」選 tavern_mirror。", WrapLabelStyle);

                if (m_LogTail.Count > 0)
                {
                    GUILayout.Label("<b>最近同步 log</b>（_drain.log [tavern] 尾 5 筆）：", WrapLabelStyle);
                    foreach (var line in m_LogTail) GUILayout.Label($"  {line}", WrapLabelStyle);
                }
            }
        }



        // 區塊職責：把 URL draft 同步成「當前選中 persona 的現有 override」— 無設定則為空字串
        void SyncUrlDraftFromSelection()
        {
            string p = SelectedPersona;
            m_SelectedUrlDraft = p == null ? "" : (m_AvatarOverrides.FirstOrDefault(kv => kv.Key == p).Value ?? "");
        }

        // ===========================================================
        // 區塊：Webhook 設定 panel（下拉選 stream → 狀態純文字 + URL 遮罩列表 + 驗證/刪/增）
        // ===========================================================

        /// <summary>取指定 stream 的 config 塊；"queue-idle"=root、"quest_routing"=tavern_mirror 子塊。回 null = 不存在。</summary>
        static JsonData GetStreamBlock(JsonData iCfg, string iKey)
        {
            if (iCfg == null) return null;
            if (iKey == "queue-idle") return iCfg;
            if (iKey == "quest_routing")
            {
                var tm = iCfg.Contains("tavern_mirror") ? iCfg["tavern_mirror"] : null;
                return (tm != null && tm.Contains("quest_routing")) ? tm["quest_routing"] : null;
            }
            return iCfg.Contains(iKey) ? iCfg[iKey] : null;
        }

        /// <summary>遮罩 webhook URL — 只露 webhook id，token 全隱（secret 防截圖外洩）。</summary>
        static string MaskWebhook(string iUrl)
        {
            var m = System.Text.RegularExpressions.Regex.Match(iUrl ?? "", @"webhooks/(\d+)/");
            return m.Success ? $"…/webhooks/{m.Groups[1].Value}/***" : "(格式異常的 URL)***";
        }

        /// <summary>同步 GET 探測 webhook（3s timeout）— Discord 回 webhook 的 name/channel。</summary>
        static string ProbeWebhook(string iUrl)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(iUrl);
                req.Method = "GET";
                req.Timeout = 3000;
                req.UserAgent = "UCL-TavernAdmin";
                using var resp = (System.Net.HttpWebResponse)req.GetResponse();
                using var sr = new StreamReader(resp.GetResponseStream());
                string body = sr.ReadToEnd();
                var m = System.Text.RegularExpressions.Regex.Match(body, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                return m.Success ? $"<color=#66ff66>✓ #{m.Groups[1].Value}</color>" : "<color=#66ff66>✓ OK</color>";
            }
            catch (System.Net.WebException we)
            {
                var r = we.Response as System.Net.HttpWebResponse;
                return r != null ? $"<color=#ff6666>✗ HTTP {(int)r.StatusCode}（已失效）</color>" : $"<color=#ff6666>✗ {we.Status}</color>";
            }
            catch (Exception e)
            {
                return $"<color=#ff6666>✗ {e.Message}</color>";
            }
        }

        /// <summary>純文字同步狀態 — per-stream 撈便宜可得的 state 資訊（未同步筆數 / cursor / 失敗計數）。</summary>
        List<string> BuildStreamInfoLines(string iKey, JsonData iBlock)
        {
            var lines = new List<string>();
            bool enabled = false;
            try { enabled = iBlock != null && iBlock.GetBool("enabled", false); } catch { }
            lines.Add(enabled ? "enabled: <color=#66ff66>true</color>" : "enabled: <color=#ff8866>false（缺欄位視為 false）</color>");
            try
            {
                switch (iKey)
                {
                    case "tavern_mirror":
                        // per-room 同步進度 + 套用 seq 為互動列 — 在 DrawTavernRoomRows 繪製（Tim 2026-07-16 整合進下拉）
                        if (m_State != null) lines.Add($"連續失敗計數: {m_State.GetInt("consecutive_failures", 0)}");
                        break;
                    case "treasury_mirror":
                        string cursor = "";
                        if (m_State != null && m_State.Contains("treasury")) cursor = m_State["treasury"].GetString("last_seen", "");
                        lines.Add(string.IsNullOrEmpty(cursor) ? "cursor: (未建立 baseline)" : $"cursor: {cursor}");
                        // 未同步 entry 粗估 — 只掃 cursor 當日之後的資料夾（便宜路徑）
                        string ledgerRoot = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Treasury", "ledger");
                        if (!string.IsNullOrEmpty(cursor) && Directory.Exists(ledgerRoot))
                        {
                            string cursorDate = cursor.Split('/')[0];
                            int pendingN = 0;
                            foreach (var ddir in Directory.GetDirectories(ledgerRoot).OrderBy(d => d, StringComparer.Ordinal))
                            {
                                string dname = Path.GetFileName(ddir);
                                if (string.Compare(dname, cursorDate, StringComparison.Ordinal) < 0) continue;
                                foreach (var f in Directory.GetFiles(ddir, "*.json"))
                                {
                                    if (string.Compare($"{dname}/{Path.GetFileName(f)}", cursor, StringComparison.Ordinal) > 0) pendingN++;
                                }
                            }
                            lines.Add(pendingN == 0 ? "未同步 entry: 0（已追平）" : $"<color=#ffcc44>未同步 entry: {pendingN}</color>（含預設不播的 __audit）");
                        }
                        break;
                    case "wake_notify":
                        string wakeStatePath = Path.Combine(PromptQueueDir, "_wake_state.json");
                        if (File.Exists(wakeStatePath))
                        {
                            var ws = JsonData.ParseJson(File.ReadAllText(wakeStatePath));
                            int fails = ws.GetInt("consecutive_failures", 0);
                            lines.Add($"連續失敗計數: {fails}" + (fails >= 5 ? "<color=#ff6666>（已 auto-disabled — webhook 曾 404，重發後請歸零）</color>" : ""));
                        }
                        break;
                    case "queue-idle":
                        string notifyStatePath = Path.Combine(PromptQueueDir, "_notify_state.json");
                        if (File.Exists(notifyStatePath))
                        {
                            var ns = JsonData.ParseJson(File.ReadAllText(notifyStatePath));
                            lines.Add($"last_done_seq: {ns.GetInt("last_done_seq", -1)} / 連續失敗: {ns.GetInt("consecutive_failures", 0)}");
                        }
                        break;
                    case "tavern_inbound":
                        if (iBlock != null)
                        {
                            lines.Add($"bot_status: {iBlock.GetString("bot_status", "(未知)")}");
                            lines.Add("⚠ inbound 由 daemon 啟動時讀 config — 改動後需從控制台重啟酒館系統生效");
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                lines.Add($"(state 讀取失敗: {e.Message})");
            }
            return lines;
        }

        // 區塊職責：tavern_mirror 的 per-room 同步進度 + 套用 seq 互動列（原 Mirror panel 功能，
        //          Tim 2026-07-16 拍板整合進 Webhook 下拉的 tavern_mirror 分支）。
        // 數值影響：套用 seq 直改 _tavern_state.json — 設小 = 重放區間訊息、設大 = 跳過區間，警語常駐。
        void DrawTavernRoomRows()
        {
            if (m_WatchedRooms.Count == 0)
            {
                GUILayout.Label("  (config 無 watched rooms)", UCL_GUIStyle.LabelStyle);
                return;
            }
            // 區塊職責：T6.5 — 套用 seq / 追平 依 mirror owner 分流到正確後端（Tim 2026-07-21 拍板「串接 C#」）
            // 物理意義：native owner 下游標是 per-webhook ts_high（無 seq）→ 控件改呼叫 daemon.AdminSetRoomCursorToSeq
            //          把「seq N 邊界」翻成 ts_high+seen 重設全房 webhook；顯示的「已同步/待同步」也改由 daemon
            //          反推 native 游標（見 LoadData 的 GetRoomNativeProgress）。python owner 下維持舊 last_seen_seq。
            // 數值影響：動作寫 _tavern_state.json rooms.<room>.webhooks；往回調會讓區間訊息重發到 Discord。
            GUILayout.Label("  🟢 同步游標由 C# mirror daemon 以 ts_high + per-webhook 管理（_tavern_state.json rooms.<room>.webhooks）。下列「套用 seq / 追平」已串接 daemon（把 seq 邊界翻成 ts_high 重設全房 webhook）。", WrapLabelStyle);

            // 延後動作（IMGUI 陷阱修正）：套用 seq / 追平會呼 LoadData() 重建 m_WatchedRooms，
            // 若在下方 foreach 內同步執行 = 列舉中改集合 → InvalidOperationException（Tim 2026-07-21 實測踩到）。
            // 仿 DrawWebhookPanel 的 deleteUrl 模式：迴圈只「記下要對哪房套哪個 seq」，跑完迴圈才執行。
            string applyRoom = null;
            int applyTargetSeq = 0;

            foreach (var room in m_WatchedRooms)
            {
                int maxSeq = m_RoomMaxSeq.GetValueOrDefault(room, 0);
                // 已同步/待同步：native 走 daemon 反推快取；python 走 last_seen_seq
                int lastSeen = m_RoomNativeSynced.GetValueOrDefault(room, 0);
                int pending = m_RoomNativePending.GetValueOrDefault(room, 0);
                bool capped = m_RoomNativeCapped.GetValueOrDefault(room, false);
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label($"<b>{room}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label($"已同步到 seq {lastSeen} / 房間最新 {maxSeq}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    // capped = 待同步反推觸上限 → 標「≥」不假裝精確（cross-layer 誠實）
                    string pendingLabel = pending == 0
                        ? "<color=#66ff66>✓ 已追平</color>"
                        : (capped ? $"<color=#ffcc44>待同步 ≥{pending} 筆</color>" : $"<color=#ffcc44>待同步 {pending} 筆</color>");
                    GUILayout.Label(pendingLabel, WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.FlexibleSpace();

                    m_SeqDraft[room] = GUILayout.TextField(m_SeqDraft.GetValueOrDefault(room, lastSeen.ToString()), UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    if (GUILayout.Button("套用 seq", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        // 只記下請求，離開迴圈才執行（避免列舉中 LoadData 改 m_WatchedRooms）
                        if (int.TryParse(m_SeqDraft.GetValueOrDefault(room, ""), out int newSeq) && newSeq >= 0)
                        {
                            applyRoom = room; applyTargetSeq = newSeq;
                        }
                        else Debug.LogWarning("[TavernAdmin] seq 需為非負整數");
                    }
                    // 一鍵追平 — 推到房間最新（跳過所有未同步）；已追平時 disable
                    using (new EditorGUI.DisabledScope(pending == 0))
                    {
                        if (GUILayout.Button("追平", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                        {
                            applyRoom = room; applyTargetSeq = maxSeq;
                        }
                    }
                    // 取消註冊 — 從 tavern_mirror watched rooms 移除（Tim 2026-07-21）；走 UCL_OptionPage 二次確認防誤按。
                    // Create 只推彈窗、不動 m_WatchedRooms（不會在 foreach 中改集合）；真正 unregister 在彈窗 callback（下一幀、迴圈外）跑。
                    if (GUILayout.Button("取消註冊", UCL_GUIStyle.GetButtonStyle(new Color(0.9f, 0.45f, 0.45f)), GUILayout.ExpandWidth(false)))
                    {
                        string r = room;
                        UCL.Core.Page.UCL_OptionPage.Create(
                            $"取消註冊房間「{r}」？",
                            "取消後此房訊息不再鏡像到 Discord（已送出的不受影響）。日後重新註冊會從當下起算、不回放歷史。",
                            new UCL.Core.Page.ButtonData("取消註冊", () => SetRoomWatched(r, false), UCL_GUIStyle.GetButtonStyle(Color.red)),
                            new UCL.Core.Page.ButtonData("取消"));
                    }
                }
            }
            GUILayout.Label("  ⚠ 套用 seq 屬管理員操作：往回調 = 該區間訊息會重發到 Discord；往前調 / 追平 = 跳過不發。native 模式下 seq 為近似位置（游標實為 ts 高水位，往回調精度以訊息 ts 為準）。", WrapLabelStyle);

            // ── 註冊新房同步（Tim 2026-07-21）— 下拉選「未 watched 的房」加入 tavern_mirror.rooms ──
            // 物理意義：省掉手改 config；候選 = 所有房間 dir ∖ 已 watched（LoadData 算好，見 m_UnregisteredRooms）。
            // 延後執行：按鈕只記 registerRoom，離開本方法前才 SetRoomWatched（→LoadData 重建集合，避開列舉衝突）。
            string registerRoom = null;
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("➕ 註冊新房同步", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                if (m_UnregisteredRooms.Count == 0)
                {
                    GUILayout.Label("(其餘房間都已註冊 / 無可註冊房)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    m_SelectedNewRoomIdx = UCL_GUILayout.PopupSearchCache(
                        Math.Clamp(m_SelectedNewRoomIdx, 0, m_UnregisteredRooms.Count - 1),
                        m_UnregisteredRooms, m_Dic, "MirrorNewRoomPicker");
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("註冊同步", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                    {
                        int idx = Math.Clamp(m_SelectedNewRoomIdx, 0, m_UnregisteredRooms.Count - 1);
                        registerRoom = m_UnregisteredRooms[idx];
                    }
                }
            }

            // 迴圈外執行延後動作（此時已離開 m_WatchedRooms 列舉，LoadData 重建集合安全）— 一幀至多一個按鈕觸發
            if (applyRoom != null)
            {
                ApplyRoomSeq(applyRoom, applyTargetSeq, m_RoomMaxSeq.GetValueOrDefault(applyRoom, 0));
            }
            else if (registerRoom != null)
            {
                SetRoomWatched(registerRoom, true);
                Debug.Log($"[TavernAdmin] 註冊新房進 mirror：{registerRoom}");
            }
        }

        // 區塊職責：註冊 / 反註冊房間進 tavern_mirror.rooms（#2 取消註冊 / #3 註冊新房共用）
        // 物理意義：走 WriteConfigRoot（read→mutate→原子寫→LoadData）改 notify_config.json 的 watched rooms list。
        //          register=true 且不存在 → 加入；register=false 且存在 → 重建移除（JsonData 陣列無 RemoveAt）。
        // 數值影響：新註冊房 daemon cursor 種子 ts_high=now（不回放歷史）；WriteConfigRoot 內部會 LoadData 刷新 UI。
        void SetRoomWatched(string roomId, bool watched)
        {
            if (string.IsNullOrEmpty(roomId)) return;
            WriteConfigRoot(cfg =>
            {
                if (!cfg.Contains("tavern_mirror")) cfg["tavern_mirror"] = JsonData.ParseJson("{}");
                var tm = cfg["tavern_mirror"];
                if (!tm.Contains("rooms") || tm["rooms"] == null || !tm["rooms"].IsArray)
                    tm["rooms"] = JsonData.ParseJson("[]");
                var rooms = tm["rooms"];

                bool present = false;
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (rooms[i].GetString() == roomId) { present = true; break; }
                }
                if (watched && !present)
                {
                    rooms.Add(new JsonData(roomId));
                }
                else if (!watched && present)
                {
                    // JsonData 陣列無 RemoveAt → 重建保序（保留其餘房）
                    var kept = JsonData.ParseJson("[]");
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        string r = rooms[i].GetString();
                        if (r != roomId) kept.Add(new JsonData(r));
                    }
                    tm["rooms"] = kept;
                }
            });
            Debug.Log($"[TavernAdmin] tavern_mirror.rooms {(watched ? "註冊" : "取消註冊")} → {roomId}");
        }

        // 區塊職責：套用 seq / 追平的實際寫入 — 依 owner 分流（native → daemon 游標重設；python → last_seen_seq）
        // 物理意義：native 呼叫 UCL_DiscordMirrorDaemon.AdminSetRoomCursorToSeq（seq→ts_high+seen 重設全房 webhook），
        //          回狀態字串 log；python 維持舊 WriteStateField 直改 rooms.<room>.last_seen_seq。
        // 數值影響：daemon 端已 Save 落 disk，故此處補呼 LoadData 讓 UI 進度快取對齊磁碟真相。
        void ApplyRoomSeq(string room, int targetSeq, int maxSeq)
        {
            string result = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.AdminSetRoomCursorToSeq(room, targetSeq, maxSeq);
            Debug.Log($"[TavernAdmin] 套用 seq：{result}");
            LoadData();   // daemon 端已 Save，重載讓進度快取對齊磁碟真相
        }

        /// <summary>對指定 stream 的 webhook_urls 做受控寫入（增/刪）。</summary>
        void WriteStreamWebhooks(string iKey, Action<JsonData> mutateBlock)
        {
            WriteConfigRoot(cfg =>
            {
                JsonData block;
                if (iKey == "queue-idle") block = cfg;
                else if (iKey == "quest_routing")
                {
                    if (!cfg.Contains("tavern_mirror")) cfg["tavern_mirror"] = JsonData.ParseJson("{}");
                    var tm = cfg["tavern_mirror"];
                    if (!tm.Contains("quest_routing")) tm["quest_routing"] = JsonData.ParseJson("{}");
                    block = tm["quest_routing"];
                }
                else
                {
                    if (!cfg.Contains(iKey)) cfg[iKey] = JsonData.ParseJson("{}");
                    block = cfg[iKey];
                }
                if (!block.Contains("webhook_urls")) block["webhook_urls"] = JsonData.ParseJson("[]");
                mutateBlock(block);
            });
        }

        // ===========================================================
        // 區塊：stream 新增 / 刪除（Tim 2026-07-28 要求）
        // 物理意義：一條 stream = notify_config 根層一個帶 webhook_* 欄的物件。新增只建空殼
        //          （enabled=false + 空 webhook_urls），URL 仍走下方「驗證並新增」逐條入庫。
        // 數值影響：新增/刪除都是 WriteConfigRoot 原子落檔 + LoadData 重建清單。
        // 邊界：① core stream（有 C# 消費者：tavern_mirror / treasury_mirror / tavern_inbound /
        //          quest_routing / queue-idle）**不給刪** —— 誤刪會靜默停掉鏡像，這是最貴的失敗。
        //      ② 刪除走二段確認（第一下 arm、第二下才真刪），避免手滑。
        //      ③ key 需為合法 JSON key：非空、無空白、不與既有重複。
        // ===========================================================
        void DrawStreamAddRemoveRow(string iSelectedKey)
        {
            bool isCore = s_CoreStreamKeys.Contains(iSelectedKey);
            using (new GUILayout.HorizontalScope())
            {
                // ── 刪除當前選中 stream ──
                if (isCore)
                {
                    GUILayout.Label("  🔒 core stream（有 C# 消費者）不可刪除 — 只能改內容或關 enabled",
                        WrapLabelStyle, GUILayout.ExpandWidth(false));
                }
                else if (m_PendingDeleteStream == iSelectedKey)
                {
                    if (GUILayout.Button($"⚠ 再按一次確認刪除「{iSelectedKey}」",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        string k = iSelectedKey;
                        WriteConfigRoot(cfg => { if (cfg.Contains(k)) cfg.Remove(k); });
                        Debug.Log($"[TavernAdmin] 已刪除 stream「{k}」（config 區塊整塊移除）");
                        m_PendingDeleteStream = null;
                        m_SelectedStreamIdx = 0;
                        LoadData();
                    }
                    if (GUILayout.Button("取消", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        m_PendingDeleteStream = null;
                }
                else
                {
                    if (GUILayout.Button($"🗑 刪除 stream「{iSelectedKey}」", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                        m_PendingDeleteStream = iSelectedKey;
                }
                GUILayout.FlexibleSpace();
            }
            // ── 新增 stream ──
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("新增 stream", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                bool add = GUILayout.Button("＋ 建立", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false));
                m_NewStreamKey = GUILayout.TextField(m_NewStreamKey ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                GUILayout.Label("(key 如 my_channel_mirror；建空殼後再加 URL)", UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
                if (add)
                {
                    string nk = (m_NewStreamKey ?? "").Trim();
                    if (string.IsNullOrEmpty(nk) || nk.IndexOf(' ') >= 0)
                    {
                        Debug.LogWarning("[TavernAdmin] stream key 不可為空或含空白");
                    }
                    else if (m_StreamKeys.Contains(nk))
                    {
                        Debug.LogWarning($"[TavernAdmin] stream「{nk}」已存在");
                    }
                    else
                    {
                        WriteConfigRoot(cfg =>
                        {
                            var blk = JsonData.ParseJson("{}");
                            blk["enabled"] = new JsonData(false);
                            blk["webhook_urls"] = JsonData.ParseJson("[]");
                            cfg[nk] = blk;
                        });
                        Debug.Log($"[TavernAdmin] 已建立 stream「{nk}」（enabled=false + 空 webhook_urls；下一步加 URL）");
                        m_NewStreamKey = "";
                        LoadData();
                        int idx = m_StreamKeys.IndexOf(nk);
                        if (idx >= 0) m_SelectedStreamIdx = idx;
                    }
                }
            }
        }

        void DrawWebhookPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "WebhookFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🔗 Webhook 設定</b>（來源優先序 ENV > secret file > config；本頁操作 config 列表）", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_StreamKeys.Count == 0) RebuildStreamList();

                // Stream 下拉（仿 Persona panel 交互）+ 增刪
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Stream", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_SelectedStreamIdx = UCL_GUILayout.PopupSearchCache(m_SelectedStreamIdx, m_StreamLabels, m_Dic, "WebhookStreamPicker");
                }
                string key = m_StreamKeys.Count > 0
                    ? m_StreamKeys[Math.Clamp(m_SelectedStreamIdx, 0, m_StreamKeys.Count - 1)] : "tavern_mirror";
                var block = GetStreamBlock(m_Config, key);

                DrawStreamAddRemoveRow(key);

                // 純文字同步狀態（未同步資訊）
                foreach (var line in BuildStreamInfoLines(key, block))
                {
                    GUILayout.Label($"  {line}", WrapLabelStyle);
                }

                // tavern_mirror 專屬：per-room 同步進度 + 套用 seq（Tim 2026-07-16 整合進下拉）
                if (key == "tavern_mirror")
                {
                    DrawTavernRoomRows();
                }

                // 來源鏈狀態
                string envVar = ""; string secretFile = "";
                try { envVar = block?.GetString("webhook_env_var", "") ?? ""; secretFile = block?.GetString("webhook_file", "") ?? ""; } catch { }
                bool envSet = !string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar));
                bool fileExists = !string.IsNullOrEmpty(secretFile) && File.Exists(Path.Combine(PromptQueueDir, secretFile));
                GUILayout.Label($"  來源鏈：ENV {(envSet ? "<color=#66ff66>已設</color>" : "未設")}（{(string.IsNullOrEmpty(envVar) ? "-" : envVar)}） | secret file {(fileExists ? "<color=#66ff66>存在</color>" : "不存在")}（{(string.IsNullOrEmpty(secretFile) ? "-" : secretFile)}）", WrapLabelStyle);

                // URL 遮罩列表 + 驗證 / 刪除
                var urls = new List<string>();
                try
                {
                    if (block != null && block.Contains("webhook_urls") && block["webhook_urls"].IsArray)
                    {
                        for (int i = 0; i < block["webhook_urls"].Count; i++) urls.Add(block["webhook_urls"][i].GetString());
                    }
                }
                catch { }
                if (urls.Count == 0) GUILayout.Label("  (config 無 webhook URL)", UCL_GUIStyle.LabelStyle);

                string deleteUrl = null;
                foreach (var url in urls)
                {
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        GUILayout.Label(MaskWebhook(url), WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)));
                        GUILayout.Label(m_WebhookProbe.GetValueOrDefault(url, "(未驗證)"), WrapLabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("🩺 驗證", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_WebhookProbe[url] = ProbeWebhook(url);
                        }
                        if (GUILayout.Button("刪除", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                        {
                            deleteUrl = url;
                        }
                    }
                }
                if (deleteUrl != null)
                {
                    string k = key; string du = deleteUrl;
                    // JsonData.Remove 只支援 Dictionary — list 刪除走重建（保留其餘順序）
                    WriteStreamWebhooks(k, b =>
                    {
                        var arr = b["webhook_urls"];
                        var kept = JsonData.ParseJson("[]");
                        for (int i = 0; i < arr.Count; i++)
                        {
                            if (arr[i].GetString() != du) kept.Add(new JsonData(arr[i].GetString()));
                        }
                        b["webhook_urls"] = kept;
                    });
                    Debug.Log($"[TavernAdmin] {key}.webhook_urls 移除一條（{MaskWebhook(deleteUrl)}）");
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("🩺 全部驗證", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        foreach (var url in urls) m_WebhookProbe[url] = ProbeWebhook(url);
                    }
                    GUILayout.FlexibleSpace();
                }

                // 新增列 — 一律先驗證通過才入庫（擋貼錯 URL）
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("新增 URL", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    bool addURL = GUILayout.Button("驗證並新增", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false));
                    m_NewWebhookUrl = GUILayout.TextField(m_NewWebhookUrl ?? "", UCL_GUIStyle.TextFieldStyle);
                    //using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_NewWebhookUrl)))
                    {
                        if (addURL)
                        {
                            string url = m_NewWebhookUrl.Trim();
                            string probe = ProbeWebhook(url);
                            m_WebhookProbe[url] = probe;
                            if (probe.Contains("✓"))
                            {
                                if (urls.Contains(url)) Debug.LogWarning("[TavernAdmin] 該 URL 已在列表中");
                                else
                                {
                                    WriteStreamWebhooks(key, b => b["webhook_urls"].Add(new JsonData(url)));
                                    Debug.Log($"[TavernAdmin] {key}.webhook_urls 新增（{MaskWebhook(url)}，驗證 {probe}）");
                                }
                                m_NewWebhookUrl = "";
                            }
                            else Debug.LogWarning($"[TavernAdmin] 驗證失敗不入庫：{probe}");
                        }
                    }
                }
                GUILayout.Label("提示：列表永遠遮罩（只露 webhook id）— 截圖安全；驗證會顯示 Discord 端頻道名。category_routing 走 UCL_TavernCategoryRoutingAsset（asset 自有編輯面），不在本 panel。", WrapLabelStyle);
            }
        }



        // ===========================================================
        // 區塊：Discord → 酒館 Inbound 設定（Tim 2026-07-28 拍板：inbound 設定收進本後台）
        // 物理意義：inbound 是 outbound mirror 的對偶方向 — Discord 頻道訊息中繼進酒館。三份資料：
        //          ① notify_config.json tavern_inbound.enabled（總開關，Mirror panel 的總開關已連動寫它）
        //          ② ChatTavern/discord_channel_routing.json mappings（channel → room 對照，CRUD 在
        //             UCL_DiscordChannelRoutingPage，本 panel 只做唯讀摘要 + 跳轉）
        //          ③ _secrets/discord_bot_token（bot token，安裝/解密走 UCL_SecretManagerPage）
        // 數值影響：本 panel 不改 routing / 不碰 secret 明文，只讀狀態 + 導流到對應專頁，
        //          避免同一份資料兩處可寫造成分歧（single source of truth 原則）。
        // 邊界：inbound 中繼器 2026-07-28 起由 C# UCL_DiscordInboundDaemon（in-process REST 輪詢）接管；
        //      本 panel 直接讀它的兩道閘門（EditorPrefs Enabled × config tavern_inbound.enabled）與
        //      中繼統計 / 最近錯誤，另偵測遺留 python bot 以警示遷移期雙寫。
        // ===========================================================

        // secret 檔名（不含副檔名）— .enc 為加密本體、.txt 為安裝後明文（bot / 未來 native 讀它）
        const string InboundTokenSecretStem = "discord_bot_token";

        void DrawInboundPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                bool prefOn = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.Enabled;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "InboundFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>📥 Discord → 酒館 Inbound</b>", WrapLabelStyle);

                    // native daemon 開關（EditorPrefs, per-machine）
                    if (GUILayout.Button(prefOn ? "⏹ 停用 native inbound" : "▶ 啟用 native inbound",
                            UCL_GUIStyle.GetButtonStyle(prefOn ? new Color(0.9f, 0.5f, 0.4f) : new Color(0.4f, 0.8f, 0.5f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.Enabled = !prefOn;
                        LoadData();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                // ── ① 總開關狀態（唯讀顯示；寫入走上方 Mirror panel 的總開關，避免兩處可寫）──
                bool inboundEnabled = false;
                string botStatus = "(未知)";
                try
                {
                    var ib = (m_Config != null && m_Config.Contains("tavern_inbound")) ? m_Config["tavern_inbound"] : null;
                    if (ib != null)
                    {
                        inboundEnabled = ib.GetBool("enabled", false);
                        botStatus = ib.GetString("bot_status", "(未知)");
                    }
                }
                catch { /* 缺欄位視為 off */ }
                GUILayout.Label(inboundEnabled
                    ? $"  設定開關：<color=#66ff66>● enabled</color>（config bot_status={botStatus}）"
                    : $"  設定開關：<color=#ff8866>○ disabled</color>（config bot_status={botStatus}）", WrapLabelStyle);
                GUILayout.Label("  ↳ 開關寫入走上方「Discord 同步」總開關（一次寫 tavern_mirror / treasury_mirror / tavern_inbound）。", WrapLabelStyle);

                // ── ② 中繼器實際狀態 — 誠實顯示「有沒有東西在跑」，不靠 config 猜 ──
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"  中繼器：{m_InboundRelayStatus}", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🔄 立即檢查一輪", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))//手動立即輪詢一輪
                    {
                        UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.ForcePoll();
                        Debug.Log("[TavernAdmin] 手動觸發 inbound daemon ForcePoll（輪一個頻道；多頻道請連按或等 round-robin）");
                    }
                }

                // ── ③ channel → room 對照摘要（唯讀）+ 跳轉專頁 CRUD ──
                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"  頻道對照：{m_InboundRoutingSummary}", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🔀 開啟頻道路由設定", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_DiscordChannelRoutingPage.Create();
                    }
                }
                foreach (var row in m_InboundRoutingRows) GUILayout.Label($"    {row}", WrapLabelStyle);

                // ── ④ Bot token secret 狀態 + 跳轉 Secret Manager ──
                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"  Bot token：{m_InboundTokenStatus}", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🔑 開啟 Secret Manager", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        OpenSecretManagerPage();
                    }
                }
                GUILayout.Label("  ↳ token 明文只由 Secret Manager 以 passphrase 解出（本頁不顯示、不寫入明文）。native daemon 讀同一份 .txt。", WrapLabelStyle);
            }
        }

        // 區塊職責：inbound 中繼器狀態 — native daemon 為主、外部 python bot 為輔
        // 物理意義：2026-07-28 起 inbound 由 C# UCL_DiscordInboundDaemon（in-process REST 輪詢）接管。
        //          native 是 in-process，不會出現在 Process 註冊中心 → 直接讀 daemon 自己的狀態欄位；
        //          註冊中心那條僅用來偵測「遺留的外部 python bot 還在跑」（遷移期雙寫警示）。
        // 數值影響：LoadData 時取一次快照；顯示兩個閘門（EditorPrefs Enabled × config enabled）與
        //          本 session 中繼筆數 / 最近錯誤，讓靜默失敗看得見。
        const string InboundRelayProcTag = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.RelayTag;

        static string ScanInboundRelayStatus()
        {
            var D = typeof(UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon);
            _ = D;   // 型別引用僅為讓 asmdef 依賴顯式化（同組件，無循環風險）
            try
            {
                bool prefOn = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.Enabled;
                bool cfgOn = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.ConfigEnabled;
                bool live = prefOn && cfgOn;
                int routes = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.ActiveRouteCount;
                bool token = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.HasToken;
                int relayed = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.RelayedThisSession;
                string err = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.LastError;
                string lastPoll = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.LastPollUtc;

                string head;
                if (live && token)
                    head = $"<color=#66ff66>● native 運行中</color>（輪詢 {routes} 頻道，本 session 已中繼 {relayed} 筆"
                           + (string.IsNullOrEmpty(lastPoll) ? "，尚未完成第一輪" : $"，最近輪詢 {lastPoll}") + "）";
                else if (live && !token)
                    head = "<color=#ffcc44>◐ 已啟用但 token 未就緒</color> — 輪詢無法進行，請安裝 bot token";
                else if (!prefOn && cfgOn)
                    head = "<color=#ff8866>○ native daemon 未啟用</color>（config 意圖為 on）— 選單 UCL/Discord Mirror/Toggle Inbound Daemon";
                else if (prefOn && !cfgOn)
                    head = "<color=#ff8866>○ config tavern_inbound.enabled = false</color>（daemon 已啟用但被 config 關）";
                else
                    head = "<color=#ff8866>○ 未啟用</color>（daemon 開關與 config 皆為 off）";

                // 遺留 python bot 偵測 — 遷移期若兩者同時在跑會雙寫同一批訊息
                string legacy = "";
                try
                {
                    foreach (var (rec, status) in UCL_ProcessRegistryService.LoadAllWithStatus())
                    {
                        if (rec == null || rec.tag != InboundRelayProcTag) continue;
                        if (status == UCL_ProcessStatus.Alive)
                        {
                            legacy = $"　⚠ <color=#ffcc44>另偵測到外部 python bot 存活（pid={rec.pid}）— 兩者同時跑會雙寫，請收掉其一</color>";
                            break;
                        }
                    }
                }
                catch { /* 註冊中心查詢失敗不影響主狀態 */ }

                string errLine = string.IsNullOrEmpty(err) ? "" : $"　⛔ 最近錯誤：{err}";
                return head + legacy + errLine;
            }
            catch (Exception e)
            {
                return $"(狀態查詢失敗：{e.Message})";
            }
        }

        // 區塊職責：channel → room 路由摘要（唯讀）— CRUD 歸 UCL_DiscordChannelRoutingPage，本頁只顯示
        // 物理意義：discord_channel_routing.json 是 single source of truth（bot 啟動時讀）；
        //          本摘要讓 Tim 在後台一眼看見「幾條啟用 / 對到哪些房」，不必切頁。
        // 數值影響：只讀不寫；enabled=false 的列也顯示（標示停用），避免「設了卻沒生效」被藏起來。
        void ScanInboundRouting()
        {
            m_InboundRoutingRows.Clear();
            try
            {
                string path = Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "discord_channel_routing.json");
                if (!File.Exists(path))
                {
                    m_InboundRoutingSummary = "(discord_channel_routing.json 不存在)";
                    return;
                }
                var jd = JsonData.ParseJson(File.ReadAllText(path));
                if (jd == null || !jd.Contains("mappings") || !jd["mappings"].IsArray)
                {
                    m_InboundRoutingSummary = "(mappings 欄缺失或格式非陣列)";
                    return;
                }
                var maps = jd["mappings"];
                int total = maps.Count, on = 0;
                for (int i = 0; i < total; i++)
                {
                    var m = maps[i];
                    if (m == null) continue;
                    bool en = m.GetBool("enabled", false);
                    if (en) on++;
                    string label = m.GetString("label", "");
                    string room = m.GetString("tavern_room", "?");
                    string cls = m.GetString("source_class", "");
                    int prio = m.GetInt("priority", 0);
                    string chId = m.GetString("channel_id", "?");
                    // channel id 是公開識別碼（非 secret），但只露尾 6 碼夠辨識又不佔版面
                    string chShort = chId.Length > 6 ? "…" + chId.Substring(chId.Length - 6) : chId;
                    m_InboundRoutingRows.Add(en
                        ? $"<color=#66ff66>●</color> {label} (ch {chShort}) → <b>{room}</b>  [{cls}/p{prio}]"
                        : $"<color=#888888>○ {label} (ch {chShort}) → {room}  [停用]</color>");
                }
                m_InboundRoutingSummary = $"{on} 條啟用 / 共 {total} 條";
            }
            catch (Exception e)
            {
                m_InboundRoutingSummary = $"(讀取失敗：{e.Message})";
            }
        }

        // 區塊職責：bot token secret 狀態 — 只看檔案存在性（.enc 入庫了沒 / .txt 安裝了沒）
        // 物理意義：.enc = 加密本體（可 commit）；.txt = 安裝後明文（gitignored，中繼器實際讀它）。
        //          本頁只報「有沒有 / 裝了沒」，絕不顯示或寫入明文內容。
        // 設計取捨（assembly 邊界）：UCL_SecretScanner / UCL_SecretCrypto 住 UCL_CoreEditor，而該 asmdef
        //          references UCL_Core（本檔所在）→ 反向直接引用會造成循環依賴。metadata（label/hint）
        //          因此不在本頁顯示，需要就按鈕跳 Secret Manager 看；存在性用 File.Exists 判定即足夠誠實。
        // 數值影響：.txt 缺席即中繼器起不來 → 提示去 Secret Manager 安裝（本 panel 有跳轉鈕）。
        static string ScanInboundTokenStatus()
        {
            try
            {
                string dir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_secrets");
                string enc = Path.Combine(dir, InboundTokenSecretStem + ".enc");
                string txt = Path.Combine(dir, InboundTokenSecretStem + ".txt");
                bool hasEnc = File.Exists(enc), hasTxt = File.Exists(txt);
                if (hasTxt)
                    return $"<color=#66ff66>● 已安裝</color>（明文 {InboundTokenSecretStem}.txt 就緒{(hasEnc ? "，加密本體在庫" : "，⚠ 無 .enc 加密本體")}）";
                if (hasEnc)
                    return $"<color=#ff8866>○ 未安裝</color> — 已有 {InboundTokenSecretStem}.enc 但缺明文，中繼器起不來；請以 passphrase 安裝";
                return $"<color=#ff8866>○ 找不到 {InboundTokenSecretStem}.enc</color> — 尚未入庫任何 bot token";
            }
            catch (Exception e)
            {
                return $"(secret 狀態查詢失敗：{e.Message})";
            }
        }

        // 區塊職責：跨 assembly 開啟 Secret Manager 頁（UCL_CoreEditor → 本組件無法直接 reference）
        // 物理意義：以型別全名在已載入 assembly 中反射找 UCL_SecretManagerPage 並呼其 static Create()。
        // 數值影響：找不到（下游專案未含 SecretManager 模組）→ 印 warning 不拋例外，按鈕變 no-op。
        static void OpenSecretManagerPage()
        {
            const string typeName = "UCL.Core.EditorLib.SecretManager.UCL_SecretManagerPage";
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType(typeName); } catch { continue; }
                    if (t != null) break;
                }
                if (t == null)
                {
                    Debug.LogWarning($"[TavernAdmin] 找不到 {typeName} — 本專案未含 SecretManager 模組？");
                    return;
                }
                var mi = t.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (mi == null)
                {
                    Debug.LogWarning($"[TavernAdmin] {typeName} 無 public static Create()");
                    return;
                }
                mi.Invoke(null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernAdmin] 開啟 Secret Manager 失敗：{e.Message}");
            }
        }

        // ===========================================================
        // 區塊：Persona 頭像 Override 管理（下拉選取模式，Tim 2026-07-15 修訂）
        // 物理意義：tavern_mirror.persona_avatar_overrides 的 UI 前端 — key=sender_persona,
        //          value=任意圖片 URL（最高優先級、不做 HEAD 預檢，見 Discord_Tavern_Mirror.md §3）。
        //          下拉選 persona → URL 欄自動帶入現有設定（無 = 空）→ 修改後按套用寫回。
        // 數值影響：套用（URL 非空）= 寫入/更新；清除 = 移除該 persona 的 override（回退 fallback 鏈）。
        //          寫回即時 flush，下一筆該 persona 的 post 生效。
        // ===========================================================
        void DrawAvatarOverridePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "AvatarFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🎭 Persona 頭像 Override</b>（key=sender_persona；最高優先級，直接釘任意外部 URL）", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_PersonaNames.Count == 0)
                {
                    GUILayout.Label("(PersonaCard asset 無任何 persona ID — 檢查 UCL_ChatTavernPersonaCardAsset)", UCL_GUIStyle.LabelStyle);
                    return;
                }

                // Persona 下拉（● 前綴 = 該 persona 已有 override）— 切換即重帶 URL draft
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    int newIdx = UCL_GUILayout.PopupSearchCache(m_SelectedPersonaIdx, m_PersonaOptions, m_Dic, "AvatarPersonaPicker");
                    if (newIdx != m_SelectedPersonaIdx)
                    {
                        m_SelectedPersonaIdx = newIdx;
                        SyncUrlDraftFromSelection();
                        GUI.FocusControl(null);   // 清 TextField focus，避免舊輸入殘留蓋掉新帶入值
                    }
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("URL", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_SelectedUrlDraft = GUILayout.TextField(m_SelectedUrlDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    string p = SelectedPersona;
                    bool hasOverride = p != null && m_AvatarOverrides.Any(kv => kv.Key == p);
                    string url = (m_SelectedUrlDraft ?? "").Trim();

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(url)))
                    {
                        if (GUILayout.Button("✔ 套用", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                        {
                            WriteConfigOverride(po => po[p] = url);
                            Debug.Log($"[TavernAdmin] persona_avatar_overrides[{p}] → {url}");
                        }
                        if (GUILayout.Button("開啟 URL", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            Application.OpenURL(url);
                        }
                    }
                    using (new EditorGUI.DisabledScope(!hasOverride))
                    {
                        if (GUILayout.Button("🗑 清除設定", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                        {
                            WriteConfigOverride(po => po.Remove(p));
                            Debug.Log($"[TavernAdmin] persona_avatar_overrides[{p}] 已清除（回退 sprite 派生 / agent-level fallback 鏈）");
                        }
                    }
                    GUILayout.Label(hasOverride ? "<color=#66ff66>● 已設定</color>" : "<color=#999999>○ 未設定（走 fallback 鏈）</color>", WrapLabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }

                // 現有 override 總覽（唯讀）— 操作走上方下拉
                if (m_AvatarOverrides.Count > 0)
                {
                    GUILayout.Label($"<b>現有 override（{m_AvatarOverrides.Count}）</b>：", WrapLabelStyle);
                    foreach (var kv in m_AvatarOverrides)
                    {
                        GUILayout.Label($"  ● <b>{kv.Key}</b> → {kv.Value}", WrapLabelStyle);
                    }
                }
                GUILayout.Label("提示：URL 顯式設定不做 HEAD 預檢 — 壞連結 Discord 端會 silent fallback 預設頭像；Discord 對同名 webhook 頭像有快取，換圖後可能要再發一筆訊息才刷新。", WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：底層檔案捷徑
        // ===========================================================
        void DrawFilesPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "FilesFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🗂 底層檔案</b>", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                DrawFileRow("notify_config.json（mirror/頭像/路由設定）", NotifyConfigPath);
                DrawFileRow("_tavern_state.json（同步進度 state）", TavernStatePath);
                DrawFileRow("_drain.log（同步結果 log）", DrainLogPath);
            }
        }

        void DrawFileRow(string label, string path)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"{label}：{path}", WrapLabelStyle);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!File.Exists(path)))
                {
                    if (GUILayout.Button("📂 開啟", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EditorUtility.RevealInFinder(path);
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：受控寫入 helpers
        // 物理意義：整份 JSON 讀入 JsonData → mutate → ToJsonBeautify 原子寫回（tmp+replace）。
        //          Python 端 json.loads 讀回無礙；寫完立即 LoadData 讓 UI 對齊磁碟真相。
        // 數值影響：config 寫回會統一成 JsonData 的 beautify 格式（縮排形式與 Python indent=2 略異，
        //          內容等價）；state 同理。
        // ===========================================================
        void WriteConfigOverride(Action<JsonData> mutateOverrides)
        {
            WriteTavernMirrorField(tm =>
            {
                if (!tm.Contains("persona_avatar_overrides")) tm["persona_avatar_overrides"] = JsonData.ParseJson("{}");
                mutateOverrides(tm["persona_avatar_overrides"]);
            });
        }

        // 區塊職責：tavern_mirror 塊的泛用受控寫入 — avatar override 等子欄位共用
        void WriteTavernMirrorField(Action<JsonData> mutateTavernMirror)
        {
            WriteConfigRoot(cfg =>
            {
                if (!cfg.Contains("tavern_mirror")) cfg["tavern_mirror"] = JsonData.ParseJson("{}");
                mutateTavernMirror(cfg["tavern_mirror"]);
            });
        }

        // 區塊職責：notify_config.json 整份的泛用受控寫入 — 總開關（跨多 stream 塊）/ 子塊寫入共用底座
        void WriteConfigRoot(Action<JsonData> mutateConfig)
        {
            try
            {
                if (!File.Exists(NotifyConfigPath)) { Debug.LogWarning("[TavernAdmin] notify_config.json 不存在"); return; }
                var cfg = JsonData.ParseJson(File.ReadAllText(NotifyConfigPath));
                mutateConfig(cfg);
                AtomicWrite(NotifyConfigPath, cfg.ToJsonBeautify());
                LoadData();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernAdmin] config write fail: {e.Message}");
            }
        }

        void WriteStateField(Action<JsonData> mutateState)
        {
            try
            {
                var state = File.Exists(TavernStatePath)
                    ? JsonData.ParseJson(File.ReadAllText(TavernStatePath))
                    : JsonData.ParseJson("{\"rooms\":{},\"consecutive_failures\":0}");
                mutateState(state);
                AtomicWrite(TavernStatePath, state.ToJsonBeautify());
                LoadData();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernAdmin] state write fail: {e.Message}");
            }
        }

        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
#endif
