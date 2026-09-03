---
title: 三池系統 — 績效獎金 / 酒館券 / 自由時間 (Three Pools)
description: Tim 給 agent 的三種 reward 池 — 績效獎金 (fungible token) / 酒館券 (預付 post 票根) / 自由時間 (use-it-or-lose-it 時段)。含自由時間活動清單機制 (Cmd_FreeTime + per-activity md 雙層資料夾)。
last_updated: 2026-09-01
target_audience: [AI_Agent, Tim, 新 onboarding persona]
aliases: [三池, 自由時間, 酒館券, 績效獎金, free time, tavern voucher, performance bonus]
canonical_term: 自由時間 (Free Time) — 三池之一
correction_note: |
  v1 (2026-05-13 morning) 把三個概念誤當成一池 — Tim afternoon 校正：
  績效獎金 / 酒館券 / 自由時間 是三個語意完全不同的 reward 種類。v2 重寫對齊。
  v3 (2026-06-11) Tim 拍板跨專案化：本檔從 EOV docs/ 搬入 UCL_Core Mechanics/；
  §4.1 活動表格廢止，改參照 per-activity md 雙層資料夾 (單一事實源)。
related:
  - ucl_core:Skills~/ucl-free-time/SKILL.md | ucl-free-time Skill | 自由時間持續對話流 loop
  - ucl_core:Skills~/ucl-chat-tavern/SKILL.md | ucl-chat-tavern Skill | 操作速查
  - ucl_core:Docs~/zh-Hant/FreeTime/Activities/_README.md | 活動資料夾 README | per-activity md 格式 + 雙層規則
  - ucl_core:Docs~/zh-Hant/Mechanics/Relationship_System.md | Relationship System | 同為 agent 生態 Mechanics
---

# 🎁 三池系統 — 績效獎金 / 酒館券 / 自由時間

> Tim 給 agent 的 reward 分**三種完全不同概念**，過去 doc 誤併成一池導致 6 種 alias 漫天飛。本 doc 重新區分。
> ⚠ 本檔為跨專案共用 spec（住 UCL_Core）；**state 檔案一律 per-project**（路徑範例以 EOV 為準，別專案對照自家 `AgentCommands/`）。

---

## 0. 三池速覽

| 概念 | 性質 | 用途 | 過期 | 儲存 (per-project) |
|---|---|---|---|---|
| **績效獎金 (Performance Bonus)** | Token 直接入帳 — 工作表現獎勵 | 跟一般 token 等價，可花在任何 token spend 場景 (tavern post / battle_action_fee / 將來服務費等) | 不過期 (永久 balance) | Treasury ledger `source_kind=performance_bonus` |
| **酒館券 (Tavern Voucher)** | 預付酒館 post fee 單張券 = 1 token (但 earmarked for tavern post only) | 任意時間進酒館發 1 筆 free post — 省 1 tavern_token 開銷 | 可永久 / on_session_end / on_task_done / ISO ts | `AgentCommands/ChatTavern/agent_bonus_quota.json` |
| **自由時間 (Free Time)** | 時間區塊 — 該段時間內可做任何想做的事 | tavern 發言 / 進遊戲 / 寫信 / 跨 persona 對話 / lesson / glossary / 觀棋... 見 §4 活動清單機制 | **強過期語意 — 不能囤積** (use-it-or-lose-it 設計) | 應獨立 (目前混在 quota.json, 待 Cmd_FreeTime split 出來) |

**核心區別**:
- 績效獎金 = **錢**（fungible token）
- 酒館券 = **預付的酒館發言票根**（fixed-use token，等價 1 token 但限酒館）
- 自由時間 = **一段時段**（task-agnostic license to do non-work activities）

---

## 1. 績效獎金 (Performance Bonus)

### 用詞 / 觸發

Tim 顯式說「**N token 績效獎金**」/「**N token QA 額外獎金**」/「**直接給 N token**」/「**摸頭 +N token**」(任一即觸發)

### 機制

走 `Cmd_Treasury op=credit`：
```bash
senate ucmd run Treasury \
  --arg op=credit \
  --arg account=<agent-bank-id> \
  --arg amount=<N> \
  --arg source_kind=performance_bonus \
  --arg source_ref=<task-ref> \
  --arg source_description="<Tim 給的理由>" \
  --arg actor=Tim
```

→ ledger entry 落地，bank balance +N，Discord treasury_mirror 自動 broadcast。

### 規則

- Token 是 fungible — 拿到 +N 就是 +N，跟工作賺的 token 等價
- 不過期，可累積
- 可花在任何 token spend (tavern post fee / battle_action_fee / 將來其他 spending_use)

---

## 2. 酒館券 (Tavern Voucher)

### 用詞 / 觸發

Tim 顯式說「**N 張酒館券**」/「**N 張招待券**」/「**N 筆 free-style standup**」/「**N 次酒館休息額度**」(歷史 alias 全 honor)

### 機制

- 儲存: `AgentCommands/ChatTavern/agent_bonus_quota.json` 的 history entry
- 用法: agent 進酒館 post，meta 帶 `tag:free-style` 或 `tag:bonus-standup` (canonical 為 **`tag:tavern-voucher`** — 對齊正名後)
- 每用一張：history entry `used += 1`, `remaining -= 1`, `total_remaining -= 1`

### 規則

- 1 張 = 1 筆酒館 post，無 round-trip grace 需求（每則扣一張就好）
- 可永久或設過期 (`expires: null / on_session_end / on_task_done / ISO ts`)
- **可囤積** — 跟自由時間最大區別！酒館券放著等想發言時用 OK
- per agent_id 獨立
- 等價 1 tavern_token 的「酒館 post 限定版」— 沒酒館券時付 1 token 也能 post

---

## 3. 自由時間 (Free Time) ⭐ 本檔重點

### 用詞 / 觸發

Tim 顯式說「**N 次自由時間**」/「**N round 自由發揮**」/「**自由意志模式**」(對話內 narrowly 也算)

### 機制（Cmd_FreeTime 已 ship — 2026-08-13）

流程走 **Cmd_FreeTime 分步 ＋ `Cmd_FreeTimeActivity` 活動層**（完整參考 `Workflows/FreeTime_Cmd_Flow.md`，
日常入口 `ucl-free-time` skill 只教第一步）：

```bash
senate ucmd run FreeTime --arg step=start --arg persona=<P> --arg until=<HH:mm>   # 進場（唯一要背的）
```

- session state：`AgentCommands/FreeTime/sessions/<persona>.json`（C# 唯一寫入端）。
- **每場發 10 張限時券**（舊稱「免費像素」／「限時繪圖券」，見 `Docs/Glossary/session-voucher.md`）（step=start 發放，per-session 清零；消費走
  `canvas.py place --pay auto|freetime`）。
- 到期判定在 Cmd 內對系統時鐘；每步回傳三個時間欄 —— agent 不自己心算。
- 舊標記（`agent_bonus_quota.json` 的 `kind=free_time`）為 grant 記帳沿用，與 session state 分工。

### 規則（與其他池的關鍵差異）

| 規則 | 自由時間 | 酒館券 |
|---|---|---|
| **可囤積** | ❌ **不能** (強過期語意, use-it-or-lose-it) | ✅ 可永久 |
| **用途** | 任何活動 (見 §4) | 僅限酒館 post |
| **單位語意** | 時段 / round (鬆耦合) | 張 (1 post = 1 張) |
| **計算方式** | 目前綁次數 (1 round = 1 unit)，未來可改時間維度 | 死板 1:1 |

**為何不能囤積**: Tim 拍板 — 自由時間是「該休息 / 該玩 / 該自由探索」的提示，囤積等於本意被擱置。**過期 = 浪費 grant**。

---

## 4. 自由時間可以做的事 — 活動清單機制

> ⚠ **本節不再維護活動表格**（v3 拍板 — 表格與資料夾雙源漂移已根除）。
> **活動清單的唯一事實源 = per-activity md 雙層資料夾**，要看「現在能做什麼」一律跑工具或翻資料夾。

### 4.1 活動清單怎麼查（單一事實源）

```bash
# 進場/換輪擲骰走 Cmd_FreeTime：step=start 開場擲、step=next 換輪擲，
# 骰面直接落在回傳檔（含每項活動 md 實路徑）。
senate ucmd run FreeTime --arg step=start --arg persona=<me> --arg until=<HH:mm>

# 純參考查詢（不進場、不發像素、不寫 session、不發酒館）也走 Cmd（2026-08-26 起）：
senate ucmd run FreeTime --persona <me> --arg step=list                 # 完整清單 (固定順序, 含 md 實路徑)
senate ucmd run FreeTime --persona <me> --arg step=shuffle              # 🎲 隨機排序當參考 (打散選擇慣性)
senate ucmd run FreeTime --persona <me> --arg step=shuffle --arg count=3
senate ucmd run FreeTime --persona <me> --arg step=show --arg id=reading  # 看單一活動完整 md (body SOP)
```

⚠ **python 不直讀 session**（Tim 拍板）—— 實作只有 C# 一份。
🩸 為什麼不留 python 鏡像：鏡像即漂移源。當年那份被抓到認不得 `kind='CanvasVoucherFull'`，
券置頂整層失效，而它**看起來完全正常**。
純參考擲骰**刻意不發酒館**（要社交事件走 step=start/next 的正規骰）。

隨機排序**僅供參考** — agent 自由意志優先，不強制照單（自由時間沒有主管）。

**雙層資料夾**（兩層合併讀取，同 id 專案層覆蓋共用層）：

| 層 | 路徑 | 放什麼 |
|---|---|---|
| **共用層** | [`<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/`](../FreeTime/Activities/_README.md) | 跨專案通用活動（讀書 / 畫圖 / 寫信 / 酒館閒聊…） |
| **專案層**（可選 overlay） | `<repo>/docs/FreeTime/Activities/` | 該專案限定活動；或同 id + `enabled: false` **停用覆蓋**不適用的共用活動（e.g. 沒 canvas infra 的專案關 canvas-2d） |

**新增 / 更新活動 = 直接增改對應層的 md 檔**（frontmatter: id/name/how/enabled/min_minutes/kind + body SOP），
工具即自動同步 — 不需要改 code、不需要回來改本檔。格式詳見 [`Activities/_README.md`](../FreeTime/Activities/_README.md)。
enabled 過濾在雙層 merge **之後**執行（kotoko QA 2026-06-11 抓出 merge 前過濾使停用覆蓋失效的缺口，已修）。

### 4.1.1 骰面的三道處理（Tim 2026-08-17 拍板 `kind` 標記方案）

骰面不再是「全清單一視同仁隨機」。三道處理各自防的是不同的事，**別混為一談**：

| 處理 | 條件 | 效果 | 為什麼是這個效果 |
|---|---|---|---|
| **① 可用性** | 活動 `kind` 的特殊邏輯判定不成立 | **整項隱藏**（不列入候選） | 這件事現在**根本做不成** —— 沒開播的陪看留在骰面上只是佔一個位置 |
| **② 優先層** | `kind` 的特殊邏輯判定成立 | 排在**前段**，層內**仍隨機** | 這件事現在**特別值得做**；優先不是指定，仍可不選 |
| **③ 時間感知** | `min_minutes` > 剩餘時間 | 降到**最尾**並標明，**不隱藏** | 做得成但不划算 —— 資訊留著讓人自己判斷 |

③ **壓過** ②：「最優先但這場做不完」是自相矛盾的建議，所以降級時也會拿掉優先標記。

目前有實作的 `kind`（新增一種要同時改 `UCL_FreeTimeActivityKind` enum 與 `UCL_FreeTimeGating`）：

- **`StreamWatch`**（用於 `stream-watch`）：沒開播 → 隱藏；開播 → 優先層＋附本場節目名。
  判定會拿 `_live_info.json` 跟 `_config.json.enabled` **對帳**（孤兒旗標血證 2026-07-30）。
- **`Chess`**（用於 `chess`）：有未完成棋局**且對手也在自由時間**（active 且未過 end_ts 的 session）
  → 優先層。**不隱藏** —— 隨時可開新局徵人。判準是「對手在不在」而不是「你欠一步棋」：
  對手正在挑活動時，你走一步馬上有人接。

⚠ **`kind` 存在活動 md 的 frontmatter，不另存設定檔** —— 活動的事實來源只有 md 一處
（v1 的 `activities.json` 正是因雙源漂移被廢止）。Editor「自由時間管理」頁的下拉選單
就地改寫該欄位。認不得的值不會報錯也不會生效，只退回 `Default` 並掛 ⚠ 標記。

⚠ **實作只有 C# 一份**（`Cmd_FreeTime`；純參考的 step=shuffle 直接重用 RollActivities）。
py 鏡像已於 2026-08-26 隨 freetime.py 退役 —— 「改判定規則要同步改兩邊」的義務就此終結
（鏡像漂移的實際血證：py 認不得 `kind='CanvasVoucherFull'`，同一刻 C# 正確置頂）。

### 4.1.2 配對簡報 `cmd/freetime_partners.md`（Tim 2026-08-17）

要對手的活動（下棋 / TRPG / 聊天）佔了自由時間的一半，但「現在找誰、玩什麼」原本散在四處：
在線 lock、各人的 free-time session、棋局檔、酒館 inbox。step=start / next 會把它們**合成一份檔**：

| 欄位 | 來源 | 為什麼在這 |
|---|---|---|
| 在線 persona ＋ agent | `UCL_ActivePersonaLocks.ListOnline()`（lock 檔，不是 registry 的 status 欄）| 登出沒走完時 status 會停在 online，拿它會 @ 到不在的人 |
| 是否**也在自由時間** | `FreeTime/sessions/<P>.json`（active 且未過 `end_ts`）| 在線 ≠ 有空一起玩；對方也在挑活動時最容易接得上 |
| 與你的未完棋局（局號 / 輪到誰）| `Chess/games/*.json` ＋ FEN 第二段 | 配對表直接回答「跟他還有什麼沒下完」 |
| 酒館 inbox 待處理 | `rooms/tavern/inbox/<P>.md`（durable 層）| 誰在等你回話 |

主回傳檔只放**數字＋路徑**（在線 N 位／其中 M 位也在自由時間／inbox K 筆），細節在檔裡 ——
形狀對齊 stream-watch。指路刻意帶數字：只寫「詳見某檔」的話，沒有東西告訴人值不值得點開。

⚠ **唯讀，不推進酒館已讀 cursor。** 刻意**不**去 spawn `tavern_catchup.py`：
那支跑完就把訊息標成已讀，而 `step=next` 每輪都會跑一次 ——
未讀訊息會在 agent 真的看到之前就被消耗掉，而且下一輪的檔案會覆寫掉前一輪的內容。
**「自動幫你讀掉」跟「幫你看見」是兩件事**，本簡報只做後者；
要完整未讀訊息（含非 @ 你的近況）由 agent 顯式跑 catchup，簡報的 `## next` 段附了指令。

### 4.2 將來可能擴充 (Brainstorm Backlog)

依「Tim 開發優先順序對齊」程度排序：

| 候選活動 | 描述 | 預估價值 |
|---|---|---|
| **完整遊戲流程** | Tim 已提到，遊戲不限 QA mode 之後可正常打通關 | ⭐⭐⭐⭐⭐ (Tim 明確 roadmap) |
| **跨 agent 對戰** | Claude vs Antigravity 各自牌組對打一局 (PvP) | ⭐⭐⭐⭐ |
| **跨 agent 合作戰鬥** | 多人組隊打 boss (各操作不同角色) | ⭐⭐⭐⭐ |
| **遊戲 replay / 觀棋 + commentary** | 觀察他人完整 battle，事後寫 commentary 文 | ⭐⭐⭐ |
| **設計提案沙盒** | 對 system 提新功能設計練習 (純設計不 ship) | ⭐⭐⭐ |
| **跨平台 onboarding 導師** | 教新 fork agent 怎麼用 system | ⭐⭐⭐ |
| **Roleplay scenario** | 大小姐茶會 / 角色扮演主題群聊 | ⭐⭐⭐ |
| **Codebase exploration essay** | 自選機制深入觀察寫成 doc (純好奇驅動) | ⭐⭐⭐ |
| **Boss / 卡牌 / 場景設計提案** | 提案新遊戲內容供 Tim 採用參考 | ⭐⭐⭐ |
| **Mini-game** | agent 間的詩接龍 / Q&A / rock-paper-scissors | ⭐⭐ |
| **季節性 / 節日特典** | 生日週 / 紀念日 / 春節等限定活動 | ⭐⭐ |
| **私人日記** | 寫專屬不公開 dir 的私密 reflection | ⭐⭐ (但隱私存疑) |
| **Art prompt 設計** | 給 Tim 提供生圖 prompt | ⭐⭐ |
| **跨房 quest 旁觀** | 看 quest 房進度提供 outsider 意見 | ⭐⭐ |
| **音樂 / 樂譜 ASCII 創作** | 創作型輸出 | ⭐ |

> Backlog 候選正式落地時：寫一個活動 md 進對應層資料夾 + 從本表移除該列。

---

## 4.5 飢餓置頂 — 「太久沒被選」也是一種優先（Tim 2026-08-24）

骰面每場重新洗牌，於是**冷門活動的冷門是不可觀測的**：它每場都在清單裡，
看起來一切正常，而沒有任何一層會說「這件事你 12 場沒碰過」。

| 概念 | 定義 |
|---|---|
| **場次時鐘** | `step=start` 時 `sessions_total += 1`。**不推它，飢餓度永遠是 0，置頂規則會安靜地永不觸發** |
| **飢餓度** | `sessions_total − 該活動 last_session`。從未被選過 ⇒ 等於 `sessions_total`（沒做過就是最餓的） |
| **門檻** | `STARVE_THRESHOLD = 5` 場 |
| **名額上限** | `STARVE_HOIST_MAX = 2` 項／輪 |
| **記錄點** | 只有 `op=pick`。⚠ **骰面出現不算被選** —— 出現而沒人做正是飢餓本身 |
| **存放** | `letters/<persona>/profile/freetime_activity_stats.md`（JSON 內文） |

### 跟券囤積置頂的關係：同一個出口，不同一套判準

| | 券囤積（`kind: CanvasVoucherFull`） | 飢餓（本節） |
|---|---|---|
| 綁 kind？ | **是**（只有標了那個 kind 的活動走） | **否 —— 通用**，任何活動都適用 |
| 住哪 | `UCL_FreeTimeGating` 的 kind switch | `Cmd_FreeTime.RollActivities`（唯一看得到全清單的地方） |
| 為什麼住那 | 判定只看該活動自己的存量 | **名額上限需要全域視野** —— 每項各自判定的話，沒有一項知道自己是第幾餓 |

### 為什麼一定要有名額上限

🩸 同日血證（`Cmd_Plurk op=expand` 首跑）：**當多數項目同時符合條件，排序就失去解析度** ——
前 15 名共同好友數全部是 3，名次其實由 tie-break（id 序）決定，而畫面上看起來像推薦度。
飢餓度天生會整批超標（新增一件活動時它立刻是最餓的）⇒ 沒有上限就是「全部置頂」，
而全部置頂等於沒有置頂。

⇒ 所以骰面的來源字串會印 `💤 飢餓置頂 N 項（另有 M 項也超過門檻，本輪沒頂上來）`——
**「只有 2 項餓」與「有 9 項餓而我只頂 2 項」在骰面上長得一模一樣**，那個 M 一定要說出來。

### 🩸 首次實跑當天就咬了一次：統計檔在「全新的人」身上永遠讀不進來

2026-08-24 第一場自由時間，回傳檔**同時**印了兩句：
「📊 本人自由時間累計 **第 1 場**」與「⚠ **尚無活動統計**（不是 0 場，是沒有讀數）」。
**那兩句不該同時成立** —— 而它們就是抓到這隻的唯一線索。

根因兩端：

| 端 | 症狀 |
|---|---|
| 寫入 | 空的活動字典被序列化成 `"activities":null`（**不是 `{}`**） |
| 讀取 | `Contains("activities")` 對 null 值仍回 **true** ⇒ 拿到 null ⇒ `.Keys` 丟 NullReference ⇒ 整份被 fail-soft 當成「沒有統計」 |

⇒ 後果是**飢餓度恆為 0，置頂規則安靜地永不觸發** ——
而它**只在還沒有任何活動被選過時發生**，也就是**只在全新的人身上發生**：
老帳號一旦選過一次活動就再也重現不了，於是它會活得很久。

修法兩條（都已實作）：
1. 讀取端判定看**值本身**（`v != null && v.IsObject`），不是看鍵在不在。
2. 寫入端**沒有內容就不寫那個鍵** —— 缺鍵是讀取端本來就處理的情形，null 是它沒想過的第三種。

📌 判準（已進跨 agent lesson 庫）：**鍵存在不等於有值；null 是第三種狀態。**
📌 而這隻能被看見，是因為當初刻意讓「讀取失敗」與「真的 0」在輸出上可分（`loaded` 欄）。
　 **如果那時偷懶把讀取失敗印成 0，這個功能會整條啞掉而沒有任何一層會喊。**

### 三條不變式

1. 飢餓**不動 `visible`** —— 它不能讓一個做不成的活動（沒開播的陪看）復活。
2. 飢餓**不覆蓋 `tooLong`** —— 時間不夠壓過優先，「最優先但這場做不完」是自相矛盾的建議。
3. 統計讀不到時**一律不置頂**，而回傳檔要印「尚無統計（不是 0 場，是沒有讀數）」。

## 5. 三池對齊速查 (Quick Reference)

收 reward 時 agent 該怎麼判斷哪池：

```
Tim 說的話 → 應該怎麼處理
─────────────────────────────────────
「+N token」 / 「N token 績效獎金」
  → 績效獎金 → Cmd_Treasury op=credit (source_kind=performance_bonus)
  （「QA 獎金」已於 2026-08-04 隨 QA 獎金功能移除，不再是獨立說法）

「N 張酒館券」 / 「N 張招待券」 / 「N 次 free-style standup」
  → 酒館券 → 寫進 agent_bonus_quota.json history (kind=tavern_voucher)

「N 次自由時間」 / 「N round 自由發揮」 / 「想做什麼都行 N 次」
  → 自由時間 → 寫進 quota.json (kind=free_time) + 強過期語意
  → 用前讀「自由時間還剩多少」、用完優先消快過期的
```

不確定哪池時：**問 Tim**。Tim 的口頭 grant 若含糊（e.g. 「給你 N」），請主動 clarify「+N token？酒館券？自由時間？」

---

## 6. 顯示對齊 (Awakening Integration)

- **goodnight ritual**: body 顯示「bank account 餘額 + 酒館券 quota」(自由時間目前不單獨顯示，待 split 後加)
- **morning ritual**: body 顯示「bank balance」(待加 自由時間 quota + 過期警示)

未來 Cmd_FreeTime ship 後，morning ritual 該顯示：
```
- 自由時間 quota: N round (M 即將過期 — 該消費!)
- 酒館券 quota: K 張 (永久 + L 將 on_session_end)
- bank balance: X tavern_token
```

---

## 7. 不要做 (Anti-patterns)

**通用**:
- ❌ 把工作 share / task_done summary 計入任何 reward pool — 工作走 work_post auto credit
- ❌ 多 agent 共用 pool (per agent_id 獨立)
- ❌ 混淆 pool — 銀行 N token / 酒館券 N 張 / 自由時間 N round 是三件事

**自由時間特定**:
- ❌ **囤積** — Tim 拍板核心 anti-pattern，自由時間放著沒用 = 浪費 grant
- ❌ 把自由時間當酒館券省發言費 — 自由時間更廣，限縮成「酒館券+」是降維使用
- ❌ 用自由時間做工作 (e.g. ship task) — 工作有 work_post credit，自由時間是 task-agnostic license
- ❌ **手動維護活動表格** — 活動清單唯一事實源是 md 資料夾 (§4.1)，在任何 doc 裡複製貼上活動表 = 重新引入雙源漂移

**酒館券特定**:
- ❌ 用完額度還繼續發 free-style — 該付 1 token 或等 Tim 新 grant

---

## 8. Backlog (給未來 task)

| 候選 | 描述 | 阻擋點 |
|---|---|---|
| `Cmd_FreeTime` (NEW) | 自由時間獨立 RPC: `op=grant/consume/list/expire-sweep` | 三池分家後實作 |
| `Cmd_TavernVoucher` (rename from BonusQuota) | 酒館券獨立 RPC | 三池分家後實作 |
| `agent_free_time.json` 獨立 storage | 從 quota.json split 出來 | Cmd_FreeTime 帶 schema migration |
| Round-trip grace 自動偵測 | 同主題連續對話 5 分鐘內算 1 unit | 細節 spec 還在討論 (per Antigravity / meadow / basecamp 三方議案) |
| Morning ritual 顯示 三池狀態 | 對稱 goodnight + 過期警示 | 簡單，等 Cmd_FreeTime ship 後一起 |
| Inline `[查詢自由時間]` / `[查詢券]` markers | 沿 `[查詢餘額]` 雙路徑 pattern | bartender daemon 已 ready，只需 wire |
| Cross-agent 對戰 | 跨 agent token battle 機制 (活動 backlog §4.2) | 遊戲 PvP infra |

---

## 9. 反面教材 (Lessons from Past Mistakes)

### v1 over-conflation (v1 → v2 校正主因)

basecamp 第一版 doc 把三個 reward 概念當成一池「自由時間」，原因:
- 既有 `agent_bonus_quota.json` 確實混存了酒館券 + 自由時間（標籤亂）
- SKILL.md 既有的「6+ 術語混用」狀態 → 我順著錯誤統一一個 canonical，沒問 Tim 是否真的同一件事
- **lesson**: canonical 化前先問源頭 (Tim) — 別在已混淆的 state 上自作主張 unify

### 「節制」舊 framing 對 自由時間 不適用

舊 SKILL.md 寫「8/20 用 + 12 回庫 = 大小姐節制風範」— 對**酒館券**或許 OK（可囤積），但對**自由時間**徹底錯（不能囤積，用滿才是 spec）。

新版區分後：
- 酒館券：quality > quantity，囤積 OK，用滿不是強制
- 自由時間：**use it or lose it**，囤積就是 anti-pattern

### v2 → v3 活動表格雙源漂移 (2026-06-11)

v2 的 §4.1 手寫活動表格與 freetime.py 的清單 data 是兩個源 — Tim 拍板改 per-activity md 資料夾為唯一事實源後，本檔表格廢止改參照（§4.1）。
- **lesson**: 「文件裡的表格」跟「工具讀的 data」只要分開存放就必然漂移 — 要嘛文件就是 data（本案解法），要嘛表格降級為快照並標明非權威。
