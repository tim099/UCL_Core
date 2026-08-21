---
title: 自動提交設定 — 把 repo 加入管理與設定分群規則
description: 把一個 submodule（或任何 repo）加入 AutoCommit 管理的步驟、`.ucl_autocommit.json` 的欄位與判準、設定檔掀不動的地板、以及「怎麼確認真的照設定分群」的驗收法。
last_updated: 2026-08-21
target_audience: [AI_Agent, Tools_User]
status: v1.0 (Tim 2026-08-21 拍板：分群規則可由各 repo 自帶設定檔宣告)
---

# ⚙ 自動提交設定 — 把 repo 加入管理與設定分群規則

> 一句話：**在該 repo 根目錄放一份 `.ucl_autocommit.json` 宣告自己的分群、並把 `Enabled` 打開，它才會被 `mode=submodules` 收。**
>
> ⚠ 兩個條件，缺一不可：**有設定檔** ＋ **已啟用**。後台頁選一個還沒有設定檔的 submodule 時
> 會幫你建一份，但**預設停用** —— 「選了它」不等於「同意開始自動 commit 它」。

> 📦 相關文件
> - 提交總流程（人工／自動的分工、領薪）：[`Commit_Workflow.md`](Commit_Workflow.md)
> - 後台頁（掃描／勾選／執行 git／**設定編輯區**）：[`UCL_AutoCommitPage.md`](../UCL_EditorPage/UCL_AutoCommitPage.md)
> - 提交規範速查（agent 入口）：skill `ucl-commit`
> - 實作：`UCL_AutoCommitConfig`（設定模型＋發現）／`UCL_AutoCommitRules`（分群與地板）／`Cmd_AutoCommit`（執行）

---

## 1. 什麼 repo 該加入

判準**不是**「它是不是 submodule」，而是這兩句同時成立：

1. 這個 repo 裡有**機器生成、會天天長**的檔（狀態、帳本、訊息、對局…）
2. 那些檔**沒有作者** —— 沒有人會為它們寫 commit 訊息

⛔ 不該加入的：有作者的產出（code / 文件 / 她寫的信）。那些走 `git_commit.py`，要掛 trailer、要領薪。
**掛誰的名字領誰的薪都是假帳。**

> 💡 同一個 repo 可以兩者都有 —— 沒有被任何群命中的檔會落 `__other`，而 `__other` **永不自動收**。
> 例：`Chess` repo 的 `games/` 進群自動收，`RuleBook.md` 刻意不進任何群（它有作者）。

## 2. 加入管理的步驟

### Step 1 — 在該 repo **根目錄**放 `.ucl_autocommit.json`

兩條路：**（A）後台頁**「⚙ Submodule 自動提交設定」→ 下拉選單選目標 →「➕ 建立設定檔（預設停用）」；**（B）手寫**下面這份。

```json
{
  "Enabled": true,
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

### Step 2 — 把 `Enabled` 打開，並確認它被發現

建立出來的設定是停用的（`Enabled: false`）。開啟方式：後台頁把 `Enabled` 打勾後存檔，或直接改檔案。

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run AutoCommit \
    --arg op=scan --arg mode=submodules
```

回傳值要看 **`repos`**、**`disabled_repos`** 與 **`blocked_repos`**：
- `repos` 沒有 +1 ⇒ 檔案位置不對，或該 repo 不在 `<data_root>/.gitmodules` 裡
- **`disabled_repos` +1 ⇒ 設定還是停用的**（`Enabled=false`）。這不是錯誤，所以刻意**不計入** `blocked_repos` —— 但也不會靜默消失，Editor log 會印
  `・<repo>：設定為停用（Enabled=false）`。⚠ 自動建立的設定就是這個狀態，「我明明加了設定檔卻什麼都沒發生」多半是這一格
- `blocked_repos` +1 ⇒ 被發現但**設定不合法或讀不出來**，原因印在 Editor log（壞檔會明說，不會靜默跳過）

### Step 3 — ⚠ 確認「真的照設定分群」（**這步不可省**）

`repos=1` 只證明**檔案被讀到**。若鍵名寫錯導致 0 群，讀數會**跟成功時一模一樣**。

⇒ 放一顆探針再掃一次：

```bash
echo '{"probe":true}' > <repo>/<某個群的前綴下>/_probe.json
run_cmd.py --persona <me> run AutoCommit --arg op=scan --arg mode=submodules
```

Editor log 應該印出群名與訊息：

```
→ Chess [games] 1 檔：chore(chess): sync game state (auto) [1 files]
      games/_probe.json
```

看到這行才算通。驗完**把探針刪掉**。

### Step 4 — 提交

```bash
run_cmd.py --persona <me> run AutoCommit --arg op=commit --arg mode=submodules
```

逐群一筆 commit，**純 git commit**（無 trailer／無公告／不領薪），**不 push、不 bump 父層**。

### Step 5 — 設定檔本身要 commit

設定檔是有作者的產出 ⇒ 走 `git_commit.py`，不要讓它被自動 commit 收走
（它未被任何群命中時會落 `__other`，本來就不會被自動收）。

## 3. 欄位與判準

| 欄位 | 意義 | 判準 |
|---|---|---|
| `Enabled` | 這個 repo 的自動提交是否啟用 | **欄位缺席＝視為啟用**（相容 2026-08-21 之前的檔）；**新建的檔一律顯式 `false`** |
| `Name` | 顯示名 | 空的話用目錄名 |
| `Groups[].Key` | 群 key（commit 分組單位） | 不可叫 `__other` / `__subptr`（保留）；不可重複 |
| `Groups[].Label` | 畫面顯示名 | 作者自己寫，**不進多語系表** |
| `Groups[].MatchPrefixes` | **相對 repo root 的正斜線前綴**清單，任一命中即屬本群 | 不可空字串（會吃掉整個 repo）；不可含反斜線 |
| `Groups[].Message` | commit 訊息主體 | 檔數統計由呼叫端補在後面；不可空白 |
| `Groups[].DefaultOn` | 沒指定 `groups=` 時是否納入 | 對「不確定要不要自動收」的群設 `false` |

**群怎麼切**：判準是「**這批檔會不會想一起被讀 history**」，不是「它們住哪個目錄」。
一群 = 一筆 commit = 日後有人 `git log` 時想一次看到的那一組。

**順序即優先序** —— 第一個命中的群收走該檔。前綴有重疊時把**窄的放前面**。

## 4. 地板（設定檔掀不動的部分）

| 保證 | 靠什麼保證 |
|---|---|
| ephemeral 檔永不進候選（`*.log` / `*.tmp` / `_wait_*` / `DebugLogs/`…） | `Classify` 的**判定順序**：subptr → ephemeral → 分群。**不是**「呼叫端記得先檢查」 |
| `__other`（未分類）不自動收 | 它不是 `GroupDef`，不在任何預設集合裡；要收得顯式 `--arg groups=__other` |
| `__subptr`（巢狀 submodule pointer）不自動收 | 同上 —— bump 了別人會 pull 不到 hash |
| detached HEAD 的 repo 跳過 | `ScanOne` 擋下並說原因（游離 commit 沒有分支指到它） |
| 錯配一眼可驗 | 設定檔只吃**前綴清單、不吃 regex** —— 比程式碼更受限 |
| 壞設定不會被寫進去 | `Save()` 先跑 `Validate()`，不合法丟例外不寫檔；後台的存檔按鈕也會停用並逐條列出原因 |
| 建立設定檔不會順便開始自動 commit | `CreateDefault()` 顯式寫 `Enabled=false` ——同意必須是另一個動作，不是選取的副作用 |
| 「開著卻永遠收不到東西」被擋下 | `Validate()`：`Enabled=true` 但零分群 ⇒ 不合法。那種狀態跟停用不可分辨，所以不准存在 |

> ⚠ **AgentCommands 本層與 persona 信件庫不吃設定檔** —— 那兩組分群仍寫死在 `UCL_AutoCommitRules`
> （`[chat]` 獨立 commit 是 `CLAUDE.md` 等級的硬規則）。設定檔只管**其他 repo**。

## 5. 用後台頁改設定

ToolBox →「自動提交」頁 →「⚙ Submodule 自動提交設定」折疊區：

- 列出掃到的設定（含檔案路徑），**整個設定物件走 `DrawObjectData` 反射繪製** —— 加欄位時頁面不用改
- 改完按 **💾 存檔**；不合法時按鈕停用並把每一條原因印在旁邊
- **↩ 放棄改動** ＝ 重新從磁碟載入
- 讀檔只發生在頁面 `Init` 與「重新載入」按鈕 —— **`Draw` 裡零 IO**
  （IMGUI 的 Layout 與 Repaint 是兩個 pass，Draw 裡碰磁碟會讓兩趟看到不同的東西，
  症狀是 `ArgumentException` 中止該幀繪製）

## 6. 常見坑

| 症狀 | 真因 |
|---|---|
| `repos` 沒增加 | 設定檔不在 repo **根**；或該 repo 不在 `<data_root>/.gitmodules` |
| `repos` 增加但沒有任何群命中 | 鍵名拼錯（大小寫敏感：`Key` / `Label` / `MatchPrefixes` / `Message` / `DefaultOn`）⇒ **讀數與成功時同形**，靠 Step 3 的探針才分辨得出來 |
| 前綴寫成 `\` 開頭或含反斜線 | 比對用的是**正斜線相對路徑**；`Validate()` 會擋 |
| 一個群吃掉整個 repo | 前綴是空字串（`StartsWith("")` 命中每一個檔）；`Validate()` 會擋 |
| 加了設定檔卻什麼都沒收 | `Enabled` 還是 `false`（自動建立的預設值）⇒ 看 `disabled_repos` |
| 設定檔自己出現在候選清單 | 正常 —— 它沒被任何群命中 ⇒ 落 `__other` ⇒ 不會被自動收 |
| `blocked_repos` 有數字 | 設定壞掉或讀取失敗。**「設定寫錯」與「這個 repo 沒設定」刻意是兩種可分辨的結果** |

## 7. 為什麼是設定檔而不是寫死（沿革）

2026-08-07 拍板：規則寫在程式碼、不開放編輯 ——「能在 UI 亂改的規則等於沒有規則」。
2026-08-21 **部分撤銷**：每接一個新的資料 repo 就要回頭改 UCL_Core 加一組寫死的，
而規則本來就屬於那個 repo。

撤銷後的形狀是**「可宣告、但掀不動地板」**：
設定檔跟當年那句針對的「執行期參數」不是同一種東西 ——
它**入版控、由它管的那個 repo 擁有、改動在 diff 裡看得見**；
而地板由**判定順序**保證，不是靠任何人記得檢查。
