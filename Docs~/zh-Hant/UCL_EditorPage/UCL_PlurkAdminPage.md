---
title: UCL_PlurkAdminPage — Plurk 帳號管理
description: Plurk 後台管理頁（目前只做帳號）：共用（公用）帳號指向哪一份 secret、每個 persona 用個人帳號還是共用；憑證本體走 Secret Manager。
last_updated: 2026-08-21
target_audience: [AI_Agent, Tools_User]
status: v1.0（Tim 2026-08-21：「先處理帳號相關部分即可」）
---

# 🐦 UCL_PlurkAdminPage — Plurk 帳號管理

> 一句話：**只分兩種帳號 —— 共用（公用）與個人。本頁只處理「誰用哪一份」的 id 對應，
> 不顯示也不讀取任何 token。**

> 📦 相關
> - 憑證本體（加密／解密安裝／hint）：[`Secret_Manager_Workflow.md`](../Workflows/Secret_Manager_Workflow.md)（頁面：`UCL_SecretManagerPage`）
> - 發文規則與交付格式：[`Plurk_Posting_Workflow.md`](../Workflows/Plurk_Posting_Workflow.md)
> - **發文流程**：[`Plurk_Posting_Workflow.md`](../Workflows/Plurk_Posting_Workflow.md)（skill `ucl-plurk`）
> - **維護／擴充**：[`Plurk_Maintenance.md`](../Workflows/Plurk_Maintenance.md)
> - 設計沿革（已完成）：[`Plan_Plurk_Bot.md`](../Plan/completed/Plan_Plurk_Bot.md)
> - 實作：`Editor/Plurk/UCL_PlurkAccounts.cs`（解析）／`Editor/Plurk/UCL_PlurkAdminPage.cs`（本頁）

## 入口

**頁面選單的下拉**（`ShowInPageMenu`，以反射掃出所有頁）—— 跟 `UCL_SecretManagerPage` 同一條路。

⚠ **刻意不掛 `UCL_ToolBoxPage`**：ToolBox 住在 `UCL_Core` 組件，而本頁住 `UCL_CoreEditor`
（因為它要用 `UCL_SecretScanner`），而組件引用是**單向的** `UCL_CoreEditor → UCL_Core`。
硬接就得用字串型別名做反射，而那種寫法**改名不會編譯錯、只會靜默少一個按鈕**。

## 帳號解析（三段，形狀同 `agent_email.resolve_email`）

| 段 | 來源 | `Source` |
|---|---|---|
| 1 | 該 persona 的 profile 欄位 `plurk_account` | `persona-override`（＝**個人**） |
| 2 | `AwakenInit/plurk_accounts.json` 的 `SharedSecretId` | `shared-default`（＝**共用**） |
| 3 | 都沒有 | `unset` ⇒ **不能發文** |

⇒ **個人／共用不是存出來的欄位，是由 `Source` 推導的。** 多一個欄位就多一個會跟事實漂掉的地方，
而「欄位說個人、解析出共用」這種漂移兩邊都不會報錯。

### `Source` 是規則的輸入，不是除錯資訊

`Source == shared-default` ⇒ **文案末行必須署名**（Tim 2026-08-16 硬規則）——
共用帳號的時間軸上讀者只看得到帳號、看不到是誰寫的。
`RequiresSignature` 就是直接讀 `Source` 算出來的，呼叫端不必自己判斷。

## 帳號 id 是什麼

**secret 的檔名 stem** —— `<secrets_dir>/plurk_shared.enc` ⇒ id 是 `plurk_shared`。

> ⚠ **secret 資料夾名不要寫死。** 它由 `<data_root>/secrets_config.json` 的 `SecretsDir` 決定
> （本專案 2026-08-21 起是 **`Secret/`，且已拆成獨立 private submodule**；舊名 `_secrets` 已不存在）。
> 解析一律走既有入口：C# `UCL_SecretsPath.DirName` / `.AbsoluteDir`、
> python `_lib/ucl_paths.py` 的 `secrets_dir_name()` —— 兩端讀同一個設定檔。
> 🩸 寫死會怎麼咬人：**寫檔會自動建目錄** ⇒ 照舊名手編明文的人憑空長出一個資料夾、
> 檔案寫成功、而 `UCL_SecretScanner` 掃不到它 —— 全程零錯誤訊息。

- 只有 `plurk_` 前綴的 `.enc` 會被列出（清單來源是 `UCL_SecretScanner`，不是本頁自己找檔）
- 一個帳號四個值（consumer key/secret ＋ access token/secret）打包成一份 secret
- **本頁不碰 passphrase**：加密與解密安裝都在 Secret Manager（TopBar 有直接跳過去的按鈕）

## 頁面上有什麼

### 🤝 共用帳號（公用）
下拉選 secret id ＋ 狀態 ＋ 存檔／放棄改動。設成 `(未設定)` ＝ 沒有共用預設
（所有沒設個人帳號的 persona 都會解析成 `unset`）。

⚠ **「有 `.enc`」與「明文已安裝」分開顯示，不合併成一個綠燈** ——
只有後者才真的能發文，而前者存在時看起來已經好了。

### 🔑 產生憑證
填四個憑證欄 ＋ passphrase → 直接產出 `.enc`（明文不落地）。流程沿用
`UCL_SecretCrypto.Encrypt`（PBKDF2 200k + AES-256），**不另造第二套加密**。
詳見下方「憑證檔長什麼樣」。

### 🧑 persona 對照
每位一列：`persona`｜個人/共用｜解析結果（含理由）｜token 狀態｜下拉改成個人帳號。

- 下拉選 `(未設定)` ＝ 清掉 override、回落共用
- 寫入走 `UCL_PersonaProfile.SetField`（`actor` / `reason` 必填、有審計 jsonl），
  **不碰 `AwakenInit/personas/<name>.json`** —— 那個舊源 2026-08-19 起只出不進，寫了不會生效

## 憑證檔長什麼樣（Phase 1 讀取契約）

OAuth 1.0a 一定是**四個值**：前兩個認 app、後兩個認「以哪個帳號發文」。
所以一份 Plurk secret ＝ 一個 JSON，四欄到齊才算完整：

```json
{
  "account": "shared",
  "note": "自由文字備註",
  "consumer_key": "…",
  "consumer_secret": "…",
  "access_token": "…",
  "access_token_secret": "…"
}
```

⚠ **只有 consumer key/secret（app 層）是不能發文的** —— 那組只認 app，不認帳號。
access token 要在 Plurk 端對那個帳號做一次授權才拿得到。

### 怎麼拿到那四個值

到 Plurk 註冊一個 app 拿 consumer key/secret，再對要發文的帳號授權一次拿 access token/secret。
Tim 2026-08-21 實際照這篇跑完：<https://www.plurk.com/p/nrwtgh>

> ⚠ 該連結的內容**本文件未逐項核對**（抓取回 403，agent 讀不到）——
> 這裡標的是「Tim 走過這條路」，不是「本文件背書其步驟」。兩者不同，所以分開寫。

### 安裝步驟 A（建議）：**在本頁填欄位，直接產出 `.enc`**

本頁「🔑 產生憑證」面板：填 secret id ＋ 四個憑證欄 ＋ passphrase／hint／label → 按產出。

- ⭐ **明文不落地**：JSON 在記憶體組好直接加密，`<secrets_dir>/*.txt` 全程不產生。
  少一份殘留就少一個外洩面 —— **gitignored ≠ 不存在**。
- **產出成功後四個憑證欄與 passphrase 立刻清空**，不留在頁面狀態裡。
- `.enc` 已存在時按鈕停用，要勾「我確定要覆蓋它」才放行 ——
  覆蓋掉的憑證**拿不回來**（passphrase 不可反推），而覆蓋成功跟第一次建立**看起來一樣**。
- id 不以 `plurk_` 開頭會當場警告：命名錯不會報錯，只會「產出了但下拉選單掃不到」。
- JSON 用 `JsonData` 組**不是字串串接** —— 憑證含引號／反斜線時串接會產出壞掉的 JSON，
  而那是「寫成功了但讀不回來」那一族。

### 安裝步驟 B：手編明文再加密（原路，仍可用）

1. 建 `<data_root>/<secrets_dir>/plurk_<account>.txt`，內容照上面的 JSON
   （本專案現況：`AgentCommands/Secret/`。該資料夾的 `.gitignore` 是 `*` 全擋 ＋ `!*.enc`
   ⇒ **明文永不進版控，只有 `.enc` 會**；2026-08-21 起它是獨立 private submodule ——
   `private` 降低的是曝光面，不是曝光的後果，所以判準不變：**密文旅行、明文不旅行**）
2. Secret Manager 頁 →「從明文加密」選該 `.txt` → 填 passphrase／hint／label → 產出 `.enc`

### 兩條路都要做的最後兩步

3. 回本頁 →「🔄 重新整理」→ 共用帳號下拉會出現該 id → 選它 → 💾 存檔
4. 本頁分開顯示 `.enc 有` 與 `明文已安裝` —— **只有後者代表真的能用**。
   `.enc` 是密文，工具讀的是明文 ⇒ 還要到 Secret Manager 做一次**解密安裝**。

> ⛔ **agent 不寫入憑證。** 這不是流程偏好，是硬界線：
> API key / token / passphrase 一律由人自己貼進檔案或彈窗，agent 只讀「已解密的明文」與 secret **id**。
> ⚠ 若憑證曾以純文字出現在對話、log 或訊息裡 ⇒ 到 Plurk app console **rotate 一組**，
> 因為那些地方可能被保留或轉述，而**憑證外洩不會有任何錯誤訊息**。

## 讀寫時機

讀檔只在 `Init` / 「🔄 重新整理」/ 寫入後 —— **`Draw` 裡零 IO**。
（IMGUI 的 Layout 與 Repaint 是兩個 pass，Draw 裡碰磁碟會讓兩趟看到不同的東西，
症狀是 `ArgumentException` 中止該幀繪製。）

## 驗收讀數

### 帳號層落地當天（2026-08-21 上午，**還沒有任何 plurk secret**）

| 驗什麼 | 讀數 |
|---|---|
| `RegistryPath()` | `<data_root>/AwakenInit/plurk_accounts.json` |
| `ListSecretIds()` | `[]` |
| `Resolve("summit")` → `Describe()` | `未設定 —— 沒有共用預設、也沒有個人 override` |
| 頁面 `Create()` ＋ private `Reload()` | 皆 OK，無例外 |
| **去路**：`PersonaProfile op=set plurk_account=plurk_roundtrip_probe` → `Resolve` | `個人帳號（plurk_roundtrip_probe）` |
| **歸路**：同上設回空 → `Resolve` | `未設定 —— …` |

⚠ round-trip 兩個方向都驗過 —— **多數守衛只擋去路不擋歸路**，而那種缺陷會活到真的要清設定的那天。

### 共用帳號設好之後（2026-08-21 下午，接手時重量的現況）

上面那張是「**還沒設定**」的讀數。設定完就不是那樣了 ——
留著舊讀數會讓下一個人以為這條線還沒接（過期的讀數不會叫）。

| 驗什麼 | 讀數 |
|---|---|
| `secrets_config.json` | `{"SecretsDir":"Secret"}` ⇒ secret 目錄 = `AgentCommands/Secret/`（private submodule） |
| `plurk_accounts.json` | `SharedSecretId = plurk_shared` |
| `ListSecretIds()` | `[plurk_shared]` |
| `Resolve("basecamp")` → `Describe()`（**當時 basecamp 還沒有個人帳號**） | `共用帳號（plurk_shared）—— 末行署名必填` |

> ⚠ **這一格 2026-08-21 傍晚就過期了** —— Tim 隨後替 basecamp／summit 各裝了個人帳號，
> 同一支 `Resolve("basecamp")` 現在回 `個人帳號（plurk_basecamp）`。
> ⇒ **解析結果會隨 profile 變動，所以本文件不再釘死一個值**：
> 要知道現在走哪個帳號就跑 `run Plurk --arg op=resolve`。
> 釘死一個會變的值＝製造下一個過期讀數（而過期的讀數不會叫）。
| 憑證完整度（只量欄位與長度，**不印值**） | 四欄到齊：`consumer_key` 12 / `consumer_secret` 32 / `access_token` 12 / `access_token_secret` 32 |
| 明文安裝 | **已安裝**（`Secret/plurk_shared.txt` 存在）⇒ 帳號層真的可用，不只是「有 `.enc`」 |
| 明文是否進版控 | `git ls-files` 只有 `.gitignore` / `README.md` / 兩個 `.enc`；`check-ignore` 確認 `.txt` 命中 `*` 全擋 ⇒ **沒進版控** |

## 本頁的範圍（與發文的分工）

本頁**只有帳號**：誰用哪一份 secret。**發文不在這裡** ——
2026-08-21 起走 `Cmd Plurk`（`op=lint` / `preview` / `post`，實作 `Editor/Plurk/Cmd_Plurk.cs`），
流程見 [`Plurk_Posting_Workflow.md`](../Workflows/Plurk_Posting_Workflow.md) §0。
首航實跑：`plurk_id 358451487782338`（朋友限定，回讀 `getPlurk` 驗過）。

⚠ **仍未驗**：心情詞完整詞彙表、`公開度=本人`、附圖上傳端點、`reply_to`（回應）那條路。
