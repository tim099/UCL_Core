---
date: 2026-05-06
index: 00005
title: SerializeReference 對稱性修復 — UCL_PolymorphicHelper / UCL_TypeReflectCache + JSON List 多型
tags: [refactor, fix]
---

# SerializeReference 對稱性修復 — UCL_PolymorphicHelper / UCL_TypeReflectCache + JSON List 多型

## What

四步漸進 refactor，把 GUI 編輯（`UCL_GUILayoutDrawObject.DrawObject`）與 JSON 序列化（`JsonConvert.SaveFieldsToJsonUnityVer` / `LoadFieldFromJson`）兩條路徑對 `[SerializeReference]` 的判定 / 行為收斂到一致：

| Step | 內容 | 行為變更 |
|---|---|---|
| 1 | 抽 `UCL_PolymorphicHelper`（多型 SSOT — IsPolymorphicField / IsPolymorphicElement / GetConcreteSubtypes）| 純新增，無 caller |
| 1.5 | 抽 `UCL_TypeReflectCache` + `UCL_FieldEntry`（per-(Type, SaveMode) 反射 metadata）；GUI cache 變 adapter；JSON 改讀共用 cache | 行為等價（位元等價驗收）|
| 2 | GUI 兩處 caller 改用 helper（純 SSOT 替換）| 等價 |
| 3a | JSON SaveFieldsToJson `[SerializeReference]` IList 強制 per-item ObjectToJson；LoadDataFromJson IList 加 wrapped 格式自動偵測 | 修復 List 元素層多型；UnityJsonSerializableObject 元素**雙邊**例外避開 double-wrap / 路徑誤跳過 |

新增檔：

```
UCL_Core_Scripts/InterfaceCore/
  + UCL_PolymorphicHelper.cs        ← 多型 SSOT
  + UCL_TypeReflectCache.cs         ← per-Type metadata cache + UCL_FieldEntry

Docs~/zh-Hant/Plan/
  + SerializeReference_Symmetry_Plan.md
```

修改檔：

```
UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs    ← FieldInfoCache/TypeFieldInfoCache 變 adapter
UCL_Core_Scripts/JsonCore/UCL_JsonLib.cs               ← 改讀 cache + List 多型修復
UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommandWatcher.cs  ← cctor delayCall fix
```

順帶修復（下游 RCG 端）：`RCG_RuntimeStructData.CreateData` 補處理 `GenericTypeDic.Keys`（"List" / "Dic"）裸名 — 與 `PrimitiveDic` 對稱，消除 Cmd_FindAssetUsages 全掃時的 noise。

## Why

兩條路徑對 `[SerializeReference]` 判定不一致 — 容易漂移、新增 attribute 時兩邊容易遺漏：

- GUI 在 `FieldInfoCache.ctor` 內檢查 `[SerializeReference]`，dropdown 透過 `UCLI_TypeListable.GetAllITypes` 拉子類
- JSON 在 `SaveFieldsToJson` / `LoadFieldFromJson` 內各自 inline `aField.GetCustomAttribute<SerializeReference>()`
- List 元素層：JSON Save/Load IList branch 只認 `UCLI_TypeListable` interface，**忽略**欄位上的 `[SerializeReference]` — 導致 `[SerializeReference] List<NonTypeListable>` 之類欄位 item 失型

修完後：
- 「這個欄位該不該多型」「這個型別當作集合元素時該不該多型」「base 型對應子類有哪些」三個問題收斂到 `UCL_PolymorphicHelper`
- 「這個型別有哪些欄位、每個欄位的 metadata 是什麼」收斂到 `UCL_TypeReflectCache`（GUI / JSON 共用同一份）
- List 元素層加自動偵測：第一個 item 含 `ClassName` 鍵就走 per-item polymorphic 路徑，不必依賴 element type 實作 `UCLI_TypeListable`

## How to use

### 加新的多型欄位

```csharp
public abstract class MyBase { ... }       // 不需要 implement UCLI_TypeListable
public class MyConcreteA : MyBase { ... }

public class MyOwner
{
    [SerializeReference] public MyBase m_Single;          // ✅ GUI dropdown + JSON round-trip 完整
    [SerializeReference] public List<MyBase> m_List;      // ✅ Step 3a 後 list item 也 round-trip
}
```

**唯一例外**：若 element 型別繼承 `UnityJsonSerializableObject`，仍走原 SerializeToJson/DeserializeFromJson 路徑（自身已有 ClassName 機制；雙邊都會偵測並避開）。

### 用反射 cache 寫新工具

```csharp
var aCache = UCL_TypeReflectCache.Get(typeof(MyAsset), JsonConvert.SaveMode.Unity);
foreach (var aEntry in aCache.m_Entries)
{
    if (aEntry.m_HideInJson) continue;
    if (aEntry.m_IsPolymorphicField) { ... }   // 共用旗標
    var aFolderExp = aEntry.GetAttr<UCL_FolderExplorerAttribute>();  // lazy + dict cache
    // ...
}
```

## 設計決策

### 1. 預過濾與否：cache 不過濾，caller 自己過濾

`UCL_TypeReflectCache.m_Entries` 不在構造期過濾任何欄位 — GUI 用 `!m_HideOnGUI`、JSON 用 `!m_HideInJson && !m_IsMulticastDelegate`，並 runtime 呼 `m_Conditional?.IsShow(obj)`。原 GUI 的「構造期就丟掉 hidden 欄位」對 GUI 自己沒問題，但若 cache 共用就會讓 JSON 看不到那些欄位 — 不可接受。

### 2. 兩級 attribute 取得：常用 eager / 冷門 lazy

UCL_FieldEntry ctor 只 eager 抓 ctor 不重的「常用旗標」attribute（`SerializeReference` / `HideInJson` / `Conditional` / `FormerlyAs` / `HideOnGUI` / `AlwaysExpendOnGUI`）。冷門 attribute 走 `GetAttr<T>()` lazy + dict cache：第一次呼叫才反射，存入 dict（含 null）。

理由：`GetCustomAttributes(true)` 會構造**所有** attribute 實例，包括 `[UCL_FolderExplorer]` 這類 ctor 內部呼叫 `UCL_ModuleService` 的重型 attribute。JSON 載入路徑很早就會建構 cache（在 ModuleService init 前），eager 構造會撞 NRE。

### 3. UCL_FieldEntry ctor 純反射，不碰任何 service

cache 在 JSON 載入路徑早期觸發。任何呼叫 `UCL_LocalizeManager` / `UCL_ModuleService` 等的操作都搬到 GUI adapter（`FieldInfoCache(UCL_FieldEntry)`）— 只 GUI render 路徑會觸發，那時 service 已 ready。

實證：原本把 `[Header]` localize 放在 cache ctor → 載入 `UCL_LocalizeAsset` 時 cache 構造觸發 LocalizeManager → LocalizeManager 反過來載 LocalizeAsset → StackOverflow。修法：cache 只存 raw header，localize 移到 GUI adapter。

### 4. UnityJsonSerializableObject 雙邊例外，對稱很重要

Save 端：[SerializeReference] List 強制 per-item ObjectToJson，但**例外**：element 是 UnityJsonSerializableObject 子類時退回 ObjectToJson(整個 list) 既有路徑（其 SerializeToJson 自身已包 ClassName，再經 ObjectToJson 會 double-wrap）。

Load 端：IList 自動偵測 wrapped 格式時也排除 UnityJsonSerializableObject — 那類元素的 ClassName 由 `DataToObject` 內專屬 handler 處理，誤走 `JsonToObject` 會跳過 DeserializeFromJson 導致資料破損。

兩邊條件**對稱**是穩定性的關鍵 — 不對稱會引爆 round-trip 不等價。

### 5. Watcher cctor 用 delayCall

`[InitializeOnLoad]` 的 cctor 在 `EditorAssemblies.ProcessInitializeOnLoadAttributes` 連續呼叫，此時 UniTask 的 PlayerLoopHelper 內部尚未 init。直接 `Register().Forget()` 會在 `WaitUntilInitialized` 走到的 `PlayerLoopHelper.AddAction` 撞 NRE。

修法：`EditorApplication.delayCall += () => Register().Forget();` — 推到下一個 editor tick，UniTask 基礎設施已就緒，後續 await 安全。Register 函式體不動，「Register 完成前不訂閱 update」的不變式保留。

### 6. RCG_RuntimeStructData：與 PrimitiveDic 對稱

RCG 端 `GetAllIDs()` 把 `GenericTypeDic.Keys`（"List", "Dic"）列為可載入 ID，但 `CreateData` 對裸名沒對應分支 → fall-through 到 base 找不到 .json 檔丟 Exception。修法：在 CreateData 加 `GenericTypeDic.ContainsKey` 分支建 schema stub，與 `PrimitiveDic.ContainsKey` 既有分支對稱。

## Breaking changes

**理論上無 breaking change**，但有一個 forward-compat 限制：

`[SerializeReference] List<Base>` 且 Base **不**實作 `UCLI_TypeListable` 的**舊存檔**會載入失敗 — 但這個情境本來就 broken（item 在舊版 save 時就失型），存的內容已經是垃圾。Step 3a 改動只是讓「以後存的會對」。

JSON layout：
- 對既有 `UCLI_TypeListable` list — **byte-identical**（外層包 + per-item ClassName，原路徑也是同樣輸出）
- 對 `[SerializeReference] List<NonTypeListable>` — 新格式（per-item ClassName）；該情境舊版本本就無法 round-trip

## Caveats

| Caveat | 說明 |
|---|---|
| Load IList 自動偵測（`iData[0].Contains("ClassName")`）| 若 caller 設計類別恰好有 JSON 鍵叫 "ClassName" 會誤觸；UCL 內部把 ClassName 當保留字，外部 caller 別這樣命名 |
| UnityJsonSerializableObject 元素的雙邊例外 | Save / Load 兩邊條件必須對稱，未來改動其中一邊請務必同步改另一邊 |
| `UCL_FieldEntry.GetAttr<T>()` 不該用來取已預抓的旗標 | 預抓旗標直接讀欄位（如 `entry.m_IsPolymorphicField`），語意更清楚 |
| Cache key 含 SaveMode | Normal / Unity 兩個模式的欄位集合不同，必須分開 cache |

## 驗收

- ✅ `Cmd_DiagnoseAssetReflection deep=true` 全 92 types / 6067 assets → **0 失敗**（Step 1.5 之後仍 2 個的孤兒 RuntimeStructData 也在 RCG fix 後消除）
- ✅ `Cmd_FindAssetUsages` Stun → **14 hits**（與 Step 1.5 baseline byte-identical，含 dotted field path 全對）
- ✅ Watcher `[InitializeOnLoad]`×UniTask race NRE 不再出現
- ✅ 三層 commits：UCL_Core / UCL / parent 全部 clean

## 相關文件

- 📋 [SerializeReference_Symmetry_Plan](../Docs~/zh-Hant/Plan/SerializeReference_Symmetry_Plan.md) — 完整四步計畫 + §2 對稱性分析
- 🏛 [Polymorphism_In_UCL](../Docs~/zh-Hant/Architecture/Polymorphism_In_UCL.md) — `[SerializeReference]` × `UCLI_TypeListable` × cache 三者角色說明
- 🤖 [Cmd_DiagnoseAssetReflection](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md) — 反射診斷工具（驗收用）
- [DevLog 00004](00004_2026-05-06.md) — 反向引用查詢 / 反射診斷工具的起源（Step 1.5 的驗收工具就出自此）
