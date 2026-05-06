
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
        static void TryAutoOpen()
        {
            // 使用者主動關閉自動彈出 → 尊重
            if (EditorPrefs.GetBool(UCL_WelcomePage.PrefKey_AutoOpenDisabled, false))
            {
                return;
            }

            string shown = EditorPrefs.GetString(UCL_WelcomePage.PrefKey_ShownVersion, "");
            if (shown == UCL_WelcomePage.CurrentVersion)
            {
                // 已展示過當前版本 → 不再彈
                return;
            }

            // 標記版本（在開窗前先寫；萬一 OpenAndShow 過程出錯也不會無限重試）
            EditorPrefs.SetString(UCL_WelcomePage.PrefKey_ShownVersion, UCL_WelcomePage.CurrentVersion);

            Debug.Log($"[UCL_Welcome] First-install / version-bump detected " +
                      $"(prev='{shown}', current='{UCL_WelcomePage.CurrentVersion}'). Opening Welcome page.");
            UCL_WelcomePage.OpenAndShow();
        }
    }
}
#endif
