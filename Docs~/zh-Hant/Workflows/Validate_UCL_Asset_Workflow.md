---
title: Validate UCL_Asset 工作流程
description: 在寫完 / 改完任何 UCL_Asset JSON 後，用 Cmd_ValidateAssetFormat 做 round-trip + 引用完整性檢查的 SOP；做為所有「建立 / 修改 UCL_Asset」型工作流的驗收門檻
last_updated: 2026-05-20
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🔍 Validate UCL_Asset 工作流程

> [!IMPORTANT]
> **這是任何「建立 / 修改 `UCL_Asset` JSON」型工作流的最後驗收步驟。** 透過 workflow / 手動 / 批次轉換寫出的 asset 之後，**必跑** `Cmd_ValidateAssetFormat` 確認 loader 真的能讀回。否則 silent data loss 會在 runtime 才被發現（例如 enum 拼錯 → 名稱 / 描述全變預設）。

## 0. TL;DR

```bash
# 寫完 asset 後，立刻驗證（路徑相對 git root）
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=<C# Type> --arg assetId=<ID> --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md

# 讀產出檔判定：
#   verdict: PASS              → ✓ 收工
#   verdict: FormattingOnly    → cp .fixed.json 蓋原檔即可
#   verdict: SchemaDiff        → 對照 diff 修原檔（看 Captured Errors 找根因）
#   reference_check: Missing   → 列出的 missing sub-asset 也要修（建立 / 改引用）
```

---

## 1. 適用範圍

> **本工作流不限於特定上層專案 — 任何使用 `UCL_Asset<T>` 的資料類型都適用**。

| 上層專案範例 | 對應 asset 類型 |
|---|---|
| Emblem of Valor (RCG) | `RCG_ItemData` / `RCG_CardData` / `RCG_BattleSet` / `RCG_UnitData` / `RCG_StoryData` / `RCG_EquipmentData` / `RCG_CustomStatusData` / `RCG_MonsterLevelActionData` ⋯ |
| 任何 UCL_Game 專案 | 所有繼承 `UCL_Asset<T>` 的類別 |
| UCL_Core 自身 | `UCL_LocalizeAsset` / `UCL_BundleAsset` / `UCL_ModulePlaylist` ⋯ |

只要 type 繼承 `UCL_Asset<T>` 並實作 `SerializeToJson` / `DeserializeFromJson`，即可用此 Cmd 驗證。

## 2. 何時觸發

| 情境 | 必跑？ | 建議 args |
|---|:-:|---|
| 透過 workflow 新增 asset | ✅ | `checkRefs=1`（**必加**，見下方警告）|
| 用 `add_entries.py` / Editor 改了 LocalizeData 後修了 asset | ✅ | `checkRefs=1`（同上）|
| 手動或腳本批次轉換 asset（schema migration）| ✅ | `checkRefs=2` |
| 從別處 `cp` 進來的 asset | ✅ | `checkRefs=1` |
| 只改了 description / note 等純 metadata | ⚠ 建議 | `checkRefs=0` |
| 只改了 Tag / SkillTag / SpriteAssetEntry / 任何 entry ID | ✅ | `checkRefs=1`（**強制**）|

> [!CAUTION]
> **Schema PASS 不等於 runtime 沒事**。Tag / SpriteAssetEntry / SkillTag 等 `UCLI_AssetEntry` 的 ID 拼錯時，loader 只會在 lazy 取用（如 Editor preview 的 `get_Tag()`）才爆例外，而**不會**在 schema diff 中現形。
>
> 真實案例：`ManaCore_Shard` 的 `Tags: ["Buff", "Mana"]` schema 完全 PASS（Tags 字串陣列形式正確），但 `Mana` 不是合法 RCG_ItemTag ID（合法的只有 11 個：Attack / Book / Broken / Buff / Debuff / Defective / Event / Heal / Lead / Skill / Treasure）。打開 Editor 預覽該 item 就會噴：
> ```
> AssetConfig.GetJsonData AssetType:RCG_ItemTag ID:Mana, !File.Exists
> ```
> **`checkRefs=1` 會在 BFS 階段嘗試 load 該 Tag → 立刻發現 missing**。沒跑 checkRefs 等於把這類問題藏到 runtime。

> [!NOTE]
> Cmd 對 Editor 開銷不大（單一 asset 約 1 秒）。除非批次驗 100+ 個，否則沒理由跳過。

## 3. Cmd 觸發範本

詳細 args 見 [Cmd_ValidateAssetFormat API](../API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md)。常用組合：

### 3.1 最小驗證（schema only）

```bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=<Type> --arg assetId=<ID> \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
```

### 3.2 含直接引用檢查（推薦預設）

```bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=<Type> --arg assetId=<ID> --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
```

### 3.3 跨資產 deep validation（複雜引用鏈）

```bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=<Type> --arg assetId=<ID> --arg checkRefs=2 --arg verbose=true \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
```

### 3.4 全鏈引用診斷（排查既有 asset）— Cmd_ResolveAssetReferences

`ValidateAssetFormat --checkRefs` 是**改完一個 asset 的驗收 gate**（per-asset，BFS 檢查它引用的 sub-asset 在不在）。
但若你要**排查一個既有 asset「哪些引用壞了」/ 看完整依賴樹**，用 `Cmd_ResolveAssetReferences` 更直觀 — 它遞迴走所有 `UCLI_AssetEntry`，每筆標 `Exists ✅/❌` + 印 `[MISSING]`，一次看完整條鏈：

```bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ResolveAssetReferences \
    --arg assetType=<Type> --arg assetIds=<ID>[,<ID2>] --arg maxDepth=2 --arg format=md
# 產出 CardGame/AgentCommands/asset_refs_<Type>_<ts>.md
# 看 "Found On Disk: N / Total" + Flat Path List 的 ❌ + Reference Tree 的 [MISSING]
```

| 工具 | 用途 | 何時用 |
|---|---|---|
| `ValidateAssetFormat --checkRefs=1` | 單一 asset 驗收（format + 直接引用）| **改完 / 新建 asset 後**（gate）|
| `ResolveAssetReferences` | 整條依賴鏈引用健康一覽（哪些 ❌）| **排查既有 asset / 找 dangling 根因** |
| `FindAssetUsages`（反向）| 誰引用了這個 asset | 判斷 asset 是否為 **orphan**（沒人用 = 可能未接好的 WIP）|

> [!IMPORTANT]
> **辨別「全域缺失」vs「此 asset 專屬缺失」** — 跑完別急著修每個 ❌。先拿一個**同類已知正常的 asset** 當對照組跑一次：若某個 missing ref（如 `RCG_PoolingData/ItemDisplay`）在**所有**同類 asset 都缺，那是全域預設 / 系統性容忍項，**不是你這個 asset 的 bug**，別去建假資料。只有「對照組有、你這個沒有」的 missing 才是真要修的專屬缺口。
>
> 真實案例（2026-05-20 ridge-001）：`RCG_CharacterData/AncientTreeSpirit` 跑出 3 個 ❌，但拿完整角色 `Lucia` 對照後發現 `ItemDisplay` 是**全角色共缺**（Lucia 也缺、遊戲照跑）→ 只有 `RCG_SkillTag/Skill_Ancient` + `RCG_UnitData/AncientTreeSpirit` 是 ATS 專屬缺口（該角色為未完成 WIP，相依資產沒建）。修這兩個即可。

## 4. Verdict 處理流程圖

```
讀 report frontmatter
  │
  ├─ verdict: PASS             → ✅ 收工
  │
  ├─ verdict: FormattingOnly   → cp <fixed_path> <original_path>
  │                              （無語意影響，純整理格式）
  │
  ├─ verdict: SchemaDiff       → 看 ## Canonical Diff 區塊
  │   │                          看 ## Captured Errors 區塊找根因
  │   │
  │   ├─ removed: N > 0        → 修欄位名（typo / 過時 schema）
  │   ├─ added: N > 0          → 補上漏寫的欄位（即使是預設值，明示比較好追）
  │   └─ value 改變             → 看 enum 是否拼錯 / 型別是否對
  │
  └─ verdict: Error            → 看頭一段錯誤訊息 → 改 args 或修 asset 路徑

並行檢查 reference_check：
  ├─ Skipped                   → 沒查（args 沒傳 checkRefs）
  ├─ OK                        → 引用全在
  └─ Missing                   → 看 ## Reference Integrity 表格
                                  逐筆處理：建立 missing asset，或修原檔引用 ID
```

## 5. 常見問題模式

| 報告徵兆 | 真實原因 | 修法 |
|---|---|---|
| `removed: N` + Captured 有 `Requested value 'X' was not found` | enum value 已被 deprecate（loader 不認識舊值）| 改成現行有效 enum value；對應的關聯欄位也要改名（例如 `LocalizeType: "Raw"` + `LocalizeRaw` 整組改為 `"Key"` + `LocalizeKey`）|
| `added: N`（多出空字串 / 預設值欄位）| 必填欄位漏寫，loader 補了預設 | 在原檔顯式加上欄位（即便是預設值，明示利於日後追蹤）|
| `reference_check: Missing` + Captured 有 `!File.Exists, aPath:.../X.json` | 引用了不存在的 sub-asset；常見：同名 ID 在不同 Type 內不存在（如 Tag 與 Item 同名）| 建立 sub-asset，或修原檔內的引用 ID |
| **`reference_check: Missing` 對 RCG_ItemTag / RCG_SkillTag** + schema PASS | JSON 內 `"Tags": ["Buff", "Mana"]` 看似純字串陣列，實則每個元素是 `RCG_TagAssetEntry` 對應一個 Tag asset；Tag ID 拼錯 / 不存在時 schema 看不出，但 Editor preview 會在 `get_Tag()` lazy load 時噴例外 | 用 `ls CardGame/Assets/.BuiltinModules/.../UCL_Assets/RCG_ItemTag/` 查合法 Tag ID；修 Tags / SkillTags 拼字或刪除無效項 |
| `verdict: Error` + `Asset file not found` | assetId 拼錯，或目標 module 沒被當前 module load 列表載入 | 檢查 ID 拼寫；確認 module 設定 |
| 多個 captured errors 但 schema PASS | Editor preview 階段觸發例外（loader 容錯了），但 asset 本身欄位未被影響 | 通常是 reference 問題，跑 `checkRefs=1` 確認 |
| `reference_check: Missing` 但**同類所有 asset 都缺同一筆** | 全域預設 / 系統性容忍項（如所有角色都缺 `RCG_PoolingData/ItemDisplay`）| **不是此 asset 的 bug**，別建假資料；拿同類正常 asset 對照確認（見 §3.4）|
| 新建的角色 / 單位有多筆 ❌（如缺 UnitData / SkillTag）| 該 asset 為未完成 WIP，相依資產還沒建（用 `FindAssetUsages` 確認它根本沒被引用 = orphan）| 補齊相依資產（用既有美術做 reference-clean placeholder，標記交原作者細修），或先別接進遊戲 |

## 6. Localize 修法案例（最常見）

當報告顯示 Name / Description 從某個 deprecated `LocalizeType` 變成現行值（例如 `Raw` → `Key`）：

```
1. 在 <project root>/Tools/Localize/ 建立 entries.json：
   cp Tools/Localize/example_entries.json Tools/Localize/<your_asset>_entries.json
   修改 entries 區塊：
   {
     "<AssetID>":      { "en": "...", "zh-Hant": "...", ... },
     "<AssetID>_Des":  { "en": "...", "zh-Hant": "...", ... }
   }

2. 加進 LocalizeData：
   python Tools/Localize/add_entries.py Tools/Localize/<your_asset>_entries.json

3. 修原 asset JSON：
   "Name":        { "LocalizeType": "Key", "LocalizeKey": "<AssetID>" },
   "Description": { "LocalizeType": "Key", "LocalizeKey": "<AssetID>_Des" }

4. 重跑 ValidateAssetFormat 確認 PASS
```

> 上層專案的 Localize helper 路徑可能不同（如 RCG 用 `<project root>/Tools/Localize/`），但流程一致。

## 7. 與「建立 asset 工作流」的整合

任何上層專案的 `Create_*Data_Workflow.md` 都應該以本工作流作為**最後驗收步驟**。範本：

```markdown
## 驗收 (Validate Asset Format)

寫完 JSON 後 **必跑**：

\`\`\`bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=<Type> --arg assetId=<ID> --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
\`\`\`

verdict 必須 = `PASS`（或 `FormattingOnly` + 套用 `.fixed.json`）才算完成。
其他 verdict / `reference_check: Missing` → 依 [Validate_UCL_Asset_Workflow](../../../UCL_Core/Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md) §4 流程修。
```

> 上層專案實際使用範例（RCG / Emblem of Valor）：
> `Create_RCG_ItemData_Workflow.md` / `Create_RCG_BattleSet_Workflow.md` / `Create_RCG_StoryData_Workflow.md` 等都以此為驗收門檻。

## 8. CI / 批次驗證

未來可加一個 `Cmd_ValidateAssetsBatch`（讀一個 asset list 檔，逐個跑）支援回歸測試。目前僅支援單一 asset，批次需 agent 端自己迴圈。

## 9. 已知限制

| 限制 | 說明 / Workaround |
|---|---|
| 預設空字串會被當 SchemaDiff（如 `Note: ""`） | 慣例上可接受（依該 asset 類型的多數慣例決定）；或在原檔顯式補上 |
| 不分析「enum 仍可解析但語意過時」 | 此 Cmd 只看 enum 解析是否成功；過時但可解析的值不會被報告 — 建議搭配 Catalog 工具人工 review |
| `checkRefs ≥ 2` 會載入 sub-asset | 觸發 sub-asset 自己的 loader → 可能噴更多 captured errors（會增加報告長度，但仍正確）|

## 10. 關聯文件

- [Cmd_ValidateAssetFormat API](../API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) — 詳細 args / 報告結構（per-asset 驗收 gate）
- [Cmd_ResolveAssetReferences API](../API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) — 全鏈引用診斷（排查既有 asset 的 dangling ref，見 §3.4）
- [Create_UCL_Asset_Workflow](Create_UCL_Asset_Workflow.md) — 建立 UCL_Asset 子類，**建完一律走本工作流驗收**
- [AgentCommands 系統架構](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Cmd 觸發底層流程（lock-file 機制）
