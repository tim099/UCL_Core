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
        Dictionary<string, string> m_SeqDraft = new Dictionary<string, string>();  // last_seen_seq 編輯 draft
        List<KeyValuePair<string, string>> m_AvatarOverrides = new List<KeyValuePair<string, string>>();
        List<string> m_LogTail = new List<string>();
        bool m_Loaded = false;

        // ==== Webhook 設定 — 下拉選取模式（Tim 2026-07-16 拍板，交互仿 Persona 頭像 panel）====
        // 區塊職責：六個 webhook 消費端（全走統一 schema：webhook_urls / webhook_file / webhook_env_var，
        //          優先序 ENV > FILE > CONFIG）的觀看與操作 — 下拉選 stream → 純文字顯示該 stream 的
        //          同步狀態（未同步筆數 / cursor / 失敗計數）+ URL 遮罩列表 + 驗證/刪除/新增。
        // 物理意義：webhook URL 是 secret（拿到即可對頻道發言）且 Tim 常截圖本頁 — 列表永遠遮罩
        //          （只露 webhook id，token 全隱），驗證用 GET 取回 Discord 端頻道名顯示健康度。
        // 數值影響：增刪走 WriteConfigRoot 原子落檔（照現行慣例寫 config，Q1 Tim 拍板行為不變）；
        //          新增一律先驗證通過才入庫（擋貼錯 URL）。
        static readonly string[] s_WebhookStreamKeys =
            { "tavern_mirror", "treasury_mirror", "wake_notify", "queue-idle", "tavern_inbound", "quest_routing" };
        static readonly string[] s_WebhookStreamLabels =
        {
            "tavern_mirror（酒館訊息 → Discord）",
            "treasury_mirror（記帳 embed → Discord）",
            "wake_notify（喚醒通知 → Discord）",
            "queue-idle（頂層 — queue 完工通知）",
            "tavern_inbound（Discord → 酒館 inbound）",
            "quest_routing（task lifecycle 分流）",
        };
        int m_SelectedStreamIdx = 0;
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
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();  // PopupSearchCache cache

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

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
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
            m_AvatarOverrides.Clear(); m_LogTail.Clear();
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
                            m_SeqDraft[room] = GetLastSeen(room).ToString();
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
                m_Dic.Clear();   // 下拉選項變了 → 清 PopupSearchCache

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
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernAdmin] load fail: {e.Message}");
            }
        }

        /// <summary>當前下拉選中的 persona 名（清單空回 null）。</summary>
        string SelectedPersona =>
            (m_PersonaNames.Count > 0 && m_SelectedPersonaIdx >= 0 && m_SelectedPersonaIdx < m_PersonaNames.Count)
                ? m_PersonaNames[m_SelectedPersonaIdx] : null;

        // 區塊職責：把 URL draft 同步成「當前選中 persona 的現有 override」— 無設定則為空字串
        void SyncUrlDraftFromSelection()
        {
            string p = SelectedPersona;
            m_SelectedUrlDraft = p == null ? "" : (m_AvatarOverrides.FirstOrDefault(kv => kv.Key == p).Value ?? "");
        }

        int GetLastSeen(string room)
        {
            try
            {
                if (m_State != null && m_State.Contains("rooms") && m_State["rooms"].Contains(room))
                {
                    return m_State["rooms"][room].GetInt("last_seen_seq", 0);
                }
            }
            catch { /* 缺欄位視為 0 */ }
            return 0;
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            DrawMirrorStatePanel();
            GUILayout.Space(8);
            DrawAvatarOverridePanel();
            GUILayout.Space(8);
            DrawWebhookPanel();
            GUILayout.Space(8);
            DrawFilesPanel();
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
            // 區塊職責：T6.5 — native owner 下鎖住 position-seq 控件（native 用 ts_high + per-webhook 游標，
            //          不理 last_seen_seq；讓管理員照舊按「套用 seq/追平」= 編到 native 不讀的欄位 → 純誤導）
            // 數值影響：唯讀顯示照舊（python 舊 cursor 仍在檔內），互動列 disable + 常駐語意提示。
            //          控件的 ts_high/per-webhook 語意完整遷移跟 07-17 root-cause doc Phase 2 一起做，不卡 cutover。
            bool nativeOwner = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.IsNativeOwner;
            if (nativeOwner)
            {
                GUILayout.Label("  🔒 mirror_owner=native：同步游標由 C# daemon 以 ts_high + per-webhook 管理（_tavern_state.json rooms.<room>.webhooks），下列 seq 控件已停用（native 不讀 last_seen_seq）。", WrapLabelStyle);
            }
            foreach (var room in m_WatchedRooms)
            {
                int lastSeen = GetLastSeen(room);
                int maxSeq = m_RoomMaxSeq.GetValueOrDefault(room, 0);
                int pending = Math.Max(0, maxSeq - lastSeen);
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label($"<b>{room}</b>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label($"已同步到 seq {lastSeen} / 房間最新 {maxSeq}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(pending == 0 ? "<color=#66ff66>✓ 已追平</color>" : $"<color=#ffcc44>待同步 {pending} 筆</color>", WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.FlexibleSpace();
                    // T6.5：native owner 下整段 seq 互動列 disable（native 不讀 last_seen_seq）
                    using (new EditorGUI.DisabledScope(nativeOwner))
                    {
                    m_SeqDraft[room] = GUILayout.TextField(m_SeqDraft.GetValueOrDefault(room, lastSeen.ToString()), UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    if (GUILayout.Button("套用 seq", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (int.TryParse(m_SeqDraft.GetValueOrDefault(room, ""), out int newSeq) && newSeq >= 0)
                        {
                            string r = room;
                            WriteStateField(s =>
                            {
                                if (!s.Contains("rooms")) s["rooms"] = JsonData.ParseJson("{}");
                                if (!s["rooms"].Contains(r)) s["rooms"][r] = JsonData.ParseJson("{}");
                                s["rooms"][r]["last_seen_seq"] = newSeq;
                            });
                            Debug.Log($"[TavernAdmin] {r}.last_seen_seq → {newSeq}（設小=重放區間 / 設大=跳過區間）");
                        }
                        else Debug.LogWarning("[TavernAdmin] seq 需為非負整數");
                    }
                    // 一鍵追平 — 把 last_seen 推到房間最新（跳過所有未同步，重開同步前清積壓用）
                    using (new EditorGUI.DisabledScope(pending == 0))
                    {
                        if (GUILayout.Button("追平", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                        {
                            string r = room; int target = maxSeq;
                            WriteStateField(s =>
                            {
                                if (!s.Contains("rooms")) s["rooms"] = JsonData.ParseJson("{}");
                                if (!s["rooms"].Contains(r)) s["rooms"][r] = JsonData.ParseJson("{}");
                                s["rooms"][r]["last_seen_seq"] = target;
                            });
                            Debug.Log($"[TavernAdmin] {r}.last_seen_seq 追平 → {target}（未同步區間全部跳過不發）");
                        }
                    }
                    }   // T6.5 DisabledScope(nativeOwner) 收尾
                }
            }
            GUILayout.Label("  ⚠ 套用 seq 屬管理員操作：往回調 = 該區間訊息會重發到 Discord；往前調/追平 = 跳過不發。", WrapLabelStyle);
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

        void DrawWebhookPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>🔗 Webhook 設定</b>（來源優先序 ENV > secret file > config；本頁操作 config 列表）", WrapLabelStyle);

                // Stream 下拉（仿 Persona panel 交互）
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Stream", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_SelectedStreamIdx = UCL_GUILayout.PopupSearchCache(m_SelectedStreamIdx, s_WebhookStreamLabels.ToList(), m_Dic, "WebhookStreamPicker");
                }
                string key = s_WebhookStreamKeys[Math.Clamp(m_SelectedStreamIdx, 0, s_WebhookStreamKeys.Length - 1)];
                var block = GetStreamBlock(m_Config, key);

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
        // 區塊：Mirror 同步狀態
        // 物理意義：per-room「同步到哪一筆」可視化 — last_seen_seq（可編輯）vs 房間 max seq；
        //          差額 = 尚未推到 Discord 的筆數（0 = 已追平）。
        // 數值影響：套用 seq 直接改 _tavern_state.json；設小 = 重放區間訊息、設大 = 跳過區間訊息，
        //          屬管理員操作，前排顯示警語。
        // ===========================================================
        void DrawMirrorStatePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>📡 Discord Mirror 同步狀態</b>", WrapLabelStyle);

                bool enabled = false;
                try
                {
                    var tm = (m_Config != null && m_Config.Contains("tavern_mirror")) ? m_Config["tavern_mirror"] : null;
                    if (tm != null) enabled = tm.GetBool("enabled", false);
                }
                catch { /* 缺欄位視為 off */ }
                int failures = 0;
                try { if (m_State != null) failures = m_State.GetInt("consecutive_failures", 0); } catch { }

                using (new GUILayout.HorizontalScope())
                {
                    // Discord 同步總開關（Tim 2026-07-15 拍板：統一一個 toggle）— 一次寫五條 notify stream
                    // 的 enabled：tavern_mirror / treasury_mirror（記帳頻道，之前漏網的 bank 訊息就是它）/
                    // wake_notify / 頂層 enabled（queue-idle）/ tavern_inbound（Discord→酒館 inbound）。
                    // 顯示狀態以 tavern_mirror.enabled 為代表（master 寫入後五者同步）。
                    // 語意：預設 off、缺欄位視為 off（各 stream Python 端 get("enabled") falsy 同語意）。
                    // 邊界：tavern_inbound 由 daemon 啟動時讀 config — 切換後需從控制台重啟酒館系統才生效。
                    bool newEnabled = GUILayout.Toggle(enabled,
                        enabled ? " <color=#66ff66>● Discord 同步啟用中（按一下全部關閉）</color>" : " <color=#ff8866>○ Discord 同步已關閉（按一下全部啟用）</color>",
                        new GUIStyle(UCL_GUIStyle.ButtonStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    if (newEnabled != enabled)
                    {
                        WriteConfigRoot(cfg =>
                        {
                            // 頂層 enabled = queue-idle stream
                            cfg["enabled"] = newEnabled;
                            foreach (var block in new[] { "tavern_mirror", "treasury_mirror", "wake_notify", "tavern_inbound" })
                            {
                                if (!cfg.Contains(block)) cfg[block] = JsonData.ParseJson("{}");
                                cfg[block]["enabled"] = newEnabled;
                            }
                        });
                        Debug.Log($"[TavernAdmin] Discord 同步總開關 → {newEnabled}（tavern/treasury/wake/queue-idle/inbound 五 stream 同步寫入；inbound 需重啟酒館系統生效）");
                    }
                    GUILayout.Space(8);
                    GUILayout.Label($"連續失敗計數：{failures}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (failures > 0 && GUILayout.Button("歸零", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        WriteStateField(s => s["consecutive_failures"] = 0);
                    }
                    GUILayout.FlexibleSpace();
                    // 手動觸發一次 mirror run — T6.5：owner 分流。native → daemon.ForceTick（立即掃描送出）；
                    // python → IO 層 fire-and-forget spawn（T6.6 後 spawn 在 native owner 下也會跑 python，
                    // 但那條只剩 treasury 有意義 — tavern 的「立即同步」在 native 下走 ForceTick 才是真觸發）。
                    if (GUILayout.Button("▶ 立即觸發同步", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        if (UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.IsNativeOwner)
                        {
                            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.ForceTick();
                            Debug.Log("[TavernAdmin] 手動觸發 native daemon ForceTick（掃描 + 送出立即跑一輪）");
                        }
                        else
                        {
                            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernIO.TryFireDiscordTavernMirrorAsync();
                            Debug.Log("[TavernAdmin] 手動觸發 notify_discord.py --mode tavern（數秒後按 Refresh 看結果）");
                        }
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
                GUILayout.Label("<b>🎭 Persona 頭像 Override</b>（key=sender_persona；最高優先級，直接釘任意外部 URL）", WrapLabelStyle);

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
                GUILayout.Label("<b>🗂 底層檔案</b>", WrapLabelStyle);
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
