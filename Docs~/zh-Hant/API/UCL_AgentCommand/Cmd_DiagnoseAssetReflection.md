---
title: Cmd_DiagnoseAssetReflection API
description: 對每個 `UCL_Asset` 型別/ID 走 `GetAllIDs → GetAsset → 反射`，逐步回報哪一步炸掉。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_DiagnoseAssetReflection.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_DiagnoseAssetReflection

> 對每個 `UCL_Asset` 型別/ID 走 `GetAllIDs → GetAsset → 反射`，逐步回報哪一步炸掉。

## 1. 概覽

- **CommandType**：`DiagnoseAssetReflection`

**什麼時候用**：整批資產有異常、但不知道是「型別掃不到」「取不到資料」還是「反射踩雷」時。

## 2. 參數 (ArgsSchema)

- `searchTypes=CSV asset types to probe (default: all UCL_Asset subclasses)`
- `maxIdsPerType=Cap on IDs probed per type (default 0 = no cap)`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run DiagnoseAssetReflection --arg maxIdsPerType=5
```

## 3. 注意

- 報告會標明**失敗發生在哪一步**（GetAllIDs / GetAsset / ShallowReflect / DeepReflect）——這個分格才是它的價值，不然只會得到一句「壞了」。
- `deep=true` 會做完整遞迴反射（比照 FindAssetUsages），慢但抓得到深層欄位的雷；預設 shallow。
- 除錯時用 `maxIdsPerType=5` 先跑小樣本，確認流程對了再全跑。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
