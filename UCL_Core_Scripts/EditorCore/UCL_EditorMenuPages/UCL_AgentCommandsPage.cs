
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// Agent Commands Page — 在 Editor 內手動查看 / 觸發 / 新增 agent commands。
// 用 PopupSearchCache 把可用指令收成一個可搜尋的下拉選單；選定後直接顯示 metadata + 新增表單。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Agent Commands 頁面 — agent 透過 queue.json 排隊指令、使用者按按鈕觸發。
    ///
    /// 架構：繼承 <see cref="UCL_CommonEditorPage"/>。
    /// 操作放在 TopBarButtons 裡（Refresh / Run / Open Folder）；
    /// ContentOnGUI 列出隊列、可搜尋的 Command 下拉 + 新增表單。
    ///
    /// 文件關聯：對應的多語系說明文件位於 Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md
    /// 物理意義：透過 [HelpURL] 將編輯器頁面與本地化文檔綁定，編輯器內的 ? 按鈕會依當前語系跳轉到對應 md。
    /// 數值影響：無執行期影響，僅影響 Inspector / 編輯器內的說明連結指向。
    /// Docs~\en\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\ja\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\zh-Hans\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\zh-Hant\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md")]
    public class UCL_AgentCommandsPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Agent Commands";

        static public UCL_AgentCommandsPage Create()
        {
            return UCL_EditorPage.Create<UCL_AgentCommandsPage>();
        }

        // ==== 新增表單的暫存欄位 ====
        // 區塊職責：保留使用者在表單中尚未送出的編輯狀態
        // 物理意義：m_SelectedCmdIdx 對應 PopupSearchCache 的索引，指向 UCL_AgentCommandRegistry.ListHandlers() 的某一筆
        // 數值影響：Add 按下時依此狀態建立一筆新的 UCL_AgentCommand 並寫回 queue.json
        int m_SelectedCmdIdx = 0;
        UCL_AgentCommandMode m_NewMode = UCL_AgentCommandMode.OneShot;
        string m_NewDescription = "";
        string m_NewArgsRaw = ""; // 形如 key1=value1;key2=value2
        //Vector2 m_Scroll = Vector2.zero;

        // PopupSearchCache 需要一個 UCL_ObjectDictionary 作為 cache 容器
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        // 區塊職責：History 每筆條目的折疊狀態 — **刻意跟 m_Dic 分開**（比照 UCL_ControlPanelPage.m_FoldDic）
        // 物理意義：折疊是使用者的 UI 偏好（該長存）；PopupSearchCache 是衍生資料（選項變了該失效）。
        //          共用一個 dic 時，資料重載路徑上的 Clear() 會把折疊值一起清掉 ——
        //          症狀是「收不起來 / 一動就全展開」，看起來像 key 撞名，其實是被連坐清掉。
        // 數值影響：key = 條目 Id（唯一），預設 false（收合）；純 UI 狀態，不寫任何檔。
        readonly UCL_ObjectDictionary m_HistoryFoldDic = new UCL_ObjectDictionary();
        // 區塊職責：大區塊（欄位群）的折疊狀態（Tim 2026-08-21 指派「依欄位做折疊」）
        // 物理意義：本頁已長到八個區塊（queue 現況／新增表單／失敗紀錄／模板／歷史／提示…），
        //          全展開時找一個東西要捲三四頁。與 m_HistoryFoldDic 分開的理由同上一段：
        //          那是「逐筆條目」的折疊，這是「整個區塊」的折疊，兩者的清除時機不同。
        // 數值影響：每區塊一個 bool，生命週期＝頁面 instance；純 UI，不寫檔。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        // ==== 失敗紀錄面板狀態（Tim 2026-08-21 派單）====
        // 區塊職責：`_cmd_failed/` 的顯示快取與篩選
        // 物理意義：失敗紀錄是**待處理清單**（補跑或刪掉才消失），所以要能搜尋、分頁；
        //          m_LegacyFailedCount 是「改版前沒有結構化紀錄、因此無法補跑」的筆數 ——
        //          它由 `_cmd_errors/`（**永久保存**的失敗報告）數出來，不是 `_cmd_results/`
        //          （那個 3 天就被 Purge，拿它當分母會讓舊失敗一天天憑空變少）。
        //          只在 Refresh / 首次繪製時算一次（Draw 裡不碰磁碟）。
        // 數值影響：純顯示；-1 = 還沒掃過（**不是 0** —— 沒掃過與掃到零不能長得一樣）。
        List<UCL_AgentCommandFailedEntry> m_FailedCache;
        string m_FailedSearch = "";
        int m_FailedPage = 0;
        int m_LegacyFailedCount = -1;

        // 區塊職責：記住上一幀選中的指令索引，用來偵測「使用者剛換了指令」
        // 物理意義：換指令代表舊的 args 已經不屬於當前指令 → 自動填入新指令的 ExampleArgs
        //          （Tim 2026-08-21：不該還要人再按一次「填入範例」）。
        // 數值影響：-1 = 尚未初始化（首次繪製也會觸發一次填入，那正是想要的行為）。
        //          ⚠ Apply（模板／歷史／補跑填回）路徑會顯式同步這個值 ——
        //          否則下一幀會把剛套用的 args 蓋成範例值，而那看起來像「Apply 沒生效」。
        int m_LastCmdIdxForExample = -1;

        // ==== 顯示用快取（每幀重讀檔太重，只在按下 Refresh 時更新）====
        UCL_AgentCommandQueueData m_Cached;

        // ==== Queue 選擇器狀態（Tim 2026-07-15 拍板 — 可切換觀察/操作不同 persona 的 queue）====
        // 區塊職責：讓本頁不再寫死 legacy 共用 queue — PopupSearchCache 下拉切換 default / per-agent queue。
        // 物理意義：底層 UCL_AgentCommandQueue / Runner / Trigger 的 API 全部已參數化 agentId
        //          （null=共用 queue.json，非 null=queues/queue-<id>.json），本頁只是把參數穿線到 UI。
        // 數值影響：切換後所有讀寫（Load/Save/Add/Remove/ClearFailed/RunPending/OpenFolder/Trigger 狀態）
        //          都作用在選中的 queue；選擇記進 EditorPrefs 跨 session 保留。
        int m_SelectedQueueIdx = 0;
        List<string> m_QueueAgentIds;   // 與 m_QueueOptions 同步的值清單；[0]=null（共用 default）
        List<string> m_QueueOptions;    // PopupSearchCache 顯示字串（含 pending 數徽章）
        const string PrefKey_SelectedQueue = "UCL.AgentCmd.SelectedQueue";

        /// <summary>當前選中 queue 的 agentId；null = legacy 共用 queue.json。</summary>
        string SelectedAgentId =>
            (m_QueueAgentIds != null && m_SelectedQueueIdx >= 0 && m_SelectedQueueIdx < m_QueueAgentIds.Count)
                ? m_QueueAgentIds[m_SelectedQueueIdx] : null;

        // ==== 分頁狀態（Tim 2026-07-15 拍板 — History/Templates >10 筆渲染性能炸裂 → 每頁 10 筆）====
        const int PageSize = 10;
        int m_TemplatePage = 0;
        int m_HistoryPage = 0;

        // ==== 指令說明折疊（Tim 2026-07-15 拍板 — ArgsSchema 長的 Cmd（如 Tavern 30+ 行）擠壓下方操作區）====
        bool m_ShowCmdInfo = false;

        // ==== Templates / History UI 狀態 ====
        // 區塊職責：紀錄兩個展開區塊的展開/隱藏狀態、搜尋字串、清理參數、以及檔案快取
        // 物理意義：避免每幀掃 Templates/ 與 History/ 資料夾 — 只有按下對應 Refresh 才重讀
        // 數值影響：m_HistoryCache / m_TemplateCache 為純顯示用快取，操作型動作（Add/Delete）後立即重整
        bool m_ShowTemplates = false;                                  // Templates 區塊是否展開
        bool m_ShowHistory = false;                                    // History 區塊是否展開
        string m_TemplateSearch = "";                                  // Templates 搜尋關鍵字
        string m_HistorySearch = "";                                   // History 搜尋關鍵字
        // 區塊職責：History 的 queue 篩選（Tim 2026-08-18 派單：查最近 anonymous 跑了什麼）
        // 物理意義：存**字串不是索引** —— 選項清單每幀由現有資料重建，筆數一變索引就指到別人身上，
        //          而那種錯位不會報錯（它會安靜地篩出另一條 queue 的結果）。
        // 數值影響：空字串 = 全部；HistoryQueueAll 以外的值即為要篩的 queue id。
        string m_HistoryQueueFilter = "";
        string m_SaveTemplateName = "";                                // 「Save as Template」用的暫存名稱
        string m_SaveTemplateNotes = "";                               // Template 補充筆記
        int m_HistoryAgeDays = 30;                                     // 清理「N 天沒重用」的門檻
        List<UCL_AgentCommandHistoryEntry> m_HistoryCache;             // 最近一次載入的 History 列表
        List<UCL_AgentCommandTemplate> m_TemplateCache;                // 最近一次載入的 Template 列表
        //Vector2 m_TemplateScroll = Vector2.zero;                       // Templates 區塊內捲動位置
        //Vector2 m_HistoryScroll = Vector2.zero;                        // History 區塊內捲動位置


        // 區塊職責：長字串 Label 用的 wordWrap 樣式（lazy 建一次後重用）
        // 物理意義：UCL_GUIStyle.LabelStyle 預設不換行 → History 條目的 Args 一旦長到一行
        //          展不下，整個 VerticalScope("box") 寬度會被它撐爆 → 同列 FlexibleSpace
        //          把右側 Apply / Re-Add / → 模板 / Delete 按鈕推到視窗外。給 Args / Id /
        //          Description 套上 wordWrap=true 即可正常折行，box 寬度回到視覺可見範圍。
        // 數值影響：純樣式快取，無副作用；richText 維持開（Id 用 <i>...</i> 顯示）
        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                {
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
                    {
                        wordWrap = true,
                        richText = true,
                    };
                }
                return m_WrapLabelStyle;
            }
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_Cached = UCL_AgentCommandQueue.Load(SelectedAgentId);
                // 區塊職責：Refresh 時一併重整 History / Template 快取 + queue 選項清單
                // 物理意義：使用者按下 Refresh 通常是因為「外部剛動過檔案」，所以全部 cache 都重抓；
                //          新 persona 第一次 submit 會長出新 queue 檔 → 選擇器也要看得到
                // 數值影響：純讀檔，不寫
                m_HistoryCache = null;
                m_TemplateCache = null;
                // 失敗紀錄也一起重抓 —— 它跟 History/Template 同一個理由（外部剛動過檔）。
                // m_LegacyFailedCount 設 -1（＝還沒掃過），不是 0：0 是「掃過且沒有」。
                m_FailedCache = null;
                m_LegacyFailedCount = -1;
                RefreshQueueOptions();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.RunPending"), UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
            {
                // 選中 per-agent queue 時直接跑該 queue 的 Runner（Menu_RunPending 只跑 default）
                if (string.IsNullOrEmpty(SelectedAgentId)) UCL_AgentCommandRunner.Menu_RunPending();
                else UCL_AgentCommandRunner.RunAsync(SelectedAgentId, default).Forget();
                DelayedRefresh().Forget();
            }
            // 區塊職責：清除 queue 內所有 LastRunResult == "Failed" 的條目，避免下次 Run 重跑壞掉的舊指令。
            // 物理意義：失敗的 OneShot 預設會留在 queue（保留 LastRunError 給作者除錯），但若 cmd 本身打錯字
            //          (例 Type 拼錯 → Unknown command type)，留著也只會每次重試都失敗，不如一鍵清掉。
            // 數值影響：寫回 queue.json；不影響任何成功跑過的條目與 Repeatable。
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ClearFailed"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.2f)), GUILayout.ExpandWidth(false)))
            {
                ClearFailedCommands();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                // per-agent queue 選中時開 queues/ 子資料夾（default 走原本的 Menu 入口）
                if (string.IsNullOrEmpty(SelectedAgentId)) UCL_AgentCommandRunner.Menu_OpenQueueFolder();
                else
                {
                    UCL_AgentCommandQueue.EnsureDir(SelectedAgentId);
                    UnityEditor.EditorUtility.RevealInFinder(UCL_AgentCommandQueue.GetQueuePath(SelectedAgentId));
                }
            }
        }

        // ===========================================================
        // 區塊：Queue 選擇器（PopupSearchCache）
        // 職責：掃描現存 queue（default + queues/queue-*.json）建選項清單，切換即重載該 queue。
        // 物理意義：agentId 是 caller（run_cmd.py --agent-id / --lane）自由填的字串，選擇器照 raw id
        //          顯示（含 ~lane 複合 id）不美化 — 美化會把 id typo 這種 bug 藏起來。
        // 數值影響：切換寫 EditorPrefs（跨 session 記住上次選擇）；選項字串帶 pending 數徽章。
        // ===========================================================
        void RefreshQueueOptions()
        {
            m_QueueAgentIds = new List<string> { null };
            m_QueueOptions = new List<string>();
            int defaultCount = UCL_AgentCommandQueue.Load()?.Commands?.Count ?? 0;
            m_QueueOptions.Add(string.Format(UCL_CodeLocalize.Get("AgentCmd.QueueDefault"), defaultCount));
            foreach (var id in UCL_AgentCommandQueue.ListAgentIds())
            {
                m_QueueAgentIds.Add(id);
                int n = UCL_AgentCommandQueue.Load(id)?.Commands?.Count ?? 0;
                m_QueueOptions.Add($"{id} ({n})");
            }
            if (m_SelectedQueueIdx >= m_QueueAgentIds.Count) m_SelectedQueueIdx = 0;
            m_Dic.Clear();   // PopupSearchCache 選項變了 → 清 cache 讓下拉重取
        }

        void DrawQueueSelector()
        {
            // 首次繪製：從 EditorPrefs 還原上次選中的 queue（找不到該 id 則回 default）
            if (m_QueueAgentIds == null)
            {
                RefreshQueueOptions();
                string saved = UCL_ProjectEditorPrefs.GetString(PrefKey_SelectedQueue, "");
                if (!string.IsNullOrEmpty(saved))
                {
                    int idx = m_QueueAgentIds.IndexOf(saved);
                    if (idx > 0) m_SelectedQueueIdx = idx;
                }
            }
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.QueueSelector"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                int newIdx = UCL_GUILayout.PopupSearchCache(m_SelectedQueueIdx, m_QueueOptions, m_Dic, "QueuePicker");
                if (newIdx != m_SelectedQueueIdx)
                {
                    m_SelectedQueueIdx = newIdx;
                    UCL_ProjectEditorPrefs.SetString(PrefKey_SelectedQueue, SelectedAgentId ?? "");
                    m_Cached = UCL_AgentCommandQueue.Load(SelectedAgentId);
                }
            }
        }

        // ===========================================================
        // 區塊職責：畫一個區塊的折疊標題列（折疊鈕 + 標題 + 收合時也看得到的摘要）
        // 物理意義：折疊語彙一律走 UCL_GUILayout.Toggle（▼/►）—— 本頁原本用 GUILayout.Button
        //          手刻「▼/▶」，那是第二套寫法（而且狀態存在欄位裡、沒有統一容器）。
        // 數值影響：狀態存 m_FoldDic；**摘要在收合時仍顯示** ——
        //          收合把資訊藏起來的話，人得先展開才知道「這裡有沒有事」，那等於沒有折疊。
        // ===========================================================
        bool FoldHeader(string iKey, string iTitle, string iSummary, bool iDefaultExpanded)
        {
            using (new GUILayout.HorizontalScope())
            {
                bool aShow = UCL_GUILayout.Toggle(m_FoldDic, iKey, 21, iDefaultValue: iDefaultExpanded);
                GUILayout.Label($"<b>{iTitle}</b>", WrapLabelStyle, GUILayout.ExpandWidth(false));
                if (!string.IsNullOrEmpty(iSummary))
                {
                    GUILayout.Label($"　{iSummary}", WrapLabelStyle, GUILayout.ExpandWidth(false));
                }
                GUILayout.FlexibleSpace();
                return aShow;
            }
        }

        protected override void ContentOnGUI()
        {
            // ==== Queue 選擇器（要先於載入 — SelectedAgentId 決定載哪條 queue）====
            // ⚠ 刻意**不可折疊** —— 它決定下面每一個區塊在講哪條 queue，收起來會讓所有讀數失去主詞。
            DrawQueueSelector();

            // 載入 / 刷新
            if (m_Cached == null)
            {
                m_Cached = UCL_AgentCommandQueue.Load(SelectedAgentId);
            }

            // ==== 統計（摘要用，收合時也要看得到）====
            // 區塊職責：queue 內的指令數量分布
            // 物理意義：OneShot 在執行成功後會被 runner 立刻移除，因此這裡的 OneShot 數即為「尚未成功執行的待辦」
            // 數值影響：純顯示，不修改任何狀態
            int total = m_Cached?.Commands?.Count ?? 0;
            int oneshot = 0, repeatable = 0;
            if (m_Cached?.Commands != null)
            {
                foreach (var c in m_Cached.Commands)
                {
                    if (c.Mode == UCL_AgentCommandMode.Repeatable) repeatable++;
                    else oneshot++;
                }
            }

            // ==== ① 佇列現況（路徑 / Watcher / 清單）====
            // 預設展開：這一區是「現在發生了什麼」，開頁第一眼要看到的就是它。
            using (new GUILayout.VerticalScope("box"))
            {
                if (FoldHeader("Fold.Queue", UCL_CodeLocalize.Get("AgentCmd.FoldQueue"),
                        string.Format(UCL_CodeLocalize.Get("AgentCmd.Stats"), total, oneshot, repeatable), true))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.QueuePath"),
                        UCL_AgentCommandQueue.GetQueuePath(SelectedAgentId)), WrapLabelStyle);
                    // 區塊職責：顯示 lock-file watcher 啟用狀態 + 當前 trigger 狀態 + 最近一次觸發時間
                    // 物理意義：watcher 啟用時，外部（Python）寫入 pending.trigger 後此 Editor 會自動接手執行
                    // 數值影響：toggle 寫入 EditorPrefs，Watcher 在 OnEditorUpdate 中讀取此值決定是否輪詢
                    DrawWatcherStatusBar();
                    DrawQueueList();
                }
            }

            GUILayout.Space(4);

            // ==== ② 新增指令（選單 + 表單）====
            using (new GUILayout.VerticalScope("box"))
            {
                if (FoldHeader("Fold.Add", UCL_CodeLocalize.Get("AgentCmd.FoldAdd"), "", true))
                {
                    DrawCommandPicker();
                }
            }

            GUILayout.Space(4);

            // ==== ③ 失敗紀錄（可補跑）====
            DrawFailedPanel();

            GUILayout.Space(4);

            // ==== ④⑤ Templates / History 兩個可摺疊區塊 ====
            // 區塊職責：把「指令模板」「歷史指令紀錄」兩種重用機制以摺疊面板呈現
            // 物理意義：模板 = 預先存好的 Cmd 範本；歷史 = 過去用過的紀錄；兩者都可一鍵載入到 Add Command 表單
            // 數值影響：Render-only — 載入按鈕會改寫 m_NewMode / m_NewArgsRaw / 表單欄位，但不直接動 queue
            DrawTemplatesPanel();
            GUILayout.Space(4);
            DrawHistoryPanel();

            GUILayout.Space(8);

            // ==== ⑥ 提示（預設收合 —— 讀過一次就不必每次佔六行）====
            using (new GUILayout.VerticalScope("box"))
            {
                if (!FoldHeader("Fold.Tips", UCL_CodeLocalize.Get("AgentCmd.FoldTips"), "", false)) return;
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tips"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_OneShot"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_Repeatable"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_WaitInit"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_FailedKept"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_Watcher"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Tip_ExportCatalog"), UCL_GUIStyle.LabelStyle);
            }

            //GUILayout.EndScrollView();
        }

        // ===========================================================
        // 區塊：Queue 中的命令清單
        // 物理意義：顯示 queue.json 內所有指令當前狀態，並提供 Remove 按鈕
        // 數值影響：Remove 後立刻寫回 queue.json
        // ===========================================================
        void DrawQueueList()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Commands"), UCL_GUIStyle.LabelStyle);
                if (m_Cached?.Commands == null || m_Cached.Commands.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.QueueEmpty"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                int removeIdx = -1;
                for (int i = 0; i < m_Cached.Commands.Count; i++)
                {
                    var c = m_Cached.Commands[i];
                    Color tagColor = GetStatusColor(c);
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(tagColor)}>●</color> [{StatusText(c)}]",
                                UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                            GUILayout.Label($"<b>{c.Type ?? "<null>"}</b>",
                                UCL_GUIStyle.LabelStyle, GUILayout.Width(220));
                            GUILayout.Label($"({c.Mode}, run×{c.RunCount})", UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Remove"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            {
                                removeIdx = i;
                            }
                        }
                        GUILayout.Label($"ID: <i>{c.Id ?? ""}</i>", UCL_GUIStyle.LabelStyle);
                        if (!string.IsNullOrEmpty(c.Description))
                        {
                            GUILayout.Label($"  {c.Description}", UCL_GUIStyle.LabelStyle);
                        }
                        if (c.Args != null && c.Args.Count > 0)
                        {
                            GUILayout.Label($"  Args: {string.Join(", ", c.Args.Select(kv => $"{kv.Key}={kv.Value}"))}", UCL_GUIStyle.LabelStyle);
                        }
                        if (!string.IsNullOrEmpty(c.LastRunAt))
                        {
                            Color resultColor = c.LastRunResult == "Success" ? Color.green : Color.red;
                            GUILayout.Label($"  Last: <color=#{ColorUtility.ToHtmlStringRGB(resultColor)}>{c.LastRunResult ?? "?"}</color> @ {c.LastRunAt}",
                                UCL_GUIStyle.LabelStyle);
                            if (!string.IsNullOrEmpty(c.LastRunError))
                            {
                                GUILayout.Label($"  Error: {c.LastRunError}", UCL_GUIStyle.GetLabelStyle(Color.red));
                            }
                        }
                    }
                }
                if (removeIdx >= 0)
                {
                    m_Cached.Commands.RemoveAt(removeIdx);
                    UCL_AgentCommandQueue.Save(m_Cached, SelectedAgentId);
                }
            }
        }

        // ===========================================================
        // 區塊：Command 選擇 + 新增表單（整合）
        // 職責：用 PopupSearchCache 提供可搜尋的下拉選單，選一個 handler 後顯示其 metadata 與新增欄位
        // 物理意義：使用者只看到目前選中的指令，不再看一大串清單；按 Add 就把目前選定的指令加入 queue
        // 數值影響：Add 按下時把一筆新指令寫進 queue.json
        // ===========================================================
        void DrawCommandPicker()
        {
            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.AddCommand"), UCL_GUIStyle.LabelStyle);

                if (handlers.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.NoHandlers"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                // 下拉選單顯示用文字（CommandType + 短描述）
                var displayOptions = handlers
                    .Select(h => string.IsNullOrEmpty(h.ShortDescription)
                        ? h.CommandType
                        : $"{h.CommandType} — {h.ShortDescription}")
                    .ToList();

                if (m_SelectedCmdIdx < 0) m_SelectedCmdIdx = 0;
                if (m_SelectedCmdIdx >= handlers.Count) m_SelectedCmdIdx = handlers.Count - 1;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Command"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_SelectedCmdIdx = UCL_GUILayout.PopupSearchCache(
                        m_SelectedCmdIdx, displayOptions, m_Dic, "CmdPicker");
                }

                var selected = handlers[m_SelectedCmdIdx];

                // ===========================================================
                // 區塊職責：選定指令後自動把該指令的 ExampleArgs 填進 Args 欄位（Tim 2026-08-21 指派）
                // 物理意義：換了指令，欄位裡的舊 args 就**屬於別的指令**了 —— 留著比清空更糟
                //          （它看起來像一組有效參數）。範例值是「可直接執行的樣本」，直接給。
                //          原本要人再按一次「填入範例」，那顆按鈕保留（改完想退回範例時用）。
                // 數值影響：只在**索引變動的那一幀**寫一次 m_NewArgsRaw；沒有 ExampleArgs 就清空。
                //          ⚠ Apply（模板／歷史／補跑填回）會顯式同步 m_LastCmdIdxForExample，
                //          否則它們設好的 args 會在下一幀被範例值蓋掉（而那看起來像 Apply 沒生效）。
                // ===========================================================
                if (m_LastCmdIdxForExample != m_SelectedCmdIdx)
                {
                    m_LastCmdIdxForExample = m_SelectedCmdIdx;
                    m_NewArgsRaw = selected.ExampleArgs ?? "";
                    GUI.FocusControl(null);
                }

                // 顯示選定 handler 的 metadata — 可折疊（Tim 2026-07-15 拍板）
                // 物理意義：ArgsSchema 長的 Cmd（如 Tavern 30+ 行）展開會把下方表單擠出視野；
                //          預設收合，只留一行 Type + 短描述，要查 schema 再展開。
                using (new GUILayout.VerticalScope("box"))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // 折疊走 UCL_GUILayout.Toggle + m_FoldDic（統一語彙，見 FoldHeader 的註解）
                        m_ShowCmdInfo = UCL_GUILayout.Toggle(m_FoldDic, "Fold.CmdInfo", 21, iDefaultValue: false);
                        GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.CmdInfoToggle"), WrapLabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.Label($"<b>{selected.CommandType}</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        if (!string.IsNullOrEmpty(selected.ShortDescription))
                        {
                            GUILayout.Label($" — {selected.ShortDescription}", WrapLabelStyle);
                        }
                        GUILayout.FlexibleSpace();
                    }
                    if (m_ShowCmdInfo)
                    {
                        if (!string.IsNullOrEmpty(selected.ArgsSchema))
                        {
                            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.ArgsSchema"), selected.ArgsSchema), UCL_GUIStyle.LabelStyle);
                        }
                        if (!string.IsNullOrEmpty(selected.ExampleArgs))
                        {
                            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.Example"), selected.ExampleArgs), UCL_GUIStyle.LabelStyle);
                        }
                        if (!string.IsNullOrEmpty(selected.HelpURL))
                        {
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ViewHelp"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                Application.OpenURL(UCL_URL.ResolveURL(selected.HelpURL));
                            }
                        }
                    }
                }

                // Mode / Description / Args 表單欄位
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Mode"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.OneShot, "OneShot", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.OneShot;
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.Repeatable, "Repeatable", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.Repeatable;
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Description"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewDescription = GUILayout.TextField(m_NewDescription ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Args"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewArgsRaw = GUILayout.TextField(m_NewArgsRaw ?? "", UCL_GUIStyle.TextFieldStyle);
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.ArgsFormat"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    // 區塊職責：將選定 handler 的 ExampleArgs 一鍵塞進 Args 欄位
                    // 物理意義：人類使用者測試新 Cmd 時不必查文件就能看到可直接執行的參數樣本
                    // 數值影響：覆寫 m_NewArgsRaw；不修改 queue
                    bool hasExample = !string.IsNullOrEmpty(selected.ExampleArgs);
                    using (new UnityEditor.EditorGUI.DisabledScope(!hasExample))
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.FillExample"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_NewArgsRaw = selected.ExampleArgs;
                            GUI.FocusControl(null);
                        }
                    }
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(string.Format(UCL_CodeLocalize.Get("AgentCmd.AddButton"), selected.CommandType, m_NewMode),
                        UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        AddCommand(selected.CommandType, m_NewMode, m_NewDescription, ParseArgsRaw(m_NewArgsRaw), source: "Manual");
                        // 清空僅與本次新增有關的欄位（保留 Mode / 選定的 Command 方便連續新增）
                        m_NewDescription = "";
                        m_NewArgsRaw = "";
                    }

                    GUILayout.Space(12);

                    // 區塊職責：把當前表單存成模板（Name 欄位空 → 用 Type 當預設名稱）
                    // 物理意義：使用者可把常用組合一鍵存檔，下次到 Templates 面板挑回來即可重用
                    // 數值影響：寫入 AgentCommands/Templates/<Name>.json；不影響 queue
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.SaveTemplateName"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_SaveTemplateName = GUILayout.TextField(m_SaveTemplateName ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(160));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.SaveTemplate"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.7f)), GUILayout.ExpandWidth(false)))
                    {
                        string name = string.IsNullOrWhiteSpace(m_SaveTemplateName) ? selected.CommandType : m_SaveTemplateName.Trim();
                        SaveCurrentAsTemplate(name, selected.CommandType);
                        m_SaveTemplateName = "";
                    }
                }

                using (new GUILayout.HorizontalScope())
                {
                    // 區塊職責：Template 補充筆記欄位
                    // 物理意義：給人類讀的「為什麼存這個模板」說明；Save Template 時一併寫入 .json 的 Notes 欄位
                    // 數值影響：純 UI 寬度調整 — 各語系字長不同，給到 140px 是兼容 zh-Hant / en 的安全值
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.TemplateNotes"), UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                    m_SaveTemplateNotes = GUILayout.TextField(m_SaveTemplateNotes ?? "", UCL_GUIStyle.TextFieldStyle);
                }
            }
        }

        // ===========================================================
        // 區塊：失敗紀錄面板（Tim 2026-08-21 派單）
        // 職責：列出 `_cmd_failed/*.json`（**所有**失敗的 Cmd，不只公告），可搜尋、補跑、填回表單、刪除
        // 物理意義：失敗的 OneShot 2026-08-07 起會即時出隊 ⇒ 從 queue 清單上看不到它們，
        //          而 verdict 檔 3 天後被 Purge ⇒ 沒有這個面板的話，「跑失敗過什麼」只剩 Editor log。
        // 數值影響：補跑會**把一筆新 cmd 寫進原本那條 queue 並立刻執行**（新 id，非原地重試）；
        //          刪除只刪紀錄檔，不動任何 queue。
        // ⚠ 補跑 = 重放副作用：酒館公告會重發（同 SHA 貼兩次＝付兩次錢）、轉帳會重轉。
        //   所以這裡**只有人按的按鈕，沒有自動重試** —— 同 UCL_AgentCommandRunner 失敗分支的判準。
        // ===========================================================
        void DrawFailedPanel()
        {
            if (m_FailedCache == null) m_FailedCache = UCL_AgentCommandFailedStore.LoadAll();
            if (m_LegacyFailedCount < 0) m_LegacyFailedCount = UCL_AgentCommandFailedStore.CountReportsWithoutRecord();

            using (new GUILayout.VerticalScope("box"))
            {
                int aCount = m_FailedCache?.Count ?? 0;
                // 摘要：有失敗就標紅並顯示筆數 —— 收合狀態下也要看得出「這裡有沒有事」
                string aSummary = aCount > 0
                    ? $"<color=red>{string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedCount"), aCount)}</color>"
                    : string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedCount"), 0);
                if (m_LegacyFailedCount > 0)
                {
                    aSummary += $"　<color=grey>{string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedLegacy"), m_LegacyFailedCount)}</color>";
                }
                // 有失敗時預設展開 —— 沒事的時候不佔位，有事的時候不用人去找
                bool aShow = FoldHeader("Fold.Failed", UCL_CodeLocalize.Get("AgentCmd.FailedTitle"), aSummary, aCount > 0);
                if (!aShow) return;
                using(new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    string aNewSearch = GUILayout.TextField(m_FailedSearch ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (aNewSearch != m_FailedSearch) { m_FailedSearch = aNewSearch; m_FailedPage = 0; }
                }
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_FailedSearch = "";
                        m_FailedPage = 0;
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_FailedCache = null;
                        m_LegacyFailedCount = -1;
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentCommandFailedStore.EnsureDir();
                        UnityEditor.EditorUtility.RevealInFinder(UCL_AgentCommandFailedStore.GetFailedDir());
                    }
                    GUILayout.FlexibleSpace();
                    // 全部清除：只清紀錄，不影響 queue —— 但清掉就再也不知道失敗過什麼，所以要二段確認
                    if (aCount > 0 && GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.FailedClearAll"),
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        if (UnityEditor.EditorUtility.DisplayDialog(
                                UCL_CodeLocalize.Get("AgentCmd.FailedTitle"),
                                string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedClearAllConfirm"), aCount),
                                UCL_CodeLocalize.Get("AgentCmd.FailedClearAll"),
                                UCL_CodeLocalize.Get("AgentCmd.Cancel")))
                        {
                            int aDeleted = UCL_AgentCommandFailedStore.DeleteAll();
                            Debug.Log($"[UCL_AgentCmd UI] 清除失敗紀錄 {aDeleted} 筆（queue 未受影響）。");
                            m_FailedCache = null;
                            m_LegacyFailedCount = -1;
                        }
                    }
                }

                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.FailedRetryWarning"), UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.8f, 0.3f)));

                if (aCount == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.FailedEmpty"), WrapLabelStyle);
                    return;
                }

                string aKeyword = (m_FailedSearch ?? "").Trim().ToLowerInvariant();
                var aFiltered = string.IsNullOrEmpty(aKeyword)
                    ? m_FailedCache
                    : m_FailedCache.Where(e => MatchesFailed(e, aKeyword)).ToList();

                int aTotalPages = Mathf.Max(1, (aFiltered.Count + PageSize - 1) / PageSize);
                if (m_FailedPage >= aTotalPages) m_FailedPage = aTotalPages - 1;
                DrawPagerRow(ref m_FailedPage, aTotalPages, aFiltered.Count);

                string aRetryId = null, aApplyId = null, aDeleteId = null;
                foreach (var aEntry in aFiltered.Skip(m_FailedPage * PageSize).Take(PageSize))
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        bool aExpanded;   // 折疊鈕畫在標題列裡，但明細畫在標題列外 → 值要活過那個 scope
                        using (new GUILayout.HorizontalScope())
                        {
                            aExpanded = UCL_GUILayout.Toggle(m_HistoryFoldDic, "Failed_" + aEntry.Id, 18, iDefaultValue: false);
                            GUILayout.Label($"<color=red>●</color> <b>{aEntry.Type ?? "<null>"}</b>",
                                WrapLabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                            GUILayout.Label($"{FormatLocalTime(aEntry.FailedAt)}　queue: {aEntry.QueueId ?? UCL_CodeLocalize.Get("AgentCmd.QueueUnrecorded")}",
                                WrapLabelStyle, GUILayout.ExpandWidth(false));
                            if (aEntry.RetryCount > 0)
                            {
                                GUILayout.Label($"　<color=grey>{string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedRetried"), aEntry.RetryCount)}</color>",
                                    WrapLabelStyle, GUILayout.ExpandWidth(false));
                            }
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.FailedRetry"),
                                    UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false))) aRetryId = aEntry.Id;
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.FailedToForm"),
                                    UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) aApplyId = aEntry.Id;
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Delete"),
                                    UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false))) aDeleteId = aEntry.Id;
                        }
                        // 錯誤訊息一律顯示（收合也看得到）—— 這是「該不該補跑」的判斷依據，藏起來就得逐筆展開
                        GUILayout.Label($"  {aEntry.Error ?? ""}", UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.5f, 0.5f)));
                        if (aExpanded)
                        {
                            GUILayout.Label($"  id: <i>{aEntry.Id}</i>　mode: {aEntry.Mode}", WrapLabelStyle);
                            if (!string.IsNullOrEmpty(aEntry.Description))
                            {
                                GUILayout.Label($"  {aEntry.Description}", WrapLabelStyle);
                            }
                            GUILayout.Label($"  Args: {ArgsToRaw(aEntry.Args)}", WrapLabelStyle);
                            if (!string.IsNullOrEmpty(aEntry.RetryCmdId))
                            {
                                GUILayout.Label($"  {string.Format(UCL_CodeLocalize.Get("AgentCmd.FailedRetryCmdId"), aEntry.RetryCmdId, FormatLocalTime(aEntry.RetriedAt))}", WrapLabelStyle);
                            }
                            if (!string.IsNullOrEmpty(aEntry.ErrorReportPath))
                            {
                                using (new GUILayout.HorizontalScope())
                                {
                                    GUILayout.Label($"  {aEntry.ErrorReportPath}", WrapLabelStyle);
                                    // 報告可能已被手動刪掉 —— 存在才給按鈕，不給一顆按了沒反應的鈕
                                    if (System.IO.File.Exists(aEntry.ErrorReportPath)
                                        && GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.FailedOpenReport"),
                                            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                    {
                                        UnityEditor.EditorUtility.RevealInFinder(aEntry.ErrorReportPath);
                                    }
                                }
                            }
                        }
                    }
                }

                // 迴圈外處理動作 —— 迴圈內改集合會讓 IMGUI 兩趟 pass 看到不同的控制項數量（ArgumentException）
                if (aRetryId != null) RetryFailedCommand(aRetryId);
                if (aApplyId != null) ApplyFailedToForm(UCL_AgentCommandFailedStore.Load(aApplyId));
                if (aDeleteId != null)
                {
                    UCL_AgentCommandFailedStore.Delete(aDeleteId);
                    m_FailedCache = null;
                    m_LegacyFailedCount = -1;
                }
            }
        }

        static bool MatchesFailed(UCL_AgentCommandFailedEntry e, string lowerKeyword)
        {
            if (Contains(e.Type, lowerKeyword)) return true;
            if (Contains(e.Error, lowerKeyword)) return true;
            if (Contains(e.QueueId, lowerKeyword)) return true;
            if (Contains(e.Description, lowerKeyword)) return true;
            if (e.Args != null)
            {
                foreach (var kv in e.Args)
                {
                    if (Contains(kv.Key, lowerKeyword)) return true;
                    if (Contains(kv.Value, lowerKeyword)) return true;
                }
            }
            return false;
        }

        // 區塊職責：ISO 時間 → 當地時間字串（顯示用）
        // 物理意義：紀錄一律存 UTC（跨機器可比），但人看的是自己的時鐘。
        // 數值影響：解析失敗就**原樣回傳**，不吞掉 —— 空白會讓人以為「沒有時間」。
        static string FormatLocalTime(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return "";
            if (DateTime.TryParse(iIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var aDt))
            {
                return aDt.ToLocalTime().ToString("MM-dd HH:mm:ss");
            }
            return iIso;
        }

        // ===========================================================
        // 區塊職責：補跑一筆失敗的 cmd
        // 物理意義：**不是原地重試** —— 產生一筆新 cmd（新 id）寫進「它原本那條 queue」，然後跑那條 queue。
        //          寫回原 queue 而不是當前選中的 queue：queue 決定路由與併發 lane，
        //          搬去別條會讓它跟別人搶 lane，而且歷史紀錄的 queue 歸屬會對不上。
        // 數值影響：寫 queue.json + History 一筆（Source=Retry:<原 id>）+ 更新失敗紀錄的補跑痕跡；
        //          然後觸發 Runner。原失敗紀錄**保留**（補跑可能又失敗，痕跡不能消失）。
        // ===========================================================
        void RetryFailedCommand(string iFailedId)
        {
            var aEntry = UCL_AgentCommandFailedStore.Load(iFailedId);
            if (aEntry == null)
            {
                Debug.LogWarning($"[UCL_AgentCmd UI] 找不到失敗紀錄 '{iFailedId}'（可能剛被刪或改名）。");
                m_FailedCache = null;
                return;
            }
            if (string.IsNullOrEmpty(aEntry.Type))
            {
                Debug.LogWarning($"[UCL_AgentCmd UI] 失敗紀錄 '{iFailedId}' 沒有 Type，無法補跑。");
                return;
            }

            // anonymous 是「共用 queue」的 id 寫法；Load/Save 端用 null 表示同一條，兩邊不可混用
            string aQueueId = (string.IsNullOrEmpty(aEntry.QueueId)
                               || aEntry.QueueId == UCL_AgentCommandQueue.AnonymousQueueId)
                ? null : aEntry.QueueId;

            var aArgs = aEntry.Args != null
                ? new Dictionary<string, string>(aEntry.Args)
                : new Dictionary<string, string>();
            // `_cmd_id` 是 Runner 為**那一次執行**注入的識別碼 —— 帶著舊值補跑會讓新的一次執行
            // 對外宣稱自己是舊那一筆（回傳檔／帳目都會掛錯 id），而它不會報錯。
            aArgs.Remove("_cmd_id");

            // ===========================================================
            // 區塊職責：擋掉「對正在跑的 queue 寫入」
            // 物理意義：Runner 開跑時把 queue 讀成記憶體清單，收尾時**整批寫回** ——
            //          期間任何 load→add→save 都會被那次寫回覆蓋掉（lost update）。
            // 🩸 實測（basecamp 2026-08-21 首次驗收）：從一個正在該 queue 執行的 Cmd_Invoke 裡呼叫本方法，
            //   紀錄標成「已補跑」、log 印了新 cmd id，而 queue.json 收尾後是空的、
            //   `_cmd_results` 與 `_cmd_errors` 都沒有那筆 —— **補跑憑空消失且全程零錯誤訊息**。
            // 數值影響：擋下時什麼都不寫（紀錄不標記補跑），等那條 queue 空閒再按即可。
            // ===========================================================
            if (UCL_AgentCommandRunner.IsRunningForAgent(aQueueId))
            {
                Debug.LogWarning($"[UCL_AgentCmd UI] queue '{aQueueId ?? "default"}' 正在執行 —— 補跑暫停。"
                                 + "現在寫進去會被那一批收尾時的整批寫回吃掉（lost update）。等它跑完再按。");
                return;
            }

            var aData = UCL_AgentCommandQueue.Load(aQueueId) ?? new UCL_AgentCommandQueueData();
            aData.Commands ??= new List<UCL_AgentCommand>();
            var aCmd = new UCL_AgentCommand
            {
                Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{aEntry.Type.ToLower()}-retry",
                Type = aEntry.Type,
                Mode = aEntry.Mode,
                RunCount = 0,
                Args = aArgs,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Description = string.IsNullOrEmpty(aEntry.Description)
                    ? $"retry of {aEntry.Id}"
                    : $"{aEntry.Description}（retry of {aEntry.Id}）",
            };
            aData.Commands.Add(aCmd);
            UCL_AgentCommandQueue.Save(aData, aQueueId);

            // 區塊職責：回讀驗證「真的寫進去了」
            // 物理意義：Save 沒有例外**不等於**檔案裡有這筆（上面那條 lost update 就是這樣消失的）。
            //          寫入類操作一律回讀 —— 而且要驗欄位值（這裡是「新 id 在不在」），不是只驗沒報錯。
            // 數值影響：驗不到就不標記補跑、不觸發 Runner —— 讓失敗留在原狀，而不是留下一個假的「已補跑」。
            var aVerify = UCL_AgentCommandQueue.Load(aQueueId);
            bool aLanded = aVerify?.Commands != null
                           && aVerify.Commands.Any(c => c != null && c.Id == aCmd.Id);
            if (!aLanded)
            {
                Debug.LogError($"[UCL_AgentCmd UI] 補跑寫入 queue '{aQueueId ?? "default"}' 後回讀不到 {aCmd.Id} —— "
                               + "可能有另一個寫入者同時收尾（lost update）。紀錄未標記補跑，請稍後再試。");
                m_Cached = UCL_AgentCommandQueue.Load(SelectedAgentId);
                return;
            }

            UCL_AgentCommandHistory.Record(aEntry.Type, aEntry.Mode, aArgs, aCmd.Description,
                source: $"Retry:{aEntry.Id}",
                queueId: aQueueId ?? UCL_AgentCommandQueue.AnonymousQueueId);
            m_HistoryCache = null;

            UCL_AgentCommandFailedStore.MarkRetried(aEntry.Id, aCmd.Id);
            m_FailedCache = null;

            Debug.Log($"[UCL_AgentCmd UI] 補跑 '{aEntry.Type}'：新 id={aCmd.Id}，queue={aQueueId ?? "default"}"
                      + $"（原失敗 {aEntry.Id}；結果看 _cmd_results/{aCmd.Id}.json）");

            if (string.IsNullOrEmpty(aQueueId)) UCL_AgentCommandRunner.Menu_RunPending();
            else UCL_AgentCommandRunner.RunAsync(aQueueId, default).Forget();
            DelayedRefresh().Forget();
        }

        // 區塊職責：把失敗紀錄填回新增表單（要改參數再跑的路徑）
        // 物理意義：補跑是「原封不動再跑一次」；打錯參數那類失敗需要的是**改完再跑**，那就走這裡。
        // 數值影響：只改表單欄位，不動 queue 與紀錄。
        void ApplyFailedToForm(UCL_AgentCommandFailedEntry iEntry)
        {
            if (iEntry == null) return;
            SyncSelectedHandlerByType(iEntry.Type);
            m_NewMode = iEntry.Mode;
            m_NewDescription = string.IsNullOrEmpty(iEntry.Description)
                ? $"retry of {iEntry.Id}" : iEntry.Description;
            var aArgs = iEntry.Args != null
                ? new Dictionary<string, string>(iEntry.Args)
                : new Dictionary<string, string>();
            aArgs.Remove("_cmd_id");   // 理由同 RetryFailedCommand
            m_NewArgsRaw = ArgsToRaw(aArgs);
            GUI.FocusControl(null);
        }

        // ===========================================================
        // 區塊：Templates 面板
        // 職責：列出 AgentCommands/Templates/*.json，可搜尋、套用到表單、刪除
        // 物理意義：模板 = 凍結的指令配方；按 Apply 會把 Type / Mode / Args / Description 填回表單，
        //          但不會自動 Add 進 queue —— 使用者再決定送出時機
        // 數值影響：Apply 改寫 m_New* 系列欄位；Delete 立刻刪 .json 檔
        // ===========================================================
        void DrawTemplatesPanel()
        {
            // 區塊職責：折疊狀態下也載入快取一次，讓 header 計數即時正確
            // 物理意義：之前在折疊狀態下 m_TemplateCache 為 null → 顯示 "0 saved" 是假的
            // 數值影響：第一次 paint 做一次 LoadAll；之後靠快取（操作後手動 invalidate 才會重抓）
            if (m_TemplateCache == null) m_TemplateCache = UCL_AgentCommandTemplateStore.LoadAll();

            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    // 區塊職責：摺疊開關 — 用 button 形式而非 CheckBox 呈現「▼ / ▶」感覺
                    // 物理意義：把面板往下展開或收起；展開時才掃資料夾
                    // 折疊語彙統一走 UCL_GUILayout.Toggle（▼/►）；狀態存 m_FoldDic，
                    // 不再各區塊自帶一個 bool 欄位＋手刻的「▼/▶」按鈕（那是本頁原本的第二套寫法）。
                    // 不在這裡 invalidate cache — 計數一直可見，展開只是顯示已快取的內容
                    m_ShowTemplates = UCL_GUILayout.Toggle(m_FoldDic, "Fold.Templates", 21, iDefaultValue: false);
                    GUILayout.Label($"<b>{UCL_CodeLocalize.Get("AgentCmd.Templates")}</b>", WrapLabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.TemplatesCount"), m_TemplateCache?.Count ?? 0), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (m_ShowTemplates)
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_TemplateCache = null;
                        }
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            UCL_AgentCommandTemplateStore.EnsureDir();
                            UnityEditor.EditorUtility.RevealInFinder(UCL_AgentCommandTemplateStore.GetTemplatesDir());
                        }
                    }
                }

                if (!m_ShowTemplates) return;

                if (m_TemplateCache == null)
                {
                    m_TemplateCache = UCL_AgentCommandTemplateStore.LoadAll();
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    string newSearch = GUILayout.TextField(m_TemplateSearch ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (newSearch != m_TemplateSearch) { m_TemplateSearch = newSearch; m_TemplatePage = 0; }   // 搜尋條件變 → 回第一頁
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_TemplateSearch = "";
                        m_TemplatePage = 0;
                        GUI.FocusControl(null);
                    }
                }

                var visible = string.IsNullOrWhiteSpace(m_TemplateSearch)
                    ? m_TemplateCache
                    : m_TemplateCache.Where(t => MatchesTemplate(t, m_TemplateSearch.Trim().ToLowerInvariant())).ToList();

                if (visible.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.NoTemplates"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                // 分頁（Tim 2026-07-15 拍板）：>10 筆時 IMGUI 每幀渲染全部 box + ConstantRepaint = 性能炸裂
                // → 只渲染當前頁 10 筆，總量再大也是常數渲染成本
                var pageItems = Paginate(visible, ref m_TemplatePage, out int tplPages);
                DrawPagerRow(ref m_TemplatePage, tplPages, visible.Count);

                string deleteName = null;
                foreach (var t in pageItems)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<b>{t.Name}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(180));
                            GUILayout.Label($"{t.Type} ({t.Mode})", UCL_GUIStyle.LabelStyle, GUILayout.Width(260));
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ApplyToForm"), UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                            {
                                ApplyTemplateToForm(t);
                            }
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.AddToQueue"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                            {
                                AddCommand(t.Type, t.Mode, t.Description, t.Args == null ? null : new Dictionary<string, string>(t.Args), source: $"Template:{t.Name}");
                                UCL_AgentCommandTemplateStore.TouchLastUsed(t.Name);
                                m_TemplateCache = null;
                            }
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Delete"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            {
                                deleteName = t.Name;
                            }
                        }
                        // Desc / Args / Notes 一律 wordWrap — 同 History 面板的修法理由
                        if (!string.IsNullOrEmpty(t.Description))
                        {
                            GUILayout.Label($"  Desc: {t.Description}", WrapLabelStyle);
                        }
                        if (t.Args != null && t.Args.Count > 0)
                        {
                            GUILayout.Label($"  Args: {string.Join(", ", t.Args.Select(kv => $"{kv.Key}={kv.Value}"))}", WrapLabelStyle);
                        }
                        if (!string.IsNullOrEmpty(t.Notes))
                        {
                            GUILayout.Label($"  Notes: {t.Notes}", WrapLabelStyle);
                        }
                        GUILayout.Label($"  LastUsed: {t.LastUsedAt ?? t.CreatedAt}", WrapLabelStyle);
                    }
                }
                //GUILayout.EndScrollView();

                if (!string.IsNullOrEmpty(deleteName))
                {
                    if (UCL_AgentCommandTemplateStore.Delete(deleteName))
                    {
                        Debug.Log($"[UCL_AgentCmd UI] Deleted template '{deleteName}'.");
                        m_TemplateCache = null;
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：分頁 helpers（Templates / History 共用）
        // 物理意義：IMGUI + RequiresConstantRepaint 下每幀渲染上百個 box 是本頁 >10 筆卡死的根因；
        //          分頁把渲染量鎖在每頁 PageSize 筆，資料總量不再影響幀率。
        // 數值影響：page 以 ref 傳入並自動 clamp（刪到最後一頁空掉時退回前一頁）。
        // ===========================================================
        static List<T> Paginate<T>(List<T> src, ref int page, out int totalPages)
        {
            totalPages = Math.Max(1, (src.Count + PageSize - 1) / PageSize);
            if (page >= totalPages) page = totalPages - 1;
            if (page < 0) page = 0;
            return src.Skip(page * PageSize).Take(PageSize).ToList();
        }

        static void DrawPagerRow(ref int page, int totalPages, int totalCount)
        {
            using (new GUILayout.HorizontalScope())
            {
                using (new UnityEditor.EditorGUI.DisabledScope(page <= 0))
                {
                    if (GUILayout.Button("◀", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(36)))) page--;
                }
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.PageInfo"), page + 1, totalPages, totalCount),
                    UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                using (new UnityEditor.EditorGUI.DisabledScope(page >= totalPages - 1))
                {
                    if (GUILayout.Button("▶", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(36)))) page++;
                }
                GUILayout.FlexibleSpace();
            }
        }

        static bool MatchesTemplate(UCL_AgentCommandTemplate t, string lowerKeyword)
        {
            if (Contains(t.Name, lowerKeyword)) return true;
            if (Contains(t.Type, lowerKeyword)) return true;
            if (Contains(t.Description, lowerKeyword)) return true;
            if (Contains(t.Notes, lowerKeyword)) return true;
            if (t.Args != null)
            {
                foreach (var kv in t.Args)
                {
                    if (Contains(kv.Key, lowerKeyword)) return true;
                    if (Contains(kv.Value, lowerKeyword)) return true;
                }
            }
            return false;
        }

        static bool Contains(string s, string lowerKeyword)
        {
            return !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(lowerKeyword);
        }

        // ===========================================================
        // 區塊：History 面板
        // 職責：列出 AgentCommands/History/*.json，可搜尋、Re-Add、刪除單筆 / 過舊 / 重複
        // 物理意義：每次按 Add Command 就會記一筆（同簽章自動合併並 +UseCount），
        //          這份歷史純為「使用者下次想找回某個用過的指令」而生
        // 數值影響：Re-Add 會把該歷史條目的 Type/Mode/Args 直接送進 queue；刪除類動作直接動 .json
        // ===========================================================
        void DrawHistoryPanel()
        {
            // 區塊職責：折疊狀態下也載入快取一次，讓 header 計數即時正確
            // 物理意義：之前在折疊狀態下 m_HistoryCache 為 null → 顯示 "0 entries" 是假的
            //          其實 Agent 透過 Runner 已經寫了一堆，使用者卻看不到正確數字
            // 數值影響：第一次 paint 做一次 LoadAll；之後靠快取（操作後手動 invalidate 才會重抓）
            if (m_HistoryCache == null) m_HistoryCache = UCL_AgentCommandHistory.LoadAll();

            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    // 同 Templates：折疊走 UCL_GUILayout.Toggle + m_FoldDic（統一語彙）
                    // 不在這裡 invalidate cache — 計數一直可見，展開只是顯示已快取的內容
                    m_ShowHistory = UCL_GUILayout.Toggle(m_FoldDic, "Fold.History", 21, iDefaultValue: false);
                    GUILayout.Label($"<b>{UCL_CodeLocalize.Get("AgentCmd.History")}</b>", WrapLabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.HistoryCount"), m_HistoryCache?.Count ?? 0), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_HistoryCache = null;
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentCommandHistory.EnsureDir();
                        UnityEditor.EditorUtility.RevealInFinder(UCL_AgentCommandHistory.GetHistoryDir());
                    }

                    GUILayout.FlexibleSpace();
                }

                if (!m_ShowHistory) return;

                if (m_HistoryCache == null)
                {
                    m_HistoryCache = UCL_AgentCommandHistory.LoadAll();
                }

                // ---- 搜尋 + 清理工具列 ----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    string newSearch = GUILayout.TextField(m_HistorySearch ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (newSearch != m_HistorySearch) { m_HistorySearch = newSearch; m_HistoryPage = 0; }   // 搜尋條件變 → 回第一頁
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_HistorySearch = "";
                        m_HistoryPage = 0;
                        GUI.FocusControl(null);
                    }
                }

                // ---- queue 篩選（Tim 2026-08-18 派單）----
                // 區塊職責：依「這筆實際跑在哪條 queue」過濾歷史。
                // 物理意義：用途是「最近 anonymous 跑了什麼」——那條 queue 的內容就是待修清單
                //          （漏帶 --persona 的派遣）。選項由**現有資料**長出來，不寫死名單：
                //          寫死的話新 persona 出現時篩選器看不到它，而且不會有人發現。
                // 數值影響：純顯示過濾；換選項回第一頁（不回的話會停在一個空白頁）。
                var queueOptions = BuildHistoryQueueOptions(m_HistoryCache);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.QueueFilter"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    int curIdx = Mathf.Max(0, queueOptions.IndexOf(string.IsNullOrEmpty(m_HistoryQueueFilter)
                        ? UCL_CodeLocalize.Get("AgentCmd.QueueFilterAll") : m_HistoryQueueFilter));
                    // PopupSearchCache 選項為 0 時會 LogError —— 這裡恆 >=1（第一項固定是 All）
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, queueOptions, m_Dic, "HistoryQueueFilter");
                    if (newIdx != curIdx && newIdx >= 0 && newIdx < queueOptions.Count)
                    {
                        m_HistoryQueueFilter = newIdx == 0 ? "" : queueOptions[newIdx];
                        m_HistoryPage = 0;
                    }
                    //GUILayout.FlexibleSpace();
                }

                using (new GUILayout.HorizontalScope())
                {
                    // 區塊職責：History 清理工具列 — 標籤寬度需容下不同語系字長
                    // 物理意義：90 / 150 是兼容 zh-Hant / en / ja 的安全寬度；數字輸入 50px 即可
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Cleanup"), UCL_GUIStyle.LabelStyle, GUILayout.Width(90));
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.OlderThanDays"), UCL_GUIStyle.LabelStyle, GUILayout.Width(150));
                    string daysText = GUILayout.TextField(m_HistoryAgeDays.ToString(), UCL_GUIStyle.TextFieldStyle, GUILayout.Width(50));
                    if (int.TryParse(daysText, out var d) && d >= 0) m_HistoryAgeDays = d;

                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.DeleteOlder"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        int n = UCL_AgentCommandHistory.DeleteOlderThan(m_HistoryAgeDays);
                        Debug.Log($"[UCL_AgentCmd UI] Deleted {n} history entries older than {m_HistoryAgeDays} days.");
                        m_HistoryCache = null;
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Dedupe"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        int n = UCL_AgentCommandHistory.DeleteDuplicates();
                        Debug.Log($"[UCL_AgentCmd UI] Deleted {n} duplicate history entries.");
                        m_HistoryCache = null;
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ClearAll"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                    {
                        // 區塊職責：清空整個 History 資料夾（不可逆）
                        // 物理意義：危險動作，所以加 Editor 級確認 dialog；即使外部 Page 被自動 Repaint 也不會誤觸
                        // 數值影響：實際刪檔；按取消則完全 no-op
                        if (UnityEditor.EditorUtility.DisplayDialog(
                                UCL_CodeLocalize.Get("AgentCmd.ClearAllConfirmTitle"),
                                string.Format(UCL_CodeLocalize.Get("AgentCmd.ClearAllConfirmBody"), m_HistoryCache?.Count ?? 0),
                                UCL_CodeLocalize.Get("AgentCmd.ConfirmDelete"), UCL_CodeLocalize.Get("AgentCmd.ConfirmCancel")))
                        {
                            int n = UCL_AgentCommandHistory.Clear();
                            Debug.Log($"[UCL_AgentCmd UI] Cleared {n} history entries.");
                            m_HistoryCache = null;
                        }
                    }
                }

                // 區塊職責：清理鈕按下後，同一個 OnGUI pass 內把快取補回來
                // 物理意義：上面三顆清理鈕（刪除過舊 / 去除重複 / 全部清空）都以 `m_HistoryCache = null`
                //          當作 invalidate 訊號，但**它們就在本行上方、同一幀執行** ——
                //          於是這一行拿到 null，下一行 `visible.Count` 直接 NullReferenceException。
                //          （Tim 2026-08-18 按「刪除過舊」實測；此路徑早於 queue 欄位就存在。）
                // 數值影響：清理後多做一次 LoadAll（清理本來就要重讀）；沒清理時 no-op。
                if (m_HistoryCache == null) m_HistoryCache = UCL_AgentCommandHistory.LoadAll();

                var visible = string.IsNullOrWhiteSpace(m_HistorySearch)
                    ? m_HistoryCache
                    : m_HistoryCache.Where(e => MatchesHistory(e, m_HistorySearch.Trim().ToLowerInvariant())).ToList();
                if (!string.IsNullOrEmpty(m_HistoryQueueFilter))
                {
                    visible = visible.Where(e => MatchesHistoryQueue(e, m_HistoryQueueFilter)).ToList();
                }

                if (visible.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.NoHistory"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                // 分頁（Tim 2026-07-15 拍板）— 同 Templates 面板的理由；多層 Scroll 已移除，分頁是唯一的量控手段
                var pageItems = Paginate(visible, ref m_HistoryPage, out int hisPages);
                DrawPagerRow(ref m_HistoryPage, hisPages, visible.Count);

                string deleteId = null;
                foreach (var e in pageItems)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        // 區塊職責：每筆歷史改成可折疊 —— 標題列一眼看完「什麼指令、跑在哪條 queue」
                        // 物理意義：比照 UCL_ControlPanelPage 的折疊慣例 ——
                        //          **關鍵操作（Apply / Re-Add / To Template / Delete）畫在折疊外層**，
                        //          收合後仍可一鍵操作；折疊內只放 Id / Desc / Args / 時間戳這些查閱型資訊。
                        // 數值影響：純 UI；折疊狀態存在 m_HistoryFoldDic（key = 條目 Id），不落檔。
                        bool aShow;
                        using (new GUILayout.HorizontalScope())
                        {
                            aShow = UCL_GUILayout.Toggle(m_HistoryFoldDic, e.Id, 18, iDefaultValue: false);
                            GUILayout.Label($"<b>{e.Type}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ApplyToForm"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                ApplyHistoryToForm(e);
                            }
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ReAdd"), UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                            {
                                AddCommand(e.Type, e.Mode, e.Description, e.Args == null ? null : new Dictionary<string, string>(e.Args), source: $"History:{e.Id}");
                                m_HistoryCache = null;
                            }
                            // 區塊職責：把這筆歷史轉存為模板（一鍵）
                            // 物理意義：使用者用過某個 cmd 後想常駐重用 → 不必到 Add Command 區塊重填，直接轉存
                            // 數值影響：寫一個新的 Templates/<Name>.json；同名衝突會自動加 _2 / _3 後綴
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.ToTemplate"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.7f)), GUILayout.ExpandWidth(false)))
                            {
                                ConvertHistoryToTemplate(e);
                            }
                            // 135：容得下 "(Repeatable, ×1234)" —— 110 會把右括號切掉（實測 ×4 就已經切到）
                            GUILayout.Label($"({e.Mode}, ×{e.UseCount})[{FormatQueueSummary(e)}],src:{e.Source ?? "?"}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));


                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Delete"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            {
                                deleteId = e.Id;
                            }
                        }
                        if (!aShow) continue;   // 收合 → 只留標題列（按鈕已畫在上面，仍可操作）

                        // 區塊職責：Id / Desc / Args 一律用 WrapLabelStyle
                        // 物理意義：args 字串長度不可控（agent 隨意傳 op=... room=... 等），
                        //          一行會把 box 撐到把同列右側按鈕推出視窗 → 必 wordWrap
                        // 數值影響：渲染上純視覺折行；不影響任何資料
                        GUILayout.Label($"  Id: <i>{e.Id}</i>", WrapLabelStyle);
                        // queue 明細：標題列只塞得下摘要，這裡列完整分布（多條 queue 時才有意義）
                        if (e.QueueCounts != null && e.QueueCounts.Count > 0)
                        {
                            GUILayout.Label($"  Queue: {string.Join(", ", e.QueueCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}"))}", WrapLabelStyle);
                        }
                        if (!string.IsNullOrEmpty(e.Description))
                        {
                            GUILayout.Label($"  Desc: {e.Description}", WrapLabelStyle);
                        }
                        if (e.Args != null && e.Args.Count > 0)
                        {
                            GUILayout.Label($"  Args: {string.Join(", ", e.Args.Select(kv => $"{kv.Key}={kv.Value}"))}", WrapLabelStyle);
                        }
                        GUILayout.Label($"  Created: {e.CreatedAt}  |  LastUsed: {e.LastUsedAt}", WrapLabelStyle);
                    }
                }
                //GUILayout.EndScrollView(); 多層Scroll影響操作 移除

                if (!string.IsNullOrEmpty(deleteId))
                {
                    if (UCL_AgentCommandHistory.Delete(deleteId))
                    {
                        Debug.Log($"[UCL_AgentCmd UI] Deleted history entry '{deleteId}'.");
                        m_HistoryCache = null;
                    }
                }
            }
        }

        // ===========================================================
        // 區塊職責：把一筆歷史的 queue 歸屬壓成一行摘要（標題列用）
        // 物理意義：回答「這道指令實際跑在哪條 queue」——
        //          `--persona` 決定 queues/<persona>/，只帶 `--arg persona=` 的會落 anonymous。
        // 數值影響：純字串；⛔ **沒有資料時回「未記錄」而不是 anonymous** ——
        //          舊條目（2026-08-18 之前）本來就沒有這個欄位，把空值畫成 anonymous
        //          等於憑空生出一個讀數，而它看起來會跟真的量到一模一樣。
        // ===========================================================
        static string FormatQueueSummary(UCL_AgentCommandHistoryEntry e)
        {
            if (e?.QueueCounts == null || e.QueueCounts.Count == 0)
            {
                return $"queue: {UCL_CodeLocalize.Get("AgentCmd.QueueUnrecorded")}";
            }
            var ordered = e.QueueCounts.OrderByDescending(kv => kv.Value).ToList();
            string head = $"queue: {ordered[0].Key} ×{ordered[0].Value}";
            return ordered.Count == 1 ? head : $"{head} (+{ordered.Count - 1})";
        }

        // ===========================================================
        // 區塊職責：由現有歷史資料長出 queue 篩選選項（第一項固定為「全部」）
        // 物理意義：不寫死名單 —— 新 persona 出現時篩選器要自己看得到它。
        //          「(未記錄)」只在**真的有**沒記錄的條目時才出現：永遠列著會讓人以為
        //          那是一個存在的 queue，而它其實是欄位還沒填的舊資料。
        // 數值影響：每幀重建（歷史筆數級的字典走訪）；已在折疊收合時 return，不會白跑。
        // ===========================================================
        static List<string> BuildHistoryQueueOptions(List<UCL_AgentCommandHistoryEntry> entries)
        {
            var opts = new List<string> { UCL_CodeLocalize.Get("AgentCmd.QueueFilterAll") };
            if (entries == null) return opts;
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            bool hasUnrecorded = false;
            foreach (var e in entries)
            {
                if (e?.QueueCounts != null && e.QueueCounts.Count > 0)
                {
                    foreach (var kv in e.QueueCounts) seen.Add(kv.Key);
                }
                else if (string.IsNullOrEmpty(e?.QueueId))
                {
                    hasUnrecorded = true;
                }
                else
                {
                    seen.Add(e.QueueId);
                }
            }
            opts.AddRange(seen);
            if (hasUnrecorded) opts.Add(UCL_CodeLocalize.Get("AgentCmd.QueueUnrecorded"));
            return opts;
        }

        // 區塊職責：判斷一筆歷史是否屬於選定的 queue。
        // 物理意義：「(未記錄)」是它自己的一格 —— 舊條目沒有欄位，**不歸進任何一條真 queue**。
        //          把它們算進 anonymous 會讓「anonymous 還剩多少待修」這個讀數直接失真。
        static bool MatchesHistoryQueue(UCL_AgentCommandHistoryEntry e, string filter)
        {
            bool recorded = (e?.QueueCounts != null && e.QueueCounts.Count > 0) || !string.IsNullOrEmpty(e?.QueueId);
            if (filter == UCL_CodeLocalize.Get("AgentCmd.QueueUnrecorded")) return !recorded;
            if (!recorded) return false;
            if (e.QueueCounts != null && e.QueueCounts.ContainsKey(filter)) return true;
            return e.QueueId == filter;
        }

        static bool MatchesHistory(UCL_AgentCommandHistoryEntry e, string lowerKeyword)
        {
            if (Contains(e.Type, lowerKeyword)) return true;
            if (Contains(e.Description, lowerKeyword)) return true;
            if (Contains(e.Source, lowerKeyword)) return true;
            // queue 也納入搜尋 —— 「有哪些指令還跑在 anonymous」直接搜得到，不必逐筆展開
            if (Contains(e.QueueId, lowerKeyword)) return true;
            if (e.QueueCounts != null)
            {
                foreach (var kv in e.QueueCounts)
                {
                    if (Contains(kv.Key, lowerKeyword)) return true;
                }
            }
            if (e.Args != null)
            {
                foreach (var kv in e.Args)
                {
                    if (Contains(kv.Key, lowerKeyword)) return true;
                    if (Contains(kv.Value, lowerKeyword)) return true;
                }
            }
            return false;
        }

        // ===========================================================
        // 區塊：Apply / Save 行為
        // 物理意義：把 Template / History 的內容轉寫到表單 m_New* 欄位，並嘗試把 PopupSearchCache 對到正確 handler
        //          Save：把當前表單寫成模板 .json
        // 數值影響：Apply 純改 UI 狀態；Save 寫入 Templates/<Name>.json
        // ===========================================================

        void ApplyTemplateToForm(UCL_AgentCommandTemplate t)
        {
            if (t == null) return;
            SyncSelectedHandlerByType(t.Type);
            m_NewMode = t.Mode;
            m_NewDescription = t.Description ?? "";
            m_NewArgsRaw = ArgsToRaw(t.Args);
            m_SaveTemplateName = t.Name;
            m_SaveTemplateNotes = t.Notes ?? "";
            UCL_AgentCommandTemplateStore.TouchLastUsed(t.Name);
            m_TemplateCache = null;
            GUI.FocusControl(null);
        }

        void ApplyHistoryToForm(UCL_AgentCommandHistoryEntry e)
        {
            if (e == null) return;
            SyncSelectedHandlerByType(e.Type);
            m_NewMode = e.Mode;
            m_NewDescription = e.Description ?? "";
            m_NewArgsRaw = ArgsToRaw(e.Args);
            GUI.FocusControl(null);
        }

        void SyncSelectedHandlerByType(string type)
        {
            // 區塊職責：依字串 Type 找到對應 handler 在 ListHandlers() 的索引
            // 物理意義：使用者按 Apply 後 PopupSearchCache 應該自動切到那個指令；找不到則維持原索引（不報錯）
            // 數值影響：m_SelectedCmdIdx 可能被覆寫；m_Dic 強制清掉相關 cache 讓下拉重新取值
            if (string.IsNullOrEmpty(type)) return;
            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            for (int i = 0; i < handlers.Count; i++)
            {
                if (string.Equals(handlers[i].CommandType, type, StringComparison.Ordinal))
                {
                    m_SelectedCmdIdx = i;
                    // ⚠ 同步「範例值自動填入」的偵測基準 —— 呼叫端（Apply 模板／歷史／補跑填回）
                    //   正要把自己的 args 寫進表單，若不同步，下一幀就會被 ExampleArgs 蓋掉。
                    m_LastCmdIdxForExample = i;
                    m_Dic.Clear();
                    return;
                }
            }
        }

        static string ArgsToRaw(Dictionary<string, string> args)
        {
            if (args == null || args.Count == 0) return "";
            return string.Join(";", args.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        // 區塊職責：把指定 history 條目一鍵轉存為 Template
        // 物理意義：以 e.Type 作為 Template Name 起始；若已存在同名模板，自動加 _2 / _3 ... 後綴避免覆寫
        //          Description / Args / Mode 完整繼承過去；Notes 留空（使用者後續可自行補充）
        // 數值影響：寫一個新 Templates/<finalName>.json；不影響 history 條目本身
        void ConvertHistoryToTemplate(UCL_AgentCommandHistoryEntry e)
        {
            if (e == null) return;
            string baseName = string.IsNullOrEmpty(e.Type) ? "Template" : e.Type;
            string finalName = baseName;
            int suffix = 2;
            // 區塊職責：name collision 處理迴圈 — 最多嘗試 99 次以防壞輸入導致無限迴圈
            // 物理意義：99 個同 Type 模板已是極端病態場景，超過時直接 fail 並印 log
            // 數值影響：純讀取 Templates/ 資料夾以判斷檔案存在
            while (UCL_AgentCommandTemplateStore.Load(finalName) != null && suffix < 100)
            {
                finalName = $"{baseName}_{suffix}";
                suffix++;
            }
            var t = new UCL_AgentCommandTemplate
            {
                Name = finalName,
                Type = e.Type,
                Mode = e.Mode,
                Args = e.Args == null ? new Dictionary<string, string>() : new Dictionary<string, string>(e.Args),
                Description = e.Description,
                Notes = null,
            };
            if (UCL_AgentCommandTemplateStore.Save(t))
            {
                Debug.Log($"[UCL_AgentCmd UI] Converted history → template '{finalName}' (Type={t.Type}, Mode={t.Mode}).");
                m_TemplateCache = null;
            }
        }

        void SaveCurrentAsTemplate(string name, string fallbackType)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Debug.LogWarning("[UCL_AgentCmd UI] Save Template aborted: name is empty.");
                return;
            }
            var t = new UCL_AgentCommandTemplate
            {
                Name = name.Trim(),
                Type = fallbackType,
                Mode = m_NewMode,
                Args = ParseArgsRaw(m_NewArgsRaw),
                Description = string.IsNullOrEmpty(m_NewDescription) ? null : m_NewDescription,
                Notes = string.IsNullOrEmpty(m_SaveTemplateNotes) ? null : m_SaveTemplateNotes,
            };
            if (UCL_AgentCommandTemplateStore.Save(t))
            {
                Debug.Log($"[UCL_AgentCmd UI] Saved template '{t.Name}' (Type={t.Type}, Mode={t.Mode}).");
                m_SaveTemplateNotes = "";
                m_TemplateCache = null;
            }
        }

        // ===========================================================
        // 區塊：Watcher 狀態列
        // 職責：toggle 啟用 / 顯示 trigger 狀態 / 顯示最近觸發時間
        // 物理意義：lock-file 機制的可視化入口 — 使用者可一眼確認 watcher 是否在跑
        // 數值影響：toggle 寫 EditorPrefs；其餘為唯讀資訊顯示
        // ===========================================================
        void DrawWatcherStatusBar()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                // 區塊職責：Auto-Watcher 開關 — 用 UCL_GUILayout.CheckBox(value, label) 而非
                //          原生 GUILayout.Toggle + LabelStyle（Workflow §7 地雷 1：傳 LabelStyle 會讓
                //          toggle 圖示失效、熱區壞掉，使用者根本點不到 → 永遠停在預設狀態）
                bool prevEnabled = UCL_AgentCommandWatcher.Enabled;
                bool newEnabled = UCL_GUILayout.CheckBox(prevEnabled, UCL_CodeLocalize.Get("AgentCmd.AutoWatcher"));
                if (newEnabled != prevEnabled)
                {
                    UCL_AgentCommandWatcher.Enabled = newEnabled;
                }

                var state = UCL_AgentCommandTrigger.GetState(SelectedAgentId);
                Color stateColor = state switch
                {
                    UCL_AgentCommandTrigger.TriggerState.Running => Color.cyan,
                    UCL_AgentCommandTrigger.TriggerState.Pending => Color.yellow,
                    _ => Color.gray,
                };
                GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(stateColor)}>● {state}</color>",
                    UCL_GUIStyle.LabelStyle, GUILayout.Width(120));

                var last = UCL_AgentCommandWatcher.LastTriggerAt;
                string lastText = last == DateTime.MinValue ? UCL_CodeLocalize.Get("AgentCmd.Never") : last.ToString("HH:mm:ss");
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentCmd.LastTrigger"), lastText), UCL_GUIStyle.LabelStyle, GUILayout.Width(180));

                GUILayout.FlexibleSpace();

                // 給人類測試 watcher 的便利按鈕：手動寫一個 pending.trigger
                if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.SimulateTrigger"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_AgentCommandTrigger.CreatePending("editor-simulate", SelectedAgentId);
                }
            }
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        Color GetStatusColor(UCL_AgentCommand c)
        {
            if (c.LastRunResult == "Failed") return Color.red;
            if (c.Mode == UCL_AgentCommandMode.Repeatable) return Color.cyan;
            // OneShot 成功會被立刻移除，所以還在 queue 內的 OneShot 等於 Pending
            return Color.yellow;
        }

        string StatusText(UCL_AgentCommand c)
        {
            if (c.LastRunResult == "Failed") return "Failed";
            if (c.Mode == UCL_AgentCommandMode.Repeatable)
            {
                return c.LastRunResult == "Success" ? "Repeat OK" : "Repeatable";
            }
            // OneShot 在 queue 內就是還沒成功跑過
            return "Pending";
        }

        // 區塊職責：批次移除所有 LastRunResult == "Failed" 的 cmd
        // 物理意義：失敗 OneShot 預設留在 queue 給作者除錯，但若是 Type 拼錯之類的死局，
        //          留著只會每次 Run Pending 都重試失敗。這顆按鈕一鍵清空。
        // 數值影響：寫回 queue.json；只清失敗的，不動 Pending / Success / Repeatable
        void ClearFailedCommands()
        {
            if (m_Cached?.Commands == null || m_Cached.Commands.Count == 0)
            {
                Debug.Log("[UCL_AgentCmd UI] queue is empty — nothing to clear.");
                return;
            }
            int before = m_Cached.Commands.Count;
            m_Cached.Commands.RemoveAll(c => c != null && c.LastRunResult == "Failed");
            int removed = before - m_Cached.Commands.Count;
            UCL_AgentCommandQueue.Save(m_Cached, SelectedAgentId);
            Debug.Log($"[UCL_AgentCmd UI] Cleared {removed} failed cmd(s) from queue '{SelectedAgentId ?? "default"}' (kept {m_Cached.Commands.Count}).");
        }

        // 區塊職責：把表單 / 模板 / 歷史的指令送進 queue，並把這次操作寫進 History
        // 物理意義：source 標籤紀錄這次「Add 動作的來源」 — Manual / Template:foo / History:id
        //          History.Record 內部會以 Type+Args 簽章自動合併 → 不會灌爆歷史資料夾
        // 數值影響：寫 queue.json + 寫 / 更新 History/<id>.json
        void AddCommand(string type, UCL_AgentCommandMode mode, string description, Dictionary<string, string> args, string source = "Manual")
        {
            if (string.IsNullOrEmpty(type))
            {
                Debug.LogWarning("[UCL_AgentCmd UI] Type is empty — abort.");
                return;
            }
            if (m_Cached == null) m_Cached = new UCL_AgentCommandQueueData();
            if (m_Cached.Commands == null) m_Cached.Commands = new List<UCL_AgentCommand>();

            var safeArgs = args ?? new Dictionary<string, string>();
            var c = new UCL_AgentCommand
            {
                Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{type.ToLower()}",
                Type = type,
                Mode = mode,
                RunCount = 0,
                Args = safeArgs,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Description = string.IsNullOrEmpty(description) ? null : description,
            };
            m_Cached.Commands.Add(c);
            UCL_AgentCommandQueue.Save(m_Cached, SelectedAgentId);

            // 寫入 / 重用 History（以管理層 API 操作，Page 不直接動檔）
            // queueId 跟上一行 Save 用的是**同一個值** —— 指令落哪條 queue、歷史就記哪條，
            // 兩者分開取值就會出現「歷史說 A、queue 檔在 B」而且不報錯。
            UCL_AgentCommandHistory.Record(type, mode, safeArgs, description, source,
                queueId: string.IsNullOrEmpty(SelectedAgentId) ? UCL_AgentCommandQueue.AnonymousQueueId : SelectedAgentId);
            m_HistoryCache = null;

            Debug.Log($"[UCL_AgentCmd UI] Added command: {c.Type} (id={c.Id}, mode={c.Mode}, source={source}, queue={SelectedAgentId ?? "default"})");
        }

        static Dictionary<string, string> ParseArgsRaw(string raw)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(raw)) return dict;
            foreach (var part in raw.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2)
                {
                    string k = kv[0].Trim();
                    string v = kv[1].Trim();
                    if (!string.IsNullOrEmpty(k)) dict[k] = v;
                }
            }
            return dict;
        }

        async UniTask DelayedRefresh()
        {
            // runner 是 async，等個 1.5 秒讓它有時間寫回 queue.json
            await UniTask.Delay(TimeSpan.FromSeconds(1.5));
            m_Cached = UCL_AgentCommandQueue.Load(SelectedAgentId);
        }
    }
}
#endif
