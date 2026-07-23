// 區塊職責：知識庫後台管理頁 (Knowledge Base Admin) — Agent 長期記憶 / 文檔向量檢索的可視化管理入口。
//            (Tim 2026-07-23 拍板；參考 UCL_ChatTavernAdminPage 結構，命名對齊「知識庫」抽象而非插件名。)
// 物理意義：真正的環境檢查 / 安裝 / 建索引 / 檢索都在 knowledge_base.py；本頁只是 Cmd/runner 之上的薄 UI —
//          按鈕 → UCL_KnowledgeBaseRunner async spawn python → 顯示結果。不在 main thread 跑重活 (不凍結)。
// 設計取捨：嵌入後端走 FlagEmbedding 的真 bge-m3，但頁面與後端解耦、命名走「知識庫」— 換模型不必改頁。
//          UI 字串仿 UCL_ChatTavernAdminPage 慣例用 zh-Hant 硬編 (內部管理頁，不走 CodeLocalize)。
#if UNITY_EDITOR
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.KnowledgeBase;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 知識庫後台管理頁 — 環境/模型狀態、依賴安裝、索引重建、檢索測試。
    /// 全部操作委派給 knowledge_base.py (經 UCL_KnowledgeBaseRunner)，與 agent 走 Cmd_KnowledgeBase 同一支腳本。
    /// </summary>
    /// <summary>
    /// 知識庫語料庫 target — 對應 knowledge_base.py 的 TARGET_DEFS。
    /// ⚠ 新增 target 時兩邊都要同步：本 enum + python 的 TARGET_DEFS / resolve_target_sources()。
    /// enum 名 PascalCase，傳給 CLI 時轉小寫 (Docs → "docs")。
    /// </summary>
    public enum KnowledgeBaseTarget { Docs, Lessons }

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
        KnowledgeBaseTarget m_SearchTarget = KnowledgeBaseTarget.Docs;
        readonly UCL_ObjectDictionary m_TargetDic = new UCL_ObjectDictionary();  // enum popup 快取
        /// <summary>target enum → CLI 小寫字串 (Docs → "docs")。</summary>
        string TargetStr => m_SearchTarget.ToString().ToLowerInvariant();

        GUIStyle m_WrapStyle;
        GUIStyle WrapStyle
        {
            get
            {
                if (m_WrapStyle == null)
                    m_WrapStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapStyle;
            }
        }

        public override void OnResume()
        {
            base.OnResume();
            RunOp("狀態", "status --format text", 60000);   // 開頁自動抓一次狀態
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Busy))
            {
                if (GUILayout.Button("🔄 重新整理狀態", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    RunOp("狀態", "status --format text", 60000);
            }
        }

        protected override void ContentOnGUI()
        {
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
                EditorGUILayout.HelpBox("target 只有兩個：docs＝專案 Docs/**/*.md；lessons＝AgentCommands/Lessons 經驗庫。填其他值會報未知 target（新增需改 knowledge_base.py）。", MessageType.None);
                using (new EditorGUI.DisabledScope(m_Busy))
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("📚 重建 Docs 索引", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("重建 Docs 索引", "reindex --target docs", 300000);
                    if (GUILayout.Button("🧠 重建 Lessons 索引", UCL_GUIStyle.ButtonStyle, GUILayout.Height(28)))
                        RunOp("重建 Lessons 索引", "reindex --target lessons", 300000);
                }
            }
        }

        // 區塊 4：檢索測試
        void DrawSearchPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>4. 檢索測試</b>（Editor 內驗證；高頻檢索 agent 直接呼 python）", WrapStyle);
                GUILayout.Label("Target：<b>Docs</b>＝專案文檔 / <b>Lessons</b>＝經驗庫（下拉選單，無法填錯）。", WrapStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Target", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_SearchTarget = UCL_GUILayout.Popup(m_SearchTarget, m_TargetDic, null, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    using (new EditorGUI.DisabledScope(m_Busy))
                    {
                        if (GUILayout.Button($"重建 {TargetStr} 索引", UCL_GUIStyle.ButtonStyle))
                            RunOp($"重建 {TargetStr} 索引", $"reindex --target {TargetStr}", 300000);
                    }
                }
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
                GUILayout.Label("<b>📋 最近操作結果</b>", WrapStyle);
                EditorGUILayout.TextArea(m_LastOutput, GUILayout.MinHeight(80));
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
