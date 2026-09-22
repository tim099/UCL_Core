---
title: Cmd_SessionClose API
description: 關掉某 persona **過期殘留**的 session（觀影場會補結算）—— 所有關場路徑走同一個門。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Session/Cmd_SessionClose.cs
namespace: UCL.Core.EditorLib.AgentCommands.Session
last_updated: 2026-09-22
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_SessionClose

> 🩸 在這支之前，「補收工」只有一個入口：`UCL_SessionAdminPage` 上的那顆鈕，
> 而它**直接呼叫** `SCP_ActivitySessionStore.Close`（三欄一翻就走）⇒ 觀影場的**結算被跳過**：
> 酬勞蒸發、seq 區間永久消失（那場觀察再也匯不進書），
> **而印出來的字跟正常收工一模一樣。**

## 1. 概覽

- **CommandType**：`SessionClose`
- **原始碼 ShortDescription**：關掉某 persona **過期殘留**的 session（觀影場會補結算）。進行中的場不從這裡關 —— 那要走該 kind 的 `step=end`。

本 Cmd 把那條路拆成三段分開講：**① 權威狀態 ② 結算（per-kind） ③ 回報**。

## 2. 參數 (ArgsSchema)

- `target_persona=<誰的場>`（必填，**不猜身分**）
- `confirm=1`（必填 —— 這會**寫別人的 session 檔**，觀影場還會**發薪**）
- `reason=<一句話>`（選填，預設 `closed-by-cmd`；會寫進 `end_reason`）

```bash
senate ucmd run SessionClose --arg target_persona=<誰> --arg confirm=1 --arg reason=<一句話>
```

## 3. ⛔ 射程：只有「殘留」

**殘留 ＝ `active` 但已過 `end_ts`。**
⛔ **進行中的場不從這裡關** —— 那要走各 kind 自己的收工步驟（`step=end`），
因為正常收工還有**收工公告與同場者判定**。

📌 這條界線是從管理頁**原樣搬過來**的，⛔ 不是本 Cmd 新加的限制。

## 4. 為什麼它是一支 Cmd 而不是一顆按鈕

管理頁要搬去 Senate（TASK-0127 ⑥），而**結算是金流、金流不搬**（TASK-0106 拍板 B 不動）
⇒ Senate 那側只能**委派回 Editor**；而委派需要一個目標，**「頁面上的一顆鈕」不是目標**。
⭐ 順帶的收益：補收工從此**有回傳檔、有讀數、agent 也用得到**（在此之前只有 GUI 按得到）。

## 5. 關聯

- 正常收工（進行中的場）→ 各 kind 自己的 `step=end`
- 改 C# 的施工場 → [Cmd_Coding](./Cmd_Coding.md)
- 場的現況查詢 → [Cmd_SessionStatus](./Cmd_SessionStatus.md)
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
