---
date: 2026-05-08
index: 00015
title: UCL_Core Bootstrap 雙向同步 — AutoTemplatePush + UCL_BootstrapAdminPage + 跨語系翻譯生態
tags: [feature, bootstrap, infra, tools, docs]
---

# UCL_Core Bootstrap 雙向同步 — Templates~ ↔ 專案 .BuiltinModules 自動分發

## What

`UCL_CoreAssetBootstrap` 從原本的「單向 create_if_missing」（Templates~ → 專案，僅補缺）擴成**雙向同步機制**，並補上 admin UI 與跨語系翻譯生態。

### 1. 反向同步：AutoTemplatePushIfNeeded（新）

`[InitializeOnLoadMethod]` + `delayCall` 自動觸發（與既有 `AutoApplyIfNeeded` 同模式）：

| 情境 | 行為 |
|---|---|
| Templates~ 與專案完全一致 | 0 副作用 silent return（熱路徑）|
| Templates~ 有 / 專案沒有（**新檔**）| **silent 自動複製**到專案 .BuiltinModules，Console log，**無 dialog** |
| 兩邊都有但 byte 不同（**衝突 — 專案本地已修改**）| 過濾 skip marker 後彈 **per-file dialog**（Win Explorer 風格 3 選項：覆蓋 / 全部覆蓋(剩餘) / 跳過）|
| 專案有 / Templates~ 沒有 | **不處理**（專案本地自訂，超出 scope）|

### 2. Skip Marker 機制（防 dialog spam）

`ProjectSettings/UCL_CoreTemplatePush.skipped.json` — JSON `{ rel_path: sha1_of_template_when_skipped }`：
- 使用者按「跳過」→ record 當下 **Template hash**（不是 project hash — 因為對使用者而言「拒絕的是這個版本的 Template」）
- 下次 reload Template hash 不變 → silent skip，不重複 prompt
- Templates~ 又被 bump 改動 → hash 變 → 重新 prompt 給 user 看新版
- 按「覆蓋」→ remove from marker
- summary 按「取消」→ 全部衝突以當下 Template hash record（明示拒絕本批）

### 3. 觸發邊界（重要 — 不是 polling）

| 動作類型 | 會觸發 AutoTemplatePush 嗎 |
|---|---|
| Editor 啟動 / `[InitializeOnLoad]` reload | ✓ |
| 改 .cs 後 recompile（含 `run Recompile` cmd）| ✓（domain reload）|
| 手動 `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` menu | ✓（即時）|
| **bash 直接 `rm`** 或外部修改 .json | ✗ |

不是 real-time polling — 對齊既有 AutoApplyIfNeeded 的 reload-driven 模式。

### 4. UCL_BootstrapAdminPage（新 IMGUI Page）

集中 4 個 Bootstrap 動作的視覺化入口（放 `Editor/` folder 跟 `UCL_CoreEditor` asmdef 對齊）：

| 區塊 | 動作 | 顏色 |
|---|---|---|
| ① Apply Missing Defaults | 補缺（Templates~ → 專案，create_if_missing）| 綠 |
| ② Push Templates → Modules | 推送 Template 變動（含衝突 dialog）| 藍 |
| ③ Diff Against Templates | 純讀，console output | 灰 |
| ④ Force Re-Apply | 破壞性，覆寫所有檔（confirm dialog）| 橘 |

`ShowInPageMenu => true` → 進 `UCL_EditorMenuPage` Page Picker 下拉。

### 5. Cmd_MigrateAssetToTemplate

通用 UCL_Asset .json 從專案 `.BuiltinModules` 搬到 `Templates~` 的工具：
- 反射驗證 `assetType` 真的繼承 `UCL_Asset<T>`（防亂遷檔）
- args：`assetType` (短型別名) / `id` (具體 ID 或 `*`) / `module` (預設 Core) / `force`
- 路徑透過 `UCL_AssetPath.GetPath(BuiltinModules / TemplateModules)` API 解析

### 6. 跨語系翻譯生態（ucl-translate-docs 配套產出）

新增 `ucl-translate-docs` skill + `Docs~/zh-Hant/Workflows/TranslateDocs_Workflow.md` 規範：
- Glossary-First 術語對齊
- 雙軌 Fallback 死連結預防
- 三層語氣架構（含傲嬌大小姐 persona 本地化）

跑出 12 篇翻譯（4 docs × 3 locales）：
- `Cmd_MigrateAssetToTemplate.md` / `Cmd_SeedTavernIdentityAssets.md` / `Create_UCL_Asset_Workflow.md` / `TranslateDocs_Workflow.md`
- 部署到 `Docs~/{en,ja,zh-Hans}/...`

---

## Why

### 1. 跨專案 Template 改動分發痛點

UCL_Core 維護者改了 Templates~ 內某檔（例 ChatTavernIdentityAsset 的 schema） → 別的下游專案 pull UCL_Core 後得**手動跑** `Apply Missing Defaults`（且該 menu 只補缺，不覆寫）。

加 `AutoTemplatePushIfNeeded` 後：
- 下游專案 pull → `[InitializeOnLoad]` 自動觸發 → 看到 Templates 有新檔 → silent 複製
- 衝突情境（下游也改了同檔）跳 dialog 讓使用者決定，**不無聲覆蓋本地修改**

### 2. Skip marker 的 hash 鍵設計

第一直覺是 record 「project hash 當下」，但這樣改不到使用者「拒絕特定版本 Template」的語意。改 record **Template hash** 後：
- Template 沒再 bump → 同樣的「我不要」決定保持有效 → silent skip
- Template 被 bump 改動 → 視為新版本 → 重新 prompt 給 user 評估
- 使用者改本地檔 → 不影響 marker（rejection 是針對 Template 版本，不是 project 狀態）

### 3. Per-file dialog（Win Explorer 風格）

不要一次性「全部覆蓋 / 全部跳過」二選一 — 每筆檔可能語意不同（有的是 schema 升級該收，有的是 user 客製化想保留）。每筆獨立確認 + 「全部覆蓋(剩餘)」逃生口，平衡 user agency 與批次效率。

### 4. UCL_BootstrapAdminPage 而非 menu only

`Tools/UCL/Bootstrap/...` 4 個 menu 散落在 Tools 選單，使用者不熟難找。集中成 IMGUI Page 並掛 `ShowInPageMenu => true`，從 `UCL_EditorMenuPage` Page Picker 下拉就能找到 — 對齊 [Create_EditorPage_Workflow §4.1](../Docs~/zh-Hant/Workflows/Create_EditorPage_Workflow.md) 「非衍生頁必須掛 EditorMenu」規則。

### 5. 翻譯生態與術語一致性

跨語系文件最大的痛是「同一術語不同地方翻不一樣」。`ucl-translate-docs` skill 強制 Glossary-First — 翻譯前先讀 `_synonyms.txt` 同義詞表，所有 agent 翻譯 UCL_Core / RCG 專有詞時對齊統一翻譯。

---

## How to use

### 自動觸發（多數情況不必手動）
Editor 啟動 / domain reload 時自動跑。Console 看 `[UCL_Core Bootstrap]` log 確認狀況。

### 手動觸發
從 EditorMenu Page Picker → `UCL_Core Bootstrap Admin` → 4 顆按鈕

或直接 menu：
```
Tools/UCL/Bootstrap/Apply Missing Defaults
Tools/UCL/Bootstrap/Push Templates → Modules (Force)
Tools/UCL/Bootstrap/Diff Against Templates
Tools/UCL/Bootstrap/Force Re-Apply (Overwrite!)
```

### Asset 回流到 Templates~（migrate）
```bash
# 把當前專案 UCL_ChatTavernIdentityAsset 全部搬到 Templates~ 當預設範本
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset --arg id=*

# 單筆 + 強制覆寫已有 Template
python ... run MigrateAssetToTemplate \
    --arg assetType=UCL_ConfigAsset --arg id=CurLangKey --arg force=true
```

### 重置 skip marker
若想重新看一遍所有「之前跳過」的衝突 — 刪 `ProjectSettings/UCL_CoreTemplatePush.skipped.json` 即可。或從 `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` menu 入口（強迫重問所有衝突，忽略 skip marker）。

### 跨語系翻譯
詳見 `Skills~/ucl-translate-docs/SKILL.md` 與 `Docs~/zh-Hant/Workflows/TranslateDocs_Workflow.md`。

---

## Files

### 新增
- `Editor/UCL_BootstrapAdminPage.cs`
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_MigrateAssetToTemplate.cs`
- `Skills~/ucl-create-asset/SKILL.md`
- `Skills~/ucl-translate-docs/SKILL.md`
- `Docs~/zh-Hant/Workflows/Create_UCL_Asset_Workflow.md`
- `Docs~/zh-Hant/Workflows/TranslateDocs_Workflow.md`
- `Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md`
- `Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_SeedTavernIdentityAssets.md`
- `Docs~/{en,ja,zh-Hans}/...` × 4 docs（12 篇翻譯）

### 修改
- `Editor/UCL_CoreAssetBootstrap.cs` — 加 AutoTemplatePushIfNeeded + RunTemplatePushWithDialogs + skip marker；TemplatesContentVersion 2 → 3
- `Docs~/zh-Hant/UCL_ModuleService/UCL_CoreBootstrap.md` — 加反向 sync section
- `Docs~/zh-Hant/Workflows/Create_EditorPage_Workflow.md` — 加 cross-link 到 Create_UCL_Asset_Workflow
- `Skills~/_manifest.json` — 加 ucl-create-asset / ucl-translate-docs 兩條
