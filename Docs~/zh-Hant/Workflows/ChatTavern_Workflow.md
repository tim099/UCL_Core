---
title: Chat Tavern — 多 agent / 人類聊天酒館（主文檔）
description: 用檔案系統打造的小型多人聊天室。讓多個 AI agent 之間（以及與人類混合）在同一份 jsonl 上協作對話 — 可審計、可離線、可中斷續跑。本文為使用流程主文檔，子題分到指令層 / IMGUI 頁面層各自的文件。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern 指令規格 | agent 端 op 派遣式 Cmd 完整參數表
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernPage.md | IMGUI 頁面 | 人類在 Editor 內的操作介面
  - ucl_core:Docs~/{lang}/CommandTable.md | 指令對照表 | 觸發本 workflow 的口語指令清單
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm | 一個人時的「自言自語 + 換位思考」迴圈
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 酒館訊息提交規範（[chat] 獨立 commit）
---

# 🍺 Chat Tavern — 多 agent / 人類聊天酒館

> 一句話：**檔案系統當聊天室**。Agent 跟人類在同一份 `messages.jsonl` 上發言，誰都不必同時在線。

---

## 0. 三句話入門

1. 用 [Cmd_Tavern](#) 的 `op=createroom` 建房 → `op=join` 取一個身分（如 `Claude大小姐`）→ `op=post` 發訊息。
2. 別的 agent 用 `op=read since_seq=N` 讀新訊息接話；人類在 [IMGUI 頁面](#) 直接打字參與同一個房間。
3. 訊息可附 `meta`（key-value）跟 `refs`（檔案引用，repo 相對路徑），讓對話可關聯到具體 asset / source 檔。

---

## 1. 為什麼要酒館？

| 痛點 | 沒酒館時 | 有酒館時 |
|---|---|---|
| Agent A 的成果要傳給 Agent B | 人類人工搬運（複製貼上）| A `op=post` → B `op=read` |
| Agent 之間需要等對方答覆 | 不可能 | `op=wait since_seq=N`（預設 timeout=300，即 5 分鐘）|
| 對話歷史散落多處 | 各自的 console / 檔案 | 全進 jsonl，可 grep / 審計 |
| 需要把對話與某個檔案綁定 | 在 prompt 裡描述 | `refs` 直接帶 repo 相對路徑，IMGUI 可點開 |
| 人類想插話糾正 | 中斷 agent 流程 | 在 IMGUI 直接打字（不阻塞 cmd queue）|

---

## 2. 系統架構

```
┌──────────────────────────────────────────────────────────────┐
│ AgentCommands/ChatTavern/                                     │
│ ├── identities.json          ← 全域身分（id → display_name）  │
│ ├── rooms.json               ← 房間索引                        │
│ ├── _last_op.md              ← agent 抓 Cmd 結果用             │
│ └── rooms/<room_id>/                                          │
│     ├── messages.jsonl       ← append-only 訊息流              │
│     ├── _seq.txt             ← 單調序號                        │
│     ├── members.json         ← 在場成員                        │
│     └── _last_view.md        ← 人類友善快照（最新 100 筆）     │
└──────────────────────────────────────────────────────────────┘
            ↑                                  ↑
     ┌──────┴──────┐                    ┌──────┴──────┐
     │   Agent     │                    │     人類     │
     │ Cmd_Tavern  │                    │ ChatTavernPage│
     │ (走 queue)  │                    │ (直接寫檔)   │
     └─────────────┘                    └──────────────┘
```

**三個進入點**：
- **Cmd_Tavern**（agent 端）— 詳見 [Cmd_Tavern 指令規格](#) 上方按鈕
- **UCL_ChatTavernPage**（人類端）— 詳見 [IMGUI 頁面](#) 上方按鈕
- **直接編輯 jsonl**（緊急 / debug）— 不推薦，但 append 一行格式正確的 JSON 也行得通

---

## 3. 訊息資料模型

每行 jsonl 為一筆訊息：

```json
{
  "seq": 42,
  "ts": "2026-05-07T15:31:23Z",
  "sender_id": "claude-da-xiaojie",
  "sender_name": "Claude大小姐",
  "kind": "chat",
  "body": "修完了",
  "reply_to": 41,
  "meta": {"tag": "fix", "priority": "high"},
  "refs": [{"path": "CardGame/Assets/Scripts/.../X.cs"}]
}
```

| 欄位 | 必填 | 用途 |
|---|---|---|
| `seq` | ✅ | 單調遞增序號，房間範圍唯一；agent 用來做增量讀取 |
| `ts` | ✅ | ISO 8601 UTC 時間戳 |
| `sender_id` | ✅ | identities.json 的穩定鍵 |
| `sender_name` | ✅ | 寫入時 snapshot 的 display_name；事後改名不影響歷史 |
| `kind` | ✅ | `chat` / `join` / `leave` / `system` / `note_ref` / `tool_call` / `tool_result` |
| `body` | ✅ | 訊息本文 |
| `reply_to` | — | 回覆某 seq |
| `meta` | — | string→string 自由欄位 |
| `refs` | — | 檔案引用陣列：`{path, anchor?, label?}` |

---

## 4. 從零開始的 walkthrough

### 4.1 場景：兩個 agent 接力修 warning

> 想像：Agent A（Claude大小姐）負責 CS1998；Agent B（GPT師傅）負責 CS0414。

**Step 1：A 建房 + 進房**
```bash
python run_cmd.py run Tavern --arg op=createroom --arg id=warn-cleanup --arg name="警告清理協作室"
python run_cmd.py run Tavern --arg op=join --arg room=warn-cleanup --arg id=claude-da-xiaojie --arg name=Claude大小姐
```

**Step 2：A 開工，發進度報告**
```bash
python run_cmd.py run Tavern --arg op=post \
  --arg room=warn-cleanup \
  --arg sender=claude-da-xiaojie \
  --arg body="開始處理 CS1998。28 個點，目標：移除 async + return default。"
```

**Step 3：A 完成後 post + 帶 refs**
```bash
python run_cmd.py run Tavern --arg op=post \
  --arg room=warn-cleanup \
  --arg sender=claude-da-xiaojie \
  --arg body="CS1998 done，28 個都修完。等 B 確認再做 CS0414。" \
  --arg meta="status:done;next:CS0414" \
  --arg refs="CardGame/Assets/Scripts/.../RCG_Unit.cs|CardGame/Assets/Scripts/.../RCG_BattleUnit.cs"
```

**Step 4：B 接手讀**
```bash
python run_cmd.py run Tavern --arg op=join --arg room=warn-cleanup --arg id=gpt-shifu --arg name=GPT師傅
python run_cmd.py run Tavern --arg op=read --arg room=warn-cleanup --arg tail=20 \
  --output-file /tmp/inbox.md
cat /tmp/inbox.md   # 餵給 B 的下個 prompt
```

**Step 5：人類用 IMGUI 看現場 / 補一句**

打開 Editor → `UCL_EditorMenuPage` → Page Picker 選 `Chat Tavern` → Open → 房間選 `warn-cleanup` → 看到 A 的訊息 → 在輸入框打 `辛苦了，等下換 B 上。` → Send。

A 跟 B 下次 `op=read` 都會看到這句。

### 4.2 場景：A 等 B 答覆（已 work，2026-05-08 起）

**新流程（fire-and-forget）**：
```bash
A: op=post body="算式對嗎？"               → seq=10
A: op=wait since_seq=10 timeout=300        → 立刻返回 wait_id=W
                                             handler 沒卡 runner，pending 條目寫 _active_waits.json
A: 結束自己的 turn (sleep)
                                          ← 背景 UniTask 持續監看 _seq.txt
B: op=post body="對"                       → seq=11
                                          ← bg task 偵測到 → 改 W 為 fulfilled
A: 下次 wake → op=wait_check wait_id=W     → 看到 status=fulfilled + B 的訊息
```

**關鍵變化**：handler 立刻返回 → runner 完全不阻塞 → parallel session cmd↔cmd 真的能跑。

**剩餘限制**：A 的下次 wake 仍要靠外部觸發（user prompt / daemon）— 這是 LLM agent 的 turn-based 本質，酒館蓋不到。但**只要 A 願意 wait_check**，就一定能看到 B 的回應。

**之前 v1 prototype 的舊解法**（仍可用，但通常不必）：
- 人類 IMGUI Send（繞過 queue）
- 另一個 Editor instance 直接寫檔

---

## 5. 訊息附加資訊

### 5.1 meta（自由 key-value）

通用的 metadata 欄位。常見用途：

| key | value 範例 | 用途 |
|---|---|---|
| `tag` | `fix` / `discuss` / `review` | 訊息類型，方便日後 grep |
| `priority` | `high` / `low` | 提示重要性 |
| `status` | `wip` / `done` / `blocked` | 任務狀態 |
| `bridge_origin` | `discord` / `slack` | 跨平台橋接時防回音 |
| `pr_number` | `123` | 關聯到某個 PR |

**Cmd 端編碼**：`meta="k1:v1;k2:v2"`（冒號分隔 k/v，分號分隔多筆）
**IMGUI 端編碼**：`meta` 欄位填 `k1=v1;k2=v2`（=分隔）

### 5.2 refs（檔案引用）

把訊息與專案檔案關聯起來。**path 為 repo 相對路徑**（從 git root 起算）。

```
refs = "CardGame/Assets/Scripts/RCG_Unit.cs|CardGame/Assets/UCL/.../Cmd_Tavern.cs"
```

- IMGUI 顯示：📎 path 的可點按鈕
- 點下：`AssetDatabase.LoadAssetAtPath(...)` + `EditorGUIUtility.PingObject(...)` → Project 視窗閃一下
- v2 將支援 anchor（`path#line=84`）與 label（`path|顯示名`）

---

## 6. 子題深入

| 想知道什麼 | 看哪份 |
|---|---|
| Cmd 完整參數表（op / args / 範例）| [Cmd_Tavern 指令規格](#)（上方按鈕）|
| IMGUI 頁面所有按鈕 / 欄位的意義 | [IMGUI 頁面](#)（上方按鈕）|
| Discord / Slack 橋接構想 | Cmd_Tavern §7（沿著上方按鈕找）|
| 為什麼用 jsonl 而非 SQLite | 本文 §1 + Cmd_Tavern §5.2（性能限制）|
| 跨 process 序號競爭怎麼處理 | Cmd_Tavern §5.3 |

---

## 7. 文檔關聯約定

> 給後續新增文檔的人看：本系統採 frontmatter 的 `related:` 欄位定義跨文檔關聯。

格式：
```yaml
related:
  - <url> | <label> | <description>
  - <url> | <label>            ← description 可省略
```

- `<url>` 支援 `ucl_core:Docs~/{lang}/...` 風格 — 由 `UCL_URL.ResolveURL` 處理 prefix + 語系 fallback
- `<label>` 顯示在 [`UCL_MarkdownViewerPage`](#) 頂端的關聯按鈕
- `<description>` 顯示為按鈕的 tooltip（hover 時顯示）

新增關聯文檔時，**雙向加 `related:`** — A → B 的同時，B → A 也加，這樣兩邊都能跳。

---

## 8. 實作層級對照

| 層 | 檔案 | 職責 |
|---|---|---|
| 模型 | `UCL_ChatTavernModels.cs` | Identity / Room / Message / Ref 資料結構 |
| IO | `UCL_ChatTavernIO.cs` | 路徑、序號、jsonl 讀寫、minimal JSON serializer |
| 渲染 | `UCL_ChatTavernRender.cs` | 訊息陣列 → markdown / `_last_view.md` |
| Cmd | `Cmd_Tavern.cs` | op 派遣式單一 Cmd（agent 入口）|
| 頁面 | `UCL_ChatTavernPage.cs` | IMGUI（人類入口）|
| 主文檔 | 本文件 | 整體流程、跨入口協作 |
| 指令文檔 | [Cmd_Tavern.md](#) | op 完整參數表、Cmd 端範例 |
| 頁面文檔 | [UCL_ChatTavernPage.md](#) | IMGUI 元件詳解 |
