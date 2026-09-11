// 區塊職責：繪圖券 (Canvas voucher) 的 C# 端 canonical ledger — grant / 三種餘額查詢 / consume 單一 owner。
// 物理意義：Tim 2026-07-22 拍板 — 券發放流程收攏到 C# static class(對齊 UCL_TreasuryLedger)，根治
//          「C# spawn python canvas.py → canvas.py 的 cwd 相對 DEFAULT_CANVAS_ROOT 解析到錯的
//           AgentCommands(CardGame/AgentCommands stray) → 寫進讀不到的地方」那一整類跨 process 路徑 split bug。
//          C# 一律用 UCL_AgentCommandsPath.DataRoot 正規解析，與讀取端同源、零 cwd 依賴。python 端改透過
//          Cmd_CanvasVoucher 走 run_cmd 操作同一 owner(單寫者)，不再各自直寫檔造成 drift。
//
// ── 2026-08-18 期間限定券（Tim 拍板方案乙）────────────────────────────────
// 餘額從純量 `balance` 改成**批次制**：一次 grant ＝ 一個 batch，各自帶 `expires_at`。
//   ① **`balance` 欄不再寫入**（方案乙）—— 它是推導值。留一個「看起來是餘額、實際是舊快照」
//      的欄位在檔裡，就是把過期額度變成可讀到的數字，而那不會報錯（見 glossary `舊快照假綠`）。
//   ② **三種餘額問題分成三個 API**，`GetBalance()` 已移除：
//      永久券 / 未過期的限時券 / 可花總額 —— 三個答案不同，而一個名字回答不了三個問題。
//      🩸 一個 `GetBalance` 同時被「查永久券數量」與「規劃付款」呼叫 ＝ glossary `一符二役`：
//         改對一邊等於默默弄錯另一邊，而**被弄錯的那邊不會喊**。
//   ③ **消費先花快過期的**（限時券按 expires_at 升冪 → 永久券）—— 否則限時券會在
//      永久券的陰影下爛掉，而使用者完全看不出來自己損失了什麼。
//   ④ 過期批次在下一次寫入時清掉並**在 history 記一筆 `expire`** ——
//      作廢必須留痕，否則「我的券去哪了」永遠沒有答案。
//
// 數值影響：讀寫 <DataRoot>/Canvas/vouchers/<persona>.json；原子 tmp+replace。
//   schema v2：{persona, batches:[{uuid,amount,remain,granted_at,expires_at,source,ref}], history:[…]}
//   **legacy 相容**：舊檔只有 `balance` 而沒有 `batches` ⇒ 讀成「一批永久券」（remain = balance）。
//   不需要遷移腳本 —— 第一次寫入自然轉成 batches，而在那之前讀取結果與舊制逐值相同。
// 設計取捨：去 #if UNITY_EDITOR guard(純 file IO，無 Editor 依賴，對齊 UCL_TreasuryLedger 2026-05-13 決定)；
//          券綁 persona(Tim 拍板「券綁 persona / token 綁 bank」)。
// ⚠ 對側契約：python 讀取端是 `canvas.py` 的 `voucher_*` 系列 —— **改 schema 要同時改那邊**，
//   否則它算出 0 而不報錯（「有券算成沒券」與「真的沒券」在輸出上一模一樣）。
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.Voucher;   // 共用底層 UCL_VoucherLedgerCommon（時戳/uuid/原子讀改寫）
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.CanvasVoucher
{
    /// <summary>
    /// 繪圖券 canonical ledger — 唯一寫入 owner。發券 / 三種餘額查詢 / 用券皆走此，C# 端與 python(經 Cmd) 共用。
    /// </summary>
    public static class UCL_CanvasVoucherLedger
    {
        // 正規路徑：一律以 DataRoot 錨定(= C# 讀取端同源)，不吃任何 cwd 相對解析
        static string VouchersDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "vouchers");
        static string PathFor(string persona) => Path.Combine(VouchersDir, persona + ".json");

        // ===========================================================
        // 區塊：讀取 —— 三個問題、三個 API（刻意沒有「GetBalance」）
        // ===========================================================

        /// <summary>**永久券**餘額（`expires_at` 空的那些）。查「這個人存了多少券」用這支。</summary>
        public static int GetPermanent(string persona)
        {
            int aSum = 0;
            foreach (var b in LoadBatches(persona)) if (b.IsPermanent && b.remain > 0) aSum += b.remain;
            return aSum;
        }

        /// <summary>**未過期的限時券**餘額。查「本場還剩幾顆免費像素」用這支。</summary>
        public static int GetExpiring(string persona)
        {
            DateTime aNow = DateTime.UtcNow;
            int aSum = 0;
            foreach (var b in LoadBatches(persona))
                if (!b.IsPermanent && b.IsSpendableAt(aNow)) aSum += b.remain;
            return aSum;
        }

        // ===========================================================
        // 區塊職責：某一批（按 ref）還剩多少 —— 給「本場自由時間的免費像素還剩幾顆」用。
        // 物理意義：`GetExpiring` 回的是**所有**未過期限時券，而自由時間要回報的是
        //          「**本場**發的那 10 顆用了幾顆」。兩者在同時持有多批限時券時不同 ——
        //          用前者去報「本場已用」會算出別場的量，而那不會報錯。
        // 數值影響：純讀。ref 空 ⇒ 回 0（不要回總額 —— 那是拿「沒指定」冒充「全部」）。
        // ===========================================================
        public static int GetExpiringByRef(string persona, string refText)
        {
            if (string.IsNullOrEmpty(refText)) return 0;
            DateTime aNow = DateTime.UtcNow;
            int aSum = 0;
            foreach (var b in LoadBatches(persona))
                if (!b.IsPermanent && b.IsSpendableAt(aNow) && b.@ref == refText) aSum += b.remain;
            return aSum;
        }

        // ===========================================================
        // 區塊職責：某一批（按 ref）的**用量三值** —— granted / remain / used，⛔ **忽略是否過期**。
        // 物理意義：收工回報要回答的是「本場用了幾張」，而那個問題**必然在批次過期之後**才被問 ——
        //          到點收工的定義是 `now > until`，而券的到期是 `until + grace`。
        //          `GetExpiringByRef` 只算**未過期**的 ⇒ 它在那一刻回 0，
        //          🩸 而呼叫端用「發放量 − 0」推出「全數用畢」＝ TASK-0195：**查無被算成用完**。
        //          兩者在輸出上一模一樣，所以沒有任何一層會喊。
        // 數值影響：純讀。回 false ＝ 帳本上找不到這個 ref 的批次（已被清理／ref 不符／別棵 DataRoot）。
        //          ⛔ 呼叫端不准把 false 當成 0 —— 那就是同一隻病換一個地方發作。
        // ===========================================================
        public static bool TryGetUsageByRef(string persona, string refText,
                                            out int granted, out int remain, out int used)
        {
            granted = 0; remain = 0; used = 0;
            if (string.IsNullOrEmpty(refText)) return false;
            bool aFound = false;
            foreach (var b in LoadBatches(persona))
            {
                if (b.IsPermanent || b.@ref != refText) continue;
                aFound = true;
                granted += b.amount;
                remain += b.remain;
            }
            used = Math.Max(0, granted - remain);
            return aFound;
        }

        // ===========================================================
        // 區塊職責：某一批（按 ref）的用量三值 —— **從 history 結算**，給批次已被清掉的場次用。
        // 物理意義：`TryGetUsageByRef` 讀的是 `batches`，而批次在**花完**或**過期後的下一次寫入**
        //          就被清掉 ⇒ 它回 false。而收工要回答的正是「本場用了幾張」。
        //          🩸 TASK-0198：0195 的修法把「查無被算成用完」換成了「無法判定」，
        //          而在「全部用完」那個 case，被換掉的那條錯公式**剛好會印對**（10 − 0 = 10）
        //          ⇒ 一個誠實但沒用的答案取代了一個答得出來的問題。**誠實不是目標，
        //          它只是不准用猜的去補。**
        // 數值影響：純讀。granted ← `grant` 列；forfeited ← `expire` 列；
        //          used = granted − forfeited（`exhaust` 列 ⇒ forfeited=0，全用完）。
        //          回 false 的條件很嚴：**沒有 grant 列，或沒有任何一筆清理列**
        //          （expire / exhaust）—— 後者代表那一批在寫入端留痕之前就被清掉了
        //          （2026-09-11 之前的所有批次），⛔ 那時仍然只能答「查無」，不准推導。
        // ===========================================================
        public static bool TryGetUsageFromHistoryByRef(string persona, string refText,
                                                       out int granted, out int forfeited, out int used)
        {
            granted = 0; forfeited = 0; used = 0;
            if (string.IsNullOrEmpty(refText)) return false;

            bool aHasGrant = false, aHasSettle = false;
            foreach (var e in LoadHistory(persona))
            {
                if (!e.Contains("ref") || e.GetString("ref", "") != refText) continue;
                string aType = e.GetString("type", "");
                int aAmount = e.GetInt("amount", 0);
                if (aType == "grant") { aHasGrant = true; granted += aAmount; }
                else if (aType == "expire") { aHasSettle = true; forfeited += aAmount; }
                else if (aType == "exhaust") { aHasSettle = true; }
            }
            // ⛔ 回 false 時把三個值歸零 —— 否則 `granted` 會帶著一個真實的發放量離開，
            //    而呼叫端只要做一次「granted − 0」就回到 TASK-0195 那隻病（查無被算成用完）。
            //    🩸 2026-09-11 實測：`op=usage` 第一版就印出 `found=0 granted=10`，
            //    我自己造的出口把那個減法重新遞了出去。⇒ 讓它在物理上拿不到那個數字。
            if (!aHasGrant || !aHasSettle) { granted = 0; forfeited = 0; used = 0; return false; }
            used = Math.Max(0, granted - forfeited);
            return true;
        }

        // history 是 append-only 且**無上界**（2026-09-11 實測：無任何裁剪碼，最長的帳 461 筆全在），
        // 所以這條回退不會因為舊列被丟掉而失效。壞檔比照 LoadBatches：回空並出聲。
        static List<JsonData> LoadHistory(string persona)
        {
            var aOut = new List<JsonData>();
            if (string.IsNullOrEmpty(persona)) return aOut;
            try
            {
                string p = PathFor(persona);
                if (!File.Exists(p)) return aOut;
                var d = JsonData.ParseJson(File.ReadAllText(p));
                if (d == null || !d.Contains("history") || !d["history"].IsArray) return aOut;
                var aArr = d["history"];
                for (int i = 0; i < aArr.Count; i++) aOut.Add(aArr[i]);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanvasVoucher] {persona} 券檔 history 讀取失敗（視為空，未吞錯）: {e.Message}");
            }
            return aOut;
        }

        /// <summary>**可花總額**（未過期的限時 ＋ 永久）。規劃付款用這支。</summary>
        public static int GetSpendable(string persona)
        {
            DateTime aNow = DateTime.UtcNow;
            int aSum = 0;
            foreach (var b in LoadBatches(persona)) if (b.IsSpendableAt(aNow)) aSum += b.remain;
            return aSum;
        }

        // ===========================================================
        // 區塊：寫入
        // ===========================================================

        /// <summary>
        /// 發券：新增一批。`iExpiresAtIso` 空 ＝ **永久券**；非空 ＝ 限時券（到期後不能花、下次寫入時清掉並記 history）。
        /// 回 (可花總額 before, after)。amount 必 &gt;0。
        /// </summary>
        public static (int before, int after) Grant(string persona, int amount, string source, string refText,
                                                    string iExpiresAtIso = null)
        {
            if (string.IsNullOrEmpty(persona)) throw new ArgumentException("persona 不可為空");
            if (amount <= 0) throw new ArgumentException($"amount 需為正整數: {amount}");

            int aBefore = 0, aAfter = 0;
            string aUuid = UCL_VoucherLedgerCommon.ShortUuid();
            string aNowIso = UCL_VoucherLedgerCommon.IsoNow();
            MutateLedger(persona, (ioBatches, ioHistory) =>
            {
                aBefore = SumSpendable(ioBatches);
                ioBatches.Add(new UCL_CanvasVoucherBatch
                {
                    uuid = aUuid,
                    amount = amount,
                    remain = amount,
                    granted_at = aNowIso,
                    expires_at = (iExpiresAtIso ?? "").Trim(),
                    source = source ?? "",
                    @ref = refText ?? "",
                });
                aAfter = SumSpendable(ioBatches);
                AppendHistory(ioHistory, "grant", amount, source, refText, aUuid,
                              (iExpiresAtIso ?? "").Trim());
            });
            Debug.Log($"[CanvasVoucher] grant {amount} → {persona}"
                    + (string.IsNullOrEmpty(iExpiresAtIso) ? "（永久）" : $"（到 {iExpiresAtIso}）")
                    + $" (可花: {aBefore} → {aAfter})");
            return (aBefore, aAfter);
        }

        // ===========================================================
        // 區塊職責：用券 —— **先花快過期的**。
        // 物理意義：限時券若排在永久券後面，它會在永久券的陰影下爛掉 ——
        //          而使用者看到的只是「我的券變少了」，看不出「我損失了本來能用的那幾張」。
        //          ⇒ 排序：限時券按 expires_at 升冪（最快過期的先），永久券最後。
        // 數值影響：餘額不足直接 throw（不部分扣款 —— 半扣的帳沒有人能對）。
        // ===========================================================
        public static (int before, int after) Consume(string persona, int amount, string source, string refText)
        {
            if (string.IsNullOrEmpty(persona)) throw new ArgumentException("persona 不可為空");
            if (amount <= 0) throw new ArgumentException($"amount 需為正整數: {amount}");

            int aBefore = 0, aAfter = 0;
            MutateLedger(persona, (ioBatches, ioHistory) =>
            {
                aBefore = SumSpendable(ioBatches);
                if (aBefore < amount)
                    throw new InvalidOperationException(
                        $"繪畫券不足: persona={persona} 可花={aBefore} < 欲用={amount}"
                        + $"（其中永久 {SumPermanent(ioBatches)}、未過期限時 {aBefore - SumPermanent(ioBatches)}）");

                DateTime aNow = DateTime.UtcNow;
                var aOrder = new List<UCL_CanvasVoucherBatch>(ioBatches);
                aOrder.RemoveAll(b => !b.IsSpendableAt(aNow));
                aOrder.Sort(CompareByExpirySoonestFirst);

                int aLeft = amount;
                foreach (var b in aOrder)
                {
                    if (aLeft <= 0) break;
                    int aTake = Math.Min(aLeft, b.remain);
                    b.remain -= aTake;
                    aLeft -= aTake;
                }
                aAfter = SumSpendable(ioBatches);
                AppendHistory(ioHistory, "consume", amount, source, refText, "", "");
            });
            Debug.Log($"[CanvasVoucher] consume {amount} ← {persona} (可花: {aBefore} → {aAfter})");
            return (aBefore, aAfter);
        }

        /// <summary>限時券排前面（expires_at 升冪），永久券最後。</summary>
        static int CompareByExpirySoonestFirst(UCL_CanvasVoucherBatch a, UCL_CanvasVoucherBatch b)
        {
            if (a.IsPermanent != b.IsPermanent) return a.IsPermanent ? 1 : -1;   // 永久的往後
            if (a.IsPermanent) return string.CompareOrdinal(a.granted_at, b.granted_at);   // 都永久 → 先發的先花
            return string.CompareOrdinal(a.expires_at, b.expires_at);            // ISO 字串序即時序
        }

        // ===========================================================
        // 區塊職責：讀檔 → batches（含 legacy 相容）。
        // 物理意義：舊檔只有純量 `balance`、沒有 `batches` ⇒ 讀成「一批永久券」。
        //          ⇒ **不需要遷移腳本**：在第一次寫入之前，讀取結果與舊制逐值相同；
        //            第一次 grant/consume 自然把它落成 batches。
        // 數值影響：純讀；壞檔回空清單並印 warning（不丟例外 —— 一份壞檔不該讓放點整批失敗，
        //          但也不靜默，否則「讀失敗」與「真的沒券」同形）。
        // ===========================================================
        static List<UCL_CanvasVoucherBatch> LoadBatches(string persona)
        {
            var aOut = new List<UCL_CanvasVoucherBatch>();
            if (string.IsNullOrEmpty(persona)) return aOut;
            try
            {
                string p = PathFor(persona);
                if (!File.Exists(p)) return aOut;
                var d = JsonData.ParseJson(File.ReadAllText(p));
                if (d == null) return aOut;
                ReadBatchesInto(d, aOut);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanvasVoucher] {persona} 券檔讀取失敗（視為 0，未吞錯）: {e.Message}");
            }
            return aOut;
        }

        static void ReadBatchesInto(JsonData d, List<UCL_CanvasVoucherBatch> ioOut)
        {
            if (d.Contains("batches") && d["batches"].IsArray)
            {
                var aArr = d["batches"];
                for (int i = 0; i < aArr.Count; i++)
                {
                    var b = new UCL_CanvasVoucherBatch();
                    b.DeserializeFromJson(aArr[i]);
                    ioOut.Add(b);
                }
                return;
            }
            // legacy：純量 balance ⇒ 一批永久券（granted_at 留空表示「來自舊制，時點不可考」）
            int aLegacy = d.Contains("balance") ? d.GetInt("balance", 0) : 0;
            if (aLegacy > 0)
            {
                ioOut.Add(new UCL_CanvasVoucherBatch
                {
                    uuid = "legacy",
                    amount = aLegacy,
                    remain = aLegacy,
                    granted_at = "",
                    expires_at = "",          // 舊制沒有到期概念 ⇒ 永久
                    source = "legacy_balance",
                    @ref = "schema v1 純量 balance 轉入",
                });
            }
        }

        // ===========================================================
        // 區塊職責：讀改寫的骨架 —— 過期清理也在這裡（唯一會動 batches 的通道）。
        // 物理意義：過期批次每次寫入時清掉，並**在 history 記一筆 `expire`** ——
        //          作廢留痕是「我的券去哪了」唯一的答案來源。
        //          花完的批次（remain=0）也一併清掉，並記一筆 `exhaust`。
        // ⚠ **不寫 `balance` 欄**（方案乙）。留著它就是留一個看起來是餘額、實際是舊快照的數字。
        //
        // ── 2026-09-11 TASK-0198：清理留痕改成**逐批可歸戶** ─────────────────
        // 🩸 舊寫法有兩格讓「這一批用了幾張」在批次被清掉之後**永久不可回答**：
        //   ① `expire` 只記**加總**（多批同時過期併成一筆）；
        //   ② 那一筆的 `ref` 欄填的是人看的字串「到期作廢」，而 `ref` 在別處一律是**批次的 ref**
        //      ⇒ glossary `借位`：欄位合法、值合法，而它回答的是另一個問題。
        //   ③ 花完的批次**一個字都不留** ⇒ 它跟「這批從來不存在」同形。
        // ⇒ 於是 TASK-0198 開單時我自己寫的補法（「history 的 grant − expire 差額」）
        //   **在舊資料上算不出來** —— 那是一個沒有去讀 schema 就寫下的處方。
        // 修法在**寫入端**：每一批各記一筆，`ref` 放批次自己的 ref、`batch` 放 uuid，
        //   人看的字串移到 `source`。⛔ 舊資料回不去（在此之前的批次仍然只能答「查無」）。
        // ===========================================================
        static void MutateLedger(string persona,
            Action<List<UCL_CanvasVoucherBatch>, JsonData> iMutate)
        {
            string p = PathFor(persona);
            UCL_VoucherLedgerCommon.MutateFile(p, () => JsonData.ParseJson("{}"), d =>
            {
                var aBatches = new List<UCL_CanvasVoucherBatch>();
                ReadBatchesInto(d, aBatches);
                if (!d.Contains("history") || !d["history"].IsArray) d["history"] = JsonData.ParseJson("[]");

                iMutate(aBatches, d["history"]);

                // 過期清理（在 mutate 之後 —— 這一筆消費本來就該能花掉「此刻仍未過期」的券）
                DateTime aNow = DateTime.UtcNow;
                int aForfeited = 0;
                var aKeep = new List<UCL_CanvasVoucherBatch>();
                foreach (var b in aBatches)
                {
                    if (b.remain <= 0)
                    {
                        // 花完了 ⇒ 記一筆 `exhaust`：⛔ 不能只靠「grant 有、batches 沒有」去推用量，
                        // 那是從**缺席**推結論（缺席另一個成因是檔被重建／別棵 DataRoot）。
                        AppendHistory(d["history"], "exhaust", b.amount, "spent", b.@ref, b.uuid, b.expires_at);
                        continue;
                    }
                    if (b.IsExpiredAt(aNow))
                    {
                        aForfeited += b.remain;
                        // 逐批一筆（⛔ 不加總）—— `ref` 是批次的 ref，人看的字串放 `source`
                        AppendHistory(d["history"], "expire", b.remain, "expired（到期作廢）",
                                      b.@ref, b.uuid, b.expires_at);
                        continue;
                    }
                    aKeep.Add(b);
                }
                if (aForfeited > 0)
                {
                    Debug.Log($"[CanvasVoucher] {persona} 限時券到期作廢 {aForfeited} 張（已逐批記 history）");
                }

                d["persona"] = persona;
                var aArr = JsonData.ParseJson("[]");
                foreach (var b in aKeep) aArr.Add(b.SerializeToJson());
                d["batches"] = aArr;
                // ⚠ 刻意**不寫** d["balance"]：餘額是推導值（方案乙）。
                //   舊檔殘留的 balance 鍵在這裡被覆蓋掉的機會只有一次 —— 顯式移除。
                if (d.Contains("balance")) d.Remove("balance");
            });
        }

        static void AppendHistory(JsonData ioHistory, string type, int amount,
                                  string source, string refText, string batchUuid, string expiresAt)
        {
            var e = JsonData.ParseJson("{}");
            e["ts"] = UCL_VoucherLedgerCommon.IsoNow();
            e["uuid"] = UCL_VoucherLedgerCommon.ShortUuid();
            e["type"] = type;
            e["amount"] = amount;
            e["source"] = source ?? "";
            e["ref"] = refText ?? "";
            if (!string.IsNullOrEmpty(batchUuid)) e["batch"] = batchUuid;
            if (!string.IsNullOrEmpty(expiresAt)) e["expires_at"] = expiresAt;
            ioHistory.Add(e);
        }

        static int SumSpendable(List<UCL_CanvasVoucherBatch> iBatches)
        {
            DateTime aNow = DateTime.UtcNow;
            int aSum = 0;
            foreach (var b in iBatches) if (b.IsSpendableAt(aNow)) aSum += b.remain;
            return aSum;
        }

        static int SumPermanent(List<UCL_CanvasVoucherBatch> iBatches)
        {
            int aSum = 0;
            foreach (var b in iBatches) if (b.IsPermanent && b.remain > 0) aSum += b.remain;
            return aSum;
        }
    }
}
