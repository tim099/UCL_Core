using System;
using System.IO;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    // 區塊職責：ScreenStream 錄影守門共用 helper — 集中「敏感 page 防錄」三件套, 避免散落各 page 重抄
    // 物理意義：讀寫 AgentCommands/_screenstream/ 下的三個 control file:
    //          - _config.json       (daemon enabled state, 讀)
    //          - _sensitive.flag    (敏感 page 在 foreground 時觸發, daemon 看到→該 frame 黑屏)
    //          - _stop.lock         (Editor 端按「中斷直播」按鈕寫入, daemon 看到→自動 enabled=false + 刪 lock)
    // 設計取捨 (Tim 2026-05-18 拍板 — 短期整合):
    //          1. 整合前: 散落在每個敏感 page 各抄 3 個 helper (現只 LoginStatusPage, 但未來 Token/Bank/Relationship page 都會敏感)
    //          2. 整合後: 共用 static class, page 端 1 行 call. 之後重構走 RCG_ScreenStreamPage event-driven 時換 caller 即可
    //          3. 仍放 UCL_Core (還沒做 cross-project 純化, 屬待重構)— 但至少 helper 集中一個地方
    // T17 — gura ship 2026-05-18 (跟 UCL_LoginStatusPage T13 inline helpers 重構配對)
    public static class UCL_ScreenStreamGuard
    {
        // 路徑常數 — daemon (screenstream_daemon.py) 跟 page 端共用 contract
        const string CONFIG_RELATIVE = "AgentCommands/_screenstream/_config.json";
        const string SENSITIVE_FLAG_RELATIVE = "AgentCommands/_screenstream/_sensitive.flag";
        const string STOP_LOCK_RELATIVE = "AgentCommands/_screenstream/_stop.lock";

        // ===========================================================
        // 公開 API — 給敏感 page 端用 (UCL_LoginStatusPage / 未來 Token/Bank/Relationship page)
        // ===========================================================

        /// <summary>
        /// 偵測 ScreenStream daemon 當前是否正在錄影 (config.enabled=true)。
        /// </summary>
        /// <remarks>
        /// 物理意義: 讀 AgentCommands/_screenstream/_config.json 的 enabled 欄位。
        /// 數值影響: 純 read, 0 副作用。檔案不存在或 parse fail → false (預設不擋)。
        /// </remarks>
        public static bool IsRecording()
        {
            try
            {
                string path = Path.Combine(GetRepoRoot(), CONFIG_RELATIVE);
                if (!File.Exists(path)) return false;
                string txt = File.ReadAllText(path)
                    .Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");
                return txt.Contains("\"enabled\":true");
            }
            catch { return false; }
        }

        /// <summary>
        /// 寫 _sensitive.flag → daemon 偵測到該 frame 直接黑屏 (img replace by blackout)。
        /// </summary>
        /// <param name="pageName">敏感 page 類別名 (e.g. nameof(UCL_LoginStatusPage)), 寫進 flag 內容方便 audit</param>
        /// <param name="reason">敏感原因 (e.g. "Contains sensitive login information")</param>
        /// <remarks>
        /// 物理意義: page 在 OnGUI 每 frame 呼叫, 寫一次 flag (touch 行為; daemon 端有 TTL 邏輯自己處理過期)。
        /// 數值影響: 每 frame 一次 File.WriteAllText, 但 daemon 端 ring buffer 已 quality=65 jpg per frame, IO 量級對齊。
        /// </remarks>
        public static void TouchSensitiveFlag(string pageName, string reason)
        {
            try
            {
                string path = Path.Combine(GetRepoRoot(), SENSITIVE_FLAG_RELATIVE);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                    $"{DateTime.UtcNow:O}\n{pageName}: {reason}\n");
            }
            catch { }
        }

        /// <summary>
        /// IMGUI 端渲染「錄影中, 此 Page 已暫停顯示」黑屏遮罩 + 中斷直播按鈕。
        /// </summary>
        /// <param name="reason">敏感原因 (顯示在中央給 user 看)</param>
        /// <param name="minHeight">遮罩最小高度 (預設 280, 對齊原 LoginStatusPage 行為)</param>
        /// <remarks>
        /// 物理意義: 純 GUILayout 渲染, 給 Tim 自己看的視覺警示 (daemon frame 端走 _sensitive.flag 是另一條路徑)。
        /// 數值影響: 黑屏內含「中斷直播」按鈕 — 按下 → 寫 _stop.lock → daemon 下個 loop tick 偵測 → enabled=false + 刪 lock。
        /// </remarks>
        public static void DrawRecordingBlackout(string reason, float minHeight = 280f)
        {
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.black;
            using (new GUILayout.VerticalScope("box", GUILayout.MinHeight(minHeight)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("🔒 ScreenStream 錄影中 — 此 Page 已暫停顯示",
                    new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 20,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = new Color(1f, 0.5f, 0.5f) }
                    });
                GUILayout.Space(10);
                GUILayout.Label(string.IsNullOrEmpty(reason) ? "(no reason given)" : reason,
                    new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = new Color(0.8f, 0.8f, 0.8f) }
                    });
                GUILayout.Label("關閉 ScreenStream 才能查看 (見 UCL_ScreenStreamPage)",
                    new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
                    });
                GUILayout.Space(14);

                // 中斷直播按鈕 (T17 新增) — 寫 _stop.lock, daemon 偵測 → 自動關閉 + 刪 lock
                // 物理意義: 給 user 一個「我要看這 page 內容」緊急閘門, 不必去 UCL_ScreenStreamPage 切 toggle
                // 數值影響: 按一次寫一個檔, daemon poll interval (~1s) 內生效
                GUI.backgroundColor = new Color(0.7f, 0.3f, 0.3f);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("🛑 中斷直播 (寫 _stop.lock)",
                        GUILayout.Width(280), GUILayout.Height(36)))
                    {
                        WriteStopLock($"User clicked from blackout overlay (reason: {reason})");
                    }
                    GUILayout.FlexibleSpace();
                }
                GUI.backgroundColor = Color.black;

                GUILayout.FlexibleSpace();
            }
            GUI.backgroundColor = oldBg;
        }

        /// <summary>
        /// 寫 _stop.lock → daemon 下個 loop tick 偵測到 → set enabled=false + 刪 lock。
        /// </summary>
        /// <param name="reason">為何中斷, 寫進 lock 內容方便 audit</param>
        /// <remarks>
        /// 物理意義: 給 Editor 端按鈕 / 程式化中斷使用, 對齊「config.enabled=false」直接 toggle 但更明確 (audit reason)。
        /// 數值影響: daemon 偵測流程 (見 screenstream_daemon.py main_loop):
        ///   1. 每 loop iteration reload config 後 check stop.lock 存在
        ///   2. 存在 → 寫 enabled=false → unlink stop.lock → log + 廣播酒保 stop announcement
        /// </remarks>
        public static void WriteStopLock(string reason)
        {
            try
            {
                string path = Path.Combine(GetRepoRoot(), STOP_LOCK_RELATIVE);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                    $"{DateTime.UtcNow:O}\n{reason}\n");
                Debug.Log($"[ScreenStreamGuard] stop.lock written → daemon 下次 poll 會中斷錄影");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScreenStreamGuard] WriteStopLock 失敗: {e.Message}");
            }
        }

        /// <summary>
        /// 一鍵組合: IsRecording → TouchSensitiveFlag + DrawRecordingBlackout。
        /// </summary>
        /// <returns>true 若該 page 應 short-circuit (錄影中 + 已渲染 blackout); false 走原邏輯</returns>
        /// <remarks>
        /// 物理意義: 給敏感 page 一行守門 — `if (UCL_ScreenStreamGuard.GuardPage(nameof(this), Reason)) return;`
        /// 數值影響: 集中 3 helper 呼叫順序 — IsRecording 否則直接 return false 不寫 flag 不畫 blackout
        /// </remarks>
        public static bool GuardPage(string pageName, string reason)
        {
            if (!IsRecording()) return false;
            TouchSensitiveFlag(pageName, reason);
            DrawRecordingBlackout(reason);
            return true;
        }

        // ===========================================================
        // Internal helpers
        // ===========================================================

        static string GetRepoRoot()
        {
            // .git walk, 跨專案安全。舊版用 dataPath 上兩層 (假設 EoV 巢狀), 本專案 project 根 = repo 根
            // 會多爬一層飛出 repo → IsRecording 讀錯 config 路徑 → 敏感頁錄影時該黑屏卻黑錯依據
            // (與 UCL_ScreenStreamPage 幻影路徑 split-brain, 2026-07-27 Tim QA fix)。
            return UCL_RepoPath.RepoRoot;
        }
    }
}
