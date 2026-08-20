// 區塊職責：酒館 CLI 指令設定頁 —— 顯示全部指令、改指令 id（如把 help 改名）、增刪指令、
//          編輯每個指令的行為清單（[SerializeReference] 多型下拉）。
// 物理意義：資料本體是 `ChatTavern/bartender/cli_commands/<id>.json` 一指令一檔
//          （UCL_BartenderCliCommandStore）；本頁只是那批檔案的編輯視圖。
//          入口在酒保管理頁的「🔧 酒館 CLI」區塊。
// 數值影響：**存檔是顯式按鈕**，不做逐 keystroke 自動存 —— id 同時是檔名，
//          打字打到一半自動存會把半成品 id 寫成檔案（然後下一個字母又生一個檔）。
//          未存檔的變更會顯示提示；重新載入會丟棄未存檔變更（按鈕上講明）。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UCL.Core;
using UCL.Core.EditorLib.AgentCommands.Bartender;
using UCL.Core.UI;

namespace UCL.Core.EditorLib.Page
{
    public class UCL_BartenderCliCommandsPage : UCL_CommonEditorPage
    {
        List<UCL_BartenderCliCommandConfig> m_Configs = new List<UCL_BartenderCliCommandConfig>();
        readonly UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        bool m_Dirty;
        string m_Status = "";

        public override string WindowName => "酒館 CLI 指令設定";
        public override bool ShowInPageMenu => false;
        public static UCL_BartenderCliCommandsPage Create() => UCL_EditorPage.Create<UCL_BartenderCliCommandsPage>();

        public override void Init(UCL_GUIPageController iGUIPage)
        {
            base.Init(iGUIPage);
            Reload();
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("💾 存檔", UCL_GUIStyle.ButtonStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(90))))
                {
                    UCL_BartenderCliCommandStore.SaveAll(m_Configs);
                    Reload();   // 回讀而不是假設寫入成功 —— id 正規化後的樣子要讓使用者看到
                    m_Status = "已存檔並回讀。";
                }
                if (GUILayout.Button("↻ 重新載入（丟棄未存檔變更）", UCL_GUIStyle.ButtonStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(220))))
                {
                    Reload();
                }
                // 開設定檔所在資料夾（同 LibraryManagePage 開 BookNotes 的用途；
                // 走 UCL_ExplorerUtil 而不是 Application.OpenURL —— 外部 Process 要登記）
                if (GUILayout.Button("📂 開啟設定檔位置", UCL_GUIStyle.ButtonStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(150))))
                {
                    UCL_ExplorerUtil.Open(UCL_BartenderCliCommandStore.GetDir(), "BartenderCliCommandsPage");
                }
            }
        }
        void Reload()
        {
            m_Configs = UCL_BartenderCliCommandStore.LoadAll();
            m_Dirty = false;
            m_Status = $"已載入 {m_Configs.Count} 個指令（{UCL_BartenderCliCommandStore.GetDir()}）";
        }

        protected override void ContentOnGUI()
        {
            using (new GUILayout.VerticalScope())
            {
                GUILayout.Label("<b>🔧 酒館 CLI 指令設定</b>",
                    new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, fontSize = 18 });
                GUILayout.Label("一指令一檔；id 就是使用者在酒館打的那個字（比對不分大小寫）。"
                    + "改 id ＝ 改名（存檔時舊檔會被清掉）。行為由上往下依序執行。",
                    UCL_GUIStyle.LabelStyle);



                if (m_Dirty)
                {
                    GUILayout.Label("⚠ 有未存檔的變更 —— 按「💾 存檔」才會寫回 json（含改名的檔案搬移）。",
                        new GUIStyle(UCL_GUIStyle.LabelStyle) { normal = { textColor = Color.yellow } });
                }
                else if (!string.IsNullOrEmpty(m_Status))
                {
                    GUILayout.Label(m_Status, UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Space(UCL_GUIStyle.GetScaledSize(6));

                // 整份 List 交給 DrawObjectData —— 新增／刪除／搬移／[SerializeReference] 多型下拉全內建，
                // config 加欄位本頁一行都不用改（Tim 2026-08-20 指定走 DrawObjectData）。
                EditorGUI.BeginChangeCheck();
                m_Configs = (List<UCL_BartenderCliCommandConfig>)UCL_GUILayout.DrawObjectData(
                    m_Configs, m_DataDic, "指令清單");
                if (EditorGUI.EndChangeCheck()) m_Dirty = true;
            }
        }
    }
}
#endif
