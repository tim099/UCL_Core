// 區塊職責：問題回報後台頁 —— 列出 / 篩選 / 認領 / 關單，stale 單一眼可見。
// 物理意義：Cmd_BugReport 的 UI 對偶。母版是 UCL_ProcessAdminPage（Tim 2026-08-18 指定），
//          抄它已經解掉的兩件事：刷新節流、破壞性動作二段確認。
// ⚠ 這頁是**處置台不是擺設**，前提是清單本身誠實 —— 那由 stale 自動標與 commit 閉環保證，
//   不是由這頁保證。所以本頁刻意不提供「手動標 stale」：人手動能標的狀態只會有人記得標一次。
// 2026-08-18 calli
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.BugReport;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 問題回報管理頁 —— 檢視 / 認領 / 關閉 <c>AgentCommands/BugReports/reports/*.md</c> 的單子。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_BugReport_System.md")]
    public class UCL_BugReportAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "問題回報管理";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // 區塊職責：顯示快取 —— 每 REFRESH_INTERVAL 秒重掃一次，不每次 OnGUI 都列目錄。
        List<UCL_BugReportEntry> m_Rows = new();
        double m_LastRefresh = -1.0;
        const double REFRESH_INTERVAL_SEC = 2.0;

        // 篩選：預設只看沒關的 —— 開這頁的人要處理的是還開著的單，不是看歷史。
        bool m_ShowClosed = false;
        string m_TypeFilter = "";       // 空＝全部
        int m_Expanded = -1;

        // 區塊職責：關單二段確認（照母版的 kill 手勢）。
        // 物理意義：**關單是對別人的宣告** —— 清單上少一筆等於大家不再看它。
        //          誤點的代價不是自己麻煩，是一隻還活著的 bug 從所有人的視野裡消失。
        int m_ArmedIndex = -1;
        string m_ArmedAction = "";
        double m_ArmedTime = -1.0;
        const double ARM_WINDOW_SEC = 5.0;

        GUIStyle m_SmallStyle;
        GUIStyle SmallStyle
        {
            get
            {
                if (m_SmallStyle == null)
                    m_SmallStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return m_SmallStyle;
            }
        }

        public static UCL_BugReportAdminPage Create()
        {
            var page = new UCL_BugReportAdminPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        void Refresh() => m_Rows = UCL_BugReportIO.LoadAll();

        protected override void ContentOnGUI()
        {
            double aNow = UnityEditor.EditorApplication.timeSinceStartup;
            if (m_LastRefresh < 0 || aNow - m_LastRefresh > REFRESH_INTERVAL_SEC)
            {
                Refresh();
                m_LastRefresh = aNow;
            }

            UCL_BugReportIO.CountOpen(out int aOpen, out int aStale, out int aBroken);

            // ── 讀數列：stale 不藏在篩選器後面 ────────────────────────────
            // 需要人主動去篩才看得到的警告等於沒有警告 —— 所以它印在最上面，永遠。
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"open {aOpen} 筆", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (aStale > 0)
                {
                    var c = GUI.color; GUI.color = new Color(1f, 0.6f, 0.3f);
                    GUILayout.Label($"　⚠ 其中 {aStale} 筆超過 {UCL_BugReportIO.STALE_DAYS} 天沒動作（stale）",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUI.color = c;
                }
                if (aBroken > 0)
                    GUILayout.Label($"　⚠ {aBroken} 筆時戳壞掉，算不出天數", SmallStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) Refresh();
                if (GUILayout.Button("開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_BugReportIO.EnsureDir();
                    UnityEditor.EditorUtility.RevealInFinder(UCL_BugReportIO.ReportsDir);
                }
            }

            using (new GUILayout.HorizontalScope())
            {
                m_ShowClosed = UCL_GUILayout.Toggle(m_ShowClosed);
                GUILayout.Label("含已關的單", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.Space(12);
                GUILayout.Label("type：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                DrawTypeBtn("全部", "");
                DrawTypeBtn("bug", "bug");
                DrawTypeBtn("doc", "doc");
                DrawTypeBtn("friction", "friction");
                DrawTypeBtn("suggestion", "suggestion");
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(6);
            var aNowUtc = DateTime.UtcNow;
            int aShown = 0;
            foreach (var e in m_Rows)
            {
                bool aClosed = e.IsClosed();
                if (!m_ShowClosed && aClosed) continue;
                if (!string.IsNullOrEmpty(m_TypeFilter)
                    && !string.Equals(e.type, m_TypeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                DrawRow(e, aNowUtc, aClosed);
                aShown++;
            }
            if (aShown == 0) GUILayout.Label("（沒有符合條件的單）", SmallStyle);
        }

        void DrawTypeBtn(string iLabel, string iValue)
        {
            bool aOn = m_TypeFilter == iValue;
            var c = GUI.color;
            if (aOn) GUI.color = new Color(0.6f, 0.85f, 1f);
            if (GUILayout.Button(iLabel, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                m_TypeFilter = iValue;
            GUI.color = c;
        }

        void DrawRow(UCL_BugReportEntry e, DateTime iNowUtc, bool iClosed)
        {
            int aDays = e.DaysSinceUpdate(iNowUtc);
            bool aStale = !iClosed && aDays >= UCL_BugReportIO.STALE_DAYS;

            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(m_Expanded == e.index ? "▼" : "▶",
                            UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(28))))
                        m_Expanded = m_Expanded == e.index ? -1 : e.index;

                    var c = GUI.color;
                    if (aStale) GUI.color = new Color(1f, 0.6f, 0.3f);
                    else if (iClosed) GUI.color = new Color(0.6f, 0.6f, 0.6f);
                    GUILayout.Label($"BUG-{e.index}", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label($"[{e.type}/{e.severity}]", SmallStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    GUILayout.Label(e.status, SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    GUILayout.Label(aDays < 0 ? "⚠ 壞時戳" : (aStale ? $"⚠ {aDays} 天" : $"{aDays} 天"),
                        SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label(e.title, UCL_GUIStyle.LabelStyle);
                    GUI.color = c;
                    GUILayout.FlexibleSpace();
                }

                if (m_Expanded != e.index) return;

                GUILayout.Label($"回報者 {e.reporter}"
                    + (string.IsNullOrEmpty(e.assignee) ? "" : $"　認領 {e.assignee}")
                    + (string.IsNullOrEmpty(e.commit_sha) ? "" : $"　commit {e.commit_sha}"), SmallStyle);
                string aPath = UCL_BugReportIO.ReportPath(e.index);
                GUILayout.Label(aPath, SmallStyle);

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("開啟報告", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (File.Exists(aPath)) UCL_MarkdownViewerPage.Create(aPath, aPath);
                        else Debug.LogError($"[BugReportAdmin] 報告檔不見了：{aPath}");
                    }
                    if (!iClosed)
                    {
                        DrawArmedButton(e, "resolved", "標記已修（resolved）");
                        DrawArmedButton(e, "wontfix", "不修（wontfix）");
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 區塊職責：二段確認按鈕 —— 第一次點只 arm，ARM_WINDOW_SEC 秒內再點同一顆才真的動手。
        // 物理意義：母版拿它防誤殺 process；這裡防的是「誤點一下，一隻還活著的 bug 從清單上消失」。
        // 數值影響：arm 狀態只存在記憶體；換頁 / 逾時自動失效（不留一顆待爆的按鈕）。
        void DrawArmedButton(UCL_BugReportEntry e, string iAction, string iLabel)
        {
            double aNow = UnityEditor.EditorApplication.timeSinceStartup;
            bool aArmed = m_ArmedIndex == e.index && m_ArmedAction == iAction
                          && aNow - m_ArmedTime < ARM_WINDOW_SEC;
            var c = GUI.color;
            if (aArmed) GUI.color = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button(aArmed ? $"再點一次確認：{iLabel}" : iLabel,
                    UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (aArmed)
                {
                    ApplyStatus(e, iAction);
                    m_ArmedIndex = -1; m_ArmedAction = "";
                }
                else
                {
                    m_ArmedIndex = e.index; m_ArmedAction = iAction; m_ArmedTime = aNow;
                }
            }
            GUI.color = c;
        }

        // ⚠ 寫入一律走 UCL_BugReportIO.Save —— 後台頁不自己碰檔案格式
        //   （兩個寫入端＝兩種格式漂移，而漂移是靜默的）。
        void ApplyStatus(UCL_BugReportEntry e, string iStatus)
        {
            e.status = iStatus;
            e.resolution = iStatus == "wontfix" ? "wontfix" : "fixed";
            e.updated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            UCL_BugReportIO.Save(e, "", "", "", "", "",
                $"{e.updated_at}　`{iStatus}`　由後台頁操作");
            Refresh();
            Debug.Log($"[BugReportAdmin] BUG-{e.index} → {iStatus}");
        }
    }
}
#endif
