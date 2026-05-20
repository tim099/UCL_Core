---
title: UCL Secret Manager Workflow — passphrase 加密 + hint 提示 + 雙重失憶救援
slug: secret-manager-workflow
status: active
created_at: 2026-05-20T09:30:00Z
last_updated: 2026-05-20T09:30:00Z
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
| 1 | `Tools~/AgentCommands/_lib/ucl_secrets_crypto.py` | TKN2 格式 + encrypt/decrypt/read_metadata 純加解密 |
| 2 | (同上, lib) | KDF(PBKDF2 200k) + Fernet, hint/label/created metadata |
| 3 | `Tools~/AgentCommands/ucl_secret.py` | CLI 7 op |
| 4 | `Editor/SecretManager/UCL_SecretInstallWindow.cs` | 解密安裝彈窗 (Unity) |
| 5 | `Editor/SecretManager/UCL_SecretRegistry.cs` + `UCL_SecretManagerPage.cs` | registry + 集中管理 Page |

## 🔐 .enc 檔格式 (TKN2)

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

## 🛠 CLI 7 op (`ucl_secret.py`)

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/ucl_secret.py"

# 加密 (互動兩次 confirm; 帶 hint/label)
$PY encrypt _secrets/discord_bot_token.txt --hint "生日後三碼+貓名" --label "EOV Discord Bot"

# 解密 (可先印 hint 輔助記憶)
$PY decrypt _secrets/discord_bot_token.enc --show-hint
echo -n "<pw>" | $PY decrypt _secrets/discord_bot_token.enc --stdin-passphrase   # 非互動

# status (給 Editor/hook): exit 0=ok 1=need-install 2=no-enc 3=stale
$PY status _secrets/discord_bot_token

# ⭐ show-hint — passphrase-free 印 metadata (失憶救援路徑 A)
$PY show-hint _secrets/discord_bot_token.enc          # 人讀
$PY show-hint _secrets/discord_bot_token.enc --json   # Page/自動化

# list — 掃資料夾所有 .enc
$PY list --root _secrets [--json]

# rotate — 換 passphrase 或改 hint (舊 pw → 新 pw)
$PY rotate _secrets/discord_bot_token.enc --hint "新提示"

# ⭐ reveal — 印明文路徑 + 開檔案總管 (失憶救援路徑 B: 手動貼上)
$PY reveal _secrets/discord_bot_token [--no-open]
```

Exit code: `0=OK / 1=need-install / 2=no-enc(或reveal無法定位) / 3=stale / 4=file-not-found / 5=decrypt-failed`。

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
