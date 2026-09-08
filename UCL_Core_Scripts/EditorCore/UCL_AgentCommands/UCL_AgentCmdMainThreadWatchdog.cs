// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/08 2026

// 主執行緒凍結的**當下**出聲的那一格（TASK-0162 第三格，2026-09-08）。
//
// 為什麼既有的兩本台帳不夠 —— 它們都站在主執行緒上：
//   `kind=stall` 是「恢復後的第一幀」回頭量出來的 ⇒ 它答得出**等了多久**，
//   答不出**當下卡在哪**；而 Editor 凍住的那 111 秒裡，主緒上的任何探針都不會被呼叫。
//   `[Tavern s_CacheLock]` 那行同理：它印在**等完之後**，凍著的時候它還沒有機會被寫。
//   ⇒ 這兩者共用同一個結構限制：**被測者停了，量具也跟著停**。
//
// 本檔換一層：一條**背景執行緒**每 250ms 讀主緒心跳，超過門檻就在**凍結進行中**落行。
//   它不依賴主緒排程，所以主緒死透的時候它是唯一還在記時間的東西。
//
// 🩸 2026-09-08 兩次實測（13:33 靜止 115s／20:39 靜止 111s）都只留下
//   「stall ＋ 一支 offload 的 `Tavern op=read`」，而那兩個讀數**分不出方向**：
//     甲 背景緒抱著訊息快取鎖 ⇒ 主緒撞上來等；乙 主緒被別的事佔住 ⇒ 鎖是無辜的。
//   本檔在凍結當下抓 `UCL_ChatTavernIO_PerMsgFile.LockHolderJson()` —— 那正是分辨兩者的欄位。
//
// ⛔ 射程（不可讀寬）：本檔回答的是「凍住的當下**誰抱著那把鎖**、有哪些 cmd 在跑」，
//   ⚠ **不回答「主緒停在哪一行」** —— 抓別條執行緒的 stack 在 Mono/Editor 下沒有安全的做法
//   （`Thread.Suspend` 已廢棄且會死鎖）。所以 holder 為 null 時，本檔給的是**排除**不是答案。
#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    public static partial class UCL_AgentCmdSlowLog
    {
        // ===========================================================
        // 區塊職責：watchdog 的門檻與節流
        // 物理意義：FREEZE_MS 比 STALL_MS（1000ms）高，因為本條路徑的用途不同 ——
        //          stall 要看得到中段（排序 handler），freeze 只管「人眼看得出 Editor 死了」那一族。
        //          ⚠ 3000ms 是刻意跟舊 `_heartbeat_stalls.jsonl` 同值，讓兩本台帳的「一次事件」對得起來。
        // 數值影響：穩態每 250ms 一次 Interlocked.Read ＋一次減法（無 IO、無配置）；
        //          只有真的凍住才碰磁碟，且同一次凍結最多每 REPEAT_MS 落一行 ⇒ 111 秒 ≈ 11 行。
        // 邊界：⛔ 不做「凍結結束」那一行 —— 那一格 `kind=stall` 已經在寫了，
        //      再寫一份就是第二把量同一件事的尺（本 repo 最貴的一族）。
        // ===========================================================
        const double FREEZE_MS = 3000.0;
        const double FREEZE_REPEAT_MS = 10000.0;
        const int WATCHDOG_POLL_MS = 250;

        static Thread s_Watchdog;
        static volatile bool s_WatchdogStop;

        // ===========================================================
        // 區塊職責：起 watchdog（domain reload 後自動；reload 前務必收掉）
        // 物理意義：⚠ 背景執行緒**不隨 domain reload 死掉**。漏收的樣子是每次編譯多留一條，
        //          它們各自抱著上一個 domain 的 static 欄位繼續寫檔 ⇒ 台帳裡出現同一時刻的重複行，
        //          而那看起來像「凍結真的發生了很多次」。⇒ 假讀數，比沒有量具貴。
        //          `IsBackground = true` 只保證它擋不住 process 退出，**不保證 reload 時被收掉**。
        // ===========================================================
        [InitializeOnLoadMethod]
        static void StartMainThreadWatchdog()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= StopMainThreadWatchdog;
            AssemblyReloadEvents.beforeAssemblyReload += StopMainThreadWatchdog;
            EditorApplication.quitting -= StopMainThreadWatchdog;
            EditorApplication.quitting += StopMainThreadWatchdog;

            if (s_Watchdog != null && s_Watchdog.IsAlive) return;
            s_WatchdogStop = false;
            s_Watchdog = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "UCL_AgentCmd MainThread Watchdog",
            };
            s_Watchdog.Start();
        }

        static void StopMainThreadWatchdog()
        {
            s_WatchdogStop = true;
            s_Watchdog = null;   // 不 Join —— 最長 250ms 後自己退，而 reload 不該被量具擋著
        }

        // ===========================================================
        // 區塊職責：背景迴圈 —— 主緒心跳超時就落一行 kind=freeze
        // 物理意義：`s_LastTickTicks` 是主緒每幀用 Interlocked 寫的；主緒被占住 ⇒ 它就停在那裡不動。
        //          ⇒ 本迴圈量的是「那個數字有多舊」，而**這條路徑完全不經過被測者**（憲法④要的形狀）。
        // 邊界：整個迴圈包在 try 裡 —— watchdog 自己拋例外死掉的失效樣子是
        //      「後來再也沒有 freeze 行」，跟「後來沒有再凍過」**同形**。⇒ 死也要出一次聲。
        // ===========================================================
        static void WatchdogLoop()
        {
            double aReportedAtMs = -1;   // 本次凍結已在第幾毫秒報過（-1 ＝ 目前沒在凍結）
            while (!s_WatchdogStop)
            {
                try
                {
                    Thread.Sleep(WATCHDOG_POLL_MS);

                    long aTicks = Interlocked.Read(ref s_LastTickTicks);
                    if (aTicks == 0) continue;   // 主緒還沒戳過第一下（domain reload 剛完成）

                    double aFrozenMs = (DateTime.UtcNow - new DateTime(aTicks, DateTimeKind.Utc)).TotalMilliseconds;

                    if (aFrozenMs < FREEZE_MS)
                    {
                        aReportedAtMs = -1;   // 主緒還活著 ⇒ 本次凍結（若有）已結束
                        continue;
                    }
                    if (aReportedAtMs >= 0 && aFrozenMs - aReportedAtMs < FREEZE_REPEAT_MS) continue;

                    aReportedAtMs = aFrozenMs;
                    AppendFreezeLine(aTicks, aFrozenMs);
                }
                catch (Exception e)
                {
                    // ⛔ 不 return —— 一次寫檔失敗不該讓整條觀測通道靜默死亡。
                    WarnOnce($"主緒 watchdog 這一輪失敗（本迴圈續跑）：{e.Message}");
                }
            }
        }

        static void AppendFreezeLine(long iLastTickTicks, double iFrozenMs)
        {
            DateTime aNow = DateTime.UtcNow;
            DateTime aLastTick = new DateTime(iLastTickTicks, DateTimeKind.Utc);

            // 凍結當下的鎖持有者 —— 這一格才是甲乙的分辨鍵（見檔頭）。
            string aLockJson = null;
            try { aLockJson = ChatTavern.UCL_ChatTavernIO_PerMsgFile.LockHolderJson(); }
            catch (Exception) { /* 讀不到就留 null —— 不猜 */ }

            var aSb = new StringBuilder(320);
            aSb.Append("{\"kind\":\"freeze\"")
               .Append(",\"observed_at\":\"").Append(Iso(aNow)).Append('"')
               .Append(",\"last_main_tick_at\":\"").Append(Iso(aLastTick)).Append('"')
               .Append(",\"frozen_ms\":").Append(F1(iFrozenMs))
               .Append(",\"threshold_ms\":").Append(F0(FREEZE_MS))
               .Append(",\"observed_from_tid\":")
               .Append(Thread.CurrentThread.ManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append(",\"tavern_cache_lock\":").Append(aLockJson ?? "null")
               .Append(",\"running_cmds\":").Append(OverlapsJson(aLastTick, aNow))
               .Append('}');
            AppendLine(aSb.ToString());
        }
    }
}
#endif
