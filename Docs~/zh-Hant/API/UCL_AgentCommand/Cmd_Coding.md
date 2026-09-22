---
title: Cmd_Coding API
description: Coding session（改 C# 的施工場）—— 進場／改狀態／退出前過編譯閘；Unity 與 Senate 兩個入口寫同一個檔位。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Session/Cmd_Coding.cs
namespace: UCL.Core.EditorLib.AgentCommands.Session
last_updated: 2026-09-22
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_Coding

> 改 C# 這件事在此之前**沒有「誰正在做」的資料** —— 於是編譯紅燈是誰造成的只能靠人肉歸因。
> 🩸 血證：2026-08-26 @basecamp 驗 TASK-0051 時，ErrorLog 混入 @summit TASK-0052 施工中的三筆紅。

## 1. 概覽

- **CommandType**：`Coding`
- **原始碼 ShortDescription**：Coding session：改 C# 前進場（**同一範圍**至多一人；不宣告 scope ＝ 全域獨佔）／場中改狀態／退出前過編譯閘。

⚠ **兩個入口寫同一個檔位**（`sessions/<persona>.json`）⇒ 互相擋得到：

| 入口 | 指令 | 需要 Editor |
|---|---|---|
| Unity 側 | `senate ucmd run Coding --arg step=…` | ✅ |
| Senate 側 | `senate cmd coding --arg op=…` | ⛔ 不需要 |

## 2. 參數 (ArgsSchema)

- `step=start|status|end`（必填）
- `persona=<名字>`（必填，**不猜身分**）
- `status=<一句話在改什麼>`（`start`／`status` 必填）
- `hours=<租期小時數>`（`start` 選填；`status` 會用它續期）
- `scope=<施工範圍，絕對路徑，取施工的最大範圍>`（`start` 選填）
- `force=1`（`end` 專用：跳過編譯閘，**需同時給 `reason`**）／`reason=<為什麼要 force>`

```bash
senate ucmd run Coding --arg step=start --arg persona=<你> --arg status=<在改什麼> --arg scope=<絕對路徑>
```

## 3. `scope` 是「別人能不能同時開工」的那個開關

範圍**不重疊**的人可以同時開場（TASK-0201）。
⛔ **不給 `scope` ＝ 整個 kind 全域獨佔**（舊行為）⇒ 你會擋掉所有人。
⚠ 而宣告要取**最大範圍**：宣告得比實際改的窄 ⇒ 閘擋不住真正會撞的人，
**而失效的樣子是「兩個人都進場了、然後改到同一支檔，閘不會叫」**。

## 4. 退出閘

`step=end` 會**讀** `.compile_status.json`（⛔ 只讀不寫）。編譯有紅字時擋下；
真的要走就 `force=1` ＋ `reason=<理由>` —— 兩個一起給，**讓跳過這件事留下痕跡**。

## 5. 關聯

- 動工時機、`tasks=` 綁單與自動收場 → skill `ucl-task` §2.5（⛔ 本檔不重抄）
- 範圍判準與編譯閘射程 → skill `ucl-coding` §「改 C# 前先進場」
- 殘留場的補收工（**不是**正常收工）→ [Cmd_SessionClose](./Cmd_SessionClose.md)
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
