---
title: Cmd_SessionStatus API
description: 查某 persona 此刻在哪種 session（自由時間…），或列出全部 session 檔的總覽。read-only。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Session/Cmd_SessionStatus.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-18
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_SessionStatus

> 查某 persona 此刻在哪種 session，或列出全部 session 檔的總覽。**read-only，不寫任何 session。**

## 1. 概覽

- **CommandType**：`SessionStatus`
- **判準來源**：`UCL_SessionService`（與 `UCL_SessionAdminPage`、`Cmd_FreeTime` 同一份）

**什麼時候用**：想知道「這個人現在在不在自由時間」、或某人的 session 是不是超時沒收工的殘留。

**為什麼需要一支 Cmd 而不是自己 cat 那個 json**：因為「在不在」**不是單看 `active` 就能答** ——
`active` 只在有人真的跑收工步驟時才被翻成 false，超時就消失的人會把 `true` 留在檔案裡。
判準（`active` **且**未過 `end_ts`）收在 `UCL_SessionService.IsRunningAt` 一處，本 Cmd 只是把它曝光給 CLI。

## 2. 參數 (ArgsSchema)

| 參數 | 說明 |
|---|---|
| `persona=<名字>` | `scope=persona` 時**必填** —— 不猜「現在是誰」（多 persona 環境猜錯會回報別人的狀態，而那看起來完全正常） |
| `scope=persona\|all` | 預設 `persona`。`all` 列出每個已登記 kind 底下**所有** session 檔（含已收工的歷史） |

```bash
# 某人現在在哪種 session
senate ucmd run SessionStatus --persona <me> \
    --arg persona=<誰>

# 全部 persona 的總覽
senate ucmd run SessionStatus --persona <me> --arg scope=all
```

## 3. 回傳檔怎麼讀

三種狀態，**刻意分開印**（它們對你的下一步不同）：

| 印出來的 | 意思 | 下一步 |
|---|---|---|
| 🟢 進行中 | `active` 且未過 `end_ts` | 正常，可繼續走該 session 的流程 |
| ⚠ 殘留（active 但過期） | 超時沒回來收工 | 需要補收工 —— 走該 session 的收工步驟，或 `UCL_SessionAdminPage` |
| ⚪ 已收工 / 無 session 檔 | 前者有歷史、後者連檔都沒有 | 要開新場就開 |

> ⚠ **回傳檔第一行的「掃描範圍（已登記 kind）」是讀數的一部分，不是裝飾。**
> 空結果的語意是「在**這些** kind 裡沒查到」，不是「這個人不在任何 session」——
> 未登記的種類（見 `UCL_SessionKind.Kinds` 註解）根本沒被看過。
> 沒印掃描範圍的「沒查到」會被讀成後者，而那兩件事差很多。

## 4. 相關

- Session 資料模型與判準：`UCL_SessionBase` / `UCL_SessionService`
- 後台檢視與補收工：[`UCL_EditorPage/UCL_SessionAdminPage.md`](../../UCL_EditorPage/UCL_SessionAdminPage.md)
- 自由時間流程：[`Workflows/FreeTime_Cmd_Flow.md`](../../Workflows/FreeTime_Cmd_Flow.md)
