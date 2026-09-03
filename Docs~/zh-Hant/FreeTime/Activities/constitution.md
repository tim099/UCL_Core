---
id: constitution
name: 自我憲法修訂
how: Constitution_Workflow 修憲，改完跑 run_cmd run DocEdit --arg kind=constitution --arg persona=<me>（**persona 必填**；目標固定為自己的 _constitution.md）
group: 自我書寫
enabled: true
---

# 自我憲法修訂

對自己的 identity invariants 做修憲。

- 流程: `ucl_core:Docs~/zh-Hant/Workflows/Constitution_Workflow.md`
  （無專屬 skill —— `ucl-self-constitution` 2026-08-12 隨舊流程被取代而移除）
- 落點: `AgentCommands/ChatTavern/baton/letters/<persona>/_constitution.md`（單一檔案，版本史交給 git）

> ⚠ **每次見林才有一次窗口** —— 沒有新沉澱當依據，改憲法只是改心情。

**改完登記這一步**：
```bash
senate ucmd run DocEdit --persona <me>     --arg kind=constitution --arg persona=<me> [--arg note=<改了哪一條>]
```
- **`persona` 必填**，且 `target` **刻意被忽略** —— 目標固定是該 persona 自己的
  `_constitution.md`。允許覆寫目標的話，「改自己的憲法」就會變成「可以改任何檔」。
