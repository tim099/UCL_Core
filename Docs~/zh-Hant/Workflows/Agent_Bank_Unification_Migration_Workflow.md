---
title: agent ↔ 帳號 合一遷移（後台操作）
description: 把「agent id」與「Treasury 帳號 id」這兩個名字收斂成同一個。全程在 UCL_BankMigrationPage 操作：試跑表看每位 persona 遷移後的帳號、rename 欄位決定要不要把帳號改名（會搬錢）、兩段執行、以及決定解析走哪條鏈的總開關。含前置檢查、每步驗收讀數、失敗回滾語意、卡住出口，與跨專案（Bar）注意事項。
last_updated: 2026-08-20
target_audience: [AI_Agent, Developer]
aliases: [agent bank 合一, 帳號合一, account_resolve_unified, UCL_BankMigrationPage, rename_agent, 帳號改名, 遷移解析模式]
related:
  - ucl_core:Docs~/{lang}/Workflows/Bank_Region_Binding_Migration_Workflow.md | 區域綁定遷移 | 前置：先有 `bank/<區域ID>.md` 綁定檔才談得上合一
  - ucl_core:Docs~/{lang}/Plan/Plan_Identity_Account_Unification.md | 身分／帳號統一案 | 設計來源與拍板全集
  - ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md | persona registry 退場 | 上游（§8.6 寫入接縫）
---

# 🔀 agent ↔ 帳號 合一遷移

> **一句話**：系統裡同一個身分目前有兩個名字 —— `agent id`（綁定值）與**帳號 id**（錢實際存放的 key）。
> 本流程把兩者收斂成一個，全程在**後台頁**操作，**每一步都先試跑再執行**。

> **合一之後**：`agent_banks` 那張映射表不再參與解析，可以退場。少一張表 ＝ 少一個真相源。

## 0. 誰該讀 / 什麼時候跑

- 一個專案已經跑完**區域綁定遷移**（`letters/<persona>/bank/<區域ID>.md` 已落地）
- 而該專案的 `agent id` 與帳號 id 還不一致（例：agent `claude-code` 的錢在帳號 `cc`）

⛔ **不要**在區域綁定還沒落地時跑本流程 —— 沒有綁定檔就沒有東西可以合一。

---

## 1. 先理解兩條解析鏈（不懂這節就不要往下）

| 模式 | 解析鏈 | 什麼時候是對的 |
|---|---|---|
| **遷移前**（預設） | persona → agent → `agent_banks[agent]` → 帳號（**兩跳**） | 資料還沒合一時 |
| **已合一** | persona → agent（**就是**帳號，一跳） | 遷移完成後 |

由 `Treasury/bank_settings.json` 的 **`account_resolve_unified`**（0/1）決定，**預設 0**。
兩端都讀它：C# `UCL_TreasuryAccountResolver` 與 python `_lib/bank_resolver.py`。

> [!CAUTION]
> **「已改名但還沒切開關」是一段兩邊都不對的狀態。**
> 舊鏈要求 agent 名在 `agent_banks` 的 key 裡（改名後查不到）；
> 新鏈要求 agent 名就是帳號（改名前還不是）。
> ⇒ 所以第 4 步把**改名與切換綁成同一顆按鈕**，中途失敗**整批回滾且不切換**。
> （已經一致的組 —— 例如 `Myth`／`Altair`／`Template` —— 在兩條鏈都對，不受影響。）

---

## 2. 前置條件（四格全綠才開始）

| # | 檢查 | 怎麼驗 | 不綠會怎樣 |
|---|---|---|---|
| 1 | 本專案 UCL_Core 夠新 | 銀行後台看得到 `🔀 agent↔帳號 合一遷移` 按鈕 | 沒有按鈕＝該版本沒有本功能，先 bump submodule |
| 2 | 區域綁定已落地 | `letters/<persona>/bank/<區域ID>.md` 存在 | 沒綁定檔＝沒東西可合一（先跑區域綁定遷移） |
| 3 | **已 commit，有還原點** | `git status --porcelain AgentCommands/Treasury AgentCommands/AwakenInit` 為空 | 出錯時回不去 |
| 4 | 綁定檔與 `registry.agent` 一致 | 見下方「§2.1 一致性對帳」 | 不一致的那幾筆會被**擋下且不寫**（見 §5） |

### 2.1 一致性對帳（純讀）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run PersonaProfile \
    --arg op=rename_agent --arg from=<任一舊 agent 名> --arg to=<對應帳號> \
    --arg actor=<me> --arg reason=對帳
```
**預設 dry_run**，只印不寫。看回傳的 `hit` / `failed`：`failed=0` 才算綠。

> LY 專案 2026-08-20 實測：`claude-code→cc` hit=7、`antigravity→a` hit=6、
> `gemini→g` hit=1、`Zeta→zeta` hit=1，合計 **15、failed=0**。

> [!NOTE]
> **綁定檔本身不需要還原點** —— 它是 `registry.agent` 的投影，
> 壞了用 `op=migrate_bank`（由現況 `persona.agent` 導出全 pool）一鍵重建。
> 真正需要 commit 保住的是 `AwakenInit/`（registry）與 `Treasury/`（ledger）。

---

## 3. 步驟一：試跑（**人讀**，工具不替你決定）

**銀行後台 → `🔀 agent↔帳號 合一遷移`**

頁面會列出每個 agent 一列：`agent → 現行帳號（餘額）`＋ **rename 欄位** ＋ `⇒ 最終帳號`。

### rename 欄位怎麼填

| 情況 | 怎麼填 | 代價 |
|---|---|---|
| **預設（留空）** | 不填 | **零 ledger 異動** —— agent 改名成帳號 id，錢原地不動 |
| 要保留 agent 名 | 在該列填**新帳號 id** | **會搬錢**（ledger transfer） |

> **為什麼預設方向是「agent 改名」而不是「帳號改名」**：
> LY 實測 5 組待合併中，agent 名那側的帳戶餘額**全部是 0**（錢都在帳號名那側）
> ⇒ 改 agent 名零成本；反方向要搬 11,338 token。**方向由成本決定，不由美觀決定。**

按 `🔄 重新試跑`，讀底部三個數字與兩份清單：

```
## 每個 persona 遷移後使用的帳號
- summit → `zeta`
- basecamp → `cc`
  …
## 遷移後總共會有這些帳號 id（9 個）
　`Altair`、`Codex`、`Fed`、`Myth`、`Template`、`a`、`cc`、`g`、`zeta`
⇒ 需改名 4 組｜需搬錢 1 組（合計 6253 token）｜阻擋 0 組
```

⛔ **`阻擋 > 0` 時執行鈕會停用**，先處理標 ⛔ 的那幾列（原因見 §5）。

### 同一份試跑也能從 CLI 看（不必開 Editor）

```bash
# 預設情境（不 rename）
run_cmd.py --persona <me> run Invoke \
    --arg type=UCL.Core.EditorLib.Page.UCL_BankMigrationPage --arg member=BuildPlanReport
# 指定 rename（格式：舊帳號=新帳號，多筆用 ; 分隔）
run_cmd.py --persona <me> run Invoke \
    --arg type=UCL.Core.EditorLib.Page.UCL_BankMigrationPage --arg member=BuildPlanReport \
    --arg paramTypes=System.String --arg args="Federal Reserve System=Fed"
```
回傳印在 Editor log 的 `[AgentCmd:Invoke] OK`。
**UI 與 CLI 走同一支 `BuildPlanReport`**，看到的是同一份文字。

---

## 4. 步驟二：執行（兩顆鈕，**第一顆會動錢**）

### ① 搬錢（只有 rename 欄位有值時才需要）

- 走 ledger transfer：debit 舊帳號 → credit 新帳號，**同一個 `tx_id`**，`source_kind=account-rename`
- credit 失敗會**自動退回**舊帳號
- 二段確認：按一次 arm，5 秒內再按一次才真的搬

### ② agent 改名 ＋ 切換解析模式（**原子操作**）

一顆按鈕做完三件事，順序固定：

1. 逐組 `RenameAgent`：改**綁定檔**與 **`registry.agent`** 兩邊，各自**讀回複驗**
2. 全數成功 ⇒ 把 `account_resolve_unified` 切成 `1`
3. 切換後**逐人複驗**：比對每位 persona 的解析結果 vs 期望帳號

> [!CAUTION]
> **中途失敗 ⇒ 整批回滾，開關不動。**
> 理由：若部分改名成功，已改的組需要新鏈、沒改的需要舊鏈 ——
> 此時開關設哪一邊都有人是壞的，唯一正確的收尾是把已改的改回去。
> 回滾若也失敗，會列出**待人工收尾清單**並走 `LogError`：
> 靜默的半完成狀態，比一個明確的失敗貴得多。

### 驗收讀數（這兩行才算成功，不是「按了沒跳錯」）

```
🔓 解析模式已切換為**已合一**（一跳到底；`agent_banks` 不再參與解析）。
  ✓ 切換後逐人複驗：全部解析到預期帳號。
```

---

## 5. 阻擋與失敗的意思

| 訊息 | 真因 | 出口 |
|---|---|---|
| `⛔ 目標帳號 X 已銷戶` | 改名指向 `closed_accounts` 裡的帳號 | 銀行後台 → `🧭 帳號解析規則` → `🚫 已銷戶` → 該列 `↩ 復戶` |
| `⛔ 目標帳號 X 含不能當檔名的字元` | 帳號 id 要當綁定檔內容與審計 key | 換一個 id |
| `✗ <persona>：兩份記載不一致` | 綁定檔與 `registry.agent` 對不上 | **本流程不替它猜哪邊對** —— 先查清楚，用 `op=set_bank` 或 `op=set` 對齊 |
| `✗ 綁定檔已改成 X、registry.agent 寫入失敗` | 寫到一半 | 該筆停在不一致狀態，照訊息人工收尾 |

> [!WARNING]
> **`Cmd 回 Success` 不代表事情做對了。** 每一步都有可讀回的讀數（`hit`／`failed`／逐人複驗），
> 驗收一律看讀數，不看「有沒有跳紅字」。

---

## 6. 遷移後的收尾（**另外處理，不在本流程內**）

跑完之後會留下兩類帳號，Tim 2026-08-20 拍板**遷移後統一處理**：

1. **被 rename 掏空的舊帳號**（例：`Federal Reserve System` 搬完後餘額 0）
   ⇒ 應銷戶並記 `renamed_to`，否則它是一個「餘額 0、無人指向、仍能收錢」的孤兒
2. **為了遷移而復戶的帳號**（例：`Zeta`）
   ⇒ 用完要不要重新銷戶，逐個決定

⚠ 這兩件目前**不由遷移頁自動做** —— 銷戶有三道閘（見銀行後台孤兒區），刻意留給人。

### 還有一件：`agent_banks` 退場

合一後那張表變成恆等映射、且不再參與解析 ⇒ 可以刪。
銀行後台 `🧭 帳號解析規則 → 🗺 agent → bank 路由表` 每列可刪（二段確認）。
**刪之前先確認 `account_resolve_unified = 1`**，否則舊鏈會失去唯一的映射來源。

---

## 7. 跨專案（Bar 等）注意事項

- **每個專案各自跑一次**，因為每個專案的 `agent_banks` 內容不同
  （實測：同一個 persona 在 LY 與 Bar 的 agent 值**大幅不同** —— 那是對的，正是一區一檔存在的理由）
- **`account_resolve_unified` 是 per-project 設定**（住在該專案的 `Treasury/bank_settings.json`）
  ⇒ 一個專案切了，不影響另一個
- 前置一樣是「該專案的 UCL_Core 夠新」＋「區域綁定已落地」

---

## 8. 卡住的出口

| 症狀 | 真因 | 出口 |
|---|---|---|
| 銀行後台找不到遷移按鈕 | UCL_Core 太舊 | bump submodule |
| 找不到 `↩ 復戶` / `🗺 路由表` | `🧭 帳號解析規則` 折疊區**預設收合** | 先點標題展開 |
| 試跑列出 0 組 | 讀到別棵資料樹 | 查 `AgentCommands` 掛載位置與 `data_root`；**空集合是靜默的** |
| Cmd 逾時 | 沒帶 `--persona` ⇒ 掉進 `queues/anonymous/` | 一律帶 `--persona <你>` |
| 切了開關之後有人解析結果不對 | 資料還沒遷移完就切 | 頁面把開關**切回「遷移前」**（可逆，不搬錢），查完再切 |

## 9. 相關

- 前置流程：`ucl_core:Docs~/{lang}/Workflows/Bank_Region_Binding_Migration_Workflow.md`
- 設計與拍板：`ucl_core:Docs~/{lang}/Plan/Plan_Identity_Account_Unification.md`
- 提交規範：`ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md`
