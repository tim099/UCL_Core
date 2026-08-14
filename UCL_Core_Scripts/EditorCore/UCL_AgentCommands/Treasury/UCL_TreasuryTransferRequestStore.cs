// 區塊職責：轉帳單（TreasuryTransferRequest）的唯一讀寫入口 —— 建立 / 列出 / 審批。
// 物理意義：跟請款單的分工是「錢從哪來」——
//            請款＝央行撥款給某人（單邊，消耗公庫）；轉帳＝A 的錢搬到 B（雙邊，總量守恆）。
//          兩者共用「提案 → 人審 → 核准才動錢」的形狀，但**刻意不共用同一張單**：
//          審批者最需要一眼看清的就是「這筆會不會消耗公庫」，混在一起等於把那個資訊藏起來。
// 數值影響：Create / Close 純檔案操作，對帳面零影響。
//          Approve 才動錢：Debit(from) → Credit(to)，共用 tx_id；credit 失敗回滾 debit。
// 設計取捨：
//   - **debit-then-credit + rollback** 照抄 Cmd_Treasury.Op_Transfer 與請款單 Approve 的既有形狀，
//     不發明第三套轉帳流程。
//   - **status 就地改寫**而非 append 事件：單子是流程物件，會計真相在 ledger（append-only 不變）。
//   - **非 pending 拒絕重複核准**：後台是單人操作、沒有旁人可看，連點兩次的重複必須由機制擋。
//   - **不檢查 from 是否為「合法」帳戶**：歸戶的出款方本來就常是孤兒帳戶
//     （agent 欄被填成 persona 名而生出來的）。擋掉它等於讓那些錢永遠搬不回來。
//     餘額不足由 Debit 自己拒絕，那才是真正該擋的條件。
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
    /// <summary>轉帳單存取層 —— 後台審批與（日後）agent 提案共用，勿另造第二份檔案 IO。</summary>
    public static class UCL_TreasuryTransferRequestStore
    {
        public const string StatusPending = "pending";
        public const string StatusApproved = "approved";
        public const string StatusRejected = "rejected";
        public const string StatusCancelled = "cancelled";

        // 邊界：from == to 直接拒絕 —— 自轉會讓 debit/credit 落在同一帳號，帳面沒變化卻多兩筆紀錄。
        public static TreasuryTransferRequest Create(
            string fromBank, string toBank, int amount, string reason,
            string kind, string requesterAgent, string requesterPersona,
            string currency = "tavern_token")
        {
            if (string.IsNullOrWhiteSpace(fromBank)) throw new ArgumentException("from_bank 必填");
            if (string.IsNullOrWhiteSpace(toBank)) throw new ArgumentException("to_bank 必填");
            if (string.Equals(fromBank.Trim(), toBank.Trim(), StringComparison.Ordinal))
                throw new ArgumentException($"from == to（{fromBank}）自轉無意義");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("reason 必填 —— 審批者要有東西可判");

            var now = DateTime.UtcNow;
            string uuid6 = Guid.NewGuid().ToString("N").Substring(0, 6);
            var req = new TreasuryTransferRequest
            {
                request_id = uuid6,
                requested_at = now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z",
                status = StatusPending,
                from_bank = fromBank.Trim(),
                to_bank = toBank.Trim(),
                amount = amount,
                currency = string.IsNullOrWhiteSpace(currency) ? "tavern_token" : currency.Trim(),
                reason = reason.Trim(),
                kind = string.IsNullOrWhiteSpace(kind) ? "manual_transfer" : kind.Trim(),
                requester_agent = requesterAgent?.Trim(),
                requester_persona = requesterPersona?.Trim(),
            };

            string dir = UCL_TreasuryPaths.GetTransferRequestDateDir(now);
            Directory.CreateDirectory(dir);
            WriteJson(Path.Combine(dir, UCL_TreasuryPaths.BuildTransferRequestFileName(now, uuid6)), req);
            Debug.Log($"[Treasury] 轉帳單建立 {uuid6}: {amount} `{req.from_bank}` → `{req.to_bank}`（{req.kind}）");
            return req;
        }

        public static List<TreasuryTransferRequest> List(bool pendingOnly = false, int maxCount = 200)
        {
            var result = new List<TreasuryTransferRequest>();
            string root = UCL_TreasuryPaths.GetTransferRequestsRoot();
            if (!Directory.Exists(root)) return result;
            foreach (var dateDir in Directory.GetDirectories(root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                foreach (var f in Directory.GetFiles(dateDir, "*__transfer.json").OrderByDescending(x => x, StringComparer.Ordinal))
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

        public static string FindPath(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            string root = UCL_TreasuryPaths.GetTransferRequestsRoot();
            if (!Directory.Exists(root)) return null;
            foreach (var dateDir in Directory.GetDirectories(root))
                foreach (var f in Directory.GetFiles(dateDir, $"*_{requestId}__transfer.json"))
                    return f;
            return null;
        }

        // 區塊職責：核准並實際搬錢。
        // 數值影響：from -amount / to +amount，總量守恆；單子就地更新 status 與兩筆 ledger uuid。
        // 邊界：credit 失敗 → 回滾 debit；連回滾都失敗 → 拋例外並點名懸空 debit 的 uuid，
        //      **絕不吞掉**（錢憑空消失且無聲是這套帳最不能接受的結局）。
        public static TreasuryTransferRequest Approve(string requestId, string decidedBy, string note = null)
        {
            string path = FindPath(requestId);
            if (path == null) throw new InvalidOperationException($"找不到轉帳單 {requestId}");
            var req = TryRead(path);
            if (req == null) throw new InvalidOperationException($"轉帳單 {requestId} 讀取失敗（JSON 壞了？）");
            if (req.status != StatusPending)
                throw new InvalidOperationException($"轉帳單 {requestId} 目前是 {req.status}，只有 pending 可核准（防重複搬錢）");

            string txId = $"transfer_request_{req.request_id}";
            TreasuryLedgerEntry debit;
            try
            {
                debit = UCL_TreasuryLedger.Debit(
                    accountId: req.from_bank, amount: req.amount,
                    useKind: "transfer_request_out", useRef: txId,
                    description: $"轉帳單 {req.request_id} → @{req.to_bank}（{req.kind}）: {req.reason}",
                    // callerAgentId 必須是 "system"：Debit 有帳戶隔離鐵律，caller 非 system
                    // 且 != accountId 就拋例外。核准者身分記在 decided_by，不靠這個欄位承載。
                    callerAgentId: "system", cmdId: txId,
                    // ⚠ resolveAccount: false —— 轉帳單的兩端都要**認字面**。
                    //   歸戶單的出款方本來就是孤兒帳戶（`summit` / `Zeta` / `zeta-bank`…），
                    //   一旦讓 Credit/Debit 的帳號歸一介入，出款方會被導向歸一後的正主帳戶：
                    //   結果是從正主身上扣錢、孤兒的錢原封不動，而**轉帳單顯示核准成功**。
                    //   單子上寫的帳號就是操作的對象，這裡不接受任何再詮釋。
                    resolveAccount: false);
            }
            catch (Exception ex)
            {
                int bal = -1;
                try { bal = UCL_TreasuryLedger.GetBalance(req.from_bank); } catch { }
                throw new InvalidOperationException(
                    $"出款失敗，轉帳單 {req.request_id} 未核准（沒有動任何錢）。" +
                    $"`{req.from_bank}` 餘額 {bal} / 本單需 {req.amount}。原因：{ex.Message}");
            }

            TreasuryLedgerEntry credit;
            try
            {
                credit = UCL_TreasuryLedger.Credit(
                    accountId: req.to_bank, amount: req.amount,
                    sourceKind: "transfer_request_in", sourceRef: txId,
                    description: $"轉帳單 {req.request_id} ← @{req.from_bank}（{req.kind}）: {req.reason}",
                    callerAgentId: "system", cmdId: txId,
                    resolveAccount: false);   // 同出款方：收款帳號由開單者指名，不再詮釋
            }
            catch (Exception ex)
            {
                try
                {
                    // 回滾必須退回**原本被扣的那個帳號**（字面），否則錢會退到別人身上，
                    // 而帳面上兩邊都「成功」—— 這是回滾路徑最不能出的錯。
                    UCL_TreasuryLedger.Credit(req.from_bank, req.amount, "transfer_request_rollback",
                        txId + "|rollback", $"轉帳單 {req.request_id} 入帳失敗回滾: {ex.Message}", "system", txId + "_rollback",
                        idempotencyKey: null, resolveAccount: false);
                }
                catch (Exception rbEx)
                {
                    throw new InvalidOperationException(
                        $"轉帳單 {req.request_id} 入帳失敗且回滾也失敗 —— " +
                        $"`{req.from_bank}` 有一筆懸空 debit（uuid={debit.uuid}, 金額 {req.amount}）需人工處理。" +
                        $"orig={ex.Message} / rollback={rbEx.Message}");
                }
                throw new InvalidOperationException($"轉帳單 {req.request_id} 入帳失敗（已回滾，帳目平）: {ex.Message}");
            }

            req.status = StatusApproved;
            req.decided_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            req.decided_by = string.IsNullOrEmpty(decidedBy) ? "Tim" : decidedBy;
            req.decision_note = note;
            req.debit_entry_uuid = debit?.uuid;
            req.credit_entry_uuid = credit?.uuid;
            WriteJson(path, req);
            Debug.Log($"[Treasury] 轉帳單核准 {requestId}: {req.amount} `{req.from_bank}` → `{req.to_bank}`");
            return req;
        }

        /// <summary>駁回 / 取消 —— 不動任何錢，只改 status 與理由。</summary>
        public static TreasuryTransferRequest Close(string requestId, string newStatus, string decidedBy, string note = null)
        {
            if (newStatus != StatusRejected && newStatus != StatusCancelled)
                throw new ArgumentException($"newStatus 只接受 {StatusRejected} / {StatusCancelled}");
            string path = FindPath(requestId);
            if (path == null) throw new InvalidOperationException($"找不到轉帳單 {requestId}");
            var req = TryRead(path);
            if (req == null) throw new InvalidOperationException($"轉帳單 {requestId} 讀取失敗");
            if (req.status != StatusPending)
                throw new InvalidOperationException($"轉帳單 {requestId} 目前是 {req.status}，只有 pending 可變更");
            req.status = newStatus;
            req.decided_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
            req.decided_by = decidedBy;
            req.decision_note = note;
            WriteJson(path, req);
            Debug.Log($"[Treasury] 轉帳單 {requestId} → {newStatus}（by {decidedBy}）");
            return req;
        }

        static TreasuryTransferRequest TryRead(string path)
        {
            try
            {
                var json = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (json == null || !json.IsObject) return null;
                var req = new TreasuryTransferRequest();
                JsonConvert.LoadFieldFromJsonUnityVer(req, json);
                return string.IsNullOrEmpty(req.request_id) ? null : req;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] 轉帳單讀取失敗（跳過）{path}: {ex.Message}");
                return null;
            }
        }

        static void WriteJson(string path, TreasuryTransferRequest req)
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
