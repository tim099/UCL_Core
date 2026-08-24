// 區塊職責：自由時間「每個活動有多少場沒被選過」的統計與置頂判定（Tim 2026-08-24 拍板）。
// 物理意義：骰面原本每場都是重新洗牌 —— 於是**冷門活動的冷門是不可觀測的**：
//          它每場都在清單裡，看起來一切正常，而沒有任何一層會說「這件事你 12 場沒碰過」。
//          ⇒ 這裡記的是「飢餓度」＝ 本 persona 已跑過的場次 − 該活動最後被選中的場次。
//          超過門檻就把它推進骰面優先層（跟券囤積那條同一個出口，但**判準是通用的**：
//          不綁 kind、不綁工具，任何活動都適用）。
//
// 為什麼統計 by persona：每個人做的事不一樣，「很久沒畫圖」對畫圖的人跟不畫圖的人
//   不是同一件事。全域統計會讓多數人的偏好把少數人的空白抹平。
// 為什麼存 letters/<persona>/profile/：那裡是身分欄的唯一版面（`ProfileField`），
//   而「我最近都在做什麼」屬於這個 persona 自己的事實，不是專案的事實。
//   ⚠ profile 欄的慣例是「檔名＝欄位、內文＝值」；本欄的值是 JSON
//   （既有先例：`identity_vector.md` / `vector_history.md` 都是 JSON 內文）。
//
// 數值影響：只影響骰面排序與名字後綴，**不擋任何事、不改任何額度**。
//   全路徑 fail-soft：讀不到／解析失敗一律當「沒有統計」（＝不置頂），
//   因為誤判飢餓只是多一個推薦，而**把讀取失敗印成 0 場飢餓會讓一個真的空白被隱藏**
//   —— 所以讀取失敗與真的 0 在回傳給呼叫端時是可分的（`Loaded` 欄）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    /// <summary>一個活動在某 persona 身上的觸發紀錄。</summary>
    public class UCL_FreeTimeActivityStat
    {
        /// <summary>被選中過幾次（累計，不隨場次重置）。</summary>
        public int picks;

        /// <summary>最後一次被選中發生在第幾場（`sessions_total` 的當時值）。0＝從未被選過。</summary>
        public int lastSession;

        /// <summary>最後一次被選中的 ISO 時刻；空＝從未。</summary>
        public string lastAt = "";
    }

    /// <summary>
    /// 某 persona 的自由時間活動統計（一個 profile 欄 ＝ 一份本型別）。
    /// </summary>
    public class UCL_FreeTimeActivityStats
    {
        /// <summary>這個 persona 一共開過幾場自由時間（飢餓度的分母基準）。</summary>
        public int sessionsTotal;

        /// <summary>最後寫入時刻（ISO）。</summary>
        public string updatedAt = "";

        /// <summary>活動 id → 紀錄。</summary>
        public Dictionary<string, UCL_FreeTimeActivityStat> activities
            = new Dictionary<string, UCL_FreeTimeActivityStat>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// false ＝ **檔案讀不到或解析失敗**（不是「這個人還沒跑過自由時間」）。
        /// 兩者的數字一模一樣（全 0），所以差別只能靠這個欄位講 ——
        /// 呼叫端要印「沒有讀數」而不是「0 場」。
        /// </summary>
        public bool loaded;

        /// <summary>
        /// 飢餓度 ＝ 已跑場次 − 該活動最後被選中的場次。
        /// <para>從未被選過 ⇒ 等於 `sessionsTotal`（沒被選過就是最餓的那一種）。</para>
        /// <para>沒有統計（`loaded=false`）⇒ 一律 0，也就是不置頂。</para>
        /// </summary>
        public int Starvation(string iActivityId)
        {
            if (!loaded || string.IsNullOrEmpty(iActivityId)) return 0;
            int aLast = activities.TryGetValue(iActivityId, out var aStat) ? aStat.lastSession : 0;
            int aGap = sessionsTotal - aLast;
            return aGap < 0 ? 0 : aGap;   // 負值只可能來自資料被手改，當 0 不當異常
        }

        /// <summary>被選中過幾次（沒有紀錄＝0）。</summary>
        public int Picks(string iActivityId)
            => (iActivityId != null && activities.TryGetValue(iActivityId, out var aStat)) ? aStat.picks : 0;
    }

    /// <summary>
    /// 統計欄的讀寫與置頂判定。**這是本欄唯一的實作** ——
    /// Cmd 擲骰、活動層記錄、管理頁顯示都走這裡，不各寫一份
    /// （兩份的漂移症狀是「頁面顯示的次數跟骰面用的不一樣」，而它不會報錯）。
    /// </summary>
    public static class UCL_FreeTimeActivityStatsIO
    {
        /// <summary>profile 欄名（＝檔名，`letters/&lt;persona&gt;/profile/&lt;此值&gt;.md`）。</summary>
        public const string FieldName = "freetime_activity_stats";

        // 區塊職責：「太久沒被選」的門檻與一次最多頂幾項（Tim 2026-08-24：參考券囤積那套）。
        // 物理意義：門檻決定「多久算久」；上限決定「置頂還算不算置頂」——
        //   🩸 今天早上 op=expand 才教過我一次：**當多數項目同時符合條件，排序就失去解析度**
        //   （前 15 名全部同分 ⇒ 名次其實由 tie-break 決定）。
        //   飢餓度天生會整批超標（新增一件活動時它立刻是最餓的），所以這裡一定要有上限。
        // 數值影響：只影響骰面排序；調它不擋任何事。
        public const int STARVE_THRESHOLD = 5;
        public const int STARVE_HOIST_MAX = 2;

        public static string FieldPath(string iPersona)
            => UCL_LettersPath.ProfileField(iPersona, FieldName);

        /// <summary>
        /// 讀某 persona 的統計。
        /// <para>⚠ 檔案不存在 ⇒ 回 `loaded=false` 的空統計（**不建檔** ——
        /// 讀取不該有副作用，而憑空長出來的空檔會讓「沒跑過」與「跑過但沒紀錄」同形）。</para>
        /// </summary>
        public static UCL_FreeTimeActivityStats Load(string iPersona)
        {
            var aRes = new UCL_FreeTimeActivityStats();
            if (string.IsNullOrEmpty(iPersona)) return aRes;
            try
            {
                string aPath = FieldPath(iPersona);
                if (!File.Exists(aPath)) return aRes;
                var aJd = JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
                if (aJd == null) return aRes;
                aRes.sessionsTotal = aJd.Contains("sessions_total") ? aJd.GetInt("sessions_total", 0) : 0;
                aRes.updatedAt = aJd.Contains("updated_at") ? aJd["updated_at"].ToString() : "";
                // 🩸 2026-08-24 首次實跑：空字典被寫成 `"activities":null`，而 `Contains` 對 null 值仍回 true
                //   ⇒ 這裡拿到 null，`.Keys` 丟 NullReference，整份統計被 catch 當成「讀不到」。
                //   症狀是回傳檔同時印「累計第 1 場」與「尚無活動統計」—— 兩句不該同時成立。
                //   ⇒ 存在（Contains）不等於有值。判定要看**值本身**，不是看鍵在不在。
                var aActs = aJd.Contains("activities") ? aJd["activities"] : null;
                if (aActs != null && aActs.IsObject)
                {
                    foreach (var aKey in aActs.Keys)
                    {
                        var aIt = aActs[aKey];
                        aRes.activities[aKey] = new UCL_FreeTimeActivityStat
                        {
                            picks = aIt.Contains("picks") ? aIt.GetInt("picks", 0) : 0,
                            lastSession = aIt.Contains("last_session") ? aIt.GetInt("last_session", 0) : 0,
                            lastAt = aIt.Contains("last_at") ? aIt["last_at"].ToString() : "",
                        };
                    }
                }
                aRes.loaded = true;
                return aRes;
            }
            catch (Exception ex)
            {
                // fail-soft，但**要留痕** —— 靜默失敗的症狀是「置頂規則好像沒在動」，
                // 而那跟「大家都不餓」長得一樣。
                Debug.LogWarning($"[FreeTimeStats] 讀 {iPersona} 的統計失敗（當沒有統計處理）：{ex.Message}");
                return aRes;
            }
        }

        static void Save(string iPersona, UCL_FreeTimeActivityStats iStats)
        {
            string aPath = FieldPath(iPersona);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath));
            var aJd = new JsonData();
            aJd["sessions_total"] = new JsonData(iStats.sessionsTotal);
            aJd["updated_at"] = new JsonData(DateTime.UtcNow.ToString("o"));
            var aActs = new JsonData();
            foreach (var aKv in iStats.activities)
            {
                var aIt = new JsonData();
                aIt["picks"] = new JsonData(aKv.Value.picks);
                aIt["last_session"] = new JsonData(aKv.Value.lastSession);
                aIt["last_at"] = new JsonData(aKv.Value.lastAt ?? "");
                aActs[aKv.Key] = aIt;
            }
            // ⚠ 空字典在這個 JsonData 實作下會序列化成 `null`（不是 `{}`）——
            //   而 null 在讀取端是個陷阱（見 Load 的血證）。**沒有內容就不要寫那個鍵**：
            //   缺鍵是讀取端本來就處理的情形，null 則是它處理不了的第三種狀態。
            if (iStats.activities.Count > 0) aJd["activities"] = aActs;
            // 原子替換：同 UCL_FreeTimeIO 慣例（半寫的 JSON 會讓下次讀取整份當「沒有統計」）
            string aTmp = aPath + ".tmp";
            File.WriteAllText(aTmp, aJd.ToJson(), new UTF8Encoding(false));
            if (File.Exists(aPath)) File.Delete(aPath);
            File.Move(aTmp, aPath);
        }

        /// <summary>
        /// 開一場自由時間 ⇒ 場次 +1。**這是飢餓度的時鐘** ——
        /// 不推它的話「幾場沒被選」永遠是 0，而置頂規則會安靜地永不觸發。
        /// </summary>
        /// <returns>推進後的場次總數；寫入失敗回 -1（呼叫端據此印「沒有讀數」）。</returns>
        public static int BumpSession(string iPersona)
        {
            try
            {
                var aStats = Load(iPersona);
                aStats.sessionsTotal += 1;
                Save(iPersona, aStats);
                return aStats.sessionsTotal;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FreeTimeStats] {iPersona} 場次推進失敗（不影響本場）：{ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 記錄一次「選中」。**唯一寫入端是活動層的 op=pick** ——
        /// 骰面出現不算被選，出現而沒人做正是飢餓的樣子。
        /// </summary>
        /// <returns>該活動累計被選次數；寫入失敗回 -1。</returns>
        public static int RecordPick(string iPersona, string iActivityId)
        {
            if (string.IsNullOrEmpty(iPersona) || string.IsNullOrEmpty(iActivityId)) return -1;
            try
            {
                var aStats = Load(iPersona);
                if (!aStats.activities.TryGetValue(iActivityId, out var aStat))
                {
                    aStat = new UCL_FreeTimeActivityStat();
                    aStats.activities[iActivityId] = aStat;
                }
                aStat.picks += 1;
                aStat.lastSession = aStats.sessionsTotal;
                aStat.lastAt = DateTime.UtcNow.ToString("o");
                Save(iPersona, aStats);
                return aStat.picks;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FreeTimeStats] {iPersona}/{iActivityId} 選中紀錄失敗：{ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 區塊職責：從一批候選活動 id 裡挑出「該被置頂」的那幾個。
        /// 物理意義：飢餓度 ≥ 門檻者為候選，取最餓的前 <see cref="STARVE_HOIST_MAX"/> 個
        ///          （同分時**穩定挑 id 序** —— 骰面本來就會再洗牌，這裡不需要第二個隨機源）。
        /// 數值影響：純計算，不寫檔。回傳 id → 飢餓度。
        /// ⚠ 回傳 `oOverflow` ＝ 符合門檻但沒被頂上來的數量。**這格一定要往外傳**：
        ///   「只有 2 項餓」與「有 9 項餓而我只頂 2 項」在骰面上長得一模一樣。
        /// </summary>
        public static Dictionary<string, int> PickStarved(UCL_FreeTimeActivityStats iStats,
            IEnumerable<string> iCandidateIds, out int oOverflow)
        {
            oOverflow = 0;
            var aRes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (iStats == null || !iStats.loaded || iCandidateIds == null) return aRes;

            var aQualified = new List<KeyValuePair<string, int>>();
            foreach (string aId in iCandidateIds)
            {
                int aGap = iStats.Starvation(aId);
                if (aGap >= STARVE_THRESHOLD) aQualified.Add(new KeyValuePair<string, int>(aId, aGap));
            }
            aQualified.Sort((a, b) =>
            {
                int aCmp = b.Value.CompareTo(a.Value);          // 餓的在前
                return aCmp != 0 ? aCmp : string.CompareOrdinal(a.Key, b.Key);
            });
            for (int i = 0; i < aQualified.Count; i++)
            {
                if (i < STARVE_HOIST_MAX) aRes[aQualified[i].Key] = aQualified[i].Value;
                else oOverflow++;
            }
            return aRes;
        }

        /// <summary>
        /// 掃出所有有統計欄的 persona（管理頁用）。
        /// <para>判準是**檔案存在**，不是任何名單 —— 名單會跟磁碟漂移。</para>
        /// </summary>
        public static List<string> ListPersonasWithStats()
        {
            var aRes = new List<string>();
            try
            {
                string aRoot = UCL_LettersPath.Root;
                if (string.IsNullOrEmpty(aRoot) || !Directory.Exists(aRoot)) return aRes;
                foreach (string aDir in Directory.GetDirectories(aRoot))
                {
                    string aName = Path.GetFileName(aDir);
                    if (File.Exists(FieldPath(aName))) aRes.Add(aName);
                }
                aRes.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FreeTimeStats] 掃 persona 統計失敗：{ex.Message}");
            }
            return aRes;
        }

        /// <summary>把飢餓度講成骰面上的一句話（給 nameSuffix 用）。</summary>
        public static string StarveSuffix(int iGap, int iPicks)
            => iPicks <= 0
                ? $" 💤 **從未做過**（已 {iGap} 場）—— 要不要試一次？"
                : $" 💤 已 **{iGap} 場**沒選它（累計做過 {iPicks} 次）";
    }
}
#endif
