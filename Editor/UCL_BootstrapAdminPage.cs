// 區塊職責：UCL_Core Bootstrap 手動操作頁 — 把 Tools/UCL/Bootstrap/ 選單下的 4 個動作集中成一個 IMGUI Page
// 物理意義：使用者懶得翻 Tools 選單時，從 EditorMenu Page Picker 下拉直接開本頁；
//          按鈕對應 UCL_CoreAssetBootstrap 的 Menu_* 入口。本頁不放 InitializeOnLoad 自動邏輯。
// 數值影響：純 UI 殼 — 全部按鈕走既有 static method（Menu_ApplyMissing / Menu_Diff / Menu_ForceReapply / Menu_PushTemplates）
// 設計取捨：放 Page Picker（低頻 admin 工具）而非外部按鈕（首屏 UCL_EditorMenuPage 不擁擠）— 對齊 Create_EditorPage_Workflow §4.1
#if UNITY_EDITOR
using UCL.Core.EditorLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// UCL_Core Bootstrap 手動操作頁 — 集中 Apply Missing / Diff / Force Re-Apply / Push Templates 四個動作。
    /// 平時 AutoApplyIfNeeded / AutoTemplatePushIfNeeded 在 Editor reload 時自動跑；本頁給「想立即觸發」的場景用。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md")]
    public class UCL_BootstrapAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_Core Bootstrap Admin";

        // opt-in 進 UCL_EditorMenuPage 的 Page Picker 下拉
        public override bool ShowInPageMenu => true;

        public static UCL_BootstrapAdminPage Create() => UCL_EditorPage.Create<UCL_BootstrapAdminPage>();

        // wordWrap label 樣式 — 底部說明用
        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };

        protected override void ContentOnGUI()
        {
            DrawHeader();
            GUILayout.Space(8);
            DrawAutoMechanismInfo();
            GUILayout.Space(8);
            DrawApplyMissingSection();
            GUILayout.Space(4);
            DrawPushTemplatesSection();
            GUILayout.Space(4);
            DrawDiffSection();
            GUILayout.Space(4);
            DrawForceReapplySection();
            GUILayout.Space(8);
            DrawDocLink();
        }

        void DrawHeader()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var title = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                GUILayout.Label("🔧 UCL_Core Bootstrap Admin", title);
                GUILayout.Label("Templates~ ↔ 專案 .BuiltinModules 同步操作", new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic,
                });
            }
        }

        // 區塊職責：說明自動機制何時 fire（給使用者建立心理模型 — 通常不必手動跑）
        void DrawAutoMechanismInfo()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("自動觸發機制（多數情況不必手動操作）", new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold });
                GUILayout.Label(
                    "[InitializeOnLoadMethod] + delayCall — 每次 Editor 啟動 / domain reload 自動跑：\n" +
                    "  • AutoApplyIfNeeded：補缺漏的 Templates 檔（marker 版控）\n" +
                    "  • AutoTemplatePushIfNeeded：推送 Templates 變動（含衝突 dialog + skip marker）\n\n" +
                    "需要手動跑的場景：bash 直接刪檔、外部改檔、想立刻看 diff、想強制覆寫所有檔。",
                    WrapLabelStyle);
            }
        }

        // ===== 各動作 section（每段一個 box + 說明 + 按鈕） =====

        void DrawApplyMissingSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("① Apply Missing Defaults（補缺）", new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold });
                GUILayout.Label(
                    "把 Templates~ 內專案還沒有的檔案複製過來。create_if_missing 語意 — 已存在則 skip。",
                    WrapLabelStyle);
                if (GUILayout.Button("▶ Apply Missing Defaults",
                    UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 0.6f)),
                    GUILayout.Width(220), GUILayout.Height(28)))
                {
                    UCL_CoreAssetBootstrap.Menu_ApplyMissing();
                }
            }
        }

        void DrawPushTemplatesSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("② Push Templates → Modules（推送 Template 變動）", new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold });
                GUILayout.Label(
                    "Templates~ 內檔案有更新時，把新版推進專案 .BuiltinModules。\n" +
                    "新檔 silent 複製、衝突 per-file dialog（Win Explorer 風格）。menu 入口忽略 skip marker，強迫重問所有衝突。",
                    WrapLabelStyle);
                if (GUILayout.Button("▶ Push Templates → Modules",
                    UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 0.8f, 1f)),
                    GUILayout.Width(220), GUILayout.Height(28)))
                {
                    UCL_CoreAssetBootstrap.Menu_PushTemplates();
                }
            }
        }

        void DrawDiffSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("③ Diff Against Templates（純讀，看差異）", new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold });
                GUILayout.Label(
                    "掃描 Templates~ 列出 missing / modified / identical 摘要到 Console，不寫任何檔。",
                    WrapLabelStyle);
                if (GUILayout.Button("▶ Diff (Console output)",
                    UCL_GUIStyle.ButtonStyle,
                    GUILayout.Width(220), GUILayout.Height(28)))
                {
                    UCL_CoreAssetBootstrap.Menu_Diff();
                }
            }
        }

        void DrawForceReapplySection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("④ Force Re-Apply（破壞性 — 用 Templates 強覆寫專案）", new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.5f, 0.3f) } });
                GUILayout.Label(
                    "⚠ 用 Templates~ 範本【覆寫】所有對應 Asset，使用者本地修改會遺失。會先彈 confirm dialog。",
                    WrapLabelStyle);
                if (GUILayout.Button("▶ Force Re-Apply (Overwrite!)",
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.4f)),
                    GUILayout.Width(220), GUILayout.Height(28)))
                {
                    UCL_CoreAssetBootstrap.Menu_ForceReapply();
                }
            }
        }

        void DrawDocLink()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("📖 詳細機制：", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                GUILayout.Label("Docs~/zh-Hant/UCL_ModuleService/UCL_CoreBootstrap.md", UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button("Open", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Application.OpenURL(UCL_URL.ResolveURL("ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md"));
                }
            }
        }
    }
}
#endif
