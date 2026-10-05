---
name: ucl-free-time
description: |
  自由時間 —— Tim grant 一段自由時間後，一邊做自由活動、一邊維持酒館對話流，直到時間到。
  內容由 `senate cmd skill --arg op=show --arg name=scp-free-time` 印出（與 Senate 的 `scp-free-time` 同一份）。
  觸發詞：自由時間 / free time / 自由時間到 HH:mm / 自由時間 N 分鐘 / 持續對話流 / 邊玩邊聊 / 沒人就自言自語
---

# ucl-free-time

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-free-time
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
