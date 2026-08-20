// 區塊職責：Discord mirror 的身分解析 — sender → (username, avatar_url) + body @-mention rewrite（Phase B T5）
// 物理意義：讓 native mirror 的 Discord 訊息顯示 persona 頭像 + 真實 @ping，取代 T4-b MVP 的「只有 DisplayName」。
//          對齊 python notify_discord.py 的 _resolve_discord_identity / _rewrite_at_mentions_for_discord。
// 設計取捨：讀的是 python 擁有的既有 config（notify_config.json.tavern_mirror + identities.json，含 _note 等
//          混雜欄位）→ 用 JsonData 直讀比硬套 class 合適（Tim class+JsonLib 指引針對「持久資料格式」= state cursor，
//          那個已走 class；讀外部 foreign config 用 JsonData indexer 更穩、不會被 _note 之類噎到）。
// 數值影響：avatar 解析優先序 persona 覆寫 > sprite 派生 > identity 覆寫 > pattern；5s 快取避免每則訊息重讀檔。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>Discord mirror 身分解析（T5）。static、帶 5s config 快取。</summary>
    public static class UCL_DiscordIdentityResolver
    {
        // python DEFAULT_CONFIG.tavern_mirror.avatar_url_base（config 沒設時的預設，native 必須跟 python 同源）
        const string DEFAULT_AVATAR_BASE =
            "https://raw.githubusercontent.com/tim099/UCL_Core/Dev/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/ModResources/Sprites/Avatars/";
        const string DEFAULT_AVATAR_PATTERN = "{base}{id}.png";
        const double CACHE_TTL_SEC = 5.0;
        const string KeyTavernMirror = "tavern_mirror";
        const string KeyTavernInbound = "tavern_inbound";
        const string KeyUserWhitelist = "user_whitelist";
        const string KeyWhitelistUsers = "users";
        const string KeyUserId = "user_id";
        const string KeyDisplayName = "display_name";
        const string KeyAliases = "aliases";

        // ── 快取狀態 ──
        static double s_CacheTime = -999;
        static string s_AvatarBase = DEFAULT_AVATAR_BASE;
        static string s_AvatarPattern = DEFAULT_AVATAR_PATTERN;
        static readonly Dictionary<string, string> s_PersonaAvatar = new Dictionary<string, string>();  // persona → url
        static readonly Dictionary<string, string> s_IdentityAvatar = new Dictionary<string, string>();  // id → avatar_url
        static readonly Dictionary<string, string> s_IdentityUsername = new Dictionary<string, string>(); // id → username override
        static readonly Dictionary<string, string> s_UserMentions = new Dictionary<string, string>();      // name → discord user_id

        // 區塊職責：載入 config + identities（5s 快取）
        static void EnsureLoaded()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_CacheTime < CACHE_TTL_SEC) return;
            s_CacheTime = now;
            try
            {
                LoadTavernMirrorConfig();
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] identity resolver load fail: {e.Message}"); }
        }

        static void LoadTavernMirrorConfig()
        {
            s_PersonaAvatar.Clear(); s_IdentityAvatar.Clear(); s_IdentityUsername.Clear(); s_UserMentions.Clear();
            s_AvatarBase = DEFAULT_AVATAR_BASE; s_AvatarPattern = DEFAULT_AVATAR_PATTERN;

            string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
            if (!File.Exists(path)) return;
            var jd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path));
            if (jd == null || !jd.IsObject) return;
            var tm = jd.Contains(KeyTavernMirror) ? jd[KeyTavernMirror] : null;
            if (tm == null || !tm.IsObject)
            {
                AddWhitelistMentionAliases(jd);
                return;
            }

            if (tm.Contains("avatar_url_base")) { var v = tm.GetString("avatar_url_base", ""); if (!string.IsNullOrEmpty(v)) s_AvatarBase = v; }
            if (tm.Contains("avatar_url_pattern")) { var v = tm.GetString("avatar_url_pattern", ""); if (!string.IsNullOrEmpty(v)) s_AvatarPattern = v; }

            // persona_avatar_overrides: {persona: url}（跳過 _note 之類非 URL 值 — 只收 http 開頭）
            CopyStringMap(tm, "persona_avatar_overrides", s_PersonaAvatar, httpOnly: true);
            // discord_user_mentions: {name: user_id}
            CopyStringMap(tm, "discord_user_mentions", s_UserMentions, httpOnly: false);
            // 白名單使用者可在同一個 Discord ID 下登記多個稱呼（例如 David / Dump）。
            // 既有 discord_user_mentions 是更明確的 outbound 設定，故此處只補未命中的 alias，不覆蓋它。
            AddWhitelistMentionAliases(jd);
            // identity_overrides: {id: {avatar_url, username}}
            if (tm.Contains("identity_overrides"))
            {
                var io = tm["identity_overrides"];
                if (io != null && io.IsObject && io.Dic != null)
                {
                    foreach (var kv in io.Dic)
                    {
                        if (kv.Value == null || !kv.Value.IsObject) continue;
                        string av = kv.Value.GetString("avatar_url", "");
                        string un = kv.Value.GetString("username", "");
                        if (!string.IsNullOrEmpty(av)) s_IdentityAvatar[kv.Key] = av;
                        if (!string.IsNullOrEmpty(un)) s_IdentityUsername[kv.Key] = un;
                    }
                }
            }
        }

        // 區塊職責：把 inbound 白名單的顯示名稱與 aliases 投影成 outbound @mention 對照。
        // 物理意義：同一真人可能在不同上下文被叫不同暱稱；全部收斂到他的 Discord snowflake，才不會 @ 錯人。
        // 數值影響：只補 s_UserMentions 尚未存在的名稱，讓 tavern_mirror.discord_user_mentions 可顯式覆寫衝突。
        static void AddWhitelistMentionAliases(UCL.Core.JsonLib.JsonData config)
        {
            var inbound = config.Contains(KeyTavernInbound) ? config[KeyTavernInbound] : null;
            var whitelist = inbound != null && inbound.Contains(KeyUserWhitelist) ? inbound[KeyUserWhitelist] : null;
            var users = whitelist != null && whitelist.Contains(KeyWhitelistUsers) ? whitelist[KeyWhitelistUsers] : null;
            if (users == null || !users.IsArray) return;
            for (int i = 0; i < users.Count; i++)
            {
                var user = users[i];
                if (user == null) continue;
                string id = user.GetString(KeyUserId, "");
                if (string.IsNullOrEmpty(id)) continue;
                AddMentionAlias(user.GetString(KeyDisplayName, ""), id);
                var aliases = user.Contains(KeyAliases) ? user[KeyAliases] : null;
                if (aliases == null || !aliases.IsArray) continue;
                for (int j = 0; j < aliases.Count; j++) AddMentionAlias(aliases[j].GetString(), id);
            }
        }

        static void AddMentionAlias(string alias, string userId)
        {
            string name = (alias ?? "").Trim();
            if (!string.IsNullOrEmpty(name) && !s_UserMentions.ContainsKey(name)) s_UserMentions[name] = userId;
        }

        // ===========================================================
        // 區塊職責：id → 顯示名稱的**唯一**解析點（渲染端）。
        // 物理意義：合一之後 sender_id 就是帳戶 id，而「這個身分顯示成什麼」的真相源是
        //          `Treasury/accounts/<id>.json` 的 display_name（後台 🏷 帳戶資料 可改）。
        //          `identities.json` **已廢棄**（Tim 2026-08-20）—— 顯示名稱不再有第二個來源。
        //          廢棄前已把 roster 裡的名字搬進帳戶資料：Claude大小姐／Antigravity大小姐／
        //          Zeta大小姐／Gemini大小姐／月讀大小姐／酒保／潛意識守夜人／Tim。
        //          Discord 真人（`discord:<id>`）不是帳戶，他們的 @ 走 notify_config 的
        //          discord_user_mentions（真 ping），不需要這張表。
        // 🩸 BUG-25（Tim 2026-08-20 抓）：寫入端已經改成帳戶資料優先，渲染端卻自己又查一次
        //   identities.json ⇒ 落盤的 sender_name 是對的、畫面上是錯的。
        //   而 roster 裡 `cc` 那筆 display_name 是 `crest-001`（persona 名塞進 agent 的欄位）——
        //   不是查不到，是**查到了一個看起來完全正常的別人**。
        //   ⇒ 同一件事只能有一個算點：渲染端與寫入端從此走同一個順序。
        // 數值影響：純顯示，不動任何金流。
        // ===========================================================
        public static string ResolveDisplayName(string iId)
        {
            if (string.IsNullOrEmpty(iId)) return null;
            EnsureLoaded();
            string fromProfile = Treasury.UCL_BankAccountProfileIO.GetDisplayName(iId);
            return string.IsNullOrEmpty(fromProfile) ? null : fromProfile;
        }

        static void CopyStringMap(UCL.Core.JsonLib.JsonData parent, string key, Dictionary<string, string> dst, bool httpOnly)
        {
            if (!parent.Contains(key)) return;
            var node = parent[key];
            if (node == null || !node.IsObject || node.Dic == null) return;
            foreach (var kv in node.Dic)
            {
                if (kv.Value == null) continue;
                string val = kv.Value.GetString();
                if (string.IsNullOrEmpty(val)) continue;
                if (httpOnly && !val.StartsWith("http")) continue;   // 濾掉 _note 等非 URL 值
                dst[kv.Key] = val;
            }
        }

        /// <summary>解析頭像 URL：persona 覆寫 > sprite 派生 > identity 覆寫 > pattern。取不到回 null（Discord 用預設頭像）。</summary>
        public static string ResolveAvatarUrl(string senderPersona, string senderAvatarSprite, string senderId)
        {
            EnsureLoaded();
            // 1. persona 顯式覆寫（最高，不做 HEAD 預檢 — 對齊 python persona_avatar_overrides）
            if (!string.IsNullOrEmpty(senderPersona) && s_PersonaAvatar.TryGetValue(senderPersona, out var pu)) return pu;
            // 2. sprite 派生（Avatars_<name> → base + <name>.png）— MVP 不做 HEAD 預檢（404 時 Discord silent 預設頭像）
            if (!string.IsNullOrEmpty(senderAvatarSprite) && !string.IsNullOrEmpty(s_AvatarBase))
            {
                string fn = senderAvatarSprite;
                if (fn.StartsWith("Avatars_")) fn = fn.Substring("Avatars_".Length);
                if (!fn.EndsWith(".png")) fn += ".png";
                return s_AvatarBase + fn;
            }
            // 3. identity 覆寫
            if (!string.IsNullOrEmpty(senderId) && s_IdentityAvatar.TryGetValue(senderId, out var iu)) return iu;
            // 4. pattern（agent-level convention）
            if (!string.IsNullOrEmpty(s_AvatarBase) && !string.IsNullOrEmpty(senderId))
                return s_AvatarPattern.Replace("{base}", s_AvatarBase).Replace("{id}", senderId);
            return null;
        }

        /// <summary>解析 username：identity 覆寫 &gt; **帳戶資料 display_name** &gt; identities roster &gt; sender_name &gt; id，再清洗成 Discord 合法 username。</summary>
        public static string ResolveUsername(string senderId, string senderName, string fallbackDisplay, string senderPersona = null)
        {
            EnsureLoaded();
            string username = null;
            if (!string.IsNullOrEmpty(senderId) && s_IdentityUsername.TryGetValue(senderId, out var ov)) username = ov;
            // 帳戶資料的 display_name 優先於 identities roster —— 見 ResolveDisplayName 的血證。
            if (string.IsNullOrEmpty(username)) { var dn = ResolveDisplayName(senderId); if (!string.IsNullOrEmpty(dn)) username = dn; }
            if (string.IsNullOrEmpty(username)) username = !string.IsNullOrEmpty(fallbackDisplay) ? fallbackDisplay : senderName;
            if (string.IsNullOrEmpty(username)) username = senderId;

            // 區塊職責：@persona 後綴（python _build_tavern_payload parity — Tim 2026-07-20 抓漏）
            // 物理意義：python 解析完身分後永遠補 "<name>@<persona>"；native 原版漏了這步 →
            //          identities.json 有登記的 sender 走 display_name 路徑丟失後綴（Claude大小姐 沒 @basecamp），
            //          沒登記的 sender fallback 到 msg.DisplayName（本身含 @persona）反而有 → 看似間歇實為查表分歧。
            //          之前沒被抓到是因為 cutover 前 python Stop-hook 雙送的那份有後綴，遮住了 native 份的缺失。
            // 邊界：fallback 路徑 username 可能已含同名後綴 → EndsWith 防重複 append；截 80 交給 CleanUsername。
            if (!string.IsNullOrEmpty(senderPersona) && !string.IsNullOrEmpty(username))
            {
                string suffix = "@" + senderPersona;
                if (!username.EndsWith(suffix, StringComparison.Ordinal)) username += suffix;
            }
            return CleanUsername(username);
        }

        // Discord webhook username 限制：不可含 "discord"（case-insensitive）/ ":"；長度 1-80
        static string CleanUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return username;
            string cleaned = username.Replace(":", "_");
            if (cleaned.ToLowerInvariant().Contains("discord"))
                cleaned = Regex.Replace(cleaned, "(?i)discord", "DC_");
            if (cleaned.Length > 80) cleaned = cleaned.Substring(0, 80);
            return cleaned;
        }

        static Regex s_MentionRe;

        /// <summary>body 內 @&lt;name&gt; rewrite：discord_user_mentions 命中 → &lt;@user_id&gt;（真 ping）；否則帳戶資料／identities 的顯示名 → @顯示名；都沒 → 原樣。</summary>
        public static string RewriteMentions(string body)
        {
            if (string.IsNullOrEmpty(body) || body.IndexOf('@') < 0) return body;
            EnsureLoaded();
            if (s_MentionRe == null) s_MentionRe = new Regex(@"@([\w.\-]+)", RegexOptions.Compiled);
            return s_MentionRe.Replace(body, m =>
            {
                string name = m.Groups[1].Value;
                if (s_UserMentions.TryGetValue(name, out var uid)) return $"<@{uid}>";          // 真 Discord ping
                { var dn = ResolveDisplayName(name); if (!string.IsNullOrEmpty(dn)) return $"@{dn}"; }  // visual @ 顯示名（帳戶資料優先）
                return m.Value;                                                                   // 沒命中保留原樣
            });
        }
    }
}
#endif
