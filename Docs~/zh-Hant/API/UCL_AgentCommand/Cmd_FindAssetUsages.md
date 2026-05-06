---
title: Cmd_FindAssetUsages API
description: 反向查詢 UCL_Asset 被引用位置 — 給定目標 Asset（例 RCG_CustomStatusData/Stun），掃描所有（或指定）UCL_Asset 子型別的全部實例，透過反射找出指向目標的 UCLI_AssetEntry 並記錄 dotted field path，輸出 (UsedBy_AssetType, UsedBy_ID, JSON 路徑, 欄位路徑) 清單
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_FindAssetUsages.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_FindAssetUsages

## 1. 概覽

`Cmd_FindAssetUsages` 是 [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) 的**反向**查詢工具：

| | 順向（Resolve） | 反向（FindUsages） |
|---|---|---|
| 起點 | 一份 Asset | 一份目標 Asset |
| 問題 | 「我引用了誰？」 | 「**誰引用了我？**」 |
| 輸出 | 依賴鏈 / 範圍 manifest | 引用點清單（含欄位路徑）|

給定目標 Asset（例：`RCG_CustomStatusData/Stun`），本 Cmd 會掃描 `searchTypes` 列出（或預設全部）的 UCL_Asset 子型別實例，反射深入每份 asset 的欄位樹，找出指向目標的 `UCLI_AssetEntry`，並記錄該引用的 **dotted field path**（如 `m_Effects[2].m_Setting.m_StatusEntry`）。

典型用途：
- 「`RCG_CustomStatusData/Stun` 被誰用？」 → 列出所有 RCG_CardData / RCG_ItemData / RCG_EquipmentData / 怪物技能 等引用點
- **重構安全網**：刪 / 重命名 Asset 前先看影響範圍
- **平衡分析**：某 Status 被多少卡片同時上 buff / 反向 debuff？
- **設計連動可視化**：某 ItemEffect 被引用幾次、分布在哪些 ItemData？

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `targetType` | ✅ | — | 被查詢 Asset 的 C# Type 名稱，例：`RCG_CustomStatusData` |
| `targetIds` | ✅ | — | 被查詢 Asset ID（CSV 多筆），例：`Stun,Burn` |
| `searchTypes` | ❌ | （所有 UCL_Asset 子類）| 限定掃描的 Asset Type CSV，例：`RCG_CardData,RCG_ItemData` |
| `outputPath` | ❌ | `AgentCommands/asset_usages_<Type>_<timestamp>.<ext>` | 輸出檔案路徑（相對 Unity project root，即 `CardGame/`）|
| `format` | ❌ | `json` | 輸出格式：`json`（機器讀）或 `md`（人類讀，依 target 分組）|
| `module` | ❌ | （所有模組）| 限定**使用方**所屬模組（不影響 target 模組）|
| `maxFieldDepth` | ❌ | `16` | 反射欄位遞迴深度上限，防巨型 cycle |

> [!TIP]
> 若已知引用大概落在哪幾類 asset 內，**強烈建議**指定 `searchTypes` 縮小掃描範圍。預設全掃對大型專案會花上數秒到數十秒。

## 3. 輸出格式

### 3.1 JSON（預設）

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
        },
        {
          "usedByType": "RCG_CardData",
          "usedById": "ThunderStrike",
          "path": "CardGame/Assets/.BuiltinModules/.../RCG_CardData/ThunderStrike.json",
          "exists": true,
          "fieldPath": "$.m_OnPlayEffects[1].m_Settings[0].m_StatusEntry"
        }
        // ...
      ]
    }
  ]
}
```

### 3.2 Markdown（`format=md`）

```markdown
# Asset Usages — RCG_CustomStatusData

- **Targets**: Stun
- **Scanned**: 873 asset(s) across 24 type(s)
- **Total Hits**: 5
- **Generated**: 2026-05-06 12:34:56

## `RCG_CustomStatusData:Stun` (5 hit(s))

| UsedBy Type | UsedBy ID | Field Path | JSON Path |
|---|---|---|---|
| `RCG_CardData` | `ThunderStrike` | `$.m_OnPlayEffects[1].m_Settings[0].m_StatusEntry` | `CardGame/.../ThunderStrike.json` |
| `RCG_ItemData` | `EmotionalDamage` | `$.m_ItemEffects[0].m_Setting.m_StatusEntry` | `CardGame/.../EmotionalDamage.json` |
| ...
```

> [!NOTE]
> Markdown 模式下，**0 命中的 target** 會以 `> No usages found.` 區塊列出，避免使用者誤以為 Cmd 跑壞。

## 4. 在 queue.json 中呼叫

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
  "Description": "找出所有引用 Stun 狀態的 Card / Item / Equipment"
}
```

## 5. 連動行為

### 5.1 反射機制

- 對每個 `searchType`，呼叫 `UCL_Util<T>.Util.GetAllIDs()` 列出所有 ID
- 對每個 ID，用 `UCL_Util<T>.Util.GetAsset(id, true)` 載入物件實例
- 反射走訪所有 instance fields（含 private，因 UCL JSON serializer 也用 fields）
- 遇到 `UCLI_AssetEntry` 即比對 `(entry.AssetType, entry.ID)` 是否命中目標
- 命中時回報 dotted field path（`$.m_Foo[2].m_Bar.m_Entry`）
- 跳過 primitive / enum / string / `UnityEngine.Object`
- 用 `ReferenceEqualityComparer` 防止 cycle / shared 子物件重複展開

### 5.2 Field Path 格式

- 根節點為 `$`
- 欄位用 `.FieldName`
- 集合索引用 `[i]`
- 範例：`$.m_Effects[2].m_Settings[0].m_StatusEntry`

> [!IMPORTANT]
> Field path 是**反射時的 C# 欄位名稱**，不一定等於 JSON 序列化後的 key（多數情況下相同；少數含自訂序列化器的會差異）。當需要與 JSON 對位時請以 C# 類別定義為準。

### 5.3 與 ResolveAssetReferences 的差異

| | ResolveAssetReferences | FindAssetUsages |
|---|---|---|
| 方向 | 順向（起點 → 引用） | 反向（目標 ← 引用方） |
| 跨 asset 跳轉 | ✅ BFS 多層 | ❌ 只看「直接引用點」|
| 命中後行為 | 把被引用 asset 加入隊列繼續展開 | 記錄欄位路徑後停止下鑽 |
| 主成本 | 引用鏈長度 | 全資產數量 × 平均欄位深度 |
| Field path | ❌ 不記錄 | ✅ 記錄 dotted path |

## 6. 限制

- 只解析**設計時 Asset 引用**（`UCLI_AssetEntry`）；不解析 Localize Key / Sprite Key（這些是字串，不是 AssetEntry 型）
- 預設掃所有 UCL_Asset 子類，大型專案請限定 `searchTypes`
- 反射只在 Editor 模式下執行（`#if UNITY_EDITOR`）
- 「同一個欄位指向多個 target ID」會展開為多筆 hits（每筆對應一個 target）
- 反射 field 名稱 ≠ JSON key 時可能造成困擾（少見，但要注意）

## 7. 關聯文件

- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — 順向版本
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — 建立新 Cmd 的 SOP

---

## 其他語系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇹🇼 繁體中文（本檔）
