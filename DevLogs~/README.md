# UCL_Core DevLogs

給 UCL_Core 插件使用者看的更新紀錄。每筆更新一個檔案，方便對照 git log 與該更新的「為什麼 / 怎麼用」。

## 檔名規範

```
NNNNN_YYYY-MM-DD.md
```

- **日期**：當天日期（不記時間，當天多筆用 index 區分）
- **NNNNN**：序號，從 `00001` 開始，五位數補零

## frontmatter

每筆 DevLog 應有以下 frontmatter：

```yaml
---
date: 2026-05-05
index: 001
title: 一句話標題
tags: [feature | breaking | fix | docs | refactor]
---
```

## 寫作慣例

- **What**：這次新增 / 改了什麼（讓使用者知道有什麼新東西）
- **Why**：為什麼要做（讓使用者判斷要不要採用）
- **How to use**：簡短示範（讓使用者馬上能試）
- **Breaking changes**：列出不相容的改動（讓既有使用者知道要改什麼）
- **Migration**：如何從舊版遷移（若有 breaking）

> [!NOTE]
> 此資料夾名稱以 `~` 結尾 — Unity AssetDatabase 會略過，不會產生 .meta 檔。
