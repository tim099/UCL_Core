---
title: UCL_AutoCommitPage — 自動 Commit 頁
last_updated: 2026-08-22
---

# UCL_AutoCommitPage

把**機器自動生成的檔**分群，**按鈕觸發**、每群各自成一筆 commit，**訊息自動生成**。
兩種掃描對象共用同一套機制：

| 模式 | 掃什麼 | 解的痛點 |
|---|---|---|
| **AgentCommands 本層** | 該 repo（預設 `UCL_RepoPath.AgentCommandsDir`，可改） | Treasury 帳本、酒館訊息、inbox cursor、bartender state 整天在長，人工 commit 的成本是「分類」不是「打字」 |
| **Persona 信件庫** | `letters/*` 底下每個 git repo（各自是巢狀 submodule） | 收尾 commit 之後才落地的**系統信**與**別人投遞的畫像** —— 落地時該 persona 已下線，沒有人會 commit 它們 |

> [!IMPORTANT]
> **按鈕觸發，不是背景全自動**（Tim 2026-08-07 拍板）。按下去之前，
> 分群結果與完整檔案清單全部攤在畫面上（每群可展開逐檔看）。

## 與 git_commit.py / ucl-commit 的分工

本頁走**純 `git commit`**（本機 git 身分），**不走** `git_commit.py` ——
那支工具的 trailer / 酒館公告 / 領薪是給「有作者的工作產出」用的；
本頁提交的是機器生成的狀態殘渣，掛誰的名字領誰的薪都是假帳。
**agent 自己的工作 commit 照舊走 ucl-commit skill**，兩條路不混。

## AgentCommands 模式的分群規則（AgentGroupDefs，順序即優先序）

| 群 | 命中 | 訊息 | 預設 |
|---|---|---|---|
| 酒館訊息 | `ChatTavern/rooms/` | `[chat] sync tavern messages & inbox (auto)` | ✅ |
| Treasury | `Treasury/` | `chore(treasury): sync ledger & account state (auto)` | ✅ |
| Runtime state | `ChatTavern/` 其餘、`AwakenInit/`、`Canvas/`、`Inbox/` | `chore(runtime): sync agent runtime state (auto)` | ✅ |
| 巢狀 submodule pointer | `git submodule status` 的路徑集合 | `chore(submodule): bump nested submodule pointers (auto)` | ⛔（一次性勾選，不持久化） |
| 未分類 | 其餘全部 | `chore(misc): sync unclassified changes (auto)` | ⛔（一次性勾選，不持久化） |

- `[chat]` 獨立 commit 是專案硬規則（見 ucl-commit skill 的檔案分類）—— 所以**本層與 persona 信件庫的**
  規則不開放 UI 編輯。⚠ 2026-08-21 起**其他 repo** 可自帶 `.ucl_autocommit.json` 宣告自己的分群，
  詳見下方「Submodule 自動提交設定」一節。
- **submodule pointer 預設不勾**：那些 pointer 指向別人（其他 persona 信件庫）的 commit，
  對方沒 push 就 bump，別人 pull 會拿到拿不到的 hash。確認對方已 push 再勾，且勾選只活一次。
- **未分類預設不勾**：分類規則沒認出來的檔，不該被「自動」二字順手帶走。

## Submodule 自動提交設定（`.ucl_autocommit.json`，2026-08-21 Tim 拍板）

> 📖 **完整步驟 SOP**（加入管理／欄位判準／地板／探針驗收）→ [`AutoCommit_Config_Workflow.md`](../Workflows/AutoCommit_Config_Workflow.md)

上面兩組規則寫死在 `UCL_AutoCommitRules`。而**每接一個新的資料 repo 就要回頭改 UCL_Core
加一組寫死的**，所以改成：該 repo 在自己根目錄放一份設定檔宣告自己的分群。

- **設定檔是加入的唯一憑據** —— `mode=submodules` 掃 `.gitmodules`，只收帶設定檔的 repo，
  沒有就跳過（**不猜規則**）。判準刻意不是「是不是 submodule」：那會把所有 persona 信件庫
  一起掃進來，而那些 repo 的分群規則不住這裡。
- 頁面上多一個 **「⚙ Submodule 自動提交設定」** 折疊區：
  - **下拉選單選目標 submodule** —— 列出 `.gitmodules` 裡**全部**的 submodule，不只有已設定的那些（否則沒有入口去建立新的）。前綴標示狀態：`✅` 已啟用／`⏸` 有設定但停用／`—` 尚無設定檔／`⛔` 設定壞掉
  - **沒有設定檔的選了會出現「➕ 建立設定檔（預設停用）」** —— 「選了它」不等於「同意開始自動 commit 它」，所以同意是另一個動作
  - 選中的那份直接改欄位（`Enabled` 開關也在裡面）、可存檔、可放棄改動
  - ⚠ 讀不出來的設定**不會被自動覆蓋** —— 覆蓋掉的是別人寫的東西，而那筆改動沒有地方留得住
  存檔前跑 `Validate()`，不合法就**停用存檔按鈕並逐條列出原因**。
- 讀檔只發生在頁面 `Init` 與「重新載入」按鈕 —— **`Draw` 裡零 IO**（IMGUI 的 Layout/Repaint
  是兩個 pass，Draw 裡碰磁碟會讓兩趟看到不同東西）。

### 格式

```json
{
  "Name": "Chess",
  "Groups": [
    {
      "Key": "games",
      "Label": "對局狀態（games/<idx>.json）",
      "MatchPrefixes": [ "games/" ],
      "Message": "chore(chess): sync game state (auto)",
      "DefaultOn": true
    }
  ]
}
```

| 欄位 | 意義 |
|---|---|
| `Key` | 群 key（commit 分組用）。不可叫 `__other` / `__subptr`（保留） |
| `Label` | 畫面顯示名。作者自己寫，不進多語系表 |
| `MatchPrefixes` | **相對 repo root 的正斜線前綴**清單，任一命中即屬本群。空字串不合法（會吃掉整個 repo） |
| `Message` | commit 訊息主體（檔數統計由呼叫端補在後面） |
| `DefaultOn` | 沒指定 `groups=` 時是否納入 |

### 地板（設定檔掀不動的部分）

| 保證 | 靠什麼保證 |
|---|---|
| ephemeral 檔永不進候選 | `Classify` 的**判定順序**：subptr → ephemeral → 分群。不是「呼叫端記得先檢查」 |
| `__other` / `__subptr` 不自動收 | 兩者不是 `GroupDef`，不在任何預設集合裡 |
| 錯配一眼可驗 | 只吃前綴清單、不吃 regex —— 設定檔比 code **更受限** |
| 寫入前擋下壞設定 | `Save()` 先跑 `Validate()`，不合法直接丟例外不寫檔 |

⚠ 沒有進任何群的檔會落 `__other` ⇒ **永不自動收**。Chess 的 `RuleBook.md` 就刻意如此：
它有作者，該走有 trailer 的 commit。

## Persona 信件庫模式的分群規則（PersonaGroupDefs，順序即優先序）

| 群 | 命中 | 訊息 | 預設 |
|---|---|---|---|
| 信件通道 | `mailbox/`（系統信・掛號信投遞）、`outbox/`（寄件存證） | `[mailbox] 收信件通道檔（系統信／投遞／存證）(auto)` | ✅ |
| 他人投遞的畫像 | `portraits/` | `[portraits] 收他人投遞的畫像 (auto)` | ✅ |
| 機械維護檔 | `_latest.md`、`cmd/.gitignore` | `[data] 同步機械維護檔（指標／目錄 ignore）(auto)` | ✅ |
| 未分類／她自己寫的 | 其餘全部（`wakes/` `fragments/` `keys/` `sketchbook/` `relationship/`…） | `[misc] 同步未分類檔 (auto)` | ⛔（一次性勾選） |

**分界不是檔案類型，是作者是誰。** 投遞件（別人寫的、系統寫的）與機械維護檔可以自動收；
她自己寫的信、碎片、見叢、素描本要掛她的名字、走她自己的收尾 commit ——
被別人的自動化順手帶走等於**替她簽名**。

### 三道守衛

- **在線的 persona 預設不勾**（判準是 `_session/_persona_*.json` lock 未過期，
  不是 registry 的 `status` 欄 —— 登出沒走完時 status 會停在 online）。
  她可能正在寫，而動別人正在寫的東西**不會有錯誤訊息**，只會靜默清掉工作。
  勾選**不持久化、每次掃描重算**（上一次的「我認了」不延續到這一次）。
- **detached HEAD 硬擋**（勾不動）—— 那裡 commit 出來沒有分支指到它，
  下次 checkout 只剩 reflog 找得到。與 ucl-commit skill 同一條規矩。
- **呼叫前 index 已有 staged 檔 → 該 repo 硬擋**（BUG-30，2026-08-22）。分群只決定
  「工具 stage 哪些檔」；index 裡本來就有的會被併進第一個群、掛上那個群的訊息，
  而 commit 會成功 ⇒ 沒有人會知道。**先自己 commit 或 unstage 再來**。
  另外提交本身走 `--pathspec-from-file`（只提交該群路徑）＋提交後與分群清單對帳。

### 父層 pointer 不會自己動

persona repo commit 完，AgentCommands 的 submodule pointer 仍停在舊 hash。
要 bump：切回「AgentCommands 本層」重掃 → submodule pointer 那一群會出現（一次性勾選）。
commit 完的報告會提醒這一句 —— bump 與否仍是人的決定。

## ephemeral 永遠排除（不進候選，連勾的機會都沒有）

`*.log`、`*.tmp`、`_last_op.md`、`_last_view.md`、`_active_waits.json`、`_wait_*`、
`pending.trigger`、`DebugLogs/`。這些是執行期瞬時檔，commit 它們只會製造 history 噪音。

## 安全設計

- **絕不 `git add -A`** —— 具名逐檔 stage（每批 40 個路徑，防 Windows 命令列 32k 上限）。
- stage 失敗 → `git reset` 退掉該群，不讓殘留的 staged 檔混進下一群。
- commit 訊息走 `-F <暫存檔>` 不走 argv —— 長文走檔案，不經 shell 解析層（Bash 反引號雙殺的教訓，C# 版）。
- 只 commit 本層：**不 push、不動父層 pointer** —— 外層 bump 與 push 是人的決定。
- 每條 git 指令登記 `UCL_ProcessRegistryService`（tag `auto_commit_git`）；
  底層共用 [`UCL_GitCli`](UCL_GitSubmoduleSyncPage.md)（與 Submodule 同步頁同一個封裝）。

## 頁面操作

| 區塊 | 說明 |
|---|---|
| 掃描對象 | 兩顆模式按鈕（AgentCommands 本層 / Persona 信件庫）—— 切換即重掃，選擇持久化 |
| Repo 根目錄 | **僅 AgentCommands 模式**：預設 `UCL_RepoPath.AgentCommandsDir`，可改 / 按「AgentCommands」還原（persona 模式的 letters 根委派 `UCL_LettersPath.Root`，不可改） |
| repo 列表 | **僅 persona 模式**：一庫一格（勾選、分支、🟢 在線、⛔ 擋下理由），乾淨的庫不列 |
| 群組列表 | 每群一格：勾選、檔數、訊息預覽、展開逐檔清單 |
| Commit 勾選群組 | `UCL_OptionPage` 二次確認（列出每筆 commit 的訊息與檔數）後依序執行 |
| 報告 | 每群一行 ✓ SHA / ✗ 原因；完成後自動重掃（不蓋掉報告） |

設定存 `EditorPrefs`（JSON）；群組開關記「被關掉的」—— 新增群組時舊設定不會把它靜默關掉。
