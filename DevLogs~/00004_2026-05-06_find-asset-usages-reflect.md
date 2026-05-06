---
date: 2026-05-06
index: 00004
title: 反向 Asset 引用查詢 + 反射診斷 — Cmd_FindAssetUsages / Cmd_DiagnoseAssetReflection 雙生工具
tags: [feature, docs]
---

# 反向 Asset 引用查詢 + 反射診斷 — Cmd_FindAssetUsages / Cmd_DiagnoseAssetReflection 雙生工具

## What

新增兩支 Agent Command + 一份建立新 Cmd 的多語系 SOP：

| 元件 | 角色 |
|---|---|
| `Cmd_FindAssetUsages` ⭐ | **反向**查詢 — 給目標 Asset，找出誰引用了它（含 dotted field path） |
| `Cmd_DiagnoseAssetReflection` 🔧 | UCL_Asset 反射管線**逐步診斷** — 找 NRE 元兇用 |
| `Workflows/Create_Cmd_Workflow.md`（4 語系）| 建立新 `Cmd_<Name>.cs` 子類的步驟化 SOP，含**文件放置自動判斷方案** |

新增 / 修改檔案：

```
UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/
  + Cmd_FindAssetUsages.cs
  + Cmd_DiagnoseAssetReflection.cs

Docs~/{en,ja,zh-Hans,zh-Hant}/
  + API/UCL_AgentCommand/Cmd_FindAssetUsages.md
  + Workflows/Create_Cmd_Workflow.md
  ✏ index.md（加入新條目）
```

## Why

### Cmd_FindAssetUsages — 順向版本不夠用

[`Cmd_ResolveAssetReferences`](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) 答的是「**這份 Asset 引用了誰**」（順向 / 依賴鏈）。但更常見的場景反過來：

- 「**誰引用了 `RCG_CustomStatusData/Stun`？**」（重構安全網 / 平衡分析）
- 「我要刪這份 asset 之前，先看影響範圍」
- 「某個 ItemEffect 被多少 ItemData 共用？」

順向工具回答不了這些 — 需要反向掃描。

`FindAssetUsages` 對所有（或指定）UCL_Asset 子型別實例做反射深掃，命中目標時記錄 **dotted field path**（如 `$.m_ItemEffects[0].m_CombineSetting.m_CombineSettings[1].m_Status`），讓使用者能精確定位到 JSON 內的位置而非只是「這份 asset 用了它」。

### Cmd_DiagnoseAssetReflection — 因為 FindAssetUsages 第一次全掃就 NRE

第一次跑 `FindAssetUsages` 全掃時拋 `NullReferenceException`，runner 只記 `e.Message`（無 stack trace），完全無從定位。

於是配套寫了診斷 Cmd：對每個 UCL_Asset 子類**逐步**測試（GetAllIDs → GetAsset → 淺反射 → 深反射），每一步個別 try/catch，把哪一步、哪個 type、哪個 id、**哪個欄位路徑**踩雷都記下來；額外 unwrap `TargetInvocationException`/`TypeInitializationException` 看真實 inner 例外。

跑下來找到兩組問題：

1. **`RCG_RuntimeData/CostDic`** — `m_DefaultValue` 的 `JsonData.<Data>k__BackingField` 中某些 entry 的 `value` 是 null，導致 `JsonData.GetEnumerator()` (UCL_JsonData.cs:1050) NRE
2. **`RCG_RuntimeStructData/Dic` 與 `/List`** — metadata 註冊存在但 `.json` 檔不存在（孤兒引用）

第 1 點正是原本 `FindAssetUsages` 全掃失敗的根因。`FindAssetUsages` 本身已加 per-asset try/catch + LogWarning，所以當前已能正常全掃；但 JsonData enumerator 對 null value 不防護是個 framework 層級的隱患，未來凡是反射走訪 JsonData 都可能再踩到。

### Create_Cmd_Workflow — 沒有 SOP 不行

`Cmd_*` 子類數量會持續成長。寫了一份 4 語系的 SOP 涵蓋：
- 命名 / 檔案位置決策樹（UCL_Core 通用層 vs 下游模組層）
- 標準範本（CommandType / ShortDescription / ArgsSchema / HelpURL 四欄 + ExecuteAsync 撰寫守則）
- 8 大常見地雷
- **§9 文件放置自動判斷方案**：用 `source_root` frontmatter + `Cmd_ValidateDocPlacement` 自動偵測「這份文件該住 UCL_Core/Docs~ 還是下游 docs/」，不再靠人類記住規則

## How to use

### 反向查詢被引用位置

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run FindAssetUsages \
    --arg targetType=RCG_CustomStatusData \
    --arg targetIds=Stun \
    --arg searchTypes=RCG_CardData,RCG_ItemData,RCG_EquipmentData \
    --arg format=md \
    --arg outputPath=AgentCommands/usages_Stun.md
```

> [!TIP]
> 大型專案請指定 `searchTypes` 縮小掃描範圍。預設全掃 6000+ assets 約需數秒。

輸出 markdown 表格示意：

| UsedBy Type | UsedBy ID | Field Path | JSON Path |
|---|---|---|---|
| `RCG_CardData` | `FullPowerImpact` | `$.m_Effects[0].m_CombineSetting.m_CombineSettings[2].m_Status` | `.../FullPowerImpact.json` |
| `RCG_ItemData` | `EmotionalDamage` | `$.m_ItemEffects[0].m_CombineSetting.m_CombineSettings[1].m_Status` | `.../EmotionalDamage.json` |

### 反射診斷（NRE 排查）

```bash
# 淺反射採樣（快速確認 type 層級沒問題）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run DiagnoseAssetReflection \
    --arg maxIdsPerType=5 \
    --arg outputPath=AgentCommands/diagnose_reflect.md

# 深反射全掃（模擬 FindAssetUsages 完整遞迴；會 unwrap inner exception）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run DiagnoseAssetReflection \
    --arg deep=true \
    --arg outputPath=AgentCommands/diagnose_reflect_deep.md
```

報告會分組列出 (type, id, **fieldPath**, ExType, Message)，把每個壞點都抓出來而非整批中止。

### 加新 Cmd

讀 [`Workflows/Create_Cmd_Workflow.md`](../Docs~/zh-Hant/Workflows/Create_Cmd_Workflow.md)，照範本貼一份 `Cmd_<Name>.cs`，等 Unity domain reload，registry 反射自動發現。**不要碰 Registry。**

## 設計決策

### 1. FindAssetUsages 不跨 asset 跳轉

`Cmd_ResolveAssetReferences` 的特色是 BFS 遞迴展開引用鏈。`FindAssetUsages` 反過來 — **命中目標即記錄欄位路徑然後停止下鑽**。原因：

- 我們只在乎「誰直接引用了我」，不需要「誰透過 N 跳間接用了我」
- 同一個 (target asset, using asset) 可能在多個欄位都被引用 → 記每一筆，不去重
- 主成本從「鏈長」變成「全資產數量 × 平均欄位深度」 → 適合用 `searchTypes` 收斂

### 2. Field path 是 dotted string，不是物件樹

`$.m_Effects[2].m_Settings[0].m_StatusEntry` 這種 dotted notation 對 agent 友善（直接 grep / 比對 JSON）、對人類也夠清楚。**用反射時的 C# field name** 而非 JSON serializer key — 兩者通常一致（UCL serializer 使用 fields），但若上層加自訂序列化器就可能差異。

### 3. DeepReflectProbe 每層 try/catch + 繼續

跟 `Cmd_FindAssetUsages.SearchForTargets` 不同：FindAssetUsages 為了效能在 entry 層之外較少 catch，靠**外層 per-asset wrapper** 接住整段；DiagnoseAssetReflection 則**每一步**都 catch，**不中止繼續走**，這樣同一個 asset 內多個壞點都能曝光。

### 4. Unwrap TargetInvocationException

`Method.Invoke()` 把目標方法的例外包成 `TargetInvocationException` — `e.Message` 會是 wrapper 訊息「Exception has been thrown by the target of an invocation」，看不到真實 NRE。最多剝 5 層（防 cycle）拿到 inner，這次正是靠 unwrap 看到 `RCG_RuntimeStructData/Dic, /List` 的真實錯誤是 `!File.Exists`。

### 5. Per-asset try/catch in FindAssetUsages（不是診斷工具，是生產品質）

即使有些 asset 因壞資料無法反射，整批掃描不該中止。Per-asset wrapper：壞蛋被吞掉並 LogWarning 到 Console，剩餘輸出有效。`RCG_RuntimeData/CostDic` 雖然踩 NRE，全掃仍正常產出 14 筆 Stun 引用點。

### 6. Create_Cmd_Workflow 住 UCL_Core 而非 docs/

這份 workflow 描述 UCL_Core 框架本身的擴充方法，不依賴下游型別 → 應住 submodule 內的 multi-lang Docs~。同時順手提出**自動判斷方案**（`source_root` frontmatter + `Cmd_ValidateDocPlacement`）讓未來新文件不必靠人類記規則。

## Breaking changes

無。新增 Cmd 與 workflow 都是 opt-in，既有 Cmd 行為不變。

## Caveats

| Caveat | 說明 |
|---|---|
| 預設全掃對大型專案約需數秒 | 用 `searchTypes` 限定加速 |
| Field path 反射名 ≠ JSON key（罕見） | 自訂序列化器才會差異；以 C# 類別定義為準 |
| 一個欄位指向多個 target ID 會出多筆 hits | 設計如此 — 每筆對應一個 target |
| 只解析 `UCLI_AssetEntry` | Localize Key / Sprite Key 是字串，不是 entry 型；本 Cmd 不抓 |
| `RCG_RuntimeData/CostDic` 的 JsonData NRE 已被吞掉 | 但 framework 層 `JsonData.GetEnumerator()` (UCL_JsonData.cs:1050) 對 null value 應加 guard |

## 相關文件

- [Cmd_FindAssetUsages API](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_FindAssetUsages.md) — 反向查詢工具
- [Cmd_ResolveAssetReferences API](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) — 順向版本
- [Create_Cmd_Workflow](../Docs~/zh-Hant/Workflows/Create_Cmd_Workflow.md) — 新增 Cmd SOP（含 §9 自動分類方案）
- [DevLog 00001](00001_2026-05-05.md) — Lock-file watcher 機制
- [DevLog 00002](00002_2026-05-05.md) — Cmd_ValidateAssetFormat 起源
- [DevLog 00003](00003_2026-05-05.md) — Hook 自動化驗證
