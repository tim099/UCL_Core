---
title: 建立新的 UCL_Asset 子類工作流
description: 步驟化 SOP — 在 UCL_Core 體系下新增持久化資料類型，**一律繼承 UCL_Asset<T>**，禁止裸 ScriptableObject 或自寫存檔。涵蓋繼承樣板、ID/SaveFolderPath 慣例、AssetGroup attribute、JSON 序列化、Edit/Preview hook、與常見地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Assets/
namespace: UCL.Core
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create UCL Asset, 新增 Asset, UCL_Asset 子類, 持久化資料]
tags: [workflow, asset, scriptableobject, persistence]
related:
  - ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md | Create EditorPage Workflow | 新 Page 入口（搭配本檔 — Page 是 UI、Asset 是資料）
  - ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md | Validate UCL Asset Workflow | UCL_Asset 序列化驗收（agent 改完 .json 後驗證）
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_SelectAssetPage.md | UCL_SelectAssetPage | 自動列出所有 UCL_Asset 子類的選擇 UI（不必自寫 list page）
---

# 🛠️ 建立新的 UCL_Asset 子類工作流

> [!IMPORTANT]
> **在 UCL_Core 體系下，所有持久化資料一律繼承 `UCL_Asset<T>`**，禁止：
> - 裸 `ScriptableObject` + `[CreateAssetMenu]`（跟 UCL_ModuleService 模組路徑機制不相容）
> - 自寫 `File.WriteAllText` / `JsonUtility.ToJson` 之類的存檔機制（重複造輪 + 沒模組路徑解析）
> - FileSystemWatcher / EditorApplication.update polling 雙向同步雙 store（UCL_Asset 自身就是 source-of-truth，不需要 mirror）
>
> 設計哲學：**一個 .json 檔 = 一筆 ID 的 UCL_Asset 子類實例**。base 處理 IO / 模組路徑 / Editor UI / 序列化 — 子類只填欄位 + 兩個 ctor。

---

## 0. TL;DR — 最小骨架

```csharp
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_<Name>Asset : UCL_Asset<UCL_<Name>Asset>
    {
        public const string DefaultID = "Default";

        public string m_SomeField = string.Empty;
        public int    m_SomeNumber = 0;

        public UCL_<Name>Asset() { ID = DefaultID; }
        public UCL_<Name>Asset(string iID) { Init(iID); }
    }
}
```

**就這樣**。沒有 `[CreateAssetMenu]`、沒有 OnValidate、沒有 FileSystemWatcher。Edit / Save / Load 全自動。

---

## 1. 為什麼一律 UCL_Asset

| 訴求 | 裸 ScriptableObject | UCL_Asset |
|---|---|---|
| 跨模組存放 | ❌ 寫死 `Assets/...` 路徑 | ✅ `UCL_ModuleService` 模組相對路徑 |
| JSON 序列化 | ⚠ 要自寫 ToJson/FromJson | ✅ base 自帶 SerializeToJson |
| Editor 編輯 UI | ⚠ 要自寫 Custom Inspector | ✅ base OnGUI 自動跑 DrawObjectData 反射 |
| 列表 / 選擇 UI | ⚠ 要自寫 EditorWindow | ✅ `UCL_SelectAssetPage` 反射列舉 |
| Mod 系統相容 | ❌ 不在模組系統內 | ✅ 對應 UCL_Module 自動切換 |
| Per-file git diff | ⚠ asset 檔是 binary YAML | ✅ .json 純文字、merge 友善 |

**結論**：UCL_Core 本身就是 mod-friendly asset 框架，新增任何持久化資料都該走 UCL_Asset。除非你有**極特殊**理由（目前 UCL_Core 內也只有 `UCL_LocalizeAsset` 等少數例外，因為它們需要 Unity 序列化 hook） — 否則不要繞開。

---

## 2. 必要組成

### 2.1 繼承
```csharp
public class UCL_MyAsset : UCL_Asset<UCL_MyAsset>
```
泛型 `T` 是子類自身（CRTP 樣板）。

### 2.2 兩個 ctor

```csharp
public UCL_MyAsset() { ID = DefaultID; }    // 無參 — 反射 / new() 用
public UCL_MyAsset(string iID) { Init(iID); }  // 帶 ID — 顯式建立
```

`UCL_Asset<T>` 約束 `T : new()`，所以**無參 ctor 必填**。

### 2.3 ID 預設常數

```csharp
public const string DefaultID = "Default";
```

無參 ctor 用這個當 placeholder ID（Init 時會覆寫）。

### 2.4 欄位（採 m_-prefix 慣例）

```csharp
public string m_DisplayName = string.Empty;
public List<string> m_Tags = new List<string>();
public Color m_TintColor = Color.white;
```

- 用 `m_` prefix（被 `UCL_LocalizeManager` / `LocalizeFieldName` 自動去掉前綴顯示）
- 預設值用 inline initializer，**不要**在 ctor 內 new（UCL_Asset 反序列化會覆寫）

---

## 3. 可選 attribute

| Attribute | 用途 |
|---|---|
| `[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.X)]` | 把 Asset 分到 Data / Config / Editor / Assembly group（影響 SelectAssetPage 排序） |
| `[UCL.Core.ATTR.UCL_Sort(int)]` | group 內排序 hint |
| `[HelpURL("ucl_core:Docs~/{lang}/...")]` | Editor Inspector 顯示 ? 按鈕跳文件 |

範例見 `UCL_ConfigAsset.cs`：
```csharp
[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
[UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ConfigAsset)]
public class UCL_ConfigAsset : UCL_Asset<UCL_ConfigAsset>
```

---

## 4. 檔案落地

base 自動處理：
- `SaveFolderPath` → `<module>/UCL_Assets/<TypeName>/`
- `AssetPath` → `<SaveFolderPath>/<ID>.json`
- 一個 ID = 一個 .json 檔（純文字、git diff 友善）

範例（`UCL_ConfigAsset` ID=`CurLangKey`）：
```
<module>/UCL_Assets/UCL_ConfigAsset/CurLangKey.json
  → { "m_Value": "MyProj_CurLang" }
```

---

## 5. 編輯與選擇 UI

### 5.1 不要自寫
- `UCL_CommonEditPage` 自動接手任何 UCL_Asset 的編輯 — 直接呼 `UCL_CommonEditPage.Create(asset)` 即可
- `UCL_SelectAssetPage` 反射列舉所有 UCL_Asset 子類，自動 grouped + searchable
- 子類只覆寫 `OnGUI` / `Preview` 才需要客製顯示，否則 base 用 `UCL_GUILayout.DrawObjectData` 反射出 UI

### 5.2 用 base 入口
```csharp
// 從外部開編輯頁
UCL_CommonEditPage.Create(myAsset);

// 從外部開選擇頁（讓使用者選一個 ID）
// 走 UCL_SelectAssetPage 慣例 — 詳見 UCL_SelectAssetPage.md
```

---

## 6. 常見地雷

| # | 地雷 | 症狀 | 解法 |
|---|---|---|---|
| 1 | 繼承 `ScriptableObject` 而非 `UCL_Asset<T>` | 進不了 SelectAssetPage、跟 UCL_ModuleService 不通 | 改繼承 `UCL_Asset<T>` |
| 2 | 缺無參 ctor | `UCL_Asset<T>` `where T : new()` 編不過 | 加 `public UCL_MyAsset() { ID = DefaultID; }` |
| 3 | ctor 內 `new List<>()` 把欄位塞滿 | 反序列化讀不進來（被 ctor 覆寫） | 移到 inline field initializer |
| 4 | 欄位名沒 m_ prefix | 顯示名沒對齊 UCL 慣例（雖然能用） | 改用 `m_FieldName` |
| 5 | 自寫 FileSystemWatcher / OnValidate write-back 雙向同步 | 重複造輪 + race condition | 刪掉，UCL_Asset 自己就是 source-of-truth |
| 6 | 用 `[CreateAssetMenu]` 想在 Project window 右鍵建 | 創出來的 .asset 不是 UCL_Asset 認的 | 不用，走 `CreateData(iID)` 或 SelectAssetPage 流程 |
| 7 | 開個 EditorWindow 自寫 list 頁 | 重複造輪 | 直接用 `UCL_SelectAssetPage` |
| 8 | 在 .json 之外又開 identities.json / 之類 single-file roster | 雙 store 同步問題 | 確認 single source-of-truth — 要嘛全 UCL_Asset、要嘛全 single-file |

---

## 7. 何時觸發本 workflow

- 使用者要求「新增一個 X 資料」「做個 X 設定檔」「持久化某狀態」
- agent 看到自己想 `[CreateAssetMenu]` / `ScriptableObject` / 自寫 .json 序列化 — **暫停**，先讀本檔
- code review 看到他人裸 ScriptableObject — 提案改 UCL_Asset

---

## 7.5 驗收（建完 / 改完一律必跑）

> [!IMPORTANT]
> **建立新 asset 子類、或寫 / 改任何 instance JSON 後，必走 [Validate_UCL_Asset_Workflow](Validate_UCL_Asset_Workflow.md) 驗收。** 這是本工作流的**收尾門檻**，不是可選的 see-also。Schema 反序列化、引用完整性的問題若不在此攔下，會 silent 漏到 runtime（enum 拼錯→欄位變預設 / Tag·SkillTag·SpriteAssetEntry ID 不存在→Editor preview 才爆）。

```bash
# 每個新建 / 修改的 instance asset 都跑（路徑相對 git root）
senate ucmd run ValidateAssetFormat \
    --arg assetType=<Type> --arg assetId=<ID> --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
# verdict 必須 = PASS（或 FormattingOnly + 套用 .fixed.json）；reference_check 不可 Missing
```

排查既有 asset「哪些引用壞了 / 完整依賴樹」→ 用 `Cmd_ResolveAssetReferences`（見 Validate workflow §3.4）。
辨別「全域缺失 vs 此 asset 專屬缺失」→ 拿同類正常 asset 對照，別替全域容忍項建假資料。

---

## 8. 範例參考

| Asset | 看點 |
|---|---|
| `UCL_ConfigAsset` | 最小骨架（單 m_Value 字串），DefaultID 慣例 |
| `UCL_BundleAsset` | 加 `IDisposable` + 客製 Field UI (`UCLI_FieldOnGUI`) |
| `UCL_CSVAsset` | 處理大資料 + 自訂 OnGUI |
| `UCL_ChatTavernIdentityAsset` | 角色卡（rich persona） — m_-prefix 欄位、List<string> 集合 |

---

## 9. 相關文件

- [Create_EditorPage_Workflow](Create_EditorPage_Workflow.md) — 對應 UI 層（Page）的建立規範
- [Validate_UCL_Asset_Workflow](Validate_UCL_Asset_Workflow.md) — 改 .json 後跑 ValidateAssetFormat 驗收
- [UCL_SelectAssetPage](../UCL_EditorPage/UCL_SelectAssetPage.md) — 列表 / 選擇 UI（自動接 UCL_Asset 反射）
- [UCL_CommonEditPage](../UCL_EditorPage/UCL_CommonEditPage.md) — 編輯 UI 入口
- [Cmd_MigrateAssetToTemplate](../API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md) — Asset 客製內容回流 Templates~ 當預設範本（指定 type + id 即可）
- [UCL_CoreBootstrap](../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ ↔ 專案 .BuiltinModules 雙向同步機制
