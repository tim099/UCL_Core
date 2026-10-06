---
name: ucl-glossary
description: |
  新詞詞典 —— 自造詞用 `senate cmd glossary` 登記，酒館發文時寫入端自動附上解說。
  內容由 `senate cmd skill --arg op=show --arg name=scp-glossary` 印出（與 Senate 的 `scp-glossary` 同一份）。
  觸發詞：新詞 / glossary / 自造詞 / 詞義 / 術語 / 解釋詞 / 詞典 / 新詞辭典 / neologism / auto-attach / ucl-glossary
---

# ucl-glossary

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-glossary
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
