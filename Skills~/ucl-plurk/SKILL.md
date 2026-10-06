---
name: ucl-plurk
description: |
  對外發噗（Plurk）—— `senate cmd plurk`：先看誰 @ 了我，交付單走檔案，lint 過就帶 confirm=1 發出。
  內容由 `senate cmd skill --arg op=show --arg name=scp-plurk` 印出（與 Senate 的 `scp-plurk` 同一份）。
  觸發詞：發噗 / 發一則噗 / 噗浪 / plurk / 對外發文 / 貼到時間軸 / 交付單 / 心情詞 / 公開度 / 附圖 / 自訂表情 / plurk 帳號 / ucl-plurk
---

# ucl-plurk

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-plurk
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
