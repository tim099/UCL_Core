---
title: Cmd_ValidateAssetFormat API
description: 對單一 UCL_Asset 做「讀檔 → roundtrip 序列化 → 比對」的 schema 完整性檢查，並可選 BFS 走訪引用驗證 sub-asset 是否存在；給 AI agent 確認 workflow 寫出的 JSON 沒有靜默格式錯誤
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ValidateAssetFormat.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_ValidateAssetFormat

## 1. 概覽

對單一 `UCL_Asset` 做格式完整性檢查。流程：

```
原檔 (originalRaw, JSON)
  │
  ├──► canonicalize → originalCanonical
  │
  ▼
UCL_Asset.GetAsset(id, useCache=false) → C# object
  │ (loader 過程的 Console Error / Exception 全被攔截到 capturedLogs)
  ▼
asset.SerializeToJson() → roundtripJson
  │
  ├──► canonicalize → roundtripCanonical
  │
  ▼
比對 originalCanonical vs roundtripCanonical
  │
  └──► PASS / FormattingOnly / SchemaDiff / Error verdict
```

可選地（`checkRefs > 0`），會繼續 BFS 走訪 asset 內所有 `UCLI_AssetEntry`，檢查被引用的 sub-asset 是否真的存在於某個 module。

設計目的：
- **驗證 workflow 產出**：AI agent 跑完 `Create_RCG_*Data_Workflow.md` 後，立刻用此 Cmd 確認寫出的 JSON 真的能被 loader 正確讀回
- **抓 silent data loss**：例如 enum 值已被 deprecate（如 `LocalizeType: "Raw"`）、欄位名拼錯、必填欄位漏寫 → loader 會用預設值補但**不報錯**；此 Cmd 透過 roundtrip diff 把這類問題暴露
- **抓 broken references**：sub-asset 引用了不存在的 ID（例如 Tag 與 Item 同名 collision）→ Editor 內會偶發 `!File.Exists` 例外，此 Cmd 一份報告就能定位

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `assetType` | ✅ |  | C# Type 名稱，例 `RCG_ItemData` |
| `assetId` | ✅ |  | Asset ID（不含副檔名），例 `ManaCore_Shard` |
| `outputPath` | ❌ | `AgentCommands/asset_format_check_<type>_<id>.md` | 報告 markdown 路徑（**相對 Unity project root**）|
| `fixedPath` | ❌ | `<outputPath>.fixed.json`（自動推導） | roundtrip JSON 輸出路徑；verdict ≠ PASS 時才寫 |
| `verbose` | ❌ | `false` | `true` = 報告附完整原檔與 roundtrip 內容 |
| `checkRefs` | ❌ | `0` | BFS 深度：0 = 不查引用，1 = 只查直接引用，2+ = 跳到孫子 |
| `ignoreEmptyIds` | ❌ | `true` | 空 ID（`""`）視為「故意不引用」略過；設 `false` 會列入報告 |

## 3. Verdict 對照

主 verdict（**schema 維度**）：

| Verdict | 條件 | 輸出 .fixed.json | 建議行動 |
|---|---|:-:|---|
| `PASS` | originalRaw == roundtripRaw（字節相等） | ❌ | 不必動 |
| `FormattingOnly` | canonical 相等但 raw 不同（純空白 / 排序差異）| ✅ | 直接 cp `.fixed.json` 蓋原檔，無語意影響 |
| `SchemaDiff` | canonical 不同（欄位增 / 減 / 值改） | ✅ | 對照 diff 修原檔；信任 loader 才用 `.fixed.json` 蓋（會丟掉不認識的欄位） |
| `Error` | 路徑找不到 / 解析失敗 / 例外 | ❌ | 看 Captured Errors 段 + stack trace 排查 |

引用 verdict（**reference 維度**，獨立於 schema）：

| reference_check | 條件 |
|---|---|
| `Skipped` | `checkRefs=0`（預設）|
| `OK` | 全部走訪到的引用都存在 |
| `Missing` | 至少一筆引用在 disk 上找不到 |

兩個維度獨立 — 例如可能 schema PASS 但 reference Missing。

## 4. 報告結構

### 4.1 Frontmatter

```yaml
---
asset_type: RCG_ItemData
asset_id: ManaCore_Shard
verdict: PASS | FormattingOnly | SchemaDiff | Error
generated: 2026-05-05T10:27:13
original_path: Assets/.BuiltinModules/.../ManaCore_Shard.json
fixed_path: AgentCommands/asset_format_check_..._.fixed.json   # 非 PASS 才有
field_diff:                # SchemaDiff 才有
  removed: 4               # original 有，loader 不認識
  added: 5                 # loader 補了預設
captured_error_count: 0
reference_check: OK | Missing | Skipped
reference_depth: 1         # checkRefs > 0 才有
reference_walked: 3
reference_missing: 1
reference_skipped_empty: 0
---
```

### 4.2 主要區塊

1. **Verdict + 解釋**：一句話判斷與背景說明
2. **Files**：原檔路徑（read-only）+ `.fixed.json` 連結
3. **Recommended Action**：依 verdict 給具體建議（含 `cp` 指令）
4. **Reference Integrity**（`checkRefs > 0` 才有）：
   - 走訪統計（walked / missing / skipped）
   - 表格逐筆列 `(Status, Type, ID, Depth, Path/Note)`
5. **Captured Errors During Parse / Serialize**：load / serialize 期間 Unity Console 噴出的 Error / Exception 全被攔截到此（含完整 stack trace）
6. **Canonical Diff**（`SchemaDiff` 才有）：unified diff（左 = 原檔 canonical，右 = roundtrip canonical）
7. **Original / Roundtrip 完整內容**（`verbose=true` 才有）

## 5. queue.json 範例

最小驗證（schema only）：
```json
{
  "Id": "20260505-validate-manacore",
  "Type": "ValidateAssetFormat",
  "Mode": "OneShot",
  "Args": {
    "assetType": "RCG_ItemData",
    "assetId": "ManaCore_Shard"
  }
}
```

含 1 層引用檢查：
```json
{
  "Type": "ValidateAssetFormat",
  "Args": {
    "assetType": "RCG_ItemData",
    "assetId": "Mana",
    "checkRefs": "1"
  }
}
```

完整除錯模式（含原檔 + roundtrip 內容 + 2 層引用）：
```json
{
  "Type": "ValidateAssetFormat",
  "Args": {
    "assetType": "RCG_StoryData",
    "assetId": "AbandonedTemple",
    "checkRefs": "2",
    "verbose": "true",
    "ignoreEmptyIds": "false"
  }
}
```

## 6. Python 包裝器呼叫

```bash
# 基本驗證
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=RCG_ItemData --arg assetId=ManaCore_Shard \
    --output-file CardGame/AgentCommands/asset_format_check_RCG_ItemData_ManaCore_Shard.md

# 含引用檢查
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=RCG_ItemData --arg assetId=Mana --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_RCG_ItemData_Mana.md
```

## 7. 典型診斷流程

| 報告徵兆 | 可能原因 | 建議修法 |
|---|---|---|
| `removed > 0` | loader 不認識的欄位（typo / 過時 schema） | 對照 C# 類別欄位名修正 |
| `added > 0` | loader 補了預設（必填欄位漏寫） | 在原檔顯式加上欄位（即便用預設值）|
| 值差異（同 key 不同 value）| enum 解析失敗 / 型別轉換失敗 | 看 Captured Errors 找 `Requested value 'X' was not found` |
| `captured_error_count > 0` 但 schema PASS | loader 在 Editor preview 時觸發例外，但欄位本身正確 | 通常配合 `reference_check: Missing` 出現 — 是 sub-asset 找不到 |
| `reference_check: Missing` | 引用了不存在的 sub-asset ID | 建立缺漏 asset，或修原檔內的引用 ID（注意 Type 與 Item ID collision）|
| `verdict: PASS` 但**打開 Editor 預覽時噴 `RCG_TagAssetEntry !File.Exists`** | Tag 是 `UCLI_AssetEntry` lazy load — schema 沒寫錯所以 PASS，但 ID 不存在；不跑 `checkRefs` 看不到 | 重跑 `checkRefs=1`；reference_check 會變 Missing 並列出該 Tag 的 Type:ID |
| `verdict: Error` + 「Asset file not found」 | assetId 拼錯或模組未載入 | 檢查 ID；確認模組 module 屬於當前 load 列表 |

## 8. 與其他 Cmd 的關係

- 與 [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) 共用「反射走 UCLI_AssetEntry」邏輯（目前刻意不抽共用 helper，避免兩支 Cmd 互相依賴）
- 設計上**獨立**於 schema 工具（Catalog 等）— 此 Cmd 只看單一 asset 的 self-consistency + 直接引用，不做跨 asset 的關聯分析

## 9. 已知限制

| 限制 | 說明 |
|---|---|
| `Note: ""` 等預設空字串會被當 SchemaDiff | 因為原檔沒寫、roundtrip 補了空字串 → 視為「added 1」；後續可能改為 `BenignDefault` 子分類 |
| **Lazy-loaded entries（Tag / SpriteAssetEntry）schema 看不出** | `RCG_TagAssetEntry` / `UCL_SpriteAssetEntry` 等 `UCLI_AssetEntry` 的 ID 拼錯時，loader 不會在 DeserializeFromJson 階段觸發 load — 只有實際取用（如 Editor preview 的 `get_Tag()`）才爆。**只有 `checkRefs >= 1` 才會主動載入引用觸發檢查** |
| 不分析 enum 是否為「有效但 deprecate」 | 目前只看 enum 解析是否成功（看 Captured Errors）|
| BFS 深度 ≥ 2 時會載入 sub-asset → 可能觸發更多 loader 例外 | 全部會被收進 Captured Errors，但會增加報告長度 |
| Cycle 偵測只在 ref id 層級 | `Type:ID` 重複會跳過；不檢查同一筆 asset 內的物件 cycle（由 fieldVisited 防護）|

## 10. 關聯文件

- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md) — 系統整體架構
- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — 全 BFS 引用解析（不做格式驗證）
- 上層專案工作流（RCG / Emblem of Valor）：`../../Workflows/Validate_UCL_Asset_Workflow.md` — 何時觸發此 Cmd 的 SOP
