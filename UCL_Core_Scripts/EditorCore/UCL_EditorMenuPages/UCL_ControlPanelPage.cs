// 區塊職責：控制台 (Control Panel) IMGUI 頁面 — 集中控制專案內各項重要設定。
// 物理意義：給人類開發者一個總控台統一開關各子系統；目前第一塊功能是「聊天酒館系統」總開關。
//          設計成可擴充 — 之後新增其他設定 (e.g. Discord / 排程 / 渲染) 各自再加一個 section method。
// 設計取捨 (Tim 2026-05-28 拍板)：
//   - 仿 UCL_ChatTavernPage 提升為 EditorMenu 外部主要按鈕 (ShowInPageMenu => false)
//   - 酒館系統開關預設關閉，存 PlayerPrefs (走 UCL_ChatTavernSystemControl 單一真相源)
//   - 關閉 → 停止酒館各自動廣播 + 背景程序 (Bartender daemon / Discord inbound daemon)
//   - 打開 → SetEnabled 內由 OFF→ON 自動 fire 重啟，讓 daemon 重新初始化
#if UNITY_EDITOR
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 控制台頁面 — 專案重要設定的總控制面板。第一塊：聊天酒館系統總開關。
    /// </summary>
    public class UCL_ControlPanelPage : UCL_CommonEditorPage
    {
        public override string WindowName => "控制台";

        // 已提升為 EditorMenu 外部主要按鈕 (見 UCL_EditorMenuPage)，關閉下拉避免重複出現
        public override bool ShowInPageMenu => false;

        public static UCL_ControlPanelPage Create() => UCL_EditorPage.Create<UCL_ControlPanelPage>();

        protected override void ContentOnGUI()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>控制台 — 專案重要設定</b>", UCL_GUIStyle.LabelStyle);
            }
            GUILayout.Space(8);

            DrawChatTavernSystemSection();
        }

        // ===========================================================
        // 區塊：聊天酒館系統總開關
        // 物理意義：讀 UCL_ChatTavernSystemControl.IsEnabled 顯示當前狀態；toggle 變動 → SetEnabled。
        //          OFF→ON 由 SetEnabled 內部自動重啟 daemon；另提供「重啟系統」按鈕手動重觸發。
        // 數值影響：寫 PlayerPrefs "UCL.ChatTavern.System.Enabled"；fire OnSystemRestart 通知 daemon。
        // ===========================================================
        void DrawChatTavernSystemSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // 標題 + 當前狀態燈
                bool enabled = UCL_ChatTavernSystemControl.IsEnabled;
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("<b>聊天酒館系統</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                    var stateStyle = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    stateStyle.normal.textColor = enabled ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.5f, 0.4f);
                    GUILayout.Label(enabled ? "● 運行中" : "○ 已停止", stateStyle);
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Space(2);
                GUILayout.Label(
                    "控制酒保自動廣播（關鍵字觸發 / 時間規則 / 跨日保管費）與 Discord inbound 背景子程序。\n" +
                    "關閉後酒館停止一切自動廣播與背景程序；打開時自動重啟系統。",
                    UCL_GUIStyle.LabelStyle);

                GUILayout.Space(4);

                // 開關 toggle — 變動才寫，避免每幀 PlayerPrefs IO
                using (new GUILayout.HorizontalScope())
                {
                    bool newEnabled = GUILayout.Toggle(
                        enabled,
                        enabled ? " 系統啟用中（按一下關閉）" : " 系統已關閉（按一下啟用）",
                        UCL_GUIStyle.ButtonStyle,
                        GUILayout.ExpandWidth(false));
                    if (newEnabled != enabled)
                    {
                        UCL_ChatTavernSystemControl.SetEnabled(newEnabled);
                        Debug.Log($"[ControlPanel] 聊天酒館系統 → {(newEnabled ? "啟用 (自動重啟)" : "關閉")}");
                    }

                    GUILayout.Space(8);

                    // 手動重啟 — 只有系統啟用時才有意義 (停止狀態重啟無作用)
                    using (new UnityEditor.EditorGUI.DisabledScope(!enabled))
                    {
                        if (GUILayout.Button("重啟系統", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            UCL_ChatTavernSystemControl.Restart();
                            Debug.Log("[ControlPanel] 手動重啟聊天酒館系統");
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }
    }
}
#endif
