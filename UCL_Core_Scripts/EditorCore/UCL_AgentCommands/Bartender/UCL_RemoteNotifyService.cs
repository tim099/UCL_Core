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

        /// <summary>權重 = 新 @ 次數 × 10（Tim 2026-08-02 給的尺：2 次→20、1 次→10）。</summary>
        public int Weight => NewMentions * 10;

        public string Describe()
        {
            string last = LastNotifiedUtc == DateTime.MinValue
                ? "從未通知"
                : $"上次 {LastNotifiedUtc.ToLocalTime():MM-dd HH:mm}";
            return $"{Persona}：新 @ {NewMentions} 次 → 權重 {Weight}｜{last}";
        }
    }

    public static class UCL_RemoteNotifyService
    {
        const string ConfigFileName = "remote_notify_config.json";
        const string StateFileName = "remote_notify_state.json";
        const string LogFileName = "remote_notify_last_run.md";
        static readonly Regex InboxEntryRegex = new Regex(@"^##\s*\[seq=(\d+)\]", RegexOptions.Multiline);

        public static bool Enabled;
        public static double IntervalSeconds = 30;
        // ⚠ 預設文字刻意是 `/ucl-ding` 而不是「叮」：Tim 手動戳是打「叮」，酒保自動戳是 `/ucl-ding`
        //   （Tim 2026-08-02 定的慣例）。改成一樣的字，收到的人就分不出這次是人在叫還是機器在叫。
        public static string NotifyText = "/ucl-ding";
        /// <summary>是否在輸入完成後送出。關閉時只把文字打進去、停在送出前一步。</summary>
        public static bool SendEnter = true;
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
        /// <summary>判定「Tim 的 @」用的 sender 標記（inbox 條目標頭行包含任一即算）— Discord inbound 顯名。</summary>
        static readonly string[] TimMarkers = { "Tim1125", "Tim099", " Tim " };

        public static string LastRunSummary = "尚未執行";
        public static DateTime LastRunUtc = DateTime.MinValue;

        public static string ConfigPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), ConfigFileName).Replace('\\', '/');
        public static string StatePath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), StateFileName).Replace('\\', '/');
        public static string LogPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), LogFileName).Replace('\\', '/');

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
        public static List<UCL_NotifyCandidate> ScanPool()
        {
            var pool = new List<UCL_NotifyCandidate>();
            var state = LoadState();
            bool stateDirty = false;
            var now = DateTime.UtcNow;
            foreach (var lockInfo in UCL_ActivePersonaLocks.ListOnline())
            {
                if (!state.TryGetValue(lockInfo.Persona, out var record))
                    record = new NotifyRecord();

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

                CountInbox(lockInfo.Persona, record.Seq, out int newCount, out int maxSeq);
                if (newCount <= 0) continue;

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
                        if (record.CapAlertUtc == DateTime.MinValue)
                        {
                            PostCapAlert(lockInfo.Persona, record.RetryCount, newCount);
                            record.CapAlertUtc = now;
                            record.CapMaxSeq = maxSeq;
                            state[lockInfo.Persona] = record;
                            stateDirty = true;
                        }
                        continue;
                    }
                }

                // ── 冷卻軌：無條件頻率限制, 與已讀狀態無關 ──
                if (now < record.CooldownUntilUtc) continue;

                pool.Add(new UCL_NotifyCandidate
                {
                    Lock = lockInfo,
                    Persona = lockInfo.Persona,
                    NewMentions = newCount,
                    MaxSeq = maxSeq,
                    LastNotifiedUtc = record.NotifiedUtc,
                });
            }
            if (stateDirty) SaveState(state);
            pool.Sort(Compare);
            return pool;
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
        static void PostCapAlert(string persona, int retryCount, int pendingMentions)
        {
            try
            {
                var msg = new ChatTavern.UCL_ChatMessage
                {
                    sender_id = "tavern-keeper",
                    sender_name = "酒保",
                    kind = "chat",
                    body = $"🔕 **自動通知放棄回報** @Tim — `{persona}` 已通知 {retryCount} 次仍無已讀跡象"
                         + $"（累積 {pendingMentions} 筆 @ 未讀）。已停止自動重戳；"
                         + $"你在酒館再次 @{persona} 會重置 retry 恢復通知，或請確認該 session 是否還活著。",
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
        static void CountInbox(string persona, int sinceSeq, out int newCount, out int maxSeq)
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
                    foreach (Match match in InboxEntryRegex.Matches(File.ReadAllText(path)))
                    {
                        if (!int.TryParse(match.Groups[1].Value, out int seq)) continue;
                        if (seq > maxSeq) maxSeq = seq;
                        if (seq > sinceSeq) newCount++;
                    }
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
                summary = "遠端視窗協作未啟動（本次 Editor session 需手動開啟）";
                return (false, summary);
            }
            var pool = ScanPool();
            if (pool.Count == 0)
            {
                summary = "沒有人有新的 @（通知池是空的）";
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
            UCL_RemoteWindowControl.TryTypeText(NotifyText, options.TypeCharDelaySec, out string typeResult);

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
            if (notified)
            {
                var state = LoadState();
                if (!state.TryGetValue(chosen.Persona, out var record)) record = new NotifyRecord();
                record.NotifiedUtc = DateTime.UtcNow;
                record.PendingSeq = Math.Max(record.PendingSeq, chosen.MaxSeq);
                if (record.PendingSinceUtc == DateTime.MinValue || record.RetryCount == 0)
                    record.PendingSinceUtc = DateTime.UtcNow;
                record.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(0f, CooldownSeconds));
                record.RetryCount++;
                state[chosen.Persona] = record;
                SaveState(state);
            }
            WriteLog(pool, chosen, summary, notified);
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
