// 區塊職責：Discord channel → ChatTavern room routing 編輯 UI
// 物理意義：discord_channel_routing.json 是 single source of truth, 由 discord_inbound_bot.py 啟動時讀;
//          本 page 提供 IMGUI CRUD: 列出 mappings + 改欄位 + 新增/刪除/啟停 + Save + Restart Bot.
// 設計理由 (Tim 2026-05-15 拍板):
//   notify_config.json 已肥到 137 行, 抽出 routing schema 獨立檔; 加 source_class freeform tag + priority desc
//   讓 waiter cycle 能 sort 出內部/工作優先. UI 走 UCL_LoginStatusPage 同款 table + per-row action 模式.
// 2026-05-18 (gura T19 BuildPlayerCheck fix): 整檔包 #if UNITY_EDITOR — page 用 UnityEditor.EditorUtility,
//   Player Build 沒 UnityEditor namespace → CS0234. 本 page 是 IMGUI editor-only, 沒 Player Build 用途.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Discord channel → ChatTavern room routing 編輯頁.
    /// 入口: UCL/Menu → Page picker → "Discord Channel Routing"; 也可從 ucl_core: HelpURL 連到 spec.
    /// </summary>
    [HelpURL("ucl_core:Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md")]
    public class UCL_DiscordChannelRoutingPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Discord Channel Routing";
        public override bool ShowInPageMenu => true;
        public static UCL_DiscordChannelRoutingPage Create() => UCL_EditorPage.Create<UCL_DiscordChannelRoutingPage>();

        // 區塊職責：routing row 結構 — 對齊 discord_channel_routing.json mappings[*] schema
        // 物理意義：channel_id (Discord 64-bit ID, 用 string 避免 long parse) → tavern_room + source_class + priority + enabled + label
        public class RoutingRow
        {
            public string ChannelId = "";       // Discord channel ID (string, 不轉 long 避免序列化爆位)
            public string TavernRoom = "tavern";
            public string Label = "";
            public string SourceClass = "external";   // freeform tag (Tim 2026-05-15 拍板)
            public int Priority = 0;                  // 越高越優先 (waiter cycle sort desc)
            public bool Enabled = true;
            public string GuildId = "";
            public string TagsCsv = "";               // tags array → CSV 編輯用 (UI 友善)
            public string Note = "";
        }

        List<RoutingRow> m_Rows = new List<RoutingRow>();
        Vector2 m_Scroll = Vector2.zero;
        bool m_Dirty = false;                         // unsaved changes flag
        string m_RoutingPath = "";
        string m_LastSaveTs = "";
        string m_BotStatus = "(unknown)";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析 — discord_channel_routing.json 在 AgentCommands/ChatTavern/
            m_RoutingPath = Path.Combine(UCL_RepoPath.AgentCommandsDir, "ChatTavern", "discord_channel_routing.json");
            LoadFile();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadFile();
            }
            using (new EditorDisabledScope(!m_Dirty))
            {
                if (GUILayout.Button(m_Dirty ? "Save *" : "Save", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    SaveFile();
                }
            }
            if (GUILayout.Button("Add Row", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_Rows.Add(new RoutingRow { ChannelId = "", TavernRoom = "tavern", SourceClass = "external", Priority = 0, Enabled = true });
                m_Dirty = true;
            }
            if (GUILayout.Button("Restart Bot", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                RestartBot();
            }
            if (GUILayout.Button("Open JSON", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (File.Exists(m_RoutingPath)) UnityEditor.EditorUtility.RevealInFinder(m_RoutingPath);
            }
        }

        // ===========================================================
        // Load / Save
        // ===========================================================
        void LoadFile()
        {
            m_Rows.Clear();
            m_Dirty = false;
            if (!File.Exists(m_RoutingPath))
            {
                Debug.LogWarning($"[DiscordChannelRouting] file not found, starting empty: {m_RoutingPath}");
                return;
            }
            try
            {
                string txt = File.ReadAllText(m_RoutingPath);
                var jd = JsonData.ParseJson(txt);
                if (!jd.IsObject || jd.Dic == null)
                {
                    Debug.LogWarning("[DiscordChannelRouting] root not object");
                    return;
                }
                if (!jd.Dic.TryGetValue("mappings", out var mapJd) || !mapJd.IsArray)
                {
                    Debug.LogWarning("[DiscordChannelRouting] mappings missing/not array");
                    return;
                }
                // 區塊職責：JsonData array 走 int indexer + Count, 沒有 public .List 屬性
                for (int ii = 0; ii < mapJd.Count; ii++)
                {
                    var item = mapJd[ii];
                    if (!item.IsObject || item.Dic == null) continue;
                    var row = new RoutingRow
                    {
                        ChannelId = item.GetString("channel_id", ""),
                        TavernRoom = item.GetString("tavern_room", "tavern"),
                        Label = item.GetString("label", ""),
                        SourceClass = item.GetString("source_class", "external"),
                        Priority = item.GetInt("priority", 0),
                        Enabled = item.GetBool("enabled", true),
                        GuildId = item.GetString("guild_id", ""),
                        Note = item.GetString("_note", ""),
                    };
                    // tags array → CSV
                    if (item.Dic.TryGetValue("tags", out var tagsJd) && tagsJd.IsArray)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int j = 0; j < tagsJd.Count; j++)
                        {
                            if (j > 0) sb.Append(",");
                            sb.Append(tagsJd[j].GetString());
                        }
                        row.TagsCsv = sb.ToString();
                    }
                    m_Rows.Add(row);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DiscordChannelRouting] LoadFile fail: {e.Message}");
            }
        }

        void SaveFile()
        {
            // 區塊職責：序列化 RoutingRow list → discord_channel_routing.json (純手構 JSON, 不靠 ParseJson 反向)
            // 物理意義：避免 JsonData 序列化破壞 _schema_version / _description 等 meta 欄位 (read 時沒拷貝);
            //          手構 JSON 對齊 schema, 之後外部 tool 用 json 函式庫讀仍正常.
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"_schema_version\": 1,\n");
                sb.Append("  \"_description\": \"Discord channel → ChatTavern room routing table. By discord_inbound_bot.py read; UCL_DiscordChannelRoutingPage edit. 多對一支援 (多 Discord channel 進同一 tavern room), 多對多 (不同 channel 配不同 room).\",\n");
                sb.Append("  \"_canonical_doc\": \"Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md\",\n");
                sb.Append("  \"_taxonomy_note\": \"source_class 是 freeform string (Tim 2026-05-15 拍板, 不限 enum). 慣例 tag: external / internal / work / chitchat / urgent; 自訂任意。priority int, 越高越優先 (waiter cycle sort desc).\",\n");
                sb.Append("  \"mappings\": [\n");
                for (int i = 0; i < m_Rows.Count; i++)
                {
                    var r = m_Rows[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"channel_id\": {JsonEscape(r.ChannelId)},\n");
                    sb.Append($"      \"tavern_room\": {JsonEscape(r.TavernRoom)},\n");
                    sb.Append($"      \"label\": {JsonEscape(r.Label)},\n");
                    sb.Append($"      \"source_class\": {JsonEscape(r.SourceClass)},\n");
                    sb.Append($"      \"priority\": {r.Priority},\n");
                    sb.Append($"      \"enabled\": {(r.Enabled ? "true" : "false")},\n");
                    sb.Append($"      \"guild_id\": {JsonEscape(r.GuildId)},\n");
                    sb.Append("      \"tags\": [");
                    if (!string.IsNullOrEmpty(r.TagsCsv))
                    {
                        var parts = r.TagsCsv.Split(',');
                        for (int j = 0; j < parts.Length; j++)
                        {
                            string t = parts[j].Trim();
                            if (string.IsNullOrEmpty(t)) continue;
                            if (j > 0) sb.Append(", ");
                            sb.Append(JsonEscape(t));
                        }
                    }
                    sb.Append("]");
                    if (!string.IsNullOrEmpty(r.Note))
                    {
                        sb.Append(",\n");
                        sb.Append($"      \"_note\": {JsonEscape(r.Note)}\n");
                    }
                    else
                    {
                        sb.Append("\n");
                    }
                    sb.Append("    }");
                    if (i < m_Rows.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("  ]\n");
                sb.Append("}\n");

                string parentDir = Path.GetDirectoryName(m_RoutingPath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
                File.WriteAllText(m_RoutingPath, sb.ToString());
                m_Dirty = false;
                m_LastSaveTs = DateTime.UtcNow.ToString("HH:mm:ss");
                Debug.Log($"[DiscordChannelRouting] saved {m_Rows.Count} rows → {m_RoutingPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DiscordChannelRouting] SaveFile fail: {e.Message}");
            }
        }

        // 區塊職責：JSON string escape — handle " \ \n \t 等控制符, 避免 invalid JSON
        // 物理意義：手構 JSON 必須自己 escape; 用 JsonData 反序列化會自動處理但寫入端要自己來
        static string JsonEscape(string s)
        {
            if (s == null) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        // ===========================================================
        // Restart Bot — kill 現有 python process, 讓 daemon 重 spawn 讀新 config
        // ===========================================================
        void RestartBot()
        {
            // 區塊職責：尋找跑 discord_inbound_bot.py 的 python process, kill 它
            // 物理意義：daemon 端 5s 內偵測子程序死掉自動 respawn, 新 process 啟動時讀 routing JSON
            // 數值影響：純 kill, 不動 daemon 跟 config; 若沒 process running 印警告但不算錯
            try
            {
                foreach (var proc in Process.GetProcessesByName("python"))
                {
                    try
                    {
                        // 跨平台 cmdline 抓不到 (Process.MainModule.FileName 只有 exe 路徑), 退而求其次:
                        // 用 ProcessStartInfo 跑 wmic / Get-CimInstance? 太重. 直接 kill 全部 python.exe 風險高
                        // → 改用 PID file pattern: 之後 daemon 寫 pid → 本 page 讀 → kill 精確 pid (TODO).
                        // MVP: 印警告, 提示走外部腳本
                    }
                    catch { /* ignore */ }
                    finally { proc.Dispose(); }
                }
                // MVP 策略: 提示 user 走外部 PowerShell 命令 (避免誤殺其他 python.exe)
                m_BotStatus = $"[{DateTime.UtcNow:HH:mm:ss}] Restart 請走外部命令:\n"
                              + "  PowerShell: Get-Process python | Where-Object { $_.MainWindowTitle -eq '' } | Stop-Process\n"
                              + "  或精確: Get-CimInstance Win32_Process -Filter \"name='python.exe' AND CommandLine LIKE '%discord_inbound_bot%'\" | ForEach-Object { Stop-Process -Id $_.ProcessId }\n"
                              + "  daemon 5s 內自動 respawn 新 process 讀新 routing.";
                Debug.Log("[DiscordChannelRouting] " + m_BotStatus);
            }
            catch (Exception e)
            {
                m_BotStatus = $"restart 失敗: {e.Message}";
                Debug.LogWarning("[DiscordChannelRouting] " + m_BotStatus);
            }
        }

        // ===========================================================
        // UI
        // ===========================================================
        protected override void ContentOnGUI()
        {
            DrawHeader();
            DrawTable();
            DrawFooter();
        }

        void DrawHeader()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label($"Routing rows: {m_Rows.Count}  ", UCL_GUIStyle.LabelStyle);
                GUILayout.Label($"File: {m_RoutingPath}", UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
                if (m_Dirty)
                {
                    var oldColor = GUI.color;
                    GUI.color = new Color(1f, 0.85f, 0.3f);
                    GUILayout.Label("● Unsaved changes", UCL_GUIStyle.LabelStyle);
                    GUI.color = oldColor;
                }
                else if (!string.IsNullOrEmpty(m_LastSaveTs))
                {
                    GUILayout.Label($"Saved @ {m_LastSaveTs}", UCL_GUIStyle.LabelStyle);
                }
            }
            if (!string.IsNullOrEmpty(m_BotStatus))
            {
                GUILayout.Label(m_BotStatus, UCL_GUIStyle.LabelStyle);
            }
        }

        void DrawTable()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // 表頭
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("On", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(34)));
                    GUILayout.Label("Channel ID", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label("Label", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label("Tavern Room", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.Label("Source Class", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.Label("Priority", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    GUILayout.Label("Tags (csv)", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label("Guild ID", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    GUILayout.Label("", GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                }

                m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
                for (int i = 0; i < m_Rows.Count; i++)
                {
                    var r = m_Rows[i];
                    using (new GUILayout.HorizontalScope())
                    {
                        // Enabled toggle
                        bool newEnabled = GUILayout.Toggle(r.Enabled, "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(34)));
                        if (newEnabled != r.Enabled) { r.Enabled = newEnabled; m_Dirty = true; }

                        // Channel ID
                        string newCid = GUILayout.TextField(r.ChannelId ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        if (newCid != r.ChannelId) { r.ChannelId = newCid; m_Dirty = true; }

                        // Label
                        string newLabel = GUILayout.TextField(r.Label ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        if (newLabel != r.Label) { r.Label = newLabel; m_Dirty = true; }

                        // Tavern Room
                        string newRoom = GUILayout.TextField(r.TavernRoom ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        if (newRoom != r.TavernRoom) { r.TavernRoom = newRoom; m_Dirty = true; }

                        // Source Class (freeform)
                        string newSc = GUILayout.TextField(r.SourceClass ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        if (newSc != r.SourceClass) { r.SourceClass = newSc; m_Dirty = true; }

                        // Priority (int)
                        string priStr = GUILayout.TextField(r.Priority.ToString(), GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                        if (int.TryParse(priStr, out int newPri))
                        {
                            if (newPri != r.Priority) { r.Priority = newPri; m_Dirty = true; }
                        }

                        // Tags CSV
                        string newTags = GUILayout.TextField(r.TagsCsv ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        if (newTags != r.TagsCsv) { r.TagsCsv = newTags; m_Dirty = true; }

                        // Guild ID
                        string newGid = GUILayout.TextField(r.GuildId ?? "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                        if (newGid != r.GuildId) { r.GuildId = newGid; m_Dirty = true; }

                        // Actions: Up / Down / Remove
                        if (GUILayout.Button("▲", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(30))))
                        {
                            if (i > 0)
                            {
                                (m_Rows[i - 1], m_Rows[i]) = (m_Rows[i], m_Rows[i - 1]);
                                m_Dirty = true;
                            }
                        }
                        if (GUILayout.Button("▼", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(30))))
                        {
                            if (i < m_Rows.Count - 1)
                            {
                                (m_Rows[i + 1], m_Rows[i]) = (m_Rows[i], m_Rows[i + 1]);
                                m_Dirty = true;
                            }
                        }
                        if (GUILayout.Button("✖ Remove", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            m_Rows.RemoveAt(i);
                            m_Dirty = true;
                            GUILayout.EndScrollView();
                            return;   // 重畫一輪避免 index 錯位
                        }
                    }
                    if (!string.IsNullOrEmpty(r.Note))
                    {
                        GUILayout.Label($"      _note: {r.Note}", UCL_GUIStyle.LabelStyle);
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        void DrawFooter()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("規範:", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  • 多個 Discord channel 可配同一 tavern_room (多對一); 也可分流到不同 room", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  • Source Class freeform string (外慣例: external / internal / work / chitchat / urgent; 自訂任意)", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  • Priority 越高越優先 (waiter cycle 按 priority desc 排序, internal/work 通常設高)", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  • 改完按 Save → 走 Restart Bot 提示 (外部 PowerShell 命令 kill python, daemon 5s 自動 respawn 讀新檔)", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // EditorDisabledScope helper
        // ===========================================================
        struct EditorDisabledScope : IDisposable
        {
            bool m_PrevEnabled;
            public EditorDisabledScope(bool disabled)
            {
                m_PrevEnabled = GUI.enabled;
                GUI.enabled = !disabled && m_PrevEnabled;
            }
            public void Dispose()
            {
                GUI.enabled = m_PrevEnabled;
            }
        }
    }
}
#endif // UNITY_EDITOR
