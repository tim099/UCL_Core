---
title: Cmd_SeedTavernRoomAssets API
description: 依 `rooms.json` roster 產生對應的 `UCL_ChatTavernRoomAsset` json 殼（一房一份）。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_SeedTavernRoomAssets.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_SeedTavernRoomAssets

> 依 `rooms.json` roster 產生對應的 `UCL_ChatTavernRoomAsset` json 殼（一房一份）。

## 1. 概覽

- **CommandType**：`SeedTavernRoomAssets`
- **原始碼 ShortDescription**：Seed UCL_ChatTavernRoomAsset .json shells from rooms.json roster (one per room).

**什麼時候用**：新增酒館房間後，要把 roster 上的房補成實體 Asset 時。

## 2. 參數 (ArgsSchema)

- `force=true|false 是否覆寫已存在的 Asset (default: false)`
- `onlyId=只 seed 指定 room id（空字串 = 全部 roster）`

```bash
senate ucmd run SeedTavernRoomAssets --arg force=false
```

## 3. 注意

- `force=false`（預設）**不覆寫**既有 Asset —— 已經手工調過的房不會被洗掉。
- `onlyId` 可只補單一房；空字串＝全 roster。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
