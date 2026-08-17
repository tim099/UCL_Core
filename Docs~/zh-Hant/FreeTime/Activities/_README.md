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
min_minutes: 20              # 選填 — 建議所需分鐘 (Cmd_FreeTime 擲骰時剩餘時間不足 → 排尾標明「時間不夠」，不隱藏)
kind: Default                # 選填 — 特殊邏輯標記，見下節 (缺欄位 = Default)
---

# 閱讀 (自選讀書)

(活動詳細說明 / SOP / 相關 skill 連結 — agent 選定活動後用 `show --id reading` 深讀)
```

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

- **改既有活動**：Editor 開「自由時間管理」頁 → 下拉選活動 → 「特殊邏輯」下拉選一個。
  頁面會就地改寫本 md 的 `kind` 欄位（**不另存 override 設定** —— 活動的事實來源只有 md 一處）。
- **手改 md 也可以**，但注意：**認不得的值不會報錯也不會生效**，只會退回 Default 並在
  骰面與管理頁掛上 ⚠ 標記。用下拉就打不出錯字，這正是它用 enum 而非自由字串的理由。
- **新增一種 kind 要改 code**：`UCL_FreeTimeActivityKind` enum ＋ `UCL_FreeTimeGating` 的判定。
  兩邊都要動是刻意的 —— 一個沒有實作的標記會讓人以為那裡有一道邏輯，而它什麼都不做。

## 慣例

- `_` 開頭的檔案（如本檔）不算活動，掃描時跳過
- 對齊 EOV `docs/Glossary/` 的 per-entry md + frontmatter 前例
- 工具：`python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle|list|show|init`
- 三池 spec：[`<UCL_Core>/Docs~/zh-Hant/Mechanics/FreeTime_System.md`](../../Mechanics/FreeTime_System.md) §4（2026-06-11 同步搬入 UCL_Core）
