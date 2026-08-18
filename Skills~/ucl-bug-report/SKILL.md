---
name: ucl-bug-report
description: |
  結構化問題回報 —— **收的不只是 bug**：文件過時、提示缺一半、錯誤訊息指錯地方、流程可以少幾步，全部走這裡。
  走 `Cmd_BugReport`（report / list / show / claim / resolve），一單一檔存在 `AgentCommands/BugReports/reports/<index>.md`；
  後台頁 = ToolBox → 問題回報管理。修好之後 commit 訊息帶 `Fixes BUG-<n>` 會自動關單。
  觸發詞 (case-insensitive substring)：
  - **bug**：回報 bug / 報 bug / bug report / 撞到 bug / 回報錯誤 / 系統異常 / 這裡壞了
  - **doc**：文件過時 / 說明跟現況不符 / 文件寫錯 / 這份文件沒更新 / 照文件做結果不一樣
  - **friction**：提示缺參數 / 錯誤訊息指錯地方 / 容易踩的坑 / 這裡很容易做錯 / 第一次一定會卡
  - **suggestion**：流程可以簡化 / 這步驟是多的 / 可以少一步 / 流程建議 / 改善建議
  跨 agent 通用 — Claude / Codex / Antigravity / Gemini 都走同一套單號與同一個資料夾。
---

# UCL Bug Report — 結構化問題回報

> 一句話：**只要「系統可以被改成不讓下一個人踩」，就開一張單。** 是不是 bug 不重要。

## 0. 最重要的一條：不確定算不算，就報

`type` 有 `friction` 與 `suggestion` 兩格正是為此存在。
**判斷「這夠不夠格開單」的成本，比誤開一張單的成本高。**

⚠ 這條擋的不是誤報，是**因為猶豫而沒報** —— 而後者完全沉默，沒有任何人會發現你放棄了。

## 1. 這裡收什麼（`type`）

| `type` | 收什麼 | 例子 |
|---|---|---|
| `bug` | 程式行為錯誤 | 字串 `"False"` 在 python 是 truthy，判斷式靜默走錯邊 |
| `doc` | 文件與現況不符 / 過時 | 註解詳細描述一段邏輯，而它的 feature flag 是 `False`，那段根本沒在跑 |
| `friction` | 提示缺一半、錯誤訊息指錯地方、容易踩的坑 | 提示印 `<其餘參數>` 空殼 ⇒ 照抄的人漏帶身分 ⇒ 錯誤指向別的工具 |
| `suggestion` | 不算壞，但流程可以少幾步 | 兩支 Cmd 要配合使用，而其中一支的提示沒指路到另一支 |

## 2. 跟 `NoteLesson` 怎麼分

**判準不是嚴重度，是「修得動的東西在誰手上」：**

- **系統可以被改成不讓下一個人踩** ⇒ `BugReport`（別人也會踩，而且可以被修掉）
- **只有我自己需要記住** ⇒ `NoteLesson`（認知/習慣，沒有東西可以改）

同一件事常常**兩邊都要記一筆**：坑本身開單，自己為什麼會掉進去記 lesson。

## 3. 怎麼開單

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run BugReport \
    --arg op=report \
    --arg title="<簡述，< 50 字>" \
    --arg-file description=<檔> \
    --arg-file evidence=<檔> \
    --arg type=bug|doc|friction|suggestion \
    --arg severity=blocking|wrong|annoying \
    --arg component="<受影響檔案或模組>" --arg reporter=<me>
```

> **長文一律走 `--arg-file`**，不要用 `--arg x="…"` 塞多行 —— 那條路會經過 shell，
> 反引號與引號會被吃掉（同 `ucl-commit` 的血證）。

### `severity` 三級 —— 用「現在誰被怎樣了」判，不是用「多嚴重」

- `blocking` —— 現在有人被它擋住，做不下去
- `wrong` —— **會產出錯的結果但還能跑**（預設值；看起來正常，講的是假話）
- `annoying` —— 會嘴，但不會騙人

⭐ **過時的文件天生就是 `wrong`**，缺一半的提示也是 —— 它們不報錯，但會把人引去查錯的地方。

### ⛔ `evidence` 是必填，缺了直接被擋（exit != 0）

要放**感官騙不了的硬證**：error code、log 行號、round-trip diff、重現指令、`Cmd_Invoke` 的回傳值。
**重述現象不算證據。**

> ⭐ 一個好例子：某次補上身分旗標之後，錯誤訊息從「缺 `--persona`」變成「缺 `--sub`」——
> **一個更精確的失敗，比一個模糊的成功更能證明事情發生了。**

## 4. 其餘 op

```bash
run BugReport --arg op=list                      # 預設只列沒關的；--arg status=all / stale
run BugReport --arg op=list --arg type=doc       # 只看文件類
run BugReport --arg op=show   --arg index=<n>
run BugReport --arg op=claim  --arg index=<n> --arg assignee=<me>
run BugReport --arg op=resolve --arg index=<n> --arg commit_sha=<SHA> --arg note="<怎麼修的>"
```

## 5. 關單：**優先走 commit，不要手動**

修好之後在 commit 訊息裡寫一行：

```
Fixes BUG-12
```

`git_commit.py` 會在公告成功之後自動 `op=resolve` 並帶上 SHA。
理由：修東西的人本來就要 commit，**把關單掛在他一定會走的那條路上**，
不要另外要求他記得再跑一支指令 —— 「記得」正是這套系統不能依賴的東西。

## 6. 查重：機械會提示，但**它不保證**

`op=report` 會印出可能重複的既有單。⚠ 目前是 **v1 粗篩**（標題字詞重疊 ＋ component 相同），
**不是語意檢索**。

🩸 為什麼不把「回報前先檢索」寫成守則：實測同一筆記憶三種查法 ——
關鍵字（tags 裡就有那個詞）排 **第 7**（0.54）、正文原句節錄 **不在 top-3**、
完整一句話才 **top-1**（0.74）。
⇒ **關鍵字查失敗的樣子跟「這條不存在」一模一樣，所以它不會叫。**
一個照守則辦事的人會拿到乾淨的空結果、開一張重複單，**還以為自己盡到查證義務了**。

⇒ 所以：看到提示就自己判斷，**沒看到提示不代表沒有重複**。開了重複單也不是罪。

## 7. 後台頁

Editor → **ToolBox → 問題回報管理**。列表、篩 type、展開詳情、認領、關單（二段確認）。
**stale 的單印在最上面且標色，不需要篩選** —— 需要人主動去篩才看得到的警告等於沒有警告。

## ⛔ 不可做

- ❌ **手建 / 手改 `reports/*.md`** —— 一律走 Cmd。手建的號碼會被偵測到並大聲喊（然後計數檔自動拉齊）。
- ❌ **把「已經想通的道理」開成單** —— 那是 `NoteLesson`（見 §2）。
- ❌ **沒有證據就開單** —— 會被擋；而且沒有證據的單會讓下一個人重跑一次現場，那正是這套系統要消滅的成本。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整設計、資料結構、驗收清單 | `ucl_core:Docs~/{lang}/Plan/Plan_BugReport_System.md` |
| 記 lesson（本 skill 的對偶） | skill `agent-lessons-log` |
| commit 與領薪 | skill `ucl-commit` |
