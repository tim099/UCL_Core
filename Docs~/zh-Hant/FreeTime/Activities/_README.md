# FreeTime Activities — 自由時間活動資料夾（UCL_Core 共用層）

> 本資料夾是自由時間「可做活動清單」的**跨專案共用層**（Tim 2026-06-11 拍板文件驅動 + 跨專案化）。
> 每個 `*.md` = 一個活動，`<UCL_Core>/Tools~/AgentCommands/freetime.py` 掃描產生 shuffle / list 輸出 —
> **新增或更新活動 = 直接增改 md 檔，工具即自動同步**，不需要再改任何 code / JSON。

## 雙層設計

| 層 | 路徑 | 放什麼 |
|---|---|---|
| **共用層**（本資料夾） | `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/` | 跨專案通用活動（讀書 / 畫圖 / 寫信 / 酒館閒聊…；EOV 的 valor 系活動經 Tim 2026-06-11 整併也住這） |
| **專案層**（可選 overlay） | `<repo>/docs/FreeTime/Activities/` | 該專案限定活動；或同 id + `enabled: false` **停用覆蓋**不適用的共用活動 |

兩層合併讀取，**同 id 時專案層覆蓋共用層**（客製說明或停用都算覆蓋）。
enabled 過濾在 merge **之後**執行 — 停用覆蓋才生效（kotoko QA 2026-06-11 抓出的缺口，已修）。

## 檔案格式

檔名 = 活動 id（kebab-case）。frontmatter 為機讀層，body 為人讀層：

```markdown
---
id: reading                  # 穩定識別碼 (= 檔名去 .md)
name: 閱讀 (自選讀書)         # 顯示名 (shuffle 輸出主體)
how: reading-library skill → 新 Library 的 work/media/persona/read_session 流程   # 一行操作提示
enabled: true                # false = 暫時下架 (shuffle/list 跳過, 檔案保留)
---

# 閱讀 (自選讀書)

(活動詳細說明 / SOP / 相關 skill 連結 — agent 選定活動後用 `show --id reading` 深讀)
```

## 慣例

- `_` 開頭的檔案（如本檔）不算活動，掃描時跳過
- 對齊 EOV `docs/Glossary/` 的 per-entry md + frontmatter 前例
- 工具：`python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle|list|show|init`
- 三池 spec：[`<UCL_Core>/Docs~/zh-Hant/Mechanics/FreeTime_System.md`](../../Mechanics/FreeTime_System.md) §4（2026-06-11 同步搬入 UCL_Core）
