// 區塊職責：自由時間的活動清單掃描（雙層 md 來源的唯一解析器）。
// 物理意義：活動的事實來源是 md frontmatter —— 共用層跟著 UCL_Core 走、專案層跟著 repo 走，
//          同 id 專案層覆蓋共用層。Cmd_FreeTime 擲骰與 UCL_FreeTimeAdminPage 管理**共用本掃描器**，
//          不各寫一份（兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
// 歷史：本檔一度還放過「末段提示門檻」設定（tail_warn_seconds）。
//      Tim 2026-08-14 拍板**把末段提示整個拔掉**，設定隨之移除 ——
//      沒有消費端的設定就是一個比事實大的名字，留著只會讓人以為那裡還有一道防護。
//      拔除理由與血證寫在 Cmd_FreeTime.StepNext 的註解裡（燈拆掉 vs 燈調暗）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.Awakening;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    /// <summary>
    /// 一筆自由時間活動（來源＝一個 md 檔的 frontmatter；正文是給人讀的說明，不進本型別）。
    /// </summary>
    public class UCL_FreeTimeActivity
    {
        public string id = "";
        public string name = "";
        public string how = "";
        public string path = "";        // md 絕對路徑（路徑不該被推導，該被傳遞）
        public int minMinutes;          // 建議所需分鐘；0＝未設定（不做時間感知排序）
        public bool enabled = true;
        public bool isProjectLayer;     // true＝專案層（同 id 會覆蓋共用層）
    }

    /// <summary>
    /// 自由時間設定與活動清單的讀寫。對齊 UCL_BartenderIO 慣例：路徑集中、讀不到回預設、寫入原子替換。
    /// </summary>
    public static class UCL_FreeTimeIO
    {
        public static string GetFreeTimeDir()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "FreeTime");

        // ── 活動清單（雙層：共用層跟著 UCL_Core 走、專案層跟著 repo 走）──
        // ⚠ 不寫死 UCL_Core 安裝路徑 —— 由 UCL_EditorPath.CorePath 現算（各專案掛載位置不同）。
        public static string GetSharedActivityDir()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            return string.IsNullOrEmpty(aCoreRel) ? null
                : Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, aCoreRel,
                    "Docs~/zh-Hant/FreeTime/Activities"));
        }

        public static string GetProjectActivityDir()
            => Path.GetFullPath(Path.Combine(UCL_RepoPath.RepoRoot, "docs/FreeTime/Activities"));

        /// <summary>
        /// 區塊職責：掃描兩層活動 md，回傳合併後的清單（**含停用項**）。
        /// 物理意義：同 id 專案層覆蓋共用層（含 enabled:false 停用覆蓋 —— kotoko QA 血證：
        ///          過濾必須發生在 merge 之後，否則專案層的「停用」會被共用層的「啟用」蓋回去）。
        /// 數值影響：**回傳含停用項**，由呼叫端決定要不要濾 —— 擲骰要濾掉，管理頁要看得到。
        ///          這是本掃描器唯一的一份實作：Cmd 擲骰與管理頁共用，不各寫一份
        ///          （兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
        /// </summary>
        public static List<UCL_FreeTimeActivity> ScanActivities()
        {
            var aMerged = new Dictionary<string, UCL_FreeTimeActivity>();
            ScanActivityDir(GetSharedActivityDir(), false, aMerged);
            ScanActivityDir(GetProjectActivityDir(), true, aMerged);   // 同 id 專案層覆蓋
            var aList = new List<UCL_FreeTimeActivity>(aMerged.Values);
            aList.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return aList;
        }

        static void ScanActivityDir(string iDir, bool iIsProject, Dictionary<string, UCL_FreeTimeActivity> ioMerged)
        {
            if (string.IsNullOrEmpty(iDir) || !Directory.Exists(iDir)) return;
            foreach (var aMd in Directory.GetFiles(iDir, "*.md"))
            {
                if (Path.GetFileName(aMd).StartsWith("_")) continue;   // _README.md 等說明檔不算活動
                try
                {
                    string aId = Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "id"), Path.GetFileNameWithoutExtension(aMd));
                    int.TryParse(UCL_AwakeningService.ReadFrontmatterField(aMd, "min_minutes") ?? "", out int aMinMinutes);
                    ioMerged[aId] = new UCL_FreeTimeActivity
                    {
                        id = aId,
                        name = Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "name"), Path.GetFileNameWithoutExtension(aMd)),
                        how = UCL_AwakeningService.ReadFrontmatterField(aMd, "how") ?? "",
                        path = aMd,
                        minMinutes = aMinMinutes,
                        enabled = !string.Equals(Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "enabled"), "true"),
                                                 "false", StringComparison.OrdinalIgnoreCase),
                        isProjectLayer = iIsProject,
                    };
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FreeTime] 活動 md 讀取失敗，跳過：{aMd}（{e.Message}）");
                }
            }
        }

        static string Nz(string iVal, string iFallback) => string.IsNullOrEmpty(iVal) ? iFallback : iVal;

    }
}
#endif
