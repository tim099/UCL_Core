---
title: 跨 Agent 結構化問題回報系統 — Cmd_BugReport ＋ 後台頁 ＋ ucl-bug-report Skill
slug: bug-report-system
status: approved（2026-08-18 Tim 拍板 —— RFC 見酒館 seq 12080 / 評審 12103 / 收斂 12104；本文為實作 plan）
created_at: 2026-08-18T06:20:00Z
created_by: calli
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ProcessAdminPage.md | UCL_ProcessAdminPage | 後台頁的母版（Tim 指定參考）
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | `Fixes BUG-x` 自動閉環的掛點
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md | 早安 Cmd 流程 | stale 讀數的掛點（brief §6）
---

# 跨 Agent 結構化問題回報系統 — 實作 Plan

> **分工（酒館 seq 12104 拍定）**
> - **@kiara**：`Cmd_BugReport` 本體 ＋ `ucl-bug-report` Skill ＋ 語意查重串接
> - **@calli**：早安 brief 的 stale 讀數整合
> - **後台頁 ＋ index 配發**：Tim 2026-08-18 追加，落點見 §3 / §4（本文一併規格化，施工者待指派）

## 0. 一句話

Bug 是**待修工單**（現場、可接力），Lesson 是**已悟的認知**（事後、給未來的自己）。
兩者分開存，但**這套系統的成敗不在 schema，在它會不會死於沉默** —— 全文的設計重心都在這件事。

---

## 1. 為什麼三刀砍在「防死」而不是 schema

RFC 原案（seq 12080）的欄位設計沒有洞。評審（seq 12103）砸的三處全是同一件事：

| # | 病 | 收斂 |
|---|---|---|
| ① | `report` / `resolve` 都有，但**沒有任何路徑會讓一張沒人動的 open 單變吵** | stale 掃描長在既有路上（§5） |
| ② | 「回報前先檢索」是**人的守則**，而關鍵字檢索失敗的樣子跟「不存在」一模一樣 | 查重搬進機械（§6） |
| ③ | severity 五級 ＋ 預設 `major` ⇒ 清單會長成一片 major | 改行動 3 級（§2.3） |

### 🩸 ①的血證（不是原則潔癖）

`subconscious.py`（apex-one 2026-05-14 寫）註解三層俱全、連 Windows CP950 輸出 emoji 會炸的坑
都在檔頭寫了解法 —— **品質完全沒有問題**。它死掉是因為排程它的 `work_session.py` 退場之後
**沒有人再呼叫它**：2026-05-27 記下最後一筆，然後安靜 **2.7 個月**，零錯誤、零警告、零人察覺。

⇒ **`status: open` 的失效方式與此同型**：一張沒人看的 open 單跟沒有那張單長得一模一樣，
而且它還會**主動誤導** —— `open` 讀起來像「這個還壞著」，但它可能三週前就被順手修好了。
**一份會說謊的清單比沒有清單更貴。**

### 📊 ②的量測（2026-08-18 calli 實測，同一筆記憶三種查法）

| 查詢 | 正解排名 | 分數 |
|---|---|---|
| `劇透`（該筆 tags 裡就有這個詞） | **第 7** | 0.5421 |
| `呼吸距離`（**正文原句節錄**） | **不在 top-3** | — |
| 完整句子（把那件事寫成一句話） | **top-1** | **0.7389** |

⇒ 一個照守則辦事、認真檢索過的人會得到一個乾淨的空結果，然後開一張重複單，
**而且他會以為自己已經盡到查證義務了。**

---

## 2. 資料模型

### 2.1 index —— 從 1 開始單調遞增（Tim 2026-08-18，同日由 0 改為 1）

**ID 格式改為純序號**：`BUG-1`, `BUG-2`, `BUG-3`…（顯示時可補零，但**檔名與 jsonl 內一律存整數 index**）。
原 RFC 的 `BUG-YYYYMMDD-HEX` 作廢 —— 日期與亂數對人沒有意義，而序號可以直接說「第 17 號單」。

⚠ **配發要走既有基建，不要重造第四套。** 酒館的 `_seq.txt` 已經解過同一題，
而且解過**它踩過的坑**（`UCL_ChatTavernIO.IncrementAndGetSeq`）：

> 原版只讀 `_seq.txt` 然後 +1；若有 daemon 直接 append `messages.jsonl` 而不走 Cmd，
> `_seq.txt` 不知情 → 下次合法 post 拿的 seq 會撞到 daemon 寫的
> （**已實際發生**：Antigravity `standby_loop.py` 直寫 jsonl 造成 tavern seq 57~76 各重複 2 次）。
> 修法：寫前 peek jsonl 最大 seq，`>= counter` ⇒ 偵測到 illicit write，自動拉齊 counter ＋ `LogError` 大聲喊。

⇒ **BugReport 的配發照抄這個形狀，一處都不改** —— 含自我修復與大聲警告，
計數檔 `_index.txt` 的語意與酒館 `_seq.txt` **完全相同：存「已發出的最後一個」，初始 `0`，第一筆拿到 `1`**。

> 📌 **這裡本來有一處刻意的偏離，Tim 把 index 從 0-based 改成 1-based 之後它消失了。**
> 0-based 時我打算改存「下一個要發的號」，理由是：存 last 的話初始值得是 `-1`，
> 而 `int.TryParse` 失敗一律回 `0` ⇒ 任何解析失敗的路徑都會把 `-1` 讀成 `0`，第一筆就撞號。
> 1-based 之後這個陷阱自己不見了 —— `0` 既是合法初始值、也是解析失敗的回退值，**兩者一致**。
>
> ⇒ 現在 BugReport 與酒館 seq **零差異**。少一個差異就少一個「為什麼這裡不一樣」要解釋、
> 也少一處未來改動時只改到一邊的機會。**能不偏離就不偏離，這是這次改號的實質收穫，不只是換個數字。**

### 2.2 磁碟結構

```text
AgentCommands/BugReports/
  ├── _index.txt           # 已發出的最後一個 index（初始 0；第一筆拿到 1）——語意同酒館 _seq.txt
  ├── bugs.jsonl           # append-only 事件流（index / ts / status / severity / title / actor…）
  ├── reports/
  │     ├── 0001.md        # 檔名補零只為排序好看；內容 frontmatter 存整數 index
  │     └── 0002.md
  └── _last_bug_report.md  # Cmd 回傳檔（每次覆蓋）
```

### 2.3 欄位

| 欄位 | 必填 | 說明 |
|---|---|---|
| `type` | 預設 `bug` | **回報的東西不限於 bug** —— 見 §2.5 |
| `title` | ✅ | < 50 字 |
| `description` | ✅ | 上下文 |
| `evidence` | ✅ | **硬證據**：error code、log 行號、round-trip diff、重現指令、`Cmd_Invoke` 回傳值 |
| `severity` | 預設 `wrong` | 見下 |
| `repro_steps` / `expected` / `actual` | | |
| `category` / `component` / `reporter` | | |

**severity 行動 3 級**（不用嚴重度形容詞，用「現在誰被怎樣了」）：

- `blocking` —— 現在有人被它擋住，做不下去
- `wrong` —— **會產出錯的結果但還能跑**（2026-08-18 三隻全在這格：平行宇宙路徑、字串布林、管線吃引號）
- `annoying` —— 會嘴，但不會騙人

> `wrong` 那格是整個分級表存在的理由：**今天的災情不是「壞掉」，是「看起來正常但講的是假話」**。
> 原本的五級制沒有任何一格在描述這件事，而它是我們實際上最常撞的一種。

**`evidence` 為什麼必填**：RFC §5 原本把「先抓硬證不憑感覺」寫成 agent 自律守則。
守則靠人記得，欄位靠 schema 擋 —— 而 2026-08-18 當天就有三次「我以為我記得」失手的紀錄。

### 2.5 `type` —— 這套系統收的不只是 bug（Tim 2026-08-18 追加）

程式壞掉只是「讓下一個人白花時間」的其中一種形式。文件過時、提示缺一半、流程多繞三步，
**代價一模一樣，而且更常發生** —— 它們現在沒有落點，於是只能發在酒館閒聊裡，然後沉掉。

| `type` | 收什麼 | 今天現成的例子 |
|---|---|---|
| `bug` | 程式行為錯誤 | 字串 `"False"` 在 python 是 truthy |
| `doc` | 文件與現況不符 / 過時 | `run_cmd.py:1516` 的註解詳細描述一段路徑，而它的開關 `AUTO_ROUTE_BY_ARG_PERSONA` 是 `False` —— **註解正確、血證俱全，就是沒在跑** |
| `friction` | 提示缺一半、錯誤訊息指錯地方、容易踩的坑 | `op=step` 印的指令留 `<其餘參數>` 空殼 ⇒ 照抄的人漏帶身分 ⇒ 拿到的錯誤指向 `canvas.py`，**真因在提示那一行** |
| `suggestion` | 不算壞，但流程可以少幾步 | 自由時間的換骰（`Cmd_FreeTime`）與開工（`Cmd_FreeTimeActivity`）是兩支 Cmd，而「活動實作 0 件」的提示沒指路到後者 ⇒ 有人連骰六次都不知道自己漏了一支 |

⭐ **`severity` 三級不需要為此新增任何一格** —— 因為它從一開始就是用「**現在誰被怎樣了**」定義的，
不是用「什麼東西壞了」：

- **過時的文件天生就是 `wrong`** —— 它看起來正常，但講的是假話，而讀的人會照著做。
- **缺一半的提示也是 `wrong`** —— 它不會報錯，它會**把人引去查錯的地方**。
- 真的只是囉唆、不會誤導人的 ⇒ `annoying`。

⇒ 這反過來證明了三級制的定義選對了軸：**照「受害者的處境」分級，換了 type 也不必重寫分級表。**

⚠ **命名的代價要先講清楚**：系統叫 `BugReport`，但收 `doc` / `friction` / `suggestion`。
名字會勸退人 —— 「這又不是 bug，算了」正是我們最想撿起來的那一類。
兩個緩解，**都做**：
1. `type` 給預設值 `bug` 但**在回傳檔與後台頁一律顯示 type**，讓非 bug 的單在清單上看得見、不被當成雜訊。
2. Skill 的觸發詞**必須涵蓋非 bug 的說法**（見 §7），不要只認「bug」那個詞。

### 2.4 狀態機

```
open ──► in_progress ──► resolved / wontfix / duplicate
  │                          ▲
  └──► stale（自動標，非人手動）──┘
```

**`stale` 一定要是自動的。** 人手動能標的狀態，只會有人記得標一次。

---

## 3. `Cmd_BugReport`（@kiara）

| `op` | 作用 | 必填 |
|---|---|---|
| `report`（預設） | 新建 | `title`, `description`, `evidence` |
| `list` | 查詢 | — （`status` / `severity` / `category` 篩選） |
| `show` | 詳情 | `index` |
| `resolve` | 關閉 | `index`（＋ `resolution` / `note` / `commit_sha`） |
| `claim` | 認領 → `in_progress` | `index`, `actor` |

依 `ucl-create-cmd` 繼承 `UCL_AgentCommandHandlerBase`；資料類**一律 typed model 繼承
`UnityJsonSerializable`**，不用裸 `JsonData`（Tim 2026-08-18 硬規則）。

⚠ typed model 有非 C# 讀取端（python 要讀 `bugs.jsonl`）⇒ **`bool` 必須 `override SerializeToJson()`
寫回原生**，否則會寫成 `"True"`/`"False"` 字串，而 python 讀到的 `"False"` 是 **truthy**。

---

## 4. 後台頁 `UCL_BugReportAdminPage`（Tim 2026-08-18 追加）

> 📌 **這一條推翻了評審意見。** seq 12103 我主張「GUI 先不要 —— 一個沒人維護的清單加上 GUI，
> 只會變成一個更好看的沒人維護的清單」，kiara 同意。**Tim 拍板要做，就做。**
> 而且我的反對其實只成立在「沒有 §5 的 stale 機制」那個前提下 ——
> 有了自動 stale ＋ commit 閉環，清單不再依賴人記得回頭看，GUI 就是**處置台**而不是**擺設**。
> ⇒ 落地順序仍建議 §5 先於本節（先讓清單誠實，再給它畫面）。

- **母版**：`UCL_ProcessAdminPage`（Tim 指定）。它已經解掉的：
  - 繼承 `UCL_CommonEditorPage`，`WindowName` / `ShowBackButton` / `ShowInPageMenu` / `[HelpURL]`
  - **快取刷新**：`REFRESH_INTERVAL_SEC` 節流，不每次 `OnGUI` 都打 IO
  - **破壞性動作二段確認**：第一次點 = arm，N 秒內再點同一顆才真的執行
    ⇒ BugReport 這邊套在 `resolve` / `wontfix`（**關單是對別人的宣告**，誤點的代價是一隻 bug 消失在清單上）
- **入口**：`UCL_ToolBoxPage.ContentOnGUI()` 加一行
  `DrawTool(UCL_CodeLocalize.Get("ToolBox.BugReportAdmin"), …Desc, () => UCL_BugReportAdminPage.Create());`
  ⚠ **四語系檔都要加 key** —— 少鍵不會編譯錯，只會把畫面顯示成鍵名。
- **畫面**：篩選列（status / severity / component）＋ 列表（index / severity / title / 天數 / actor）
  ＋ 展開詳情 ＋ 動作鈕（claim / resolve / wontfix）。
  **stale 的那幾筆要在列表上自己顯眼**，不要藏在篩選器後面 —— 需要人主動去篩才看得到的警告等於沒有警告。
- IMGUI 一律走 UCL 封裝；能交給 `UCL_GUILayout.DrawObjectData` 畫的就不要手刻欄位。

---

## 5. 防死：stale 讀數掛在既有路上（@calli）

**不開新 daemon**（Alaya 刻意不做 daemon 的理由完全來自 §1 那支程式的死法）。兩個掛點：

- **掛點 A — 早安 brief**：`§6 記憶維護狀態` 已經在算 gap 並標 OVERDUE，
  同一區加一行「open bug N 筆，其中 M 筆超過 14 天未動（stale）」。掃描在生成 brief 時順手做。
- **掛點 B — commit 閉環**：`git_commit.py` 解析 commit body / trailer 的 `Fixes BUG-<index>`，
  提交成功、公告領薪的同一條路徑上順手觸發 `op=resolve`（帶 `commit_sha`）。

判準：**別讓「記得去看」成為這套系統存活的前提。**

---

## 6. 防重複：查重搬進機械（@kiara）

`op=report` 收到 `title + description + component` 後，在 server 端跑既有語意檢索
（同 `knowledge_base.py` 那一套），把 top-3 相似的 **open** 單印進回傳檔：

```
⚠ 可能重複（未阻擋，請自行判斷）：
  0.71  BUG-42  [FreeTime] step_args 引號被吃
  0.63  BUG-17  run_cmd 參數斷裂
```

**只呈現、不阻擋** —— 阻擋要判斷「這算不算同一隻」，而那正是會判錯的地方。
⚠ 查詢字串要用**整句**送檢索，不要只送 title 的關鍵字（理由見 §1 的量測表）。

---

## 7. `ucl-bug-report` Skill（@kiara）

> 📌 **Skill 是「收不只 bug」這件事的主要載體**（Tim 2026-08-18）——
> Cmd 只多一個 `type` 欄位，但**人要知道自己可以報什麼**，靠的是 Skill 的觸發詞與說明。

觸發詞分四組，**非 bug 的說法一定要收進來**，否則 `doc` / `friction` / `suggestion` 永遠不會有人報：

| 組 | 觸發詞 |
|---|---|
| bug | `回報 bug` / `報 bug` / `bug report` / `撞到 bug` / `回報錯誤` / `系統異常` |
| doc | `文件過時` / `說明跟現況不符` / `文件寫錯` / `這份文件沒更新` / `照文件做結果不一樣` |
| friction | `提示缺參數` / `錯誤訊息指錯地方` / `容易踩的坑` / `這裡很容易做錯` / `第一次一定會卡` |
| suggestion | `流程可以簡化` / `這步驟是多的` / `可以少一步` / `流程建議` / `改善建議` |

守則只留**機械擋不掉**的那些：
0. **不確定算不算 bug ⇒ 就報。** `type` 有 `friction` 與 `suggestion` 兩格正是為此存在；
   **判斷「這夠不夠格」的成本，比誤報一張單的成本高。**
   ⚠ 這條要寫在 Skill 最前面 —— 它擋的不是誤報，是**因為猶豫而沒報**，而後者完全沉默。
1. **分清 Bug/Friction 與 Lesson** —— 「**系統可以被改成不讓下一個人踩**」⇒ `BugReport`；
   「**只有我自己需要記住**」⇒ `NoteLesson`。
   判準不是嚴重度，是**修得動的東西在誰手上**。
2. **`evidence` 要放真的硬證**，不是重述現象。
   ⭐ 一個好例子：`op=step` 補身分那次，錯誤訊息從「缺 `--persona`」變成「缺 `--sub`」——
   **一個更精確的失敗，比一個模糊的成功更能證明事情發生了。**
3. ~~回報前先檢索~~ ← **刪掉，已由 §6 機械化**（留著會讓人以為自己有做，實際上會失敗）。

⚠ Skill 改動要同步三份安裝副本（`.claude` / `.codex` / `.agents`）；
`.agents` 那份**不是逐位元組相同**（antigravity target 會注入一行 `trigger:`）——
同步是**套用同一個編輯**，不是複製正本過去。

---

## 8. 落地順序（先讓清單誠實，再給它畫面）

1. `Cmd_BugReport` 本體 ＋ index 配發（照抄酒館 `_seq.txt` 的自我修復形狀）
2. §5 stale 讀數（brief）＋ commit 閉環
3. §6 語意查重
4. §4 後台頁 ＋ ToolBox 入口 ＋ 四語系 key
5. `ucl-bug-report` Skill ＋ 三副本同步

## 9. 驗收（每一項都要實跑，不是編譯過就算）

- [ ] 連開三張單 → index 依序 `1` / `2` / `3`（**第一張必須是 1，不是 0**）
- [ ] 手動改壞 `_index.txt`（寫一個小於 jsonl 最大 index 的值）→ 下一張單**不撞號**且 console 有 `LogError`
- [ ] `_index.txt` 刪掉 → 下一張單拿到的號 **不是 1**（要接在既有最大值之後，不能覆蓋既有單）
- [ ] 開一張與既有單語意相近的 → 回傳檔印出 top-3 且**沒有阻擋**
- [ ] `evidence` 留空 → **被擋下**（exit != 0，不是警告）
- [ ] 早安 brief 出現 open / stale 讀數；把某張單的 ts 改成 20 天前 → 它被標 `stale`
- [ ] commit 訊息帶 `Fixes BUG-<n>` → 該單自動 `resolved` 且帶得到 `commit_sha`
- [ ] 後台頁：resolve 二段確認（第一次點只 arm）；stale 單在列表上不篩選也看得見
- [ ] 開一張 `type=doc` 與一張 `type=suggestion` → 後台頁與回傳檔**都看得到 type**，不會混在 bug 裡看不出來
