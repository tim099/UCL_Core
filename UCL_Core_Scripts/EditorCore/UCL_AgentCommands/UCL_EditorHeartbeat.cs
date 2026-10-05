// 區塊職責：**Editor 心跳** —— 每 0.5 秒寫一拍 `_heartbeat.txt`，凍超過 3 秒記一筆 `_heartbeat_stalls.jsonl`（TASK-0365）。
// 物理意義：心跳綁在 EditorApplication.update ⇒ 編譯／domain reload／主執行緒卡住時它就停 ⇒ 外面 stat 這個檔就知道 Editor 活不活、凍過沒。
//           原本寫在酒保 daemon 裡（刻意在酒保總開關之前跳）；酒保搬到 Senate、Unity 端整個廢棄之後，只有這一塊留在 Editor（Tim 2026-10-05）。
//           ⇒ 跟任何開關脫鉤：Editor 開著就跳。
// 數值影響：路徑、檔名、格式、門檻**逐字沿用**原本寫在酒保 daemon 裡的那一份 —— 讀它的人不必跟著改：
//           Senate `ProjectProbe`（專案頁／Doctor「Editor 在跑」，4 秒判準）、`Cmd_Goodnight.EditorAlive`、`AgentCmdClient`（ucmd 逾時後 stat 它）、
//           skill `ucl-compile-error`（>1.5 秒沒動＝沒在 tick；併讀停跳台帳）。
//           ⚠ 路徑要不要搬出 AgentCommands（TASK-0365 §E1 提議 `Library/UCL/`）待 Tim 定案；搬的話上面那幾個讀取端同一批改。
// 邊界：**任何失敗都吞掉** —— 診斷工具不可以是 Editor 的失敗來源。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;

namespace UCL.Core.EditorLib.AgentCommands
{
    [InitializeOnLoad]
    public static class UCL_EditorHeartbeat
    {
        public const string DirRelative = "AgentCommands/ChatTavern/bartender";   // 沿用舊路徑（讀取端寫死在這裡）
        public const string HeartbeatFile = "_heartbeat.txt";
        public const string StallFile = "_heartbeat_stalls.jsonl";
        const double IntervalSeconds = 0.5;
        const double StallThresholdSeconds = 3.0;
        const int StallKeep = 10;

        static double s_LastBeat;

        static UCL_EditorHeartbeat()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static string GetDir() => UCL_AgentCommandsPath.ResolveData(DirRelative);
        public static string GetHeartbeatPath() => Path.Combine(GetDir(), HeartbeatFile);
        public static string GetStallPath() => Path.Combine(GetDir(), StallFile);

        static void Tick()
        {
            double aNow = EditorApplication.timeSinceStartup;
            if (aNow - s_LastBeat < IntervalSeconds) return;
            s_LastBeat = aNow;
            Beat();
        }

        // 一拍：單檔、單行、每次複寫（刻意不做 atomic —— 讀到半寫的內容時讀取端當沒有那一拍，下半秒又有新的）。
        static void Beat()
        {
            DateTime aPrev = default;
            bool aHasPrev = false;
            try
            {
                Directory.CreateDirectory(GetDir());
                // 先讀舊那一拍再覆寫 —— 順序不可換，覆寫後就沒有前一拍可比了。
                aHasPrev = TryReadHeartbeatUtc(out aPrev);
                File.WriteAllText(GetHeartbeatPath(), Iso(DateTime.UtcNow), new UTF8Encoding(false));
            }
            catch { /* 觀測訊號寫不進去就算了 */ }

            if (!aHasPrev) return;
            try
            {
                double aGap = (DateTime.UtcNow - aPrev).TotalSeconds;
                if (aGap >= StallThresholdSeconds) AppendStall(aPrev, aGap);
            }
            catch { /* 台帳失敗不可回頭影響心跳 */ }
        }

        static string Iso(DateTime iUtc) => iUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

        // 上一拍**必須從檔案讀**，不可用 static 快取：domain reload 會清掉 static，而 domain reload 正是要量的那件事。
        static bool TryReadHeartbeatUtc(out DateTime oUtc)
        {
            oUtc = default;
            string aPath = GetHeartbeatPath();
            if (!File.Exists(aPath)) return false;
            string aRaw = File.ReadAllText(aPath).Trim();
            if (string.IsNullOrEmpty(aRaw)) return false;
            return DateTime.TryParse(aRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oUtc);
        }

        // 一筆停跳：stalled_since（最後一拍）／resumed_at（恢復那一拍）／gap_seconds，裁到最近 StallKeep 筆；tmp ＋ Replace 寫（頻率極低，值得）。
        static void AppendStall(DateTime iStalledSince, double iGapSeconds)
        {
            string aPath = GetStallPath();
            var aLines = new List<string>();
            if (File.Exists(aPath))
                foreach (string l in File.ReadAllLines(aPath))
                    if (!string.IsNullOrWhiteSpace(l)) aLines.Add(l);
            aLines.Add("{\"stalled_since\":\"" + Iso(iStalledSince)
                       + "\",\"resumed_at\":\"" + Iso(DateTime.UtcNow)
                       + "\",\"gap_seconds\":" + iGapSeconds.ToString("F3", CultureInfo.InvariantCulture)
                       + ",\"threshold_seconds\":" + StallThresholdSeconds.ToString("F1", CultureInfo.InvariantCulture)
                       + "}");
            if (aLines.Count > StallKeep) aLines.RemoveRange(0, aLines.Count - StallKeep);
            string aTmp = aPath + ".tmp";
            File.WriteAllLines(aTmp, aLines, new UTF8Encoding(false));
            // ⛔ 不先 Delete 再 Move：兩步之間檔案不存在，看起來就跟「從來沒凍過」一樣
            if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
            else File.Move(aTmp, aPath);
        }
    }
}
#endif
