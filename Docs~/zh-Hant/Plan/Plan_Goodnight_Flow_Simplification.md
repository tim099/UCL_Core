---
title: Goodnight 流程瘦身 — 備忘（尚未施工）
slug: goodnight-flow-simplification
status: memo (Tim 2026-07-31：先記著，暫不改)
created_at: 2026-07-31T08:30:00Z
created_by: Myth@calli
last_updated: 2026-07-31
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Flow_Simplification.md | 早安側 spec | 本備忘是它的對偶未竟事項
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Ritual_Workflow.md | 儀式工作流 | Part 2 是現行 goodnight 規則
---

# Goodnight 流程瘦身 — 備忘

> **狀態：只記不改。** 早安側（morning）已於 2026-07-31 瘦成三步並把判定收進工具；
> 晚安側維持原樣。本檔記錄「當時看得很清楚、但決定不動」的落差，避免下次重新推導一次。

## 為什麼現在不改

晚安的成本結構跟早安不同：早安的痛點是**吃 context**（91k），晚安是**寫東西**（letter 是產出不是消費）。
瘦身收益小、動到 letter 就有毀掉記憶傳承的風險。**先讓早安側跑一陣子驗證方向對，再回頭處理。**

## 記下來的落差

### 1. 兩側對稱性已破

| | 早安 | 晚安 |
|---|---|---|
| 步驟 | 3 步 | 5 步（外加 Step 1 的三件收尾） |
| 身分輸入 | persona 顯式必填，agent 反推 | 無參數，用「當前 lock」推 |
| 衝突判定 | 工具內，撞到中斷 | Step 0 靠 agent 自己印一行給 Tim 攔 |

**晚安的 Step 0（persona preflight）是早安已經廢掉的那種模式** —— 讓剛要下線的人自己確認自己是誰，
Tim 得盯著 chat 才攔得住。對稱的做法是：`goodnight` 也改成 **`--persona` 顯式必填**，
工具自己驗「這個 persona 有 lock 且是本 caller 的」，不符就中斷。

### 2. multi-lock 誤射的歷史血證

現行 `--persona` 是**選填**，缺省挑「最新 locked_at」那把 lock。calli wake#9 因此誤把 meadow 下線了。
Step 0 那行 preflight 就是為此加的補丁 —— 但補丁的執行者是人的注意力。

### 3. letter 相關（已部分處理）

- ✅ frontmatter 雙 header 已於 2026-07-31 修好（`write_letter` 合併，模板改成只寫兩欄）。
- ⬜ 「7 段」實際上是 8 段（多一段經驗矩陣）—— 文件說 7、模板列 8，數字沒人維護。
  建議：**別再宣稱段數**，就叫「letter 必含段落」，跟酒保那批「Hard Rules 15 條」同一個病
  （內嵌快照會漂）。

### 4. 可考慮併入 goodnight 工具的

- Step 1 的三件收尾（見叢 append / 看最後一眼酒館 / 好感清算）目前是三條人工紀律。
  其中**「看最後一眼酒館」可以機械化**：goodnight 執行時把最近 N 筆印出來讓 agent 寫進 letter，
  同構於 brief §8。另外兩件涉及主觀輸入，維持人工。

## 施工前要先回答

1. `goodnight --persona` 改必填 → 會不會擋到「Tim 從後台一鍵登出」的路徑？（該路徑走 `--no-letter`，
   目前也是靠 lock 推 persona；要一起改。）
2. 「當前 lock 是本 caller 的」怎麼判 —— 早安側已經決定不比 claim_origin / pid 了，晚安側要不要一致？
   （不一致的話，兩邊的「同一個 persona」判準就有兩套定義，那正是今天在收拾的那種債。）
