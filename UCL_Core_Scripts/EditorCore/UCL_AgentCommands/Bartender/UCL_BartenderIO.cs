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

        // ===========================================================
        // 路徑 helper
        // ===========================================================

        public static string GetBartenderDir()
            => Path.Combine(UCL_RepoPath.RepoRoot, BartenderDirRelative);

        public static string GetTriggersPath() => Path.Combine(GetBartenderDir(), TriggersFile);
        public static string GetTimeRulesPath() => Path.Combine(GetBartenderDir(), TimeRulesFile);
        public static string GetStatePath() => Path.Combine(GetBartenderDir(), StateFile);

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
