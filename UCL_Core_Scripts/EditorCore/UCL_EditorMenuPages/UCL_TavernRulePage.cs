// 區塊職責: Tavern Rule System IMGUI 頁面 (Tim 2026-05-12 拍板, 對應 Cmd_Rule)
// 物理意義: 讓 Tim 用 GUI 查看 active / reverted rule + 一鍵 revert (走 Cmd_Rule.Op_Revert 統一路徑)
// 數值影響: 純 view; revert 透過 Cmd_Rule 統一 ledger / file 改動, 不在 page 內 bypass 業務邏輯
// 設計取捨:
//   - 模仿 UCL_ChatTavernPage 的 layout 慣例 (左 list / 右 detail)
//   - revert 走 Cmd_Rule.ExecuteAsync 同統一路徑 (跟 agent run_cmd.py 完全一致)
//   - 不做 propose UI — propose 需構造 title/body, 走 CLI 比 GUI 表單合適 (Cmd_Rule --arg)
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands.Rules;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Tavern Rule IMGUI 頁面 — 列規則 + 看內容 + 撤回 (走 Cmd_Rule 統一路徑)。
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_TavernRulePage.md")]
    public class UCL_TavernRulePage : UCL_CommonEditorPage
    {
        public override string WindowName => "Tavern Rules";
        public static UCL_TavernRulePage Create() => UCL_EditorPage.Create<UCL_TavernRulePage>();

        // ===========================================================
        // UI 狀態
        // 物理意義: list 選中的 rule id + revert reason 表單暫存 + status filter
        // 數值影響: 純 UI; revert 按鈕觸發才走 Cmd_Rule
        // ===========================================================
        string m_SelectedRuleId = "";
        string m_RevertReason = "";
        string m_StatusFilter = "all";   // "active" / "reverted" / "all"
        Vector2 m_ListScroll = Vector2.zero;
        Vector2 m_DetailScroll = Vector2.zero;
        bool m_AutoRefresh = true;
        double m_LastRefreshTime = 0;
        const double RefreshIntervalSec = 3.0;

        // ===========================================================
        // 快取
        // 物理意義: 每幀掃 dir 太重; refresh / 進頁面 / 按鈕觸發才重抓
        // ===========================================================
        List<RuleViewEntry> m_RulesCache = new List<RuleViewEntry>();
        string m_SelectedRuleBody = "";

        // ===========================================================
        // 區塊職責: 頁面繪製入口
        // 物理意義: 上 toolbar / 左 rule list / 右 detail+revert; revert 走 Cmd_Rule 統一路徑
        // ===========================================================
        protected override void ContentOnGUI()
        {
            EnsureInitialRefresh();
            MaybeAutoRefresh();

            using (new GUILayout.VerticalScope())
            {
                DrawToolbar();
                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    DrawRuleList();      // 左欄
                    DrawRuleDetail();    // 右欄
                }
            }
        }

        void DrawToolbar()
        {
            using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"📜 Rules ({m_RulesCache.Count})", EditorStyles.boldLabel, GUILayout.Width(120));

                GUILayout.Label("filter:", GUILayout.Width(40));
                int filterIdx = System.Array.IndexOf(s_FilterOptions, m_StatusFilter);
                if (filterIdx < 0) filterIdx = 0;
                int newFilterIdx = EditorGUILayout.Popup(filterIdx, s_FilterOptions, EditorStyles.toolbarPopup, GUILayout.Width(100));
                if (newFilterIdx != filterIdx)
                {
                    m_StatusFilter = s_FilterOptions[newFilterIdx];
                    RefreshRules();
                }

                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    RefreshRules();
                }
                m_AutoRefresh = GUILayout.Toggle(m_AutoRefresh, "auto", EditorStyles.toolbarButton, GUILayout.Width(50));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Open Dir", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    string dir = RulesDir;
                    if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir);
                    else Debug.LogWarning($"[RulePage] rules dir 不存在: {dir}");
                }
            }
        }

        void DrawRuleList()
        {
            using (new GUILayout.VerticalScope(GUI.skin.box, GUILayout.Width(280), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label("規則清單", EditorStyles.boldLabel);
                m_ListScroll = GUILayout.BeginScrollView(m_ListScroll);
                if (m_RulesCache.Count == 0)
                {
                    GUILayout.Label("_(無命中)_", EditorStyles.miniLabel);
                }
                else
                {
                    foreach (var r in m_RulesCache)
                    {
                        bool selected = r.ruleId == m_SelectedRuleId;
                        string statusIcon = r.status == "active" ? "🟢" : "⚫";
                        string label = $"{statusIcon} {r.ruleId}: {Truncate(r.title, 30)}";
                        var style = selected ? EditorStyles.boldLabel : EditorStyles.label;
                        if (GUILayout.Button(label, style))
                        {
                            m_SelectedRuleId = r.ruleId;
                            LoadSelectedRuleBody();
                        }
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        void DrawRuleDetail()
        {
            using (new GUILayout.VerticalScope(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                var selected = m_RulesCache.FirstOrDefault(r => r.ruleId == m_SelectedRuleId);
                if (selected == null)
                {
                    GUILayout.Label("← 從左邊選一條 rule 看內容", EditorStyles.centeredGreyMiniLabel);
                    return;
                }

                // 標頭
                GUILayout.Label($"📜 {selected.ruleId} — {selected.title}", EditorStyles.boldLabel);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"status: **{selected.status}**", GUILayout.Width(140));
                    GUILayout.Label($"by: {selected.createdBy}", GUILayout.Width(180));
                    GUILayout.Label($"created: {selected.createdAt}");
                }
                GUILayout.Space(4);

                // 內容
                m_DetailScroll = GUILayout.BeginScrollView(m_DetailScroll, GUILayout.ExpandHeight(true));
                GUILayout.TextArea(m_SelectedRuleBody ?? "", EditorStyles.textArea, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();

                // Revert 區塊 — 只 active 才能 revert
                GUILayout.Space(4);
                using (new GUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    bool canRevert = selected.status == "active";
                    GUI.enabled = canRevert;
                    GUILayout.Label(canRevert ? "↩ Revert (refund 100 token 給 creator)" : "已 reverted, 不可重複 revert", EditorStyles.miniBoldLabel);
                    m_RevertReason = EditorGUILayout.TextField("reason", m_RevertReason ?? "");
                    using (new GUILayout.HorizontalScope())
                    {
                        bool reasonValid = !string.IsNullOrEmpty(m_RevertReason);
                        GUI.enabled = canRevert && reasonValid;
                        if (GUILayout.Button("Revert this rule", GUILayout.Width(180)))
                        {
                            DoRevert(selected.ruleId, m_RevertReason);
                        }
                        GUI.enabled = true;
                        if (!reasonValid && canRevert)
                        {
                            GUILayout.Label("⚠ 需填 reason", EditorStyles.miniLabel);
                        }
                    }
                    GUI.enabled = true;
                }
            }
        }

        // ===========================================================
        // 動作 — revert 走 Cmd_Rule.ExecuteAsync 統一路徑
        // 物理意義: 跟 agent 走 run_cmd.py 完全一致, 自動帶 ledger / file 改動 / audit trail
        // 安全: async void 適合 IMGUI button click; 立刻 return GUI 繼續 redraw, refresh 在 await 後
        // ===========================================================
        async void DoRevert(string ruleId, string reason)
        {
            var args = new Dictionary<string, string>
            {
                { "op", "revert" },
                { "rule_id", ruleId },
                { "reason", reason },
                { "reverted_by", "Tim" },   // GUI 操作預設 Tim 觸發 (人類在 Editor 內按按鈕)
            };
            try
            {
                var cmd = new Cmd_Rule();
                await cmd.ExecuteAsync(args, default);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RulePage] DoRevert via Cmd_Rule fail: {ex.Message}\n{ex.StackTrace}");
            }
            m_RevertReason = "";
            RefreshRules();
            LoadSelectedRuleBody();   // 重抓 body 顯示新的 reverted frontmatter
        }

        // ===========================================================
        // 資料載入
        // 物理意義: 跟 Cmd_Rule.LoadAllRules 同邏輯, 但 page 端讀完整 body (Cmd_Rule 只 parse frontmatter)
        // ===========================================================
        void EnsureInitialRefresh()
        {
            if (m_RulesCache.Count == 0 && !m_InitialRefreshed)
            {
                RefreshRules();
                m_InitialRefreshed = true;
            }
        }
        bool m_InitialRefreshed = false;

        void MaybeAutoRefresh()
        {
            if (!m_AutoRefresh) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - m_LastRefreshTime < RefreshIntervalSec) return;
            m_LastRefreshTime = now;
            RefreshRules();
        }

        void RefreshRules()
        {
            m_RulesCache.Clear();
            string dir = RulesDir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Equals("README", System.StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                var view = ParseRuleViewEntry(f);
                if (view == null) continue;
                if (m_StatusFilter != "all" && view.status != m_StatusFilter) continue;
                m_RulesCache.Add(view);
            }
            m_RulesCache = m_RulesCache.OrderBy(x => x.ruleId).ToList();
            if (!string.IsNullOrEmpty(m_SelectedRuleId)) LoadSelectedRuleBody();
        }

        void LoadSelectedRuleBody()
        {
            m_SelectedRuleBody = "";
            if (string.IsNullOrEmpty(m_SelectedRuleId)) return;
            string path = Path.Combine(RulesDir, m_SelectedRuleId + ".md");
            if (!File.Exists(path)) return;
            string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
            // 切掉 frontmatter, 只顯示 body — frontmatter 上半段已在 detail header 摘要顯示
            int end = content.IndexOf("\n---", 3, System.StringComparison.Ordinal);
            if (end > 0 && end < content.Length - 4)
            {
                m_SelectedRuleBody = content.Substring(end + 4).TrimStart();
            }
            else
            {
                m_SelectedRuleBody = content;
            }
        }

        // ===========================================================
        // 內部 — Rule view 解析 (簡易 frontmatter parser, 跟 Cmd_Rule 對齊)
        // ===========================================================
        class RuleViewEntry
        {
            public string ruleId;
            public string title;
            public string createdBy;
            public string createdAt;
            public string status;
        }

        static RuleViewEntry ParseRuleViewEntry(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                if (!content.StartsWith("---")) return null;
                int end = content.IndexOf("\n---", 3, System.StringComparison.Ordinal);
                if (end < 0) return null;
                string fm = content.Substring(3, end - 3);

                var entry = new RuleViewEntry();
                foreach (var rawLine in fm.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r').Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) continue;
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = colonIdx < line.Length - 1 ? line.Substring(colonIdx + 1).Trim() : "";
                    switch (key)
                    {
                        case "rule_id": entry.ruleId = val; break;
                        case "title": entry.title = UnescapeYamlInline(val); break;
                        case "created_by": entry.createdBy = val; break;
                        case "created_at": entry.createdAt = val; break;
                        case "status": entry.status = val; break;
                    }
                }
                if (string.IsNullOrEmpty(entry.ruleId)) return null;
                if (string.IsNullOrEmpty(entry.status)) entry.status = "active";
                return entry;
            }
            catch { return null; }
        }

        static string UnescapeYamlInline(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }

        static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s ?? "";
            return s.Substring(0, maxLen) + "...";
        }

        // ===========================================================
        // 路徑常量 — 跟 Cmd_Rule 對齊, 反推 git-root/AgentCommands/Rules/
        // 物理意義: Application.dataPath = "<git-root>/CardGame/Assets"; 反推兩層
        // ===========================================================
        static string RulesDir
        {
            get
            {
                string projRoot = Directory.GetParent(Application.dataPath)?.Parent?.FullName ?? "";
                return Path.Combine(projRoot, "AgentCommands", "Rules");
            }
        }

        static readonly string[] s_FilterOptions = new[] { "all", "active", "reverted" };
    }
}
#endif
