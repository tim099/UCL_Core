
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：偵測「首次安裝 UCL_Core 模組」並自動開啟 Welcome 頁。
// 物理意義：[InitializeOnLoad] 在 Editor 啟動 / domain reload 時自動執行。
//          比對 EditorPrefs 內紀錄的 ShownVersion 與 UCL_WelcomePage.CurrentVersion，
//          若不同（首次安裝 / 大版本更新）→ 透過 EditorApplication.delayCall 在 Editor 完成
//          初始化後彈出 Welcome 頁，並更新 ShownVersion 避免下次重啟又彈。
//          使用者可在 Welcome 頁按「不再自動彈出」勾選 → 寫入 AutoOpenDisabled flag，
//          即使日後 ShownVersion 不同也不再彈，保留手動入口（UCL → Welcome 選單）。
// 數值影響：寫 EditorPrefs（per-user / per-machine）；可能開啟一個 EditorWindow。
#if UNITY_EDITOR
using UCL.Core.EditorLib.Page;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// [InitializeOnLoad] 自動觸發類別：domain reload 時檢查是否該彈出 Welcome 頁。
    ///
    /// 觸發條件（必須全部成立）：
    /// <list type="number">
    ///   <item>EditorPrefs <c>UCL_Core.Welcome.AutoOpenDisabled</c> 為 false（使用者沒主動關）</item>
    ///   <item>EditorPrefs <c>UCL_Core.Welcome.ShownVersion</c> 與 <see cref="UCL_WelcomePage.CurrentVersion"/> 不同</item>
    /// </list>
    /// 觸發後：
    /// <list type="bullet">
    ///   <item>更新 ShownVersion（避免下次重啟 Editor 又彈）</item>
    ///   <item>呼叫 <see cref="UCL_WelcomePage.OpenAndShow"/> 開啟 Welcome 視窗</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class UCL_WelcomeAutoOpen
    {
        // 區塊職責：static ctor — Domain reload 觸發
        // 物理意義：Unity Editor 啟動或 script 重新編譯後立刻跑一次
        // 數值影響：可能 schedule 一次 delayCall，由 delayCall 決定是否實際彈出
        static UCL_WelcomeAutoOpen()
        {
            // 用 delayCall 推遲一個 frame：避免 InitializeOnLoad 執行時 Editor 還沒完全 ready
            // （否則 GetWindow / repaint 可能炸出 NRE）
            EditorApplication.delayCall += TryAutoOpen;
        }

        // 區塊職責：實際的彈出判定 + 觸發
        // 物理意義：使用者主動關掉就尊重；未來想再看可重設 PrefKey 或從 UCL→Welcome 手動開
        // public 改：Cmd_Invoke 可直接觸發，便於重啟 Editor 之外的測試流程
        public static void TryAutoOpen()
        {
            string disabledKey = UCL_WelcomePage.PrefKey_AutoOpenDisabled;
            string versionKey = UCL_WelcomePage.PrefKey_ShownVersion;
            string current = UCL_WelcomePage.CurrentVersion;

            Debug.Log($"[UCL_Welcome] TryAutoOpen begin. " +
                      $"disabledKey='{disabledKey}', versionKey='{versionKey}', current='{current}'");

            // Gate 1：使用者主動關閉自動彈出 → 尊重
            bool disabled = EditorPrefs.GetBool(disabledKey, false);
            if (disabled)
            {
                Debug.Log("[UCL_Welcome] Gate 1: AutoOpenDisabled=true → skip.");
                return;
            }

            // Gate 2：已展示過當前版本 → 不再彈
            string shown = EditorPrefs.GetString(versionKey, "");
            if (shown == current)
            {
                Debug.Log($"[UCL_Welcome] Gate 2: shown='{shown}' == current='{current}' → skip.");
                return;
            }

            // Gates passed — 寫版本 + 開窗
            EditorPrefs.SetString(versionKey, current);
            Debug.Log($"[UCL_Welcome] Gates passed (prev='{shown}', current='{current}'). " +
                      $"Calling ShowWindow + OpenAndShow.");

            // 先強制開啟 UCL_MenuWindow — 否則 OpenAndShow 內的 ExecuteMenuItem("UCL/Menu") 在
            // [InitializeOnLoad] + delayCall 早期時序可能不會真的在當輪 frame 把 window 帶進
            // OnGUI cycle，導致 s_OnFirstDraw hook 沒人消費，Welcome 不彈。
            // 同 asmdef 可直接 reference UCL_MenuWindow，比走 ExecuteMenuItem 可靠。
            var window = UCL_MenuWindow.ShowWindow(new UCL_EditorMenu());
            Debug.Log($"[UCL_Welcome] UCL_MenuWindow.ShowWindow returned: {window} (null={window == null})");

            UCL_WelcomePage.OpenAndShow();
            Debug.Log("[UCL_Welcome] OpenAndShow returned. Hook s_OnFirstDraw set; will fire on next OnGUI.");
        }

        // 區塊職責：跳過所有 gate，直接觸發開窗流程；給 Cmd_Invoke / 手動測試用
        // 物理意義：不寫 / 不讀 EditorPrefs，純流程驗證。如果 Welcome 沒彈，問題不在 pref。
        public static void ForceOpen()
        {
            Debug.Log("[UCL_Welcome] ForceOpen — bypassing gates.");
            var window = UCL_MenuWindow.ShowWindow(new UCL_EditorMenu());
            Debug.Log($"[UCL_Welcome] (Force) UCL_MenuWindow.ShowWindow returned: {window} (null={window == null})");
            UCL_WelcomePage.OpenAndShow();
            Debug.Log("[UCL_Welcome] (Force) OpenAndShow returned.");
        }
    }
}
#endif
