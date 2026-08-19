using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;                      // logout 走 Cmd_GoodNight in-process（UniTaskVoid/Forget）
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.AgentCommands.Awakening;   // UCL_AwakeningService（morning 已遷 C#，Cmd_GoodMorning 同一份實作）
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
    // 說明連結改指本頁自己的文件（Tim 2026-08-17）。
    // 舊值 `Plan/Plan_Awakening_Init_Protocol.md` 有兩個問題：
    //   ① 那份檔**整個 UCL_Core 都不存在**（死連結，按了沒反應）
    //   ② 即使存在，Plan 是設計沿革不是操作說明 —— 使用者按「說明」要的是「這頁怎麼用」
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_LoginStatusPage.md")]
    public class UCL_LoginStatusPage : UCL_CommonEditorPage
    {
        // Process 註冊中心的 tag（硬規則：每顆外部 Process 都要登記）。
        const string PROC_TAG_PY = "loginstatus_py";

        public override string WindowName => UCL_CodeLocalize.Get("LoginStatus.Title");
        public override bool ShowInPageMenu => true;
        public override string SensitiveContentReason => "Contains sensitive login information";
        // 區塊職責：Lock entry 結構 — 對齊 awakening.py write_lock() schema
        // 物理意義：session lock 一檔, 含 persona/agent/model/bank/lock 時間戳/session_key/pid/session_token
        public class LockEntry
        {
            public string Persona = "";
            public string Agent = "";
            public string ActualAgent = "";
            public string Model = "";
            public string BankAccount = "";
            public string LockedAt = "";
            public string SessionKey = "";
            public int Pid = 0;
            // T07 (2026-05-15 apex-two) — 32-hex UUID4 token 發於 morning ritual; 空 = T07 前建的 lock
            public string SessionToken = "";
            // §8.5 now_status —— 「這個人現在在做什麼」。StatusAge / StatusStale 由
            // UCL_PersonaLockInfo 換算（顯示端不自己算，否則後台與 catchup 會對同一個時間戳各講一套）。
            public string NowStatus = "";
            public string StatusAge = "";
            public bool StatusStale = false;
        }

        // 區塊職責：Persona pool entry — 對齊 AwakenInit/personas/<name>.json
        public class PersonaEntry
        {
            public string Name = "";
            public string Agent = "";
            public string ActualAgent = "";
            public string Status = "";   // online / offline
            public int WakeCount = 0;
            public string LayerRole = "";
            public string LastActive = "";
            public bool HasLock = false;
        }

        // 區塊職責：快取資料
        // 物理意義：locks 列當前活躍, pool 列 registry 全部 personas (offline + online)
        List<LockEntry> m_Locks = new List<LockEntry>();
        readonly Dictionary<string, UCL_ActualAgent> m_ActualAgentDrafts = new Dictionary<string, UCL_ActualAgent>();
        readonly UCL_ObjectDictionary m_ActualAgentPopupDic = new UCL_ObjectDictionary();
        // 每個 lock 的「詳細」折疊狀態（key = persona）—— 走 UCL 封裝的 Toggle，
        // 狀態存 UCL_ObjectDictionary 而不是自己開 Dictionary<string,bool>（沿 AgentSkillManagerPage 慣例）。
        readonly UCL_ObjectDictionary m_LockFoldDic = new UCL_ObjectDictionary();
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
        string m_LoginPersona = "";
        string m_LoginModel = "Opus 4.7 1M";
        // m_LoginForkName 已移除（2026-08-13 R11/R14）：fork 走後台「🧬 Persona & Agent 管理頁」

        // 註：手動登出走 awakening.py goodnight --no-letter (Tim 2026-06-14 拍板不寫信) —
        //     原 DEFAULT_MANUAL_LETTER placeholder 已移除, 不再偽造心得信。

        string m_AgentCommandsDir = "";
        string m_SessionDir = "";
        string m_PersonasDir = "";
        // persona 信件庫根目錄（letters/<persona>/…）。與 UCL_PersonaInspectorPage 的 m_LettersDir 同一個位置，
        // 兩邊都從 UCL_RepoPath.AgentCommandsDir 推導 —— 不寫死安裝路徑（AgentCommands 本身可能是 submodule）。
        string m_LettersDir = "";
        string m_UCLCorePath = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析
            // 物理意義：走 UCL_RepoPath.AgentCommandsDir 撈 _session 跟 AwakenInit/personas
            //          UCL_Core path 用來找 awakening.py 給 process spawn
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_SessionDir = Path.Combine(m_AgentCommandsDir, "_session");
            // ⚠ persona 目錄改走單一解析點（UCL_AwakeningService.PersonasDir → UCL_AgentCommandsPath.DataRoot）。
            //   本行原本走 UCL_RepoPath.AgentCommandsDir（canonical, 不搬），而 persona 檔屬於
            //   「持久狀態資料」，依 UCL_AgentCommandsPath 的類別契約該走可 override 的 DataRoot。
            //   ⇒ 設了 DataRoot override 的機器上，本頁與 Cmd_LoginStatus **原本讀不同目錄**，
            //     而兩邊都不會報錯（頁面顯示一組、Cmd 操作另一組）。預設模式下兩者逐字相同。
            m_PersonasDir = AgentCommands.Awakening.UCL_AwakeningService.PersonasDir;
            // letters 走唯一解析點（BUG-2）—— 本頁原本自己拼 `ChatTavern/baton/letters`，
            // 等於把佈局知識複製一份；佈局調整時它不會跟著改，而且**不會報錯**。
            m_LettersDir = UCL_LettersPath.Root;
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
#if UNITY_EDITOR
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                OpenLettersFolder();
            }
#endif
        }


        // 區塊職責：開啟 persona 信件庫資料夾
        // 物理意義：AgentCommands/ChatTavern/baton/letters —— 每個 persona 一個子夾，
        //          裡面是收尾信 / _keys_open.md / longterm/ / portraits/ 等。
        //          路徑走 UCL_RepoPath.AgentCommandsDir（git-root walk）推導，不寫死安裝路徑：
        //          AgentCommands 在部分專案是 submodule，寫死的路徑跨專案會靜默失效。
        // 數值影響：純讀 + spawn 檔案管理器，不改任何檔。
        //
        // 為什麼開的是 letters 根而不是某個 persona 的子夾：
        //   本頁沒有「當前選中 persona」這個狀態 —— m_LoginPersona 是手動登入表單的輸入欄，
        //   拿它當「要開哪個夾」會讓同一個字串身兼兩種語意，按下去的結果無法從按鈕字面預測。
        //   要看單一 persona 的信件走 Persona Inspector 頁（那裡有選中狀態）。
        void OpenLettersFolder()
        {
            if (string.IsNullOrEmpty(m_LettersDir))
            {
                // 空字串 = Init 沒跑或 AgentCommandsDir 解析失敗；這種要叫出來，
                // 否則按鈕按了沒反應會被當成 UI 壞掉，而真正的病在路徑解析。
                Debug.LogWarning("[LoginStatus] letters 路徑未解析（Init 未執行？）—— 請按 Refresh 或重開本頁。");
                return;
            }
            UCL_ExplorerUtil.Open(m_LettersDir, "LoginStatus");
        }

        /// <summary>
        /// 區塊職責：載入並反序列化 lock + pool
        /// 物理意義：scan _session/_persona_*.json + AwakenInit/personas/*.json
        /// 數值影響：更新 m_Locks / m_Pool / m_SameKeyCount
        /// </summary>
        void LoadData()
        {
            m_Locks.Clear();
            m_ActualAgentDrafts.Clear();
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

            // 區塊：scan locks —— 走 UCL_ActivePersonaLocks 唯一掃描實作（本頁要含過期視圖，供人手動清）
            foreach (var l in UCL_ActivePersonaLocks.ListLocks())
            {
                var entry = new LockEntry
                {
                    Persona = l.Persona,
                    Agent = l.Agent,
                    ActualAgent = l.ActualAgentRaw,
                    Model = l.Model,
                    BankAccount = l.BankAccount,
                    LockedAt = l.LockedAt,
                    SessionKey = l.SessionKey,
                    Pid = l.Pid,
                    SessionToken = l.RawSessionToken,
                    NowStatus = l.NowStatus,
                    StatusAge = l.StatusAgeText,
                    StatusStale = l.IsStatusStale,
                };
                m_Locks.Add(entry);
                m_ActualAgentDrafts[entry.Persona] = l.ActualAgent;

                // 區塊：同 session_key 計數 (collision 偵測)
                if (!string.IsNullOrEmpty(entry.SessionKey))
                {
                    m_SameKeyCount[entry.SessionKey] = m_SameKeyCount.GetValueOrDefault(entry.SessionKey, 0) + 1;
                }
            }

            // 區塊：scan persona pool —— 走 UCL_PersonaProfile 唯一讀取入口（Phase 0 接縫）
            {
                var lockedPersonas = new HashSet<string>();
                foreach (var l in m_Locks) lockedPersonas.Add(l.Persona);

                foreach (var name in UCL_PersonaProfile.PoolNamesSorted())
                {
                    var jd = UCL_PersonaProfile.GetRaw(name);
                    if (jd == null) continue;   // 壞檔接縫已警告
                    m_Pool.Add(new PersonaEntry
                    {
                        Name = name,
                        Agent = jd.GetString("agent", ""),
                        ActualAgent = jd.GetString("actual_agent", ""),
                        Status = jd.GetString("status", ""),
                        WakeCount = jd.GetInt("wake_count", 0),
                        LayerRole = jd.GetString("layer_role", ""),
                        LastActive = jd.GetString("last_active", ""),
                        HasLock = lockedPersonas.Contains(name),
                    });
                }
                // 區塊職責：對 Persona 池進行多級排序
                // 物理意義：第一優先級為「持有 lock」的 Persona 排最前，第二優先級為 WakeCount 降序，第三優先級為名字升序以保持確定性。
                // 數值影響：不修改資料庫，僅變更 UI 渲染順序。
                //   ⚠ 判準用 HasLock 不是 registry 的 status 欄 (Tim 2026-07-31 回報)：
                //   status 是快取，登出流程沒走完就會停在 "online"（實測 zenith-one：status=online 但無 lock），
                //   於是上方「活躍 Lock」清單與下方 Persona 池對同一個人給出兩種答案。
                //   lock 檔的存在與否是既成事實，快取不是 —— 一律以 lock 為準。
                m_Pool.Sort((a, b) =>
                {
                    bool aOnline = a.HasLock;
                    bool bOnline = b.HasLock;

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
            // 設計取捨: 未來重構走 event-driven 時換 caller 即可, 本層 code drop 數量最小
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

                // snapshot 迭代：DoLogout 內部呼叫 LoadData() 會清空 m_Locks，直接 foreach 原 list 會 throw Collection modified
                foreach (var l in m_Locks.ToArray())
                {
                    // ===========================================================
                    // 區塊職責：一個 lock 的顯示 —— **摘要一行 ＋ 詳細折疊**（Tim 2026-08-19 指定）。
                    // 物理意義：舊版把 8 個欄位橫著攤成「標題在上、值在下」的兩行 ×8 欄，
                    //          每個 lock 佔兩行、寬度爆出視窗，而**最想知道的那件事
                    //          （這個人現在在做什麼）根本沒顯示** —— now_status 欄自 §8.5 就存在 lock 裡，
                    //          catchup／ding 都印，只有這頁沒接。
                    //          ⇒ 摘要行只留身分三格（persona／agent／帳戶）＋ now_status；
                    //            排錯用的欄（實際 Agent／pid／鎖定時間／session_key／token）收進折疊。
                    // 數值影響：折疊狀態存 m_LockFoldDic（key=persona），收合時不繪製 body。
                    // ===========================================================
                    using (new GUILayout.VerticalScope("box"))
                    {
                        bool aExpanded;
                        using (new GUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Logout"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                            {
                                DoLogout(l);
                            }
                            // 續期按鈕已隨過期機制移除（Tim 2026-08-19）—— lock 生命週期由 goodnight/logout 顯式刪檔決定
                            aExpanded = UCL_GUILayout.Toggle(m_LockFoldDic, l.Persona, 18);

                            GUILayout.Label($"<b>{l.Persona}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                            GUILayout.Label(l.Agent, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                            GUILayout.Label($"🏦 {l.BankAccount}", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));

                            // now_status —— §8.5 的消費端。三態刻意長得不一樣：
                            //   有且新 → 💬 一句話（多久前）／有但過舊 → ⚠（§8.5：過舊的狀態比沒有狀態更會誤導）／
                            //   沒有 → 明講「沒設定」，不留空白（空白會被讀成「讀取失敗」）。
                            if (string.IsNullOrEmpty(l.NowStatus))
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.NowStatus.None"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
                            }
                            else
                            {
                                string aAge = string.IsNullOrEmpty(l.StatusAge) ? "" : $"（{l.StatusAge}）";
                                string aMark = l.StatusStale ? "⚠" : "💬";
                                GUILayout.Label($"{aMark} {l.NowStatus}{aAge}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
                            }

                            if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.ForceRm"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(75))))
                            {
                                DoForceRemove(l.Persona);
                            }
                        }

                        if (!aExpanded) continue;

                        using (new GUILayout.VerticalScope("box"))
                        {
                            // 區塊職責：編輯實際承載 agent；它只影響 remote routing / 下次 morning 的 --agent，不動顯示歸屬或 bank。
                            // 物理意義：同一 persona 可由不同桌面平台承載，而帳務與酒館顯示仍必須維持原本綁定。
                            // 數值影響：套用同時寫 active lock 與 persona registry 的 actual_agent，下一次 morning 可帶入同一 agent。
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("實際 Agent", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                                UCL_ActualAgent current = m_ActualAgentDrafts.TryGetValue(l.Persona, out var draft) ? draft : UCL_ActualAgentUtility.ParseOrNone(l.ActualAgent);
                                current = UCL_GUILayout.PopupAuto(current, m_ActualAgentPopupDic.GetSubDic(l.Persona), "ActualAgent", 6, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                                m_ActualAgentDrafts[l.Persona] = current;
                                if (GUILayout.Button("套用", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)))) ApplyActualAgent(l.Persona, current);
                                GUILayout.FlexibleSpace();
                            }
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Pid"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                                GUILayout.Label(l.Pid.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                                GUILayout.FlexibleSpace();
                            }
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.LockedAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                                GUILayout.Label(TruncTs(l.LockedAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                                GUILayout.FlexibleSpace();
                            }
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.SessionKey"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                                GUILayout.Label(TruncKey(l.SessionKey), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                                GUILayout.FlexibleSpace();
                            }
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Col.Token"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
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
                                GUILayout.FlexibleSpace();
                            }
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
                    // agent 欄位已移除（Tim 2026-07-31）：agent 由 persona 綁定反推，
                    // 留一個可自由打字的 agent 欄等於留一條「宣稱錯身分」的路。換綁走 Persona & Agent 管理頁。
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.Persona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_LoginPersona = GUILayout.TextField(m_LoginPersona, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LoginStatus.Field.Model"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_LoginModel = GUILayout.TextField(m_LoginModel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                }
                // fork 欄位已移除（2026-08-13 R11/R14）：morning 遷 C# 後 fork 不隨 wake 走 ——
                // 開分身走後台「🧬 Persona & Agent 管理頁」（建 persona 可選 fork 來源），建完再正常登入。
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
                            // 只複製 persona（Tim 2026-07-31）：agent 由 persona 綁定反推，
                            // 帶 agent 反而讓 caller 有機會宣稱一個跟綁定不符的身分。
                            GUIUtility.systemCopyBuffer = $"/ucl-morning {p.Name}";
                        }
                        
                        // 計算狀態文字：**一律由 lock 判定**，不顯示 registry 的 status 快取欄。
                        //   舊版直接印 p.Status，於是登出沒走完的 persona 會停在 "online"
                        //   （zenith-one 實測），跟上方「活躍 Lock」清單自相矛盾。
                        //   快取與 lock 不一致時附註一行 —— 修不修是另一回事，但**不准靜默**：
                        //   這種漂移沒人喊就會一直在（今天的 wake_count 事件同一個病）。
                        bool statusDrift = p.HasLock
                            != string.Equals(p.Status, "online", StringComparison.OrdinalIgnoreCase);
                        string status = p.HasLock
                            ? "<color=#66ff99>online 🔒</color>"
                            : "offline";
                        if (statusDrift)
                            status += $" <color=#ffcc66>(registry 快取: {p.Status})</color>";
                        
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

        // 區塊職責：手動 login — 走 C# UCL_AwakeningService（與 Cmd_GoodMorning 同一份實作），不再 spawn python
        // 物理意義：morning 已遷移 C#（Plan_Awakening_Flow_Simplification R14-R18，2026-08-13），
        //          awakening.py morning 現為指路 stub。頁面登入 = step=wake + step=brief 兩步代跑；
        //          step=intro（上線自介）屬 persona 親筆內容，**不代發** —— 本人上線後自己跑。
        // 數值影響：StepWake 寫 registry/lock/token/memo（原子）；RunBrief spawn python 生成 brief。
        //          報告走 Debug.Log（與 RunAwakening 同慣例）；完成後回主線程 LoadData()。
        void DoMorning()
        {
            if (string.IsNullOrWhiteSpace(m_LoginPersona))
            {
                Debug.LogWarning("[LoginStatus] persona 不能空");
                return;
            }
            if (m_AwakeningRunning)
            {
                Debug.LogWarning("[LoginStatus] 已有 awakening 操作進行中 — 等前一筆完成再操作");
                return;
            }
            string aPersona = m_LoginPersona.Trim();
            string aModel = string.IsNullOrWhiteSpace(m_LoginModel) ? "" : m_LoginModel.Trim();
            var aEntry = m_Pool.Find(p => p.Name == aPersona);
            var aActual = UCL_ActualAgentUtility.ParseOrNone(aEntry?.ActualAgent ?? "");
            if (aActual == UCL_ActualAgent.None) aActual = UCL_ActualAgentUtility.ParseOrNone(aEntry?.Agent ?? "");
            string aActualStr = aActual != UCL_ActualAgent.None ? UCL_ActualAgentUtility.ToStorageValue(aActual) : "";
            // 主執行緒先解析路徑＋暖快取（CorePath 走 AssetDatabase、DataRoot 走 PlayerPrefs，皆 main-thread 資源）
            string aScript = UCL_AwakeningService.ResolveAwakeningScriptPath();
            string aWarmLetters = UCL_LettersPath.Root;
            m_AwakeningRunning = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var aWake = UCL_AwakeningService.StepWake(aPersona, aModel, aActualStr, "login-page");
                    if (aWake.ok)
                    {
                        Debug.Log($"[LoginStatus:morning] step=wake 完成:\n{aWake.report}");
                        var aBrief = UCL_AwakeningService.RunBrief(aPersona, nameof(UCL_LoginStatusPage), 120000, aScript);
                        if (aBrief.ok)
                            Debug.Log($"[LoginStatus:morning] ✓ wake+brief 完成:\n{aBrief.report}\n"
                                      + "上線自介（step=intro）屬本人親筆，請該 persona 自己跑 run_cmd GoodMorning step=intro。");
                        else
                            Debug.LogError($"[LoginStatus:morning] brief 生成失敗（wake 已完成，登入有效）:\n{aBrief.report}");
                    }
                    else
                    {
                        Debug.LogError($"[LoginStatus:morning] step=wake {(aWake.blocked ? "blocked" : "失敗")}:\n{aWake.report}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LoginStatus:morning] 例外: {e}");
                }
                finally
                {
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        m_AwakeningRunning = false;
                        LoadData();
                    };
#endif
                }
            });
        }

        // 區塊職責：套用 active persona 的實際承載 agent，將同一值寫入 lock（當前在線事實）與 persona registry（下次早安預設）。
        // 物理意義：顯示 agent / bank 是另兩條身份資料，這裡絕不改它們；只給遠端切換決定要帶哪個桌面 agent。
        // 數值影響：使用 UTF-8 原子替換兩個 JSON；任一檔不存在即停止並保留另一個檔不變，避免造成半套設定。
        void ApplyActualAgent(string persona, UCL_ActualAgent actualAgent)
        {
            string value = UCL_ActualAgentUtility.ToStorageValue(actualAgent);
            if (string.IsNullOrEmpty(value))
            {
                Debug.LogWarning("[LoginStatus] 請先選擇實際 Agent 再套用");
                return;
            }
            string lockPath = Path.Combine(m_SessionDir, $"_persona_{persona}.json");
            string personaPath = Path.Combine(m_PersonasDir, persona + ".json");
            if (!File.Exists(lockPath) || !File.Exists(personaPath))
            {
                Debug.LogWarning($"[LoginStatus] 套用實際 Agent 失敗：lock 或 persona 檔不存在 ({persona})");
                return;
            }
            try
            {
                var lockData = JsonData.ParseJson(File.ReadAllText(lockPath));
                var personaData = JsonData.ParseJson(File.ReadAllText(personaPath));
                lockData["actual_agent"] = new JsonData(value);
                personaData["actual_agent"] = new JsonData(value);
                AtomicWriteUtf8(lockPath, lockData.ToJsonBeautify());
                AtomicWriteUtf8(personaPath, personaData.ToJsonBeautify());
                Debug.Log($"[LoginStatus] {persona} actual_agent → {value}（顯示 Agent / bank 未變）");
                LoadData();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[LoginStatus] 套用實際 Agent 失敗：{exception.Message}");
            }
        }

        static void AtomicWriteUtf8(string path, string content)
        {
            string tempPath = path + ".actual-agent.tmp";
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
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

        // 區塊職責：登出 — 走 Cmd_GoodNight step=logout（in-process，2026-08-13 Tim 拍板：
        //          登出透過 CMD、可單獨跑、persona 顯式必填；不再 spawn awakening.py goodnight）
        // 物理意義：logout = 不寫信的 cleanup（不偽造心得信，廣播標明未留信），與晚安全流程解耦。
        // explicitNoToken=true → 帶 no_token=true（顯式不帶 token，enforce reject path 除錯，三態語意沿用）
        void RunLogout(string persona, string agent, bool explicitNoToken)
        {
            if (m_AwakeningRunning)
            {
                Debug.LogWarning("[LoginStatus] 已有 awakening 操作進行中 — 等前一筆完成再操作");
                return;
            }
            m_AwakeningRunning = true;
            var aArgs = new Dictionary<string, string> { { "step", "logout" }, { "persona", persona } };
            if (explicitNoToken) aArgs["no_token"] = "true";
            RunLogoutAsync(aArgs, persona).Forget();
        }

        async Cysharp.Threading.Tasks.UniTaskVoid RunLogoutAsync(Dictionary<string, string> iArgs, string iPersona)
        {
            try
            {
                await new Cmd_GoodNight().ExecuteAsync(iArgs, System.Threading.CancellationToken.None);
                Debug.Log($"[LoginStatus] ✓ logout {iPersona} 完成（詳見 letters/{iPersona}/cmd/goodnight_logout.md）");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LoginStatus] logout {iPersona} 失敗: {e.Message}");
            }
            finally
            {
                m_AwakeningRunning = false;
                LoadData();
            }
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
                    // 硬規則：每顆外部 Process 都要登記（Coding_Standards.md「外部 Process」）。
                    // using 宣告 → 正常結束與例外路徑都會反登記，成對性由語言保證。
                    using var procScope_ = UCL_ProcessRegistryService.RegisterScope(
                        p, PROC_TAG_PY, "awakening / login 系列 python 工具", nameof(UCL_LoginStatusPage));
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
