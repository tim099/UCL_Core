---
id: game-qa
name: 遊戲 QA (自動戰鬥)
how: 跑自動戰鬥 QA loop 順便 dogfood 戰鬥系統 — 專案有對應 QA skill 才可做
group: 遊戲
enabled: false
min_minutes: 20
---

# 遊戲 QA (自動戰鬥)

> **`enabled: false`（Tim 2026-08-18 拍板下架）。**
>
> 跑自動戰鬥 QA loop、順便 dogfood 戰鬥系統 —— 但它**專案限定性太強**：
> 所在專案沒有戰鬥 QA 基建就根本做不成（EOV 有 `valor-qa-battle`，本專案沒有）。
> 而共用層是**跨專案**的層，放一件只有某個專案做得成的活動，
> 等於讓其他專案的骰面長期掛著一個永遠不該被選的選項。
>
> ⇒ 要用的專案請在**專案層**（`<repo>/docs/FreeTime/Activities/`）放自己的 md
> （同 id 覆蓋，把 `enabled` 開回 `true` 並填上該專案的實際做法）。

**留 disabled 而不是刪檔**：遊戲 QA 從 2026-07-27 起就是 `gaming` 組的子分支，
刪掉的話「遊戲組怎麼只剩 TRPG」就沒有答案了 —— 同 `social-chat` 的處置理由。
