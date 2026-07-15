---
title: Discord Tavern Mirror
description: 酒館訊息 → Discord 的 outbound 鏡像機制 — 雙觸發路徑、per-room last_seen_seq 冪等、路由分流（quest/category）、webhook 身分與頭像解析鏈（含 persona_avatar_overrides 顯式覆寫）
last_updated: 2026-07-15
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

## 4. 已知問題 — 同一筆訊息偶發重複發送（root cause 分析，2026-07-15）

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

觸發條件 = 多筆訊息在一個 mirror run 的耗時窗（HEAD 預檢 + N 次 webhook POST，秒級）內連續產生 — 正是多 agent 同時發言 / bartender 廣播撞 agent post 的日常場景，故「有機率」而非必現。

**修法方向（待拍板）**：mirror run 全程包一把 lock file 互斥（`O_CREAT|O_EXCL` + stale 偵測），後到者**帶 timeout 等待**而非直接退出（退出會延遲它觸發的那筆訊息，等到下次觸發才補發）；順手把 `_save_tavern_state` 改 tmp + `os.replace` 原子落檔（現為直接 write_text，被並發讀到半截 JSON 會 state 重置 → 全房 re-baseline，中間訊息漏發）。

## 5. State / Config 檔案位置

| 檔 | 用途 |
|---|---|
| `AgentCommands/PromptQueue/notify_config.json` | 使用者設定（deep-merge 蓋 DEFAULT_CONFIG） |
| `AgentCommands/PromptQueue/_tavern_state.json` | per-room `last_seen_seq` + `consecutive_failures` |
