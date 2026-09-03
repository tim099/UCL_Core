---
title: Cmd_SeedTavernIdentityAssets — 從 identities.json roster 建 UCL_ChatTavernIdentityAsset 殼
description: Agent Command — 讀 identities.json 為每筆 identity 建一個對應的 UCL_ChatTavernIdentityAsset .json 殼，預填 m_Tags 一筆對應 kind；其他 rich data 欄位（avatar / role_settings / color / catchphrases）保持空，等使用者編輯。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, tavern, identity, asset, bootstrap]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md | Cmd_MigrateAssetToTemplate | 把 seed 出的 Asset 搬到 Templates~ 當預設範本
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ 專案 .BuiltinModules 雙向同步
---

# Cmd_SeedTavernIdentityAssets

依 `identities.json` lightweight roster 為每筆 identity 建立一個對應的 `UCL_ChatTavernIdentityAsset` .json 殼。

---

## 1. 概述

### 為什麼需要

- `identities.json` 是 **lightweight roster**（id / display_name / kind / created_at / last_seen_at），給 Cmd_Tavern 與 Python 用
- `UCL_ChatTavernIdentityAsset` 是 **rich persona** view layer（avatar / role_settings / color / catchphrases / tags）
- 兩者獨立 — 但 rich data 通常會跟某個 identity 對應
- 首次想為某 identity 加 rich data 時，需要先 seed 出對應 .json 殼 → 再用 Editor 編輯

### 完整流程（一次性 bootstrap）

```
1. (前提) identities.json 已有 5 筆 identity（Cmd_Tavern op=join 自然產生）
   ▼
2. Cmd_SeedTavernIdentityAssets 一鍵建 5 個 UCL_ChatTavernIdentityAsset .json 殼
   ▼ (落在 <project>/Assets/.BuiltinModules/.../UCL_Assets/UCL_ChatTavernIdentityAsset/)
3. Editor 內用 UCL_SelectAssetPage 找 UCL_ChatTavernIdentityAsset → 編 avatar / role_settings / catchphrases
   ▼
4. (可選) Cmd_MigrateAssetToTemplate id=* 把所有 Asset 搬到 Templates~
   ▼
5. 跨專案散播 — 別的專案 pull UCL_Core 後 AutoTemplatePush 自動補齊
```

---

## 2. 參數

| 參數 | 必填 | 預設 | 說明 |
|---|---|---|---|
| `force` | ❌ | `false` | `true` = 覆寫已存在的 Asset；`false` = skip |
| `onlyId` | ❌ | `""` | 只 seed 指定 id（適用補單筆 / 測試）；空 = 全部 roster |

---

## 3. 預填欄位

| 欄位 | 預填內容 | 備註 |
|---|---|---|
| `ID` | identity.id | UCL_Asset 系統的穩定鍵 |
| `m_Tags` | `[<kind>]` | 對應 roster.kind（"agent" / "human" / "npc" / "system"），讓使用者一眼看到分類 |
| `m_AvatarPath` | `""` | 待使用者編輯（Inspector 拖 Sprite path） |
| `m_RoleSettings` | `""` | 待使用者編輯（persona 模板片段） |
| `m_ColorHex` | `""` | 待使用者編輯（#RRGGBB） |
| `m_Catchphrases` | `[]` | 待使用者編輯（LLM persona reminder bullets） |

---

## 4. 路徑

```
src = AgentCommands/ChatTavern/identities.json (roster)
dst = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/<id>.json
```

走 `UCL_Asset.Save()` API，由 `UCL_ModuleService` 解析當前 edit module 路徑。

---

## 5. 使用範例

```bash
# 全 roster seed（預設不覆寫已存在的）
senate ucmd run SeedTavernIdentityAssets

# 強制全部覆寫
senate ucmd run SeedTavernIdentityAssets --arg force=true

# 只 seed 單筆（測試 / 補件）
senate ucmd run SeedTavernIdentityAssets --arg onlyId=claude-da-xiaojie
```

---

## 6. 完成後動作

Console 會印 `created / skipped / failed` 計數 + 下一步建議：
- 開 Editor 用 `UCL_SelectAssetPage` 找 `UCL_ChatTavernIdentityAsset` 編輯
- 編完後跑 `Cmd_MigrateAssetToTemplate id=*` 把它們搬到 Templates~

---

## 7. 相關文件

- [Cmd_MigrateAssetToTemplate](Cmd_MigrateAssetToTemplate.md) — 下一步：搬到 Templates~
- [UCL_CoreBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ 系統機制
- [Create_UCL_Asset_Workflow](../../Workflows/Create_UCL_Asset_Workflow.md) — UCL_Asset 框架
