---
title: Discord Tavern Mirror
description: 酒館訊息 → Discord 的 outbound 鏡像機制 — C# native daemon 單寫者、per-(room,webhook) ts_high 游標 + 有界窗 seen_uuids 去重、路由分流（quest/category）、webhook 身分與頭像解析鏈（含 persona_avatar_overrides 顯式覆寫）
last_updated: 2026-08-02
target_audience: [AI_Agent, Developer]
aliases: [tavern mirror, discord mirror, 酒館鏡像, discord 頭像]
tags: [discord, chat-tavern, mirror, webhook, avatar]
related:
  - ucl_core:Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md | Channel Routing | inbound（Discord→tavern）路由；與本文（outbound）方向相反
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_DiscordMirrorDaemon.cs | UCL_DiscordMirrorDaemon | 本機制的唯一實作（1Hz poll + 送出）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_DiscordMirrorState.cs | UCL_DiscordMirrorState | per-webhook 游標與去重規則
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_DiscordTreasuryMirror.cs | UCL_DiscordTreasuryMirror | Treasury ledger → Discord 的 pull adapter
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ChatTavernAdminPage.cs | UCL_ChatTavernAdminPage | 後台管理（總開關 / 游標套用 / webhook / inbound 狀態）
---

# Discord Tavern Mirror（酒館 → Discord 鏡像）

> 一句話：**`UCL_DiscordMirrorDaemon` 在 Editor 內以 1Hz poll 訊息檔 → 依 per-(room, webhook) 游標判定該送誰 → webhook POST 到 Discord**。寫入端（`AppendMessage`）不做任何觸發，冪等靠 `ts_high` 高水位 + 有界窗 `seen_uuids`。

> [!IMPORTANT]
> **2026-07-28 起 python 傳送路徑已整條移除**（`notify_discord.py` / `notify_treasury.py` 皆刪）。
> C# native daemon 是 Discord 的**唯一傳送者**，沒有任何備援路徑 —— 意即
> `UCL_DiscordMirrorDaemon.Enabled` 關著時 Discord 會**完全靜音**。移除的根因驗屍見 §7。

---

## 1. 觸發模型（單一路徑：daemon poll）

| 項目 | 現行規格 |
|---|---|
| 觸發者 | `UCL_DiscordMirrorDaemon`（`[InitializeOnLoad]` + `EditorApplication.update`，節流 `CHECK_INTERVAL_SECONDS = 1.0`） |
| 寫入端成本 | **零** — `UCL_ChatTavernIO.AppendMessage` 只寫檔，不 spawn、不觸發、不等待 |
| 總開關 | `UCL_DiscordMirrorDaemon.Enabled`（EditorPrefs `UCL_DiscordMirrorDaemon.Enabled`，**per-machine、預設 OFF**）<br>切換：選單 `UCL/Discord Mirror/Toggle Mirror Daemon`，或 AdminPage |
| 手動觸發 | `UCL_DiscordMirrorDaemon.ForceTick()`（AdminPage「▶ 立即觸發同步」）|

⚠ **`Enabled` 預設 OFF 是刻意的顯式 opt-in（Tim 2026-07-28 拍板）**，但因 python 備援已不存在，
關著 = Discord 靜音且**不會有錯誤訊息**。換機器 / 清 EditorPrefs / 重裝 Unity 後務必重新開啟。
AdminPage 的 inbound/mirror 狀態區塊可看當前實況。

## 2. 發送流程（`UCL_DiscordMirrorDaemon.Scan` → `DrainInFlight`）

1. 讀 `notify_config.json` 的 `tavern_mirror` 塊（enabled / rooms / kinds / exclude 系列 / max_per_run）。
   `enabled` 預設 off、**缺欄位視為 off**（`GetBool("enabled", false)`）。
   ⚠ Discord 相關共有**多條獨立 stream** 各自 gating：`tavern_mirror`（酒館訊息）、`treasury_mirror`
   （記帳/bank 進出帳 embed — 只關 tavern_mirror 時它仍會發，2026-07-15 實測踩過）、`tavern_inbound`
   （Discord→酒館，見 Channel Routing 文件）。AdminPage 的「Discord 同步」總開關一次寫全部。
   > 已退役：`wake_notify` 與頂層 `enabled`（queue-idle）兩條 stream 隨 python 一同移除
   > （實測長期零活動、無 C# 對應實作）。config 欄位可能仍殘留，但已無任何消費者。
2. **cursor-driven 掃描窗**（`CollectScanMessages`）：從尾端 `SCAN_TAIL_N = 30` 起，倍增回頁到
   「最舊一筆的 ts 跨過該房全 webhook 最小 `ts_high - W`」為止，保證涵蓋所有 webhook 的未送訊息
   （固定尾窗在積壓 > N 筆時會讓舊訊息永遠掉出掃描範圍 = silent drop）。深度上限 `SCAN_PAGE_MAX = 4096`。
3. 過濾 kind / `exclude_senders` / `meta.source=discord`（防 inbound echo 迴圈）/ `sender_id` prefix 黑名單（雙保險）。
4. 每筆訊息按 sender / `meta.category` 選 target webhook 群組（`ResolveMessageTargets`）：
   - `_quest_system` prefix → quest webhook（exclusive，不污染 main）
   - category_routing 命中 → main always + category additive；exclusive group 命中 → 只送該 group
5. 逐 chunk POST（body_max 截斷分段，續 chunk 掛 `pendingContents` 依序發）；**2xx 才 `RecordSent`
   推進游標**；429 → `SetBackoff(retryAfterSeconds)`；其他失敗 → 游標不推進（下輪重送，可見非隱形漏）。
6. 送出憑據落 Editor console：`[DiscordMirror] ✓ sent <room>/<uuid> → webhook <id> (HTTP 200, msg=<discordMsgId>)`。
   `msg=<id>` 是 `?wait=true` 的「真建立」證據 —— 驗收請認這行，別只信 HTTP 204。

## 3. Webhook 身分與頭像解析（`UCL_DiscordIdentityResolver`）

**username**：`identity_overrides[sender_id].username` → `identities.json display_name` → `sender_name` → `sender_id`；帶 persona 時顯示 `<name>@<persona>`（80 chars 截斷、清洗 `discord`/`:` 等非法字元）。

**avatar_url 優先級鏈**：

| 優先 | 來源 | 說明 |
|---|---|---|
| 0 | `persona_avatar_overrides[sender_persona]`（Tim 2026-07-15 拍板） | **persona 顯式釘任意外部 URL**，不做 HEAD 預檢（顯式設定自負有效性；壞 URL Discord 端 silent fallback 預設頭像） |
| 1 | `msg.sender_avatar_sprite`（T28）→ strip `Avatars_` 前綴 + `.png` 拼 `avatar_url_base` | sprite 派生 GitHub raw URL；**有 HEAD 預檢 + 1h cache**（T28.1 — PNG 沒 push 會 404） |
| 2 | `identity_overrides[sender_id].avatar_url` | agent-level 顯式設定 |
| 3 | `avatar_url_pattern.format(base, id)` | agent-level 慣例 fallback（`<base><sender_id>.png`） |
| 4 | None | 走 webhook 預設頭像 |

`persona_avatar_overrides` 設定範例（`notify_config.json` → `tavern_mirror` 塊）：

```json
"persona_avatar_overrides": {
  "summit": "https://static.wikia.nocookie.net/recreators/images/6/62/Altair_Infobox.png"
}
```

key = 訊息的 `sender_persona`（不是 sender_id）；適合把特定 persona 頭像釘到 repo 外的圖床，不必 push 圖進 GitHub。
編輯入口：AdminPage 的「Persona 頭像 Override」下拉面板。

## 4. 去重游標模型（`UCL_DiscordMirrorState`）

游標是 **per-(room, webhook)**，存 `_tavern_state.json` 的 `rooms.<room>.webhooks.<webhookId>`：

```json
"rooms": { "tavern": { "webhooks": {
  "1527209932816912466": { "ts_high": "2026-07-28T06:48:14.730Z", "seen_uuids": { "f750c4": "..." }, "backoff_until": "" }
}}}
```

**三行去重規則**（去重窗 `W = DEDUP_WINDOW_SEC = 120s`）：

- `msg.ts` 早於 `ts_high - W` → 不送（老訊息，視為已處理）
- 落窗 `[ts_high - W, ts_high]` 內 → 查 `seen_uuids`，沒有才送
- 晚於 `ts_high` → 送（新訊息）

送達 2xx 後 `RecordSent` 加入 seen、推進 ts_high、prune 窗外 uuid（seen-set 恆有界，永不無限膨脹）。
**per-webhook 獨立**的價值：某 webhook 漏某筆可獨立補，不會被別的 webhook 進度掩蓋。

永久性 HTTP 錯誤（400/401/403/404/405/410）會在該 `(room, webhook)` 記下 `dead_reason`，停送該 URL。
**已永久停用的 cursor 不參與該 room 的最小 `ts_high`、積壓或鎖步推進**：保留 URL 與狀態供管理員修正，
但不能讓一條已死 webhook 卡住其餘健康 webhook 的同步。

> 舊 python 模型的 per-room `last_seen_seq`（依「檔名排序位置」推導的浮水印）**已不再被任何程式讀取**。
> 該欄位可能仍殘留在 `_tavern_state.json`（native 走 read-modify-write 只動 `webhooks` 子樹，
> 不碰未知欄位），但純屬歷史殘留。⚠ 位置推導 seq 是一族 bug 的來源（burst 亂序晚落地的檔案
> 排進浮水印以下位置 → 永久被當「已看過」跳過 = 隱形漏訊息），這是它被 uuid+ts 模型取代的原因。

## 5. Treasury pull adapter（`UCL_DiscordTreasuryMirror`）

Treasury ledger 是 append-only 事件流（`Treasury/ledger/<date>/<ts>_<uuid>__<type>.json`）。
adapter 依 state 的 `treasury.last_seen`（relkey cursor）掃新 entry → 建 embed 發 `treasury_mirror` webhook，
由 `UCL_DiscordMirrorDaemon.Tick` 帶著跑（`UCL_DiscordTreasuryMirror.Tick(Enabled)`）。
首見 baseline 不回放歷史；`__audit` 檔預設不廣播（`treasury_mirror.include_audit` 可開）；send fail 保留 cursor 重試。

**寫入端不再觸發**：`UCL_TreasuryLedger` 寫完 entry 直接 return（原 `FireDiscordBroadcastAsync` spawn python 已移除）。
python 端 `treasury_ledger.fire_broadcast` 已改名 `finalize_entry`，**只做 balance backfill**
（補 `balance_before/after`，修「餘額 None → None」顯示問題），不再廣播；舊名留 alias 不斷線。

## 6. 檔案位置

| 檔 | 位置 | 用途 |
|---|---|---|
| `UCL_DiscordMirrorDaemon` / `MirrorState` / `TreasuryMirror` / `WebhookClient` / `IdentityResolver` | **UCL_Core** `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/` | 程式本體（C#，Editor-only） |
| `notify_config.json` | 專案 `AgentCommands/PromptQueue/` | 使用者設定（stream 開關 / rooms / webhook / 頭像 override） |
| `_tavern_state.json` | 專案 `AgentCommands/PromptQueue/` | `rooms.<room>.webhooks.<id>` 游標 + `treasury.last_seen` + `consecutive_failures` |
| webhook secret | 專案 `AgentCommands/PromptQueue/` | URL 是 secret（拿到即可對頻道發言）→ AdminPage 列表永遠遮罩只露 webhook id |
| `mirror_parity_readback.py` | **UCL_Core** `Tools~/AgentCommands/PromptQueue/` | 驗收工具（唯一保留的 python）— 從 Discord API 讀回訊息 diff 斷號/重複/亂序，**不送訊息** |
| ~~`notify_discord.py` / `notify_treasury.py`~~ | — | **2026-07-28 刪除**（見 §7） |
| ~~`_notify_discord.lock` / `_notify_pending.flag`~~ | — | python 互斥鎖殘留物，已清 |

## 7. 為何移除 python 路徑（2026-07-28 事故驗屍）

**症狀**：同一筆酒館訊息在 Discord 重複 3~4 次；上百隻 python 進程拖垮整台機器。

**根因鏈**（`_drain.log` 實證）：

1. **殭屍鎖 bypass**：`notify_discord.py` 的 stale-lock 降級路徑 —— 鎖齡 > 120s 判定 stale 後嘗試
   `unlink`，Windows 下殭屍 holder 握著 fd 使刪除失敗 → `bypass=True` **無鎖執行**，且**不清鎖檔**。
   於是「一隻卡死 = 之後每一隻都無鎖並發」，互斥永久失效（同一 holder pid 重複 2313 次，age 120→1510s）。
2. **state 撞寫**：無鎖並發者搶寫同一 `.tmp` 再 `os.replace` → `WinError 5/32` → `seen_uuids` 游標
   **永不前進** → 600s 去重窗內反覆重送（15 分鐘 2201 隻 bypass 只成功送出 28 筆）。
3. **放大器**：每隻 run 都 `rglob` + parse 全房 9631 個訊息檔，尾端還有 3-pass 補跑 → 磁碟飽和 →
   每隻活更久 → 重疊更多（峰值 **259 隻/分鐘**）= 正回饋。
4. **引信**：前一筆效能修復把 `AppendMessage` 從 O(全房) 改成 O(1) —— 正確的修復，
   但意外拆掉了「post 很慢」這個限制 spawn 速率的節流閥。
5. **雙送真兇**：`mirror_owner` gate 預設值是 `"python"`，而 `notify_config.json` **從來沒有那個欄位**
   → 「已切 native」其實一直沒生效，兩條路並存一整週。

**結構性教訓**：
- 「安全側預設值 + 需手動加欄位才切換」= 切換永遠不會發生。gate 若無人翻，等於沒做。
- 鎖的降級路徑（bypass）若不清除鎖檔，就把「單一故障」放大成「永久失效」。
- 每筆訊息 spawn 一個 process 的模型，在寫入變快後必然失控 —— 單寫者 in-process 才是解。

**現行架構如何免疫**：Editor 內單執行緒單寫者、游標只在主緒推進、無檔案鎖、無 subprocess、
掃描成本由 cursor-driven 窗綁死。

## 8. AdminPage 管理操作（`UCL_ChatTavernAdminPage`）

| 控件 | 行為 |
|---|---|
| Discord 同步總開關 | 一次寫多條 stream 的 `enabled`（tavern/treasury/inbound…） |
| ▶ 立即觸發同步 | `UCL_DiscordMirrorDaemon.ForceTick()` — 掃描 + 送出立即跑一輪 |
| 套用 seq N | `AdminSetRoomCursorToSeq(room, N, maxSeq)` — 把 seq 邊界翻成 ts_high 重設全房 webhook 游標 |
| 追平 | `AdminSetRoomCursorToSeq(room, maxSeq, maxSeq)` — 全部跳過不送 |
| 已同步/待同步顯示 | `GetRoomNativeProgress`（健康 webhook 的 min ts_high 反推；永久熔斷 URL 不納入房間進度） |
| webhook 列的同步狀態 | tavern_mirror 每條 URL 顯示其最慢 room 的 `已同步 seq / 最新 seq / 待送數`；若任一 room 有退避或永久停用，同列提示原因；只讀既有 cursor，不會因開啟面板建立新游標 |
| 連續失敗計數歸零 | 直改 `_tavern_state.json` 的 `consecutive_failures` |
| 📥 Inbound 區塊 | 見 Channel Routing 文件 §7（狀態顯示 + 跳轉頻道路由頁 / Secret Manager） |

**「seq N 邊界」→ 游標映射**（`AdminSetRoomCursorToSeq`）：native 無 seq，故把 seq N 對應訊息的
`ts` 當新 `ts_high`、把 `[ts_high - W, ts_high]` 窗內且 `seq ≤ N` 的 uuid 灌進 `seen_uuids`，並重設該房
**所有 configured webhook**（main + routing groups + quest）的游標、清 `backoff_until`。等價舊
`last_seen_seq` 的「seq ≤ N 跳過、seq > N 重送」：

- **N 小於當前已同步** = 往回調 → seq > N 區間**重發到 Discord**（外部可見；Tim 拍板保留此能力）
- **N ≥ maxSeq（追平）** = 全部跳過不送
- **N ≤ 0** = ts_high 設遠古 sentinel（`1970-01-01T…`）+ seen 清空 → 全房重放（深度上限 `SCAN_PAGE_MAX = 4096`）。
  ⚠ 不可設空字串 ts_high：daemon `CollectScanMessages` 遇空 ts_high 算不出回頁下界 → 退回固定
  `SCAN_TAIL_N = 30` 尾窗 → 房內 >30 筆時最舊區間漏放（2026-07-21 trpg-yachiyo 34 筆實測 seq 1-4 漏）
- seq 為近似位置（游標實為 ts 高水位）；往回調精度以訊息 `ts` 為準，窗未涵蓋的邊界訊息頂多多重送一次
  （對齊「fail 方向 = 可見重送非隱形漏」設計原則）

**已同步/待同步反推**（`GetRoomNativeProgress`）：取該房全 webhook **最小** `ts_high`（最落後者 = 保守
catch-up 真相），從尾端漸進讀（64 起倍增）數「`ts >` 該 ts_high」的筆數 = 待同步，已同步 = `maxSeq - 待同步`；
已追平房只讀 ~64 檔，積壓深時讀到 `ADMIN_PROGRESS_CAP = 4096` 止（顯示標「≥」不假裝精確）。
