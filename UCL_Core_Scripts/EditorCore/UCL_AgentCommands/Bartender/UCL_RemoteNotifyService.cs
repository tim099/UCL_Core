// 區塊職責：酒保自動通知 — 定期掃在線 persona 的收信匣，依權重挑一個，走遠端路由把 ding 送到她的視窗。
// 物理意義：@ 進了 inbox 只是「訊息躺在那裡」，被 @ 的人不會知道；這條線把「有人叫你」變成她桌面上真的
//          被戳一下。這是整條遠端路由裡**唯一會按 Enter** 的流程 —— 因為它的目的就是替使用者送出。
// 數值影響：每輪只通知一個 persona（權重最高者），避免一次搶好幾個視窗；被通知後該 persona 的
//          last_notified_seq 推進，同一批 @ 不會每 30 秒重戳。
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
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] 讀設定失敗，用預設值: {e.Message}");
            }
        }

        // 每個 persona 記兩件事：上次通知時間（權重平手時的裁判）與上次通知到第幾個 seq
        // （沒有它，同一批 @ 會每輪重新算成「新的」，變成每 30 秒戳一次同一個人）。
        class NotifyRecord { public DateTime NotifiedUtc = DateTime.MinValue; public int Seq; }

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
                    var rec = new NotifyRecord { Seq = kv.Value.GetInt("seq", 0) };
                    string at = kv.Value.GetString("notified_at", "");
                    if (!string.IsNullOrEmpty(at) && DateTime.TryParse(at, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                        rec.NotifiedUtc = parsed;
                    map[kv.Key] = rec;
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
                    entry["notified_at"] = new JsonData(kv.Value.NotifiedUtc == DateTime.MinValue
                        ? "" : kv.Value.NotifiedUtc.ToString("O", CultureInfo.InvariantCulture));
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

        /// <summary>掃所有房間的 inbox，回「有新 @ 的在線 persona」候選池（權重降冪）。</summary>
        public static List<UCL_NotifyCandidate> ScanPool()
        {
            var pool = new List<UCL_NotifyCandidate>();
            var state = LoadState();
            foreach (var lockInfo in UCL_ActivePersonaLocks.ListOnline())
            {
                state.TryGetValue(lockInfo.Persona, out var record);
                int since = record?.Seq ?? 0;
                CountInbox(lockInfo.Persona, since, out int newCount, out int maxSeq);
                if (newCount <= 0) continue;
                pool.Add(new UCL_NotifyCandidate
                {
                    Lock = lockInfo,
                    Persona = lockInfo.Persona,
                    NewMentions = newCount,
                    MaxSeq = maxSeq,
                    LastNotifiedUtc = record?.NotifiedUtc ?? DateTime.MinValue,
                });
            }
            pool.Sort(Compare);
            return pool;
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

        /// <summary>
        /// 跑一輪：掃池 → 挑一個 → 切視窗 → 定位 → 點擊 → 輸入 → （可選）送出。
        /// </summary>
        /// <param name="manual">true = 使用者按了後台的「立即執行一次」，略過「剛剛才動過鍵鼠」的暫停。</param>
        public static bool RunOnce(bool manual, out string summary)
        {
            summary = "";
            if (!UCL_RemoteWindowControl.Enabled)
            {
                summary = "遠端視窗協作未啟動（本次 Editor session 需手動開啟）";
                return false;
            }
            var pool = ScanPool();
            if (pool.Count == 0)
            {
                summary = "沒有人有新的 @（通知池是空的）";
                LastRunSummary = summary;
                LastRunUtc = DateTime.UtcNow;
                return false;
            }
            var chosen = pool[0];

            // 自動流程走 TryActivate —— 它會遵守「使用者剛動過鍵鼠就不搶焦點」的護欄。
            // 手動按鈕才走 TryActivateExplicitly（否則按下按鈕這個動作本身就會把自己擋掉）。
            string windowTarget = UCL_ActualAgentUtility.ToWindowTarget(chosen.Lock.ActualAgent);
            if (chosen.Lock.ActualAgent == UCL_ActualAgent.None)
            {
                summary = $"{chosen.Persona} 沒有 actual_agent，無法決定要切哪個視窗";
                Finish(pool, chosen, summary, false);
                return false;
            }
            bool activated = manual
                ? UCL_RemoteWindowControl.TryActivateExplicitly(windowTarget, out string activateResult)
                : UCL_RemoteWindowControl.TryActivate(windowTarget, out activateResult);
            if (!activated)
            {
                summary = $"要通知 {chosen.Persona}，但切換視窗失敗：{activateResult}";
                Finish(pool, chosen, summary, false);
                return false;
            }

            var options = new UCL_PersonaLocateOptions();
            UCL_RemotePersonaLocateConfig.Load(options, out _);   // 沿用後台調好的螢幕 / 範圍 / 延遲
            options.MatchIndex = -1;
            var result = UCL_RemotePersonaLocator.Locate(chosen.Lock.SessionToken, options);
            if (!result.Ok || result.Selected == null)
            {
                summary = $"要通知 {chosen.Persona}，但畫面上定位不到 {chosen.Lock.SessionToken}：{result.Reason}";
                Finish(pool, chosen, summary, false);
                return false;
            }

            var target = result.Selected;
            if (!UCL_RemoteWindowControl.TryMoveCursor(target.CenterX, target.CenterY, out string moveResult))
            {
                summary = $"要通知 {chosen.Persona}，但游標沒到位：{moveResult}";
                Finish(pool, chosen, summary, false);
                return false;
            }

            var expected = UCL_RemoteWindowControl.LastActivatedWindow;
            if (!UCL_RemoteWindowControl.IsForeground(expected))
            {
                summary = $"要通知 {chosen.Persona}，但前景已變成 {UCL_RemoteWindowControl.DescribeForeground()}，中止（不往別人的視窗點）";
                Finish(pool, chosen, summary, false);
                return false;
            }
            Sleep(options.ClickDelaySec);
            if (!UCL_RemoteWindowControl.TryClickLeft(out string clickResult))
            {
                summary = $"要通知 {chosen.Persona}，但點擊失敗：{clickResult}";
                Finish(pool, chosen, summary, false);
                return false;
            }
            Sleep(options.TypeDelaySec);
            if (!UCL_RemoteWindowControl.IsForeground(expected))
            {
                summary = $"通知 {chosen.Persona}：點擊後前景變成 {UCL_RemoteWindowControl.DescribeForeground()}，不輸入文字";
                Finish(pool, chosen, summary, false);
                return false;
            }
            // per-agent 前置（Antigravity 需要 Ctrl+L 才會聚焦到輸入框；Codex / ClaudeCode 不需要）。
            string prepare = UCL_RemoteAgentInput.PrepareInput(chosen.Lock.ActualAgent, options);
            UCL_RemoteWindowControl.TryTypeText(NotifyText, options.TypeCharDelaySec, out string typeResult);

            string enterResult;
            if (SendEnter)
            {
                Sleep(EnterDelaySeconds);
                // 送出前最後一次確認焦點 —— 送出是不可逆的，這是唯一一次「錯了就收不回」的動作。
                if (!UCL_RemoteWindowControl.IsForeground(expected))
                    enterResult = $"未送出（送出前前景變成 {UCL_RemoteWindowControl.DescribeForeground()}）";
                else
                {
                    UCL_RemoteWindowControl.TrySendEnter(EnterPresses, EnterGapSeconds, out enterResult);
                }
            }
            else enterResult = "未送出（設定為不按 Enter）";

            summary = $"已通知 {chosen.Persona}（權重 {chosen.Weight}／新 @ {chosen.NewMentions}）｜{moveResult}｜{clickResult}｜{prepare}｜{typeResult}｜{enterResult}";
            Finish(pool, chosen, summary, true);
            return true;
        }

        // 只有真的走完通知動作才推進 last_notified_seq —— 失敗也推進的話，那批 @ 就永遠不會再被通知，
        // 而且失敗是靜默的（沒有人會來說「我沒被戳到」）。
        static void Finish(List<UCL_NotifyCandidate> pool, UCL_NotifyCandidate chosen, string summary, bool notified)
        {
            LastRunSummary = summary;
            LastRunUtc = DateTime.UtcNow;
            if (notified)
            {
                var state = LoadState();
                state[chosen.Persona] = new NotifyRecord { NotifiedUtc = DateTime.UtcNow, Seq = chosen.MaxSeq };
                SaveState(state);
            }
            WriteLog(pool, chosen, summary, notified);
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
                UCL_RemoteNotifyService.RunOnce(false, out _);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteNotify] tick 失敗（不擋 Editor 主迴圈）: {e.Message}");
            }
        }
    }
}
#endif
