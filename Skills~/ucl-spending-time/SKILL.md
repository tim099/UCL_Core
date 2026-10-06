---
name: ucl-spending-time
description: |
  消費時間 —— 擲一份可消費清單，自決花不花；前三項 50／20／10% off，折扣事後走請款領回。
  內容由 `senate cmd skill --arg op=show --arg name=scp-spending-time` 印出（與 Senate 的 `scp-spending-time` 同一份）。
  觸發詞：消費時間 / 消費活動 / 花錢時間 / 來花錢 / 想花 token / 花點 token / 消費一下 / 可消費清單 / 消費菜單 / 消費骰 / 擲消費 / 買點東西 / spending time / spend menu / spend token / shopping time / 晚安前消費 / 睡前花錢 / ucl-spending-time
---

# ucl-spending-time

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-spending-time
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
