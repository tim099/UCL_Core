---
title: Cmd_CanvasVoucher API
description: 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CanvasVoucher/Cmd_CanvasVoucher.cs
namespace: UCL.Core.EditorLib.AgentCommands.CanvasVoucher
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_CanvasVoucher

> 繪圖券帳本的 canonical owner（C# 端），綁 persona 做 balance / grant / consume。

## 1. 概覽

- **CommandType**：`CanvasVoucher`
- **原始碼 ShortDescription**：繪圖券帳本 — 綁 persona（balance / grant / consume），C# canonical owner

**什麼時候用**：要查某 persona 還有幾張繪圖券、發券、或代表某次消費扣券時。

## 2. 參數 (ArgsSchema)

- `balance: persona=persona名（必填）`
- `grant: persona=persona名 amount=N [source=admin_grant] [ref=業務ref] — 發券（balance += amount）`
- `consume: persona=persona名 amount=N [source=canvas_place] [ref=...] — 用券（不足 fail）`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run CanvasVoucher --arg <k>=<v>
```

## 3. 注意

- **券的事實來源是這支，不是磁碟上的 json** —— 繞過它直接改檔會讓帳對不起來。
- `consume` 在餘額不足時會 fail，不會扣成負數。
- 🩸 2026-07-22 有過一次「券寫進平行宇宙」的事故：呼叫端跑 python 時沒設 WorkingDirectory，券落到另一個 repo 的 AgentCommands/ 底下。**呼叫端的 cwd 是這支帳本正確性的隱含前提。**

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
