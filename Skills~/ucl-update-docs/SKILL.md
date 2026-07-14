---
name: ucl-update-docs
description: |
  改完 code（.cs / .py）後同步對應文件 — 避免文件 stale 漂移。
  使用者要求「更新文件」/「同步文件」/「文件落後了」/「改完 code 文件要不要動」/「update docs」/「sync docs」/「last_updated 還沒改」/ 變動 public API 後等場景時用本 skill。
  涵蓋：找對應文件、frontmatter last_updated 推進、內容同步、cross-link 維護、避免 over-update（只改私有成員不必動文件）。
trigger: { on_files: ["*.cs", "*.py"], on_intent: ["更新文件", "同步文件", "update docs", "sync docs", "文件落後", "last_updated"] }
---

# UCL Update Docs — 改 code 後同步文件

> 一句話：**改完 .cs / .py 別馬上跑，先想「對應的 .md 文件要不要動」**。public API 改了 / 行為改了 / 新增功能 → 文件必動；私有成員 / 重構 / 註解 → 文件不必動。

## 必讀

完整流程（文件位置慣例、怎麼找對應文件的三種方法、多語系 caveat、改完 code 後的順手 SOP、高頻地雷、跨 skill 提醒）→ `ucl_core:Docs~/zh-Hant/Workflows/UpdateDocs_Workflow.md`

## 該動什麼（change-type → action）

| 變動類型 | 該動文件 | 怎麼動 |
|---|---|---|
| 新增 public class / method | ✅ | 加章節 + API table 新行 + cross-link |
| 改 public 簽名 | ✅ | 章節描述 + 範例同步 |
| 改 public 行為（同簽名不同效果）| ✅ | 章節 + 加 caveats / migration note |
| 刪 public 成員 | ✅ | 移除章節 + 加 deprecated note 或刪除 |
| 新增 [HelpURL] 指向新 doc | ✅ | 建新 .md + 加 frontmatter related: cross-link |
| 改 internal / private | ❌ | 不必動（API surface 沒變）|
| 純重構 / rename 內部變數 | ❌ | 不必動 |
| 純註解 / 排版 | ❌ | 不必動 |
| 修 bug 但行為對外無感 | ❌ | 不必動（除非 doc 描述了錯誤行為）|

## frontmatter 必動兩處

每次改文件**必須**：

1. `last_updated: YYYY-MM-DD` 推到今天
2. 若變動影響 cross-link（新文件 / 重命名 / 拆分）→ 雙向更新 `related:` 區塊

```yaml
related:
  - ucl_core:Docs~/{lang}/Workflows/Foo.md | Foo Workflow | 一句話描述
```

## ⛔ 不可做

- ❌ 改 .cs / .py 影響 public API / 行為卻不動對應 .md — 文件 stale，害後人讀錯。
- ❌ 改 public 簽名卻不同步範例 — 抄舊範例的人編譯失敗。
- ❌ **過度更新**：只改私有成員 / 純重構 / 純註解也動文件 — 噪音，git history 雜訊。
- ❌ 動了文件卻忘記推 `last_updated` — 看不出文件是否反映最新狀態。
