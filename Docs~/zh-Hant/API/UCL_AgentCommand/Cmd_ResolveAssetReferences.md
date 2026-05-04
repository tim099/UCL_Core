---
title: Cmd_ResolveAssetReferences API
description: 批次解析 UCL_Asset 的連動 Asset 鏈 — 給定起點 Asset 用反射遞迴掃描所有 UCLI_AssetEntry 引用，限制深度與去重後輸出全部 (AssetType, ID, JSON 路徑) 清單，方便 AI agent 一次取得整個依賴範圍
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_ResolveAssetReferences.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_ResolveAssetReferences

## 1. 概覽

`Cmd_ResolveAssetReferences` 給定一份起點 UCL_Asset（例：`RCG_StoryData/AbandonedTemple`），用 BFS 遞迴掃描其欄位內所有 `UCLI_AssetEntry` 引用 — 每碰到一個就把被引用的 Asset 加入隊列繼續展開 — 直到 `maxDepth` 上限。最後輸出所有被觸及到的 Asset 清單，每筆含 **(AssetType, ID, JSON 檔案路徑, 父引用)**，路徑為**專案根目錄相對路徑**方便 AI agent 直接 Read。

典型用途：
- AI agent 想理解一個 Story 的完整資源範圍 → 一次拿到 Story + BattleSet + Unit + Item + DropPool 的所有 JSON 路徑
- Multi-Asset Blueprint 的 A1 階段 → 自動填 EXISTING asset 的 manifest（Phase A1 會用此 Cmd 拉出範圍）
- Cross-impact 分析 → 修改一個 Asset 前先看誰會被波及

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `assetType` | ✅ | — | 起點 Asset 的 C# Type 名稱，例：`RCG_StoryData` / `RCG_BattleSet` |
| `assetIds` | ✅ | — | 起點 Asset ID（CSV 多筆），例：`AbandonedTemple,MysteriousCave` |
| `maxDepth` | ❌ | `3` | BFS 最大層級。`0` = 只列起點本身、`1` = 起點 + 直接引用、`3` = 三跳內全部 |
| `outputPath` | ❌ | `AgentCommands/asset_refs_<Type>_<timestamp>.<ext>` | 輸出檔案路徑（**相對 Unity project root，即 `CardGame/`**）→ 預設實際落在 `CardGame/AgentCommands/...` |
| `format` | ❌ | `json` | 輸出格式：`json`（機器讀）或 `md`（人類讀，含 tree） |
| `module` | ❌ | （所有模組） | 限定模組名（如 `Core` / `Fate`），預設不限 |

## 3. 輸出格式

### 3.1 JSON（預設）

```json
{
  "command": "ResolveAssetReferences",
  "timestamp": "2026-05-05T12:34:56",
  "rootType": "RCG_StoryData",
  "seeds": ["AbandonedTemple"],
  "maxDepth": 3,
  "totalAssets": 7,
  "foundOnDisk": 7,
  "assets": [
    {
      "key": "RCG_StoryData:AbandonedTemple",
      "assetType": "RCG_StoryData",
      "id": "AbandonedTemple",
      "depth": 0,
      "path": "CardGame/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/RCG_StoryData/AbandonedTemple.json",
      "exists": true,
      "parent": "<root>",
      "refs": ["RCG_BattleSet:Event_AbandonedTemple"]
    },
    {
      "key": "RCG_BattleSet:Event_AbandonedTemple",
      "assetType": "RCG_BattleSet",
      "id": "Event_AbandonedTemple",
      "depth": 1,
      "path": "CardGame/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/RCG_BattleSet/Event_AbandonedTemple.json",
      "exists": true,
      "parent": "RCG_StoryData:AbandonedTemple",
      "refs": ["RCG_UnitData:AutomataMaid", "RCG_ItemData:EnergyCore", "RCG_ItemData:OverclockModule"]
    }
    // ... 其他層級
  ]
}
```

### 3.2 Markdown（`format=md`）

含 Flat Path 表格 + Reference Tree 視覺化縮排清單。

## 4. 在 queue.json 中呼叫

```json
{
  "Id": "20260505-resolve-abandonedtemple",
  "Type": "ResolveAssetReferences",
  "Mode": "OneShot",
  "Args": {
    "assetType": "RCG_StoryData",
    "assetIds": "AbandonedTemple",
    "maxDepth": "3",
    "format": "md"
  },
  "Description": "解析 AbandonedTemple Story 的完整 Asset 依賴鏈"
}
```

## 5. 連動行為

### 5.1 反射機制
- 對每個訪問到的 Asset，反射走訪所有 instance fields（含 private，因 UCL JSON serializer 也用 fields）
- 遇到 `UCLI_AssetEntry` 即記錄並加入 BFS 隊列
- 遇到集合（List / Array / IEnumerable）展開逐一檢查
- 跳過 primitive / enum / string / `UnityEngine.Object`
- 用 `ReferenceEqualityComparer` 防止 cycle / shared 子物件重複展開

### 5.2 去重
以 `<AssetType>:<ID>` 為 key。同一 Asset 若在多條路徑都被引用，只解析一次（記第一個發現的 parent）。

### 5.3 找不到的 Asset
若引用的 ID 在 UCL_ModuleService 中不存在 → `exists: false` + `path: ""`，仍列出供使用者辨識（可能是設計地雷或拼字錯誤）。

## 6. 限制

- 只解析**設計時 Asset 引用**（UCL_AssetEntry）；不解析 Localize Key / Sprite 引用（這些是字串，不是 AssetEntry 型）
- 反射只在 Editor 模式下執行（`#if UNITY_EDITOR`）
- 巨大的圖（>1000 nodes）建議降低 maxDepth 或限定 module

## 7. 關聯文件
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [Cmd_DebugLog API](./Cmd_DebugLog.md)
- [Blueprint_Workflow](eov_docs:Workflows/Blueprint_Workflow.md)
