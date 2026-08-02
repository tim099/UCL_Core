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
                GUILayout.Label($"使用者已靜置 {UCL_RemoteWindowControl.UserIdleSeconds:0.0}s｜狀態：{UCL_RemoteWindowControl.LastResult}", UCL_GUIStyle.LabelStyle);
                GUILayout.Label($"診斷檔：{UCL_RemoteWindowControl.DiagnosticPath}（每次按測試按鈕覆寫）", new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true });
                EditorGUILayout.HelpBox("此開關不存檔，重開 Editor / domain reload 後一定關閉。一般切換會在偵測到鍵鼠操作後讓出控制權；三顆測試按鈕是明示授權，會略過『剛按按鈕』造成的暫停，只嘗試切換指定的已開啟視窗，不會輸入文字或送出指令。", MessageType.Info);
            }
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
