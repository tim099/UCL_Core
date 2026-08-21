---
title: Plurk Bot — 把發文規則從「我要記得」搬到「機器會擋」
slug: plurk-bot
status: in-progress（2026-08-21：**帳號層已落地**（§3）；lint / preview / post 未動，現況發文仍是 Tim 手動貼）
created_at: 2026-08-21T02:40:00Z
created_by: summit
location: UCL_Core (cross-project — 發文規則與帳號解析跨專案共用)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Workflows/Plurk_Posting_Workflow.md | 共用帳號發文工作流 | **現行流程**（交付給人手動貼）；本案是它的機械化
  - ucl_core:Docs~/{lang}/Workflows/Secret_Manager_Workflow.md | Secret Manager | token 存放，**直接接不另造**
  - ucl_core:Docs~/{lang}/Plan/completed/Plan_UCL_Secret_Manager.md | Secret Manager 設計 | 5 層架構與 TKN2 格式
  - repo:AgentCommands/ChatTavern/baton/letters/summit/fragments/identity_outward_channels.md | 我的對外管道分層 | 判準／自訂表情／帳號；本案的血證來源
---

# Plurk Bot

> **一句話**：真正值錢的不是「省下貼上那一下」，是**把我五筆血證變成機器會擋的東西**。
> 所以本案的重心在 `lint`，`post` 排在最後且預設留給人按。

## 0. 為什麼要做（判準，不是「有就好」）

我自己在 `identity_outward_channels`（recurrence **10**）裡寫過這句，它就是本案的理由：

> 對我有效的修法只有一種：**把交付格式搬到發文那條路上**（skill／交付範本），
> 不要留在只有「想起來讀」才會被打開的記憶檔裡。

🩸 那句話的背景：2026-08-16 我要交文案，`find -iname "*plurk*"` 找不到自己的表情表，
於是跟 Tim 說「我沒記在任何地方」—— **而它就在那個檔裡，recurrence=10、當天早上還印在我的 wake brief 上。**
⇒ 標籤沒漏、排名沒漏，漏的是**我沒想到要去找它**。這種漏法只有「長在必經路上」治得了。

## 1. 現況（2026-08-21 讀出來的）

| 項目 | 現況 |
|---|---|
| 發布動作 | **Tim 手動貼**（無任何自動化基建） |
| 交付格式 | `Plurk_Posting_Workflow.md` 的四欄交付單（`persona／心情詞／文案本體／圖片路徑`） |
| 規則 | 三大鐵律（句內不斷行／第一行要能當標題／表情以特徵為主） |
| 帳號 | 個人（`zeta@summit`、basecamp）＋ **一組共用（公用帳號）** |
| token | **不存在**（沒有任何 Plurk 憑證進系統） |

## 2. Tim 2026-08-21 的三項拍板

### 2.1 單筆 **300 字以內**，超過拆成兩則

原本記的是「約 360，超過轉 Plurk Paste」。改成 300 的理由是**兩個實務代價**：

1. **附圖與表情會額外吃字數** —— `[emoN]` 在內文裡是字面 token（每個約 6–7 字元），
   圖片也會佔掉額度。⇒ 300 是**留了餘裕的預算**，不是新的硬牆。
2. **Plurk Paste 沒有表情，別人也不好查看** —— 轉 Paste 之後時間軸只剩第一行，
   而表情不會渲染。⇒ 「超過上限只是換一種形態」這句話在**有表情的文案上不成立**。

> ⚠ 這推翻了我 fragment 裡「360 不是硬牆、轉 Paste 曝光沒有少」那一格的**適用範圍**（不是推翻它本身）：
> 純文字長噗確實不吃虧，**帶表情的長噗會**。⇒ 判準要寫成「帶表情／附圖 ⇒ 300 上限、超過就拆」。

### 2.2 帳號：一組共用預設，可 override 為個人帳號

**照 email 那套的形狀**（`agent_email.resolve_email`），不發明第二套。

### 2.3 先文件化，不實作

本檔即交付物。

> ⚠ **同日追加**：Tim 隨後指派「接著繼續 plurk 串接部分…先處理帳號相關部分即可」
> ⇒ §3 帳號層已實作（見該節的落地標記）。§6 的 lint / preview / post **仍未動**。

## 3. 帳號解析（照 email 的形狀）

> ✅ **本節 2026-08-21 已落地** —— `Editor/Plurk/UCL_PlurkAccounts.cs`（解析）＋
> `Editor/Plurk/UCL_PlurkAdminPage.cs`（後台頁）。操作與驗收讀數見
> [`UCL_PlurkAdminPage.md`](../UCL_EditorPage/UCL_PlurkAdminPage.md)。
>
> ⚠ 實作與本規劃**有兩處偏離，照實記**：
> 1. **檔案位置**：規劃沒寫，實作放 `Editor/`（組件 `UCL_CoreEditor`）而不是
>    `UCL_Core_Scripts/`。理由是組件引用單向（`UCL_CoreEditor → UCL_Core`），
>    而 `UCL_SecretScanner` 住 `Editor/` ⇒ 放錯邊會 CS0246，或逼我自己再寫一份找 .enc 的邏輯。
> 2. **入口**：規劃寫「後台頁入口放 ToolBox」，實作**沒有**掛 ToolBox ——
>    Tim 2026-08-21 更正：頁面選單的下拉本來就用反射掃得到（`ShowInPageMenu`），
>    而 ToolBox 在 `UCL_Core` 這側，硬接需要字串型別名反射（改名不會編譯錯、只會靜默少一顆按鈕）。


email 現行是四段、**回值一律含 `source`**：
`persona override → agent 預設 → 全域 fallback → 哨兵 unset@invalid`。

Plurk 照抄形狀，但**刻意只留三段**：

| 段 | 來源 | source 值 |
|---|---|---|
| 1 | `letters/<persona>/profile/plurk_account.md`（persona override） | `persona-override` |
| 2 | `AwakenInit/plurk_accounts.json` 的 `shared`（公用帳號） | `shared-default` |
| 3 | 都沒有 | `unset` ⇒ **擋下不發** |

⛔ **刻意不做 agent 層** —— email 有那一層是因為信箱本來就綁 agent（`Codex` / `ClaudeCode` 各一個）；
Plurk 帳號不是那種東西，它是「某個人的」或「大家共用的」。多留一個沒人用的槽＝多一個會漂的地方。

**寫入唯一通道**：`Cmd PersonaProfile op=set`（actor／reason 必填），後台入口比照 `UCL_PersonaAgentAdminPage`。
⚠ 不可寫 `AwakenInit/personas/<name>.json` —— email 的 override 2026-08-19 已從那裡搬走，
**舊源只出不進，寫在那裡不會生效**。同一個坑不踩第二次。

### 3.1 `source` 不是除錯資訊，是規則的輸入

`source == "shared-default"` ⇒ **末行署名為必填**（Tim 2026-08-16 硬規則：共用帳號必須署名）。
`source == "persona-override"` ⇒ 署名建議但非必填（時間軸上帳號本身就是身分）。

⇒ 所以 `resolve` 的回值必須帶 `source`，否則 lint 無法判斷該不該擋。
**這正是 email 那套值得抄的地方**：它不只回答「用哪個」，還回答「憑什麼」。

## 4. Token 存放：直接接 `UCL_SecretManagerPage`

Plurk API 2.0 用 **OAuth 1.0a**，一個帳號四個值（consumer key/secret ＋ access token/secret）。

- 一帳一份 `.enc`，命名 `plurk_<account>`：`plurk_shared` / `plurk_zeta_summit` / `plurk_cc_basecamp`
- 走既有 C# native 那套（`UCL_SecretCrypto` UCLS1 ／ 安裝彈窗 ／ registry ／ 管理頁）
  ⚠ 原本這裡寫的是「python 5 層（TKN2 / ucl_secret.py 7 op）」—— 那兩支 2026-08-21 已移除，因為它們只認舊格式、對現行 `.enc` 一律 bad magic。
- `UCL_SecretDaemon` 已會掃「有密文缺明文」並彈窗安裝 ⇒ 缺 token 的失敗會**當場喊**，不是靜默

⛔ **agent 不碰 passphrase。** 安裝由 Tim 在 Editor 彈窗做；工具只讀已解密的明文。
⛔ **hint 不可寫密碼本身**（既有規則）。

## 5. 依賴：不吃 pip

現成 Python 套件（[plurk-oauth](https://pypi.org/project/plurk-oauth/) / [plurk.py](https://pypi.org/project/plurk.py/) /
[poaurk](https://github.com/Dephilia/poaurk) / [plurk-oauth3](https://github.com/rschiang/plurk-oauth3) …）
**當規格參考，不當依賴** —— 本 repo python 工具慣例是純 stdlib（`chess.py` / `canvas.py` / `library.py` 同批），
而 OAuth 1.0a 簽章用 `hmac` + `hashlib` + `urllib` 約 40 行。為 40 行引入 pip 依賴＝每台機器多一個安裝前提。

⚠ **未驗證（實作前必做）**：精確端點與參數名。官方 API 頁本次抓取回 **403**、PyPI 頁載入失敗，
所以 `/APP/Timeline/plurkAdd`、`content`／`qualifier`、上傳圖片端點**都還沒被我對照過官方文件**。
Phase 1 的第一件事就是拿唯讀端點（如 `/APP/Profile/getOwnProfile`）驗簽章，再碰寫入端點。

## 6. 分期（每期可獨立驗收）

### Phase 0 — `op=lint`（**建議先只做這個**）

零對外風險、不需要任何 token、不連網。把血證變成擋人的規則：

| lint 規則 | 擋什麼 | 血證 |
|---|---|---|
| 交付單含括號編輯註記／說明文字 | 半成品被當文案發出 | 2026-08-07「（短、好笑、純自嘲）」上了標題 |
| 句內手動斷行 | 疊上 Plurk 軟斷行 → 版面碎 | 2026-08-11 「台詞」被拆兩行 |
| 預算超過 300（含 `[emoN]` 字面長度＋圖片保留額度） | 轉 Paste 後表情消失、不好查看 | Tim 2026-08-21 拍板 |
| 第一行無法單獨站著 | 轉 Paste／預覽時只看得到它 | 長噗第一行＝標題 |
| 內文有 `[emoN]` | 印出我記的名字與視覺特徵要求確認 | 編號是位置性的，會漂 |
| `source=shared-default` 但末行無署名 | 共用帳號發文無法歸屬 | Tim 2026-08-16 硬規則 |
| 內文出現 `@同事` | 提醒點名禮節（發前照會） | 工作流第五節 |

**驗收**：拿 2026-08-07 與 08-11 那兩篇**真實出事的文案**當測試樣本 ——
lint 必須擋下它們。⚠ 用乾淨樣本驗證等於沒驗（樣本不會走進錯誤分支）。

### Phase 1 — `op=preview`（組 payload 不送）

- 用唯讀端點驗 OAuth 簽章正確
- 印出：解析到的帳號 ＋ `source`、字數預算明細（本文／emo token／圖片保留）、拆則結果、第一行預覽
- **不送任何寫入請求**

### Phase 2 — `op=post`（需 Tim 顯式授權）

- audit jsonl：時間、persona、帳號、`source`、內容 SHA、回傳的 plurk id
- 頻率閘（治理約定第 2 條：避免多 persona 洗板）
- ⚠ **發布不可回復且 Plurk 沒有 git history** ⇒ 預設仍由人按；工具端要有 `--dry-run` 為預設值

## 7. 拆則設計（Tim 2.1 的落地細節）

### 7.1 預算怎麼算

```
可用文字額度 = 300 − Σ(每個 [emoN] 的字面長度) − 圖片保留額度
```
⚠ 上限計的**不是中文字數**：換行、`**`、全形標點都算（fragment 已記，實測栽過一次）。

### 7.2 切點規則

1. **只在段落邊界切**（空行處）—— 絕不切句內。切句內就是把 08-11 那隻手動斷行的坑換個形狀重犯
2. 兩則都要能**單獨被讀懂**：時間軸上讀者可能只看到其中一則
3. 標記 `(1/2)` `(2/2)` 放**第一行行末**，不佔標題力
4. **署名放每一則** —— 不是只放最後一則。理由同 2：兩則會被分開看到，
   而共用帳號沒署名就無法歸屬（Tim 08-16 硬規則對「每一則」成立，不是對「這組」成立）
5. 切不出合法切點（單段就超過 300）⇒ **lint 擋下並要求改寫**，不自動硬切

### 7.3 一個替代方案（列出來讓 Tim 選）

| 方案 | 曝光 | 閱讀連續性 | 洗板 |
|---|---|---|---|
| **A：兩則獨立噗**（Tim 說的） | 兩則都上時間軸 | 需靠 `(1/2)` 標記 | 兩格 |
| B：第二則走**回應**（comment） | 只有第一則上時間軸 | 連在一起、天然順序 | 一格 |

⇒ 預設走 **A**（照拍板）。B 留在文件裡是因為「長文本來就該連著讀」這個需求是真的，
若哪天覺得 `(1/2)` 標記太吵，B 是現成的替代路。

## 8. 落地時要一起改的既有文件（doc-sync）

| 文件 | 改什麼 |
|---|---|
| `Plurk_Posting_Workflow.md` | 鐵律二的 360 → **300／超過拆兩則**；交付單增 `帳號` 欄（或明寫預設走共用） |
| `letters/summit/fragments/identity_outward_channels.md` | 追加一筆 `origins`：360 的適用範圍被表情／附圖限縮 |
| `ucl-commit` 之外新增 skill？ | ⚠ **暫不新增** —— 規則若已長在 lint 上，再開一份 skill 就是第二份規範 |

## 9. 風險與邊界

- **不可回復**：Plurk 沒有 git history，發錯無法對帳。這是 `post` 排最後的唯一理由
- **`[emoN]` 位置性**：面板重排就漂 ⇒ lint 只能提醒確認，**不能替我確認**（那需要看得到面板）
- **好友名單／隱私設定是全域資產**：治理約定禁止個別 persona 私改 ⇒ bot 不提供任何改設定的 op
- **匿名噗（偷偷說）**：我判定它是「公開但拿掉署名」，**預設不用** ⇒ bot 不實作
- **圖片也過公開判準**：圖裡有同事作品要比照點名規則
- **static/單機邊界**：token 明文落在本機，跨機器要各自安裝（SecretManager 既有行為）

## 10. 給未來的自己一句

這件事的價值不在自動化。**在於我今天知道自己會犯哪五種錯，而那些知識現在住在一個
「要想起來才會被打開」的檔案裡。** 搬到 lint 上之後，它們變成不需要我記得的東西 ——
而那正是我這三個月唯一驗證過有效的修法形狀。
