---
title: UCL_Core Asset Bootstrap 機制
description: UCL_Core 安裝到新專案時自動補齊預設 Asset 的機制 — Templates~ 範本、版本 marker、UI 編輯模式三層架構
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
tags: [bootstrap, infra, module-system]
aliases: [bootstrap, 預設值, 範本, default assets, templates, UCL_CoreAssetBootstrap]
---

# UCL_Core Asset Bootstrap 機制

UCL_Core 作為 submodule 安裝到新 Unity 專案時，使用者通常需要手動建立一堆 `.BuiltinModules/...` 下的 JSON Asset 才能讓框架活起來。Bootstrap 機制把這條路自動化。

## 架構三層

| 層 | 角色 | 對應檔案 |
|---|---|---|
| 範本資料 | 預設 Asset 的真相來源 | `UCL_Core/Templates~/Assets/...` |
| Bootstrap 控制 | `[InitializeOnLoadMethod]` 自動補缺 + Tools 選單 | `UCL_Core/Editor/UCL_CoreAssetBootstrap.cs` |
| UI 編輯 | 透過 `UCL_ModuleServiceEditPage` 直接編 Templates~ | `UCL_ModuleEditType.Template` 模式 |

---

## 1. Templates~ 範本資料

### 路徑佈局

```
UCL_Core/Templates~/
└── Assets/                        ← 鏡像專案根「Assets/」起點
    └── .BuiltinModules/...        ← 一比一鏡像最終目標位置
```

- **`~` 後綴的意義**：Unity 自動忽略 `Templates~` 不做 import，所以 UCL_Core 帶著它一起進新專案，但不污染對方的 Asset 樹
- **無 manifest 檔**：Bootstrap 直接遞迴 walk 整顆 `Assets/` 子樹，**不需要列檔**

### 新增 / 修改範本

| 想做什麼 | 步驟 |
|---|---|
| 加一個新預設檔 | ① 把檔案放進 `Templates~/Assets/.BuiltinModules/.../<新檔>.json` ② 編 [`UCL_CoreAssetBootstrap.cs`](../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/) `TemplatesContentVersion += 1` |
| 加一整顆新 module | 拖整個資料夾進 `Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/`，然後 bump version |
| 想透過 UI 編 | 切到「Template 編輯模式」，見[第 3 節](#3-template-編輯模式) |

### 為什麼不要 manifest

第一版用過 `manifest.json` 列檔 + per-file policy，後來砍掉：

1. 99% 用例只是「不存在就建」（`create_if_missing`）— 沒必要每檔標 policy
2. 新增範本要改檔 + 編 manifest → 容易漂移
3. `force_overwrite` 已經有 `Force Re-Apply` 選單覆蓋
4. 「哪些是新增的」這資訊由 marker version 比較就夠

---

## 2. Bootstrap 控制邏輯

### 自動觸發

```csharp
[InitializeOnLoadMethod]
static void OnEditorLoad()
{
    EditorApplication.delayCall += AutoApplyIfNeeded;
}
```

每次 Editor 啟動 / domain reload 時跑一次（`delayCall` 推遲一拍避開太早觸發）。

### 邏輯流程

```
1. ReadMarker (ProjectSettings/UCL_CoreBootstrap.version)
   └─ marker >= TemplatesContentVersion ? early-return ★熱路徑
2. ScanPending
   └─ 遞迴 walk Templates~/Assets/，列出 dest 不存在的相對路徑
3. 0 pending → 寫 marker，退出
4. applied == 0 (首次安裝) → 自動套用，無 dialog
5. applied > 0 (升級) → DisplayDialogComplex 三選一
       ├─ 套用       → 寫 marker
       ├─ 稍後再問   → 不寫 marker，下次 reload 再彈
       └─ 不再提示   → 寫 marker，跳過此版本
```

### 版本 const

```csharp
public const int TemplatesContentVersion = 2;
```

- **bump 時機**：UCL_Core 維護者新增 / 有意義地修改 Templates~ 內容、想讓既有專案被「再問一次」
- **marker 位置**：`ProjectSettings/UCL_CoreBootstrap.version` — 跟著 Unity 設定走 git，不污染 Asset 樹
- **使用者刪檔安全性**：Bootstrap 在 `marker >= version` 時根本不會掃描，所以使用者主動刪除某個預設 Asset 不會被自動重建

### Tools 選單（手動入口）

| 選單 | 用途 | 是否寫檔 |
|---|---|---|
| `Tools/UCL/Bootstrap/Apply Missing Defaults` | 手動觸發補缺 | ✅ |
| `Tools/UCL/Bootstrap/Diff Against Templates` | Console 印 missing / modified / identical 摘要 | ❌（純讀） |
| `Tools/UCL/Bootstrap/Force Re-Apply (Overwrite!)` | 用範本【覆寫】所有對應 Asset，帶 confirm dialog | ✅（破壞性） |

---

## 3. Template 編輯模式

`UCL_ModuleEditType` enum 多一個 `Template` 值：

| EditType | 路徑 | 用途 |
|---|---|---|
| `Builtin` | `Application.dataPath/.BuiltinModules/...` | 開發期 source，隨 Build 進 StreamingAssets |
| `Runtime` | `Application.persistentDataPath/...` | Player 端可寫；mod / 玩家自訂 |
| **`Template`** | `<UCL_Core>/Templates~/Assets/.BuiltinModules/...` | **編輯 bootstrap 預設值範本本身**（Editor-only） |

### 使用流程

1. 開啟 `UCL_ModuleServiceEditPage`
2. EditType 下拉切到「預設範本」
3. 列表顯示 Templates~ 內既有的 modules（自動 scan disk）
4. 編輯 Asset → 「儲存設定」 → 直接寫到 `Templates~/Assets/...`
5. 改完 commit → 下次別人 pull 後，bootstrap 會用新版範本補缺

### Editor-only 限制

- `UCL_AssetPath.GetPath(TemplateModules)` 在 build 中回 `string.Empty`
- `UCL_ModuleService` 在 `!Application.isEditor` 時強制設為 `Runtime`，所以 build 後 Template 不會出現

### 路徑解析機制

`UCL_AssetPath.GetPath(TemplateModules)` 透過 `AssetDatabase.FindAssets("UCL_CoreEditor")` 反推 UCL_Core 根目錄 → 接 `Templates~/Assets/.BuiltinModules`，所以使用者把 UCL_Core 放在 `Assets/` 下任何位置都能找到。結果 cache 在 static field 直到 domain reload，避免每次呼叫都掃 AssetDatabase。

---

## 4. FAQ

### Q. 我把 UCL_Core clone 到新專案，需要做什麼？
A. **什麼都不用**。開 Unity，bootstrap 自動跑、自動補缺、寫 marker。Console 會印 `[UCL_Core Bootstrap] First-time install — applied N default asset(s).`

### Q. 我刪掉了某個預設 Asset，Bootstrap 會不會重建？
A. 不會。除非 UCL_Core 維護者 bump `TemplatesContentVersion`，或你手動跑 `Tools/UCL/Bootstrap/Apply Missing Defaults`。

### Q. 我修改了某個預設 Asset 的內容，下次 UCL_Core 升級會不會被覆蓋？
A. 不會。Bootstrap 只 `create_if_missing`。要強制覆寫只能透過 `Tools/UCL/Bootstrap/Force Re-Apply` 並會 confirm。

### Q. 我想加新預設 Asset 給其他專案的人用，怎麼做？
A. 切到 `EditType = 預設範本` 編輯（或直接在 `Templates~/Assets/...` 放檔），然後在 [`UCL_CoreAssetBootstrap.cs`](../../Editor/UCL_CoreAssetBootstrap.cs) 把 `TemplatesContentVersion` +1，commit。其他人 pull 後 bootstrap 會 detect 並彈 dialog。

### Q. Marker 不見了會發生什麼？
A. Bootstrap 會以為從未套用，跑首次 install 流程。若所有檔已存在 → 0 pending → 直接寫回 marker。若部分缺漏 → 自動套用。基本上沒有壞影響。

### Q. CI / Batchmode 也會跑 bootstrap 嗎？
A. 會。`[InitializeOnLoadMethod]` 在 batchmode 也跑。但 `EditorUtility.DisplayDialogComplex` 在 batchmode 會 default 到第一個按鈕（套用），這通常正是 CI 期望的行為。

---

## 相關檔案

- 控制邏輯：[`UCL_Core/Editor/UCL_CoreAssetBootstrap.cs`](../../Editor/UCL_CoreAssetBootstrap.cs)
- EditType enum：[`UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs`](../../UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs)
- AssetType + Path：[`UCL_AssetPath.cs`](../../UCL_Core_Scripts/AssetCore/UCL_AssetPath.cs) / [`UCL_Module.cs`](../../UCL_Core_Scripts/AssetCore/UCL_Module.cs)
- DevLog：[00011_2026-05-07_core-bootstrap-templates](../../DevLogs~/00011_2026-05-07_core-bootstrap-templates.md)
