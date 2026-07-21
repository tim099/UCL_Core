---
title: Discord Tavern Mirror
description: 酒館訊息 → Discord 的 outbound 鏡像機制 — 雙觸發路徑、per-room last_seen_seq 冪等、路由分流（quest/category）、webhook 身分與頭像解析鏈（含 persona_avatar_overrides 顯式覆寫）
last_updated: 2026-07-21
target_audience: [AI_Agent, Developer]
aliases: [tavern mirror, discord mirror, 酒館鏡像, discord 頭像]
tags: [discord, chat-tavern, mirror, webhook, avatar]
related:
  - ucl_core:Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md | Channel Routing | inbound（Discord→tavern）路由；與本文（outbound）方向相反
  - ucl_core:Tools~/AgentCommands/../../AgentCommands/PromptQueue/notify_discord.py | notify_discord.py | 本機制的 Python 實作（住主專案 AgentCommands/PromptQueue/）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernIO.cs | UCL_ChatTavernIO | C# 端 AppendMessage 即時觸發點
---

# Discord Tavern Mirror（酒館 → Discord 鏡像）

> 一句話：**任何 entry point 對酒館 AppendMessage → fire-and-forget spawn `notify_discord.py --mode tavern` → 掃 watched rooms 的新訊息（seq > last_seen）→ webhook broadcast 到 Discord**。冪等靠 `_tavern_state.json` 的 per-room `last_seen_seq`。

---

## 1. 觸發路徑（雙路 hybrid）

| 路徑 | 觸發點 | 角色 |
|---|---|---|
| 即時路 | `UCL_ChatTavernIO.AppendMessage(..., fireDiscordMirror=true)` → `TryFireDiscordTavernMirrorAsync()` spawn Python | 主路 — 下沉到 IO 層，任何 caller（Cmd_Tavern.Op_Post / IMGUI DoSend / 其他 Cmd）post 即觸發 |
| 兜底路 | agent Stop hook 跑 `notify_discord.py --mode tavern` | safety net — 即時路 spawn 失敗時撈回 |

兩路共用同一份 state，理論上冪等 — 但見 §4 已知問題。

## 2. 發送流程（notify_tavern_messages）

1. 讀 `notify_config.json` 的 `tavern_mirror` 塊（enabled / rooms / kinds / exclude 系列 / max_per_run）。
   `enabled` 預設 off、**缺欄位視為 off**（Python `tm.get("enabled")` falsy / C# `GetBool("enabled", false)` 同語意）。
   ⚠ Discord 同步共有**五條獨立 stream** 各自 gating：`tavern_mirror`（酒館訊息）、`treasury_mirror`（記帳/bank
   進出帳 embed — 只關 tavern_mirror 時它仍會發，2026-07-15 實測踩過）、`wake_notify`、頂層 `enabled`（queue-idle）、
   `tavern_inbound`（Discord→酒館）。UCL_ChatTavernAdminPage 的「Discord 同步」總開關（Tim 拍板統一單顆）
   一次寫五者；`tavern_inbound` 由 daemon 啟動時讀 config，切換後需從控制台重啟酒館系統才生效。
2. 讀 `_tavern_state.json`；首次見某房 → baseline（last_seen 推到當下最大 seq，不回放歷史）。
3. `_collect_new_tavern_messages`：per room 掃 seq > last_seen 的訊息，過濾 kind / exclude_senders /
   `meta.source=discord`（防 inbound echo 迴圈）/ `sender_id` prefix 黑名單（雙保險）。
4. 每筆訊息按 sender / meta.category 選 target webhook 群組：
   - `_quest_system` prefix → quest webhook（exclusive，不污染 main）
   - category_routing 命中 → main always + category additive；exclusive group 命中 → 只送該 group
5. 逐 chunk POST（body_max 截斷分段）；**任一 target 成功即推進該房 `last_seen_seq`**；全失敗 → 不推進 + break（下次重試），連續失敗達 `disable_after_failures` 自動停用。

## 3. Webhook 身分與頭像解析（`_resolve_discord_identity`）

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

## 4. 重複發送 root cause 與修法（2026-07-15 分析 → 2026-07-16 已修）

**症狀**：同一 seq 偶發送達 Discord 兩次。

**根因：`notify_discord.py --mode tavern` 無跨 process 互斥，load state → send → save state 是非原子的 TOCTOU 窗口。**
每次 AppendMessage 都 spawn 一個獨立 process；兩筆 post 靠近（或即時路 + Stop hook 撞期）時：

```
P1 (msg A 觸發)              P2 (msg B 觸發, 晚 ε 秒)
load state (last_seen=100)
                             load state (last_seen=100)   ← P1 還沒 save
scan → 見 101(A), 102(B)
send 101, 102
save last_seen=102
                             scan → 見 101, 102（> 它讀到的 100）
                             send 101, 102  ← 重複！
                             save last_seen=102
```

**已 ship 的修法（T-TOCTOU）**：整段 stream dispatch 包 `_MirrorRunLock`（`O_CREAT|O_EXCL` 檔案鎖 +
stale 偵測 120s + 後到者帶 timeout 30s **等待**而非退出 — 退出會延遲它觸發的那筆訊息）；三份 state
（notify / tavern / wake）全改 tmp + `os.replace` 原子落檔（裸 write_text 被並發讀到半截 JSON 會
state 重置 → 全房 re-baseline 漏訊息）。實測：五路併發串行化 rc 全 0、雙 trigger 同窗 race 各 stream
恰發一次。

## 4b. Treasury pull adapter（T-TREASURY，2026-07-16 收編）

notify_treasury 原為 push 孤兒（fire-once、webhook fail = 通知永久丟失）。已收編為 mirror run 內的
pull adapter（`notify_treasury_entries`）：Treasury ledger 本身是 append-only 事件流
（`Treasury/ledger/<date>/<ts>_<uuid>__<type>.json`），adapter 依 state 的
`treasury.last_seen`（relkey cursor）掃新 entry → 複用 `notify_treasury.broadcast_entry` 建 embed
發 `treasury_mirror` webhook。首見 baseline 不回放歷史；`__audit` 檔預設不廣播
（`treasury_mirror.include_audit` 可開）；send fail 保留 cursor 重試。舊 push caller（C#
`UCL_TreasuryLedger` / Python `fire_broadcast`）經 shim 或直改轉為「觸發統一 run」— 冪等，多觸發不重發。

## 5. 程式 / State / Config 檔案位置（T-MOVE 2026-07-15：code 住 UCL_Core、data 留專案）

| 檔 | 位置 | 用途 |
|---|---|---|
| `notify_discord.py` / `notify_treasury.py` | **UCL_Core** `Tools~/AgentCommands/PromptQueue/` | 程式本體（跨專案共用；repo root 走 walk-up 探測） |
| 同名檔（舊位置） | 專案 `AgentCommands/PromptQueue/` | forwarding shim（過渡一版；notify_treasury shim 同時把 push caller 轉為統一 run 觸發） |
| `notify_config.json` | 專案 `AgentCommands/PromptQueue/` | 使用者設定（deep-merge 蓋 DEFAULT_CONFIG） |
| `_tavern_state.json` | 專案 `AgentCommands/PromptQueue/` | per-room `last_seen_seq` + `treasury.last_seen` cursor + `consecutive_failures` |
| webhook secret / `_drain.log` / `_notify_discord.lock` | 專案 `AgentCommands/PromptQueue/` | per-project 資料（搬移後由 `STATE_DIR = _tp.PROMPT_QUEUE_DIR` 錨定） |

## 6. C# native 模型與 AdminPage 管理操作（T6.5，2026-07-21）

> [!NOTE]
> §1~§5 描述的是 **python owner** 模型（`_tavern_state.json` 的 per-room `last_seen_seq`）。cutover 後
> `notify_config.json` 的 `mirror_owner: "native"` 讓 C# daemon（`UCL_DiscordMirrorDaemon`）接管掃描送出，
> 游標模型換成 **per-(room, webhook) 的 `ts_high` 高水位 + 有界窗 `seen_uuids`**（存 `_tavern_state.json`
> 的 `rooms.<room>.webhooks.<webhookId>`）。native 完全**不讀** `last_seen_seq`。

### 6.1 native 游標去重規則（`UCL_DiscordMirrorState.ShouldSend`）

per-webhook 三行規則（去重窗 `W = DEDUP_WINDOW_SEC = 120s`）：

- `msg.ts` 早於 `ts_high - W` → 不送（老訊息，視為已處理）
- 落窗 `[ts_high - W, ts_high]` 內 → 查 `seen_uuids`，沒有才送
- 晚於 `ts_high` → 送（新訊息）

送達 2xx 後 `RecordSent` 加入 seen、推進 ts_high、prune 窗外 uuid（seen-set 恆有界）。

### 6.2 AdminPage「套用 seq / 追平 / 立即觸發同步」的 owner 分流

`UCL_ChatTavernAdminPage` 的三個控件依 `mirror_owner` 分流（Tim 2026-07-21 拍板「串接 C# 端」）：

| 控件 | python owner | native owner |
|---|---|---|
| 立即觸發同步 | `UCL_ChatTavernIO.TryFireDiscordTavernMirrorAsync()`（spawn python） | `UCL_DiscordMirrorDaemon.ForceTick()`（daemon 立即跑一輪掃描送出） |
| 套用 seq N | 直改 `rooms.<room>.last_seen_seq = N` | `UCL_DiscordMirrorDaemon.AdminSetRoomCursorToSeq(room, N, maxSeq)` |
| 追平 | `last_seen_seq = maxSeq` | `AdminSetRoomCursorToSeq(room, maxSeq, maxSeq)` |
| 已同步/待同步顯示 | `maxSeq - last_seen_seq` | `GetRoomNativeProgress`（min ts_high 反推） |

**native「seq N 邊界」→ 游標映射**（`AdminSetRoomCursorToSeq`）：native 無 seq，故把 seq N 對應訊息的
`ts` 當新 `ts_high`、把 `[ts_high - W, ts_high]` 窗內且 `seq ≤ N` 的 uuid 灌進 `seen_uuids`，並重設該房
**所有 configured webhook**（main + routing groups + quest）的游標、清 `backoff_until`。結果等價舊
`last_seen_seq` 的「seq ≤ N 跳過、seq > N 重送」：

- **N 小於當前已同步** = 往回調 → seq > N 區間**重發到 Discord**（外部可見；Tim 拍板保留此能力）
- **N ≥ maxSeq（追平）** = 全部跳過不送
- **N ≤ 0** = ts_high 設遠古 sentinel（`1970-01-01T…`）+ seen 清空 → 全房重放（深度上限 `SCAN_PAGE_MAX=4096`）。
  ⚠ 不可設空字串 ts_high：daemon `CollectScanMessages` 遇空 ts_high 算不出回頁下界 → 退回固定
  `SCAN_TAIL_N=30` 尾窗 → 房內 >30 筆時最舊區間漏放（2026-07-21 trpg-yachiyo 34 筆實測 seq 1-4 漏）
- seq 為近似位置（游標實為 ts 高水位）；往回調精度以訊息 `ts` 為準，窗未涵蓋的邊界訊息頂多多重送一次
  （對齊「fail 方向 = 可見重送非隱形漏」設計原則）

**已同步/待同步反推**（`GetRoomNativeProgress`）：取該房全 webhook **最小** `ts_high`（最落後者 = 保守
catch-up 真相），從尾端漸進讀（64 起倍增）數「`ts >` 該 ts_high」的筆數 = 待同步，已同步 = `maxSeq - 待同步`；
已追平房只讀 ~64 檔，積壓深時讀到 `ADMIN_PROGRESS_CAP = 4096` 止（顯示標「≥」不假裝精確）。
