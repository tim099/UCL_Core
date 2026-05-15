
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
    // 區塊職責：登入狀態管理 UI — 列出 active persona locks + persona registry 池 + 手動 morning/goodnight 操作
    // 物理意義：awakening.py 端 session lock 存在 AgentCommands/_session/_persona_<persona>.json；persona pool
    //          存在 AgentCommands/AwakenInit/personas/<persona>.json。Page 直讀檔顯示, 操作走 process spawn 跑 awakening.py
    // 數值影響：UI 顯示純 read。Login/Logout 按鈕觸發外部 python process, 改 lock/persona file
    //
    // 設計理由 (Tim 2026-05-13 拍板):
    //   原生 awakening.py 只有 CLI 介面, agent / Tim 想看誰登入 / 哪個 lock 卡死 / 手動清，沒有可視化介面。
    //   本 page 補可視化 + 手動操作 fallback, 避免 bug 卡 lock 必須 ssh 進 _session 手動 rm。
    [HelpURL("ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md")]
    public class UCL_LoginStatusPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("LoginStatus.Title");
        public override bool ShowInPageMenu => true;
        public static UCL_LoginStatusPage Create() => UCL_EditorPage.Create<UCL_LoginStatusPage>();

        // 區塊職責：Lock entry 結構 — 對齊 awakening.py write_lock() schema
        // 物理意義：session lock 一檔, 含 persona/agent/model/bank/lock 時間戳/session_key/pid/session_token
        public class LockEntry
        {
            public string Persona = "";
            public string Agent = "";
            public string Model = "";
            public string BankAccount = "";
            public string LockedAt = "";
            public string ExpiresAt = "";
            public string SessionKey = "";
            public int Pid = 0;
            public bool Expired = false;
            // T07 (2026-05-15 apex-two) — 32-hex UUID4 token 發於 morning ritual; 空 = T07 前建的 lock
            public string SessionToken = "";
        }

        // 區塊職責：Persona pool entry — 對齊 AwakenInit/personas/<name>.json
        public class PersonaEntry
        {
            public string Name = "";
            public string Agent = "";
            public string Status = "";   // online / offline
            public int WakeCount = 0;
            public string LayerRole = "";
            public string LastActive = "";
            public bool HasLock = false;
        }

        // 區塊職責：快取資料
        // 物理意義：locks 列當前活躍, pool 列 registry 全部 personas (offline + online)
        List<LockEntry> m_Locks = new List<LockEntry>();
        List<PersonaEntry> m_Pool = new List<PersonaEntry>();
        Dictionary<string, int> m_SameKeyCount = new Dictionary<string, int>();   // session_key → count (collision 偵測)

        // T07 (2026-05-15 apex-two) — Token enforce state cache
        // 物理意義：讀 _session/_token_enforce.json + _session/_tokens.json 當前狀態, 給 UI toggle 顯示
        bool m_TokenEnforce = false;
        int m_ActiveTokenCount = 0;
        int m_ExpiredTokenCount = 0;
        //Vector2 m_LocksScroll = Vector2.zero;
        //Vector2 m_PoolScroll = Vector2.zero;

        // 區塊職責：手動 login 表單 state
        // 物理意義：Tim 輸入 agent + persona 字串, 按 Morning 後 spawn process
        string m_LoginAgent = "";
        string m_LoginPersona = "";
        string m_LoginModel = "Opus 4.7 1M";
        bool m_LoginStrictPersona = true;
        bool m_LoginRebindAgent = false;
        string m_LoginForkName = "";

        // 區塊職責：goodnight letter body default — Tim 手動清 lock 時帶這個簡短 letter
        // 物理意義：避免 letter 為空導致 awakening 報錯; 內容標記「Editor 手動清」audit trail
        const string DEFAULT_MANUAL_LETTER = "Manual logout via UCL_LoginStatusPage (Editor IMGUI). 本筆非 agent 自決 goodnight, 不含 reframe content.";

        string m_AgentCommandsDir = "";
        string m_SessionDir = "";
        string m_PersonasDir = "";
        string m_UCLCorePath = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析
            // 物理意義：走 UCL_RepoPath.AgentCommandsDir 撈 _session 跟 AwakenInit/personas
            //          UCL_Core path 用來找 awakening.py 給 process spawn
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_SessionDir = Path.Combine(m_AgentCommandsDir, "_session");
            m_PersonasDir = Path.Combine(m_AgentCommandsDir, "AwakenInit", "personas");
            // 區塊：UCL_Core path 解析 — 走 UCL_EditorPath.CorePath (per AgentSkillManagerPage)
            string corePathRel = UCL_EditorPath.CorePath;
            if (!string.IsNullOrEmpty(corePathRel))
            {
                m_UCLCorePath = Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, corePathRel));
            }
            LoadData();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
        }

        // 區塊職責：載入並反序列化 lock + pool
        // 物理意義：scan _session/_persona_*.json + AwakenInit/personas/*.json
        // 數值影響：更新 m_Locks / m_Pool / m_SameKeyCount
        void LoadData()
        {
            m_Locks.Clear();
            m_Pool.Clear();
            m_SameKeyCount.Clear();

            // T07: enforce state + tokens summary
            m_TokenEnforce = false;
            m_ActiveTokenCount = 0;
            m_ExpiredTokenCount = 0;
            try
            {
                string enforcePath = Path.Combine(m_SessionDir, "_token_enforce.json");
                if (File.Exists(enforcePath))
                {
                    var jd = JsonData.ParseJson(File.ReadAllText(enforcePath));
                    if (jd != null && jd.IsObject && jd.Dic != null)
                        m_TokenEnforce = jd.GetBool("enforce", false);
                }
                string tokensPath = Path.Combine(m_SessionDir, "_tokens.json");
                if (File.Exists(tokensPath))
                {
                    var jd = JsonData.ParseJson(File.ReadAllText(tokensPath));
                    if (jd != null && jd.IsObject && jd.Dic != null
                        && jd.Dic.TryGetValue("tokens", out var tokensNode)
                        && tokensNode != null && tokensNode.IsObject && tokensNode.Dic != null)
                    {
                        foreach (var kv in tokensNode.Dic)
                        {
                            if (kv.Value == null || !kv.Value.IsObject) continue;
                            string status = kv.Value.GetString("status", "");
                            if (status == "active") m_ActiveTokenCount++;
                            else if (status == "expired") m_ExpiredTokenCount++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LoginStatus] T07 token state load failed: {e.Message}");
            }

            // 區塊：scan locks
            if (Directory.Exists(m_SessionDir))
            {
                foreach (var lockFile in Directory.GetFiles(m_SessionDir, "_persona_*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(lockFile);
                        var jd = JsonData.ParseJson(json);
                        if (!jd.IsObject || jd.Dic == null) continue;
                        var entry = new LockEntry
                        {
                            Persona = jd.GetString("persona", ""),
                            Agent = jd.GetString("agent", ""),
                            Model = jd.GetString("model", ""),
                            BankAccount = jd.GetString("bank_account", ""),
                            LockedAt = jd.GetString("locked_at", ""),
                            ExpiresAt = jd.GetString("expires_at", ""),
                            SessionKey = jd.GetString("session_key", ""),
                            Pid = jd.GetInt("pid", 0),
                            SessionToken = jd.GetString("session_token", ""),
                        };
                        // expires 判斷 — 用字串比對 ISO ts (lexicographic order)
                        entry.Expired = !string.IsNullOrEmpty(entry.ExpiresAt)
                                        && string.Compare(entry.ExpiresAt, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), StringComparison.Ordinal) < 0;
                        m_Locks.Add(entry);

                        // 區塊：同 session_key 計數 (collision 偵測)
                        if (!string.IsNullOrEmpty(entry.SessionKey))
                        {
                            m_SameKeyCount[entry.SessionKey] = m_SameKeyCount.GetValueOrDefault(entry.SessionKey, 0) + 1;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LoginStatus] parse lock {lockFile} failed: {e.Message}");
                    }
                }
            }

            // 區塊：scan persona pool
            if (Directory.Exists(m_PersonasDir))
            {
                var lockedPersonas = new HashSet<string>();
                foreach (var l in m_Locks) lockedPersonas.Add(l.Persona);

                foreach (var pf in Directory.GetFiles(m_PersonasDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(pf);
                    if (name.StartsWith("_") || name.StartsWith(".")) continue;
                    try
                    {
                        string json = File.ReadAllText(pf);
                        var jd = JsonData.ParseJson(json);
                        if (!jd.IsObject || jd.Dic == null) continue;
                        var entry = new PersonaEntry
                        {
                            Name = name,
                            Agent = jd.GetString("agent", ""),
                            Status = jd.GetString("status", ""),
                            WakeCount = jd.GetInt("wake_count", 0),
                            LayerRole = jd.GetString("layer_role", ""),
                            LastActive = jd.GetString("last_active", ""),
                            HasLock = lockedPersonas.Contains(name),
                        };
                        m_Pool.Add(entry);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LoginStatus] parse persona {pf} failed: {e.Message}");
                    }
                }
                // sort by wake_count desc
                m_Pool.Sort((a, b) => b.WakeCount.CompareTo(a.WakeCount));
            }
        }

        protected override void ContentOnGUI()
        {
            DrawCollisionBanner();
            DrawTokenEnforcePanel();
            GUILayout.Space(8);
            DrawActiveLocks();
            GUILayout.Space(12);
            DrawManualLogin();
            GUILayout.Space(12);
            DrawPersonaPool();
        }

        // 區塊職責：Token Enforce 後台開關 (T07, 2026-05-15 apex-two)
        // 物理意義：寫 _session/_token_enforce.json {"enforce": bool}.
        //          Cmd_Tavern.Op_Post 讀此檔判斷是否驗 token. 預設 OFF.
        // 數值影響：toggle 後立即 flush 到 disk, 下一次 op=post 即生效 (不必 reload).
        void DrawTokenEnforcePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.Title"), UCL_GUIStyle.LabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    bool newVal = GUILayout.Toggle(m_TokenEnforce,
                        m_TokenEnforce ? UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.On")
                                        : UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.Off"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    if (newVal != m_TokenEnforce)
                    {
                        m_TokenEnforce = newVal;
                        WriteTokenEnforce(newVal);
                    }
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.SummaryFmt"),
                                                  m_ActiveTokenCount, m_ExpiredTokenCount),
                                    UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }

        void WriteTokenEnforce(bool enabled)
        {
            try
            {
                Directory.CreateDirectory(m_SessionDir);
                string p = Path.Combine(m_SessionDir, "_token_enforce.json");
                string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
                string json = $"{{\n  \"enforce\": {(enabled ? "true" : "false")},\n  \"updated_at\": \"{ts}\"\n}}\n";
                File.WriteAllText(p, json);
                Debug.Log($"[LoginStatus:T07] token enforce → {(enabled ? "ON" : "OFF")} ({p})");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LoginStatus:T07] write enforce failed: {e.Message}");
            }
        }

        // 區塊職責：collision banner — 同 session_key 多 lock 警告
        // 物理意義：cwd-hash session_key 多 Claude IDE 同 cwd 會撞 (per session-key-collision-fix)
        void DrawCollisionBanner()
        {
            var collisions = new List<string>();
            foreach (var kv in m_SameKeyCount)
            {
                if (kv.Value >= 2) collisions.Add($"{kv.Key.Substring(0, Math.Min(32, kv.Key.Length))} (× {kv.Value})");
            }
            if (collisions.Count == 0) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Collision.Title"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Collision.Desc"), UCL_GUIStyle.LabelStyle);
                foreach (var c in collisions)
                {
                    GUILayout.Label($"  • {c}", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Collision.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：active locks 表 + per-row Logout 按鈕
        void DrawActiveLocks()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.Locks.HeaderFmt"), m_Locks.Count), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Locks.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Locks.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                //m_LocksScroll = GUILayout.BeginScrollView(m_LocksScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(220)));

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Space(UCL_GUIStyle.GetScaledSize(160));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Agent"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Bank"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Pid"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LockedAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.ExpiresAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.SessionKey"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Token"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    GUILayout.Label("", GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                }
                foreach (var l in m_Locks)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Logout"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            DoLogout(l.Persona, l.Agent);
                        }

                        string personaLabel = l.Expired ? string.Format(UCL_CodeLocalize.Get("LoginStatus.ExpiredFmt"), l.Persona) : l.Persona;
                        GUILayout.Label(personaLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label(l.Agent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                        GUILayout.Label(l.BankAccount, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label(l.Pid.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        GUILayout.Label(TruncTs(l.LockedAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        GUILayout.Label(TruncTs(l.ExpiresAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        GUILayout.Label(TruncKey(l.SessionKey), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));

                        // T07: session token 顯示 — 前 12 碼 + "…" 方便 Tim 肉眼確認; Copy 鈕拷全碼
                        using (new GUILayout.HorizontalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(220))))
                        {
                            if (string.IsNullOrEmpty(l.SessionToken))
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Token.None"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                            }
                            else
                            {
                                string displayToken = l.SessionToken.Length > 12
                                    ? l.SessionToken.Substring(0, 12) + "…"
                                    : l.SessionToken;
                                GUILayout.Label(displayToken, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                                if (GUILayout.Button(UCL_CodeLocalize.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                                {
                                    GUIUtility.systemCopyBuffer = l.SessionToken;
                                }
                            }
                        }

                        if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.ForceRm"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(75))))
                        {
                            DoForceRemove(l.Persona);
                        }
                    }
                }
                //GUILayout.EndScrollView();
            }
        }

        // 區塊職責：手動 login 表單
        // 物理意義：Tim 輸入 agent + persona, 觸發 awakening.py morning
        void DrawManualLogin()
        {
            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.ManualLogin.Title"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.Agent"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_LoginAgent = GUILayout.TextField(m_LoginAgent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_LoginPersona = GUILayout.TextField(m_LoginPersona, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.Model"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_LoginModel = GUILayout.TextField(m_LoginModel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    m_LoginStrictPersona = GUILayout.Toggle(m_LoginStrictPersona, "--strict-persona", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    m_LoginRebindAgent = GUILayout.Toggle(m_LoginRebindAgent, "--rebind-agent", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.ForkName"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_LoginForkName = GUILayout.TextField(m_LoginForkName, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Morning"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        DoMorning();
                    }
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Login.RejectHint"), UCL_GUIStyle.LabelStyle);
                }
            }
        }

        // 區塊職責：persona registry 池
        // 物理意義：列所有 personas (含 offline), 給 Tim 看 wake_count + status + 是否有 lock
        void DrawPersonaPool()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.Pool.HeaderFmt"), m_Pool.Count), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Pool.Count == 0)
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.Pool.EmptyFmt"), m_PersonasDir), UCL_GUIStyle.LabelStyle);
                    return;
                }
                //m_PoolScroll = GUILayout.BeginScrollView(m_PoolScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(240)));
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Status"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Agent"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Wake"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LayerRole"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LastActive"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                foreach (var p in m_Pool)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            GUIUtility.systemCopyBuffer = $"/ucl-morning {p.Agent} {p.Name}";
                        }
                        string status = p.HasLock ? $"<color=#66ff99>{p.Status} 🔒</color>" : p.Status;
                        GUILayout.Label(status, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        GUILayout.Label(p.Name, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        GUILayout.Label(p.Agent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        
                        GUILayout.Label(p.WakeCount.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        GUILayout.Label(TruncStr(p.LayerRole, 28), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                        GUILayout.Label(TruncTs(p.LastActive), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    }
                }
                //GUILayout.EndScrollView();
            }
        }

        // ==================== Process actions ====================

        // 區塊職責：spawn awakening.py morning
        // 物理意義：手動 login — Tim 輸入 agent/persona, 走 ritual 跟 CLI 等價
        void DoMorning()
        {
            if (string.IsNullOrWhiteSpace(m_LoginAgent) || string.IsNullOrWhiteSpace(m_LoginPersona))
            {
                Debug.LogWarning("[LoginStatus] agent 跟 persona 都不能空");
                return;
            }
            var args = new List<string>
            {
                $"\"{AwakeningPyPath()}\"", "morning",
                "--agent", m_LoginAgent.Trim(),
                "--model", string.IsNullOrWhiteSpace(m_LoginModel) ? "Opus 4.7 1M" : m_LoginModel.Trim(),
                "--persona", m_LoginPersona.Trim(),
            };
            if (m_LoginStrictPersona) args.Add("--strict-persona");
            if (m_LoginRebindAgent) args.Add("--rebind-agent");
            if (!string.IsNullOrWhiteSpace(m_LoginForkName))
            {
                args.Add("--fork-name");
                args.Add(m_LoginForkName.Trim());
            }
            RunAwakening(args, "morning");
            LoadData();
        }

        // 區塊職責：spawn awakening.py goodnight per persona
        // 物理意義：手動 logout — 帶最簡 letter body 走 ritual
        // 數值影響：persona status → offline, lock removed, letter 寫進 baton/letters/<actor>/<persona>/
        void DoLogout(string persona, string agent)
        {
            var args = new List<string>
            {
                $"\"{AwakeningPyPath()}\"", "goodnight",
                "--persona", persona,
                "--agent", agent,
                "--letter-body", $"\"{DEFAULT_MANUAL_LETTER}\"",
                "--perturbation", "0.02",
            };
            RunAwakening(args, $"goodnight {persona}");
            LoadData();
        }

        // 區塊職責：force remove lock file 直接刪 _persona_<X>.json
        // 物理意義：bug 卡死場景, awakening goodnight 跑不通也得清。**不寫 letter, 不改 persona status**.
        //          只刪 lock — 算最後 fallback. 跑完該手動 Refresh 看 persona pool status.
        // 數值影響：persona registry 的 status 可能仍 online (因為沒走 goodnight), Tim 之後得手動修正
        void DoForceRemove(string persona)
        {
            string lockPath = Path.Combine(m_SessionDir, $"_persona_{persona}.json");
            if (!File.Exists(lockPath))
            {
                Debug.LogWarning($"[LoginStatus] lock 不存在: {lockPath}");
                return;
            }
            var page = UCL.Core.Page.UCL_OptionPage.Create(UCL_CodeLocalize.Get("LoginStatus.Dialog.ForceRm.Title"),
                    string.Format(UCL_CodeLocalize.Get("LoginStatus.Dialog.ForceRm.BodyFmt"), lockPath),
                    new ButtonData(UCL_CodeLocalize.Get("LoginStatus.Btn.ConfirmRemove"), () =>
                    {
                        try
                        {
                            File.Delete(lockPath);
                            Debug.Log($"[LoginStatus] lock removed: {lockPath}");
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[LoginStatus] force remove failed: {e.Message}");
                        }
                        LoadData();
                    }, UCL.Core.UI.UCL_GUIStyle.GetButtonStyle(Color.red)),
                    new ButtonData(UCL_CodeLocalize.Get("Cancel"), () =>
                    {

                    }));

            //if (!EditorUtility.DisplayDialog(
            //    "Force Remove Lock",
            //    $"確定強制刪除 lock?\n\n{lockPath}\n\n注意: persona registry status 不會自動改成 offline, 需後續手動修正。建議優先用 Logout (走 goodnight ritual)。",
            //    "確定 ✂", "取消"))
            //{
            //    return;
            //}
            //try
            //{
            //    File.Delete(lockPath);
            //    Debug.Log($"[LoginStatus] lock removed: {lockPath}");
            //}
            //catch (Exception e)
            //{
            //    Debug.LogError($"[LoginStatus] force remove failed: {e.Message}");
            //}
            //LoadData();
        }

        void RunAwakening(List<string> args, string opLabel)
        {
            string scriptPath = AwakeningPyPath();
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[LoginStatus] awakening.py 不存在: {scriptPath}");
                return;
            }
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "python";
                    p.StartInfo.Arguments = string.Join(" ", args);
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                    p.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                    p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[LoginStatus:{opLabel}] stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[LoginStatus:{opLabel}] stderr:\n{stderr}");
                    if (p.ExitCode != 0)
                    {
                        Debug.LogError($"[LoginStatus:{opLabel}] awakening.py exit={p.ExitCode}");
                    }
                    else
                    {
                        Debug.Log($"[LoginStatus:{opLabel}] ✓ ritual 完成");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LoginStatus:{opLabel}] spawn failed: {e.Message}");
            }
        }

        string AwakeningPyPath()
        {
            return Path.Combine(m_UCLCorePath, "Tools~", "AgentCommands", "awakening.py");
        }

        // ==================== Helpers ====================

        static string TruncTs(string ts)
        {
            if (string.IsNullOrEmpty(ts)) return "";
            // 顯示 yyyy-MM-ddTHH:mm:ss (砍 ms + Z)
            return ts.Length > 19 ? ts.Substring(0, 19) : ts;
        }

        static string TruncKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length > 28 ? key.Substring(0, 28) + "…" : key;
        }

        static string TruncStr(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        }
    }
}
