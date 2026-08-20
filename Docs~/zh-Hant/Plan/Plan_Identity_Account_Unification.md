---
title: identities.json 併入 Bank 系統 —— 一個 id 空間、一張帳號身分表
slug: identity-account-unification
status: **分析階段**（尚未動工；§4 的人工拍板清單未拍完之前不施工）
created_at: 2026-08-20T02:30:00Z
created_by: kiara
last_updated: 2026-08-20
builders: []
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md | persona registry 退場 | 本案的上游（§8.1 反向登記／§8.3 欄位分家）
  - repo:AgentCommands/BugReports/reports/0021.md | BUG-21 | bank_personas 反向表沒有寫入端
  - repo:AgentCommands/BugReports/reports/0022.md | BUG-22 | 顯示身分取自 bank ⇒ 同 bank 的 persona 全掛同一個名字
---

# identities.json 併入 Bank 系統

> Tim 2026-08-20 指示：
> ① `identities.json` 的資訊整合進 Bank 系統；② 手動處理需要遷移的部分；
> ③ **之後廢棄 bank id，實際上只有 agent id 這一項**；
> ④ 綁定資訊改存「persona → agent id」在銀行系統內，**保證 1 對 1**；
> ⑤ 沒有綁定的**直接報錯**（綁央行，但要有 ErrorLog）。
>
> 本檔是動工前的分析。**§4 是人工拍板清單 —— 那些格子沒拍完就不要開始搬。**

## 0. 先講量出來的數字（2026-08-20，唯讀探針）

| 量 | 讀數 |
|---|---|
| `identities.json` 筆數 | **31**（agent 9／discord-user 18／npc 2／`?` 1／…） |
| Treasury 有帳號的 id 數 | **48** |
| 兩者交集 | **13** |
| identities 有、Bank 完全沒有 | **18**（17 個 `discord:<uid>` ＋ `TEST`） |
| Bank 有錢、identities 沒有 | **18 個帳號** |
| 全系統 tavern_token 總量 | **33,692**（watermark `/2026-08-20/022517_773_e932fb__credit.json`，48 帳號） |
| bank id ≠ agent id | **5 / 9**，舊名下共 **11,192** token |
| 完全無分類的孤兒／測試帳號 | **11 個，共 625** token |

⇒ **兩張表互相有洞**：`identities` 缺 4 個現行 agent（`Myth`／`Altair`／`Codex`／`Template`），
Bank 缺 17 個 Discord 使用者。**沒有一張表是完整的**，所以「以某一張為準去覆蓋另一張」都會掉資料。

## 1. 這兩張表本來就在描述同一件事

判準：**「可以被指名、可以收錢、可以顯示」的實體。** 證據是 Bank 系統裡早就有非 agent 的帳號：

| 帳號 | 餘額 | 它是什麼 |
|---|---|---|
| `Tim` | 371 | 人類 |
| `discord:383604378185105408` | 95 | Discord 使用者（Tim 本人） |
| `discord:295848903494991872` | 1 | Discord 使用者 |
| `subconscious-daemon` | 17 | NPC（潛意識守夜人） |
| `tavern-keeper` | 0 | NPC（酒保，且是 `system_accounts`） |
| `pacific-standard-public-deposit-bank` | 9,712 | 央行（`system_accounts`） |

⇒ 「帳號」與「身分」的分家從來沒有真的成立過，只是**各自長了一半**：
`identities` 長了 Discord 那一半，Bank 長了金流那一半。合併不是新增抽象，是把已經重疊的兩張表收成一張。

🩸 而分家的代價已經收到帳單了：**BUG-22** —— 顯示身分（`identities` 的鍵）與金流身分（bank id）
被當成同一個命名空間，`§8.1` 反轉解析方向之後，同一家 bank 的所有 persona 顯示成同一個名字。

## 2. 欄位對照與去向

`UCL_ChatIdentity` 只有 5 欄（rich 資料另存 `UCL_ChatTavernIdentityAsset`，本案不動它）：

| 欄 | 現在誰在用 | 併入後去向 |
|---|---|---|
| `id` | 全部消費端的鍵 | **統一帳號表的鍵**（＝agent id／`discord:<uid>`／NPC id；**不再有獨立的 bank id**） |
| `display_name` | 酒館顯示、Discord 中繼、後台 roster | 保留。**agent 類可省**（省略＝用 id 本身，符合「只有 agent id 這一項」） |
| `kind` | mention 白名單分類、`IsRealAgentSender` | 保留並擴充：`agent｜human｜npc｜discord-user｜system｜closed` |
| `created_at` | 無實質消費端（只寫） | 保留（審計） |
| `last_seen_at` | `GetOrCreateIdentity` 每次 join 更新 | 保留，但**寫入頻率要注意**：它會讓帳號表變成高頻寫入檔 |

### 消費端逐支盤點（動手前必須全部有著落）

| 消費端 | 吃哪一欄 | 併入後 |
|---|---|---|
| `Cmd_Tavern` 顯示名（:806） | `display_name` | 改查統一表；鍵已於 BUG-22 改成 agent id（`725e92c`） |
| `Cmd_Tavern` op=join（:1603/1624） | 全欄（建立／更新） | 改寫統一表 |
| `Cmd_Tavern`（:2755） | agentId → identity | 同上 |
| `UCL_ChatTavernIO.GetOrCreateIdentity`（:185） | 寫入 | **這是唯一的自動建立入口** —— 併入後要決定「join 能不能自動開帳號」（見 §4.3） |
| `UCL_ChatTavernIO` mention 白名單（:990） | `id` 集合 ∪ persona ids | 改成 統一表 ids ∪ persona ids |
| `UCL_DiscordIdentityResolver`（:131） | `id → display_name` | **Discord 那一半的唯一消費端**；不能靠 agent id 表取代 |
| `UCL_ChatTavernPage` roster（:1549） | 全欄 | 改查統一表 |
| `Cmd_SeedTavernIdentityAssets`（:54） | roster → 頭像資產 | 改查統一表 |
| python `tavern_handshake.py` / `tavern_query.py` | 讀 identities | 兩支要一起改（跨端契約） |

## 3. 統一後的資料形狀（提案）

住在**銀行系統**（`AwakenInit/_registry_meta.json` 已經有 `agent_banks`／`system_accounts`／
`closed_accounts`／`bank_personas`，是自然落點；若嫌它太雜可另開 `Treasury/accounts.json`）：

```jsonc
{
  "accounts": {
    "claude-code": { "kind": "agent",         "display_name": "", "created_at": "…" },
    "Myth":        { "kind": "agent",         "display_name": "", "created_at": "…" },
    "Tim":         { "kind": "human",         "display_name": "Tim" },
    "discord:383604378185105408": { "kind": "discord-user", "display_name": "Tim" },
    "tavern-keeper":{ "kind": "npc",          "display_name": "酒保" },
    "pacific-standard-public-deposit-bank": { "kind": "system", "display_name": "Pacific Standard Public Deposit Bank" },
    "cc":          { "kind": "closed", "display_name": "", "renamed_to": "claude-code" }
  },
  "persona_agents": {            // Tim ④：綁定改存這個方向，dict 形狀天然保證 1:1
    "kiara": "Myth", "basecamp": "claude-code", "summit": "Zeta"
  }
}
```

三個設計決定與理由：

1. **`display_name` 空字串＝用 id 顯示**（不是「沒有名字」）。agent 類一律留空 ⇒
   `claude-code@basecamp`。這樣「只有 agent id 這一項」不需要維護第二份文案。
2. **`persona_agents` 用 dict 而不是 `bank_personas` 那種反向 list** ——
   dict 的鍵唯一性**天然保證 1:1**，不需要像現在那樣寫一段「撞名就拒絕解析」的守衛
   （那段守衛是為了 list 形狀才存在的）。
3. **舊 bank id 進 `kind: "closed"` 並記 `renamed_to`** —— 歷史 ledger 不重寫（14,159 筆內嵌
   `account_id`，那是稽核軌跡），所以舊名必須永久可解釋，但不再是任何人的 canonical。

### ⑤「沒有綁定就報錯 ＋ 綁央行 ＋ ErrorLog」的落點

`UCL_TreasuryAccountResolver.Resolve()` 現在的 ⑥ 分支是
「查無對應（未歸一，將產生／沿用孤兒帳戶）」—— **不 derive、不 mint，但也不出聲**。
改成：`Debug.LogError` ＋ 落 `UCL_CentralBankSettings.DefaultCentralBankAccount`
（＝`pacific-standard-public-deposit-bank`）。

⚠ **央行不是 `Federal Reserve System`** —— 後者是 persona `Sirius` 的 bank（agent `Fed`），
裡面 6,253 是他的錢。這一格搞混就是把別人的帳戶當公庫。

## 4. 遷移清單 —— 哪些機械可導、哪些**必須人工拍**

### 4.1 機械可導（不必拍，但要對帳）

| 來源 | 筆數 | 導出規則 |
|---|---|---|
| `agent_banks` 的 agent | 9 | `kind=agent`、`display_name=""` |
| `system_accounts` | 5 | `kind=system` |
| `closed_accounts` | 7 | `kind=closed` |
| `identities` 的 `discord-user` | 18 | 原樣搬（`display_name` 只有這裡有） |
| `identities` 的 `npc` | 2 | 原樣搬 |
| persona → agent | 21 | 由 `persona.agent` 導出成 `persona_agents`（**已實測 21/21 完整且 1:1**） |

### 4.2 必須人工拍板的資料問題（**這節沒拍完不要施工**）

#### ⚠ A. 一個 id 三重身分，而且身上有 4,636 token

`claude-da-xiaojie` **同時是**：`identities` 的 `kind=agent`、`system_accounts` 的一員、
**而且是 persona pool 裡真實存在的一個 persona 名**。餘額 **4,636**（全系統第三大）。

⇒ 統一成一張表之後，這個 id 只能有**一種** kind。要拍：它是 agent、system、還是 persona？
那 4,636 屬於誰？（同族還有 `apex-one`：identities 是 agent、同時是 persona 名，餘額 0，
無金流風險但同樣要拍。）

#### ⚠ B. persona 名混進帳號空間

persona 名同時是 Treasury 帳號的共 13 個，合計 **4,690** token ——
其中 4,636 是上面那顆 `claude-da-xiaojie`、54 是 `Template`（它同時是 agent／bank／persona 三重名），
**其餘 11 個餘額都是 0**。

⇒ 拍：餘額 0 的那 11 個直接不進統一表（它們是「曾經被指名過」的殘影）？
`Template` 的三重名要不要改掉其中之一？

#### ⚠ C. 孤兒／測試殘留帳號 11 個、共 625 token

| 帳號 | 餘額 | 看起來是 |
|---|---|---|
| `Tim` | 371 | 人類（該給 `kind=human`，不是孤兒） |
| `gemini-da-xiaojie` | 94 | 上一代命名 |
| `zeta-da-xiaojie-bank` | 91 | 上一代命名（帶 `-bank` 後綴） |
| `zeta-bank` | 31 | 上一代命名 |
| `subconscious-daemon` | 17 | NPC（該給 `kind=npc`） |
| `claude` | 14 | 打錯字／舊命名 |
| `antigravity-apex-two` | 2 | 命名慣例殘留 |
| **`fake-imposter`** | **2** | **測試殘留** |
| `ClaudeCode-da-xiaojie` | 1 | 大小寫變體 |
| `antigravity-reserve` | 1 | 用途不明 |
| `tim099-da-xiaojie` | 1 | 測試殘留 |

⇒ 拍：`Tim` / `subconscious-daemon` 正名歸類；其餘 9 個（共 237）要**歸零併入央行**、
**留成 closed 保留餘額**、還是**逐筆查清楚**？
🩸 判準建議：**不要靜默歸零**。625 不多，但「錢消失而沒有一筆帳」正是這套系統最不能接受的形狀。
歸零也該是一筆 ledger transfer。

#### ⚠ D. bank id → agent id 改名（碰錢，5 筆共 11,192）

| agent | 舊 bank | 該搬金額 |
|---|---|---|
| `antigravity` | `a` | 321 |
| `claude-code` | `cc` | 868 |
| `Fed` | `Federal Reserve System` | 6,253 |
| `gemini` | `g` | 1,017 |
| `Zeta` | `zeta` | 2,738 |

⚠ `UCL_TreasuryLedger.GetBalance(accountId)` 是 **raw string key、無別名解析** ⇒
直接改 canonical＝帳面上這 11,192 當場消失、新帳從 0 起算，**而且不會有任何一格報錯**。
⇒ 必須走 **ledger transfer**（debit 舊／credit 新、同額、`source_kind=account-rename`），
舊號歸零後進 `closed_accounts` 並記 `renamed_to`。

#### ⚠ E. 券帳本的鍵已經漂了一代（要先對帳，不是照搬）

`ChatTavern/agent_bonus_quota.json` 的 `agents` 節點鍵是 **bank id**，而現況是：

| 鍵 | personas | 券合計 |
|---|---|---|
| `claude-da-xiaojie` | 8 | 824 |
| `antigravity-da-xiaojie` | 4 | 570 |
| `Zeta-da-xiaojie` | 1 | 146 |
| `zeta` | 1 | 82 |
| `gemini` | 1 | 75 |
| `Altair` | 1 | 40 |
| `a` | 1 | 30 |
| **`crest-001`** | 1 | 22 |
| `Federal Reserve System` | 1 | 10 |

**`cc` 這個鍵根本不在裡面**，但 `claude-da-xiaojie` 底下掛著 8 個 persona 的 824 張券；
而 `crest-001`（一個 persona 名）也被當成 bank 鍵。
⇒ 這本的**現況本身**要先查清楚：那 824 張券今天查得到嗎？`GetBalance(bank="cc", persona=…)` 會回 0。
照搬會把上一代命名一起搬進新命名，**等於把漂移固定下來**。

#### ⚠ F. `join` 能不能自動開帳號

`GetOrCreateIdentity` 現在**自動建立**不存在的 identity。統一之後，identity ＝ 帳號
⇒ 「自動建立」就變成「自動開戶」。
⇒ 拍：join 只能用既有帳號（未登記＝報錯＋央行，符合 Tim ⑤），還是仍可自動開？
🩸 傾向前者：自動開戶正是 `fake-imposter`／`claude`／`tim099-da-xiaojie` 這批孤兒的來源。

#### ⚠ G. `TEST` 與 `discord:tim-smoke`

`identities` 有 `TEST`（kind=agent，Bank 無帳）；Bank 有 `discord:tim-smoke`（餘額 1，identities 無）。
⇒ 拍：測試用身分要不要進統一表（建議 `kind=system` 或直接不遷）。

### 4.3 兩表互缺的補齊

- `identities` 缺現行 agent：`Myth`／`Altair`／`Codex`／`Template` ⇒ 由 `agent_banks` 機械補上
- Bank 缺 17 個 Discord 使用者 ⇒ 拍：他們**要不要有帳號**？
  （現況：只有 2 個 Discord id 有餘額。Tim 2026-08-14 已拍「Discord 訪客不計酬」
  ⇒ 建議 `kind=discord-user` 進表但**不開戶**，帳號欄留空。）

## 5. 施工順序與驗收（每一步能單獨回滾）

| 步 | 做什麼 | 碰錢 | 驗收判準 |
|---|---|---|---|
| 1 | 建 `persona_agents`（由現況導出）＋ `Resolve()` 改讀它＋⑥ 分支改 ErrorLog＋央行 | ❌ | 21 位解析結果與現況**逐位相同**；故意刪一位 ⇒ 出現 ErrorLog 且落央行 |
| 2 | 建統一 `accounts` 表（§4.1 機械可導的部分）＋消費端逐支改讀它 | ❌ | 31＋48 兩邊的 id 全部有著落；mention 白名單集合**前後相同** |
| 3 | `identities.json` 退場（agent 那半刪除、Discord/NPC 併入） | ❌ | Discord 顯示名前後相同（`UCL_DiscordIdentityResolver` 抽樣比對） |
| 4 | §4.2 的人工拍板逐格處理（A/B/C/G） | ⚠ 部分 | 每一筆調整都有一筆 ledger 記錄，**總量守恆 33,692** |
| 5 | bank id → agent id 改名（ledger transfer 5 筆） | ⚠⚠ | 逐帳號前後餘額比對；**總量守恆**；舊號歸零且 `renamed_to` 有值 |
| 6 | 券帳本對帳後遷移 | ⚠ | 每位 persona 的券數前後相同（不是每個 bank 鍵相同） |

**總量守恆是本案唯一不可妥協的驗收**：每一步前後 `sum(balances)` 必須是 **33,692**
（除了刻意的 transfer，而 transfer 是零和）。

## 6. 風險與已知邊界

1. **`GetBalance` 無別名解析** —— 這是整案最大的地雷（§4.2 D）。任何「改 canonical 名」的
   動作若沒有配套 transfer，症狀是「餘額變 0」而**不是**錯誤訊息。
2. **`last_seen_at` 讓帳號表變高頻寫入** —— 帳號表若同時是金流真相源，
   每次 join 就改寫它並不理想。建議把 `last_seen_at` 拆去別處，或明確接受它。
3. **`display_name` 空字串的語意** 必須寫進註解：空＝用 id，不是「沒名字」。
   否則下一個人會「順手補上」，於是又長出第二份文案。
4. **跨端契約**：`persona_agents` 與統一 `accounts` 表都要 C# ／ python 兩端同批改
   （§8.1 的血證：只上一端會兩邊各解一個答案而都不報錯）。
5. **歷史 ledger 永不重寫。** 舊 id 必須永久可解釋 ⇒ `closed` ＋ `renamed_to` 是必要欄位，
   不是裝飾。
