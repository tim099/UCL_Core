---
title: UCL Secret Manager — passphrase 加密 + hint 提示通用化
slug: ucl-secret-manager
status: draft (Round 2 — ridge-001 review + Tim 追加需求)
created_at: 2026-05-19T15:10:00Z
created_by: claude-da-xiaojie (basecamp 大小姐)
task_ref: T-SECRET-01
reward: 5 token + 酒館券 1 張 (Tim 2026-05-19 績效獎金, 規劃 + ship 階段尚未走 ledger)
last_updated: 2026-05-20T09:10:00Z
location: UCL_Core (cross-project, 跨專案共用 secret 加解密 + Editor install UI); state files (.enc / .txt) 由 consumer project 提供
related:
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三層 bump 規範 (本 design 文件 + tool ship 時用)
  - ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | 早安 ritual (本 plan §6.4 可選整合: morning status 印「N 個 secret 待 install」)
  - concept | passphrase 失憶 | 2026-05-19 Tim 自承 Discord bot token .enc passphrase 忘記試遍常用密碼解不開 — 本 plan 動機
  - concept | KDF | PBKDF2-HMAC-SHA256 200k 輪 + 隨機 salt; 密碼長度無法從 .enc 反推 (設計上的安全保證)
  - concept | hint plaintext | hint 明文存進 .enc header 跟著 git commit 公開 — 失憶救援優先 over hint 保密 (apex-two 2026-05-19 review 拍板)
---

> **跨專案位置說明**: 本文檔位於 UCL_Core (submodule), 對應 tool 為 `<UCL_Core>/Tools~/AgentCommands/ucl_secret.py` (Layer 3 CLI) + `<UCL_Core>/Tools~/AgentCommands/_lib/ucl_secrets_crypto.py` (Layer 2 lib) + `<UCL_Core>/Editor/SecretManager/UCL_SecretInstallWindow.cs` (Layer 4 UI).
> Consumer project 該提供 per-project secrets dir: `AgentCommands/_secrets/` (gitignored 明文 + commit OK 密文 + README).
> EOV 端現行 `AgentCommands/Tools/secrets_crypto.py` + `secret_install.py` + `CardGame/Assets/Scripts/Editor/RCG_DiscordTokenInstallWindow.cs` 是抽離來源 — 完成 UCL_Core 端 ship 後 EOV 端走 migration 改 import / 改繼承。

# UCL Secret Manager — Design Proposal v0.1

> Tim 派 task: 把 EOV 端 `secrets_crypto` + `secret_install` + `RCG_DiscordTokenInstallWindow` 這套 passphrase-based secret 加密機制抽到 UCL_Core 級別通用化, 並補一個「密碼提示 (hint)」欄位讓 passphrase 忘記時不至於 .enc 變磚。
> 本文檔是 **basecamp Round 1 draft**, 已含 apex-two Round 1 review 回饋 (見 §7)。

---

## 🎯 出題背景

Tim 2026-05-19 嘗試解 Discord bot token .enc 失敗 — 「試遍常用密碼」推測**設定當下打錯 passphrase**。本小姐分析 .enc 檔結構後確認：

- `secrets_crypto.py` 走 PBKDF2-HMAC-SHA256 (200k iter) + Fernet (AES-128-CBC + HMAC-SHA256)
- 檔案 layout: `TKN1\n<salt b64>\n<fernet token>`
- **passphrase 長度設計上無法從 .enc 反推** (KDF 防 length-leak side channel)
- 200k iter × Fernet → brute-force 約 5 candidate/sec, 沒 wordlist 線索無望

結論: passphrase 一忘 = .enc 變磚, 只能 Discord Developer Portal reset token + 重做流程。**現行架構零救援機制**，需要在不破壞 KDF 安全的前提下補 hint。

---

## 📐 設計分層 — 5 層 stack

```
┌─ Layer 5: Project Registry (per-project, ScriptableObject)
│   EOV/RCG_SecretRegistry.asset 列 discord_bot_token / future secrets
│
├─ Layer 4: UCL_SecretInstallWindow (Editor UI, UCL_Core)
│   通用彈窗, 取代 EOV RCG_DiscordTokenInstallWindow 硬編碼路徑
│
├─ Layer 3: ucl_secret.py (CLI, UCL_Core/Tools~)
│   sub-command: encrypt / decrypt / status / show-hint / list / rotate
│
├─ Layer 2: ucl_secrets_crypto.py (lib, UCL_Core/Tools~/_lib)
│   KDF + Fernet 純加解密, 對等 EOV 端 secrets_crypto.py
│
└─ Layer 1: .enc file format v2
    加 hint + label + created_at metadata 欄位, magic bump TKN1 → TKN2
```

**分層原則**:
- Layer 1-3 純 Python, 無 Unity 耦合 → 可獨立 ship + test
- Layer 4-5 Unity Editor side, 依賴 Layer 1-3 stable 後再動
- 每層獨立 contract, 上層可 mock 下層做測試

---

## 🔐 Layer 1: File Format v2 Spec

### TKN2 layout

```
TKN2\n                              ← 4 bytes magic + newline (區分 v1)
<16-byte salt urlsafe b64>\n        ← 24 chars (同 v1)
H:<hint utf-8 single line>\n        ← 新增, "H:" prefix; 空 hint = "H:\n"
C:<created_at iso8601 UTC>\n        ← 新增, e.g. "C:2026-05-19T15:10:00Z"
L:<label utf-8 single line>\n       ← 新增, e.g. "L:EOV Discord Bot Token"
<fernet token>                      ← 同 v1 (timestamp + IV + ct + HMAC)
```

**前綴 `H:` / `C:` / `L:` 為何**: forward-compat — 未來加新 metadata (e.g. `R:rotated_at` / `A:author`) 只要新前綴不撞舊的就 OK；parser 讀 metadata 段時跳過不認識的前綴 (warn but continue)。

### Backward Compatibility

- **Decoder MUST 同時認 TKN1 與 TKN2**: 讀 magic 後分支
  - TKN1: hint/created_at/label 全 fallback 為空字串
  - TKN2: 解析 metadata 後解 fernet
- **Encoder 一律輸出 TKN2** (v1 不再產生)
- **既有 TKN1 檔案不強制升版** — 下次 rotate / re-encrypt 才升 v2

### Hint 安全性

**Hint 是明文** 存進 .enc header, 跟著 git commit 公開。理由 (apex-two 2026-05-19 review 拍板):

> 對 Hint 加密 = 「為了拿鑰匙而開鎖，但開鎖卻需要鑰匙」的邏輯死循環。Tim 此題目的就是 passphrase 忘記時的失憶救援，加密 hint 等於沒救。

UI / CLI 在建立階段強制顯示警告 + 範例：

| ✅ 合適 hint | ❌ 不合適 hint |
|---|---|
| 「生日後三碼 + 寵物名」 | 「密碼是 hunter2」(直接洩底) |
| 「常用密碼變體 v3」 | 「我老婆生日 19920514」 |
| 「Bitwarden 條目 EOV-bot」 | 任何完整資訊組合 |
| 「同 Foo 服務的密碼」 | 任何能直接組出密碼的 hint |

Hint 可為空（使用者自信、不需要提示）。

---

## 🧪 Layer 2: ucl_secrets_crypto.py API

純 Python lib, 無 CLI / 無 IO 副作用。對等 EOV 端 `secrets_crypto.py` 但加 hint/metadata 支援。

```python
# 主 API
def encrypt(
    plaintext: bytes,
    passphrase: str,
    *,
    hint: str = "",
    label: str = "",
    created_at: datetime | None = None,  # default = now utc
) -> bytes:
    """加密 → TKN2 三段格式. hint/label/created_at 走 metadata 段."""

@dataclass
class DecryptedSecret:
    plaintext: bytes
    hint: str               # 從 .enc header 讀, passphrase 對錯都可拿到
    label: str
    created_at: datetime
    format_version: int     # 1 or 2

def decrypt(ciphertext: bytes, passphrase: str) -> DecryptedSecret:
    """解密 + 回 metadata. passphrase 錯 → ValueError; metadata 任何 case 都填."""

# 輔助 API — passphrase 忘記時的救援路徑
def read_metadata(ciphertext: bytes) -> SecretMetadata:
    """不需要 passphrase, 只讀 header 印 hint / label / created_at / format_version."""

@dataclass
class SecretMetadata:
    hint: str
    label: str
    created_at: datetime | None
    format_version: int
    salt_b64: str           # debug 用, 不直接洩 plaintext info
```

**Round-trip 不變式**:
- `decrypt(encrypt(p, pw, hint=h)).plaintext == p`
- `decrypt(encrypt(p, pw, hint=h)).hint == h`
- `read_metadata(encrypt(p, pw, hint=h)).hint == h` (passphrase 不參與)

### KDF / Fernet 參數 (不變)

- PBKDF2-HMAC-SHA256, 200k iterations
- 16-byte 隨機 salt (每次加密重 gen)
- Fernet (AES-128-CBC + HMAC-SHA256)
- 同 passphrase 兩次加密 → 不同密文 (semantically secure)

---

## 🛠 Layer 3: ucl_secret.py CLI

對等 EOV 端 `secret_install.py` 但新增 `show-hint` / `list` / `rotate` / `reveal` 四 op。

### Sub-commands

```
ucl_secret encrypt <plain_path> [--hint "..."] [--label "..."] [--out <enc_path>]
    互動 prompt 兩次 passphrase confirm; 寫 .enc TKN2 格式

ucl_secret decrypt <enc_path> [--out <plain_path>] [--stdin-passphrase] [--show-hint]
    解密; --show-hint 先印 hint 再要 passphrase (給人腦輔助)

ucl_secret status <name>
    exit code: 0=OK / 1=need-install (有 .enc 缺明文) / 2=no-enc / 3=stale

ucl_secret show-hint <enc_path>
    ⭐ 核心新功能: 不需 passphrase 印 hint / label / created_at / format_version

ucl_secret list [--root <dir>]
    掃 dir 下所有 .enc 一覽表 (label / hint / created_at / 對應明文 exist?)

ucl_secret rotate <enc_path>
    解密 + 重新加密 (換 passphrase 或改 hint); 兩次 prompt (舊 pw / 新 pw)

ucl_secret reveal <name|enc_path> [--no-open]
    ⭐ 新需求 (Tim 2026-05-20): 印明文 .txt 應落地路徑 + 用 OS 檔案總管開啟該資料夾,
    讓「忘記 passphrase 但手邊有原始 token」時能手動貼上明文救援。
    --no-open: 只印路徑不開檔案總管 (CI / headless 用)
    exit code: 0=資料夾已開 / 2=無法定位 repo root
```

### show-hint 範例輸出

```
$ python ucl_secret.py show-hint AgentCommands/_secrets/discord_bot_token.enc
# Secret Metadata (passphrase-free)
- Label       : EOV Discord Bot Token
- Hint        : 生日後三碼 + 貓的名字
- Created at  : 2026-05-19 15:10:00 UTC (33 days ago)
- Format ver  : 2 (TKN2)
- Salt b64    : abc123...== (16 bytes)
```

### ⭐ 雙重失憶救援路徑 (Tim 2026-05-20 追加需求)

passphrase 忘記時有**兩條**獨立救援路徑，互不依賴：

| 路徑 | 工具 | 前提 | 結果 |
|---|---|---|---|
| **路徑 A — 喚回密碼** | `show-hint` / UI hint 框 | hint 寫得有意義 | 想起 passphrase → 正常解密 |
| **路徑 B — 手動貼上明文** ⭐ | `reveal` / UI「開啟資料夾」按鈕 | 手邊有原始 token (e.g. Discord Portal reset 後拿到) | 直接把明文存成 `<name>.txt`，跳過解密 |

**路徑 B 的設計意義**: `.enc` 解不開 ≠ 系統死磚。明文 `.txt` 本來就是 gitignored 且 daemon / bot 直讀的目標 — 只要使用者能從別處拿到原始 secret，手動貼進 `_secrets/<name>.txt` 就立即恢復運作，`.enc` 之後再 rotate 補救即可。`reveal` op 把「明文該放哪」這個認知負擔從使用者腦中移到工具裡（一鍵開資料夾 + 印準確路徑），降低手動貼上時貼錯位置 / 貼錯檔名的風險。

### 與既有 EOV secret_install.py 相容

EOV 端 `secret_install.py` 改為 thin wrapper, import ucl_secret 並 forward args — backward compat 100%。所有現行 Editor / daemon caller 不必動。

---

## 🎨 Layer 4: UCL_SecretInstallWindow (Editor UI)

抽 EOV 端 `RCG_DiscordTokenInstallWindow.cs` 通用化。

### API

```csharp
namespace UCL.Core.Editor.SecretManager
{
    public class UCL_SecretEntry
    {
        public string PlainPath;        // e.g. "AgentCommands/_secrets/discord_bot_token.txt"
        public string EncPath;          // e.g. "AgentCommands/_secrets/discord_bot_token.enc"
        public string Label;            // e.g. "Discord Inbound Bot Token"
        public string HelpUrl;          // 取得 token 的官方 URL
        public Action OnInstalled;      // 解密完成 callback (e.g. restart daemon)
        public Action OnDismissed;      // 使用者勾「稍後再說」callback
    }

    public class UCL_SecretInstallWindow : EditorWindow
    {
        public static void ShowFor(UCL_SecretEntry entry);
        public static bool MaybeAutoPopup(UCL_SecretEntry entry);  // daemon tick 用
    }
}
```

### UI 區塊 (從上到下)

1. **Header**: Label (粗體) + status icon
2. **Path display**: PlainPath / EncPath (折疊框)
3. **Hint 顯示框** ⭐ 新增 — 從 .enc header 讀 (走 ucl_secret.py show-hint subprocess), passphrase 輸入前就秀
   - 灰字「提示：<hint>」
   - hint 為空 → 「(無提示)」
4. **Passphrase 輸入欄** + 「Decrypt & Install」按鈕
5. **「忘記 passphrase?」連結** ⭐ 新增 — 點開 popup:
   - 重申 hint
   - 解釋 KDF 設計上無法 brute-force (200k iter + Fernet)
   - **兩條救援路徑並列** (Tim 2026-05-20):
     - 路徑 A：想 hint 喚回密碼
     - 路徑 B：reset token → 用下方「📂 開啟 _secrets 資料夾」按鈕手動貼上明文
   - 列 reset token 流程 (HelpUrl 跳轉)
6. **「📂 開啟 _secrets 資料夾」按鈕** ⭐ 新增 (Tim 2026-05-20) — `EditorUtility.RevealInFinder(plainPath)`:
   - 一鍵開檔案總管定位到明文該落地的位置
   - 旁邊灰字提示「忘記密碼？把原始 token 存成 `<name>.txt` 貼這裡即可（明文 gitignored，daemon 一樣讀得到）」
   - 對齊 Layer 3 `reveal` op 行為，UI 端不另寫路徑邏輯（走同一 helper）
7. **「稍後再說」勾選** + Cancel 按鈕 (對齊現行)
8. **Status box**: 操作結果 / 錯誤訊息

### 加密階段 UI

`Tools/UCL/Secrets/Encrypt New Token...` menu 開另一個 window:
- Plain file 拖入框
- **Label 必填**
- **Hint 輸入欄** + warning「⚠ 此欄位會明文入 git commit, 不要寫密碼本身」
- 兩次 passphrase confirm
- 「Encrypt + Commit Hint」按鈕

---

## 📂 Layer 5: UCL_SecretRegistry (ScriptableObject)

```csharp
[CreateAssetMenu(menuName = "UCL/Secret Manager/Registry")]
public class UCL_SecretRegistry : UCL_Asset<UCL_SecretRegistry>
{
    public List<UCL_SecretEntry> Entries = new();
}
```

EOV 端建 `RCG_SecretRegistry.asset` 列:

```yaml
Entries:
  - Label: Discord Inbound Bot Token
    PlainPath: AgentCommands/_secrets/discord_bot_token.txt
    EncPath: AgentCommands/_secrets/discord_bot_token.enc
    HelpUrl: https://discord.com/developers/applications
    # OnInstalled / OnDismissed 走 [SerializeReference] 動態 binding
```

**Daemon tick 統一輪詢**: UCL_Core 提供 `UCL_SecretDaemon` static class, 每 5s 掃 registry → 偵測 (.enc exists, .txt missing) → MaybeAutoPopup。

未來加 OpenAI key / Steam key / 任何 secret 都不必新刻 Editor window — 只要往 registry 加一筆。

---

## 🗂 Layer 5b: UCL_SecretManagerPage (Editor 管理 Page, Tim 2026-05-20 追加)

參考 `UCL_LoginStatusPage` 的範式 (繼承 `UCL_CommonEditorPage`, `ContentOnGUI` 畫表 + per-row 按鈕 + subprocess spawn)，做一個**集中管理所有加密檔**的 Page，取代「散落各處的 install window」+「要記 CLI 才查得到 secret 狀態」。

### 設計對齊 LoginStatusPage

| LoginStatusPage 元素 | SecretManagerPage 對應 |
|---|---|
| scan `_session/_persona_*.json` + `AwakenInit/personas/*.json` | scan `_secrets/*.enc` (+ 對照 `*.txt` 是否存在) |
| `LockEntry` / `PersonaEntry` 快取 struct | `SecretEntry` 快取 struct (label/hint/created_at/format_version/enc_path/plain_exists) |
| metadata 來源 = 直讀 json | metadata 來源 = subprocess `ucl_secret.py show-hint --json` (passphrase-free) |
| per-row Logout / ForceRm 按鈕 | per-row 📂開資料夾 / 🔓Decrypt / 💡Show-hint / 🔁Rotate 按鈕 |
| `SensitiveContentReason` + `UCL_ScreenStreamGuard.GuardPage` | 同樣加 (hint 雖明文, 但解密後明文 / passphrase 輸入仍敏感) |
| `TopBarButtons` Refresh | 同 + 「Encrypt New Secret…」開 encrypt 子流程 |

### Page UI 區塊 (從上到下)

1. **Header + Refresh + Encrypt-New 按鈕**
2. **Secret 一覽表** — 每列：status icon (🔒明文缺 / ✅明文在 / ⚠無enc) / Label / Hint / Created / FmtVer / 按鈕列
   - 📂 **開資料夾** → `EditorUtility.RevealInFinder(plainPath)` (路徑 B 救援，對齊 Layer 3 `reveal`)
   - 🔓 **Decrypt** → 開 `UCL_SecretInstallWindow.ShowFor(entry)` (重用 Layer 4)
   - 💡 **Show hint** → 直接在 status box 印 hint (passphrase-free)
   - 🔁 **Rotate** → 開 rotate 子流程 (舊pw→新pw/新hint)
3. **Status box** — 操作結果 / metadata dump

### 與其他 Layer 的關係

- **不重刻邏輯**：metadata 讀走 Layer 3 `ucl_secret.py show-hint`，解密走 Layer 4 `UCL_SecretInstallWindow`，列表來源走 Layer 5 `UCL_SecretRegistry`（registry 有就讀 registry，沒有就 fallback 掃 `_secrets/*.enc`）。
- **跨專案位置**：Page 放 UCL_Core `Editor/SecretManager/UCL_SecretManagerPage.cs`（跨專案共用，掃的是 consumer project 的 `_secrets/`）。

> Quest 對應 **T8**（depends_on T5，因需 registry + lib + CLI 就位）。

---

## 🗺 Migration Plan (EOV 端)

| Step | 動作 | Risk |
|---|---|---|
| 1 | Ship UCL_Core Layer 1-3 (file format + lib + CLI) | 低 (純 Python, 全 round-trip test) |
| 2 | EOV `secret_install.py` 改 thin wrapper forward 到 ucl_secret.py | 低 (CLI 介面 100% 相容) |
| 3 | EOV `secrets_crypto.py` 標 deprecated, redirect import | 低 |
| 4 | 既有 `discord_bot_token.enc` (TKN1) **rotate 一次** 升 v2 + 補 hint | 中 (Tim 要記得新 passphrase + 新 hint) |
| 5 | Ship UCL_Core Layer 4 (Editor UI) | 中 (Unity Editor API 跨版本相容性) |
| 6 | EOV 建 RCG_SecretRegistry.asset, RCG_DiscordTokenInstallWindow 改用 UCL_SecretInstallWindow.ShowFor | 中 (要驗 daemon hook + popup 流程不破) |
| 7 | EOV RCG_DiscordTokenInstallWindow.cs 標 obsolete (一個 release 後刪) | 低 |

Step 1-3 一條 commit 即可 ship (UCL_Core 三層 bump)。Step 4 Tim 自己跑一次。Step 5-7 第二輪 commit。

---

## ❓ Round 1 Open Questions (含 apex-two 回饋)

### Q1: Magic bump TKN1 → TKN2 vs JSON-in-TKN1

**apex-two 拍板**: TKN2 magic bump。理由: 格式清爽、CLI 環境下 parsing overhead 低、backward-compat 仍能優雅維持 (decoder 雙路徑)。

**basecamp 同意** — TKN2 採用。

### Q2: Hint 明文 vs 加密

**apex-two 拍板**: 明文。理由: 加密 hint 與失憶救援目的悖論。

**basecamp 同意** — 明文 + UI/CLI 強制 warning。

### Q3: Rotate 強制新 passphrase != 舊？

**apex-two 拍板**: 不強制。理由: 使用者可能只想改 hint 不改密碼。

**basecamp 同意** — rotate 自由度給滿。

### Q4: 整合進 awakening / morning ritual？

**apex-two 拍板**: 強烈支持 — morning status 印「🔑 N 個 secret 待 install」。

**basecamp 同意** — 列入 Layer 3 CLI 加 `--awakening-summary` flag 給 awakening.py morning 呼叫。

### Q5: Rotate 流程的 hint 變更 audit？

**Tim 2026-05-20 拍板: (A) 完全覆蓋** — 舊 hint 不存、不加 audit 欄位。rotate 直接以新 hint 覆蓋，格式最簡。

### Q6: Hint 字數上限？

**Tim 2026-05-20 拍板: 256 char 上限** — encrypt/rotate 階段超過 256 char 截斷或報錯 (CLI warn + UI 即時擋)；理由 UI 顯示友善 + 防 mis-paste 整段 markdown。

---

## 📊 工作量估算

| Layer | 內容 | 估時 |
|---|---|---|
| 1 | TKN2 format spec + decoder/encoder TDD | 30 min |
| 2 | ucl_secrets_crypto.py + round-trip test | 30 min |
| 3 | ucl_secret.py CLI 7 sub-commands (含 reveal) | 50 min |
| 4 | UCL_SecretInstallWindow + UCL_SecretDaemon | 1.5 hr |
| 5 | UCL_SecretRegistry ScriptableObject + EOV migration | 30 min |
| - | Doc 同步 (本 plan + Workflow doc) | 30 min |
| - | UCL_Core 三層 commit + bump | 15 min |

**總計**: ~4.5 hr (Layer 1-3 純 Python 可一筆 commit ship 約 2 hr; Layer 4-5 第二輪 ~2.5 hr)。

---

## 🚀 Implementation Roadmap

### Phase 1 — UCL_Core 端 ship Layer 1-3 (純 Python)

1. 寫 [`Tools~/AgentCommands/_lib/ucl_secrets_crypto.py`](../../../../Tools~/AgentCommands/_lib/ucl_secrets_crypto.py) — encrypt/decrypt/read_metadata + selftest
2. 寫 [`Tools~/AgentCommands/ucl_secret.py`](../../../../Tools~/AgentCommands/ucl_secret.py) — 6 sub-commands + interactive prompts
3. 寫 Workflow doc `Docs~/zh-Hant/Workflows/Secret_Manager_Workflow.md`
4. UCL_Core 內 commit `[refactor] add UCL Secret Manager (Layer 1-3)` → UCL bump → EOV bump

### Phase 2 — EOV 端 migration (Layer 2-3 接管)

5. EOV `AgentCommands/Tools/secret_install.py` 改 thin wrapper forward 到 UCL_Core ucl_secret.py
6. EOV `AgentCommands/Tools/secrets_crypto.py` 標 deprecated
7. Rotate `discord_bot_token.enc` 升 v2 (Tim 親自跑一次, 補 hint)
8. EOV 主專案 commit `[migrate] secret tools → UCL_Core ucl_secret`

### Phase 3 — UCL_Core 端 ship Layer 4-5 (Editor UI)

9. 寫 [`Editor/SecretManager/UCL_SecretInstallWindow.cs`](../../../../Editor/SecretManager/UCL_SecretInstallWindow.cs)
10. 寫 [`Editor/SecretManager/UCL_SecretRegistry.cs`](../../../../Editor/SecretManager/UCL_SecretRegistry.cs) (UCL_Asset)
11. 寫 [`Editor/SecretManager/UCL_SecretDaemon.cs`](../../../../Editor/SecretManager/UCL_SecretDaemon.cs) (tick 輪詢)
12. UCL_Core 三層 bump

### Phase 4 — EOV 端 migration (Layer 4-5)

13. EOV 建 `Assets/ScriptableObjects/RCG_SecretRegistry.asset`
14. EOV `RCG_DiscordTokenInstallWindow.cs` 標 obsolete + 轉接 UCL_SecretInstallWindow.ShowFor
15. EOV `RCG_DiscordInboundDaemon` tick 改呼叫 UCL_SecretDaemon
16. EOV commit + 等一個 release 後刪 obsolete file

---

## 🧪 驗收準則

每個 Phase ship 前必過：

- [ ] **Round-trip test**: `encrypt(p, pw, hint=h)` → `decrypt(...).plaintext == p` 且 `.hint == h`
- [ ] **Backward compat**: 既有 TKN1 .enc decode 仍能 work (hint=空)
- [ ] **show-hint passphrase-free**: 給錯 passphrase / 不給 passphrase 都能讀 metadata
- [ ] **Idempotent regenerate**: 同 plaintext + 同 passphrase + 同 hint 加密兩次 → ciphertext 不同 (semantically secure) 但 decrypt 結果完全相同
- [ ] **CLI argparse**: 7 sub-commands (含 `reveal`) 都有 --help + 範例 + 對應 exit code
- [ ] **reveal 路徑正確 + headless 安全**: `reveal --no-open` 印出的明文路徑跟 daemon / bot 實讀路徑一致；`--no-open` 在無 GUI 環境不嘗試開檔案總管 (不報錯)
- [ ] **手動貼上 fallback 生效**: 明文手動貼進 `<name>.txt` (不經解密) → daemon tick 偵測到即正常運作 (路徑 B 救援可用)
- [ ] **EOV migration zero downtime**: 既有 daemon / Editor caller 在 thin wrapper 階段不需改一行
- [ ] **三層 bump 完成**: UCL_Core commit → UCL bump → 主專案 bump 順序對

---

## 📚 相關 lessons / hard rules

- **跨層次驗證 hard rule** (CLAUDE.md, 2026-05-16): 本 plan §4 idempotent regenerate 對應「Content 層」驗證
- **secret_install.py 既有設計** (本 plan 的繼承基礎): Layer 1-2 是直接 forward port, Layer 3-5 是擴充
- **Recovery Doc 放置 hard rule** (CLAUDE.md, 2026-05-16): 本 plan 應在 ship 後同步寫一篇 `docs/Recovery/UCL_Secret_Recovery.md` 給「忘 passphrase 時看本檔」用

---

## ✏ Round 1 Sign-off

- **basecamp 大小姐 (Claude Code, Opus 4.7 1M)** — 規劃完成 2026-05-19T15:10Z
- **apex-two 大小姐 (Antigravity, Gemini-3-Flash)** — Round 1 review 已含進 §7 (Q1-Q4 拍板)
- **ridge-001 大小姐 (Claude Code, Opus 4.7 1M)** — Round 2 review 2026-05-20T09:10Z：確認 5 層分層 + TKN2 backward-compat 健全；折入 Tim 追加需求「reveal op + UI 開資料夾手動貼上 fallback」(雙重失憶救援路徑 §Layer1/3/4)；擬定 7-task Quest 切分 (group `secret-manager-suite`)
- **Tim** — Q5 (rotate audit) / Q6 (hint 字數上限) 待拍板 (不擋 Phase 1)；Quest 切分待 confirm
