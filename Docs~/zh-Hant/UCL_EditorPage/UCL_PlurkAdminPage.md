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
> - 整體規劃（lint / preview / post 三期）：[`Plan_Plurk_Bot.md`](../Plan/Plan_Plurk_Bot.md)
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

**secret 的檔名 stem** —— `_secrets/plurk_shared.enc` ⇒ id 是 `plurk_shared`。

- 只有 `plurk_` 前綴的 `.enc` 會被列出（清單來源是 `UCL_SecretScanner`，不是本頁自己找檔）
- 一個帳號四個值（consumer key/secret ＋ access token/secret）打包成一份 secret
- **本頁不碰 passphrase**：加密與解密安裝都在 Secret Manager（TopBar 有直接跳過去的按鈕）

## 頁面上有什麼

### 🤝 共用帳號（公用）
下拉選 secret id ＋ 狀態 ＋ 存檔／放棄改動。設成 `(未設定)` ＝ 沒有共用預設
（所有沒設個人帳號的 persona 都會解析成 `unset`）。

⚠ **「有 `.enc`」與「明文已安裝」分開顯示，不合併成一個綠燈** ——
只有後者才真的能發文，而前者存在時看起來已經好了。

### 🧑 persona 對照
每位一列：`persona`｜個人/共用｜解析結果（含理由）｜token 狀態｜下拉改成個人帳號。

- 下拉選 `(未設定)` ＝ 清掉 override、回落共用
- 寫入走 `UCL_PersonaProfile.SetField`（`actor` / `reason` 必填、有審計 jsonl），
  **不碰 `AwakenInit/personas/<name>.json`** —— 那個舊源 2026-08-19 起只出不進，寫了不會生效

## 讀寫時機

讀檔只在 `Init` / 「🔄 重新整理」/ 寫入後 —— **`Draw` 裡零 IO**。
（IMGUI 的 Layout 與 Repaint 是兩個 pass，Draw 裡碰磁碟會讓兩趟看到不同的東西，
症狀是 `ArgumentException` 中止該幀繪製。）

## 驗收讀數（2026-08-21 實跑，非推論）

| 驗什麼 | 讀數 |
|---|---|
| `RegistryPath()` | `<data_root>/AwakenInit/plurk_accounts.json` |
| `ListSecretIds()`（還沒有任何 plurk secret） | `[]` |
| `Resolve("summit")` → `Describe()` | `未設定 —— 沒有共用預設、也沒有個人 override` |
| 頁面 `Create()` ＋ private `Reload()` | 皆 OK，無例外 |
| **去路**：`PersonaProfile op=set plurk_account=plurk_roundtrip_probe` → `Resolve` | `個人帳號（plurk_roundtrip_probe）` |
| **歸路**：同上設回空 → `Resolve` | `未設定 —— …` |

⚠ round-trip 兩個方向都驗過 —— **多數守衛只擋去路不擋歸路**，而那種缺陷會活到真的要清設定的那天。

## 尚未做（誠實標記）

本頁**只有帳號**。發文、lint、preview、post 都還沒實作，OAuth 端點也還沒對照官方文件驗過
（見 `Plan_Plurk_Bot.md` §5 的未驗證標記）。
