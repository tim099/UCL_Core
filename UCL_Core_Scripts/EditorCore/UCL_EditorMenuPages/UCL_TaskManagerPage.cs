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
        // 狀態篩選直接用 UCL_TaskStatus（Tim 2026-08-26：不另開第二個 enum，`all` 加在同一份）——
        // 成員名即顯示文字（PopupAuto enum 版回 key 原文）。all 不是狀態、只給篩選用，守衛見 enum 註解。
        UCL_TaskStatus m_StatusFilter = UCL_TaskStatus.all;
        // type 篩選同一個模式（Tim 2026-08-28：`all` 入 UCL_TaskType）——
        // bug/epic 混在任務海裡，沒有一鍵篩等於沒有清單（問題回報頁母版的三不可丟之一）。
        UCL_TaskType m_TypeFilter = UCL_TaskType.all;
        string m_PersonaFilter = "";      // 空＝全部人
        int m_Expanded = -1;

        // PopupSearch 需要一個 UCL_ObjectDictionary 當它的展開/搜尋字快取容器。
        // ⚠ **不與折疊或其他 UI 狀態共用同一個容器** —— 🩸 既有血證：共用時資料重載路徑上的
        //   `Clear()` 會把另一邊的值一起清掉（症狀是「收不起來」而沒有任何錯誤）。
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();

        // 🗑 STATUS_VALUES / STATUS_LABELS 兩張字串表已退場（Tim 2026-08-26 改用 enum）——
        //   「值與顯示分開兩張表」的維護債由 enum 一次收掉：成員名既是 wire 值也是顯示文字。

        // 破壞性動作二段確認（照母版的手勢）。
        // 物理意義：**結單是對別人的宣告** —— 清單上少一筆等於大家不再看它。
        int m_ArmedIndex = -1;
        string m_ArmedAction = "";
        double m_ArmedTime = -1.0;
        const double ARM_WINDOW_SEC = 5.0;

        // 區塊職責：留言輸入 —— 只在展開的那一張單上（Tim 2026-08-24：展開 Task 才顯示留言）。
        // 物理意義：草稿存在**記憶體且綁定單號** —— 換單、換頁就消失。
        //   ⚠ 不做「跨單共用一個草稿」：那會讓在 A 單打的字送進 B 單，而送出之後看起來一切正常。
        int m_DraftIndex = -1;
        string m_Draft = "";
        // 後台頁的操作者。留言要有作者，而作者不能是「後台頁」——
        // 一則沒有人負責的留言，讀的人無從判斷它的份量。
        string m_Author = "Tim";

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
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) Refresh();

            // 人員清單：從**現有單子上實際出現過的 persona** 產生，不寫死名單
            //（寫死的名單會在有人加入時安靜地漏掉他）
            var aPersonas = m_Rows.SelectMany(e => e.participants.Select(p => p.persona))
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

            GUILayout.Label("Status", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            // enum 下拉（Tim 2026-08-26）：顯示即成員名原文；真相源是 enum 值本身，
            // 沒有「index 指到別人」的問題（那是字串清單版需要 DrawFilterPopup 防的坑）。
            m_StatusFilter = UCL_GUILayout.PopupAuto(m_StatusFilter, m_Dic, "StatusFilter",
                10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));

            GUILayout.Label("Type", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            m_TypeFilter = UCL_GUILayout.PopupAuto(m_TypeFilter, m_Dic, "TypeFilter",
                10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));

            GUILayout.Label("參與者", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

            // 選項永遠 ≥ 1（第一項是「全部」）—— PopupSearch 空清單會 LogError
            var aPersonaValues = new List<string> { "" };
            aPersonaValues.AddRange(aPersonas);
            var aPersonaLabels = new List<string> { "全部" };
            aPersonaLabels.AddRange(aPersonas);
            DrawFilterPopup(ref m_PersonaFilter, aPersonaValues, aPersonaLabels, "PersonaFilter",
                GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));


            if (GUILayout.Button("Open Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_TaskIO.EnsureDir();
                UnityEditor.EditorUtility.RevealInFinder(UCL_TaskIO.TasksDir);
            }
        }
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
                m_ShowClosed = UCL_GUILayout.CheckBox(m_ShowClosed);
                GUILayout.Label("含(done / cancelled)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));


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
            }



            // ⚠ 篩到已關的狀態卻沒開「含已關」⇒ 清單必定是空的，而**兩個設定各自看起來都正常**。
            //   ⇒ 這裡直接放行並說明，不讓人對著一個空清單找原因。
            bool aClosedFilter = m_StatusFilter == UCL_TaskStatus.done || m_StatusFilter == UCL_TaskStatus.cancelled;
            if (aClosedFilter && !m_ShowClosed)
                GUILayout.Label($"ℹ 狀態選了 `{m_StatusFilter}`（已關）⇒ 本次自動含已關的單"
                    + "（否則清單必定是空的，而那看起來像「沒有這種單」）", SmallStyle);

            GUILayout.Space(6);
            var aNowUtc = DateTime.UtcNow;
            int aShown = 0;
            // stale 置頂（Tim 2026-08-28，抄問題回報頁母版：警告不藏在排序後面）——
            // 判準與 DrawRow 的標色同一把尺（in_progress 且 ≥ STALE_DAYS 沒動），其餘維持單號序。
            var aOrdered = m_Rows.OrderByDescending(e => !e.IsClosed()
                    && e.status == UCL_TaskStatus.in_progress
                    && e.DaysSinceUpdate(aNowUtc) >= UCL_TaskIO.STALE_DAYS)
                .ThenBy(e => e.index);
            foreach (var e in aOrdered)
            {
                bool aClosed = e.IsClosed();
                if (!m_ShowClosed && !aClosedFilter && aClosed) continue;
                if (m_StatusFilter != UCL_TaskStatus.all && e.status != m_StatusFilter) continue;
                if (m_TypeFilter != UCL_TaskType.all && e.type != m_TypeFilter) continue;
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

        // ===========================================================
        // 區塊職責：篩選下拉（Tim 2026-08-24：改用 `UCL_GUILayout.PopupSearch`）。
        //
        // ⚠ **真相源是字串，不是索引。** PopupSearch 收/回的是 index，而選項清單是**從資料算出來的**
        //   （參與者清單會隨單子增減而變長變短、順序也可能變）。
        //   把 index 存下來的話，清單一變同一個數字就指到**別人** —— 而那個錯誤看起來完全正常
        //   （下拉顯示一個合法的名字、清單也篩出東西，只是篩的是另一個人）。
        //   ⇒ 所以每一幀從字串反查 index；查不到就退回「全部」並**說出來**。
        //
        // ⚠ 為什麼用 `PopupSearch` 而不是 `PopupSearchCache`：後者的快取失效條件是**選項數量變了**，
        //   而「數量不變、內容換了」（一個 persona 退出、另一個加入）它偵測不到 ——
        //   那正是這裡最可能發生的情況。
        // ===========================================================
        void DrawFilterPopup(ref string ioField, IList<string> iValues, IList<string> iLabels,
            string iKey, params GUILayoutOption[] iOptions)
        {
            int aIdx = -1;
            for (int i = 0; i < iValues.Count; i++)
                if (string.Equals(iValues[i], ioField, StringComparison.OrdinalIgnoreCase)) { aIdx = i; break; }

            if (aIdx < 0)
            {
                // 上次選的值已經不在清單裡（那個人不再出現在任何單子上）
                // ⇒ 退回「全部」並留一行 —— 靜默退回會讓人以為篩選還生效
                GUILayout.Label($"ℹ 原本選的 `{ioField}` 已不在任何單子上 ⇒ 退回全部", SmallStyle,
                    GUILayout.ExpandWidth(false));
                ioField = iValues.Count > 0 ? iValues[0] : "";
                aIdx = 0;
            }
            int aNext = UCL_GUILayout.PopupSearch(aIdx, iLabels, m_Dic, iKey, iOptions);
            if (aNext != aIdx && aNext >= 0 && aNext < iValues.Count) ioField = iValues[aNext];
        }

        // 🗑 `DrawFilterBtn`（一排橫向按鈕）已隨下拉選單退場 ——
        //   留著死碼會讓下一個人以為還有第二條篩選路徑（而它永遠不會被呼叫）。

        void DrawRow(UCL_TaskEntry e, DateTime iNowUtc, bool iClosed)
        {
            int aDays = e.DaysSinceUpdate(iNowUtc);
            bool aStale = !iClosed && aDays >= UCL_TaskIO.STALE_DAYS
                          && e.status == UCL_TaskStatus.in_progress;
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
                    GUILayout.Label($"[{e.type}/{e.priority}"
                        + (e.severity == UCL_TaskSeverity.none ? "" : $"/{e.severity}") + "]", SmallStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    GUILayout.Label(e.status.ToString(), SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    //GUILayout.Label(aDays < 0 ? "⚠ 壞時戳" : (aStale ? $"⚠ {aDays} 天" : $"{aDays} 天"),
                    //SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label(e.ParticipantsName, SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
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
                        // 狀態推進（非破壞性 ⇒ 單擊即動，不必二段）—— 按鈕文字＝enum key 原文（Tim 2026-08-26）
                        if (e.status != UCL_TaskStatus.in_progress
                            && GUILayout.Button($"→ {UCL_TaskStatus.in_progress}", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            ApplyStatus(e, UCL_TaskStatus.in_progress);
                        if (e.status != UCL_TaskStatus.in_review
                            && GUILayout.Button($"→ {UCL_TaskStatus.in_review}", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            ApplyStatus(e, UCL_TaskStatus.in_review);

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
                            DrawArmedButton(e, UCL_TaskStatus.done, UCL_TaskStatus.done.ToString());
                        }
                        DrawArmedButton(e, UCL_TaskStatus.cancelled, UCL_TaskStatus.cancelled.ToString());
                    }
                    GUILayout.FlexibleSpace();
                }

                DrawComments(e);
            }
        }

        // ===========================================================
        // 區塊職責：留言區（**只在展開時畫** —— Tim 2026-08-24 指定）。
        // 物理意義：列表列要能一眼掃完，所以討論不能佔用它的高度；
        //          而展開的那一張單是「我正在處理這件事」，那時討論才是主角。
        // 數值影響：讀取用已載入的 `e.comments`（LoadAll 時一併解析，不另外讀磁碟）；
        //          送出走 `UCL_TaskIO.Save` ＋ `UCL_TaskNotify`（與 Cmd 同一條路，不是第二個寫入端）。
        // ===========================================================
        void DrawComments(UCL_TaskEntry e)
        {
            GUILayout.Space(4);
            GUILayout.Label($"💬 留言（{e.comments.Count}）", UCL_GUIStyle.LabelStyle);
            if (e.comments.Count == 0)
                GUILayout.Label("（還沒有人留言）", SmallStyle);
            foreach (var c in e.comments)
            {
                using (new GUILayout.VerticalScope(GUI.skin.box))
                {
                    GUILayout.Label($"#{c.id}　**{c.persona}**　{LocalTime(c.at)}", SmallStyle);
                    GUILayout.Label(c.body, SmallStyle);
                }
            }

            // ── 輸入列 ────────────────────────────────────────────────
            if (m_DraftIndex != e.index) { m_DraftIndex = e.index; m_Draft = ""; }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("作者", SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                m_Author = GUILayout.TextField(m_Author, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                GUILayout.Label("　留言（送出後會同步發到酒館並 @ 參與者）", SmallStyle);
            }
            m_Draft = GUILayout.TextArea(m_Draft, GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(48)));
            using (new GUILayout.HorizontalScope())
            {
                bool aCanSend = !string.IsNullOrWhiteSpace(m_Draft) && !string.IsNullOrWhiteSpace(m_Author);
                // ⚠ 空白留言不給送 —— 一則空留言會推進 updated_at（讓 stale 計時歸零），
                //   於是「有人在動它」這個讀數會被一個沒有內容的動作洗掉。
                if (!aCanSend)
                {
                    var c = GUI.color; GUI.color = new Color(0.6f, 0.6f, 0.6f);
                    GUILayout.Label(string.IsNullOrWhiteSpace(m_Author)
                        ? "（要填作者 —— 沒有作者的留言，讀的人無從判斷它的份量）"
                        : "（留言是空的 ⇒ 不給送：空留言會把 stale 計時洗掉）", SmallStyle);
                    GUI.color = c;
                }
                else if (GUILayout.Button("送出留言", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    AddComment(e, m_Author.Trim(), m_Draft.Trim());
                    m_Draft = "";
                    GUIUtility.keyboardControl = 0;
                }
                GUILayout.FlexibleSpace();
            }
        }

        // ⚠ 寫入與通知都走與 Cmd 相同的兩支（`UCL_TaskIO.Save` / `UCL_TaskNotify`）——
        //   後台頁不自己組 md、也不自己發酒館訊息（兩份格式會漂，而漂移是靜默的）。
        void AddComment(UCL_TaskEntry e, string iAuthor, string iBody)
        {
            string aNow = UCL_TaskIO.NowUtc();
            var aComment = new UCL_TaskComment
            { id = UCL_TaskIO.NextCommentId(e), persona = iAuthor, at = aNow, body = iBody };
            e.comments.Add(aComment);
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`comment`　{iAuthor} 留言 #{aComment.id}（後台頁）");
            UCL_TaskNotify.PostFireAndForget(e, UCL_TaskNotify.Kind.Comment, iAuthor, "", iBody);
            Refresh();
            Debug.Log($"[TaskManager] {e.Id} 留言 #{aComment.id} by {iAuthor}（已請酒館通知；失敗會印 [TaskNotify]）");
        }

        /// <summary>UTC ISO → 本地 `MM-dd HH:mm`。解析不了就**原樣回**（不假裝知道時間）。</summary>
        static string LocalTime(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return "(無時間)";
            if (!DateTime.TryParse(iIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)) return iIso;
            return aUtc.ToLocalTime().ToString("MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // 區塊職責：二段確認按鈕 —— 第一次點只 arm，ARM_WINDOW_SEC 秒內再點同一顆才真的動手。
        // 數值影響：arm 狀態只存在記憶體；換頁 / 逾時自動失效（不留一顆待爆的按鈕）。
        void DrawArmedButton(UCL_TaskEntry e, UCL_TaskStatus iAction, string iLabel)
        {
            double aNow = UnityEditor.EditorApplication.timeSinceStartup;
            bool aArmed = m_ArmedIndex == e.index && m_ArmedAction == iAction.ToString()
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
                    m_ArmedIndex = e.index; m_ArmedAction = iAction.ToString(); m_ArmedTime = aNow;
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
        void ApplyStatus(UCL_TaskEntry e, UCL_TaskStatus iStatus)
        {
            // 守衛之二（見 UCL_TaskStatus 的 all 註解）：all 是篩選成員不是狀態 ——
            // 少了這道，`status: all` 會落盤而且看起來像一張正常的單。
            if (iStatus == UCL_TaskStatus.all)
            {
                Debug.LogError($"[TaskManager] {e.Id} 拒寫 status=all —— all 是篩選用成員，不是任務狀態");
                return;
            }
            if (iStatus == UCL_TaskStatus.done)
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
            if (iStatus == UCL_TaskStatus.done && aQa.Count > 0)
                aNote = $"（後台頁代簽 —— 單上的 QA 是 {string.Join(" / ", aQa)}）";

            var aFrom = e.status;
            e.status = iStatus;   // 成員名＝wire 字串（UCL_TaskStatus 的約定；frontmatter 落盤仍是字串）
            if (iStatus == UCL_TaskStatus.done || iStatus == UCL_TaskStatus.cancelled) e.closed_at = aNow;
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
