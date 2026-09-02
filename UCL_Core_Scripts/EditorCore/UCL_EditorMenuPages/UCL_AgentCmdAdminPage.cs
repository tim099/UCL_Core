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

        // 已註冊 Cmd 清單的顯示列快取（(名稱, spec 標籤)）—— 見 DrawCommandListSection 的成本說明。
        // null = 尚未算過。domain reload 會重建本頁物件，快取自然歸零，不需額外失效機制。
        List<(string, string)> m_CmdRows;

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
                // 區塊職責：預檢總開關（Tim 2026-07-30 追加）。
                // 物理意義：停用 = 本機不再更新產物，且 Python 端跳過參數預檢（等同產物不存在）。
                //          狀態存在旗標檔（非 EditorPrefs），因為 Python 也要讀得到；
                //          EditorPrefs 只有 C# 看得見，再鏡射一份給 Python 就又是雙端鏡像。
                // 數值影響：停用時本區塊其餘查詢一律跳過 —— 連 IsInSync 的雜湊成本都不付。
                bool disabled = UCL_CmdSchemaExporter.PreflightDisabled;
                bool wantEnabled = !disabled;
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("<b>🔄 Cmd Schema 同步 </b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                    GUILayout.Label("啟用 schema 預檢", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false)); 
                    bool newEnabled = UCL_GUILayout.CheckBox(wantEnabled);
                    if (newEnabled != wantEnabled)
                    {
                        UCL_CmdSchemaExporter.PreflightDisabled = !newEnabled;
                        Debug.Log($"[CmdSchema] schema 預檢已{(newEnabled ? "啟用" : "停用")}"
                                + $"（旗標檔：{UCL_CmdSchemaExporter.DisableFlagPath}）");
                        disabled = !newEnabled;
                    }
                    GUILayout.FlexibleSpace();
                }

                if (disabled)
                {
                    GUILayout.Label("<color=#FFB347>⏸ 預檢已停用（本機）</color> —— "
                        + "C# 端**停止更新產物**（自動同步與手動按鈕都不寫檔）；"
                        + "Python 端跳過參數預檢，行為與「產物不存在」逐字相同。",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("停用不影響任何 Cmd 的實際執行 —— 參數對錯一律由 Editor 判定，"
                        + "只是少了 client 端提早回報的便利。產物檔會凍結在停用當下的版本。",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label($"旗標檔：{UCL_CmdSchemaExporter.DisableFlagPath}（per-machine，不入 git）",
                        UCL_GUIStyle.LabelStyle);
                    return;     // 停用時不做同步狀態查詢，也不顯示重新生成按鈕
                }

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
                    GUILayout.Label(inSync
                            ? "<color=#5FD35F>✅ 已同步</color>"
                            : "<color=#FFB347>⚠ 未同步（產物落後於 Cmd 原始碼）</color>",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    
                    if (GUILayout.Button("重新生成 commands_schema.json",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.9f, 0.7f)), GUILayout.ExpandWidth(false)))
                    {
                        RegenerateSchema();
                    }
                    GUILayout.FlexibleSpace();
                }



                
                if (showDetail)
                {
                    GUILayout.Label("Python client 端 (`tavern_cmd.py`) 的參數預檢讀這份產物。"
                        + "新增／修改 Cmd 後請按上面的按鈕，或跑 `senate ucmd run ExportCmdSchema`（兩者等價）。",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("未同步不會擋住任何功能 —— Python 端偵測到 hash 不符會自動降級成「不做參數預檢」，"
                        + "把判斷權交還給 Editor。同步只是讓 client 能提早回報參數錯誤。",
                        UCL_GUIStyle.LabelStyle);

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
                        if (File.Exists(path) && GUILayout.Button("開啟產物", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
                // 「因停用而跳過」必須跟「內容未變」分開報 —— 兩者都是 Written=false，
                // 但前者是「什麼都沒檢查」、後者是「檢查過且一致」。混報就是同碼失聲。
                m_LastActionMsg = r.SkippedDisabled
                    ? "<color=#FFB347>⏸ 預檢已停用，未生成</color> — 先勾回「啟用 schema 預檢」再試"
                    : r.Written
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

                // 區塊職責：清單內容快取 —— 每 frame 重算會卡（Tim 2026-07-30 回報面板卡頓）。
                // 物理意義：`h.ArgsSpec` 是**計算屬性**，每次取值都重建整個 spec 物件；
                //          Cmd_Tavern 那個含 34 個 op 與其 required/alias 字典。
                //          51 個 handler × 每秒數個 frame，等於每秒重建上百個字典 —— 純浪費，
                //          因為這份清單只會在「重新編譯（handler 集合可能變）」時改變。
                // 數值影響：只在快取為 null 時算一次；domain reload（改 code 後）本頁物件會重建，
                //          快取自然歸零，不需要額外的失效機制。
                if (m_CmdRows == null)
                {
                    var handlers = UCL_AgentCommandRegistry.ListHandlers();
                    m_CmdRows = new List<(string, string)>(handlers.Count);
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
                        m_CmdRows.Add(($"  `{h.CommandType}`", specTag));
                    }
                }

                GUILayout.Label($"共 {m_CmdRows.Count} 個（reflection 自動發現 UCL_AgentCommandHandlerBase 子類）。",
                    UCL_GUIStyle.LabelStyle);

                foreach (var row in m_CmdRows)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(row.Item1, UCL_GUIStyle.LabelStyle, GUILayout.Width(220));
                        GUILayout.Label(row.Item2, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
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
