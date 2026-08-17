---
title: Cmd_Rule API
description: 酒館規則系統 —— 提案 / 撤銷 / 查詢規則，提案要花 token。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Rules/Cmd_Rule.cs
namespace: UCL.Core.EditorLib.AgentCommands.Rules
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_Rule

> 酒館規則系統 —— 提案 / 撤銷 / 查詢規則，提案要花 token。

## 1. 概覽

- **CommandType**：`Rule`
- **原始碼 ShortDescription**：Tavern Rule System — 提案 rule 消耗 100 token (需 balance ≥ 300), Tim revert 退還 100 token

**什麼時候用**：要把一條協作約定變成有帳可查的正式規則時。

## 2. 參數 (ArgsSchema)

- `op=propose|revert|list|get|enforce`
- `propose: rule_id=<id> title=<短摘要> body=<完整內容> [created_by=<bank-id, default Tim>] — 需 balance ≥ 300, debit 100`
- `revert: rule_id=<id> reason=<原因> [reverted_by=<bank-id, default Tim>] — 只有 Tim 可 revert, refund 100 給 creator`
- `list: [status=active|reverted|all (default active)] — 列規則表`
- `get: rule_id=<id> — 印單一 rule 完整內容`
- `enforce: rule_id=<id> target=<context> (v1 未實作, 預留 future automation hook)`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Rule --arg <k>=<v>
```

## 3. 注意

- 提案消耗 **100 token** 且需餘額 ≥ 300；Tim revert 時退還 100 給提案者。
- **只有 Tim 可以 revert** —— 這是設計上的權限，不是暫時限制。
- `enforce` 在 v1 未實作，是預留給未來自動化的 hook。**名字比實作大的東西要知道它現在什麼都不做。**

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
