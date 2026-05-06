---
date: 2026-05-05
index: 00002
title: 新增 Cmd_ValidateAssetFormat — UCL_Asset 格式 & 引用完整性檢查
tags: [feature, docs]
---

# 新增 Cmd_ValidateAssetFormat — UCL_Asset 格式 & 引用完整性檢查

## What

新增一個 Agent Command **`ValidateAssetFormat`**，對任意 `UCL_Asset<T>` 做兩維度驗證：

| 維度 | 機制 | Verdict |
|---|---|---|
| **Schema 完整性** | 讀原檔 → loader deserialize → re-serialize → canonical diff | `PASS` / `FormattingOnly` / `SchemaDiff` / `Error` |
| **引用完整性**（選擇性 `checkRefs=N`）| BFS 反射走訪所有 `UCLI_AssetEntry`，檢查 sub-asset 是否真的存在於 module | `Skipped` / `OK` / `Missing` |

> **這是給 AI agent 自我驗證 workflow 產出的核心工具** — 寫完一個 RCG_*Data JSON 後跑這個 Cmd，loader 看不懂的東西 / 引用拼錯的 ID 全會暴露在報告裡。

新增元件：

| 檔案 | 角色 |
|---|---|
| `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ValidateAssetFormat.cs` | Cmd 主體（約 1080 行，含 walker / 簡易 unified diff / canonical 化 / 報告渲染）|
| `Docs~/{4 langs}/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md` | API 文件（zh-Hant 詳細版 + 3 langs 精簡 stub）|
| `Docs~/{4 langs}/Workflows/Validate_UCL_Asset_Workflow.md` | 工作流 SOP（zh-Hant 詳細版 + 3 langs 精簡 stub）|

## Why

`UCL_Asset` loader 對「不認識的欄位」與「拼錯的引用 ID」是**容錯**的：
- 不認識的欄位 → 靜默丟棄
- 拼錯的引用 ID → lazy 取用（如 `get_Tag()`）才爆例外

這對 runtime 來說是「容錯設計」，但對寫資料的人（特別是 AI agent）是「silent data loss」陷阱。寫了個 Item 描述「Grants 2 layers of Mana Boost」，內部寫 `Status: "ManaBoost"`，但沒人發現 `RCG_CustomStatusData/ManaBoost.json` 不存在 → 道具按下去什麼也不會發生，Editor preview 也只會偶發噴 console 例外，沒人去追。

`Cmd_ValidateAssetFormat` 就是把這層 silent data loss **強制顯性化**：

```
靜默問題                     報告顯示
──────                       ──────
loader 不認識的欄位      →   removed line in canonical diff
loader 補了預設           →   added line in canonical diff
enum value 解析失敗       →   captured Console error
引用拼錯 / sub-asset 不在 →   reference_check: Missing
```

### 起源案例（已寫進文件）

實際上 `RCG_ItemData/ManaCore_Shard` 就被這個 Cmd 抓到 4 個獨立問題：

1. `LocalizeType: "Raw"` 已 deprecated（loader 不認識）→ Name / Description 變預設空字串 → **完全失去名稱與敘述**
2. `Tags: ["Buff", "Mana"]` — `Mana` 不是合法 RCG_ItemTag → schema PASS 但 Editor preview 噴例外
3. `Status: "ManaBoost"` — RCG_CustomStatusData 沒這個 → 用了道具什麼都不會發生
4. Walker false positive：未啟用的 `[Conditional]` 欄位被誤報 missing → 加上 `IsShow` 過濾與 serializer 一致

## How to use

### 基本驗證（schema only）

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=RCG_ItemData --arg assetId=ManaCore_Shard \
    --output-file CardGame/AgentCommands/asset_format_check_RCG_ItemData_ManaCore_Shard.md
```

### 推薦預設：含直接引用檢查

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=RCG_ItemData --arg assetId=ManaCore_Shard --arg checkRefs=1 \
    --output-file CardGame/AgentCommands/asset_format_check_RCG_ItemData_ManaCore_Shard.md
```

### 跨層 deep validation（Story / BattleSet 整套）

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat \
    --arg assetType=RCG_StoryData --arg assetId=AbandonedTemple \
    --arg checkRefs=2 --arg verbose=true \
    --output-file CardGame/AgentCommands/asset_format_check_RCG_StoryData_AbandonedTemple.md
```

> 完整 args 表格 + verdict 處理 + 報告結構 → [Cmd_ValidateAssetFormat API](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md)
>
> 何時觸發 + 整合到上層專案的 SOP → [Validate_UCL_Asset_Workflow](../Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md)

## Args 一覽

```
assetType=<C# Type>              # 必填
assetId=<ID>                     # 必填
outputPath=<.md>                 # 預設 AgentCommands/asset_format_check_<type>_<id>.md
fixedPath=<.fixed.json>          # 預設 sibling，verdict ≠ PASS 時寫
verbose=true|false               # 預設 false
checkRefs=N                      # 預設 0；1 = 直接引用，2+ = 跳到孫子
ignoreEmptyIds=true|false        # 預設 true（空 ID 視為「故意不引用」）
```

## 重要設計決策

### 1. 攔截 `Application.logMessageReceived`

Loader 在 deserialize 過程會把 enum 解析失敗等錯誤丟到 Unity Console（不 throw exception），如果只看 return value 看不到根因。Cmd 在 GetAsset / SerializeToJson 期間掛 callback，把所有 `LogType.Error` / `LogType.Exception` 收進 `Captured Errors` 區段，agent 一次拿到完整診斷線索。

### 2. Reference walker 尊重 `[UCL.Core.PA.Conditional]`

`RCG_StatusSetting` 內 `[Conditional(StatusModifiedType.StatusDropPool)] m_StatusDropPool` 等欄位，當條件不成立時 serializer 不寫 JSON。但反序列化會用建構子預設值補（如 `m_ID = "Default"`）→ walker 若不過濾就會誤把這個未啟用的預設值報成 missing。

修法：在反射走欄位時呼叫 `aConditional.IsShow(obj)`，與 `JsonConvert.SaveFieldsToJson` 同邏輯。

### 3. 三維 verdict 而非單一

不把「reference missing」併進 schema verdict — 兩件事獨立：

```
schema verdict: PASS | FormattingOnly | SchemaDiff | Error
reference_check: Skipped | OK | Missing
```

可能 schema PASS 但 reference Missing（前述 ManaCore_Shard 修一輪後的真實狀態）→ agent 一眼看出該修哪邊。

### 4. 不修原檔；不靠 cache

- `useCache=false` 強制 loader 重新讀檔（避免 Editor 內已被修改的 in-memory 版本污染驗證）
- `.fixed.json` 永遠寫到不同路徑，**不**自動覆蓋原檔（即使 FormattingOnly）
- agent 拿到 `.fixed.json` 後若決定採用，自己用 `cp` 蓋

## Breaking changes

無。**新增** Cmd 不影響既有 Cmd / Watcher / Runner 行為。

## Migration

- **既有上層專案**（RCG / Emblem of Valor）：在每個 `Create_*Data_Workflow.md` 的最後加一個「驗收」段，引用 [Validate_UCL_Asset_Workflow](../Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md)
- **未來新 asset 類型**：只要繼承 `UCL_Asset<T>` 並實作 `SerializeToJson` / `DeserializeFromJson`（基底已實作），即可用此 Cmd 驗證，無需改 Cmd

## Caveats

| 限制 | 說明 |
|---|---|
| 預設空字串會被當 SchemaDiff（如 `Note: ""`）| 因為原檔沒寫 / roundtrip 補了空字串 → 後續可能加 `BenignDefault` 子分類 |
| 不分析「enum 仍可解析但語意過時」 | 此 Cmd 只看 enum 解析是否成功 — 過時但可解析的值不會被報告，需搭配 Catalog 工具人工 review |
| `checkRefs ≥ 2` 會載入 sub-asset | 觸發 sub-asset 自己的 loader → 可能噴更多 captured errors，報告會變長但仍正確 |

## 相關文件

- [Cmd_ValidateAssetFormat API](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) — 完整 args / 報告結構 / 診斷流程
- [Validate_UCL_Asset_Workflow](../Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md) — 何時跑、怎麼處理 verdict、Localize 修法案例
- [00001_2026-05-05](00001_2026-05-05.md) — 上一筆：lock-file 自動觸發機制（本 Cmd 透過該機制觸發）
