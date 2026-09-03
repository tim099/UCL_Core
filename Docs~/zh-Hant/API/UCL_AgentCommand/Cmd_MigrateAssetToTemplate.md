---
title: Cmd_MigrateAssetToTemplate — UCL_Asset .json 從專案遷移到 Templates~
description: Agent Command — 把指定 UCL_Asset 子類的 .json 從當前專案 .BuiltinModules 複製到 UCL_Core 的 Templates~（成為跨專案範本）。配合 UCL_CoreAssetBootstrap 的 AutoTemplatePush 自動分發機制使用。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, migration, template, asset]
related:
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ 專案 .BuiltinModules 雙向同步機制
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL_Asset Workflow | 新增持久化資料的 SOP
---

# Cmd_MigrateAssetToTemplate

把指定 UCL_Asset 子類的 .json 實例從當前專案 .BuiltinModules 遷移到 UCL_Core 倉庫 Templates~ 內，**讓該 Asset 變成跨專案預設範本**。

---

## 1. 概述

### 何時用

- 開發者在某專案內客製化了某個 UCL_Asset（例如：UCL_ChatTavernIdentityAsset 的 `claude-da-xiaojie`）
- 想把這份客製內容當作預設範本回流到 UCL_Core 倉庫
- 後續其他專案 pull UCL_Core 後，[UCL_CoreAssetBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) 的 **AutoTemplatePush** 會自動把它推送到那些專案的 .BuiltinModules 中

### 跟既有機制的關係

| 動作 | 工具 |
|---|---|
| 新增 / 修改 UCL_Asset 實例 | UCL_SelectAssetPage / UCL_CommonEditPage（Editor UI） |
| 把已修改的 Asset 變成 Template（**本 Cmd**）| Cmd_MigrateAssetToTemplate |
| Template 自動分發到別的專案 | [`AutoTemplatePushIfNeeded`](../../UCL_ModuleService/UCL_CoreBootstrap.md) (InitializeOnLoad) |
| 手動觸發 Template 推送 | `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` |

---

## 2. 參數

| 參數 | 必填 | 預設 | 說明 |
|---|---|---|---|
| `assetType` | ✅ | — | UCL_Asset 子類短名（例 `UCL_ChatTavernIdentityAsset`）；大小寫敏感 |
| `id` | ✅ | — | 要遷移的 Asset ID（例 `claude-da-xiaojie`）；填 `*` 表示遷移該類型全部 |
| `module` | ❌ | `Core` | 來源 module id（多 module 專案才需指定） |
| `force` | ❌ | `false` | `true` = 直接覆寫已存在的 Template；`false` = 已存在則 skip |

---

## 3. 路徑映射

```
src = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
dst = <UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
```

例（id=`claude-da-xiaojie`、assetType=`UCL_ChatTavernIdentityAsset`、module=`Core`）：
- src：`<project>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`
- dst：`<UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`

`UCL_AssetPath.GetPath(BuiltinModules / TemplateModules)` 解析路徑。

---

## 4. 行為

1. **驗證 assetType**：反射跨 assembly 找名字符合 + 真的繼承 `UCL_Asset<T>` 的 class；找不到 → fail
2. **計算 src / dst 目錄**：`UCL_Assets/<TypeName>` 子資料夾（對齊 [`UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath`](../../../UCL_Core_Scripts/AssetCore/UCL_ModulePath.RelativePath.cs)）
3. **單檔 vs 全部**：
   - `id=<具體 ID>`：copy 單檔
   - `id=*`：enumerate src 內所有 `*.json`，逐筆 copy
4. **存在則 skip / overwrite**：依 `force` 旗標決定
5. **完成**：印 `copied / skipped / missing` 計數 + src/dst 路徑 + 「未自動 commit」提醒

---

## 5. 使用範例

### 從 Python (run_cmd.py)

```bash
# 單筆遷移
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=claude-da-xiaojie

# 全部遷移
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=*

# 強制覆寫（已有 Template 也覆蓋）
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ConfigAsset \
    --arg id=CurLangKey \
    --arg force=true

# 指定 module
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=MyAsset \
    --arg id=MyID \
    --arg module=MyCustomModule
```

### 從 UCL_AgentCommandsPage（Editor UI）

`Tools/UCL/Agent Commands` → 找 `MigrateAssetToTemplate` → 「Fill Example」自動填入示範參數 → Run

---

## 6. 完成後動作

⚠ **Cmd 不會自動 commit** — 寫完 Templates~ 後仍須走 [ucl-commit skill](../../../Skills~/ucl-commit/SKILL.md) 三層 bump 流程：

```bash
# 1. UCL_Core 切 Dev → commit
git -C <UCL_Core> switch Dev
git -C <UCL_Core> add Templates~
git -C <UCL_Core> commit -m "[feat] migrate <assetType>:<id> as default template"

# 2. UCL submodule bump
git -C <UCL> switch Dev
git -C <UCL> add UCL_Core
git -C <UCL> commit -m "[bump] UCL_Core <hash>"

# 3. 主專案 bump
git -C <project> add CardGame/Assets/UCL
git -C <project> commit -m "[bump] UCL <hash>"
```

詳見 [Commit_Workflow.md](../../Workflows/Commit_Workflow.md)。

---

## 7. 失敗情境與排查

| 症狀 | 原因 | 解法 |
|---|---|---|
| `找不到 UCL_Asset 子類 'X'` | type 名拼錯 / 還沒編譯 | 檢查拼字（短名，不含 namespace）+ 確認 .cs 檔已編入 |
| `來源目錄不存在` | 該 type 在當前專案還沒任何實例 | 先在 Editor 內用 UCL_SelectAssetPage 建一筆，編完後再跑遷移 |
| `來源檔不存在 — skip` | 指定 ID 對應的 .json 不存在 | 確認 ID 拼字 / 用 `id=*` 看實際存在哪些 |
| `target 已存在 (force=false) — skip` | Template 已有同檔，預設不覆寫 | 加 `--arg force=true` 強制覆寫 |
| `找不到 TemplateModules 路徑` | UCL_CoreEditor.asmdef 找不到 | 檢查 UCL_Core 路徑是否完整 |

---

## 8. 相關文件

- [UCL_CoreBootstrap.md](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ 系統與 AutoTemplatePush 機制全貌
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — 新增 UCL_Asset 子類的 SOP
- [UCL_AgentCommand_Architecture.md](UCL_AgentCommand_Architecture.md) — Agent Command 系統架構
- [Commit_Workflow.md](../../Workflows/Commit_Workflow.md) — 三層 submodule bump 流程
- [Cmd_SeedTavernIdentityAssets](Cmd_SeedTavernIdentityAssets.md) — 從 identities.json roster 建 UCL_ChatTavernIdentityAsset 殼（搬遷前的前置 seed）
