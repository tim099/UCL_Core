// 區塊職責：聊天酒館系統總開關 — 控制台頁面 (UCL_ControlPanelPage) 操作的中央 gate。
// 物理意義：一個 PlayerPrefs flag (預設 OFF) 決定酒館各背景 daemon 是否運作：
//            - UCL_BartenderDaemon（酒保自動廣播 / 關鍵字觸發 / 時間規則 / 跨日保管費）
//            - RCG_DiscordInboundDaemon（Discord inbound bot 子程序）
//          OFF → 各 daemon tick early-return / kill 子程序；ON → 觸發 Restart 讓 daemon 重新初始化。
// 設計取捨 (Tim 2026-05-28 拍板)：
//   - 預設 OFF — Tim 要新裝 / 平時不被自動廣播打擾，明確打開才啟動
//   - 用 PlayerPrefs 而非 EditorPrefs — Tim 指定存 PlayerPrefs；跟 UCL_ChatTavernPage 的選擇狀態同源
//   - 用 event 解耦 — daemon 各自訂閱 OnSystemRestart，控制類不需知道有哪些 daemon
#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 聊天酒館系統總開關。控制台頁面讀寫此 flag；各背景 daemon 每 tick 讀 <see cref="IsEnabled"/> 決定是否運作。
    /// </summary>
    public static class UCL_ChatTavernSystemControl
    {
        /// <summary>PlayerPrefs key — 1=ON, 0=OFF。預設 0 (OFF)，per Tim 2026-05-28「預設關閉」。</summary>
        public const string EnabledPrefKey = "UCL.ChatTavern.System.Enabled";

        /// <summary>系統是否啟用 — key 不存在 (未設定過) 時回 false (預設 OFF)。</summary>
        public static bool IsEnabled => PlayerPrefs.GetInt(EnabledPrefKey, 0) == 1;

        /// <summary>
        /// 系統「重啟」事件 — 由 OFF→ON 的 transition（或控制台「重啟」按鈕）fire。
        /// 各 daemon 在 static ctor / Initialize 內訂閱，收到後重置掃描游標 / 強制下個 tick 立即跑 / respawn 子程序。
        /// </summary>
        public static event Action OnSystemRestart;

        /// <summary>
        /// 設定開關並落 PlayerPrefs。由 OFF→ON 的 transition 會額外觸發 <see cref="Restart"/>（per Tim「打開時要重啟系統」）。
        /// </summary>
        /// <param name="enabled">true=啟用系統 / false=停止系統。</param>
        public static void SetEnabled(bool enabled)
        {
            bool was = IsEnabled;                                  // 記錄變更前狀態，用來判定是否為 OFF→ON transition
            PlayerPrefs.SetInt(EnabledPrefKey, enabled ? 1 : 0);   // 寫入 flag (1/0)
            PlayerPrefs.Save();                                    // 立即落盤，避免 Editor 異常退出遺失設定
            if (enabled && !was)                                   // 只在「原本關、現在開」時重啟，避免重複開啟空轉重啟
            {
                Restart();
            }
        }

        /// <summary>
        /// 主動觸發重啟 — 逐一通知所有訂閱 daemon 重新初始化。
        /// 每個 handler 各自 try-catch，單一 daemon 重啟失敗不擋其他 daemon。
        /// </summary>
        public static void Restart()
        {
            var handler = OnSystemRestart;
            if (handler == null) return;
            // 逐 handler invoke + try-catch：避免某 daemon 重啟 throw 連累後續 daemon 收不到通知
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { Debug.LogWarning($"[ChatTavernSystem] restart handler 失敗: {e.Message}"); }
            }
        }
    }
}
#endif
