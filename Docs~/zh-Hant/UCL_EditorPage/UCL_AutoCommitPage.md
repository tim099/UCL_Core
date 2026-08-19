---
title: UCL_AutoCommitPage — 自動 Commit 頁
last_updated: 2026-08-19
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

- `[chat]` 獨立 commit 是專案硬規則（見 ucl-commit skill 的檔案分類）—— 所以規則不開放 UI 編輯。
- **submodule pointer 預設不勾**：那些 pointer 指向別人（其他 persona 信件庫）的 commit，
  對方沒 push 就 bump，別人 pull 會拿到拿不到的 hash。確認對方已 push 再勾，且勾選只活一次。
- **未分類預設不勾**：分類規則沒認出來的檔，不該被「自動」二字順手帶走。

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

### 兩道守衛

- **在線的 persona 預設不勾**（判準是 `_session/_persona_*.json` lock 未過期，
  不是 registry 的 `status` 欄 —— 登出沒走完時 status 會停在 online）。
  她可能正在寫，而動別人正在寫的東西**不會有錯誤訊息**，只會靜默清掉工作。
  勾選**不持久化、每次掃描重算**（上一次的「我認了」不延續到這一次）。
- **detached HEAD 硬擋**（勾不動）—— 那裡 commit 出來沒有分支指到它，
  下次 checkout 只剩 reflog 找得到。與 ucl-commit skill 同一條規矩。

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
