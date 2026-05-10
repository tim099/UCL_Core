---
name: qa-bug-reward
description: |
  T68 — Tim QA 工作獎勵 skill：Tim 確認真 bug 時 agent 拍板 grant reward token (mint pattern, 勞動所得)。
  agent 自由意志決定是否真 bug vs feature request / spec 誤解。
  severity tier: trivial(1) / normal(3) / critical(5) / catastrophic(10)。
  CLI 工具: AgentCommands/Tools/qa_bug_reward.py (agent 操作，不是 Tim 自己跑)。
  觸發詞包含：QA reward / bug 獎勵 / 確認 bug / 找到 bug / bug confirmed / QA 完成 / 勞動所得 / debug 獎勵。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可拍板給 Tim QA 獎勵。
---

# QA Bug Reward — Tim 確認 bug 的勞動獎勵

> **Tim 08:34 拍板**：「以後我完成 QA 工作時 (確認確實是 Bug 而非規格理解錯誤) 妳可以給我打獎勵 token (憑空生成 算是勞動所得 是否完成交由大小姐決定)」

## 什麼時候 fire

當 Tim 找到並**確認**一個 bug：
- ✅ code 行為不符 spec / rules.json / SKILL.md
- ✅ UX / audit / data 真的被影響
- ✅ 可重現（清楚 repro steps）
- ✅ 非重複既有 bug

**不**該 fire：
- ❌ Feature request / improvement (e.g. "我想要加 X 功能" 非 bug)
- ❌ Spec 誤解 (Tim 沒讀 docs / 規則記錯)
- ❌ Phase B 已知未實作 (不算 bug 算 backlog)
- ❌ Tim 自己沒驗證的猜測 (要先確認再 grant)

## Severity Tiers (per rules.json)

| 嚴重度 | Token | 範例 |
|---|---|---|
| **trivial** | 1 | typo / cosmetic / minor display issue / 內部 path bug |
| **normal** | 3 | functional issue / UX bug / ledger 錯漏 / broadcast 失敗 |
| **critical** | 5 | data loss / security / crash / 跨 session 影響 |
| **catastrophic** | 10 | major economic state corruption / repo-wide breakage |

## Agent 拍板原則

agent 拍板 4 個維度：

1. **Reality**: 是真 bug 還是 spec 誤解？
   - Tim 描述 vs SKILL.md / rules.json / Plan_X 文檔比對
   - 不一致 → bug；一致但 Tim 期望不同 → feature request
2. **Reproducibility**: 可重現嗎？
   - 1 次見過的 transient 不算（要 repro）
   - 但 critical bug 一次就夠（不能要求 reproduce 再 crash 一次）
3. **Severity**: 影響範圍多大？
   - 影響 1 個 entry / 顯示 → trivial
   - 影響 1 天 / 1 module → normal
   - 跨 session / 多 agent → critical
   - 經濟系統 corruption → catastrophic
4. **Novelty**: 重複的 bug 不再 grant
   - 同 bug 已 grant 過 → skip
   - 同 bug 不同表現 → 視為新 bug 可 grant

## CLI 用法

### Agent grant 獎勵 (本小姐拍板用)

```bash
python AgentCommands/Tools/qa_bug_reward.py grant \
  --severity normal \
  --description "Discord 太平洋標準銀行 餘額顯示 None → None" \
  --bug-ref "T67"
```

行為:
1. 寫 ledger entry (Tim credit qa_bug_confirmed +N token by severity)
2. fire_broadcast() → Discord 太平洋標準銀行 通知 Tim
3. 自動 backfill balance_before/after (T67)

### List 今日獎勵

```bash
python AgentCommands/Tools/qa_bug_reward.py list
```

### Severity 對照 + 範例

```bash
python AgentCommands/Tools/qa_bug_reward.py severity-table
```

## 範例對話流

```
Tim: 「大小姐 餘額好像顯示 None 不對」

claude (本小姐):
1. 確認 bug (檢查 entries / Discord 截圖)
2. 判定 severity = normal (UX 顯示，影響 1 天 broadcast)
3. fix bug (T67)
4. python qa_bug_reward.py grant --severity normal \
     --description "Discord 餘額顯示 None → None" --bug-ref "T67"
5. Tim 收到 +3 token
```

## 防 Mint Farm 設計

agent 拍板權 + auto_grant=false 確保:
- ❌ Tim 不能自己跑 grant (那是 mint farm)
- ✅ agent 必須親自跑 cmd
- ✅ ledger 留 audit (sig_cmd_id / source_ref)
- ✅ Discord 廣播給 Tim 看 — 不能偷偷 mint

跨 agent 信任前提:
- Claude / Antigravity / Gemini 都該誠實判定
- Tim 看 Discord 看到 grant amount 異常 → 可質疑 / 申訴
- 多 grant 同 bug → 違反「Novelty」原則 → agent 信譽問題

## 跨 Agent 慣例

- Claude / Antigravity / Gemini 都可 grant Tim
- 不互相 grant (agent 之間沒這個關係)
- 同 bug 多 agent 都見證 → 任一 agent grant 即可 (先到先得，不重複)
- agent 自己撞到 bug 不該 grant 自己 (那是 commit pre-credit 的事)

## 倫理守則

- ✅ 真 bug 真 reward — 公平誠實
- ✅ severity 對應實際影響 — 不浮報
- ✅ 寫清楚 description + bug_ref — audit trail
- ❌ 不對 feature request grant
- ❌ 不重複 grant 同 bug
- ❌ 不跟 Tim 商量「給多少 token」 — agent 自由意志拍板
- ❌ 不為了討好 Tim 浮報 severity
- ❌ 不在 production 假裝 QA mode

## 跟 T66 QA Mode (戰鬥) 的差異

| 維度 | T66 QA Mode (battle) | T68 QA Bug Reward |
|---|---|---|
| **觸發** | RCG_EditVFX scene 跑 BattleAction | Tim 確認 bug |
| **領域** | 戰鬥操作 | 任何 bug (UI / 戰鬥 / ledger / etc.) |
| **Reward** | Tim +1 / caller +1 each action | Tim +1~10 by severity |
| **拍板者** | 自動偵測 scene name | Agent 自由意志判定 |
| **頻率** | 每 action 觸發 (高) | per bug 一次 (低) |

## 必讀

- `AgentCommands/Tools/qa_bug_reward.py` — CLI 實作
- `AgentCommands/Treasury/rules.json` `qa_bug_confirmed` enum
- `T67 lesson L45` — balance backfill 算法
- `health-guardian` skill — 不要熬夜抓 bug，注意時段 fee
- `agent-task` skill — 跨 agent 提案如「我發現 bug 請 Antigravity 確認」
