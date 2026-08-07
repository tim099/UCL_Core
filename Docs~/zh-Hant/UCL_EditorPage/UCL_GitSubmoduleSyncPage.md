---
title: UCL_GitSubmoduleSyncPage — Git Submodule 同步頁
last_updated: 2026-08-07
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
| Repo 根目錄 | 預設 `UCL_RepoPath.RepoRoot`（本專案 git root），可改路徑 / 按「本專案」還原 |
| 全域預設 branch | 目標 branch 的最後一層 fallback；解析順序見下方 |
| root repo 開關 | root 可一起 pull / push；**切 branch 永遠不含 root**（專案根換分支該是人自己下的動作） |
| 狀態表 | 每個 submodule 一列：納入勾選、目前 branch（detached 紅 / 偏離目標黃 / 對齊綠）、逐項 branch 覆寫、dirty / 未 init / ↑ahead ↓behind |
| 重新掃描 | 唯讀。進頁面自動跑一次（不 fetch，快） |
| Fetch 全部後掃描 | 逐 submodule `git fetch` 再掃 —— **ahead/behind 要準需要先 fetch** |
| 切到預設 branch | 逐項 checkout 到目標 branch（安全線見下） |
| Pull（ff-only） | `git pull --ff-only origin <target>`；分岔就 fail loud 列出，不替人 merge |
| Push | 二次確認後執行；**由深到淺**（巢狀最深先推、root 最後） |
| 一鍵同步 | 切 → pull → push 一條龍，同樣走二次確認 |

設定存 `EditorPrefs`（JSON）。路徑是絕對路徑，換機器要重填（慣例同 FlattenSync）。

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
| dirty（有未 commit 的追蹤檔修改） | 切 branch / pull 可能吃掉未收的工作；stash 是把別人的工作區當自己的 |
| detached HEAD 不在目標 branch 歷史上 | 上面可能有未合併 commit，切走 = 指標脫錨（reflog 能救但沒人會去看） |
| 目標 branch 本地與 origin 都不存在 | 無中生有一條 branch 不是同步，是建構 —— 該是人自己做的 |
| pull 遇到分岔（non-fast-forward） | merge / rebase 的選擇不該由批次工具代下 |
| 解析不到目標 branch | 見上節 —— 沒有目標就沒有「預設」可言 |

## Push 為什麼由深到淺

parent 的 bump commit 引用 child 的 SHA。先推 parent 的話，別人 pull 下來會拿到
指向**遠端還不存在的 commit** 的 gitlink —— 而且是靜默壞（clone / update 的人才會發現）。
所以巢狀最深的先推，root 最後。

## 安全線用即時值，不用掃描快照（Sirius 2026-08-07 砸磚）

dirty / 目前 branch 的判斷在**批次執行當下**逐 repo 重新問 git，不讀狀態表的快照 ——
狀態表是上一次掃描的照片，而 Unity Editor 在兩次點擊之間會 import asset、寫 `.meta`、存 scene；
照片乾淨、現在髒了的話，「dirty 跳過」的承諾會靜默失效。

同一次砸磚定的另一條：**checkout 之前先對該 repo `fetch`**（只有真的要切的才 fetch）——
「branch 存不存在」「HEAD 有沒有未合併 commit」兩道檢查都拿 `origin/<target>` 當尺，
過期的尺做出來的是**決定**（切 / 不切）不是報告，決定要用新鮮資料做。
Push 端則刻意**不**強制 fetch：non-fast-forward 被拒本來就很大聲，fetch 只是把
「遠端大聲拒絕」換成「本地大聲跳過」，沒換到資訊。

## 已知邊界

- ahead/behind 對 `@{upstream}` 計算；沒設 upstream 的 branch 顯示未知（不顯示 0 —— 0 是「對齊」，未知不是）。
- ahead/behind 的新鮮度**逐列標**（各列顯示上次 fetch 距今多久，FETCH_HEAD mtime）——
  一句全域警語會把剛 fetch 的跟三天沒動的混為一談。
- untracked 檔不算 dirty（不擋 checkout / pull；算進來會讓每個 submodule 都紅，假警報訓練人忽略警報）。
- `GIT_TERMINAL_PROMPT=0`：認證失敗直接 fail 列出，不會停在看不見的終端等輸入。
- 單條 git 指令逾時 5 分鐘 —— 命中代表卡住（credential / 網路），不是「檔案多」。
