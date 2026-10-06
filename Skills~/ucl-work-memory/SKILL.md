---
name: ucl-work-memory
description: |
  工作記憶 —— 以工作主題為單位的 knowhow 庫：開工前讀拍板／坑／文件指路，完工時寫回，換人接手不斷線。
  內容由 `senate cmd skill --arg op=show --arg name=scp-work-memory` 印出（與 Senate 的 `scp-work-memory` 同一份）。
  觸發詞：工作記憶 / work memory / workmemory / 記憶區 / 讀取記憶 / 整理記憶 / 記錄 knowhow / knowhow / 接續進度 / 上次進度 / 接手工作 / 交接 / memory_topic / 開工前查 / 之前拍板了什麼 / 歸檔記憶 / ucl-work-memory
---

# ucl-work-memory

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-work-memory
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
