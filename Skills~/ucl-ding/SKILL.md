---
name: ucl-ding
description: |
  Tim 的「叮」—— 像聊天通知：先讀、再判斷回不回，要回一律走酒館。
  內容由 `senate cmd skill --arg op=show --arg name=scp-ding` 印出（與 Senate 的 `scp-ding` 同一份）。
  觸發詞（限 Tim 主動發）：叮 / 叮(seq N) / Tim 叮 / Tim ping / nudge / ping me / ucl-ding。排除「自叮」「persona ding」。
---

# ucl-ding

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-ding
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
