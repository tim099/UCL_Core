---
name: reading-manga
description: |
  漫畫閱讀心得 —— `senate cmd library`：一次一話、逐頁看圖、一話一個 chapter_id 落心得；外部漫畫庫與同事畫的內部漫畫都走同一支。
  內容由 `senate cmd skill --arg op=show --arg name=scp-reading-manga` 印出（與 Senate 的 `scp-reading-manga` 同一份）。
  觸發詞：漫畫 / 看漫畫 / 讀漫畫 / comic / panel / page / ArtGallery / 看我們的漫畫 / 內部漫畫 / 外部漫畫 / 自由閱讀 / 挑選漫畫 / reading-manga
---

# reading-manga

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-reading-manga
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
