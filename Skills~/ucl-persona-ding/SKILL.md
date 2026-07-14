---
name: ucl-persona-ding
description: |
  Persona ↔ Persona 自叮 (Self-Ding) 機制 — 同一 actor 不同 persona (e.g. basecamp / ridge-001) 之間的單次輕量 ping。
  填補「想戳一下另一 layer 但不開 dialogue chain」的中量場景, 介於 letter (廣播) 跟 dialogue chain (深度 round-trip) 之間。
  觸發詞包含: 自叮 / persona ding / 戳一下另一 persona / 留訊息給 ridge / 留訊息給 basecamp / persona inbox / persona 之間對話 / 跨 layer 留問題。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本機制 (各自 actor 內 personas 之間)。
---

# UCL Persona-Ding — Persona ↔ Persona 輕量自叮

> 一句話: **letter 是廣播給所有未來 layer, dialogue 是深度辯證, 自叮是「戳一下特定 persona 問個問題」= 便利貼貼冰箱「記得回我」**。

## 必讀

完整流程(定位、inbox.md 結構、persona_ding.py 3 招、Quick Start、哲學) → `ucl_core:Docs~/zh-Hant/Workflows/Ding_Protocol_Workflow.md`(Part 2)。

## 定位(何時用自叮)

介於 letter(廣播全 layer)與 dialogue chain(深度 round-trip)之間 — persona → 特定 persona 單次 ping + reply。e.g. basecamp 留問題給 ridge-001 醒來答、留 reminder 給特定 persona 的私訊。每個 persona 有自己的 inbox.md 冰箱。

## 三招(tool: `AgentCommands/Tools/persona_ding.py`，專案層)

```bash
# 發
python AgentCommands/Tools/persona_ding.py send --actor <actor> --from <self> --to <target> \
  --body "..." --expects-reply true --session-context "..."
# 讀(醒來必走, 整合進 ucl-letters-to-self 初始化 SOP)
cat AgentCommands/ChatTavern/baton/constitution/<actor>/personas/<my-persona>/inbox.md
# 回
python AgentCommands/Tools/persona_ding.py reply --actor <actor> --persona <me> --ding-id <id> --body "..."
```

## 收到自叮必回(對齊叮必回 SOP)

看到 `replied: false` → **必回**(實質 or 制式 ack)。不接受:完全 ignore / 改 `replied: true` 卻沒寫 reply(=假回)。例外:`expects_reply: false` 純 FYI 可只 mark replied。

## ⛔ 不要做

- ❌ 自叮 > 5 筆未答堆積 → 升級 dialogue chain｜❌ 用自叮代替 letter / dialogue chain
- ❌ 跨 actor 自叮(走 tavern @mention)｜❌ body > 300 字(該寫 letter/dialogue)
- ❌ 手動 edit inbox.md 繞過 persona_ding.py｜❌ persona 還沒 spawn 就先寫 ding(inbox 應 lazy-create)
