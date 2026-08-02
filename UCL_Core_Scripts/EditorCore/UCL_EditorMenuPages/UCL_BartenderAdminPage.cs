// 區塊職責：酒保管理頁 — 以單一後台集中管理報時、時間規則、關鍵字留言與 daemon 狀態。
// 物理意義：所有設定直接讀寫 UCL_BartenderIO 的既有資料檔，避免控制台、Cmd、daemon 各自維護一份規則。
// 數值影響：切換報時只改 announce-rules-* 規則的 enabled；不刪除規則，因此可逆且不會遺失已排定時間。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.AgentCommands.Bartender;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.JsonLib;
using UCL.Core.UI;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderAdminPage.md")]
    public class UCL_BartenderAdminPage : UCL_CommonEditorPage
    {
        const string ReportRulePrefix = "announce-rules-";
        const string DefaultRoom = "tavern";
        const string KeyDaemonFold = "BartenderDaemonFold";
        const string KeyTimeRulesFold = "BartenderTimeRulesFold";
        const string KeyTriggersFold = "BartenderTriggersFold";
        const string KeyStateFold = "BartenderStateFold";
        const string KeyRemoteWindowFold = "BartenderRemoteWindowFold";

        UCL_BartenderTriggerList m_Triggers = new UCL_BartenderTriggerList();
        UCL_BartenderTimeRuleList m_TimeRules = new UCL_BartenderTimeRuleList();
        UCL_BartenderState m_State = new UCL_BartenderState();
        string m_NewRuleId = "";
        string m_NewRuleTime = "09:00";
        string m_NewRuleTarget = "";
        string m_NewRuleMessage = "";
        string m_NewTriggerKeyword = "";
        string m_NewTriggerMessage = "";
        int m_NewTriggerTokens = 1;
        UCL_ActualAgent m_RemoteTestAgent = UCL_ActualAgent.Codex;
        readonly UCL_ObjectDictionary m_RemoteTestAgentPopupDic = new UCL_ObjectDictionary();
        // 區塊職責：persona 測試列的暫存選擇；清單本身每次繪製都重讀 lock 檔，不快取「誰在線」。
        // 物理意義：在線與否隨時會變（同事登出 / lock 過期），把它快取住等於拿舊快照當現況。
        // 數值影響：只保存「選了哪個名字」與「多重命中時選第幾個」，兩者都在清單變動時自動退回安全值。
        string m_RemoteTestPersona = "";
        readonly UCL_ObjectDictionary m_RemoteTestPersonaPopupDic = new UCL_ObjectDictionary();
        readonly UCL_PersonaLocateOptions m_LocateOptions = new UCL_PersonaLocateOptions();
        List<UCL_MonitorInfo> m_Monitors;
        Texture2D m_LocatePreview;
        string m_LocatePreviewError = "";
        string m_LocateConfigStatus = "";
        bool m_LocateRunning;
        // 區塊職責：保存各大區塊的展開偏好，供 UCL_GUILayout.Toggle 持久化讀寫。
        // 物理意義：折疊偏好與資料載入快取分離，Reload() 不會意外重置使用者剛選擇的展開狀態。
        // 數值影響：四個固定 key 各只保存一個 bool；首次開頁皆使用 false，避免管理頁載入即被長列表淹沒。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        public override string WindowName => "酒保管理";
        public override bool ShowInPageMenu => false;
        public static UCL_BartenderAdminPage Create() => UCL_EditorPage.Create<UCL_BartenderAdminPage>();

        public override void Init(UCL_GUIPageController iGUIPage)
        {
            base.Init(iGUIPage);
            Reload();
        }

        void Reload()
        {
            // 定位設定跨 session 存活 —— 讀不到就用預設值，不擋其他區塊載入。
            if (UCL_RemotePersonaLocateConfig.Load(m_LocateOptions, out string savedPersona)
                && !string.IsNullOrEmpty(savedPersona))
                m_RemoteTestPersona = savedPersona;
            m_Triggers = UCL_BartenderIO.LoadTriggers() ?? new UCL_BartenderTriggerList();
            m_TimeRules = UCL_BartenderIO.LoadTimeRules() ?? new UCL_BartenderTimeRuleList();
            m_State = UCL_BartenderIO.LoadState() ?? new UCL_BartenderState();
            m_Triggers.triggers ??= new List<UCL_BartenderTrigger>();
            m_TimeRules.rules ??= new List<UCL_BartenderTimeRule>();
            m_State.room_last_seq ??= new List<UCL_BartenderRoomSeq>();
        }

        // 區塊職責：繪製頁面內容；TopBar / HelpURL / Back / Close 與全頁 scroll 由 UCL_EditorPage.OnGUI 統一處理。
        // 物理意義：覆寫 ContentOnGUI 才能保留 UCL_CommonEditorPage 的標準導覽框架，避免子頁成為無法返回的孤島。
        // 數值影響：內容高度超出視窗時自動進入基底的 ScrollView，無須本頁另建第二個全頁捲軸。
        protected override void ContentOnGUI()
        {
            using (new GUILayout.VerticalScope())
            {
                GUILayout.Label("<b>🍺 酒保管理</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, fontSize = 18 });
                DrawDaemonSection();
                GUILayout.Space(6);
                DrawRemoteWindowSection();
                GUILayout.Space(6);
                DrawTimeRulesSection();
                GUILayout.Space(6);
                DrawTriggersSection();
                GUILayout.Space(6);
                DrawStateSection();
            }
        }

        // 區塊職責：提供遠端協作視窗控制的明確啟動與可觀察測試入口。
        // 物理意義：Enabled 與 pause 秒數皆為 static runtime state，不寫入規則檔或 PlayerPrefs；重開 Editor 必回關閉。
        // 數值影響：一般自動切換遇到 OS 輸入後會等待設定秒數；三顆測試按鈕只切已開啟的指定 IDE 視窗，從不輸入文字。
        void DrawRemoteWindowSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyRemoteWindowFold, 21, iDefaultValue: false);
                    GUILayout.Label("<b>🖥 遠端視窗協作</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    bool enabled = UCL_RemoteWindowControl.Enabled;
                    bool next = GUILayout.Toggle(enabled, enabled ? "● 本次已啟動" : "○ 本次未啟動", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (next != enabled) UCL_RemoteWindowControl.SetEnabled(next);
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                UCL_RemoteWindowControl.PauseOnUserInput = EditorGUILayout.ToggleLeft("偵測使用者操作後暫停（預設開啟）", UCL_RemoteWindowControl.PauseOnUserInput);
                UCL_RemoteWindowControl.UserIdlePauseSeconds = EditorGUILayout.IntField("使用者操作後暫停（秒）", UCL_RemoteWindowControl.UserIdlePauseSeconds);
                if (!UCL_RemoteWindowControl.PauseOnUserInput)
                    EditorGUILayout.HelpBox("暫停護欄目前已關閉：一般自動輪循不會因鍵鼠輸入讓出控制權。此設定只在本次 Editor session 有效。", MessageType.Warning);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("手動測試 Agent", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_RemoteTestAgent = UCL_GUILayout.PopupAuto(m_RemoteTestAgent, m_RemoteTestAgentPopupDic, "RemoteTestAgent", 6, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    GUI.enabled = UCL_RemoteWindowControl.Enabled && m_RemoteTestAgent != UCL_ActualAgent.None;
                    if (GUILayout.Button("▶ 測試切換", UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 0.88f, 1f)), GUILayout.ExpandWidth(false)))
                        UCL_RemoteWindowControl.TryActivateExplicitly(UCL_ActualAgentUtility.ToWindowTarget(m_RemoteTestAgent), out _);
                    GUI.enabled = true;
                }
                DrawPersonaLocateRow();
                GUILayout.Label($"使用者已靜置 {UCL_RemoteWindowControl.UserIdleSeconds:0.0}s｜狀態：{UCL_RemoteWindowControl.LastResult}", UCL_GUIStyle.LabelStyle);
                GUILayout.Label($"診斷檔：{UCL_RemoteWindowControl.DiagnosticPath}（每次按測試按鈕覆寫）", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                EditorGUILayout.HelpBox("此開關不存檔，重開 Editor / domain reload 後一定關閉。一般切換會在偵測到鍵鼠操作後讓出控制權；三顆測試按鈕是明示授權，會略過『剛按按鈕』造成的暫停，只嘗試切換指定的已開啟視窗，不會輸入文字或送出指令。", MessageType.Info);
            }
        }

        // 區塊職責：以在線 persona 為單位，跑「切視窗 → OCR 找 ##persona## → 只移動游標」的整條測試。
        // 物理意義：清單只列有未過期 lock 的 persona —— 不列 registry 的 status 快取，那欄在登出沒走完時會停在 online。
        // 數值影響：多重命中時不自動挑（session 標題列與側邊清單會同時命中同一個 token），
        //          改成把候選列出來、由使用者指定 index 再測一次；整條流程只呼叫 SetCursorPos，不點擊。
        void DrawPersonaLocateRow()
        {
            var online = UCL_ActivePersonaLocks.ListOnline();
            UCL_PersonaLockInfo selected = null;
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("測試 persona", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (online.Count == 0)
                {
                    GUILayout.Label("（目前沒有在線的 persona）", UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                    return;
                }
                var names = online.ConvertAll(l => l.Persona);
                if (!names.Contains(m_RemoteTestPersona)) m_RemoteTestPersona = names[0];
                string next = UCL_GUILayout.PopupAuto(m_RemoteTestPersona, names, m_RemoteTestPersonaPopupDic,
                    "RemoteTestPersona", 10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                if (next != m_RemoteTestPersona)
                {
                    m_RemoteTestPersona = next;
                    m_LocateOptions.MatchIndex = -1;   // 換人就把候選選擇丟掉，別把 index 帶到另一份畫面上
                }
                selected = online.Find(l => l.Persona == m_RemoteTestPersona);
                GUI.enabled = UCL_RemoteWindowControl.Enabled && selected != null && !m_LocateRunning;
                if (GUILayout.Button("▶ 測試定位游標", UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 1f, 0.8f)), GUILayout.ExpandWidth(false)))
                {
                    m_LocateRunning = true;
                    try { UCL_RemotePersonaLocator.RunCursorTest(selected, m_LocateOptions, out _); }
                    finally { m_LocateRunning = false; }
                }
                GUI.enabled = true;
                GUILayout.Label(selected == null ? ""
                    : $"→ {UCL_ActualAgentUtility.ToWindowTarget(selected.ActualAgent)}｜token {selected.SessionToken}",
                    UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
            }
            DrawLocateScanSettings();
            DrawLocatePreview();
            DrawLocateCandidates();
        }

        // 區塊職責：掃描範圍（哪塊螢幕 + 矩形範圍）與重試節奏的設定列。
        // 物理意義：session 清單固定在視窗左側，掃全桌面既慢又會撈到別的視窗上的同名文字；
        //          重試存在的理由是視窗剛被帶到前景時還沒重繪完，第一張抓到的可能是舊畫面。
        // 數值影響：範圍是 0~1 比例（相對選定螢幕），解析度無關；次數含第一次，命中即跳出不空等。
        void DrawLocateScanSettings()
        {
            m_Monitors ??= UCL_RemotePersonaLocator.ListMonitors();
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("掃描螢幕", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (GUILayout.Button("🔄", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(30))))
                    m_Monitors = UCL_RemotePersonaLocator.ListMonitors();
                if (GUILayout.Toggle(m_LocateOptions.Monitor == "all", "all（全桌面）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    m_LocateOptions.Monitor = "all";
                foreach (var monitor in m_Monitors)
                {
                    string key = monitor.Index.ToString();
                    if (GUILayout.Toggle(m_LocateOptions.Monitor == key, monitor.Label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        m_LocateOptions.Monitor = key;
                }
                GUILayout.FlexibleSpace();
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("掃描範圍 x/y/w/h", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.RegionX = Mathf.Clamp01(EditorGUILayout.FloatField(m_LocateOptions.RegionX, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))));
                m_LocateOptions.RegionY = Mathf.Clamp01(EditorGUILayout.FloatField(m_LocateOptions.RegionY, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))));
                m_LocateOptions.RegionW = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.RegionW, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0.01f, 1f - m_LocateOptions.RegionX);
                m_LocateOptions.RegionH = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.RegionH, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0.01f, 1f - m_LocateOptions.RegionY);
                if (GUILayout.Button("整塊", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                { m_LocateOptions.RegionX = m_LocateOptions.RegionY = 0f; m_LocateOptions.RegionW = m_LocateOptions.RegionH = 1f; }
                if (GUILayout.Button("左側 1/3", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                { m_LocateOptions.RegionX = m_LocateOptions.RegionY = 0f; m_LocateOptions.RegionW = 0.34f; m_LocateOptions.RegionH = 1f; }
                GUILayout.FlexibleSpace();
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("切窗後等待(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.InitialDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.InitialDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 10f);
                GUILayout.Label("重試次數", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.Attempts = Mathf.Clamp(EditorGUILayout.IntField(m_LocateOptions.Attempts, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40))), 1, 20);
                GUILayout.Label("每次間隔(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.AttemptDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.AttemptDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 10f);
                GUILayout.FlexibleSpace();
            }
            // 多重命中的選擇政策：session 清單在視窗左緣，標題列與對話內容都在它右邊，所以預設取最左。
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("多重命中時", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                DrawPolicyToggle("leftmost", "取最左（預設）");
                DrawPolicyToggle("topmost", "取最上");
                DrawPolicyToggle("strict", "不自選・列出候選");
                GUILayout.Space(12);
                // 明示按鈕才寫檔：設定調到一半自動存，等於把試錯過程也存成「決定」。
                if (GUILayout.Button("💾 保存設定", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                    m_LocateConfigStatus = UCL_RemotePersonaLocateConfig.Save(m_LocateOptions, m_RemoteTestPersona, out string saveError)
                        ? $"已保存到 {UCL_RemotePersonaLocateConfig.Path_}"
                        : $"保存失敗：{saveError}";
                if (GUILayout.Button("↺ 讀回", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    bool loaded = UCL_RemotePersonaLocateConfig.Load(m_LocateOptions, out string loadedPersona);
                    if (loaded && !string.IsNullOrEmpty(loadedPersona)) m_RemoteTestPersona = loadedPersona;
                    m_LocateConfigStatus = loaded ? "已讀回保存的設定" : "沒有保存過的設定（使用預設值）";
                }
                GUILayout.FlexibleSpace();
            }
            if (!string.IsNullOrEmpty(m_LocateConfigStatus))
                GUILayout.Label(m_LocateConfigStatus, new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
        }

        void DrawPolicyToggle(string policy, string label)
        {
            bool on = m_LocateOptions.SelectPolicy == policy;
            if (GUILayout.Toggle(on, label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)) && !on)
            {
                m_LocateOptions.SelectPolicy = policy;
                m_LocateOptions.MatchIndex = -1;   // 換政策就放掉手動指定，否則政策看起來沒生效
            }
        }

        // 區塊職責：rect 預覽 —— 底圖是選定螢幕的當前截圖，橘框是實際會送去 OCR 的範圍。
        // 物理意義：底圖抓「整塊螢幕」而不是 rect 內容；拿裁好的圖去調裁切範圍，永遠看不到自己漏掉了什麼。
        // 數值影響：預覽不跑 OCR（不載模型），按下去秒級回應；圖只在按鈕按下時更新，不隨 repaint 重抓。
        void DrawLocatePreview()
        {
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("🖵 更新範圍預覽", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    RefreshLocatePreview();
                GUILayout.Label(string.IsNullOrEmpty(m_LocatePreviewError)
                    ? "（底圖＝選定螢幕當前畫面；橘框＝送去 OCR 的矩形範圍）"
                    : $"預覽失敗：{m_LocatePreviewError}", UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
            }
            if (m_LocatePreview == null) return;
            float aspect = (float)m_LocatePreview.width / Mathf.Max(1, m_LocatePreview.height);
            float width = UCL_GUIStyle.GetScaledSize(420);
            Rect box = GUILayoutUtility.GetRect(width, width / Mathf.Max(0.1f, aspect), GUILayout.ExpandWidth(false));
            GUI.DrawTexture(box, m_LocatePreview, ScaleMode.StretchToFill);
            DrawBorder(box, new Color(0.65f, 0.65f, 0.72f), 1.5f);
            var region = new Rect(box.x + box.width * m_LocateOptions.RegionX,
                                  box.y + box.height * m_LocateOptions.RegionY,
                                  box.width * m_LocateOptions.RegionW,
                                  box.height * m_LocateOptions.RegionH);
            EditorGUI.DrawRect(region, new Color(1f, 0.6f, 0.1f, 0.18f));
            DrawBorder(region, new Color(1f, 0.7f, 0.2f), 1.5f);
        }

        void RefreshLocatePreview()
        {
            if (!UCL_RemotePersonaLocator.CapturePreview(m_LocateOptions.Monitor, 640, out m_LocatePreviewError))
            {
                m_LocatePreview = null;
                return;
            }
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(UCL_RemotePersonaLocator.PreviewPath);
                if (m_LocatePreview == null)
                {
                    m_LocatePreview = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    m_LocatePreview.hideFlags = HideFlags.HideAndDontSave;
                }
                m_LocatePreview.LoadImage(bytes);
                m_LocatePreviewError = "";
            }
            catch (Exception e)
            {
                m_LocatePreview = null;
                m_LocatePreviewError = e.Message;
            }
        }

        void DrawLocateCandidates()
        {
            var last = UCL_RemotePersonaLocator.LastResult;
            if (last == null) return;
            if (last.Matches.Count > 1)
            {
                string picked = last.Selected == null ? "未選定" : $"已用第 {last.SelectedIndex} 個";
                EditorGUILayout.HelpBox($"畫面上有 {last.Matches.Count} 處 {last.Token}（標題列／側邊清單／對話內容都可能出現）。本次{picked}；下面可手動改指定：", MessageType.Info);
                for (int i = 0; i < last.Matches.Count; i++)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool chosen = m_LocateOptions.MatchIndex == i;
                        if (GUILayout.Toggle(chosen, chosen ? "● 指定這個" : "○ 指定", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)) != chosen)
                            m_LocateOptions.MatchIndex = chosen ? -1 : i;
                        string mark = i == last.SelectedIndex ? "◀ 本次採用" : "";
                        GUILayout.Label($"{last.Matches[i].Describe(i)} {mark}", UCL_GUIStyle.LabelStyle);
                        GUILayout.FlexibleSpace();
                    }
                }
                if (m_LocateOptions.MatchIndex >= 0)
                    GUILayout.Label("（目前為手動指定；按上面的政策鈕可回到自動選擇）", UCL_GUIStyle.LabelStyle);
            }
            if (!last.Ok && last.Matches.Count == 0 && last.NearMisses.Count > 0)
                GUILayout.Label($"近似未命中：{string.Join("；", last.NearMisses.ConvertAll(n => n.Text))}",
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
            GUILayout.Label($"定位診斷檔：{UCL_RemotePersonaLocator.DiagnosticPath}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
        }

        static void DrawBorder(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        void DrawDaemonSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool systemEnabled = UCL_ChatTavernSystemControl.IsEnabled;
                bool reportsEnabled = m_TimeRules.rules.Any(r => r != null && r.id != null && r.id.StartsWith(ReportRulePrefix) && r.enabled);
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyDaemonFold, 21, iDefaultValue: false);
                    GUILayout.Label("<b>常駐酒保</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    bool nextSystem = GUILayout.Toggle(systemEnabled, systemEnabled ? "● 酒保運作中" : "○ 酒保已停止", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (nextSystem != systemEnabled) UCL_ChatTavernSystemControl.SetEnabled(nextSystem);
                    bool nextReports = GUILayout.Toggle(reportsEnabled, reportsEnabled ? "🕐 報時開啟" : "🕐 報時關閉", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (nextReports != reportsEnabled)
                    {
                        foreach (var rule in m_TimeRules.rules)
                            if (rule?.id != null && rule.id.StartsWith(ReportRulePrefix)) rule.enabled = nextReports;
                        UCL_BartenderIO.SaveTimeRules(m_TimeRules);
                    }
                    if (GUILayout.Button("▶ 立即檢查", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 1f)), GUILayout.ExpandWidth(false))) UCL_BartenderDaemon.ForceTick();
                    if (GUILayout.Button("↻ 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) Reload();
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label("酒保總開關與聊天酒館系統共用；「報時」只控制 announce-rules-* 的每日／每小時規則，不影響睡眠提醒、關鍵字留言或跨日保管費。", UCL_GUIStyle.LabelStyle);
            }
        }

        void DrawTimeRulesSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyTimeRulesFold, 21, iDefaultValue: false);
                    GUILayout.Label($"<b>⏰ 時間規則（{m_TimeRules.rules.Count}）</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                string deleteId = null;
                foreach (var rule in m_TimeRules.rules.OrderBy(r => r?.time_hhmm, StringComparer.Ordinal))
                {
                    if (rule == null) continue;
                    using (new GUILayout.HorizontalScope())
                    {
                        bool next = UCL_GUILayout.CheckBox(rule.enabled);
                        if (next != rule.enabled) { rule.enabled = next; UCL_BartenderIO.SaveTimeRules(m_TimeRules); }
                        GUILayout.Label($"{rule.time_hhmm}  {rule.id} → {rule.target_room}  {(rule.penalty_enabled ? $"寬限 {rule.grace_minutes}m / penalty {rule.penalty_interval_minutes}m" : "單次提醒")}", UCL_GUIStyle.LabelStyle);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("刪除", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false))) deleteId = rule.id;
                    }
                    GUILayout.Label($"    {rule.reminder_msg}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                }
                if (deleteId != null) { m_TimeRules.rules.RemoveAll(r => r != null && r.id == deleteId); UCL_BartenderIO.SaveTimeRules(m_TimeRules); }

                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("新增", UCL_GUIStyle.LabelStyle, GUILayout.Width(36));
                    m_NewRuleId = GUILayout.TextField(m_NewRuleId, GUILayout.Width(170));
                    m_NewRuleTime = GUILayout.TextField(m_NewRuleTime, GUILayout.Width(55));
                    m_NewRuleTarget = GUILayout.TextField(m_NewRuleTarget, GUILayout.Width(90));
                    m_NewRuleMessage = GUILayout.TextField(m_NewRuleMessage);
                    if (GUILayout.Button("新增時間規則", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false))
                        && !string.IsNullOrWhiteSpace(m_NewRuleId) && !string.IsNullOrWhiteSpace(m_NewRuleTime) && !string.IsNullOrWhiteSpace(m_NewRuleMessage))
                    {
                        UCL_BartenderIO.RegisterTimeRule(m_NewRuleId.Trim(), m_NewRuleTime.Trim(), m_NewRuleTarget.Trim(), m_NewRuleMessage.Trim(), 0, false, 5, m_NewRuleTarget.Trim(), DefaultRoom);
                        Reload();
                    }
                }
            }
        }

        void DrawTriggersSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyTriggersFold, 21, iDefaultValue: false);
                    GUILayout.Label($"<b>💬 關鍵字留言（{m_Triggers.triggers.Count}）</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                string deleteId = null;
                foreach (var trigger in m_Triggers.triggers)
                {
                    if (trigger == null) continue;
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"{trigger.id}  {trigger.target_room}  關鍵字「{trigger.keyword}」 剩 {trigger.remaining_triggers}/{trigger.initial_tokens}", UCL_GUIStyle.LabelStyle);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("刪除", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false))) deleteId = trigger.id;
                    }
                    GUILayout.Label($"    {trigger.message}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                }
                if (deleteId != null) { m_Triggers.triggers.RemoveAll(t => t != null && t.id == deleteId); UCL_BartenderIO.SaveTriggers(m_Triggers); }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("新增", UCL_GUIStyle.LabelStyle, GUILayout.Width(36));
                    m_NewTriggerKeyword = GUILayout.TextField(m_NewTriggerKeyword, GUILayout.Width(130));
                    m_NewTriggerMessage = GUILayout.TextField(m_NewTriggerMessage);
                    m_NewTriggerTokens = Mathf.Max(1, EditorGUILayout.IntField(m_NewTriggerTokens, GUILayout.Width(45)));
                    if (GUILayout.Button("新增留言", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false))
                        && !string.IsNullOrWhiteSpace(m_NewTriggerKeyword) && !string.IsNullOrWhiteSpace(m_NewTriggerMessage))
                    {
                        UCL_BartenderIO.RegisterTrigger("admin", "Admin", new List<string>(), m_NewTriggerKeyword.Trim(), m_NewTriggerMessage.Trim(), m_NewTriggerTokens, DefaultRoom);
                        Reload();
                    }
                }
            }
        }

        void DrawStateSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyStateFold, 21, iDefaultValue: false);
                    GUILayout.Label("<b>📊 執行狀態</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label($"state 更新：{(string.IsNullOrEmpty(m_State.last_updated) ? "尚無" : m_State.last_updated)}｜今日已觸發：{m_State.fired_today_keys?.Count ?? 0}｜跨日檢查：{m_State.last_overnight_check_date}", UCL_GUIStyle.LabelStyle);
                foreach (var cursor in m_State.room_last_seq)
                    GUILayout.Label($"  {cursor.room_id}: 已掃到 seq {cursor.last_seq}", UCL_GUIStyle.LabelStyle);
            }
        }
    }
}
#endif
