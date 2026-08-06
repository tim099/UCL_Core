// 區塊職責：Bartender 系統的檔案 IO — load/save triggers.json / time_rules.json / state.json
// 物理意義：所有資料存 <repoRoot>/AgentCommands/ChatTavern/bartender/, 跟 tavern 訊息分目錄
// 設計取捨：用 JsonUtility (對齊 UCL_ChatTavernIO 慣例), 寫入用 atomic .tmp + os.replace pattern
//          避免 daemon tick 跟 Cmd_Bartender 並行寫入互相覆蓋.
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// Bartender 持久化資料的 IO 介面 — triggers / time_rules / state 三檔案.
    /// 根目錄: &lt;repoRoot&gt;/AgentCommands/ChatTavern/bartender/
    /// </summary>
    public static class UCL_BartenderIO
    {
        // 區塊職責: 路徑常數 — 跟 UCL_ChatTavernIO.TavernDirRelative 同層, 在其下再分 bartender/
        public const string BartenderDirRelative = "AgentCommands/ChatTavern/bartender";
        public const string TriggersFile = "triggers.json";
        public const string TimeRulesFile = "time_rules.json";
        public const string StateFile = "state.json";
        public const string AssignmentsFile = "assignments.json";  // T06.2 — task dispatch pending queue

        // 區塊職責：Editor 存活心跳檔（2026-08-04 Tim 提案）
        // 物理意義：daemon 每 HEARTBEAT_INTERVAL 秒複寫這一個檔。**心跳停止 = Editor 的 update
        //          迴圈沒在跑**，最常見原因就是編譯 / domain reload。
        //          讀取端只要 stat 一次 mtime 就知道 Editor 活不活，**不必送 Cmd 等 round-trip**。
        // 設計取捨（Tim 2026-08-04 定調）：固定單檔複寫 + **單行內容只寫這次心跳時間**。
        //          - 不按日分桶：這是「最新一刻」的訊號，歷史沒價值，留檔只會長垃圾。
        //          - 不做 atomic tmp+move：mtime 就是訊號，內容讀到半寫最多這一輪跳過，
        //            下半秒又有新的；為此多兩次檔案操作反而讓心跳自己變成 IO 負擔。
        //          - 單行純文字（不是 JSON）：讀取端 stat mtime 就夠，連 parse 都不必；
        //            內容那行時間是給人眼看的，也讓跨機器讀取不必依賴本機時鐘。
        public const string HeartbeatFile = "_heartbeat.txt";

        // 區塊職責：tick 目前階段檔（2026-08-06，診斷長時間 TickInternal）
        // 物理意義：心跳只能證明 EditorApplication.update 還活著；此檔補上酒保業務 tick
        //          最後成功進入的階段，讓外部在主執行緒卡住時能直接定位等待的是哪個 sweep。
        // 數值影響：每個業務 tick 最多覆寫 4 次、每次兩行 UTF-8 純文字；寫入失敗不影響本業。
        // 設計取捨：不保留歷史、不用 JSON。診斷問題只需要「現在在哪」與進入時間，
        //          固定檔名覆寫可讓外部工具不掃目錄即可讀取最新事實。
        public const string TickStateFile = "_tick_state.txt";

        // 區塊職責：心跳「停跳」台帳（2026-08-05 Tim 提案）
        // 物理意義：心跳停止的那段空隙**本身就是 Editor 凍結過的物證** —— 編譯 / domain reload
        //          會凍住 update 迴圈。`_heartbeat.txt` 只答「現在活不活」，答不出「剛剛凍過沒」；
        //          本檔補的就是後者，讓「08:57 改完檔之後到底有沒有編譯過」變成可 stat 的事實，
        //          而不必人去翻 Editor.log。
        // 數值影響：只在 gap ≥ STALL_THRESHOLD_SECONDS 時寫一行；正常節拍 0.5s 完全不寫。
        //
        // 為什麼是 jsonl 而不是 json：讀取端只要「最後 N 行」，用行切就夠，連 parse 都不必失敗；
        //          單檔 ring 保最近 STALL_KEEP 筆（Tim 原提 3 筆，實作取 10 —— 一個小檔的成本一樣，
        //          而 3 筆會被無關停跳擠掉你真正要查的那一筆）。
        //
        // ⚠ 這個檔證明「凍過」，**不證明「編譯過」**（判準：名字只能叫停跳，不能叫編譯時段）：
        //   domain reload / 資產匯入 / 主執行緒長工 / modal dialog / Editor 失焦降頻 /
        //   **Editor 關閉期間**都會產生停跳。Editor 關了整夜再開，會落一筆數小時的 gap，
        //   而那段時間顯然沒編譯 —— 讀取端要照這個前提解讀，不可反推「有 gap 就有編譯」。
        // ⚠ 停跳只有在**恢復的那一拍**才寫得出來：正在凍結中沒有紀錄，Editor 死掉不再回來則永遠不寫。
        //   **沒有條目 ≠ 沒有停跳**（不會叫的壞掉那一族）。
        public const string StallFile = "_heartbeat_stalls.jsonl";

        // 門檻取 3s：正常節拍 0.5s、alive 判定門檻 1.5s，3s 之上才算異常。
        // 代價說清楚：4 秒級的編譯抓得到，1 秒級的增量編譯抓不到 —— 這是噪音與覆蓋率的交換，不是 bug。
        const double STALL_THRESHOLD_SECONDS = 3.0;
        const int STALL_KEEP = 10;

        // ===========================================================
        // 路徑 helper
        // ===========================================================

        public static string GetBartenderDir()
            => UCL_AgentCommandsPath.ResolveData(BartenderDirRelative);  // 走可 override 資料根;預設與舊行為逐字相同

        public static string GetTriggersPath() => Path.Combine(GetBartenderDir(), TriggersFile);
        public static string GetTimeRulesPath() => Path.Combine(GetBartenderDir(), TimeRulesFile);
        public static string GetStatePath() => Path.Combine(GetBartenderDir(), StateFile);
        public static string GetAssignmentsPath() => Path.Combine(GetBartenderDir(), AssignmentsFile);
        public static string GetHeartbeatPath() => Path.Combine(GetBartenderDir(), HeartbeatFile);
        public static string GetTickStatePath() => Path.Combine(GetBartenderDir(), TickStateFile);
        public static string GetStallPath() => Path.Combine(GetBartenderDir(), StallFile);

        // ===========================================================
        // 區塊職責：寫一拍心跳 —— **單檔、單行、每次複寫**（Tim 2026-08-04 定調）
        // 物理意義：把「我現在還在 tick」這件事變成磁碟上可 stat 的事實。
        // 數值影響：一次 WriteAllText（一行 ISO8601，約 25 bytes）。節流由呼叫端負責，本函式不判頻率。
        // 邊界：**任何失敗都吞掉** —— 心跳只是觀測訊號，絕不可因為寫檔失敗而影響 daemon 本業。
        // ===========================================================
        public static void WriteHeartbeat()
        {
            DateTime prev = default;
            bool hasPrev = false;
            try
            {
                EnsureBartenderDir();
                // 先讀舊那一拍再覆寫 —— 順序不可換，覆寫後就沒有前一拍可比了。
                hasPrev = TryReadHeartbeatUtc(out prev);
                string iso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
                File.WriteAllText(GetHeartbeatPath(), iso, new UTF8Encoding(false));
            }
            catch { /* 觀測訊號寫不進去就算了，不能影響 daemon 本業 */ }

            // 停跳判定獨立一段 try —— 心跳本業已經寫完了，台帳失敗絕不可回頭影響它。
            if (!hasPrev) return;
            try
            {
                double gap = (DateTime.UtcNow - prev).TotalSeconds;
                if (gap >= STALL_THRESHOLD_SECONDS) AppendStall(prev, gap);
            }
            catch { /* 同上 */ }
        }

        // 區塊職責：覆寫 bartender 業務 tick 的目前階段與進入時間。
        // 物理意義：外部讀取端看到非 Idle 且時間長於預期，即可將卡頓縮小到對應的檢查流程。
        // 數值影響：一次 WriteAllText（兩行、約 70 bytes）；刻意不做 atomic 寫入，避免診斷訊號本身增加 IO 延遲。
        // 邊界：觀測檔寫入失敗必須吞掉，不能讓原本要診斷的 tick 因診斷工具而中斷。
        public static void WriteTickState(string state)
        {
            try
            {
                EnsureBartenderDir();
                string body = "State=" + (state ?? string.Empty) + "\n"
                    + "EnteredAtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z\n";
                File.WriteAllText(GetTickStatePath(), body, new UTF8Encoding(false));
            }
            catch { /* 觀測訊號寫不進去就算了，不能影響 daemon 本業 */ }
        }

        // 區塊職責：讀上一拍心跳時間（UTC）
        // 物理意義：**必須從檔案讀，不可用 static 欄位快取。** domain reload 會清掉所有 static，
        //          而 domain reload 正是我們要量的那件事 —— 用 static 就剛好在最該量到的時候失憶，
        //          而且它會「安靜地量不到」（不會叫的壞掉）。
        // 邊界：心跳檔刻意不做 atomic 寫入（見 HeartbeatFile 註解），所以有極小機率讀到半寫的內容。
        //      parse 失敗就當沒有前一拍 —— 下半秒又有新的一拍，漏一次無害；
        //      這裡**不可以**為了「補齊」而猜一個時間，猜出來的 gap 會變成假的物證。
        static bool TryReadHeartbeatUtc(out DateTime utc)
        {
            utc = default;
            string path = GetHeartbeatPath();
            if (!File.Exists(path)) return false;
            string raw = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(raw)) return false;
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out utc);
        }

        // 區塊職責：把一筆停跳 append 進台帳，並裁到最近 STALL_KEEP 筆
        // 物理意義：stalled_since = 最後一拍（凍結開始），resumed_at = 恢復那一拍（凍結結束），
        //          gap_seconds = 兩者之差。三個欄位齊全才能回答「某個時間點之後有沒有凍過」。
        // 數值影響：讀整檔（≤ STALL_KEEP 行）→ 加一行 → 裁切 → atomic 覆寫。
        //          頻率極低（正常節拍完全不觸發），所以這裡值得用 tmp + replace，跟心跳的取捨不同。
        static void AppendStall(DateTime stalledSince, double gapSeconds)
        {
            string path = GetStallPath();
            var lines = new System.Collections.Generic.List<string>();
            if (File.Exists(path))
            {
                foreach (var l in File.ReadAllLines(path))
                {
                    if (!string.IsNullOrWhiteSpace(l)) lines.Add(l);
                }
            }
            string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            lines.Add("{\"stalled_since\":\"" + Iso(stalledSince)
                      + "\",\"resumed_at\":\"" + Iso(DateTime.UtcNow)
                      + "\",\"gap_seconds\":"
                      + gapSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                      + ",\"threshold_seconds\":"
                      + STALL_THRESHOLD_SECONDS.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                      + "}");
            if (lines.Count > STALL_KEEP) lines.RemoveRange(0, lines.Count - STALL_KEEP);

            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static void EnsureBartenderDir()
        {
            string dir = GetBartenderDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // ===========================================================
        // Triggers IO
        // ===========================================================

        // 區塊職責: load triggers.json — 不存在回空 list, 解析失敗 log warning + 回空 list (fail-safe, 不擋 daemon)
        // 數值影響: 純 read, 無副作用; daemon tick 每 N 秒呼叫一次
        public static UCL_BartenderTriggerList LoadTriggers()
        {
            string path = GetTriggersPath();
            if (!File.Exists(path)) return new UCL_BartenderTriggerList();
            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<UCL_BartenderTriggerList>(json)
                           ?? new UCL_BartenderTriggerList();
                if (data.triggers == null) data.triggers = new System.Collections.Generic.List<UCL_BartenderTrigger>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] LoadTriggers fail, 回空: {e.Message}");
                return new UCL_BartenderTriggerList();
            }
        }

        // 區塊職責: 原子寫入 — 先寫 .tmp 再 rename, 避免 daemon 讀到半寫檔
        public static void SaveTriggers(UCL_BartenderTriggerList data)
        {
            EnsureBartenderDir();
            string path = GetTriggersPath();
            string tmp = path + ".tmp";
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ===========================================================
        // TimeRules IO
        // ===========================================================

        public static UCL_BartenderTimeRuleList LoadTimeRules()
        {
            string path = GetTimeRulesPath();
            if (!File.Exists(path)) return new UCL_BartenderTimeRuleList();
            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<UCL_BartenderTimeRuleList>(json)
                           ?? new UCL_BartenderTimeRuleList();
                if (data.rules == null) data.rules = new System.Collections.Generic.List<UCL_BartenderTimeRule>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] LoadTimeRules fail, 回空: {e.Message}");
                return new UCL_BartenderTimeRuleList();
            }
        }

        public static void SaveTimeRules(UCL_BartenderTimeRuleList data)
        {
            EnsureBartenderDir();
            string path = GetTimeRulesPath();
            string tmp = path + ".tmp";
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ===========================================================
        // State IO
        // ===========================================================

        public static UCL_BartenderState LoadState()
        {
            string path = GetStatePath();
            if (!File.Exists(path)) return new UCL_BartenderState();
            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<UCL_BartenderState>(json) ?? new UCL_BartenderState();
                if (data.room_last_seq == null) data.room_last_seq = new System.Collections.Generic.List<UCL_BartenderRoomSeq>();
                if (data.fired_today_keys == null) data.fired_today_keys = new System.Collections.Generic.List<string>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] LoadState fail, 回空: {e.Message}");
                return new UCL_BartenderState();
            }
        }

        public static void SaveState(UCL_BartenderState data)
        {
            EnsureBartenderDir();
            data.last_updated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string path = GetStatePath();
            string tmp = path + ".tmp";
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ===========================================================
        // T06.2 — Assignments IO (Pull model task dispatch pending queue)
        // ===========================================================

        // 區塊職責: load assignments.json — pattern 跟 LoadTriggers 對齊
        // 物理意義: agent 醒來 (awakening.py morning T06.4) 透過此檔 catch-up pending tasks
        // 數值影響: 純 read; 不存在/解析失敗 → 回空 list (fail-safe, 不擋 morning)
        public static UCL_BartenderAssignmentList LoadAssignments()
        {
            string path = GetAssignmentsPath();
            if (!File.Exists(path)) return new UCL_BartenderAssignmentList();
            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<UCL_BartenderAssignmentList>(json)
                           ?? new UCL_BartenderAssignmentList();
                if (data.pending == null) data.pending = new System.Collections.Generic.List<UCL_BartenderAssignment>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] LoadAssignments fail, 回空: {e.Message}");
                return new UCL_BartenderAssignmentList();
            }
        }

        // 區塊職責: 原子寫入 assignments.json
        public static void SaveAssignments(UCL_BartenderAssignmentList data)
        {
            EnsureBartenderDir();
            string path = GetAssignmentsPath();
            string tmp = path + ".tmp";
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// T06.2 — 新增 assignment 進 pending queue. 回傳 assignment_id.
        /// 用於 Cmd_Bartender.Op_AssignAdd.
        /// </summary>
        public static string RegisterAssignment(
            string targetPersona, string taskBody, string supervisor,
            int rewardTokens, string deadline)
        {
            string id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var entry = new UCL_BartenderAssignment
            {
                assignment_id = id,
                target_persona = targetPersona,
                task_body = taskBody,
                supervisor = supervisor,
                reward_tokens = System.Math.Max(0, rewardTokens),
                deadline = deadline ?? "",
                created_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                status = "pending",
            };
            var data = LoadAssignments();
            data.pending.Add(entry);
            SaveAssignments(data);
            return id;
        }

        // ===========================================================
        // 區塊：Shared register helper — Cmd_Bartender + Daemon inline parser 共用
        // 物理意義：建構 UCL_BartenderTrigger / UCL_BartenderTimeRule + atomic 寫入 triggers.json / time_rules.json
        // 設計取捨：把 register 邏輯集中, 確保 inline parse 跟 CMD 走完全一樣的程式碼路徑 (per Tim spec)
        // ===========================================================

        /// <summary>
        /// 註冊新 trigger — 構建 entry + persist. 回傳 generated id (8-hex).
        /// 用於 Cmd_Bartender.Op_Add + Daemon inline [進行留言] 解析共用底層.
        /// </summary>
        public static string RegisterTrigger(
            string creatorId, string creatorName,
            System.Collections.Generic.List<string> targets,
            string keyword, string message,
            int tokens, string room)
        {
            string id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var trigger = new UCL_BartenderTrigger
            {
                id = id,
                creator_id = creatorId,
                creator_name = string.IsNullOrEmpty(creatorName) ? creatorId : creatorName,
                targets = targets ?? new System.Collections.Generic.List<string>(),
                keyword = keyword,
                message = message,
                remaining_triggers = System.Math.Max(1, tokens),
                initial_tokens = System.Math.Max(1, tokens),
                created_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                target_room = string.IsNullOrEmpty(room) ? "tavern" : room,
            };
            var data = LoadTriggers();
            data.triggers.Add(trigger);
            SaveTriggers(data);
            return id;
        }

        /// <summary>
        /// 註冊新 time rule — 構建 entry + persist (同 id 覆寫). 回傳 rule id.
        /// </summary>
        public static string RegisterTimeRule(
            string id, string timeHHmm, string targetId, string reminderMsg,
            int graceMinutes, bool penaltyEnabled, int penaltyIntervalMinutes,
            string penaltyTarget, string room)
        {
            var rule = new UCL_BartenderTimeRule
            {
                id = id,
                time_hhmm = timeHHmm,
                target_id = targetId,
                reminder_msg = reminderMsg,
                grace_minutes = System.Math.Max(0, graceMinutes),
                penalty_enabled = penaltyEnabled,
                penalty_interval_minutes = System.Math.Max(1, penaltyIntervalMinutes),
                penalty_target = string.IsNullOrEmpty(penaltyTarget) ? targetId : penaltyTarget,
                target_room = string.IsNullOrEmpty(room) ? "tavern" : room,
                enabled = true,
            };
            var data = LoadTimeRules();
            // 同 id 覆寫 (idempotent register)
            data.rules.RemoveAll(r => r != null && r.id == id);
            data.rules.Add(rule);
            SaveTimeRules(data);
            return id;
        }

        // ===========================================================
        // State helper — per-room last_seq 取/設
        // ===========================================================

        public static int GetLastSeq(UCL_BartenderState state, string roomId)
        {
            if (state?.room_last_seq == null) return 0;
            foreach (var entry in state.room_last_seq)
            {
                if (entry != null && entry.room_id == roomId) return entry.last_seq;
            }
            return 0;
        }

        public static void SetLastSeq(UCL_BartenderState state, string roomId, int seq)
        {
            if (state.room_last_seq == null) state.room_last_seq = new System.Collections.Generic.List<UCL_BartenderRoomSeq>();
            foreach (var entry in state.room_last_seq)
            {
                if (entry != null && entry.room_id == roomId)
                {
                    entry.last_seq = seq;
                    return;
                }
            }
            state.room_last_seq.Add(new UCL_BartenderRoomSeq { room_id = roomId, last_seq = seq });
        }
    }
}
#endif
