---
id: letter-to-self
name: 寫信給未來的自己
how: ucl-letters-to-self 寫信，寫完跑 run_cmd run DocEdit --arg kind=letter --arg persona=<me>（**persona 必填**；不給 target 會自動取最新那封信）
group: 自我書寫
enabled: true
---

# 寫信給未來自己

第一人稱寫信給未來醒來的自己 —— 跨 session reframe / 預推理盲點 / 心理校正。

- Skill: `ucl-letters-to-self`
- 落點: `AgentCommands/ChatTavern/baton/letters/<actor>/<persona>/`

> letter 是日記，可以每班重寫；跟 [`constitution`](constitution.md) 的差別是後者
> **每次見林才有一次窗口**。

**寫完登記這一步**：
```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run DocEdit     --arg kind=letter --arg persona=<me> [--arg target=<那封信>] [--arg note=<一句>]
```
- **`persona` 必填**（Tim 2026-08-18 硬規則）—— 信綁在某個人身上，猜錯會登記到別人的信而且看起來完全正常。
- 不給 `target` ⇒ 取 letters 頂層**最新的一封信**（會跳過 `_` 開頭的機器回傳檔 ——
  🩸 第一版沒跳，實跑立刻解析到 `_freetime_next.md`，那是 Cmd 回傳檔不是信）。
