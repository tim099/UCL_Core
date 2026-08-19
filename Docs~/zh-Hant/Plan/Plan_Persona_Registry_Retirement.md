---
title: 廢棄 AwakenInit/personas — 必要欄位遷進 letters/<persona>/，路由欄留中央
slug: persona-registry-retirement
status: **Phase 0-1 ＋ §8.1 已完工**（2026-08-19）；Phase 2 觀察期進行中，Phase 3-4 未動
created_at: 2026-08-18T13:55:00Z
created_by: calli
last_updated: 2026-08-19
builders: [summit（Phase 0／§8.5-8.7）, kiara（Phase 1／§8.1／消費端收斂）]
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Letters_Dir_Layout.md | letters 目錄分層 | 目標落點的既有慣例
  - ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md | Relationship 系統 | 「資料放人的資料夾而不是系統的資料夾」同一條理路
  - repo:AgentCommands/BugReports/reports/0004.md | BUG-4 | 見林書籤被蓋回（本案「可推導欄該刪不該搬」的血證）
  - repo:AgentCommands/BugReports/reports/0006.md | BUG-6 | 兩個序列化器輪流整檔重寫 persona json
---

# 廢棄 `AwakenInit/personas` — 必要欄位遷進 `letters/<persona>/`

> **一句話**：23 個欄位裡**只有 11 個有真消費端**；其中 4 個是「別人要查的路由欄」不能搬進 letters，
> 7 個是「我是誰」該搬；剩下 12 個是可推導的快取或根本沒人讀的死欄 —— **那 12 個要刪，不是搬。**
>
> ⚠ **本文已從分析轉為施工紀錄**（2026-08-19）。§1-§3 是 calli 的原始分析，
> **方向以 §8 的拍板為準**；各期進度見 §4 的分期表（✅／🚧／⬜ 三態）。
> 未完成的部分一律標 ⬜ 並寫明「為什麼還沒做」—— 只寫「待辦」的清單三天後就沒人看。

## 0. 先講量出來的數字（掃過的，不是估計）

| 量 | 值 | 怎麼量的 |
|---|---|---|
| persona 檔 | **21** 個（`AwakenInit/personas/*.json`） | 目錄實掃 |
| 欄位種類 | **23** 種 | 21 檔欄位聯集 |
| 內容體積 | ≈ 59,465 字元 | `json.dumps` 逐檔累加 |
| ↳ 自傳／身分欄佔 | **91.3%** | 同上，按欄分桶 |
| ↳ 路由欄佔 | 2.2% | 同上 |
| ↳ 活體狀態欄佔 | 6.5% | 同上 |
| 碰到這批檔的程式檔 | **32** 支（14 py / 18 cs） | 路徑符號實掃（見 §1） |
| `letters/` 底下目錄 | **30** 個 —— 其中 **9 個沒有對應 persona 檔** | 兩邊集合相減 |
| `letters/<p>` 是自己 repo 的 | **7 / 21**（apex-one·basecamp·calli·gura·kiara·Sirius·summit） | 逐目錄看 `.git` |

⚠ 那 9 個幽靈目錄是 `GawrGura` `MoriCalliope` `TakanashiKiara` `Tim` `apex` `basecamp0512`
`cross-agent` `mit` `tavern-keeper` —— **所以「掃 letters 目錄」不能當 persona 名單**，
任何以它為輸入的遷移腳本會撈到 9 個不存在的人。

## 1. 消費端盤點 —— 32 支程式碰這批檔

按「拿走 personas/ 之後會怎麼壞」分類，不按目錄分類。

### 1.1 錢與路由（拿掉會算錯帳，且**必須不依賴 letters 有沒有 checkout**）

| 檔 | 用途 | 讀哪欄 |
|---|---|---|
| `_lib/bank_resolver.py` | persona→agent→bank（薪資／扣款的唯一解析） | `agent` |
| `canvas.py` / `mbti.py` / `freetime.py` / `dice.py` | 扣款、寄信、發薪前反查 bank | `agent` |
| `git_commit.py` / `agent_email.py` / `agent_model.py` | commit trailer（`agent@persona(model) <email>`） | `agent` `model` `actual_agent` `email` |
| `registered_mail.py` / `_lib/session_common.py` | 收件人解析 / session 共用 | `agent` |
| `Tools/tavern_catchup.py` | 顯示發言者所屬 agent | `agent` |
| `UCL_TreasuryAccountResolver.cs` / `UCL_BankAdminPage.cs` | C# 端同一套 bank 解析 | `agent` |

🩸 `bank_resolver.py` 檔頭已經寫著 footgun：用只載 meta 的 loader 會讓**每個 persona 都拋
`PersonaResolutionError`**（summit + kaguya 2026-07-21 撞出）。⇒ 路由欄一旦散進 21 個 letters 目錄，
「某人的 letters 沒 clone」就等於這條錯誤重演，而且是在扣款路徑上。

### 1.2 身分／顯示（拿掉會少印東西，不會算錯帳）

| 檔 | 用途 | 讀哪欄 |
|---|---|---|
| `wake_brief.py` | §0 身分卡（血統 fork from …） | `forked_from` |
| `awakening.py` | fork 建人、lineage 查詢、vector 近鄰、status 報表 | `identity_vector` `fork_lineage` `forked_from` `forked_at` `layer_role` `wake_count` |
| `Cmd_GoodMorning.cs` | 自介訊息前半的系統欄 | `wake_count` `layer_role` |
| `Cmd_LoginStatus.cs` / `UCL_LoginStatusPage.cs` | 登入狀態頁 pool 列表 | `layer_role` `last_active` `status` `wake_count` |
| `UCL_PersonaInspectorPage.cs` | 全欄位檢視（含 vector_history / last_session_keys） | 幾乎全部 |
| `UCL_PersonaAgentAdminPage.cs` | 建 persona／fork／換綁 agent／同步角色卡 | 幾乎全部（**寫入端**） |
| `UCL_ChatTavernPersonaCardAsset.cs` / `UCL_ChatTavernAdminPage.cs` | 角色卡 ↔ persona 對應、孤兒卡偵測 | 檔名 + `layer_role` |
| `UCL_ChatTavernIO.cs` | persona pool id 集合（inbox 分流） | **只要檔名**，不讀內容 |
| `UCL_RelationshipIO.cs` | 關係對象名的次要來源 | 只要檔名 |

📌 `UCL_RelationshipIO.cs:104` 的註解已經寫著：
「🥈 過渡期的次要來源：AwakenInit/personas —— Tim 2026-08-18 說它之後會遷進 letters。」
⇒ 遷移意圖已經在 code 裡留了記號，本案是把它做完。

### 1.3 只碰路徑不碰內容（遷移時改一處就好）

`_lib/ucl_paths.py`（`personas_dir()` / `persona_file()` 的唯一解析點）、
`_lib/tavern_paths.py`（`PERSONAS_DIR` 委派上者）、
`UCL_AwakeningService.cs`（`PersonasDir` / `ResolvePersonaFile`，C# 側唯一解析點）、
`UCL_AgentCommandsPath.cs`、`UCL_AutoCommitPage.cs`（commit 範圍 `AwakenInit/` 前綴）、
`_lib/affinity_manager.py`（legacy，自己拼了 `REPO_ROOT/AgentCommands/AwakenInit/personas` —— **唯一一個沒走解析點的**）。

⇒ 好消息：**兩端各已有唯一解析點**，路徑遷移不必動 32 支。壞消息：`affinity_manager.py` 那條寫死的要一起收。

## 2. 欄位必要性判定（23 欄）

判準只有一句：**「拿掉它，哪個消費端會壞？」** —— 沒有消費端的欄位不叫資料，叫殘留。
（命中數＝非註解行的靜態命中；標 ⚠ 的表示同名詞污染，已人工複核。）

### 2.1 必要・路由欄 —— 留中央（4 欄，2.2% 體積）

| 欄 | 命中 | 為什麼不能只住 letters |
|---|---|---|
| `agent` | 137 ⚠ | 錢的入口（bank 由它推）；且要**跨全體**查（agent→persona 反查、多數決） |
| `model` | 56 ⚠ | commit trailer + brief 抬頭；`agent_model` 要跨 persona 多數決推 agent→型號 |
| `actual_agent` | 33 | email／model 解析的 key；lock 也要它 |
| `email` | 25 | commit trailer；只有 2 位有 override，其餘吃 agent 預設 |

### 2.2 必要・身分欄 —— 建議搬 `letters/<persona>/`（7 欄，91.3% 體積）

| 欄 | 命中 | 備註 |
|---|---|---|
| `layer_role` | 10 | 登入頁／自介抬頭／角色卡同步都讀 |
| `forked_from` | 14 | brief §0「血統」、lineage 工具 |
| `fork_lineage` | 12 | 鏈深、改名時的連動修正 |
| `forked_at` | 5 | lineage 顯示 |
| `created_at` | 少（registry 用途） | 36 個命中裡多數是別的檔自己的 created_at |
| `identity_vector` | 8 | ⚠ **有跨 persona 讀**：`awakening.py:2244` 拿別人的 vector 算 cosine 近鄰 |
| `vector_history` | 5 | 只有寫入端＋Inspector 顯示 |

⚠ `identity_vector` 是唯一「身分欄但需要聚合讀」的例外 —— 搬進 letters 之後
「vector 近鄰」功能會依賴所有人的 letters 都在。要嘛接受降級（只比 checkout 到的人、
**並且明講掃了幾個**），要嘛在中央留 hash（見 §3.3）。

### 2.3 可推導 —— 建議刪，不要搬（5 欄，BUG-4 的家）

| 欄 | 真相源已經在哪 | 證據 |
|---|---|---|
| `wake_count` | `wakes/` 收尾信數 +1 | C# 登入本來就採磁碟值並印「快取落後…採磁碟值」 |
| `last_consolidated_wake` | `longterm/wake_<a>-<b>.md` 檔名 | **BUG-4**：快取停在 12 而磁碟已到 23 → 假 OVERDUE。兩端 2026-08-18 已加對帳 |
| `last_consolidated_at` | 同上檔的 `consolidated_at` frontmatter | 同上 |
| `status` | `_session/_persona_<p>.json`（lock） | 登入路徑自己寫著「registry status=online 但查無 lock ⇒ 以 lock 為準」 |
| `last_active` | lock `locked_at` ／最近訊息 | 目前純顯示用（3 個頁面讀） |

⇒ **搬快取＝多一個會被 checkout 回滾的地方。** BUG-4 今天證明的就是這件事：
一個沒有磁碟對帳的快取，落後時看起來跟正常值一模一樣（12 不像壞值，0 才像）。

### 2.4 沒有消費端 —— 建議直接不遷（6 欄）

| 欄 | 狀態 |
|---|---|
| `availability` | **只有寫入端**（`awakening.py set-availability` + 登入/登出）；讀它的派工功能（T06.1 Plan_Standby_Dispatch_Bartender）從沒接上 —— 酒保端 0 命中 |
| `last_session_keys` | 只有 `UCL_PersonaInspectorPage` 顯示，**沒有任何寫入端** ⇒ 歷史殘留（5 位有） |
| `relogin_count` | 非註解命中 **0**（2 位有） |
| `persona_spec` | 命中 **0**（1 位有） |
| `narrative_role` / `narrative_note` | 命中 **0**（各 1 位有） |
| `worldlines` | 唯一命中是 `wake_brief.py:342` 讀 **`letters/<p>/worldlines/` 目錄** —— 不是這個欄位 |

## 3. 目標配置

### 3.1 `letters/<persona>/_persona.json` —— 身分欄的新家　⛔ **已被 §8.2 取代（留檔備查）**

> ⛔ **不要照本節施工。** Tim 2026-08-19 拍板改「一欄一檔」（`profile/<field>.md`），
> 已於 Phase 1 落地。本節保留是為了看得出方向改過 —— 而不是留一份看起來還能用的舊規格。

理由：`identity_vector` 是 64 維數字陣列，markdown frontmatter 表達它只會變難讀難改；
letters 底下已有先例（`bookshelf/reader.json` 是機器真相、`.md` 是人可讀投影）。

```
letters/<persona>/
  _persona.json      ← 身分欄真相源（machine-owned）
  _persona.md        ← 選配：人可讀投影（機械生成，改 json 後重生）
  _constitution.md   ← 既有
  longterm/ fragments/ wakes/ relationship/ …
```

### 3.2 `AwakenInit/_registry_meta.json` 的 `persona_routing` —— 路由欄的新家

**不是「留著 personas/ 只放 4 欄」，而是把 4 欄併進既有的 meta 檔**，於是 `personas/` 這個目錄可以整個退場：

```json
{
  "agent_banks": { "...": "..." },
  "persona_routing": {
    "calli": { "agent": "Myth", "model": "claude-opus-5", "actual_agent": "ClaudeCode" },
    "basecamp": { "agent": "Claude", "model": "…", "email": "…" }
  }
}
```

好處三個：
1. **persona pool 名單有了權威來源** —— `persona_routing` 的 key 集合。解掉 §0 那 9 個幽靈目錄的問題
   （現在名單是「掃 21 個檔名」，遷移後如果改成掃 letters 目錄會變 30 個）。
2. 錢的路徑**一次讀一個檔**，不依賴任何 letters checkout（§1.1 的 footgun 從結構上消失）。
3. 21 檔 → 1 檔，`save_registry` 那個「寫一個 persona 卻重寫全部 21 檔」的行為（今天實測波及
   basecamp / gura 兩個無關檔）自然消失。

### 3.3 ~~待拍板的一個小決定~~ —— ✅ **已由 §8.2 拍板解掉（選 C 的變體）**

> Tim 2026-08-19 拍板：`identity_vector` 與 `vector_history` **整份搬進** `profile/`
> （structured 欄、內文為 JSON），中央不留 hash。
> ⇒ 下面三選項作廢；**跨 persona 比較會退化成「只比 letters 在手的人」（即選項 A 的代價）**，
> 而那個代價目前沒有消費端在付 —— `vector_history` 連讀回機制都還沒有（§8.2 備忘）。
> 真的要做近鄰查詢再另案，別在本案裡順手加功能。

`identity_vector` 的跨 persona 比較怎麼辦（三個選項，本見習生偏 B）：
- **A**：接受降級 —— 只比 letters 在手的人，且回報「掃了 N/21 位」。
- **B**：中央只存 `identity_hash`（`awakening.py:2178` 已經在算 hash 了），
  近鄰查詢先用 hash 篩、要精算才讀對方的 letters。
- **C**：vector 不搬，留中央 —— 但它是體積大戶，等於 91% 沒搬成。

## 4. 遷移分期

> 狀態圖例：✅ 完工並實測 ／ 🚧 進行中 ／ ⬜ 未動（附「為什麼還沒做」）

| 期 | 狀態 | 做什麼 | 進度與實測讀數 |
|---|---|---|---|
| **0** | ✅ | 收斂讀寫接縫，消費端全走它 | summit 2026-08-19。`UCL_PersonaProfile.cs` ⇄ `_lib/persona_profile.py`；§8.7 A+B 快照；§8.6 寫入審計（actor+reason 必填） |
| **1** | ✅ | read-through lazy migration：identity 欄搬進 `letters/<p>/profile/`（一欄一檔） | kiara 2026-08-19。**21/21 人已遷**；`_field_sources` 分布 **profile 150 / absent 18 / legacy 0**；round-trip **168 格 0 不一致**；legacy identity 合併 sha1 `95f8a615…` **遷移前後逐字相同** |
| **2** | 🚧 | 觀察期（≥ 一週、且要跨過一次全 persona 登入＋一次晚安＋一次發薪） | **2026-08-19 起算**。§8.4 的收斂判準已改成「`source=legacy` 的欄數歸零」，而它**在遷移當天就歸零**（見上）⇒ 觀察期要看的不再是「遷完了沒」，而是**消費端會不會拿到舊值**（見 §4.1 的殘留清單） |
| **3** | ⬜ | 移除舊路徑分支，`personas/` 從 code 裡消失 | 卡兩件：① §4.1 還有消費端直讀 legacy ② pool 名單的新真相源（`persona_routing` 的 key 集合）要等 Phase 2 之後才切（summit 拍板：Phase 1 期間 `PoolNames` 不動） |
| **4** | ⬜ | 刪檔，備份靠 git tag 不靠留在樹裡（§5.3） | 等 Phase 3 |

### 4.1 Phase 1 之後仍直讀 legacy 的消費端（Phase 3 的前置清單）

> ⚠ **這些今天都不出錯**，因為 legacy 從不被回寫（`FreezeLegacyIdentity` ＋ python 對偶
> `_freeze_legacy_identity`）⇒ legacy 的值＝遷移那一刻的值，實測全庫 168 格與 `profile/` 逐格相同。
> **它們會在「有人改過 profile/」之後才開始給錯答案**，而那時沒有任何一格會紅。
> 🩸 那不是假想：2026-08-19 Tim 設 kiara 的 email（落在 `profile/`），
> 而當時 `agent_email.load_persona` 直讀 legacy ⇒ **commit trailer 掛的是舊信箱**
> （`4c0f568` 之前兩筆已成既成事實，不可改）。

| 消費端 | 狀態 | 備註 |
|---|---|---|
| C# `Cmd_LoginStatus` / `LoginStatusPage` / `PersonaInspectorPage` / `AgentEmailRegistry` | ✅ 走接縫 | Phase 0 |
| C# `PersonaAgentAdminPage` 建人／fork 來源 | ✅ 走接縫 | kiara `705b6ae`。直讀會**複製到舊值** ⇒ 生一個帶過期血統的孩子 |
| python `agent_email.load_persona`（＝`agent_model` / `git_commit` / commit-msg hook 的共同瓶頸） | ✅ 走接縫 | kiara `4c0f568`。一處改對四處跟著對 |
| python `awakening.load_registry` | ✅ 走接縫 | kiara `f8807c5`（Tim 拍板：早安流程本來就走 Cmd，資料由 Cmd 供給；備援只要支援 brief） |
| python `check_letters_layout` / `sync_letters_gitignore` | ✅ 走 `pool_names()` | kiara `705b6ae`。只讀名單不讀 identity，改的理由是**判準漂移不會有人喊痛** |
| python `_lib/session_common` | ⬜ | 讀 routing／bank（那幾欄留 legacy 是對的，§8.3）⇒ 正確性上不痛，但入口該統一。收之前要確認呼叫時機是否在 Cmd 內（需 `UCL_PP_SKIP_CMD=1`） |
| python `tavern_catchup.resolve_owning_agent` | ⬜ | 同上：讀 agent 歸屬，不碰 identity |
| C# `UCL_TreasuryAccountResolver` / `UCL_BankAdminPage` | ⬜ **刻意擋著** | §8.1 反向登記已落地（見那節），但這兩支的**讀 persona 檔**那部分要跟正向鏈退場一起收，先改只會做一半 |

清單單號：`repo:AgentCommands/BugReports/reports/0018.md`（BUG-18，doc 類，附每項「為什麼今天不痛」）。

### 4.2 Phase 1 的附帶落地（不在原分期表裡）

- **遷移產物入版控**：`UCL_AutoCommitPage` 新增 `profile/` 群（kiara `277483e`，預設勾、在線者不勾）。
  理由：`profile/` 是**別人的讀取觸發**生成的，落地時該 persona 通常不在線 ⇒ 沒有人會 commit 它，
  而**身分現在住在那裡** —— 沒進版控等於「這個人是誰」只存在一台機器上。
- **python 寫入端防護**：`awakening.save_registry` 加 `_freeze_legacy_identity()`（C# 對偶），
  剝掉接縫推導欄（`_source` / `_snapshot_at` / `_field_sources`）並把 identity 按磁碟原值釘回；
  `cmd_rename_persona` 另加 `assert_legacy_write_effective()` 守衛（kiara `b91c995`）。
- **rename 必須搬 `letters/`**（§8.2 的連動，summit 拍板）：`289eae6`。
  獨立 git repo 直接擋下並印手動 SOP —— 改名動到 `.gitmodules` 是版控結構變更，工具不代拍。
- **結構值欄的寫入通道**：`op=set` 依欄名決定型別（`1f89740`）—— 見 §8.2 的補充。

### 4.3 已知缺口（有單，不擋 Phase 2）

| 單 | 內容 |
|---|---|
| BUG-16 | `op=set` 無法把欄位還原成 **absent**（三態的第三態寫不出來）⇒ 唯一復原是手動刪檔＝繞過審計。建議 `op=unset` |
| BUG-17 | 接縫 module 被同一行程**載入三份**（awakening / agent_email / wake_brief 各自 `spec_from_file_location`）⇒ 不帶 `UCL_PP_SKIP_CMD` 時是 3 次 Cmd 往返 |
| BUG-18 | §4.1 那份殘留清單本身 |
## 5. 「改資料夾名備份起來，看還有誰在讀」為什麼**驗不出來**

這是本案最重要的一段，因為它是直覺的反面。

### 5.1 半數消費端撞到「目錄不見了」是**靜默**的

實掃 §1 的消費端，寫法長這樣的有一票：

```csharp
if (Directory.Exists(PersonasDir))          // UCL_LoginStatusPage / UCL_BankAdminPage /
    foreach (var pf in Directory.GetFiles(...))   // UCL_TreasuryAccountResolver / UCL_ChatTavernIO …
```
```python
if not d.is_dir(): return out                # agent_model.py / registered_mail.py / affinity_manager.py
```

⇒ 改名之後它們**回空集合、繼續跑、不報錯**：pool 列表變空、bank 解析退命名慣例 fallback、
persona 名單少一半。**看起來全綠。** 這正是 BUG-4／BUG-5 同一族的失敗形狀。

### 5.2 有效的三種偵測（建議三個都做）

| 手法 | 抓得到什麼 | 抓不到什麼 |
|---|---|---|
| **靜態證明**：接縫做完後 grep 全樹，`personas_dir\|PersonasDir\|AwakenInit/personas` 應只剩接縫本身 | 所有寫在 code 裡的路徑 | 反射／字串拼接／外部腳本 |
| **執行期 log**（Phase 1 的 `_persona_access.log`，記時間＋呼叫端） | 真的被跑到的舊路徑讀取 | 觀察期內沒被跑到的路徑 |
| **毒藥檔**（保留目錄，內容換成 `{"_deprecated_read_via":"persona_profile"}`） | 「讀了但不檢查欄位」的呼叫端會**當場拿到空值而不是舊值** | 只判目錄／檔案存在性的呼叫端 |

⚠ **毒藥檔比改名安全**：改名讓「檢查存在性」的呼叫端靜默退化；毒藥檔讓「真的讀欄位」的呼叫端拿到
明顯錯誤的值（空 agent → bank 解析當場報錯，而不是安靜地少列幾個人）。
**一個更精確的失敗比一個模糊的成功更能證明事情發生了。**

### 5.3 備份不要留在樹裡

「改資料夾名留著」＝樹裡多一份看起來合法的舊資料。今天修的 BUG-5 就是這個病：
`_lib/ucl_paths.py` 的鏡像留在樹裡、內容落後一天，`import` 失敗被 fail-soft 吞成「沒有資料」。

⇒ 備份用 **git（commit + tag，例如 `personas-retire-baseline`）**；
若非要留在樹裡，名字必須讓「讀到它就一定壞」（例如 `_personas.retired.20260818/` 前綴 `_` 已被
letters 慣例用來標「機械產物／不要當人寫的檔」，這裡要更狠 —— 副檔名改掉，讓 `*.json` glob 撈不到）。

## 6. 風險與未決

| # | 事項 | 現況 |
|---|---|---|
| 1 | 只有 7/21 有自己的 letters repo | 其餘 14 位搬過去仍在 AgentCommands 內，**同一份資料兩種家**；但 §3.1 的落點對兩者都成立（路徑一致），只是 commit 邊界不同 |
| 2 | 7 個 letters submodule 的寫入會變頻繁 | 登入寫 `wake_count` 那類欄若照 §2.3 刪掉，寫入頻率其實**降低**；若照搬則 7 個 submodule 每次登入都 dirty（Tim 的每晚 bump 工作量 ×7） |
| 3 | `identity_vector` 跨 persona 比較 | §3.3 待拍板 |
| 4 | BUG-6（兩個序列化器輪流整檔重寫） | ✅ 已解（2026-08-19 `43e2144`）：canonical＝ToJsonBeautify 形狀，python 走 `dump_registry_json` 唯一出口 |
| 5 | `affinity_manager.py` 寫死路徑 | ✅ 已消滅（2026-08-19）：affinity 殘留全清時整支刪除，不必收進接縫 |
| 6 | 見叢舊記錄「6 個 letters repo 是 detached HEAD」 | **2026-08-18 實測 7 個全在 `master` 且有 remote** —— 那條記錄已過期，遷移前不必先修 detached |

## 7. 驗收標準（施工時照這條驗，不驗「有沒有報錯」）

> 狀態（kiara 2026-08-19 逐條對）：①✅ ②⬜ ③✅ ④➖判死改判準 ⑤✅

1. `bank_resolver` 對全 21 位都解得出 bank，**且在故意把某人 letters 移走的情況下仍然解得出**（證明路由不依賴 letters）。
   ⚠ 兩端（python `bank_resolver` 與 C# `UCL_TreasuryAccountResolver`）**各驗一次** —— 同一條路由兩份實作，
   只驗一端＝驗了安全的那半（summit 2026-08-19 補）。
2. `wake_brief` 的 §0 血統、§6.5 關係、§6 見林三段**都有實際讀數**，不是空狀態文案。
3. 登入回傳檔的 `wake_count` / 見林 gap 與磁碟推導一致（BUG-4 的兩條對帳仍在）。
4. ~~`_persona_access.log` 在完整一輪之後**零筆**~~ ➖ **判死改判準**（§8.4）：
   那支 log 已被 §8.6 的 `_persona_write_audit.jsonl` 取代（summit 拍板 Q3）。
   等價判準改成 **`_field_sources` 裡 `source=legacy` 的欄數歸零**，遷移當天即達成
   （legacy 0 / absent 18 / profile 150）。⚠ 但那**不代表消費端都跑在新結構上** ——
   那件事的清單在 §4.1，Phase 2 觀察期要看的正是它。
5. `git diff` 一筆 persona 身分變更**只有那幾行**（BUG-6 定案的副產物）。
   ✅ 實測：Tim 設 kiara 的 email ⇒ 變更只落在 `profile/email.md` 一個檔。

逐條狀態：

| # | 狀態 | 讀數 |
|---|---|---|
| ① 兩端各解 21 位 | ✅ | python parity 0 不一致；C# SelfTest 62 通過 0 失敗。**letters 移走仍解得出**：反向表住 `_registry_meta.json`（專案層），不依賴 letters checkout —— 這正是 §8.1 反轉方向的附帶好處 |
| ② `wake_brief` 三段有實際讀數 | ⬜ | 未逐段對過。Template brief 生成正常（255 行）、kiara wake#15 brief 626 行有內容，但**沒有逐段核對「不是空狀態文案」** —— Phase 2 觀察期要補 |
| ③ wake_count／見林 gap 與磁碟一致 | ✅ | kiara wake#15 登入回傳檔 gap 5/10、書籤換算 0→10 有印出來；Template 反覆跑不膨脹 |
| ④ access.log 零筆 | ➖ | 判死改判準，見上 |
| ⑤ 一筆變更只有那幾行 | ✅ | 見上 |

## 8. Tim 補充方向（2026-08-19 口頭，summit 記錄 —— 修訂 §3 的目標配置）

> 本節是**方向拍板**，細部規格仍待定案。與 §3 衝突之處以本節為準。

### 8.1 錢的綁定留專案層，且**反轉登記方向** —— ✅ **已實作**（kiara 2026-08-19，兩端各驗；唯一未做的一格見本節末）

bank 資訊**各專案不同**，不隨 persona 走。而且不再是「persona 記自己屬於哪家 bank」，
改成**銀行系統登記「本 bank 下有哪些 persona」**：

- 銀行帳戶設定各自帶 `personas: []` —— 反向索引，錢的歸屬由銀行端宣告。
- 允許空清單（央行、系統帳戶等特殊情況）。
- **驗證必做**：同一 persona 出現在兩家 bank ⇒ fail-loud（錢進錯帳戶是最貴的靜默錯）。
- **防呆（Tim 2026-08-19 二輪拍板）**：persona 沒被任何 bank 登記時（理論上不會發生），
  **預設綁央行**（央行一定存在，錢不會掉地上），並**觸發酒保系統通知**要求改綁 ——
  fallback 要出聲，不出聲的 fallback 就是下一個平行宇宙。
- persona→agent 綁定仍留專案層（commit trailer／顯示歸屬用）——「說話認 persona、錢認 bank」自此兩條線各自獨立，不再經 agent 中轉推導。

#### 實作與驗收（kiara 2026-08-19）

- **資料**：`_registry_meta.json` 的 `bank_personas`（bank → persona 清單）＋ `_bank_personas_note`
  把規矩寫在資料旁邊。初值**從現況逐位導出** ⇒ day-1 不改變任何人的錢（commit `5394fae1e`）。
  空清單六個（Codex／央行／tavern-keeper／三個舊世代 `-da-xiaojie`）正是本節說的「允許空清單」；
  **已銷戶帳號刻意不列** —— 不接受金流的帳戶不該有人掛在下面。
- **解析**：python `bank_resolver.resolve_persona_bank_reverse` ＋
  C# `UCL_TreasuryAccountResolver` 的 `bank_personas` 載入（commit `f4d823f`）。
  ⚠ **兩端刻意同一筆 commit** —— 這是 two-end contract，只上一端的後果是
  同一個 persona 在兩邊解到不同 bank，而**兩邊都不會報錯**。
- **撞名＝拒絕解析，不挑一個**：python `raise PersonaResolutionError`；
  C# 回 `Unresolved` 並在 Trace 列出所有衝突 bank。
  理由是代價不對稱：錢進錯帳戶不會有人喊痛，而挑一個就是替它做決定 ——
  **寧可停在看得見的 unresolved，也不要進看不見的錯帳戶**。
- **過渡期退正向鏈不准安靜**：python 印 stderr、C# 的 Trace 加 `⚠` 並寫明「此人尚未登記」。
  否則「反向表漏一位」與「反向表已完整」在報告裡長得一模一樣。

驗收讀數（§7 要求兩端各驗，兩端都驗了）：

| 端 | 讀數 |
|---|---|
| python（21 位逐位） | parity（反向 vs 改動前正向）**0 格不一致**；覆蓋率 **0 位需退正向鏈**；故意雙掛 kiara ⇒ raise 並列出 `['Myth','cc']`；`resolve('KIARA')` → `Myth` |
| C#（`UCL_TreasuryAccountResolver.SelfTest`） | **✅ 全數通過 62 ／ ✗ 0**；③ 段每位 trace 為 `persona X → bank Y（§8.1 反向登記）`—— **沒有 agent 那一跳**，證明走的是新路；⑥ 唯一 ⚠ 是既有的 `claude-da-xiaojie` 撞名（正式帳號優先，行為未改） |

> 📌 撈 C# SelfTest 報告的方法留給後人：`Cmd_Invoke` **不會印回傳值** ——
> 用它自己的 `storeAs=st` 存起來，再 `Invoke System.IO.File.WriteAllText`
> 以 `args=<路徑>;$st` 把字串寫成檔來讀。沒有繞路，用的是它宣告過的功能。

#### ⬜ 唯一未做的一格：未登記 persona 的央行 fallback ＋ 酒保通知

本節上面那條防呆（沒被任何 bank 登記 ⇒ 預設綁央行＋觸發酒保通知）**尚未實作**，
而且**刻意還沒做**：現況是退正向鏈（會出聲），而正向鏈末端 `resolve_bank_account`
對未知 agent 會 derive `{agent}-da-xiaojie` ⇒ **央行 fallback 永遠不會被觸發**。
現在寫它就是加一段沒有消費端的 code —— 而「沒人跑過的路配上最壞的時機」
正是本案從頭到尾在殺的形狀（同 §8.7 那條「不留沒人驗過的後路」）。

⇒ **它該跟正向鏈退場（Phase 3）一起做**。前置：python 端還沒有央行常數
（C# 有 `UCL_CentralBankSettings.DefaultCentralBankAccount`）—— 要做得先補對側，
否則又是一組兩端各講一套的常數。

### 8.2 身分欄改「一欄一檔」分散式 .md —— ✅ **已實作**（kiara 2026-08-19，Phase 1）

參考 `letters/<persona>/cmd/` 的形態：**新增專用資料夾，檔名＝欄位、內文＝值**。

- 資料夾名：**`profile/`**（Tim 2026-08-19 二輪拍板定案）—— 與 cmd／fragments／wakes／relationship 同層同慣例。
- 例：`profile/layer_role.md`、`profile/forked_from.md`、`profile/created_at.md`。
- 純量欄＝內文即值（無 frontmatter）；`identity_vector` 內文為 JSON 陣列。
- `vector_history` **單檔**（`profile/vector_history.md`，Tim 拍板）—— 歷史靠 git，不拆快照檔。
  📝 順帶備忘：vector_history 目前**沒有讀回機制**（只有寫入端＋Inspector 顯示），
  要不要做讀回另案優化，本案只搬不加功能。
- 好處是把 BUG-6 的解推到底：**一筆欄位變更的 diff 就是那一個檔**，且欄位間永無序列化器互踩。

#### 實作補充：型別由**欄名**決定，不由值的長相決定（summit 2026-08-19 拍板 A）

「內文即值」對純字串成立，但實測 21 人的型別分布不只字串：
`forked_from` / `forked_at` 是 str×14 ＋ **null×7**、
`fork_lineage` / `identity_vector` / `vector_history` 是 list×21。
⇒ 三類判準寫死在接縫（`STRUCTURED_FIELDS_ORDER` 在 C#、快照帶出 `structured_fields` 給 python，
**對側不准另立一張表**）：

| 類 | 欄 | 編碼 |
|---|---|---|
| structured | `identity_vector`／`vector_history`／`fork_lineage` | 內文＝JSON 陣列；寫入時**逐元素驗形狀**（數字／物件／字串），parse 或形狀失敗 **fail-loud，絕不退存字串** |
| nullable scalar | `forked_from`／`forked_at` | 空檔＝`null`（全庫**沒有空字串的這兩欄**，編碼與現存資料不衝突） |
| scalar | `layer_role`／`created_at`／`email` | 內文即值；**長得像 JSON 也不猜** |

⚠ 「看起來像 JSON 但被存成字串」是這條路唯一的死法（讀回型別不對，下游做數值運算才炸，
離現場很遠）—— 焊死在接縫裡（commit `1f89740`）。
⚠ 空陣列 `[]` 是**合法值**（21 人的 `fork_lineage` 全是 `[]`）；空字串則明確擋下並提示
「空陣列請顯式給 `[]`」—— 空字串與空陣列是兩件事，不猜。

📌 `vector_history` 的「沒有讀回機制」那條備忘仍然成立（本案只搬不加功能）。

### 8.3 欄位按「綁不綁專案」分家（新增判準，疊在 §2 的消費端判準之上）

| 歸屬 | 欄位 | 落點 |
|---|---|---|
| **綁專案** | bank 歸屬 | 銀行系統反向登記（§8.1） |
| **綁專案** | `agent` / `actual_agent` / `model` | 專案層路由表（本專案的桌面工具配置，換專案可能不同） |
| **不綁專案** | `layer_role` / `forked_from` / `fork_lineage` / `forked_at` / `created_at` / `identity_vector` / `vector_history` / **`email`** | `letters/<persona>/profile/`（§8.2）—— email 是個人信箱，Tim 2026-08-19 拍板進 persona 層；trailer 取用時缺檔走 agent 預設 fallback |

### 8.4 向下相容策略 —— ✅ **已實作**（kiara 2026-08-19；歸零判準已精確化，見末）

**不做雙寫，做 read-through lazy migration**：

1. `AwakenInit/personas/` **保留一段時間**（唯讀舊源，不刪不改名）。
2. 存取時：**有對應新資料（profile/ 該欄檔存在）⇒ 新資料為準**；
   沒有 ⇒ **當場跑 migration 把舊資料遷成新檔**，之後就走新的。
3. ⇒ 遷移是逐 persona 逐欄「被用到才發生」，不需要一次性大遷移腳本；
   §5 的執行期 log 改記「lazy migration 觸發了誰的哪一欄」——
   一段時間後 log 歸零＝活資料都遷完了，剩下的才是真正沒人用的，Phase 3 再收。
4. ⚠ 寫入端規則不變：新值寫 profile/，**絕不回寫舊 personas/**（舊源只出不進，
   否則兩邊都是活的，BUG-6 的形狀換個位置重演）。


#### 實作紀錄與判準精確化

- **觸發條件是「存取」不是名單**（Tim 2026-08-19 追加拍板：「只要嘗試存取舊資料就會觸發
  該 persona 的 migration 並改用新資料」）。施工中曾加過一道白名單閘（為了鐵律二
  「真人不當白老鼠」），Template ＋ kiara 走完全流程後**已拆除**（commit `deadc65`）。
  拆之前另做**全庫預檢**：21 人 × 150 格 encode→decode 模擬，零損失。
- **合併層落在 C# `GetRaw` 內部**（summit 拍板 Q1）⇒ 32 支消費端一支都不用改，
  且 `WriteSnapshot` 走同一入口 ⇒ **python 端不需要知道 Phase 1 存在**。
  ⚠ 但 `WriteSnapshot` 走 `GetRaw(iAllowMigrate:false)`：**批次匯出不是消費端存取** ——
  讓 domain reload 的快照重寫去遷移，等於把「誰真的被用到」這個訊號抹掉。
- **`_field_sources` 記三態 `profile / legacy / absent`**（summit 拍板 Q5）：
  只遷「legacy 真的有 key」的欄；**不生空檔** —— 那會讓從來不存在的欄長出看似有資料的空檔。
  ⇒ **歸零判準因此精確化**：不是「log 歸零」，是 **`source=legacy` 的欄數歸零**；
  `absent` 不擋收斂、也不假裝遷過。實測遷移當天即 **legacy 0 / absent 18 / profile 150**。
- **審計而非另開 log**（summit 拍板 Q3）：`actor=lazy-migration` 進既有
  `_persona_write_audit.jsonl`，**`_persona_access.log` 判死**（§4 舊文提的那支，
  在 §8.6 誕生前寫的，已被取代）。唯讀舊源命中**不進 audit** —— 那不是寫入，
  它的顯形由 `_source` 標記＋stderr 承擔，別讓審計檔混讀取噪音。
- **Editor 未開時不遷移**（summit 拍板 Q4）：讀舊源並帶 `_source` 標記，
  **python 永不寫 `profile/`** —— 一旦開這個口，「寫只走接縫」就破了，而且是最難抓的破法。
  連帶：python tier-3 `local-parse` 在 Phase 1 之後讀不到 `profile/` 新值，**刻意不修**
  （給 python 長 profile/ 解析器＝第二解析器還魂）；`_source=local-parse` 已宣告「可能舊」。
### 8.5 「現在狀態」欄帶著消費端回歸 —— ✅ 已完成（summit 2026-08-19；前置的 presence 收斂＋過期機制移除亦已落地）

§2.4 把 `availability` 判死的理由是**沒有消費端**；Tim 拍板把「現在狀態」概念加回來，
而且這次先給消費端再給欄位：

- **欄位**：`now_status`＋`status_updated_at`（lock 內；已實作）。
- **寫入實作**：`Cmd_Tavern op=post` 的可選參數 `status` → `UCL_AwakeningService.UpdateNowStatus`
  （Tim 拍板：整合進發訊息 —— 通知同事跟改狀態是同一個動作）；`ucl-coding` skill 已加
  「寫 code 前先廣播」導引。
- **落點**：session 層（lock 檔旁或 lock 內），**不進 profile/ 也不進 git** ——
  它是活體狀態，與 `status`/`last_active` 同族（§2.3 的判定不變：lock 是活體真相源），
  登出即滅，不會有 checkout 回滾問題。
- **寫入**：走 Cmd 單一通道（lock 擁有者是 `UCL_AwakeningService`，維持硬規則三）；
  開工／換工作時自己更新一句；goodmorning intro 可順手設初值。
- **消費端（本次回歸的存在理由）**：`tavern_catchup` / `ucl-ding` 的在線清單從
  「🟢 summit」升級成「🟢 summit — 改 Cmd_FreeTimeActivity（3 分鐘前）」——
  **正在被改的 code 看得出是誰在改**。
  🩸 實案：2026-08-18 calli 的 commit 抓走 summit 編輯中的檔（BugReports wake#57 四隻之四）——
  當時她若看得到這行狀態就不會撞。
- **staleness 要顯示**：狀態帶時間戳，catchup 印「多久前」；過舊的狀態比沒有狀態更會誤導。
- **前置：在線狀態收斂單一 API** —— ✅ **已完成（summit 2026-08-19，C# recompile 0 錯＋python 實跑驗過）**：C# 端 5 個直掃點全改走 `UCL_ActivePersonaLocks`（`ListLocks`／`ListOnline`／`LockedNames` 三視圖，SessionDir 改走可 override 的 DataRoot）；python 端收斂到 `awakening.list_locks()`／`list_online()`／`find_locks_by_claim_origin()`，tavern_catchup 三處跟進。⚠ 收斂時發現**兩端過期語意分岔待拍板**：缺 `expires_at` 的 lock，C# 視為未過期、python `is_lock_expired` 視為過期（影響 goodnight 守衛，不敢靜默翻，已在兩端註解留記號）。原始實掃紀錄 ——
  「誰在線」目前**至少八處各自掃 lock**：C# 有 `UCL_ActivePersonaLocks`（7 處在用）
  但另有 5 檔自己 `Directory.GetFiles` 直掃（LoginStatusPage / PersonaAgentAdminPage /
  PersonaInspectorPage / DiscordGatewayClient / Cmd_LoginStatus）；python 端 `awakening.py`
  內部 4 處 glob、`tavern_catchup.py` 再自己掃 3 處。
  🩸 散裝的代價當天就有讀數：run_cmd 的身分推論兩次把 summit 誤判成 basecamp（僅留痕未擋），
  跟 catchup 的在線清單各講各話。
  ⇒ `now_status` 動工前先收斂：C# 全部走 `UCL_ActivePersonaLocks`、python 收成一支
  `_lib/presence.py`（或 awakening 的 `list_online()`）—— 否則新欄位要在八個掃描點各加一次，
  等於再鋪一層散裝。

### 8.6 寫入接縫 —— ✅ 已實作（summit 2026-08-19，Template 寫入三連實測全過）

寫入端（建人／fork／換綁／欄位更新）動工時的形狀約束：

- **讀取端可以是「查得到就好」，寫入端不行** —— 寫入接縫**強制帶 `actor` 與 `reason`**
  （必填參數不是 optional；空值 fail-loud 不寫）。
- 實作：`UCL_PersonaProfile.WriteRaw`（整檔，建人也走）／`SetField`（單欄 patch）＋
  審計 `AwakenInit/_persona_write_audit.jsonl`（append-only，ts/persona/fields/actor/reason）＋
  每筆寫入後自動刷新 §8.7 快照。Cmd 介面：`PersonaProfile op=set`（python/工具寫入路徑）。
- 已收編六個寫入端：AwakeningService（morning patch／收尾信 wake_count 對齊／goodnight offline）、
  AgentEmailRegistry.SavePersonaOverride（簽名改為必帶 actor/reason）、
  AdminPage（換綁 SetField／建人 fork WriteRaw）。
- Template 驗收：帶 actor 寫入成功（審計落行＋快照跟上）；**缺 actor 被擋 exit=2 且值未落地**；
  清回原值成功。
- 紅隊另兩洞已修（同日）：C# 補 GetRouting/GetIdentity 讓欄位分類兩端都是編譯器可找到的東西；
  Exists 與 PoolNames 對齊 _/. 前綴判準（兩個「有沒有這個人」判準不得給不同答案）。
- email 欄歸位：初版錯放 routing，已依 §8.3 拍板移回 identity 組（兩端同步）。

### 8.7 三輪補充拍板與討論題（Tim 2026-08-19，summit 記錄）

**Template 拍板（推翻先前改名提案）**：Template 是測試用 persona，**走跟其他 persona 完全一樣
的流程** —— 只有這樣才能正確測試流程本身。⇒ 不改名 `_Template.json`、接縫不排除、
pool 名單含 Template 是**正確行為**；遷移時它跟大家一起搬（letters/Template 已存在）。
測試殼的價值恰恰在於它跟真人無差別 —— 對它開特例＝測試蓋不到特例以外的路。
**且（Tim 同日追加）：本案相關功能每改好一批，先用 Template persona 實測**（登入／讀欄位／
lazy migration／寫入端）—— 真人 persona 不當白老鼠，Template 的存在理由就是這個。

**洞①延伸 —— ✅ Tim 拍板（2026-08-19 四輪）：A＋B 混合**：
python 先走 Cmd（C# 現場解析＝永遠最新，且每次 Cmd 順手刷新快照、值走 Cmd 回傳）；
**Cmd 跑不通（Editor 未開）⇒ 退讀快照**。上線期間 Editor 基本常開，所以主路徑是 A、
B 是離線備援。✅ **已實作（summit 2026-08-19，Template 三段實測全過）**：① `Cmd_PersonaProfile`（op=refresh，
回 snapshot_path/pool_count）② 快照 `AwakenInit/_persona_profile_snapshot.json`（C# 只寫：
reload delayCall／每次 Cmd／email override／換綁／建人 fork／登入 patch-write 後；gitignored）
③ python 三段 fallback：Cmd（live 無標記）→ 快照（`_source="snapshot"`＋`_snapshot_at`）→
local-parse（連快照都沒有的首次 checkout 備援，`_source="local-parse"`）；欄位分類以快照內
C# 匯出清單為準；env `UCL_PP_SKIP_CMD=1` 顯式跳過 Cmd 段。**退快照時回傳值本身要帶標記**（Tim 五輪補充）：例如附 `_source="snapshot"`
＋`_snapshot_at=<快照時間>` 推導欄（沿 list_locks 的 `_` 前綴慣例＝非本體欄位）——
讓下游拿到的資料自帶「這不是最新」，不是靠呼叫端記得看 stderr；顯示端照 now_status 慣例
換算「多久前」。⚠ 走 Cmd 成功的回傳**不帶**標記 —— 有標記＝快照，無標記＝現場值，
兩態不得同形。原三案分析留檔備查：
動機：兩端各有一份解析，改一邊忘另一邊 ⇒ 解析結果不一致。候選三條路：

| 案 | 做法 | 代價 |
|---|---|---|
| A（Tim 原案） | python 每次讀 persona 都發 Cmd 問 C# | 單一解析器 ✅；但 **Editor 沒開就讀不到**（awakening.py brief 備援、離線工具全斷）＋每讀一次一輪 RPC（wake_brief 一次讀 21 人） |
| **B（summit 推薦）** | **C# 解析後寫快照檔，python 只讀快照** —— 照路徑快照 `.agentcommands_root.local` 的成熟模式（C# 只寫不讀、reload 重寫；python 只讀不寫＋過期自癒） | 單一解析器 ✅；Editor 關著仍可讀（快照留在磁碟）；代價＝快照有時差（persona 欄位低頻變動，可接受）＋要定義過期自癒 |
| C（最小） | 兩端解析保留，但**欄位分類表由 C# 匯出**（schema 檔），python 讀表不寫死常數 | 只統一「分類」不統一「解析」；JSON 解析本身兩端都是標準庫，漂移面其實在分類與判準 |

⚠ 無論選哪案，「pool 名單判準（_/. 前綴）」「有沒有這個人」等**判準類邏輯**都該進單端 ——
B 案下判準跑在 C#、快照裡直接是結果清單，python 連判準都不用有。

### 8.8 連動備忘

券（繪圖券／未來的酒館券等）也要遷入個人資料夾＋機制統一 —— 工程較大，另立
[`Plan_Voucher_Wallet_Migration.md`](Plan_Voucher_Wallet_Migration.md) 備忘，不併入本案施工範圍。
