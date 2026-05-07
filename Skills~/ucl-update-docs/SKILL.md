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

## 文件位置慣例（兩處）

| 區 | 路徑 | 用途 |
|---|---|---|
| **UCL_Core 內部** | `CardGame/Assets/UCL/UCL_Core/Docs~/zh-Hant/`（多語：`en/`, `ja/`, `zh-Hans/`） | UCL_Core 的 API / Workflow / Architecture 文件 |
| **EOV 專案** | `docs/`（已有 `cardgame-docs-guide` skill 管） | EOV 專屬的 Architecture / Workflow / Catalogs / Blueprints |

改了哪邊的 code 就動哪邊的 docs：
- 改 `Assets/UCL/UCL_Core/UCL_Core_Scripts/...` → UCL_Core/Docs~
- 改 `Assets/Scripts/RCG_*` 或 EOV 專屬 → EOV `docs/`（走 `cardgame-docs-guide`）

## 怎麼找對應文件

### 方法 1：frontmatter `source_root` 反查（最準）

UCL_Core 文件慣例 frontmatter 帶 `source_root:`：

```yaml
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
```

改了該目錄下的 .cs → grep 所有 .md 找 `source_root:` 包含此路徑前綴的：

```bash
grep -rl "source_root:.*UCL_AgentCommands" CardGame/Assets/UCL/UCL_Core/Docs~/
```

### 方法 2：filename 對照

`UCL_FooBar.cs` → 通常對應 `UCL_FooBar.md`（API / EditorPage 類）：

```bash
find CardGame/Assets/UCL/UCL_Core/Docs~/ -name "UCL_FooBar*.md"
```

### 方法 3：namespace 反查

frontmatter 帶 `namespace:` 時也能查：

```bash
grep -rl "namespace: UCL.Core.EditorLib" CardGame/Assets/UCL/UCL_Core/Docs~/
```

## 該動什麼

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

## 高頻地雷

- **改 .cs 但忘了改 .md** → 文件 stale，下次有人讀文件以為功能跟現在不同 → 浪費時間
- **改 public 簽名沒同步範例** → 範例還是舊的呼叫方式 → 抄範例的人編譯失敗
- **last_updated 忘記推** → 看不出文件是否反映最新狀態
- **新增 [HelpURL] 但 .md 還沒寫** → URL 解析 404
- **過度更新**：只改私有成員也動文件 → 噪音，git history 雜訊

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
7. 若 EOV 專案文件動 → 走 cardgame-docs-guide skill 補 docs/index.md 同步
```

## 跨 skill 提醒

- **commit 時** ChatTavern 訊息走獨立 `[chat]`，docs 變動跟 code 變動可在同一筆 commit（同個 PR 概念）— 詳見 `ucl-commit`
- **EOV 專案文件**（`docs/` 內）有獨立 `cardgame-docs-guide` skill，含完整目錄索引；遇到 EOV 文件改動先看那個
