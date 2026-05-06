---
title: Cmd_FindAssetUsages API
description: 反向查询 UCL_Asset 被引用位置 — 给定目标 Asset（例 RCG_CustomStatusData/Stun），扫描所有（或指定）UCL_Asset 子类型的全部实例，透过反射找出指向目标的 UCLI_AssetEntry 并记录 dotted field path，输出 (UsedBy_AssetType, UsedBy_ID, JSON 路径, 字段路径) 清单
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_FindAssetUsages.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_FindAssetUsages

## 1. 概览

`Cmd_FindAssetUsages` 是 [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) 的**反向**查询工具：

| | 顺向（Resolve） | 反向（FindUsages） |
|---|---|---|
| 起点 | 一份 Asset | 一份目标 Asset |
| 问题 | 「我引用了谁？」 | 「**谁引用了我？**」 |
| 输出 | 依赖链 / 范围 manifest | 引用点清单（含字段路径）|

给定目标 Asset（例：`RCG_CustomStatusData/Stun`），本 Cmd 扫描 `searchTypes` 列出（或预设全部）的 UCL_Asset 子类型实例，反射深入每份 asset 的字段树，找出指向目标的 `UCLI_AssetEntry`，并记录该引用的 **dotted field path**（如 `m_Effects[2].m_Setting.m_StatusEntry`）。

典型用途：
- 「`RCG_CustomStatusData/Stun` 被谁用？」 → 列出所有 RCG_CardData / RCG_ItemData / RCG_EquipmentData / 怪物技能 等引用点
- **重构安全网**：删 / 重命名 Asset 前先看影响范围
- **平衡分析**：某 Status 被多少卡片同时上 buff / 反向 debuff？

## 2. 参数格式 (Args Schema)

| 参数 | 必填 | 预设 | 说明 |
|---|:-:|---|---|
| `targetType` | ✅ | — | 被查询 Asset 的 C# Type 名称，例：`RCG_CustomStatusData` |
| `targetIds` | ✅ | — | 被查询 Asset ID（CSV 多笔），例：`Stun,Burn` |
| `searchTypes` | ❌ | （所有 UCL_Asset 子类）| 限定扫描的 Asset Type CSV |
| `outputPath` | ❌ | `AgentCommands/asset_usages_<Type>_<timestamp>.<ext>` | 输出路径（相对 Unity project root）|
| `format` | ❌ | `json` | 输出格式：`json` 或 `md` |
| `module` | ❌ | （所有模块）| 限定**使用方**所属模块 |
| `maxFieldDepth` | ❌ | `16` | 反射字段递归深度上限 |

> [!TIP]
> 若已知引用大概落在哪几类 asset 内，**强烈建议**指定 `searchTypes`。

## 3. 输出格式

### 3.1 JSON（预设）

```json
{
  "command": "FindAssetUsages",
  "timestamp": "2026-05-06T12:34:56",
  "targetType": "RCG_CustomStatusData",
  "targetIds": ["Stun"],
  "scannedTypes": 24,
  "scannedAssets": 873,
  "totalHits": 5,
  "usagesByTarget": [
    {
      "targetKey": "RCG_CustomStatusData:Stun",
      "hitCount": 5,
      "hits": [
        {
          "usedByType": "RCG_ItemData",
          "usedById": "EmotionalDamage",
          "path": "CardGame/Assets/.BuiltinModules/.../RCG_ItemData/EmotionalDamage.json",
          "exists": true,
          "fieldPath": "$.m_ItemEffects[0].m_Setting.m_StatusEntry"
        }
      ]
    }
  ]
}
```

### 3.2 Markdown（`format=md`）

依 target 分组的表格；0 命中的 target 会以 `> No usages found.` 区块列出。

## 4. 在 queue.json 中调用

```json
{
  "Id": "20260506-find-stun-usages",
  "Type": "FindAssetUsages",
  "Mode": "OneShot",
  "Args": {
    "targetType": "RCG_CustomStatusData",
    "targetIds": "Stun",
    "searchTypes": "RCG_CardData,RCG_ItemData,RCG_EquipmentData",
    "format": "md"
  },
  "Description": "找出所有引用 Stun 状态的 Card / Item / Equipment"
}
```

## 5. 联动行为

### 5.1 反射机制

- 对每个 `searchType`，呼叫 `UCL_Util<T>.Util.GetAllIDs()` 列出所有 ID
- 对每个 ID，用 `UCL_Util<T>.Util.GetAsset(id, true)` 载入物件实例
- 反射走访所有 instance fields（含 private）
- 遇到 `UCLI_AssetEntry` 即比对 `(entry.AssetType, entry.ID)` 是否命中目标
- 命中时回报 dotted field path
- 跳过 primitive / enum / string / `UnityEngine.Object`
- 用 `ReferenceEqualityComparer` 防止 cycle / shared 子物件重复展开

### 5.2 Field Path 格式

- 根节点为 `$`，字段用 `.FieldName`，集合索引用 `[i]`
- 范例：`$.m_Effects[2].m_Settings[0].m_StatusEntry`

### 5.3 与 ResolveAssetReferences 的差异

| | ResolveAssetReferences | FindAssetUsages |
|---|---|---|
| 方向 | 顺向 | 反向 |
| 跨 asset 跳转 | ✅ BFS 多层 | ❌ 只看「直接引用点」|
| 命中后行为 | 加入队列继续展开 | 记录字段路径后停止下钻 |
| 主成本 | 引用链长度 | 全资产数量 × 平均字段深度 |
| Field path | ❌ 不记录 | ✅ 记录 dotted path |

## 6. 限制

- 只解析**设计时 Asset 引用**（`UCLI_AssetEntry`）
- 预设扫所有 UCL_Asset 子类，大型项目请限定 `searchTypes`
- 反射只在 Editor 模式下执行（`#if UNITY_EDITOR`）

## 7. 关联文件

- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md)
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md)

---

## 其他语系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
