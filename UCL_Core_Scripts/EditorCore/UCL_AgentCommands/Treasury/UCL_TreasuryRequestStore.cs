// 區塊職責：請款單（TreasuryPayoutRequest）的唯一讀寫入口 —— 建立 / 列出 / 審批。
// 物理意義：請款流程有**兩個端點**在碰同一批檔案：
//            ① Cmd_Treasury 的 op=request / request_list / request_cancel（agent 側，走 queue）
//            ② UCL_BankAdminPage 的請款審批面板（Tim 側，Editor GUI）
//          兩端各寫一份檔案 IO = 雙實作，schema 一漂就對不上（2026-07-31 已經吃過
//          「同一語意三處實作」的教訓，見 docs/Glossary/same-code-mute.md 一族）。故收攏於此。
// 數值影響：本類別只在 Approve() 內動錢，且 2026-08-01 起**不再憑空產生** —— 改為
//          「央行 Debit → 收款方 Credit」的閉環撥款（Tim：請款流程改為從央行扣，經濟循環）。
//          央行不足額 = 核准被拒，不偷偷 mint。詳見 Approve() 內區塊註解。
//          Create/Cancel/Reject 全是純檔案操作，對帳面零影響。
// 設計取捨：
//   - **status 就地改寫**（pending → approved/rejected/cancelled）而非 append 新事件：
//     請款單是流程物件不是會計紀錄，會計真相在 ledger（append-only 不變）。
//     單一檔案帶最終狀態，人在後台看列表時不必自己 fold 事件流。
//   - **核准是 credit-then-mark**：先寫 ledger 再標記 approved。順序刻意如此 ——
//     若中間掛掉，結果是「錢付了但單子還 pending」（可由人看見並手動標記），
//     反過來則是「單子說付了但沒錢」（agent 以為收到了，最難查）。**寧可重複可見，不要靜默漏付。**
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>請款單存取層 — Cmd 側與 BankAdminPage 側共用（勿另造第二份檔案 IO）。</summary>
    public static class UCL_TreasuryRequestStore
    {
        public const string StatusPending = "pending";
        public const string StatusApproved = "approved";
        public const string StatusRejected = "rejected";
        public const string StatusCancelled = "cancelled";

        // 區塊職責：建立一張 pending 請款單
        // 物理意義：agent 主張「該付我 N token 到 bank X，理由 Y」。此刻不動任何錢。
        // 數值影響：寫一個新 .json；request_id = 6 hex，對齊檔名（跟 ledger entry 同慣例）。
        // 邊界：amount <= 0 / 缺 target_bank / 缺 reason → 直接丟 ArgumentException，
        //      由 caller 轉成 reject 訊息。**不允許無理由請款** —— 審批者要有東西可判。
        public static TreasuryPayoutRequest Create(
            string targetBank, int amount, string reason,
            string sourceKind, string sourceRef,
            string requesterAgent, string requesterPersona,
            string currency = "tavern_token")
        {
            if (string.IsNullOrWhiteSpace(targetBank)) throw new ArgumentException("target_bank 必填（收款 agent id，不是 persona 名）");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("reason 必填 —— 審批者要有東西可判");

            var now = DateTime.UtcNow;
            string uuid6 = Guid.NewGuid().ToString("N").Substring(0, 6);
            var req = new TreasuryPayoutRequest
            {
                request_id = uuid6,
                requested_at = now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z",
                status = StatusPending,
                target_bank = targetBank.Trim(),
                amount = amount,
                currency = string.IsNullOrWhiteSpace(currency) ? "tavern_token" : currency.Trim(),
                reason = reason.Trim(),
                source_kind = string.IsNullOrWhiteSpace(sourceKind) ? "manual_request" : sourceKind.Trim(),
                source_ref = sourceRef?.Trim(),
                requester_agent = requesterAgent?.Trim(),
                requester_persona = requesterPersona?.Trim(),
            };

            string dir = UCL_TreasuryPaths.GetRequestDateDir(now);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, UCL_TreasuryPaths.BuildRequestFileName(now, uuid6));
            WriteJson(path, req);
            Debug.Log($"[Treasury] 請款單建立 {uuid6}: {amount} {req.currency} → bank `{req.target_bank}`（{req.requester_agent}@{req.requester_persona}）");
            return req;
        }

        // 區塊職責：列出請款單（可選只列 pending）
        // 物理意義：後台面板與 agent 查詢共用同一個列舉邏輯。
        // 數值影響：純讀；單檔壞（寫入中 / JSON 不完整）→ 跳過該檔，不擋整份列表。
        public static List<TreasuryPayoutRequest> List(bool pendingOnly = false, int maxCount = 200)
        {
            var result = new List<TreasuryPayoutRequest>();
            string root = UCL_TreasuryPaths.GetRequestsRoot();
            if (!Directory.Exists(root)) return result;
            // 日期夾字典序 = 時間序；由新到舊掃，湊滿 maxCount 就停
            foreach (var dateDir in Directory.GetDirectories(root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                foreach (var f in Directory.GetFiles(dateDir, "*__request.json").OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    var req = TryRead(f);
                    if (req == null) continue;
                    if (pendingOnly && req.status != StatusPending) continue;
                    result.Add(req);
                    if (result.Count >= maxCount) return result;
                }
            }
            return result;
        }

        /// <summary>按 request_id 找檔案路徑；找不到回 null。</summary>
        public static string FindPath(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            string root = UCL_TreasuryPaths.GetRequestsRoot();
            if (!Directory.Exists(root)) return null;
            foreach (var dateDir in Directory.GetDirectories(root))
            {
                foreach (var f in Directory.GetFiles(dateDir, $"*_{requestId}__request.json"))
                    return f;
            }
            return null;
        }

        // 區塊職責：核准並實際打款
        // 物理意義：這是整個機制唯一會生出錢的地方。credit 進 ledger 之後才把單子標記 approved。
        // 數值影響：目標帳戶 +amount；請款單 status/decided_* / ledger_entry_uuid 就地更新。
        // 邊界：非 pending 的單子拒絕重複核准（防連點兩次付兩次）—— 這裡**有**防重複，
        //      跟 commit 公告那條刻意不防不同：那邊的重複肉眼可見（酒館公開），
        //      這邊是後台單人操作，沒有旁人可看，所以必須由機制擋。
        public static TreasuryPayoutRequest Approve(string requestId, string decidedBy, string note = null)
        {
            string path = FindPath(requestId);
            if (path == null) throw new InvalidOperationException($"找不到請款單 {requestId}");
            var req = TryRead(path);
            if (req == null) throw new InvalidOperationException($"請款單 {requestId} 讀取失敗（JSON 壞了？）");
            if (req.status != StatusPending)
                throw new InvalidOperationException($"請款單 {requestId} 目前是 {req.status}，只有 pending 可核准（防重複打款）");

            // ── 區塊：請款改由央行出帳（Tim 2026-08-01「請款流程可以改為從央行扣（經濟循環）」）──
            // 物理意義：此前核准是**憑空 Credit** —— token 從無到有，經濟體只進不出。
            //          改成從央行扣之後閉環成立：保管費流進央行 → 核准的請款從央行流回 agent。
            //          央行餘額因此是**這個經濟體真實的可支配預算**，不再是一個純統計數字。
            // 數值影響：央行不足額 → **拒絕核准並明說差額**，不靜默憑空生錢。
            //          這是刻意的：閉環經濟的意義就在於「發得出來的錢有上限」，
            //          若不足時偷偷 mint，央行餘額就變成裝飾品，而且沒有人會發現。
            //          真的要超發 → 走 BankAdminPage 打款先補央行（那是有紀錄的動作）。
            // 邊界：收款方就是央行本身時不扣（自轉無意義，同 daemon 的央行豁免）。
            // 設計取捨：debit→credit→rollback 的三段式照抄 Cmd_Treasury.Op_Transfer 的既有形狀，
            //          不發明第二套轉帳流程。
            string centralBank = UCL_CentralBankSettings.CentralBankAccount;
            bool drawFromCentralBank = !string.IsNullOrEmpty(centralBank) && req.target_bank != centralBank;
            TreasuryLedgerEntry cbDebit = null;
            if (drawFromCentralBank)
            {
                try
                {
                    cbDebit = UCL_TreasuryLedger.Debit(
                        accountId: centralBank,
                        amount: req.amount,
                        useKind: "payout_request_disbursement",
                        useRef: req.source_ref,
                        description: $"payout request {req.request_id} 撥款給 @{req.target_bank}"
                                     + $"（核准者 {(string.IsNullOrEmpty(decidedBy) ? "system" : decidedBy)}）: {req.reason}",
                        // ⚠ callerAgentId **必須是 "system"**，不能傳 decidedBy（2026-08-01 實測踩到）：
                        //   Debit 有帳戶隔離鐵律 —— caller 非 "system" 且 != accountId 就拋例外
                        //   （UCL_TreasuryLedger.cs「不可動用對方帳戶」）。傳 "Tim" 的話
                        //   **每一次核准都會被自己的隔離規則擋死**，而且錯誤訊息長得像央行的問題。
                        //   央行撥款本來就是系統內部轉帳（人只是按下核准），核准者身分記在
                        //   description 與請款單的 decided_by 欄，不靠 callerAgentId 承載。
                        callerAgentId: "system",
                        cmdId: $"payout_request_{req.request_id}");
                }
                catch (Exception ex)
                {
                    int cbBalance = -1;
                    try { cbBalance = UCL_TreasuryLedger.GetBalance(centralBank); } catch { }
                    throw new InvalidOperationException(
                        $"央行 `{centralBank}` 出帳失敗，請款單 {req.request_id} 未核准（沒有動任何錢）。" +
                        $"央行餘額 {cbBalance} / 本單需 {req.amount}。" +
                        $"要放行請先從銀行後台打款補足央行，不要繞過閉環。原因：{ex.Message}");
                }
            }

            TreasuryLedgerEntry entry;
            try
            {
                entry = UCL_TreasuryLedger.Credit(
                    accountId: req.target_bank,
                    amount: req.amount,
                    sourceKind: req.source_kind,
                    sourceRef: req.source_ref,
                    description: $"payout request {req.request_id} approved: {req.reason}"
                                 + (drawFromCentralBank ? $"（由 {centralBank} 撥款）" : ""),
                    callerAgentId: string.IsNullOrEmpty(decidedBy) ? "system" : decidedBy,
                    cmdId: $"payout_request_{req.request_id}");
            }
            catch (Exception ex)
            {
                // 央行已扣但收款方沒進帳 → 退回央行，否則那筆錢憑空消失且無聲
                if (cbDebit != null)
                {
                    try
                    {
                        UCL_TreasuryLedger.Credit(centralBank, req.amount, "payout_request_rollback",
                            req.source_ref + "|rollback",
                            $"請款 {req.request_id} 撥款失敗回滾: {ex.Message}", "system",
                            $"payout_request_{req.request_id}_rollback");
                    }
                    catch (Exception rollbackEx)
                    {
                        throw new InvalidOperationException(
                            $"請款 {req.request_id} Credit 失敗且央行回滾也失敗 —— " +
                            $"央行有一筆懸空 debit（uuid={cbDebit.uuid}, 金額 {req.amount}）需人工處理。" +
                            $"orig={ex.Message} / rollback={rollbackEx.Message}");
                    }
                }
                throw new InvalidOperationException(
                    $"請款 {req.request_id} 撥款失敗{(cbDebit != null ? "（央行已回滾，帳目平）" : "")}: {ex.Message}");
            }

            req.status = StatusApproved;
            req.decided_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            req.decided_by = string.IsNullOrEmpty(decidedBy) ? "Tim" : decidedBy;
            req.decision_note = note;
            req.ledger_entry_uuid = entry != null ? entry.uuid : null;
            WriteJson(path, req);
            Debug.Log($"[Treasury] 請款單核准 {requestId}: +{req.amount} → `{req.target_bank}`（餘額 {entry?.balance_after}）");
            return req;
        }

        /// <summary>駁回 / 取消 —— 不動任何錢，只改 status 與理由。</summary>
        public static TreasuryPayoutRequest Close(string requestId, string newStatus, string decidedBy, string note = null)
        {
            if (newStatus != StatusRejected && newStatus != StatusCancelled)
                throw new ArgumentException($"newStatus 只接受 {StatusRejected} / {StatusCancelled}");
            string path = FindPath(requestId);
            if (path == null) throw new InvalidOperationException($"找不到請款單 {requestId}");
            var req = TryRead(path);
            if (req == null) throw new InvalidOperationException($"請款單 {requestId} 讀取失敗");
            if (req.status != StatusPending)
                throw new InvalidOperationException($"請款單 {requestId} 目前是 {req.status}，只有 pending 可變更");
            req.status = newStatus;
            req.decided_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            req.decided_by = decidedBy;
            req.decision_note = note;
            WriteJson(path, req);
            Debug.Log($"[Treasury] 請款單 {requestId} → {newStatus}（by {decidedBy}）");
            return req;
        }

        // ===========================================================
        // 區塊：檔案 IO —— atomic write（tmp + move），避免半寫檔被列表端讀到
        // ===========================================================
        static TreasuryPayoutRequest TryRead(string path)
        {
            try
            {
                var json = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (json == null || !json.IsObject) return null;
                var req = new TreasuryPayoutRequest();
                JsonConvert.LoadFieldFromJsonUnityVer(req, json);
                if (string.IsNullOrEmpty(req.request_id)) return null;
                return req;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] 請款單讀取失敗（跳過）{path}: {ex.Message}");
                return null;
            }
        }

        static void WriteJson(string path, TreasuryPayoutRequest req)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SaveFieldsToJsonUnityVer(req).ToJsonBeautify(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
#endif
