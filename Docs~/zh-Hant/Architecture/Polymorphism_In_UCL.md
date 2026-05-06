---
title: UCL_Core 多型支援架構
description: 解釋 [SerializeReference]、UCLI_TypeListable、UCL_PolymorphicHelper、UCL_TypeReflectCache 四者在 UCL_Asset 編輯（GUI）+ 序列化（JSON）兩條路徑中的角色與互動，以及加新多型欄位的標準寫法。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/InterfaceCore/
namespace: UCL.Core
last_updated: 2026-05-06
target_audience: [Tools_Maintainer, Gameplay_Programmer, AI_Agent]
---

# UCL_Core 多型支援架構

## 1. 四個元件的責任

```
┌───────────────────────────────────────────────────────────────────────┐
│                         多型訊號層                                     │
│  ┌──────────────────────────┐    ┌──────────────────────────────────┐ │
│  │ [SerializeReference]      │    │ UCLI_TypeListable / UCLI_TypeList │ │
│  │ (Unity 內建 attribute)    │    │ (UCL_Core 介面標記)              │ │
│  │                           │    │                                   │ │
│  │ 「這個**欄位**要多型」     │    │ 「這個**型別**當作集合元素時要多型」│ │
│  └──────────────────────────┘    └──────────────────────────────────┘ │
│                  │                              │                      │
│                  └──────────────┬───────────────┘                      │
│                                 ▼                                      │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │ UCL_PolymorphicHelper（多型 SSOT）                                │ │
│  │   IsPolymorphicField(FieldInfo)   ← 讀 [SerializeReference]      │ │
│  │   IsPolymorphicElement(Type)      ← 讀 UCLI_TypeList(able)        │ │
│  │   GetConcreteSubtypes(Type)       ← 拉合法具體子類清單            │ │
│  └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────┬────────────────────────────────────┘
                                   │
                                   ▼
┌───────────────────────────────────────────────────────────────────────┐
│                        反射 metadata cache                              │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │ UCL_TypeReflectCache（per-(Type, SaveMode) cache）                │ │
│  │   m_Entries: List<UCL_FieldEntry>  ← 每個欄位 metadata（不過濾） │ │
│  │   m_EnableUCLEditor + m_AllMethods ← Type 級（GUI 用）            │ │
│  │                                                                   │ │
│  │ UCL_FieldEntry                                                    │ │
│  │   m_IsPolymorphicField  ← 透過 helper 計算                        │ │
│  │   m_HideOnGUI / m_HideInJson / m_IsMulticastDelegate             │ │
│  │   m_Conditional / m_FormerlyAs / m_AlwaysExpendOnGUI             │ │
│  │   m_HeaderRaw（GUI adapter 才 localize）                          │ │
│  │   GetAttr<T>()  ← 冷門 attribute lazy + dict cache                │ │
│  └──────────────────────────────────────────────────────────────────┘ │
└────────────────┬─────────────────────────────────┬────────────────────┘
                 │                                 │
                 ▼                                 ▼
        ┌──────────────────┐               ┌──────────────────────┐
        │ GUI 編輯路徑      │               │ JSON 序列化路徑       │
        │                   │               │                       │
        │ FieldInfoCache /  │               │ SaveFieldsToJson      │
        │ TypeFieldInfoCache│               │ LoadFieldFromJson     │
        │ (adapter,讀 cache)│               │ (讀 cache + 套過濾)   │
        │                   │               │                       │
        │ 過濾: !m_HideOnGUI│               │ 過濾: !m_HideInJson   │
        │                   │               │      && !MulticastDel │
        │                   │               │      && Cond.IsShow() │
        │ Header localize   │               │                       │
        └──────────────────┘               └──────────────────────┘
```

## 2. 兩個多型訊號的職責分工

| 訊號 | 加在哪 | 含意 | 觸發誰 |
|---|---|---|---|
| `[SerializeReference]` | **欄位** | 「這個欄位的執行期值可能是宣告型的子類別」 | GUI dropdown 啟用、JSON ClassName 包裝 |
| `UCLI_TypeListable` | **型別**（介面 implement）| 「這個型別當作集合元素時要保留子類資訊」 | JSON IList 序列化 per-item 包 ClassName |

**兩者可同時用**，職責互補：
- 單欄位多型：只需 `[SerializeReference]`
- 集合元素多型：欄位上的 `[SerializeReference]` **加上**元素型別實作 `UCLI_TypeListable`，或者 Step 3a 自動偵測 wrapped 格式

## 3. 加新多型欄位的標準寫法

### 3.1 單欄位（最常見）

```csharp
public abstract class MyBase { ... }
public class MyConcrete : MyBase { ... }

public class MyOwner
{
    [SerializeReference] public MyBase m_Field;     // ✅ GUI dropdown + JSON round-trip
}
```

無需 base 實作 `UCLI_TypeListable`。GUI 透過 `UCL_PolymorphicHelper.GetConcreteSubtypes(MyBase)` 列出 MyConcrete；JSON Save 包 `{ClassName, ClassData}`、Load 讀 ClassName 還原。

### 3.2 集合（List / Array）

```csharp
public class MyOwner
{
    [SerializeReference] public List<MyBase> m_List;     // ✅ Step 3a 後 list item round-trip
}
```

JSON layout：
```json
{
  "m_List": {
    "ClassName": "System.Collections.Generic.List`1[[MyBase, ...]], ...",
    "ClassData": [
      { "ClassName": "MyConcrete, ...", "ClassData": { ... } },
      ...
    ]
  }
}
```

### 3.3 例外：`UnityJsonSerializableObject` 子類元素

```csharp
[SerializeReference] public List<MyUnityJsonSerSubclass> m_List;
```

仍正常工作 — JSON 走 element 自身的 `SerializeToJson` / `DeserializeFromJson`（其本身已產生 `{ClassName, ClassData}` 包裝）。Save / Load 兩邊都會偵測並走原路徑，**不**重複包裝。

## 4. 反射 cache 的使用

### 4.1 取得 cache

```csharp
var aCache = UCL_TypeReflectCache.Get(typeof(MyAsset), JsonConvert.SaveMode.Unity);
```

`(Type, SaveMode)` 為 key。Unity 模式對應 `GetAllFieldsUnityVer`（跳過 `[HideInInspector]`、要求 `[SerializeField]` for nonpublic），Normal 模式對應 `GetAllFieldsUntil`（全 instance public+nonpublic）。

### 4.2 走 entries

```csharp
foreach (var aEntry in aCache.m_Entries)
{
    if (aEntry.m_HideInJson) continue;             // JSON 跳過
    if (aEntry.m_IsMulticastDelegate) continue;
    if (aEntry.m_Conditional?.IsShow(iObj) == false) continue;

    if (aEntry.m_IsPolymorphicField) { /* 多型路徑 */ }

    // 冷門 attribute lazy 取
    var aFolderExp = aEntry.GetAttr<UCL_FolderExplorerAttribute>();
}
```

### 4.3 為何 cache 不預過濾？

GUI 構造 `TypeFieldInfoCache` 時就丟掉 `[HideInInspector]` / `[UCL_HideOnGUI]` — 對 GUI 自己沒問題。但 cache **共用** GUI 與 JSON：JSON 不該因為 GUI 隱藏就不存檔。所以 cache 保留全部欄位，**過濾邏輯交給 caller**。

## 5. 為什麼 UCL_FieldEntry ctor 不能碰 service

UCL_TypeReflectCache 在 JSON 載入路徑很早被觸發 — 載入 `UCL_LocalizeAsset` / 各種 `UCL_Asset<>` 時都會構造 cache。此時 `UCL_ModuleService` / `UCL_LocalizeManager` 等 service 可能尚未 init。

**禁忌**（會引爆早期載入 NRE / 循環）：

| 操作 | 為什麼壞 |
|---|---|
| `UCL_LocalizeManager.Get(header)` | LocalizeManager 自身載入觸發 cache 構造 → cache 構造呼叫 LocalizeManager → 死循環 → StackOverflow |
| `GetCustomAttributes(true)` | 構造**所有** attribute 實例，包含 `[UCL_FolderExplorer]` 那種 ctor 內呼叫 `UCL_ModuleService.ModResourcesPath`（service 還沒 init → NRE）|

**安全的單型別 attribute 取**：`GetCustomAttribute<T>()` 只構造 T 一個實例。常用旗標（SerializeReference / HideInJson / Conditional / FormerlyAs / HideOnGUI / AlwaysExpendOnGUI）的 attribute 類別沒有 service 依賴，eager 取無虞。

冷門 attribute（含可能有 service 依賴的）走 `GetAttr<T>()` lazy + dict cache：第一次呼叫才反射，由 GUI render 等 service-ready 路徑觸發。

## 6. 兩條路徑的 metadata 來源對照（重構前後）

| 操作 | 重構前 | 重構後（Step 1.5 起）|
|---|---|---|
| GUI 拿欄位 metadata | `TypeFieldInfoCache.ctor` 反射 + 存自家 dict | `UCL_TypeReflectCache.Get` → wrapper 取出 GUI 子集 |
| JSON Save 拿欄位 + attribute | 每次 `GetAllFieldsUnityVer` + per-field `GetCustomAttribute<X>()` | 讀 cache.m_Entries + 旗標欄位 |
| JSON Load 拿欄位 + attribute | 同上，每次走 reflection | 同上，讀 cache |
| `[SerializeReference]` 判定 | 三處 inline 各自呼 | 統一 `UCL_PolymorphicHelper.IsPolymorphicField`（cache.m_IsPolymorphicField 是其結果）|
| 拉子類清單 | `UCLI_TypeListable.GetAllITypes` | `UCL_PolymorphicHelper.GetConcreteSubtypes`（delegate 過去）|

## 7. JSON List 多型機制（Step 3a）

### 7.1 Save

```csharp
// UCL_JsonLib.SaveFieldsToJson
if (aValue != null && aEntry.m_IsPolymorphicField)
{
    if (aValue is IList aPolyList)
    {
        // element 是 UnityJsonSerializableObject → 走原 ObjectToJson(整個 list)
        // 否則：強制 per-item ObjectToJson 包，外層加 ClassName/ClassData wrapper
    }
    else
    {
        aData[aFieldName] = ObjectToJson(aValue, ...);  // 單欄位 wrap
    }
}
```

### 7.2 Load

```csharp
// UCL_JsonLib.LoadDataFromJson IList branch
bool aIsPolyByInterface = (UCLI_TypeList || UCLI_TypeListable) && !UnityJsonSerializableObject;
bool aIsPolyByWrapper = !aIsPolyByInterface
                     && !UnityJsonSerializableObject  // 雙邊例外對稱
                     && iData[0].Contains(ClassNameID);
if (aIsPolyByInterface || aIsPolyByWrapper) per-item JsonToObject
else per-item DataToObject
```

兩個訊號擇一觸發 per-item polymorphic 路徑：
- 既有訊號 — element type 實作 UCLI_TypeListable
- 新訊號 — items 是 wrapped 格式（自動偵測）

## 8. 相關文件

- 📋 [SerializeReference_Symmetry_Plan](../Plan/SerializeReference_Symmetry_Plan.md) — 完整四步計畫
- 📖 [DevLog 00005](../../../DevLogs~/00005_2026-05-06.md) — 重構過程紀錄
- 🤖 [Cmd_DiagnoseAssetReflection](../API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md) — 反射管線診斷工具
- 🤖 [Cmd_FindAssetUsages](../API/UCL_AgentCommand/Cmd_FindAssetUsages.md) — 反向 asset 引用查詢

---

## 其他語系

- 🇬🇧 [English](../../en/Architecture/Polymorphism_In_UCL.md)
- 🇯🇵 [日本語](../../ja/Architecture/Polymorphism_In_UCL.md)
- 🇨🇳 [简体中文](../../zh-Hans/Architecture/Polymorphism_In_UCL.md)
- 🇹🇼 繁體中文（本檔）
