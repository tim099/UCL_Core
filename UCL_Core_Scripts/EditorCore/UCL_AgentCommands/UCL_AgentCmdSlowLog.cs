// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/07 2026

// AgentCommand 的耗時量具（TASK-0161）—— 兩條**互相獨立**的路徑寫同一份 jsonl：
//
//   ① cmd 路徑：Runner 在每支 handler 前後夾一個 Stopwatch ⇒ 那支 cmd 自己花了多久。
//   ② stall 路徑：本檔自己訂閱 EditorApplication.update **每幀**戳一次 UtcNow ⇒
//      兩幀之間的間隔就是「主執行緒被占住了多久」，而**它不需要 cmd 配合**。
//
// 🩸 為什麼要兩條而不是一條（這是本檔存在的理由）：
//   `.Forget()` 的 UniTask 會**就地同步跑到第一個真正 yield 的 await 為止** ⇒ 一支同步 handler
//   會整段跑在 `UCL_AgentCommandWatcher.OnEditorUpdate` 這一拍裡。於是：
//   - 只有①：一支「跑了 30 秒但在 thread pool 上」的 cmd 跟一支「卡住主執行緒 30 秒」的 cmd
//     **在讀數上完全同形**，而它們一個沒事一個讓 Editor 彈對話框。
//   - 只有②：斷拍量得到，但**不知道是誰**（既有的 `_heartbeat_stalls.jsonl` 就停在這裡，
//     2026-09-07 那筆 42.5s 至今無主）。
//   ⇒ 所以 stall 那條**自己去跟 cmd 的區間對重疊**，把歸因從人工夾區間變成機械欄位。
//
// 🩸 **handler_ms 與 runner_ms 是兩個數字，因為第一版只有前者而它漏掉了實際的兇手**
//   （2026-09-07 實測，變因單一：同一支 `AutoCommit op=scan` 連跑三趟）：
//   handler `elapsed_ms = 869.3`（**未達 1000ms 門檻 ⇒ 不落行**），而同一區間的
//   `kind=stall` 量到 **2007.1ms** 主執行緒斷拍並歸因到它。
//   ⇒ 卡住的那 2 秒在 handler 的括號**外**：Runner 自己的 queue load/save、`WriteCmdResult`、
//   `WriteLastOpStubIfAbsent`、`PurgeOldCmdResults`、ModuleService 等待都在那裡。
//   📌 所以「handler 不慢」**不等於**「這支 cmd 不卡 Editor」——
//   只有 handler 那一個數字時，這族的讀數跟「今天沒有慢的 cmd」一模一樣。
//   ⇒ `elapsed_ms`（handler）＋ `runner_ms`（Runner 這一圈）兩個都落，門檻**各判一次**。
//   ⛔ 刻意不把 handler 那欄的口徑改寬 —— 已落的行會換意思而沒有人知道。
//
// 📌 `offloaded` / `bg_tid` / `main_tid`：這支有沒有真的離開主緒，以及它落在哪條緒。
//   由 **offload 入口在背景區段裡面**戳（`NoteOffloaded`）—— 因為外面問不到。
//   🩸 同一個問題我量錯過兩次，兩次的失效樣子都是「欄位永遠給同一個答案」：
//   ① 量在 `End()`：Runner 之後統一切回主緒 ⇒ 永遠 main。
//   ② 量在 Runner 的 `await handlerTask` 之後：UniTask 把續接送回 PlayerLoop ⇒ **還是**永遠 main，
//      即使 handler 全程在背景緒上跑。實測讀數：offload 已生效的 `AutoCommit op=scan`
//      仍印 `handler_thread=main`（2026-09-07）。
//   ⇒ 一般形：**問「這段程式跑在哪條緒」不能站在它外面問** —— 外面永遠是 await 恢復的地方。
//   ⛔ 而一個永遠給同一個答案的欄位比沒有欄位更貴：它看起來像讀數。
//
// ⛔ 本檔零行為變更：不改任何 handler 的執行緒歸屬（offload 是另一張單）。量具壞掉不准影響 cmd 本業，
//    所以每一個對外入口都自己 try 起來（形狀沿用已驗過的 UCL_BartenderIO.AppendSlowTick）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 一支 cmd 的量測句柄 —— 由 <see cref="UCL_AgentCmdSlowLog.Begin"/> 產生、
    /// <see cref="UCL_AgentCmdSlowLog.End"/> 收掉。呼叫端只傳遞它，不讀它的欄位。
    /// </summary>
    public sealed class UCL_AgentCmdProbe
    {
        internal string CmdId;
        internal string CmdType;
        internal string Op;
        internal string Persona;
        internal string Lane;
        internal DateTime StartUtc;
        internal System.Diagnostics.Stopwatch Watch;

        /// <summary>Runner 標下的分相位耗時（name → ms，保序）—— 相位是「誰慢」的下一層定語。</summary>
        internal System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>> Phases;

    }

    /// <summary>
    /// per-cmd 耗時 ＋ 主執行緒斷拍的落檔量具。輸出：
    /// <c>&lt;DataRoot&gt;/_diagnostics/_cmd_slow.jsonl</c>（kind=cmd / kind=stall 兩種行）。
    /// </summary>
    [InitializeOnLoad]
    public static partial class UCL_AgentCmdSlowLog
    {
        // ===========================================================
        // 區塊職責：門檻與上界常數
        // 物理意義：門檻決定「什麼叫慢」。1000ms 是刻意比既有 stall 門檻（3000ms）低的 ——
        //          既有那份是給「daemon 卡住」用的，本份要拿來**排序 48 支 handler**，
        //          而排序需要看得到中段（3 秒門檻會把「每次都 1.5 秒」那族整族藏起來，
        //          而那族才是天天在拖 Editor 的）。
        // 數值影響：低於門檻的 cmd 不落行（只進記憶體 ring 供 stall 歸因）；
        //          行數上界 KEEP_PER_KIND **每 kind 各算**（理由是血證，寫在 AppendLine 檔內）
        //          ⇒ 檔案大小有界，不會長成第二個 Editor.log，而 stall 也餓不死 cmd。
        // ===========================================================
        const double CMD_SLOW_MS = 1000.0;
        const double STALL_MS = 1000.0;
        const int KEEP_PER_KIND = 200;   // 每 kind 各自的上界（見 AppendLine 的血證：共用上界會讓 stall 餓死 cmd）
        const int RING_SIZE = 32;

        /// <summary>輸出檔（相對資料根）—— 給後台頁 / 診斷文件引用同一個字面。</summary>
        public const string LogRelative = "_diagnostics/_cmd_slow.jsonl";

        // 主執行緒錨 —— End() 可能落在背景執行緒（handler offload 過），那一格正是我們要量的
        static int s_MainThreadId = -1;

        // stall 探針的上一幀時刻。⚠ 用 DateTime.UtcNow 而不是 EditorApplication.timeSinceStartup：
        //   後者是 Unity API，只在主執行緒可靠；而本檔的比較要能在 End()（可能在背景緒）那側做。
        static DateTime s_LastTickUtc = DateTime.MinValue;

        // 同一個讀數的原子副本，**只給背景 watchdog 讀**（見 OnEditorUpdateProbe 的區塊註解）。
        static long s_LastTickTicks;

        // 已結束 / 進行中的 cmd 區間 ring —— stall 行靠它回答「那段時間誰在跑」。
        // ⚠ 上鎖：Begin/End 可能在背景緒，探針在主緒。
        static readonly List<Entry> s_Ring = new List<Entry>();
        static readonly object s_RingLock = new object();

        // ===========================================================
        // 區塊職責：**在飛的 probe 依 cmd_id 查得到** —— 讓 handler 自己標相位。
        // 🩸 為什麼補這個（TASK-0120，2026-09-10）：`MarkPhase` 只收 probe 物件，而 probe 是
        //   Runner 的局部變數 ⇒ **handler 拿不到它**，於是相位只有 Runner 前後那幾格
        //   （`batch_queue_load` / `pre_handler` / …），handler 內部是一個不透明的 `elapsed_ms`。
        //   ⇒ 2026-09-09 一筆 `op=observe` 佔住主緒 **147.9 秒**，而量具只能告訴我們「148 秒」，
        //   說不出那 148 秒花在哪一段。**那正是「下次再發生也一樣查不出來」的形狀。**
        // 物理意義：Runner 在 `Begin` 登記、`End` 移除；handler 用 args 裡的 `_cmd_id` 查。
        // 邊界：⛔ 查不到就**不寫**（非 queue 路徑、或量具自己壞掉）——
        //   ⚠ 而「查不到」與「這段很快」在輸出上同形，所以查不到時**不靜默**：出聲一次。
        // ===========================================================
        static readonly object s_ActiveLock = new object();
        static readonly Dictionary<string, UCL_AgentCmdProbe> s_Active = new Dictionary<string, UCL_AgentCmdProbe>(StringComparer.Ordinal);
        static readonly HashSet<string> s_WarnedMissingProbe = new HashSet<string>(StringComparer.Ordinal);

        // 寫檔鎖 —— stall 探針在主緒、End() 可能在背景緒，兩邊寫同一個檔（見 AppendLineLocked 的血證）
        static readonly object s_WriteLock = new object();

        // 量具自己失敗時**出聲一次**（不洗版）。⛔ 全吞掉的話，「沒有慢的 cmd」與「量具在漏行」同形。
        static bool s_WarnedWriteFail = false;

        // 落檔路徑在 domain reload 當下就解析好並快取。
        // 🩸 理由：DataRoot 第一次解析會讀 PlayerPrefs（主執行緒 only），而 End() 可能在背景緒 ——
        //   那一格會丟例外，而它的失敗樣子是「量具安靜地什麼都沒寫」＝跟「沒有慢的 cmd」同形。
        static string s_LogPath;

        sealed class Entry
        {
            public string CmdId;
            public string CmdType;
            public string Op;
            public string Persona;
            public DateTime StartUtc;
            public DateTime? EndUtc;
            public bool Offloaded;      // 這支有沒有走 UCL_AgentCmdOffload.EnterBackground
            public int BgThreadId;      // 走了之後落在哪條緒（≠ 主緒才算真的離開）
        }

        // ===========================================================
        // 區塊職責：註冊 stall 探針（domain reload 後自動）
        // 物理意義：**就地同步訂閱 EditorApplication.update，不經 delayCall。**
        //          同 repo 已有這一課的血證（UCL_AgentCommandWatcher 檔頭）：delayCall 是單次
        //          schedule，Editor 在背景沒人動它就永遠不來，於是整條通道靜默死亡。
        //          量具死掉比功能死掉更難發現 —— 它不會有人來報「我的量具沒動」。
        // 數值影響：每幀一次 DateTime.UtcNow ＋ 一次減法（無 IO、無配置）；
        //          只有在間隔 >= STALL_MS 時才碰磁碟。
        // ===========================================================
        static UCL_AgentCmdSlowLog()
        {
            s_MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            try
            {
                s_LogPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, LogRelative).Replace('\\', '/');
            }
            catch (Exception e)
            {
                // 路徑解析不到 ⇒ 明說一次。⛔ 不可以安靜 —— 「沒有慢的 cmd」與「量具沒在量」不准同形。
                s_LogPath = null;
                Debug.LogWarning($"[UCL_AgentCmdSlowLog] 落檔路徑解析失敗 ⇒ **本輪沒有人在量**：{e.Message}");
            }
            s_LastTickUtc = DateTime.UtcNow;
            EditorApplication.update += OnEditorUpdateProbe;
        }

        // ===========================================================
        // 區塊職責：每幀戳一次時間，間隔過大就落一行 stall
        // 物理意義：主執行緒被占住時本函式**不會被呼叫** —— 所以量到斷拍的一定是「恢復後的第一幀」，
        //          而那一幀看到的間隔就是被占住的長度。⇒ 這是一條**不需要 cmd 自己誠實**的路徑
        //          （憲法④要的第二條路徑：它不經過被測者）。
        // 數值影響：斷拍 >= STALL_MS 時寫一行 kind=stall，附**與該區間重疊**的 cmd 清單。
        //          ⚠ 重疊 ≠ 兇手：一段斷拍可能同時重疊到數筆（並行 lane），本檔照實列出全部，
        //          不替它挑一個（挑了就是在沒有讀數的情況下宣告歸屬）。
        // ===========================================================
        static void OnEditorUpdateProbe()
        {
            DateTime aNow = DateTime.UtcNow;
            DateTime aPrev = s_LastTickUtc;
            s_LastTickUtc = aNow;
            // ⚠ 給背景 watchdog 讀的那一份要**原子**寫：`DateTime` 是 8 bytes 的 struct，
            //   跨執行緒讀寫沒有原子性保證（撕裂讀的樣子是一個荒謬的時刻 ⇒ 假的 freeze 行）。
            //   ⇒ 另存一份 ticks，用 Interlocked 寫、Interlocked.Read 讀。
            System.Threading.Interlocked.Exchange(ref s_LastTickTicks, aNow.Ticks);
            if (aPrev == DateTime.MinValue) return;

            double aGapMs = (aNow - aPrev).TotalMilliseconds;
            if (aGapMs < STALL_MS) return;

            try
            {
                string aOverlaps = OverlapsJson(aPrev, aNow);
                var aSb = new StringBuilder(256);
                aSb.Append("{\"kind\":\"stall\"")
                   .Append(",\"observed_at\":\"").Append(Iso(aNow)).Append('"')
                   .Append(",\"stalled_since\":\"").Append(Iso(aPrev)).Append('"')
                   .Append(",\"gap_ms\":").Append(F1(aGapMs))
                   .Append(",\"threshold_ms\":").Append(F0(STALL_MS))
                   .Append(",\"overlapping_cmds\":").Append(aOverlaps)
                   .Append('}');
                AppendLine(aSb.ToString());
            }
            catch (Exception e) { WarnOnce($"stall 讀數落檔失敗：{e.Message}"); }
        }

        // ===========================================================
        // 區塊職責：開始量一支 cmd
        // 物理意義：Stopwatch（非 DateTime 差）量 elapsed —— 系統時鐘被調整時 DateTime 會給出負值或跳躍，
        //          而 elapsed 是本檔唯一會被拿去排序的數字。UtcNow 只用來標「哪一段時間」（給 stall 對重疊）。
        // 數值影響：一個小物件 ＋ 一筆進 ring（ring 有上界，超過丟最舊）。門檻在 End 才判 ⇒
        //          **快的 cmd 也進 ring**，因為 stall 歸因需要它們（一段斷拍可能落在一支「很快」的 cmd 附近）。
        // ===========================================================
        public static UCL_AgentCmdProbe Begin(string iCmdId, string iCmdType, string iLane, Dictionary<string, string> iArgs)
        {
            try
            {
                var aProbe = new UCL_AgentCmdProbe
                {
                    CmdId = iCmdId ?? "",
                    CmdType = iCmdType ?? "",
                    Lane = iLane ?? "",
                    Op = PickArg(iArgs, "op", "step", "sub"),
                    Persona = PickArg(iArgs, "persona", "target_persona", null),
                    StartUtc = DateTime.UtcNow,
                    Watch = System.Diagnostics.Stopwatch.StartNew(),
                };
                lock (s_RingLock)
                {
                    s_Ring.Add(new Entry
                    {
                        CmdId = aProbe.CmdId,
                        CmdType = aProbe.CmdType,
                        Op = aProbe.Op,
                        Persona = aProbe.Persona,
                        StartUtc = aProbe.StartUtc,
                        EndUtc = null,
                    });
                    if (s_Ring.Count > RING_SIZE) s_Ring.RemoveRange(0, s_Ring.Count - RING_SIZE);
                }
                if (aProbe.CmdId.Length > 0) lock (s_ActiveLock) s_Active[aProbe.CmdId] = aProbe;
                return aProbe;
            }
            catch
            {
                return null;   // 量具自己壞掉 ⇒ 回 null，End() 收得住
            }
        }

        // ===========================================================
        // 區塊職責：標一個相位的耗時（由 Runner 呼叫）
        // 物理意義：`elapsed_ms` 只回答「這支慢」，相位回答「**慢在哪一格**」——
        //   而 2026-09-07 的讀數說那個答案往往不在 handler 裡：AutoCommit offload 之後，
        //   1.3-1.5s 的斷拍搬到了 handler 的**前後**（Runner 的前奏與收尾）。
        //   形狀沿用已驗過的 `UCL_BartenderIO.AppendSlowTick`（那份的相位讓「哪一格慢」不必靠人夾區間）。
        // 數值影響：純記憶體；相位只在該筆 cmd 落行時一起寫出去。
        // ===========================================================
        /// <summary>
        /// handler 用的相位入口：**用 args 裡的 `_cmd_id`** 找到在飛的 probe 並標一格（TASK-0120）。
        /// <para>⚠ 查不到會 `Debug.LogWarning` 一次（per cmd_id）—— ⛔ 不靜默：
        /// 「這支沒被量到」與「這一段很快」在 jsonl 上完全同形。</para>
        /// </summary>
        public static void MarkPhase(string iCmdId, string iName, double iMs)
        {
            if (string.IsNullOrEmpty(iCmdId) || string.IsNullOrEmpty(iName)) return;
            UCL_AgentCmdProbe aProbe = null;
            lock (s_ActiveLock) s_Active.TryGetValue(iCmdId, out aProbe);
            if (aProbe == null)
            {
                bool aFirst;
                lock (s_ActiveLock) aFirst = s_WarnedMissingProbe.Add(iCmdId);
                if (aFirst)
                    Debug.LogWarning($"[AgentCmdSlowLog] 相位 '{iName}' 標不上去：查不到 cmd_id='{iCmdId}' 的 probe"
                        + "（非 queue 路徑，或 Begin/End 已經收掉了）⇒ **這一格不會出現在 _cmd_slow.jsonl**。"
                        + " ⚠ 而缺席的相位跟「這段很快」長得一樣 —— 所以這行警告是刻意的。");
                return;
            }
            lock (aProbe) MarkPhase(aProbe, iName, iMs);
        }

        public static void MarkPhase(UCL_AgentCmdProbe iProbe, string iName, double iMs)
        {
            if (iProbe == null || string.IsNullOrEmpty(iName)) return;
            if (iProbe.Phases == null)
                iProbe.Phases = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>(6);
            iProbe.Phases.Add(new System.Collections.Generic.KeyValuePair<string, double>(iName, iMs));
        }

        // ===========================================================
        // 區塊職責：由 offload 入口在**背景緒上**戳一筆「我離開主緒了，落在 tid=N」
        // 物理意義：🩸 這是同一個問題的**第三個**量測位置，前兩個都量錯了時刻：
        //   ① 量在 `End()` —— Runner 之後統一切回主緒 ⇒ 永遠回 main。
        //   ② 量在 Runner 的 `await handlerTask` 之後 —— UniTask 把 await 的續接送回主緒
        //      （PlayerLoop 是它的預設 context）⇒ **還是**永遠回 main，即使 handler 全程在背景。
        //   ⇒ 唯一量得到真相的位置是**背景區段裡面**，而只有 offload 入口站在那裡。
        //   📌 一般形：問「這段程式在哪條緒上跑」不能在它外面問 —— 外面永遠是 await 恢復的地方。
        // 數值影響：只寫記憶體（ring），落檔在 End 那一刻讀它。找不到對應 cmd（非 queue 路徑）就不寫。
        // ===========================================================
        public static void NoteOffloaded(string iCmdId)
        {
            if (string.IsNullOrEmpty(iCmdId)) return;
            int aTid = System.Threading.Thread.CurrentThread.ManagedThreadId;
            lock (s_RingLock)
            {
                for (int i = s_Ring.Count - 1; i >= 0; i--)
                {
                    if (s_Ring[i].CmdId == iCmdId && s_Ring[i].EndUtc == null)
                    {
                        s_Ring[i].Offloaded = true;
                        s_Ring[i].BgThreadId = aTid;
                        return;
                    }
                }
            }
        }

        /// <summary>主執行緒的 managed thread id —— 讀數解讀用（bg_tid 等於它就表示根本沒離開）。</summary>
        public static int MainThreadId => s_MainThreadId;

        // ===========================================================
        // 區塊職責：收掉一支 cmd 的量測，超過門檻就落一行
        // 物理意義：`offloaded` ＋ `bg_tid` 是本行最貴的兩欄 —— 它們回答「這支到底有沒有閃開主執行緒」，
        //          而那正是下一張單（逐支 offload）要照著排的順序。
        //          `offloaded=false` / `bg_tid=0` ＝ 從沒離開主緒；`bg_tid == main_tid` 也是同一件事。
        //   🩸 **這一段原本寫的欄位名叫 `ended_on_main_thread`，而那個鍵從來沒有被 emit 過**
        //     （basecamp 2026-09-08 讀 jsonl 抓到，我自己 grep 複驗：全 repo 只有註解命中、
        //      **零個寫入端**）。⇒ 修的是**名字**不是欄位：`offloaded`／`bg_tid` 已經回答了那個問題，
        //      再 emit 一個 `ended_on_main_thread` 就是替同一件事造第二個名字。
        //   ⚠ 而它的失效樣子特別壞：拿那個名字去 grep jsonl 會回零，
        //     而**「這個鍵不存在」與「這支沒被量到」在畫面上一模一樣**。
        //          ⚠ 量的是**結束時**站在哪條緒：一支「前半段同步跑完才 await」的 handler
        //          在這兩欄上看起來會像沒離開過 ⇒ 真正的卡住證據仍要跟 kind=stall 那條路徑對，
        //          這兩欄只是分流線索。
        // 數值影響：elapsed < CMD_SLOW_MS ⇒ 不寫檔（ring 仍更新，供 stall 歸因）。
        // ===========================================================
        public static void End(UCL_AgentCmdProbe iProbe, bool iSuccess, string iError, double iRunnerMs)
        {
            if (iProbe == null) return;
            // ⛔ 先摘登記再做其他事：底下任何一步丟例外都不該讓這張表長出殘留
            //   （殘留的 probe 會讓下一支同 cmd_id 的相位標到一個已經落檔的物件上）。
            if (!string.IsNullOrEmpty(iProbe.CmdId))
                lock (s_ActiveLock) { s_Active.Remove(iProbe.CmdId); s_WarnedMissingProbe.Remove(iProbe.CmdId); }
            try
            {
                iProbe.Watch.Stop();
                double aMs = iProbe.Watch.Elapsed.TotalMilliseconds;
                DateTime aEnd = DateTime.UtcNow;
                // 本支有沒有真的離開主緒 —— 由 offload 入口在背景緒上戳的那一筆（見 NoteOffloaded）。
                bool aOffloaded = false;
                int aBgTid = -1;

                lock (s_RingLock)
                {
                    for (int i = s_Ring.Count - 1; i >= 0; i--)
                    {
                        if (s_Ring[i].CmdId == iProbe.CmdId && s_Ring[i].EndUtc == null)
                        {
                            s_Ring[i].EndUtc = aEnd;
                            aOffloaded = s_Ring[i].Offloaded;
                            aBgTid = s_Ring[i].BgThreadId;
                            break;
                        }
                    }
                }

                // ⚠ 門檻對**兩個數字**各判一次 —— 只看 handler 會漏掉「handler 很快而 Runner 很慢」那族，
                //   而那族是實測出來的（見本檔 handler_vs_runner 血證），不是假想的。
                if (aMs < CMD_SLOW_MS && iRunnerMs < CMD_SLOW_MS) return;

                var aSb = new StringBuilder(320);
                aSb.Append("{\"kind\":\"cmd\"")
                   .Append(",\"finished_at\":\"").Append(Iso(aEnd)).Append('"')
                   .Append(",\"started_at\":\"").Append(Iso(iProbe.StartUtc)).Append('"')
                   .Append(",\"elapsed_ms\":").Append(F1(aMs))
                   .Append(",\"runner_ms\":").Append(F1(iRunnerMs))
                   .Append(",\"threshold_ms\":").Append(F0(CMD_SLOW_MS))
                   .Append(",\"cmd_id\":\"").Append(Esc(iProbe.CmdId)).Append('"')
                   .Append(",\"type\":\"").Append(Esc(iProbe.CmdType)).Append('"')
                   .Append(",\"op\":\"").Append(Esc(iProbe.Op)).Append('"')
                   .Append(",\"persona\":\"").Append(Esc(iProbe.Persona)).Append('"')
                   .Append(",\"lane\":\"").Append(Esc(iProbe.Lane)).Append('"')
                   .Append(",\"offloaded\":").Append(aOffloaded ? "true" : "false")
                   .Append(",\"bg_tid\":").Append(aBgTid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(",\"main_tid\":").Append(s_MainThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(",\"success\":").Append(iSuccess ? "true" : "false");
                if (iProbe.Phases != null && iProbe.Phases.Count > 0)
                {
                    aSb.Append(",\"phases\":[");
                    for (int i = 0; i < iProbe.Phases.Count; i++)
                    {
                        if (i > 0) aSb.Append(',');
                        aSb.Append("{\"name\":\"").Append(Esc(iProbe.Phases[i].Key)).Append('"')
                           .Append(",\"ms\":").Append(F1(iProbe.Phases[i].Value)).Append('}');
                    }
                    aSb.Append(']');
                }
                if (!string.IsNullOrEmpty(iError))
                    aSb.Append(",\"error\":\"").Append(Esc(Trunc(iError, 240))).Append('"');
                aSb.Append('}');
                AppendLine(aSb.ToString());
            }
            catch (Exception e)
            {
                // 不影響 cmd 本業，但**要出聲一次** —— 漏行的失效樣子跟「這支不慢」一模一樣。
                WarnOnce($"per-cmd 讀數落檔失敗（cmd_id={iProbe?.CmdId}）：{e.Message}");
            }
        }

        static void WarnOnce(string iMsg)
        {
            if (s_WarnedWriteFail) return;
            s_WarnedWriteFail = true;
            try { Debug.LogWarning($"[UCL_AgentCmdSlowLog] {iMsg} ⇒ **這一行沒有進檔**（本警告只出一次）"); }
            catch { }
        }

        // ===========================================================
        // 區塊職責：把與 [iFrom, iTo] 有重疊的 cmd 區間輸出成 JSON 陣列
        // 物理意義：重疊判定用半開區間 —— 「在斷拍開始前就結束」與「在斷拍結束後才開始」都排除；
        //          進行中（EndUtc == null）的一律算重疊（它確實橫跨了那段時間）。
        // 數值影響：純記憶體掃 ring（<= RING_SIZE 筆），零 IO。
        // ===========================================================
        static string OverlapsJson(DateTime iFrom, DateTime iTo)
        {
            var aSb = new StringBuilder(96);
            aSb.Append('[');
            bool aFirst = true;
            lock (s_RingLock)
            {
                foreach (var e in s_Ring)
                {
                    if (e.StartUtc > iTo) continue;
                    if (e.EndUtc.HasValue && e.EndUtc.Value < iFrom) continue;
                    if (!aFirst) aSb.Append(',');
                    aFirst = false;
                    aSb.Append("{\"cmd_id\":\"").Append(Esc(e.CmdId)).Append('"')
                       .Append(",\"type\":\"").Append(Esc(e.CmdType)).Append('"')
                       .Append(",\"op\":\"").Append(Esc(e.Op)).Append('"')
                       .Append(",\"persona\":\"").Append(Esc(e.Persona)).Append('"')
                       .Append(",\"running\":").Append(e.EndUtc.HasValue ? "false" : "true")
                       .Append('}');
                }
            }
            aSb.Append(']');
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：追加一行，並把檔案行數壓在上界內（原子寫）
        // 物理意義：先讀既有行、加一行、超界丟最舊、寫 tmp 再 move ——
        //          形狀沿用 UCL_BartenderIO.AppendSlowTick（已在 repo 活了一個月的那份）。
        //          ⚠ 不用 File.AppendAllText：那條路沒有上界，診斷檔遲早長到沒人願意讀。
        //
        // 🩸 **上界要分 kind 各算，不可以共用一個。** 第一版我寫成共用 400 行 —— 而 stall 那條路徑
        //   會被編譯、asset import、domain reload 這些**跟 cmd 無關的斷拍**餵爆（那些行的
        //   overlapping_cmds 是空陣列），於是它會把 cmd 那半整族擠出檔外。
        //   ⇒ 失效樣子不是「檔案爆掉」，是**「檔案還在、格式完整、而我要排序的那一半不見了」** ——
        //   跟「今天沒有慢的 cmd」同形。分開算之後，兩條路徑不會互相餓死。
        // 數值影響：每 kind 各 KEEP_PER_KIND 行（檔案上界＝兩者之和）。修剪只丟該 kind 最舊的，
        //   另一 kind 一行都不動；認不出 kind 的行（手改／舊格式）一律留下，不替它猜。
        // ===========================================================
        static void AppendLine(string iLine)
        {
            if (string.IsNullOrEmpty(s_LogPath)) return;
            lock (s_WriteLock) { AppendLineLocked(iLine); }
        }

        // ===========================================================
        // 區塊職責：真正的寫檔（**已在鎖內**）
        // 🩸 血證（2026-09-07 本檔第一次活體，當場咬到）：第一版沒有這把鎖 ——
        //   stall 探針在**主執行緒**寫，而 `End()` 在**背景緒**寫（handler offload 過的那幾支，
        //   例如 GoodMorning step=brief 走完 RunOnThreadPool 之後就再也沒回主緒）。
        //   兩邊各自「讀全檔 → 加一行 → Delete → Move」⇒ 撞在 Delete/Move 那一格會丟例外，
        //   而 catch 把它吞掉 ⇒ **那一行就這樣不見了**。
        //   實測樣子：檔案裡 5 行 stall 全在、`kind=cmd` **一行都沒有**，
        //   而 ring 明明記著那兩支 cmd 都跑完了（running=false）⇒
        //   **「這支不慢」與「這行被撞掉了」在檔案上完全同形。** 量具自己犯了它要抓的那族病。
        // 數值影響：寫入序列化（同時最多一個 writer）。鎖只包檔案 IO，不包量測本身 ⇒
        //   不會把 cmd 的 elapsed 算進等鎖的時間。
        // ===========================================================
        static void AppendLineLocked(string iLine)
        {
            string aDir = Path.GetDirectoryName(s_LogPath);
            if (!string.IsNullOrEmpty(aDir) && !Directory.Exists(aDir)) Directory.CreateDirectory(aDir);

            var aLines = new List<string>();
            if (File.Exists(s_LogPath))
            {
                foreach (var l in File.ReadAllLines(s_LogPath))
                {
                    if (!string.IsNullOrWhiteSpace(l)) aLines.Add(l);
                }
            }
            aLines.Add(iLine);
            TrimPerKind(aLines, "\"kind\":\"cmd\"");
            TrimPerKind(aLines, "\"kind\":\"stall\"");

            string aTmp = s_LogPath + ".tmp";
            File.WriteAllLines(aTmp, aLines, new UTF8Encoding(false));
            if (File.Exists(s_LogPath)) File.Delete(s_LogPath);
            File.Move(aTmp, s_LogPath);
        }

        // ===========================================================
        // 區塊職責：把某一個 kind 的行數修到上界內（就地改 iLines）
        // 物理意義：以「行內含不含那個 kind 標記」分群 —— 刻意用字串比對而不解析 JSON：
        //          修剪器多解析一層就多一種自己會壞的方式，而它壞掉時**看起來只是檔案短了一點**。
        // 數值影響：只移除該 kind 最舊的幾行，保持其餘行的相對順序（檔案仍是時間序）。
        // ===========================================================
        static void TrimPerKind(List<string> iLines, string iKindMark)
        {
            int aCount = 0;
            for (int i = 0; i < iLines.Count; i++)
            {
                if (iLines[i].Contains(iKindMark)) aCount++;
            }
            int aExcess = aCount - KEEP_PER_KIND;
            if (aExcess <= 0) return;
            for (int i = 0; i < iLines.Count && aExcess > 0; )
            {
                if (iLines[i].Contains(iKindMark))
                {
                    iLines.RemoveAt(i);
                    aExcess--;
                    continue;   // 移除後同一個 index 是下一行，不遞增
                }
                i++;
            }
        }

        // ===========================================================
        // 區塊職責：小工具（取參數 / 逃脫 / 格式化）
        // 物理意義：`op` 這一欄在不同 Cmd 家族用不同名字（op / step / sub）—— 取第一個有值的，
        //          因為排序時人要看得懂「Tavern 的哪個 op」，而不是只看到 type=Tavern。
        // 數值影響：純字串處理。數字一律 InvariantCulture ⇒ 不會因地區設定寫出 `1,5` 這種壞 JSON。
        // ===========================================================
        static string PickArg(Dictionary<string, string> iArgs, string iA, string iB, string iC)
        {
            if (iArgs == null) return "";
            if (iA != null && iArgs.TryGetValue(iA, out var a) && !string.IsNullOrEmpty(a)) return a;
            if (iB != null && iArgs.TryGetValue(iB, out var b) && !string.IsNullOrEmpty(b)) return b;
            if (iC != null && iArgs.TryGetValue(iC, out var c) && !string.IsNullOrEmpty(c)) return c;
            return "";
        }

        static string Iso(DateTime iUtc) => iUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";

        static string F1(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        static string F0(double v) => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        static string Trunc(string s, int iMax)
            => string.IsNullOrEmpty(s) || s.Length <= iMax ? s : s.Substring(0, iMax) + "…";

        /// <summary>JSON 字串值逃脫 —— 控制字元直接寫進檔案會讓整份診斷檔變成壞 JSON，
        /// 而診斷檔壞掉的代價是**下次卡住時沒有證據**（同 UCL_BartenderIO.EscapeJsonString 的理由）。</summary>
        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
