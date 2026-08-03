// 區塊職責：時間規則編輯頁 — 從 UCL_BartenderAdminPage 抽離的 TimeRule 專用編輯器。
// 物理意義：AdminPage 的規則區只能開關/刪除/單行新增，reminder_msg 唯讀；本頁提供每條規則的
//          時間（time_hhmm）與內文（reminder_msg 多行 TextArea）就地編輯。
// 數值影響：所有編輯只動記憶體工作副本（deep copy），**按「存檔」才寫回 time_rules.json**；
//          未存檔離頁會彈確認（存檔離開 / 捨棄離開 / 取消），不會靜默丟修改也不會靜默寫檔。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.Bartender;
using UCL.Core.UI;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderTimeRulePage.md")]
    public class UCL_BartenderTimeRulePage : UCL_CommonEditorPage
    {
        public override string WindowName => "時間規則編輯";

        public static UCL_BartenderTimeRulePage Create() => UCL_EditorPage.Create<UCL_BartenderTimeRulePage>();

        // 區塊職責：工作副本與 dirty 狀態。
        // 物理意義：m_WorkingRules 是 time_rules.json 的 deep copy（JsonUtility round-trip），
        //          編輯全打在副本上；m_SavedSnapshot 存「上次載入/存檔時」的序列化字串，
        //          dirty 判定 = 現況序列化 != snapshot（不靠人肉 flag，改什麼欄位都測得到）。
        // 數值影響：Reload 與 Save 都會重設 snapshot；副本與原檔的唯一同步點是 DoSave()。
        UCL_BartenderTimeRuleList m_WorkingRules;
        string m_SavedSnapshot = "";
        string m_StatusMsg = "";

        string m_NewRuleId = "";
        string m_NewRuleTime = "09:00";
        string m_NewRuleTarget = "";

        bool IsDirty => m_WorkingRules != null && JsonUtility.ToJson(m_WorkingRules) != m_SavedSnapshot;

        // 區塊職責：載入 time_rules.json 成工作副本（捨棄未存修改）。
        // 物理意義：JsonUtility round-trip 做 deep copy — 讀進來的物件不與任何快取共享參照，
        //          避免「別處持有同一份 list 順手 Save」把本頁未完成的編輯寫出去。
        // 數值影響：m_SavedSnapshot 同步重設 → dirty 歸零。
        void LoadWorkingCopy()
        {
            var loaded = UCL_BartenderIO.LoadTimeRules() ?? new UCL_BartenderTimeRuleList();
            loaded.rules ??= new List<UCL_BartenderTimeRule>();
            m_WorkingRules = JsonUtility.FromJson<UCL_BartenderTimeRuleList>(JsonUtility.ToJson(loaded));
            m_WorkingRules.rules ??= new List<UCL_BartenderTimeRule>();
            m_SavedSnapshot = JsonUtility.ToJson(m_WorkingRules);
            m_StatusMsg = "";
        }

        // 區塊職責：存檔 — 驗證後把工作副本寫回 time_rules.json。
        // 物理意義：time_hhmm 打錯時 daemon 端 TryParseHHmm 會**靜默跳過**該規則（永不觸發、不報錯），
        //          所以格式驗證必須擋在寫檔前 — 這裡是唯一能把「規則悄悄死掉」變成可見錯誤的位置。
        // 數值影響：任一規則不合法 → 整份不寫、印紅字定位；全合法 → SaveTimeRules（atomic write）+ snapshot 重設。
        void DoSave()
        {
            foreach (var rule in m_WorkingRules.rules)
            {
                if (rule == null) continue;
                if (string.IsNullOrWhiteSpace(rule.id))
                {
                    m_StatusMsg = "✗ 有規則的 id 是空的 — 未存檔";
                    return;
                }
                if (!TryParseHHmm(rule.time_hhmm))
                {
                    m_StatusMsg = $"✗ 規則 `{rule.id}` 的時間「{rule.time_hhmm}」不是合法 HH:mm — 未存檔（格式錯誤的規則 daemon 會靜默跳過, 等於悄悄停用）";
                    return;
                }
                if (string.IsNullOrWhiteSpace(rule.reminder_msg))
                {
                    m_StatusMsg = $"✗ 規則 `{rule.id}` 的內文是空的 — 未存檔";
                    return;
                }
            }
            var dup = m_WorkingRules.rules.Where(r => r != null).GroupBy(r => r.id).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                m_StatusMsg = $"✗ id `{dup.Key}` 重複（{dup.Count()} 條）— 未存檔（fired_today 去重靠 id, 重複 id 會互吃觸發）";
                return;
            }
            UCL_BartenderIO.SaveTimeRules(m_WorkingRules);
            m_SavedSnapshot = JsonUtility.ToJson(m_WorkingRules);
            m_StatusMsg = $"✓ 已存檔（{m_WorkingRules.rules.Count} 條）{DateTime.Now:HH:mm:ss}";
        }

        // 與 UCL_BartenderDaemon.TryParseHHmm 同判準（該方法為 private, 此處最小複刻並以 daemon 版為語意權威）
        static bool TryParseHHmm(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Contains(":")) return false;
            var parts = s.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out int hour) || !int.TryParse(parts[1], out int min)) return false;
            return hour >= 0 && hour <= 23 && min >= 0 && min <= 59;
        }

        // 區塊職責：返回/關閉前的未存檔守門 — 彈確認三選一。
        // 物理意義：本頁的存檔語意是顯式的（沒按存檔就不寫 json），但「顯式不寫」與「靜默丟失」
        //          是兩回事 — 使用者按 Back 時如果有未存修改, 必須讓丟失成為一個看得見的選擇。
        // 數值影響：存檔離開 = DoSave 成功才 Pop（驗證失敗留在頁上顯示紅字）；捨棄 = 直接 Pop；取消 = 留在頁上。
        protected override void BackButtonClicked()
        {
            if (!IsDirty) { base.BackButtonClicked(); return; }
            int choice = EditorUtility.DisplayDialogComplex("時間規則有未存檔的修改",
                "要存檔後離開嗎？", "存檔離開", "取消", "捨棄修改離開");
            if (choice == 0)
            {
                DoSave();
                if (m_StatusMsg.StartsWith("✓")) base.BackButtonClicked();
            }
            else if (choice == 2) base.BackButtonClicked();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(!IsDirty))
            {
                if (GUILayout.Button("💾 存檔", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false)))
                    DoSave();
            }
            if (GUILayout.Button("↻ 重新載入（捨棄未存）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                LoadWorkingCopy();

            // 區塊職責：用系統預設程式直接開 time_rules.json（Tim 2026-08-03 加的入口, 補實作）。
            // 物理意義：本頁存檔語意是顯式的, 直接開原檔給「想看/想外部編輯」的人一條路 —
            //          有未存檔修改時警告先存檔, 避免外部改完被本頁存檔蓋掉。
            // 數值影響：唯讀行為（只開檔不寫入）; 檔案不存在時警告不開。
            if (GUILayout.Button("📂 開啟 json", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                string path = UCL_BartenderIO.GetTimeRulesPath();
                if (!System.IO.File.Exists(path))
                    m_StatusMsg = $"✗ 檔案不存在: {path}";
                else
                {
                    if (IsDirty) m_StatusMsg = "⚠ 本頁有未存檔修改 — 外部編輯前建議先存檔, 否則之後按存檔會蓋掉外部改動";
                    Application.OpenURL("file://" + path.Replace('\\', '/'));
                }
            }
        }

        protected override void ContentOnGUI()
        {
            if (m_WorkingRules == null) LoadWorkingCopy();   // 首幀 lazy-load（不在 ctor 碰 IO）

            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"<b>⏰ 時間規則（{m_WorkingRules.rules.Count}）{(IsDirty ? " *未存檔" : "")}</b>",
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true }, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            GUILayout.Label("修改只留在本頁，**按「💾 存檔」才寫回 time_rules.json**。時間格式 HH:mm（24 小時制）。",
                UCL_GUIStyle.LabelStyle);
            if (!string.IsNullOrEmpty(m_StatusMsg))
                GUILayout.Label(m_StatusMsg, new GUIStyle(UCL_GUIStyle.LabelStyle)
                { wordWrap = true, normal = { textColor = m_StatusMsg.StartsWith("✓") ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.5f) } });
            GUILayout.Space(6);

            DrawRulesPanel();
            GUILayout.Space(8);
            DrawAddRulePanel();
        }

        // 區塊職責：規則清單 — 每條可編輯 time_hhmm / reminder_msg + enabled 開關 + 刪除。
        // 物理意義：清單依 time_hhmm 排序顯示但**不重排底層 list**（排序只是視圖, 存檔保持原順序,
        //          diff 才不會因顯示順序抖動）；TextArea 不自帶 scroll（Page 外層已有, 避免雙層卷軸）。
        // 數值影響：一切修改僅落工作副本；刪除也只是副本移除, 按存檔才生效。
        void DrawRulesPanel()
        {
            string deleteId = null;
            foreach (var rule in m_WorkingRules.rules.OrderBy(r => r?.time_hhmm, StringComparer.Ordinal).ToList())
            {
                if (rule == null) continue;
                using (new GUILayout.VerticalScope("box"))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool next = UCL_GUILayout.CheckBox(rule.enabled);
                        if (next != rule.enabled) rule.enabled = next;
                        GUILayout.Label("時間", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        string newTime = GUILayout.TextField(rule.time_hhmm ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(55)));
                        if (newTime != rule.time_hhmm) rule.time_hhmm = newTime;
                        if (!TryParseHHmm(rule.time_hhmm))
                            GUILayout.Label("⚠ 非 HH:mm", new GUIStyle(UCL_GUIStyle.LabelStyle) { normal = { textColor = new Color(1f, 0.5f, 0.5f) } }, GUILayout.ExpandWidth(false));
                        if (GUILayout.Button("刪除", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            deleteId = rule.id;
                        GUILayout.Label($"`{rule.id}` → {rule.target_room}{(string.IsNullOrEmpty(rule.target_id) ? "" : $" @{rule.target_id}")}  {(rule.penalty_enabled ? $"寬限 {rule.grace_minutes}m / penalty {rule.penalty_interval_minutes}m" : "單次提醒")}",
                            UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                    string newMsg = GUILayout.TextArea(rule.reminder_msg ?? "", UCL_GUIStyle.TextAreaStyle,
                        GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(40)));
                    if (newMsg != rule.reminder_msg) rule.reminder_msg = newMsg;
                }
            }
            if (deleteId != null) m_WorkingRules.rules.RemoveAll(r => r != null && r.id == deleteId);
        }

        // 區塊職責：新增規則列 — 從 AdminPage 抽離過來的建立入口。
        // 物理意義：新規則直接進工作副本（內文先給佔位字串, 建完就地編輯）, 與其他修改一樣等存檔才落地；
        //          與 AdminPage 舊版差異 = 舊版 RegisterTimeRule 即時寫檔, 本頁統一走顯式存檔語意。
        // 數值影響：同 id 已存在 → 拒絕並提示（存檔時也有重複檢查, 這裡先擋掉好路徑）。
        void DrawAddRulePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>新增規則</b>（id / HH:mm / target 可空 — 建立後直接在上方編輯內文）",
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true });
                using (new GUILayout.HorizontalScope())
                {
                    m_NewRuleId = GUILayout.TextField(m_NewRuleId, GUILayout.Width(UCL_GUIStyle.GetScaledSize(170)));
                    m_NewRuleTime = GUILayout.TextField(m_NewRuleTime, GUILayout.Width(UCL_GUIStyle.GetScaledSize(55)));
                    m_NewRuleTarget = GUILayout.TextField(m_NewRuleTarget, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    if (GUILayout.Button("＋ 新增", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)), GUILayout.ExpandWidth(false)))
                    {
                        string id = m_NewRuleId.Trim();
                        if (string.IsNullOrEmpty(id)) m_StatusMsg = "✗ 新增失敗：id 不可空";
                        else if (m_WorkingRules.rules.Any(r => r != null && r.id == id)) m_StatusMsg = $"✗ 新增失敗：id `{id}` 已存在";
                        else
                        {
                            m_WorkingRules.rules.Add(new UCL_BartenderTimeRule
                            {
                                id = id,
                                time_hhmm = m_NewRuleTime.Trim(),
                                target_id = m_NewRuleTarget.Trim(),
                                reminder_msg = "（在此編輯提醒內文）",
                                grace_minutes = 0,
                                penalty_enabled = false,
                                penalty_interval_minutes = 5,
                                penalty_target = m_NewRuleTarget.Trim(),
                                target_room = "tavern",
                                enabled = true,
                            });
                            m_NewRuleId = "";
                            m_StatusMsg = $"＋ 已加入 `{id}`（尚未存檔）";
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }
    }
}
#endif
