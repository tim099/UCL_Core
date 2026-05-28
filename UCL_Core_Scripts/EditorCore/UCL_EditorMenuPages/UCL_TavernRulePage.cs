// 區塊職責: Tavern Rule System IMGUI 頁面 (Tim 2026-05-12 拍板, 對應 Cmd_Rule)
// 物理意義: 讓 Tim 用 GUI 查看 active / reverted rule + 一鍵 revert (走 Cmd_Rule.Op_Revert 統一路徑)
// 數值影響: 純 view; revert 透過 Cmd_Rule 統一 ledger / file 改動, 不在 page 內 bypass 業務邏輯
// 設計取捨:
//   - 模仿 UCL_AffinitySystemPage 的 layout 慣例 (上 toolbar / 左 list / 右 detail)
//   - revert 走 Cmd_Rule.ExecuteAsync 同統一路徑 (跟 agent run_cmd.py 完全一致)
//   - 不做 propose UI — propose 需構造 title/body, 走 CLI 比 GUI 表單合適 (Cmd_Rule --arg)
//   - 2026-05-13 重構: 移除 UnityEditor namespace 依賴 (EditorStyles / EditorGUILayout / EditorUtility /
//     EditorApplication), 改用 UCL_GUIStyle + 純 GUILayout. 仍保 #if UNITY_EDITOR 因檔在
//     EditorCore/ 目錄 + Cmd_Rule 走 UnityEditor (revert handler), 但本檔自身 UI 層 zero-Editor.
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands.Rules;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
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
        public override string WindowName => UCL_CodeLocalize.Get("Rule.Title");

        /// <summary>把此頁面註冊進 EditorMenu 的 Page Picker 下拉選單中 (對齊 UCL_AffinitySystemPage 慣例).</summary>
        public override bool ShowInPageMenu => true;

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
        float m_LastRefreshTime = 0f;
        const float RefreshIntervalSec = 3.0f;

        // ===========================================================
        // 快取
        // 物理意義: 每幀掃 dir 太重; refresh / 進頁面 / 按鈕觸發才重抓
        // ===========================================================
        List<RuleViewEntry> m_RulesCache = new List<RuleViewEntry>();
        string m_SelectedRuleBody = "";
        bool m_InitialRefreshed = false;

        static readonly string[] s_FilterOptions = new[] { "all", "active", "reverted" };

        // ===========================================================
        // 區塊職責: 頁面繪製入口
        // 物理意義: 上 toolbar / 左 rule list / 右 detail+revert; revert 走 Cmd_Rule 統一路徑
        // 數值影響: 純 UI 繪製; 跑 EnsureInitialRefresh + MaybeAutoRefresh 兩個 IO hook
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

        // 區塊職責: 上方 toolbar — count + filter 切換 + Refresh button + auto toggle
        // 物理意義: filter 3 選 1 用 horizontal button row (對齊 UCL_AffinitySystemPage TopBarButtons 風格)
        // 數值影響: 純 UI, filter 改值才觸發 RefreshRules
        void DrawToolbar()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Rule.Toolbar.CountFmt"), m_RulesCache.Count), UCL_GUIStyle.LabelStyle,
                                GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));

                GUILayout.Label(UCL_CodeLocalize.Get("Rule.Toolbar.Filter"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
                // 區塊: filter 用 button-row 取代 dropdown (3 選 1 不必弄 popup) — 選中的按鈕著色區分
                // filter id 維持 internal 英文 (all/active/reverted), 顯示走 localize
                foreach (var opt in s_FilterOptions)
                {
                    bool selected = (m_StatusFilter == opt);
                    var style = selected
                        ? UCL_GUIStyle.GetButtonStyle(Color.cyan)
                        : UCL_GUIStyle.ButtonStyle;
                    string label = UCL_CodeLocalize.Get("Rule.Filter." + char.ToUpperInvariant(opt[0]) + opt.Substring(1));
                    if (GUILayout.Button(label, style, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                    {
                        if (!selected)
                        {
                            m_StatusFilter = opt;
                            RefreshRules();
                        }
                    }
                }

                GUILayout.Space(8);

                if (GUILayout.Button(UCL_CodeLocalize.Get("Rule.Btn.Refresh"), UCL_GUIStyle.ButtonStyle,
                                     GUILayout.Width(UCL_GUIStyle.GetScaledSize(100))))
                {
                    RefreshRules();
                }

                m_AutoRefresh = GUILayout.Toggle(m_AutoRefresh, UCL_CodeLocalize.Get("Rule.Toolbar.Auto"), UCL_GUIStyle.ButtonStyle,
                                                  GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));

                GUILayout.FlexibleSpace();

                // 區塊: 移除 EditorUtility.RevealInFinder (Editor-only) — 改 print 路徑給 Console
                // 物理意義: 純 runtime 路徑沒辦法開檔總管; 顯示路徑讓使用者複製貼系統檔總管
                if (GUILayout.Button(UCL_CodeLocalize.Get("Rule.Btn.Path"), UCL_GUIStyle.ButtonStyle,
                                     GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                {
                    Debug.Log($"[RulePage] Rules dir: {RulesDir}");
                }
            }
        }

        // 區塊職責: 左欄 rule list — 每筆 active=綠燈 / reverted=灰燈, 點擊載 detail
        // 物理意義: 用 GUILayout.Button + 不同 style 表達「選中態」 (cyan)
        void DrawRuleList()
        {
            using (new GUILayout.VerticalScope("box",
                                              GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)),
                                              GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Rule.List.Title"), UCL_GUIStyle.LabelStyle);
                m_ListScroll = GUILayout.BeginScrollView(m_ListScroll);
                if (m_RulesCache.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Rule.List.Empty"), UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    foreach (var r in m_RulesCache)
                    {
                        bool selected = r.ruleId == m_SelectedRuleId;
                        string statusIcon = r.status == "active" ? "🟢" : "⚫";
                        string label = $"{statusIcon} {r.ruleId}: {Truncate(r.title, 30)}";
                        // 選中態用 cyan button style 突出 (不依賴 EditorStyles.boldLabel)
                        var style = selected
                            ? UCL_GUIStyle.GetButtonStyle(Color.cyan)
                            : UCL_GUIStyle.ButtonStyle;
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

        // 區塊職責: 右欄 rule detail — 標頭 + body + revert 表單
        // 物理意義: 用 TextArea 顯示 body (TextAreaStyle 已有 scaled font + wordWrap);
        //          revert 區塊用 helpBox 風的 box scope, 內含 reason 輸入 + revert button
        void DrawRuleDetail()
        {
            using (new GUILayout.VerticalScope("box",
                                              GUILayout.ExpandWidth(true),
                                              GUILayout.ExpandHeight(true)))
            {
                var selected = m_RulesCache.FirstOrDefault(r => r.ruleId == m_SelectedRuleId);
                if (selected == null)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Rule.Detail.EmptyHint"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                // 標頭
                GUILayout.Label($"<b>📜 {selected.ruleId}</b> — {selected.title}", UCL_GUIStyle.LabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    // status: active=綠 / reverted=灰
                    var statusColor = selected.status == "active" ? Color.green : Color.gray;
                    GUILayout.Label(UCL_CodeLocalize.Get("Rule.Detail.Status"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label($"<b>{selected.status}</b>",
                                    UCL_GUIStyle.GetLabelStyle(statusColor),
                                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Rule.Detail.ByFmt"), selected.createdBy), UCL_GUIStyle.LabelStyle,
                                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Rule.Detail.CreatedFmt"), selected.createdAt), UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Space(4);

                // 內容 — 用 TextArea (read-only 效果靠不接收輸入即可; 仍可框選複製)
                m_DetailScroll = GUILayout.BeginScrollView(m_DetailScroll, GUILayout.ExpandHeight(true));
                GUILayout.TextArea(m_SelectedRuleBody ?? "", UCL_GUIStyle.TextAreaStyle, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();

                // Revert 區塊 — 只 active 才能 revert
                GUILayout.Space(4);
                using (new GUILayout.VerticalScope("box"))
                {
                    bool canRevert = selected.status == "active";
                    string hint = canRevert
                        ? UCL_CodeLocalize.Get("Rule.Revert.CanHint")
                        : UCL_CodeLocalize.Get("Rule.Revert.AlreadyHint");
                    GUILayout.Label(hint, UCL_GUIStyle.LabelStyle);

                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(UCL_CodeLocalize.Get("Rule.Revert.Reason"), UCL_GUIStyle.LabelStyle,
                                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        GUI.enabled = canRevert;
                        m_RevertReason = GUILayout.TextField(m_RevertReason ?? "", UCL_GUIStyle.TextFieldStyle,
                                                              GUILayout.ExpandWidth(true));
                        GUI.enabled = true;
                    }

                    using (new GUILayout.HorizontalScope())
                    {
                        bool reasonValid = !string.IsNullOrEmpty(m_RevertReason);
                        GUI.enabled = canRevert && reasonValid;
                        // revert 按鈕用紅字突出 (危險動作)
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Rule.Revert.Btn"), UCL_GUIStyle.ButtonTextRed,
                                              GUILayout.Width(UCL_GUIStyle.GetScaledSize(200))))
                        {
                            DoRevert(selected.ruleId, m_RevertReason);
                        }
                        GUI.enabled = true;
                        if (!reasonValid && canRevert)
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("Rule.Revert.NeedReason"), UCL_GUIStyle.LabelStyle);
                        }
                    }
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

        void MaybeAutoRefresh()
        {
            if (!m_AutoRefresh) return;
            // 區塊: 改用 Time.realtimeSinceStartup (runtime 可用), 取代 EditorApplication.timeSinceStartup
            // 物理意義: realtimeSinceStartup 在 Editor + runtime 都同義 (since process start), 不依賴 UnityEditor
            float now = Time.realtimeSinceStartup;
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
                // 走可 override 資料根;預設 = RepoRoot/AgentCommands/Rules (與舊行為相同)
                return Path.Combine(UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot, "Rules");
            }
        }
    }
}
#endif
