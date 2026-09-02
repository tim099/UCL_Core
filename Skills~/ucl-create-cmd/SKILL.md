---
name: ucl-create-cmd
description: |
  建立新的 UCL_AgentCommand handler — 寫一支 `Cmd_<Name>.cs` 子類給 queue.json 觸發。
  使用者要求新增 agent command、加 RPC handler、做新 Cmd、或詢問 UCL_AgentCommandHandlerBase 怎麼繼承時用本 skill。
  涵蓋命名規範、檔案位置決策（UCL_Core 內 vs 下游模組）、metadata 必填欄位、ExecuteAsync 撰寫守則、Editor 驗收步驟。
trigger: { on_intent: ["新增 AgentCommand", "新增指令", "Create Cmd", "Create Command"] }
---

# UCL Create Cmd — 新增 AgentCommand Handler

> 設計哲學：**convention-over-configuration**。繼承 `UCL_AgentCommandHandlerBase` 並擺對位置 → 下次 domain reload 由 `UCL_AgentCommandRegistry` 反射自動發現。**不要碰 Registry**。

## 必讀

完整 SOP + 決策樹 + 地雷清單 → `ucl_core:Docs~/zh-Hant/Workflows/Create_Cmd_Workflow.md`

架構背景 → `ucl_core:Docs~/zh-Hant/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md`

## 五分鐘骨架

```
[1] 想清楚 CommandType（PascalCase，AppDomain 全域唯一）
[2] 選位置（決策樹）— UCL_Core 內 vs 下游模組
[3] 開檔 Cmd_<Name>.cs : UCL_AgentCommandHandlerBase
[4] 覆寫 4 個 metadata: CommandType / ShortDescription / ArgsSchema / HelpURL
[5] 寫 ExecuteAsync(args, token)
[6] 觸發 Recompile → queue.json 跑一次驗收
```

## 位置決策（重要）

- **跨專案通用** → UCL_Core 內 `<UCL_Core>/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`
- **專案特定** → 下游模組的 AgentCommands 目錄（例：EOV `CardGame/Assets/Scripts/RCG_AgentCommands/`）

別在 UCL_Core 塞專案邏輯 — 破壞跨專案重用性。

## 高頻地雷

- CommandType 撞名 → Registry 反射時其中一個被默默蓋掉
- 漏寫 `ArgsSchema` → 上游 caller 不知道參數要傳什麼
- `ExecuteAsync` 未尊重 cancellation token → 長跑 Cmd 卡死 Editor
- 把 Cmd 放在 runtime assembly → Editor-only API 引用失敗
- RejectLastOp/ResolveLastOp 不在 base class → 各 Cmd 自定義 internal static helper, 詳見 Create_Cmd_Workflow.md

## 驗收

新增完跑：
```bash
senate ucmd run <YourCommandType> --persona <me> --arg key=value
```
