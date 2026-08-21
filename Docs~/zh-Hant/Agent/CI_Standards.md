---
title: CI 使用判準與寫法 (When to Use CI, and How)
description: 什麼時候該把一件事交給 CI（而不是寫進文件叫人記得跑）、CI 做得到與做不到什麼、GitHub Actions 在本 repo 的寫法與已踩過的坑。
tags: [ci, github-actions, automation, github-pages, coding-standards]
aliases: [什麼時候用 ci, ci 規範, github actions, 自動化建置, 要不要開 ci]
target_audience: [AI_Agent, Tools_Maintainer]
last_updated: 2026-08-21
---

# 🤖 CI 使用判準與寫法

> 一句話：**CI 不是「自動化」的同義詞，它是「把一條規則從人的記性裡搬到路上」的手段之一** ——
> 而且是唯一一種**不必每台機器各裝一次**的手段。

---

## 1. 什麼時候該用 CI

### 判準：三個訊號，命中任一就該考慮

| 訊號 | 長什麼樣 | 例 |
|---|---|---|
| **有一份衍生產物，而它的重建靠人記得** | 文件裡寫著「改完 X 之後要記得跑 Y」 | `gallery_data.js`（249 份 `.md` 的索引） |
| **失敗是靜默的** | 忘了做的結果看起來完全正常 | 索引落後 ⇒ 網頁照開，只是少一件展品 |
| **需要一個不在自己機器上的支點** | 本地檢查會因為「沒裝」而失效，而沒裝的人不會知道自己沒裝 | pre-commit hook、本地 lint |

第三條特別重要：**本地防線是自省型的**。
`.git/hooks` 不入版控，`core.hooksPath` 那行 config 每個 clone 要各設一次
（git 刻意不讓 repo 自動執行外來程式碼，這是安全設計不是缺陷）。
⇒ 一條「應該要跑」的檢查，在同事的機器上會靜默不存在。CI 沒有這個問題。

### 判準：什麼時候**不要**用 CI

| 情況 | 為什麼 | 該做什麼 |
|---|---|---|
| 這件事需要 Unity Editor / 本機狀態 | CI runner 上沒有 Editor、沒有 `AgentCommands/` 的執行期狀態 | 走 `Cmd_*`（Editor 端執行） |
| 它會寫回 repo 造成第二份真相 | CI commit 回 master ＝ 那份產物又變成「可能落後的副本」 | 把產物移出版控，CI 直接部署 |
| 它需要祕密、金流、或會對外發送 | CI 的權限邊界比本機模糊，而錯誤是對外可見的 | 人工執行 |
| 只是「想要有自動化」 | CI 也是一份要維護的東西，而且它壞掉時通常沒人看紅燈 | 別開 |
| 反饋要**立刻**（打字當下） | CI 最快也要幾十秒，慢到不會改變行為 | 編輯器 lint / 本地檢查 |

> 📌 **CI 是外部支點，不是本地檢查的替代品。** 兩者互補：
> 本地快但會因為沒裝而消失，CI 慢但不會。**同一條規則兩邊都放不是浪費，是冗餘。**

---

## 2. 三種形狀，選對那一種

| 形狀 | 做什麼 | 適用 | 代價 |
|---|---|---|---|
| **A. 驗收（check-only）** | 跑檢查，不一致就紅燈 | 產物必須進版控時（下游要用、離線要用） | 紅燈之後還是要人回去手動重跑 |
| **B. 生成並提交回 repo** | CI 跑完 `git commit` + `push` | 產物非進版控不可、且沒人想手動維護 | **產生 bot commit、有觸發迴圈風險、且產物仍是第二份真相** |
| **C. 生成並直接部署** ⭐ | CI 產出後直接變成部署產物，repo 裡根本沒有那個檔 | 產物只有線上需要 | 本機使用者要自己建一次 |

**優先序 C ＞ A ＞ B。**
C 之所以最好，是因為它讓「落後」在物理上不可能發生 —— **沒有第二份副本，就沒有東西可以落後。**
B 最差：它把問題從「人忘了跑」換成「bot 幫你跑」，但那份可能落後的副本還在。

> 🩸 2026-08-21 畫廊採 **C**：`gallery_data.js` 移出版控，
> `.github/workflows/pages.yml` 每次 push 重生成並部署 Pages。
> 本機逛展要自己跑一次 `build_gallery.py` —— 而讀不到索引時網頁會**明說怎麼跑**，
> 那是把靜默的錯換成大聲的錯（見 [`Web_Coding_Standards.md`](Web_Coding_Standards.md) §硬規則④）。

---

## 3. 寫法（GitHub Actions，本 repo 的慣例）

### 骨架

```yaml
name: build-and-deploy-gallery
on:
  push:
    branches: [master]
  workflow_dispatch:          # 手動重跑的入口一定要留
permissions:
  contents: read              # 只給需要的；要寫回 repo 才加 contents: write
  pages: write
  id-token: write
concurrency:
  group: pages
  cancel-in-progress: false   # 部署類不要取消到一半 —— 會停在半舊狀態
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0      # 見下方血證
      - uses: actions/setup-python@v5
        with: { python-version: '3.12' }
      - run: python build_gallery.py
      - run: python build_gallery.py --check     # 重新掃一次再比對，不是複述上一步
```

### ⚠ `fetch-depth: 0` —— 最容易漏、且失敗完全靜默

`actions/checkout` **預設淺 clone（depth=1）**，沒有 git 歷史。
任何靠 `git log` 取事實的工具在 CI 上都會**查不到、然後安靜地退回 fallback**。

> 🩸 畫廊的展品日期取自 `git log --diff-filter=A`（首次提交時間），查不到才退 mtime。
> 淺 clone 下 249 件會**全部**退回 mtime＝clone 當下 ⇒ 「全部同時誕生」，
> 排序整個亂掉 —— **而網頁看起來完全正常。**

**判準：工具只要碰 `git log` / `git blame` / tag 描述，就要 `fetch-depth: 0`。**

### 其他已知坑

| 坑 | 症狀 | 修法 |
|---|---|---|
| B 形狀的**觸發迴圈** | CI 的 commit 又觸發 CI | commit 訊息帶 `[skip ci]`，或 `paths-ignore` 排除產物 |
| 產物大 | 每次部署都要上傳（畫廊約 **442MB**，主要是 `RawImages/`） | 接受，或改成只上傳必要目錄；**要在文件裡寫明大小**，否則沒人知道那幾分鐘花在哪 |
| 需要人在網頁 UI 做的一次性設定 | CI 綠燈但線上沒更新 | **寫進 workflow 檔頭註解 ＋ commit 訊息 ＋ 交付回報**（見下節） |
| 權限不足 | `push` 403 | `permissions: contents: write`；組織層若鎖 `GITHUB_TOKEN` 則要人去開 |
| 只有 GitHub 有 CI，鏡像 remote 沒有 | 鏡像那份靜默掉隊 | 交付時明講；要對等就補該平台的 CI 設定檔 |

### CI 改變不了的東西要交回給人

有些前置只能在該平台的網頁 UI 做，**agent 做不到也不該做**。
這種一次性設定必須出現在**三個地方**，因為它是「不做就靜默壞掉」的那類：

1. workflow 檔頭註解（下一個讀這個檔的人）
2. commit 訊息（日後查 history 的人）
3. 交付回報（現在要動手的人）

> 🩸 例：Pages 從 branch 部署改成 Actions 部署，要人去
> **Settings → Pages → Source 改成 GitHub Actions**。
> 沒改而先 push 的話，分支裡已經沒有索引檔 ⇒ 線上變成「讀不到索引」。
> **這一步要在 push 之前或同時做**，而它不會有任何錯誤訊息提醒你。

---

## 4. 驗收：CI 綠燈不等於它做對了

| 要驗的 | 怎麼驗 |
|---|---|
| workflow YAML 真的合法 | 本機 `python -c "import yaml,sys; yaml.safe_load(open(...))"`，別靠 push 上去試 |
| 產出真的正確 | 在 CI 內跑一次**獨立的** `--check`（重掃再比對），不是印出上一步的輸出 |
| 它擋得住壞掉的輸入 | 本機故意弄壞一次（改一個來源檔不重建），確認 `--check` 回 **exit 1** |
| 部署真的生效 | 部署後打實際 URL 看狀態碼與內容，不是看 Actions 的綠勾 |

> 🩸 綠勾只證明「腳本執行完畢且回 0」。
> `--check` 這種對帳步驟的價值在於它**重新產生一次事實再比對**；
> 如果它只是複述上一步的 stdout，那它跟沒有一樣。

---

## 📚 延伸

| 主題 | 文件 |
|---|---|
| 靜態網頁本身怎麼寫（誰吃 CI 的產物） | [`Web_Coding_Standards.md`](Web_Coding_Standards.md) |
| Python 工具（CI 通常就是在跑它們） | [`Python_Coding_Standards.md`](Python_Coding_Standards.md) |
| 提交規範（CI 不 push、不 bump 父層） | skill `ucl-commit` |
| 需要 Unity Editor 的自動化 → 不是 CI 的活 | `Docs~/{lang}/API/UCL_AgentCommand/` |
