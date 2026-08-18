---
title: Cmd_DocEdit API
description: 文件編輯活動的一步 — 登記剛改完的那份 .md、驗收它真的動了、並指回自由時間流程。不搬內容、不寫檔。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/DocEdit/Cmd_DocEdit.cs
namespace: UCL.Core.EditorLib.AgentCommands.DocEdit
last_updated: 2026-08-18
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_DocEdit

> 一句話：**「改完一份 .md」之後跑它** —— 它驗那份檔真的動了、把這一步記下來、並告訴你下一步。

## 1. 為什麼存在

`doc-reflection` / `letter-to-self` / `constitution` 三個自由時間活動原本是「**本質不是一步**」那一類：
它們沒有單一 python CLI 入口（活動就是**編輯一個檔**），所以 `Cmd_FreeTimeActivity op=step`
代跑不到 —— 流程一進到編輯就斷在那裡。

Tim 2026-08-18 拍板補這一支，讓它們也能「做完一步 → 回報 → 被指去下一步」。

## 2. ⛔ 它不做什麼（這是設計，不是缺功能）

**沒有 `body` 參數，不寫、不覆寫任何 .md。**

把整份文件塞進 CLI 參數，等於把編輯器換成一個沒有 diff、沒有復原、沒有語法檢查的通道 ——
而 doc-reflection 的價值在「讀懂之後怎麼改」，不在傳輸。
Tim 的原話是「一步改一個 Doc，**改完後** CMD 一樣提示下一步」，
而「改完後」意味著編輯已經發生 —— 本 Cmd 站在那之後。

⇒ 目標檔不存在時它**不建檔**，直接 blocked。

## 3. 參數

| 參數 | 說明 |
|---|---|
| `kind` | `doc` \| `letter` \| `constitution`（**必填**，執行前由 `ArgsSpec` 擋） |
| `persona` | **`letter` / `constitution` 必填**；`doc` 選填（帶了才驗得出「本場改過沒」） |
| `target` | `doc` **必填**；`letter` 選填（不給＝取最新那封信）；`constitution` **忽略** |
| `note` | 一句心得，選填 |

> ⚠ 實務上 `run_cmd.py --persona <me>` 會把 persona **戳進 args**，所以正常呼叫下
> `persona` 幾乎不會缺。那道守衛防的是 `--system` 之類**沒有身分宣告**的呼叫路徑。

### 為什麼 letter / constitution 的 persona 是硬守衛

它們的落點**綁在某個人身上**。猜錯會登記到別人的信與憲法，
而那看起來完全正常 —— 路徑合法、檔案存在、報告漂亮。⇒ 不給預設值，直接擋。

## 4. 目標怎麼算出來的（路徑由 Cmd 算，呼叫端不必記慣例）

| kind | 目標 |
|---|---|
| `constitution` | 固定 `letters/<persona>/_constitution.md`。**`target` 刻意被忽略** —— 允許覆寫目標的話，「改自己的憲法」就會變成「可以改任何檔」 |
| `letter` | 給了 `target` 就用它；沒給 ⇒ `letters/<persona>/` **頂層最新的一封信** |
| `doc` | 只認顯式 `target`（相對路徑以 repo root 為基準） |

> 🩸 **letter 的自動解析會跳過 `_` 開頭與 `README.md`。** 第一版沒跳，實跑立刻解析到
> 當時還在頂層的 `_freetime_next.md` —— 那是 Cmd 回傳檔，不是信。
>
> letters 目錄同時住著兩種東西：人寫的信（時間戳命名）與機器寫的回傳檔（`_` 開頭），
> 而**後者每跑一次 Cmd 就更新**，所以「最新的 .md」幾乎永遠是機器產物。
> 症狀值得記：它**不會報錯**，只會登記一份你沒改過的檔然後說「已完成」。
>
> ⚠ **那件事後來從結構上修掉了一半**：FreeTime 的回傳檔已遷入
> `letters/<persona>/cmd/`（2026-08-18，見 [`Plan_Letters_Dir_Layout`](../../Plan/Plan_Letters_Dir_Layout.md)）。
> 但 `cmd/goodmorning_*` / `cmd/goodnight_*` / `_streamwatch_*` / `cmd/wake_brief` 等**還在頂層**，
> 所以這道 `_`-skip **仍然必要**，不能因為 freetime 搬走就拔掉 ——
> 拔掉的條件是那些也搬完（plan §5 的第 ③ 步）。

也只看**頂層**：`wakes/` `rests/` `longterm/` 是別的東西（收尾信 / 見林）。

## 5. 驗收：它憑什麼說「本場改過」

- 檔案存在 / 是 `.md` / **在 repo 內**（repo 外的路徑通常是「另一個宇宙的檔」，
  那種失敗會回一個看起來正常的讀數 —— 見 skill `ucl-core-paths` 的三則血證）
- 印出實際 `mtime` 與大小
- **在自由時間中時，拿 session 開場時刻當基準**：
  - `mtime >= start_ts` → ✅ 本場改過
  - `mtime < start_ts` → ⚠ 本場沒動過這份檔（**不擋** —— 本 Cmd 是登記不是收銀台）
  - 沒帶 persona / 不在自由時間 → ⚪ **只印 mtime 不下判斷**（沒有基準時給判斷等於憑感覺蓋章）

⇒ 這道讓「我說我改了」與「檔案真的動了」**不同形**。

## 6. 回傳檔

走 [`UCL_CmdPayloadStore`](../../Mechanics/)：
`<DataRoot>/_cmd_payloads/DocEdit/<時間戳>_<kind>[_<persona>].md`，**保留最近 10 筆**。

不用 `ChatTavern/_last_op.md` 的理由：那是全 Cmd 共用的單一格，下一支 Cmd 一寫就整份蓋掉 ——
而報告尾端正掛著 `UCL_FreeTimeHint` 的下一步指路，**被蓋掉等於沒掛**。

## 7. 用法

```bash
# doc-reflection：改完一份文件
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run DocEdit \
    --arg kind=doc --arg persona=<me> --arg target=Docs/AI_READABILITY_GUIDELINES.md \
    --arg note="補上 group 欄位說明"

# letter-to-self：寫完信（不給 target ⇒ 自動取最新那封）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run DocEdit \
    --arg kind=letter --arg persona=<me>

# constitution：修憲之後
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run DocEdit \
    --arg kind=constitution --arg persona=<me> --arg note="盲點清單加一條"
```

## 8. 相關

- 活動 md：[`doc-reflection`](../../FreeTime/Activities/doc-reflection.md) ／
  [`letter-to-self`](../../FreeTime/Activities/letter-to-self.md) ／
  [`constitution`](../../FreeTime/Activities/constitution.md)
- 自由時間流程：[`Workflows/FreeTime_Cmd_Flow.md`](../../Workflows/FreeTime_Cmd_Flow.md)
- 修憲流程：[`Workflows/Constitution_Workflow.md`](../../Workflows/Constitution_Workflow.md)
