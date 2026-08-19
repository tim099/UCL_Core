// 區塊職責：本地 LLM 模型管理頁 —— 環境狀態、模型目錄、安裝／解除安裝、試跑。
// 物理意義：真正的環境檢查與安裝都在 llm_admin.py（ollama 之上的薄層）；本頁是 runner 之上的薄 UI ——
//          按鈕 → async spawn python → 顯示結果。重活不在 main thread 跑（模型下載動輒數分鐘）。
//          形狀對齊 UCL_MediaAdminPage（python 為唯一真相源、C# 只做顯示與確認）。
// 數值影響：安裝／解除安裝**會真的動磁碟**（模型 0.5–5 GB），兩者都走二次確認；其餘唯讀。
//
// 設計取捨：
//   · **不由本頁啟動 ollama 服務** —— 那是常駐 process，domain reload 會清掉 C# 端的控制權
//     而 OS 層的它不會死（屍潮）。服務沒跑就**明講怎麼開**，不替使用者按下去。
//   · **「已安裝」一律以 python 對帳 `ollama list` 的結果為準**，不看磁碟、不看本頁快取 ——
//     磁碟上有檔 ≠ ollama 註冊得到，兩者不一致時**兩邊都不會報錯**。
//   · **安裝完自動不試跑**，另給一顆「試跑」鈕：`pull` 成功只證明檔案下載完，
//     不證明它在這台機器跑得動（顯存不夠會退 CPU 或直接失敗）。兩件事分兩顆鈕，帳才分得開。
//   · UI 字串硬編 zh-Hant（同 MediaAdmin / KnowledgeBase 等內部管理頁慣例）；
//     只有 ToolBox 入口那兩行走 UCL_CodeLocalize（四語系檔都補）。
// RequiresConstantRepaint：python 在背景跑，忙碌狀態與結果要即時反映。
#if UNITY_EDITOR
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.LLMAdmin;
using UCL.Core.JsonLib;
using UCL.Core.Page;      // UCL_OptionPage / ButtonData（二次確認彈窗，既有基建）
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 本地 LLM 模型管理 —— 選模型、安裝、解除安裝、試跑。
    /// 全部操作委派 llm_admin.py（經 <see cref="UCL_LLMAdminRunner"/>），python 為唯一真相源。
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_LLMModelAdminPage.md")]
    public class UCL_LLMModelAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "本地 LLM 模型";
        public override bool ShowInPageMenu => true;

        public static UCL_LLMModelAdminPage Create() => UCL_EditorPage.Create<UCL_LLMModelAdminPage>();

        const int TIMEOUT_QUERY = 60 * 1000;             // 查詢類：本機指令，1 分鐘已是異常
        const int TIMEOUT_INSTALL = 60 * 60 * 1000;      // 下載類：好幾 GB，慢不是錯
        const int TIMEOUT_TEST = 3 * 60 * 1000;          // 試跑：含冷啟動載入

        LLMStatusResult m_Status = new LLMStatusResult();
        List<LLMCatalogEntry> m_Catalog = new List<LLMCatalogEntry>();
        List<LLMInstalledModel> m_NotInCatalog = new List<LLMInstalledModel>();
        List<LLMInstalledModel> m_LoadedModels = new List<LLMInstalledModel>();   // 現在佔著顯存的
        // ⚠ 刻意不叫 m_Loaded —— 那個名字已經被首幀 lazy-load 的旗標佔著（撞名編譯錯）
        int m_Selected = 0;
        // 預設只列「這張卡放得下」的 —— 大模型不是不能選，是不該擋在預設清單的第一排。
        // ⚠ 關掉之後選到放不下的，ollama **不會報錯**，只會把層數丟給 CPU（速度掉一個數量級）。
        bool m_OnlyFits = true;
        // 下拉選單的開合／搜尋狀態。⚠ 不與別處共用 —— 資料重載路徑的 Clear() 會把它一起清掉
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();

        bool m_Loaded = false;          // 首幀 lazy-load（OnResume 不會在首次 Push 觸發，比照既有頁慣例）
        bool m_Busy = false;
        string m_BusyLabel = "";
        string m_Report = "(尚未載入 —— 按「🔄 重新整理」)";
        // 區塊職責：試跑結果**與操作報告分開存**。
        // 物理意義：兩者的壽命與用途不同 —— 報告是「這次操作成不成功」，
        //          回覆是「這顆模型講了什麼」（要拿來評估、要跟上一顆比）。
        //   🩸 混在同一格的代價實測過兩次：① Refresh 把試跑結果蓋掉（Tim 2026-08-19 撞到）
        //     ② 想比較上一顆講得怎樣時，畫面上已經只剩最後一次操作的訊息。
        LLMTestResult m_Test;                  // 最後一次試跑（null＝還沒跑過）
        bool m_FoldThinking = false;           // 思考段預設收合 —— 它動輒上千字，會把回覆推出畫面
        bool m_FoldHistory = true;             // 歷史紀錄預設收合
        // 預設值全部是**實測跑得出來**的那一組（2026-08-19，qwen3:4b／0.6b 皆過）——
        // 初版預設上限 300、且沒傳 --timeout，結果 4b 在 python 端 60s 逾時 ⇒ 畫面「什麼都跑不出來」。
        string m_TestPrompt = "跟剛進門的客人打個招呼";
        // 🩸 2026-08-19 實測（qwen3:4b）：think=false 也一樣會把推理寫進回答
        //   （「首先，問題是…關鍵點：…」），而 CLI 下看起來就是「跑滿 GPU 但沒回傳」。
        //   ⇒ 診斷時要看得到思考段，才分得出「它在想」與「它死了」。預設開著。
        bool m_ShowThink = true;
        int m_NumPredict = 4096;        // 生成上限；4b 的思考段實測吃到 3680 token 才收尾
        int m_TestTimeout = 180;        // 等待上限（秒）—— 必須 ≥ 模型實際要的時間（4b 實測 50s）
        // 酒保人設一併帶進試跑：不帶 system 的試跑跟酒保實際的講話條件不一樣，
        // 那種「試跑好好的、上線卻怪怪的」最難查。
        string m_TestSystem = "你是酒館的酒保，講話簡短親切帶點幽默，一律繁體中文（台灣用語），只輸出要說的那一句話。";
        int m_KeepAlive = 120;          // 用完幾秒把模型從顯存卸掉（0＝立刻卸，代價是每次冷啟動）

        GUIStyle m_WrapStyle;
        GUIStyle WrapStyle => m_WrapStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
        GUIStyle m_DimStyle;
        GUIStyle DimStyle => m_DimStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        { wordWrap = true, normal = { textColor = new Color(0.62f, 0.62f, 0.68f) } };

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Busy))
            {
                if (GUILayout.Button("🔄 重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Refresh().Forget();
                }
            }
            // 裝完模型的下一個動作十之八九是去指定它 —— 兩頁互跳，不必經 ToolBox 繞一圈
            if (GUILayout.Button("🍺 酒保設定", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_BartenderAdminPage.Create();
            }
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) { m_Loaded = true; Refresh().Forget(); }

            GUILayout.Label("🤖 本地 LLM 模型管理（ollama）", WrapStyle);
            EditorGUILayout.HelpBox(
                "管理本機大語言模型：查狀態、裝／移除模型、跑一句驗收。實際動作全在 " +
                "llm_admin.py（ollama 之上的薄層），本頁只是 UI。\n" +
                "⚠ 顯存是跟 Unity 共用的 —— 判準是「可用顯存」不是「總顯存」，" +
                "而顯存不夠時通常不會報錯，只會退回 CPU 變很慢。",
                MessageType.Info);

            DrawStatus();
            DrawCatalog();
            DrawActions();
            DrawReport();
        }

        // ===========================================================
        // 狀態
        // ===========================================================
        void DrawStatus()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(m_Status.ollama_installed
                        ? $"✅ ollama {m_Status.version}"
                        : "❌ 尚未安裝 ollama", WrapStyle);
                GUILayout.Label(m_Status.service_reachable
                        ? $"✅ 服務可連線　已安裝模型 {m_Status.installed_count} 個"
                        : "❌ 服務打不到", WrapStyle);
                if (!string.IsNullOrEmpty(m_Status.hint))
                {
                    // 服務沒起來時**只指路，不代按** —— 常駐 process 由使用者決定何時起。
                    // 「ollama 根本沒裝」則是一次性動作，那個可以代按（見 DrawRuntimeInstall）。
                    EditorGUILayout.HelpBox(m_Status.hint, MessageType.Warning);
                }
                if (!m_Status.ollama_installed) DrawRuntimeInstall();
                if (!string.IsNullOrEmpty(m_Status.error))
                {
                    GUILayout.Label($"⚠ {m_Status.error}", DimStyle);
                }
                DrawLoaded();
            }
        }

        // ===========================================================
        // 顯存佔用（`ollama ps`）與卸載
        // ===========================================================
        // 區塊職責：讓「現在誰佔著顯存」看得見，並提供把它放掉的動作。
        // 物理意義：`ollama list` 是**磁碟上有什麼**，`ollama ps` 是**顯存裡有什麼** —— 兩件事。
        //   🩸 實測 2026-08-19：Process 管理頁已經空的，顯存仍被佔 3.2GB ——
        //     因為模型是 **ollama 服務**持有的，不是我們 spawn 的任何 process。
        //     ⇒ 想還顯存只有一條路：`ollama stop <model>`（本區塊的按鈕）。
        // 數值影響：卸載只釋放顯存，**不刪磁碟上的模型**（下次要用會重新載入，冷啟動幾秒）。
        void DrawLoaded()
        {
            if (m_LoadedModels.Count == 0)
            {
                if (m_Status.service_reachable) GUILayout.Label("顯存：目前沒有模型載入", DimStyle);
                return;
            }
            foreach (var aModel in m_LoadedModels)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"🧠 顯存中：{aModel.id}　{aModel.size}　{aModel.processor}",
                        WrapStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(m_Busy))
                    {
                        if (GUILayout.Button("🧹 從顯存卸載", UCL_GUIStyle.ButtonStyle,
                                GUILayout.ExpandWidth(false)))
                        {
                            // 不做二次確認：這動作可逆（下次用會自己載回來）且是「救顯存」的急動作
                            RunOp($"卸載 {aModel.id}", $"stop --model {aModel.id} --format text",
                                TIMEOUT_QUERY).Forget();
                        }
                    }
                }
                if (!string.IsNullOrEmpty(aModel.processor) && aModel.processor.Contains("CPU"))
                {
                    EditorGUILayout.HelpBox(
                        "PROCESSOR 欄出現 CPU ⇒ 顯存放不下，部分層落在 CPU 上跑。" +
                        "**不會報錯，只會慢一個數量級** —— 換小一顆，或先卸掉別的。",
                        MessageType.Warning);
                }
            }
        }

        // ===========================================================
        // ollama 本體安裝（一次性；只在「沒裝」時出現）
        // ===========================================================
        // 區塊職責：把官方 Windows 安裝腳本變成一顆按鈕。
        // 物理意義：`irm https://ollama.com/install.ps1 | iex` ＝ **下載並執行遠端腳本** ——
        //          等於把安裝過程完全託付給那個網址當下的內容。所以這裡刻意做三件事：
        //            ① 指令原文攤在畫面上（不是藏在按鈕後面）
        //            ② 二次確認，講明它會做什麼、代價是什麼
        //            ③ 開**看得見**的 PowerShell 視窗（可能跳 UAC；藏起來會變成「按了沒反應」）
        //          不想讓 Editor 代跑的人有另外兩條路：複製指令自己貼、或開官方下載頁。
        // 數值影響：安裝軟體、動 PATH。⚠ 裝完**本 Editor 行程的 PATH 仍是舊的** —— 要重開 Editor。
        //          （llm_admin.py 另有已知安裝路徑的 fallback，所以重開前也可能直接找得到。）
        const string OLLAMA_INSTALL_CMD = "irm https://ollama.com/install.ps1 | iex";

        void DrawRuntimeInstall()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("安裝 ollama（Windows）", UCL_GUIStyle.LabelStyle);
                GUILayout.Label(OLLAMA_INSTALL_CMD, WrapStyle);
                GUILayout.Label("↑ 這行會**下載並執行**官方遠端腳本。不放心就用右邊兩顆自己來。", DimStyle);
                using (new GUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(m_Busy))
                    {
                        if (GUILayout.Button("⚡ 一鍵安裝（開 PowerShell）",
                                UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            ConfirmInstallRuntime();
                        }
                    }
                    if (GUILayout.Button("📋 複製指令", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        GUIUtility.systemCopyBuffer = OLLAMA_INSTALL_CMD;
                        // 讀回來才算數 —— 剪貼簿被占用時是靜默失敗
                        m_Report = GUIUtility.systemCopyBuffer == OLLAMA_INSTALL_CMD
                            ? "✓ 指令已複製，貼到 PowerShell 執行" : "✗ 複製失敗（剪貼簿被占用？）";
                    }
                    if (GUILayout.Button("🌐 官方下載頁", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Application.OpenURL("https://ollama.com/download");
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        void ConfirmInstallRuntime()
        {
            UCL_OptionPage.Create("執行 ollama 官方安裝腳本？",
                OLLAMA_INSTALL_CMD + "\n\n" +
                "· 會開一個 PowerShell 視窗（可能跳 UAC）—— 進度看那個視窗，不是本頁\n" +
                "· 這是**下載並執行遠端腳本**：內容由 ollama.com 當下提供\n" +
                "· 裝完 PATH 對這個 Editor 行程仍是舊的 ⇒ 重開 Unity 後再按「重新整理」",
                new ButtonData("執行", () => RunOp("安裝 ollama", "install-runtime --format text",
                        TIMEOUT_QUERY).Forget(),
                    UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f))),
                new ButtonData("取消"));
        }

        // ===========================================================
        // 目錄（選取模型 —— 下拉選單）
        // ===========================================================
        void DrawCatalog()
        {
            if (m_Catalog.Count == 0)
            {
                GUILayout.Label("（目錄尚未載入）", DimStyle);
                return;
            }
            // 過濾後的索引 → 原始目錄索引的對照表。
            // ⚠ 不能拿過濾後的 index 當 m_Selected 用 —— 切換開關時清單長度會變，
            //   同一個數字會指到不同的模型（而畫面看起來完全正常）。
            var aVisible = new List<int>(m_Catalog.Count);
            for (int i = 0; i < m_Catalog.Count; i++)
            {
                if (!m_OnlyFits || m_Catalog[i].fits_budget) aVisible.Add(i);
            }
            if (aVisible.Count == 0) { m_OnlyFits = false; return; }   // 全被濾掉就自動放行，不留空畫面

            using (new GUILayout.HorizontalScope())
            {
                bool aNext = UCL_GUILayout.CheckBox(m_OnlyFits);
                if (aNext != m_OnlyFits) m_OnlyFits = aNext;
                GUILayout.Label("只列這張卡放得下的（顯存 ≦ 預算）", DimStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }

            // 下拉的顯示字串把「裝了沒／推不推薦／下載多大／要多少顯存／中文幾分」壓進一行 ——
            // 選之前就看得到，不必先選一顆再回頭看說明。
            var aOptions = new List<string>(aVisible.Count);
            foreach (int idx in aVisible)
            {
                var aItem = m_Catalog[idx];
                aOptions.Add($"{(aItem.installed ? "✅" : "▫")}{(aItem.recommend ? "★" : " ")} " +
                             $"{aItem.id}　{aItem.params_}　下載 {aItem.size_gb}GB／顯存 ~{aItem.vram_gb}GB" +
                             $"　中文{aItem.zh}/5{(aItem.fits_budget ? "" : "　⚠放不下")}");
            }
            int aCur = aVisible.IndexOf(m_Selected);
            if (aCur < 0) aCur = 0;                                    // 選中的被濾掉了 → 退回第一筆
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("模型", UCL_GUIStyle.LabelStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                // ⚠ 選項為 0 時 PopupSearchCache 會 LogError —— 上面已保證 > 0
                aCur = UCL_GUILayout.PopupAuto(aCur, aOptions, m_Dic, "CatalogPopup");
            }
            if (aCur >= 0 && aCur < aVisible.Count) m_Selected = aVisible[aCur];

            var aSel = m_Catalog[m_Selected];
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"{(aSel.installed ? "✅ 已安裝" : "▫ 未安裝")}　{aSel.id}　{aSel.params_}" +
                    (aSel.recommend ? "　★純聊天推薦" : ""), WrapStyle);
                // 兩個數字分開講 —— 使用者最常把「下載量」讀成「顯存需求」
                GUILayout.Label($"下載量／磁碟 {aSel.size_gb} GB　｜　顯存需求 約 {aSel.vram_gb} GB" +
                    "（權重＋KV cache＋執行期開銷）　｜　中文 " + aSel.zh + "/5", WrapStyle);
                GUILayout.Label(aSel.note, DimStyle);
                if (!aSel.fits_budget)
                {
                    EditorGUILayout.HelpBox(
                        "這顆超過顯存預算。放不下時 ollama **不會報錯**，只會把層數丟給 CPU —— " +
                        "症狀是「跑得出來但慢十倍」，不是「失敗」。",
                        MessageType.Warning);
                }
                if (aSel.installed && !aSel.exact)
                {
                    // 變體命中：磁碟上是同族的另一個 tag。說清楚，否則「已安裝」會誤導
                    GUILayout.Label("↳ 已安裝的是同族**變體 tag**，不是這個精確 tag", DimStyle);
                }
            }
            if (m_NotInCatalog.Count > 0)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label("目錄外（你自己 pull 的，本頁不管理）", DimStyle);
                    foreach (var aModel in m_NotInCatalog)
                    {
                        GUILayout.Label($"　· {aModel.id}　{aModel.size}", DimStyle);
                    }
                }
            }
        }

        // ===========================================================
        // 動作
        // ===========================================================
        void DrawActions()
        {
            var aSel = (m_Selected >= 0 && m_Selected < m_Catalog.Count) ? m_Catalog[m_Selected] : null;
            using (new EditorGUI.DisabledScope(m_Busy || aSel == null || !m_Status.service_reachable))
            {
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(aSel != null && aSel.installed ? "⬇️ 重新安裝" : "⬇️ 安裝",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        ConfirmInstall(aSel);
                    }
                    using (new EditorGUI.DisabledScope(aSel == null || !aSel.installed))
                    {
                        if (GUILayout.Button("🗑 解除安裝",
                                UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.5f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            ConfirmUninstall(aSel);
                        }
                        if (GUILayout.Button("▶ 試跑一句", UCL_GUIStyle.ButtonStyle,
                                GUILayout.ExpandWidth(false)))
                        {
                            // ⚠ python 端也有自己的逾時（--timeout）。不傳的話它用預設 60s，
                            //   而 C# 這邊等 3 分鐘 —— 於是 4b（實測 50s）卡在邊界隨機失敗，
                            //   畫面看起來是「什麼都跑不出來」。兩邊的上限必須一起給，且 C# 要更寬。
                            RunTest(aSel.id).Forget();
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("試跑提示詞", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_TestPrompt = GUILayout.TextField(m_TestPrompt, UCL_GUIStyle.TextFieldStyle);
                }
                using (new GUILayout.HorizontalScope())
                {
                    bool aThink = UCL_GUILayout.CheckBox(m_ShowThink);
                    if (aThink != m_ShowThink) m_ShowThink = aThink;
                    GUILayout.Label("🧠 顯示思考過程", DimStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Space(UCL_GUIStyle.GetScaledSize(12));
                    GUILayout.Label("生成上限", DimStyle, GUILayout.ExpandWidth(false));
                    m_NumPredict = UCL_GUILayout.IntField("", m_NumPredict, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label("token　閒置卸載", DimStyle, GUILayout.ExpandWidth(false));
                    m_KeepAlive = UCL_GUILayout.IntField("", m_KeepAlive, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label("秒　等待上限", DimStyle, GUILayout.ExpandWidth(false));
                    m_TestTimeout = UCL_GUILayout.IntField("", m_TestTimeout,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label("秒", DimStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("人設 prompt", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    m_TestSystem = GUILayout.TextField(m_TestSystem, UCL_GUIStyle.TextFieldStyle);
                }
                GUILayout.Label("🩸 實測（2026-08-19）：qwen3:4b 開 think 要 **50 秒 / 3680 token** 才吐出" +
                    "「您好，點什麼？」；關 think 它會把推理寫進回答本身。qwen3:0.6b 則 **3 秒 / 20 token** 收尾。" +
                    "⇒ 上限或等待秒數不夠時「回答」會是空的（被截斷，不是失敗）。卡住按 ⛔ 中斷。", DimStyle);
            }
            if (m_Busy)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"⏳ 執行中（{m_BusyLabel}）—— 下載大模型可能要好幾分鐘",
                        WrapStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    // ⛔ 中斷做兩段：kill 我們的 python ＋ 把模型從顯存放掉。
                    // 只做前者的話畫面停了、顯存沒還（實測 Process 頁空著而顯存仍滿）。
                    if (GUILayout.Button("⛔ 中斷", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.55f, 0.5f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        AbortCurrent();
                    }
                }
            }
            if (!m_Status.service_reachable)
            {
                GUILayout.Label("（服務打不到時所有動作停用 —— 先把 ollama 裝好／跑起來）", DimStyle);
            }
        }

        void ConfirmInstall(LLMCatalogEntry iEntry)
        {
            if (iEntry == null) return;
            UCL_OptionPage.Create("確認安裝模型？",
                $"{iEntry.id}（{iEntry.params_}）\n" +
                $"下載量約 {iEntry.size_gb} GB（佔磁碟），執行時顯存約 {iEntry.vram_gb} GB。\n" +
                "下載期間本頁會鎖住。\n\n" +
                "⚠ 顯存是跟 Unity Editor 共用的 —— 判準是 nvidia-smi 的**可用**顯存，不是總量。",
                new ButtonData("安裝", () => RunOp($"安裝 {iEntry.id}",
                        $"install --model {iEntry.id} --format text", TIMEOUT_INSTALL).Forget(),
                    UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f))),
                new ButtonData("取消"));
        }

        void ConfirmUninstall(LLMCatalogEntry iEntry)
        {
            if (iEntry == null) return;
            UCL_OptionPage.Create("確認解除安裝？",
                $"{iEntry.id}\n\n⚠ **不可逆** —— 要用回來得重新下載約 {iEntry.size_gb} GB。",
                new ButtonData("解除安裝", () => RunOp($"解除安裝 {iEntry.id}",
                        $"uninstall --model {iEntry.id} --format text", TIMEOUT_QUERY).Forget(),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.45f))),
                new ButtonData("取消"));
        }

        // 區塊職責：把「操作報告」與「模型講的話」分開畫，並提供歷史。
        // 物理意義：報告是機器對我說的（成不成功、幾秒、幾個 token）；
        //          回覆是模型對讀者說的（要拿來當酒保發言的那一段）。混在一起會互相蓋掉，
        //          而蓋掉的那一刻看起來就像「什麼都沒跑出來」（實測過）。
        // 不開內層 ScrollView —— UCL_EditorPage 已經包好捲動區，再包一層是雙捲軸。
        void DrawReport()
        {
            if (!string.IsNullOrEmpty(m_Report))
            {
                GUILayout.Label("📋 報告", UCL_GUIStyle.LabelStyle);
                EditorGUILayout.TextArea(m_Report, WrapStyle);
            }
            if (m_Test == null) return;

            if (!string.IsNullOrEmpty(m_Test.output))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("💬 模型回覆", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("📋 複製", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        GUIUtility.systemCopyBuffer = m_Test.output;
                        // 讀回來才算數 —— 剪貼簿被占用時是靜默失敗
                        m_Report = GUIUtility.systemCopyBuffer == m_Test.output
                            ? "✓ 回覆已複製" : "✗ 複製失敗（剪貼簿被占用？）";
                    }
                }
                EditorGUILayout.TextArea(m_Test.output, WrapStyle);
            }
            else if (m_Test.ok)
            {
                EditorGUILayout.HelpBox(
                    "回覆是空的。thinking 模型把上限用在思考上時會這樣 —— " +
                    "**那是被截斷，不是失敗**（見報告的說明）。提高生成上限或換小模型。",
                    MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(m_Test.thinking))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool aNext = UCL_GUILayout.Toggle(!m_FoldThinking);   // ▼/►
                    if (aNext == m_FoldThinking) m_FoldThinking = !m_FoldThinking;
                    GUILayout.Label($"🧠 思考過程（{m_Test.thinking.Length} 字）—— 預設收合，它會把回覆推出畫面",
                        DimStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!m_FoldThinking) EditorGUILayout.TextArea(m_Test.thinking, WrapStyle);
            }

            DrawHistory();
        }

        void DrawHistory()
        {
            using (new GUILayout.HorizontalScope())
            {
                bool aNext = UCL_GUILayout.Toggle(!m_FoldHistory);
                if (aNext == m_FoldHistory) m_FoldHistory = !m_FoldHistory;
                GUILayout.Label("🗂 試跑紀錄（append-only，換模型／改提示詞後可回頭比）",
                    DimStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("📂 開啟紀錄檔", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_ExplorerUtil.Open(UCL_LLMTestLog.GetPath(), nameof(UCL_LLMModelAdminPage));
                }
            }
            if (m_FoldHistory) return;
            var aLines = UCL_LLMTestLog.TailLines(5);
            if (aLines.Length == 0)
            {
                GUILayout.Label("（還沒有紀錄）", DimStyle);
                return;
            }
            foreach (var aLine in aLines)
            {
                try
                {
                    var aJson = JsonData.ParseJson(aLine);
                    GUILayout.Label($"· {aJson.GetString("ts", "").Replace("T", " ").Substring(0, 19)}　" +
                        $"{aJson.GetString("model", "")}　{aJson.GetString("seconds", "")}s　" +
                        $"→ {aJson.GetString("output", "").Replace("\n", " ")}", DimStyle);
                }
                catch (System.Exception)
                {
                    GUILayout.Label($"· （這行讀不出來）{aLine}", DimStyle);
                }
            }
        }

        // ===========================================================
        // 執行（全部走 python）
        // ===========================================================
        // 區塊職責：整頁刷新（狀態／目錄／顯存）。
        // ⚠ iQuiet：**不要覆寫 m_Report**。
        //   🩸 2026-08-19 Tim 實測：試跑明明成功，畫面只剩「狀態與目錄已更新」——
        //     因為 RunOp 做完會呼叫 Refresh 對帳，而 Refresh 把報告區蓋掉了。
        //     操作結果與刷新訊息搶同一格 ⇒ 後到的贏，而畫面看起來像「什麼都沒跑出來」。
        //     （同一個坑我在 UCL_AutoCommitPage 已經踩過一次，那裡的解法就是 quiet 參數。）
        async UniTask Refresh(bool iQuiet = false)
        {
            if (m_Busy) return;
            m_Busy = true; m_BusyLabel = "讀取狀態";
            try
            {
                var aStatus = await UCL_LLMAdminRunner.RunAsync("status --format json", TIMEOUT_QUERY);
                m_Status = ParseStatus(aStatus.Stdout);
                if (!aStatus.Launched)
                {
                    if (!iQuiet) m_Report = aStatus.DisplayText;   // 連 python 都沒跑起來 —— 原因攤開
                    return;
                }
                m_BusyLabel = "讀取模型目錄";
                var aList = await UCL_LLMAdminRunner.RunAsync("list --format json", TIMEOUT_QUERY);
                ParseList(aList.Stdout);
                m_BusyLabel = "讀取顯存佔用";
                var aPs = await UCL_LLMAdminRunner.RunAsync("ps --format json", TIMEOUT_QUERY);
                m_LoadedModels = ParseLoaded(aPs.Stdout);
                if (!iQuiet)
                {
                    m_Report = $"狀態與目錄已更新（目錄 {m_Catalog.Count} 筆、已安裝 {m_Status.installed_count} 個）";
                }
            }
            finally { m_Busy = false; m_BusyLabel = ""; }
        }

        // 區塊職責：試跑一句 —— 走 `--format json`，把回覆／思考／統計**分開**拿回來。
        // 物理意義：文字格式只能整段塞進報告區，分不了區也存不了帳；JSON 才能分欄顯示與落檔。
        // ⚠ python 端也有自己的 --timeout：兩邊都要給，且 C# 要更寬（否則 C# 先 kill，
        //   而畫面上看起來會像模型沒回應）。
        async UniTask RunTest(string iModelId)
        {
            if (m_Busy) return;
            m_Busy = true; m_BusyLabel = $"試跑 {iModelId}";
            m_Report = $"⏳ 試跑 {iModelId} 中…（thinking 模型可能要數十秒）";
            try
            {
                string aArgs = $"test --model {iModelId}" +
                    $" --prompt \"{m_TestPrompt.Replace("\"", "'")}\"" +
                    $" --system \"{m_TestSystem.Replace("\"", "'")}\"" +
                    (m_ShowThink ? " --think" : "") +
                    $" --num-predict {m_NumPredict} --keep-alive {m_KeepAlive}" +
                    $" --timeout {m_TestTimeout} --format json";
                var aResult = await UCL_LLMAdminRunner.RunAsync(aArgs, (m_TestTimeout + 30) * 1000);
                m_Test = ParseTest(aResult.Stdout);
                if (m_Test == null)
                {
                    // 解析不出來就把原始輸出攤在報告區 —— 靜默留空會讓人以為模型沒回應
                    m_Report = $"❌ 試跑 {iModelId}：結果無法解析\n{aResult.DisplayText}";
                }
                else
                {
                    m_Report = (m_Test.ok ? "✅ " : "❌ ") + $"試跑 {iModelId}　" +
                        $"{m_Test.seconds}s　{m_Test.eval_count} token　{m_Test.tokens_per_sec} tok/s" +
                        (string.IsNullOrEmpty(m_Test.note) ? "" : "\n" + m_Test.note) +
                        (string.IsNullOrEmpty(m_Test.error) ? "" : "\n⚠ " + m_Test.error);
                    m_Test.prompt = m_TestPrompt;
                    UCL_LLMTestLog.Append(m_Test, m_TestSystem);   // 落檔：之後換模型要能比
                }
            }
            finally { m_Busy = false; m_BusyLabel = ""; }
            await Refresh(iQuiet: true);   // 對帳但別蓋報告（顯存佔用會因為這次試跑而改變）
        }

        static LLMTestResult ParseTest(string iStdout)
        {
            try
            {
                var aJson = JsonData.ParseJson(iStdout);
                if (aJson == null) return null;
                var aResult = new LLMTestResult();
                aResult.DeserializeFromJson(aJson);
                return aResult;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        async UniTask RunOp(string iLabel, string iArgs, int iTimeoutMs)
        {
            if (m_Busy) return;
            m_Busy = true; m_BusyLabel = iLabel;
            m_Report = $"⏳ {iLabel} 執行中…";
            try
            {
                var aResult = await UCL_LLMAdminRunner.RunAsync(iArgs, iTimeoutMs);
                // exit code 才是成敗判準 —— 失敗時 python 也會印東西，只看有沒有輸出會把失敗讀成成功
                m_Report = (aResult.Ok ? "✅ " : "❌ ") + iLabel + "\n" + aResult.DisplayText;
            }
            finally { m_Busy = false; m_BusyLabel = ""; }
            // 動完磁碟一定重讀 —— 但**安靜地**重讀：報告區留給剛剛那次操作的結果
            await Refresh(iQuiet: true);
        }

        // 區塊職責：中斷 —— 兩段都做。
        // 物理意義：① kill 我們起的 python（畫面解鎖）② 把選中的模型從顯存放掉（顯存還回來）。
        //          ⚠ 只做 ① 的話 Process 管理頁會變空、而顯存仍被佔著（實測過的形狀）。
        //          下載中的 pull 被中斷是安全的 —— ollama 的分塊下載可續傳，重按安裝會接著跑。
        void AbortCurrent()
        {
            int aKilled = UCL_LLMAdminRunner.Abort();
            m_Busy = false; m_BusyLabel = "";
            var aSel = (m_Selected >= 0 && m_Selected < m_Catalog.Count) ? m_Catalog[m_Selected] : null;
            string aStop = "";
            if (m_LoadedModels.Count > 0)
            {
                // 顯存裡有東西就一起放掉（優先放選中的那顆，沒有就放第一顆）
                string aTarget = aSel != null && m_LoadedModels.Exists(m => m.id == aSel.id)
                    ? aSel.id : m_LoadedModels[0].id;
                aStop = aTarget;
                RunOp($"中斷後卸載 {aTarget}", $"stop --model {aTarget} --format text",
                    TIMEOUT_QUERY).Forget();
            }
            m_Report = $"⛔ 已中斷：收掉 {aKilled} 個 python 行程"
                + (string.IsNullOrEmpty(aStop) ? "（顯存本來就沒有載入的模型）" : $"，並卸載 {aStop}");
        }

        static List<LLMInstalledModel> ParseLoaded(string iStdout)
        {
            try
            {
                var aJson = JsonData.ParseJson(iStdout);
                return LLMAdminParse.Installed(aJson, "loaded");
            }
            catch (System.Exception)
            {
                return new List<LLMInstalledModel>();   // 讀不到就當沒有；錯誤已在 report 那條路徑顯示
            }
        }

        static LLMStatusResult ParseStatus(string iStdout)
        {
            var aResult = new LLMStatusResult();
            try
            {
                var aJson = JsonData.ParseJson(iStdout);
                if (aJson != null) aResult.DeserializeFromJson(aJson);
            }
            catch (System.Exception e)
            {
                aResult.error = $"狀態解析失敗：{e.Message}";
            }
            return aResult;
        }

        void ParseList(string iStdout)
        {
            try
            {
                var aJson = JsonData.ParseJson(iStdout);
                m_Catalog = LLMAdminParse.Catalog(aJson);
                m_NotInCatalog = LLMAdminParse.Installed(aJson, "not_in_catalog");
                if (m_Selected >= m_Catalog.Count) m_Selected = 0;
            }
            catch (System.Exception e)
            {
                m_Report = $"目錄解析失敗：{e.Message}";
            }
        }
    }
}
#endif
