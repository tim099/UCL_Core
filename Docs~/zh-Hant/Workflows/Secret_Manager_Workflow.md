---
title: UCL Secret Manager Workflow — passphrase 加密 + hint 提示 + 雙重失憶救援
slug: secret-manager-workflow
status: active
created_at: 2026-05-20T09:30:00Z
last_updated: 2026-08-21
location: UCL_Core (cross-project tool); state files (.enc/.txt) 由 consumer project 提供
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_UCL_Secret_Manager.md | Secret Manager Design Plan | 5 層設計 spec + Q1-Q6 拍板
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三層 bump 規範
---

# UCL Secret Manager Workflow

把外部服務 token / API key 用 **passphrase 加密**成可入 git 的 `.enc`，跨機器同步時解密回明文。相較舊版多兩件事：**hint 提示**（passphrase 忘記時喚回記憶）+ **手動貼上救援**（連 hint 都救不回時的逃生路）。

## 🧱 5 層架構

| Layer | 檔案 | 職責 |
|---|---|---|
| 1 | `Editor/SecretManager/UCL_SecretCrypto.cs` | UCLS1 格式 + Encrypt/Decrypt/ReadMetadata（**唯一**加解密實作，C# native） |
| 2 | (同上) | KDF(PBKDF2 200k) + AES-256，hint/label/created metadata |
| 3 | `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_SecretsPath.cs` | 資料夾名解析（設定檔驅動；python 對側 `ucl_paths.secrets_dir()`） |
| 4 | `Editor/SecretManager/UCL_SecretInstallWindow.cs` | 解密安裝彈窗 (Unity) |
| 5 | `Editor/SecretManager/UCL_SecretRegistry.cs` + `UCL_SecretManagerPage.cs` | registry + 集中管理 Page |

## 🔐 .enc 檔格式（現行 **UCLS1**；下方 TKN2 說明保留為舊格式參考）

```
TKN2\n                          ← magic (區分 TKN1)
<16-byte salt urlsafe-b64>\n
H:<hint 明文單行>\n              ← 明文! 跟著 git 公開
C:<created_at ISO8601 UTC>\n
L:<label 明文單行>\n
<fernet token>                  ← AES-128-CBC + HMAC-SHA256
```

- **向後相容**：decoder 同時認 TKN1（舊 3 段格式，metadata fallback 空）；encoder 一律輸出 TKN2。
- **Hint 明文**：加密 hint 與「忘密碼救援」目的悖論，故明文存。CLI/UI 強制警告「別寫密碼本身」。
- **Hint ≤ 256 char**（Tim Q6）；rotate 改 hint **全覆蓋不留軌跡**（Tim Q5）。

## 📁 資料夾位置（2026-08-21 起可設定，不再寫死）

secrets 資料夾**名稱**住設定檔，C# 與 python **共讀同一份**：

| 項目 | 值 |
|---|---|
| 設定檔 | `<data_root>/secrets_config.json` |
| key | `SecretsDir`（相對 `data_root`，正斜線） |
| 缺席時預設 | `Secret` |
| C# 解析點 | `UCL_SecretsPath.DirName` / `.AbsoluteDir` |
| python 解析點 | `ucl_paths.secrets_dir_name()` / `secrets_dir()` |
| 改哪裡 | Secret Manager 頁的「資料夾名稱 (相對 DataRoot)」→ 💾 套用 |

**為什麼要做**：`"AgentCommands/_secrets"` 這個字面值原本散在 **7 處 code、兩種語言**
（scanner 常數／3 處 `Path.Combine`／2 支 python／文件）⇒ 改名等於七處同步，
而**漏一處的症狀是靜默的**：Discord daemon 只會說「token 未就緒」，
那句話跟「還沒安裝」長得一模一樣。

> ⚠ **這跟 2026-08-17 廢除的 `_config/tavern_paths.json` 不是同一種東西。**
> 那套是 per-machine + gitignored 的細粒度覆寫，被廢的理由正是
> 「兩台機器各看各的目錄，且兩邊都不報錯」。
> 本設定**入版控、全機器同值** —— 不是「這台機器把 secrets 放別處」（那是 DataRoot override 的職責），
> 而是「這個專案的 secrets 資料夾叫什麼」。前者是漂移的入口，後者是佈局事實。

⚠ **改名不搬檔。** 那一欄只換「去哪裡找」；資料夾要自己搬（或先搬再改）。
所以改完掃不到東西不是壞掉，是指到了一個空的／不存在的位置。
⚠ 既有專案**要顯式寫設定檔** —— 靠預設值 `Secret` 會在資料夾還叫 `_secrets` 的機器上當場全斷。
刻意**不做「找不到 Secret 就退回 _secrets」的 fallback**：自排 fallback 是
「跑起來了但讀的是另一個宇宙的檔」那族的入口，而它不會叫。

## 🛠 怎麼操作（全部在 Editor，沒有 python 入口）

| 要做什麼 | 走哪裡 |
|---|---|
| 從明文產出 `.enc` | `UCL_SecretManagerPage` →「🔐 明文加密」面板（選 `.txt` → passphrase／hint／label） |
| Plurk 憑證（四欄直接產出，明文不落地） | `UCL_PlurkAdminPage` →「🔑 產生憑證」 |
| 解密安裝（產出明文供工具讀） | 該列的「解密安裝」→ `UCL_SecretInstallWindow` |
| 看 hint（忘記 passphrase，救援路徑 A） | 該列的「顯示提示」（passphrase-free 讀 metadata） |
| 手動貼明文（救援路徑 B） | 該列的「開資料夾」→ 直接貼 `<name>.txt` |
| 換 passphrase / 改 hint | 重新加密一次（覆蓋同名 `.enc`） |

> ⛔ **舊的 python CLI `ucl_secret.py` 已於 2026-08-21 移除**（連同它唯一的 lib
> `_lib/ucl_secrets_crypto.py`）。理由不是精簡：2026-07-22「全切 C#」把格式換成 **UCLS1**，
> 而那兩支只認舊的 TKN1/TKN2 ⇒ 對現行 `.enc` 一律 `bad magic`，**7 個 op 全部失效**，
> 而文件還在教人用。歷史留在 git。
>
> ⚠ 所以「加解密只有一份實作」現在是**事實**而不是原則：`UCL_SecretCrypto`（C# native）。


## 🆘 雙重失憶救援路徑

passphrase 忘記時兩條獨立路徑，互不依賴：

| 路徑 | 工具 | 前提 | 結果 |
|---|---|---|---|
| **A 喚回密碼** | `show-hint` / UI hint 框 | hint 寫得有意義 | 想起 passphrase → 正常解密 |
| **B 手動貼上明文** | `reveal` / UI「📂開資料夾」 | 手邊有原始 token (e.g. Portal reset) | 直接把明文存成 `<name>.txt`，跳過解密；daemon 照樣讀 |

> `.enc` 解不開 ≠ 系統死磚。明文 `.txt` 本就是 gitignored + daemon 直讀的目標，手動貼進去即恢復，`.enc` 之後再 rotate 補救。

## 📦 環境依賴

- `cryptography` 套件（`pip install cryptography`）。缺套件時 import 即失敗 — 見 `docs/Recovery/UCL_Secret_Recovery.md`（consumer project 端）的 requirements。

## ✅ Hint 寫法準則

| ✅ 合適 | ❌ 不合適 |
|---|---|
| 「生日後三碼 + 寵物名」 | 「密碼是 hunter2」(直接洩底) |
| 「常用密碼變體 v3」 | 「我老婆生日 19920514」 |
| 「Bitwarden 條目 EOV-bot」 | 任何能直接組出密碼的資訊 |
