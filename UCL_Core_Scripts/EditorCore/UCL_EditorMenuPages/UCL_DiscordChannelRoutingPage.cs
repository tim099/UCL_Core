// 區塊職責：Discord channel → ChatTavern room routing 編輯 UI
// 物理意義：discord_channel_routing.json 是 single source of truth，由 C# native
//          UCL_DiscordInboundDaemon 以 mtime 快取讀取；本頁用「選取→編輯」流程管理 mappings，
//          並可驗證 Channel ID、快取 Discord 頻道名稱、新增／複製／刪除與儲存。
// 設計理由 (Tim 2026-05-15 拍板):
//   notify_config.json 已肥到 137 行, 抽出 routing schema 獨立檔; 加 source_class freeform tag + priority desc
//   讓 waiter cycle 能 sort 出內部/工作優先. UI 走 UCL_LoginStatusPage 同款 table + per-row action 模式.
// 2026-05-18 (gura T19 BuildPlayerCheck fix): 整檔包 #if UNITY_EDITOR — page 用 UnityEditor.EditorUtility,
//   Player Build 沒 UnityEditor namespace → CS0234. 本 page 是 IMGUI editor-only, 沒 Player Build 用途.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.Networking;
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
            public string CachedChannelName = "";
        }

        List<RoutingRow> m_Rows = new List<RoutingRow>();
        bool m_Dirty = false;                         // unsaved changes flag
        string m_RoutingPath = "";
        string m_LastSaveTs = "";
        int m_SelectedRowIndex = -1;
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        UnityWebRequest m_ChannelProbeRequest;
        int m_ChannelProbeRowIndex = -1;
        string m_ChannelProbeStatus = "";
        const string ChannelNameCacheKeyPrefix = "UCL_DiscordChannelRoutingPage.ChannelName.";

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
            if (GUILayout.Button("＋ 新增路由", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_Rows.Add(new RoutingRow { ChannelId = "", TavernRoom = "tavern", SourceClass = "external", Priority = 0, Enabled = true });
                m_SelectedRowIndex = m_Rows.Count - 1;
                m_Dirty = true;
            }
            if (GUILayout.Button("💬 Discord 設定", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UCL_DiscordSettingsPage.Create();
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
            m_SelectedRowIndex = -1;
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
                        CachedChannelName = GetCachedChannelName(item.GetString("channel_id", "")),
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
                if (m_Rows.Count > 0) m_SelectedRowIndex = 0;
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
                string validation = GetValidationMessage();
                if (!string.IsNullOrEmpty(validation))
                {
                    Debug.LogWarning($"[DiscordChannelRouting] 儲存取消：{validation}");
                    return;
                }
                var sb = new System.Text.StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"_schema_version\": 1,\n");
                sb.Append("  \"_description\": \"Discord channel → ChatTavern room routing table. Read by UCL_DiscordInboundDaemon; UCL_DiscordChannelRoutingPage edits it. 多對一支援 (多 Discord channel 進同一 tavern room), 多對多 (不同 channel 配不同 room).\",\n");
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
                        int tagCount = 0;
                        for (int j = 0; j < parts.Length; j++)
                        {
                            string t = parts[j].Trim();
                            if (string.IsNullOrEmpty(t)) continue;
                            if (tagCount++ > 0) sb.Append(", ");
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
        // UI
        // ===========================================================
        protected override void ContentOnGUI()
        {
            PollChannelProbe();
            DrawHeader();
            DrawRoutePicker();
            DrawSelectedRouteEditor();
            DrawFieldGuide();
        }

        void DrawHeader()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                int activeCount = 0;
                for (int i = 0; i < m_Rows.Count; i++) if (m_Rows[i].Enabled) activeCount++;
                GUILayout.Label($"<b>🔀 Discord 頻道 → 酒館房間</b>　{activeCount}/{m_Rows.Count} 啟用", UCL_GUIStyle.LabelStyle);
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
            GUILayout.Label("每一列只決定一件事：某個 Discord 頻道的訊息要不要進酒館，以及要進哪一個房間。儲存後 native inbound daemon 會自動讀取新設定。", UCL_GUIStyle.LabelStyle);
            string validation = GetValidationMessage();
            if (!string.IsNullOrEmpty(validation))
            {
                GUILayout.Label($"⚠ {validation}", UCL_GUIStyle.LabelStyle);
            }
        }

        void DrawRoutePicker()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>① 選擇要管理的頻道路由</b>", UCL_GUIStyle.LabelStyle);
                if (m_Rows.Count == 0)
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("尚未有路由。按上方「＋ 新增路由」建立第一筆。", UCL_GUIStyle.LabelStyle);
                    return;
                }
                if (m_SelectedRowIndex < 0 || m_SelectedRowIndex >= m_Rows.Count) m_SelectedRowIndex = 0;
                var labels = new List<string>();
                for (int i = 0; i < m_Rows.Count; i++)
                {
                    var r = m_Rows[i];
                    string identity = !string.IsNullOrEmpty(r.CachedChannelName) ? "#" + r.CachedChannelName : (string.IsNullOrEmpty(r.Label) ? (string.IsNullOrEmpty(r.ChannelId) ? "未命名頻道" : r.ChannelId) : r.Label);
                    labels.Add($"{(r.Enabled ? "●" : "○")} {identity}  →  {r.TavernRoom}  [{r.SourceClass}/p{r.Priority}]");
                }
                int next = UCL_GUILayout.PopupSearchCache(m_SelectedRowIndex, labels, m_PickerDic, "DiscordChannelRoutePicker");
                if (next >= 0 && next < m_Rows.Count) m_SelectedRowIndex = next;
            }
        }

        void DrawSelectedRouteEditor()
        {
            if (m_SelectedRowIndex < 0 || m_SelectedRowIndex >= m_Rows.Count) return;
            var r = m_Rows[m_SelectedRowIndex];
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>② 編輯選取路由</b>", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("核心路由欄位（決定 Discord 訊息如何進入酒館）", UCL_GUIStyle.LabelStyle);
                bool enabled = UCL_GUILayout.CheckBox(r.Enabled, "啟用這個頻道的 inbound 中繼");
                if (enabled != r.Enabled) { r.Enabled = enabled; m_Dirty = true; }
                DrawChannelIdField(r);
                DrawChannelValidation(r);
                DrawTextField("酒館 Room ID", "收到的訊息寫入哪個 Chat Tavern room，例如 tavern。", ref r.TavernRoom);
                DrawTextField("來源分類", "寫入訊息 meta.source_class 的自由標籤，例如 external、internal 或 work。", ref r.SourceClass);
                string priorityText = r.Priority.ToString();
                string nextPriority = GUILayout.TextField(priorityText, UCL_GUIStyle.TextFieldStyle);
                GUILayout.Label("優先度：會寫入訊息 meta.priority，供下游等待／提示流程判讀；數字越高代表越高優先。", UCL_GUIStyle.LabelStyle);
                if (int.TryParse(nextPriority, out int priority) && priority != r.Priority) { r.Priority = priority; m_Dirty = true; }
                using(new GUILayout.HorizontalScope())
                {
                    bool showMetadata = UCL_GUILayout.Toggle(m_FoldDic, "DiscordChannelRouteMetadata", 21, iDefaultValue: false);
                    using(new GUILayout.VerticalScope())
                    {
                        GUILayout.Label("<b>辨識與備註（不影響中繼目標）</b>", UCL_GUIStyle.LabelStyle);
                        if (showMetadata)
                        {
                            DrawTextField("顯示名稱", "只供管理頁與診斷辨識這個頻道。", ref r.Label);
                            DrawTextField("Guild ID", "頻道所屬 Discord Guild；供診斷與 Discord 設定頁預填。", ref r.GuildId);
                            DrawTextField("Tags（逗號分隔）", "組織用標籤；目前不改變中繼行為。", ref r.TagsCsv);
                            DrawTextArea("備註", "給管理者的背景說明；不會送進酒館。", ref r.Note);
                        }
                    }
                }


                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("複製為新路由", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Rows.Add(new RoutingRow { ChannelId = r.ChannelId, TavernRoom = r.TavernRoom, Label = r.Label + "（副本）", SourceClass = r.SourceClass, Priority = r.Priority, Enabled = false, GuildId = r.GuildId, TagsCsv = r.TagsCsv, Note = r.Note });
                        m_SelectedRowIndex = m_Rows.Count - 1;
                        m_Dirty = true;
                        return;
                    }
                    if (GUILayout.Button("✖ 刪除選取路由", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Rows.RemoveAt(m_SelectedRowIndex);
                        m_SelectedRowIndex = m_Rows.Count == 0 ? -1 : Math.Min(m_SelectedRowIndex, m_Rows.Count - 1);
                        m_Dirty = true;
                    }
                }
            }
        }

        void DrawFieldGuide()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>③ 儲存與生效</b>", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("按上方 Save 後，UCL_DiscordInboundDaemon 會偵測檔案變更並自動套用，\n" +
                    "不需要重啟 bot 或手動結束 Python process。驗證過的頻道名稱會快取在本專案 EditorPrefs，下次開頁直接顯示。", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("同一個酒館 room 可以接多個 Discord 頻道；\n" +
                    "但啟用中的同一 Channel ID 不應重複，否則只會採用第一筆路由。", UCL_GUIStyle.LabelStyle);
            }
        }

        void DrawTextField(string label, string help, ref string value)
        {
            GUILayout.Label(label, UCL_GUIStyle.LabelStyle);
            string next = GUILayout.TextField(value ?? "", UCL_GUIStyle.TextFieldStyle);
            if (next != value) { value = next; m_Dirty = true; }
            GUILayout.Label(help, UCL_GUIStyle.LabelStyle);
        }

        void DrawChannelIdField(RoutingRow row)
        {
            GUILayout.Label("Discord Channel ID", UCL_GUIStyle.LabelStyle);
            string next = GUILayout.TextField(row.ChannelId ?? "", UCL_GUIStyle.TextFieldStyle);
            if (next != row.ChannelId)
            {
                row.ChannelId = next;
                row.CachedChannelName = GetCachedChannelName(next);
                m_ChannelProbeStatus = "";
                m_Dirty = true;
            }
            GUILayout.Label("要讀取的 Discord 文字頻道 ID（必填）。已驗證過的 ID 會直接帶入本機快取名稱。", UCL_GUIStyle.LabelStyle);
        }

        void DrawTextArea(string label, string help, ref string value)
        {
            GUILayout.Label(label, UCL_GUIStyle.LabelStyle);
            string next = GUILayout.TextArea(value ?? "", UCL_GUIStyle.TextAreaStyle, GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(54)));
            if (next != value) { value = next; m_Dirty = true; }
            GUILayout.Label(help, UCL_GUIStyle.LabelStyle);
        }

        void DrawChannelValidation(RoutingRow row)
        {
            using (new GUILayout.HorizontalScope())
            {
                bool isProbing = m_ChannelProbeRequest != null && m_ChannelProbeRowIndex == m_SelectedRowIndex;
                using (new EditorDisabledScope(isProbing || string.IsNullOrWhiteSpace(row.ChannelId)))
                {
                    if (GUILayout.Button(isProbing ? "驗證中…" : "🩺 驗證 Channel ID", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) StartChannelProbe(m_SelectedRowIndex, row.ChannelId);
                }
                string name = row.CachedChannelName;
                GUILayout.Label(string.IsNullOrEmpty(name) ? "頻道名稱：未驗證" : $"<color=#66ff66>✓ #{name}（快取）</color>", UCL_GUIStyle.LabelStyle);
            }
            if (!string.IsNullOrEmpty(m_ChannelProbeStatus)) GUILayout.Label(m_ChannelProbeStatus, UCL_GUIStyle.LabelStyle);
        }

        static string GetCachedChannelName(string channelId)
        {
            return string.IsNullOrEmpty(channelId) ? "" : UCL_ProjectEditorPrefs.GetString(ChannelNameCacheKeyPrefix + channelId, "");
        }

        static void SetCachedChannelName(string channelId, string channelName)
        {
            if (!string.IsNullOrEmpty(channelId) && !string.IsNullOrEmpty(channelName)) UCL_ProjectEditorPrefs.SetString(ChannelNameCacheKeyPrefix + channelId, channelName);
        }

        void StartChannelProbe(int rowIndex, string channelId)
        {
            if (!UCL_DiscordInboundDaemon.TryCreateChannelRequest(channelId?.Trim(), out var request, out string error))
            {
                m_ChannelProbeStatus = $"<color=#ff6666>✗ {error}</color>";
                return;
            }
            m_ChannelProbeRequest?.Dispose();
            m_ChannelProbeRequest = request;
            m_ChannelProbeRowIndex = rowIndex;
            m_ChannelProbeStatus = "驗證中…";
            m_ChannelProbeRequest.SendWebRequest();
        }

        void PollChannelProbe()
        {
            if (m_ChannelProbeRequest == null || !m_ChannelProbeRequest.isDone) return;
            try
            {
                if (m_ChannelProbeRequest.result != UnityWebRequest.Result.Success)
                {
                    m_ChannelProbeStatus = $"<color=#ff6666>✗ HTTP {(long)m_ChannelProbeRequest.responseCode}：{m_ChannelProbeRequest.error}</color>";
                    return;
                }
                var result = JsonData.ParseJson(m_ChannelProbeRequest.downloadHandler.text);
                string channelName = result?.GetString("name", "") ?? "";
                if (string.IsNullOrEmpty(channelName))
                {
                    m_ChannelProbeStatus = "<color=#ff6666>✗ Discord 回應未包含頻道名稱</color>";
                    return;
                }
                if (m_ChannelProbeRowIndex >= 0 && m_ChannelProbeRowIndex < m_Rows.Count)
                {
                    var row = m_Rows[m_ChannelProbeRowIndex];
                    row.CachedChannelName = channelName;
                    SetCachedChannelName(row.ChannelId, channelName);
                }
                m_ChannelProbeStatus = $"<color=#66ff66>✓ #{channelName}（已快取）</color>";
            }
            catch (Exception e)
            {
                m_ChannelProbeStatus = $"<color=#ff6666>✗ 驗證回應解析失敗：{e.Message}</color>";
            }
            finally
            {
                m_ChannelProbeRequest.Dispose();
                m_ChannelProbeRequest = null;
                m_ChannelProbeRowIndex = -1;
            }
        }

        string GetValidationMessage()
        {
            var enabledChannelIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < m_Rows.Count; i++)
            {
                var r = m_Rows[i];
                if (!r.Enabled) continue;
                if (string.IsNullOrWhiteSpace(r.ChannelId) || string.IsNullOrWhiteSpace(r.TavernRoom)) return "啟用中的路由必須同時填入 Discord Channel ID 與酒館 Room ID。";
                if (!enabledChannelIds.Add(r.ChannelId)) return $"Discord Channel ID {r.ChannelId} 有多筆啟用路由；daemon 只會採用第一筆。";
            }
            return "";
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
