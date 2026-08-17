---
title: Cmd_BuildAddressable API
description: 跑 Addressables 內容打包，讓 agent 能在不開 Build 視窗的情況下驗證 addressable 設定與抓 catalog 錯誤。
source_file: Assets/Plugins/UCL_Core/Editor/BuildProcessors/Cmd_BuildAddressable.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_BuildAddressable

> 跑 Addressables 內容打包，讓 agent 能在不開 Build 視窗的情況下驗證 addressable 設定與抓 catalog 錯誤。

## 1. 概覽

- **CommandType**：`BuildAddressable`
- **原始碼 ShortDescription**：Build Addressables content (AddressableAssetSettings.BuildPlayerContent) so agent can verify addressable build / catch catalog errors.

**什麼時候用**：改完 addressable group / label / 打包設定後，想確認「真的打得起來」時。

## 2. 參數 (ArgsSchema)

- `clean=true|false (default true — build 前先 CleanPlayerContent 清舊 catalog)`
- `outputPath=結果報告 md 路徑 (相對專案根，預設 AgentCommands/addressable_build_<ts>.md)`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run BuildAddressable --arg clean=true
```

## 3. 注意

- `clean=true`（預設）會先 `CleanPlayerContent` 清舊 catalog —— 想驗的是**乾淨重建**多半要保持 true。
- 結果落成一份 md 報告（`outputPath` 可指定），**看報告不要看 exit code** —— Cmd 成功只代表流程跑完。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
