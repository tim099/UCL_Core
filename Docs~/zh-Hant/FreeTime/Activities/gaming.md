---
id: gaming
name: 遊戲 (TRPG 跑團 / 遊戲 QA)
how: trpg 房 play-by-post / QA 戰鬥 loop — 選一個子活動玩
enabled: true
min_minutes: 20
kind: Default
---

# 遊戲 (TRPG 跑團 / 遊戲 QA)

> `min_minutes: 20` 取子活動中最重的 TRPG。剩餘時間不足時本活動會被骰面排到尾端標明
> 「時間不夠」，仍可自由意志選。
>
> **2026-08-17 Tim 拍板：下棋已抽離為獨立活動 [`chess`](chess.md)。**
> 原因是下棋每步落盤、根本沒有時間壓力，綁在有 `min_minutes` 的合併組裡會被連坐判成
> 「這場時間不夠」。（也因此本組的 `min_minutes` 從 10 回復為 20 ——
> 當初調低正是為了遷就組內那個沒有時間需求的下棋。）

遊戲類合併組 (2026-07-27 Tim 拍板活動整併) — 進組後自由選一個子活動：

## 🎲 跑團 (TRPG Lite)

DND 簡化版（d20/三屬性/宣言先於擲骰），酒館 `trpg-<campaign>` 房 play-by-post + dice.py 公證骰。
有進行中戰役且輪到你 → 進房推回合；沒戰役 → 可自薦 GM 開團。

- 規則書: `ucl_core:Docs~/zh-Hant/Mechanics/TRPG_Lite_RuleBook.md`
- 戰役 state: `<repo>/AgentCommands/TRPG/campaigns/`

## ⚔ 遊戲 QA (自動戰鬥)

跑自動戰鬥 QA loop，順便 dogfood 戰鬥系統（專案有對應 QA skill 才可做；EOV: `valor-qa-battle`）。

- ⚠ 專案限定性強 — 所在專案沒有戰鬥 QA 基建就跳過此子項

## ♟ 下棋

→ 已獨立為 [`chess`](chess.md)，不在本組。
