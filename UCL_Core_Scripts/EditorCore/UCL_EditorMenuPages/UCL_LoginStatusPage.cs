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
    // RequiresConstantRepaint (B 修 summit 2026-06-14): RunAwakening 改背景非阻塞後,
    // 完成回呼走 EditorApplication.delayCall 重整資料; 常駐 repaint 讓「處理中」提示 + 完成後的
    // 列表更新即時反映, 不必等使用者滑鼠移動觸發 repaint。
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Init_Protocol.md")]
    public class UCL_LoginStatusPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("LoginStatus.Title");
        public override bool ShowInPageMenu => true;
        public override string SensitiveContentReason => "Contains sensitive login information";
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

        // 註：手動登出走 awakening.py goodnight --no-letter (Tim 2026-06-14 拍板不寫信) —
        //     原 DEFAULT_MANUAL_LETTER placeholder 已移除, 不再偽造心得信。

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


        /// <summary>
        /// 區塊職責：載入並反序列化 lock + pool
        /// 物理意義：scan _session/_persona_*.json + AwakenInit/personas/*.json
        /// 數值影響：更新 m_Locks / m_Pool / m_SameKeyCount
        /// </summary>
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
                // 區塊職責：對 Persona 池進行多級排序
                // 物理意義：第一優先級為 Status 為 "online" (不分大小寫) 的 Persona 排在最前面，第二優先級為 WakeCount 的降序排列，第三優先級為 Persona 名字的升序排列以保持確定性。
                // 數值影響：不修改資料庫，僅變更 UI 中資料的渲染順序，優先呈現目前活動在線的 Persona 以利觀察。
                m_Pool.Sort((a, b) =>
                {
                    // 偵測 a 的狀態是否等於 "online" (忽略大小寫差異)
                    bool aOnline = string.Equals(a.Status, "online", StringComparison.OrdinalIgnoreCase);
                    // 偵測 b 的狀態是否等於 "online" (忽略大小寫差異)
                    bool bOnline = string.Equals(b.Status, "online", StringComparison.OrdinalIgnoreCase);
                    
                    // 如果兩者的在線狀態不一致
                    if (aOnline != bOnline)
                    {
                        // 讓 online 狀態在前 (true 大於 false，藉由 bOnline 對 aOnline 進行比較來實現 true 優先排序)
                        return bOnline.CompareTo(aOnline);
                    }
                    
                    // 當在線狀態相同時，依據 WakeCount 醒來次數進行降序排序 (b 對 a)
                    int wakeCompare = b.WakeCount.CompareTo(a.WakeCount);
                    // 如果醒來次數不相等，直接返回次數比較結果
                    if (wakeCompare != 0) return wakeCompare;
                    
                    // 若前兩者均相等，則依據名字字串的 ASCII 順序進行升序排序，以提供唯一的穩定性排序結果
                    return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
            }
        }

        protected override void ContentOnGUI()
        {
            // T17 (2026-05-18 gura) — Inline guard 改走 UCL_ScreenStreamGuard 共用 static helper
            // 物理意義: 原 T13 散落的 IsScreenStreamRecording / TouchSensitiveFlag / DrawRecordingBlackout
            //          3 個 helper 集中到 UCL_ScreenStreamGuard.GuardPage, page 端 1 行守門
            // 設計取捨: 未來重構走 RCG event-driven 時換 caller 即可, 本層 code drop 數量最小
            if (UCL_ScreenStreamGuard.GuardPage(nameof(UCL_LoginStatusPage), SensitiveContentReason))
            {
                return;
            }

            // 區塊職責：背景 awakening.py 執行中提示 (B 修 summit 2026-06-14)
            // 物理意義：登入/登出改非阻塞後, Editor 不再凍結; 此提示告知使用者操作進行中, 完成後列表自動重整。
            if (m_AwakeningRunning)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label("⏳ awakening.py 執行中… (完成後自動重整, 期間請勿重複點擊)", UCL_GUIStyle.LabelStyle);
                }
            }

            DrawCollisionBanner();
            DrawTokenEnforcePanel();
            GUILayout.Space(8);
            DrawActiveLocks();
            GUILayout.Space(12);
            DrawManualLogin();
            GUILayout.Space(12);
            DrawPersonaPool();
        }

        // T13 ScreenStream Guard helpers 搬到 UCL_ScreenStreamGuard.cs (2026-05-18 gura T17 refactor)
        // 4 個 helpers (IsScreenStreamRecording / TouchSensitiveFlag / DrawRecordingBlackout / GetRepoRoot)
        // 統一在 UCL.Core.EditorLib.UCL_ScreenStreamGuard 共用. 加 WriteStopLock + DrawRecordingBlackout
        // 內嵌中斷直播按鈕 (寫 _stop.lock → daemon poll 偵測自動關閉).

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
                    
                    
                    
                    
                    
                    


                    GUILayout.Label("", GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                }
                // snapshot 迭代：DoLogout 內部呼叫 LoadData() 會清空 m_Locks，直接 foreach 原 list 會 throw Collection modified
                foreach (var l in m_Locks.ToArray())
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Logout"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            DoLogout(l);
                        }
#if UNITY_EDITOR
                        // 手動續期 (Tim 2026-07-15 拍板方案 C-3) — Tim 確認 session 還活著但 lock 已/將過期時
                        // 一鍵把 expires_at 推到 now+TTL，不必進 CLI。續期後 LoadData 重整讓「已過期」標記即時消失。
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Renew"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            bool renewed = UCL.Core.EditorLib.AgentCommands.ChatTavern.Cmd_Tavern.RenewPersonaLock(l.Persona);
                            Debug.Log(renewed
                                ? $"[LoginStatus] persona lock renewed: {l.Persona}"
                                : $"[LoginStatus] persona lock renew no-op（lock 不存在或缺 expires_at）: {l.Persona}");
                            LoadData();
                            GUIUtility.ExitGUI();
                        }
#endif
                        string personaLabel = l.Expired ? string.Format(UCL_CodeLocalize.Get("LoginStatus.ExpiredFmt"), l.Persona) : l.Persona;
                        using(new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                            GUILayout.Label(personaLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        }
                        
                        using(new GUILayout.VerticalScope()) 
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Agent"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                            GUILayout.Label(l.Agent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Bank"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                            GUILayout.Label(l.BankAccount, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Pid"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                            GUILayout.Label(l.Pid.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            var width = GUILayout.Width(UCL_GUIStyle.GetScaledSize(180));
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LockedAt"), UCL_GUIStyle.LabelStyle, width);
                            GUILayout.Label(TruncTs(l.LockedAt), UCL_GUIStyle.LabelStyle, width);
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.ExpiresAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                            GUILayout.Label(TruncTs(l.ExpiresAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.SessionKey"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                            GUILayout.Label(TruncKey(l.SessionKey), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Token"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                            // T07: session token 顯示 — 前 12 碼 + "…" 方便 Tim 肉眼確認; Copy 鈕拷全碼
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

        // 區塊職責：繪製 Persona 池的數值資訊
        // 物理意義：將硬碟中採樣出來的所有 Persona 的註冊狀態、角色設定及最後活動時間進行可視化排列顯示。
        // 數值影響：無修改，僅作排版視覺化呈現，方便開發者比對物理狀態。
        void DrawPersonaPool()
        {
            // 呼叫 GUILayout.Label 繪製 Persona 池的標題，包含目前池中總個數。
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.Pool.HeaderFmt"), m_Pool.Count), UCL_GUIStyle.LabelStyle);
            
            // 使用 VerticalScope 包裹整個 Persona 池的區域，並套用 "box" 樣式以利視覺區隔。
            using (new GUILayout.VerticalScope("box"))
            {
                // 判斷如果 Persona 池的資料筆數為零，則顯示空池提示並直接返回。
                if (m_Pool.Count == 0)
                {
                    // 呼叫 GUILayout.Label 顯示空池提示資訊，並帶入 Personas 目錄路徑。
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LoginStatus.Pool.EmptyFmt"), m_PersonasDir), UCL_GUIStyle.LabelStyle);
                    // 提早結束函數呼叫。
                    return;
                }
                
                // m_Pool 已經在載入時依 WakeCount 降序排列。
                // 迭代遍歷 m_Pool 串列中的每一個 PersonaEntry 項目，動態繪製每一列的資料。
                foreach (var p in m_Pool)
                {
                    // 為每一筆 PersonaEntry 項目建立一個水平排版區間，讓所有屬性欄位在一列內橫向排列。
                    using (new GUILayout.HorizontalScope())
                    {
                        // 繪製最左側的「複製」按鈕，寬度設為自動展開（依據文字寬度縮放）。
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            // 當按鈕被點擊時，將對應的 /ucl-morning 指令字串複製到系統剪貼簿中。
                            GUIUtility.systemCopyBuffer = $"/ucl-morning {p.Agent} {p.Name}";
                        }
                        
                        // 計算狀態文字：若該 Persona 當前被 Locked (HasLock 為真)，則加上綠色字體與鎖頭符號。
                        string status = p.HasLock ? $"<color=#66ff99>{p.Status} 🔒</color>" : p.Status;
                        
                        // 建立狀態 (Status) 欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「狀態」欄位的小標題，設定寬度比例為縮放後的 90 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Status"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                            // 繪製「狀態」的實際字串值，設定寬度比例為縮放後的 90 像素。
                            GUILayout.Label(status, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        }
                        
                        // 建立 Persona 名字欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「Persona」欄位的小標題，設定寬度比例為縮放後的 180 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                            // 繪製該 Persona 的名字，設定寬度比例為縮放後的 180 像素。
                            GUILayout.Label(p.Name, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        }
                        
                        // 建立 Agent (代理) 欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「Agent」欄位的小標題，設定寬度比例為縮放後的 120 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Agent"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                            // 繪製該 Persona 對應的 Agent 名稱，設定寬度比例為縮放後的 120 像素。
                            GUILayout.Label(p.Agent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        }
                        
                        // 建立 Wake (醒來次數) 欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「Wake」欄位的小標題，設定寬度比例為縮放後的 60 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Wake"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                            // 繪製該 Persona 的醒來次數，設定寬度比例為縮放後的 60 像素。
                            GUILayout.Label(p.WakeCount.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        }
                        
                        // 建立 LayerRole (角色擔當) 欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「LayerRole」欄位的小標題，設定寬度比例為縮放後的 240 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LayerRole"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                            // 繪製截短至 28 字元的角色擔當說明文字，設定寬度比例為縮放後的 240 像素。
                            GUILayout.Label(TruncStr(p.LayerRole, 28), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                        }
                        
                        // 建立 LastActive (最後活躍) 欄位的垂直排版區間，使欄位小標題與資料數值呈上下排版。
                        using (new GUILayout.VerticalScope())
                        {
                            // 繪製「LastActive」欄位的小標題，設定寬度比例為縮放後的 180 像素。
                            GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LastActive"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                            // 繪製截短後的最後活躍 UTC 時間戳，設定寬度比例為縮放後的 180 像素。
                            GUILayout.Label(TruncTs(p.LastActive), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        }
                        GUILayout.FlexibleSpace();
                    }
                }
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
            // RunAwakening 現為背景非阻塞 — 完成後自動回主線程 LoadData()，不再同步 reload
            RunAwakening(args, "morning");
        }

        // 區塊職責：彈窗確認後 spawn awakening.py goodnight per persona
        // 物理意義：手動 logout — destructive action (一按即走 goodnight: vector perturb / status→offline /
        //          lock 刪; --no-letter 不寫信), 為防誤按改為三按鈕 popup (Tim 2026-05-16 拍板, T07.4):
        //            (1) 取消                    → 完全 no-op
        //            (2) 不帶 Token 登出          → 顯式 --session-token "" (enforce ON 時 tavern 廣播會 reject,
        //                                          但主 ritual lock/perturb 仍跑 — 適合 token 過期 /
        //                                          lock 損毀的逃生路徑)
        //            (3) 自動帶正確 Token 登出 (推薦) → 不帶 --session-token (awakening.py auto-fallback 從
        //                                          lock.session_token 撈, enforce ON 也能正常廣播下線)
        // 數值影響：persona status → offline, lock removed; 手動登出走 --no-letter 不寫心得信 (Tim 2026-06-14)
        void DoLogout(LockEntry l)
        {
            string tokenPreview = string.IsNullOrEmpty(l.SessionToken)
                ? UCL_CodeLocalize.Get("LoginStatus.Token.None")
                : (l.SessionToken.Length > 12 ? l.SessionToken.Substring(0, 12) + "…" : l.SessionToken);
            string enforceStateLabel = m_TokenEnforce
                ? UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.On")
                : UCL_CodeLocalize.Get("LoginStatus.TokenEnforce.Off");
            string body = string.Format(UCL_CodeLocalize.Get("LoginStatus.Dialog.Logout.BodyFmt"),
                l.Persona, l.Agent, l.BankAccount, TruncTs(l.LockedAt), tokenPreview, enforceStateLabel);

            UCL.Core.Page.UCL_OptionPage.Create(
                string.Format(UCL_CodeLocalize.Get("LoginStatus.Dialog.Logout.TitleFmt"), l.Persona),
                body,
                new ButtonData(UCL_CodeLocalize.Get("Cancel"), () => { }),
                new ButtonData(UCL_CodeLocalize.Get("LoginStatus.Btn.LogoutNoToken"),
                    () => RunLogout(l.Persona, l.Agent, explicitNoToken: true),
                    UCL.Core.UI.UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.7f, 0.3f))),   // 橙: 警告但不致命
                new ButtonData(UCL_CodeLocalize.Get("LoginStatus.Btn.LogoutWithToken"),
                    () => RunLogout(l.Persona, l.Agent, explicitNoToken: false),
                    UCL.Core.UI.UCL_GUIStyle.GetButtonStyle(Color.red))                    // 紅: 推薦預設
            );
        }

        // 區塊職責：實際 spawn awakening.py goodnight subprocess
        // 物理意義：popup 三按鈕統一收斂到本 method, 差異只在 --session-token 帶法
        // explicitNoToken=true  → 帶 --session-token "" (caller 故意不帶, awakening.py 不從 lock 撈)
        // explicitNoToken=false → 完全省略 --session-token (awakening.py auto-fallback 從 lock.session_token 撈)
        void RunLogout(string persona, string agent, bool explicitNoToken)
        {
            // --no-letter (Tim 2026-06-14): 手動登出不寫信 — 登出常失敗但信在 ritual 最前面就寫了,
            //   累積一堆無意義 placeholder 信。手動登出是 cleanup, 不偽造心得信。
            var args = new List<string>
            {
                $"\"{AwakeningPyPath()}\"", "goodnight",
                "--persona", persona,
                "--agent", agent,
                "--no-letter",
                "--perturbation", "0.02",
            };
            if (explicitNoToken)
            {
                // shell escape: empty string 走 "\"\"" 雙引號對, 否則 string.Join 會吃掉
                args.Add("--session-token");
                args.Add("\"\"");
            }
            // else: 不加 --session-token, awakening.py 走 args.session_token is None 分支 → 自動撈 lock
            // RunAwakening 現為背景非阻塞 — 完成後自動回主線程 LoadData()，不再同步 reload
            RunAwakening(args, $"goodnight {persona}");
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

        // 區塊職責：標記 awakening.py 子行程是否正在背景執行 (B 修, summit 2026-06-14)
        // 物理意義：避免重複點擊同時 spawn 多個 ritual; ContentOnGUI 顯示「處理中」提示。
        //          主線程設 true (spawn 前) / delayCall 主線程回呼設 false (完成後) — 都在主線程, 無 race。
        bool m_AwakeningRunning = false;

        // 區塊職責：在「背景執行緒」spawn awakening.py，主線程保持空閒
        // 物理意義 (B 修, summit 2026-06-14 — Editor↔subprocess 重入死鎖根治):
        //   舊版主線程同步 p.WaitForExit 卡死 → 而 awakening.py 內 tavern_post 走 run_cmd.py 又要
        //   Editor 主線程去處理 queue trigger 才返回 → 兩邊互等死鎖, 撐到 30s timeout 才解開。
        //   (登入 morning 因 write_lock 先於 broadcast 而倖存; 登出 goodnight 的 remove_lock 在 broadcast
        //    之後被卡掉 → lock 不刪 → 「發了訊息卻卡在登入」。awakening.py 端已併行把 remove_lock 前移。)
        //   本層改丟 Task.Run 跑阻塞段, 主線程保持可服務 tavern trigger → 廣播正常完成、不再 30s 凍結。
        // 數值影響：完成後用 EditorApplication.delayCall 回主線程跑 LoadData() 重整 UI (背景緒不可碰 Unity GUI)。
        void RunAwakening(List<string> args, string opLabel)
        {
            string scriptPath = AwakeningPyPath();
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[LoginStatus] awakening.py 不存在: {scriptPath}");
                return;
            }
            if (m_AwakeningRunning)
            {
                Debug.LogWarning($"[LoginStatus] 已有 awakening 操作進行中 — 忽略 op={opLabel}, 等前一筆完成再操作");
                return;
            }
            // 主線程設旗標 — spawn 前; 完成後在 delayCall (同主線程) 解除, 無跨緒 race
            m_AwakeningRunning = true;
            // args 在主線程先 join 成字串, 避免背景緒讀共享 list
            string argLine = string.Join(" ", args);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 區塊：async stdout + stderr 同時讀取，避免 .NET Process redirect deadlock
                    // 物理意義：同步 ReadToEnd() 只讀一個 stream 時，若 child 寫另一個 stream 填滿 buffer
                    //          → child 卡在 write / caller 卡在 ReadToEnd → 永久 deadlock。
                    //          BeginOutputReadLine + BeginErrorReadLine 讓兩個 stream 非阻塞並行消費。
                    var stdoutSb = new System.Text.StringBuilder();
                    var stderrSb = new System.Text.StringBuilder();
                    using (var p = new Process())
                    {
                        p.StartInfo.FileName = "python";
                        p.StartInfo.Arguments = argLine;
                        p.StartInfo.UseShellExecute = false;
                        p.StartInfo.RedirectStandardOutput = true;
                        p.StartInfo.RedirectStandardError = true;
                        p.StartInfo.CreateNoWindow = true;
                        p.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                        p.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                        p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
                        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };
                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        // 主線程已不阻塞 → tavern handshake 能被 Editor 正常處理, 通常數秒內結束。
                        // 90s 純安全上限 (> run_cmd.py 預設 poll 窗口), 命中 = handshake 真的卡死。
                        bool exited = p.WaitForExit(90000);
                        string stdout = stdoutSb.ToString();
                        string stderr  = stderrSb.ToString();
                        if (!string.IsNullOrEmpty(stdout))
                            Debug.Log($"[LoginStatus:{opLabel}] stdout:\n{stdout}");
                        if (!string.IsNullOrEmpty(stderr))
                            Debug.LogWarning($"[LoginStatus:{opLabel}] stderr:\n{stderr}");
                        if (!exited)
                            Debug.LogError($"[LoginStatus:{opLabel}] awakening.py 90s 未結束 (timeout) — 疑 tavern handshake 卡住");
                        else if (p.ExitCode != 0)
                            Debug.LogError($"[LoginStatus:{opLabel}] awakening.py exit={p.ExitCode}");
                        else
                            Debug.Log($"[LoginStatus:{opLabel}] ✓ ritual 完成");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LoginStatus:{opLabel}] spawn failed: {e.Message}");
                }
                finally
                {
#if UNITY_EDITOR
                    // 回主線程: 背景緒不可碰 Unity GUI, 且 LoadData 須在 awakening.py 完成 (lock 已刪) 後才讀
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        m_AwakeningRunning = false;
                        LoadData();
                    };
#endif
                }
            });
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
