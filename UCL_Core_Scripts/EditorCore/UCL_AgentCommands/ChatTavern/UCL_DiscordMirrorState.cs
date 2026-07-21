// 區塊職責：Discord Mirror 去重游標 state model（C# native）— Discord Mirror Phase B T4-b 核心演算法
// 物理意義：per-(room, webhook) 獨立游標，實作 basecamp 拍板的「有界窗去重」——ts_high 高水位 + 只對
//          「ts 落在 ts_high 往回窗 W 內」的訊息存 uuid 去重。seen-set 尺寸被 W 綁死（個位～十位數），永不 prune 政策。
// 設計取捨（basecamp seq 12678 拍板）：
//   - 不存全量 seen_uuids（現行 python 12,677 筆訊息也只存 2 條）— 有界窗即足夠防「邊界 ts 亂序」重複
//   - per-webhook 獨立游標 → 某 webhook 漏某筆可獨立補（解多 webhook partial drop），儲存代價僅幾條 uuid × webhook 數
//   - 初次 rehydrate 從 python _tavern_state.json 讀「各房 ts_high」當種子（只讀不寫）→ 不重播 12k 歷史
// 數值影響：三行去重規則 — msg.ts 早於 ts_high-W → skip（老訊息不補送）；落窗內 → 查 seen_uuids；晚於 ts_high → 送。
//          寫入層（Save 落 disk）留 T4-b 討論1 定案後接（native 檔 vs 共用檔的並發寫者 race）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>單一 (room, webhook) 的去重游標。ts_high 高水位 + 有界窗 seen_uuids + 429 backoff 到期時刻。</summary>
    public class UCL_MirrorWebhookCursor
    {
        public string ts_high = "";                                       // ISO ts 高水位（已確認送達的最新訊息 ts）
        public Dictionary<string, string> seen_uuids = new Dictionary<string, string>();  // uuid → ts（只留窗內）
        public string backoff_until = "";                                 // ISO；非空 = 429 退避到期前不送
    }

    /// <summary>
    /// Discord Mirror 去重 state（T4-b）。純演算法 + in-memory 狀態；Save（落 disk）待討論1 定案。
    /// 用法：EnsureLoaded() → 對每則訊息 ShouldSend() 判定 → 送達 2xx 後 RecordSent() → 週期 Save()。
    /// </summary>
    public static class UCL_DiscordMirrorState
    {
        // 區塊職責：去重窗常數（basecamp 建議 W=120s，對齊 python _STABLE_CURSOR_SKEW_WINDOW_SEC 家族）
        // 物理意義：只有 ts 落在 [ts_high-W, ts_high] 的訊息需查 seen_uuids 防亂序重複；更舊的直接視為已處理
        // 數值影響：W 越大 → seen-set 越大、容忍越大亂序；120s 對多 agent 並發寫檔的毫秒級亂序綽綽有餘
        public const double DEDUP_WINDOW_SEC = 120.0;

        // in-memory 狀態：room → webhookId → cursor
        static readonly Dictionary<string, Dictionary<string, UCL_MirrorWebhookCursor>> s_State =
            new Dictionary<string, Dictionary<string, UCL_MirrorWebhookCursor>>();
        // 各房 ts_high 種子（初次從 python _tavern_state.json 讀入，給新 webhook cursor 當起始高水位，避免重播歷史）
        static readonly Dictionary<string, string> s_RoomBaselineTsHigh = new Dictionary<string, string>();
        // 各房 python room 級 seen_uuids 種子（follow-up#3：flip 時邊界訊息 ts==ts_high 落窗內、
        // 新 cursor seen 空 → 重送一次。繼承 python 的 seen 就把這個 cutover 邊界 dup 消掉）
        static readonly Dictionary<string, Dictionary<string, string>> s_RoomBaselineSeen =
            new Dictionary<string, Dictionary<string, string>>();
        static bool s_Loaded = false;

        /// <summary>lazy load — 從 python _tavern_state.json 讀各房 ts_high 當種子（只讀不寫，避免並發寫者 race）。</summary>
        public static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            try
            {
                string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_tavern_state.json");
                if (!File.Exists(path)) return;
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path));
                if (jd == null || !jd.IsObject || jd.Dic == null) return;
                if (jd.Dic.TryGetValue("rooms", out var roomsNode) && roomsNode != null && roomsNode.IsObject && roomsNode.Dic != null)
                {
                    foreach (var kv in roomsNode.Dic)
                    {
                        string room = kv.Key;
                        var roomNode = kv.Value;
                        if (roomNode == null) continue;

                        // python 端 per-room ts_high 種子（給「無自己 cursor 的新 webhook」當起始高水位，避免重播歷史）
                        string tsHigh = roomNode.GetString("ts_high", "");
                        if (!string.IsNullOrEmpty(tsHigh)) s_RoomBaselineTsHigh[room] = tsHigh;

                        // follow-up#3：一併繼承 python room 級 seen_uuids（uuid→ts dict，與 cursor 同構）
                        if (roomNode.Contains("seen_uuids"))
                        {
                            var seenNode = roomNode["seen_uuids"];
                            if (seenNode != null && seenNode.IsObject && seenNode.Dic != null)
                            {
                                var seen = new Dictionary<string, string>();
                                foreach (var sv in seenNode.Dic)
                                    if (sv.Value != null) seen[sv.Key] = sv.Value.GetStringWithDefaultValue("");
                                if (seen.Count > 0) s_RoomBaselineSeen[room] = seen;
                            }
                        }

                        // 讀回 native 自己寫的 per-webhook 游標（seen-set / ts_high / backoff）→ 活過 domain reload（kiara 要求）
                        // Tim 拍板 class + JsonLib：LoadDataFromJson<T> 把純欄位 JsonData 還原成 cursor class（SaveMode.Normal 對齊 Save）
                        if (roomNode.Contains("webhooks"))
                        {
                            var webhooksNode = roomNode["webhooks"];
                            if (webhooksNode != null && webhooksNode.IsObject && webhooksNode.Dic != null)
                            {
                                foreach (var whKv in webhooksNode.Dic)
                                {
                                    if (whKv.Value == null) continue;
                                    var cursor = UCL.Core.JsonLib.JsonConvert.LoadDataFromJson<UCL_MirrorWebhookCursor>(
                                        whKv.Value, UCL.Core.JsonLib.JsonConvert.SaveMode.Normal);
                                    if (cursor == null) continue;
                                    if (cursor.seen_uuids == null) cursor.seen_uuids = new Dictionary<string, string>();
                                    if (!s_State.TryGetValue(room, out var byWebhook))
                                    {
                                        byWebhook = new Dictionary<string, UCL_MirrorWebhookCursor>();
                                        s_State[room] = byWebhook;
                                    }
                                    byWebhook[whKv.Key] = cursor;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiscordMirror] state seed load fail（走空白 baseline）: {e.Message}");
            }
        }

        // 區塊職責：取（或初始化）某 (room, webhook) 的游標；新 cursor 的 ts_high 種子自各房 baseline
        // 物理意義：新 webhook 首次出現時，從 python 端該房 ts_high 起步 → 不補送該時刻之前的歷史
        static UCL_MirrorWebhookCursor GetOrCreateCursor(string room, string webhookId)
        {
            if (!s_State.TryGetValue(room, out var byWebhook))
            {
                byWebhook = new Dictionary<string, UCL_MirrorWebhookCursor>();
                s_State[room] = byWebhook;
            }
            if (!byWebhook.TryGetValue(webhookId, out var cursor))
            {
                cursor = new UCL_MirrorWebhookCursor();
                if (s_RoomBaselineTsHigh.TryGetValue(room, out var seed))
                {
                    cursor.ts_high = seed;
                    // follow-up#3：連 python room 級 seen_uuids 一起繼承 — 邊界訊息（ts==ts_high）
                    // 在窗內查 seen 會命中 → flip 不再重送一次（07-19 cutover 實錄的一次性 dup 根治）
                    if (s_RoomBaselineSeen.TryGetValue(room, out var seedSeen))
                        foreach (var sv in seedSeen) cursor.seen_uuids[sv.Key] = sv.Value;
                }
                else
                {
                    // Bug A fix（凍結契約 seq 12684：種子缺席 = 從 ts_high=now 起步，絕不 backfill 整房歷史）
                    // 原實作留空 ts_high → ShouldSend 視全部為新 = 整房 replay（被 SCAN_TAIL_N 兜住才沒炸）
                    cursor.ts_high = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                }
                byWebhook[webhookId] = cursor;
            }
            return cursor;
        }

        /// <summary>
        /// 三行去重規則（basecamp 拍板）：
        ///   msg.ts 早於 ts_high-W → false（老訊息不補送）；落窗 [ts_high-W, ts_high] 內 → seen_uuids 沒有才 true；
        ///   晚於 ts_high → true（新訊息）。ts_high 空（全新 cursor 無種子）→ 一律視為新 → true。
        /// </summary>
        public static bool ShouldSend(string room, string webhookId, string uuid, string ts)
        {
            EnsureLoaded();
            var cursor = GetOrCreateCursor(room, webhookId);

            // backoff 中（429 退避未到期）→ 本輪不送（daemon 之後 tick 再試）
            if (IsInBackoff(cursor)) return false;

            DateTime? tsMsg = ParseIsoUtc(ts);
            DateTime? tsHigh = ParseIsoUtc(cursor.ts_high);

            if (tsMsg == null) return true;              // ts 解析不出 → 保守送（fail 方向 = 可見重送非隱形漏）
            if (tsHigh == null) return true;             // 無高水位種子 → 視為新

            double W = DEDUP_WINDOW_SEC;
            if (tsMsg.Value < tsHigh.Value.AddSeconds(-W)) return false;   // 早於 ts_high-W → 老訊息 skip
            if (tsMsg.Value <= tsHigh.Value)                              // 落窗內 → 查 seen
                return !cursor.seen_uuids.ContainsKey(uuid);
            return true;                                                  // 晚於 ts_high → 新訊息送
        }

        // 區塊職責：查該房「全 webhook 中最小的 ts_high」— Bug B cursor-driven Scan 的回頁下界
        // 物理意義：Scan 只要往回讀到「跨過 min(ts_high) - W」就保證涵蓋所有 webhook 的未送訊息；
        //          比固定 Tail(N) 窗正確（積壓 > N 筆時固定窗會讓舊訊息永遠掉出掃描範圍 = silent drop）。
        // 數值影響：回傳 ISO ts 字串（ordinal 比較 = 時間序）；任一 cursor ts_high 為空（parse 壞檔等異常）
        //          → 回空字串，caller 退回固定窗 fallback（有界安全側，不做無界全房掃）。
        /// <summary>
        /// 該房全 webhook 最小 ts_high。
        /// iSkipEmpty=false（daemon Scan 下界用）：任一 cursor 無 ts_high → 回空字串（caller 退固定窗，安全側）。
        /// iSkipEmpty=true（AdminPage 進度顯示用）：跳過 ts_high 空的 webhook（空=未建立位置/backoff 卡住的殘留，
        ///   不應把整房顯示歸零）；全部皆空才回空字串。
        /// </summary>
        public static string GetMinTsHigh(string room, List<string> webhookIds, bool iSkipEmpty = false)
        {
            EnsureLoaded();
            string min = null;
            foreach (var wid in webhookIds)
            {
                if (string.IsNullOrEmpty(wid)) continue;
                // GetOrCreateCursor：新 webhook 在此即按契約種子化（baseline 或 now），與 ShouldSend 一致
                var cursor = GetOrCreateCursor(room, wid);
                if (string.IsNullOrEmpty(cursor.ts_high))
                {
                    if (iSkipEmpty) continue;   // 顯示用：空 ts_high 不當 min 約束（跳過）
                    return "";                  // 掃描用：任一空 → 安全側回空（固定窗 fallback）
                }
                if (min == null || string.CompareOrdinal(cursor.ts_high, min) < 0) min = cursor.ts_high;
            }
            return min ?? "";
        }

        /// <summary>送達 2xx 後記帳：加入 seen_uuids、推進 ts_high、prune 窗外舊 uuid（維持有界）。</summary>
        public static void RecordSent(string room, string webhookId, string uuid, string ts)
        {
            EnsureLoaded();
            var cursor = GetOrCreateCursor(room, webhookId);
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(ts))
                cursor.seen_uuids[uuid] = ts;

            DateTime? tsMsg = ParseIsoUtc(ts);
            DateTime? tsHigh = ParseIsoUtc(cursor.ts_high);
            if (tsMsg != null && (tsHigh == null || tsMsg.Value > tsHigh.Value))
                cursor.ts_high = ts;   // 推進高水位

            PruneWindow(cursor);
        }

        // 區塊職責：prune 掉 ts 落在 ts_high-W 之前的 seen_uuids（維持 seen-set 有界，永不無限膨脹）
        static void PruneWindow(UCL_MirrorWebhookCursor cursor)
        {
            DateTime? tsHigh = ParseIsoUtc(cursor.ts_high);
            if (tsHigh == null) return;
            DateTime lowerBound = tsHigh.Value.AddSeconds(-DEDUP_WINDOW_SEC);
            List<string> toRemove = null;
            foreach (var kv in cursor.seen_uuids)
            {
                DateTime? t = ParseIsoUtc(kv.Value);
                if (t != null && t.Value < lowerBound)
                {
                    (toRemove ??= new List<string>()).Add(kv.Key);
                }
            }
            if (toRemove != null)
                foreach (var k in toRemove) cursor.seen_uuids.Remove(k);
        }

        // ===== 429 backoff =====

        /// <summary>該 (room, webhook) 是否在 429 退避中（backoff_until 未到期）。</summary>
        public static bool IsInBackoff(string room, string webhookId)
        {
            EnsureLoaded();
            return IsInBackoff(GetOrCreateCursor(room, webhookId));
        }

        static bool IsInBackoff(UCL_MirrorWebhookCursor cursor)
        {
            if (string.IsNullOrEmpty(cursor.backoff_until)) return false;
            DateTime? until = ParseIsoUtc(cursor.backoff_until);
            if (until == null) return false;
            return DateTime.UtcNow < until.Value;
        }

        /// <summary>設 429 退避到期時刻 = now + seconds（daemon 拿到 PostOnceAsync 的 retryAfterSeconds 後呼叫）。</summary>
        public static void SetBackoff(string room, string webhookId, double seconds)
        {
            EnsureLoaded();
            var cursor = GetOrCreateCursor(room, webhookId);
            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
            cursor.backoff_until = until.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>清掉 backoff（送達成功後）。</summary>
        public static void ClearBackoff(string room, string webhookId)
        {
            EnsureLoaded();
            GetOrCreateCursor(room, webhookId).backoff_until = "";
        }

        // ===========================================================
        // 區塊：admin 手動游標重設（T6.5 — AdminPage「套用 seq / 追平」native 端接點）
        // 物理意義：舊 python 模型的「套用 seq N」= 設 last_seen_seq=N，native 無 seq → 改為「把該房
        //          指定 webhook 們的游標整組重設到某高水位」。呼叫方（UCL_DiscordMirrorDaemon.AdminSetRoomCursorToSeq）
        //          已把「seq N」翻成「該 seq 對應訊息的 ts（當 ts_high）+ 窗內 seq≤N 的 uuid（當 seen）」。
        // 數值影響：重設後 ShouldSend 對 ts≤ts_high-W 一律 skip、對窗內已在 seen 的 skip、對窗內未 seen 或
        //          晚於 ts_high 的送 → 等價「seq≤N 跳過、seq>N 重送」。iTsHigh 空 = 清空高水位（全房重放）。
        //          一律清 backoff_until（管理員手動重設意圖 = 立即從新游標起算，不受殘留退避卡住）。
        //          寫入即 Save() 落 disk（單寫者 mirror_owner 硬互鎖保證原子覆寫安全，見 Save 區塊註解）。
        // ===========================================================
        /// <summary>
        /// 把某房「指定 webhook 們」的去重游標整組重設為 (iTsHigh, iSeenUuids)、並清 backoff。
        /// 給 AdminPage 的「套用 seq / 追平」用（seq→ts 映射由 UCL_DiscordMirrorDaemon 端完成）。
        /// iTsHigh 為空字串 = 清空高水位（全房重放）；iSeenUuids 為 null 視同空 dict。
        /// </summary>
        public static void AdminSetRoomCursors(string room, IEnumerable<string> webhookIds, string iTsHigh, Dictionary<string, string> iSeenUuids)
        {
            if (string.IsNullOrEmpty(room) || webhookIds == null) return;
            EnsureLoaded();
            foreach (var wid in webhookIds)
            {
                if (string.IsNullOrEmpty(wid)) continue;
                var cursor = GetOrCreateCursor(room, wid);
                cursor.ts_high = iTsHigh ?? "";
                // 深拷貝 seen（各 webhook 各持一份，避免共用同一 dict 被後續 prune 互相污染）
                cursor.seen_uuids = iSeenUuids != null
                    ? new Dictionary<string, string>(iSeenUuids)
                    : new Dictionary<string, string>();
                cursor.backoff_until = "";
            }
            Save();
        }

        // ===== Save 落 disk（討論1 拍板：共用 canonical _tavern_state.json + mirror_owner 硬互鎖）=====
        // 區塊職責：把 in-memory per-webhook 游標寫回共用 canonical state 檔
        // 物理意義：basecamp 拍板條件 b — read-modify-write 整份 JSON，**只動 rooms.<room>.webhooks 子樹**，
        //          treasury / 別房 / 未知 key 全部原樣寫回。JsonData round-trip 天然保留未知欄位（絕不強型別
        //          serialize 回去 → 那會靜默洗掉未知欄位，是共用檔方案唯一資料毀損點，比 race 更容易踩）。
        // 數值影響：mirror_owner 硬互鎖保證同時只有一個 writer（python 或 native），故此處直接 atomic 覆寫安全。
        //          舊有的 python 欄位（last_seen_seq / ts_high / seen_uuids 在 room 頂層 / treasury）完全不碰。
        public static void Save()
        {
            try
            {
                string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_tavern_state.json");
                var state = File.Exists(path)
                    ? UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path))
                    : UCL.Core.JsonLib.JsonData.ParseJson("{\"rooms\":{},\"consecutive_failures\":0}");
                if (state == null) return;
                if (!state.Contains("rooms")) state["rooms"] = UCL.Core.JsonLib.JsonData.ParseJson("{}");
                var roomsNode = state["rooms"];

                foreach (var roomKv in s_State)
                {
                    string room = roomKv.Key;
                    if (!roomsNode.Contains(room)) roomsNode[room] = UCL.Core.JsonLib.JsonData.ParseJson("{}");
                    var roomNode = roomsNode[room];
                    if (!roomNode.Contains("webhooks")) roomNode["webhooks"] = UCL.Core.JsonLib.JsonData.ParseJson("{}");
                    var webhooksNode = roomNode["webhooks"];

                    foreach (var whKv in roomKv.Value)
                    {
                        // Tim 拍板：資料格式定 class + UCL.Core.JsonLib 轉換。cursor(class) → SaveDataToJson → 純欄位 JsonData
                        // 用 SaveDataToJson（非 ObjectToJson）: 前者出 {ts_high,seen_uuids,backoff_until} 純欄位、
                        // 後者會多包 ClassName/ClassData 型別 wrapper（污染契約 schema，且 python 讀不懂）。
                        // SaveMode.Normal = 欄位名原樣（ts_high 等 snake_case 已對齊 JSON key，不做 m_ 改名）。
                        var whNode = UCL.Core.JsonLib.JsonConvert.SaveDataToJson(whKv.Value, UCL.Core.JsonLib.JsonConvert.SaveMode.Normal);
                        webhooksNode[whKv.Key] = whNode;   // splice 進 read-modify-write 樹，別的 webhook / 欄位不動
                    }
                }

                AtomicWrite(path, state.ToJsonBeautify());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiscordMirror] state save fail: {e.Message}");
            }
        }

        // 區塊職責：原子寫檔（tmp + delete + move）— 對齊 AdminPage.AtomicWrite；防寫到一半被讀到半截 JSON
        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ===== helpers =====

        // 區塊職責：容忍多種 ISO8601 變體（帶/不帶 Z、帶/不帶毫秒）→ DateTime(UTC)；失敗回 null
        // （Bug B 後開放 public — daemon cursor-driven Scan 算回頁下界時需與去重判定用同一套 ts 解析）
        public static DateTime? ParseIsoUtc(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return null;
        }
    }
}
#endif
