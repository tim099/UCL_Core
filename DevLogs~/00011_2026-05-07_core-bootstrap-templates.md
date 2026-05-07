---
date: 2026-05-07
index: 00011
title: UCL_Core Asset Bootstrap + Templates~ + Template 編輯模式
tags: [feature, infra, module-system]
---

# UCL_Core Asset Bootstrap — 一條龍解決「裝到新專案要手動建檔」的痛點

## What

三件事一氣呵成：

1. **`Templates~/` 範本資料夾** — 放 UCL_Core 安裝到新專案時的預設 Asset；初版 5 種 8 個檔（Config / 4 國語系 LanguageCodeAsset / LocalizeAsset.UCL / ConfigAsset.CurLangKey / ModulePlaylist.Default）
2. **`UCL_CoreAssetBootstrap`（[Editor/](../Editor/UCL_CoreAssetBootstrap.cs)）** — `[InitializeOnLoadMethod]` 自動補缺；`Tools/UCL/Bootstrap/` 提供 Apply Missing / Diff / Force Re-Apply 三顆手動入口
3. **`UCL_ModuleEditType.Template` 編輯模式** — `UCL_ModuleServiceEditPage` 多一個下拉選項，讓使用者用既有 ModuleService UI 直接編輯 Templates~ 內的 Asset（編輯範本本身）

## Why

### 痛點
- UCL_Core 是 submodule，clone 進新專案後 `.BuiltinModules/ModulesRoot/Modules/Core/` **完全空的** — 沒 LanguageCodeAsset、沒 Config、什麼都沒有
- 既有 install pipeline（`UCL_WelcomeAutoOpen` / `UCL_CoreDocsBootstrap`）只做 UI / 文件註冊，**不會生資料 Asset**
- 結果：每次有人開新專案就要從別處 copy-paste 一堆 JSON，或文件指引「請手動建立」 — 摩擦大、容易漏

### 目標：cd 進新專案 → 開 Unity → 框架就活

## How — 三段架構

### 1. `Templates~/` 範本（資料層）

```
UCL_Core/Templates~/
└── Assets/
    └── .BuiltinModules/
        └── ModulesRoot/
            └── Modules/
                └── Core/
                    ├── Config.json
                    ├── ModResources/LocalizeDatas/UCL/{en,ja,zh-Hant,zh-Hans}.txt
                    └── UCL_Assets/
                        ├── UCL_LanguageCodeAsset/{en,zh-Hant,zh-Hans,ja}.json
                        ├── UCL_LocalizeAsset/UCL.json
                        ├── UCL_ConfigAsset/CurLangKey.json
                        └── UCL_ModulePlaylist/Default.json
```

- 路徑與最終目標位置 **一比一鏡像**（去掉 `Templates~/` 前綴 = 專案根相對路徑）
- `~` 後綴讓 Unity 不 import → 不污染目標專案的 Asset 樹
- **無 manifest 檔**：曾用過 `manifest.json` 列檔，後來砍了 — 改成「直接掃整顆 Assets/ 子樹」少一層維護負擔（[參見此檔的演化](#evolution--manifest-json--folder-walk)）

### 2. `UCL_CoreAssetBootstrap`（控制層）

#### 觸發點
```csharp
[InitializeOnLoadMethod]
static void OnEditorLoad()
{
    EditorApplication.delayCall += AutoApplyIfNeeded;
}
```

延後一拍跑 — 跟 `UCL_WelcomeAutoOpen` 同款手法，避開 InitializeOnLoad 太早 NRE 風險。

#### 邏輯流程
```
1. ReadMarker (ProjectSettings/UCL_CoreBootstrap.version)
   └─ marker >= TemplatesContentVersion ? early-return (熱路徑)
2. ScanPending — 遞迴 walk Templates~/Assets/，列出 dest 不存在的相對路徑
3. pending 為空 → 寫 marker，退出
4. applied == 0（首次安裝）→ 自動套用，不打擾
5. applied > 0（升級）→ DisplayDialogComplex「套用 / 稍後再問 / 不再提示」
```

#### 版本機制 — C# const，不是檔案

```csharp
public const int TemplatesContentVersion = 2;
```

- bump 條件：UCL_Core 維護者新增 / 有意義地修改 Templates~ 內容、想讓既有專案被「再問一次」
- marker（`ProjectSettings/UCL_CoreBootstrap.version`）追蹤使用者已套用版本
- 不放 Assets/ — marker 是 per-project 設定，不該污染 Asset 樹
- **使用者刪掉某個預設 Asset → 不會被 bootstrap 重建**：因為 marker 已經 ≥ version，整段 ScanPending 跑不到

#### 三顆 Tools 選單
| 入口 | 用途 |
|---|---|
| `Tools/UCL/Bootstrap/Apply Missing Defaults` | 手動觸發補缺（不 bump version 也能用） |
| `Tools/UCL/Bootstrap/Diff Against Templates` | Console 印 missing / modified / identical 摘要，純讀 |
| `Tools/UCL/Bootstrap/Force Re-Apply (Overwrite!)` | 用範本覆寫所有對應 Asset；帶 confirm dialog |

### 3. `UCL_ModuleEditType.Template` 編輯模式（UI 層）

Module 系統原本有 `Builtin` / `Runtime` 兩種模式對應不同存儲位置。新增第三種 `Template`：

| EditType | 路徑 | 用途 |
|---|---|---|
| `Builtin` | `Application.dataPath/.BuiltinModules/...` | 開發期 source（隨 Build 進 StreamingAssets） |
| `Runtime` | `Application.persistentDataPath/...` | Player 端可寫；mod / 玩家自訂 |
| **`Template`** | `<UCL_Core>/Templates~/Assets/.BuiltinModules/...` | **編輯 bootstrap 預設值範本本身**（Editor-only） |

切換流程：
1. 開 `UCL_ModuleServiceEditPage`
2. EditType 下拉選「預設範本」
3. 列表顯示 Templates~ 內的 modules（自動掃 disk）
4. 編輯 Asset → Save → 直接寫到 Templates~/

#### 改動點
- `UCL_ModuleEditType` enum 加 `Template`（[UCL_ModuleService.cs](../UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs)）
- `UCL_AssetType` enum 加 `TemplateModules`（[UCL_Module.cs](../UCL_Core_Scripts/AssetCore/UCL_Module.cs)）
- `UCL_AssetPath.GetPath(TemplateModules)` 用 `AssetDatabase.FindAssets("UCL_CoreEditor")` 反推 UCL_Core 根 → 接 `Templates~/Assets/.BuiltinModules`（cache 後重用，`#if UNITY_EDITOR` 包裹）
- `UCL_ModulePathConfig.AssetType` 加 case `Template → TemplateModules`
- `UCL_ModulePath.PersistantPath.GetModulesEntry` + `ModulesEntry` ctor 加 Template case
- 4 國語系 `UCL_ModuleEditType_Template` localize key：預設範本 / 预设范本 / Template / テンプレート

## Evolution — manifest.json → folder-walk

第一版設計是 `Templates~/manifest.json` 列出每個範本檔案 + per-file policy（`create_if_missing` / `force_overwrite`）+ `since` 版本欄。

砍掉的理由：
1. 99% 用例是 `create_if_missing`，policy 維護成本大於收益
2. 新增範本要改兩處（檔 + manifest）→ 容易漂移
3. `force_overwrite` 已經被 `Force Re-Apply` 選單覆蓋
4. `since` 用來算「哪些是新增的」，但其實「目前缺漏」這個資訊已足夠（marker version 升級時 walk 一次即可）

改成 `Directory.EnumerateFiles(Templates~/Assets/, "*", AllDirectories)` 後：
- 加新範本 = 拖檔進 `Templates~/Assets/` + bump 一個 const
- 加整個 module = 拖整個資料夾進去 + bump const
- 程式碼少 ~80 行（manifest 解析 / entry 結構整批刪）

## 驗證

| 場景 | 預期 | 實測 |
|---|---|---|
| 全新 install（marker 不存在） | 自動套用所有缺漏，不彈 dialog | ✅ 本 repo 首跑時 marker=1 寫入，無打擾 |
| 升 version（marker < const） | DisplayDialogComplex 三選一 | ✅ v1→v2 時對 dummy 缺漏專案實測通過 |
| 已最新（marker == const） | early-return，0 副作用 | ✅ marker=2 後 Editor reload 無 log |
| 編譯 | 0 errors | ✅ 0 errors / 93 unrelated warnings |

## 副作用 / 待觀察

- **使用者編輯 Templates~ 後忘記 git add**：因為 `~` 後綴 Unity 不 import，但 git 會看到 — 不算地雷，正常 git status 即可發現
- **Bootstrap dialog 太頻繁的擔憂**：因為 marker version-gated，只有 UCL_Core 維護者主動 bump 才會再彈；理論上一個版本只彈一次
- **`Apply to Form` 名稱冗餘？**：Template 模式現在跟 Builtin 視覺上幾乎一樣（只有路徑不同），未來可能要在 UI 上加個提示「你正在編輯 framework 範本，不是本專案資料」
