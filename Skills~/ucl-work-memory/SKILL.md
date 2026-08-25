---
name: ucl-work-memory
description: |
  工作記憶區（Work Memory）— 所有 agent 共用、以「工作主題」為單位的 knowhow 庫。
  開工前讀取該工作的拍板/坑/文件指路, 完工時整理回寫 — 換 agent 接手不斷線。
  核心分工: 知識點放文件, 記憶透過 key 標註「要找哪些文件」+ 記決策/踩坑(decision/pitfall)。
  記憶是工作期間的鷹架不是永久資產，全關後歸檔或刪除，紀錄留 git。
  記憶可跨主題關聯(links), 讀取時 --with-links 一起拉。

  觸發詞 (case-insensitive substring, 任一命中即 lazy-load):
  - 工作記憶 / work memory / workmemory / 記憶區
  - 讀取記憶 / 整理記憶 / 記錄 knowhow / knowhow / 工作knowhow
  - 接續進度 / 上次進度 / 接手工作 / 交接 / memory_topic
  - 開工前查 / 這項工作怎麼做 / 之前拍板了什麼 / 歸檔記憶 / archive memory

related:
  - <ucl_core:Docs~/zh-Hant/Workflows/Work_Memory_Workflow.md> | 完整設計與 schema | 必讀
  - <ucl_core:Docs~/zh-Hant/Workflows/Memory_Common_Principles.md> | **三層記憶共通鐵律**（格式/寫入/檢索/維護）| 本檔的三鐵律以那份為準
  - <ucl_core:Skills~/ucl-task/SKILL.md> | ucl-task | **任務承諾 / 看板進度** 歸那邊
  - <ucl_core:Skills~/ucl-memory/SKILL.md> | ucl-memory | **個人記憶 / 集體潛意識 Alaya / 回憶** 歸那邊
last_updated: "2026-08-25 v1.4 (TASK-0016: 補齊 10 個子指令、task_indices 反向索引、archive/delete 退場與墓碑機制)"
---

# UCL Work Memory — 工作記憶區

> 🎯 **核心金句**：
> **「記憶回答『為什麼』與『怎麼踩過』，Task 回答『到哪了』，文件回答『怎麼用』。三者重疊的那部分不是備援，是漂移。」**
> **「記憶是工作期間的鷹架不是永久資產，相關 Task 全完成後歸檔或刪除，紀錄留 git。」**
> **「歸檔不等於失聯：archived 主題仍有 commit 歷史錨點，絕不靜默消失。」**

---

## 🛠 CLI（`<UCL_Core>/Tools~/AgentCommands/work_memory.py` 10 個子指令）

```bash
WM="python <UCL_Core>/Tools~/AgentCommands/work_memory.py"

# 1. 查詢主題清單
$WM topics                                              # 列出所有工作主題（active / archived）

# 2. 建立新主題
$WM init --topic <slug> --title <title> --desc <一段簡介>

# 3. 新增記憶片段 (Fragment)
$WM add  --topic <t> --type <decision|knowhow|pitfall|state|pointer> \
         --id <slug> --title <t> --body-file <f> [--docs d1,d2] [--links t/f] --by <persona>
#    ⚠ `state` 這一型 code 裡有，但 **Tim 2026-08-25 拍板「進度不進記憶」**
#      ⇒ 綁了 Task 的主題**不該再寫 state**（進度由 Task 時間線紀錄）。
#      這裡列出來是因為它存在；不列＝低報，列了不說限制＝誤導。

# 4. 讀取主題內容與共讀 Briefing
$WM read --topic <slug> --with-links                    # 生成共讀 briefing（含 1-hop 關聯與本地來源前 100 行）
$WM read --topic <t> --types decision,pitfall,pointer   # 讀取決策、踩坑與指路（最速接手）

# 5. 一步式更新/取代舊片段
$WM supersede --topic <t> --id <舊> [--by <新frag-id>]                 # 只標退場
$WM supersede --topic <t> --id <舊> --new-id <新slug> --new-title <t> \
              (--new-body <內容> | --new-body-file <f>) [--new-by <persona>]   # 一步式

# 6. 跨主題雙向關聯
$WM link --from <topic>/<frag> --to <topic>/<frag>

# 7. 重建/檢查索引
$WM index [--topic <slug>]                              # 重建索引（不給 --topic ＝全部）

# 8. Task ↔ 記憶雙向錨點管理（維護主題卡之 task_indices 反向索引）
$WM tasks --topic <slug>                                # 不帶動作＝只印現況（含每張單的 status／參與者）
$WM tasks --topic <slug> --add 17 --remove 3            # 追加／移除
$WM tasks --topic <slug> --set 5,8,15                   # 整組覆寫

# 9. 主題完工歸檔（前置檢查 Git 乾淨才放行）
$WM archive --topic <slug>                              # status→archived，archived_commit 取 HEAD
$WM archive --topic <slug> --commit <sha>               # 指定 sha（不給則取 HEAD）
$WM archive --topic <slug> --undo                       # 改回 active（守衛只保護退場方向）

# 10. 主題廢棄刪除（留下 tombstone 墓碑，防壞鏈）
$WM delete --topic <slug> [--by <persona>]              # 不帶 --confirm 是 **dry-run**，什麼都不刪
$WM delete --topic <slug> --by <persona> [--commit <sha>] --confirm
#    ⚠ `--confirm` 是旗標不接值；`--commit` 不給則取**擁有該內容那個工作區**的 HEAD
```

> [!WARNING]
> **`archive` / `delete` 的 git 前置守衛量的是「真的在版控裡」，不是「沒有待處理的變更」。**
> 判準：`ls-files` 回空 ⇒ 擋；磁碟上任一檔不在追蹤集合裡 ⇒ 擋（**ignore 與 untracked 分開講**，
> 因為處置不同：改 ignore 規則 vs `git add`）；全部追蹤到了才問 `status`。
>
> 🩸 兩隻血證（2026-08-25）：① `WorkMemory` 是**巢狀 submodule**，對父 repo 問 `status`
> 會回空字串 ⇒ 被讀成「乾淨」而放行；② 被 ignore 的路徑，`status` 一樣回空字串。
> **兩次都是空字串被讀成乾淨。**
> ⇒ `archived_commit` 也取**擁有這份內容的工作區**的 HEAD，不是父 repo 的
> （父層 pointer 長期未 bump ⇒ 那顆 sha 指到的版本裡 fragment 根本不在，而 sha 長得完全正常）。
>
> 📌 驗墓碑不是驗「有沒有寫一行」，是驗**「照那一行走得回內容嗎」**（拿 sha 去 `git show`）。

---

## 🔑 記憶的本體 = key → 知識點位置的映射（雙向錨點機制）

fragment 的核心價值是 `related_docs`/`links` 這組 **ref** — 把「這項工作的某個 key」映射到具體出處：

| ref 形式 | 例 | 指向與機制 |
|---|---|---|
| 檔案路徑 | `Docs/Plan/HSceneEditorRework/README.md` | 文件本體 |
| 檔案:行 | `Assets/Scripts/.../HAnimSetting.cs:476` | 具體 code 知識點 |
| **任務單 (Task)** | `task:TASK-0008` | **雙向錨點機制**：<br>① Task 側：`memory_topic`（單值字串）＋ `memory_archived_commit`<br>② 記憶側：主題卡 `_topic.md` 的 `task_indices`（由 `work_memory.py tasks` 管理）<br>③ `op=show <N>` 自動印出關聯主題之決策與指路摘要 |
| **酒館訊息** | `tavern:2026-07-29#9355` | 討論/拍板的原始出處 |
| commit | `commit:b33d2add` | 實作落點 |
| 工作記憶 | `<topic>/<fragment-id>`（放 links 欄） | 跨主題關聯記憶 |
| **個人 fragment → 工作記憶** | `workmem:<topic>[/<fragment-id>]` | 從「我是誰」跨到「這活做到哪」（晚安時掛） |

---

## 🔄 四個機械觸發點（掛在必經路徑，不塞早安）

1. **開工/回看 Task 時讀 (`Cmd_Task op=show <index>`)**：
   - 自動檢驗並印出 `memory_topic` 狀態（5 種狀態：主題在 / 全部已退場 / 已歸檔 / 已刪除 / 連結壞了）與 pointer 指路，開工接回上下文。
2. **結單時提示回寫 (`Cmd_Task op=resolve <index>`)**：
   - 結單成功時，系統印出警示提醒：「本單有沒有值得留的 decision / pitfall？」（提示不阻擋）。
3. **晚安雙向對帳 (`Cmd_GoodNight step=check`)**：
   - 自動檢查未關單 `updated_at` 逾期 14 天未動、或 Task ↔ 記憶單向斷鏈時印出警示（只印不改）。
4. **主 Task 全關後 PM 手動歸檔 (`work_memory.py archive`)**：
   - 所有子單結案後，PM 手動執行歸檔；前置機械檢查 Git 狀態確保已落盤。

---

## ⛔ 不要做

- ❌ 把進度與看板快照記在記憶裡（違反單一真相源 — 進度由 Task 時間線紀錄）
- ❌ 把 plan 文件內容整段轉貼進 fragment（違反分工核心 — 寫 key 指路）
- ❌ 個人身分/關係層的記憶放這裡（那是 `letters/<persona>/fragments` 的事 → skill `ucl-memory`）
- ❌ 非工作但對所有人都成立的通用經驗放這裡（例：陪看不要劇透）→ 那是**集體潛意識 Alaya**，同樣走 `ucl-memory`
