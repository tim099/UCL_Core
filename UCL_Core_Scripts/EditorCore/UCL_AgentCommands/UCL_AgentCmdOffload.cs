// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/07 2026

// Cmd handler 離開主執行緒的**唯一入口**（TASK-0162）。
//
// 為什麼要有這一層，而不是每支 handler 自己寫 `await UniTask.SwitchToThreadPool()`：
//   背景緒上碰不得的東西**不是「Unity API」這個模糊集合**，是三個具體的路徑解析器
//   （`UCL_AgentCommandsPath.DataRoot` → PlayerPrefs、`UCL_RepoPath.*` → `Application.dataPath`、
//   `UCL_LettersPath.Root` → 前兩者）。它們**都有快取**，所以只要**在主緒先摸一次**，
//   之後背景緒拿到的是字串而不是 Unity API 呼叫。
//   ⇒ 這個「先摸一次」就是本檔存在的理由：40 支 handler 各自記得摸哪三個，
//   等於同一份清單散成 40 份，而漏摸的那一支**只在冷啟動的第一次**炸（最難重現的那種）。
//
// 🩸 已知先例（本檔是把它們的形狀收成一份）：`Cmd_GoodMorning` step=brief 的註解自己寫著
//   「路徑解析在主執行緒先做（CorePath 走 AssetDatabase，main-thread-only）；長跑段丟背景」——
//   那句話是對的，而它當時只長在一支 handler 上。
//
// ⛔ 不在 `UCL_AgentCommandRunner` 那一層切執行緒：Runner 的 catch 讀
//   `EditorApplication.isPlayingOrWillChangePlaymode`（主緒 only），而且 timeout 那條路
//   走 `UniTask.Delay`（Editor 下掛在 update 上）—— 在 Runner 切會讓那兩格的執行緒歸屬變成
//   「看哪個先完成」。⇒ 切在 handler 內，Runner 只負責**回來的時候把自己拉回主緒**。
#if UNITY_EDITOR
using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// handler 把自己移出主執行緒的入口。用法（handler 的**第一行**，還在主緒時）：
    /// <code>
    /// public override async UniTask ExecuteAsync(Dictionary&lt;string,string&gt; args, CancellationToken token)
    /// {
    ///     await UCL_AgentCmdOffload.EnterBackground();   // 取代 await UniTask.Yield();
    ///     …原本的同步 IO 全部照舊，只是不在主緒上跑…
    /// }
    /// </code>
    /// 回主緒**不必由 handler 負責** —— Runner 在 handler 結束後會把自己拉回主緒
    /// （見 <see cref="UCL_AgentCommandRunner"/> 那段 `SwitchToMainThread` 的區塊註解）。
    /// </summary>
    public static class UCL_AgentCmdOffload
    {
        // 主執行緒錨：用來判斷 EnterBackground 是不是真的從主緒被呼叫的。
        // ⚠ 這不是防禦，是**觀測** —— 從背景緒呼叫它，prewarm 會靜默失效（那幾個 getter 會丟例外
        //   而我們吞掉），於是「快取暖好了」與「這次沒暖到」同形。所以要出聲。
        static int s_MainThreadId = -1;
        static bool s_WarnedOffMain = false;

        [UnityEditor.InitializeOnLoadMethod]
        static void AnchorMainThread()
            => s_MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        // ===========================================================
        // 區塊職責：把主緒 only 的路徑解析器各摸一次，讓它們的 static 快取填滿
        // 物理意義：這三族 getter 第一次呼叫會走 Unity API（`Application.dataPath` / PlayerPrefs），
        //          之後回快取字串。**背景緒讀快取是安全的**（值在同一個 domain 內不變，
        //          唯一的寫入點是 domain reload 與後台頁按 Apply，兩者都在主緒）。
        // 數值影響：冷啟動時各做一次實際解析（毫秒級）；之後純屬讀 static 欄位。
        //          逐個 try —— 某一個解析不到（例如 repo 結構特殊）不該讓其他幾個也不暖。
        // ===========================================================
        public static void PrewarmMainThreadCaches()
        {
            if (s_MainThreadId >= 0
                && System.Threading.Thread.CurrentThread.ManagedThreadId != s_MainThreadId
                && !s_WarnedOffMain)
            {
                s_WarnedOffMain = true;
                Debug.LogWarning("[UCL_AgentCmdOffload] PrewarmMainThreadCaches 被從**非主執行緒**呼叫"
                    + " ⇒ 這一輪的路徑快取沒有暖到（本警告只出一次）。"
                    + " 修法：EnterBackground() 要放在 handler 的第一行，那時還在主緒。");
            }

            try { _ = UCL_AgentCommandsPath.DataRoot; } catch (Exception) { }
            try { _ = UCL_RepoPath.RepoRoot; } catch (Exception) { }
            try { _ = UCL_RepoPath.UnityProjectRoot; } catch (Exception) { }
            try { _ = UCL_RepoPath.UCLCoreDir; } catch (Exception) { }
            try { _ = UCL_RepoPath.CoreToolsDir; } catch (Exception) { }
            try { _ = UCL_RepoPath.ProjectFingerprint; } catch (Exception) { }
            try { _ = UCL_LettersPath.Root; } catch (Exception) { }
        }

        // ===========================================================
        // 區塊職責：prewarm ＋ 切到背景執行緒
        // 物理意義：⚠ **順序不可反。** 先切再暖 ＝ 在背景緒呼叫那些 getter ＝ 它們丟例外被吞掉，
        //          於是 handler 之後每一次用到路徑都在冷快取上重試 Unity API ⇒
        //          症狀是「這支 cmd 偶爾失敗」而不是「這支 cmd 不能 offload」。
        // 數值影響：呼叫端之後的程式碼跑在 thread pool 上，主執行緒立刻空出來。
        //          ⛔ 這不會讓工作變少 —— `elapsed_ms` 應該**幾乎不變**，變的是
        //          `kind=stall` 那條路徑不再把斷拍歸因到它（TASK-0161 的量具就是驗這個的）。
        // ===========================================================
        public static async UniTask EnterBackground(System.Collections.Generic.IDictionary<string, string> iArgs = null)
        {
            PrewarmMainThreadCaches();
            await UniTask.SwitchToThreadPool();
            // ⏱ 到這一行時**已經在背景緒上** —— 就地戳一筆讀數（TASK-0161 的欄位 offloaded / bg_tid）。
            //   為什麼在這裡而不是由 Runner 量：Runner 站在 `await` 的外面，而 await 的續接會被
            //   UniTask 送回主緒 ⇒ 在外面問永遠得到 main（實測踩過兩次，見 UCL_AgentCmdSlowLog 檔頭）。
            //   cmd id 從 args 的框架欄拿（`_cmd_id`）；非 queue 路徑拿不到就不戳，不猜。
            if (iArgs != null && iArgs.TryGetValue(UCL_AgentCmdContexts.ARG_CMD_ID, out var aCmdId))
                UCL_AgentCmdSlowLog.NoteOffloaded(aCmdId);
        }
    }
}
#endif
