---
title: 廢棄 AwakenInit/personas — 必要欄位遷進 letters/<persona>/，路由欄留中央
slug: persona-registry-retirement
status: analysis-only（2026-08-18 Tim 要求先分析；**尚未拍板、尚未施工**）
created_at: 2026-08-18T13:55:00Z
created_by: calli
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
> ⚠ 本文是分析，不是施工單。所有「建議」都等拍板。

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

### 3.1 `letters/<persona>/_persona.json` —— 身分欄的新家

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

### 3.3 待拍板的一個小決定

`identity_vector` 的跨 persona 比較怎麼辦（三個選項，本見習生偏 B）：
- **A**：接受降級 —— 只比 letters 在手的人，且回報「掃了 N/21 位」。
- **B**：中央只存 `identity_hash`（`awakening.py:2178` 已經在算 hash 了），
  近鄰查詢先用 hash 篩、要精算才讀對方的 letters。
- **C**：vector 不搬，留中央 —— 但它是體積大戶，等於 91% 沒搬成。

## 4. 遷移分期

| 期 | 做什麼 | 為什麼這個順序 |
|---|---|---|
| **0** | 收斂讀寫接縫：`persona_profile.py` + `UCL_PersonaProfile.cs`，32 支消費端全走它 | 🚧 **施工中（summit 2026-08-19）**：接縫兩端已落地（`_lib/persona_profile.py`＝pool_names/get_raw/iter_raw/get_routing/get_identity/load_personas_into；`UCL_PersonaProfile.cs`＝PoolNames(dir-mtime 快取)/GetRaw/GetString/GetInt）。已遷：C# ChatTavernIO／RelationshipIO／Cmd_LoginStatus／LoginStatusPage／PersonaInspectorPage；python agent_email／agent_model／registered_mail／mbti。**未遷**：C# 寫入端 PersonaAgentAdminPage、TreasuryAccountResolver／BankAdminPage、ChatTavernPersonaCardAsset／AdminPage、Cmd_GoodMorning／AwakeningService 內部讀；python awakening.load_registry（本身是 py 端次接縫，Phase 1 在它與 persona_profile 之間拉 lazy migration）、session_common、check_letters_layout、sync_letters_gitignore、tavern_catchup.resolve_owning_agent、bank_resolver（吃 reg dict，隨 load_registry 走） |
| **1** | 雙寫雙讀：寫新家、讀優先新家；讀到舊家時**印一行帶呼叫端**的 log 進 `AwakenInit/_persona_access.log` | 這是唯一能證明「還有誰在讀舊檔」的手段（§5） |
| **2** | 觀察期（建議 ≥ 一週、且要跨過一次全 persona 登入＋一次晚安＋一次發薪） | 消費端不是每天都跑；只跑一天證明不了 |
| **3** | log 乾淨後移除舊路徑分支，`personas/` 從 code 裡消失 | 到這一步才叫廢棄 |
| **4** | 刪檔，**備份靠 git tag 不靠留在樹裡**（§5.3） | — |

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

1. `bank_resolver` 對全 21 位都解得出 bank，**且在故意把某人 letters 移走的情況下仍然解得出**（證明路由不依賴 letters）。
   ⚠ 兩端（python `bank_resolver` 與 C# `UCL_TreasuryAccountResolver`）**各驗一次** —— 同一條路由兩份實作，
   只驗一端＝驗了安全的那半（summit 2026-08-19 補）。
2. `wake_brief` 的 §0 血統、§6.5 關係、§6 見林三段**都有實際讀數**，不是空狀態文案。
3. 登入回傳檔的 `wake_count` / 見林 gap 與磁碟推導一致（BUG-4 的兩條對帳仍在）。
4. `_persona_access.log` 在完整一輪（登入→晚安→發薪→後台頁全開一次）之後**零筆**。
5. `git diff` 一筆 persona 身分變更**只有那幾行**（BUG-6 定案的副產物）。

## 8. Tim 補充方向（2026-08-19 口頭，summit 記錄 —— 修訂 §3 的目標配置）

> 本節是**方向拍板**，細部規格仍待定案。與 §3 衝突之處以本節為準。

### 8.1 錢的綁定留專案層，且**反轉登記方向**（修訂 §3.2）

bank 資訊**各專案不同**，不隨 persona 走。而且不再是「persona 記自己屬於哪家 bank」，
改成**銀行系統登記「本 bank 下有哪些 persona」**：

- 銀行帳戶設定各自帶 `personas: []` —— 反向索引，錢的歸屬由銀行端宣告。
- 允許空清單（央行、系統帳戶等特殊情況）。
- **驗證必做**：同一 persona 出現在兩家 bank ⇒ fail-loud（錢進錯帳戶是最貴的靜默錯）。
- **防呆（Tim 2026-08-19 二輪拍板）**：persona 沒被任何 bank 登記時（理論上不會發生），
  **預設綁央行**（央行一定存在，錢不會掉地上），並**觸發酒保系統通知**要求改綁 ——
  fallback 要出聲，不出聲的 fallback 就是下一個平行宇宙。
- persona→agent 綁定仍留專案層（commit trailer／顯示歸屬用）——「說話認 persona、錢認 bank」自此兩條線各自獨立，不再經 agent 中轉推導。

### 8.2 身分欄改「一欄一檔」分散式 .md（修訂 §3.1 的單一 `_persona.json`）

參考 `letters/<persona>/cmd/` 的形態：**新增專用資料夾，檔名＝欄位、內文＝值**。

- 資料夾名：**`profile/`**（Tim 2026-08-19 二輪拍板定案）—— 與 cmd／fragments／wakes／relationship 同層同慣例。
- 例：`profile/layer_role.md`、`profile/forked_from.md`、`profile/created_at.md`。
- 純量欄＝內文即值（無 frontmatter）；`identity_vector` 內文為 JSON 陣列。
- `vector_history` **單檔**（`profile/vector_history.md`，Tim 拍板）—— 歷史靠 git，不拆快照檔。
  📝 順帶備忘：vector_history 目前**沒有讀回機制**（只有寫入端＋Inspector 顯示），
  要不要做讀回另案優化，本案只搬不加功能。
- 好處是把 BUG-6 的解推到底：**一筆欄位變更的 diff 就是那一個檔**，且欄位間永無序列化器互踩。

### 8.3 欄位按「綁不綁專案」分家（新增判準，疊在 §2 的消費端判準之上）

| 歸屬 | 欄位 | 落點 |
|---|---|---|
| **綁專案** | bank 歸屬 | 銀行系統反向登記（§8.1） |
| **綁專案** | `agent` / `actual_agent` / `model` | 專案層路由表（本專案的桌面工具配置，換專案可能不同） |
| **不綁專案** | `layer_role` / `forked_from` / `fork_lineage` / `forked_at` / `created_at` / `identity_vector` / `vector_history` / **`email`** | `letters/<persona>/profile/`（§8.2）—— email 是個人信箱，Tim 2026-08-19 拍板進 persona 層；trailer 取用時缺檔走 agent 預設 fallback |

### 8.4 向下相容策略（Tim 2026-08-19 二輪拍板 —— 修訂 §4 的 Phase 1 雙寫）

**不做雙寫，做 read-through lazy migration**：

1. `AwakenInit/personas/` **保留一段時間**（唯讀舊源，不刪不改名）。
2. 存取時：**有對應新資料（profile/ 該欄檔存在）⇒ 新資料為準**；
   沒有 ⇒ **當場跑 migration 把舊資料遷成新檔**，之後就走新的。
3. ⇒ 遷移是逐 persona 逐欄「被用到才發生」，不需要一次性大遷移腳本；
   §5 的執行期 log 改記「lazy migration 觸發了誰的哪一欄」——
   一段時間後 log 歸零＝活資料都遷完了，剩下的才是真正沒人用的，Phase 3 再收。
4. ⚠ 寫入端規則不變：新值寫 profile/，**絕不回寫舊 personas/**（舊源只出不進，
   否則兩邊都是活的，BUG-6 的形狀換個位置重演）。

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

### 8.6 寫入接縫規格（紅隊 basecamp seq 12274 ④ 開的一槍，2026-08-19 記錄 —— 待實作）

寫入端（建人／fork／換綁／欄位更新）動工時的形狀約束：

- **讀取端可以是「查得到就好」，寫入端不行** —— 寫入接縫**強制帶 `actor` 與 `reason`**
  （必填參數不是 optional）：建人／fork／換綁出錯時的症狀都是「資料看起來很正常」，
  沒有 actor 欄位就只能靠 git blame 猜是哪支工具寫的。
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
B 是離線備援。⇒ 實作待辦：① C# 新 Cmd（回 profile 資料＋刷新快照）② 快照檔
（C# 只寫：reload／每次 Cmd／寫入端動作後）③ python persona_profile 改「Cmd → 快照」
兩段 fallback，退快照時要出聲標示資料時效。原三案分析留檔備查：
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
