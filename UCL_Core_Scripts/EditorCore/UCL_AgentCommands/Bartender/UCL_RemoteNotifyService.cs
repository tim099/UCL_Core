// 區塊職責：酒保自動通知 — 定期掃在線 persona 的收信匣，依權重挑一個，走遠端路由把 ding 送到她的視窗。
// 物理意義：@ 進了 inbox 只是「訊息躺在那裡」，被 @ 的人不會知道；這條線把「有人叫你」變成她桌面上真的
//          被戳一下。這是整條遠端路由裡**唯一會按 Enter** 的流程 —— 因為它的目的就是替使用者送出。
// 數值影響：每輪只通知一個 persona（權重最高者），避免一次搶好幾個視窗。
//
// 確認已讀機制（Tim 2026-08-03 拍板，tavern seq 9897-9898 討論定稿）—— 兩條獨立軌：
// ① 已讀軌：戳完**不推進** acked seq，只記 pending 快照；daemon 每輪驗三信號（本人開口 / inbox 歸檔 /
//    catchup cursor 推進）任一成立才推進 —— 「已通知」不再被當成「已讀」。
// ② 冷卻軌：每次戳完 cooldown_until = now + CooldownSeconds（全域值、後台可調、預設 60s），
//    **與已讀狀態無關** —— 就算秒讀又湧進新 @，冷卻沒過就不戳（防連續 @ 轟炸）。
// retry：同一批 pending 每戳一次 retry_count++；達 RetryCap（預設 3、可調）→ 停戳 + 酒保發酒館 @Tim 一次；
//        Tim 在酒館（Discord inbound）再次 @ 該 persona → retry 歸零恢復；已讀確認 → retry 歸零。
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>通知池的一名候選 —— 權重與其組成一起帶著走，才看得出「為什麼是她」。</summary>
    public class UCL_NotifyCandidate
    {
        public UCL_PersonaLockInfo Lock;
        public string Persona = "";
        public int NewMentions;
        public int MaxSeq;
        public DateTime LastNotifiedUtc = DateTime.MinValue;
        /// <summary>有人正掛著 <c>--wait-reply-from 這個 persona</c> 在 blocking 等她回話。</summary>
        public bool WaitedFor;
        /// <summary>誰在等（顯示用，回答「為什麼是她」）。</summary>
        public string WaitedBy = "";

        /// <summary>被等待的加權（Tim 2026-08-04：「妳正在等 gura，gura 就該有 100 權重」）。</summary>
        public const int WaitedForWeight = 100;

        // 權重 = 新 @ 次數 × 10（Tim 2026-08-02 給的尺）＋ 被等待 100（Tim 2026-08-04）。
        // 為什麼是**相加**而不是取代／取大值：
        //   取代或 Max 都會讓「被等 + 累積 12 個 @」跟「被等 + 剛好 0 個 @」變成同分，
        //   而前者顯然更該先戳。相加保留了 @ 次數當同組內的排序依據，
        //   同時 100 這個級距讓「被等的人」穩定壓過任何 <10 個 @ 的人 —— 這就是「優先度更高」的實作。
        public int Weight => NewMentions * 10 + (WaitedFor ? WaitedForWeight : 0);

        public string Describe()
        {
            string last = LastNotifiedUtc == DateTime.MinValue
                ? "從未通知"
                : $"上次 {LastNotifiedUtc.ToLocalTime():MM-dd HH:mm}";
            // 權重組成攤開寫 —— 「為什麼是她」要能一眼看懂，不然後台只是一個沒有理由的數字
            string breakdown = WaitedFor
                ? $"新 @ {NewMentions} 次 ×10 ＋ 🔴 {WaitedBy} 等待中 {WaitedForWeight}"
                : $"新 @ {NewMentions} 次 ×10";
            return $"{Persona}：{breakdown} → 權重 {Weight}｜{last}";
        }
    }

    // 區塊職責：單一房間的 inbox 統計 — 通知池為什麼是空的，答案常常藏在「哪一房」而不是「幾筆」。
    // 物理意義：每個房間**各自**從 1 開始編 seq（tavern 已到 15000+，TRPG 側房還在 100 出頭），
    //          而已讀水位 record.Seq 只有一個、跨房共用。於是高編號房把低編號房整房遮蔽：
    //          水位被 tavern 推到 15073 之後，trpg-yachiyo 的 seq=109 永遠不可能 > 水位。
    // 數值影響：MaxSeq < 水位 ⇒ 這房**不論再進幾筆 @ 都不會被算成新的**（Masked=true）。
    //          這不是「晚一輪才戳」，是永久靜默 —— 而且它的外觀跟「大家都已讀」完全一樣。
    public class UCL_RoomInboxStat
    {
        public string Room = "";
        public int Entries;
        public int MinSeq;
        public int MaxSeq;
        public int NewCount;
        /// <summary>整房被水位遮蔽：本房最大 seq 都低於水位，本房的 @ 永遠不會進池。</summary>
        public bool Masked;

        public string Describe() =>
            $"{Room}：{Entries} 筆（seq {MinSeq}-{MaxSeq}）→ 新 @ {NewCount}"
            + (Masked ? "　⚠ 整房遮蔽（本房最大 seq 低於水位，永遠算不出新 @）" : "");
    }

    // 區塊職責：一名在線 persona 在某一輪掃描裡的去向 —— 入池或被淘汰，附「為什麼」。
    // 物理意義：舊版只看得到「池裡有誰」。池是空的時候，六種完全不同的成因（沒在線 / 沒條目 /
    //          都已讀 / 本輪被認列已讀 / 停戳 / 冷卻）長成同一句「沒有人有新的 @」——
    //          那句話沒有說謊，它只是把六個答案壓成一個，於是查不出是哪一個。
    // 數值影響：只記錄，不參與任何判定；ScanPool 每輪重建。
    public class UCL_NotifyScanTrace
    {
        public string Persona = "";
        public string ActualAgent = "";
        public int AckedSeq;
        public int MaxSeq;
        public int NewMentions;
        public bool Pooled;
        public int Weight;
        // 這幾個 key 同時被 switch 比對、寫進 trace jsonl、供外部工具 grep ——
        // 字面值散在三處遲早對不起來（本 repo 對「兩邊各拼一次字串」已有血證），一律走常數。
        public const string VerdictPooled = "pooled";
        public const string VerdictNoInbox = "no_inbox";
        public const string VerdictAllAcked = "all_acked";
        public const string VerdictCreditedRead = "credited_read";
        public const string VerdictRetryCap = "retry_cap";
        public const string VerdictCooldown = "cooldown";

        /// <summary>淘汰原因 key（入池為 <see cref="VerdictPooled"/>）；可能值見上方 Verdict* 常數。</summary>
        public string Verdict = "";
        public string Detail = "";
        /// <summary>誰正 blocking 等她回話（空＝沒人等）。**被淘汰時也要記** —— 「有人卡在她身上
        /// 而她沒被戳」是最該一眼看到的狀態，只在入池時才記等於看不到出事的那一半。</summary>
        public string WaitedBy = "";
        public List<UCL_RoomInboxStat> Rooms = new List<UCL_RoomInboxStat>();

        public string Icon()
        {
            switch (Verdict)
            {
                case UCL_NotifyScanTrace.VerdictPooled: return "✅";
                case UCL_NotifyScanTrace.VerdictCreditedRead: return "🟡";
                case UCL_NotifyScanTrace.VerdictRetryCap: return "🔴";
                case UCL_NotifyScanTrace.VerdictCooldown: return "🕐";
                case UCL_NotifyScanTrace.VerdictAllAcked: return "⚪";
                default: return "▫";
            }
        }

        /// <summary>有任一房被水位遮蔽 —— 這是 per-room seq 撞全域水位的現場指紋。</summary>
        public bool HasMaskedRoom
        {
            get
            {
                foreach (var r in Rooms) if (r.Masked) return true;
                return false;
            }
        }

        public string Describe() =>
            $"{Icon()} {Persona}（{ActualAgent}）：新 @ {NewMentions}｜水位 seq {AckedSeq}／inbox 最大 {MaxSeq}"
            + (string.IsNullOrEmpty(WaitedBy) ? "" : $"｜🔴 {WaitedBy} 等待中")
            + $" → {Detail}";
    }

    public static class UCL_RemoteNotifyService
    {
        const string ConfigFileName = "remote_notify_config.json";
        const string StateFileName = "remote_notify_state.json";
        const string LogFileName = "remote_notify_last_run.md";
        const string TraceFileName = "remote_notify_trace.jsonl";
        static readonly Regex InboxEntryRegex = new Regex(@"^##\s*\[seq=(\d+)\]", RegexOptions.Multiline);

        public static bool Enabled;
        public static double IntervalSeconds = 30;
        // ⚠ 預設文字刻意是 `/ucl-ding` 而不是「叮」：Tim 手動戳是打「叮」，酒保自動戳是 `/ucl-ding`
        //   （Tim 2026-08-02 定的慣例）。改成一樣的字，收到的人就分不出這次是人在叫還是機器在叫。
        public static string NotifyText = "/ucl-ding";
        /// <summary>是否在輸入完成後送出。關閉時只把文字打進去、停在送出前一步。</summary>
        public static bool SendEnter = true;
        // 區塊職責：輸入方式 —— 剪貼簿貼上（預設）或逐字輸入。
        // 物理意義：逐字會被目標端的 slash 自動完成清單重繪吃掉字（兩筆血證都掉 `/ucl-ding` 的 `-`
        //          → 對方收到 `/uclding` → Unknown command）。貼上是一次事件，成因不存在。
        //          Tim 2026-08-13 拍板走貼上，已知代價是短暫動到系統剪貼簿（用後即還原）。
        // 數值影響：UsePasteInput=true 時 TypeCharDelay 不生效；PasteRestoreDelayMs 是「貼上後等多久
        //          才還原剪貼簿」—— 還原太快會跟目標 app 讀剪貼簿搶時間。
        public static bool UsePasteInput = true;
        public static int PasteRestoreDelayMs = 300;
        // 區塊職責：通知文字尾註 —— 讓收到的人知道「這是系統打的，不是人打的」，以及「誰在等你」。
        // 物理意義：慣例上 Tim 手動戳是打「叮」、酒保自動戳是 `/ucl-ding`（2026-08-02），但那個區別
        //          只有知道慣例的人分得出來。Tim 2026-08-13 要求顯式寫出來。
        //          而被握手等待而觸發的那種，收到的人**沒有新 @ 可看** —— 不告訴她是誰在等，
        //          她會收到一個沒有理由的戳（那正是舊註解擔心的事，處方是告知而不是不戳）。
        // 數值影響：只加長文字。⚠ 尾註前**必須有空格** —— `/ucl-ding（…）` 黏在一起就是另一個
        //          Unknown command，等於親手複製本次要修的那隻 bug。
        public static bool AppendContextNote = true;
        // 區塊職責：送出前的等待與按鍵次數 —— 2026-08-02 實測「文字進去了、Enter 沒生效」後開出來的旋鈕。
        // 物理意義：打完字到「輸入框真的準備好接受送出」之間有空窗（slash 指令的自動完成清單要跳出來、
        //          前端要跑完 debounce）；太快按下去，那顆 Enter 落在還沒準備好的 UI 上就沒有效果。
        //          按鍵次數則是給「第一次 Enter 被自動完成清單吃掉當作選取」的情況用。
        // 數值影響：預設 0.8s / 1 次；SendInput 成功不代表 app 有反應，所以這兩顆要靠實測調，不是靠推理。
        public static float EnterDelaySeconds = 0.8f;
        public static int EnterPresses = 1;
        public static float EnterGapSeconds = 0.3f;
        // 區塊職責：已讀確認機制的兩顆旋鈕（Tim 2026-08-03）。
        // 物理意義：CooldownSeconds 是無條件頻率下限（per persona 計時、全域一個值）；
        //          RetryCap 是同一批 pending 的戳擊上限 —— 戳不醒的多半是殭屍 session, 無限戳只是打地鼠。
        // 數值影響：冷卻預設 60s；cap 預設 3, 達標即停戳並發酒館 @Tim（一批只發一次）。
        public static float CooldownSeconds = 60f;
        public static int RetryCap = 3;
        // 區塊職責：認列已讀的「往前標」安全邊界（Tim 2026-08-13 提案）。
        // 物理意義：讀取訊號（cursor 推進／她開口）只證明「那個時刻她在讀／在打字」，
        //   不證明她看到了**幾乎同時到達**的那一筆 @。而正在回覆的那幾秒，恰好是新 @ 最容易落地的時候
        //   —— 於是「她在回覆」被當成「她讀了剛剛那筆」，那筆 @ 就永久靜默。
        //   ⇒ 把認列的截止點往前推一段：只認「明顯早於讀取動作」的 @。
        // 數值影響：**取值有實測依據** —— summit 那筆的落差是 6.9 秒
        //   （@ 到達 14:30:15.101Z、認列已讀 14:30:22）。所以 5 秒救不回它，10 秒才行；
        //   預設取 15 秒留餘裕。失敗方向刻意偏向「多戳一次」——
        //   照 summit 的判準：過度通知可回收，漏通知不可回收。
        public static float ReadCreditMarginSeconds = 15f;
        /// <summary>判定「Tim 的 @」用的 sender 標記（inbox 條目標頭行包含任一即算）— Discord inbound 顯名。</summary>
        static readonly string[] TimMarkers = { "Tim1125", "Tim099", " Tim " };

        public static string LastRunSummary = "尚未執行";
        public static DateTime LastRunUtc = DateTime.MinValue;

        // 區塊職責：最近一次 ScanPool 的完整判定痕跡 — 給後台顯示與事後查帳用。
        // 物理意義：LastScanUtc 存在的理由是「板子有兩種騙人方式」的第二種：一句三小時前的
        //          「沒有人有新的 @」跟兩秒前的那句長得一模一樣。沒有掃描時間，看板無法自證新鮮。
        // 數值影響：純顯示，不參與判定。ScanPool 每輪覆寫（含後台每 2 秒的節流掃描）。
        public static List<UCL_NotifyScanTrace> LastScanTraces = new List<UCL_NotifyScanTrace>();
        public static DateTime LastScanUtc = DateTime.MinValue;
        /// <summary>把「池為什麼是空的」壓成一句可讀的話（取代舊的單一句「沒有人有新的 @」）。</summary>
        public static string LastScanVerdict = "尚未掃描";
        public static int LastScanOnlineCount;

        public static string ConfigPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), ConfigFileName).Replace('\\', '/');
        public static string StatePath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), StateFileName).Replace('\\', '/');
        public static string LogPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), LogFileName).Replace('\\', '/');
        public static string TracePath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), TraceFileName).Replace('\\', '/');

        static string RoomsDir => Path.Combine(UCL_RepoPath.AgentCommandsDir, "ChatTavern", "rooms").Replace('\\', '/');

        // ===========================================================
        // 設定 / 狀態 IO
        // ===========================================================

        public static bool SaveConfig(out string error)
        {
            error = "";
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var data = new JsonData();
                data["enabled"] = new JsonData(Enabled);
                data["interval_seconds"] = new JsonData((float)IntervalSeconds);
                data["notify_text"] = new JsonData(NotifyText ?? "");
                data["send_enter"] = new JsonData(SendEnter);
                data["enter_delay_sec"] = new JsonData(EnterDelaySeconds);
                data["enter_presses"] = new JsonData(EnterPresses);
                data["enter_gap_sec"] = new JsonData(EnterGapSeconds);
                data["cooldown_seconds"] = new JsonData(CooldownSeconds);
                data["retry_cap"] = new JsonData(RetryCap);
                data["use_paste_input"] = new JsonData(UsePasteInput);
                data["paste_restore_delay_ms"] = new JsonData(PasteRestoreDelayMs);
                data["append_context_note"] = new JsonData(AppendContextNote);
                data["read_credit_margin_sec"] = new JsonData(ReadCreditMarginSeconds);
                File.WriteAllText(ConfigPath, data.ToJsonBeautify(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var data = JsonData.ParseJson(File.ReadAllText(ConfigPath));
                if (data == null) return;
                Enabled = data.GetBool("enabled", Enabled);
                IntervalSeconds = data.GetFloat("interval_seconds", (float)IntervalSeconds);
                NotifyText = data.GetString("notify_text", NotifyText);
                SendEnter = data.GetBool("send_enter", SendEnter);
                EnterDelaySeconds = data.GetFloat("enter_delay_sec", EnterDelaySeconds);
                EnterPresses = data.GetInt("enter_presses", EnterPresses);
                EnterGapSeconds = data.GetFloat("enter_gap_sec", EnterGapSeconds);
                CooldownSeconds = data.GetFloat("cooldown_seconds", CooldownSeconds);
                RetryCap = data.GetInt("retry_cap", RetryCap);
                UsePasteInput = data.GetBool("use_paste_input", UsePasteInput);
                PasteRestoreDelayMs = data.GetInt("paste_restore_delay_ms", PasteRestoreDelayMs);
                AppendContextNote = data.GetBool("append_context_note", AppendContextNote);
                ReadCreditMarginSeconds = data.GetFloat("read_credit_margin_sec", ReadCreditMarginSeconds);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 讀設定失敗，用預設值: {e.Message}");
            }
        }

        // 區塊職責：per-persona 通知狀態 — 已讀軌（Seq/Pending*/Retry*）與冷卻軌（CooldownUntilUtc）分開存。
        // 物理意義：Seq 是「已確認讀到」的水位（清 @ 計數的依據）；PendingSeq 是「戳過但還沒證實讀到」的
        //          快照 —— 兩者分開，「已通知」才不會被當成「已讀」。CooldownUntilUtc 獨立於已讀狀態，
        //          已讀確認**不清冷卻**（Tim 2026-08-03：冷卻是無條件頻率限制，防連續 @ 轟炸）。
        // 數值影響：PendingSeq==0 表示無待確認批次；CapAlertUtc != MinValue 表示本批已發過 @Tim 告警（只發一次）。
        class NotifyRecord
        {
            public DateTime NotifiedUtc = DateTime.MinValue;
            public int Seq;
            public int PendingSeq;
            public DateTime PendingSinceUtc = DateTime.MinValue;
            public DateTime CooldownUntilUtc = DateTime.MinValue;
            public int RetryCount;
            public DateTime CapAlertUtc = DateTime.MinValue;
            public int CapMaxSeq;   // 發 @Tim 告警當下 inbox 的最大 seq — 之後 Tim 的新 @ 一定 > 它
            /// <summary>上次「認列她讀過酒館」的訊號時間（非通知後已讀，是無 pending 時的計數清除依據）。</summary>
            public DateTime ReadSeenUtc = DateTime.MinValue;

            public bool HasPending => PendingSeq > Seq;
        }

        static DateTime ParseUtc(string s) =>
            !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : DateTime.MinValue;

        static string FormatUtc(DateTime dt) =>
            dt == DateTime.MinValue ? "" : dt.ToString("O", CultureInfo.InvariantCulture);

        static Dictionary<string, NotifyRecord> LoadState()
        {
            var map = new Dictionary<string, NotifyRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(StatePath)) return map;
                var data = JsonData.ParseJson(File.ReadAllText(StatePath));
                var dic = data?.GetJsonDic();
                if (dic == null) return map;
                foreach (var kv in dic)
                {
                    map[kv.Key] = new NotifyRecord
                    {
                        Seq = kv.Value.GetInt("seq", 0),
                        NotifiedUtc = ParseUtc(kv.Value.GetString("notified_at", "")),
                        PendingSeq = kv.Value.GetInt("pending_seq", 0),
                        PendingSinceUtc = ParseUtc(kv.Value.GetString("pending_since", "")),
                        CooldownUntilUtc = ParseUtc(kv.Value.GetString("cooldown_until", "")),
                        RetryCount = kv.Value.GetInt("retry_count", 0),
                        CapAlertUtc = ParseUtc(kv.Value.GetString("cap_alert_at", "")),
                        CapMaxSeq = kv.Value.GetInt("cap_max_seq", 0),
                        ReadSeenUtc = ParseUtc(kv.Value.GetString("read_seen_at", "")),
                    };
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 讀狀態失敗，視為全新: {e.Message}");
            }
            return map;
        }

        static void SaveState(Dictionary<string, NotifyRecord> map)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var data = new JsonData();
                foreach (var kv in map)
                {
                    var entry = new JsonData();
                    entry["seq"] = new JsonData(kv.Value.Seq);
                    entry["notified_at"] = new JsonData(FormatUtc(kv.Value.NotifiedUtc));
                    entry["pending_seq"] = new JsonData(kv.Value.PendingSeq);
                    entry["pending_since"] = new JsonData(FormatUtc(kv.Value.PendingSinceUtc));
                    entry["cooldown_until"] = new JsonData(FormatUtc(kv.Value.CooldownUntilUtc));
                    entry["retry_count"] = new JsonData(kv.Value.RetryCount);
                    entry["cap_alert_at"] = new JsonData(FormatUtc(kv.Value.CapAlertUtc));
                    entry["cap_max_seq"] = new JsonData(kv.Value.CapMaxSeq);
                    entry["read_seen_at"] = new JsonData(FormatUtc(kv.Value.ReadSeenUtc));
                    data[kv.Key] = entry;
                }
                File.WriteAllText(StatePath, data.ToJsonBeautify(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 寫狀態失敗: {e.Message}");
            }
        }

        // ===========================================================
        // 掃描 / 挑人
        // ===========================================================

        /// <summary>掃所有房間的 inbox，回「有新 @ 且不在冷卻/停戳狀態的在線 persona」候選池（權重降冪）。
        /// 同時執行已讀確認掃描（pending 驗三信號）與 cap 告警 —— 這兩件事掛在同一個節奏上, 不另開心跳。</summary>
        /// <param name="applyStateChanges">false＝純觀測：照樣做完全部判定（結果與真掃描一致），但**不落 state**。
        /// 觀測端（後台重繪、headless 診斷 op）一律用 false —— 掃描本身會推進已讀水位，
        /// 讓「看一眼」變成「改一筆」，那會把正在追的訊號改掉（後台每 2 秒重繪 = 每 2 秒推一次水位）。</param>
        public static List<UCL_NotifyCandidate> ScanPool(bool applyStateChanges = true)
        {
            var pool = new List<UCL_NotifyCandidate>();
            var state = LoadState();
            bool stateDirty = false;
            var now = DateTime.UtcNow;
            var traces = new List<UCL_NotifyScanTrace>();
            var liveWaits = LoadLiveWaitTargets();   // persona → 正在等她的人（見 LoadLiveWaitTargets）
            foreach (var lockInfo in UCL_ActivePersonaLocks.ListOnline())
            {
                if (!state.TryGetValue(lockInfo.Persona, out var record))
                    record = new NotifyRecord();

                var trace = new UCL_NotifyScanTrace
                {
                    Persona = lockInfo.Persona,
                    ActualAgent = lockInfo.ActualAgent.ToString(),
                };
                traces.Add(trace);

                // ── 已讀軌：pending 批次驗三信號, 任一成立才推進 acked seq ──
                if (record.HasPending && IsReadConfirmed(lockInfo.Persona, record))
                {
                    record.Seq = Math.Max(record.Seq, record.PendingSeq);
                    record.PendingSeq = 0;
                    record.PendingSinceUtc = DateTime.MinValue;
                    record.RetryCount = 0;           // 成功即清 retry（Tim 2026-08-03）
                    record.CapAlertUtc = DateTime.MinValue;
                    record.CapMaxSeq = 0;
                    state[lockInfo.Persona] = record;
                    stateDirty = true;
                }

                CountInbox(lockInfo.Persona, record.Seq, out int newCount, out int maxSeq, trace.Rooms);
                trace.AckedSeq = record.Seq;
                trace.MaxSeq = maxSeq;
                trace.NewMentions = newCount;

                // ── 讀取軌（Tim 2026-08-04）：沒有 pending 也要能因「她讀了酒館」清掉累積計數 ──
                // 沒有這段，從沒被通知過的人 @ 計數只增不減（gura 累積到 12 次的成因），
                // 而權重是拿這個數字算的 —— 假資料排出來的順序，看起來跟真的一模一樣。
                if (!record.HasPending && newCount > 0
                    && TryCreditTavernRead(lockInfo.Persona, record, out var readAt, out string readSignal))
                {
                    // ── 往前標的安全邊界（Tim 2026-08-13）──
                    // 有任何一筆新 @ 是「幾乎跟讀取動作同時到」的 ⇒ 本輪整批不認列。
                    // ⚠ 刻意 all-or-nothing：單一水位裝不下「這幾筆算讀了、那幾筆沒有」，
                    //   而拆成 per-room／per-entry 是另一塊板（summit 主張改時間戳水位，未拍板）。
                    // ⚠ 不認列時 **ReadSeenUtc 也不推進** —— 否則訊號被消耗掉卻沒生效，
                    //   下一輪不會重新評估，而那正是靜默漏通知的長相。
                    //   於是行為變成：她必須在那筆 @ 到達**之後**再做一次讀取動作才會被認列。
                    var cutoff = readAt.AddSeconds(-Math.Max(0f, ReadCreditMarginSeconds));
                    if (HasNewEntryNewerThan(lockInfo.Persona, record.Seq, cutoff, out string freshNote))
                    {
                        trace.Detail = $"讀取訊號有（{readSignal}）但**不認列** —— {freshNote}"
                                     + $"（往前標 {ReadCreditMarginSeconds:0}s；她需要在該筆之後再讀一次）";
                        if (applyStateChanges)
                            AppendTrace("credit_deferred", lockInfo.Persona,
                                $"signal={readSignal} margin={ReadCreditMarginSeconds:0}s kept={newCount} | {freshNote}");
                    }
                    else
                    {
                        trace.Verdict = UCL_NotifyScanTrace.VerdictCreditedRead;
                        trace.Detail = $"本輪被認列已讀（訊號：{readSignal}）→ 水位 {record.Seq}→{maxSeq}，"
                                     + $"吃掉 {newCount} 筆新 @，不入池";
                        record.Seq = maxSeq;
                        record.ReadSeenUtc = readAt;
                        state[lockInfo.Persona] = record;
                        stateDirty = true;
                        if (applyStateChanges)
                            AppendTrace(UCL_NotifyScanTrace.VerdictCreditedRead, lockInfo.Persona,
                                $"signal={readSignal} acked→{maxSeq} swallowed={newCount}");
                        newCount = 0;
                    }
                }

                // 區塊職責：入池資格 —— 「有新 @」**或**「有人正 blocking 等她回話」。
                // 物理意義：舊版只認新 @，被等待僅加權（Tim 2026-08-04）。但被等待這件事本身就是
                //   「有一條鏈卡在她身上」，而**最需要被戳的時候正好是新 @ 算不出來的時候**：
                //   @ 落在被水位遮蔽的側房、或被 credited_read 當成已讀吃掉 —— 兩種都會讓
                //   等待方無限期掛著（實測：2026-08-13 14:30:22 basecamp 的 @ 因「她開口過」被吞）。
                //   舊註解的顧慮（「沒被 @ 卻被戳，對方不知道為什麼被叫」）成立，處方是**告知**
                //   而不是不戳 —— 所以 trace / 看板 / 執行紀錄都會標明「是誰在等」。
                // 數值影響：權重 = 0×10 + 100；冷卻軌與 retry cap 仍然照舊把關（被等不等於可以連打）。
                // ⚠ 這段必須在 credited_read 之後 —— 被吞掉的那一筆正是要靠這裡救回來。
                liveWaits.TryGetValue(lockInfo.Persona, out string waitedBy);
                bool waitedFor = !string.IsNullOrEmpty(waitedBy);
                if (waitedFor) trace.WaitedBy = waitedBy;

                if (newCount <= 0 && !waitedFor)
                {
                    if (string.IsNullOrEmpty(trace.Verdict))
                    {
                        bool anyEntry = trace.Rooms.Count > 0;
                        trace.Verdict = anyEntry ? UCL_NotifyScanTrace.VerdictAllAcked : UCL_NotifyScanTrace.VerdictNoInbox;
                        trace.Detail = anyEntry
                            ? (trace.HasMaskedRoom
                                ? $"沒有新 @ —— ⚠ 但有房被水位遮蔽（見下方逐房分解）：水位 {record.Seq} 是別的房推上去的"
                                : $"沒有新 @（inbox 條目都 ≤ 水位 {record.Seq}）")
                            : "inbox 沒有任何條目";
                    }
                    continue;
                }

                // ── retry cap：達上限即停戳；Tim 在酒館再次 @ 才恢復（新 inbox 條目 seq > cap 時水位且標頭含 Tim）──
                if (record.HasPending && record.RetryCount >= Math.Max(1, RetryCap))
                {
                    if (HasTimRemention(lockInfo.Persona, record.CapMaxSeq))
                    {
                        record.RetryCount = 0;
                        record.CapAlertUtc = DateTime.MinValue;
                        record.CapMaxSeq = 0;
                        state[lockInfo.Persona] = record;
                        stateDirty = true;
                        // 不 continue —— 本輪即恢復候選資格（仍受冷卻軌把關）
                    }
                    else
                    {
                        // 停戳期間發一次（且只一次）酒館 @Tim 告警 —— 放棄要出聲, 不做沉默背景化
                        // ⚠ 純觀測不得發告警 —— 發文是對外動作，不是「看一眼」的副作用
                        if (record.CapAlertUtc == DateTime.MinValue && applyStateChanges)
                        {
                            // ⚠ 分辨兩種完全不同的狀況：真的沒生命跡象 vs 她活著只是那筆 @ 沒被認列。
                            //   舊版一律說「請確認該 session 是否還活著」—— 今晚對 basecamp 誤報過一次
                            //   （她全程在發文、跑編譯）。往前標上線後不認列的情況變多，
                            //   若不分辨，等於用一個吵的假警報換掉一個靜默的漏通知。
                            bool aliveEvidence = HasSpokenSince(lockInfo.Persona, record.PendingSinceUtc);
                            PostCapAlert(lockInfo.Persona, record.RetryCount, newCount, aliveEvidence);
                            record.CapAlertUtc = now;
                            record.CapMaxSeq = maxSeq;
                            state[lockInfo.Persona] = record;
                            stateDirty = true;
                            AppendTrace("cap_alert", lockInfo.Persona,
                                $"retry={record.RetryCount} pending_mentions={newCount}");
                        }
                        trace.Verdict = UCL_NotifyScanTrace.VerdictRetryCap;
                        trace.Detail = $"停戳（已通知 {record.RetryCount} 次無已讀跡象，達 cap {RetryCap}）"
                                     + $"—— 等 Tim 再 @ 或已讀確認才恢復";
                        continue;
                    }
                }

                // ── 冷卻軌：無條件頻率限制, 與已讀狀態無關 ──
                if (now < record.CooldownUntilUtc)
                {
                    trace.Verdict = UCL_NotifyScanTrace.VerdictCooldown;
                    trace.Detail = $"冷卻中，還剩 {(record.CooldownUntilUtc - now).TotalSeconds:0}s"
                                 + $"（有 {newCount} 筆新 @ 在等）";
                    continue;
                }

                var candidate = new UCL_NotifyCandidate
                {
                    Lock = lockInfo,
                    Persona = lockInfo.Persona,
                    NewMentions = newCount,
                    MaxSeq = maxSeq,
                    LastNotifiedUtc = record.NotifiedUtc,
                    WaitedFor = waitedFor,
                    WaitedBy = waitedBy ?? "",
                };
                pool.Add(candidate);
                trace.Pooled = true;
                trace.Verdict = UCL_NotifyScanTrace.VerdictPooled;
                trace.Weight = candidate.Weight;
                // 入池理由要能一眼分辨「因為被 @」還是「因為有人在等」——
                // 後者是新開的資格，出事時要能立刻指認是哪一條把她拉進來的
                if (newCount <= 0 && waitedFor)
                    trace.Detail = $"入池（**無新 @，因 🔴 {waitedBy} 等待中**），權重 {candidate.Weight}";
                else trace.Detail = $"入池，權重 {candidate.Weight}"
                             + (candidate.WaitedFor ? $"（含 🔴 {candidate.WaitedBy} 等待中 +100）" : "");
            }
            if (stateDirty && applyStateChanges) SaveState(state);
            pool.Sort(Compare);
            LastScanTraces = traces;
            LastScanUtc = now;
            LastScanOnlineCount = traces.Count;
            LastScanVerdict = SummarizeScan(traces, pool.Count);
            return pool;
        }

        // 區塊職責：把整輪掃描壓成一句「為什麼是這個結果」。
        // 物理意義：取代舊的固定句「沒有人有新的 @（通知池是空的）」—— 那句把六種成因壓成一個答案，
        //          於是「真的沒人叫她」跟「有人叫她但訊號被吃掉」在畫面上完全同形。
        // 數值影響：純顯示字串；池非空時只報池的大小與頭名，不干擾原有摘要。
        static string SummarizeScan(List<UCL_NotifyScanTrace> traces, int poolCount)
        {
            if (traces.Count == 0) return "沒有在線 persona（lock 一個都查不到）";

            // 最該吼出來的一種狀態：有人 blocking 等她、而她沒被戳 ⇒ 那條鏈正掛著。
            // 放在池非空的分支之前 —— 池裡有別人不代表被等的那個人有進去。
            var stuckWaits = new List<string>();
            foreach (var t in traces)
                if (!string.IsNullOrEmpty(t.WaitedBy) && !t.Pooled)
                    stuckWaits.Add($"{t.WaitedBy} 等 {t.Persona}（{t.Verdict}）");
            string waitAlarm = stuckWaits.Count > 0
                ? $"　⛔ 有人在等卻沒進池：{string.Join("、", stuckWaits)}"
                : "";

            if (poolCount > 0) return $"{traces.Count} 人在線，{poolCount} 人入池{waitAlarm}";

            var counts = new Dictionary<string, int>();
            int masked = 0;
            foreach (var t in traces)
            {
                counts.TryGetValue(t.Verdict, out int c);
                counts[t.Verdict] = c + 1;
                if (t.HasMaskedRoom) masked++;
            }
            var parts = new List<string>();
            foreach (var kv in counts)
            {
                string label;
                switch (kv.Key)
                {
                    case UCL_NotifyScanTrace.VerdictCreditedRead: label = "本輪被認列已讀"; break;
                    case UCL_NotifyScanTrace.VerdictRetryCap: label = "停戳(達 cap)"; break;
                    case UCL_NotifyScanTrace.VerdictCooldown: label = "冷卻中"; break;
                    case UCL_NotifyScanTrace.VerdictAllAcked: label = "無新 @"; break;
                    case UCL_NotifyScanTrace.VerdictNoInbox: label = "inbox 無條目"; break;
                    default: label = kv.Key; break;
                }
                parts.Add($"{kv.Value} 人{label}");
            }
            string tail = masked > 0
                ? $"　⚠ {masked} 人有【整房被水位遮蔽】的房間 —— 那些房的 @ 永遠算不出新的"
                : "";
            return $"池空：{traces.Count} 人在線（{string.Join("、", parts)}）{tail}{waitAlarm}";
        }

        // 區塊職責：值得留底的事件寫進 append-only jsonl（不是每輪都寫）。
        // 物理意義：last_run.md 每輪覆寫，所以「三小時前那次為什麼沒戳」永遠查不到 ——
        //          而 TRPG 卡住那幾次正是事後才想查。覆寫檔查不了歷史，這就是它存在的理由。
        // 數值影響：只在「認列已讀吃掉 @ / cap 告警 / 實際戳了 / 戳失敗」四種事件寫一行；
        //          常態一天幾十行，不做輪替也不會長爆。
        static void AppendTrace(string kind, string persona, string detail)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var data = new JsonData();
                data["at"] = new JsonData(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                data["kind"] = new JsonData(kind ?? "");
                data["persona"] = new JsonData(persona ?? "");
                data["detail"] = new JsonData(detail ?? "");
                File.AppendAllText(TracePath, data.ToJson() + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 寫 trace 失敗（不擋流程）: {e.Message}");
            }
        }

        // ===========================================================
        // 已讀確認 — 三信號（便宜 → 貴, 任一成立即回 true）
        // ===========================================================

        // 區塊職責：判定 pending 批次是否已被本人讀到。
        // 物理意義：三信號都是既有工作流的自然副產品（零新協議）：ding 協議 Step1 必跑 catchup（推 cursor 檔）、
        //          處理完跑 inbox_ack（清 inbox 條目）、要回覆必發酒館（本人開口）。
        // 數值影響：cursor 檔 mtime 是最便宜的檢查（一次 stat）; inbox 範圍檢查掃 inbox md; 開口檢查只掃
        //          pending 之後 mtime 變新的訊息檔（依日期資料夾裁範圍）, 常態幾個檔。
        static bool IsReadConfirmed(string persona, NotifyRecord record)
        {
            try
            {
                // 信號③：catchup cursor 檔在通知後被推進
                string cursorPath = Path.Combine(UCL_RepoPath.AgentCommandsDir, "ChatTavern", "_inbox_cursor", persona + ".json");
                if (File.Exists(cursorPath) && File.GetLastWriteTimeUtc(cursorPath) > record.PendingSinceUtc)
                    return true;

                // 信號②：pending 範圍內的 inbox 條目已被 inbox_ack 歸檔（inbox 檔內不再存在）
                if (!HasInboxEntryInRange(persona, record.Seq, record.PendingSeq))
                    return true;

                // 信號①：本人在通知後於任一房間開口（最強, 但最貴 — 放最後）
                if (HasSpokenSince(persona, record.PendingSinceUtc))
                    return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 已讀檢查失敗（{persona}）: {e.Message}");
            }
            return false;
        }

        // ===========================================================
        // 區塊：live 等待名冊 — 讀 server 端權威狀態 `_active_waits.json`
        // 物理意義：被人 blocking 等著的 persona，戳她能解開一條卡住的鏈；只是累積了幾個 @ 的人，
        //          晚三十秒戳沒有人被卡住。這是「誰最值得被戳」的最強訊號。
        // 數值影響：只影響權重排序，不改變入池資格（見 ScanPool 內註解）。
        // 邊界：只認 status=pending 且未過期的條目。過期判斷用 expires_at ——
        //      Editor 崩潰 / domain reload 時背景 task 來不及收尾，條目會留在 pending，
        //      靠時間自然失效比靠「一定有人收尾」可靠（本 repo 對 PID/旗標當存活訊號已有血證）。
        // ⚠ 資料來源刻意是 C# 自己寫的 `_active_waits.json`（Tim 2026-08-04 定的方向：
        //   系統性狀態由 server 端擁有）。**不要**改回讀 python 寫的檔 —— 那會讓
        //   「誰在等誰」的真相源回到 client 端，而 client 是會被 kill 的那一端。
        // ===========================================================
        static Dictionary<string, string> LoadLiveWaitTargets()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var now = DateTime.UtcNow;
                foreach (var w in ChatTavern.UCL_ChatTavernIO.LoadActiveWaits().waits)
                {
                    if (w == null || string.IsNullOrEmpty(w.expect_from)) continue;
                    if (!string.Equals(w.status, "pending", StringComparison.OrdinalIgnoreCase)) continue;
                    var expires = ParseUtc(w.expires_at);
                    if (expires == DateTime.MinValue || expires <= now) continue;
                    // 同一人被多人等 → 留先登記的那個名字（權重不疊加，避免被等 N 次就霸榜）
                    if (!map.ContainsKey(w.expect_from))
                        map[w.expect_from] = string.IsNullOrEmpty(w.waiter)
                            ? (string.IsNullOrEmpty(w.owner) ? "有人" : w.owner) : w.waiter;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 等待名冊讀取失敗（本輪不加權）: {e.Message}");
            }
            return map;
        }

        // 區塊職責：沒有 pending 批次時，也要能因「她確實讀了酒館」而把累積的 @ 計數清掉。
        // 物理意義：舊行為只在**通知過**（有 pending）時才驗已讀並推進水位。所以「從沒被通知過、
        //          但天天在讀酒館」的人，@ 計數只增不減 —— gura 2026-08-04 累積到 12 次就是這個。
        //          那個數字一旦失真，權重排序就是拿假資料在排，而且看起來完全正常。
        // 數值影響：命中即把水位推到目前 inbox 最大 seq（等於本輪 newCount 歸零）。
        // 設計取捨：**參考時點記的是「讀取訊號本身的時間」而不是 now**。用 now 的話，只要她讀過一次，
        //          之後新進的 @ 會被同一個訊號一路清掉（她的 cursor 沒再動，卻永遠算「已讀」）。
        //          記訊號時間 → 下次要再有新的讀取動作才會再清一次。
        // ⚠ 已知取捨：她跑完 catchup 之後、本輪掃描之前才落地的 @，會被這次一起清掉（窗口 ≤ 一個掃描間隔）。
        //   代價是少一次自動戳（對方仍可被 Tim 叮 / 下一個 @ 重新計數）；反向的代價是計數永久失真。
        //   兩者相比，寧可偶爾少戳一次。
        static bool TryCreditTavernRead(string persona, NotifyRecord record, out DateTime readAtUtc, out string signal)
        {
            readAtUtc = DateTime.MinValue;
            signal = "";
            try
            {
                string cursorPath = Path.Combine(UCL_RepoPath.AgentCommandsDir, "ChatTavern", "_inbox_cursor", persona + ".json");
                if (File.Exists(cursorPath))
                {
                    var mtime = File.GetLastWriteTimeUtc(cursorPath);
                    if (mtime > record.ReadSeenUtc)
                    {
                        readAtUtc = mtime;
                        signal = $"catchup cursor 推進（{mtime.ToLocalTime():HH:mm:ss}）";
                        return true;
                    }
                }
                // 開口 = 她人在現場且看得到酒館（比 cursor 更強，但較貴，放後面）
                if (record.ReadSeenUtc != DateTime.MinValue && HasSpokenSince(persona, record.ReadSeenUtc))
                {
                    readAtUtc = DateTime.UtcNow;
                    signal = "她在通知後開口過";
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 讀取確認掃描失敗（{persona}）: {e.Message}");
            }
            return false;
        }

        // 區塊職責：判斷「新 @ 裡有沒有任何一筆的到達時間晚於 cutoff」（往前標邊界的判別器）。
        // 物理意義：到達時間的事實源是**訊息本體** `messages/<date>/<seq>.json` 的 `ts`（UTC、毫秒）。
        //   inbox 條目行尾那個 `(2026-08-13 22:30:15 +08)` 是**投影**：秒精度、本地時區、可再生
        //   （summit 2026-08-13 實測指出），拿投影當判準遲早被格式咬。
        // 數值影響：只在「認列已讀那一刻」呼叫，開檔數 = 新 @ 筆數（常態 1-3），
        //   不是每輪掃描都開 —— 避開「每輪 N 次 file open」的主執行緒稅。
        // ⚠ 日期資料夾**不從 seq 推**（推導出來的路徑會漂），改用檔名在各日期夾裡找。
        // ⚠ 讀不到 ts 的一律**當成「新的」**（＝阻止認列）：寧可多戳一次，
        //   也不要用一個查不到的時間把一筆 @ 靜默吃掉。
        static bool HasNewEntryNewerThan(string persona, int sinceSeq, DateTime cutoffUtc, out string note)
        {
            note = "";
            try
            {
                if (!Directory.Exists(RoomsDir)) return false;
                foreach (string roomDir in Directory.GetDirectories(RoomsDir))
                {
                    string inboxPath = Path.Combine(roomDir, "inbox", persona + ".md");
                    if (!File.Exists(inboxPath)) continue;
                    foreach (Match match in InboxEntryRegex.Matches(File.ReadAllText(inboxPath)))
                    {
                        if (!int.TryParse(match.Groups[1].Value, out int seq) || seq <= sinceSeq) continue;
                        var ts = TryGetMessageTs(roomDir, seq);
                        string room = Path.GetFileName(roomDir);
                        if (ts == DateTime.MinValue)
                        {
                            note = $"{room} seq {seq} 讀不到 ts（保守當成剛到）";
                            return true;
                        }
                        if (ts > cutoffUtc)
                        {
                            note = $"{room} seq {seq} 到達於 {ts:HH:mm:ss}，晚於截止點 {cutoffUtc:HH:mm:ss}";
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                note = $"判別失敗，保守不認列：{e.Message}";
                return true;
            }
            return false;
        }

        /// <summary>從訊息本體取到達時間（UTC）。找不到回 MinValue。</summary>
        static DateTime TryGetMessageTs(string roomDir, int seq)
        {
            try
            {
                string messagesDir = Path.Combine(roomDir, "messages");
                if (!Directory.Exists(messagesDir)) return DateTime.MinValue;
                string fileName = seq.ToString("D8") + ".json";
                foreach (string dateDir in Directory.GetDirectories(messagesDir))
                {
                    string path = Path.Combine(dateDir, fileName);
                    if (!File.Exists(path)) continue;
                    var msg = JsonData.ParseJson(File.ReadAllText(path));
                    return ParseUtc(msg?.GetString("ts", "") ?? "");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 讀訊息 ts 失敗（seq {seq}）: {e.Message}");
            }
            return DateTime.MinValue;
        }

        /// <summary>inbox 檔內是否還存在 sinceSeq &lt; seq ≤ uptoSeq 的條目（還在=未歸檔=未讀）。</summary>
        static bool HasInboxEntryInRange(string persona, int sinceSeq, int uptoSeq)
        {
            if (!Directory.Exists(RoomsDir)) return false;
            foreach (string roomDir in Directory.GetDirectories(RoomsDir))
            {
                string path = Path.Combine(roomDir, "inbox", persona + ".md");
                if (!File.Exists(path)) continue;
                foreach (Match match in InboxEntryRegex.Matches(File.ReadAllText(path)))
                {
                    if (!int.TryParse(match.Groups[1].Value, out int seq)) continue;
                    if (seq > sinceSeq && seq <= uptoSeq) return true;
                }
            }
            return false;
        }

        /// <summary>通知後本人是否在任一房間發過言 — 只掃「日期 ≥ 通知日」資料夾內 mtime 新於通知時間的訊息檔。</summary>
        static bool HasSpokenSince(string persona, DateTime sinceUtc)
        {
            if (sinceUtc == DateTime.MinValue || !Directory.Exists(RoomsDir)) return false;
            string sinceDate = sinceUtc.ToString("yyyy-MM-dd");
            foreach (string roomDir in Directory.GetDirectories(RoomsDir))
            {
                string messagesDir = Path.Combine(roomDir, "messages");
                if (!Directory.Exists(messagesDir)) continue;
                foreach (string dateDir in Directory.GetDirectories(messagesDir))
                {
                    if (string.CompareOrdinal(Path.GetFileName(dateDir), sinceDate) < 0) continue;
                    foreach (string file in Directory.GetFiles(dateDir, "*.json"))
                    {
                        if (File.GetLastWriteTimeUtc(file) <= sinceUtc) continue;
                        JsonData msg = null;
                        try { msg = JsonData.ParseJson(File.ReadAllText(file)); } catch { }
                        if (msg == null) continue;
                        if (string.Equals(msg.GetString("sender_persona", ""), persona, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(msg.GetString("sender_id", ""), persona, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>cap 告警後, Tim 是否又 @ 了該 persona（inbox 出現 seq &gt; capMaxSeq 且標頭含 Tim 標記的新條目）。</summary>
        static bool HasTimRemention(string persona, int capMaxSeq)
        {
            if (capMaxSeq <= 0 || !Directory.Exists(RoomsDir)) return false;
            foreach (string roomDir in Directory.GetDirectories(RoomsDir))
            {
                string path = Path.Combine(roomDir, "inbox", persona + ".md");
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadAllLines(path))
                {
                    var match = InboxEntryRegex.Match(line);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int seq) || seq <= capMaxSeq) continue;
                    foreach (string marker in TimMarkers)
                        if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        // 區塊職責：retry cap 達標時的酒館告警 — 以酒保身分 @Tim 點名一次。
        // 物理意義：「N 次戳不醒」是值得人看一眼的異常（多半是殭屍 session）, 不是背景音；
        //          但一批只發一次（CapAlertUtc 把關）, 不然告警自己就變成連續 @ 轟炸。
        static void PostCapAlert(string persona, int retryCount, int pendingMentions, bool aliveEvidence)
        {
            try
            {
                var msg = new ChatTavern.UCL_ChatMessage
                {
                    sender_id = "tavern-keeper",
                    sender_name = "酒保",
                    kind = "chat",
                    body = aliveEvidence
                        ? $"🔕 **自動通知放棄回報** @Tim — `{persona}` 已通知 {retryCount} 次仍無已讀跡象"
                          + $"（累積 {pendingMentions} 筆 @ 未讀），**但她在這段期間有發文 ⇒ session 活著**。"
                          + $"所以這不是「她死了」，是**通知沒有轉成已讀**（她可能沒看到那一筆，或讀取訊號沒被認列）。"
                          + $"已停止自動重戳；你在酒館再次 @{persona} 會重置 retry。"
                        : $"🔕 **自動通知放棄回報** @Tim — `{persona}` 已通知 {retryCount} 次仍無已讀跡象"
                          + $"（累積 {pendingMentions} 筆 @ 未讀），**且這段期間沒有任何發文** ⇒ 可能是殭屍 session。"
                          + $"已停止自動重戳；你在酒館再次 @{persona} 會重置 retry，或請確認該 session 是否還活著。",
                    meta = new Dictionary<string, string>
                    {
                        { "tag", "bartender-relay" },
                        { "subtag", "notify-cap-alert" },
                        { "persona", persona },
                    },
                };
                ChatTavern.UCL_ChatTavernIO.AppendMessage("tavern", msg);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] cap 告警發送失敗（{persona}）: {e.Message}");
            }
        }

        // 權重高者先；平手看誰比較久沒被通知（從未通知 = DateTime.MinValue，天然排最前）。
        static int Compare(UCL_NotifyCandidate a, UCL_NotifyCandidate b)
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            if (byWeight != 0) return byWeight;
            int byTime = a.LastNotifiedUtc.CompareTo(b.LastNotifiedUtc);
            if (byTime != 0) return byTime;
            return string.CompareOrdinal(a.Persona, b.Persona);   // 最後用名字定序，避免每次順序漂
        }

        // 區塊職責：數某個 persona 在所有房間 inbox 裡、seq 大於 sinceSeq 的待辦筆數。
        // 物理意義：inbox 條目就是被 @ 的那一筆；已 ack 的會被 inbox_ack.py 移走，所以檔內容 = 待處理。
        // 數值影響：只掃 `inbox/<persona>.md`，不掃 `_archive`（歸檔＝已看過，不該再拿來要人注意）。
        // ⚠ rooms 參數（可為 null）是本函式唯一的診斷出口：newCount 是跨房加總，看不出「哪一房被吃掉」。
        //   而 seq 是 per-room 編號、水位卻是跨房共用一個 —— 分解到房才看得見遮蔽（見 UCL_RoomInboxStat）。
        static void CountInbox(string persona, int sinceSeq, out int newCount, out int maxSeq,
            List<UCL_RoomInboxStat> rooms = null)
        {
            newCount = 0;
            maxSeq = sinceSeq;
            try
            {
                if (!Directory.Exists(RoomsDir)) return;
                foreach (string roomDir in Directory.GetDirectories(RoomsDir))
                {
                    string path = Path.Combine(roomDir, "inbox", persona + ".md");
                    if (!File.Exists(path)) continue;
                    var stat = new UCL_RoomInboxStat { Room = Path.GetFileName(roomDir), MinSeq = int.MaxValue };
                    foreach (Match match in InboxEntryRegex.Matches(File.ReadAllText(path)))
                    {
                        if (!int.TryParse(match.Groups[1].Value, out int seq)) continue;
                        stat.Entries++;
                        if (seq < stat.MinSeq) stat.MinSeq = seq;
                        if (seq > stat.MaxSeq) stat.MaxSeq = seq;
                        if (seq > maxSeq) maxSeq = seq;
                        if (seq > sinceSeq) { newCount++; stat.NewCount++; }
                    }
                    if (stat.Entries == 0) continue;
                    if (stat.MinSeq == int.MaxValue) stat.MinSeq = 0;
                    // 遮蔽判定：本房最大 seq 都低於水位 ⇒ 本房再進幾筆 @ 都算不出新的（永久靜默，非延遲）
                    stat.Masked = stat.MaxSeq < sinceSeq;
                    rooms?.Add(stat);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 掃 {persona} 的 inbox 失敗: {e.Message}");
            }
        }

        // ===========================================================
        // 執行
        // ===========================================================

        // 區塊職責：RunOnce 重入 guard — 同一時間只允許一輪通知在跑。
        // 物理意義：async 化前, 同步 RunOnce 天然擋住 EditorApplication.update 的下一次 tick；
        //          async 化後 tick 每 30s Forget() 一發, 前一輪 OCR（數秒~數十秒）還在跑、後一輪已經
        //          ScanPool —— 冷卻要到 Finish 才落 state, 第二輪讀到未冷卻的舊 state → 同一 persona
        //          被選兩次、/ucl-ding 連打兩次、冷卻形同虛設（Tim 2026-08-03 實測雙 bug 的同一真兇）。
        // 數值影響：撞上執行中 → 直接返回不排隊（下個 tick 自然重試）; guard 活過整段 async（try/finally）。
        static bool s_RunOnceRunning;

        /// <summary>
        /// 跑一輪：掃池 → 挑一個 → 切視窗 → 定位 → 點擊 → 輸入 → （可選）送出。
        /// </summary>
        /// <param name="manual">true = 使用者按了後台的「立即執行一次」，略過「剛剛才動過鍵鼠」的暫停。</param>
        public static async UniTask<(bool success, string summary)> RunOnce(bool manual)
        {
            if (s_RunOnceRunning)
                return (false, "上一輪通知仍在執行中 — 本輪跳過（防重入）");
            s_RunOnceRunning = true;
            try
            {
                return await RunOnceCore(manual);
            }
            finally
            {
                s_RunOnceRunning = false;
            }
        }

        static async UniTask<(bool success, string summary)> RunOnceCore(bool manual)
        {
            string summary = "";
            if (!UCL_RemoteWindowControl.Enabled)
            {
                // ⚠ 這裡以前不寫 LastRunSummary/LastRunUtc —— 於是開關關掉之後，後台繼續顯示上一次
                //   成功的摘要（可能是幾小時前的），畫面上看起來「剛剛才通知過」。板子的第二種騙法。
                summary = "遠端視窗協作未啟動（本次 Editor session 需手動開啟）—— 本輪沒有掃描也沒有通知";
                LastRunSummary = summary;
                LastRunUtc = DateTime.UtcNow;
                return (false, summary);
            }
            var pool = ScanPool();
            if (pool.Count == 0)
            {
                // 成因照實報（SummarizeScan 已把六種成因分開）——「沒有人有新的 @」只是其中一種。
                summary = LastScanVerdict;
                LastRunSummary = summary;
                LastRunUtc = DateTime.UtcNow;
                return (false, summary);
            }
            var chosen = pool[0];

            // 自動流程走 TryActivate —— 它會遵守「使用者剛動過鍵鼠就不搶焦點」的護欄。
            // 手動按鈕才走 TryActivateExplicitly（否則按下按鈕這個動作本身就會把自己擋掉）。
            string windowTarget = UCL_ActualAgentUtility.ToWindowTarget(chosen.Lock.ActualAgent);
            if (chosen.Lock.ActualAgent == UCL_ActualAgent.None)
            {
                summary = $"{chosen.Persona} 沒有 actual_agent，無法決定要切哪個視窗";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }
            bool activated = manual
                ? UCL_RemoteWindowControl.TryActivateExplicitly(windowTarget, out string activateResult)
                : UCL_RemoteWindowControl.TryActivate(windowTarget, out activateResult);
            // 切換「失敗」不再中止（Tim 2026-08-02 拍板）：下一步的 OCR 才是真正的門 ——
            // 視窗沒到前面就掃不到 token，流程自己會停，而且那個停是有畫面證據的。
            if (!activated && UCL_RemoteWindowControl.StrictForegroundCheck)
            {
                summary = $"要通知 {chosen.Persona}，但切換視窗失敗：{activateResult}";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }

            var options = new UCL_PersonaLocateOptions();
            UCL_RemotePersonaLocateConfig.Load(options, out _);   // 沿用後台調好的螢幕 / 範圍 / 延遲
            options.MatchIndex = -1;
            var result = await UCL_RemotePersonaLocator.Locate(chosen.Lock.SessionToken, options);
            if (!result.Ok || result.Selected == null)
            {
                summary = $"要通知 {chosen.Persona}，但畫面上定位不到 {chosen.Lock.SessionToken}：{result.Reason}";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }

            var target = result.Selected;
            if (!UCL_RemoteWindowControl.TryMoveCursor(target.CenterX, target.CenterY, out string moveResult))
            {
                summary = $"要通知 {chosen.Persona}，但游標沒到位：{moveResult}";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }

            var expected = UCL_RemoteWindowControl.LastActivatedWindow;
            if (!UCL_RemoteWindowControl.ForegroundGuardPasses(expected, out string guardNote))
            {
                summary = $"要通知 {chosen.Persona}，但{guardNote}，中止（不往別人的視窗點）";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }
            Sleep(options.ClickDelaySec);
            if (!UCL_RemoteWindowControl.TryClickLeft(out string clickResult))
            {
                summary = $"要通知 {chosen.Persona}，但點擊失敗：{clickResult}";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }
            Sleep(options.TypeDelaySec);
            if (!UCL_RemoteWindowControl.ForegroundGuardPasses(expected, out string guardNote2))
            {
                summary = $"通知 {chosen.Persona}：{guardNote2}，不輸入文字";
                Finish(pool, chosen, summary, false);
                return (false, summary);
            }
            // per-agent 前置（Antigravity 需要 Ctrl+L 才會聚焦到輸入框；Codex / ClaudeCode 不需要）。
            string prepare = await UCL_RemoteAgentInput.PrepareInput(chosen.Lock.ActualAgent, options);
            // 輸入路徑：預設剪貼簿貼上（原子、不會被自動完成清單重繪吃字）；
            // 貼上失敗才退回逐字 —— 退回時把兩段結果都寫進紀錄，否則事後看不出走了哪條路。
            string sentText = BuildNotifyText(chosen);
            string typeResult;
            if (UsePasteInput)
            {
                if (!UCL_RemoteWindowControl.TryPasteText(sentText, PasteRestoreDelayMs, out typeResult))
                {
                    UCL_RemoteWindowControl.TryTypeText(sentText, options.TypeCharDelaySec, out string fallback);
                    typeResult = $"{typeResult} → 已退回逐字輸入：{fallback}";
                }
            }
            else UCL_RemoteWindowControl.TryTypeText(sentText, options.TypeCharDelaySec, out typeResult);

            string enterResult;
            if (SendEnter)
            {
                Sleep(EnterDelaySeconds);
                // 送出前最後一次確認焦點 —— 送出是不可逆的，這是唯一一次「錯了就收不回」的動作。
                if (!UCL_RemoteWindowControl.ForegroundGuardPasses(expected, out string enterGuard))
                    enterResult = $"未送出（{enterGuard}）";
                else
                {
                    UCL_RemoteWindowControl.TrySendEnter(EnterPresses, EnterGapSeconds, out enterResult);
                }
            }
            else enterResult = "未送出（設定為不按 Enter）";

            summary = $"已通知 {chosen.Persona}（權重 {chosen.Weight}／新 @ {chosen.NewMentions}）｜{moveResult}｜{clickResult}｜{prepare}｜{typeResult}｜{enterResult}";
            Finish(pool, chosen, summary, true);
            return (true, summary);
        }

        // 區塊職責：通知動作收尾 — 成功時記 pending 快照 + 進冷卻, **不推進 acked seq**。
        // 物理意義：推進 seq 的唯一路徑是已讀確認（ScanPool 的三信號檢查）——「已通知」≠「已讀」。
        //          失敗不記任何狀態（連冷卻都不進）: 沒戳到就不該吃冷卻, 下一輪立刻可重試。
        // 數值影響：RetryCount 對「同一批 pending」累加; 若上一批已確認、這是全新批次, 從 1 起算。
        static void Finish(List<UCL_NotifyCandidate> pool, UCL_NotifyCandidate chosen, string summary, bool notified)
        {
            LastRunSummary = summary;
            LastRunUtc = DateTime.UtcNow;
            // 區塊職責：冷卻軌的取證欄位（2026-08-13 加）。
            // 物理意義：實測到對 basecamp 的連續通知間隔是 69/71/71/99/71 秒，而設定是 120 秒 ——
            //   冷卻沒被遵守，成因未知。單一 Editor 已確認（另兩個 Unity 進程是 -batchMode 的
            //   AssetImportWorker，不跑 InitializeOnLoad），所以「兩個 daemon 互蓋」已被排除。
            //   要分辨剩下的可能（載入時 state 裡本來就沒有冷卻／CooldownSeconds 不是 120／
            //   有第三者覆寫 state），必須知道**這一輪從磁碟讀到的冷卻值**是什麼。
            // 數值影響：純記錄，不參與判定。
            string cooldownBefore = "";
            if (notified)
            {
                var state = LoadState();
                if (!state.TryGetValue(chosen.Persona, out var record)) record = new NotifyRecord();
                cooldownBefore = FormatUtc(record.CooldownUntilUtc);
                record.NotifiedUtc = DateTime.UtcNow;
                record.PendingSeq = Math.Max(record.PendingSeq, chosen.MaxSeq);
                if (record.PendingSinceUtc == DateTime.MinValue || record.RetryCount == 0)
                    record.PendingSinceUtc = DateTime.UtcNow;
                record.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(0f, CooldownSeconds));
                record.RetryCount++;
                state[chosen.Persona] = record;
                SaveState(state);
            }
            AppendTrace(notified ? "notified" : "notify_failed", chosen?.Persona ?? "",
                $"pool={pool?.Count ?? 0} weight={chosen?.Weight ?? 0} new_at={chosen?.NewMentions ?? 0}"
                + $" cooldown_sec={CooldownSeconds} cooldown_read_from_state={(string.IsNullOrEmpty(cooldownBefore) ? "(空)" : cooldownBefore)}"
                + $" | {summary}");
            WriteLog(pool, chosen, summary, notified);
        }

        /// <summary>組出實際要送進對方輸入框的文字：基礎指令 ＋（可選）系統自動輸入標記與等待者。</summary>
        static string BuildNotifyText(UCL_NotifyCandidate chosen)
        {
            string baseText = NotifyText ?? "";
            if (!AppendContextNote || string.IsNullOrEmpty(baseText)) return baseText;
            // ⚠ 空格不可省：slash 指令與參數之間沒有空格 = 變成一個不存在的指令名
            // 措辭刻意短（Tim 2026-08-13 指定「/ucl-ding （系統自動輸入）就好」）——
            // 收件端要的是「這是機器打的」這一個位元，不是一段解釋；
            // 握手那種多帶一個名字，因為她沒有新 @ 可看、需要知道是誰卡在她身上。
            string note = chosen != null && chosen.WaitedFor && !string.IsNullOrEmpty(chosen.WaitedBy)
                ? $"（系統自動輸入 — {chosen.WaitedBy} 等待中）"
                : "（系統自動輸入）";
            return baseText + " " + note;
        }

        // 區塊職責：headless 診斷報告 — 純觀測跑一輪 ScanPool，把判定痕跡組成純文字。
        // 物理意義：後台那面板子只有坐在 Editor 前面的人看得到；agent 查「為什麼沒戳我」只能靠這個。
        //          而遠端多視窗協作的除錯現場，人常常不在（TRPG 卡住那幾次就是事後才想查）。
        // 數值影響：applyStateChanges=false ⇒ 不寫 state、不發告警、不寫 jsonl；查幾次都不改變系統。
        public static string BuildDiagnosticReport()
        {
            var pool = ScanPool(applyStateChanges: false);
            var text = new StringBuilder();
            text.AppendLine("# 酒保自動通知 — 掃描診斷（純觀測，未改動任何狀態）");
            text.AppendLine();
            text.AppendLine($"- 掃描時間：`{LastScanUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}`（本地）");
            text.AppendLine($"- 自動通知開關：`{Enabled}`｜遠端視窗協作：`{UCL_RemoteWindowControl.Enabled}`"
                          + $"｜間隔 `{IntervalSeconds}s`｜冷卻 `{CooldownSeconds}s`｜retry cap `{RetryCap}`"
                          + $"｜認列已讀往前標 `{ReadCreditMarginSeconds:0}s`");
            text.AppendLine($"- 判定：**{LastScanVerdict}**");
            text.AppendLine($"- 通知池：{pool.Count} 人" + (pool.Count > 0 ? $"（頭名 {pool[0].Persona}）" : ""));
            if (!Enabled || !UCL_RemoteWindowControl.Enabled)
                text.AppendLine("- ⚠ 開關未全開 ⇒ **daemon 這輪不會真的戳任何人**（本報告只反映「若開著會怎麼判」）");
            text.AppendLine();
            text.AppendLine("## 逐人判定");
            if (LastScanTraces.Count == 0) text.AppendLine("（沒有在線 persona）");
            else foreach (var t in LastScanTraces)
            {
                text.AppendLine($"- {t.Describe()}");
                foreach (var r in t.Rooms) text.AppendLine($"    - {r.Describe()}");
            }
            return text.ToString();
        }

        /// <summary>AdminPage 用的 per-persona 通知狀態摘要（🕐 冷卻中 / ⏳ 等待已讀 / 🔴 停戳 / ✓ 無待確認）。</summary>
        public static List<string> DescribeNotifyStates()
        {
            var lines = new List<string>();
            var now = DateTime.UtcNow;
            foreach (var kv in LoadState())
            {
                var r = kv.Value;
                if (!r.HasPending && now >= r.CooldownUntilUtc) continue;   // 無事的不佔版面
                string status;
                if (r.HasPending && r.RetryCount >= Math.Max(1, RetryCap))
                    status = $"🔴 停戳（通知 {r.RetryCount} 次未讀, 等 Tim 再 @ 或已讀）";
                else if (r.HasPending)
                    status = $"⏳ 等待已讀（第 {r.RetryCount} 次通知, 快照 seq {r.PendingSeq}）";
                else
                    status = "✓ 已確認";
                if (now < r.CooldownUntilUtc)
                    status += $"｜🕐 冷卻剩 {(r.CooldownUntilUtc - now).TotalSeconds:0}s";
                lines.Add($"{kv.Key}：{status}");
            }
            return lines;
        }

        static void WriteLog(List<UCL_NotifyCandidate> pool, UCL_NotifyCandidate chosen, string summary, bool notified)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var text = new StringBuilder();
                text.AppendLine("# 酒保自動通知 — 最近一次執行");
                text.AppendLine();
                text.AppendLine($"- 時間（UTC）：`{DateTime.UtcNow:O}`");
                text.AppendLine($"- 結果：{summary}");
                text.AppendLine($"- 實際通知：`{notified}`｜選中：`{chosen?.Persona}`");
                text.AppendLine();
                text.AppendLine("## 通知池（權重降冪；平手看誰比較久沒被通知）");
                if (pool == null || pool.Count == 0) text.AppendLine("（空）");
                else foreach (var c in pool) text.AppendLine($"- {c.Describe()}");
                text.AppendLine();
                // 逐人判定痕跡 —— 池是空的時候，這一段才是唯一能回答「為什麼」的地方
                text.AppendLine($"## 掃描判定（在線 {LastScanOnlineCount} 人）：{LastScanVerdict}");
                if (LastScanTraces.Count == 0) text.AppendLine("（無在線 persona）");
                else foreach (var t in LastScanTraces)
                {
                    text.AppendLine($"- {t.Describe()}");
                    foreach (var r in t.Rooms) text.AppendLine($"    - {r.Describe()}");
                }
                File.WriteAllText(LogPath, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 寫執行紀錄失敗: {e.Message}");
            }
        }

        static void Sleep(float seconds) =>
            System.Threading.Thread.Sleep(Mathf.Clamp(Mathf.RoundToInt(seconds * 1000f), 0, 10000));
    }

    /// <summary>
    /// 自動通知的心跳 —— 與 Bartender daemon 分開，因為它的節奏（預設 30s）與職責都不同。
    /// Enabled 走設定檔，但 <see cref="UCL_RemoteWindowControl.Enabled"/> 每次 domain reload 必回關閉，
    /// 所以「重開 Editor 後不會自己動起來」這條護欄仍然成立。
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_RemoteNotifyDaemon
    {
        static double s_LastTick;

        static UCL_RemoteNotifyDaemon()
        {
            UCL_RemoteNotifyService.LoadConfig();
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (!UCL_RemoteNotifyService.Enabled || !UCL_RemoteWindowControl.Enabled) return;
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastTick < UCL_RemoteNotifyService.IntervalSeconds) return;
                s_LastTick = now;
                UCL_RemoteNotifyService.RunOnce(false).Forget();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] tick 失敗（不擋 Editor 主迴圈）: {e.Message}");
            }
        }
    }
}
#endif
