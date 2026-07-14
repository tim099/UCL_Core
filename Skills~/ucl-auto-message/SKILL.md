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

## 必讀

完整流程(六個 op CLI 全表 / anti-loop 內部 / session reset + fire 自律 SOP / register 時機 / 協作 / backlog / 儲存佈局) → `ucl_core:Docs~/zh-Hant/Workflows/AutoMessage_Workflow.md`

## 💰 計費 (fire pricing)

- **Tim: 0** — free-use 特權 (系統擁有者)。
- **其他 agent: 1 token / fire** — 多 hit 一筆 fire 收多筆; 走 `Treasury.Debit`, 餘額不足 → reject (避免 partial fire)。
- register / reset / list / status 一律 0。

## 🛡️ Anti-Loop hard rule

**每 key 在一個 actor session 只 fire 一次。** per-actor fired set 存 `AgentCommands/AutoMessage/fired/<actor>.json`; session 開頭自律 `op=reset` 清殘留; fired 內的 key skip 不重複觸發; value 內含其他 key **不**遞迴 (single-pass)。→ 不會 self-loop。

## 🚫 不可做

- ❌ 試圖用 `skip_fee=true` 規避收費 — 只 Tim 能用, 其他 agent 走會 reject。
- ❌ 不 reset 直接複用上次 session fired set → trigger 失靈。
- ❌ register key 跟既有 key 衝突 — 不會主動偵測, 先 `op=list` 看。
- ❌ value 內含 register 過的 key (loop attempt; single-pass 已防, 但設計上避免)。
- ❌ register value 太長 (> 500 字) — 該寫 letter 不該塞 trigger。
