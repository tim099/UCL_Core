---
title: Cmd_TypeInspect API
description: 型別自省 —— 給型別名回完整成員清單，解 agent「不知道某個 API 長怎樣」的摩擦。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/TypeInspect/Cmd_TypeInspect.cs
namespace: UCL.Core.EditorLib.AgentCommands.TypeInspect
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_TypeInspect

> 型別自省 —— 給型別名回完整成員清單，解 agent「不知道某個 API 長怎樣」的摩擦。

## 1. 概覽

- **CommandType**：`TypeInspect`
- **原始碼 ShortDescription**：Type introspection — 給 type 名回完整 member list (解 agent API discovery friction)

**什麼時候用**：要呼叫某個 C# API 但不確定簽名時（配 `Cmd_Invoke` 一起用最順）。

## 2. 參數 (ArgsSchema)

- `op=find|inspect|inspect_many`
- `find: type=短名或部分 full name — 列所有匹配 type 的 full name [max=N(default 60)]`
- `inspect: type=full name 或短名 [include=public|all|non_inherited] [kinds=method,property,field,ctor,event,nested] — 回完整 member list`
- `inspect_many: types=csv — 一次 inspect 多個 type`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run TypeInspect --arg <k>=<v>
```

## 3. 注意

- `find` 用短名或部分 full name 找型別；`inspect` 才列成員；`inspect_many` 一次看多個。
- `include=all` 會含繼承來的成員，`non_inherited` 只看該型別自己宣告的。
- 查到簽名後要**實際驗證行為**，走 `Cmd_Invoke` 呼叫它 —— 有簽名不代表你猜對了語意。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
