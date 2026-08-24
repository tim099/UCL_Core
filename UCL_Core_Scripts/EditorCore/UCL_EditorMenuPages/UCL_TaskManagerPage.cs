// 區塊職責：任務管理後台頁 —— 列出 / 篩選 / 認領 / 結單，blocker 與 stale 一眼可見。
// 物理意義：Cmd_Task 的 UI 對偶。母版是 UCL_BugReportAdminPage（Tim 2026-08-24 指定），
//          抄它已經解掉的三件事：刷新節流、破壞性動作二段確認、警告不藏在篩選器後面。
//
// ⚠ 本頁**不是第二個寫入端**：所有寫入都走 `UCL_TaskIO`，而狀態機的判斷
//   （blocker 未解不准 Done / 有 QA 就不能替他簽）走與 Cmd 相同的 `OpenBlockers` / `QaGateBlocked`。
//   🩸 判準來自 2026-08-21 那一天的血證：同一條規則寫在兩個地方 ⇒ 兩份產線，
//     兩邊都不報錯，而它們遲早各說各話（C# 說「查不到就絕不 mint」、python 說「查不到就 derive」，
//     兩份都是我寫的）。⇒ 這頁只是**視圖 ＋ 呼叫**，不重新實作任何判斷。
//
// ⚠ 這頁刻意**沒有**：手動標 stale（人手動能標的狀態只會有人記得標一次）、
//   看板拖曳（我們一天 wake 一次、跨天換人接手，拖曳的價值拿不到而成本照付）、
//   「全部結單」批次鈕（「這張該不該關」機器判不了，而批次會讓那一格沒有人看過）。
// 2026-08-24 summit（TASK-0002）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands.TaskMgmt;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 任務與專案管理頁 —— 檢視 / 認領 / 結單 <c>AgentCommands/Tasks/tasks/*.md</c> 的單子。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Workflows/Task_Management_Workflow.md")]
    public class UCL_TaskManagerPage : UCL_CommonEditorPage
    {
        public override string WindowName => "任務與專案管理";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // 顯示快取 —— 每 REFRESH_INTERVAL 秒重掃一次，不每次 OnGUI 都列目錄。
        // 🩸 basecamp 2026-08-24（`382fe80`）：統計區每幀讀磁碟會讓 Editor 凍結。
        List<UCL_TaskEntry> m_Rows = new List<UCL_TaskEntry>();
        double m_LastRefresh = -1.0;
        const double REFRESH_INTERVAL_SEC = 2.0;

        // 統計與 blocker 也走同一份快取 —— 它們每筆都要 Find() 別的單，逐幀重算就是 N² 次磁碟讀。
        int m_Open, m_Stale, m_Broken, m_Blocked;
        readonly Dictionary<int, List<string>> m_BlockerCache = new Dictionary<int, List<string>>();

        // 篩選：預設只看沒關的 —— 開這頁的人要處理的是還開著的單，不是看歷史。
        bool m_ShowClosed = false;
        string m_StatusFilter = "";       // 空＝全部（未關）
        string m_PersonaFilter = "";      // 空＝全部人
        int m_Expanded = -1;

        // 破壞性動作二段確認（照母版的手勢）。
        // 物理意義：**結單是對別人的宣告** —— 清單上少一筆等於大家不再看它。
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

        public static UCL_TaskManagerPage Create()
        {
            var page = new UCL_TaskManagerPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        void Refresh()
        {
            m_Rows = UCL_TaskIO.LoadAll();
            UCL_TaskIO.CountStats(out m_Open, out m_Stale, out m_Broken, out m_Blocked);
            m_BlockerCache.Clear();
            foreach (var e in m_Rows)
                if (!e.IsClosed() && e.blocked_by.Count > 0)
                    m_BlockerCache[e.index] = UCL_TaskIO.OpenBlockers(e);
        }

        List<string> Blockers(UCL_TaskEntry e)
            => m_BlockerCache.TryGetValue(e.index, out var aList) ? aList : new List<string>();

        protected override void ContentOnGUI()
        {
            double aNow = UnityEditor.EditorApplication.timeSinceStartup;
            if (m_LastRefresh < 0 || aNow - m_LastRefresh > REFRESH_INTERVAL_SEC)
            {
                Refresh();
                m_LastRefresh = aNow;
            }

            // ── 讀數列：blocker 與 stale 不藏在篩選器後面 ────────────────────
            // 需要人主動去篩才看得到的警告等於沒有警告 —— 所以它印在最上面，永遠。
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"未關 {m_Open} 張", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (m_Blocked > 0)
                {
                    var c = GUI.color; GUI.color = new Color(1f, 0.45f, 0.45f);
                    GUILayout.Label($"　🛑 其中 {m_Blocked} 張被未解的 blocker 卡住",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUI.color = c;
                }
                if (m_Stale > 0)
                {
                    var c = GUI.color; GUI.color = new Color(1f, 0.6f, 0.3f);
                    GUILayout.Label($"　⚠ {m_Stale} 張 in_progress 超過 {UCL_TaskIO.STALE_DAYS} 天沒動作（stale）",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUI.color = c;
                }
                if (m_Broken > 0)
                    GUILayout.Label($"　⚠ {m_Broken} 張時戳壞掉，算不出天數（不算進 stale）",
                        SmallStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) Refresh();
                if (GUILayout.Button("開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_TaskIO.EnsureDir();
                    UnityEditor.EditorUtility.RevealInFinder(UCL_TaskIO.TasksDir);
                }
            }

            using (new GUILayout.HorizontalScope())
            {
                m_ShowClosed = UCL_GUILayout.Toggle(m_ShowClosed);
                GUILayout.Label("含已關（done / cancelled）", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.Space(12);
                GUILayout.Label("狀態：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                DrawFilterBtn("全部", ref m_StatusFilter, "");
                DrawFilterBtn("backlog", ref m_StatusFilter, "backlog");
                DrawFilterBtn("todo", ref m_StatusFilter, "todo");
                DrawFilterBtn("進行中", ref m_StatusFilter, "in_progress");
                DrawFilterBtn("待驗收", ref m_StatusFilter, "in_review");
                GUILayout.FlexibleSpace();
            }

            // 人員篩選：從**現有單子上實際出現過的 persona** 產生，不寫死名單
            //（寫死的名單會在有人加入時安靜地漏掉他）
            var aPersonas = m_Rows.SelectMany(e => e.participants.Select(p => p.persona))
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            if (aPersonas.Count > 0)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("參與者：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    DrawFilterBtn("全部", ref m_PersonaFilter, "");
                    foreach (var p in aPersonas) DrawFilterBtn(p, ref m_PersonaFilter, p);
                    GUILayout.FlexibleSpace();
                }
            }

            GUILayout.Space(6);
            var aNowUtc = DateTime.UtcNow;
            int aShown = 0;
            foreach (var e in m_Rows)
            {
                bool aClosed = e.IsClosed();
                if (!m_ShowClosed && aClosed) continue;
                if (!string.IsNullOrEmpty(m_StatusFilter)
                    && !string.Equals(e.status, m_StatusFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(m_PersonaFilter) && e.RolesOf(m_PersonaFilter).Count == 0) continue;
                DrawRow(e, aNowUtc, aClosed);
                aShown++;
            }
            if (aShown == 0)
            {
                // 「篩不到」與「系統裡沒東西」要分得開 —— 兩者長得一樣時，人會以為資料丟了
                GUILayout.Label($"（沒有符合條件的單。全部有 {m_Rows.Count} 張 —— 這是篩選的結果，不是系統空的）",
                    SmallStyle);
            }
        }

        void DrawFilterBtn(string iLabel, ref string ioField, string iValue)
        {
            bool aOn = ioField == iValue;
            var c = GUI.color;
            if (aOn) GUI.color = new Color(0.6f, 0.85f, 1f);
            if (GUILayout.Button(iLabel, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                ioField = iValue;
            GUI.color = c;
        }

        void DrawRow(UCL_TaskEntry e, DateTime iNowUtc, bool iClosed)
        {
            int aDays = e.DaysSinceUpdate(iNowUtc);
            bool aStale = !iClosed && aDays >= UCL_TaskIO.STALE_DAYS
                          && string.Equals(e.status, "in_progress", StringComparison.OrdinalIgnoreCase);
            var aBlockers = Blockers(e);

            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(m_Expanded == e.index ? "▼" : "▶",
                            UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(28))))
                        m_Expanded = m_Expanded == e.index ? -1 : e.index;

                    var c = GUI.color;
                    if (aBlockers.Count > 0) GUI.color = new Color(1f, 0.45f, 0.45f);
                    else if (aStale) GUI.color = new Color(1f, 0.6f, 0.3f);
                    else if (iClosed) GUI.color = new Color(0.6f, 0.6f, 0.6f);
                    GUILayout.Label(e.Id, UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(110)));
                    GUILayout.Label($"[{e.type}/{e.priority}]", SmallStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    GUILayout.Label(e.status, SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    GUILayout.Label(aDays < 0 ? "⚠ 壞時戳" : (aStale ? $"⚠ {aDays} 天" : $"{aDays} 天"),
                        SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (aBlockers.Count > 0)
                        GUILayout.Label($"🛑{aBlockers.Count}", SmallStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    GUILayout.Label(e.title, UCL_GUIStyle.LabelStyle);
                    GUI.color = c;
                    GUILayout.FlexibleSpace();
                }

                if (m_Expanded != e.index) return;

                // ── 展開區：參與者、依賴、commit、檔案路徑 ──────────────────
                GUILayout.Label($"開單 {Nz(e.reporter)}　參與：{Participants(e)}", SmallStyle);
                if (e.QaPersonas().Count == 0 && !iClosed)
                    GUILayout.Label("⚠ 這張單沒有指名 QA ⇒ 結單沒有閘會擋（由開單人或 PM 判）", SmallStyle);
                if (e.blocked_by.Count > 0 || e.blocks.Count > 0 || e.related_to.Count > 0)
                    GUILayout.Label($"blocked_by {Ids(e.blocked_by)}　blocks {Ids(e.blocks)}"
                        + $"　related_to {Ids(e.related_to)}", SmallStyle);
                foreach (var b in aBlockers)
                {
                    var c = GUI.color; GUI.color = new Color(1f, 0.45f, 0.45f);
                    GUILayout.Label("🛑 未解 blocker：" + b, SmallStyle);
                    GUI.color = c;
                }
                if (e.commit_shas.Count > 0)
                    GUILayout.Label("commit：" + string.Join(" ", e.commit_shas), SmallStyle);
                string aPath = UCL_TaskIO.TaskPath(e.index);
                GUILayout.Label(aPath, SmallStyle);

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("開啟單檔", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (File.Exists(aPath)) UCL_MarkdownViewerPage.Create(aPath, aPath);
                        else Debug.LogError($"[TaskManager] 單檔不見了：{aPath}");
                    }
                    if (!iClosed)
                    {
                        // 狀態推進（非破壞性 ⇒ 單擊即動，不必二段）
                        if (!string.Equals(e.status, "in_progress", StringComparison.OrdinalIgnoreCase)
                            && GUILayout.Button("→ 進行中", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            ApplyStatus(e, "in_progress");
                        if (!string.Equals(e.status, "in_review", StringComparison.OrdinalIgnoreCase)
                            && GUILayout.Button("→ 待驗收", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            ApplyStatus(e, "in_review");

                        // 結單：破壞性 ⇒ 二段確認，且 blocker 未解時**按鈕本身就不給按**
                        if (aBlockers.Count > 0)
                        {
                            var c = GUI.color; GUI.color = new Color(0.6f, 0.6f, 0.6f);
                            GUILayout.Label($"（🛑 {aBlockers.Count} 個 blocker 未解 ⇒ 不能結單）",
                                SmallStyle, GUILayout.ExpandWidth(false));
                            GUI.color = c;
                        }
                        else
                        {
                            DrawArmedButton(e, "done", "結單（done）");
                        }
                        DrawArmedButton(e, "cancelled", "取消（cancelled）");
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 區塊職責：二段確認按鈕 —— 第一次點只 arm，ARM_WINDOW_SEC 秒內再點同一顆才真的動手。
        // 數值影響：arm 狀態只存在記憶體；換頁 / 逾時自動失效（不留一顆待爆的按鈕）。
        void DrawArmedButton(UCL_TaskEntry e, string iAction, string iLabel)
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

        // ===========================================================
        // 區塊職責：狀態變更 —— **寫入一律走 UCL_TaskIO.Save**，後台頁不自己碰檔案格式
        //   （兩個寫入端＝兩種格式漂移，而漂移是靜默的）。
        // 物理意義：兩道閘與 Cmd 端**共用同一個判斷函式**，不在這裡重寫：
        //   ① blocker 未解不准 done（`OpenBlockers`）—— UI 已先禁用按鈕，這裡是第二層
        //     （UI 的禁用是給眼睛的，這一層是給資料的；只有前者的話，快捷路徑一開就破）
        //   ② 單上有 QA 而按鈕是「別人」按的 ⇒ 後台頁的操作者是 Tim，
        //     所以這裡帶 `qa_note` 等價於 RFC §2④ 的「附驗收紀錄」，並在時間線寫明是後台代簽。
        // ===========================================================
        void ApplyStatus(UCL_TaskEntry e, string iStatus)
        {
            if (iStatus == "done")
            {
                var aBlockers = UCL_TaskIO.OpenBlockers(e);
                if (aBlockers.Count > 0)
                {
                    Debug.LogError($"[TaskManager] {e.Id} 不能結單：還有 {aBlockers.Count} 個未解 blocker —— "
                        + string.Join("；", aBlockers));
                    Refresh();
                    return;
                }
            }
            string aNow = UCL_TaskIO.NowUtc();
            var aQa = e.QaPersonas();
            string aNote = "";
            if (iStatus == "done" && aQa.Count > 0)
                aNote = $"（後台頁代簽 —— 單上的 QA 是 {string.Join(" / ", aQa)}）";

            string aFrom = e.status;
            e.status = iStatus;
            if (iStatus == "done" || iStatus == "cancelled") e.closed_at = aNow;
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`{iStatus}`　由後台頁操作（原狀態 {aFrom}）{aNote}");
            Refresh();
            Debug.Log($"[TaskManager] {e.Id} {aFrom} → {iStatus}{aNote}");
        }

        static string Participants(UCL_TaskEntry e)
            => e.participants.Count == 0
             ? "**無**（沒有人在做這件事）"
             : string.Join("、", e.participants.Select(p => $"{p.persona}({p.role})"));

        static string Ids(List<int> iList)
            => iList == null || iList.Count == 0 ? "—"
             : string.Join(" ", iList.Select(i => "TASK-" + i.ToString("0000")));

        static string Nz(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }
}
#endif
