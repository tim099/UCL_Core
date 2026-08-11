---
title: UCL_GitSubmoduleSyncPage — Git Submodule 同步頁
last_updated: 2026-08-11
---

# UCL_GitSubmoduleSyncPage

批量對本專案（或指定 repo）的**所有 submodule** 做三件事：**切到預設 branch / pull / push**。
解的是多層 submodule 專案的日常痛點：`submodule update` 之後全員 detached HEAD、
分支跑掉、誰 ahead 誰 behind 沒人一眼看得到。

與 [`UCL_GitFlattenSyncPage`](UCL_GitFlattenSyncPage.md) 的分工：
那頁「攤平檔案到另一個 repo，**兩邊 git 都不碰**」；本頁「**只碰 git**（branch / pull / push），
不動任何工作目錄檔案內容」。

> [!NOTE]
> 本頁由 **C# 直呼 git CLI**（不用 LibGit2Sharp、不另寫 Python 端）——
> 與 FlattenSync 相反的取捨：那邊的邏輯要能在無 Editor 環境跑所以事實來源是腳本，
> 本頁是互動操作台，agent 端已有自己的 git 流程（`git_commit.py` / ucl-commit skill），
> 沒有共用需求。git CLI 的認證走系統 credential manager，push 不必自己管憑證。
> 每條 git 指令都登記 `UCL_ProcessRegistryService`（tag `git_submodule_sync`）。

## 頁面操作

| 區塊 | 說明 |
|---|---|
| Repo 根目錄 | **每次開頁一律回到本專案**（`UCL_RepoPath.RepoRoot`）。當次可改路徑跨 repo 操作，但**不留過夜**；改成別的 repo 時頁面與確認框都會警示 |
| 全域預設 branch | 目標 branch 的最後一層 fallback；解析順序見下方 |
| root repo 開關 | root 可一起 pull / push；**切 branch 永遠不含 root**（專案根換分支該是人自己下的動作） |
| Push 到所有 remote 開關 | 見下節。關（預設）= 只推 `origin` |
| 狀態表 | 每個 submodule 一列：納入勾選、目前 branch（detached 紅 / 偏離目標黃 / 對齊綠）、逐項 branch 覆寫、dirty / 未 init / ↑ahead ↓behind / `⇈` 多 remote 清單 |
| 重新掃描 | 唯讀。進頁面自動跑一次（不 fetch，快） |
| Fetch 全部後掃描 | 逐 submodule `git fetch` 再掃 —— **ahead/behind 要準需要先 fetch** |
| 切到預設 branch | 逐項 checkout 到目標 branch（安全線見下） |
| Pull（ff-only） | `git pull --ff-only origin <target>`；分岔就 fail loud 列出，不替人 merge。**detached 的列會被跳過** —— pull 不負責移動 branch，訊息會指路到下一顆 |
| 切 → pull（不推） | 一鍵同步**減掉 push**。「我只想把本地全部弄到最新」用這顆；不寫任何遠端，所以**不走二次確認** |
| Push | 二次確認後執行；**由深到淺**（巢狀最深先推、root 最後）、每 repo 推完它的全部 remote 才換下一個 |
| 一鍵同步 | 切 → pull → push 一條龍，同樣走二次確認 |

## 設定存哪 —— 以及它 2026-08-11 之前會咬人的地方

設定存 `EditorPrefs`（JSON），key 是 **`UCL_GitSubmoduleSync.Settings@<ProjectFingerprint>`**。
路徑是絕對路徑，換機器要重填。

> [!WARNING]
> **`EditorPrefs` 是 per-machine，不是 per-project** —— 同一台機器上所有 Unity 專案共用
> `HKCU\Software\Unity Technologies\Unity Editor 5.x` 一份。
>
> **血證（2026-08-11）**：本頁 2026-08-10 之前的 key 沒有加專案後綴，於是在 LY 設好的
> `Root=D:/Unity/LY` 漂進了 Bar 專案。在 Bar 按 pull / 一鍵同步時，本頁**誠實地對 LY 動手、
> 回報一整排 ✓**，而 Bar 的 submodule 一個位元組都沒動 —— 綠燈全亮，量到的是別的 repo。
> 同一份設定裡的 `Overrides` 更毒：`AgentCommands -> LY` 漂到 Bar 之後，一鍵同步會試著把
> Bar 的 `AgentCommands` 從 `main` 切到 `LY`。
>
> **現在的三道防線**
> 1. key 加 `@<ProjectFingerprint>`（`UCL_RepoPath.ProjectFingerprint`）→ 各專案各一份設定
> 2. `Root` 標 `[NonSerialized]` 且開頁**無條件**重設成本專案 → 不存在「存了不知道多久的舊目標」
> 3. `Root` ≠ 本專案時，頁面顯示警示、確認框第一行也講 → 跨 repo 操作合法，但必須用吵的
>
> ⚠ 舊 key（無後綴）**刻意不遷移、也不刪**：遷移等於把汙染過的值搬進第一個開啟的專案，
> 正是本次要根治的東西。舊值留在 registry 當孤兒，無害；想清掉自己去 registry 刪。

> [!NOTE]
> 任何**屬於單一專案**的 `EditorPrefs` key 都該加 `UCL_RepoPath.ProjectFingerprint`。
> 該 getter 是這件事的唯一解析點（`UCL_WelcomePage` / `UCL_AgentSkillManagerPage`
> 原本各有一份逐字相同的私有副本，2026-08-11 收攏過去；演算法未變，既有 key 不失效）。

## 目標 branch 解析順序

```
逐項覆寫（本頁狀態表的欄位） > .gitmodules 的 branch 欄 > 本頁全域預設 > 啟發式
```

啟發式（Tim 2026-08-07 拍板，掃描時逐 repo 算好）：

1. 資料夾名以 `UCL_` 開頭（UCL_Core 與其他 UCL 系）→ `Dev`
2. 全 repo（本地＋origin）只有一條 branch → 就是它（沒有歧義可言）
3. 其餘 → `master`；沒有 `master` 才 `main`（GitHub / GitLab 2020 後新 repo 預設 main、
   舊 repo 是 master —— 目前沒有兩者並存的 repo，並存時 master 贏）

四層都空 → 該項**跳過並列出**，不會靜默拿「目前所在 branch」頂替。
`.gitmodules` 的 `branch =` 是 git 原生欄位（`git config -f .gitmodules submodule.<name>.branch`），
已填的直接尊重 —— 想讓預設 branch 進版控跟著 repo 走，填那裡；只想本機生效，用本頁覆寫。

## 跳過不硬上（fail loud，不是自動修）

以下情況一律**跳過該項並在報告列出**，本頁不 stash、不 force、不替人做決定：

| 情況 | 為什麼 |
|---|---|
| **狀態表沒勾的列** | 勾選＝納入批次。沒勾的一律不進 targets，連 git 都不會被呼叫 |
| dirty（有未 commit 的追蹤檔修改）—— **切與 pull 都擋** | 切 branch 會吃掉未收的工作；pull 雖然 git 會逐檔拒絕覆蓋衝突檔，但**不衝突的檔照 ff 過去** → 未 commit 的工作跟新拉的版本混在同一個工作目錄，而人不會知道那一刻發生過合併。stash 是把別人的工作區當自己的，本頁不做。<br>⚠ **Push 不受 dirty 影響**：推的是已 commit 的東西，跟工作目錄乾不乾淨無關。（2026-08-11 之前本頁的說明寫「dirty 一律跳過」而實作只涵蓋 checkout —— 承諾比實作大，那種說明比沒有說明更糟，因為它讓人不去查。） |
| detached HEAD 不在目標 branch 歷史上 | 上面可能有未合併 commit，切走 = 指標脫錨（reflog 能救但沒人會去看） |
| 目標 branch 本地與 origin 都不存在 | 無中生有一條 branch 不是同步，是建構 —— 該是人自己做的 |
| pull 遇到分岔（non-fast-forward） | merge / rebase 的選擇不該由批次工具代下 |
| 解析不到目標 branch | 見上節 —— 沒有目標就沒有「預設」可言 |

## Push 為什麼由深到淺

parent 的 bump commit 引用 child 的 SHA。先推 parent 的話，別人 pull 下來會拿到
指向**遠端還不存在的 commit** 的 gitlink —— 而且是靜默壞（clone / update 的人才會發現）。
所以巢狀最深的先推，root 最後。

## Push 到所有 remote（2026-08-10，預設 off）

同一份程式碼同時掛 GitHub 與 GitLab 時，只推 `origin` 會讓另一邊**靜默落後** ——
而落後的那一邊不會叫（沒人 pull 它就沒人知道）。開這個開關後，push 對每個 repo
展開它自己的 remote 清單（`git remote`）各推一次。

| 行為 | 規則 |
|---|---|
| 推去哪 | 該 repo `git remote` 列出的**每一個** remote，branch 一律是解析出來的目標 branch |
| 順序 | 一個 repo 推完它的全部 remote，才換下一個 repo（repo 之間仍是深→淺） |
| 一個 remote 失敗 | **不中斷其他 remote** —— GitHub 成功、GitLab 認證掛掉是兩件獨立的事，為後者放棄前者等於白跑 |
| 部分成功怎麼記 | 整列記成**失敗**（`✗ push 2/3（失敗: gitlab）`）—— 部分成功不是成功 |
| 該 repo 沒有任何 remote | 跳過並列出（`⏭ 無 remote`），不靜默算成 ✓ |
| Pull | **不跟進** —— 從哪個 remote 合併是 merge 決策，不是同步動作，仍固定 `origin` |

深→淺的 gitlink 不變量不因多 remote 而破：對**每一個** remote 來說，child 都在 parent 之前推出去。

二次確認視窗印的是**具體 remote 名字**（掃描時看到的清單）而不是「所有 remote」——
「所有」是設定的名字，人要確認的是它今天實際展開成什麼。狀態表上多 remote 的列標 `⇈ a / b`。

> [!NOTE]
> 執行時的 remote 清單是**即時重問**的，狀態表那份只給顯示與確認視窗用
> （理由同下節：照片能拿來報告，不能拿來下決定 —— 掃描後才加的 remote 會被照片漏掉，而漏掉不會叫）。

> [!TIP]
> 只想要「一個 remote 推兩個 URL」的鏡像效果，git 原生就有：
> `git remote set-url --add --push origin <url2>`。代價是 fetch 仍只從第一個 URL、
> ahead/behind 只反映其中一邊，而且要逐 repo 逐機器設。本開關反過來：remote 各自獨立、
> 狀態各自看得到，代價是要一次寫多個遠端。

## 安全線用即時值，不用掃描快照（Sirius 2026-08-07 砸磚）

dirty / 目前 branch 的判斷在**批次執行當下**逐 repo 重新問 git，不讀狀態表的快照 ——
狀態表是上一次掃描的照片，而 Unity Editor 在兩次點擊之間會 import asset、寫 `.meta`、存 scene；
照片乾淨、現在髒了的話，「dirty 跳過」的承諾會靜默失效。

同一次砸磚定的另一條：**checkout 之前先對該 repo `fetch`**（只有真的要切的才 fetch）——
「branch 存不存在」「HEAD 有沒有未合併 commit」兩道檢查都要用新鮮的尺，
過期的尺做出來的是**決定**（切 / 不切）不是報告。

> [!IMPORTANT]
> ### 先快轉目標分支，再 checkout（Tim 2026-08-11）
>
> ⚠ 本節原本寫「兩道檢查都拿 `origin/<target>` 當尺」—— **那句是錯的**：
> 本地已有該 branch 時，程式拿的是**本地那條**（`checkRef = hasLocal ? target : origin/target`）。
> 而 `git fetch` 只更新 `refs/remotes/*`，**不動 `refs/heads/*`** —— 所以「切之前先 fetch」
> 這道防線對「本地分支落後」完全無效，而文件那句話讓它看起來已經被涵蓋了。
>
> **兩個後果，都不會叫**：
> 1. detached 在 `origin/<target>` tip 的 submodule，會被 `--is-ancestor HEAD <本地舊 branch>`
>    判成「HEAD 未合併」**整列跳過** —— 那道安全線在保護一個不存在的風險，
>    而跳過訊息（「可能有未合併 commit」）看起來完全像盡責。
> 2. 就算通過，`checkout <本地舊 branch>` 會把工作目錄**倒退**到舊 commit，
>    等後面 pull 再前進 —— Unity 專案白吃一輪 reimport。
>
> **現在的順序**：`fetch` → **`git fetch origin <target>:<target>`（把本地目標分支快轉）**
> → 兩道檢查 → `checkout`。
> refspec fetch 可以在**不 checkout** 的情況下快轉本地分支，非 fast-forward 時 git 自己拒絕。
> 只在「目標分支已存在且不是目前所在」時做；本地還沒有這條 branch 時走原本的
> `checkout -b --track origin/<target>`（那條會順便設好 upstream，refspec 建的不會）。
>
> 沙盒實證（2026-08-11）：本地 `master` 停在 c1、detached HEAD 在 origin 的 c2 →
> 現行安全線 ✗ 跳過；先 refspec fetch 後 ✓ 通過並直接落在 c2。
Push 端則刻意**不**強制 fetch：non-fast-forward 被拒本來就很大聲，fetch 只是把
「遠端大聲拒絕」換成「本地大聲跳過」，沒換到資訊。

## 已知邊界

- ahead/behind 對 `@{upstream}` 計算；沒設 upstream 的 branch 顯示未知（不顯示 0 —— 0 是「對齊」，未知不是）。
- ahead/behind 的新鮮度**逐列標**（各列顯示上次 fetch 距今多久，FETCH_HEAD mtime）——
  一句全域警語會把剛 fetch 的跟三天沒動的混為一談。
- untracked 檔不算 dirty（不擋 checkout / pull；算進來會讓每個 submodule 都紅，假警報訓練人忽略警報）。
- `GIT_TERMINAL_PROMPT=0`：認證失敗直接 fail 列出，不會停在看不見的終端等輸入。
- 單條 git 指令逾時 5 分鐘 —— 命中代表卡住（credential / 網路），不是「檔案多」。
- **child push 失敗不會擋住 parent push**：批次不中斷，parent 照樣推出去，於是遠端會短暫出現
  指向不存在 commit 的 gitlink。報告會把 child 那筆記成 `✗`，但**需要人自己看**。
  （2026-08-10 記錄，待拍板是否改成「child 失敗則跳過其 parent」。）
- `pull` 固定 `origin`：remote 不叫 `origin` 的 repo（例如只有 `github` / `gitlab`）pull 會失敗列出。
  多 remote push 開關不改變這件事。
