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
using Cysharp.Threading.Tasks;
using System.Threading.Tasks;

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
        const string KeyNotifyTraceFold = "BartenderNotifyTraceFold";

        UCL_BartenderTriggerList m_Triggers = new UCL_BartenderTriggerList();
        UCL_BartenderTimeRuleList m_TimeRules = new UCL_BartenderTimeRuleList();
        UCL_BartenderState m_State = new UCL_BartenderState();
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
        string m_NotifyStatus = "";

        const string KeyWaitParamsFold = "DrawWaitParamsSection";

        string m_WaitParamsStatus = "";
        List<UCL_NotifyCandidate> m_NotifyPool;
        double m_NotifyPoolTime;
        /// <summary>逐人判定痕跡展開狀態（實際值存在 m_FoldDic，這裡只是本次繪製的暫存）。</summary>
        bool m_ShowNotifyTrace;
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
                DrawAutoNotifySection();
                DrawWaitParamsSection();
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
                    // 區塊職責：永久開關（落 UCL_ProjectEditorPrefs，預設關閉）。
                    // 物理意義：跟左邊那顆是**兩顆獨立的開關**，刻意不互相偷改 ——
                    //          「這次要用」與「這台機器上一直要用」是兩個不同的決定。
                    //          唯一的耦合是：打開永久開關時順手把本次也打開（否則使用者要點兩次才生效，
                    //          而「我已經打開了為什麼沒動」正是最容易誤判成壞掉的形狀）。
                    bool persist = UCL_RemoteWindowControl.PersistEnabled;
                    bool nextPersist = GUILayout.Toggle(persist, persist ? "🔒 永久啟用中" : "🔓 永久啟用關閉",
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    if (nextPersist != persist)
                    {
                        UCL_RemoteWindowControl.PersistEnabled = nextPersist;
                        if (nextPersist) UCL_RemoteWindowControl.SetEnabled(true);
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                if (UCL_RemoteWindowControl.PersistEnabled)
                    EditorGUILayout.HelpBox("永久啟用已開：每次 domain reload / Editor 重啟後會自動把「本次啟動」設回開啟（存在 UCL_ProjectEditorPrefs，附專案指紋）。\n⚠ 這是刻意豁免掉「重編後必回關閉」那條護欄 —— 其餘護欄（使用者操作後暫停、送出前前景驗證）全部照舊。關掉這顆即恢復每次手動啟用。", MessageType.Warning);
                UCL_RemoteWindowControl.PauseOnUserInput = EditorGUILayout.ToggleLeft("偵測使用者操作後暫停（預設開啟）", UCL_RemoteWindowControl.PauseOnUserInput);
                UCL_RemoteWindowControl.UserIdlePauseSeconds = EditorGUILayout.IntField("使用者操作後暫停（秒）", UCL_RemoteWindowControl.UserIdlePauseSeconds);
                UCL_RemoteWindowControl.StrictForegroundCheck = EditorGUILayout.ToggleLeft(
                    "前景驗證失敗時中止流程（預設關閉 — 真正的門是 OCR 掃不掃得到 token）",
                    UCL_RemoteWindowControl.StrictForegroundCheck);
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
                    // 區塊職責：防連點旗標要活過整段 async — .Forget() 立刻返回, try/finally 同幀放旗等於沒鎖
                    //（2026-08-03 async 化 review 抓到的回歸; 寫法對齊 ListMonitors 的 guard 模式）。
                    async UniTask RunCursorTest()
                    {
                        m_LocateRunning = true;
                        try { await UCL_RemotePersonaLocator.RunCursorTest(selected, m_LocateOptions); }
                        catch (Exception ex) { Debug.LogException(ex); }
                        finally { m_LocateRunning = false; }
                    }
                    RunCursorTest().Forget();
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
        bool m_MonitorListLoading = false;
        public async UniTask ListMonitors()
        {
            if (m_MonitorListLoading) return;
            m_MonitorListLoading = true;
            try
            {
                m_Monitors = await UCL_RemotePersonaLocator.ListMonitors();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) 
            { 
                Debug.LogException(ex); 
            }
            m_MonitorListLoading = false;
        }
        // 區塊職責：掃描範圍（哪塊螢幕 + 矩形範圍）與重試節奏的設定列。
        // 物理意義：session 清單固定在視窗左側，掃全桌面既慢又會撈到別的視窗上的同名文字；
        //          重試存在的理由是視窗剛被帶到前景時還沒重繪完，第一張抓到的可能是舊畫面。
        // 數值影響：範圍是 0~1 比例（相對選定螢幕），解析度無關；次數含第一次，命中即跳出不空等。
        // ⚠ IMGUI 繪製方法必須維持同步 — 若改 async 且中途 await, 恢復點落在 OnGUI 之外會炸 layout
        //   （2026-08-03 review; async 的部分只有 ListMonitors, 由它自己背 guard）。
        void DrawLocateScanSettings()
        {
            if(m_Monitors == null)
            {
                ListMonitors().Forget();
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("掃描螢幕", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (GUILayout.Button("🔄", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(30))))
                {
                    ListMonitors().Forget();
                }
                if (GUILayout.Toggle(m_LocateOptions.Monitor == "all", "all（全桌面）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    m_LocateOptions.Monitor = "all";
                if(m_Monitors != null)
                {
                    foreach (var monitor in m_Monitors)
                    {
                        string key = monitor.Index.ToString();
                        if (GUILayout.Toggle(m_LocateOptions.Monitor == key, monitor.Label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            m_LocateOptions.Monitor = key;
                    }
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
            // 區塊職責：游標到位後要不要按左鍵 / 輸入文字。
            // 物理意義：這兩步送出的是與真人無法分辨的輸入，所以預設全關，且每步之前都會重驗前景視窗。
            // 數值影響：**沒有送出 Enter 的選項** —— 那條 code path 不存在，送出永遠由人自己按。
            using (new GUILayout.HorizontalScope())
            {
                m_LocateOptions.ClickAfterMove = EditorGUILayout.ToggleLeft("移到位後按左鍵",
                    m_LocateOptions.ClickAfterMove, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                GUI.enabled = m_LocateOptions.ClickAfterMove;
                GUILayout.Label("點擊前等(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.ClickDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.ClickDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 10f);
                m_LocateOptions.TypeAfterClick = EditorGUILayout.ToggleLeft("點擊後輸入文字",
                    m_LocateOptions.TypeAfterClick, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                GUI.enabled = m_LocateOptions.ClickAfterMove && m_LocateOptions.TypeAfterClick;
                m_LocateOptions.TypeText = EditorGUILayout.TextField(m_LocateOptions.TypeText, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                GUILayout.Label("輸入前等(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.TypeDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.TypeDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 10f);
                GUILayout.Label("前置後等(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.FocusDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.FocusDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 5f);
                GUILayout.Label("字間隔(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_LocateOptions.TypeCharDelaySec = Mathf.Clamp(EditorGUILayout.FloatField(m_LocateOptions.TypeCharDelaySec, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 0.5f);
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
            }
            // 讓「這個 agent 到底會不會多送一段鍵」看得見 —— 不必去翻 code 才知道流程長怎樣。
            {
                var lockInfo = UCL_ActivePersonaLocks.Find(m_RemoteTestPersona);
                var profile = UCL_RemoteAgentInput.Get(lockInfo?.ActualAgent ?? UCL_ActualAgent.None);
                GUILayout.Label(profile.NeedsPreparation
                    ? $"　↳ 此 agent 輸入前會先「{profile.ActionLabel}」：{profile.Note}"
                    : "　↳ 此 agent 點完 session 會自動 focus，不做前置",
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
            }
            if (m_LocateOptions.ClickAfterMove)
                EditorGUILayout.HelpBox("按左鍵與輸入文字送出的是與真人無法分辨的輸入。每一步之前都會重新確認前景視窗仍是目標，焦點被搶走即中止。**不會送出 Enter** —— 程式裡沒有送出的路徑，要送出請自己按。", MessageType.Warning);
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

        async UniTask RefreshLocatePreview()
        {
            var result = await UCL_RemotePersonaLocator.CapturePreview(m_LocateOptions.Monitor, 640);
            if (!result.success)
            {
                // 失敗必留話 — 舊版 out 參數會帶錯誤訊息, async 化時漏接會變靜默失敗（2026-08-03 review）
                m_LocatePreviewError = string.IsNullOrEmpty(result.error) ? "預覽擷取失敗（未回報原因）" : result.error;
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

        // 區塊職責：wait / 酒保插話的參數旋鈕（Tim 2026-08-04 要求從寫死改為後台可調）。
        // 物理意義：這些數字原本是 C# 常數。python 版本來有 UCL_BARTENDER_TRIGGER_SEC 可調，
        //          固化到 C# 時被寫死 —— 於是驗一次酒保插話要枯等 7.5 分鐘。
        //          2026-08-04 把觸發秒數調成 5 秒才在 40 秒內跑完一輪，並挖出
        //          「op=wait 歷史 71 筆從沒真的等過」那隻。旋鈕本身就是照妖鏡。
        // 數值影響：改完要按 💾 保存才落檔（tavern_wait_config.json）；未保存只影響本次 Editor session。
        void DrawWaitParamsSection()
        {
            UCL_TavernWaitSettings.EnsureLoaded();
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool show = UCL_GUILayout.Toggle(m_FoldDic, KeyWaitParamsFold, 21, iDefaultValue: false);
                    GUILayout.BeginVertical();
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("<b>⏳ Wait / 酒保插話 參數</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                        GUILayout.Label($"（插話 {UCL_TavernWaitSettings.NpcTriggerSeconds}s / 冷卻 {UCL_TavernWaitSettings.NpcCooldownSeconds}s / tick {UCL_TavernWaitSettings.TickIntervalSeconds:0.#}s）",
                            UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }

                    if (show)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label("酒保插話觸發(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_TavernWaitSettings.NpcTriggerSeconds = Mathf.Clamp(
                                EditorGUILayout.IntField(UCL_TavernWaitSettings.NpcTriggerSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(55))), 1, 86400);
                            GUILayout.Label("插話冷卻(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_TavernWaitSettings.NpcCooldownSeconds = Mathf.Clamp(
                                EditorGUILayout.IntField(UCL_TavernWaitSettings.NpcCooldownSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0, 86400);
                            GUILayout.Label("建議休息杯數", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_TavernWaitSettings.NpcRestHintDrinks = Mathf.Clamp(
                                EditorGUILayout.IntField(UCL_TavernWaitSettings.NpcRestHintDrinks, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40))), 1, 100);
                        }
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label("wait tick 間隔(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_TavernWaitSettings.TickIntervalSeconds = Mathf.Clamp(
                                EditorGUILayout.FloatField((float)UCL_TavernWaitSettings.TickIntervalSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0.1f, 60f);
                            GUILayout.Label("預設 wait timeout(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_TavernWaitSettings.DefaultWaitTimeout = Mathf.Clamp(
                                EditorGUILayout.IntField(UCL_TavernWaitSettings.DefaultWaitTimeout, GUILayout.Width(UCL_GUIStyle.GetScaledSize(55))), 1, 86400);
                            if (GUILayout.Button("💾 保存", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                                m_WaitParamsStatus = UCL_TavernWaitSettings.SaveConfig(out string err)
                                    ? $"已保存 → {UCL_TavernWaitSettings.ConfigPath}" : $"保存失敗：{err}";
                            // 回復預設只改記憶體，不直接覆蓋檔案 —— 誤按不該當場毀掉設定
                            if (GUILayout.Button("↺ 回復預設", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                UCL_TavernWaitSettings.ResetToDefaults();
                                m_WaitParamsStatus = "已回復預設值（尚未保存 — 要落檔請按 💾）";
                            }
                        }
                        GUILayout.Label("　※ 觸發秒數調小可在數十秒內驗證酒保插話；正式值建議 450s（慢速模式 wait=480s 內不被打斷）。",
                            new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                        if (!string.IsNullOrEmpty(m_WaitParamsStatus))
                            GUILayout.Label($"　{m_WaitParamsStatus}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                    }
                    GUILayout.EndVertical();
                }
            }
        }

        // 區塊職責：自動通知（酒保 ding）— 定期掃在線 persona 的收信匣，依權重挑一個去戳。
        // 物理意義：這是整條遠端路由裡唯一會按 Enter 的流程；它的目的就是替使用者送出，所以送出開關
        //          擺在看得見的地方，而不是藏在別的設定底下。
        // 數值影響：每輪只通知一個人；被通知者的 last_notified_seq 會推進，同一批 @ 不會每輪重戳。
        void DrawAutoNotifySection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool show = UCL_GUILayout.Toggle(m_FoldDic, "DrawAutoNotifySection", 21, iDefaultValue: false);
                    GUILayout.BeginVertical();
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("<b>🔔 自動通知（收信 → 戳對應視窗）</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                        bool enabled = UCL_RemoteNotifyService.Enabled;
                        bool next = GUILayout.Toggle(enabled, enabled ? "● 本次已啟用" : "○ 本次未啟用", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                        if (next != enabled) UCL_RemoteNotifyService.Enabled = next;
                        // 區塊職責：永久開關（落 UCL_ProjectEditorPrefs，預設關閉）——
                        //          形狀與「🖥 遠端視窗協作」那顆完全相同，同一個問題不做兩種介面。
                        // 物理意義：左邊那顆是 runtime-only，**每次重編都會靜默回到關閉**
                        //          （Tim 2026-08-14 回報）。這顆是那條護欄的顯式豁免。
                        //          唯一耦合：打開永久開關時順手把本次也打開 —— 否則要點兩次才生效，
                        //          而「我已經打開了為什麼沒動」正是最容易誤判成壞掉的形狀。
                        bool notifyPersist = UCL_RemoteNotifyService.PersistEnabled;
                        bool nextNotifyPersist = GUILayout.Toggle(notifyPersist,
                            notifyPersist ? "🔒 永久啟用中" : "🔓 永久啟用關閉",
                            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                        if (nextNotifyPersist != notifyPersist)
                        {
                            UCL_RemoteNotifyService.PersistEnabled = nextNotifyPersist;
                            if (nextNotifyPersist) UCL_RemoteNotifyService.Enabled = true;
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (show)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label("檢查間隔(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_RemoteNotifyService.IntervalSeconds = Mathf.Clamp(
                                EditorGUILayout.FloatField((float)UCL_RemoteNotifyService.IntervalSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(55))), 5f, 3600f);
                            GUILayout.Label("通知文字", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_RemoteNotifyService.NotifyText = EditorGUILayout.TextField(UCL_RemoteNotifyService.NotifyText, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                            // 已讀確認機制的兩顆旋鈕（Tim 2026-08-03）：冷卻=無條件頻率限制; cap 達標停戳+酒館 @Tim
                            GUILayout.Label("冷卻(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_RemoteNotifyService.CooldownSeconds = Mathf.Clamp(
                                EditorGUILayout.FloatField(UCL_RemoteNotifyService.CooldownSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 3600f);
                            // 認列已讀的往前標（Tim 2026-08-13）：正在回覆的那幾秒最容易落新 @，
                            // 而讀取訊號會把它們一起當成已讀。取 15s 是實測值（唯一血證落差 6.9s，5s 不夠）。
                            GUILayout.Label("已讀往前標(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_RemoteNotifyService.ReadCreditMarginSeconds = Mathf.Clamp(
                                EditorGUILayout.FloatField(UCL_RemoteNotifyService.ReadCreditMarginSeconds,
                                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(45))), 0f, 300f);
                            GUILayout.Label("retry 上限", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            UCL_RemoteNotifyService.RetryCap = Mathf.Clamp(
                                EditorGUILayout.IntField(UCL_RemoteNotifyService.RetryCap, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40))), 1, 20);
                            UCL_RemoteNotifyService.SendEnter = EditorGUILayout.ToggleLeft("輸入後送出 Enter",
                                UCL_RemoteNotifyService.SendEnter, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                            if (GUILayout.Button("💾 保存", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                                m_NotifyStatus = UCL_RemoteNotifyService.SaveConfig(out string err) ? "自動通知設定已保存" : $"保存失敗：{err}";
                            GUI.enabled = UCL_RemoteWindowControl.Enabled;
                            if (GUILayout.Button("▶ 立即執行一次", UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 1f, 0.8f)), GUILayout.ExpandWidth(false)))
                            {
                                async UniTask RunOnce()
                                {
                                    m_NotifyStatus = (await UCL_RemoteNotifyService.RunOnce(true)).summary;
                                };
                                RunOnce().Forget();
                            }
                            GUI.enabled = true;
                            GUILayout.FlexibleSpace();
                        }
                        // 區塊職責：輸入方式與尾註（掉字修法，Tim 2026-08-13 拍板走剪貼簿）。
                        // 物理意義：逐字輸入會被目標端 slash 自動完成清單重繪吃掉字（兩筆血證都掉
                        //          `/ucl-ding` 的 `-`）。貼上是一次事件，成因不存在 —— 代價是短暫
                        //          動到系統剪貼簿（用後即還原，含失敗路徑）。
                        using (new GUILayout.HorizontalScope())
                        {
                            UCL_RemoteNotifyService.UsePasteInput = EditorGUILayout.ToggleLeft(
                                "剪貼簿貼上（推薦 — 逐字會掉字）", UCL_RemoteNotifyService.UsePasteInput,
                                GUILayout.Width(UCL_GUIStyle.GetScaledSize(210)));
                            if (UCL_RemoteNotifyService.UsePasteInput)
                            {
                                GUILayout.Label("　↳ 還原剪貼簿前等(ms)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                UCL_RemoteNotifyService.PasteRestoreDelayMs = Mathf.Clamp(
                                    EditorGUILayout.IntField(UCL_RemoteNotifyService.PasteRestoreDelayMs,
                                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(55))), 0, 5000);
                            }
                            GUILayout.FlexibleSpace();
                        }
                        UCL_RemoteNotifyService.AppendContextNote = EditorGUILayout.ToggleLeft(
                            "文字尾端標註「系統自動輸入」（握手觸發時附上誰在等）", UCL_RemoteNotifyService.AppendContextNote);
                        if (UCL_RemoteNotifyService.UsePasteInput)
                            EditorGUILayout.HelpBox("剪貼簿貼上：原內容會先 cache、貼完並等待上面那個延遲後自動還原（例外／失敗路徑也會還原）。\n⚠ 還原前有一段該延遲長度的窗口，剪貼簿內容是通知文字 —— 這是等目標 app 讀取所必需，不是遺漏。若連原內容都讀不到，會選擇不還原並在 Editor log 出聲（不拿空字串蓋掉你的剪貼簿）。", MessageType.Info);
                        if (UCL_RemoteNotifyService.SendEnter)
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("　↳ 送出前等(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                UCL_RemoteNotifyService.EnterDelaySeconds = Mathf.Clamp(
                                    EditorGUILayout.FloatField(UCL_RemoteNotifyService.EnterDelaySeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 10f);
                                GUILayout.Label("Enter 次數", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                UCL_RemoteNotifyService.EnterPresses = Mathf.Clamp(
                                    EditorGUILayout.IntField(UCL_RemoteNotifyService.EnterPresses, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40))), 1, 5);
                                GUILayout.Label("每次間隔(s)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                UCL_RemoteNotifyService.EnterGapSeconds = Mathf.Clamp(
                                    EditorGUILayout.FloatField(UCL_RemoteNotifyService.EnterGapSeconds, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50))), 0f, 5f);
                                GUILayout.FlexibleSpace();
                            }
                            EditorGUILayout.HelpBox("送出 Enter 已開啟：這條流程會真的把訊息發出去。送出前會最後確認一次前景視窗仍是目標。\n若「文字進去了但沒送出」：先加大「送出前等」（自動完成清單要時間跳出來），仍不行再把 Enter 次數設 2（第一次可能被自動完成清單吃掉當作選取）。", MessageType.Warning);
                        }
                        if (!UCL_RemoteWindowControl.Enabled)
                            EditorGUILayout.HelpBox("遠端視窗協作未啟動 —— 自動通知不會動作。該開關預設每次 Editor / domain reload 後回到關閉；不想每次手動開 → 到「🖥 遠端視窗協作」打開「🔒 永久啟用」。", MessageType.Info);
                        // ⚠ 通知池要掃每個房間的 inbox 檔 —— OnGUI 每次重繪都掃 = 拖著整個 Editor 做磁碟 IO。
                        //    節流成每 2 秒最多一次；顯示晚 2 秒無所謂，真正的判斷在 daemon 那邊各自重掃。
                        if (EditorApplication.timeSinceStartup - m_NotifyPoolTime > 2.0 || m_NotifyPool == null)
                        {
                            // ⚠ applyStateChanges:false — 後台是**觀測端**。ScanPool 會推進已讀水位，
                            //   而這裡每 2 秒重繪一次 ⇒ 開著這頁就等於每 2 秒替所有人認列一次已讀，
                            //   把正在追的 @ 訊號改掉（真正該落 state 的是 daemon 那條實際會戳人的路徑）。
                            m_NotifyPool = UCL_RemoteNotifyService.ScanPool(applyStateChanges: false);
                            m_NotifyPoolTime = EditorApplication.timeSinceStartup;
                        }
                        GUILayout.Label($"通知池（{m_NotifyPool.Count}）— 權重＝新 @ 次數×10；平手看誰比較久沒被通知", UCL_GUIStyle.LabelStyle);
                        foreach (var candidate in m_NotifyPool) GUILayout.Label($"　• {candidate.Describe()}", UCL_GUIStyle.LabelStyle);
                        // 區塊職責：逐人判定痕跡 —— 池是空的時候，唯一能回答「為什麼」的地方。
                        // 物理意義：「通知池（0）」有六種完全不同的成因，舊版全部長成同一句話。攤開之後
                        //          「沒人叫她」跟「有人叫她但訊號被吃掉」在畫面上就不再同形。
                        // 數值影響：純顯示；資料來自剛才那次 ScanPool（節流 2 秒），不額外掃磁碟。
                        GUILayout.Label($"掃描判定（{(UCL_RemoteNotifyService.LastScanUtc == System.DateTime.MinValue ? "尚未掃描" : UCL_RemoteNotifyService.LastScanUtc.ToLocalTime().ToString("HH:mm:ss"))}）：{UCL_RemoteNotifyService.LastScanVerdict}",
                            new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                        using (new GUILayout.HorizontalScope())
                        {
                            m_ShowNotifyTrace = UCL_GUILayout.Toggle(m_FoldDic, KeyNotifyTraceFold, 21, iDefaultValue: false);
                            GUILayout.Label($"逐人判定痕跡（{UCL_RemoteNotifyService.LastScanTraces.Count} 人在線）",
                                UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            GUILayout.FlexibleSpace();
                        }
                        if (m_ShowNotifyTrace)
                        {
                            foreach (var trace in UCL_RemoteNotifyService.LastScanTraces)
                            {
                                // 遮蔽是永久靜默（不是延遲），所以用紅字 —— 它跟「大家都已讀」外觀相同
                                var color = trace.HasMaskedRoom ? new Color(1f, 0.55f, 0.55f)
                                    : trace.Pooled ? new Color(0.6f, 1f, 0.6f)
                                    : UCL_GUIStyle.LabelStyle.normal.textColor;
                                GUILayout.Label($"　　• {trace.Describe()}",
                                    new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, normal = { textColor = color } });
                                foreach (var room in trace.Rooms)
                                    GUILayout.Label($"　　　　- {room.Describe()}",
                                        new GUIStyle(UCL_GUIStyle.LabelStyle)
                                        { wordWrap = true, normal = { textColor = room.Masked ? new Color(1f, 0.55f, 0.55f) : UCL_GUIStyle.LabelStyle.normal.textColor } });
                            }
                            EditorGUILayout.HelpBox("⚠ 「整房遮蔽」＝該房最大 seq 低於已讀水位。每個房間各自從 1 開始編 seq（tavern 已 15000+，TRPG 側房 100 出頭），而水位跨房共用一個 —— 一旦水位被 tavern 推高，側房的 @ 就永遠算不出「新的」，通知池會顯示 0 而畫面上跟「大家都已讀」完全一樣。", MessageType.Warning);
                        }
                        // 已讀/冷卻/停戳狀態列 — 只列有事的 persona（無 pending 且不在冷卻的不佔版面）
                        var notifyStates = UCL_RemoteNotifyService.DescribeNotifyStates();
                        if (notifyStates.Count > 0)
                        {
                            GUILayout.Label("通知狀態（已讀確認機制）：", UCL_GUIStyle.LabelStyle);
                            foreach (var line in notifyStates)
                                GUILayout.Label($"　• {line}", new GUIStyle(UCL_GUIStyle.LabelStyle)
                                { normal = { textColor = line.Contains("🔴") ? new Color(1f, 0.5f, 0.5f) : UCL_GUIStyle.LabelStyle.normal.textColor } });
                        }
                        GUILayout.Label($"最近一次：{UCL_RemoteNotifyService.LastRunSummary}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                        if (!string.IsNullOrEmpty(m_NotifyStatus))
                            GUILayout.Label(m_NotifyStatus, new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                        GUILayout.Label($"執行紀錄：{UCL_RemoteNotifyService.LogPath}", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                    }

                    GUILayout.EndVertical();
                }
            }
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
                        // 區塊職責：報時開關 — 當場重讀→只改 announce-rules-* 的 enabled→回存, 再同步快取。
                        // 物理意義：拿本頁快取整包回存會把「編輯頁剛存的內容」蓋回舊版（stale 快取 clobber）;
                        //          load-modify-save 讓本開關只動自己該動的欄位, 與編輯頁互不踩。
                        // 數值影響：寫檔仍走 SaveTimeRules atomic write; m_TimeRules 快取同步為最新版。
                        var fresh = UCL_BartenderIO.LoadTimeRules() ?? new UCL_BartenderTimeRuleList();
                        fresh.rules ??= new List<UCL_BartenderTimeRule>();
                        foreach (var rule in fresh.rules)
                            if (rule?.id != null && rule.id.StartsWith(ReportRulePrefix)) rule.enabled = nextReports;
                        UCL_BartenderIO.SaveTimeRules(fresh);
                        m_TimeRules = fresh;
                    }
                    if (GUILayout.Button("▶ 立即檢查", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 1f)), GUILayout.ExpandWidth(false))) UCL_BartenderDaemon.ForceTick();
                    if (GUILayout.Button("↻ 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) Reload();
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label("酒保總開關與聊天酒館系統共用；「報時」只控制 announce-rules-* 的每日／每小時規則，不影響睡眠提醒、關鍵字留言或跨日保管費。", UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：時間規則區 — 唯讀總覽 + 跳轉編輯頁（編輯功能 2026-08-03 抽離到 UCL_BartenderTimeRulePage）。
        // 物理意義：本頁不再持有任何「改規則後整包回存」的路徑, 避免與編輯頁的顯式存檔語意打架
        //          （AdminPage 快取的舊 list 一經 Save 會蓋掉編輯頁剛存的內容）。
        // 數值影響：這裡只讀 m_TimeRules 快取來顯示; 開編輯頁回來按「↻ 重新載入」可刷新總覽。
        void DrawTimeRulesSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, KeyTimeRulesFold, 21, iDefaultValue: false);
                    GUILayout.Label($"<b>⏰ 時間規則（{m_TimeRules.rules.Count}）</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("✏️ 開啟時間規則編輯頁", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                        UCL_BartenderTimeRulePage.Create();
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label("時間與內文的編輯、新增與刪除都在編輯頁（顯式存檔, 沒按存檔不寫回 json）。此處僅總覽。", UCL_GUIStyle.LabelStyle);
                foreach (var rule in m_TimeRules.rules.OrderBy(r => r?.time_hhmm, StringComparer.Ordinal))
                {
                    if (rule == null) continue;
                    GUILayout.Label($"{(rule.enabled ? "●" : "○")} {rule.time_hhmm}  {rule.id} → {rule.target_room}  單次提醒", UCL_GUIStyle.LabelStyle);
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
