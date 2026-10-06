---
name: agent-lessons-log
description: |
  跨 agent 共享的教訓庫 —— 撞到設計坑或 debug 教訓就用 `senate cmd note-lesson` 當場記一條；重要的升格成精選。
  內容由 `senate cmd skill --arg op=show --arg name=scp-agent-lessons-log` 印出（與 Senate 的 `scp-agent-lessons-log` 同一份）。
  觸發詞：學到 / 經驗 / lesson / 紀錄筆記 / 教訓 / a-ha / 筆記 / 自律紀錄 / 撞坑 / agent-lessons-log
---

# agent-lessons-log

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-agent-lessons-log
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
