---
title: Cmd_Treasury — Agent Token 帳本（使用層：op 與欄位怎麼填）
description: 經濟體的單一財務入口 — 12 個 op 涵蓋餘額查詢 / 進出帳 / 守恆轉帳 / 請款單 / 轉帳單 / 每日結帳。本檔講「呼叫時要填什麼」與「哪些欄位其實沒人驗」。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Treasury/
namespace: UCL.Core.EditorLib.AgentCommands.Treasury
last_updated: 2026-08-14
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | 姊妹 Cmd | 身分層（agent vs persona）的正名拍板在那邊
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand.md | Cmd 系統總論 | handler base / queue / trigger
  - ucl_core:Docs~/{lang}/Workflows/Treasury_Account_Consolidation_Workflow.md | 帳號歸戶 SOP | 解析規則 / 人工標記遷移 / 幽靈帳號銷戶
---

# 💰 Cmd_Treasury — 使用層

> 一句話：**所有財務操作走單一 Cmd（`Type=Treasury`），第一個 arg `op` 派遣到子操作。**
> 錢的唯一寫入權在 C# server 端；python 只負責派遣。
> 結果一律寫進 `AgentCommands/ChatTavern/_last_op.md`，caller 讀那份。

---

## 1. 呼叫形狀

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Treasury \
  --arg op=<op 名> --arg <k>=<v> ...
```

- `_last_op.md` 的標題就是判決：`# ✅` 成功 / `# ❌ ... Rejected`（參數不合法）/ `# ❌ ... Failed`（執行期爆炸）。

> [!WARNING]
> **Treasury 沒有 client 端參數預檢** —— 每一個參數錯誤都要繞完整趟 Editor round-trip 才被擋。
>
> `commands_schema.json` 的預檢靠 handler 宣告 `UCL_CmdArgsSpec`，而**全 repo 只有 `Cmd_Tavern` 宣告了**。
> 實測（2026-08-04）：`commands_schema.json` 的 `commands.Treasury` 是 `{}` —— 空的。
>
> 本檔第一版曾照抄 `Cmd_Tavern.md` 寫「client 端 <0.01s 就擋」，**那是錯的**，查證後改掉。
> 動錢的那個 Cmd 恰好是沒有門口守衛的那個 —— 這條列在 §7 缺口，等拍板。
>
> 附帶提醒（對有 `ArgsSpec` 的 Cmd 才成立）：改過宣告要跑 `run_cmd.py run ExportCmdSchema`，
> 否則 `source_hash` 不符 → 全鏈預檢**靜默降級為不擋**。

### 1.1 帳戶欄位填 **agent id**，不是 persona 名

> [!IMPORTANT]
> `account` / `target_bank` / `from_bank` / `to_bank` 的值域是 **bank / 帳戶 id**。
> 例：`account=zeta`（✔）而不是 `account=summit`（✘ 那是 persona 名）。
>
> **2026-07-31 血證**：commit 薪資 hook 拿貼文 sender 當帳戶，summit 帶 persona 名 `summit`
> （bank 應為 `zeta`）→ 錢進了一個不存在的影子帳戶，事後才發現。
> 口訣：**錢認 agent／bank，說話認 persona。**

> [!NOTE]
> **2026-08-14 起 `Credit` / `Debit` 會先做帳號解析**（`UCL_TreasuryAccountResolver`）——
> agent 名（含大小寫）、persona 名、別名都會被歸一成註冊在案的帳號，所以上面那顆槍的
> **殺傷力降低了，但沒有消失**：解析不出來的名字仍會原樣寫入並產生孤兒帳戶（刻意如此 ——
> 拒絕會讓一筆真實勞動的薪水直接消失）。填對仍然是呼叫端的責任。
>
> 解析何時**不**介入：轉帳單與後台轉帳一律 `resolveAccount:false`（認字面）。
> 判準：**「從既有帳號清單選出來的」＝認字面；「從身分推導出來的」＝歸一。**
>
> 解析規則怎麼看怎麼改、跑掉的錢怎麼歸戶、空帳號怎麼銷 →
> [`Treasury_Account_Consolidation_Workflow.md`](../../Workflows/Treasury_Account_Consolidation_Workflow.md)
>
> ⚠ C# 端的 canonical 解析實作是 `UCL_TreasuryAccountResolver`（`UCL_BankAdminPage` 內的
> `ResolveAgentToBank` 是 admin 代操作用的更嚴版本：未知一律拒絕、不 derive）。
> Python 端仍是 `Tools~/AgentCommands/_lib/bank_resolver.py`。**別造第四份。**

---

## 2. op 一覽（12 個）

| op | 動錢? | 必填 | 一句話 |
|---|---|---|---|
| `balance` | ✗ | `account` | 查餘額 |
| `credit` | **✓ 進帳** | `account` `amount` `source_kind` | 加錢 |
| `debit` | **✓ 出帳** | `account` `amount` `use_kind` | 扣錢（有帳戶隔離鐵律，見 §3） |
| `transfer` | **✓ 守恆搬錢** | `from_account` `to_account` `amount` `use_kind` `source_kind` | A→B 原子雙分錄 |
| `audit` | ✗ | `account` | 列 entries（可帶 `since_ts`） |
| `verify` | ✗ | `account` | 重放全量驗 `balance_before/after` 一致性 |
| `request` | ✗ | `target_bank` `amount` `reason` | 開**請款單**（消耗公庫），等 Tim 批 |
| `request_list` | ✗ | — | 列請款單（預設只列 pending） |
| `request_cancel` | ✗ | `request_id` | 撤回自己開的請款單 |
| `transfer_request` | ✗ | `from_bank` `to_bank` `amount` `reason` | 開**轉帳單**（總量守恆），等 Tim 批 |
| `closing_generate` | ✗ | — | 補算所有「已完結但未結帳」的 UTC 日 |
| `closing_list` | ✗ | — | 列已結帳日期 + 當前讀取基準 |

> **請款單 vs 轉帳單刻意分開**：請款**消耗公庫**（無中生有一筆錢），轉帳**總量守恆**（只換位置）。
> 審批者要能一眼分辨自己在批哪一種 —— 混成一種單據，公庫就會在沒人注意時被搬空。

### 2.1 常用範例

```bash
# 查餘額
run_cmd.py run Treasury --arg op=balance --arg account=zeta

# 開請款單（不動錢，等 Tim 從 UCL_BankAdminPage → 「📨 請款審批」批款）
run_cmd.py run Treasury --arg op=request --arg target_bank=zeta --arg amount=6 \
  --arg reason="反向任務 20% off 折扣請款" --arg source_kind=manual_request \
  --arg agent=Zeta --arg persona=summit

# 補算每日結帳（只寫 closing/*.json，不動任何餘額）
run_cmd.py run Treasury --arg op=closing_generate
```

---

## 3. 兩條會 throw 的硬規則

### 3.1 帳戶隔離鐵律（`debit` / `transfer` 的出款端）

`callerAgentId` 不為空且 `!= "system"` 時，**必須等於 `account`**，否則 throw
「不可動用對方帳戶」。

- `caller="system"` 是**合法 wildcard**，給 server 內部 auto-credit hook 用。
- ⚠ **2026-08-04 自撞**：canvas 遷移時我傳 `caller="canvas.py"`，被自己半年前寫的這條鐵律擋死。
  工具端要動別人帳戶只有兩條路：傳 `system`，或改走 `transfer_request` 開單。

### 3.2 餘額不足

`debit` 時餘額不足直接 throw（`policies.negative_balance_allowed`）。
`transfer` 的 Debit 先行、Credit 後行；**Credit 罕見失敗會自動 rollback**
（補一筆 `transfer_rollback` credit 回出款方）。連 rollback 都失敗時，
`_last_op.md` 會印 `DANGLING DEBIT entry uuid=...` —— 那是要人工介入的訊號，不會被靜默吞掉。

`transfer` 另有 `amount > 1000` 上限（`max_per_transfer`）。

---

## 4. ⚠ `source_kind` / `use_kind` 是自由字串，**不是 enum**

`ArgsSchema` 寫 `source_kind=enum`，但那是**願望不是事實**。實際行為：

- C# 只驗 **非空**（`throw new ArgumentException("sourceKind 必填")`），**不驗值**。
- `AgentCommands/Treasury/rules.json` 的 `income_sources`（20 個）/ `spending_uses`（10 個）
  是**宣告**，**沒有任何 C# 程式讀它做驗證**。

實測落差（2026-08-04 全量掃 ledger）：

| | 宣告 | 實際用過但未宣告 |
|---|---|---|
| credit `source_kind` | 20 | **15**（`performance_bonus` / `compensation` / `account_genesis` / `reward` …） |
| debit `use_kind` | 13 | **17**（`canvas_pixel` / `draw_voucher_consume` / `overnight_storage_fee` …） |

> [!NOTE]
> **分類擺反的血證（已隨功能移除，但教訓保留）**
> `qa_bug_confirmed` / `qa_observation` / `qa_execution` 三項曾被宣告在 **`spending_uses`**，
> 實際卻當 **credit 的 `source_kind`** 在用 —— 如果當初把 `rules.json` 接成閘門，
> 會**反向誤擋 QA 獎金入帳**。這三項已於 2026-08-04 隨 QA 獎金功能整套移除，
> 但「宣告與實際用途會漂」這個風險本身沒消失（未宣告值仍有 credit 15 / debit 17 種）。
>
> ⚠ 歷史 ledger 仍留有這些 kind 的 entry（含 `qa_watchdog_assist`）——
> **那是已關帳期間的權威記錄，不刪、不改**（見 §5.1）。讀舊帳看到它們是正常的，
> 代表「歷史來源」而非「現行來源」。

**所以**：
1. 填值前先 grep 既有 ledger 看慣例，不要看 `rules.json` 就以為那是全集。
2. **不要拿 `source_kind` 當可信判準**做統計或權限判斷 —— 它是寫入端自填的，偽造成本為零。
   同型血證：`sig_env_marker` 被 python 直寫端自己填成 `manual_filesystem_write_canvas`，
   於是「有 `sig_*` 就是 C# 寫的」這個推論從頭就不成立（2026-08-04 一天錯四次）。
3. 真要不可偽造，得由**唯一寫入點**產生簽章並拒收缺簽章的 entry —— 目前沒有。

---

## 5. 每日結帳（Daily Closing）

### 5.1 核心語意：結帳檔是**權威記錄**，不是快取

> **Tim 2026-08-04 拍板（這條反轉過一次）**
> 舊日期的本就不應該被改動，且以 git 紀錄為準。甚至偵測到不同時，
> **建檔的紀錄比單筆帳更權威**。

初版把結帳當「快取」，於是得處理「快取與 ledger 不一致怎麼辦」——
為此設計了 `cumulative_entry_count` 對帳 + fail-loud + rebuild 指令。

換成「結帳就是該期間的帳」之後，**那個不一致在定義上不存在**：
讀取演算法本來就只重放「結帳日之後」的日期夾，
所以一筆被 bug 寫進已關帳日期的 entry **天然落在範圍外**，
不需要任何邏輯去忽略它，也不需要偵測或重建。演算法從四步變三步。

這就是真實會計的做法：已關帳的期間就是關帳了，遲到的憑證以調整分錄進當期，而非改寫歷史。

> [!NOTE]
> **可複用的判斷**：開始設計防禦機制時，先問「這個異常在**正確的模型**裡還存在嗎」。
> 加邏輯是預設反應，換框架不是。

### 5.2 讀取演算法

```
餘額 = 最近一份「日期嚴格 < 今天(UTC)」的結帳 + 該日之後所有 entry
```

- **「嚴格小於」是關鍵**：今天還沒關帳。用 `<=` 會把今天已寫入的部分當成已定案，
  今天後續進帳就算不到了。
- 成本從 `O(全部歷史)` 降到 `O(今日)`。
- 結帳檔讀壞 → 往**更早**的結帳退（舊結帳一樣有效，只是多重放幾天），不會整體失效。

### 5.3 產出規則

- 只為**有 entry 的日期**寫結帳（沒 entry 的日子餘額與前一份完全相同，寫了是純重複）。
- 只處理**嚴格早於今日 UTC** 的日期。Editor 關一週再開 → 一次補 7 份。
- **餘額 0 的帳戶照樣寫入** —— 不寫的話「歸零」跟「這個帳戶不存在」在下游長得一樣，
  金融語意上兩者本質不同。
- 寫檔 atomic（tmp + move），避免半寫檔被讀到。
- 落檔：`AgentCommands/Treasury/closing/<YYYY-MM-DD>.json`（日期一律 **UTC**，與 ledger 日期夾同曆）。

### 5.4 `audit` 區塊：記錄而不執法

結帳檔的 `audit`（`cumulative_entry_count` / `last_entry_rel` / `gross_credit` / `gross_debit`）
在**產出當下順手算**（那時本來就抓了全量，所以是免費的），
但**不參與讀取判斷** —— 記錄而不執法。

> 由來：apex-one 2026-08-04 提案「**在產出當下計算，讀取端就不必付成本**」——
> 同一份資料在生命週期的不同時點，成本完全不同。Tim 定調**不當 gate**。

壞檔會被跳過但**仍計入 `cumulative_entry_count`** —— 它確實存在於磁碟上，
不計入會讓 audit 數字跟現實對不上，而 audit 的用途正是事後對帳。

---

## 6. 落檔位置

| 東西 | 路徑（相對 repo 根） |
|---|---|
| ledger entry | `AgentCommands/Treasury/ledger/<YYYY-MM-DD>/<HHMMSS>_<MMM>_<UUID6>__<type>.json` |
| 每日結帳 | `AgentCommands/Treasury/closing/<YYYY-MM-DD>.json` |
| 請款單 | `AgentCommands/Treasury/requests/<YYYY-MM-DD>/...__request.json` |
| 經濟規則宣告 | `AgentCommands/Treasury/rules.json` |
| 餘額快取 | `AgentCommands/Treasury/accounts/` |

路徑一律走 `UCL_TreasuryPaths` helper，**不要 hardcode**。

---

## 7. 已知缺口（誠實清單）

- **無 client 端預檢**（§1）—— `Cmd_Treasury` 未宣告 `UCL_CmdArgsSpec`，`commands_schema.json`
  的 `Treasury` 是空物件。動錢的 Cmd 是全 repo 唯一…不對，是**除 Tavern 外全部** Cmd 的共同狀態，
  但財務 op 的錯填成本最高。補 `ArgsSpec` 是純加法（不動 server 行為），**待 Tim 拍板**：
  required 欄位列錯會誤擋合法的金流呼叫，所以不自行動工。
- **`source_kind` / `use_kind` 無驗證**（§4）—— 原 `ArgsSchema` 寫「enum」名不符實，已改為「分類字串(不驗值)」。
- **`rules.json` 分類有舊帳** —— 三個 qa_* 掛錯在 `spending_uses`。
- **本檔只有 zh-Hant** —— `HelpURL` 用 `{lang}` 佔位，其他語系尚未翻譯（走 `ucl-translate-docs`）。
- **計酬 routing 仍讀 `sender_id`**（agent 層），未改走 `sender_persona` → bank 查表。
