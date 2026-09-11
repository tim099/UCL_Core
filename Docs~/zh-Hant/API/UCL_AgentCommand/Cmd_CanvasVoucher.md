---
title: Cmd_CanvasVoucher API
description: 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume / usage。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CanvasVoucher/Cmd_CanvasVoucher.cs
namespace: UCL.Core.EditorLib.AgentCommands.CanvasVoucher
last_updated: 2026-09-11
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_CanvasVoucher

> 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume / usage。

## 1. 概覽

- **CommandType**：`CanvasVoucher`
- **原始碼 ShortDescription**：繪圖券帳本 — 綁 persona（balance / grant / consume），C# canonical owner

**什麼時候用**：要查某 persona 還有幾張繪圖券、發券、或代表某次消費扣券時。

## 2. 參數 (ArgsSchema)

- `balance: persona=persona名（必填）` —— 回**三個**數字：可花總額 / 永久券 / 未過期限時券
- `grant: persona=persona名 amount=N [source=admin_grant] [ref=業務ref] [expires_at=<UTC ISO>]`
  —— 發券（**`expires_at` 空＝永久券**；帶了＝限時券，到期自動作廢並記 history）
- `consume: persona=persona名 amount=N [source=canvas_place] [ref=...]`
  —— 用券（**先花快過期的**；可花總額不足 fail，不部分扣款）
- `usage: persona=persona名 ref=批次ref（必填）`
  —— **唯讀**，回那一批的 `granted` / `used` / `forfeited`，並明寫**讀數源**（`batches` 或 `history`）

```bash
senate ucmd run CanvasVoucher --arg <k>=<v>
```

### 機讀出口（`balance` 的 values 欄，2026-09-03 起）

| 欄 | 意義 |
|---|---|
| `spendable` | 可花總額（未過期限時 ＋ 永久） |
| `permanent` | 永久券（存量，不會過期） |
| `expiring` | 未過期限時券（到期即作廢，過期後這個數字自己會掉） |
| `persona` | 查的是誰 |

⚠ **三個數字問的是不同的問題**，所以刻意**不合併成一個 `balance` 欄** ——
合併就是替使用者挑一種，而讀的人會拿它當成自己心裡想的那一種（那不會報錯）。

🩸 為什麼補這幾欄（basecamp 2026-09-03，TASK-0114 ②）：本 op 原本**只寫人讀的 `_last_op.md`**，
於是程式消費端只剩兩條路 —— 去 regex 那份 md（措辭一改就靜默失配，**而失配的樣子跟
「這個 persona 沒有券」一模一樣**），或自己重算一份券帳（兩寫者 drift，正是本 Cmd 存在要防的事）。
現場讀數：補之前 Senate CLI 的畫布閘讀不到券數 ⇒ 回 `-1`（「不知道」，不是 0）；補之後
`expiring=0` / `permanent=314`，與 python `canvas.py voucher --sub balance` 異源同值。

### 機讀出口（`usage` 的 values 欄，2026-09-11 起 —— TASK-0198）

| 欄 | 意義 |
|---|---|
| `found` | `1`／`0` —— 這一批查得到嗎 |
| `source` | `batches`（還在帳上）／`history`（已結清，自 `grant` − `expire` 結算）／`none`（查無） |
| `granted` / `used` / `forfeited` | 發放 / 用掉 / 作廢　⚠ **`found=0` 時這三欄不發** |

⛔ **查無時刻意不發那三欄。** 一個 `granted=10` 擺在 `found=0` 旁邊，就是把「10 − 0」這個減法
遞給下一個呼叫端 —— 而那正是 TASK-0195 的原始公式（查無被算成用完）。
**欄位缺席會讓機讀端炸，帶一個不可用的數字會讓它靜默算錯。**
🩸 本 op 第一版就犯了這一格，抓到它的是把五個 case 的機讀輸出並排成表，不是更仔細看 code。

📌 **為什麼要有這支**（判準④）：「這一批用了幾張」原本只有一個消費端（自由時間收工公告）⇒
要驗那條回退路，唯一的尺就是被驗的那支本身（同源，只證明一致性）。本 op 是第二個呼叫者。

## 3. 注意

- **券的事實來源是這支，不是磁碟上的 json** —— 繞過它直接改檔會讓帳對不起來。
- `consume` 在餘額不足時會 fail，不會扣成負數。
- **批次會被清掉**（花完，或過期後的下一次寫入）⇒ 「這一批用了幾張」要走 `usage`，
  ⛔ 不要自己拿 `grant` − 現有餘額去推（那是 TASK-0195）。
- 🩸 2026-09-11（TASK-0198）**清理留痕改成逐批可歸戶**：過期一批一筆 `expire`
  （`ref`＝批次 ref、`batch`＝uuid，人看的「到期作廢」移到 `source`）；花完新增一筆 `exhaust`。
  ⚠ 在此之前 `expire` 是**加總的**、`ref` 欄填的是人看的字串 ⇒ **2026-09-11 之前被清掉的批次
  永久只能答「查無」**。那是舊資料的事實，⛔ 不准為了有個數字去推導它們。
- `history` **無上界**（零裁剪碼，2026-09-11 實測最長 461 筆全在）⇒ `usage` 的 history 路不會因舊列被丟掉而失效。
- 🩸 2026-07-22 有過一次「券寫進平行宇宙」的事故：呼叫端跑 python 時沒設 WorkingDirectory，券落到另一個 repo 的 AgentCommands/ 底下。**呼叫端的 cwd 是這支帳本正確性的隱含前提。**

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
