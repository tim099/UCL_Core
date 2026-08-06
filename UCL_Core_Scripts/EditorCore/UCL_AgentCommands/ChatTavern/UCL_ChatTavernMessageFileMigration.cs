// 區塊職責：訊息檔名 migration — 舊格式（HHMMSS_ms_uuid.json）改名為全域 seq（00000001.json）。
// 物理意義：把「seq 靠排序位置算出來」變成「seq 直接寫在檔名上」。
//          改名**照 GetOrderedMessageFilePaths 的既有順序**逐一指派，
//          所以排序結果與 seq 的對應關係一個都不動 —— 改的是「怎麼知道 seq」，不是 seq 本身。
// 數值影響：Plan 完全唯讀。Apply 只改**檔名**，不碰任何檔案內容、不動任何 git 狀態。
//
// 為什麼在 C# 而不是 python（Tim 2026-08-06 拍板）：
//   整個 migration 的正確性押在「排序與 C# 端一致」這件事上。python 版是**複製**了
//   GetSortedMessageFiles 的規則（GetFiles(AllDirectories) → 去 root 前綴 → '\'→'/' → Ordinal），
//   驗過當下一致，但那是「今天一致」不是「永遠一致」——
//   哪天本體改了排序規則，複製的那份不會知道，而症狀是 seq 全體位移、外觀完全正常。
//   本檔直接呼叫 `UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths()` 本人，
//   **不是複製規則，是用同一個函式** —— 那一整類漂移從定義上消失。
//   代價：只能在 Editor 內跑，沒有 headless / CI 入口（一次性 + 人工觸發，可接受）。
//
// 為什麼不呼叫 git mv：git 靠**內容比對**自己認得出改名（同內容 = 100% 相似度），
//   純 File.Move + `git add -A` 之後 status 就是 `R`（2026-08-06 實測）。
//   少開一顆 process 就少一整族問題（登記 / 編碼 / deadlock / 逾時）。
//
// ⚠ 執行前必須關閉聊天酒館系統總開關（UCL_ControlPanelPage）。
//   改名會動到日期目錄 mtime → 檔案清單快取失效 → daemon 重新列舉，
//   而**改名進行中**那個窗口的排序是半舊半新的，seq 對應會暫時錯亂：
//   bartender 可能對舊訊息誤觸發 keyword trigger（會真的發文）。
//   改完之後順序與 seq 完全不變，只有「進行中」有這個窗口。呼叫端（管理頁）負責擋。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>訊息檔名 migration 的結果報告。</summary>
    public class UCL_MsgFileMigrationResult
    {
        public int RoomCount;
        public int FileCount;
        public int PlanCount;                 // 需要改名的檔數
        public int RenamedCount;
        public bool Cancelled;
        public readonly List<string> Problems = new List<string>();   // 有任何一筆 → 一個檔都不動
        public readonly List<string> VerifyErrors = new List<string>();
        public readonly List<string> Preview = new List<string>();    // dry-run 的樣本
        public bool Ok => Problems.Count == 0 && VerifyErrors.Count == 0 && !Cancelled;
    }

    public static class UCL_ChatTavernMessageFileMigration
    {
        /// <summary>新格式：8 位補零的全域 seq。字典序 == 數值序，所以改名不影響排序。</summary>
        const string NewNameFormat = "00000000";

        static bool IsNewFormat(string fileName)
        {
            if (fileName.Length != 13 || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            for (int i = 0; i < 8; i++) if (!char.IsDigit(fileName[i])) return false;
            return true;
        }

        // 舊格式：HHMMSS_<ms 任意位數>_<uuid6>.json。
        // ⚠ 中間那段寬度**有三種**（6 / 3 / 2 位）—— 2026-08-06 實測。
        //   寫死一種的話涵蓋率只有 1.3%，而「跑完沒問題」的輸出跟全跑完一模一樣。
        static bool IsOldFormat(string fileName)
        {
            if (!fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            string stem = fileName.Substring(0, fileName.Length - 5);
            string[] parts = stem.Split('_');
            if (parts.Length != 3) return false;
            if (parts[0].Length != 6) return false;
            foreach (char c in parts[0]) if (!char.IsDigit(c)) return false;
            if (parts[1].Length == 0) return false;
            foreach (char c in parts[1]) if (!char.IsDigit(c)) return false;
            if (parts[2].Length == 0) return false;
            foreach (char c in parts[2]) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        static string[] RoomIds()
        {
            string root = UCL_ChatTavernIO.GetRoomsRoot();
            if (!Directory.Exists(root)) return Array.Empty<string>();
            var ids = new List<string>();
            foreach (string d in Directory.GetDirectories(root)) ids.Add(Path.GetFileName(d));
            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        static string ReadUuid(string path)
        {
            try
            {
                var msg = UCL_ChatTavernIO.ParseMessage(File.ReadAllText(path, Encoding.UTF8));
                return msg?.uuid ?? "";
            }
            catch (Exception e) { return "<unreadable:" + e.GetType().Name + ">"; }
        }

        /// <summary>
        /// 區塊職責：試跑 —— 只算不改，回報「要改幾檔 / 有沒有問題」。
        /// 物理意義：**已是新格式但位置對不上 → 列為問題並整批停手**。
        ///          那代表排序模型與實際檔名的假設不成立，硬改下去會讓 seq 全體位移。
        ///          （反過來說：現行 970+ 個新格式檔全部位置吻合，就是排序模型正確的正向證據。）
        /// </summary>
        public static UCL_MsgFileMigrationResult Plan()
        {
            var r = new UCL_MsgFileMigrationResult();
            foreach (string roomId in RoomIds())
            {
                string[] files = UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths(roomId);
                if (files.Length == 0) continue;
                r.RoomCount++;
                r.FileCount += files.Length;
                for (int i = 0; i < files.Length; i++)
                {
                    string cur = Path.GetFileName(files[i]);
                    string want = (i + 1).ToString(NewNameFormat) + ".json";
                    if (cur == want) continue;
                    if (IsNewFormat(cur))
                    {
                        r.Problems.Add($"[{roomId}] 新格式檔位置不符：{cur} 期望 {want}");
                        continue;
                    }
                    if (!IsOldFormat(cur))
                    {
                        r.Problems.Add($"[{roomId}] 無法辨識的檔名：{cur}");
                        continue;
                    }
                    r.PlanCount++;
                    if (r.Preview.Count < 10) r.Preview.Add($"{roomId}: {cur} → {want}  (seq {i + 1})");
                }
            }
            return r;
        }

        /// <summary>
        /// 區塊職責：實際改名 + 全量對帳。
        /// 物理意義：對帳三項 —— 檔數不變 / 每個 seq 對到**同一則訊息**（比 uuid）/ 檔名 == seq。
        ///          比 uuid 而不是比路徑：路徑本來就會變，uuid 是那則訊息的身分證。
        /// 數值影響：Plan 有任何問題 → **一個檔都不動**（fail closed，不做部分遷移）。
        ///          部分遷移是最糟的狀態：一半舊一半新，而排序在兩者之間是混的。
        /// </summary>
        public static UCL_MsgFileMigrationResult Apply()
        {
            var r = Plan();
            if (r.Problems.Count > 0) return r;      // fail closed

            try
            {
                foreach (string roomId in RoomIds())
                {
                    string[] files = UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths(roomId);
                    if (files.Length == 0) continue;

                    // 先把「改名前的 seq → uuid」拍下來，改完拿它對帳。
                    // 必須在改名**之前**讀，否則對的是改完的自己（自己的輸出替自己背書）。
                    var beforeUuid = new string[files.Length];
                    for (int i = 0; i < files.Length; i++) beforeUuid[i] = ReadUuid(files[i]);

                    if (EditorUtility.DisplayCancelableProgressBar("訊息檔名 migration",
                            $"{roomId}（{files.Length} 檔）", 0f))
                    { r.Cancelled = true; return r; }

                    // 逐筆改名。files 是 cache 本體，改名後內容即失效 —— 所以先複製一份索引用。
                    var srcPaths = (string[])files.Clone();
                    for (int i = 0; i < srcPaths.Length; i++)
                    {
                        string src = srcPaths[i];
                        string want = (i + 1).ToString(NewNameFormat) + ".json";
                        if (Path.GetFileName(src) == want) continue;
                        string dst = Path.Combine(Path.GetDirectoryName(src), want);
                        if (File.Exists(dst))
                        {
                            r.VerifyErrors.Add($"[{roomId}] 目標已存在，中止：{want}");
                            return r;
                        }
                        File.Move(src, dst);
                        r.RenamedCount++;
                    }

                    // 改完必須讓兩份快取失效：目錄指紋會因 mtime 自動失效，
                    // 但 parse cache 的 key 是**舊路徑** —— 不清會留一堆孤兒條目。
                    UCL_ChatTavernIO_PerMsgFile.InvalidateMessageCache(roomId);

                    // ---- 對帳 ----
                    string[] after = UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths(roomId);
                    if (after.Length != srcPaths.Length)
                    {
                        r.VerifyErrors.Add($"[{roomId}] 檔數變了：{srcPaths.Length} → {after.Length}");
                        continue;
                    }
                    for (int i = 0; i < after.Length; i++)
                    {
                        string want = (i + 1).ToString(NewNameFormat) + ".json";
                        if (Path.GetFileName(after[i]) != want)
                        {
                            r.VerifyErrors.Add($"[{roomId}] 檔名不等於 seq：{Path.GetFileName(after[i])} 期望 {want}");
                            break;
                        }
                        string now = ReadUuid(after[i]);
                        if (now != beforeUuid[i])
                        {
                            r.VerifyErrors.Add($"[{roomId}] seq {i + 1} 對到不同訊息：{beforeUuid[i]} → {now}");
                            if (r.VerifyErrors.Count > 10) return r;
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return r;
        }

        /// <summary>把結果排成人看的報告（管理頁直接貼）。</summary>
        public static string Format(UCL_MsgFileMigrationResult r, bool applied)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"房間 {r.RoomCount} / 訊息檔 {r.FileCount} / 需改名 {r.PlanCount}");
            if (r.Problems.Count > 0)
            {
                sb.AppendLine($"\n🚫 {r.Problems.Count} 個問題 —— 一個檔都沒動：");
                for (int i = 0; i < r.Problems.Count && i < 20; i++) sb.AppendLine("   " + r.Problems[i]);
                return sb.ToString();
            }
            if (!applied)
            {
                sb.AppendLine("\n（試跑 — 一個檔都沒動）");
                foreach (string p in r.Preview) sb.AppendLine("   " + p);
                return sb.ToString();
            }
            if (r.Cancelled) { sb.AppendLine("\n⚠ 使用者取消 —— 已改名的部分不會自動還原，請重跑一次補完。"); return sb.ToString(); }
            sb.AppendLine($"\n✅ 改名 {r.RenamedCount} 檔");
            if (r.VerifyErrors.Count > 0)
            {
                sb.AppendLine($"🚨 對帳失敗 {r.VerifyErrors.Count} 筆：");
                for (int i = 0; i < r.VerifyErrors.Count && i < 20; i++) sb.AppendLine("   " + r.VerifyErrors[i]);
            }
            else
            {
                sb.AppendLine("✅ 對帳通過：檔數相同、每個 seq 對到同一則訊息（uuid 逐筆一致）、檔名 == seq");
            }
            return sb.ToString();
        }
    }
}
#endif
