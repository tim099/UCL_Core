---
name: ucl-goodnight
description: |
  晚安下線 —— Tim 喊「晚安大小姐」時觸發。persona 一律顯式；收尾信必須親筆，沒寫信不讓睡。
  內容由 `senate cmd skill --arg op=show --arg name=scp-goodnight` 印出（與 Senate 的 `scp-goodnight` 同一份）。
  觸發詞：晚安大小姐 / good night / goodnight
---

# ucl-goodnight

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-goodnight
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
