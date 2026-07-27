// 區塊職責：訊息寫入服務 — 把 AppendMessage 內「寫檔 + 算 derived seq + 寫 _seq.txt」的臨界區
//          抽離成獨立 Service，並用 per-room lock 包起來。
// 物理意義：目前 Unity Editor 是單一主執行緒 + 全同步呼叫鏈（WriteMessageFile / CountMessageFiles 都不
//          await 任何東西），理論上同一個 process 內不會有真正的資料競爭。但 Tim 2026-07-27 拍板要求
//          「把寫入邏輯抽出來、上鎖，確保就算之後真的有多執行緒 / 重入情境跑進來也不會錯」——這是防禦性
//          設計 (belt-and-suspenders)，不是在解決目前已知存在的 race（目前沒有）。
// 設計取捨：
//   - per-room lock（Dictionary<string, object> + lazy-create）而非單一 global lock —
//     不同房間的寫入不會互相排隊等待，只有「同一房間」的兩次寫入才會序列化。
//   - lock 範圍只包「寫檔 → derive seq → 寫 _seq.txt」這段臨界區；Discord mirror 的 fire-and-forget
//     spawn 留在 lock 外（呼叫端 AppendMessage 處理）——它不動 message 檔案本體，不需要序列化保護，
//     也不該讓訊息寫入被它拖慢。
//   - seq 唯一性保證：把「WriteMessageFile 寫檔」跟「CountMessageFiles 算這是第幾筆」包在同一個臨界區內，
//     保證同一 process、同一房間內，不會有兩筆訊息因為交錯執行而算出同一個 seq。
//     Tim 2026-07-27 拍板：seq 只要求「不重複」，不要求嚴格對應「送出順序」（T26 pacing delay 等機制
//     本來就會讓送出順序跟實際寫入順序不同，這點可接受）——本 Service 解的是「唯一性」，不是「順序」。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 訊息寫入服務 — AppendMessage 的實際寫入臨界區（寫檔 + derive seq + 寫 _seq.txt）。
    /// per-room lock，防禦性保護寫入路徑：目前單執行緒下没有已知 race，這裡是為了未來就算重入 /
    /// 多執行緒也不會壞掉而預先上的鎖，不影響現行效能（lock 本身在無競爭時開銷可忽略）。
    /// </summary>
    public static class UCL_ChatTavernWriteService
    {
        // per-room lock object 池 — lazy-create，room 之間互不阻塞。
        static readonly Dictionary<string, object> s_RoomLocks = new Dictionary<string, object>();
        // 保護 s_RoomLocks 這個 Dictionary 本身的讀寫（不同房間第一次拿 lock 物件時可能同時發生）。
        static readonly object s_RoomLocksTableLock = new object();

        // ===========================================================
        // 區塊：訊息計數快取 (2026-07-27, Tim 拍板整合進本 Service)
        // 物理意義：CountMessageFiles 每次都要 Directory.GetFiles 列舉整個房間的檔案路徑，就算不讀檔
        //          內容，隨房間訊息量成長 (13000+ 筆) 這個列舉本身也不是免費的 (尤其防毒軟體對目錄列舉
        //          也會攔截掃描)。既然「寫入」已經被 per-room lock 序列化，可以在 process 生命週期內
        //          快取「目前訊息數」：只在該房間第一次被寫入時算一次 (CountMessageFiles)，
        //          之後每次寫入直接 +1，不必再列目錄 — 把單次寫入的 seq derive 成本從
        //          O(列目錄) 降到 O(1)（第一次除外）。
        // 數值影響：s_RoomMessageCounts[roomId] = 「目前已知的訊息檔數」，跟磁碟實際檔數同步 (只要
        //          全部寫入都走本 Service)。
        // 邊界 / 已知風險 (刻意記錄，不是忽略)：
        //   - 快取只在本 process 生命週期內有效；domain reload / Editor 重啟會清空 static 欄位，
        //     下次該房間第一次寫入時用 CountMessageFiles 重新校正一次 — 代價可接受 (一個 process
        //     生命週期只發生一次，不是每次 tick / 每次 post)。
        //   - 若有「繞過本 Service 直接寫檔」的情境 (bypass writer — e.g. 未來又出現類似
        //     Antigravity 直寫 jsonl 那次事故的工具、或另一個 Unity Editor process 同時開著同一個
        //     repo 各自維護自己的快取)，本快取值會跟磁碟實際檔案數漂移，算出的 seq 可能重複。
        //     這是「相信 in-memory counter」必然要接受的 trade-off，跟舊版 IncrementAndGetSeq
        //     (UCL_ChatTavernIO.cs，現已是死代碼、零呼叫點) 當年放棄 atomic counter 改用「每次
        //     列目錄重算」的理由同源。目前先不加自動校正/自癒檢查 (YAGNI)；真的觀察到漂移，可以比照
        //     死代碼那套「發現 illicit write 就 log + 自動拉齊」的精神補一個週期性校正。
        // ===========================================================
        static readonly Dictionary<string, int> s_RoomMessageCounts = new Dictionary<string, int>();

        static object GetRoomLock(string roomId)
        {
            lock (s_RoomLocksTableLock)
            {
                if (!s_RoomLocks.TryGetValue(roomId, out var lockObj))
                {
                    lockObj = new object();
                    s_RoomLocks[roomId] = lockObj;
                }
                return lockObj;
            }
        }

        /// <summary>
        /// 寫入一筆訊息並分配 seq 的臨界區入口 — 取代原本散在 UCL_ChatTavernIO.AppendMessage 裡的邏輯。
        /// 回傳分配好的 seq（msg.seq 也已同步設定，供 caller 直接使用）。
        /// </summary>
        public static int WriteMessageWithSeq(string roomId, UCL_ChatMessage msg)
        {
            object roomLock = GetRoomLock(roomId);
            lock (roomLock)
            {
                // 初始化快取 (只在本房間、本 process 生命週期內第一次寫入時算一次) — 這裡的
                // CountMessageFiles 反映「這筆訊息寫入前」的既有檔案數，之後 +1 就是這筆的 seq。
                if (!s_RoomMessageCounts.TryGetValue(roomId, out int currentCount))
                {
                    currentCount = UCL_ChatTavernIO_PerMsgFile.CountMessageFiles(roomId);
                }

                int derivedSeq = currentCount + 1;

                // 檔名現在直接用 seq (2026-07-27, 拿掉 uuid6 — seq 已保證不重複, 不需要隨機尾碼防撞檔)。
                // T38: PerMsgFile 內部處理 ts / uuid(仍寫進內容, 只是不進檔名) / _writer / _pid 簽章。
                var (record, _, wrote) = UCL_ChatTavernIO_PerMsgFile.WriteMessageFileWithSeq(roomId, msg, derivedSeq);

                if (!wrote)
                {
                    // 撞檔 = 快取跟磁碟真實檔案數不同步的訊號 (理論上不該發生——per-room lock 應該
                    // 已經保證同房間內序列化寫入；會走到這裡通常代表有東西繞過本 Service 直接寫檔，
                    // 或者跨 process 各自維護了不同步的快取)。self-heal：不猜新號碼，直接問磁碟真相
                    // 重新算一次，retry 有界次數 (3 次)，每次都失敗才真的放棄 (資料層真的壞了)。
                    const int maxRetries = 3;
                    bool healed = false;
                    for (int attempt = 1; attempt <= maxRetries && !wrote; attempt++)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[Tavern WriteService] 房間 '{roomId}' seq={derivedSeq} 對應檔名已存在 — " +
                            $"快取疑似跟磁碟漂移，重新問磁碟真相校正 (attempt {attempt}/{maxRetries})。");
                        int trueCount = UCL_ChatTavernIO_PerMsgFile.CountMessageFiles(roomId);
                        derivedSeq = trueCount + 1;
                        (record, _, wrote) = UCL_ChatTavernIO_PerMsgFile.WriteMessageFileWithSeq(roomId, msg, derivedSeq);
                        healed = wrote;
                    }
                    if (!healed)
                    {
                        throw new IOException(
                            $"[Tavern WriteService] 房間 '{roomId}' 寫入訊息失敗 — {maxRetries} 次自我校正後仍撞檔，" +
                            $"資料層可能真的損壞，需要人工檢查 messages/ 目錄。");
                    }
                }

                s_RoomMessageCounts[roomId] = derivedSeq;
                record.seq = derivedSeq;

                // 寫 _seq.txt 給 wait 機制當「最大 seq」cache（合法 reader-only 用，不再是 atomic counter）
                try
                {
                    File.WriteAllText(UCL_ChatTavernIO.GetSeqPath(roomId), derivedSeq.ToString(), new UTF8Encoding(false));
                }
                catch { /* fail swallow — 不影響訊息已寫入的事實 */ }

                return derivedSeq;
            }
        }
    }
}
#endif
