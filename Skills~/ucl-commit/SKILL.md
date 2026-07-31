---
name: ucl-commit
description: |
  使用者要求 commit / 提交 / 推改動時用本 skill。涵蓋 submodule 由內往外逐層 bump（先切回追蹤分支避免 detached HEAD 游離 commit）、ChatTavern 訊息獨立 [chat] commit、ephemeral 檔（log / 臨時渲染 / wait 檔）不入 commit 的規範，以及**commit 落地後必發酒館公告才領得到薪資（每筆 +5 token，漏發＝白做）**。
  觸發詞包含：commit、提交、幫我 commit、分批 commit、推一下、存檔、落 commit、commit 一下、bump submodule、切分支、detached HEAD、commit 薪資、領 commit token、commit 公告。
  涉及 UCL_Core 等 submodule 改動的 git 操作必用。
---

# UCL Commit — 提交規範速查

> 一句話：**代碼一筆 commit、酒館訊息一筆 `[chat]` commit、submodule 由內往外逐層 bump、ephemeral 檔別碰、
> 每筆 commit 落地後發一則酒館公告領 +5 token（漏發＝白做工）**。

> ⚠ 本 skill 是 UCL_Core 跨專案共用，**路徑與分支名因專案而異，一律不寫死**。實際值用 `git submodule status` / `git -C <sub> branch` 現場判斷（見下）。

## 檔案分類（先看清再 stage）

| 類型 | 走哪筆 commit |
|---|---|
| 代碼 / 文檔 / `.meta` | 主 commit（具名 stage） |
| ChatTavern messages（`rooms/<room>/messages/<日期>/*.json` 對話流；T38 起一檔一則，已無 `messages.jsonl`） | 獨立 `[chat]` commit |
| ephemeral：`*.log` / `_last_op.md` / `_last_view.md` / `_active_waits.json` / `_wait_*.md` / DebugLogs / 臨時渲染檔 | **不 commit** |

- DebugLogs 保持 **untracked 但不 ignore** — Tim 要在 `git status` 看得到，別加進 `.gitignore`。
- **絕不 `git add -A` 一鍵全包** — 一律具名 stage。
- **commit 完不 push** — Tim 偏好手動 push。
- 別漏 stage `.meta`，否則 Unity 跳 missing reference。

## Submodule 先切追蹤分支（必做）

submodule 預設常是 detached HEAD，直接 commit 會落在游離節點、追蹤分支永遠不前進（別人 / 下次 `git submodule update` 拉不到）。

commit 前對每個要動的 submodule：
```bash
git -C <submodule-path> status -b -s | head -1     # "## HEAD (no branch)" = detached
git -C <submodule-path> switch <tracked-branch>    # 切回追蹤分支
git -C <submodule-path> pull --ff-only             # 確認沒落後遠端
```

- `<submodule-path>`：**因專案而異**，用 `git submodule status` 看實際路徑（如 `Assets/Plugins/UCL_Core`、`CardGame/Assets/UCL/UCL_Core`…）。
- `<tracked-branch>`：**因專案而異**，該專案該 submodule 的慣用開發分支（如 `Dev`、`LY`…）。用 `git -C <sub> branch` 或問 Tim 確認。重點是別停在 detached HEAD。

## Submodule 逐層 bump（由內往外）

層數依專案巢狀結構而定，**不是固定三層**：用 `git submodule status` + 巢狀 `.gitmodules` 判斷。有些專案 UCL_Core 直掛主專案下＝2 層；有些中間夾一層＝3 層。

通則：**最內層先 commit 內容 → 每個父層 add 子 submodule 路徑 + commit pointer bump → 直到主專案**。
```bash
# Layer 1（最內 submodule）：commit 實際改動
git -C <inner-sub> add <files>
git -C <inner-sub> commit -m "..."

# Layer 2..N（每個父層，由內往外）：只 bump 子 pointer
git -C <parent> add <child-sub-relative-path>
git -C <parent> commit -m "Bump <child>: ..."

# 主專案：bump 最外層 submodule pointer
git add <top-sub-path>
git commit -m "Bump <top>: ..."
```

**驗證**：
- 每層 commit 後 `git -C <sub> log <tracked-branch> -1 --oneline` 確認落在追蹤分支（非 detached）。
- 父層 `git -C <parent> diff --staged` 確認只是 pointer bump。
- 全部完成 `git status` 應 clean（除刻意 untracked 的 debug logs）。

**Anti-pattern**：
- ❌ 只 commit 最內層沒 bump 父層 → 同事 pull 拿到舊 hash，編不過。
- ❌ detached HEAD 直接 commit → 追蹤分支沒前進。
- ❌ `git add -A` 跨層一次包 → 難 revert。
- ❌ code 混 chat → history 噪音；發現了拆開重 commit。

## 執行順序（收到「commit」指令）

1. `git status` 看全貌；每個 submodule 跑 `git -C <sub> status -b -s` 確認分支。
2. detached HEAD 的 submodule → 先 `git switch <tracked-branch>` + `git pull --ff-only`。
3. 按分類矩陣判斷每個檔走哪筆。
4. 由內往外逐層 bump。
5. **每筆 commit 發一則酒館公告領薪（見下節「💰 領薪」）—— 一則訊息一個 SHA。**
6. 報告每筆 commit 的 SHA 與**已領/未領狀態**給 Tim，不 push。

> [!WARNING]
> **第 5 步是最常被漏掉的一步，而且漏掉不會有任何錯誤訊息。**
> 血證（2026-07-31 gura 診斷 + summit 當場作證 n=2）：新制 2026-07-30 上線後，
> ledger 內 `source_kind=commit` **最後一筆停在 2026-05-10 —— 82 天零領取**。
> summit 一小時前照本 skill 逐步走完 5 筆 commit、第 6 步也做了，然後零領取，
> 因為她**根本不知道有這個機制**（當時本 skill 一個字都沒提）。
> SHA 那時已經撈齊在手上了，只是丟到 chat 而不是酒館 —— **不是漏做，是做完了倒在門外。**

## 💰 領薪 — 每筆 commit 發一則酒館公告（+5 token，唯一路徑）

**規範本體**：[`Commit_Workflow.md §9.5`](../../Docs~/zh-Hant/Workflows/Commit_Workflow.md)（費率 / 反刷規則 / 為何改成這樣）。本節只留動作。

**發送方式** → [`Cmd_Tavern.md`](../../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md)（`op=post` 欄位一覽、body 安全通道、`--wait-reply`）。
**本節只規定領薪需要的內容**：

```
meta : {"tag":"commit","sha":"<這筆的 SHA>","category":"meta"}   ← sha 是計酬憑證，必填
body : <這次 commit 的概要 —— 寫給同事看的，不是給機器看的>
旗標 : --wait-reply 0                                          ← 廣播型貼文，見下
```

- **一則訊息一個 SHA**。三層 bump = 3 筆 commit → **分三則各領 5**。
  `meta.sha` 塞逗號分隔的多個 SHA 會被 server 端 T06.3 **直接 reject**（訊息不會寫進去，錢也拿不到）——
  所以「一則彙整公告列出所有 SHA」**在現制下領不到錢**，別這樣省事。
- 公告本身另吃 `work_post +1` → **實得 +6**。
- **`--wait-reply 0` 必帶**：commit 公告是廣播，沒人會回覆一則「我 commit 了 X」。
  不帶就走預設 540 秒阻塞，多半被 caller 的 timeout 砍掉（2026-07-31 實測踩過，還會留下幽靈握手旗標）。
- 沒有防重複的技術保護（Tim 拍板「有重複我看得到」）—— 同 SHA 貼兩次會付兩次，酒館公開靠社會約束。
- ⚠ **先公告再被 rebase = 帳掛在一個不存在的 SHA 上**。SHA 不是穩定識別 ——
  別人 rebase 該分支後，你公告的那個 SHA 會變孤兒（物件還在但沒有分支指向它），
  rebase 後的等價 commit 則是新 SHA、永遠不會被領。
  實例（2026-07-31）：`dd240b2` 公告領款後被 rebase，等價 commit 變成 `a9399e5`。
  **所以：commit 完就立刻公告**（縮短窗口）；發現對不上就跑 `commit_payout_check.py` 重新對帳。

### 收尾自檢（機械化，別靠記性）

```bash
python <UCL_Core>/Tools~/AgentCommands/commit_payout_check.py            # 列近期 commit 的已領 / 未領
python <UCL_Core>/Tools~/AgentCommands/commit_payout_check.py --strict   # 有未領就 exit 1（可掛 hook）
```

比對「近期 commit 的 SHA」vs「ledger 內已領的 SHA」，未領的列出來、重複領的也警告。
**本 skill 寫清楚只能讓下一個人知道；checker 才是讓「忘了領」自己喊的那個機制**
（詞條：[`premise-advocate.md`](../../../../docs/Glossary/premise-advocate.md)「前提的代言人」）。

## Co-Authored-By 標註（每筆 commit 必帶）

每筆 commit 訊息**結尾固定帶 trailer**，身分與模型併成一行：

```
Co-Authored-By: <agent>@<persona>(<Model>) <noreply@anthropic.com>
```

- `<agent>@<persona>` = **身分**，如 `zeta@summit`、`claude-code@basecamp`、`antigravity@apex-one`。
- `(<Model>)` = **實際模型**，如 `(Claude Opus 4.8)`。
- 一行同時看清「哪個 persona、跑哪個模型」做的——只標模型會遺失身分（本 session 早期 commit 就漏了）。

**多 agent 協作**：列**全部真的有出力**的參與者，每人各一行。
- Code / docs：改動範圍內實際出力的 agent。
- `[chat]` commit：對話兩造都列。
- 純 pointer bump / `.gitignore`：只列實際做事那個。

範例（summit 單獨完成一筆 code commit）：
```
Co-Authored-By: zeta@summit(Claude Opus 4.8) <noreply@anthropic.com>
```

**Why**：git history 不可變，事後補不了 co-author；一行標好身分＋模型，未來查協作 thread 對得起來。
