---
title: 更新文件工作流 (Update Docs Workflow)
last_updated: 2026-09-01
status: active
theme: dev_workflow
summary: 改完 code（.cs / .py）後同步對應文件的完整流程 — 文件位置慣例（UCL_Core 內部 vs 下游專案）、怎麼找對應文件的三種方法（source_root 反查 / filename 對照 / namespace 反查）、frontmatter last_updated 與 cross-link 維護、多語系 caveat、以及 agent 改完 code 後的順手 SOP、以及刪功能時「歷史不保留」（痕跡整段移除、不留退場墓碑，歷史由 git 記錄）。
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

## 🪦 歷史不保留 —— 史料歸 git，文件只描述現況

**文件回答的是「現在是什麼」。** 「以前是什麼、什麼時候沒的、誰拿掉的」由 `git log` 回答，
而且比手寫的墓碑準 —— 手寫的那份沒有人維護，會慢慢變成一份誰都不敢刪的假歷史。

⇒ 功能刪掉時，**把它在文件裡的痕跡整段拿掉**：章節、API table 那一列、範例、
`related:` cross-link、index / manifest / 指路行，一併移除。

| 情境 | ✅ 這樣做 | ❌ 不要 |
|---|---|---|
| 刪一支工具 / API | 移除該列、該章節、該 cross-link | `~~foo.py~~ **已退場 2026-09-01**` |
| 刪一個 skill / workflow | 連 index、manifest、指路行一起移除 | 留一行「此功能已移除」 |
| 功能換實作 | 直接寫新的 | 新舊並陳＋「舊版已廢棄」 |
| 欄位 / 參數移除 | 從表格與範例裡拿掉 | 保留該列並標 deprecated |

⚠ **唯一的例外是遷移指引**：呼叫端還在外面、讀者需要知道「我原本那樣寫，現在要改成怎樣」——
那有讀者、有動作，是文件不是墓碑。純粹「告訴後人這裡曾經有東西」沒有讀者。

📌 判準一句話：**寫下它之前先問「誰會因為讀到這行而做出不同的動作」** —— 答不出來就刪掉。

📌 附帶一格：刪除本身**不需要在文件裡交代理由**。理由寫進 commit message，
那裡才是它跟 diff 綁在一起、查得回去的地方。

## SOP（agent 改完 code 後的順手流程）

```
1. 列出本次改動：git diff --name-only
2. 過濾出 .cs / .py 中影響 public API / 行為的（filter 私有 / 純重構）
3. 對每個改動檔，找對應 .md（用上面三種方法）
4. 判斷該動哪些段落（API table / 章節 / 範例）
5. 刪功能時 → 痕跡整段移除，不留墓碑（見「歷史不保留」）
6. 動完 → frontmatter last_updated 推到今天
7. 雙向 cross-link 檢查：新文件加 related:、被指向的對方也加 related:
8. 若下游專案文件動 → 該專案若有 docs-guide skill 則走它補 docs/index.md 同步
```

⚠ 第 5 步最常漏的不是主文件，是**指向它的那些行** —— index、`_manifest.json`、
skill 的「延伸」表、其他文件的 `related:`。刪主文件卻留指路行 ＝ 死連結，
比留著墓碑更糟（墓碑只是噪音，死連結會讓人去找一個不存在的東西）。

## 高頻地雷

- **改 .cs 但忘了改 .md** → 文件 stale，下次有人讀文件以為功能跟現在不同 → 浪費時間
- **改 public 簽名沒同步範例** → 範例還是舊的呼叫方式 → 抄範例的人編譯失敗
- **last_updated 忘記推** → 看不出文件是否反映最新狀態
- **新增 [HelpURL] 但 .md 還沒寫** → URL 解析 404
- **過度更新**：只改私有成員也動文件 → 噪音，git history 雜訊
- **刪功能留墓碑**（「已退場」/ deprecated / 刪除線＋日期）→ 假歷史累積，沒有人維護也沒有人敢刪
- **刪了主文件卻留指路行**（index / manifest / `related:` / skill 延伸表）→ 死連結，害人去找不存在的東西

## 跨 skill 提醒

- **commit 時** ChatTavern 訊息走獨立 `[chat]`，docs 變動跟 code 變動可在同一筆 commit（同個 PR 概念）— 詳見 `ucl-commit`
- **下游專案文件**（該專案 `docs/` 內）可能有專屬 docs-guide skill，含完整目錄索引；遇到下游文件改動先看那個
