---
title: UCL_Core 多型支援架构
description: 解释 [SerializeReference]、UCLI_TypeListable、UCL_PolymorphicHelper、UCL_TypeReflectCache 四者在 UCL_Asset 编辑（GUI）+ 序列化（JSON）两条路径中的角色与互动，以及加新多型字段的标准写法。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/InterfaceCore/
namespace: UCL.Core
last_updated: 2026-05-06
target_audience: [Tools_Maintainer, Gameplay_Programmer, AI_Agent]
---

# UCL_Core 多型支援架构

## 1. 四个元件

```
[SerializeReference] (Unity 内建 attribute，加在字段)
UCLI_TypeListable / UCLI_TypeList (UCL_Core 接口标记，加在型别)
        │
        ▼
UCL_PolymorphicHelper       ← 多型 SSOT
  IsPolymorphicField / IsPolymorphicElement / GetConcreteSubtypes
        │
        ▼
UCL_TypeReflectCache        ← per-(Type, SaveMode) 字段 metadata，不预过滤
  m_Entries: List<UCL_FieldEntry>
  UCL_FieldEntry: m_IsPolymorphicField / m_HideOnGUI / m_HideInJson / m_Conditional / ...
                  GetAttr<T>() — lazy + 缓存
        │
        ▼
GUI 路径                                 JSON 路径
  FieldInfoCache (adapter)                 SaveFieldsToJson
  TypeFieldInfoCache (adapter)             LoadFieldFromJson
  过滤: !m_HideOnGUI                       过滤: !m_HideInJson && !MulticastDelegate
                                           runtime: m_Conditional?.IsShow(obj)
```

## 2. 两个多型信号

| 信号 | 加在哪 | 含意 | 触发谁 |
|---|---|---|---|
| `[SerializeReference]` | **字段** | 「这个字段的执行期值可能是宣告型的子类别」 | GUI dropdown、JSON ClassName 包装 |
| `UCLI_TypeListable` | **型别**（接口 implement）| 「这个型别当作集合元素时要保留子类资讯」 | JSON IList per-item ClassName 包 |

两者可同时用。单字段多型只需 `[SerializeReference]`。集合元素多型需要型别侧的接口 implement，或 Step 3a 自动侦测 wrapped 格式。

## 3. 标准写法

### 3.1 单字段

```csharp
public abstract class MyBase { ... }
public class MyConcrete : MyBase { ... }

public class MyOwner
{
    [SerializeReference] public MyBase m_Field;     // ✅ GUI dropdown + JSON round-trip
}
```

无需 base 实作 `UCLI_TypeListable`。GUI 透过 `UCL_PolymorphicHelper.GetConcreteSubtypes` 列出子类；JSON 包 `{ClassName, ClassData}`。

### 3.2 集合

```csharp
[SerializeReference] public List<MyBase> m_List;    // ✅ Step 3a 后 list item round-trip
```

JSON layout:

```json
"m_List": {
  "ClassName": "List<MyBase> AQN",
  "ClassData": [
    { "ClassName": "MyConcrete, ...", "ClassData": { ... } },
    ...
  ]
}
```

### 3.3 例外：`UnityJsonSerializableObject` 子类元素

仍正常工作 — JSON 走元素自身的 `SerializeToJson` / `DeserializeFromJson`（已产生 ClassName 包装）。Save / Load 两边都会侦测并走原路径，**不**重复包装。

## 4. 反射 cache 用法

```csharp
var aCache = UCL_TypeReflectCache.Get(typeof(MyAsset), JsonConvert.SaveMode.Unity);
foreach (var aEntry in aCache.m_Entries)
{
    if (aEntry.m_HideInJson) continue;
    if (aEntry.m_IsPolymorphicField) { /* 多型路径 */ }
    var aFolderExp = aEntry.GetAttr<UCL_FolderExplorerAttribute>();  // lazy + 缓存
}
```

**不预过滤** — GUI 用 `!m_HideOnGUI`，JSON 用 `!m_HideInJson && !m_IsMulticastDelegate`。两条路径共享同一份字段集合是 cache 抽离的核心。

## 5. 为什么 `UCL_FieldEntry` ctor 不能碰 service

Cache 在 JSON 载入路径很早被触发（载入 `UCL_LocalizeAsset` 时就会构造 cache）。此时 `UCL_ModuleService` / `UCL_LocalizeManager` 等 service 可能尚未 init。

**禁忌**（会引爆早期载入 NRE / 循环）：

| 操作 | 理由 |
|---|---|
| `UCL_LocalizeManager.Get(header)` | LocalizeManager 载入 `UCL_LocalizeAsset` → cache 构造 → LocalizeManager → 循环 → StackOverflow |
| `GetCustomAttributes(true)` | 构造**所有** attribute 实例，包含 `[UCL_FolderExplorer]` 那种 ctor 内呼叫 `UCL_ModuleService` 的重型 attribute（service 未 init → NRE） |

**安全**：`GetCustomAttribute<T>()` 只构造 T。预抓的旗标（SerializeReference / HideInJson / Conditional / FormerlyAs / HideOnGUI / AlwaysExpendOnGUI）的 attribute 类别没有 service 依赖。

冷门 attribute 走 `GetAttr<T>()` lazy + dict cache — 只在 GUI render 等 service-ready 路径才反射。

## 6. JSON list 多型机制（Step 3a）

**Save**：`[SerializeReference] List<...>` → 外层 `{ClassName, ClassData}` + per-item `ObjectToJson`。元素是 `UnityJsonSerializableObject` 子类时退回原 `ObjectToJson(整个 list)` 路径。

**Load**：`LoadDataFromJson` IList 分支侦测两个信号之一:
- `UCLI_TypeList(able)` 元素型（既有路径）
- 第一个 item 含 `ClassName` 键（新自动侦测）

两者都走 per-item `JsonToObject`。`UnityJsonSerializableObject` 元素双边排除 — 由 `DataToObject` 内专属 handler 处理。

## 7. 相关文件

- 📋 [SerializeReference_Symmetry_Plan](../Plan/SerializeReference_Symmetry_Plan.md)
- 📖 [DevLog 00005](../../../DevLogs~/00005_2026-05-06.md)
- 🤖 [Cmd_DiagnoseAssetReflection](../API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md)
- 🤖 [Cmd_FindAssetUsages](../API/UCL_AgentCommand/Cmd_FindAssetUsages.md)

---

## 其他语系

- 🇬🇧 [English](../../en/Architecture/Polymorphism_In_UCL.md)
- 🇯🇵 [日本語](../../ja/Architecture/Polymorphism_In_UCL.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../zh-Hant/Architecture/Polymorphism_In_UCL.md)
