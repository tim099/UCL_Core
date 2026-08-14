// 區塊職責：Discord 設定集中管理頁 — inbound 白名單、名稱／別名／個人簡介，以及 Guild 成員候選匯入。
// 物理意義：channel routing 是「從哪裡收」，此頁管理「收到誰」與「怎麼稱呼他」，讓 Discord 身分資料有單一操作入口。
// 數值影響：所有寫入只修改 notify_config.json.tavern_inbound.user_whitelist；候選清單是 runtime-only 快取，不會自動加入白名單。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.JsonLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_DiscordSettingsPage.md")]
    public class UCL_DiscordSettingsPage : UCL_CommonEditorPage
    {
        const string KeyTavernInbound = "tavern_inbound";
        const string KeyUserWhitelist = "user_whitelist";
        const string KeyEnabled = "enabled";
        const string KeyUsers = "users";
        const string KeyUserId = "user_id";
        const string KeyDisplayName = "display_name";
        const string KeyProfile = "profile";
        const string KeyAliases = "aliases";
        const string KeyMappings = "mappings";
        const string KeyGuildId = "guild_id";
        const string KeyTavernMirror = "tavern_mirror";
        const string KeyDiscordUserMentions = "discord_user_mentions";

        class WhitelistUser
        {
            public string userId = "";
            public string displayName = "";
            public string profile = "";
            public List<string> aliases = new List<string>();
        }

        class GuildMemberCandidate
        {
            public string userId = "";
            public string displayName = "";
            public bool isWhitelisted;
        }

        class MentionMapping
        {
            public string name = "";
            public string userId = "";
        }

        class DiscordPersonRow
        {
            public string userId = "";
            public readonly List<string> aliases = new List<string>();
            public WhitelistUser whitelistUser;
        }

        static string NotifyConfigPath => Path.Combine(UCL_AgentCommandsPath.DataRoot, "PromptQueue", "notify_config.json");
        static string RoutingPath => Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "discord_channel_routing.json");

        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();
        readonly List<WhitelistUser> m_Users = new List<WhitelistUser>();
        readonly List<MentionMapping> m_MentionMappings = new List<MentionMapping>();
        readonly List<GuildMemberCandidate> m_Candidates = new List<GuildMemberCandidate>();
        bool m_Loaded;
        bool m_WhitelistEnabled;
        string m_NewUserId = "";
        string m_NewUserName = "";
        string m_NewMentionName = "";
        string m_NewMentionUserId = "";
        readonly Dictionary<string, string> m_AliasDraftByUserId = new Dictionary<string, string>();
        string m_SelectedPersonId = "";
        string m_GuildIdDraft = "";
        string m_Status = "";
        UnityWebRequest m_MemberRequest;
        string m_NextAfter = "";
        int m_ImportedMemberCount;

        public override string WindowName => "Discord 設定";
        public override bool ShowInPageMenu => true;
        public static UCL_DiscordSettingsPage Create() => UCL_EditorPage.Create<UCL_DiscordSettingsPage>();

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) LoadData();
            if (GUILayout.Button("🍺 開啟酒館後台", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UCL_ChatTavernAdminPage.Create();
            if (GUILayout.Button("🔀 開啟頻道路由", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UCL_DiscordChannelRoutingPage.Create();
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();
            PollMemberImport();
            GUILayout.Label("<b>💬 Discord 設定</b>", new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, fontSize = 18 });
            DrawPeopleSection();
            GUILayout.Space(8);
            DrawGuildImportSection();
            GUILayout.Space(8);
            DrawNavigationSection();
            if (!string.IsNullOrEmpty(m_Status)) GUILayout.Label(m_Status, UCL_GUIStyle.LabelStyle);
        }

        // 區塊職責：讀取白名單與預填 Guild ID。
        // 物理意義：白名單唯一真相仍是 notify_config；候選 Guild 優先取既有 inbound routing，減少手抄 snowflake。
        // 數值影響：唯讀快取重建，不會寫設定或送網路請求。
        void LoadData()
        {
            m_Loaded = true;
            m_Users.Clear();
            m_MentionMappings.Clear();
            m_PickerDic.Clear();
            m_WhitelistEnabled = false;
            try
            {
                if (File.Exists(NotifyConfigPath))
                {
                    var config = JsonData.ParseJson(File.ReadAllText(NotifyConfigPath));
                    LoadMentionMappings(config);
                    var inbound = config != null && config.Contains(KeyTavernInbound) ? config[KeyTavernInbound] : null;
                    var whitelist = inbound != null && inbound.Contains(KeyUserWhitelist) ? inbound[KeyUserWhitelist] : null;
                    if (whitelist != null)
                    {
                        m_WhitelistEnabled = whitelist.GetBool(KeyEnabled, false);
                        var users = whitelist.Contains(KeyUsers) ? whitelist[KeyUsers] : null;
                        if (users != null && users.IsArray)
                            for (int i = 0; i < users.Count; i++) AddUserFromJson(users[i]);
                    }
                }
                if (string.IsNullOrEmpty(m_GuildIdDraft)) m_GuildIdDraft = DiscoverGuildId();
                RefreshCandidateWhitelistFlags();
            }
            catch (Exception e) { m_Status = $"讀取設定失敗：{e.Message}"; }
        }

        // 區塊職責：讀取既有 outbound @mention 對照。
        // 物理意義：tavern_mirror.discord_user_mentions 是已上線的 @David → Discord ID 權威來源，不能被新白名單覆蓋或遺忘。
        // 數值影響：唯讀投影；表內名稱即使尚未允許 inbound，仍可作 outbound 真實 ping。
        void LoadMentionMappings(JsonData config)
        {
            var mirror = config != null && config.Contains(KeyTavernMirror) ? config[KeyTavernMirror] : null;
            var mentions = mirror != null && mirror.Contains(KeyDiscordUserMentions) ? mirror[KeyDiscordUserMentions] : null;
            if (mentions == null || !mentions.IsObject || mentions.Dic == null) return;
            foreach (var entry in mentions.Dic)
            {
                string id = entry.Value?.GetString() ?? "";
                if (!string.IsNullOrEmpty(entry.Key) && !string.IsNullOrEmpty(id))
                    m_MentionMappings.Add(new MentionMapping { name = entry.Key, userId = id });
            }
            m_MentionMappings.Sort((left, right) => string.Compare(left.name, right.name, StringComparison.OrdinalIgnoreCase));
        }

        void AddUserFromJson(JsonData node)
        {
            if (node == null) return;
            string id = node.GetString(KeyUserId, "");
            if (string.IsNullOrEmpty(id)) return;
            var user = new WhitelistUser { userId = id, displayName = node.GetString(KeyDisplayName, ""), profile = node.GetString(KeyProfile, "") };
            var aliases = node.Contains(KeyAliases) ? node[KeyAliases] : null;
            if (aliases != null && aliases.IsArray)
                for (int i = 0; i < aliases.Count; i++)
                {
                    string alias = aliases[i].GetString().Trim();
                    if (!string.IsNullOrEmpty(alias) && !user.aliases.Contains(alias)) user.aliases.Add(alias);
                }
            m_Users.Add(user);
        }

        static string DiscoverGuildId()
        {
            try
            {
                if (!File.Exists(RoutingPath)) return "";
                var config = JsonData.ParseJson(File.ReadAllText(RoutingPath));
                var mappings = config != null && config.Contains(KeyMappings) ? config[KeyMappings] : null;
                if (mappings == null || !mappings.IsArray) return "";
                for (int i = 0; i < mappings.Count; i++)
                {
                    string id = mappings[i].GetString(KeyGuildId, "");
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            return "";
        }

        // 區塊職責：以 Discord user ID 為主鍵繪製統一人員表。
        // 物理意義：舊 outbound @ 對照與新 inbound 白名單是同一批真人的不同權限面，拆成兩張表會造成 David/Dump 重複、難以判讀。
        // 數值影響：名稱／別名寫回 tavern_mirror.discord_user_mentions；白名單、個人簡介寫回 tavern_inbound.user_whitelist，各自保留既有 consumer。
        void DrawPeopleSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "DiscordSettingsPeopleFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>👥 Discord 人員與 @ 對照</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    bool nextEnabled = GUILayout.Toggle(m_WhitelistEnabled, "啟用", GUILayout.ExpandWidth(false));
                    if (nextEnabled != m_WhitelistEnabled) { WriteWhitelist(w => w[KeyEnabled] = new JsonData(nextEnabled)); return; }
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label(m_WhitelistEnabled ? "白名單啟用中：非名單真人訊息不會進酒館。名稱與別名則一律可用於 outbound @ ping。" : "白名單未啟用：可先整理名稱、別名與個人資料，再啟用 inbound 門禁。", UCL_GUIStyle.LabelStyle);
                var people = BuildPeopleRows();
                if (people.Count == 0)
                {
                    GUILayout.Label("尚無既有 @ 對照或白名單成員。可下方新增白名單人員，或從 Guild 候選加入。", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    int selectedIdx = Math.Max(0, people.FindIndex(row => row.userId == m_SelectedPersonId));
                    var labels = people.Select(row =>
                    {
                        string whitelistState = row.whitelistUser == null ? "○ 未白名單" : "● 白名單";
                        string names = row.aliases.Count > 0 ? string.Join(", ", row.aliases) : "（未命名）";
                        return $"{whitelistState}  {names}  [{row.userId}]";
                    })
                        .ToList();
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("選取人員", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        int nextIdx = UCL_GUILayout.PopupSearchCache(selectedIdx, labels, m_PickerDic, "DiscordPersonPicker");
                        if (nextIdx >= 0 && nextIdx < people.Count && nextIdx != selectedIdx)
                        {
                            m_SelectedPersonId = people[nextIdx].userId;
                            GUI.FocusControl(null);
                        }
                    }
                    if (string.IsNullOrEmpty(m_SelectedPersonId)) m_SelectedPersonId = people[selectedIdx].userId;
                    var selected = people.FirstOrDefault(row => row.userId == m_SelectedPersonId) ?? people[0];
                    m_SelectedPersonId = selected.userId;
                    DrawPersonRow(selected);
                }
                using (new GUILayout.HorizontalScope())
                {
                    m_NewUserId = GUILayout.TextField(m_NewUserId, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    m_NewUserName = GUILayout.TextField(m_NewUserName, UCL_GUIStyle.TextFieldStyle);
                    if (GUILayout.Button("新增白名單人員", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) AddWhitelistUser(m_NewUserId, m_NewUserName);
                }
            }
        }

        List<DiscordPersonRow> BuildPeopleRows()
        {
            var rows = new Dictionary<string, DiscordPersonRow>(StringComparer.Ordinal);
            DiscordPersonRow GetRow(string id)
            {
                if (!rows.TryGetValue(id, out var row)) { row = new DiscordPersonRow { userId = id }; rows[id] = row; }
                return row;
            }
            foreach (var mapping in m_MentionMappings) AddAlias(GetRow(mapping.userId).aliases, mapping.name);
            foreach (var user in m_Users)
            {
                var row = GetRow(user.userId);
                row.whitelistUser = user;
                AddAlias(row.aliases, user.displayName);
                foreach (var alias in user.aliases) AddAlias(row.aliases, alias);
            }
            return rows.Values.OrderBy(row => row.aliases.Count > 0 ? row.aliases[0] : row.userId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void AddAlias(List<string> aliases, string value)
        {
            string alias = (value ?? "").Trim();
            if (!string.IsNullOrEmpty(alias) && !aliases.Contains(alias)) aliases.Add(alias);
        }

        void DrawPersonRow(DiscordPersonRow row)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "DiscordSettingsPerson_" + row.userId, 21, iDefaultValue: false);
                    GUILayout.Label(row.userId, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(175)));
                    GUILayout.Label(row.aliases.Count > 0 ? string.Join(", ", row.aliases) : "（尚無 @ 名稱）", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label(row.whitelistUser == null ? "○ 未白名單" : "● 白名單", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (row.whitelistUser == null && GUILayout.Button("加入白名單", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) { AddWhitelistUser(row.userId, row.aliases.FirstOrDefault() ?? ""); return; }
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                string aliasDraft = m_AliasDraftByUserId.TryGetValue(row.userId, out var saved) ? saved : "";
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("新增 @ 別名", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    aliasDraft = GUILayout.TextField(aliasDraft, UCL_GUIStyle.TextFieldStyle);
                    m_AliasDraftByUserId[row.userId] = aliasDraft;
                    if (GUILayout.Button("加入對照", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        string alias = aliasDraft.Trim();
                        if (string.IsNullOrEmpty(alias)) { m_Status = "@ 別名不可空白。"; return; }
                        WriteMentionMappings(map => map[alias] = new JsonData(row.userId));
                        m_AliasDraftByUserId.Remove(row.userId);
                        return;
                    }
                }
                if (row.whitelistUser == null) return;
                var user = row.whitelistUser;
                GUILayout.Label("Inbound 顯示名稱／個人簡介", UCL_GUIStyle.LabelStyle);
                user.displayName = GUILayout.TextField(user.displayName ?? "", UCL_GUIStyle.TextFieldStyle);
                user.profile = GUILayout.TextArea(user.profile ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(55)));
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("套用 inbound 個人資料", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) { UpdateUser(user); return; }
                    if (GUILayout.Button("✕ 移出白名單", UCL_GUIStyle.GetButtonStyle(new Color(0.9f, 0.5f, 0.4f)), GUILayout.ExpandWidth(false))) { RemoveUser(user.userId); return; }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        void DrawOutboundMentionsSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "DiscordSettingsMentionsFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>📣 Outbound @ 提及對照</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label($"{m_MentionMappings.Count} 筆", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label("來源：tavern_mirror.discord_user_mentions。這是目前 @David 等名稱轉成 Discord 真實 ping 的既有權威表；不需要先加入 inbound 白名單。", UCL_GUIStyle.LabelStyle);
                foreach (var mapping in m_MentionMappings)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("@" + mapping.name, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                        GUILayout.Label(mapping.userId, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(175)));
                        using (new GUIEnabledScope(!m_Users.Any(user => user.userId == mapping.userId)))
                            if (GUILayout.Button("加入白名單", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) AddWhitelistUser(mapping.userId, mapping.name);
                        if (GUILayout.Button("✕ 移除對照", UCL_GUIStyle.GetButtonStyle(new Color(0.9f, 0.5f, 0.4f)), GUILayout.ExpandWidth(false))) { RemoveMentionMapping(mapping.name); return; }
                    }
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("新增", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_NewMentionName = GUILayout.TextField(m_NewMentionName ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    m_NewMentionUserId = GUILayout.TextField(m_NewMentionUserId ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(175)));
                    if (GUILayout.Button("新增 @ 對照", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) AddMentionMapping();
                }
            }
        }

        void DrawWhitelistUser(WhitelistUser user)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                string foldKey = "DiscordSettingsUser_" + user.userId;
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, foldKey, 21, iDefaultValue: false);
                    GUILayout.Label(user.userId, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(175)));
                    user.displayName = GUILayout.TextField(user.displayName ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (GUILayout.Button("套用名稱", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) { UpdateUser(user); return; }
                    if (GUILayout.Button("✕ 移除", UCL_GUIStyle.GetButtonStyle(new Color(0.9f, 0.5f, 0.4f)), GUILayout.ExpandWidth(false))) { RemoveUser(user.userId); return; }
                }
                if (!show) return;
                GUILayout.Label("個人簡介（職位、溝通脈絡；隨 inbound 訊息提供給 agent）", UCL_GUIStyle.LabelStyle);
                user.profile = GUILayout.TextArea(user.profile ?? "", UCL_GUIStyle.TextFieldStyle, GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(55)));
                GUILayout.Label("@ 提及別名（逗號分隔；顯示名稱也會自動映射）", UCL_GUIStyle.LabelStyle);
                string aliases = GUILayout.TextField(string.Join(", ", user.aliases), UCL_GUIStyle.TextFieldStyle);
                user.aliases = ParseAliases(aliases);
                if (GUILayout.Button("套用個人資料與別名", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UpdateUser(user);
            }
        }

        void DrawGuildImportSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, "DiscordSettingsGuildImportFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>⬇ Guild 成員候選匯入</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                GUILayout.Label("Discord API 能列出 Guild 成員，不代表可得知誰正在觀看一般文字頻道。匯入只建立候選清單，需逐人按加入才會成為白名單。", UCL_GUIStyle.LabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Guild ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_GuildIdDraft = GUILayout.TextField(m_GuildIdDraft ?? "", UCL_GUIStyle.TextFieldStyle);
                    using (new GUIEnabledScope(m_MemberRequest == null))
                        if (GUILayout.Button("⬇ 從 Discord 匯入", UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 0.8f, 0.5f)), GUILayout.ExpandWidth(false))) StartMemberImport();
                }
                if (m_MemberRequest != null) GUILayout.Label($"匯入中：已讀取 {m_ImportedMemberCount} 位成員…", UCL_GUIStyle.LabelStyle);
                foreach (var candidate in m_Candidates)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(candidate.userId, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(175)));
                        GUILayout.Label(candidate.displayName, UCL_GUIStyle.LabelStyle);
                        using (new GUIEnabledScope(!candidate.isWhitelisted))
                            if (GUILayout.Button(candidate.isWhitelisted ? "已在白名單" : "加入白名單", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                AddWhitelistUser(candidate.userId, candidate.displayName);
                    }
                }
            }
        }

        void DrawNavigationSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("其他 Discord 設定", UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button("🍺 酒館後台：Webhook、鏡像、Daemon 狀態", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UCL_ChatTavernAdminPage.Create();
                if (GUILayout.Button("🔀 頻道路由：Channel → Tavern room", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) UCL_DiscordChannelRoutingPage.Create();
            }
        }

        // 區塊職責：Guild members 分頁 REST 匯入。
        // 物理意義：Discord 每頁至多回 1000 位 guild member，以 after snowflake 接續才能完整列舉大伺服器。
        // 數值影響：唯讀 API GET；結果只留本頁 session 快取，按「加入白名單」才寫 notify_config。
        void StartMemberImport()
        {
            m_Candidates.Clear();
            m_ImportedMemberCount = 0;
            m_NextAfter = "";
            StartNextMemberPage();
        }

        void StartNextMemberPage()
        {
            if (!UCL_DiscordInboundDaemon.TryCreateGuildMembersRequest(m_GuildIdDraft?.Trim(), m_NextAfter, out m_MemberRequest, out string error))
            {
                m_Status = "匯入無法開始：" + error + "。請確認 bot token，並在 Discord Developer Portal 開啟 GUILD MEMBERS INTENT。";
                m_MemberRequest = null;
                return;
            }
            m_MemberRequest.SendWebRequest();
        }

        void PollMemberImport()
        {
            if (m_MemberRequest == null || !m_MemberRequest.isDone) return;
            var request = m_MemberRequest;
            m_MemberRequest = null;
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    m_Status = $"Discord 成員匯入失敗（HTTP {request.responseCode}）：{request.error}。請確認 GUILD_MEMBERS intent 已在 Developer Portal 開啟。";
                    return;
                }
                var members = JsonData.ParseJson(request.downloadHandler.text);
                if (members == null || !members.IsArray) { m_Status = "Discord 回應不是 members 陣列。"; return; }
                string lastId = "";
                for (int i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    var discordUser = member != null && member.Contains("user") ? member["user"] : null;
                    string id = discordUser?.GetString("id", "") ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = member.GetString("nick", "");
                    if (string.IsNullOrEmpty(name)) name = discordUser.GetString("global_name", "");
                    if (string.IsNullOrEmpty(name)) name = discordUser.GetString("username", id);
                    m_Candidates.Add(new GuildMemberCandidate { userId = id, displayName = name, isWhitelisted = m_Users.Any(user => user.userId == id) });
                    lastId = id;
                }
                m_ImportedMemberCount += members.Count;
                if (members.Count >= 1000 && !string.IsNullOrEmpty(lastId)) { m_NextAfter = lastId; StartNextMemberPage(); return; }
                m_Status = $"已從 Discord 匯入 {m_Candidates.Count} 位 Guild 成員候選；尚未自動加入白名單。";
            }
            catch (Exception e) { m_Status = "解析 Discord 成員失敗：" + e.Message; }
            finally { request.Dispose(); }
        }

        void AddWhitelistUser(string idInput, string nameInput)
        {
            string id = (idInput ?? "").Trim();
            if (!ulong.TryParse(id, out _)) { m_Status = "Discord user ID 必須是正整數 snowflake。"; return; }
            if (m_Users.Any(user => user.userId == id)) { m_Status = "此 user ID 已在白名單。"; return; }
            WriteWhitelist(whitelist =>
            {
                var entry = JsonData.ParseJson("{}");
                entry[KeyUserId] = new JsonData(id);
                entry[KeyDisplayName] = new JsonData((nameInput ?? "").Trim());
                entry[KeyProfile] = new JsonData("");
                entry[KeyAliases] = JsonData.ParseJson("[]");
                whitelist[KeyUsers].Add(entry);
            });
            m_NewUserId = "";
            m_NewUserName = "";
        }

        void UpdateUser(WhitelistUser user)
        {
            WriteWhitelist(whitelist =>
            {
                var users = whitelist[KeyUsers];
                for (int i = 0; i < users.Count; i++)
                    if (users[i].GetString(KeyUserId, "") == user.userId)
                    {
                        users[i][KeyDisplayName] = new JsonData((user.displayName ?? "").Trim());
                        users[i][KeyProfile] = new JsonData((user.profile ?? "").Trim());
                        var aliases = JsonData.ParseJson("[]");
                        foreach (var alias in user.aliases) aliases.Add(new JsonData(alias));
                        users[i][KeyAliases] = aliases;
                        break;
                    }
            });
        }

        void RemoveUser(string id)
        {
            WriteWhitelist(whitelist =>
            {
                var kept = JsonData.ParseJson("[]");
                var users = whitelist[KeyUsers];
                for (int i = 0; i < users.Count; i++) if (users[i].GetString(KeyUserId, "") != id) kept.Add(users[i]);
                whitelist[KeyUsers] = kept;
            });
        }

        void AddMentionMapping()
        {
            string name = (m_NewMentionName ?? "").Trim();
            string id = (m_NewMentionUserId ?? "").Trim();
            if (string.IsNullOrEmpty(name) || !ulong.TryParse(id, out _)) { m_Status = "@ 名稱不可空白，Discord user ID 必須是正整數 snowflake。"; return; }
            WriteMentionMappings(map => map[name] = new JsonData(id));
            m_NewMentionName = "";
            m_NewMentionUserId = "";
        }

        void RemoveMentionMapping(string name) => WriteMentionMappings(map => map.Dic.Remove(name));

        void WriteMentionMappings(Action<JsonData> mutate)
        {
            try
            {
                var config = File.Exists(NotifyConfigPath) ? JsonData.ParseJson(File.ReadAllText(NotifyConfigPath)) : JsonData.ParseJson("{}");
                if (!config.Contains(KeyTavernMirror)) config[KeyTavernMirror] = JsonData.ParseJson("{}");
                var mirror = config[KeyTavernMirror];
                if (!mirror.Contains(KeyDiscordUserMentions)) mirror[KeyDiscordUserMentions] = JsonData.ParseJson("{}");
                mutate(mirror[KeyDiscordUserMentions]);
                AtomicWrite(NotifyConfigPath, config.ToJsonBeautify());
                LoadData();
            }
            catch (Exception e) { m_Status = "寫入 @ 提及對照失敗：" + e.Message; }
        }

        void WriteWhitelist(Action<JsonData> mutate)
        {
            try
            {
                var config = File.Exists(NotifyConfigPath) ? JsonData.ParseJson(File.ReadAllText(NotifyConfigPath)) : JsonData.ParseJson("{}");
                if (!config.Contains(KeyTavernInbound)) config[KeyTavernInbound] = JsonData.ParseJson("{}");
                var inbound = config[KeyTavernInbound];
                if (!inbound.Contains(KeyUserWhitelist)) inbound[KeyUserWhitelist] = JsonData.ParseJson("{\"enabled\":false,\"users\":[]}");
                var whitelist = inbound[KeyUserWhitelist];
                if (!whitelist.Contains(KeyUsers) || !whitelist[KeyUsers].IsArray) whitelist[KeyUsers] = JsonData.ParseJson("[]");
                mutate(whitelist);
                AtomicWrite(NotifyConfigPath, config.ToJsonBeautify());
                LoadData();
            }
            catch (Exception e) { m_Status = "寫入白名單失敗：" + e.Message; }
        }

        static List<string> ParseAliases(string text) => (text ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(alias => alias.Trim()).Where(alias => !string.IsNullOrEmpty(alias)).Distinct(StringComparer.Ordinal).ToList();

        void RefreshCandidateWhitelistFlags()
        {
            foreach (var candidate in m_Candidates) candidate.isWhitelisted = m_Users.Any(user => user.userId == candidate.userId);
        }

        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".discord-settings.tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        struct GUIEnabledScope : IDisposable
        {
            readonly bool m_Previous;
            public GUIEnabledScope(bool enabled) { m_Previous = GUI.enabled; GUI.enabled = enabled && m_Previous; }
            public void Dispose() { GUI.enabled = m_Previous; }
        }
    }
}
#endif
