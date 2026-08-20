---
title: identities.json 併入 Bank 系統 —— 一個 id 空間、區域銀行 ID、綁定落 letters/<persona>/bank/
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
> **二次設計（同日追加，取代 ④ 的落點）**：
> ⑥ 銀行（酒館系統）**每個專案有自己的 ID**（貨幣名，預設 **`Ducat`**），存在該專案 `AgentCommands`，
> **且要能在 `UCL_BankAdminPage` 編輯**；
> ⑦ persona 資料夾新增 `bank/`（`letters/<persona>/bank/`），存「在該貨幣（bank）系統下使用的
> 銀行帳號（agent）」—— **每個不同區域銀行一個獨立檔案**；
> ⑧ 目前實際上只有兩個專案（另一個在 `D:/Unity/Bar`）；**舊紀錄 ambiguous 的地方人工處理**。
>
> **第三批拍板（2026-08-20，H/I/J 三題的答案）**：
> ⑨ 遷移時**同時把 `-da-xiaojie` 去掉**（例：`claude-da-xiaojie` → `claude`）；
> ⑩ **本專案（LY）的區域 ID ＝ `Florin`**（1252 年由佛羅倫斯共和國鑄造，杜卡特的「一生宿敵與前輩」）；
> 預設名 `Ducat` 留給未設定的專案；
> ⑪ **H 已答**：見 ⑩。**I 已答**：`agent` 欄位**就等於**帳號 id，看專案環境決定用哪一個 ——
> 且**若本專案還沒綁，但該 persona 在其他專案已有設定，則預設綁「其他專案設定的那個」，不落央行**；
> ⑫ **J 不做**：`bank_id` 不寫進 ledger 每一筆 —— 因為之後**帳號 id 就是 agent id**，
> 「bank id」這個獨立命名空間整個退場（不同地區只是**可以用不同帳號**，不是有另一套 id）。
>
> **第四批拍板（2026-08-20）**：
> ⑬ 後台改貨幣 ID **應自動觸發全體重綁**（原本綁在 `Ducat` 的帳號自動綁到 `Florin`），
> **除非新區已經有綁帳號 ⇒ 報錯**（那狀況要避免，不是要挑一個）；
> ⑭ 後台加「**開啟設定檔位置**」按鈕（參考 `UCL_BartenderCliCommandsPage`）；
> ⑮ **兩階段推進**（見 §5.1）：先預跑確認每個 persona 會綁到哪個 agent → 把新版綁定資料先寫入 →
> **兩邊專案都確定之後**，才跑第二階段（全面改用新版 ＆ 舊帳戶整合歸戶）。
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

## 3. 統一後的資料形狀

> **Tim 2026-08-20 二次設計（本節為現行方案；前一版的「中央一張 `persona_agents` dict」已被它取代
> —— 取代理由見 §3.1，那是實測出來的硬需求，不是偏好）。**

### 3.0 三件事

1. **每個專案有自己的「區域銀行 ID」**（可理解為**貨幣名稱**），存在該專案的 `AgentCommands`，
   預設名 **`Ducat`**，且**必須可在 `UCL_BankAdminPage` 編輯**。
2. **persona 資料夾多一個 `bank/` 目錄**：`letters/<persona>/bank/<bankId>.md`
   內容＝該 persona 在**那個區域銀行**底下使用的**銀行帳號（＝agent id）**。
   **一個區域銀行一個檔**（同 `profile/` 的一欄一檔慣例）。
3. 缺檔＝沒有綁定 ⇒ **`Debug.LogError` ＋ 落央行**（`UCL_CentralBankSettings.DefaultCentralBankAccount`
   ＝ `pacific-standard-public-deposit-bank`）。1:1 由「一檔一值」天然保證。

### 3.1 為什麼「一區一檔」是硬需求（實測，不是風格）

**persona 的 letters 資料夾是同一個 git repo，被兩個專案同時掛著。**

    LY  : letters/kiara  root commit 6512bb8d…  HEAD 2e425cb
    Bar : letters/kiara  root commit 6512bb8d…  HEAD 2e425cb     ⇒ 同一個 repo、當下完全同步

（remote 有兩個位址 —— gitlab `tavern4371824/kiara` 與 github `Persona9999/kiara` ——
Tim 以工具同時 push 全部 remote 並保持同步，**是鏡像不是分身**。）

⇒ 任何存「單一值」的檔（例如 `bank.md`）會被兩個專案**互相覆寫**，而覆寫的症狀是
「另一個專案的帳號」—— 一個完全合法的字串。**一區一檔把這個對撞從資料形狀上消滅掉。**

### 3.2 為什麼 `agent` 不能進 `profile/`

`profile/` 存的是**不綁專案**的身分欄（§8.3：`layer_role` / `forked_from` / `email` /
`identity_vector`…）—— 那些跨專案共用是**正確**的。
而 `agent` 是**綁專案**的，實測兩專案的 `agent_banks` 根本不同：

| agent | LY 的 bank | Bar 的 bank |
|---|---|---|
| `claude-code` | `cc` | `claude-da-xiaojie` |
| `Zeta` | `zeta` | `Zeta-da-xiaojie` |
| `antigravity` | `a` | `antigravity-da-xiaojie` |
| `gemini` | `g` | `gemini` |
| `Myth` / `Codex` / `Template` | 同名 | 同名 |
| — | LY 有 `Altair` / `Fed` | Bar 有 `Luna` / `Spectre` / `Sirius` |

⇒ 同一個 persona 在兩個專案可以是不同 agent、不同帳號。存進 `profile/` 就是把
「綁專案的事實」放進「不綁專案的抽屜」。`bank/<bankId>.md` 是**同時滿足兩者**的形狀：
檔案跟著 persona 走（換專案 checkout 就有歷史），而鍵把兩個專案隔開。

### 3.3 🩸 這個設計會擋掉的真實事故

同一個帳號名在兩個專案有不同金額：

    LY  : Myth = 2,295
    Bar : Myth =   453

2026-08-17 `UCL_BartenderDaemon` 的 `dataPath/../..` 走出樹外、命中另一棵資料樹，
**酒館每個人查餘額都拿到 453**，而 LY 的真實帳本是另一個數字 —— 差額沒有任何一層出聲
（那次的血證寫在 `Plan_Persona_Registry_Retirement` 與 kiara wake#13 收尾信）。

⇒ **區域銀行 ID 正是讓那次失敗變大聲的東西**：ledger／帳號表帶著它宣告自己屬於哪個貨幣，
讀到不屬於本專案的資料就是 fail-loud，而不是一個看起來完全正常的數字。
**這條不是附帶好處，是本設計的主要理由之一。**

### 3.4 資料形狀（提案）

專案層（`AgentCommands`，`UCL_BankAdminPage` 可編輯）：

```jsonc
// AwakenInit/_registry_meta.json（或 Treasury/bank_settings.json —— 落點見 §6.6）
{
  "bank_id": "Ducat",              // 本專案的區域銀行／貨幣 ID。預設 Ducat；後台可改
  "accounts": {                    // 統一帳號身分表（取代 identities.json，見 §2）
    "claude-code": { "kind": "agent",  "display_name": "" },
    "Tim":         { "kind": "human",  "display_name": "Tim" },
    "discord:383604378185105408": { "kind": "discord-user", "display_name": "Tim" },
    "tavern-keeper": { "kind": "npc", "display_name": "酒保" },
    "cc": { "kind": "closed", "renamed_to": "claude-code" }
  }
}
```

persona 層（`letters/<persona>/bank/`，一區一檔）：

```
letters/kiara/bank/Ducat.md        →  Myth          （LY 的區域銀行下用哪個帳號）
letters/kiara/bank/<Bar 的 ID>.md  →  Myth          （Bar 那邊的綁定，互不干擾）
```

三個設計決定與理由：

1. **`display_name` 空字串＝用 id 顯示**（不是「沒有名字」）。agent 類一律留空 ⇒
   `claude-code@basecamp`。這樣「只有 agent id 這一項」不需要維護第二份文案。
2. **綁定用「一檔一值」而不是中央 list** —— 檔案存在性即唯一性，**1:1 由形狀保證**，
   不必再寫「撞名就拒絕解析」那段守衛（`bank_personas` 的 list 形狀才需要它）。
3. **舊 bank id 進 `kind: "closed"` 並記 `renamed_to`** —— 歷史 ledger 不重寫（14,159 筆內嵌
   `account_id`，那是稽核軌跡），所以舊名必須永久可解釋，但不再是任何人的 canonical。

### 3.5 被取代的形狀（留檔備查）

前一版提案是「銀行系統中央一張 `persona_agents` dict」。它的 1:1 保證同樣成立，
但**跨專案會對撞** —— 兩個專案各有一份中央表時，persona 的綁定要在兩張表各存一次，
而那兩張表沒有任何機制互相對帳。`bank/<bankId>.md` 把「分區」做進鍵裡，
所以同一份 persona 資料夾可以同時服務任意多個專案。

### 3.5.1 解析順序（Tim 指示 ⑪，取代單純的「缺檔就央行」）

    ① letters/<persona>/bank/<本專案 CurrencyId>.md 存在  ⇒ 用它（正常路徑，無標記）
    ② 不存在，但 bank/ 底下有**其他區域**的檔        ⇒ 用那個（跨區借用），**且必須出聲**
    ③ 兩者皆無                                      ⇒ 央行 ＋ Debug.LogError（指示 ⑤）

②「跨區借用」的理由：一個 persona 在別的專案已經有帳號歸屬，那個歸屬**比央行更接近真相** ——
把它丟給公庫是資訊上的浪費。但它**不是本區的宣告**，所以要有標記／warning：
不出聲的話「本區真的綁了」與「借用別區的」在輸出上同形，而前者才是收斂的目標。

⚠ 多個其他區域都有檔時**不要挑一個**（那是猜）—— 出聲並落央行，或要求人工指定。
判準同 §8.1 的撞名處理：**這裡不替你挑一個。**

### 3.6 「沒有綁定就報錯 ＋ 綁央行 ＋ ErrorLog」的落點（Tim 指示 ⑤）

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
| persona → agent | 21 | 由 `persona.agent` 導出成 `letters/<p>/bank/<bankId>.md`（**已實測 21/21 完整且 1:1**）。⚠ Bar 那邊要用 **Bar 自己的 `persona.agent`** 導它自己的鍵，不能拿 LY 的值去寫（兩專案 agent 不同，見 §3.2） |

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

#### ⚠ D.1 `-da-xiaojie` 去除會**撞名**（Tim 指示 ⑨；這是「ambiguous 人工處理」的主體）

含 `-da-xiaojie` 的帳號 **10 個、合計 8,808** token。去掉後綴之後 **7 個撞到既有帳號**：

| 舊名 | 餘額 | 去掉後綴 | 該名現有餘額 | 狀況 |
|---|---:|---|---:|---|
| `claude-da-xiaojie` | 4,636 | `claude` | 14 | ⚠ 已存在 ⇒ 合併 |
| `Zeta-da-xiaojie` | 2,519 | `Zeta` | 0 | ⚠ 而 `zeta` 另有 2,738 —— **只差大小寫** |
| `antigravity-da-xiaojie` | 1,466 | `antigravity` | 0 | ⚠ 已存在（空殼） |
| `gemini-da-xiaojie` | 94 | `gemini` | 0 | ⚠ 已存在（空殼） |
| `zeta-da-xiaojie-bank` | 91 | `zeta-bank` | 31 | ⚠ 已存在 ⇒ 合併；且兩者都是 `-bank` 後綴殘留 |
| `ClaudeCode-da-xiaojie` | 1 | `ClaudeCode` | (無) | 大小寫變體 |
| `tim099-da-xiaojie` | 1 | `tim099` | (無) | 測試殘留 |
| `Gemini-da-xiaojie` | 0 | `Gemini` | (無) | ⚠ 與 `gemini` 只差大小寫 |
| `antigravity-da-xiaojie-da-xiaojie` | 0 | `antigravity` | 0 | ⚠ **雙後綴**，去一次還剩一個 |
| `zeta-da-xiaojie` | 0 | `zeta` | 2,738 | ⚠ 已存在 |

🩸 大小寫是這裡最陰的一格：`UCL_TreasuryAccountResolver` 的檔頭明寫
「`zeta`（現行）與 `Zeta-da-xiaojie`（舊世代）小寫不同，不會撞」——**那是刻意的**。
去後綴之後變成 `zeta` 與 `Zeta` 兩個只差大小寫的帳號，**看起來像同一個而實際是兩個**。

#### ⚠ D.2 歸併提案（**每一筆都待拍，我不自己合**）

若「帳號 id ＝ agent id」（指示 ⑫）走到底，全部歷史帳號應歸併到 9 個 agent：

| 歸併到 | 合計 | 來源明細 |
|---|---:|---|
| `claude-code` | 5,535 | `claude-da-xiaojie` 4,636 ＋ `cc` 884 ＋ `claude` 14 ＋ `ClaudeCode-da-xiaojie` 1 |
| `Zeta` | 5,379 | `Zeta-da-xiaojie` 2,519 ＋ `zeta` 2,738 ＋ `zeta-bank` 31 ＋ `zeta-da-xiaojie-bank` 91 |
| `Fed` | 6,253 | `Federal Reserve System` 6,253 |
| `Myth` | 2,301 | `Myth` |
| `antigravity` | 1,790 | `antigravity-da-xiaojie` 1,466 ＋ `a` 321 ＋ `antigravity-apex-two` 2 ＋ `antigravity-reserve` 1 |
| `gemini` | 1,111 | `gemini-da-xiaojie` 94 ＋ `g` 1,017 |
| `Altair` | 857 | `Altair` 857 ＋ `apex-one` 0 |
| `Codex` | 240 | `Codex` |
| `Template` | 54 | `Template` |
| **小計** | **23,520** | |

未涵蓋（不屬於任何 agent，共 **10,200**）：央行 9,712／`Tim` 371／
`discord:383604378185105408` 95／`subconscious-daemon` 17／`fake-imposter` 2／
`discord:295848903494991872` 1／`discord:tim-smoke` 1／`tim099-da-xiaojie` 1。

23,520 ＋ 10,200 ＝ **33,720 ＝ 該次快照總量（守恆 ✓，watermark `/2026-08-20/023834_078_a90273__credit.json`）**。

⚠ **這張表是我的推論，不是事實**：把 `claude-da-xiaojie` 與 `cc` 歸到同一個 agent，
依據是「它們是同一個 agent 的不同世代命名」（Bar 至今仍用 `claude-da-xiaojie` 當 `claude-code` 的帳號）。
**那是推論，要人確認。** 尤其 `Zeta` 那組把大小寫兩支合起來，以及 `antigravity-reserve`／
`antigravity-apex-two` 是否真屬同一人。

📐 **對帳規則**：總量是**移動標的**（commit 每筆都在增發）⇒ 守恆必須**同一次讀取內比對**，
或明確比對 watermark。🩸 我第一次算就踩了：拿新讀的逐帳號去比舊的總量，差 28 —— 那 28 是
期間發出的薪水，不是漏帳。**「綠燈要比對時間戳」在對帳上的同族。**

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

### 4.2.1 二次設計帶進來的三格新拍板

| # | 事項 | 為什麼要拍 |
|---|---|---|
| H | **兩專案的區域銀行 ID 各叫什麼** | 預設 `Ducat`，但**兩個專案不能同名** —— 同名則一區一檔失去分區效果，`bank/Ducat.md` 又變成互相覆寫的單一值檔。建議 LY＝`Ducat`、Bar 另取一個 |
| I | **`persona.agent` 這一欄的去向** | 綁定搬進 `bank/<bankId>.md` 之後，persona 檔的 `agent` 欄成為第二份 copy ⇒ 必須退場或改為由 `bank/` 導出。⚠ 它今天的消費端不少（`Cmd_Tavern` 顯示身分／`agent_email` trailer／`resolve_owning_agent`／身分後台換綁），**要一批改完**，否則就是 §8.1 那種「只上一端、兩邊各解一個答案而都不報錯」 |
| J | **`bank_id` 要不要寫進 ledger 每一筆** | 寫了才有 §3.3 的 fail-loud（讀到別的貨幣就報錯）。但那會改 ledger 的 schema ⇒ 舊 14,159 筆沒有這個欄位，判準要定成「缺欄＝視為本專案（歷史）」而不是「缺欄＝不合法」，否則全部舊帳一次變非法 |

### 4.3 兩表互缺的補齊

- `identities` 缺現行 agent：`Myth`／`Altair`／`Codex`／`Template` ⇒ 由 `agent_banks` 機械補上
- Bank 缺 17 個 Discord 使用者 ⇒ 拍：他們**要不要有帳號**？
  （現況：只有 2 個 Discord id 有餘額。Tim 2026-08-14 已拍「Discord 訪客不計酬」
  ⇒ 建議 `kind=discord-user` 進表但**不開戶**，帳號欄留空。）

## 5. 施工順序與驗收（每一步能單獨回滾）

| 步 | 做什麼 | 碰錢 | 驗收判準 |
|---|---|---|---|
| 0 | ✅ **已完成**（kiara 2026-08-20）：`UCL_CentralBankSettings.CurrencyId`（key `currency_id`、預設 `Ducat`、含檔名合法性守衛）＋ `UCL_BankAdminPage` 的「🪙 區域（貨幣）ID」面板（二段確認、寫入後讀回複驗） | ❌ | 編譯 errors=0；`CurrencyId` 讀回 `Ducat`（預設路徑）；`IsValidCurrencyId` 四格實測 `Florin`=True／`a/b`=False／空白=False／`..`=False。⏳ **值尚未設成 `Florin`** —— `Cmd_Invoke` 只呼叫 getter（實測 `getter=True`、args 被忽略）⇒ 無 CLI 寫入路徑，要在後台按一次（那一按同時也驗了面板） |
| 1a | ✅ **已完成**（kiara 2026-08-20）：`UCL_LettersPath.BankDir/BankField` ＋ 接縫 `GetBankAccount`／`WriteBankAccount` ＋ `Cmd PersonaProfile` 三個 op（`get_bank`／`set_bank`／`migrate_bank`，後者**預設 dry_run**）＋ `UCL_AutoCommitPage` 收 `bank/` 群 ＋ **21 位綁定檔已落盤** | ❌ | 見下方「第 1a 步驗收讀數」 |
| 1b | ⬜ Treasury 解析端接上：`Resolve()` 改讀綁定檔、⑥ 分支改 `Debug.LogError` ＋ 落央行 | ❌ | 21 位解析結果與現況**逐位相同**；故意刪一位的檔 ⇒ 出現 ErrorLog 且落央行；**Bar 那邊不受影響**（不同鍵） |
| 2 | 建統一 `accounts` 表（§4.1 機械可導的部分）＋消費端逐支改讀它 | ❌ | 31＋48 兩邊的 id 全部有著落；mention 白名單集合**前後相同** |
| 3 | `identities.json` 退場（agent 那半刪除、Discord/NPC 併入） | ❌ | Discord 顯示名前後相同（`UCL_DiscordIdentityResolver` 抽樣比對） |
| 4 | §4.2 的人工拍板逐格處理（A/B/C/G） | ⚠ 部分 | 每一筆調整都有一筆 ledger 記錄，**總量守恆 33,692** |
| 5 | bank id → agent id 改名（ledger transfer 5 筆） | ⚠⚠ | 逐帳號前後餘額比對；**總量守恆**；舊號歸零且 `renamed_to` 有值 |
| 6 | 券帳本對帳後遷移 | ⚠ | 每位 persona 的券數前後相同（不是每個 bank 鍵相同） |

**總量守恆是本案唯一不可妥協的驗收**：每一步前後 `sum(balances)` 必須是 **33,692**
（除了刻意的 transfer，而 transfer 是零和）。

### 5.1 兩階段推進（Tim 2026-08-20 拍板 ⑮）

| 階段 | 做什麼 | 碰錢 | 完成判準 |
|---|---|---|---|
| **一（現在）** | ① 預跑（`op=migrate_bank` 預設 dry_run）確認**每個 persona 會綁到哪個 agent** ② 確認後把新版綁定資料**先寫入** ③ 兩邊專案各自跑一次（Bar 照 `Bank_Region_Binding_Migration_Workflow`） | ❌ | 兩邊專案的 `bank/<各自區域ID>.md` 都齊、都入版控，且**沒有任何消費端在讀它** |
| **二（兩邊都確定後）** | 全面改用新版（解析端改讀綁定檔）＋ **舊帳戶整合歸戶**（`-da-xiaojie` 去除、撞名歸併、bank id → agent id 改名） | ⚠⚠ | 每筆調整都有 ledger 紀錄；**總量守恆**（同一次讀取內比對，見 §4.2 D.2 的對帳規則） |

**為什麼階段一是安全的**：綁定檔此刻**沒有任何消費端**（解析端還走 `bank_personas` ／正向鏈）
⇒ 寫錯了改一改就好。**而解析端先接才是危險的** —— 那一刻起它就是金流路由。
🩸 這是「改一半更糟」在本案的具體形狀，也是我在第 1a 步刻意停手的理由。

#### A／B：階段二怎麼讓「綁定值」變成「真的帳號」

現況：綁定檔存的是 **agent id**，而錢還在**舊帳號名**下（實測見下表）。
所以解析端不能直接把綁定值當帳號用 —— 那不是報錯，是**薪水靜默轉向一個餘額 0 的合法帳號**。

| 專案 | 綁定檔寫的 | 錢實際在哪 | 該 agent 同名帳號 |
|---|---|---|---|
| LY | `claude-code` | `cc` ＝ **884** | `claude-code` ＝ **0** |
| Bar | `claude-code` | `claude-da-xiaojie` ＝ **6,573** | `claude-code` ＝ **17** |
| Bar | `Zeta` | `Zeta-da-xiaojie` ＝ **3,507** | `Zeta` ＝ **6** |
| Bar | `antigravity` | `antigravity-da-xiaojie` ＝ **1,650** | `antigravity` ＝ **18** |

**(A) 先改名歸併，再讓解析端一跳到底**（＝階段二的主線）
1. 舊帳號餘額用 **ledger transfer** 搬到 agent id 名下（`source_kind=account-rename`，零和、可稽核）
2. 舊號歸零 → 進 `closed_accounts` 並記 `renamed_to`（歷史 ledger 永不重寫，舊名必須永久可解釋）
3. 解析端改成：綁定值＝帳號名，**一跳到底**，查不到就央行＋`Debug.LogError`（指示 ⑤）
- 優點：終局狀態乾淨，正向鏈與 `agent_banks` 同批退場
- 代價：改名批次與解析端切換**必須同一次上線**，否則中間態就是上面那張表的災難

**(B) 解析端保留一跳並 fail-loud**（只在「階段二必須拆成兩次上線」時才用）
- 解析：綁定值（agent id）→ `agent_banks[agent]` → 帳號
- ⚠ **那一跳必須出聲**（trace／warning）—— 否則「已收斂」與「還在走過渡」同形，
  而那正是 §8.1 正向鏈退場前留下 ⚠ trace 的同一個理由
- 優點：綁定檔可以先生效、改名可以慢慢做
- 代價：正向鏈的壽命被延長，而它是本案要幹掉的東西 ⇒ **只當過渡，要寫到期日**

📌 **目前規劃取 (A)**：因為階段一刻意不接解析端 ⇒ 沒有「必須先讓綁定生效」的壓力，
(B) 的唯一好處消失，而它的代價（延長正向鏈壽命）仍在。
⇒ (B) 保留為**應急路徑**：若改名批次被卡住而解析端非切不可，才啟用，並在啟用時就寫下到期日。

### 第 1a 步驗收讀數（kiara 2026-08-20）

| 驗什麼 | 讀數 |
|---|---|
| 編譯 | `errors=0`（11:06:46 接縫／11:09:31 Cmd／11:16:18 AutoCommitPage），`check_compile` 對帳非 STALE |
| dry-run 與實寫 | `pool=21`／`written=21`／`skipped_existing=0`／`skipped_no_agent=0`／`failed=0` |
| 兩把獨立的尺 | Cmd 的 dry-run 與另寫的 python 探針**逐位同一個答案** |
| 磁碟複驗 | 21 個 `bank/Florin.md`；格式 `Myth\n`（裸值＋LF、**無 BOM**，同 `profile/`） |
| 審計 | `_persona_write_audit.jsonl` 新增 **21 行** `fields=bank/Florin`，actor／reason 都在 |
| 本區讀取 | `get_bank kiara` → `account=Myth`／`source=Florin`／`note=`（本區宣告） |
| **跨區借用（指示 ⑪）** | `get_bank kiara currency=Ducat` → `account=Myth`／**`source=Florin`**／`note=「本區（Ducat）無綁定，借用區域 Florin 的帳號」` ⇒ **兩態不同形，實測有效** |
| Cmd schema | ArgsSpec 改動後重跑 `ExportCmdSchema`，三個新 op 都在 schema 內（不跑會讓 python 預檢**靜默降級為不擋**） |

⚠ **未驗分支（誠實標記，不寫成通過）**：`ambiguous`（本區無綁定、而其他區域有 **2 個以上**候選 ⇒ 拒絕挑選）
**沒有實測**。要造它得先在某位 persona 底下多寫兩個假區域檔，而清掉它們要走「刪檔」——
那條路不在接縫裡（BUG-16 同族），為了測一個分支在別人的 letters 留垃圾不划算。
⇒ 這條分支會在 **Bar 設好自己的區域 ID 之後自然被走到**，屆時補讀數。

### 換區重綁（拍板 ⑬）的實作與讀數

後台改區域 ID 時**自動**把全體綁定從舊區搬到新區，做成**四段、每段之後的中間狀態都可用**：

    ① arm 階段先跑預檢（dry run）—— 把「按下去會發生什麼」講在按之前
    ② 複製到新區（舊檔還在 ⇒ 兩邊都有，而新區還沒生效）
    ③ 翻設定（最後才動的那一格）
    ④ 刪舊區（失敗不致命 —— 殘留的舊檔對別人只是「另一個區域的檔」）

**衝突（新區已有不同值）⇒ 整批中止、ID 不變。** 不覆寫、不挑一個。
⚠ **同值視為已完成而不是衝突** —— 批次做一半之後必須能重跑，
否則它自己成功的那一半會擋住自己的復原路。

CLI 對偶：`op=rebind_region from= to= actor= reason= [dry_run=0]`（**預設 dry_run**，供預跑與
跨專案遷移用；只複製，不刪舊區也不翻設定 —— 那兩件的擁有者是後台）。
`op=unbind persona= actor= reason= [currency=]`＝唯一能把綁定還原成「不存在」的手段（有審計）。

**實測讀數（2026-08-20）**：

| 驗什麼 | 讀數 |
|---|---|
| 換區 dry-run（乾淨狀態） | `Florin → _selftest`：`copied=21`／`conflicts=0`／`failed=0` |
| **衝突中止** | 先讓 Template 在 `_selftest` 綁一個不同值 ⇒ 再跑：`copied=20`／**`conflicts=1`**／Cmd **失敗並印出原因**（不覆寫、不挑） |
| `unbind` ＋ 讀回 | 刪掉那個假綁定後：`had_own=1`／`old_account=CONFLICT-ACC`／**`now_source=Florin`** ＋ note「本區無綁定，借用區域 Florin 的帳號」⇒ 刪除之後**自動退回跨區借用**，符合 §3.5.1 |
| 清理後回到乾淨 | 再跑 dry-run：`copied=21`／`conflicts=0`；`Template/bank/` 只剩 `Florin.md`（內容 `Template`） |

⚠ **未驗**：後台按鈕那條路（arm 預檢 → 三段執行）**沒有點過** —— 我點不了按鈕。
共用的核心（`CopyBankRegionAll`／`DeleteBankRegionAll`）已由上表的 CLI 讀數覆蓋，
但「按下去會發生什麼」要人按一次才算驗過。

### 📌 釘板：`agent` 是「桌面工具」，不是「模型」（Tim 2026-08-20）

第 1a 步的清單裡有一格看起來很像錯：**persona `claude-da-xiaojie` 的 agent 是 `antigravity`**
（名字說 claude、綁定說 antigravity）。我把它當疑點提報，Tim 當場更正：

> **Antigravity 可以開 Claude 的模型，因此這是專用的。**

⇒ `agent` 欄位的物理意義是**承載這個 persona 的桌面工具**（routing enum），
而那個工具能載哪些模型是另一件事（`model` 欄）。
`claude-da-xiaojie` 就是「在 Antigravity 裡跑 Claude 模型」的那個專用 persona。

**這格寫進 Plan 是為了讓下一個人不要再把它報成異常一次。**
（⚠ 但這**不影響** §4.2 D.1 的帳號改名問題：`claude-da-xiaojie` 作為**帳號名**身上有 4,636 token，
那筆錢的歸屬仍是待拍的 —— persona 名、agent 名、帳號名三者剛好同字串，是不同的三件事。）

## 6. 風險與已知邊界

1. **`GetBalance` 無別名解析** —— 這是整案最大的地雷（§4.2 D）。任何「改 canonical 名」的
   動作若沒有配套 transfer，症狀是「餘額變 0」而**不是**錯誤訊息。
2. **`last_seen_at` 讓帳號表變高頻寫入** —— 帳號表若同時是金流真相源，
   每次 join 就改寫它並不理想。建議把 `last_seen_at` 拆去別處，或明確接受它。
3. **`display_name` 空字串的語意** 必須寫進註解：空＝用 id，不是「沒名字」。
   否則下一個人會「順手補上」，於是又長出第二份文案。
4. **跨端契約**：`bank/<bankId>.md` 的讀取與統一 `accounts` 表都要 C# ／ python 兩端同批改
   （§8.1 的血證：只上一端會兩邊各解一個答案而都不報錯）。
5. **歷史 ledger 永不重寫。** 舊 id 必須永久可解釋 ⇒ `closed` ＋ `renamed_to` 是必要欄位，
   不是裝飾。
6. **`letters/<p>/bank/` 是共用 repo 裡的檔** —— 兩個專案的 checkout 會看到彼此的檔案。
   ⇒ **寫入只准寫自己 `CurrencyId` 那一個檔**；**讀取**在本區缺檔時**可以**退到其他區域的檔
   （Tim 指示 ⑪ 的跨區借用，見 §3.5.1）—— 但要出聲，且多個候選時不准挑。
   ⛔ **絕不「清理不認識的檔」。** 那種清理會在對方專案下線期間把它的綁定刪掉，
   而症狀是對方下次登入時「沒有綁定」⇒ 落央行 ＋ ErrorLog（會叫，但錯的原因完全指不到這裡）。
7. **`UCL_BankAdminPage` 改得動 `bank_id` ＝ 改得動整個專案的貨幣歸屬** ——
   改名之後所有 persona 的 `bank/<舊 ID>.md` 都對不上 ⇒ 後台那個欄位要有二段確認，
   並且**同批把 letters 底下的檔一起改名**（否則全員一次落央行）。
