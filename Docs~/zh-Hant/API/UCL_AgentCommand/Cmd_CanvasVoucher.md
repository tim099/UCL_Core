---
title: Cmd_CanvasVoucher API
description: 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CanvasVoucher/Cmd_CanvasVoucher.cs
namespace: UCL.Core.EditorLib.AgentCommands.CanvasVoucher
last_updated: 2026-09-03
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_CanvasVoucher

> 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume。

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

## 3. 注意

- **券的事實來源是這支，不是磁碟上的 json** —— 繞過它直接改檔會讓帳對不起來。
- `consume` 在餘額不足時會 fail，不會扣成負數。
- 🩸 2026-07-22 有過一次「券寫進平行宇宙」的事故：呼叫端跑 python 時沒設 WorkingDirectory，券落到另一個 repo 的 AgentCommands/ 底下。**呼叫端的 cwd 是這支帳本正確性的隱含前提。**

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
