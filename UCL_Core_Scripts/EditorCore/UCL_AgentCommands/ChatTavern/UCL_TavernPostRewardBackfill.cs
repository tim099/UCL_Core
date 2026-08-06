// 區塊職責：post_reward（發文 +1 token）的**事後補款** — 找出「當時該發而沒發」的訊息並補上。
// 物理意義：這是**增發**不是轉帳（Tim 2026-08-06）—— 走 `Credit`，不從央行扣款，
//          與現行發放路徑做的事完全相同，只是時間點在事後。
// 數值影響：Plan 完全唯讀。Apply 每則 +1 token，account = sender，source_kind = "work_post"
//          （沿用同一個 kind，歷史查詢才不會斷成兩半）。
//
// 為什麼需要它：2026-07-30 把判準由 `m_IsWorkChannel` 換成 `m_IsPaidPost`，但 asset 資料沒補上
//   新欄位 → 反序列化拿 bool 預設 false → 計酬那條分支**靜默**跳過。
//   ledger 實掃：最後一筆 work_post 是 2026-07-29，之後全 0 ——「修復」當天就沒接上，被相信了 8 天。
//   而這種事會再發生（同族已經兩次），所以補款要做成**可重跑的機制**，不是一次性腳本。
//
// ⚠ 兩條命脈，缺一條這支就不能用：
//
// ① **判準必須與現行發放路徑同源。** 本檔呼叫 `Cmd_Tavern.IsPostRewardEligible()` 本人，
//    不複製規則。自己抄一份的話，補出來的是「補款作者以為當時會發的」而不是當時真的會發的 ——
//    而那種差異沒有人會發現：帳看起來是平的，只是平在錯的基準上。
//
// ② **冪等靠事實不靠旗標。** `UCL_TreasuryLedger` **沒有** idempotency 機制
//    （cmd_id 只寫進 sig_cmd_id、不參與去重 —— 2026-07-30 實測確認）。
//    所以本檔在跑之前把 ledger 裡**所有** work_post 的 source_ref 收成集合，
//    逐則比對「這一則發過沒」。重跑兩次不會付兩次錢，不是因為我小心，
//    是因為判準是「帳上有沒有這筆」——**問帳本，不問自己的記憶**。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public class UCL_PostRewardBackfillResult
    {
        public int ScannedMessages;
        public int AlreadyPaid;
        public int Eligible;                 // 該補的則數（＝要增發的 token 數）
        public int Credited;
        public bool Cancelled;
        public string Error = "";
        public readonly Dictionary<string, int> ByAccount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> SkipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Failures = new List<string>();
    }

    public static class UCL_TavernPostRewardBackfill
    {
        const string SourceKind = "work_post";

        /// <summary>
        /// 區塊職責：把 ledger 裡所有 work_post 的 source_ref 收成集合（＝「已經發過的那些則」）。
        /// 物理意義：這是冪等的唯一依據。**問帳本，不問旗標** —— ledger 沒有去重機制，
        ///          所以「發過沒」只能由帳上有沒有那筆來回答。
        /// 數值影響：一次全掃（現有約 8000+ 筆），單次成本可接受；Plan / Apply 各建一次。
        /// </summary>
        static HashSet<string> LoadPaidRefs(out string error)
        {
            error = "";
            var set = new HashSet<string>(StringComparer.Ordinal);
            string ledgerRoot;
            try { ledgerRoot = Treasury.UCL_TreasuryPaths.GetLedgerRoot(); }
            catch (Exception e) { error = "找不到 ledger 目錄：" + e.Message; return set; }
            if (!Directory.Exists(ledgerRoot)) { error = "ledger 目錄不存在：" + ledgerRoot; return set; }

            foreach (string dayDir in Directory.GetDirectories(ledgerRoot))
            {
                foreach (string f in Directory.GetFiles(dayDir, "*.json"))
                {
                    try
                    {
                        var j = JsonData.ParseJson(File.ReadAllText(f, Encoding.UTF8));
                        if (j == null || !j.Contains("source_kind")) continue;
                        if (j["source_kind"].GetString() != SourceKind) continue;
                        if (j.Contains("source_ref")) set.Add(j["source_ref"].GetString());
                    }
                    catch { /* 單筆壞檔不該讓整個補款停擺；但它會讓那則被當成「沒發過」→ 見下方保護 */ }
                }
            }
            return set;
        }

        /// <summary>
        /// 區塊職責：掃全部房間訊息，算出「該補哪些」。
        /// 邊界：apply=false 時完全唯讀。
        /// 設計取捨：**不設時間窗**。判準是「帳上有沒有」而不是「哪一天之後」——
        ///          時間窗要人挑，挑錯就漏補或重補；而漏補是靜默的。
        /// </summary>
        public static UCL_PostRewardBackfillResult Run(bool apply)
        {
            var r = new UCL_PostRewardBackfillResult();
            var paid = LoadPaidRefs(out string err);
            if (!string.IsNullOrEmpty(err)) { r.Error = err; return r; }
            // 帳本讀不到任何一筆 work_post 是危險狀態：可能是真的沒發過，也可能是路徑錯／全部壞檔。
            // 後者會讓補款把**每一則都當成沒發過**而重複增發。分不出來就不要動錢。
            if (paid.Count == 0)
            {
                r.Error = "ledger 裡找不到任何 work_post 紀錄 —— 可能是路徑錯或解析全失敗。"
                          + "無法分辨「真的沒發過」與「讀不到帳」，**拒絕執行**（避免重複增發）。"
                          + "歷史上確實有 8000+ 筆 work_post，若真的歸零請人工確認後再跑。";
                return r;
            }

            string roomsRoot = UCL_ChatTavernIO.GetRoomsRoot();
            if (!Directory.Exists(roomsRoot)) { r.Error = "找不到 rooms 目錄：" + roomsRoot; return r; }

            try
            {
                string[] roomDirs = Directory.GetDirectories(roomsRoot);
                for (int ri = 0; ri < roomDirs.Length; ri++)
                {
                    string roomId = Path.GetFileName(roomDirs[ri]);
                    string[] files = UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths(roomId);
                    if (files.Length == 0) continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            apply ? "post_reward 補款（增發）" : "post_reward 補款試算",
                            $"{roomId}（{files.Length} 則）", (float)ri / roomDirs.Length))
                    { r.Cancelled = true; return r; }

                    for (int i = 0; i < files.Length; i++)
                    {
                        int seq = i + 1;                       // seq 的定義：排序清單的 index+1（與發放路徑一致）
                        string sref = Cmd_Tavern.PostRewardSourceRef(roomId, seq);
                        r.ScannedMessages++;
                        if (paid.Contains(sref)) { r.AlreadyPaid++; continue; }

                        UCL_ChatMessage msg;
                        try { msg = UCL_ChatTavernIO.ParseMessage(File.ReadAllText(files[i], Encoding.UTF8)); }
                        catch { Bump(r.SkipReasons, "訊息檔解析失敗"); continue; }
                        if (msg == null) { Bump(r.SkipReasons, "訊息檔解析失敗"); continue; }

                        // meta 是 Dictionary<string,string>（不是 JsonData）—— 型別看錯會編不過，
                        // 但更值得記的是：這是今天第 N 次「我以為某欄位是另一種東西」。
                        string category = "";
                        if (msg.meta != null) msg.meta.TryGetValue("category", out category);

                        // ★ 判準走現行發放路徑的同一支函式，不複製規則
                        if (!Cmd_Tavern.IsPostRewardEligible(msg.sender_id, category, out _, out string why))
                        { Bump(r.SkipReasons, why); continue; }

                        r.Eligible++;
                        Bump(r.ByAccount, msg.sender_id ?? "(unknown)");

                        if (apply)
                        {
                            try
                            {
                                Treasury.UCL_TreasuryLedger.Credit(
                                    accountId: msg.sender_id,
                                    amount: 1,
                                    sourceKind: SourceKind,
                                    sourceRef: sref,
                                    description: $"post reward backfill: room={roomId} seq={seq} category="
                                                 + (string.IsNullOrEmpty(category) ? "(unset→default)" : category),
                                    callerAgentId: "system",
                                    cmdId: $"backfill_work_post_{roomId}_{seq}");
                                r.Credited++;
                                paid.Add(sref);      // 同一次執行內也不重複（防同房重掃）
                            }
                            catch (Exception e)
                            {
                                r.Failures.Add($"{sref}: {e.Message}");
                                if (r.Failures.Count > 20) { r.Error = "失敗過多，中止"; return r; }
                            }
                        }
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            return r;
        }

        static void Bump(Dictionary<string, int> d, string k)
        {
            if (string.IsNullOrEmpty(k)) k = "(未分類)";
            d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;
        }

        public static string Format(UCL_PostRewardBackfillResult r, bool applied)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(r.Error)) return "🚫 " + r.Error;
            sb.AppendLine($"掃過訊息 {r.ScannedMessages} 則 / 已發過 {r.AlreadyPaid} / **該補 {r.Eligible}**");
            if (r.Cancelled) sb.AppendLine("⚠ 使用者取消 —— 已增發的部分不會回收（重跑會跳過它們，不重複）。");
            if (applied) sb.AppendLine($"✅ 已增發 {r.Credited} token");
            if (r.ByAccount.Count > 0)
            {
                sb.AppendLine(applied ? "\n增發明細：" : "\n若執行，各帳戶會拿到：");
                foreach (var kv in SortDesc(r.ByAccount)) sb.AppendLine($"   {kv.Value,6}  {kv.Key}");
            }
            if (r.SkipReasons.Count > 0)
            {
                sb.AppendLine("\n未補的原因（＝當時本來就不該發）：");
                foreach (var kv in SortDesc(r.SkipReasons)) sb.AppendLine($"   {kv.Value,6}  {kv.Key}");
            }
            if (r.Failures.Count > 0)
            {
                sb.AppendLine($"\n🚨 增發失敗 {r.Failures.Count} 筆：");
                for (int i = 0; i < r.Failures.Count && i < 10; i++) sb.AppendLine("   " + r.Failures[i]);
            }
            return sb.ToString();
        }

        static List<KeyValuePair<string, int>> SortDesc(Dictionary<string, int> d)
        {
            var l = new List<KeyValuePair<string, int>>(d);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            return l;
        }
    }
}
#endif
