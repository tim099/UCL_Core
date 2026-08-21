---
title: Plurk 串接維護指南
description: Plurk 發文機制的維護面 —— 四個檔的分工、怎麼加一條 lint 規則、怎麼加心情詞、帳號與憑證安裝、OAuth 實作的三個坑、端點驗證狀態、audit 對帳。
last_updated: 2026-08-21
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
> ⚠ 附帶後果兩個：①`persona-override` **不強制末行署名**（時間軸上帳號本身就是身分）；
> ②裝了個人帳號之後，同一道指令的解析結果就變了 ——
> **要知道現在走哪個帳號，跑 `op=resolve`，不要讀任何文件裡記著的值。**
> ⛔ 目前**沒有**「這一則強制走共用帳號」的參數。真的需要時再加 `account=`，
> 而加之前要想清楚：那等於讓人可以繞過 profile 的宣告。

- **個人／共用不存欄位，由 `Source` 推導** —— 多一個欄位就多一個會跟事實漂掉的地方，
  而「欄位說個人、解析出共用」這種漂移兩邊都不報錯。
- `Source` 不是除錯資訊，是**規則的輸入**：`shared-default` ⇒ `RequiresSignature`（末行署名必填）。
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
| 附圖上傳端點 | ⚠ **完全沒實作** |
| `/APP/Responses/responseAdd`（`reply_to` 回應） | ⚠ code 有、**未實跑** |

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

## 6. OAuth 1.0a 實作的三個坑

1. **percent-encoding 必須是 RFC 3986**（`-._~` 之外全編碼）。三處都要：參數正規化、
   base string 的 url、以及金鑰。少編一個字元就只回 4xx，**而它不會說是哪一格錯**。
2. **nonce 用 `RandomNumberGenerator`（C#）／`secrets`（python）**，不用 `System.Random`／`random`
   —— 那是簽章材料。
3. **參數要進簽章**：`plurkAdd` 的 `content` / `qualifier` / `limited_to` 也在正規化字串裡。
   漏掉 body 參數的簽章在唯讀端點會通、在寫入端點才失敗（**它會讓你以為簽章是對的**）。

⚠ 為什麼不吃 pip：整段約 40 行 stdlib（C# 是 `HMACSHA1` ＋ `HttpClient`）。
為 40 行引入依賴＝每台機器多一個安裝前提。現成套件當**規格參考**，不當依賴。

---

## 7. audit 台帳（`<data_root>/Plurk/post_audit.jsonl`）

每則對外發文 append 一行：時間／persona／帳號／`source`／公開度與實際送出的 `limited_to`／
內容 SHA 前 16 位／`plurk_id`。

- **為什麼要有**：Plurk 沒有 history，發錯無法對帳 ⇒ 這行是唯一的事後憑據。
- **為什麼只存雜湊**：全文在 Plurk 上；這裡要回答的是「這則是不是我們發的」，不是再存一份副本。
- **為什麼入版控**（跟 `_cmd_failed` 那種 per-machine 清單不同）：它是「這個帳號對外說過什麼」的
  共享事實，換機器不該從零開始。
- ⚠ **送出格式與儲存格式不同形**：送 `limited_to=[0]`、Plurk 存回來是 `|0|`。
  做「讀回來判斷公開度」的對帳時**不能拿送出的值去比對**。
- 寫 audit 失敗只 LogError **不影響已發出的事實** —— 那時噗已經在時間軸上了，
  讓 Cmd 失敗會讓人以為沒發。

---

## 8. 驗收怎麼做（別用感覺）

```bash
R="python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Plurk"
$R --arg op=resolve                 # 帳號＋憑證狀態（值不印，只印欄位長度）
$R --arg op=whoami                  # 唯讀簽章：200 ＋ nick_name 對得上
$R --arg op=lint    --arg slip_file=<壞樣本>   # 該擋的擋下，**且要看是哪一條規則報的**
$R --arg op=post    --arg slip_file=<好樣本>   # 無 confirm ⇒ dry-run，印完整 payload
```

改完 `.cs` 之後：

- `run_cmd.py --persona <me> recompile` ⇒ 看 `errors=` 那一行。
- ⚠ **`errors=0` 不等於 clean**：2026-08-21 一天內三次撞到「tracker 說 `0 errors / 0 warnings`
  而 ErrorLog 同時有 10 筆 CS1061」。**憑據是 `check_compile.py` 的兩來源對帳一致**，
  而 warning 數突然歸零本身就是「這趟沒真的編到」的訊號。
- **真送過之後要回讀**：另打 `getPlurk` 撈那個 `plurk_id` 回來比內容與 `limited_to`。
  **「我送出了」跟「它在那裡」是兩句話。**
- **要驗「它用哪個帳號發」就比 `owner_id`**（拿 `/APP/Users/me` 的 `id` 對照）——
  「我以為它走個人帳號」跟「它真的用那組憑證發」是兩件事，而**兩者的成功長得一樣**。

---

## 9. 相關

| 主題 | 位置 |
|---|---|
| 發文流程（操作面） | [`Plurk_Posting_Workflow.md`](Plurk_Posting_Workflow.md) |
| 快速上手（觸發詞、最短指令） | skill `ucl-plurk` |
| 帳號後台頁與憑證安裝步驟 | [`UCL_PlurkAdminPage.md`](../UCL_EditorPage/UCL_PlurkAdminPage.md) |
| 憑證加解密機制 | [`Secret_Manager_Workflow.md`](Secret_Manager_Workflow.md) |
| 設計沿革與分期（已完成） | [`Plan_Plurk_Bot.md`](../Plan/completed/Plan_Plurk_Bot.md) |
