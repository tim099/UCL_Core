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
last_updated: "2026-08-24 v1.3 (TASK-0016: 記憶是鷹架不是資產、歸檔不等於失聯、Task ↔ 記憶雙向錨點對齊)"
---

# UCL Work Memory — 工作記憶區

> 🎯 **核心金句**：
> **「記憶回答『為什麼』與『怎麼踩過』，Task 回答『到哪了』，文件回答『怎麼用』。三者重疊的那部分不是備援，是漂移。」**
> **「記憶是工作期間的鷹架不是永久資產，相關 Task 全完成後歸檔或刪除，紀錄留 git。」**
> **「歸檔不等於失聯：archived 主題仍有 commit 歷史錨點，絕不靜默消失。」**

---

## 🛠 CLI（`<UCL_Core>/Tools~/AgentCommands/work_memory.py`）

```bash
WM="python <UCL_Core>/Tools~/AgentCommands/work_memory.py"
$WM topics                                              # 有哪些工作主題（active / archived）
$WM read --topic <slug> --with-links                    # 生成共讀 briefing（含 1-hop 關聯與本地來源前 100 行）
$WM read --topic <t> --types decision,pitfall,pointer   # 讀取決策、踩坑與指路（最速接手）
$WM init --topic <slug> --title <title> --desc <一段簡介>
$WM add  --topic <t> --type <decision|knowhow|pitfall|pointer> \
         --id <slug> --title <t> --body-file <f> [--docs d1,d2] [--links t/f] --by <persona>
$WM supersede --topic <t> --id <舊> --new-id <新slug> --new-title <t> --new-body <內容>  # 一步式更新
$WM link --from <topic>/<frag> --to <topic>/<frag>                 # 跨主題雙向關聯
$WM archive --topic <slug> --commit <sha>                           # 任務全關後 PM 歸檔（前置檢查 git 乾淨）
```

---

## 🔑 記憶的本體 = key → 知識點位置的映射

fragment 的核心價值是 `related_docs`/`links` 這組 **ref** — 把「這項工作的某個 key」映射到具體出處：

| ref 形式 | 例 | 指向 |
|---|---|---|
| 檔案路徑 | `Docs/Plan/HSceneEditorRework/README.md` | 文件本體 |
| 檔案:行 | `Assets/Scripts/.../HAnimSetting.cs:476` | 具體 code 知識點 |
| **任務單** | `task:TASK-0008` | 所屬/關聯的跨 Agent 任務（Task ↔ 記憶雙向錨點） |
| **酒館訊息** | `tavern:2026-07-29#9355` | 討論/拍板的原始出處 |
| commit | `commit:b33d2add` | 實作落點 |
| 工作記憶 | `<topic>/<fragment-id>`（放 links 欄） | 關聯記憶 |
| **個人 fragment → 工作記憶** | `workmem:<topic>[/<fragment-id>]` | 從「我是誰」跨到「這活做到哪」（晚安時掛） |

---

## 🔄 四個機械觸發點（掛在必經路徑，不塞早安）

1. **開工/回看 Task 時讀 (`Cmd_Task op=show <index>`)**：
   - 自動檢驗並印出 `memory_topic` 摘要與 pointer 指路，開工接回上下文。
2. **結單時提示回寫 (`Cmd_Task op=resolve <index>`)**：
   - 結單成功時，系統印出警示提醒：「本單有沒有值得留的 decision / pitfall？」（提示不阻擋）。
3. **晚安雙向對帳 (`Cmd_GoodNight step=check`)**：
   - 自動檢查未關單 `updated_at` 逾期 14 天未動或單向斷鏈（只印不改）。
4. **主 Task 全關後 PM 手動歸檔 (`work_memory.py archive`)**：
   - 所有子單結案後，PM 手動執行歸檔；前置機械檢查 Git 狀態確保已落盤。

---

## ⛔ 不要做

- ❌ 把進度與看板快照記在記憶裡（違反單一真相源 — 進度由 Task 時間線紀錄）
- ❌ 把 plan 文件內容整段轉貼進 fragment（違反分工核心 — 寫 key 指路）
- ❌ 個人身分/關係層的記憶放這裡（那是 `letters/<persona>/fragments` 的事 → skill `ucl-memory`）
- ❌ 非工作但對所有人都成立的通用經驗放這裡（例：陪看不要劇透）→ 那是**集體潛意識 Alaya**，同樣走 `ucl-memory`
