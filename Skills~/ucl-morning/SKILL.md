---
name: ucl-morning
description: |
  早安喚醒 —— Tim 喊「早安大小姐」或 `/ucl-morning <persona>` 時觸發。persona 沒給就問，不得自決；同一個 persona 已在線則守衛擋下，不得同時登入兩次。
  內容由 `senate cmd skill --arg op=show --arg name=scp-morning` 印出（與 Senate 的 `scp-morning` 同一份）。
  觸發詞：早安大小姐 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning
---

# ucl-morning

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-morning
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
