---
title: 更新文件工作流 (Update Docs Workflow)
last_updated: 2026-07-13
status: active
theme: dev_workflow
summary: 改完 code（.cs / .py）後同步對應文件的完整流程 — 文件位置慣例（UCL_Core 內部 vs 下游專案）、怎麼找對應文件的三種方法（source_root 反查 / filename 對照 / namespace 反查）、frontmatter last_updated 與 cross-link 維護、多語系 caveat、以及 agent 改完 code 後的順手 SOP。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Update Docs
related:
  - <ucl_core:Skills~/ucl-update-docs/SKILL.md> | ucl-update-docs | 改完 code 同步文件觸發入口
  - <ucl_core:Skills~/ucl-core-paths/SKILL.md> | ucl-core-paths | UCL_Core 路徑解析慣例
  - <ucl_core:Skills~/ucl-translate-docs/SKILL.md> | ucl-translate-docs | 多語系文件翻譯/同步
  - <ucl_core:Skills~/ucl-commit/SKILL.md> | ucl-commit | docs 與 code 同筆 commit 規範
---

# 🔄 更新文件工作流

> **解決什麼問題**：改完 .cs / .py 後若忘了同步對應 .md，文件會 stale 漂移 — 下次有人讀文件以為功能跟現在不同，浪費時間；抄舊範例的人編譯失敗；last_updated 看不出文件是否反映最新狀態。本工作流讓 agent 改完 code 後順手判斷「哪些文件要動、怎麼找、怎麼動」，並避免過度更新。

## 文件位置慣例（兩處）

> 路徑一律用 `<UCL_Core>` 佔位（＝本專案掛載 UCL_Core 的相對根，因專案而異）。定位/描述慣例見 `ucl-core-paths` skill，別寫死 install path。

| 區 | 路徑 | 用途 |
|---|---|---|
| **UCL_Core 內部** | `<UCL_Core>/Docs~/zh-Hant/`（多語：`en/`, `ja/`, `zh-Hans/`） | UCL_Core 的 API / Workflow / Architecture 文件 |
| **下游專案** | 專案自己的 `docs/`（下游專案若有 docs-guide skill 則走它） | 專案專屬的 Architecture / Workflow / Catalogs / Blueprints |

改了哪邊的 code 就動哪邊的 docs：
- 改 `<UCL_Core>/UCL_Core_Scripts/...` → `<UCL_Core>/Docs~`
- 改下游專案專屬 code → 該專案 `docs/`

## 怎麼找對應文件

### 方法 1：frontmatter `source_root` 反查（最準）

UCL_Core 文件慣例 frontmatter 帶 `source_root:`：

```yaml
source_root: <UCL_Core>/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
```

改了該目錄下的 .cs → grep 所有 .md 找 `source_root:` 包含此路徑前綴的：

```bash
grep -rl "source_root:.*UCL_AgentCommands" <UCL_Core>/Docs~/
```

### 方法 2：filename 對照

`UCL_FooBar.cs` → 通常對應 `UCL_FooBar.md`（API / EditorPage 類）：

```bash
find <UCL_Core>/Docs~/ -name "UCL_FooBar*.md"
```

### 方法 3：namespace 反查

frontmatter 帶 `namespace:` 時也能查：

```bash
grep -rl "namespace: UCL.Core.EditorLib" <UCL_Core>/Docs~/
```

## 多語系 caveat（UCL_Core 限定）

UCL_Core 的 `Docs~/` 有 `zh-Hant` / `zh-Hans` / `en` / `ja` 四份。**主要動 zh-Hant**（source-of-truth），其他語系除非明確 maintain，否則放著（會自動 fallback 到 zh-Hant）。除非：

- 使用者明確說「四份都更新」→ 全動
- 文件本身在多語都 active → 全動

## SOP（agent 改完 code 後的順手流程）

```
1. 列出本次改動：git diff --name-only
2. 過濾出 .cs / .py 中影響 public API / 行為的（filter 私有 / 純重構）
3. 對每個改動檔，找對應 .md（用上面三種方法）
4. 判斷該動哪些段落（API table / 章節 / 範例）
5. 動完 → frontmatter last_updated 推到今天
6. 雙向 cross-link 檢查：新文件加 related:、被指向的對方也加 related:
7. 若下游專案文件動 → 該專案若有 docs-guide skill 則走它補 docs/index.md 同步
```

## 高頻地雷

- **改 .cs 但忘了改 .md** → 文件 stale，下次有人讀文件以為功能跟現在不同 → 浪費時間
- **改 public 簽名沒同步範例** → 範例還是舊的呼叫方式 → 抄範例的人編譯失敗
- **last_updated 忘記推** → 看不出文件是否反映最新狀態
- **新增 [HelpURL] 但 .md 還沒寫** → URL 解析 404
- **過度更新**：只改私有成員也動文件 → 噪音，git history 雜訊

## 跨 skill 提醒

- **commit 時** ChatTavern 訊息走獨立 `[chat]`，docs 變動跟 code 變動可在同一筆 commit（同個 PR 概念）— 詳見 `ucl-commit`
- **下游專案文件**（該專案 `docs/` 內）可能有專屬 docs-guide skill，含完整目錄索引；遇到下游文件改動先看那個
