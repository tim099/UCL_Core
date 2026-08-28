// 區塊職責：晚安對帳 —— 見叢（`_keys_open.md`）的 `[TASK-n]` 引用 ✕ 單子的實際狀態。
//
// 物理意義：Tim 2026-08-24 拍板「**早安 brief 不新增任何節**」，Task 經由見叢的引用行進入 brief。
//   ⇒ 那條拍板開了一個洞：**別人指派給我、而我沒寫進見叢的單，早安不會提** ——
//     因為見叢是我寫的，而我不知道的事不會出現在我自己列的清單上（枚舉盲區那一族）。
//   本檔就是那個洞的補丁，而它**補在晚安**（我們本來就會停下來的那一格），不補在早安。
//
// ⚠ **只印不改**（RFC §2③）：晚安 `step=check` 的契約是「唯讀起手」，
//   而在那裡靜默改任務狀態的話，那一行沒有人會讀 —— 自動化要掛在有 SHA 當證據的 commit 那一格。
//   逾期認領的釋放是**機械但顯式**的：走 `Cmd_Task op=sweep`（本檔只把候選印出來 ＋ 附上那道指令）。
//
// 數值影響：純讀（見叢一次、tasks/ 一次）。任何失敗都回傳一行「對帳失敗」而不是靜默跳過 ——
//   晚安流程不因對帳壞掉而中斷，但「今晚沒對到帳」必須看得見。
// 2026-08-24 summit（TASK-0004）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskReconcile
    {
        static readonly Regex TASK_REF = new Regex(@"TASK-(\d+)", RegexOptions.Compiled);

        /// <summary>見叢裡出現過的單號 → 該行原文（同一單號出現多行時保留第一行）。</summary>
        public static Dictionary<int, string> ReadKeysRefs(string iKeysPath)
        {
            var aOut = new Dictionary<int, string>();
            if (!File.Exists(iKeysPath)) return aOut;
            foreach (var aLine in File.ReadAllLines(iKeysPath, Encoding.UTF8))
            {
                foreach (Match m in TASK_REF.Matches(aLine))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int aIdx)) continue;
                    if (!aOut.ContainsKey(aIdx)) aOut[aIdx] = aLine.Trim();
                }
            }
            return aOut;
        }

        /// <summary>這張單跟這個 persona 有關嗎（參與者或開單人）。</summary>
        static bool Involves(UCL_TaskEntry e, string iPersona)
            => e.RolesOf(iPersona).Count > 0
            || string.Equals(e.reporter, iPersona, StringComparison.OrdinalIgnoreCase);

        // ===========================================================
        // 區塊職責：組出晚安對帳那一段（markdown）。
        // 物理意義：以下每一段都**只印**（段號別當成段數 —— 這個清單長過，而註解漏過一次：
        //   ④ 是 TASK-0015 加的，而本行當時還寫著「三類」）：
        //   ① 見叢引用了已關 / 不存在的單 ⇒ 那一行可以劃掉了（或它指錯了）
        //   ② 跟我有關、還開著、而見叢**完全沒有引用** ⇒ 那就是 Tim 那條拍板開的洞
        //   ③ 我掛在 in_progress 且逾期 ⇒ 認領變成占位（釋放走 op=sweep，顯式）
        //   ④ Task ↔ 工作記憶：(a) 連結壞掉 (b) 久未更新
        //   ⑤ 收工預告：等一下 `step=sleep` 會擋什麼（**只列不擋**，TASK-0019）
        // 數值影響：純讀。回傳字串一定非空 —— 沒有不一致時也要印「對過帳了，沒有不一致」，
        //   因為「沒印」跟「沒對」在回傳檔上長得一樣。
        // ===========================================================
        public static string BuildReport(string iPersona, string iKeysPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 📋 Task 對帳（見叢引用 ✕ 單子實際狀態）—— **只印不改**");
            try
            {
                var aAll = UCL_TaskIO.LoadAll();
                if (aAll.Count == 0)
                {
                    sb.AppendLine("- 系統裡目前沒有任何單（`AgentCommands/Tasks/tasks/` 是空的）——"
                        + " 這是「沒有單」，不是「沒對帳」。");
                    return sb.ToString();
                }
                var aRefs = ReadKeysRefs(iKeysPath);
                sb.AppendLine($"- 讀數：單 **{aAll.Count}** 張／見叢引用 **{aRefs.Count}** 筆"
                    + $"（見叢：`{iKeysPath}`{(File.Exists(iKeysPath) ? "" : " ⚠ **檔不存在**")}）");

                // ① 見叢引用了已關 / 不存在的單
                var aStaleRefs = new List<string>();
                foreach (var kv in aRefs.OrderBy(k => k.Key))
                {
                    var e = aAll.FirstOrDefault(t => t.index == kv.Key);
                    if (e == null)
                    {
                        aStaleRefs.Add($"TASK-{kv.Key:0000} **單子不存在** —— 見叢那行指向一個沒有的東西"
                            + $"\n      · 見叢原文：{Trunc(kv.Value, 120)}");
                        continue;
                    }
                    if (e.IsClosed())
                        aStaleRefs.Add($"{e.Id} 已 `{e.status}` —— 見叢那行可以劃掉了"
                            + $"\n      · 見叢原文：{Trunc(kv.Value, 120)}");
                }
                sb.AppendLine(aStaleRefs.Count == 0
                    ? "- ✅ ① 見叢引用的單都還開著（沒有指向已關或不存在的單）"
                    : $"- ⚠ ① 見叢有 **{aStaleRefs.Count}** 筆引用該收了：");
                foreach (var s in aStaleRefs) sb.AppendLine("    · " + s);

                // ② 跟我有關、還開著、而見叢沒引用 —— Tim 那條拍板開的洞
                var aMissing = aAll.Where(e => !e.IsClosed() && Involves(e, iPersona)
                                               && !aRefs.ContainsKey(e.index)).ToList();
                sb.AppendLine(aMissing.Count == 0
                    ? "- ✅ ② 跟我有關的未關單，見叢都有引用（早安 brief 會經由見叢提到它們）"
                    : $"- 🕳 ② 有 **{aMissing.Count}** 張跟我有關的單，**見叢完全沒有引用** ⇒"
                        + " 早安 brief 不會提它們（早安流程刻意零改動，所以這個洞補在這裡）：");
                foreach (var e in aMissing)
                    sb.AppendLine($"    · {e.Id} `{e.status}` / `{e.priority}`　{Trunc(e.title, 60)}"
                        + $"　我的角色：{string.Join("/", RolesOrReporter(e, iPersona))}");
                if (aMissing.Count > 0)
                {
                    sb.AppendLine("    ⇒ **自己手寫一行見叢引用**（不自動寫 —— 自動寫出來的那行沒有人會讀）：");
                    foreach (var e in aMissing.Take(5))
                        sb.AppendLine($"      `awakening.py keys --persona {iPersona}"
                            + $" --add \"[{e.Id}] {Trunc(e.title, 40)}\"`");
                }

                // ③ 逾期認領（占位）
                var aNow = DateTime.UtcNow;
                var aStaleClaims = aAll.Where(e => !e.IsClosed()
                        && e.status == UCL_TaskStatus.in_progress
                        && e.RolesOf(iPersona).Count > 0
                        && e.DaysSinceUpdate(aNow) >= UCL_TaskIO.STALE_DAYS).ToList();
                sb.AppendLine(aStaleClaims.Count == 0
                    ? $"- ✅ ③ 我沒有逾期認領（in_progress 超過 {UCL_TaskIO.STALE_DAYS} 天沒動）"
                    : $"- ⏳ ③ 有 **{aStaleClaims.Count}** 張我認領後逾期未動 ⇒ 認領已經變成占位：");
                foreach (var e in aStaleClaims)
                    sb.AppendLine($"    · {e.Id} {Trunc(e.title, 60)}　{e.DaysSinceUpdate(aNow)} 天沒動");
                if (aStaleClaims.Count > 0)
                    sb.AppendLine($"    ⇒ 釋放回 todo（機械、可重跑）："
                        + $"`run Task --arg op=sweep --arg confirm=1`");

                // ===========================================================
                // ④ Task ↔ 工作記憶（TASK-0015；契約 ②「不一致只印不自動修」）
                // 物理意義：跨多日的大 Task 最常死在「單子還開著，而沒有人記得上次做到哪」。
                //   ⇒ 兩類都只印：
                //     (a) **單向連結** —— 單子指向一個主題，而**那個主題不在磁碟上**
                //     (b) **久未更新** —— 未關單的 `updated_at` 超過門檻
                //   ⚠ (a) 只判「主題不存在」，**不判「主題在但沒有 state」** —— 後者不是壞連結，
                //     是拍板後的正確形狀（Tim 2026-08-24：進度由 Task 紀錄，記憶不額外記進度）。
                //     🩸 2026-08-25 修正：本註解原本多寫了「（或沒有 state）」而 code 從未這樣判 ——
                //     **註解比實作大**。照拍板改註解不改 code：把合規列成異常，就是製造一個天天亮的警示。
                // ⚠ 門檻沿用 `STALE_DAYS`（basecamp 拍板 ②）：Tim 的新約束是「進度由 Task 本身紀錄，
                //   記憶不額外記進度」⇒ 這裡量的與 sweep 量的是**同一件事：這張單多久沒動**。
                //   📌 同一個量就該一個常數；不同的量才需要各自的常數。
                // ===========================================================
                var aMine = aAll.Where(e => !e.IsClosed() && Involves(e, iPersona)).ToList();
                var aBrokenLink = new List<string>();
                var aColdMemory = new List<string>();
                foreach (var e in aMine)
                {
                    string aTopic = (e.memory_topic ?? "").Trim();
                    if (aTopic.Length > 0 && !UCL_TaskMemoryLink.TopicExists(aTopic))
                    {
                        aBrokenLink.Add($"{e.Id} → `{aTopic}`　"
                            + ((e.memory_archived_commit ?? "").Length > 0
                                ? $"（已歸檔 `{e.memory_archived_commit}` —— 這是正常的，只是提醒接手要去 git 找）"
                                : "**主題不在磁碟上且沒有歸檔 sha** ⇒ 連結壞了，不是沒有記憶"));
                    }
                    int aDays = e.DaysSinceUpdate(aNow);
                    if (aDays >= UCL_TaskIO.STALE_DAYS)
                        aColdMemory.Add($"{e.Id} `{e.status}` {Trunc(e.title, 50)}　**{aDays} 天沒動**"
                            + (aTopic.Length == 0 ? "（沒掛記憶 ⇒ 接手的人只有這張單）"
                                                  : $"　記憶：`{aTopic}`"));
                }
                sb.AppendLine(aBrokenLink.Count == 0
                    ? "- ✅ ④a 記憶連結沒有壞的（掛了主題的單，主題都在）"
                    : $"- ⚠ ④a 有 **{aBrokenLink.Count}** 筆記憶連結要看：");
                foreach (var s in aBrokenLink) sb.AppendLine("    · " + s);
                sb.AppendLine(aColdMemory.Count == 0
                    ? $"- ✅ ④b 跟我有關的未關單都在 {UCL_TaskIO.STALE_DAYS} 天內動過"
                    : $"- 🧊 ④b 有 **{aColdMemory.Count}** 張未關單超過 {UCL_TaskIO.STALE_DAYS} 天沒動"
                        + "（跨多日大 Task 死在這裡：單還開著，而沒人記得上次做到哪）：");
                foreach (var s in aColdMemory) sb.AppendLine("    · " + s);
                if (aColdMemory.Count > 0)
                    sb.AppendLine("    ⇒ **只印不改**（契約②）：要嘛去推進它，要嘛把現況寫進它的記憶主題，"
                        + "要嘛 `op=update` 改狀態說明它為什麼停著。");

                // ===========================================================
                // ⑤ 收工預告（TASK-0019 唯一退回的那格；QA basecamp 2026-08-27）
                // 物理意義：PM 把原驗收的「印出…並擋住」拆成兩半 —— **擋**留在 `step=sleep`
                //   （閘要長在真正下線的必經路上），**印**補在這裡。而在此之前 check 對收工閘
                //   **一個字都沒印**：人要一路走到 sleep 才第一次知道等一下會被什麼擋住。
                // ⚠ 這一段**只列不擋** —— check 的契約是唯讀起手（本檔開頭那條「只印不改」）。
                // ⚠ 印的是 `PendingWrapups` 本人，**跟閘同一個述詞**，不是另寫一份
                //   「我猜它會擋什麼」的清單 —— 那就是第二個真相源，而它會漂：
                //   預告與實擋一旦分家，最先出現的樣子是「預告說沒事而 sleep 擋下」，
                //   那比沒有預告更糟（人會開始不信預告）。
                //   📌 代價是 `LoadAll()` 多跑一次（本段與 ①-④ 各載一次）——
                //     刻意付：省下那一次就得把清單傳進來，而傳進來的東西會跟閘的判準分岔。
                // ⚠ 零筆也要印：「沒印」跟「沒對」在回傳檔上長得一樣（同本方法開頭那條）。
                // ===========================================================
                var aPreWrapup = PendingWrapups(iPersona);
                sb.AppendLine(aPreWrapup.Count == 0
                    ? "- ✅ ⑤ 收工預告：目前**沒有單會擋下線**"
                        + "（本次醒來後有動靜（含別人在單上留言）、還開著、而收工紀錄已過期或從未收工的單：0 張）"
                    : $"- 🔔 ⑤ 收工預告：有 **{aPreWrapup.Count}** 張單會在 `step=sleep` **實擋**"
                        + "（本次醒來後有動靜（含別人在單上留言）＋ 還開著 ＋ 我是參與者 ＋ 最後一次收工之後又有動靜／從沒收過工）：");
                foreach (var e in aPreWrapup)
                    sb.AppendLine($"    · {e.Id} `{e.status}` {Trunc(e.title, 60)}");
                if (aPreWrapup.Count > 0)
                {
                    sb.AppendLine("    ⇒ 現在就可以收（**本段只列不擋**，擋的是 `step=sleep`）：");
                    // ⚠ 不做 Take(N)：截斷的清單看起來跟完整的清單一模一樣，
                    //   而這份清單的長度本來就被「本次醒來有動靜的單」夾住了。
                    foreach (var e in aPreWrapup)
                        sb.AppendLine($"      `run Task --arg op=wrapup --arg index={e.index}"
                            + " --arg-file progress=<還剩什麼、下一步從哪接>"
                            + " [--arg-file why=<為什麼卡住／試過什麼不行 ⇒ 進工作記憶>]`");
                    sb.AppendLine("    ⇒ 真的沒東西可寫 → `step=sleep` 帶 `--arg skip_reason=<一句話>`"
                        + "（理由會寫進那幾張單的時間線）。");
                }
            }
            catch (Exception ex)
            {
                // ⚠ 失敗要看得見：晚安流程照走，但「今晚沒對到帳」不可以長得像「對過帳沒問題」
                sb.AppendLine($"- ⚠ **對帳失敗**（{ex.Message}）—— 這一段沒有讀數，"
                    + "不要當成「沒有不一致」。");
            }
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：晚安的**收工閘** —— 本次醒來後有動靜（含別人在單上留言）、還開著、我是參與者，
        //   而**最後一次收工之後又有動靜**（或從沒收過工）的單。
        //
        // 物理意義（Tim 2026-08-24 補的洞）：跨多日接回真正會斷的地方不是「忘了寫記憶」，
        //   是**單子還開著、狀態還是 in_progress，而沒有人知道停在哪一步**。
        //   ⇒ 所以閘的判準是「**本次醒來後有動靜**」而不是「有沒有記憶」：
        //     今天沒碰的單不該擋我下線（那是別天的事）。
        //
        // ⚠ 判定「今天收工過了」的唯一依據是**該單時間線裡今天的 `wrapup` 事件** ——
        //   不另存一份「收過工的清單」（那就是第二個真相源，而它會漂）。
        //   ⚠ `last_wrapup_at` 不算第二真相源：它由 `op=wrapup` 在同一次寫入裡落下，
        //     而讀不到時**回頭問時間線**（見 `LastWrapupUtc`）—— 兩者永遠指向同一個事件。
        //
        // 🩸 血證 2026-08-25（summit 自驗 TASK-0019 那格「跨夜沒驗」）——
        //   舊版用 `DateTime.UtcNow.ToString("yyyy-MM-dd")` 當「今天」，並拿它去**字串比對** UTC 時間戳。
        //   ⇒ 換日發生在 **UTC 午夜**，而本地是 +08 ⇒ **邊界落在本地早上 08:00**，
        //     也就是這個團隊每天開工的時間（08-25 那天 summit 08:16、basecamp 08:17 早安）。
        //   實測（探針 TASK-0029，同一張單只改 `updated_at` 一格）：
        //     `2026-08-25T01:00Z`（本地 09:00）⇒ 🛑 擋下並點名；
        //     `2026-08-24T23:50Z`（**本地同一天 07:50**）⇒ ✅ **靜默放行，一個字都沒說**。
        //   📌 而我當初記在見叢的猜測是錯的：我寫「午夜前後語意模糊」——
        //     **跨本地午夜反而安全**（那時 UTC 還在同一天），危險的是跨本地早上八點。
        //     ⇒ 一般形：**「今天」是人的概念，而人講的今天是本地日；
        //       拿 UTC 日去代表它，等於把換日點搬到一個沒有人會注意的時刻。**
        //
        // ⚠ 2026-08-25 Tim 拍板：**系統面一律 UTC，只有顯示面用本地時間。**
        //   ⇒ 於是「本地日」那個修法不能留（閘是系統面）。而單純換回 UTC 日會**把上面那隻放回去**。
        //   📌 兩個都不對，代表問題不在「用哪套曆」，在**這裡根本不該用曆**：
        //     這道閘真正要問的從來不是「今天」，是「**我這次上線之後**」——
        //     goodnight 依定義就是一段 session 的結尾，而 session 的起點寫在
        //     `_session/_persona_<name>.json` 的 `locked_at`（本來就是 UTC）。
        //   ⇒ 現行：兩個述詞都改成**跟 `locked_at` 比大小的純 UTC 時間戳比較** ——
        //     零日曆、零時區轉換，比「一律 UTC 日期」更嚴格地符合拍板。
        //     附帶好處：跨夜 session（wake#62 那種）本來就該把整段都算進來，而日曆做不到。
        // ⚠ 拿不到 lock（例如沒登入就跑）⇒ 退回 **UTC 日**（拍板的預設），不退回本地日。
        // ===========================================================
        public static List<UCL_TaskEntry> PendingWrapups(string iPersona)
        {
            var aOut = new List<UCL_TaskEntry>();
            DateTime aSince = SessionStartUtc(iPersona);
            foreach (var e in UCL_TaskIO.LoadAll())
            {
                if (e.IsClosed()) continue;                       // 已關的不看（反向驗收要求）
                if (e.RolesOf(iPersona).Count == 0) continue;      // 別人的單不看
                if (!IsAfterUtc(e.updated_at, aSince)) continue;   // ① 本次上線後沒動過的不看
                // ② 因果判準：**最後一次收工之後，有沒有又動過**（TASK-0036）。
                //   舊版問的是「本次上線後有沒有收過工」⇒ 10:00 收工、11:00 又改了照樣放行，
                //   而那正是閘要防的東西：**收工留言寫完之後又動了，那份收工紀錄就過期了。**
                //   🩸 讀數 2026-08-25（探針 TASK-0042）：wrapup 02:19:50 → updated_at 推到 02:59
                //     ⇒ 舊版收工閘**零命中**。
                DateTime aLastWrapup = LastWrapupUtc(e);
                if (aLastWrapup != DateTime.MinValue && !IsAfterUtc(e.updated_at, aLastWrapup)) continue;
                aOut.Add(e);
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：本次 session 的起點（UTC）。
        // 物理意義：`locked_at` 是早安登入寫的，**它就是這段工作的起點**。
        // ⚠ 讀不到就退回「UTC 今天 00:00」—— 那是拍板的預設曆，**不是本地日**。
        //   （讀不到的情境：沒登入就跑閘、lock 被清掉。此時退回日曆是刻意的降級，不是 fail-open：
        //     它仍然會擋 UTC 今天動過的單，只是拿不到跨夜那一段。）
        // ===========================================================
        static DateTime SessionStartUtc(string iPersona)
        {
            try
            {
                var aLock = Awakening.UCL_AwakeningService.ReadLock(iPersona);
                if (aLock != null && !string.IsNullOrWhiteSpace(aLock.locked_at)
                    && DateTime.TryParse(aLock.locked_at, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc))
                    return aUtc;
            }
            catch { /* 讀不到就走下面的降級 —— 降級是有曆的那個，不是沒有閘 */ }
            return DateTime.UtcNow.Date;
        }

        /// <summary>ISO 時間戳晚於 <paramref name="iSinceUtc"/> 嗎（純 UTC 比大小，不碰時區轉換）。</summary>
        static bool IsAfterUtc(string iIso, DateTime iSinceUtc)
        {
            if (string.IsNullOrWhiteSpace(iIso)) return false;
            return DateTime.TryParse(iIso, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AdjustToUniversal
                       | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)
                   && aUtc > iSinceUtc;
        }

        // ===========================================================
        // 區塊職責：解析時間戳的兩個述詞（都在 §PendingWrapups 上方那段血證的射程內）。
        // ⚠ 解析不出來一律回 **false** —— 對 `updated_at` 是「不擋」，對 `wrapup` 是「當成沒收工」，
        //   兩邊都倒向**擋下**那一側（而擋下有出口：`skip_reason`；放行沒有）。
        // ===========================================================

        // ===========================================================
        // 區塊職責：這張單**最後一次 `wrapup`** 的時戳（UTC）；沒有回 `DateTime.MinValue`。
        //
        // 來源順序（**刻意不是「缺值就擋」**，這格是我對驗收標準的一處偏離，已在單上標明）：
        //   ① frontmatter 的 `last_wrapup_at` —— `op=wrapup` 寫的，正常路徑走這條
        //   ② 讀不到就**回頭問時間線**（`wrapup` 事件本來就在那裡）
        //   ③ 兩邊都沒有 ⇒ 從來沒收過工 ⇒ 回 MinValue ⇒ 呼叫端**擋下**
        //
        // 為什麼不直接「缺值就擋」：本欄位是 2026-08-25 才加的，**所有既有單都缺值** ——
        //   一律擋的話，上線當晚每個人都會被自己收過工的舊單擋住，
        //   而那正是「修完立刻天天亮」。⇒ 時間線是同一件事的既有紀錄，問它就不必回填。
        // ⚠ 而「兩邊都沒有」仍然倒向擋下（擋下有 `skip_reason` 出口，放行沒有）——
        //   驗收標準要的那個方向沒有被放掉，只是把「缺值」拆成了「真的沒收過工」與「欄位還沒補」。
        // ===========================================================
        public static DateTime LastWrapupUtc(UCL_TaskEntry e)
        {
            if (e == null) return DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(e.last_wrapup_at)
                && DateTime.TryParse(e.last_wrapup_at, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aField))
                return aField;
            return LastWrapupFromTimeline(e.index);
        }

        /// <summary>時間線裡**最後一筆** `wrapup` 的時戳；沒有回 <c>DateTime.MinValue</c>。</summary>
        static DateTime LastWrapupFromTimeline(int iIndex)
        {
            var aOut = DateTime.MinValue;
            try
            {
                string aPath = UCL_TaskIO.TaskPath(iIndex);
                if (!File.Exists(aPath)) return aOut;
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    string aTrim = aLine.TrimStart();
                    if (!aTrim.StartsWith("- ", StringComparison.Ordinal)) continue;
                    if (aLine.IndexOf("`wrapup`", StringComparison.Ordinal) < 0) continue;
                    string aStamp = aTrim.Substring(2).TrimStart();
                    int aCut = aStamp.IndexOfAny(new[] { ' ', '\t', '　' });
                    if (aCut > 0) aStamp = aStamp.Substring(0, aCut);
                    if (DateTime.TryParse(aStamp, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal
                            | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)
                        && aUtc > aOut) aOut = aUtc;
                }
            }
            catch { /* 讀不到就當沒有 —— 呼叫端會擋下，而擋下有出口 */ }
            return aOut;
        }

        // ⛔ `HasWrapupSince(index, since)` 已於 2026-08-25（TASK-0036）移除。
        //   它問的是「**有沒有**收過工」，而閘要問的是「**最後一次**收工之後有沒有又動過」——
        //   兩者在「10:00 收工、11:00 又改了」那格給相反答案，而舊的那個會**放行**。
        //   ⚠ 刻意不留著：一支長得像判準的死函式，下一個人會拿它去重造同一隻。
        //   要最後一次收工時戳請用 `LastWrapupUtc(entry)`。
        /// <summary>
        /// 顯式跳過收工閘：把理由寫進**那張單的時間線**。
        /// <para>⚠ 不是寫進 log —— **跳過要留在別人看得到的地方**（basecamp 拍板：
        /// 可跳過但留名，比不可跳過更持久；硬擋會讓人去找繞過的方法，而繞過一次那道閘就永久失效）。</para>
        /// </summary>
        public static void WriteSkip(UCL_TaskEntry e, string iPersona, string iReason)
        {
            if (e == null) return;
            string aNow = UCL_TaskIO.NowUtc();
            UCL_TaskIO.Touch(e, aNow);
            UCL_TaskIO.Save(e, "", "", $"{aNow}　`wrapup-skip`　{iPersona} 顯式跳過收工："
                + iReason.Replace("\r", " ").Replace("\n", " "));
        }

        static List<string> RolesOrReporter(UCL_TaskEntry e, string iPersona)
        {
            var aRoles = e.RolesOf(iPersona);
            if (aRoles.Count > 0) return aRoles.Select(r => r.ToString()).ToList();
            return new List<string> { "reporter（開單人，未列參與者）" };
        }

        static string Trunc(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
#endif
