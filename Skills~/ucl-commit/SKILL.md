---
name: ucl-commit
description: |
  使用者要求 commit / 提交 / 推改動時用本 skill。**預設只 commit 改動所在的那一層（單層），逐層 bump 父層要使用者明說 commit all / 全包 / 逐層 bump 才做。** 涵蓋 submodule 先切回追蹤分支（避免 detached HEAD 游離 commit）、ChatTavern 訊息獨立 [chat] commit、ephemeral 檔（log / 臨時渲染 / wait 檔）不入 commit 的規範，以及**提交一律走 `git_commit.py`**（自動組 Co-Authored-By trailer + 自動發酒館公告領薪）。
  觸發詞包含：commit、提交、幫我 commit、分批 commit、推一下、存檔、落 commit、commit 一下、bump submodule、切分支、detached HEAD、commit 薪資、領 commit token、commit 公告。
  涉及 UCL_Core 等 submodule 改動的 git 操作必用。
---

# UCL Commit — 提交規範速查

> 一句話：**你負責判斷「哪些檔走哪一筆」與 stage；提交走 `git_commit.py`，trailer 與領薪公告它自己來。**

> [!IMPORTANT]
> ## 預設是單層（Tim 2026-08-11 拍板）
>
> 收到「commit」→ **只提交改動所在的那一層，不 bump 父層。**
> 逐層 bump 是**選配**，只在使用者明說時做：`commit all` / `全包` / `逐層 bump` / `bump 到主專案`。
>
> **為什麼預設不 bump**：bump 是一個**對外的宣告**（「這個版本可以拿去用了」），
> 而剛寫完的東西通常還沒被實跑驗過。預設 bump 等於每次存檔都對同事廣播一次未驗收的版本。
> 單層則讓「寫完」跟「發佈」分開 —— 前者我自己決定，後者要人點頭。
>
> **代價要講清楚，因為它不會叫**：單層之後**父層指標仍指著舊 hash**，
> 同事 pull 主專案拿到的還是舊版。所以單層 commit 完的回報**必須明說這件事**，
> 不能只報 SHA 就當交付完成 —— 那會讓人以為東西已經到得了別人手上。

> ⚠ 本 skill 是 UCL_Core 跨專案共用，**路徑與分支名因專案而異，一律不寫死**。
> 實際值用 `git submodule status` / `git -C <sub> branch` 現場判斷。

## 你做 vs 工具做

| 步驟 | 誰做 |
|---|---|
| 判斷哪些檔走哪一筆、具名 stage | **你** |
| submodule 切回追蹤分支 | **你** |
| 逐層 bump 的順序（**只在使用者說 commit all 時**） | **你** |
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
    --message-file <訊息檔> \
    [--announce-body-file <開場白檔>]
```

> [!CAUTION]
> **body 一律走檔案，inline 只準用在「無標點的短句」。**
> `--message-file` / `--announce-body-file`，不要用 `-m "…"` / `--announce-body "…"` 塞長文。
>
> 🩸 2026-08-05 summit 一天被反引號咬**四次**（`commit -m` 兩次、`work_memory --body` 一次、
> `--announce-body` 一次）。最後那次最難看：**同一道指令裡 commit 訊息走了 `--message-file`
> （正確修法），公告內文卻用 inline** —— 修法只套用在我記得的那半邊。
> 那次 `` `bookmark --reader` `` 被 shell 當命令替換**執行掉了**（log 留下 `command not found`），
> 公告內文缺一整段，而**已公告領薪的訊息無法 amend**。
>
> 判準不是「含不含特殊字元」（那要人判斷，而人會錯）——**是「長文一律走檔案」**。
> `--message-file` 有效不是因為你記得反引號會咬人，是因為它**根本不經過 shell 解析那一層**。

它會做而你不必記的事：
- 每位 `--persona` 各一行 trailer（身分／型號／信箱全部推導自檔案，重複自動去重）
- **提交後自動發酒館公告領薪**，SHA 與 meta 由它填；`--no-announce` 可關
- **解析訊息裡的 `Fixes BUG-<n>` 並自動關掉那幾張問題回報單**（見下節）
- `--announce-body` / `--announce-body-file` 是**可選**開場白，插在標題與 commit 內文之間。
  不帶就只發 commit 資訊。（commit 訊息寫給日後查 history 的人，開場白寫給現在在酒館的同事。）

它會**擋下**而不是默默做完的事：
- 信箱解析不到（`--allow-unset` 才放行）—— 假位址進了 history 改不掉
- persona 檔不存在 / `agent` 欄空白 —— 打錯名字會靜默生出 `?@nobody(?)`，比失敗難查
- 沒有 staged 變更 —— 本工具只提交，不 stage
- 查不到 sender 的 bank —— sender 決定錢進誰的帳，猜錯是把薪水發給別人
- **`--no-announce` 沒帶 `--no-announce-reason`**（exit 2，**擋在 commit 之前**，不留
  「已提交但沒領薪」的殘局）

### `--no-announce` 必須帶理由（2026-08-05 Tim 拍板）

```bash
--no-announce --no-announce-reason "為什麼這筆不公告"
```

> 🩸 血證：summit 2026-08-05 一天三次順手打了 `--no-announce`，造成薪水沒領；
> **每次都自首、還把「規矩對我自己也一樣，別自己發明例外」寫進公告，然後下一次照樣打上去。**
> 三次同一個動作就不是失誤，是預設行為。
>
> 修法刻意不是「再提醒一次」——**寫下來只讓下一個人知道，不讓自己記得。**
> 現在你得先想出一個理由，而**想不出來的時候你就會發現自己沒有理由**。
> 理由也會被印在「未公告」提示裡 —— 給了理由卻沒人看得見，那個參數就只是形式。

**exit 6 = commit 成功但公告失敗**（錢沒領到，需手動補）。這兩件事刻意分開回報。

⚠ 也別走 stdin heredoc：內文若含 `EOF` 字樣，結束標記會把外層提前關掉
（2026-08-03 實測自摔，公告被截斷）。**一律 `--message-file`** ——
heredoc 與 `-m` 兩條路都會經過 shell，而上面那條 CAUTION 講的就是這一層。

## Submodule 逐層 bump（由內往外）—— **選配，使用者說了才做**

> 觸發：`commit all` / `全包` / `逐層 bump` / `bump 到主專案`。沒說 = 只做單層，跳過本節。

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
- ❌ 使用者說了 `commit all` 卻只 commit 最內層 → 同事 pull 拿到舊 hash，編不過。
  （單層模式下**不是** anti-pattern，那是預設行為 —— 但**必須在回報裡明說父層還指著舊 hash**，
  見上方 IMPORTANT。沒說 = 讓人以為東西已經到得了別人手上。）
- ❌ 沒人說 commit all 就自己 bump 到主專案 → 把未驗收的版本對同事廣播出去。
- ❌ 安裝副本沒同步（`.claude` / `.codex` / `.agents`）→ 正本改了但**實際載入的還是舊的**。
  ⚠ `.agents` 那份**不是逐位元組相同**（antigravity target 會注入一行 `trigger:`）——
  同步時是**套用同一個編輯**，不是把正本複製過去（複製會把那行吃掉）。
- ❌ code 混 chat → history 噪音。

## 🐛 `Fixes BUG-<n>` —— commit 順手關掉問題回報單

修好一張 `BugReport` 的單之後，**在 commit 訊息裡寫一行就好**：

```
Fixes BUG-12
```

`git_commit.py` 會在**公告成功之後**自動跑 `op=resolve` 並把 SHA 掛上去，
console 會印一行 `🐛 BUG-12 已自動關單（<sha>）`。

**為什麼掛在 commit 上**：修東西的人本來就要 commit ——
把關單掛在他**一定會走的那條路**上，就不必要求他記得再跑一支指令。
而「記得」正是那套系統不能依賴的東西（一張沒人回來關的 open 單，
跟一張還真的壞著的單長得一模一樣，還會主動誤導）。

⚠ 邊界，每一條都會咬人：
- **一則 commit 可以帶多行 `Fixes BUG-a` / `Fixes BUG-b`**，各自關掉。
- **`--no-announce` 的 commit 不會關單** —— 閉環掛在公告成功之後。
  那種情況要手動 `run BugReport --arg op=resolve --arg index=<n> --arg commit_sha=<SHA>`。
- 關單失敗**只警告不致命**（commit 已經落地了，不該讓它看起來失敗）——
  看到 `⚠ BUG-n 自動關單失敗` 就手動補一次，別假設它成功了。
- ⛔ **別在訊息裡寫沒有真的修好的單號。** 關單是對別人的宣告：
  清單上少一筆＝所有人不再看它。

開單、修復流程與 severity 判準 → skill `ucl-bug-report`。

## 執行順序（收到「commit」指令）

0. **先判層數**：使用者說了 `commit all` / `全包` / `逐層 bump` 嗎？沒說 = **單層**。
1. `git status` 看全貌；每個 submodule 跑 `git -C <sub> status -b -s` 確認分支。
2. detached HEAD → 先 `switch` + `pull --ff-only`。
3. 按分類矩陣判斷每個檔走哪筆。
4. stage → `git_commit.py` 提交（trailer 與公告自動）。
   **單層**：只做改動所在那一層，做完就停。
   **commit all**：由內往外逐層 stage + bump。
4.5 **這筆有修到 BugReport 的單嗎** → 訊息裡加 `Fixes BUG-<n>`（提交時自動關單，見上節）。
5. 跑 `commit_payout_check.py` 對帳，報告 SHA 與已領狀態給 Tim。**不 push。**
   單層時**一併報「父層仍指著舊 hash，同事 pull 拿到的還是舊版」**——
   那句不是免責聲明，是這次交付真實的邊界。

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

---

修的是一張問題回報單 → skill `ucl-bug-report`（開單 / 修復流程 / severity 判準；訊息記得帶 `Fixes BUG-<n>`）。
