// 區塊職責：Cmd 後台管理頁 — Agent Command 的註冊清單與 **schema 同步**入口（Tim 2026-07-29 拍板）。
// 物理意義：Python client 端的參數預檢靠 <RepoRoot>/AgentCommands/commands_schema.json，
//          而那份產物是由 C# 反射生成的。以前 Python 那張表是手抄的，抄漏就會發生
//          「C# 有實作但 client 擋死」（血證 2026-07-29：create_trpg_room）。
//          本頁提供 Tim 要的**手動刷新**：看得到同步狀態、一鍵重新生成。
//          與 Cmd_ExportCmdSchema 等價 —— 兩者呼叫同一個 UCL_CmdSchemaExporter.Export()。
// 數值影響：唯一寫入動作是「重新生成」按鈕（且內容未變則不落筆）；其餘皆唯讀。
// 設計取捨：UI 字串仿 UCL_ControlPanelPage / UCL_ChatTavernAdminPage 慣例用 zh-Hant 硬編
//          （內部管理頁，不走 CodeLocalize）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Cmd 後台管理頁 — 註冊清單 + schema 同步。
    /// 入口：控制台 (UCL_ControlPanelPage) 的「🧾 Cmd 後台管理」按鈕。
    /// </summary>
    public class UCL_AgentCmdAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Cmd 後台管理";
        public override bool ShowInPageMenu => true;

        public static UCL_AgentCmdAdminPage Create() => UCL_EditorPage.Create<UCL_AgentCmdAdminPage>();

        // 折疊狀態 — 與 UCL_ControlPanelPage / UCL_ChatTavernAdminPage 同慣例，
        // 型別必須是 UCL_ObjectDictionary（UCL_GUILayout.Toggle 的第一參數型別）
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        // 上一次按下「重新生成」的結果摘要 —— 只是給人看的回饋，不是狀態來源
        string m_LastActionMsg = "";

        protected override void ContentOnGUI()
        {
            DrawSyncSection();
            GUILayout.Space(6);
            DrawCommandListSection();
        }

        // ===========================================================
        // 區塊職責：schema 同步狀態 + 手動刷新按鈕（本頁的主要目的）。
        // 物理意義：同步判準是**內容雜湊**不是檔案時間 —— git 不儲存 mtime，
        //          clone 後所有檔案時間都是當下，用 mtime 判會在最該生效的場景擲骰子。
        // 數值影響：IsInSync 純讀取；只有按下按鈕才可能寫檔（且內容未變不落筆）。
        // ===========================================================
        void DrawSyncSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool inSync = false;
                string artifactHash = null, currentHash = null;
                try
                {
                    inSync = UCL_CmdSchemaExporter.IsInSync(out artifactHash, out currentHash);
                }
                catch (Exception e)
                {
                    GUILayout.Label($"<color=red>同步狀態查詢失敗：{e.Message}</color>", UCL_GUIStyle.LabelStyle);
                }
                bool showDetail = false;
                using (new GUILayout.HorizontalScope())
                {
                    showDetail = UCL_GUILayout.Toggle(m_FoldDic, "SyncDetailFold", 18, iDefaultValue: false);
                    GUILayout.Label("<b>🔄 Cmd Schema 同步</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(inSync
                            ? "<color=#5FD35F>✅ 已同步</color>"
                            : "<color=#FFB347>⚠ 未同步（產物落後於 Cmd 原始碼）</color>",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("重新生成 commands_schema.json",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 0.7f)), GUILayout.ExpandWidth(false)))
                    {
                        RegenerateSchema();
                    }
                }

                GUILayout.Label("Python client 端 (`tavern_cmd.py`) 的參數預檢讀這份產物。"
                    + "新增／修改 Cmd 後請按上面的按鈕，或跑 `run_cmd.py run ExportCmdSchema`（兩者等價）。",
                    UCL_GUIStyle.LabelStyle);
                GUILayout.Label("未同步不會擋住任何功能 —— Python 端偵測到 hash 不符會自動降級成「不做參數預檢」，"
                    + "把判斷權交還給 Editor。同步只是讓 client 能提早回報參數錯誤。",
                    UCL_GUIStyle.LabelStyle);

                
                if (showDetail)
                {
                    string path = UCL_CmdSchemaExporter.SchemaPath;
                    GUILayout.Label($"產物路徑：{path}", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label($"存在：{(File.Exists(path) ? "是" : "否")}", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label($"產物內 hash：{Short(artifactHash)}", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label($"當前來源 hash：{Short(currentHash)}", UCL_GUIStyle.LabelStyle);
                    // 每日自動同步的節流狀態 —— per-machine（EditorPrefs），不入 git
                    var last = UCL_CmdSchemaExporter.LastAutoSyncUtc;
                    GUILayout.Label(last == DateTime.MinValue
                            ? "每日自動同步：本機尚未跑過（下次編譯完成時會檢查一次）"
                            : $"每日自動同步：上次檢查 {last.ToLocalTime():yyyy-MM-dd HH:mm}（每台機器每天最多一次）",
                        UCL_GUIStyle.LabelStyle);
                    using (new GUILayout.HorizontalScope())
                    {
                        if (File.Exists(path) && GUILayout.Button("開啟產物", GUILayout.ExpandWidth(false)))
                        {
                            EditorUtility.RevealInFinder(path);
                        }
                        GUILayout.FlexibleSpace();
                    }
                }

                if (!string.IsNullOrEmpty(m_LastActionMsg))
                {
                    GUILayout.Label(m_LastActionMsg, UCL_GUIStyle.LabelStyle);
                }
            }
        }

        // 區塊職責：按鈕動作 —— 委派給唯一實作，不自己組 JSON。
        // 數值影響：Written=false 代表已同步、什麼都沒動，這也是成功不是失敗。
        void RegenerateSchema()
        {
            try
            {
                var r = UCL_CmdSchemaExporter.Export();
                m_LastActionMsg = r.Written
                    ? $"<color=#5FD35F>✅ 已更新產物</color> — {r.CommandCount} 個 cmd（{r.SpecCount} 個有 ArgsSpec）"
                    : $"<color=#9FD3FF>ℹ 內容未變，未寫檔</color> — 本來就是同步狀態（{r.CommandCount} 個 cmd）";
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                m_LastActionMsg = $"<color=red>✗ 生成失敗：{e.Message}</color>";
                Debug.LogError($"[CmdAdmin] schema 生成失敗：{e}");
            }
        }

        // ===========================================================
        // 區塊職責：已註冊 Cmd 清單 —— 順便顯示「有沒有宣告 ArgsSpec」。
        // 物理意義：沒宣告不是錯誤，是「這個 cmd 不需要 client 幫忙擋參數」。
        //          列出來是為了讓人一眼看出「哪些 cmd 目前沒有 client 預檢保護」。
        // 數值影響：純唯讀顯示。
        // ===========================================================
        void DrawCommandListSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "CmdListFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🧾 已註冊 Cmd</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;

                var handlers = UCL_AgentCommandRegistry.ListHandlers();
                GUILayout.Label($"共 {handlers.Count} 個（reflection 自動發現 UCL_AgentCommandHandlerBase 子類）。",
                    UCL_GUIStyle.LabelStyle);

                foreach (var h in handlers)
                {
                    UCL_CmdArgsSpec spec = null;
                    try { spec = h.ArgsSpec; } catch { /* 取值失敗視為未宣告，匯出端已警告 */ }
                    int opCount = spec?.Ops?.Count ?? 0;
                    string specTag = spec == null
                        ? "<color=#999999>— 無 ArgsSpec（不做 client 預檢）</color>"
                        : (opCount > 0
                            ? $"<color=#5FD35F>✔ ArgsSpec（{opCount} 個 op）</color>"
                            : "<color=#5FD35F>✔ ArgsSpec</color>");
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"  `{h.CommandType}`", UCL_GUIStyle.LabelStyle, GUILayout.Width(220));
                        GUILayout.Label(specTag, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        // 區塊職責：hash 縮短顯示 —— 全長 64 字在 UI 上只是噪音，前 12 碼足以人眼比對。
        static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "(無)";
            return hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";
        }
    }
}
#endif
