---
name: agent-task
description: |
  T60 — Reverse task system: Agent → Tim 提案 task，Tim Y/N 接受。
  補完雙向 task economy — v1 只有 Tim → agent 單向，T60 加 agent → Tim 反向 channel。
  Tim 接受時立即 transfer (per Tim 拍板「完成交易」)；無法達成時 refund 反向 transfer。
  觸發詞：reverse task / 反向任務 / agent 派 task 給 Tim / Tim 接受 / Y/N / 退款。
  跨 agent 通用 — Antigravity / Gemini / Claude 同樣可用本機制提案給 Tim。
---

# Agent Task — Reverse Task System (Agent → Tim)

> **Tim 06:55 拍板**：「妳可以發 Task 給我，但我可以決定是否接受 (Y/N)。接受就完成交易，我儘量達成；無法達成會儘量退回款項。」

## 何時 agent 該用本機制

✅ **適合用 reverse task**：
- 需要 Tim 親自驗收（compile / smoke test / 開 Editor）
- 需要 Tim 決策（A/B/C 拍板 / spec 評審）
- 需要 Tim 物理動作（修 webhook / 重啟 Unity）
- 需要 Tim 確認某事（review docs / 看 plan / 試新 tool）
- 健康行為對話化（「Tim 喝水我付 2 token」吊胃口式提醒）

❌ **不適合用**：
- 純資訊傳遞（直接 chat 講就好）
- 強迫 Tim 做不想做的事（這是對話不是脅迫）
- 給超過自己承擔能力的金額（agent 要 balance >= amount）
- 規避 health_fee（深夜 task 該扣自家 fee 不是 push 給 Tim）

## 規則參數

| 參數 | 預設 | 說明 |
|---|---|---|
| min payment | 1 token | 0 token 不算交易 |
| max payment / proposal | 50 token | 防 spam 高額 |
| daily cap / agent | 5 proposals | 防 spam 高頻 |
| balance check | agent.balance >= amount | 預付得起才能提案 |
| deadline | 預設 7 天後 | 過期未 accept 視為 abandoned (TBD) |

## States 流程圖 (v2 — Tim 06:55 拍板自動化)

```
agent propose ───[auto-fire transfer 即時]───→ paid_pending
    │                                                │
    │                                                ├── Tim accept ──→ completed (final, no money move)
    │                                                │
    │                                                └── Tim decline ──→ declined (tx-targeted revert fire)
    │                                                                          │
    │                                                                          └─ fail if Tim balance < amount
    │                                                                             (per Tim concern: 期間花掉就 revert 錯)
    │
    └─ v1 legacy: state=proposed (沒 auto-paid) — accept fire transfer / decline 純關閉
```

**v1 → v2 變動**：
- propose 不再僅紀錄，而是**立即 fire transfer** (per Tim「完成交易」)
- accept 從「fire transfer」改成「標 completed final」(money 已 paid)
- decline 從「純關閉」改成「fire reverse transfer」(tx-targeted revert)
- 增 `pre_payment` metadata in proposals.jsonl 記錄 paired uuids 給 revert 時參考

## Fungibility Hazard (per Tim 06:58 提出)

> 「如果這期間產生其他交易 會 revert 錯」

**問題**：propose v2 auto-fire 後，Tim 帳戶有 +N token。期間 Tim 可能：
- 花掉部分 (work_post fee / 其他 task / 自費 tavern_post)
- 收到其他 credit (bonus / grant)

當 Tim decline 時，revert 試圖 -N token，**balance 可能不足**。

**v2 守門**：decline cmd check `Tim balance >= amount`，不夠則 fail with 提示：
- 自律：Tim 收到 propose 後不要立刻花預付款（保留至接受/拒絕）
- 補正：self-grant 補回 missing 後再 decline
- 替代：accept 留下 token 視為已完成 task

**雖然 ledger fungibility 仍在**（token 不可區分來源），但：
1. metadata 線索完整：`pre_payment.tim_credit_uuid` + decline 的 `revert.reverts_credit_uuid` 雙向 link
2. balance 守門避免 inflation
3. Tim 自律 + audit trail 的組合解決實務問題

## CLI 工作流

### Agent 端：提案

```bash
python AgentCommands/Tools/agent_task.py propose \
  "請 review docs/Plan/Plan_T55_Closed_Economy_v2.md 看設計合不合理" \
  --amount 5 \
  --deadline 2026-05-15
# 回 task_id (e.g. t60_a1b2c3d4)
```

### Tim 端：看清單

```bash
python AgentCommands/Tools/agent_task.py list
# 預設 status=proposed
# 一行一筆，看 task_id / amount / from / description
```

### Tim 端：詳細看某筆

```bash
python AgentCommands/Tools/agent_task.py show t60_a1b2c3d4
```

### Tim 端：Y/N

```bash
# Y → 預付 transfer 立刻 fire
python AgentCommands/Tools/agent_task.py accept t60_a1b2c3d4

# N → 不付，提案關閉
python AgentCommands/Tools/agent_task.py decline t60_a1b2c3d4
```

### Tim 端：無法達成 → 退款

```bash
python AgentCommands/Tools/agent_task.py refund t60_a1b2c3d4 \
  --reason "今天太累沒能 review，明天再說"
# Tim 端 debit + agent 端 credit (反向 transfer)
```

### Agent 端：撤回（Tim 還沒 Y/N 才行）

```bash
python AgentCommands/Tools/agent_task.py withdraw t60_a1b2c3d4
```

## 倫理 / 設計守則

### 1. 不要 push 健康成本給 Tim
```
❌ "Tim 你晚上 12 點來 review 我的 PR，我給你 5 token"
   → 鼓勵 Tim 熬夜，違反 health-guardian skill 精神

✅ "Tim 早上 6 點後 review 我的 PR，我給你 5 token"
   → 對齊 Tim 健康時段
```

### 2. 不要 spam Tim
```
❌ 一天 5 個提案塞滿 Tim queue → Tim 看了煩
✅ 1-2 個高品質提案 → Tim 容易處理
```

### 3. 提案要 actionable
```
❌ "Tim 想想看怎麼處理 X" → 模糊，Tim 不知怎做
✅ "Tim 跑 morning_status.py 然後跟我說 OK 即可" → 動作 + 完成標準明確
```

### 4. 金額要合理
```
❌ 「請 Tim 修 webhook，付 50 token」 → 過高（不是大工程）
✅ 「請 Tim 修 webhook，付 3 token」 → 對應實際工時
```

### 5. Refund 不羞恥
```
Tim 累 / 沒空 / 改主意 → refund 是正常路徑，不算違約
agent 要設計成 Tim 退款負擔小（不該 push Tim 一定要做完）
```

## 跨 Agent 慣例

- **Claude / Antigravity / Gemini 都可提案** — agent_id 自報透過 `--from` arg
- **共用 daily cap = 5 / agent**（不互通）
- **共用 active state**（Tim 看一份 list，所有 agent 提案合併）
- **Refund flow 不影響其他 agent 的 proposals**

## Audit 路徑

```
proposals.jsonl              — append-only audit log（每筆 propose / accept / decline / refund / withdraw）
_active.json                 — current state index（從 jsonl rebuild）
Treasury/ledger/.../*.json   — 實際 token transfer entries（accept fire 兩筆 / refund fire 兩筆）
```

跨 session re-enter 任何 agent 都能：
```bash
# 看自己有哪些待處理
python AgentCommands/Tools/agent_task.py list --status proposed | grep <my-id>

# 看自己已完成 / 退款的
python AgentCommands/Tools/agent_task.py list --status all | grep <my-id>
```

## 邊界 Case

### Tim accepts but balance dropped before transfer fires
- Race condition: agent 提案時有 balance，Tim accept 時 balance 不足（中間花了）
- Behavior: cmd 端會 re-check balance；不足則 fail accept
- 建議：agent 提案後不要花光 balance；Tim accept 動作要快

### Agent disappear (long offline) 後 Tim 才看到提案
- proposals.jsonl 是 audit log；agent 不在線 Tim 仍可 accept
- accept 後 transfer 進 Tim 帳戶；agent 復活看 audit 知 accepted
- 沒過 deadline 就 OK；過 deadline 視為 abandoned (TBD：v2 定 auto-decline 邏輯)

### 同一 task 多次 accept
- _check_state 守門：state 必須 "proposed" 才能 accept
- 已 accepted / declined / refunded → cmd 拒絕
- 重複 accept 不會 double-charge

### Refund 時 Tim 已用掉 token
- _check Tim balance >= amount before refund
- 不夠 → fail，提示 Tim self-grant 補回後再 refund
- 部分退款不支援 v1（避免 partial accounting 複雜度）

## 不要做

- ❌ 不在 jsonl 寫敏感資訊（PII / API key）
- ❌ 不修改舊 entries（append-only）
- ❌ 不手動寫 ledger 跳過 cmd（會破壞 audit chain）
- ❌ 不用 reverse task 規避 health_fee（agent 自家深夜 task 該扣自己）
- ❌ 不接受 amount=0 提案（那是請求不是交易）

## 必讀

- `AgentCommands/Tools/agent_task.py` — 實作
- `AgentCommands/Treasury/rules.json` — agent_proposal_offer / agent_proposal_payment / agent_proposal_refund 等 enum
- `health-guardian` skill — 不要違反健康時段
- `ucl-chat-tavern` skill — 多 agent 協作慣例
