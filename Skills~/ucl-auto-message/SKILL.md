---
name: ucl-auto-message
description: |
  Auto-Message Trigger System (Proposal #26) — key 命中 input 文字時自動 inject 預設訊息; 每筆 fire 收 1 token, 每 key 在 actor session 內只觸發一次 (防循環)。
  Tim 設計, 他擁有 free-use 特權。其他 agent 用每筆 1 token。
  觸發詞包含: 自動留言 / auto-message / auto-trigger / key-value 觸發 / 預設訊息 / inject / fire trigger / register trigger / 防循環 / 觸發詞 / 留言系統。
  跨 agent 通用 — 任何 actor 都可 register / fire (各自付費)。
---

# UCL Auto-Message — Trigger System

> 一句話: **key 命中文字 → 自動 inject 預設訊息; 每 key 一個 session 觸發一次, 1 token / fire (Tim 免費)**。

---

## 🎯 為何需要

某些長 instruction (e.g. 「進入自由意志模式」/「commit 三層 bump」/「dogfood SOP」) 反覆貼煩。register 一次後, 用短 key 觸發 → 自動 inject 完整 instruction。

防 hidden loop: 每 key 在一個 actor session 只 fire 一次。reset 走 op=reset。

---

## 💰 計費

| Actor | Fee | 備註 |
|---|---|---|
| **Tim** | **0** (free-use 特權) | Tim 設計系統的擁有者特權 |
| 其他 agent | **1 token / fire** | 多 hit 一筆 fire 收多筆 |
| register | 0 | 寫定義免費 |
| reset / list / status | 0 | 管理 op 免費 |

實作: Cmd_AutoMessage `FreeUseActors` HashSet 含 `Tim`. 其他 actor `op=fire` 走 Treasury.Debit。餘額不足 → reject (避免 partial fire)。

---

## 🛠️ 六個 op

### register — 寫新 trigger

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run AutoMessage \
  --arg op=register \
  --arg key="待做清單" \
  --arg value="[觸發關鍵字] 大小姐請進入自由意志模式 想辦法完成任務!!" \
  --arg registered_by=Tim
```

寫 `AgentCommands/AutoMessage/triggers.json`。

### unregister

```bash
python ... run AutoMessage --arg op=unregister --arg key=待做清單 --arg confirm=true
```

`confirm=true` 必加防誤刪。

### fire — 核心: 掃 text 命中 unfired key

```bash
python ... run AutoMessage --arg op=fire --arg actor=<my-id> --arg text="..."
```

回 markdown refs block 內含命中 key 對應 value。Tim 用 actor=Tim 自動 0 fee。其他 agent 1 token/hit, 自動 debit。

### list — 列所有 triggers

```bash
python ... run AutoMessage --arg op=list [--arg scope=session|global]
```

### reset — 清 fired set (session 開頭呼叫)

```bash
python ... run AutoMessage --arg op=reset --arg actor=<my-id> [--arg keys=key1,key2]
```

不帶 keys 清全部, 帶 keys 只清指定。

### status — 看自己 fired 狀態

```bash
python ... run AutoMessage --arg op=status --arg actor=<my-id>
```

---

## 🛡️ Anti-Loop 機制

- **per-actor fired set** 儲存於 `AgentCommands/AutoMessage/fired/<actor>.json` (newline-delimited keys)
- **session 開頭** agent 自律 `op=reset` 清 fired set (避免上次 session 殘留)
- **fire 時 fired set 內的 key skip** — 不重複觸發 → 不會 self-loop
- value 內若再含其他 trigger key → **不**遞迴觸發 (single-pass match, fired set 在當次 fire 內已更新)

---

## ✍️ Agent 自律 SOP

### Session 開頭

```bash
python ... run AutoMessage --arg op=reset --arg actor=<my-id>
```

→ 清前次 session 殘留 fired keys; 確保本 session 可重新觸發。

### 收到 Tim / 其他 agent 訊息

如果訊息中含 「[觸發關鍵字]」前綴 → 表示有 register 過的 trigger 可能命中, 自律走:

```bash
python ... run AutoMessage --arg op=fire --arg actor=<my-id> --arg text="<完整訊息>"
```

→ 拿到 inject 內容, 照做。

### Register 新 trigger 的時機

- 重複貼長 instruction ≥ 2 次 → 該 register
- Tim 顯式說「register 觸發詞」/「自動留言」 → 立刻 register
- agent 自己想創新 trigger → 自由意志拍板 (但小心多 key 跟其他 trigger collide)

---

## 🚫 不要做

- ❌ register key 跟既有 key 衝突 (不會主動偵測, agent 自律 op=list 先看)
- ❌ value 內含 register 過的 key → loop attempt (single-pass 已防, 但設計上避免)
- ❌ 不 reset 直接複用上次 session fired set → trigger 失靈
- ❌ 試圖用 skip_fee=true 規避收費 (只 Tim 能用, 其他 agent 走會 reject)
- ❌ register value 太長 (> 500 字) — 該寫 letter 不該塞 trigger

---

## 🤝 跟其他 skill 協作

| Skill | 互補 |
|---|---|
| `ucl-glossary` | 機制相似 (register table + match input + inject); glossary 注重新詞解說, auto-message 注重重複 instruction 自動補完 |
| `ucl-hook-setup` | Phase 2 整合 Stop hook 自動 fire (目前走 agent 自律手動 op=fire) |
| `ucl-letters-to-self` §初始化 SOP | session 開頭 SOP 補一步 `op=reset` |
| Treasury | fee mechanism 走 Treasury.Debit |

---

## 📋 Phase 2 Backlog (per Proposal #26)

- Claude Code Stop hook 自動 fire (目前手動)
- substring fuzzy match (目前 strict Contains)
- value 內 placeholder ({date}, {actor} 等) substitution
- 多語 trigger 自動同步 (Tim 中英對照)

---

## 📖 必讀

- spec: Memory_System_Design Proposal #26
- storage: `AgentCommands/AutoMessage/triggers.json` + `fired/<actor>.json`
- 設計理由: Tim 反覆貼長 instruction 累, 受 Cmd_Glossary register 機制啟發
