---
name: ucl-create-asset
description: |
  建立新的 UCL_Asset 子類 — UCL_Core 體系下任何持久化資料一律繼承 `UCL_Asset<T>`，禁止裸 ScriptableObject 或自寫存檔。
  使用者要求新增資料類型 / 設定檔 / 角色卡 / 配置 asset、或 agent 自己想動 ScriptableObject / [CreateAssetMenu] / 自寫 JSON 序列化時用本 skill。
  涵蓋繼承樣板、ID/SaveFolderPath 慣例、AssetGroup attribute、與 SelectAssetPage / CommonEditPage 的關係。
  觸發詞包含：新 asset、新增 asset、做個設定檔、scriptable object、create asset menu、persistent data、持久化資料、UCL_Asset、UCL Asset、新 ScriptableObject、新 SO、做張角色卡。
---

# UCL Create Asset — 新增持久化資料類型

> **鐵律**：UCL_Core 體系下，所有持久化資料一律繼承 `UCL_Asset<T>`。**不要**裸 ScriptableObject、**不要**自寫 File.WriteAllText、**不要**自做 FileSystemWatcher 雙向同步。

## 必讀

完整 SOP + 樣板 + 地雷 → `ucl_core:Docs~/zh-Hant/Workflows/Create_UCL_Asset_Workflow.md`

驗收（改完 .json 後）→ `ucl_core:Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md`

## 三分鐘骨架

```csharp
[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
public class UCL_<Name>Asset : UCL_Asset<UCL_<Name>Asset>
{
    public const string DefaultID = "Default";

    public string m_SomeField = string.Empty;
    public List<string> m_SomeList = new List<string>();

    public UCL_<Name>Asset() { ID = DefaultID; }
    public UCL_<Name>Asset(string iID) { Init(iID); }
}
```

就這樣。Edit / Save / Load / List UI 全部 base 自動處理。

## 為什麼不裸 ScriptableObject

| 訴求 | 裸 SO | UCL_Asset |
|---|---|---|
| 跨模組存放 | ❌ | ✅ UCL_ModuleService |
| JSON 序列化 | ⚠ 自寫 | ✅ base 自帶 |
| Editor 編輯 UI | ⚠ 自寫 | ✅ DrawObjectData 反射 |
| 列表 / 選擇 | ⚠ 自寫 | ✅ UCL_SelectAssetPage |
| Mod 系統相容 | ❌ | ✅ |
| Per-file git diff | ⚠ binary YAML | ✅ 純 JSON |

## 高頻地雷

- **裸 ScriptableObject + [CreateAssetMenu]** → 進不了 SelectAssetPage、跟模組系統不通
- **缺無參 ctor** → `UCL_Asset<T> where T : new()` 編不過
- **ctor 內 new List<>** → 反序列化讀不進來，要用 inline initializer
- **自寫 FileSystemWatcher / OnValidate write-back 雙向同步** → 重複造輪 + race condition；UCL_Asset 自己就是 source-of-truth
- **欄位沒 m_ prefix** → 沒對齊 `LocalizeFieldName` 慣例（雖然能用）

## 何時用本 skill

- 使用者說「新增一個 X 設定」「做個 X 資料」「持久化某狀態」「做張角色卡」
- agent 自己想用 `[CreateAssetMenu]` / `ScriptableObject` / 自寫 JSON 持久化時 — **暫停**，先讀本 skill
- code review 看到他人裸 ScriptableObject — 提案改 UCL_Asset

## 範例參考

- `UCL_ConfigAsset` — 最小骨架（單欄）
- `UCL_BundleAsset` — IDisposable + 客製 Field UI
- `UCL_ChatTavernIdentityAsset` — rich data（List<string> 集合 + m_-prefix）
