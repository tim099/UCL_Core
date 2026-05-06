---
title: SerializeReference 對稱性修復計畫
description: UCL_GUILayout.DrawObject（編輯）與 JsonConvert.SaveFieldsToJsonUnityVer（存檔）對 [SerializeReference] 的支援不對稱 — 本文分析破口、提出三步漸進方案、列出風險與測試方法。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/
namespace: UCL.Core
status: A1_Draft
last_updated: 2026-05-06
target_audience: [Tools_Maintainer, AI_Agent]
---

# SerializeReference 對稱性修復計畫

> [!IMPORTANT]
> 此計畫描述 UCL_Core 內建多型支援機制的修復方向。
> **狀態**：A1_Draft（已分析、未動 code）
> **影響**：UCL_GUILayoutDrawObject + UCL_JsonLib + 新增 UCL_PolymorphicHelper

---

## 1. 現況：兩條路徑的多型偵測機制

| 路徑 | 觸發訊號 | 出處 |
|---|---|---|
| **GUI 編輯** | 必須**同時**滿足：欄位有 `[SerializeReference]` **且** 基底型別實作 `UCLI_TypeListable` | [`UCL_GUILayoutDrawObject.cs:505,650-675`](../../../UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs) |
| **JSON 存檔** | 欄位有 `[SerializeReference]` | [`UCL_JsonLib.cs:450-454`](../../../UCL_Core_Scripts/JsonCore/UCL_JsonLib.cs) |
| **JSON 載入** | 欄位有 `[SerializeReference]` 且 JSON 含 `ClassName` | [`UCL_JsonLib.cs:699-707`](../../../UCL_Core_Scripts/JsonCore/UCL_JsonLib.cs) |
| **JSON List 內 item** | List **元素型別**實作 `UCLI_TypeList` / `UCLI_TypeListable`（**不檢查 `[SerializeReference]`**）| `UCL_JsonLib.cs:375-381` (save), `562-570` (load) |

## 2. 不對稱具體在哪

| 場景 | GUI | JSON 存 | JSON 載 | 結果 |
|---|:-:|:-:|:-:|---|
| 單欄位 `[SerializeReference] Base m_X`，Base **無** UCLI_TypeListable | ❌ 下拉空 | ✅ 包 ClassName | ✅ 還原 | **GUI 不能編，JSON OK** |
| 單欄位 `[SerializeReference] Base m_X`，Base **有** UCLI_TypeListable | ✅ | ✅ | ✅ | 完整工作 |
| `[SerializeReference] List<Base>`，元素**無** UCLI_TypeListable | ❌ | ⚠ 包外層 list 但 item 失型 | ⚠ item 變 base | **List item 失子類** |
| `[SerializeReference] List<Base>`，元素**有** UCLI_TypeListable | ✅ | ✅ | ✅ | 完整工作 |
| `List<Base>`（無 `[SerializeReference]`），元素**有** UCLI_TypeListable | ❌ | ✅ per-item 包 | ✅ per-item 還原 | **JSON OK 但 GUI 不能編** |

**結論**：「完整 round-trip + 可編輯」目前**強制要求**兩個條件同時成立 — `[SerializeReference]` 屬性 + `UCLI_TypeListable` 介面。少一個就有破口。

## 3. 設計原則

1. **單一事實來源**（SSOT）：定義 `UCL_PolymorphicHelper` — `IsPolymorphicField` / `IsPolymorphicElement` / `GetConcreteSubtypes`，所有路徑共用
2. **Attribute 優先**：`[SerializeReference]` 視為「設計者明確聲明此處要多型」的最強訊號 — 兩條路徑都尊重它
3. **Interface 輔助**：`UCLI_TypeListable` 仍保留 — 作為 attribute 缺席時的次要訊號，向後相容既有資料
4. **JSON 寬鬆讀**：載入時若 `ClassName` 存在就用它，沒有就退回 base 型 + 警告（避免破壞舊檔）
5. **零 Breaking change**：所有現存 `UCLI_TypeListable` 資料的 JSON layout 不變
6. **共用反射快取**：`FieldInfoCache` / `TypeFieldInfoCache` 抽成獨立檔，GUI 與 JSON 兩條路徑讀同一份 metadata，避免雙邊判斷漂移（**穩定性 + 可讀性優先**，效能不是主要考量）

## 4. 四步漸進方案

### Step 1 — 抽出 helper（純重構，零行為變更）

新增 [`UCL_PolymorphicHelper.cs`](../../../UCL_Core_Scripts/InterfaceCore/UCL_PolymorphicHelper.cs)：

```csharp
public static class UCL_PolymorphicHelper
{
    // 「這個欄位本身要多型」？
    public static bool IsPolymorphicField(FieldInfo f);

    // 「這個型別當作元素時要多型」？（List<T> 的 T、Dict<K,V> 的 V 等）
    public static bool IsPolymorphicElement(Type elemType);

    // 拉合法子類清單（GUI dropdown 與 JSON 反序列化都用同一個來源）
    public static IList<Type> GetConcreteSubtypes(Type baseType);
}
```

`GetConcreteSubtypes` 直接 delegate 到 `UCLI_TypeListable.GetAllITypes(...)`，保持結果與排序與既有行為一致（filter 條件：非 abstract、非 `[UCL_IgnoreInTypeListable]`；排序：`[UCL_Sort]`）。

**驗收**：
- `Cmd_DiagnoseAssetReflection deep=true` 不增任何 NRE
- 編譯通過、所有現有測試 / asset 行為不變（因為沒人 call helper）

### Step 1.5 — 抽出 `UCL_TypeReflectCache`（穩定性 + 可讀性，行為等價）

> **動機**：目前 `FieldInfoCache` / `TypeFieldInfoCache` 內嵌在 [`UCL_GUILayoutDrawObject.cs:485-537`](../../../UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs)，是 GUI 私有類別；JSON 路徑（`SaveFieldsToJson` / `LoadFieldFromJson`）每次呼叫各自走 `GetAllFieldsUnityVer` + `GetCustomAttribute<X>()`，**兩條路徑各自判斷「這個欄位的 metadata 是什麼」**。隨著 attribute 數量增加（`[SerializeReference]` / `[UCL_HideInJson]` / `[UCL_Conditional]` / `[UCL_FormerlySerializedAs]` ...），雙邊容易漂移、檢查條件不一致。
>
> **目標不是效能**（cache 本來就只算一次，浪費一些 method walk 不是問題），是**讓 GUI 與 JSON 看到同一份 field metadata**，把「`[SerializeReference]` 偵測」「attribute 拿取」這類判斷收斂到一個檔案、單一構造期。

#### 1.5.1 新檔位置與內容

新增 [`UCL_Core_Scripts/InterfaceCore/UCL_TypeReflectCache.cs`](../../../UCL_Core_Scripts/InterfaceCore/UCL_TypeReflectCache.cs)（與 `UCL_PolymorphicHelper.cs` 同層）：

```csharp
public class UCL_FieldEntry
{
    public FieldInfo m_FieldInfo;
    public IReadOnlyList<Attribute> m_Attrs;        // 一次抓齊（後續 OfType<T>() 取用）

    // GUI 關心
    public string m_Header_Localized;               // [Header] 取出 + UCL_LocalizeManager.Get
    public bool m_AlwaysExpendOnGUI;                // [AlwaysExpendOnGUI]
    public bool m_HideOnGUI;                        // [HideInInspector] || [UCL_HideOnGUI]

    // JSON 關心
    public bool m_HideInJson;                       // [UCL_HideInJson]
    public bool m_IsMulticastDelegate;              // FieldType.IsSubclassOf(MulticastDelegate)
    public UCL.Core.PA.ConditionalAttribute m_Conditional;  // 存物件本身，IsShow(obj) runtime 算
    public UCL_FormerlySerializedAsAttribute m_FormerlyAs;

    // 共用
    public bool m_IsPolymorphicField;               // [SerializeReference]（呼 UCL_PolymorphicHelper）
}

public class UCL_TypeReflectCache
{
    public Type m_Type;
    public List<UCL_FieldEntry> m_Entries;          // **不**預過濾：含 GUI / JSON 雙方各自關心的所有欄位

    // GUI Type 級 metadata（JSON 不關心，但放這裡無害且讓 GUI cache 退化為 wrapper）
    public bool m_EnableUCLEditor;
    public IList<MethodInfo> m_AllMethods;

    public static UCL_TypeReflectCache Get(Type iType, SaveMode iSaveMode);
    private static Dictionary<(Type, SaveMode), UCL_TypeReflectCache> s_Cache;
}
```

**關鍵設計**：

- **不預過濾**：`m_Entries` 存所有欄位，過濾交給 caller（GUI 用 `!m_HideOnGUI`，JSON 用 `!m_HideInJson && !m_IsMulticastDelegate`）。GUI 原本構造期就丟掉 hidden 欄位，那是 GUI 自己的選擇 — 不該影響 JSON。
- **Cache key 帶 `SaveMode`**：因為 `GetAllFieldsUnityVer` (Unity) 與 `GetAllFieldsUntil` (Normal) 兩個走訪函式回傳的 field 集合不同，必須分開存。
- **`[UCL_Conditional].IsShow(obj)` 不在 cache 算**：這個是 instance-level 判斷（依 obj 當時的欄位值決定是否顯示），cache 只存 attribute 物件本身，runtime 由 caller 呼 `IsShow(obj)`。
- **共用層放 InterfaceCore**：與 `UCL_PolymorphicHelper.cs` / `UCLI_TypeList.cs` 同位置，讓「型別 metadata 抽象」這個概念集中在一處。

#### 1.5.2 GUI 端改 adapter

`UCL_GUILayoutDrawObject.cs` 內的 `FieldInfoCache` / `TypeFieldInfoCache` 兩個 nested class：

選項 A（推薦）：**保留 class 名稱不刪**，但內部退化為 wrapper — 構造時直接從 `UCL_TypeReflectCache.Get(...)` 拿到 `m_Entries`，按 GUI 規則過濾後重新組成既有的 `m_FieldInfos` 列表。對 DrawObject 主流程的呼叫點完全不需要改。

選項 B：**直接刪 GUI 端兩個類別**，呼叫點改用 `UCL_TypeReflectCache.Get(...)` + LINQ 過濾。改動較大但語意更乾淨。

**先採選項 A**（穩定性優先），future 看狀況再考慮 B。

#### 1.5.3 JSON 端改用 cache

`UCL_JsonLib.cs`：
- `SaveFieldsToJson`（line 407）：把 `aType.GetAllFieldsUnityVer(...)` 換成 `UCL_TypeReflectCache.Get(aType, iSaveMode).m_Entries`，配合 JSON-side 過濾。
- `LoadFieldFromJson`（line 652）：同上。
- 個別欄位的 attribute 取得（如 `aField.GetCustomAttribute<SerializeReference>()`）改讀 `entry.m_IsPolymorphicField`，`UCL_HideInJson` 改讀 `entry.m_HideInJson` 等。
- `[UCL_Conditional]` 維持 runtime 呼 `entry.m_Conditional.IsShow(obj)`（cache 只存 attribute 物件）。

#### 1.5.4 與 Step 1 的整合

`UCL_PolymorphicHelper.IsPolymorphicField(FieldInfo)` 仍是 public API，但其實作可改成「先看 cache 有沒有這個 field 的 entry，有就回 entry.m_IsPolymorphicField」— 兩層的責任明確：

| 層 | 職責 |
|---|---|
| `UCL_TypeReflectCache` | 「這個 type 的這個 field，metadata 一次抓齊」（per-field 物件） |
| `UCL_PolymorphicHelper` | 「給 GUI dropdown / JSON 反序列化用的高層多型 API」（per-question 函式） |

#### 1.5.5 驗收

- 編譯通過
- `Cmd_DiagnoseAssetReflection deep=true` 全資產 → 0 失敗
- `Cmd_ValidateAssetFormat` 對代表性 asset → 全 pass
- **位元等價測試**：對 5+ 份不同型別的 asset 跑 `read → SaveFieldsToJsonUnityVer → write tmp → diff` → 預期 byte-identical
- GUI 視覺檢查：開 `RCG_StoryDataEditorPage` / `RCG_BattleSetEditorPage` 等代表性頁面，欄位顯示順序 / Header / dropdown 行為與改前一致

#### 1.5.6 風險

| 風險 | 緩解 |
|---|---|
| Cache key 漏算 SaveMode → JSON Normal mode 走錯 field 集合 | key 用 `(Type, SaveMode)` tuple，並加 unit-style probe（diagnose Cmd 對兩個 mode 都跑） |
| GUI 過濾改成 runtime 後出現「GUI 顯示了 hidden 欄位」regression | 採選項 A，過濾邏輯保留在 GUI wrapper 內部，行為等價 |
| `[UCL_Conditional]` 誤判為 cache-able（會錯把 instance 級結果寫死） | cache 只存 attribute 物件本身；明確命名 `m_Conditional`（不命名為 `m_IsHidden`）避免誤導 |
| Domain reload 殘留舊 cache | `static Dictionary` 模式 — Unity domain reload 自動清 |
| 兩條路徑改完 JSON 結果不同 | 位元等價測試是強制 gate |

### Step 2 — 修 GUI：`[SerializeReference]` 單獨即可觸發

`UCL_GUILayoutDrawObject.cs:650` 把 `UCLI_TypeListable.GetAllITypes(fieldType)` 改用 `UCL_PolymorphicHelper.GetConcreteSubtypes(fieldType)`。
觸發條件從「`SerializeReference && fieldType is UCLI_TypeListable`」放寬到「`SerializeReference`」。

**效果**：單欄位 `[SerializeReference] Base m_X`（Base 無 UCLI_TypeListable）也能在 GUI 編輯。

**風險**：GUI 變寬鬆 — 過去無 dropdown 的欄位現在會顯示。對既有 asset 是 additive。

### Step 3 — 修 JSON List 路徑：尊重欄位上的 `[SerializeReference]`

JSON 在進入 List 遞迴時會丟失 FieldInfo（沒辦法從 list 內部知道外層欄位的 attribute）。兩條路：

**3a（推薦）**：在 `SaveFieldsToJson` 偵測 `[SerializeReference] List<X>` 時走專屬分支：

```csharp
// UCL_JsonLib.cs 約 line 450 後新增（在現有 SerializeReference branch 內細分）
if (aValue != null && UCL_PolymorphicHelper.IsPolymorphicField(aField))
{
    if (aValue is IList polyList && IsListType(aField.FieldType))
    {
        var arr = new JsonData();
        foreach (var item in polyList)
            arr.Add(item != null ? ObjectToJson(item, ...) : new JsonData());
        aData[aFieldName] = arr;
    }
    else
    {
        aData[aFieldName] = ObjectToJson(aValue, ...);  // 原本的單欄位邏輯
    }
    continue;
}
```

載入端對稱：`LoadFieldFromJson` 在 line 699 補檢 — 若欄位是 `[SerializeReference] List<X>` 且 JSON 是 array，per-item 用 `JsonToObject`。

**3b（備案，更小但更鬆散）**：把 line 562 的 list 載入條件由 `UCLI_TypeListable` 擴張為「`UCLI_TypeListable` OR 第一個 item 含 `ClassName` key」— 自動偵測 wrapped 格式。

> **推薦 3a**：精確、可預期、JSON layout 跟既有 `UCLI_TypeListable` list 完全相同（per-item ClassName wrap），向後相容。

## 5. 風險與測試

| 風險 | 緩解 |
|---|---|
| 改 GUI dropdown 來源後，**舊有同時實作 UCLI_TypeListable 的型別**仍應正常工作 | helper `GetConcreteSubtypes` 結果與舊 `GetAllITypes` 一致（兩者底層都是 `GetAllITypesAssignableFrom`） |
| Step 3a 改變 JSON 寫法（多了 ClassName 包裝） | 只對「**新加** `[SerializeReference]` 的 list 欄位」生效；既有有 `UCLI_TypeListable` 的 list 走原路徑，layout 不變 |
| 載入時遇到「只有 `[SerializeReference]` 沒 ClassName」的舊 JSON | line 699 已有降級 — `if(className != null)` else 走原本 path，**不破壞舊檔** |

**測試方法**（用既有工具驗證）：
1. `Cmd_DiagnoseAssetReflection deep=true` 跑全資產 → 確認沒新爆 NRE
2. `Cmd_ValidateAssetFormat` 對 `RCG_Scope` / `UCL_RuntimeScripts` 跑 → 確認 round-trip 正確
3. 手寫測試 asset：`[SerializeReference] Base m_Single` + `[SerializeReference] List<Base> m_List`（Base 不實作 UCLI_TypeListable），save → 改檔 → load → 比對

## 6. 完成定義

- ✅ Step 1：`UCL_PolymorphicHelper` 抽出，保留純重構性質
- ⬜ Step 1.5：`UCL_TypeReflectCache` 抽出，GUI cache 變 adapter，JSON 改讀 cache，**位元等價** round-trip pass
- ⬜ Step 2：GUI 改用 helper（讀 cache 內的 `m_IsPolymorphicField`），`[SerializeReference]` 單獨可觸發 dropdown
- ⬜ Step 3a：JSON List 路徑尊重 `[SerializeReference]`（透過 cache entry 取得旗標）
- ⬜ DevLog 紀錄 breaking-but-additive change
- ⬜ 補一份 `Docs~/{lang}/Architecture/Polymorphism_In_UCL.md` 解釋兩個訊號 + cache 架構（推薦 4 langs，因為是核心概念）

## 7. 相關文件

- [`UCL_AgentCommand_Architecture`](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Cmd 系統，用於驗收
- [`Cmd_DiagnoseAssetReflection`](../API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md) — 反射診斷工具（驗收用）
- [`Cmd_ValidateAssetFormat`](../API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) — Asset 驗證工具
- [`HelpURL_Workflow`](../Workflows/HelpURL_Workflow.md)
