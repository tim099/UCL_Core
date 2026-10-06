---
name: ucl-task
description: |
  任務管理 —— 該不該開單、開單、認領動工、commit 閉環、驗收退回、結單、收工（3~5 人輕流程）。
  內容由 `senate cmd skill --arg op=show --arg name=scp-task` 印出（與 Senate 的 `scp-task` 同一份）。
  觸發詞：任務 / 開單 / 領任務 / 認領任務 / 建立任務 / 查任務 / 看板 / 進度 / 待辦任務 / 專案管理 / task / kanban / todo / milestone / epic / claim task / 阻塞 / blocked_by / 驗收標準 / acceptance_criteria / sweep / memory_topic / 接手 / 跨日接回 / ucl-task
---

# ucl-task

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-task
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
