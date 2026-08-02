---
name: ucl-commit
description: |
  使用者要求 commit / 提交 / 推改動時用本 skill。涵蓋 submodule 由內往外逐層 bump（先切回追蹤分支避免 detached HEAD 游離 commit）、ChatTavern 訊息獨立 [chat] commit、ephemeral 檔（log / 臨時渲染 / wait 檔）不入 commit 的規範，以及**提交一律走 `git_commit.py`**（自動組 Co-Authored-By trailer + 自動發酒館公告領薪）。
  觸發詞包含：commit、提交、幫我 commit、分批 commit、推一下、存檔、落 commit、commit 一下、bump submodule、切分支、detached HEAD、commit 薪資、領 commit token、commit 公告。
  涉及 UCL_Core 等 submodule 改動的 git 操作必用。
---

# UCL Commit — 提交規範速查

> 一句話：**你負責判斷「哪些檔走哪一筆」與 stage；提交走 `git_commit.py`，trailer 與領薪公告它自己來。**

> ⚠ 本 skill 是 UCL_Core 跨專案共用，**路徑與分支名因專案而異，一律不寫死**。
> 實際值用 `git submodule status` / `git -C <sub> branch` 現場判斷。

## 你做 vs 工具做

| 步驟 | 誰做 |
|---|---|
| 判斷哪些檔走哪一筆、具名 stage | **你** |
| submodule 切回追蹤分支 | **你** |
| 逐層 bump 的順序 | **你** |
| commit 訊息內容 | **你** |
| Co-Authored-By（身分／型號／信箱） | 工具 |
| 酒館公告 + 領薪 | 工具 |
| push | **沒有人** —— Tim 手動 |

## 檔案分類（先看清再 stage）

| 類型 | 走哪筆 commit |
|---|---|
| 代碼 / 文檔 / `.meta` | 主 commit（具名 stage） |
| ChatTavern messages（`rooms/<room>/messages/<日期>/*.json`） | 獨立 `[chat]` commit |
| ephemeral：`*.log` / `_last_op.md` / `_last_view.md` / `_active_waits.json` / `_wait_*.md` / DebugLogs / 臨時渲染檔 | **不 commit** |

- DebugLogs 保持 **untracked 但不 ignore** — Tim 要在 `git status` 看得到。
- **絕不 `git add -A`** — 一律具名 stage。**別人正在寫的檔會被你一起 commit 走**，而那不會有錯誤訊息。
- 別漏 stage `.meta`，否則 Unity 跳 missing reference。

## Submodule 先切追蹤分支（必做）

detached HEAD 直接 commit → 落在游離節點、追蹤分支永遠不前進（別人拉不到）。

```bash
git -C <submodule-path> status -b -s | head -1     # "## HEAD (no branch)" = detached
git -C <submodule-path> switch <tracked-branch>
git -C <submodule-path> pull --ff-only
```

## 提交 — `git_commit.py`

```bash
git -C <repo> add <files>          # stage 自己來

python <UCL_Core>/Tools~/AgentCommands/git_commit.py \
    --persona <你> [--persona <協作者> ...] \
    --repo <該層 repo 路徑> \
    [--announce-body "給同事看的開場白"] \
    -m "commit 訊息"
```

它會做而你不必記的事：
- 每位 `--persona` 各一行 trailer（身分／型號／信箱全部推導自檔案，重複自動去重）
- **提交後自動發酒館公告領薪**，SHA 與 meta 由它填；`--no-announce` 可關
- `--announce-body` / `--announce-body-file` 是**可選**開場白，插在標題與 commit 內文之間。
  不帶就只發 commit 資訊。（commit 訊息寫給日後查 history 的人，開場白寫給現在在酒館的同事。）

它會**擋下**而不是默默做完的事：
- 信箱解析不到（`--allow-unset` 才放行）—— 假位址進了 history 改不掉
- persona 檔不存在 / `agent` 欄空白 —— 打錯名字會靜默生出 `?@nobody(?)`，比失敗難查
- 沒有 staged 變更 —— 本工具只提交，不 stage
- 查不到 sender 的 bank —— sender 決定錢進誰的帳，猜錯是把薪水發給別人

**exit 6 = commit 成功但公告失敗**（錢沒領到，需手動補）。這兩件事刻意分開回報。

⚠ 訊息內文若含 `EOF` 字樣，**別走 stdin heredoc** —— 內文裡的結束標記會把外層提前關掉
（2026-08-03 實測自摔，公告被截斷）。改用 `-m` 或 `--message-file`。

## Submodule 逐層 bump（由內往外）

層數依專案巢狀結構而定，**不是固定三層**。通則：最內層先提交內容 → 每個父層 add 子 submodule
路徑 + 提交 pointer bump → 直到主專案。**每一層都是一筆獨立 commit，各自帶 trailer、各自領薪。**

```bash
git -C <inner-sub> add <files>
python <UCL_Core>/Tools~/AgentCommands/git_commit.py --persona <你> --repo <inner-sub> -m "..."

git -C <parent> add <child-sub-relative-path>
python <UCL_Core>/Tools~/AgentCommands/git_commit.py --persona <你> --repo <parent> -m "Bump <child>: ..."
```

**驗證**：每層 `git -C <sub> log <tracked-branch> -1 --oneline` 確認落在追蹤分支（非 detached）；
父層 `git diff --staged` 確認只是 pointer bump；全部完成 `git status` 應 clean。

**Anti-pattern**：
- ❌ 只 commit 最內層沒 bump 父層 → 同事 pull 拿到舊 hash，編不過。
- ❌ 安裝副本沒同步（`.claude` / `.codex` / `.agents`）→ 正本改了但**實際載入的還是舊的**。
- ❌ code 混 chat → history 噪音。

## 執行順序（收到「commit」指令）

1. `git status` 看全貌；每個 submodule 跑 `git -C <sub> status -b -s` 確認分支。
2. detached HEAD → 先 `switch` + `pull --ff-only`。
3. 按分類矩陣判斷每個檔走哪筆。
4. 由內往外逐層 stage → `git_commit.py` 提交（trailer 與公告自動）。
5. 跑 `commit_payout_check.py` 對帳，報告 SHA 與已領狀態給 Tim。**不 push。**

## 💰 領薪 — 現在是自動的，但有兩件事仍要人看

規範本體：[`Commit_Workflow.md §9.5`](../../Docs~/zh-Hant/Workflows/Commit_Workflow.md)。

- **一則訊息一個 SHA**。三層 bump = 3 筆 = 3 則。`meta.sha` 塞多個 SHA 會被 server 端直接 reject。
- **同 SHA 貼兩次會付兩次錢**（沒有防重複保護，靠社會約束）。工具發過了就別再手動貼。
- ⚠ **先公告再被 rebase = 帳掛在一個不存在的 SHA 上**。rebase 後的等價 commit 是新 SHA、永遠不會被領
  （實例 2026-07-31：`dd240b2` 領款後被 rebase，等價 commit 變成 `a9399e5`）。發現對不上就重新對帳。

```bash
python <UCL_Core>/Tools~/AgentCommands/commit_payout_check.py            # 列已領 / 未領
python <UCL_Core>/Tools~/AgentCommands/commit_payout_check.py --strict   # 有未領就 exit 1
```

> [!NOTE]
> **為什麼這些會被收進工具**：2026-07-30 新制上線後，ledger 內 `source_kind=commit` 一度
> **82 天零領取**。summit 那次是照 skill 一步步走完、SHA 都撈齊在手上了，只是丟到 chat 而不是酒館 ——
> **不是漏做，是做完了倒在門外。** 同族的還有 trailer 手打造成的漂移（同一位 meadow 三筆 commit
> 出現過三種型號寫法與兩種 domain）。
> **寫進 skill 只能讓下一個人知道；把它變成工具的預設行為，才是讓它不再需要被記得。**
