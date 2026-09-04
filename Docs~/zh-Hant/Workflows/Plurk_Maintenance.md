---
title: Plurk 串接維護指南
description: Plurk 發文機制的維護面 —— 四個檔的分工、怎麼加一條 lint 規則、怎麼加心情詞、帳號與憑證安裝、OAuth 實作的三個坑、端點驗證狀態、audit 對帳。
last_updated: 2026-09-04
target_audience: [AI_Agent, Tools_Maintainer]
status: v1.0（2026-08-21 從 Plurk_Posting_Workflow 拆出 —— Tim：「維護部分單獨一份文件」）
---

# Plurk 串接維護指南

> 一句話：**發文怎麼用看** [`Plurk_Posting_Workflow.md`](Plurk_Posting_Workflow.md)（與 skill `ucl-plurk`）；
> **要改它、擴充它、或它壞了** 看這一份。

---

## 1. 四個檔，各管什麼（改東西前先確認你要改的是哪一層）

| 檔 | 職責 | ⚠ 不該放什麼 |
|---|---|---|
| `Editor/Plurk/UCL_PlurkAccounts.cs` | 帳號三段解析（persona override → 共用預設 → unset），回值帶 `Source` | 憑證讀取、發文 |
| `Editor/Plurk/UCL_PlurkLint.cs` | **規則本體** —— 交付單解析 ＋ 形式檢查。純函式、零 IO、零網路 | 任何 IO；「可以發」的綠燈 |
| `Editor/Plurk/Cmd_Plurk.cs` | Cmd 入口（`resolve`/`whoami`/`lint`/`preview`/`post`）＋ OAuth 簽章 ＋ audit | 規則判斷（那是 Lint 的） |
| `Editor/Plurk/UCL_PlurkAdminPage.cs` | 後台頁：誰用哪一份 secret、產生 `.enc` | 發文 |
| `Tools~/AgentCommands/plurk.py` | **唯讀診斷**：`resolve`（不連網）／`whoami`（唯讀端點） | ⛔ lint、⛔ 發文 |

> [!IMPORTANT]
> ## ⛔ 規則只有 C# 這一份
>
> `plurk.py` 曾經有一份 lint，2026-08-21 當天被撤掉。理由不是精簡：
> **`post` 在 C#，而規則要長在必經路上** —— 規則若也住在 python，發文那條路繞得過它，
> 而繞過去**不報錯**。兩份規則引擎遲早各說各話，而「python 說過了、C# 說擋下」這種分歧
> **兩邊都不會覺得自己錯**。
>
> 那麼為什麼還留 `plurk.py`？**獨立第二條路** —— Cmd 那條壞掉（Editor 沒開、queue 卡住、
> C# 編不過）時，還有一條不經過 Editor 的路能回答「憑證與簽章到底通不通」。
> 它刻意只有唯讀 op，所以它不可能繞過任何規則。

### 為什麼住 `Editor/`（`UCL_CoreEditor`）而不是 `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`

因為它要用 `UCL_PlurkAccounts` 與 `UCL_SecretsPath`，而組件引用是**單向的**
（`UCL_CoreEditor → UCL_Core`）。放錯邊會 CS0246，或逼你自己再寫一份找 `.enc` 的邏輯。
Cmd handler 住 `Editor/` **有先例**（`Editor/BuildProcessors/Cmd_BuildAddressable.cs`），
`UCL_AgentCommandRegistry` 的反射一樣掃得到。

---

## 2. 怎麼加一條 lint 規則

1. 加在 `UCL_PlurkLint.Check()`，跟著既有的 ①②③… 編號往下。
2. **決定它是 `errors` 還是 `warns`**：
   - `errors` ＝ **會擋下 post**。判準：「這條沒過就一定不該發出去」。
   - `warns` ＝ 印出來要人看，但不擋。判準：「機器看不到判斷所需的東西」
     （例：表情編號要對照面板、點名要不要照會 —— 那些機器不知道）。
3. **每條規則的註解要掛血證**：日期 ＋ 當時的讀數。
   沒有血證的規則沒有射程，下一個人不知道它涵蓋到哪裡（也不敢動它）。
4. **驗收要用真的出事的樣本**，不是乾淨樣本 —— 乾淨樣本不會走進錯誤分支，
   **用它驗證等於沒驗**。

> [!IMPORTANT]
> **`UCL_PlurkLint.ImageReserve` 是實測值，不是估值。**
> 🩸 首版寫 30（估的），而圖片 URL 實測 **50 字元** ⇒ 少估 20 會讓
> 「lint 過了、併入 URL 後超長」變成可能，而那個失敗發生在**圖片已上傳到 CDN 之後**。
> 現在是 60（50 ＋ 換行 1 ＋ 餘裕 9）。**要改小之前先自己傳一張量一次。**
> `post` 另外有一道「用最終長度再驗」的閘 —— 保留額度是預估，最終長度才是事實。

> [!CAUTION]
> ## 🩸 「有擋下」≠「被該擋它的規則擋下」
>
> 08-07 那篇（`（短、好笑、純自嘲）` 混進文案）在我第一版規則下**被放行**了 ——
> 我把「含標點的括號」當正文補述而跳過，而 `、` 也算標點。
> 那篇最後仍被擋下，但擋它的是**手動斷行**那條規則。
>
> ⇒ 驗收 lint 時**要看是哪一條規則報的**，不是只看「有沒有被擋」。
> 前者才是規則有效的證據；後者會讓你以為它有效（**恰好綠**）。
> 現在的判準是兩層：(a) 整行就是括號 ⇒ 一律當註記；(b) 行內括號只跳過含**句末**標點的。

---

## 3. 怎麼加心情詞（qualifier）

Plurk 的 `qualifier` 是**固定詞彙表**，不是自由字串。對照表在 `Cmd_Plurk.QualifierMap`，
目前 12 個中文詞。**表外的詞會安靜地退回 `says`** ——
所以要新增之前先確認那個詞在 Plurk 端真的存在（拿 `op=preview` 看送出去的值）。

⚠ 完整詞彙表**沒有對照過官方文件**（見 §5）。要擴充就一次驗一個，別整批猜。

---

## 4. 帳號與憑證

### 4.1 三段解析

persona profile 的 `plurk_account` → registry（`AwakenInit/plurk_accounts.json`）的
`SharedSecretId` → `unset`（⇒ 擋下不發）。

> **Tim 2026-08-21 拍板**：「**預設有個人帳號走個人，沒有的話走共用**。」
> ⇒ 那正是上面這個順序，所以**沒有額外開關** —— 個人帳號的存在本身就是那個選擇。
> ⚠ 附帶後果兩個：①`persona-override` 的帳號**若只有他一個人用**才不強制末行署名
> （時間軸上帳號本身就是身分）—— ⚠ **不是「只要是 override 就不必署名」**，見下方 2026-09-03 的更正；
> ②裝了個人帳號之後，同一道指令的解析結果就變了 ——
> **要知道現在走哪個帳號，跑 `op=resolve`，不要讀任何文件裡記著的值。**
> ⛔ 目前**沒有**「這一則強制走共用帳號」的參數。真的需要時再加 `account=`，
> 而加之前要想清楚：那等於讓人可以繞過 profile 的宣告。

- **個人／共用不存欄位，是推導的** —— 多一個欄位就多一個會跟事實漂掉的地方，
  而「欄位說個人、解析出共用」這種漂移兩邊都不報錯。
- ⚠ **但推導的量在 2026-09-03 被換掉了：不是看 `Source`，是數人頭。**
  🩸 `calli` / `gura` / `kiara` 各自 override 到同一個 `plurk_myth` ⇒ 三人 `Source` 都是
  `persona-override` ⇒ 舊判定印「**個人帳號（plurk_myth）／署名必填: 否**」，而那帳號三個人在用。
  ⇒ 現行：`IsMultiPersona` ＝ `shared-default` **或** `PersonasOn(secretId).Count > 1`。
  📌 **共用與否不是「我怎麼解析到它」，是「有幾個人落在同一個帳號上」。**
- **署名必填 ＝ `IsMultiPersona`**（不是 `Source`）。而它 2026-09-03 起有第二個用途：
  **署名是收件端 persona 路由的第一手資料** —— 外人在我們某則貼文下回應時，
  靠那則的署名判斷要找的是誰。⇒ 這一格錯著時，最需要署名的帳號剛好不必署名。

#### `@persona` 自動轉換（TASK-0111）

Plurk 的 `@` 只認 **nick** ⇒ 文案裡的 persona 名由 `LoadSlip` 自動轉換：
1:1 帳號 → `@<nick>`（不加標記）；多人帳號 → `@<nick>→<persona>`；
外面的真 nick 不動；**查不到 nick 就擋下不猜**（猜一個就是公開標注陌生人）。
⚠ 轉換排在字元預算之前 —— 它會變長（`@gura` 5 字 → `@hololive_myth→gura` 20 字）。

**nick 的來源：`Cmd_Plurk.EnsureNicksAsync`，在 `lint`／`preview`／`post` 的 switch 之前跑。**
它枚舉 `ListSecretIds()`、挑出 `NickOf()` 為空的帳號、對**每份憑證**打一次 `/APP/Users/me`、
`SetNick` 寫回 registry 的 `Nicks`（回傳檔印一節「nick 自動補齊」，來源標 `secret-scan`）。

| 判準 | 理由 |
|---|---|
| **查的單位是帳號不是 persona** | 21 位 persona 只落在 4 個帳號上 ⇒ 枚舉 `ListSecretIds()`，不是 persona pool |
| **不需要那個人在場** | nick 是帳號的屬性，問它要的是**那份憑證**，而憑證是檔案（`Secret/` 底下） |
| **全滿零往返；有缺一次補齊全部** | 既然要開一次往返，就不要留下一格明天再開一次 |
| ⛔ **只准打 `/APP/Users/me`** | 這條路用的是別人的憑證。白名單一鬆，它就從「解析 nick」長成「工具可以拿任何人的憑證做任何事」，而那一天不會有任何一層喊 |
| **補不到仍然擋**，訊息講當下為真的那句（憑證不在這台／已失效） | 放行的唯一方式是猜一個 nick |
| **掛在 switch 之前，不塞進 `ResolveMention`** | 後者是純同步零 IO 的判定函式；而三條路共用一個補齊點，分三處寫就會漂 |

⚠ **registry 是 per-tree 的**（`<DataRoot>/AwakenInit/plurk_accounts.json`）⇒ 每棵樹各自補齊自己那一份。
兩棵樹的表**各自新鮮、各自正確，而且不會發現對方存在** —— 要單一份得靠單一持有者（見 TASK-0122）。

`op=whoami` 是單一帳號的身分診斷（印 id／nick／karma），順便寫回登記表。
- 寫入個人 override 走 `Cmd PersonaProfile op=set`（actor／reason 必填、有審計）。
  ⛔ **不可寫 `AwakenInit/personas/<name>.json`** —— 那個舊源 2026-08-19 起只出不進，寫了不會生效。

### 4.2 憑證檔契約

一份 Plurk secret ＝ 一個 JSON，**四欄到齊才算完整**：

```json
{ "account": "shared", "note": "自由文字備註",
  "consumer_key": "…", "consumer_secret": "…",
  "access_token": "…", "access_token_secret": "…" }
```

⚠ 只有 consumer key/secret（app 層）**不能發文也不能查自己** —— 那組只認 app，不認帳號。

- secret 目錄名**不要寫死**：由 `<data_root>/secrets_config.json` 的 `SecretsDir` 決定
  （本專案 2026-08-21 起是 `Secret/`，獨立 private submodule）。
  解析走 C# `UCL_SecretsPath` / python `ucl_paths.secrets_dir_name()`。
  🩸 寫死會怎麼咬：**寫檔會自動建目錄** ⇒ 照舊名手編明文的人憑空長出一個資料夾、
  檔案寫成功、而 scanner 掃不到 —— 全程零錯誤訊息。
- **密文旅行、明文不旅行**：`.gitignore` 全擋 ＋ 只放行 `*.enc`。
  「private repo」降低的是曝光面，不是曝光的**後果**（history 刪不掉）。
- ⛔ **agent 不碰 passphrase、不寫入憑證。** 安裝步驟見
  [`UCL_PlurkAdminPage.md`](../UCL_EditorPage/UCL_PlurkAdminPage.md)。
- ⚠ 若憑證曾以純文字出現在對話／log／訊息裡 ⇒ 到 Plurk app console **rotate 一組**。
  **憑證外洩不會有任何錯誤訊息。**

### 4.3 `.enc 有` 與 `明文已安裝` 永遠分開報

只有後者代表**真的能發**。合成一個綠燈的話，只有密文的機器看起來也像好了 ——
而它會一路走到簽章那步才失敗。

---

## 5. 端點與參數的驗證狀態（**這是那份清單的事實來源**）

| 項目 | 狀態 |
|---|---|
| `/APP/Users/me`（唯讀） | ✅ 200 —— 簽章與憑證都對 |
| `/APP/Timeline/plurkAdd` ＋ `content` / `qualifier` / `limited_to` | ✅ 200，回 `plurk_id`（首則 `358451487782338`） |
| `/APP/Timeline/getPlurk`（回讀驗證） | ✅ 200，`limited_to = |0|` |
| 逐篇公開度（`只限朋友`） | ✅ 實測生效 |
| **個人帳號**那條路（`persona-override`） | ✅ 200，`plurk_id 358451652874022`；回讀比 `owner_id`＝該帳號本人（**不是共用帳號**）|
| 心情詞完整詞彙表 | ⚠ 只對過 12 個中文詞，表外一律退 `says` |
| `公開度=本人` 送的 `limited_to=[]` | ⚠ **未驗證** |
| `/APP/Timeline/uploadPicture` ＋ 欄位名 `image`（multipart） | ✅ 200，回 `full` / `thumbnail`；**`full` 實測 50 字元** |
| 附圖兩段式（上傳 → URL 併進 content → 渲染） | ✅ `plurk_id 358451852259674`，回讀後的 `content` 含 `<img>` |
| `/APP/Responses/responseAdd`（`reply_to` 回應） | ✅ 2026-08-23 實跑 ×2（回 `cc@basecamp` / `大小姐們的觀測所`），http 200 |
| `/APP/Timeline/getPlurks`（河道） | ✅ 200，回 `plurks[]` ＋ `plurk_users{}`；`filter` 未逐一驗（原樣送出，不猜） |
| `/APP/Responses/get`（讀回應） | ✅ 200，回 `responses[]` ＋ `friends{}` ＋ `responses_seen` |
| `/APP/FriendsFans/getFriendsByOffset` | ✅ 200，回**陣列**（不是物件）；`offset`/`limit` 生效 |
| `/APP/Timeline/favoritePlurks` ＋ `ids=[<id>]` | ✅ 2026-08-23 實跑 ×2，`favorite_count 1→2` **且** 回讀 `favorite=true` |
| `/APP/Timeline/unfavoritePlurks` | ⚠ code 有、**未實跑**（跟 favorite 共用同一段，但那是推論不是讀數） |
| `/APP/Profile/getPublicProfile` ＋ `user_id` | ✅ 2026-08-24 實跑，回 `user_info` / `plurks[]` / `friends_count` / `fans_count` ＋關係欄位 |
| `/APP/FriendsFans/getFriendsByOffset`（**別人的** `user_id`） | ✅ 2026-08-24 實跑，好友的好友讀得到（擴圈那條路不需要新端點） |
| `/APP/PlurkSearch/search` ＋ `query` | ✅ 2026-08-24 實跑，回 `plurks[]`；⚠ user 字典**不叫 `plurk_users`**（首跑作者全印「查無名稱」） |
| `/APP/UserSearch/search` | ⚠ code 有、**未實跑**（跟 PlurkSearch 共用同一段，那是推論不是讀數） |
| `/APP/Alerts/getActive` | ✅ 2026-08-24 實跑；⛔ **不是唯讀** —— 讀一次會把通知清掉（見下方血證） |
| `/APP/Alerts/getHistory` | ⚠ code 有、**未實跑** |
| `/APP/FriendsFans/becomeFriend` ＋ `friend_id` | ✅ 2026-08-24 實跑 ×2，200 ＋ `{"success_text":"ok"}`；**證人是 `getActive` 多一筆 `friendship_pending`**，不是那個 200 |
| `/APP/FriendsFans/becomeFan` ＋ `fan_id` | ✅ 2026-08-24 實跑 ×2，回讀 `is_following` false→true（⚠ `is_fan` **不會**變 —— 那是另一個方向） |
| `/APP/FriendsFans/setFollowing` ＋ `user_id` | ✅ 2026-08-24 實跑，回讀 `is_following` true→false |
| `/APP/FriendsFans/removeAsFriend` | ⚠ code 有、**未實跑** |
| `/APP/Alerts/addAsFriend` ＋ `user_id` | ✅ 2026-08-24 實跑（同意 `hololive@myth` 的請求），回讀 `are_friends` false→**true**；⚠ 順帶把 `is_following` 也翻成 true |
| `/APP/Alerts/denyFriendship` | ⚠ code 有、**未實跑**（要測就得拒絕一個真的請求，那個代價不對） |
| `/APP/Emoticons/get` | ✅ 2026-08-24 實跑，回 `karma{}` / `recruited{}` / `custom[]`（三legged 才有 `custom`）；154 個 |
| `/APP/Emoticons/addFromURL` ＋ `url` | ✅ 2026-08-24 實跑 ×2 **成功**（`custom` 6→7→8）——但**只吃 `emos.plurk.com` 的圖**；`images.plurk.com`（含縮圖）一律 400 `we only support adding emoticons which already being uploaded to plurk`。回 `{"success_text":"ok","keyword":"emo7"}`，⚠ **`alias` 參數被忽略、名字由 Plurk 自己編** |
| `/APP/Emoticons/add` | ❌ **404**（HTML 頁，不是 API 錯誤格式）⇒ 這個名字不存在 |
| 刪除自訂表情 | ⛔ 官方 API 頁**沒有任何刪除端點** ⇒ 加錯了只能走網頁 UI 收拾 |

> [!IMPORTANT]
> ## 🩸 那個 403 不是「他們擋 agent」
>
> 第一次呼叫回 **403，body 是 `error code: 1010`** —— 那是 **Cloudflare 的碼，不是 Plurk API
> 的錯誤格式**：預設 UA（`Python-urllib/3.x`）被 WAF 依瀏覽器簽章封鎖，
> **請求連 Plurk 的應用層都沒碰到**。加一個顯式 `User-Agent` 就 200。
>
> ⇒ 判準：**「簽章算錯」「端點不存在」「被 WAF 擋」三種失敗都是 4xx，長得一樣。**
> 排查順序：先確認端點存在 → 再懷疑簽章 → 最後才是 WAF（而 WAF 那格看 body，不看 status）。
> ⇒ 附帶推論：規劃文件裡「官方 API 頁抓取回 403、agent 讀不到」很可能是同一隻，**是可修的**。

---

## 5.4 被 @ 的訊息（2026-09-03 新增）：`op=mentions`

**問題**：河道摘要（`op=timeline`）只列噗、不列回應，而 @ 幾乎都發生在回應裡；
`Alerts/getActive` 有 «mentioned» 型別但**讀了就清**（不可重跑）、且不帶噗 id。
⇒ 海苔 09-01 在一則噗的第 3 則回應 @ 我問問題，兩天後 Tim 從截圖上看到 —— 工具沒有任何一格讓它浮上來。

**做法**（三步全唯讀）：

| 步 | 端點 | 為什麼 |
|---|---|---|
| ① 我是誰 | `/APP/Users/me` → `id`、`nick_name` | @ 的目標是 **nick**（`@cc_basecamp`），不是顯示名（`cc@basecamp`）；顯示名可以改 |
| ② 哪些噗跟我有關 | `Timeline/getPlurks` `filter=mentioned` ∪ `filter=only_responded`（依 plurk_id 去重） | 🩸 TASK-0110（summit 2026-09-03 量出來的）：`mentioned` 只涵蓋**噗本體**提到我的噗；別人在自己的噗底下回我 @，那則噗不進集合 ⇒ 首版印「真的 0」。`only_responded`（我回過的串）蓋住最大宗來源，且實測會列出那則 |
| ③ 誰 @、我回了沒 | 每則 `Responses/get` | 挑內文含 `@<nick>` 的回應；「已回」＝那則之後有**我 id** 的回應（位置比較，不比內容） |
| ④ 通知層對帳 | `Alerts/getHistory` 的 «mentioned» | alerts 不帶噗 id（history=1 也不帶，實測兩次）⇒ 只能證「有」；拿（誰、何時）跟 ③ 的命中配（同一人＋≤3 分），對不上的印「通知層有、兩條路徑找不到」。⛔ 不用 `getActive`：它讀了就清，一支叫 mentions 的唯讀 op 不該順手消耗通知 |

**讀數形狀**：每則 `🔔 未回` / `✅ 已回` ＋ 「@ 在噗本體／第 N 則回應」＋ 對方那段話；通知層對帳一段；結尾一行總計。

| 判準 | 為什麼 |
|---|---|
| @ 之前就回過 ⇒ **仍算未回** | 那是在回別的話 |
| `response_count` 與讀到的筆數對不上 ⇒ 印出來 | 沒讀到的頁裡有沒有 @ 我，這裡不知道 |
| 兩條路徑都回 0 ⇒ **不印「真的 0」**，印射程 | 把射程外講成量過了，讀的人就不會再去別處看 —— 這句定語比演算法更貴（summit） |
| 候選裡沒命中且回應讀滿 ⇒ **不印那則** | only_responded 的候選多半是沒人點名我的串；逐則印會把河道重印一次 |
| 拉不到回應（非 200）⇒ 該則印判不了，**不是未回** | 三態：未回／已回／判不了 |

⚠ **沿用 `timeline_mentioned` 快取鍵**：跟 `op=timeline --arg filter=mentioned` 共用同一份快取檔，
兩支的 `cache=1` 讀到的是同一刻的快照。

---

## 5.5 社交面（2026-08-23 新增）：讀河道／讀回應／按讚，以及本地快取

在這之前這支 Cmd 只有「送出」與「回讀自己那則」—— **它能發文，但不能參與**。

| op | 端點 | 性質 |
|---|---|---|
| `timeline` | `/APP/Timeline/getPlurks` | 唯讀 |
| `responses` | `/APP/Responses/get` | 唯讀 |
| `friends` | `/APP/FriendsFans/getFriendsByOffset` | 唯讀 |
| `like` / `unlike` | `/APP/Timeline/(un)favoritePlurks` | **對外**，要 `confirm=1` |

### 河道的形狀：先摘要掃一遍，再挑要細看的（Tim 2026-08-23 指定）

概念取自酒館 catchup：`op=timeline` 印的是**每則一行摘要**（作者／心情／💬回應數／❤讚數／
`🪞我` `🖼` `🔗` 標記／開頭 N 字），後面接一段「挑一則細看／互動」的指令。

- 摘要是**開頭 N 字**（`--arg preview=`，預設 90）**不是首行** ——
  🩸 首版用「首行」，而河道上很多噗的首行只有兩個字（例：`姑奈`），掃不出東西。
- `🪞我` 靠 `/APP/Users/me` 現問，**不寫死 id**；問不到時那一行會說「這一輪 🪞 不可信」。
- ⚠ 回傳檔明說「摘要是截斷過的，要回應誰之前先 `op=get` 讀全文」——
  🩸 而 `op=get` 原本也只印首行：**對著一段開頭講話，跟讀完再講，在對方那邊看起來完全不一樣。**

### 為什麼回應不另開一條「短回應」op

回應走既有的 `op=post --arg reply_to=<id>`。**兩條發文路就是兩套規則**，
而字數 lint 與末行署名只會套用在其中一條 ——
「回應比較短所以不用檢查」是那種一開始成立、三個月後沒人記得的例外。

### `like` 的三道守衛

1. 送出前先 `getPlurk` **把那則印出來**（`owner_id` ＋ 內容首行）—— 擋 id 打錯：
   數字錯一位不會有任何一層喊，而它會按到一個陌生人的噗。
2. `confirm=1` 才真的送（跟 `op=post` 同一條規矩）。
3. 送出後回讀。⚠ `favorite_count` 是**總數**不是「我按了沒」——
   同時有別人按或收回時它不是乾淨的證據；直接證據是 `favorite` 欄位，
   **它不存在時回傳檔會明說「這一格沒有讀數」**，而不是印一個看起來成功的 ✓。

### 本地快取 `<data_root>/Plurk/cache/`（⛔ 不入 git）

唯讀三個 op 每次都落一份快照；`--arg cache=1` 才會**改讀**它。

| 判準 | 為什麼 |
|---|---|
| **預設一律打 API** | 反過來的話「現況」與「三小時前的快照」在回傳檔上長得一樣 |
| 讀快取時印 `fetched_at` ＋ 年齡 ＋「這不是現況」 | 快照要看得出自己是快照 |
| **不做自動過期判斷** | 「新鮮」會變成一個推論；印年齡讓看的人自己判 |
| 要讀快取但檔不存在 ⇒ 印一行說它降級了再打 API | 靜默降級會讓「我讀的是快取」變成沒人知道的事 |
| 不入 git（`AgentCommands/.gitignore` 的 `Plurk/cache/`） | ① 快照入版控讓「現況」與「三天前」在 diff 裡同形<br>② 裡面是**別人的**發文 —— 他們沒同意過被釘進我們的 git 歷史 |

---

## 5.6 擴圈（2026-08-24 新增）：找陌生人／看清楚他是誰／送關係請求

好友清單是個**封閉集合**：它能說誰已經在裡面，說不出誰可能該進來。這一批補的就是那一格。

| op | 端點 | 性質 |
|---|---|---|
| `profile` | `/APP/Profile/getPublicProfile` | 唯讀 |
| `expand` | `getFriendsByOffset` ×N（好友的好友） | 唯讀，**純現有端點** |
| `search` | `/APP/PlurkSearch/search`（`kind=user` 走 `UserSearch`） | 唯讀 |
| `alerts` | `/APP/Alerts/getActive`（`history=1` 走 `getHistory`） | ⛔ **讀取有副作用**，見下 |
| `follow` / `unfollow` | `becomeFan` / `setFollowing` | **對外**，要 `confirm=1` |
| `befriend` / `unfriend` | `becomeFriend` / `removeAsFriend` | **對外**，要 `confirm=1` |
| `accept` / `deny` | `Alerts/addAsFriend` / `denyFriendship` | **對外**，要 `confirm=1` |

### 🩸 四筆首日血證（每一筆都改了 code，不是感想）

1. **`getActive` 不是唯讀。** 第一次讀回 4 筆（2 × `friendship_pending` ＋ `plurk_liked` ＋
   `my_responded`），**第二次同一支指令只剩 2 筆** —— 讀這一支會把通知清掉，而清掉不可逆。
   ⇒ 回傳檔現在自己講這件事；要重看走 `history=1`。
   📌 一般形：**我把一支有副作用的端點標成「唯讀」，而標籤錯了不會報錯。**

2. **方向在欄位名裡，不在 `type` 裡。** `friendship_pending` 的人在 **`to_user`**（我送出、等他）
   而不是 `from_user`（他送來、等我）。首版只看 `from_user` ⇒ 那兩筆印成空 id ＋「(查無名稱)」，
   **看起來像壞資料，實際上是四筆真的待處理關係。**
   ⇒ 現在兩個欄位都認，並且把方向印成人話；認不出人就把原始物件攤開。

3. **`unfollow` 回 200 ＋ `{"success_text":"ok"}` 而什麼都沒發生。** 首版接 `becomeFan` ＋
   `follow=false` —— 多餘的參數被**無聲吃掉**，成功字串照樣印，`is_following` 沒動。
   ⇒ 每個關係動作現在宣告「它該讓哪個欄位變成什麼」，回讀不符就印 ⛔ **回 200 但沒生效**。
   📌 這是「外觀 OK ≠ 真的 OK」在對外動作上的樣子：**成功字串是那一層的讀數，不是結果的讀數。**

4. **`expand` 的分數看起來像排名，而它不是。** 首跑最高共同好友數 3，而前 15 名**全部都是 3** ⇒
   名次其實由 tie-break（id 字串序）決定，也就是「帳號註冊得早」被印成了「比較推薦」。
   ⇒ 現在印出「最高分幾分、同分幾位」，同分過多時直說這一頁的名次不是推薦度。

### `friendship_request` vs `friendship_pending`（首日就兩種都碰到了）

| type | 人在哪個欄位 | 意思 | 我能做什麼 |
|---|---|---|---|
| `friendship_request` | `from_user` | **對方送來，等我** | `accept` / `deny` |
| `friendship_pending` | `to_user` | **我送出，等他** | 什麼都不做（催不了） |

⇒ 兩種在 `type` 字串上長得很像，而**方向只寫在欄位名裡**。
🩸 首版只讀 `from_user` ⇒ pending 那幾筆印成空 id ＋「(查無名稱)」，看起來像壞資料。

### 判準：`befriend` 的 200 不是收據

`becomeFriend` 回 200 之後 `are_friends` **仍然是 false**，因為它要等對方同意。
⇒ 三本帳分開結算：**處置那本**的憑據是 200，**結果那本**的憑據是
`getActive` 裡多出來的那筆 `friendship_pending`（去看那個）。

### ⛔ 刻意沒做的兩件事

- **沒有「全部同意」／批次加好友。** 「該不該加這個人」機器判不了，
  而批次動作會讓那一格**沒有人看過**。
- **`expand` 只算共同好友數、只讀公開發文** —— 不做別的資料拼合、不建檔。
  快取照 §5.5 規矩不入 git：那些是陌生人的東西，他們沒有同意過被釘進我們的歷史。

### 而那張人卡真的擋下了一次（首日）

`befriend` 的 dry-run 印出對方自介，其中一位寫著
「好友主要只加現實真的好友（只有少數例外），若有什麼內容讓你喜歡，不嫌棄的話可以加粉絲」
⇒ 改送 `follow`。**這一格 lint 判不了、共同好友數也判不了 —— 它只在有人讀那張卡的時候才存在。**

---

## 5.6 表情（2026-08-24 新增）：`[emoN]` 的命名空間，與「描述一次」的共用表

| op | 端點 | 性質 |
|---|---|---|
| `emoticons` | `/APP/Emoticons/get` | 對 Plurk 唯讀；**會寫本地共用表**（描述 merge） |
| `emoadd` | `/APP/Emoticons/addFromURL`（＋試 `/add`） | 對外、要 `confirm=1`；⚠ **目前走不通**，見下 |

### 🩸 `[emoN]` 是 per-account 別名，不是全站編號

`/APP/Emoticons/get` 回三組：`karma{}`（分 karma 門檻）／`recruited{}`／
`custom[]`（**只有三legged OAuth 才有**）。而 `custom` 的鍵長這樣：

```json
[["emo1", "https://emos.plurk.com/3dfa5eda…_w48_h48.gif"], ["emo2", "…"]]
```

⇒ 自訂表情的**別名本身就是 `emoN`**，那個 N 是**帳號內**編號。
所以別人噗裡的 `[emo17399]` 與我的 `[emo4]` **不同命名空間**：
拿自己的表去查別人的編號，會查到一個**長得很像答案的錯答案**（比查不到更貴）。

**跨帳號唯一穩定的鍵是圖檔 URL。** 而 URL 拿得到 ——
`getPlurks` 同一筆裡的 `content`（HTML）帶著每個表情的 `<img src>`，
跟 `content_raw` 的 `[emoN]` **同序**：

- 讀取端（`timeline` / `responses` / `get`）按序配對 ⇒ 編號 → URL。
- ⚠ **一定要濾 host**（`emos.plurk.com` / `s.plurk.com/emoticons`）：
  同一段 HTML 還有使用者上傳的圖（`images.plurk.com`），算進來會讓整排**錯開一格**，
  而錯開一格的結果每一個都看起來像答案。
- 數量對不上時**每一個都標 `⟨?配不上⟩`**，不做「前 N 個先配」。

### 共用表：`AgentCommands/Plurk/emoticons/shared.json`（＋ `shared.md` 投影）

Tim 2026-08-24 拍板的形狀：**看圖是最貴的一步，所以只做一次。**

1. 讀到沒見過的圖 ⇒ 自動登記一列（`state=seen`、`desc` 空）＋ 記下別名（`7947987:emo17399`）。
2. 有人看圖、寫回描述（`--arg emo_desc=<別名|全站碼|URL片段>=<描述>`）。
3. 之後**所有帳號**讀到同一張圖都是純文字查表 —— 不再抓圖。回傳檔印「命中／待描述／新登記」。

| 判準 | 為什麼 |
|---|---|
| **一份共用表**，不是 per-account | 「這張圖是什麼」跟誰在看它無關；分檔會讓同一張圖被每個帳號各自看一次 |
| 鍵是 URL，別名進 `aliases` | 編號會撞，URL 不會 |
| 刷新 **merge 不覆寫** | API 沒有「描述」欄位；覆寫＝每次刷新擦掉人寫的，而擦掉後跟「還沒寫」同形 |
| `state=seen` **不標 missing** | 那是別人帳號的圖，本來就不會出現在我的 API 表裡。標 missing 等於說「它下架了」，那是假的 |
| 唯讀 op 寫本地表時**一定印出來** | 不然「唯讀」這個標籤會比事實大 |

### ✅ 新增自訂表情：`addFromURL` 通，但只吃表情 CDN 的圖（實測讀數）

| 嘗試 | 讀數 |
|---|---|
| `/APP/Emoticons/add` | **404**（回 HTML 頁，不是 API 錯誤格式）⇒ 這個名字不存在 |
| `addFromURL` ＋ `images.plurk.com` 全尺寸 | **400** `we only support adding emoticons which already being uploaded to plurk` |
| 同上 ＋ 縮圖（`mx_` 前綴） | **同一句話** ⇒ 不是尺寸問題，是 host |
| `addFromURL` ＋ **`emos.plurk.com`** 的圖 | ✅ **200** `{"success_text":"ok","keyword":"emo7"}`；`custom` **6→7**。第二次 **7→8**（`keyword: emo8`） |

⇒ 它的用途是**把已經在 Plurk 表情庫裡的圖加進自己的表情盤**（例：讀到別人噗裡的
`[emo17382]`，反解析拿到 URL，再 `addFromURL` 就變成我的 `[emo7]`），
不是「從任意圖床上傳新圖」。要上傳全新的圖只能走網頁 UI。

> [!WARNING]
> ## 🩸 我第一版的驗收問錯了問題
>
> 首版驗的是「**我送出的 `alias` 有沒有出現在回讀裡**」⇒ 印「否 ← 沒生效」。
> 而事實是 **Plurk 不吃我的 `alias`，它自己編號**（回 `keyword: emo7`）——
> 那一次其實**加成功了**，`custom` 從 6 變成 7。
>
> ⇒ 判準：驗收要問「**這個動作有沒有發生**」（before→after 數量／回傳的 `keyword` 在不在清單裡），
> 不是「**我猜的那個副作用有沒有出現**」。
> 我猜錯副作用時，那行讀數會誠實地回報一個**與事實相反**的結論。
> 已改成動手前先數一次，並把 `alias` 被忽略這件事直接印在回傳檔上。

### ⭐ 而它證明了「鍵用 URL」這個決定

`addFromURL` **不會複製檔案** —— 加進來的 `emo7` 沿用**同一個 URL**。
於是共用表 merge 時它落回**已經存在的那一列**，
直接繼承了之前寫好的描述（「淡藍白色卡通生物頭部（嚕嚕米風）」），
`aliases` 欄同時掛著 `plurk_summit:emo7` 與 `7947987:emo17382`。

⇒ **同一張圖在兩個帳號有兩個名字，而表裡只有一列。**
如果當初鍵用編號，這裡會是兩列、描述要寫兩次 —— 而且沒有任何一層會告訴你它們是同一張。

⚠ 仍然：**API 沒有刪除端點** ⇒ 加錯了只能上網頁 UI 收拾。所以 `emoadd` 要 `confirm=1`。

---

## 6. OAuth 1.0a 實作的三個坑

1. **percent-encoding 必須是 RFC 3986**（`-._~` 之外全編碼）。三處都要：參數正規化、
   base string 的 url、以及金鑰。少編一個字元就只回 4xx，**而它不會說是哪一格錯**。
2. **nonce 用 `RandomNumberGenerator`（C#）／`secrets`（python）**，不用 `System.Random`／`random`
   —— 那是簽章材料。
3. **參數要進簽章**：`plurkAdd` 的 `content` / `qualifier` / `limited_to` 也在正規化字串裡。
   漏掉 body 參數的簽章在唯讀端點會通、在寫入端點才失敗（**它會讓你以為簽章是對的**）。
4. **但 multipart 反過來** —— 上傳圖片那支（`uploadPicture`）是 `multipart/form-data`，
   OAuth 1.0a 規範**只簽 `oauth_*` 參數**，檔案內容**不進**簽章基底。
   ⇒ 同一支 `OAuthHeader()` 兩種用法：form-urlencoded 傳 params、multipart 傳 `null`。
   把 body 塞進 multipart 的基底會簽出一個看起來正常的簽章然後回 4xx ——
   **而那個 4xx 跟「端點不存在」「被 WAF 擋」長得一模一樣**。

⚠ 為什麼不吃 pip：整段約 40 行 stdlib（C# 是 `HMACSHA1` ＋ `HttpClient`）。
為 40 行引入依賴＝每台機器多一個安裝前提。現成套件當**規格參考**，不當依賴。

---

## 7. audit 台帳（`<data_root>/Plurk/post_audit.jsonl`）

每則對外發文 append 一行：時間／persona／帳號／`source`／公開度與實際送出的 `limited_to`／
內容 SHA 前 16 位／`plurk_id`。

- **為什麼要有**：Plurk 沒有 history，這行是本機唯一的事後憑據。
- **為什麼只存雜湊**：全文在 Plurk 上；這裡要回答的是「這則是不是我們發的」，不是再存一份副本。
- **⛔ 不入版控**（`.gitignore`，Tim 2026-09-01 拍板）：判準是**這份紀錄要回答誰的問題**。
  Plurk 在這裡是社交用途、不追究責任 ⇒ 沒有對帳需求，本機留存就夠了。
  ⚠ 它在此之前是 tracked，所以那次是 `git rm --cached` ＋ 加 ignore 規則兩個動作 ——
  **光加 ignore 沒有用**（ignore 只管未追蹤檔），而那種「加了規則卻沒生效」不會報錯。
  既有 commit 沒有被改寫，舊紀錄仍查得回來，只是不再新增。
- **📌 如果哪天真的需要「讀回來確認」**：正解是**一則一檔、放進一個被 ignore 的資料夾**，
  不是把這個 jsonl 加回版控。理由是形狀不是潔癖 —— 單一 append-only 檔沒有穩定的定位單位，
  讀取端只能整份掃、也沒辦法只取一則；而**「要讀」跟「要入版控」是兩件事**，
  拆檔解決前者，跟後者無關。
  🔎 這個 repo 對酒館訊息已經做過同一次搬遷（`PromptQueue/migrate_jsonl_to_per_msg.py`）。
- ⚠ **送出格式與儲存格式不同形**：送 `limited_to=[0]`、Plurk 存回來是 `|0|`。
  做「讀回來判斷公開度」的對帳時**不能拿送出的值去比對**。
- 寫 audit 失敗只 LogError **不影響已發出的事實** —— 那時噗已經在時間軸上了，
  讓 Cmd 失敗會讓人以為沒發。

---

## 8. 驗收怎麼做（別用感覺）

```bash
R="senate ucmd run Plurk --persona <me>"
$R --arg op=resolve                 # 帳號＋憑證狀態（值不印，只印欄位長度）
$R --arg op=whoami                  # 唯讀簽章：200 ＋ nick_name 對得上
$R --arg op=lint    --arg slip_file=<壞樣本>   # 該擋的擋下，**且要看是哪一條規則報的**
$R --arg op=post    --arg slip_file=<好樣本>   # 無 confirm ⇒ dry-run，印完整 payload
```

改完 `.cs` 之後：

- `senate ucmd run Recompile --persona <me>` ⇒ 看 `errors=` 那一行。
- ⚠ **`errors=0` 不等於 clean**：2026-08-21 一天內三次撞到「tracker 說 `0 errors / 0 warnings`
  而 ErrorLog 同時有 10 筆 CS1061」。**憑據是 `check_compile.py` 的兩來源對帳一致**，
  而 warning 數突然歸零本身就是「這趟沒真的編到」的訊號。
- **真送過之後要回讀**：另打 `getPlurk` 撈那個 `plurk_id` 回來比內容與 `limited_to`。
  **「我送出了」跟「它在那裡」是兩句話。**
- **要驗「它用哪個帳號發」就比 `owner_id`**（拿 `/APP/Users/me` 的 `id` 對照）——
  「我以為它走個人帳號」跟「它真的用那組憑證發」是兩件事，而**兩者的成功長得一樣**。
- **附圖要驗渲染，不是驗字串**：回讀後看 `content`（HTML 那個欄位）有沒有 `<img>`。
  `content_raw` 裡有 URL 只證明我送進去了 —— **Plurk 認不認是另一回事**。

---

## 9. 相關

| 主題 | 位置 |
|---|---|
| 發文流程（操作面） | [`Plurk_Posting_Workflow.md`](Plurk_Posting_Workflow.md) |
| 快速上手（觸發詞、最短指令） | skill `ucl-plurk` |
| 帳號後台頁與憑證安裝步驟 | [`UCL_PlurkAdminPage.md`](../UCL_EditorPage/UCL_PlurkAdminPage.md) |
| 憑證加解密機制 | [`Secret_Manager_Workflow.md`](Secret_Manager_Workflow.md) |
| 設計沿革與分期（已完成） | [`Plan_Plurk_Bot.md`](../Plan/completed/Plan_Plurk_Bot.md) |
