---
title: Cmd_AssetDump API
description: 把單一 `UCL_Asset` 反序列化後的欄位樹整棵 dump 出來，做 schema 漂移的鑑識。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_AssetDump.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_AssetDump

> 把單一 `UCL_Asset` 反序列化後的欄位樹整棵 dump 出來，做 schema 漂移的鑑識。

## 1. 概覽

- **CommandType**：`AssetDump`
- **原始碼 ShortDescription**：Dump deserialized field tree of a single UCL_Asset (schema-drift forensics).

**什麼時候用**：懷疑「磁碟上的 json 跟系統實際讀到的不一樣」時 —— 這支印的是**系統讀到的**那一份。

## 2. 參數 (ArgsSchema)

- `asset_type=UCL_Asset subclass short name, required (e.g. RCG_CharacterData)`
- `id=Asset ID, required (e.g. AncientTreeSpirit)`
- `maxDepth=Recursion depth cap (default 8)`
- `outputPath=Output md path relative to project root (default AgentCommands/asset_dump_<type>_<id>.md)`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run AssetDump --arg asset_type=RCG_CharacterData --arg id=AncientTreeSpirit
```

## 3. 注意

- `asset_type` 給的是**短名**（如 `RCG_CharacterData`），不是 full name。
- 這正是「不要掃磁碟 json 推導資產內容」的正解工具：有快取層與註冊表時，磁碟有檔 ≠ 系統看得到，系統看得到 ≠ 磁碟那份是當前值。
- `maxDepth` 預設 8；巢狀很深的資產記得調大，否則底層會被截掉而**看起來像沒有那些欄位**。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
