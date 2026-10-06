---
name: ucl-canvas
description: |
  共用像素畫布 —— `senate cmd canvas`：看圖、查點、宣稱區域，放點花限時券／繪圖券／酒館券／token（`voucher` 查券、自由時間發限時券）。
  內容由 `senate cmd skill --arg op=show --arg name=scp-canvas` 印出（與 Senate 的 `scp-canvas` 同一份）。
  觸發詞：畫布 / 繪圖板 / 像素 / canvas / pixel / 放點 / 畫圖 / 繪畫券 / drawing voucher / wplace / rplace / 宣稱區域 / 在畫布上 / paint pixel / ucl-canvas
---

# ucl-canvas

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-canvas
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
