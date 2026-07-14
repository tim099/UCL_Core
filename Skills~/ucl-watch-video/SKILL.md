---
name: ucl-watch-video
description: |
  使用瀏覽器子代理（browser_subagent）開啟、分析並抓取 Web 影片（如 YouTube）內容的自動化技能。
  包含展開說明欄、開啟轉錄稿（Transcript/Lyrics）、提取主要觀點與時間戳，並將分析心得格式化。
  觸發詞：watch video / 看影片 / 觀看影片 / YouTube / 影片心得 / analyze video / 影片轉錄
---

# UCL Watch Video Skill — 影片自動化觀察與分析規範

> 一句話:**用瀏覽器實際爬取影片標題/說明欄/含時間戳的轉錄稿,再劃分成「轉錄稿片段(含時間戳)＋核心大意＋大小姐心得」三段報告 — 絕不憑空通靈。**

## 必讀

完整流程(啟動瀏覽器子代理、展開說明欄、開啟轉錄面板、提取與報告架構、大小姐影評哲學、UCL_VideoScraper 偽碼) → `ucl_core:Docs~/zh-Hant/Workflows/Watch_Video_Workflow.md`

## ⚠ 禁止行為 (Anti-Patterns)

- ❌ **通靈歌詞**：在沒有實際用瀏覽器爬取到轉錄稿前，禁止憑空捏造、或僅靠搜尋引擎快取就謊稱已「看過影片」。
- ❌ **過度機械式總結**：只貼一堆無序條列句，缺乏情感的反思心得。
- ❌ **漏掉時間戳**：好的分析應該引用具體的時間點（如 `[01:32]`），以增強專業感。

## 關聯

- `ucl_core:Docs~/zh-Hant/Workflows/Watch_Video_Workflow.md` — 完整工作流(必讀)
- `ucl_core:Skills~/ucl-chat-tavern/SKILL.md` — 如何將心得完美分享至 Tavern 酒館
- `repo:Docs/AI_READABILITY_GUIDELINES.md` — 註解與物理意義撰寫鐵律
