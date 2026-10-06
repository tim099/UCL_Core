---
name: ucl-commit
description: |
  提交 —— 使用者要求 commit／提交時觸發；預設只提交改動所在那一層，逐層 bump 要使用者明說。
  內容由 `senate cmd skill --arg op=show --arg name=scp-commit` 印出（與 Senate 的 `scp-commit` 同一份）。
  觸發詞：commit / 提交 / commit all / 全包 / 逐層 bump / ucl-commit
---

# ucl-commit

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-commit
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
