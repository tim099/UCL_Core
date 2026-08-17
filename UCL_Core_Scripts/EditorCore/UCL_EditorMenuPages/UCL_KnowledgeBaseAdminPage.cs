// 區塊職責：知識庫後台管理頁 (Knowledge Base Admin) — Agent 長期記憶 / 文檔向量檢索的可視化管理入口。
//            (Tim 2026-07-23 拍板；參考 UCL_ChatTavernAdminPage 結構，命名對齊「知識庫」抽象而非插件名。)
// 物理意義：真正的環境檢查 / 安裝 / 建索引 / 檢索都在 knowledge_base.py；本頁只是 Cmd/runner 之上的薄 UI —
//          按鈕 → UCL_KnowledgeBaseRunner async spawn python → 顯示結果。不在 main thread 跑重活 (不凍結)。
// 設計取捨：嵌入後端走 FlagEmbedding 的真 bge-m3，但頁面與後端解耦、命名走「知識庫」— 換模型不必改頁。
//          UI 字串仿 UCL_ChatTavernAdminPage 慣例用 zh-Hant 硬編 (內部管理頁，不走 CodeLocalize)。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.KnowledgeBase;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 知識庫後台管理頁 — 環境/模型狀態、依賴安裝、索引重建、檢索測試。
    /// 全部操作委派給 knowledge_base.py (經 UCL_KnowledgeBaseRunner)，與 agent 走 Cmd_KnowledgeBase 同一支腳本。
    /// </summary>
    // 知識庫 target 清單改為「執行期向 knowledge_base.py 的 `targets` op 動態抓」
    // (config-driven；加 target = 改 kb_targets.json，本頁零改動、下拉自動更新)。
    // 不再寫死 enum，也不再需要 C#/Python 雙邊手動同步。

    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_KnowledgeBaseAdminPage.md")]
    public class UCL_KnowledgeBaseAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "知識庫管理";
        public override bool ShowInPageMenu => true;

        public static UCL_KnowledgeBaseAdminPage Create() => UCL_EditorPage.Create<UCL_KnowledgeBaseAdminPage>();

        // ==== 狀態快取 ====
        string m_StatusText = "(尚未載入 — 按「🔄 重新整理狀態」)";
        string m_LastOutput = "";
        bool m_Busy = false;
        string m_BusyLabel = "";
        string m_SearchQuery = "如何為 UCL_Asset 設定 SaveFolderPath？";
        // config-driven target 清單（開頁向 python `targets` op 抓 + 附 "all"）。
        string[] m_Targets = new[] { "docs" };
        int m_TargetIdx = 0;
        /// <summary>目前選定 target 字串（CLI 用；含 "all" 跨庫多選）。</summary>
        string TargetStr => (m_Targets != null && m_TargetIdx >= 0 && m_TargetIdx < m_Targets.Length)
                            ? m_Targets[m_TargetIdx] : "docs";

        // ==== 檢索結果（結構化）====
        // 區塊職責：把 python `search --format json` 的 hits 存成可渲染的列，供每列掛「定位/預覽/開啟」。
        // 物理意義：舊版只把 stdout 整坨丟進 Box —— 看得到命中卻**到不了那份檔**（要自己複製路徑去找）。
        //          對齊 UCL_DocSearchPage 的三顆按鈕慣例（Tim 2026-08-16）。
        // 數值影響：m_Hits == null → 尚未搜過；Count == 0 → 搜過但無命中（兩者顯示不同，別合併）。
        class KbHit
        {
            public float Score;
            public string Target, Id, File, Rel, Preview;
            public int Line;
        }
        List<KbHit> m_Hits;
        string m_SearchHeader = "";   // 查詢摘要（延遲 / chunks / 自動補建紀錄）
        string m_SearchError = "";    // 檢索失敗訊息（含自動補建失敗原因）
        string m_StaleText = "";      // 索引新鮮度（stale op 的輸出）

        GUIStyle m_WrapStyle;
        GUIStyle m_TitleStyle;
        GUIStyle TitleStyle => m_TitleStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
        private UCL_ObjectDictionary m_Dic = new();
        GUIStyle WrapStyle
        {
            get
            {
                if (m_WrapStyle == null)
                    m_WrapStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapStyle;
            }
        }

        bool m_Loaded = false;   // 首幀 lazy-load 守門

        // ⚠ 初始化不能放 OnResume：OnResume 只在「從子頁 pop 返回」時觸發（見 UCL_GUIPageController.Pop），
        //    第一次開頁走 Push→Init、不呼叫 OnResume。故改比照 UCL_ChatTavernAdminPage 的 m_Loaded 慣例，
        //    在 ContentOnGUI 首幀 lazy-load（此時頁面已在 stack、EditorWindow context 有效，async RunOp 安全）。
        void RefreshAll()
        {
            LoadTargets();                                  // 直接讀 kb_targets.json 建下拉（同步、即時）
            RefreshTargetsFromPython();                     // 再補上 config `expand` 自動展開的 target（唯讀、62ms）
            RunOp("狀態", "status --format text", 60000);   // 抓一次狀態
            RunStale();                                     // 順手抓新鮮度（唯讀、不載模型）
        }

        // 區塊職責：索引新鮮度 —— 「磁碟變了，索引還是舊的」這件事要在畫面上看得到（Tim 2026-08-16 提問）
        // 物理意義：純 stat 比對，秒級、不載模型 ⇒ 便宜到可以開頁就跑。
        // ⚠ 它跟 m_Busy 分開排隊：新鮮度是唯讀觀測，不該被一次 reindex 卡住，也不該卡住別人。
        void RunStale()
        {
            RunStaleAsync().Forget();
        }

        async UniTaskVoid RunStaleAsync()
        {
            var r = await UCL_KnowledgeBaseRunner.RunAsync("stale --target all --format text",
                                                           CancellationToken.None, 120000);
            await UniTask.SwitchToMainThread();
            m_StaleText = r.DisplayText;
            EditorWindow.focusedWindow?.Repaint();
        }

        // ===========================================================
        // 區塊職責：把 python 端 `expand` **自動展開**的 target（如 frag_<persona> 每人一份索引）補進下拉。
        // 物理意義：`LoadTargets()` 只看 config 的 targets 字面 key，看不到展開結果 ——
        //          而展開的名單來自磁碟（新 persona 一出現就有），只有 python 那端知道全貌。
        //          `targets` op 就是為此存在的（唯讀、不載模型，實測 62ms），所以開頁時補跑一次。
        // 數值影響：純顯示層。失敗不清空既有下拉（寧可少幾個選項，不要突然只剩 fallback 的 docs）；
        //          合併後**依名字**還原選取項，不用 index —— 名單長度會變，index 會指到別的 target。
        // ===========================================================
        void RefreshTargetsFromPython() => RefreshTargetsFromPythonAsync().Forget();

        async UniTaskVoid RefreshTargetsFromPythonAsync()
        {
            var r = await UCL_KnowledgeBaseRunner.RunAsync("targets --format json",
                                                           CancellationToken.None, 60000);
            await UniTask.SwitchToMainThread();
            string stdout = r.Stdout ?? "";
            int s = stdout.IndexOf('{'), e = stdout.LastIndexOf('}');
            if (s < 0 || e <= s) return;                     // 拿不到就維持現況（同步那份仍可用）
            try
            {
                var root = JsonData.ParseJson(stdout.Substring(s, e - s + 1));
                if (root == null || !root.IsObject || !root.Contains("targets")) return;
                var arr = root["targets"];
                if (arr == null || !arr.IsArray || arr.Count == 0) return;

                string selected = TargetStr;                 // 先記名字，合併後照名字找回來
                var list = new System.Collections.Generic.List<string>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var pair = arr[i];                       // [name, desc]
                    string name = (pair != null && pair.IsArray && pair.Count > 0) ? pair[0].GetString() : null;
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("_") && !list.Contains(name))
                        list.Add(name);
                }
                if (list.Count == 0) return;
                list.Add("all");
                m_Targets = list.ToArray();
                int idx = System.Array.IndexOf(m_Targets, selected);
                m_TargetIdx = idx >= 0 ? idx : Mathf.Clamp(m_TargetIdx, 0, m_Targets.Length - 1);
                EditorWindow.focusedWindow?.Repaint();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[KnowledgeBaseAdminPage] targets op 解析失敗，維持 config 字面清單: {ex.Message}");
            }
        }

        // 直接讀 kb_targets.json（與 python 同一份 config）建 target 下拉 —
        // 同步、開頁即時，免 subprocess/async。config-driven：加 target = 改 config，本頁零改動。
        // ⚠ 這裡只看得到 config 的字面 key；`expand` 展開出來的（frag_<persona>）由
        //   RefreshTargetsFromPython() 補 —— 兩者刻意分開：同步那份保證開頁立刻有東西可選。
        void LoadTargets()
        {
            //Debug.LogError("LoadTargets");
            System.Collections.Generic.List<string> list = new();
            try
            {
                string dir = Path.GetDirectoryName(UCL_KnowledgeBaseRunner.ScriptPath) ?? "";
                string cfgPath = Path.Combine(dir, "kb_targets.json");
                if (File.Exists(cfgPath))
                {
                    var root = JsonData.ParseJson(File.ReadAllText(cfgPath));
                    if (root != null && root.IsObject && root.Contains("targets"))
                    {
                        var targets = root["targets"];
                        if (targets != null && targets.IsObject)
                        {
                            foreach (var key in targets.Keys)
                            {
                                if (!string.IsNullOrEmpty(key) && !key.StartsWith("_"))
                                    list.Add(key);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[KnowledgeBaseAdminPage] 讀 kb_targets.json 失敗，改用 fallback: {e.Message}");
                Debug.LogException(e);
            }
            //Debug.LogError($"LoadTargets:{list.ConcatToString()}");
            if (list.Count == 0) list.Add("docs");   // fallback：config 缺失/壞檔時至少有 docs
            list.Add("all");                          // 跨庫一次搜 / 全量重建
            m_Targets = list.ToArray();
            
            m_TargetIdx = Mathf.Clamp(m_TargetIdx, 0, m_Targets.Length - 1);
        }

        // 共用 target 下拉（config-driven，含 all）
        void DrawTargetPopup()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Target", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                int idx = Mathf.Clamp(m_TargetIdx, 0, Mathf.Max(0, m_Targets.Length - 1));
                m_TargetIdx = UCL_GUILayout.PopupSearchCache(idx, m_Targets, m_Dic, nameof(m_TargetIdx));
            }
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Busy))
            {
                if (GUILayout.Button("🔄 重新整理狀態", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    RefreshAll();   // 同時重讀 kb_targets.json（config 改了下拉也刷新）
            }
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) { m_Loaded = true; RefreshAll(); }   // 首幀 lazy-load（取代失效的 OnResume 初始化）
            GUILayout.Label("🧠 Agent 知識庫 / 長期記憶向量檢索", WrapStyle);
            EditorGUILayout.HelpBox(
                "管理 Agent 知識庫：文檔 / 經驗庫的向量索引與語意檢索。" +
                "計算全在 knowledge_base.py（嵌入後端 FlagEmbedding 的 bge-m3，可經 KB_EMBED_MODEL 換模型），" +
                "本頁與 agent 走 Cmd_KnowledgeBase 同一支腳本。",
                MessageType.Info);

            if (m_Busy)
                EditorGUILayout.HelpBox($"⏳ 執行中：{m_BusyLabel}…（python 於背景執行，完成後自動更新）", MessageType.Warning);

            GUILayout.Space(6);
            DrawStatusPanel();
            GUILayout.Space(6);
            DrawSetupPanel();
            GUILayout.Space(6);
            DrawReindexPanel();
            GUILayout.Space(6);
            DrawSearchPanel();
            GUILayout.Space(6);
            DrawOutputPanel();
        }

        // 區塊 1：環境 / 模型 / 索引狀態
        void DrawStatusPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>1. 環境與索引狀態</b>", WrapStyle);
                GUILayout.Label(string.IsNullOrEmpty(m_StatusText) ? "(無)" : m_StatusText, WrapStyle);
            }
        }

        // 區塊 2：依賴安裝與模型權重
        void DrawSetupPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>2. 依賴安裝與權重</b>（首次啟用需先安裝 + 預熱；torch 較大，請耐心等）", WrapStyle);
                EditorGUILayout.HelpBox("安裝走 knowledge_base.py（op=install），跨專案/機器可重現。torch 下載可能數分鐘。", MessageType.None);
                using (new EditorGUI.DisabledScope(m_Busy))
                {
                    if (GUILayout.Button("📦 安裝 bge-m3 依賴（FlagEmbedding + torch）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(30)))
                        RunOp("安裝 bge-m3 依賴", "install --full", 1800000);
                    if (GUILayout.Button("⬇️ 下載並預熱 bge-m3 權重（~1.2GB）", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("預熱權重", "prefetch", 1800000);
                }
            }
        }

        // 區塊 3：索引重建
        void DrawReindexPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>3. 知識庫索引重建</b>（掃描文件 → 切塊 → 建向量）", WrapStyle);
                EditorGUILayout.HelpBox("target 清單來自 kb_targets.json（config-driven，加 target 免改 code）。選單一或 all（全部重建）。有 GPU 時全量約數分鐘。", MessageType.None);
                DrawTargetPopup();
                using (new EditorGUI.DisabledScope(m_Busy))
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button($"🔨 重建「{TargetStr}」索引", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp($"重建 {TargetStr} 索引", $"reindex --target {TargetStr}", 1800000);
                    if (GUILayout.Button("🧱 重建全部 (all)", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("重建全部索引", "reindex --target all", 1800000);
                    if (GUILayout.Button("🧭 檢查新鮮度", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunStale();
                }
                // 區塊職責：把「哪份索引落後磁碟」直接攤在重建按鈕旁邊
                // 物理意義：重建是有代價的動作，人需要先知道「有沒有必要」；
                //          而檢索本身已經會自動增量更新 —— 這區是給「想先看清楚再決定」的人，不是必經步驟。
                if (!string.IsNullOrEmpty(m_StaleText))
                    GUILayout.Label(m_StaleText, WrapStyle);
            }
        }

        // 區塊 4：檢索（含「缺索引就地補建」）
        void DrawSearchPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>4. 檢索</b>（Editor 內查；高頻檢索 agent 直接呼 python）", WrapStyle);
                GUILayout.Label("Target 下拉來自 kb_targets.json；選 <b>all</b> 可跨全部語料庫一次搜（分數同空間可比）。\n"
                                + "選到<b>尚未建索引</b>的 target 時**不會報錯，會就地建**（首次可能數十秒~數分鐘，建完立刻查）。", WrapStyle);
                DrawTargetPopup();
                m_SearchQuery = GUILayout.TextField(m_SearchQuery, UCL_GUIStyle.TextFieldStyle);
                using (new EditorGUI.DisabledScope(m_Busy || string.IsNullOrWhiteSpace(m_SearchQuery)))
                {
                    if (GUILayout.Button("🔍 執行檢索", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                    {
                        // ⚠ 逾時比照 reindex（不是 120s）——「查詢時自動建索引」讓 search 有可能真的花上數分鐘，
                        //   沿用舊的 120s 會讓「正在建索引」長得跟「壞掉」一模一樣（而它其實正在做對的事）。
                        string arg = $"search --query {UCL_KnowledgeBaseRunner.QuoteArg(m_SearchQuery)} --target {TargetStr} --topk 8 --format json";
                        RunOp("檢索", arg, 1800000);
                    }
                }
                DrawSearchResults();
            }
        }

        // ===========================================================
        // 區塊：檢索結果列表 — 每列 定位 / 預覽 / 開啟（對齊 UCL_DocSearchPage）
        // 物理意義：命中的是磁碟上一份真檔案，人要能**到得了它**；只印路徑字串等於要人自己再找一次。
        // 數值影響：純顯示；按鈕行為委派既有實作（RevealInFinder / UCL_MarkdownViewerPage / OpenDocByUrl），
        //          不在本頁重造第二套開檔邏輯。
        // ===========================================================
        void DrawSearchResults()
        {
            if (!string.IsNullOrEmpty(m_SearchError))
                EditorGUILayout.HelpBox(m_SearchError, MessageType.Error);
            if (m_Hits == null) return;

            if (!string.IsNullOrEmpty(m_SearchHeader))
                GUILayout.Label(m_SearchHeader, WrapStyle);
            if (m_Hits.Count == 0)
            {
                // 「查了，0 命中」與「沒查」要能分辨 —— 空結果是一個答案，不是沒有答案
                EditorGUILayout.HelpBox("查了，0 命中（索引存在但沒有語意相近的片段）。", MessageType.Info);
                return;
            }
            for (int i = 0; i < m_Hits.Count; i++) DrawHitRow(i, m_Hits[i]);
        }

        void DrawHitRow(int idx, KbHit hit)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                string abs = (hit.File ?? "").Replace('\\', '/');
                string rel = string.IsNullOrEmpty(hit.Rel) ? abs : hit.Rel;
                bool exists = !string.IsNullOrEmpty(abs) && File.Exists(abs);
                using (new GUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!exists))
                    {
                        if (GUILayout.Button("📂 定位", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            EditorUtility.RevealInFinder(abs);
                        // 預覽只給 .md —— MarkdownViewer 對 .jsonl（lessons）沒有意義，
                        // 給一顆按下去只會顯示原始行的按鈕，是把「能看」講得比事實大。
                        if (abs.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase))
                        {
                            if (GUILayout.Button("📄 預覽", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                UCL_MarkdownViewerPage.Create(rel, abs);
                        }
                        if (GUILayout.Button("📖 開啟", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                            UCL_DocSearchPage.OpenDocByUrl(rel, abs);
                    }
                    GUILayout.Label($"#{idx + 1}", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    GUILayout.Label($"★ {hit.Score:0.0000}", UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.83f, 0.48f)));
                    GUILayout.Label($"({hit.Target}) {hit.Id}", TitleStyle);
                    GUILayout.FlexibleSpace();
                }
                // 檔案不存在 = 索引比磁碟舊（檔被改名/刪了）。這種列必須說出來，
                // 否則使用者會以為是按鈕壞了，而真相是該重建索引。
                if (!exists)
                    GUILayout.Label("  <color=#FF9B9B>⚠ 檔案不存在（索引比磁碟舊 → 重建該 target 索引）</color>", WrapStyle);
                string line = hit.Line > 0 ? $" (L{hit.Line})" : "";
                GUILayout.Label($"  {rel}{line}", UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(hit.Preview))
                    GUILayout.Label($"  {hit.Preview}", WrapStyle);
            }
        }

        // 區塊 5：輸出
        void DrawOutputPanel()
        {
            if (string.IsNullOrEmpty(m_LastOutput)) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("<b>📋 最近操作結果</b>", WrapStyle);
                if (GUILayout.Button("Copy", UCL_GUIStyle.ButtonStyle))
                {
                    EditorGUIUtility.systemCopyBuffer = m_LastOutput;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Box(m_LastOutput, UCL_GUIStyle.BoxStyle);
            }
        }

        // ===========================================================
        // 區塊：async 執行 — 委派 UCL_KnowledgeBaseRunner，完成後回主執行緒更新 UI + Repaint
        // 物理意義：重活 (install/prefetch/reindex) 全在背景 python，Editor 不凍結；完成才刷 UI。
        // ===========================================================
        void RunOp(string label, string argLine, int timeoutMs)
        {
            if (m_Busy) return;
            m_Busy = true;
            m_BusyLabel = label;
            var win = EditorWindow.focusedWindow;   // 捕捉當前宿主視窗，完成後主動 Repaint
            RunOpAsync(label, argLine, timeoutMs, win).Forget();
        }

        async UniTaskVoid RunOpAsync(string label, string argLine, int timeoutMs, EditorWindow win)
        {
            var r = await UCL_KnowledgeBaseRunner.RunAsync(argLine, CancellationToken.None, timeoutMs);
            await UniTask.SwitchToMainThread();
            m_Busy = false;
            m_BusyLabel = "";
            m_LastOutput = $"[{label}]\n{r.DisplayText}";
            if (label == "狀態") m_StatusText = r.DisplayText;
            if (label == "檢索") ParseSearchJson(r.DisplayText);
            if (win != null) win.Repaint();
        }

        // ===========================================================
        // 區塊：search --format json → m_Hits
        // 物理意義：python 是唯一真相源，本頁只是把它的 hits 轉成可點的列；解析失敗**不吞**——
        //          原始輸出仍留在「最近操作結果」面板，錯誤另外顯示（不要讓人對著空列表猜）。
        // 數值影響：只認 stdout 裡第一個 '{' 到最後一個 '}'（tqdm 進度條走 stderr，正常不混入；
        //          真混進來時這個夾子讓解析仍成立）。
        // ===========================================================
        void ParseSearchJson(string stdout)
        {
            m_Hits = null;
            m_SearchHeader = "";
            m_SearchError = "";
            if (string.IsNullOrEmpty(stdout)) { m_SearchError = "檢索無輸出（python 未啟動或被逾時砍掉）。"; return; }
            int s = stdout.IndexOf('{'), e = stdout.LastIndexOf('}');
            if (s < 0 || e <= s) { m_SearchError = "檢索輸出不是 JSON（見下方原始輸出）。"; return; }
            try
            {
                var root = JsonData.ParseJson(stdout.Substring(s, e - s + 1));
                if (root == null || !root.IsObject) { m_SearchError = "檢索 JSON 解析失敗（見下方原始輸出）。"; return; }

                // 自動補建紀錄：成功與失敗都顯示 —— 那是一筆改了磁碟的動作，不可靜默
                string autoLine = "";
                if (root.Contains("auto_reindexed") && root["auto_reindexed"] != null && root["auto_reindexed"].IsArray)
                {
                    var arr = root["auto_reindexed"];
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var a = arr[i];
                        bool ok = a.Contains("ok") && a["ok"].GetString() != null && a["ok"].GetString().ToLower() == "true";
                        autoLine += ok
                            ? $"\n🔨 自動建索引 <b>{a.GetString("target", "?")}</b>：{a.GetString("files", "?")} 檔 → {a.GetString("chunks", "?")} chunks"
                            : $"\n⚠ 自動建索引 <b>{a.GetString("target", "?")}</b> 失敗：{a.GetString("error", "")}";
                    }
                }

                if (!root.Contains("hits") || root["hits"] == null || !root["hits"].IsArray)
                {
                    // ok=false 的失敗路徑：把 error 攤在面板上（含補建失敗原因）
                    m_SearchError = root.GetString("error", "檢索失敗（無 hits 欄位）") + autoLine;
                    return;
                }
                var hits = root["hits"];
                var list = new List<KbHit>(hits.Count);
                for (int i = 0; i < hits.Count; i++)
                {
                    var h = hits[i];
                    list.Add(new KbHit
                    {
                        Score = h.GetFloat("score", 0f),
                        Target = h.GetString("target", "?"),
                        Id = h.GetString("id", ""),
                        File = h.GetString("file", ""),
                        Rel = h.GetString("rel", ""),
                        Line = h.GetInt("line", 0),
                        Preview = h.GetString("preview", ""),
                    });
                }
                m_Hits = list;
                m_SearchHeader = $"🔍 <b>{root.GetString("query", "")}</b> — {list.Count} 命中 / "
                                 + $"掃 {root.GetString("searched_chunks", "?")} chunks / {root.GetString("latency_ms", "?")}ms"
                                 + autoLine
                                 + (root.Contains("note") && !string.IsNullOrEmpty(root.GetString("note", "")) ? $"\n⚠ {root.GetString("note", "")}" : "");
            }
            catch (System.Exception ex)
            {
                m_SearchError = $"檢索 JSON 解析例外：{ex.Message}（原始輸出見下方面板）";
            }
        }
    }
}
#endif
