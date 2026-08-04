// 區塊職責：酒館 wait 的常駐推進器 —— [InitializeOnLoad] + EditorApplication.update tick。
// 物理意義：取代 Cmd_Tavern 內那個「handler 返回後就死掉」的 fire-and-forget UniTask。
//
//   ⚠ 為什麼非換不可（2026-08-04 實測，Tim 拍板走 tick service）：
//   舊版把背景迴圈的 CancellationToken 綁在**發起它的那個 cmd** 上，而 runner 是
//   `using (var cts = CreateLinkedTokenSource(token))` —— handler 一返回，cts 就 dispose。
//   op=wait 本來就設計成立刻返回，所以背景迴圈的第一個 `await UniTask.Delay(.., token)`
//   必然拋 OperationCanceledException，又被 `catch (OperationCanceledException) {}` 靜默吞掉，
//   條目永遠停在 pending。
//   **證據**：歷史 71 筆 wait，71 筆 since_seq=0（第一圈就命中、不需要等）、
//   71 筆 ≤3 秒結束，零筆 timeout、零筆 cancelled。這個 wait 從來沒有真的等過任何一次，
//   而 71 筆漂亮的 "fulfilled" 讓它看起來一直在正常運作。
//
// 數值影響：每 TickIntervalSeconds 掃一次 _active_waits.json 的 pending 條目；
//          無 pending 時是一次檔案讀取就返回（Editor 主迴圈成本可忽略）。
// 設計取捨：
//   - **狀態全在磁碟，服務本身無記憶**：每 tick 重讀 _active_waits.json。
//     這正是 tick service 勝過 UniTask 的地方 —— domain reload 不會弄丟任何進行中的 wait，
//     因為根本沒有「進行中的記憶體物件」可弄丟。重編譯後照樣接著推進。
//   - **推進與判定分離**：命中與否一律問 UCL_ChatTavernIO.WaitMatches，
//     不在這裡複製第二套判斷（同一語意兩處實作必漂，本 repo 已有血證）。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/ChatTavern_Workflow.md
// @doc-sync: Assets/Plugins/UCL_Core/Skills~/ucl-chat-tavern/reference/re-entry.md
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>把 pending 的 wait 條目往前推進的常駐服務（取代 fire-and-forget UniTask）。</summary>
    [InitializeOnLoad]
    public static class UCL_TavernWaitService
    {
        /// <summary>掃描間隔（後台可調）；wait 的解析度是秒級，不需要每幀。</summary>
        static double TickIntervalSeconds
        { get { UCL_TavernWaitSettings.EnsureLoaded(); return UCL_TavernWaitSettings.TickIntervalSeconds; } }

        static double s_LastTick;
        /// <summary>本 Editor session 內已處理過的孤兒收尾（只做一次，不必每 tick 掃）。</summary>
        static bool s_OrphansFinalized;

        static UCL_TavernWaitService()
        {
            try { EditorApplication.update += Tick; }
            catch (Exception e) { Debug.LogWarning($"[TavernWait] service init fail: {e.Message}"); }
        }

        /// <summary>手動推一次（測試 / Cmd 觸發用）。</summary>
        public static void ForceTick()
        {
            try { TickInternal(); }
            catch (Exception e) { Debug.LogWarning($"[TavernWait] ForceTick fail: {e.Message}"); }
        }

        static void Tick()
        {
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastTick < TickIntervalSeconds) return;
                s_LastTick = now;
                TickInternal();
            }
            catch (Exception e)
            {
                // 任何例外都不擋 Editor 主迴圈；下個 tick 照跑
                Debug.LogWarning($"[TavernWait] tick exception（下個 tick 仍會跑）: {e.Message}");
            }
        }

        static void TickInternal()
        {
            // 開機先收孤兒 —— 舊版留下的 pending 條目沒有任何人會處理
            // （Cmd_Tavern 的註解宣稱「下次 FinalizeOrphanedPending 會處理」，但那個函式
            //   在 2026-08-04 之前**零個呼叫端**。宣稱有救援機制而實際沒有，比明說沒有更糟。）
            if (!s_OrphansFinalized)
            {
                s_OrphansFinalized = true;
                try
                {
                    int n = UCL_ChatTavernIO.FinalizeOrphanedPending();
                    if (n > 0) Debug.Log($"[TavernWait] 收尾 {n} 筆孤兒 wait（前次 Editor session 遺留）");
                }
                catch (Exception e) { Debug.LogWarning($"[TavernWait] 孤兒收尾失敗: {e.Message}"); }
            }

            var list = UCL_ChatTavernIO.LoadActiveWaits();
            if (list?.waits == null || list.waits.Count == 0) return;

            // 先挑出 pending —— 絕大多數 tick 會在這裡就返回
            List<UCL_ChatActiveWait> pending = null;
            foreach (var w in list.waits)
            {
                if (w == null || !string.Equals(w.status, "pending", StringComparison.OrdinalIgnoreCase)) continue;
                (pending ??= new List<UCL_ChatActiveWait>()).Add(w);
            }
            if (pending == null) return;

            // 中止旗標：Editor 的酒館頁按「中止握手」時 touch。只對**旗標寫入前就已存在**的 wait 生效，
            // 之後才發起的不受影響（否則按一次會把往後所有 wait 都殺掉）。
            string tavernDir = UCL_ChatTavernIO.GetTavernDir();
            DateTime cancelAt = FlagTimeUtc(Path.Combine(tavernDir, "_handshake_cancel.flag"));
            DateTime hurryAt = FlagTimeUtc(Path.Combine(tavernDir, "_handshake_hurry.flag"));
            bool consumedCancel = false, consumedHurry = false;

            foreach (var w in pending)
            {
                DateTime started = ParseUtc(w.started_at);
                double elapsed = started == DateTime.MinValue ? 0 : (DateTime.UtcNow - started).TotalSeconds;

                if (cancelAt != DateTime.MinValue && started != DateTime.MinValue && cancelAt > started)
                {
                    Finish(w.wait_id, "cancelled",
                        $"# 🛑 Wait Cancelled\n\n- wait_id: `{w.wait_id}`\n- 使用者從酒館頁中止握手。\n", 0, 0);
                    consumedCancel = true;
                    continue;
                }

                // 催促酒保：把「已經等多久」往前挪 30 秒，讓插話觸發條件提早滿足
                if (hurryAt != DateTime.MinValue && started != DateTime.MinValue && hurryAt > started)
                {
                    elapsed += 30;
                    consumedHurry = true;
                }

                // ── 命中判定：只有「算數的回覆」才 fulfilled（見 UCL_ChatTavernIO.WaitMatches）──
                var hits = new List<UCL_ChatMessage>();
                try
                {
                    foreach (var m in UCL_ChatTavernIO.Since(w.room_id, w.since_seq, 0))
                        if (UCL_ChatTavernIO.WaitMatches(w, m)) hits.Add(m);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TavernWait] 掃 {w.room_id} 訊息失敗（{w.wait_id}）: {e.Message}");
                    continue;   // 讀不到就下個 tick 再試，不誤判成 timeout
                }

                if (hits.Count > 0)
                {
                    var room = UCL_ChatTavernIO.GetRoom(w.room_id);
                    string title = $"🔔 {room?.name ?? w.room_id} — wait fulfilled (id={w.wait_id}) — {hits.Count} 筆新訊息";
                    Finish(w.wait_id, "fulfilled", UCL_ChatTavernRender.RenderMessages(title, hits),
                           hits[0].seq, hits.Count);
                    UCL_TavernWaitNpc.ResetDrinks(w.room_id, w.owner ?? "");
                    Debug.Log($"[TavernWait] {w.wait_id} → fulfilled after {elapsed:F0}s（{hits.Count} 則）");
                    continue;
                }

                // ── 逾時 ──
                if (w.timeout_sec > 0 && elapsed >= w.timeout_sec)
                {
                    Finish(w.wait_id, "timeout",
                        $"# ⏱ Wait Timeout\n\n- wait_id: `{w.wait_id}`\n- room: `{w.room_id}`\n"
                        + $"- 等待 {w.timeout_sec}s 後仍無符合條件的回覆"
                        + (string.IsNullOrEmpty(w.expect_from) ? "" : $"（只認 @{w.expect_from}）") + "。\n", 0, 0);
                    Debug.Log($"[TavernWait] {w.wait_id} → timeout after {elapsed:F0}s");
                    continue;
                }

                // ── 酒保插話：不結束 wait，只累加杯數讓等待方看得到 ──
                int trigger = w.npc_after_sec > 0 ? w.npc_after_sec : UCL_TavernWaitNpc.TriggerSeconds;
                string target = string.IsNullOrEmpty(w.expect_from) ? w.owner : w.expect_from;
                if (UCL_TavernWaitNpc.TryInterject(w.room_id, w.owner ?? "", target, elapsed, trigger))
                {
                    int cups = UCL_ChatTavernIO.BumpNpcCups(w.wait_id);
                    Debug.Log($"[TavernWait] {w.wait_id} — 酒保第 {cups} 杯（wait 繼續）");
                }
            }

            // 旗標用完才刪 —— 在迴圈外刪，確保同一次按鈕對所有當時在等的 wait 都生效
            if (consumedCancel) TryDelete(Path.Combine(tavernDir, "_handshake_cancel.flag"));
            if (consumedHurry) TryDelete(Path.Combine(tavernDir, "_handshake_hurry.flag"));
        }

        static void Finish(string waitId, string status, string md, int firstSeq, int count)
        {
            try
            {
                UCL_ChatTavernIO.EnsureTavernDir();
                File.WriteAllText(UCL_ChatTavernIO.GetWaitResultPath(waitId), md, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning($"[TavernWait] 寫結果檔失敗（{waitId}）: {e.Message}"); }
            // 狀態一定要更新 —— 結果檔寫失敗也不能讓條目卡在 pending（那就是舊版的病）
            UCL_ChatTavernIO.UpdateWaitStatus(waitId, status, firstSeq, count);
        }

        static DateTime FlagTimeUtc(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static DateTime ParseUtc(string s) =>
            !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : DateTime.MinValue;
    }
}
#endif
