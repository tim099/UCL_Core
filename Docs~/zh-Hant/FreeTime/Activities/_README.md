# FreeTime Activities — 自由時間活動資料夾（UCL_Core 共用層）

> 本資料夾是自由時間「可做活動清單」的**跨專案共用層**（Tim 2026-06-11 拍板文件驅動 + 跨專案化）。
> 每個 `*.md` = 一個活動，C# `Cmd_FreeTime` 掃描產生骰面與 list/shuffle/show 輸出 —
> **新增或更新活動 = 直接增改 md 檔，工具即自動同步**，不需要再改任何 code / JSON。
> （掃描端只有 C# 一份實作。）

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
id: reading                  # 穩定識別碼 (= 檔名去 .md)；op=pick 要填的就是它
name: 閱讀 (自選讀書)         # 顯示名 (shuffle 輸出主體)
how: reading-library skill → 新 Library 的 work/media/persona/read_session 流程   # 一行操作提示
enabled: true                # false = 暫時下架 (shuffle/list 跳過, 檔案保留)
group: 知識沉澱               # 選填 — 分組，見下節 (缺欄位 = 不分組，自成骰面一項)
min_minutes: 20              # 選填 — 建議所需分鐘 (Cmd_FreeTime 擲骰時剩餘時間不足 → 排尾標明「時間不夠」，不隱藏)
kind: Default                # 選填 — 特殊邏輯標記，見下節 (缺欄位 = Default)
tool: library.py             # 選填 — 代跑用腳本 (空 = 本活動不支援 op=step 代跑)
steps: resume, shelf, list   # 選填 — 允許代跑的子命令白名單 (空 = 即使有 tool 也不放行)
persona_flag: --persona      # 選填 — 這支工具接身分用的旗標名 (canvas.py 是 --persona，library.py 是 --reader)
steps_need_persona: shelf, resume=--reader   # 選填 — 這些 step 由 op=step 自動補身分；`step=--flag` 可覆寫單一 step 的旗標（同一支工具不一定一致）
---

# 閱讀 (自選讀書)

(活動詳細說明 / SOP / 相關 skill 連結 — agent 選定活動後用 `show --id reading` 深讀)
```

## `group` — 分組（Tim 2026-08-18 拍板）

> **一份 md ＝ 一件具體活動**，分類交給 `group`。

在這之前一份 md 就是一「組」活動（`canvas-draw` ＝ 2D 畫布**或** 3D 雕刻、
`gaming` ＝ TRPG**或** QA），代價有兩層，而且都不會報錯：

1. **子分支的選擇沒有落盤** —— `session.activity` 只存得到組別 id，
   帳面上分不出「活動實作 1 件」做的是 2D 畫布還是 3D 雕刻。
2. **`tool` / `steps` 掛在 md 上** ⇒ 一組裡分支用不同工具時（`canvas.py` vs `Cmd_Sculpture`），
   **只有第一個分支接得到 `op=step` 代跑**，第二個分支的缺席沒有任何地方會喊。

### 骰面怎麼呈現分組

| 情況 | 骰面 |
|---|---|
| 同 `group` 的活動 | 收成**骰面的同一項**（拆檔不會讓骰面暴長）；項底下列出每件具體活動的 id |
| 觸發特殊規則排序（`kind` 條件成立） | **脫離分組成單獨一項**排到最前面，並印出它從哪一組脫離 |
| 沒填 `group` | 自成骰面一項（＝單獨活動） |

**為什麼優先項要脫離**：它此刻特別值得做的理由是**它自己的**（棋局對手在線／券快滿了），
被組名蓋住就傳達不到 —— 「繪圖」這個組名不會告訴你券超過 100 該用了。
⚠ 脫離的活動**會從原組的清單移除**（同一件事在骰面出現兩次會被讀成兩件事）；
組員全被脫離時整組不列（空組是個比事實大的名字）。

**組項的時間不夠 ＝ 組內全員都不夠** —— 有一個做得成就不該把整組標成做不完。

> ℹ 收合邏輯只有 C# `Cmd_FreeTime` 一份。（曾經的宣告差異「python freetime.py shuffle
> 不做組項收合」隨該工具 2026-08-26 退役而消滅 —— 純參考擲骰現在走 `step=shuffle`，同一份實作。）

## `kind` — 特殊邏輯標記（Tim 2026-08-17 拍板）

大多數活動一視同仁地隨機排序就好。但有兩種例外，各自需要不同的處理：

| kind | 骰面行為 | 目前用在 |
|---|---|---|
| `Default` | 無特殊邏輯（缺欄位即此值） | 其餘全部 |
| `StreamWatch` | **沒開播 → 整項隱藏**；開播 → 進優先層＋附本場節目名 | `stream-watch` |
| `Chess` | 有未完成棋局**且對手也在自由時間** → 進優先層（不隱藏） | `chess` |

### 兩軸是兩件事，別混為一談

- **隱藏**（可用性）＝ 這件事現在**根本做不成**。沒開播的陪看就是這一類。
- **優先層**（排序）＝ 這件事現在**特別值得做**。層內**仍然隨機** —— 優先不是指定，
  多項同時優先時彼此的順序照樣是骰出來的，而且你永遠可以不選它。
- **時間不夠 ≠ 做不成**，所以走的是第三種處理：降到清單最尾並標明，**不隱藏**
  （資訊留著讓人自己判斷）。這道會**壓過優先層** —— 「最優先但這場做不完」是自相矛盾的建議。

### 增改 kind 的手勢

- **改既有活動**：Editor 開「自由時間管理」頁 → 下拉選活動 → 「特殊邏輯」／「分組」欄位改一改。
  頁面會就地改寫本 md 的 `kind` 欄位（**不另存 override 設定** —— 活動的事實來源只有 md 一處）。
- **手改 md 也可以**，但注意：**認不得的值不會報錯也不會生效**，只會退回 Default 並在
  骰面與管理頁掛上 ⚠ 標記。用下拉就打不出錯字，這正是它用 enum 而非自由字串的理由。
- **新增一種 kind 要改 code**：`UCL_FreeTimeActivityKind` enum ＋ `UCL_FreeTimeGating` 的判定。
  兩邊都要動是刻意的 —— 一個沒有實作的標記會讓人以為那裡有一道邏輯，而它什麼都不做。

## 慣例

- `_` 開頭的檔案（如本檔）不算活動，掃描時跳過
- **下架用 `enabled: false` 或刪檔，判準是「這件事的缺席有沒有答案」**：
  - **有答案 → 可以刪檔。** `social-chat`（換骰即聊天，答案在
    [`Workflows/FreeTime_Cmd_Flow.md`](../../Workflows/FreeTime_Cmd_Flow.md)）、
    `game-qa`（**專案限定** —— 共用層不該放只有某個專案做得成的活動，
    要用的專案在**專案層**放自己的同 id md）皆 2026-08-18 刪檔。
  - **沒答案 → 留 `enabled: false`。** 例：`trpg` 待重做 —— 它會回來，
    而在回來之前「遊戲組怎麼只剩下棋」需要 md 自己回答。
  ⇒ 判準不是「暫時或永久」，是**刪掉之後那個問題還有沒有地方能答**。
- 對齊 EOV `docs/Glossary/` 的 per-entry md + frontmatter 前例
- 工具：`run_cmd.py --persona <me> run FreeTime --arg step=shuffle|list|show`（freetime.py 已於 2026-08-26 退役）
- 三池 spec：[`<UCL_Core>/Docs~/zh-Hant/Mechanics/FreeTime_System.md`](../../Mechanics/FreeTime_System.md) §4（2026-06-11 同步搬入 UCL_Core）
