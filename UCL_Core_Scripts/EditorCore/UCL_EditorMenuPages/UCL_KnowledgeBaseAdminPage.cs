// 區塊職責：知識庫後台管理頁 (Knowledge Base Admin) — Agent 長期記憶 / 文檔向量檢索的可視化管理入口。
//            (Tim 2026-07-23 拍板；參考 UCL_ChatTavernAdminPage 結構，命名對齊「知識庫」抽象而非插件名。)
// 物理意義：真正的環境檢查 / 安裝 / 建索引 / 檢索都在 knowledge_base.py；本頁只是 Cmd/runner 之上的薄 UI —
//          按鈕 → UCL_KnowledgeBaseRunner async spawn python → 顯示結果。不在 main thread 跑重活 (不凍結)。
// 設計取捨：嵌入後端走 FlagEmbedding 的真 bge-m3，但頁面與後端解耦、命名走「知識庫」— 換模型不必改頁。
//          UI 字串仿 UCL_ChatTavernAdminPage 慣例用 zh-Hant 硬編 (內部管理頁，不走 CodeLocalize)。
#if UNITY_EDITOR
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

        GUIStyle m_WrapStyle;
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
            RunOp("狀態", "status --format text", 60000);   // 抓一次狀態
        }

        // 直接讀 kb_targets.json（與 python 同一份 config）建 target 下拉 —
        // 同步、開頁即時，免 subprocess/async。config-driven：加 target = 改 config，本頁零改動。
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
                }
            }
        }

        // 區塊 4：檢索測試
        void DrawSearchPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>4. 檢索測試</b>（Editor 內驗證；高頻檢索 agent 直接呼 python）", WrapStyle);
                GUILayout.Label("Target 下拉來自 kb_targets.json；選 <b>all</b> 可跨全部語料庫一次搜（分數同空間可比）。", WrapStyle);
                DrawTargetPopup();
                m_SearchQuery = GUILayout.TextField(m_SearchQuery, UCL_GUIStyle.TextFieldStyle);
                using (new EditorGUI.DisabledScope(m_Busy || string.IsNullOrWhiteSpace(m_SearchQuery)))
                {
                    if (GUILayout.Button("🔍 執行檢索", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                    {
                        string arg = $"search --query {UCL_KnowledgeBaseRunner.QuoteArg(m_SearchQuery)} --target {TargetStr} --topk 5";
                        RunOp("檢索", arg, 120000);
                    }
                }
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
            if (win != null) win.Repaint();
        }
    }
}
#endif
