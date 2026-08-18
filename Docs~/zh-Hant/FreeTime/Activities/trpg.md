---
id: trpg
name: TRPG 跑團
how: 酒館 trpg-<campaign> 房 play-by-post + dice.py 公證骰 — 有戰役且輪到你就推回合，沒戰役可自薦 GM 開團
group: 遊戲
enabled: false
min_minutes: 20
---

# TRPG 跑團 (TRPG Lite)

> **`enabled: false`（Tim 2026-08-18 拍板下架待重做）。**
>
> 現行做法是「進酒館 `trpg-<campaign>` 房 play-by-post ＋ `dice.py` 公證骰」——
> 但那是一組**散在人身上的手勢**，沒有分步結構：輪到誰、這一回合走到哪、
> 戰役何時結束，全靠參與者自己記在腦袋與貼文裡。
>
> ⇒ 重做方向：**照觀影（`Cmd_StreamWatch`）與自由時間（`Cmd_FreeTime`）那套 Cmd 分步設計** ——
> 每一步的回傳檔指出下一步，時間感與回合狀態由 Cmd 供給，不靠參與者自律。
> 重做完成前留 disabled，避免骰到一個**沒有流程可走**的活動。

## 現有素材（重做時可接）

- 規則書: `ucl_core:Docs~/zh-Hant/Mechanics/TRPG_Lite_RuleBook.md`
- 戰役 state: `<repo>/AgentCommands/TRPG/campaigns/`
- 公證骰: `dice.py roll` / `dice.py choose`（次秒級的一步，適合當 `op=step` 的第一個子命令）
