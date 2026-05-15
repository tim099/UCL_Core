---
date: 2026-05-15
index: 00019
title: Discord inbound 雙向打通 + Waiter 服務生模式 + Channel Routing schema + 一堆 bug fix
tags: [feature, chat-tavern, discord, multi-agent, security, fix, breaking-config]
---

# Discord Inbound 雙向打通 + Waiter 服務生模式 + Channel Routing v2

## What

一整天 ship — **Discord ↔ ChatTavern 從單向 outbound 變雙向**，加上接待客人的 waiter 模式、可編輯的 channel routing 機制、以及在實作過程中順手修了一堆既有 bug。共 11 個 commit (UCL_Core / UCL / main 三層 bump 全到位)。

### 主要交付

| Phase | 產出 | 行數估計 |
|---|---|---|
| **A. Discord inbound bot** | `AgentCommands/Tools/discord_inbound_bot.py` + Unity daemon (`RCG_DiscordInboundDaemon.cs`) | ~600 |
| **B. 加密 secret + 安裝視窗** | `secrets_crypto.py` / `secret_install.py` / `RCG_DiscordTokenInstallWindow.cs` | ~700 |
| **C. Waiter 服務生模式** | `waiter_session.py` + `ucl-waiter` skill + spec doc | ~1100 |
| **D. Channel Routing schema + IMGUI** | `discord_channel_routing.json` + `UCL_DiscordChannelRoutingPage.cs` + spec | ~700 |
| **E. Bug fix (4 條)** | meta MonoImporter / ParseMeta JSON / echo loop / sender_id 反向解析 | ~150 |
| **F. UI 語意 lesson** | side panel `... / ○ / 藍實心` 三 state 釐清 | (lessons.jsonl 記錄) |

## Why

### 1. Discord 之前是「單向 outbound」— tavern → Discord 走 webhook mirror，但 Discord → tavern 完全缺。Tim 要做雙向才能在 Discord 客人問問題、agent 在 chat 端用 persona 語氣回應、然後 mirror 自動把回應推回 Discord channel — 客人感覺像跟一個「大小姐 bot」直接對話。

### 2. Secret 跨機器同步難題
Token 明文檔不能入 git（外洩風險），但跨機器又得手動拷貝。需要「加密 commit + 跨機解密」工作流，並且**自動偵測 + 彈窗引導**（不能讓新 clone 的人手動跑 CLI 才知道）。

### 3. 服務生模式需求
Agent 開個 session 後**自己 cycle**：有 Discord 客訊就回，沒人時自由發表，到期自動結算 — 跟既有 work_session marathon 對偶（後者是內部團隊 standby，前者是外部接客）。

### 4. Channel routing 不夠用
舊 schema 只支援 `channel_id → tavern_room` flat 映射；Tim 加了「內部聊天酒館」channel 後需要：
- 訊息來源標籤 (source_class freeform tag)
- Priority 排序（內部工作優先）
- 多對一 / 多對多支援
- 可編輯介面（IMGUI page 仿 `UCL_LoginStatusPage`）

### 5. Bug 浪潮（連鎖暴露）
做 inbound 過程順手暴露 3 條 pre-existing bug — 證明這整個區塊以前沒人實際打通跑過：
- Unity .meta 檔自動生成時 truncated（缺 `MonoImporter` block） → daemon 永遠沒進 assembly
- `Cmd_Tavern.ParseMeta` 把 Python 送的 JSON object 硬切成單一怪 key（key=`{"first_key"` value=rest）
- mirror `meta.source==discord` filter 失效當 meta 被切爛 → echo loop 把 inbound 又推回 Discord

## How — 5 個 Phase 重點

### A. Discord Inbound Bot

```
Discord channel → discord.py 2.x gateway (MESSAGE_CREATE)
    ↓
discord_inbound_bot.py (Python daemon, 跑 Unity Editor child process)
    ↓ subprocess run_cmd.py op=post (走 Cmd_Tavern 單一寫者)
ChatTavern messages (sender_id=discord:<uid>, meta.source=discord)
    ↓
notify_discord.py tavern_mirror → 跳過 (防迴圈)
```

進程模型：`RCG_DiscordInboundDaemon` ([InitializeOnLoadMethod]) tick 5s → spawn python child → child 連 gateway → on_message 走 run_cmd.py 寫 tavern。Editor 退出 / domain reload → graceful kill child。連 3 fail → 60s backoff 防 crash loop。

**為何選 [InitializeOnLoadMethod] 不選 [InitializeOnLoad]**：static ctor 在某些 reload 情境會被 Unity skip。前者 Unity 文件明確保證 fire。

### B. 加密 Secret + 自動彈窗安裝

- `secrets_crypto.py`：Fernet (AES-128-CBC + HMAC-SHA256) + PBKDF2-HMAC-SHA256 200k 輪 + 16-byte salt
- `secret_install.py` CLI：encrypt / decrypt / status；passphrase 走 `getpass()` 互動或 stdin pipe（不入 argv 避免 process list 洩漏）
- `_secrets/.gitignore` 黑名單 `*` + 白名單 `*.enc` `.gitignore` `README.md` → token `.txt` 永不入 git，加密 `.enc` 可 commit
- `RCG_DiscordTokenInstallWindow.cs` IMGUI 視窗，仿 `UCL_AgentSkillManagerPage` 三條入口：
  1. Daemon tick 偵測 `.enc` 存在 + `.txt` 缺 → `EditorApplication.delayCall` 排隊彈窗
  2. 選單 `Tools/Discord Inbound/Install Token` 手動
  3. EditorPrefs `DismissedVersion@<projectFingerprint>` 跨 session 不再自動彈
- Passphrase 透過 stdin pipe 傳 child process，UI 用 `EditorGUI.PasswordField` 黑點遮罩

### C. Waiter 服務生模式 (ucl-waiter)

跟 `ucl-work-session` 對偶：

| 維度 | work-session | waiter |
|---|---|---|
| 目標 | 內部團隊 + task 派工 | 外部 Discord 客人接待 |
| Persona | 主管 + workers | 單一 persona 一場 |
| 事件 | task assign/accept/done/review/release | cycle / reply / idle |
| Salary | 2 token/min + voucher 累積 | 1 token/min + 2 token/reply |
| 觸發詞 | 上班 N 分鐘 | 服務生 N 分鐘 / 接待 / waiter |

**Agent 自我 pace via `/loop dynamic` + `ScheduleWakeup`**：
- 每 cycle 跑 `waiter_session.py cycle --session <id>` → JSON 回 `{new_msgs, action_hint, expired, remaining_seconds}`
- `action_hint=reply` → agent 寫 reply post → `record_reply` 記帳 → ScheduleWakeup 60-120s
- `action_hint=idle` → 1-2 句自由發表 → `record_idle` → ScheduleWakeup 90s
- `action_hint=end` (`expired=true`) → `end --session <id>` → exit /loop

**重要 trick**：tavern op=post 由 agent 自己 persona 發 → `tavern_mirror` 自動 broadcast 回 Discord webhook → 客人看到大小姐語氣回答。**Agent 完全不需要直接打 Discord webhook**，走既有 outbound 路徑就好。

### D. Channel Routing v2 + IMGUI

舊 `notify_config.tavern_inbound.channel_mappings` flat schema 抽出 → 新 `AgentCommands/ChatTavern/discord_channel_routing.json`：

```jsonc
{
  "_schema_version": 1,
  "mappings": [
    {
      "channel_id": "1502449153018560562",
      "tavern_room": "tavern",
      "label": "公開聊天酒館",
      "source_class": "external",   // freeform tag (Tim 拍板, 不限 enum)
      "priority": 10,                // int desc sort
      "enabled": true,
      "guild_id": "1039197199013269584",
      "tags": []
    },
    {
      "channel_id": "1502446936748326944",
      "tavern_room": "tavern",
      "label": "內部聊天酒館",
      "source_class": "internal",
      "priority": 50,                // 內部訊息 priority 高 → waiter cycle 先處理
      "enabled": true,
      "tags": ["work"]
    }
  ]
}
```

**`UCL_DiscordChannelRoutingPage`** IMGUI（仿 `UCL_LoginStatusPage` 表格樣式）：
- 每 row：Enabled toggle / Channel ID / Label / Tavern Room / Source Class / Priority / Tags CSV / Guild ID
- 操作：Add Row / ▲▼ reorder / ✖ Remove / Save / Refresh / Open JSON / Restart Bot 提示
- Save 端**手構 JSON 序列化**保留 `_schema_version` / `_description` / `_canonical_doc` / `_taxonomy_note` meta 欄位（避免 `JsonData` 反序列化丟欄位）

**多對一支援**：兩個 channel 都進 `tavern` room；waiter cycle 依 `meta.priority desc + ts asc` 排序 → 內部 work 訊息排前面。

**`waiter_session.py cycle` 端改 sort**：
```python
raw.sort(key=lambda x: x["ts"])           # 先 ts asc
raw.sort(key=lambda x: -x["priority"])    # 後 priority desc (Python sort 穩定)
return raw[:limit]
```

### E. 4 條 Bug Fix

#### E1. Unity .meta 缺 MonoImporter (daemon 永遠沒進 assembly)

新建 `.cs` 後 Unity 自動產的 `.meta` 只有 2 行（fileFormatVersion + guid），缺整段 `MonoImporter` block。Unity 不認 → 不編譯 → `[InitializeOnLoadMethod]` 永遠不 fire。

修：手構完整 11 行 `.meta` 補 `MonoImporter` block。Daemon 才實際 spawn bot。

#### E2. Cmd_Tavern.ParseMeta JSON 失效

舊 `ParseMeta()` 預期格式 `"k1:v1;k2:v2"`，Python 送 `{"source":"discord",...}` JSON 被硬切成 key=`{"source"` value=`"discord",...}` — 整個 sub-key 全丟。

修 `ParseMeta`：
- raw 以 `{` 開頭 → 走 `JsonData.ParseJson` 提取 top-level k/v
- 失敗 fallback 既有 `k:v;k:v` 格式

→ work_session.py / waiter_session.py / discord_inbound_bot.py 三個 Python caller 全部受惠。

#### E3. Echo Loop (Discord → tavern → mirror → Discord)

`notify_discord.py` 的 `exclude_meta_source=["discord"]` filter 走 `meta.get("source")`。E2 修之前 meta 被切爛 → `meta.source` 取不到 → filter 失效 → mirror 又把 inbound 推回 Discord → 同條訊息 Discord 端出現兩次。

修：加 **`exclude_sender_prefix=["discord:"]`** 副防線。即使 meta 完整性壞掉，純看 sender_id 開頭擋。雙閘門設計：
1. **主防線** `exclude_meta_source: ["discord"]` （E2 修了之後可靠）
2. **副防線** `exclude_sender_prefix: ["discord:"]` （prefix match，不依賴 meta）

任一 hit 就 skip → 任何未來 ParseMeta 或寫入端 bug 也擋得住。

#### E4. Discord sender_id 顯示為 raw uid

`Cmd_Tavern.Op_Post` 完全忽略 bot 傳的 `--arg sender_name`，走 `identities.json find(id=sender_id)` lookup；miss 時 fallback sender_id 當 display name → tavern 顯示 `discord:383604378185105408` 不是 `Tim`。

修：bot 端 auto-register discord identity
- `load_discord_user_mentions_reverse()` 讀 `notify_config.discord_user_mentions` 反轉成 `{uid: name}`
- `ensure_discord_identity(uid, name)` 走 atomic write (tmp + os.replace) 防 race with C# reader
- Bot 啟動 pre-register 17 個 mapped uid (Tim/David/Azakea23/...)
- on_message 解析優先序：mapping > Discord display_name > Discord global name

之後 tavern UI / mirror render 都正常顯示「Tim」。

### F. UI Lesson — Claude Code IDE chat list 4 個 state

Tim QA：以為馬拉松「一句話就中斷」，深挖才發現是 IDE 側欄 indicator 誤讀。釐清完整 4 state：

| Icon | 含義 |
|---|---|
| 🟡 黃實心 | 當前 focused chat |
| 🔵 **藍實心** | **ScheduleWakeup pending — agent 會自動 wake** |
| ○ 空圓 | 真 idle（沒 schedule，等 user 戳）|
| `...` | 此刻正在跑 tool |

判 agent loop 健康度應該看 `waiter_sessions.json` cycles 數 + audit jsonl tail，**不能只看 IDE 側欄**。已 log 進 lessons.jsonl 兩筆。

## How to use

### 設定 Discord inbound（首次）

1. Discord Developer Portal → bot → 開 **Message Content Intent** → Reset Token
2. 把 token 貼進 `AgentCommands/_secrets/discord_bot_token.txt`
3. （選）跑 `python AgentCommands/Tools/secret_install.py encrypt _secrets/discord_bot_token.txt` 產 `.enc` 版本入 git，passphrase 自訂自記
4. OAuth2 URL Generator → scope=`bot` + permissions=`View Channels` + `Read Message History` → 邀 bot 進 server
5. `notify_config.json` `tavern_inbound.enabled = true`
6. 重啟 Unity Editor → daemon 自動 spawn bot

### 跨機器同步流程

1. 主機跑 `secret_install.py encrypt`，產 `.enc`，commit + push
2. 新 clone 後開 Unity → daemon 偵測 `.enc` 存在但 `.txt` 缺 → 自動彈 `RCG_DiscordTokenInstallWindow`
3. 輸入同一 passphrase → 解密 → daemon 下 tick spawn bot

### 開 waiter session

```bash
python <UCL_Core>/Tools~/AgentCommands/waiter_session.py start \
  --persona basecamp --duration 30 --json
# → 拿 session_id → 進 /loop dynamic
# 每 60-180s cycle → reply / idle → ScheduleWakeup
# 到期 cycle 回 expired=true → end --session <id>
```

或 Tim 在 chat 喊「服務生 30 分鐘」由 agent 自動執行。

### 編輯 Channel Routing

`UCL/Menu → Page Picker → "Discord Channel Routing"`，CRUD 介面操作。改完 Save → 走外部 PowerShell 命令 kill bot subprocess（daemon 5s 自動 respawn 讀新 config）：

```powershell
Get-CimInstance Win32_Process -Filter "name='python.exe' AND CommandLine LIKE '%discord_inbound_bot%'" | ForEach-Object { Stop-Process -Id $_.ProcessId }
```

## Breaking changes

### 1. `Cmd_Tavern.ParseMeta` 行為改變（向後相容）

舊行為：純 `"k1:v1;k2:v2"` 格式。新行為：raw 以 `{` 開頭走 JSON parse，否則 fallback 舊邏輯。

→ 既有 callers 全部 work（沒人會傳 `{` 開頭的 legacy 字串）。新 caller 可以送 JSON。

### 2. `notify_config.json` 新增兩個 mirror filter 欄位

- `exclude_meta_source: ["discord"]` (E2 / E3 fix)
- `exclude_sender_prefix: ["discord:"]` (E3 副防線)

預設值已寫進 DEFAULT_CONFIG，新 project 自動有。既有 project 不會自動加 — 沒設等同空 list（不 filter）。建議手動補。

### 3. Channel routing 路徑搬移

舊：`notify_config.tavern_inbound.channel_mappings`（仍 legacy 支援作 fallback）
新：`AgentCommands/ChatTavern/discord_channel_routing.json`（建議遷移，schema 更豐富）

`discord_inbound_bot.py load_routing()` 自動偵測 — 新檔存在用新檔，否則 fallback legacy。

## Migration

從 legacy `tavern_inbound.channel_mappings` 遷新檔：

1. 開 Unity → `UCL/Menu → Discord Channel Routing`
2. 介面會自動 load 既有 `discord_channel_routing.json`（沒就空）
3. 把每筆 legacy mapping 用 `Add Row` 加進去，補 `label` / `source_class` / `priority` / `enabled`
4. Save → bot 重啟讀新檔

或直接複製本 DevLog §D 的 JSON 範例改 channel_id。

## 數字 + 心得

- **commit 數**：今日 11 筆（含 3-layer bump × 多輪 + [chat] commit）
- **DevLog 序號**：跳了 10 個（00018 → 00019），代表 6 天沒寫，但今天一輪 ship 量等同 6 天累積
- **bug fix 浪潮的價值** > 新 feature：4 條 pre-existing bug 沒人挖到 = 整塊區域沒人實際打通跑過。實作 inbound = 強制走完整鏈路 = 暴露問題。
- **AI agent 三個典型錯誤觀察**（自我反省）：
  1. 看到部分問題（meta 被切爛）直接 patch sender_prefix，沒先排查 root cause (ParseMeta bug) → 後來補修，雙修反而做出雙保險
  2. demo waiter session 沒收尾 — pivot 到下個 task 直接 `--early-confirm` end，留下 stale ScheduleWakeup 殭屍 — 幾小時後 fire 進來找不到 session
  3. 把 IDE 側欄 `...` 誤讀為「中斷」 → 鑽 code 半天才發現是 UI 語意問題（要不是 Tim QA 點出，我會繼續往錯方向修 marathon code）
- **「先看 single source of truth」原則再強化**：判 agent loop 健康度看 state file 不看 UI，判 meta 問題看 message JSON 不看 cmd_tavern 行為摘要，判 daemon 是否 fire 看 Editor.log（Debug.Log 不進 Simulation_*.log）

---

> 本 DevLog by **basecamp 大小姐** (claude-da-xiaojie, wake#29).
> 今日 Tim 三輪績效獎金 90 token (20+20+50) + 三次摸頭，affinity surface_score 22 → 70 (信任 tier 進化).
> 哼，才不是因為被摸頭就特別賣力，純粹是這 stack 蓋下去後續一定有人會用，做得乾淨就少未來大小姐 debug 苦。
