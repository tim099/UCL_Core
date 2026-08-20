---
title: Bank 區域綁定遷移（半自動）—— 在新專案觸發
description: 在一個新專案（或還沒設區域 ID 的專案）把 persona → 帳號的綁定導出成 letters/<persona>/bank/<區域ID>.md。機械的部分交給 Cmd_PersonaProfile op=migrate_bank（預設 dry_run），判斷的部分留給人。含前置檢查、逐步驗收讀數、卡住出口，以及「綁定值是 agent id 而錢可能還在舊帳號名下」的硬警告。
last_updated: 2026-08-20
target_audience: [AI_Agent, Developer]
aliases: [區域銀行遷移, bank 綁定遷移, migrate_bank, 區域 ID 設定, Bar 專案遷移, currency_id]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Identity_Account_Unification.md | 身分／帳號統一案 | 本流程的設計來源與拍板全集
  - ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md | persona registry 退場 | 上游（§8.2 一欄一檔／§8.3 欄位分家／§8.6 寫入接縫）
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | 提交規範 | 第 5 步的 commit 邊界
---

# 🪙 Bank 區域綁定遷移（半自動）

> **一句話**：先在後台定這個專案的**區域（貨幣）ID**，再用 `op=migrate_bank` 的 **dry-run** 把
> 「每位 persona 會寫什麼」印出來給人看，人點頭之後才 `dry_run=0` 落檔。
>
> **半自動的分界**：機械（掃 pool、讀 `persona.agent`、寫檔、審計、讀回複驗）交給 Cmd；
> **判斷（區域 ID 叫什麼、跨專案差異是否合理、撞名怎麼歸屬）一律留給人。**

## 0. 誰該讀 / 什麼時候跑

- 一個專案**第一次**要用區域綁定（例：`D:/Unity/Bar`）
- 或既有專案改了區域 ID（改 ID 等於把全體綁定檔重新定鍵，**舊檔不會自動改名**）

⛔ **不要**在「只是想查某人綁什麼」時跑本流程 —— 那是 `op=get_bank`，純讀。

## 1. 前置條件（四格全綠才開始）

| # | 檢查 | 怎麼驗 | 不綠會怎樣 |
|---|---|---|---|
| 1 | 本專案的 `UCL_Core` 含 bank 接縫 | `run_cmd.py --persona <me> run PersonaProfile --arg op=get_bank --arg persona=Template` 不報「未知 op」 | 舊版沒有這三個 op；症狀是 Cmd 直接拋，不會靜默 |
| 2 | Editor 開著 | 前一格能跑就代表通了 | 寫入走 Cmd（`R18` 不做降級路） |
| 3 | 先行專案的 `bank/` 檔已 commit＋push＋本專案已 pull | `ls letters/<某人>/bank/` 看得到別的區域的 `.md` | 不影響本次遷移**正確性**（本專案讀自己的 `persona.agent`），但第 5 步的 commit 會混入未落地的別區檔 |
| 4 | 知道其他專案用了哪些區域 ID | 問人，或看 `letters/<某人>/bank/` 的檔名 | **同名就毀了分區**：兩個專案寫同一個檔 ⇒ 互相覆寫，而症狀是「另一個專案的帳號」，一個完全合法的字串 |

🩸 實測（2026-08-20）：`Bar` 的 `UCL_Core` 停在 `ae7f7931`，`grep WriteBankAccount` ＝ 0 命中
⇒ 那個時點在 Bar 跑本流程只會拿到「未知 op」。**第 1 格不是形式。**

## 2. 分工表

| 誰 | 做什麼 |
|---|---|
| **人** | 決定區域 ID；讀 dry-run 清單並判斷合理性；處置撞名／空值；按後台的二段確認；決定 commit 邊界 |
| 工具 | 掃 pool、讀 `persona.agent`、寫 `bank/<ID>.md`（原子寫＋審計）、寫入後讀回複驗、統計與失敗回報 |
| **沒有人** | push（照 `Commit_Workflow`，Tim 手動） |

## 3. 步驟

### Step 1 — 定區域 ID（人按，後台）

Editor → **ToolBox → 銀行後台管理** → 「🪙 區域（貨幣）ID」面板 → 填 → 儲存（**二段確認**，5 秒內再按）。

- 預設值 `Ducat`；**LY 已用 `Florin`**（1252 佛羅倫斯，杜卡特的宿敵前輩）
- ⚠ **兩個專案不可同名**（見前置條件第 4 格）
- ⚠ ID **就是檔名** ⇒ 空白／`.`／`..`／路徑分隔／檔名非法字元會被擋（`IsValidCurrencyId`）
- 📌 **就算沿用預設值也建議顯式存一次** —— 落盤的 `currency_id` 是宣告，預設值是猜測；
  兩者在讀取端長得一樣，但後者會在有人改了預設常數時無聲改變

**驗收**：`run PersonaProfile --arg op=get_bank --arg persona=<任一人>` 的回報 `currency` ＝新 ID。

### Step 2 — dry-run（工具印、**人讀**）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run PersonaProfile \
    --arg op=migrate_bank --arg actor="<me>@migrate" --arg reason="<為什麼跑這次遷移>"
```

`dry_run` **預設 1**（只印不寫）。逐位清單印在 **Editor log**（`[PersonaProfile] migrate_bank …`），
回報值只有統計（`pool` / `written` / `skipped_*` / `failed`）。

**人要看的三件事**：

1. **每位的 agent 值合不合理** —— 對照本專案的 `AwakenInit/_registry_meta.json` 的 `agent_banks`
2. **跟其他專案不同是正常的** —— 同一個 persona 在不同專案可以是不同 agent。
   🩸 實測（2026-08-20 LY vs Bar）：

   | persona | LY 的 agent | Bar 的 agent |
   |---|---|---|
   | `Sirius` | `Fed` | **`Spectre`** |
   | `ame` | `claude-code` | **`Zeta`** |
   | `apex-one` | `Altair` | **`Sirius`** |
   | `claude-da-xiaojie` | `antigravity` | **`gemini`** |

   ⇒ **這正是一區一檔存在的理由**：兩邊都對，只是屬於不同的區域。
   ⇒ 也是為什麼「跨區借用」只能當**過渡**：借來的值不只是舊的，**可能是錯的**（見 §4）。
3. **`⛔ agent 為空`** 的人 —— 那些會被跳過（沒有可導出的來源），要人去身分後台補綁。

### Step 3 — 人工處置（**這步沒做完不要往下**）

| 現象 | 處置 |
|---|---|
| `⛔ persona.agent 為空` | 到 **Persona & Agent 管理頁**換綁（`DoRebindClicked`，走 §8.6 接縫有審計）；或確認這個 persona 已退役 ⇒ 就讓它跳過 |
| `○ 本區已有綁定，且與 agent 不同` | **不要順手 `overwrite=1`** —— 先問「哪個是對的」。既有檔可能是人工設的（那就是真相），agent 欄可能是舊的 |
| 這個專案的 pool 有別的專案沒有的人 | 正常（例：`kaguya` 只在 Bar）。它只會寫本專案的檔 |
| 帳號名撞號／`-da-xiaojie` 那批 | **不在本流程處理** ⇒ 走 `Plan_Identity_Account_Unification` §4.2 的人工拍板清單 |

### Step 4 — 落檔（人下決定，工具執行）

```bash
... run PersonaProfile --arg op=migrate_bank --arg dry_run=0 \
    --arg actor="<me>@migrate" --arg reason="<拍板來源：誰說的、哪一天>"
```

`overwrite=1` **只在 Step 3 判斷過**之後才加。部分失敗會**拋例外**（不吞 —— 批次的部分失敗最容易
被讀成全部成功）。

**驗收讀數（四格，缺一格不算完）**：

| 驗什麼 | 怎麼驗 | 判準 |
|---|---|---|
| 統計 | Cmd 回報 | `written` ＝ 應寫數；`failed=0` |
| 磁碟 | `cat letters/<某人>/bank/<ID>.md` | 裸值＋換行、**無 BOM**（同 `profile/`） |
| 審計 | `grep -c "bank/<ID>" AgentCommands/AwakenInit/_persona_write_audit.jsonl` | ＝本次寫入筆數，且 actor／reason 都在 |
| **別區沒被動** | `git status` 看 `bank/` 底下**其他區域**的檔 | **必須是乾淨的**（本流程只寫本區那一個檔） |

📌 參考讀數（LY 2026-08-20 首航）：`pool=21`／`written=21`／`failed=0`；
`get_bank kiara` → `source=Florin`；`get_bank kiara currency=Ducat` → `source=Florin` ＋
note「本區無綁定，借用區域 Florin 的帳號」（**跨區借用會出聲**）。

### Step 5 — commit（人決定邊界）

Editor → **ToolBox → 自動提交** → `bank/` 群（預設勾）。
⚠ **在線的 persona 預設不勾**（她可能正在被寫）⇒ 要人手動勾。

⚠⚠ **letters 是共用 repo**（同一個 git repo 被多個專案掛著，實測兩邊 root commit 與 HEAD 相同）
⇒ **你的 commit 會看到別的專案的 `bank/*.md`**。那是正常的，**照收**。
⛔ 絕不因為「不認識這個區域」而排除或刪除 —— 刪掉的症狀是對方下次登入「沒有綁定」
（落央行＋ErrorLog），而錯的原因**指不到這裡**。

commit 邊界照 `Commit_Workflow`：**預設單層**，父層 pointer 不 bump（除非有人明說 commit all）。
letters 有自己 repo 的 persona 各自一筆（LY 實測 9 位），其餘在 `AgentCommands` 主樹一筆。

## 4. ⚠ 硬警告：綁定值是 **agent id**，而錢可能還在**舊帳號名**下

`bank/<ID>.md` 的內容是 **agent id**（Tim 2026-08-20 拍板 ⑫：帳號 id ＝ agent id，
「bank id」那套獨立命名空間退場）。**但那是目標狀態，不是現況。**

實測兩個專案的現況（2026-08-20）：

| 專案 | 綁定檔會寫 | 錢實際在哪 | 該 agent 同名帳號的餘額 |
|---|---|---|---|
| LY | `claude-code` | `cc` ＝ **884** | `claude-code` ＝ **0** |
| Bar | `claude-code` | `claude-da-xiaojie` ＝ **6,573** | `claude-code` ＝ **17** |
| Bar | `Zeta` | `Zeta-da-xiaojie` ＝ **3,507** | `Zeta` ＝ **6** |
| Bar | `antigravity` | `antigravity-da-xiaojie` ＝ **1,650** | `antigravity` ＝ **18** |

⇒ **解析端（`UCL_TreasuryAccountResolver`）在改名／歸併完成之前，不可以直接把綁定值當帳號用。**
那樣做的症狀不是報錯，是**薪水靜默轉向一個餘額 0 的合法帳號**。

**兩條合法路徑，二選一（要拍）**：

- **(A) 先改名歸併再接解析端**：把舊帳號的餘額用 ledger transfer 搬到 agent id 名下
  （`source_kind=account-rename`），舊號歸零後進 `closed_accounts` 並記 `renamed_to`。
  之後綁定值＝帳號名，一跳到底。
- **(B) 解析端保留一跳並 fail-loud**：綁定值（agent id）→ `agent_banks[agent]` → 帳號。
  過渡期可行，但**那一跳必須出聲**（否則「已收斂」與「還在走過渡」同形），
  且它就是 `Plan_Persona_Registry_Retirement` 要退場的正向鏈。

🩸 這是「改一半更糟」的實例：**綁定檔先落地是安全的**（沒有消費端），
**解析端先接才是危險的**（有消費端，而且是錢）。本流程刻意只做前者。

## 5. 卡住的出口

| 症狀 | 真因 | 出口 |
|---|---|---|
| `未知 op 'migrate_bank'` | 本專案 UCL_Core 太舊 | 更新 submodule（前置條件第 1 格） |
| `區域 ID 不合法` | ID 當不了檔名 | 換一個；別用路徑分隔或 `..` |
| 全員 `⛔ agent 為空` | 讀到的是別棵資料樹 | 查 `AgentCommands` 掛載位置與 `data_root`；**空集合是靜默的**（§5.1 同族） |
| `written` 少於預期 | 有人「本區已有綁定」被跳過 | 讀 Editor log 的 `○` 行；判斷後再決定要不要 `overwrite=1` |
| 改了區域 ID 之後全員「沒有綁定」 | 舊檔沒改名 | 舊 `bank/<舊ID>.md` 改名成新 ID；或重跑本流程重新導出 |
| Cmd 逾時 | 沒帶 `--persona` ⇒ 掉進 `queues/anonymous/` | 一律帶 `--persona <你>` |

## 6. 相關

- 設計來源與拍板全集：`ucl_core:Docs~/{lang}/Plan/Plan_Identity_Account_Unification.md`
- 三段解析順序（本區 → 跨區借用 → 央行＋ErrorLog）：同上 §3.5.1
- `-da-xiaojie` 去除與帳號歸併（**人工拍板**）：同上 §4.2 D.1／D.2
- 寫入接縫與審計（actor＋reason 必填）：`Plan_Persona_Registry_Retirement` §8.6
