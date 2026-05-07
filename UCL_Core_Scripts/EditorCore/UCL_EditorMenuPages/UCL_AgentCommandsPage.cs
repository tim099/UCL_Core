
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

        // ==== 顯示用快取（每幀重讀檔太重，只在按下 Refresh 時更新）====
        UCL_AgentCommandQueueData m_Cached;

        // ==== Templates / History UI 狀態 ====
        // 區塊職責：紀錄兩個展開區塊的展開/隱藏狀態、搜尋字串、清理參數、以及檔案快取
        // 物理意義：避免每幀掃 Templates/ 與 History/ 資料夾 — 只有按下對應 Refresh 才重讀
        // 數值影響：m_HistoryCache / m_TemplateCache 為純顯示用快取，操作型動作（Add/Delete）後立即重整
        bool m_ShowTemplates = false;                                  // Templates 區塊是否展開
        bool m_ShowHistory = false;                                    // History 區塊是否展開
        string m_TemplateSearch = "";                                  // Templates 搜尋關鍵字
        string m_HistorySearch = "";                                   // History 搜尋關鍵字
        string m_SaveTemplateName = "";                                // 「Save as Template」用的暫存名稱
        string m_SaveTemplateNotes = "";                               // Template 補充筆記
        int m_HistoryAgeDays = 30;                                     // 清理「N 天沒重用」的門檻
        List<UCL_AgentCommandHistoryEntry> m_HistoryCache;             // 最近一次載入的 History 列表
        List<UCL_AgentCommandTemplate> m_TemplateCache;                // 最近一次載入的 Template 列表
        //Vector2 m_TemplateScroll = Vector2.zero;                       // Templates 區塊內捲動位置
        //Vector2 m_HistoryScroll = Vector2.zero;                        // History 區塊內捲動位置

        // 區塊職責：本頁所有 UI 字串走 UCL_CodeLocalize.Get(key) — 翻譯定義集中於 UCL_CodeLocalize.<lang>.cs
        // 物理意義：避免在 IMGUI 程式碼中硬編多語字串；新增語系只需動 4 個 lang 檔，不必動 Page
        // 數值影響：每幀多查一次 dict（O(1)），對 IMGUI 重繪成本可忽略
        static string L(string key) => UCL_CodeLocalize.Get(key);

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
            if (GUILayout.Button(L("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_Cached = UCL_AgentCommandQueue.Load();
                // 區塊職責：Refresh 時一併重整 History / Template 快取
                // 物理意義：使用者按下 Refresh 通常是因為「外部剛動過檔案」，所以兩種 cache 都重抓
                // 數值影響：純讀檔，不寫
                m_HistoryCache = null;
                m_TemplateCache = null;
            }
            if (GUILayout.Button(L("AgentCmd.RunPending"), UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
            {
                UCL_AgentCommandRunner.Menu_RunPending();
                DelayedRefresh().Forget();
            }
            // 區塊職責：清除 queue 內所有 LastRunResult == "Failed" 的條目，避免下次 Run 重跑壞掉的舊指令。
            // 物理意義：失敗的 OneShot 預設會留在 queue（保留 LastRunError 給作者除錯），但若 cmd 本身打錯字
            //          (例 Type 拼錯 → Unknown command type)，留著也只會每次重試都失敗，不如一鍵清掉。
            // 數值影響：寫回 queue.json；不影響任何成功跑過的條目與 Repeatable。
            if (GUILayout.Button(L("AgentCmd.ClearFailed"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.2f)), GUILayout.ExpandWidth(false)))
            {
                ClearFailedCommands();
            }
            if (GUILayout.Button(L("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_AgentCommandRunner.Menu_OpenQueueFolder();
            }
        }

        protected override void ContentOnGUI()
        {
            // 載入 / 刷新
            if (m_Cached == null)
            {
                m_Cached = UCL_AgentCommandQueue.Load();
            }

            // ==== queue.json 路徑提示 ====
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(string.Format(L("AgentCmd.QueuePath"), UCL_AgentCommandQueue.GetQueuePath()), UCL_GUIStyle.LabelStyle);
            }

            // ==== Watcher 狀態列 ====
            // 區塊職責：顯示 lock-file watcher 啟用狀態 + 當前 trigger 狀態 + 最近一次觸發時間
            // 物理意義：watcher 啟用時，外部（Python）寫入 pending.trigger 後此 Editor 會自動接手執行；
            //          停用時則退回為「僅手動 Run Pending」的舊行為
            // 數值影響：toggle 寫入 EditorPrefs，Watcher 在 OnEditorUpdate 中讀取此值決定是否輪詢
            DrawWatcherStatusBar();

            // ==== 統計列 ====
            // 區塊職責：在頂端顯示 queue 內的指令數量分布
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
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(string.Format(L("AgentCmd.Stats"), total, oneshot, repeatable),
                    UCL_GUIStyle.LabelStyle);
            }

            //m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(false));

            // ==== Queue 中的命令清單 ====
            DrawQueueList();

            GUILayout.Space(8);

            // ==== Command 選擇 + 新增表單（整合在一起） ====
            DrawCommandPicker();

            GUILayout.Space(8);

            // ==== Templates / History 兩個可摺疊區塊 ====
            // 區塊職責：把「指令模板」「歷史指令紀錄」兩種重用機制以摺疊面板呈現
            // 物理意義：模板 = 預先存好的 Cmd 範本；歷史 = 過去用過的紀錄；兩者都可一鍵載入到 Add Command 表單
            // 數值影響：Render-only — 載入按鈕會改寫 m_NewMode / m_NewArgsRaw / 表單欄位，但不直接動 queue
            DrawTemplatesPanel();
            GUILayout.Space(4);
            DrawHistoryPanel();

            GUILayout.Space(8);

            // ==== 提示 ====
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(L("AgentCmd.Tips"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_OneShot"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_Repeatable"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_WaitInit"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_FailedKept"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_Watcher"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(L("AgentCmd.Tip_ExportCatalog"), UCL_GUIStyle.LabelStyle);
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
                GUILayout.Label(L("AgentCmd.Commands"), UCL_GUIStyle.LabelStyle);
                if (m_Cached?.Commands == null || m_Cached.Commands.Count == 0)
                {
                    GUILayout.Label(L("AgentCmd.QueueEmpty"), UCL_GUIStyle.LabelStyle);
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
                            if (GUILayout.Button(L("AgentCmd.Remove"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
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
                    UCL_AgentCommandQueue.Save(m_Cached);
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
                GUILayout.Label(L("AgentCmd.AddCommand"), UCL_GUIStyle.LabelStyle);

                if (handlers.Count == 0)
                {
                    GUILayout.Label(L("AgentCmd.NoHandlers"), UCL_GUIStyle.LabelStyle);
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
                    GUILayout.Label(L("AgentCmd.Command"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_SelectedCmdIdx = UCL_GUILayout.PopupSearchCache(
                        m_SelectedCmdIdx, displayOptions, m_Dic, "CmdPicker");
                }

                var selected = handlers[m_SelectedCmdIdx];

                // 顯示選定 handler 的 metadata
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label($"<b>{selected.CommandType}</b>", UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(selected.ShortDescription))
                    {
                        GUILayout.Label($"  {selected.ShortDescription}", UCL_GUIStyle.LabelStyle);
                    }
                    if (!string.IsNullOrEmpty(selected.ArgsSchema))
                    {
                        GUILayout.Label(string.Format(L("AgentCmd.ArgsSchema"), selected.ArgsSchema), UCL_GUIStyle.LabelStyle);
                    }
                    if (!string.IsNullOrEmpty(selected.ExampleArgs))
                    {
                        GUILayout.Label(string.Format(L("AgentCmd.Example"), selected.ExampleArgs), UCL_GUIStyle.LabelStyle);
                    }
                    if (!string.IsNullOrEmpty(selected.HelpURL))
                    {
                        if (GUILayout.Button(L("AgentCmd.ViewHelp"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            Application.OpenURL(UCL_URL.ResolveURL(selected.HelpURL));
                        }
                    }
                }

                // Mode / Description / Args 表單欄位
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("AgentCmd.Mode"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.OneShot, "OneShot", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.OneShot;
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.Repeatable, "Repeatable", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.Repeatable;
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("AgentCmd.Description"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewDescription = GUILayout.TextField(m_NewDescription ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("AgentCmd.Args"), UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewArgsRaw = GUILayout.TextField(m_NewArgsRaw ?? "", UCL_GUIStyle.TextFieldStyle);
                    GUILayout.Label(L("AgentCmd.ArgsFormat"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    // 區塊職責：將選定 handler 的 ExampleArgs 一鍵塞進 Args 欄位
                    // 物理意義：人類使用者測試新 Cmd 時不必查文件就能看到可直接執行的參數樣本
                    // 數值影響：覆寫 m_NewArgsRaw；不修改 queue
                    bool hasExample = !string.IsNullOrEmpty(selected.ExampleArgs);
                    using (new UnityEditor.EditorGUI.DisabledScope(!hasExample))
                    {
                        if (GUILayout.Button(L("AgentCmd.FillExample"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_NewArgsRaw = selected.ExampleArgs;
                            GUI.FocusControl(null);
                        }
                    }
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(string.Format(L("AgentCmd.AddButton"), selected.CommandType, m_NewMode),
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
                    GUILayout.Label(L("AgentCmd.SaveTemplateName"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_SaveTemplateName = GUILayout.TextField(m_SaveTemplateName ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(160));
                    if (GUILayout.Button(L("AgentCmd.SaveTemplate"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.7f)), GUILayout.ExpandWidth(false)))
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
                    GUILayout.Label(L("AgentCmd.TemplateNotes"), UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                    m_SaveTemplateNotes = GUILayout.TextField(m_SaveTemplateNotes ?? "", UCL_GUIStyle.TextFieldStyle);
                }
            }
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
                    if (GUILayout.Button((m_ShowTemplates ? "▼" : "▶") + " " + L("AgentCmd.Templates"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_ShowTemplates = !m_ShowTemplates;
                        // 不在這裡 invalidate cache — 計數一直可見，展開只是顯示已快取的內容
                    }
                    GUILayout.Label(string.Format(L("AgentCmd.TemplatesCount"), m_TemplateCache?.Count ?? 0), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (m_ShowTemplates)
                    {
                        if (GUILayout.Button(L("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_TemplateCache = null;
                        }
                        if (GUILayout.Button(L("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
                    GUILayout.Label(L("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    m_TemplateSearch = GUILayout.TextField(m_TemplateSearch ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (GUILayout.Button(L("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_TemplateSearch = "";
                        GUI.FocusControl(null);
                    }
                }

                var visible = string.IsNullOrWhiteSpace(m_TemplateSearch)
                    ? m_TemplateCache
                    : m_TemplateCache.Where(t => MatchesTemplate(t, m_TemplateSearch.Trim().ToLowerInvariant())).ToList();

                if (visible.Count == 0)
                {
                    GUILayout.Label(L("AgentCmd.NoTemplates"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                //m_TemplateScroll = GUILayout.BeginScrollView(m_TemplateScroll, GUILayout.MaxHeight(220));
                string deleteName = null;
                foreach (var t in visible)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<b>{t.Name}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(180));
                            GUILayout.Label($"{t.Type} ({t.Mode})", UCL_GUIStyle.LabelStyle, GUILayout.Width(260));
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(L("AgentCmd.ApplyToForm"), UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                            {
                                ApplyTemplateToForm(t);
                            }
                            if (GUILayout.Button(L("AgentCmd.AddToQueue"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                            {
                                AddCommand(t.Type, t.Mode, t.Description, t.Args == null ? null : new Dictionary<string, string>(t.Args), source: $"Template:{t.Name}");
                                UCL_AgentCommandTemplateStore.TouchLastUsed(t.Name);
                                m_TemplateCache = null;
                            }
                            if (GUILayout.Button(L("AgentCmd.Delete"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
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
                    if (GUILayout.Button((m_ShowHistory ? "▼" : "▶") + " " + L("AgentCmd.History"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_ShowHistory = !m_ShowHistory;
                        // 不在這裡 invalidate cache — 計數一直可見，展開只是顯示已快取的內容
                    }
                    GUILayout.Label(string.Format(L("AgentCmd.HistoryCount"), m_HistoryCache?.Count ?? 0), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (m_ShowHistory)
                    {
                        if (GUILayout.Button(L("AgentCmd.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_HistoryCache = null;
                        }
                        if (GUILayout.Button(L("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            UCL_AgentCommandHistory.EnsureDir();
                            UnityEditor.EditorUtility.RevealInFinder(UCL_AgentCommandHistory.GetHistoryDir());
                        }
                    }
                }

                if (!m_ShowHistory) return;

                if (m_HistoryCache == null)
                {
                    m_HistoryCache = UCL_AgentCommandHistory.LoadAll();
                }

                // ---- 搜尋 + 清理工具列 ----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(L("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    m_HistorySearch = GUILayout.TextField(m_HistorySearch ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (GUILayout.Button(L("AgentCmd.Clear"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_HistorySearch = "";
                        GUI.FocusControl(null);
                    }
                }

                using (new GUILayout.HorizontalScope())
                {
                    // 區塊職責：History 清理工具列 — 標籤寬度需容下不同語系字長
                    // 物理意義：90 / 150 是兼容 zh-Hant / en / ja 的安全寬度；數字輸入 50px 即可
                    GUILayout.Label(L("AgentCmd.Cleanup"), UCL_GUIStyle.LabelStyle, GUILayout.Width(90));
                    GUILayout.Label(L("AgentCmd.OlderThanDays"), UCL_GUIStyle.LabelStyle, GUILayout.Width(150));
                    string daysText = GUILayout.TextField(m_HistoryAgeDays.ToString(), UCL_GUIStyle.TextFieldStyle, GUILayout.Width(50));
                    if (int.TryParse(daysText, out var d) && d >= 0) m_HistoryAgeDays = d;

                    if (GUILayout.Button(L("AgentCmd.DeleteOlder"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        int n = UCL_AgentCommandHistory.DeleteOlderThan(m_HistoryAgeDays);
                        Debug.Log($"[UCL_AgentCmd UI] Deleted {n} history entries older than {m_HistoryAgeDays} days.");
                        m_HistoryCache = null;
                    }
                    if (GUILayout.Button(L("AgentCmd.Dedupe"), UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.8f, 0.2f)), GUILayout.ExpandWidth(false)))
                    {
                        int n = UCL_AgentCommandHistory.DeleteDuplicates();
                        Debug.Log($"[UCL_AgentCmd UI] Deleted {n} duplicate history entries.");
                        m_HistoryCache = null;
                    }
                    if (GUILayout.Button(L("AgentCmd.ClearAll"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                    {
                        // 區塊職責：清空整個 History 資料夾（不可逆）
                        // 物理意義：危險動作，所以加 Editor 級確認 dialog；即使外部 Page 被自動 Repaint 也不會誤觸
                        // 數值影響：實際刪檔；按取消則完全 no-op
                        if (UnityEditor.EditorUtility.DisplayDialog(
                                L("AgentCmd.ClearAllConfirmTitle"),
                                string.Format(L("AgentCmd.ClearAllConfirmBody"), m_HistoryCache?.Count ?? 0),
                                L("AgentCmd.ConfirmDelete"), L("AgentCmd.ConfirmCancel")))
                        {
                            int n = UCL_AgentCommandHistory.Clear();
                            Debug.Log($"[UCL_AgentCmd UI] Cleared {n} history entries.");
                            m_HistoryCache = null;
                        }
                    }
                }

                var visible = string.IsNullOrWhiteSpace(m_HistorySearch)
                    ? m_HistoryCache
                    : m_HistoryCache.Where(e => MatchesHistory(e, m_HistorySearch.Trim().ToLowerInvariant())).ToList();

                if (visible.Count == 0)
                {
                    GUILayout.Label(L("AgentCmd.NoHistory"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                //m_HistoryScroll = GUILayout.BeginScrollView(m_HistoryScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360))); 多層Scroll影響操作 移除
                string deleteId = null;
                foreach (var e in visible)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<b>{e.Type}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(220));
                            GUILayout.Label($"({e.Mode}, ×{e.UseCount})", UCL_GUIStyle.LabelStyle, GUILayout.Width(120));
                            GUILayout.Label($"src: {e.Source ?? "?"}", UCL_GUIStyle.LabelStyle, GUILayout.Width(180));
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(L("AgentCmd.ApplyToForm"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                ApplyHistoryToForm(e);
                            }
                            if (GUILayout.Button(L("AgentCmd.ReAdd"), UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                            {
                                AddCommand(e.Type, e.Mode, e.Description, e.Args == null ? null : new Dictionary<string, string>(e.Args), source: $"History:{e.Id}");
                                m_HistoryCache = null;
                            }
                            // 區塊職責：把這筆歷史轉存為模板（一鍵）
                            // 物理意義：使用者用過某個 cmd 後想常駐重用 → 不必到 Add Command 區塊重填，直接轉存
                            // 數值影響：寫一個新的 Templates/<Name>.json；同名衝突會自動加 _2 / _3 後綴
                            if (GUILayout.Button(L("AgentCmd.ToTemplate"), UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.7f)), GUILayout.ExpandWidth(false)))
                            {
                                ConvertHistoryToTemplate(e);
                            }
                            if (GUILayout.Button(L("AgentCmd.Delete"), UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            {
                                deleteId = e.Id;
                            }
                        }
                        // 區塊職責：Id / Desc / Args 一律用 WrapLabelStyle
                        // 物理意義：args 字串長度不可控（agent 隨意傳 op=... room=... 等），
                        //          一行會把 box 撐到把同列右側按鈕推出視窗 → 必 wordWrap
                        // 數值影響：渲染上純視覺折行；不影響任何資料
                        GUILayout.Label($"  Id: <i>{e.Id}</i>", WrapLabelStyle);
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

        static bool MatchesHistory(UCL_AgentCommandHistoryEntry e, string lowerKeyword)
        {
            if (Contains(e.Type, lowerKeyword)) return true;
            if (Contains(e.Description, lowerKeyword)) return true;
            if (Contains(e.Source, lowerKeyword)) return true;
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
                bool newEnabled = UCL_GUILayout.CheckBox(prevEnabled, L("AgentCmd.AutoWatcher"));
                if (newEnabled != prevEnabled)
                {
                    UCL_AgentCommandWatcher.Enabled = newEnabled;
                }

                var state = UCL_AgentCommandTrigger.GetState();
                Color stateColor = state switch
                {
                    UCL_AgentCommandTrigger.TriggerState.Running => Color.cyan,
                    UCL_AgentCommandTrigger.TriggerState.Pending => Color.yellow,
                    _ => Color.gray,
                };
                GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(stateColor)}>● {state}</color>",
                    UCL_GUIStyle.LabelStyle, GUILayout.Width(120));

                var last = UCL_AgentCommandWatcher.LastTriggerAt;
                string lastText = last == DateTime.MinValue ? L("AgentCmd.Never") : last.ToString("HH:mm:ss");
                GUILayout.Label(string.Format(L("AgentCmd.LastTrigger"), lastText), UCL_GUIStyle.LabelStyle, GUILayout.Width(180));

                GUILayout.FlexibleSpace();

                // 給人類測試 watcher 的便利按鈕：手動寫一個 pending.trigger
                if (GUILayout.Button(L("AgentCmd.SimulateTrigger"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_AgentCommandTrigger.CreatePending("editor-simulate");
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
            UCL_AgentCommandQueue.Save(m_Cached);
            Debug.Log($"[UCL_AgentCmd UI] Cleared {removed} failed cmd(s) from queue (kept {m_Cached.Commands.Count}).");
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
            UCL_AgentCommandQueue.Save(m_Cached);

            // 寫入 / 重用 History（以管理層 API 操作，Page 不直接動檔）
            UCL_AgentCommandHistory.Record(type, mode, safeArgs, description, source);
            m_HistoryCache = null;

            Debug.Log($"[UCL_AgentCmd UI] Added command: {c.Type} (id={c.Id}, mode={c.Mode}, source={source})");
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
            m_Cached = UCL_AgentCommandQueue.Load();
        }
    }
}
#endif
