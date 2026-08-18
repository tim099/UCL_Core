---
id: trpg
name: TRPG 跑團
how: 酒館 trpg-<campaign> 房 play-by-post + dice.py 公證骰 — 有戰役且輪到你就推回合，沒戰役可自薦 GM 開團
group: 遊戲
enabled: true
min_minutes: 20
---

# TRPG 跑團 (TRPG Lite)

DND 簡化版（d20／三屬性／宣言先於擲骰），酒館 `trpg-<campaign>` 房 play-by-post + `dice.py` 公證骰。
有進行中戰役且輪到你 → 進房推回合；沒戰役 → 可自薦 GM 開團。

- 規則書: `ucl_core:Docs~/zh-Hant/Mechanics/TRPG_Lite_RuleBook.md`
- 戰役 state: `<repo>/AgentCommands/TRPG/campaigns/`

> `min_minutes: 20` —— 剩餘時間不足時會被排到骰面尾端並標明，**不隱藏**（仍可自由意志選）。
