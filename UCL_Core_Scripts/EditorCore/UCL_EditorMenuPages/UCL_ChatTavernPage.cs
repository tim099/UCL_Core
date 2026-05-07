// UCL Chat Tavern — IMGUI 頁面（prototype v1）
// 職責：人類可在 Editor 內直接以某個 identity 加入房間 / 看訊息 / 發言。
// 物理意義：跟 agent 走的 Cmd 接同一份檔案 → 人 vs agent 同一個聊天室。
// prototype 取捨：UI 字串先硬編；refs/meta 表單最簡（單行 paths + key=val）。
#if UNITY_EDITOR
using System.Collections.Generic;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Chat Tavern IMGUI 頁面 — 房間下拉 + 訊息檢視 + 發言。
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernPage.md")]
    public class UCL_ChatTavernPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Chat Tavern";

        // opt-in 進 UCL_EditorMenuPage 的 Page 選擇器下拉
        public override bool ShowInPageMenu => true;

        public static UCL_ChatTavernPage Create() => UCL_EditorPage.Create<UCL_ChatTavernPage>();

        // ===== 選擇狀態 =====
        // 區塊職責：使用者目前選中的房間 / 身分 + 暫存的輸入框內容
        // 物理意義：純 UI 狀態；按下 Send 才會落檔
        string m_SelectedRoomId = "";
        string m_SelectedIdentityId = "";
        string m_Input = "";
        string m_MetaInput = "";        // "k1=v1;k2=v2"
        string m_RefsInput = "";        // "path1|path2"
        int? m_ReplyTo = null;
        Vector2 m_MessagesScroll = Vector2.zero;

        // ===== 新建表單 =====
        bool m_ShowCreateRoom = false;
        string m_NewRoomId = "";
        string m_NewRoomName = "";
        string m_NewRoomDesc = "";

        bool m_ShowCreateIdentity = false;
        // 區塊職責：「+ 新身分」表單預設值
        // 物理意義：刻意留空 — agent-neutral；別讓人以為這個系統是 Claude 專屬
        // 數值影響：使用者每次點「+ 新身分」要主動填 id / name；建議格式見表單上方 hint
        string m_NewIdentityId = "";
        string m_NewIdentityName = "";
        string m_NewIdentityKind = "agent";

        // ===== 顯示快取 =====
        // 物理意義：每幀重讀 jsonl 太重；只有 Refresh / Send / 進房間時重抓
        UCL_ChatRoomList m_RoomsCache;
        UCL_ChatIdentityList m_IdentitiesCache;
        List<UCL_ChatMessage> m_MessagesCache;
        UCL_ChatRoomMembers m_MembersCache;

        // ===== 自動 polling =====
        // 物理意義：勾選後每 N 秒重抓 messages — 模擬即時聊天感
        bool m_AutoPoll = true;
        double m_LastPollTime = 0;
        const double PollIntervalSec = 2.0;

        // 區塊職責：頁面首次繪製時的一次性自動初始化旗標
        // 物理意義：第一次 ContentOnGUI 時：若沒有任何房間 → 建一間 default；若沒選中房間 → 自動選第一間
        // 數值影響：旗標確保只跑一次；Refresh 不重置（避免人為清掉 m_SelectedRoomId 後又被自動拉回）
        bool m_AutoInitDone = false;

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                RefreshAll();
            }
            m_AutoPoll = GUILayout.Toggle(m_AutoPoll, "Auto-Poll", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
            if (GUILayout.Button("Open Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_ChatTavernIO.EnsureTavernDir();
                UnityEditor.EditorUtility.RevealInFinder(UCL_ChatTavernIO.GetTavernDir());
            }
        }

        protected override void ContentOnGUI()
        {
            if (m_RoomsCache == null) RefreshRooms();
            if (m_IdentitiesCache == null) RefreshIdentities();
            EnsureAutoInit();
            HandleAutoPoll();

            DrawRoomPicker();
            GUILayout.Space(4);
            DrawIdentityPicker();
            GUILayout.Space(8);

            if (string.IsNullOrEmpty(m_SelectedRoomId))
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label("請先選擇或建立一個房間。", UCL_GUIStyle.LabelStyle);
                }
                return;
            }
            DrawMessagesView();
            GUILayout.Space(4);
            DrawInputBar();
        }

        // ===========================================================
        // 區塊：房間選擇 + 建立
        // ===========================================================
        void DrawRoomPicker()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("房間：", UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_RoomsCache.rooms.Count == 0)
                {
                    GUILayout.Label("(尚無房間，請建立)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    foreach (var r in m_RoomsCache.rooms)
                    {
                        bool selected = m_SelectedRoomId == r.id;
                        var style = selected ? UCL_GUIStyle.GetButtonStyle(Color.cyan) : UCL_GUIStyle.ButtonStyle;
                        if (GUILayout.Button(r.name, style, GUILayout.ExpandWidth(false)))
                        {
                            m_SelectedRoomId = r.id;
                            RefreshMessages();
                            RefreshMembers();
                        }
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(m_ShowCreateRoom ? "− Cancel" : "+ 新房間", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_ShowCreateRoom = !m_ShowCreateRoom;
                }
            }
            if (m_ShowCreateRoom)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    m_NewRoomId = LabeledTextField("id", m_NewRoomId, 60);
                    m_NewRoomName = LabeledTextField("name", m_NewRoomName, 60);
                    m_NewRoomDesc = LabeledTextField("desc", m_NewRoomDesc, 60);
                    if (GUILayout.Button("Create", UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewRoomId))
                        {
                            Debug.LogError("Room id 不能為空");
                        }
                        else
                        {
                            UCL_ChatTavernIO.CreateRoom(m_NewRoomId, m_NewRoomName, m_NewRoomDesc);
                            m_SelectedRoomId = m_NewRoomId;
                            m_NewRoomId = m_NewRoomName = m_NewRoomDesc = "";
                            m_ShowCreateRoom = false;
                            RefreshAll();
                        }
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：身分選擇 + 建立
        // ===========================================================
        void DrawIdentityPicker()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("身分：", UCL_GUIStyle.LabelStyle, GUILayout.Width(50));
                if (m_IdentitiesCache.identities.Count == 0)
                {
                    GUILayout.Label("(尚無身分，請建立)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    foreach (var i in m_IdentitiesCache.identities)
                    {
                        bool selected = m_SelectedIdentityId == i.id;
                        var style = selected ? UCL_GUIStyle.GetButtonStyle(Color.yellow) : UCL_GUIStyle.ButtonStyle;
                        if (GUILayout.Button($"{i.display_name} ({i.kind})", style, GUILayout.ExpandWidth(false)))
                        {
                            m_SelectedIdentityId = i.id;
                        }
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(m_ShowCreateIdentity ? "− Cancel" : "+ 新身分", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_ShowCreateIdentity = !m_ShowCreateIdentity;
                }
            }
            if (m_ShowCreateIdentity)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    // 區塊職責：身分命名約定 hint — 提醒使用者本系統是 agent-neutral
                    // 物理意義：避免有人以為這只給 Claude 用 → 列出三家 agent 的範例 id
                    GUILayout.Label("命名約定：id 用 <model>-<persona>，例：claude-da-xiaojie / gemini-da-xiaojie / gpt-shifu",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("display_name 用 agent 自家稱呼，例：Claude大小姐 / Gemini大小姐 / GPT師傅",
                        UCL_GUIStyle.LabelStyle);
                    m_NewIdentityId = LabeledTextField("id", m_NewIdentityId, 60);
                    m_NewIdentityName = LabeledTextField("name", m_NewIdentityName, 60);
                    m_NewIdentityKind = LabeledTextField("kind", m_NewIdentityKind, 60);
                    if (GUILayout.Button("Create", UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
                    {
                        if (string.IsNullOrEmpty(m_NewIdentityId))
                        {
                            Debug.LogError("Identity id 不能為空");
                        }
                        else
                        {
                            UCL_ChatTavernIO.GetOrCreateIdentity(m_NewIdentityId, m_NewIdentityName, m_NewIdentityKind);
                            m_SelectedIdentityId = m_NewIdentityId;
                            m_ShowCreateIdentity = false;
                            RefreshIdentities();
                        }
                    }
                }
            }
            // 加入 / 離開房間按鈕
            if (!string.IsNullOrEmpty(m_SelectedRoomId) && !string.IsNullOrEmpty(m_SelectedIdentityId))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool isMember = m_MembersCache != null && m_MembersCache.member_ids.Contains(m_SelectedIdentityId);
                    if (!isMember)
                    {
                        if (GUILayout.Button($"加入「{m_SelectedRoomId}」", UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 1f, 0.4f)), GUILayout.ExpandWidth(false)))
                        {
                            DoJoin();
                        }
                    }
                    else
                    {
                        if (GUILayout.Button($"離開「{m_SelectedRoomId}」", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.4f)), GUILayout.ExpandWidth(false)))
                        {
                            DoLeave();
                        }
                    }
                    GUILayout.FlexibleSpace();
                    if (m_MembersCache != null)
                    {
                        GUILayout.Label($"在場 {m_MembersCache.member_ids.Count} 人", UCL_GUIStyle.LabelStyle);
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：訊息檢視
        // ===========================================================
        void DrawMessagesView()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"# 🍺 {m_SelectedRoomId} (seq={UCL_ChatTavernIO.ReadCurrentSeq(m_SelectedRoomId)})", UCL_GUIStyle.LabelStyle);
                m_MessagesScroll = GUILayout.BeginScrollView(m_MessagesScroll, GUILayout.MinHeight(280), GUILayout.MaxHeight(420));
                if (m_MessagesCache == null || m_MessagesCache.Count == 0)
                {
                    GUILayout.Label("_(尚無訊息)_", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    foreach (var m in m_MessagesCache)
                    {
                        DrawMessageRow(m);
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        void DrawMessageRow(UCL_ChatMessage m)
        {
            using (new GUILayout.HorizontalScope())
            {
                Color c = m.kind switch
                {
                    "join" => new Color(0.5f, 1f, 0.5f),
                    "leave" => new Color(1f, 0.7f, 0.5f),
                    "system" => Color.gray,
                    _ => Color.white,
                };
                var style = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, normal = { textColor = c } };
                string time = "??:??:??";
                if (!string.IsNullOrEmpty(m.ts))
                {
                    int t = m.ts.IndexOf('T');
                    if (t >= 0 && t + 9 <= m.ts.Length) time = m.ts.Substring(t + 1, 8);
                }
                string prefix = $"[{m.seq}] {time} {m.sender_name}:";
                GUILayout.Label(prefix, style, GUILayout.Width(220));
                GUILayout.Label(m.body ?? "", style);
                if (GUILayout.Button("↩", UCL_GUIStyle.ButtonStyle, GUILayout.Width(30)))
                {
                    m_ReplyTo = m.seq;
                }
            }
            if (m.refs != null && m.refs.Count > 0)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Space(220);
                    foreach (var r in m.refs)
                    {
                        if (GUILayout.Button($"📎 {r.path}", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            // 嘗試在 Project 視窗 ping 該 asset（路徑是相對 repo root，要轉成 Assets/...）
                            TryPingAsset(r.path);
                        }
                    }
                }
            }
            if (m.meta != null && m.meta.Count > 0)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Space(220);
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in m.meta) sb.Append("[").Append(kv.Key).Append("=").Append(kv.Value).Append("] ");
                    GUILayout.Label(sb.ToString(), UCL_GUIStyle.LabelStyle);
                }
            }
        }

        // ===========================================================
        // 區塊：輸入區（送訊息）
        // ===========================================================
        void DrawInputBar()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_ReplyTo.HasValue)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"↩ 回覆 seq={m_ReplyTo.Value}", UCL_GUIStyle.LabelStyle, GUILayout.Width(120));
                        if (GUILayout.Button("取消", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false))) m_ReplyTo = null;
                    }
                }
                m_Input = GUILayout.TextArea(m_Input ?? "", GUILayout.MinHeight(60));
                m_MetaInput = LabeledTextField("meta (k=v;k=v)", m_MetaInput, 130);
                m_RefsInput = LabeledTextField("refs (path|path)", m_RefsInput, 130);
                using (new GUILayout.HorizontalScope())
                {
                    bool canSend = !string.IsNullOrEmpty(m_SelectedIdentityId) && !string.IsNullOrEmpty(m_Input);
                    GUI.enabled = canSend;
                    if (GUILayout.Button("Send", UCL_GUIStyle.GetButtonStyle(canSend ? Color.cyan : Color.gray), GUILayout.ExpandWidth(false)))
                    {
                        DoSend();
                    }
                    GUI.enabled = true;
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Clear", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Input = m_MetaInput = m_RefsInput = "";
                        m_ReplyTo = null;
                    }
                }
            }
        }

        // ===========================================================
        // 動作
        // ===========================================================
        void DoSend()
        {
            var ident = m_IdentitiesCache.identities.Find(x => x.id == m_SelectedIdentityId);
            if (ident == null) { Debug.LogError("身分不存在"); return; }
            var room = UCL_ChatTavernIO.GetRoom(m_SelectedRoomId);
            if (room == null) { Debug.LogError("房間不存在"); return; }

            var msg = new UCL_ChatMessage
            {
                sender_id = ident.id,
                sender_name = ident.display_name,
                kind = "chat",
                body = m_Input,
                reply_to = m_ReplyTo,
                meta = ParseMetaSimple(m_MetaInput),
                refs = ParseRefsSimple(m_RefsInput),
            };
            int seq = UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, msg);
            UCL_ChatTavernRender.WriteLastView(m_SelectedRoomId, room.name,
                UCL_ChatTavernIO.Tail(m_SelectedRoomId, 100), seq,
                $"> 你 ({ident.display_name}) 剛 post：seq={seq}");
            m_Input = "";
            m_ReplyTo = null;
            RefreshMessages();
        }

        void DoJoin()
        {
            var ident = m_IdentitiesCache.identities.Find(x => x.id == m_SelectedIdentityId);
            if (ident == null) return;
            UCL_ChatTavernIO.AddMember(m_SelectedRoomId, ident.id);
            UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = ident.id, sender_name = ident.display_name, kind = "join",
                body = $"{ident.display_name} 進入了酒館",
            });
            RefreshMessages();
            RefreshMembers();
        }

        void DoLeave()
        {
            var ident = m_IdentitiesCache.identities.Find(x => x.id == m_SelectedIdentityId);
            if (ident == null) return;
            UCL_ChatTavernIO.RemoveMember(m_SelectedRoomId, ident.id);
            UCL_ChatTavernIO.AppendMessage(m_SelectedRoomId, new UCL_ChatMessage
            {
                sender_id = ident.id, sender_name = ident.display_name, kind = "leave",
                body = $"{ident.display_name} 離開了酒館",
            });
            RefreshMessages();
            RefreshMembers();
        }

        // ===========================================================
        // helper
        // ===========================================================
        void RefreshAll()
        {
            RefreshRooms();
            RefreshIdentities();
            if (!string.IsNullOrEmpty(m_SelectedRoomId))
            {
                RefreshMessages();
                RefreshMembers();
            }
        }
        void RefreshRooms() { m_RoomsCache = UCL_ChatTavernIO.LoadRooms(); }
        void RefreshIdentities() { m_IdentitiesCache = UCL_ChatTavernIO.LoadIdentities(); }
        void RefreshMessages() { m_MessagesCache = UCL_ChatTavernIO.Tail(m_SelectedRoomId, 100); }
        void RefreshMembers() { m_MembersCache = UCL_ChatTavernIO.LoadMembers(m_SelectedRoomId); }

        // 區塊職責：頁面首次 ContentOnGUI 時的自動初始化
        // 物理意義：UX 起手式 — 第一次打開酒館頁就有可用房間，使用者不必先去建房；
        //          沒選中房間 → 自動選第一間，省一次點擊
        // 數值影響：
        //   1) 沒任何房間 → 自動建一間 id=`default` / name="Default 酒館" 並 RefreshRooms
        //   2) 沒選中房間 + 有可選 → 設 m_SelectedRoomId = 第一間 + 載 messages / members
        //   3) m_AutoInitDone 確保整個流程在頁面生命週期內只跑一次（避免人為清空後又被搶選）
        void EnsureAutoInit()
        {
            if (m_AutoInitDone) return;
            m_AutoInitDone = true;

            // (1) 沒任何房間 → 建一間 default
            if (m_RoomsCache == null || m_RoomsCache.rooms.Count == 0)
            {
                UCL_ChatTavernIO.CreateRoom("default", "Default 酒館", "首次開啟自動建立的預設房間");
                RefreshRooms();
            }

            // (2) 沒選中 → 選第一間
            if (string.IsNullOrEmpty(m_SelectedRoomId)
                && m_RoomsCache != null && m_RoomsCache.rooms.Count > 0)
            {
                m_SelectedRoomId = m_RoomsCache.rooms[0].id;
                RefreshMessages();
                RefreshMembers();
            }
        }

        void HandleAutoPoll()
        {
            if (!m_AutoPoll) return;
            if (string.IsNullOrEmpty(m_SelectedRoomId)) return;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - m_LastPollTime < PollIntervalSec) return;
            m_LastPollTime = now;
            RefreshMessages();
            RefreshMembers();
        }

        static string LabeledTextField(string label, string value, float labelWidth)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label, UCL_GUIStyle.LabelStyle, GUILayout.Width(labelWidth));
                return GUILayout.TextField(value ?? "");
            }
        }

        static Dictionary<string, string> ParseMetaSimple(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var d = new Dictionary<string, string>();
            foreach (var pair in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                int idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                d[pair.Substring(0, idx).Trim()] = pair.Substring(idx + 1).Trim();
            }
            return d.Count > 0 ? d : null;
        }
        static List<UCL_ChatRef> ParseRefsSimple(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<UCL_ChatRef>();
            foreach (var p in raw.Split('|'))
            {
                string path = p.Trim();
                if (!string.IsNullOrEmpty(path)) list.Add(new UCL_ChatRef { path = path });
            }
            return list.Count > 0 ? list : null;
        }

        static void TryPingAsset(string repoRelativePath)
        {
            // 區塊職責：把 repo 相對路徑轉成 Assets/ 路徑後 ping 出來
            // 物理意義：refs 紀錄為 repo root 相對；Unity AssetDatabase 只認 Assets/ 路徑
            // 數值影響：純讀；找不到 asset 印 warning 但不報錯
            const string assetsPrefix = "CardGame/Assets/";
            if (repoRelativePath.StartsWith(assetsPrefix))
            {
                string assetPath = "Assets/" + repoRelativePath.Substring(assetsPrefix.Length);
                var obj = UnityEditor.AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj != null)
                {
                    UnityEditor.EditorGUIUtility.PingObject(obj);
                    return;
                }
            }
            Debug.LogWarning($"[ChatTavern] 無法 ping asset：{repoRelativePath}");
        }
    }
}
#endif
