// 區塊職責: Session 管理頁 — UCL_SessionService 的 UI 入口，列出各種 session（FreeTime…）
//            每個 persona 的現況（進行中 / 已收工 / active 但過期的殘留），並提供補收工與開檔。
// 物理意義: 「誰現在在自由時間」「誰超時沒回來收工」的可視化與處置台。
//            超時殘留不是 cosmetic：那份檔的 active 還是 true，而 python 端判「在不在自由時間」
//            會先看 active，只靠 end_ts 過期才擋下來 —— 少一層防護就會叫人去 @ 一個下線的人。
// 2026-08-18 basecamp（配套 UCL_SessionService / Cmd_SessionStatus）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Session 管理頁 — 檢視 / 處置所有經 <see cref="UCL_SessionService"/> 納管的 session。
    /// 補收工走 service 的 Close（翻 active + 記原因 + 記時刻，三件一起），不手改檔。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_SessionAdminPage.md")]
    public class UCL_SessionAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Session 管理";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // 區塊職責: 顯示快取 —— 每 REFRESH_INTERVAL 秒重讀一次，不每 OnGUI 都掃檔。
        // 物理意義: session 檔由 Cmd 端寫入，頁面只是觀測窗；每幀重讀會在 session 多時明顯拖慢 IMGUI。
        // 數值影響: 2 秒的顯示延遲 —— 對「誰在自由時間」這種分鐘級的判斷不影響決策。
        readonly List<Row> m_Rows = new();
        double m_LastRefresh = -1.0;
        const double REFRESH_INTERVAL_SEC = 2.0;

        // 補收工二段確認（仿 UCL_ProcessAdminPage 的 kill arm）：第一次點 = arm，5 秒內再點同一列才真的寫。
        // 理由：補收工會改別人的 session 檔 —— 誤點的後果是把一場**真的在跑**的 session 關掉，
        //       而那個人不會收到通知，他只會在下一次 step=next 撞到「沒有進行中的 session」。
        string m_ArmedKey = "";
        double m_ArmedTime = -1.0;
        const double ARM_WINDOW_SEC = 5.0;

        struct Row
        {
            public string Kind;
            public string Persona;
            public UCL_SessionBase Session;
            public bool Running;
            public bool Stale;        // active=true 但已過 end_ts —— 超時未收工的殘留
        }

        public static UCL_SessionAdminPage Create()
        {
            var aPage = new UCL_SessionAdminPage();
            UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }

        void Refresh()
        {
            m_Rows.Clear();
            DateTime aNow = DateTime.Now;
            foreach (string aKind in UCL_SessionService.ScannedKinds())
            {
                foreach (string aPersona in UCL_SessionService.ListPersonas(aKind))
                {
                    var aS = UCL_SessionService.Peek(aKind, aPersona);
                    if (aS == null) continue;
                    bool aRunning = aS.IsRunningAt(aNow, out _);
                    m_Rows.Add(new Row
                    {
                        Kind = aKind,
                        Persona = aPersona,
                        Session = aS,
                        Running = aRunning,
                        Stale = !aRunning && aS.active,
                    });
                }
            }
            // 進行中排最前，殘留次之 —— 需要動手的東西不該要人自己找。
            m_Rows.Sort((a, b) =>
            {
                int aRank = a.Running ? 0 : a.Stale ? 1 : 2;
                int bRank = b.Running ? 0 : b.Stale ? 1 : 2;
                if (aRank != bRank) return aRank.CompareTo(bRank);
                int aKind = string.CompareOrdinal(a.Kind, b.Kind);
                return aKind != 0 ? aKind : string.CompareOrdinal(a.Persona, b.Persona);
            });
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 立即重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Refresh();
                m_LastRefresh = UnityEditor.EditorApplication.timeSinceStartup;
            }
            if (GUILayout.Button("📂 開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                try
                {
                    string aDir = UCL_SessionService.SessionsDir(UCL_SessionKind.FreeTime);
                    if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
                    UnityEditor.EditorUtility.RevealInFinder(aDir);
                }
                catch (Exception e) { Debug.LogWarning($"[UCL_SessionAdminPage] 開啟資料夾失敗: {e.Message}"); }
            }
        }
        protected override void ContentOnGUI()
        {
            double aNow = UnityEditor.EditorApplication.timeSinceStartup;
            if (aNow - m_LastRefresh > REFRESH_INTERVAL_SEC)
            {
                Refresh();
                m_LastRefresh = aNow;
            }

            GUILayout.Space(10);
            GUILayout.Label("🗂 Session 管理", new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold });
            GUILayout.Label("各種 session（自由時間…）的現況。「進行中」＝ active 且未過 end_ts；"
                          + "只看 active 會把超時沒回來收工的人算成在線，那種殘留在下面標成 ⚠。", WrapStyle);

            // ⚠ 這行是讀數的一部分，不是說明文字：清單為空的語意是「**已登記的種類**裡沒有」，
            //   不是「系統裡沒有任何 session」。沒印掃描範圍的空清單會被讀成後者。
            GUILayout.Label($"掃描範圍（已登記 kind）：{string.Join(" / ", UCL_SessionService.ScannedKinds())}"
                          + "　—— 未登記的種類不在其中（見 UCL_SessionKind.Kinds 註解）", WrapStyle);
            GUILayout.Space(6);

            if (m_Rows.Count == 0)
            {
                GUILayout.Label("（已登記的種類底下沒有任何 session 檔）",
                    new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic });
                return;
            }

            // arm 過期自動解除 —— 逾時仍記著會讓「五分鐘前點過一次」變成一鍵寫入。
            if (!string.IsNullOrEmpty(m_ArmedKey) && aNow - m_ArmedTime > ARM_WINDOW_SEC) m_ArmedKey = "";

            var aOldColor = GUI.backgroundColor;
            bool aDirty = false;
            foreach (var aRow in m_Rows)
            {
                GUI.backgroundColor = aRow.Running ? new Color(0.25f, 0.45f, 0.25f)
                    : aRow.Stale ? new Color(0.6f, 0.35f, 0.1f)
                    : new Color(0.35f, 0.35f, 0.35f);
                using (new GUILayout.VerticalScope(GUI.skin.box))
                {
                    GUI.backgroundColor = aOldColor;
                    var aS = aRow.Session;
                    aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
                    string aState = aRow.Running ? "🟢 進行中" : aRow.Stale ? "⚠ 殘留（active 但已過期）" : "⚪ 已收工";

                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"<b>{aRow.Persona}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label(aRow.Kind, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(110)));
                        GUILayout.Label(aState, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(190)));
                        if (aRow.Running && aEnd.HasValue)
                        {
                            int aRemain = (int)Math.Max(0, (aEnd.Value - DateTime.Now).TotalMinutes);
                            GUILayout.Label($"剩 {aRemain} 分", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        }
                        GUILayout.FlexibleSpace();

                        // 補收工只對「殘留」開放：進行中的場要收工請走 Cmd（step=end，會發收工宣告、
                        // 結算免費像素）——從後台直接關會跳過那些結算，留下對不上的帳。
                        if (aRow.Stale)
                        {
                            string aKey = aRow.Kind + "/" + aRow.Persona;
                            bool aArmed = m_ArmedKey == aKey;
                            if (GUILayout.Button(aArmed ? "⚠ 再按一次確認補收工" : "🧹 補收工",
                                    UCL_GUIStyle.GetButtonStyle(aArmed ? Color.red : new Color(1f, 0.8f, 0.3f)),
                                    GUILayout.ExpandWidth(false)))
                            {
                                if (aArmed)
                                {
                                    UCL_SessionService.Close(aRow.Kind, aRow.Persona, aS,
                                        "expired-closed-by-admin-page");
                                    Debug.Log($"[UCL_SessionAdminPage] 補收工 {aKey}（原 end_ts={aS.end_ts}）");
                                    m_ArmedKey = "";
                                    aDirty = true;
                                }
                                else
                                {
                                    m_ArmedKey = aKey;
                                    m_ArmedTime = aNow;
                                }
                            }
                        }
                        if (GUILayout.Button("📄 開啟檔案", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            UnityEditor.EditorUtility.RevealInFinder(
                                UCL_SessionService.SessionPath(aRow.Kind, aRow.Persona));
                        }
                    }
                    GUILayout.Label($"　session_id: {aS.session_id}", SmallStyle);
                    GUILayout.Label($"　開場 {aS.start_ts}　預定收工 {aS.until_local}（end_ts {aS.end_ts}）", SmallStyle);
                    if (!string.IsNullOrEmpty(aS.end_reason) || !string.IsNullOrEmpty(aS.ended_at))
                    {
                        GUILayout.Label($"　收工 {aS.ended_at}　reason: {(string.IsNullOrEmpty(aS.end_reason) ? "（未記）" : aS.end_reason)}", SmallStyle);
                    }
                }
            }
            GUI.backgroundColor = aOldColor;
            if (aDirty) Refresh();
        }

        // WrapStyle 不是基底成員 —— 各頁自己定（UCL_GUIStyle.LabelStyle 預設不換行，
        // 長說明會把 box 撐寬到把右側按鈕推出視窗）。
        GUIStyle m_WrapStyle;
        GUIStyle WrapStyle
        {
            get
            {
                if (m_WrapStyle == null)
                    m_WrapStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
                return m_WrapStyle;
            }
        }

        GUIStyle m_SmallStyle;
        GUIStyle SmallStyle
        {
            get
            {
                if (m_SmallStyle == null)
                    m_SmallStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return m_SmallStyle;
            }
        }
    }
}
#endif
