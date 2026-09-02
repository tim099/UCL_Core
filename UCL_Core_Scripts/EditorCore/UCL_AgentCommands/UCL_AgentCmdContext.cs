// 區塊職責：單筆 Cmd 執行期間的「回傳槽」容器 —— 取代跨 lane 共用的全域 static。
// 物理意義：queue 依 persona 分 lane 之後，Watcher 會**並行**送出多條 lane
//          （`UCL_AgentCommandWatcher.OnEditorUpdate` 逐 agentId 派遣、不等前一個完成；
//           重入閘 `s_RunningAgents` 是 per-agent，同 agent 擋、不同 agent 放行）。
//          此時「本次 cmd 落了哪些檔／post_seq 是多少」若存在單一全域 static，
//          兩條 lane 會互相覆寫 —— 而**覆寫後的值是合法數字，回傳檔照印，沒有任何一格會紅**。
//
// 🩸 為什麼不用 AsyncLocal（ambient）—— 實測，不是選型偏好（basecamp 2026-08-16）：
//    `UCL_AgentCmdScopeProbe.SelfTestConcurrent()` 讀數：
//      A:afterOtherStarted=LEAK,second=LEAK | B:OWN,OWN | A.seq=0(want 1) B.seq=2(want 2)
//    ⇒ UniTask 不在 await 邊界複製 ExecutionContext，兩條並行流共用主執行緒同一份 ambient 值，
//      **後設定的蓋掉先設定的**。而單流測試（SelfTest）全綠 —— 它證明不了任何併發性質。
//    ⇒ 所以 context **不做 ambient，改由 `args` 顯式攜帶 `_cmd_id`**：
//      args 是唯一一個本來就明確傳到每個 handler 手上的東西（runner 早就在往裡塞
//      `_caller_env_marker`）。缺 `_cmd_id` 時取不到 context ⇒ **出聲**，不靜默退回全域
//      （留退路等於把併發下的錯誤重新變回無聲的）。
#if UNITY_EDITOR
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>單筆 Cmd 執行的回傳槽（outputs / values / post_seq / env marker）。</summary>
    public sealed class UCL_AgentCmdContext
    {
        public string CmdId;
        public string AgentId;          // lane = persona（Tim 2026-08-16：--agent-id 移除，身分只有 persona 一層）
        public string CallerEnvMarker;  // 記帳來源標記（串線會影響錢，故一併收進 context）

        // ⚠ 同一筆 cmd 內部仍可能多執行緒續接（UniTask.SwitchToThreadPool），故集合操作上鎖。
        readonly object m_Lock = new object();
        readonly List<string> m_Outputs = new List<string>();
        readonly List<KeyValuePair<string, string>> m_Values = new List<KeyValuePair<string, string>>();

        /// <summary>本次執行落了哪個檔（絕對路徑）；同路徑去重、保序。</summary>
        public void AddOutput(string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return;
            lock (m_Lock) { if (!m_Outputs.Contains(iPath)) m_Outputs.Add(iPath); }
        }

        /// <summary>本次執行的一個純量結果（如 post_seq）。同 key 重複回報**保留全部、不覆寫**
        /// （單一 cmd 內 Op_Post 可能跑不只一次；後蓋前會讓 caller 拿到另一筆的號碼）。</summary>
        public void AddValue(string iKey, string iValue)
        {
            if (string.IsNullOrEmpty(iKey)) return;
            lock (m_Lock) m_Values.Add(new KeyValuePair<string, string>(iKey, iValue ?? ""));
        }

        public List<string> SnapshotOutputs() { lock (m_Lock) return new List<string>(m_Outputs); }
        public List<KeyValuePair<string, string>> SnapshotValues() { lock (m_Lock) return new List<KeyValuePair<string, string>>(m_Values); }

        // 區塊職責：in-process 呼叫 Cmd_Tavern 之後取回剛寫入的 seq
        // 物理意義：舊制是 `Cmd_Tavern.LastPostSeq` 這顆全域 static，呼叫端用法是
        //          「歸零 → await 別的 Cmd → 讀回」——**寫入與讀取之間隔著一個 await**。
        //          併行時會拿到別人的 seq，而那是一個完全合法的數字（summit 交接 §6 點名的第一隻）。
        // 數值影響：0 = 本次流程內沒有成功貼文（沿用舊語意，呼叫端判斷式不必改）。
        public int LastPostSeq;
    }

    /// <summary>
    /// 執行中 cmd 的 context 登錄簿 —— 以 **cmd id** 為鍵，由 `args["_cmd_id"]` 顯式索引。
    /// </summary>
    public static class UCL_AgentCmdContexts
    {
        /// <summary>args 內攜帶 cmd id 的欄位名（runner 注入，handler 只讀）。</summary>
        public const string ARG_CMD_ID = "_cmd_id";

        static readonly Dictionary<string, UCL_AgentCmdContext> s_Map = new Dictionary<string, UCL_AgentCmdContext>();
        static readonly object s_Lock = new object();

        public static UCL_AgentCmdContext Create(string iCmdId, string iAgentId, string iCallerEnvMarker)
        {
            var aCtx = new UCL_AgentCmdContext { CmdId = iCmdId, AgentId = iAgentId, CallerEnvMarker = iCallerEnvMarker };
            if (!string.IsNullOrEmpty(iCmdId)) { lock (s_Lock) s_Map[iCmdId] = aCtx; }
            return aCtx;
        }

        public static void Release(string iCmdId)
        {
            if (string.IsNullOrEmpty(iCmdId)) return;
            lock (s_Lock) s_Map.Remove(iCmdId);
        }

        // 區塊職責：in-process 呼叫另一個 Cmd 時，把「我是哪筆 cmd」帶進子 args
        // 物理意義：子 Cmd（典型是 Cmd_Tavern）要把 seq / 產出檔回報到**呼叫者這一筆**的 context。
        //          舊制靠全域 static 遞出，併行時會串線；現在改成顯式攜帶。
        // 數值影響：父 args 沒有 `_cmd_id`（IMGUI 手動路徑）時什麼都不做 —— 不造假、不猜。
        public static void PropagateCmdId(IDictionary<string, string> iParentArgs, IDictionary<string, string> ioChildArgs)
        {
            if (iParentArgs == null || ioChildArgs == null) return;
            if (iParentArgs.TryGetValue(ARG_CMD_ID, out var aId) && !string.IsNullOrEmpty(aId))
                ioChildArgs[ARG_CMD_ID] = aId;
        }

        /// <summary>依 cmd id 取 context。</summary>
        public static UCL_AgentCmdContext Get(string iCmdId)
        {
            if (string.IsNullOrEmpty(iCmdId)) return null;
            lock (s_Lock) return s_Map.TryGetValue(iCmdId, out var aCtx) ? aCtx : null;
        }

        // 區塊職責：handler 端的唯一入口 —— 從自己手上的 args 取回本次 cmd 的 context
        // 物理意義：`args` 是顯式流動的，不依賴 ExecutionContext ⇒ 併行下不會串線。
        // 數值影響：取不到時回 null，**並且出聲一次**（IMGUI 手動跑 handler 是合法的 null 情境，
        //          故用 Warning 不用 Error；但 in-process 呼叫忘了帶 `_cmd_id` 也會落在這裡，
        //          那才是真正要被看見的那種 —— 訊息把兩種可能都寫出來，機器不猜）。
        public static UCL_AgentCmdContext FromArgs(IDictionary<string, string> iArgs, string iWho = null)
        {
            string aId = null;
            if (iArgs != null) iArgs.TryGetValue(ARG_CMD_ID, out aId);
            if (string.IsNullOrEmpty(aId))
            {
                UnityEngine.Debug.LogWarning(
                    $"[AgentCmdContext] args 缺 `{ARG_CMD_ID}`{(string.IsNullOrEmpty(iWho) ? "" : $"（{iWho}）")} —— " +
                    "本次回報不會進 result 檔。兩種可能：①非 queue 路徑（IMGUI 手動跑 handler，正常） " +
                    "②in-process 呼叫另一個 Cmd 時新造了 args 卻沒把 `_cmd_id` 帶過去（要修的是這種）。");
                return null;
            }
            var aCtx = Get(aId);
            if (aCtx == null)
            {
                UnityEngine.Debug.LogWarning(
                    $"[AgentCmdContext] 找不到 cmd_id={aId} 的 context{(string.IsNullOrEmpty(iWho) ? "" : $"（{iWho}）")} —— " +
                    "該筆可能已結束（回報比 cmd 活得久＝背景動作沒被等待）。");
            }
            return aCtx;
        }
    }

    // ===========================================================
    // 🩸 血證探針 —— 這個類**不是機制的一部分**，它存在的唯一理由是證明
    //    「ambient（AsyncLocal）方案在本專案不可用」，並讓任何人都能自己重跑一次。
    // 跑法：senate ucmd run Invoke --arg type=UCL.Core.EditorLib.AgentCommands.UCL_AgentCmdScopeProbe
    //                            --arg member=SelfTest        （單流：會全綠 —— 而它證明不了併發）
    //                            --arg member=SelfTestConcurrent（雙流：LEAK 就在這裡）
    // 結論（basecamp 2026-08-16 實測）：SelfTestConcurrent → A 全 LEAK、A.seq 停在 0。
    // ⇒ 不要因為 SelfTest 全綠就回頭改用 AsyncLocal。**單流測試對併發性質沒有證據力。**
    // ===========================================================
    public static class UCL_AgentCmdScopeProbe
    {
        static readonly System.Threading.AsyncLocal<UCL_AgentCmdContext> s_Current
            = new System.Threading.AsyncLocal<UCL_AgentCmdContext>();

        static UCL_AgentCmdContext Current => s_Current.Value;
        static void SetCurrent(UCL_AgentCmdContext iCtx) => s_Current.Value = iCtx;

        public static string SelfTest()
        {
            SelfTestAsync().Forget();
            return "已送出 SelfTest（結果印在 Editor Console：[CmdScope-SelfTest]）";
        }

        static async UniTaskVoid SelfTestAsync()
        {
            var aCtx = new UCL_AgentCmdContext { CmdId = "selftest", AgentId = "selftest" };
            SetCurrent(aCtx);
            var aSb = new System.Text.StringBuilder("[CmdScope-SelfTest] ");
            aSb.Append("set=").Append(Current == aCtx ? "HIT" : "MISS").Append(" | ");
            await UniTask.Yield();
            aSb.Append("afterYield=").Append(Current == aCtx ? "HIT" : "MISS").Append(" | ");
            await UniTask.SwitchToThreadPool();
            aSb.Append("onThreadPool=").Append(Current == aCtx ? "HIT" : "MISS").Append(" | ");
            await UniTask.SwitchToMainThread();
            aSb.Append("backOnMain=").Append(Current == aCtx ? "HIT" : "MISS").Append(" | ");
            await NestedAsync();
            aSb.Append("afterNested=").Append(Current == aCtx ? "HIT" : "MISS").Append(" | ");
            aSb.Append("nestedWroteBack=").Append(aCtx.LastPostSeq == 4242 ? "HIT" : "MISS");
            SetCurrent(null);
            aSb.Append(" | cleared=").Append(Current == null ? "HIT" : "MISS");
            UnityEngine.Debug.Log(aSb.ToString());
        }

        static async UniTask NestedAsync()
        {
            await UniTask.Yield();
            var c = Current;
            if (c != null) c.LastPostSeq = 4242;
        }

        public static string SelfTestConcurrent()
        {
            RunConcurrentProbe().Forget();
            return "已送出 SelfTestConcurrent（結果印在 Editor Console：[CmdScope-Concurrent]）";
        }

        static async UniTaskVoid RunConcurrentProbe()
        {
            var aA = new UCL_AgentCmdContext { CmdId = "A", AgentId = "laneA" };
            var aB = new UCL_AgentCmdContext { CmdId = "B", AgentId = "laneB" };
            var aLog = new System.Text.StringBuilder("[CmdScope-Concurrent] ");
            var t1 = LaneFlow(aA, "A", aLog);
            var t2 = LaneFlow(aB, "B", aLog);
            await UniTask.WhenAll(t1, t2);
            aLog.Append("| A.seq=").Append(aA.LastPostSeq).Append("(want 1)")
                .Append(" B.seq=").Append(aB.LastPostSeq).Append("(want 2)");
            SetCurrent(null);
            UnityEngine.Debug.Log(aLog.ToString());
        }

        static async UniTask LaneFlow(UCL_AgentCmdContext iCtx, string iTag, System.Text.StringBuilder ioLog)
        {
            SetCurrent(iCtx);
            await UniTask.Yield();
            bool aOwn1 = Current == iCtx;
            await UniTask.Yield();
            bool aOwn2 = Current == iCtx;
            var c = Current;
            if (c != null) c.LastPostSeq = iTag == "A" ? 1 : 2;
            ioLog.Append(iTag).Append(":afterOtherStarted=").Append(aOwn1 ? "OWN" : "LEAK")
                 .Append(",second=").Append(aOwn2 ? "OWN" : "LEAK").Append(" ");
        }
    }
}
#endif
