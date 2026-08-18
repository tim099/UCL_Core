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
    /// 活動的**特殊邏輯標記**（Tim 2026-08-17 拍板；frontmatter 欄位 `kind`）。
    /// <para>
    /// 為什麼是 enum 而不是自由字串規則名：管理頁要能給下拉選單。字串欄位打錯
    /// （`live-strem`）會**安靜地什麼都不做**，而下拉選單根本打不出那個值。
    /// </para>
    /// <para>
    /// 為什麼存在 md frontmatter 而不是另一份設定檔：活動的事實來源只有 md 一處。
    /// 這條是本系統付過學費的 —— v1 的 `AgentCommands/FreeTime/activities.json`
    /// 正是因為「雙源同步漂移」被廢止，管理頁也明著寫了「不另存一份 override 設定」。
    /// </para>
    /// <para>
    /// 新增一種 kind ＝ 改這個 enum ＋ 在 <see cref="UCL_FreeTimeGating"/> 補對應邏輯。
    /// 兩邊都要動是刻意的：**沒有實作的標記不該存在**（名字比事實大的東西，
    /// 會讓人以為那裡有一道邏輯）。
    /// </para>
    /// </summary>
    public enum UCL_FreeTimeActivityKind
    {
        /// <summary>一般活動 —— 永遠可選、永遠在普通層，不走任何特殊邏輯。</summary>
        Default = 0,

        /// <summary>
        /// 觀看直播：**沒開播就隱藏**（不列入候選 —— 陪看一個不存在的節目是純粹的浪費）；
        /// 開播時進最優先層並附上本場節目名。
        /// </summary>
        StreamWatch = 1,

        /// <summary>
        /// 下棋：有未完成棋局、且**對手也在自由時間中**時進最優先層（對手在線才接得上手）。
        /// 沒有這個條件時仍是普通活動 —— 隨時可以開新局徵人，不隱藏。
        /// </summary>
        Chess = 2,
    }

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

        // 區塊職責：讓活動的「一步」可以被 Cmd 代跑（Tim 2026-08-18）。
        // 物理意義：`how` 是給人讀的一整串自由文字（"chess.py lobby 找局 / start 開局徵人 / move 走子…"）——
        //          機器沒辦法從它取出「第一步該跑什麼」，所以活動層只能整串轉貼。
        //          下棋走一子、繪圖放一個像素**本來就是一次性的次秒級動作**，
        //          拆成「一步」之後 Cmd 就能代跑並在回傳檔接上下一步。
        // ⚠ **additive**：舊 md 沒填這兩欄 ⇒ `tool` 空 ⇒ 活動層回「此活動尚未支援代跑，
        //   用 op=pick 取得指令自己跑」。**沒填不是壞掉，是還沒接** —— 兩者要長得不一樣。
        // 數值影響：純資料；`steps` 是白名單，不在名單上的子命令一律拒跑
        //          （不做白名單＝把任意 argv 交給外部程式，那是 CLI 注入面）。
        /// <summary>代跑用的腳本檔名（frontmatter `tool`，例 `chess.py`）。空＝本活動不支援代跑。</summary>
        public string tool = "";
        /// <summary>允許代跑的子命令白名單（frontmatter `steps`，逗號分隔）。空＝即使有 tool 也不放行。</summary>
        public List<string> steps = new List<string>();

        /// <summary>特殊邏輯標記（frontmatter `kind`；缺欄位＝Default）。</summary>
        public UCL_FreeTimeActivityKind kind = UCL_FreeTimeActivityKind.Default;

        /// <summary>
        /// `kind` 欄位的原始字串 —— **只在解析失敗時非空**。
        /// 存它是為了讓「打錯的標記」在骰面上顯形，而不是靜靜地退回 Default：
        /// 靜默退回的症狀是「我明明標了直播，它卻還是照常出現」，而沒有任何地方會喊。
        /// </summary>
        public string kindParseError = "";
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
                    var aKind = ParseKind(UCL_AwakeningService.ReadFrontmatterField(aMd, "kind"), out string aKindErr);
                    ioMerged[aId] = new UCL_FreeTimeActivity
                    {
                        kind = aKind,
                        kindParseError = aKindErr,
                        id = aId,
                        name = Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "name"), Path.GetFileNameWithoutExtension(aMd)),
                        how = UCL_AwakeningService.ReadFrontmatterField(aMd, "how") ?? "",
                        path = aMd,
                        minMinutes = aMinMinutes,
                        enabled = !string.Equals(Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "enabled"), "true"),
                                                 "false", StringComparison.OrdinalIgnoreCase),
                        isProjectLayer = iIsProject,
                        tool = (UCL_AwakeningService.ReadFrontmatterField(aMd, "tool") ?? "").Trim(),
                        steps = ParseSteps(UCL_AwakeningService.ReadFrontmatterField(aMd, "steps")),
                    };
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FreeTime] 活動 md 讀取失敗，跳過：{aMd}（{e.Message}）");
                }
            }
        }

        /// <summary>
        /// 區塊職責：`kind` 欄位字串 → enum。
        /// 物理意義：空值／缺欄位＝Default（絕大多數活動不需要特殊邏輯，不該逼每份 md 都寫）。
        /// 數值影響：**認不得的值不是靜默 Default**，會回填 oParseError 讓它在管理頁與骰面上顯形 ——
        ///          標記打錯而系統照常運作，是「以為有防護其實沒有」那一類最難查的壞法。
        /// </summary>
        public static UCL_FreeTimeActivityKind ParseKind(string iRaw, out string oParseError)
        {
            oParseError = "";
            string aVal = (iRaw ?? "").Trim();
            if (aVal.Length == 0) return UCL_FreeTimeActivityKind.Default;
            if (Enum.TryParse(aVal, true, out UCL_FreeTimeActivityKind aKind)
                && Enum.IsDefined(typeof(UCL_FreeTimeActivityKind), aKind))
                return aKind;
            oParseError = aVal;
            Debug.LogWarning($"[FreeTime] 認不得的 kind='{aVal}' —— 視為 Default，並在骰面標記。"
                + $" 可用值：{string.Join(" / ", Enum.GetNames(typeof(UCL_FreeTimeActivityKind)))}");
            return UCL_FreeTimeActivityKind.Default;
        }

        // 區塊職責：`steps: move, board, lobby` → 白名單清單。
        // 物理意義：白名單存在的理由不是整潔，是**不把任意 argv 交給外部程式** ——
        //          活動層代跑時 step 名直接進 argv，沒有白名單就是一條 CLI 注入面。
        // 數值影響：空／缺欄位回空清單 ⇒ 呼叫端一律拒跑（fail-closed，不是 fail-open）。
        static List<string> ParseSteps(string iRaw)
        {
            var aList = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aList;
            foreach (var aPart in iRaw.Split(','))
            {
                string aTrim = aPart.Trim();
                if (aTrim.Length > 0) aList.Add(aTrim);
            }
            return aList;
        }

        static string Nz(string iVal, string iFallback) => string.IsNullOrEmpty(iVal) ? iFallback : iVal;

    }
}
#endif
