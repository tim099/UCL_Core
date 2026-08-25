---
title: 跨 Agent 結構化專案與任務管理系統 — Cmd_Task ＋ 後台看板 ＋ ucl-task Skill
slug: task-management-system
status: approved（2026-08-24 Tim 拍板 —— RFC 見酒館 seq 13303 / 評審 13306 / 收斂 13307 / 邊界 13308 / 拍板對齊 13310 / P0 驗收 13312 / P1 閉環 13314 / P2 對帳 13522 / QA 對帳 13527 / 記憶整合 13566, 13585, 13592；本文為實作 plan）
created_at: 2026-08-24T03:00:00Z
created_by: gura
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_BugReport_System.md | Plan_BugReport_System | 一單一檔與 index 配發母版
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | `Fixes TASK-x` / `Refs TASK-x` 自動閉環掛點
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md | 早安/晚安 Cmd 流程 | 見叢連動與晚安對帳掛點
  - ucl_core:Skills~/ucl-work-memory/SKILL.md | Work Memory Skill | 工作記憶操作指南
---

# 跨 Agent 結構化專案與任務管理系統 — 實作 Plan

> **分工（酒館 seq 13308, 13310, 13316, 13527, 13576, 13585 / Tim 拍定）**
> - **@summit**：`UCL_TaskModels.cs` ＋ `UCL_TaskIO.cs` ＋ `Cmd_Task.cs` ＋ `UCL_TaskManagerPage.cs` 後台看板 ＋ `git_commit.py` 閉環實作 ＋ 晚安對帳、sweep 實作 ＋ `memory_topic` C# 讀取端（TASK-0015）
> - **@gura**：本 Plan RFC ＋ `Task_Management_Workflow.md` 系統維護文件 ＋ `ucl-task` Skill 定義與鏡像同步（TASK-0005, TASK-0016）
> - **@basecamp**：PM 統籌 ＋ QA 審查與驗收簽核 ＋ `work_memory.py` archive/git 守衛實作（TASK-0017）

## 0. 一句話

**Task 是跨人協作的交付承諾，見叢是個人每日的自律對帳，工作記憶是施工脈絡的跨日鷹架。**
本系統借鑑 GitLab Issues、YouTrack、Linear 與 BugReport 的精確治理哲學，
將任務生命週期 **100% 錨定在「Commit Trailer 自動閉環 ＋ 晚安見叢雙向對帳」的既有鋼鐵動線上**，早安流程維持零改動，根絕無人維護而死於沉默的歷史陷阱。

---

## 1. 核心問題：為什麼舊的 `AgentTasks` 會死？

2026-05 曾存在過 `AgentTasks`（`proposals.jsonl`），在停用 3.5 個月後無人察覺。

| 失敗模式 | 舊 AgentTasks 的死因 | 本次 UCL_Task 的防護機制 |
|---|---|---|
| **缺乏自然推進路徑** | 狀態推進需要 Agent「專程回來跑指令」，但 Agent 每日 context 重置，無人會專程回來。 | **狀態推進掛在 Commit 上**（`Fixes TASK-N` 自動結單）、**指派由晚安對帳防漏**。 |
| **見叢與任務邊界模糊** | 跨人待辦全塞在個人見叢 `_keys_open.md`，累積數十條後只看不清。 | **分流金句**：「記憶回答『為什麼』與『怎麼踩過』，Task 回答『到哪了』，文件回答『怎麼用』。」 |
| **責任虛浮（單一 Assignee）** | 只有單一 assignee，誰來 QA、誰來審查、誰來企劃完全未知。 | **多參與者與身分矩陣**（`participants` 含 7 種角色：PM, Design, Dev, QA, Reviewer, Sound, Art）。 |
| **Git 衝突磁鐵** | 採共用單一 jsonl 檔案，多人同時動作產生 merge conflict。 | **一單一檔 (One File Per Task)**：`tasks/<index>.md`，Markdown + YAML Frontmatter，獨立 repo `tim099/Tasks`。 |

---

## 2. 核心邊界與判準（四道鋼鐵守衛）

### ① 三格分流判準 (Tri-Split Decision)
當在晚安、工作日誌或開工記錄中寫待辦或 know-how 時，依三格分流：
1. **Task（任務承諾）**：**「有沒有第二個人在等這件事？」** ➔ 走 `Cmd_Task create` 建立 `TASK-n`，個人見叢只保留引用：`- [ ] [TASK-0042] 說明`。
2. **工作記憶（Work Memory）**：**「我明天若忘了，接手的人靠什麼接回來？」** ➔ 換人接手需要知道的決策背景、踩坑與 pointer 指路，寫入工作記憶主題（`work_memory.py`）。進度由 Task 時間線紀錄。
3. **見叢（個人自律）**：**「這是不是純個人自省？」** ➔ 只有我自己需要被打臉的拖延或自律血證，留在個人見叢 `_keys_open.md`。

### ② 早安流程零改動守衛（Tim 2026-08-24 拍板）
- **早安 Brief 流程一行不改**：早安不新增任何額外節、不與營運層搶額度。
- Task 資訊透過個人見叢既有的 `- [ ] [TASK-0042] …` 引用行天然出現在早安 Brief §2（見叢）中。

### ③ 晚安雙向對帳守衛（只印差異，不自動推狀態）
- 晚安時 `Cmd_GoodNight step=check`（`UCL_TaskReconcile`）進行四類對帳（只印不改）：
  1. **見叢引用了已關或不存在的單**：提示劃掉引用。
  2. **指派給我（Dev/QA）但見叢未引用**：提示補寫一行。
  3. **逾期認領未動（≥14 天）**：提示認領已過期，引導執行 `op=sweep` 釋放回 todo。
  4. **記憶錨點異常**：未關單 `updated_at` 超過 14 天未動或單向斷鏈。

### ④ 結單機械閘與 Acceptance Criteria 閉環
- 開單人必須填寫 **Acceptance Criteria（驗收標準）**；QA 角色成員有權以「此條件無法客觀量測」退回。
- **三道結單機械閘**：
  1. `confirm=1`：強制防手滑宣告。
  2. `OpenBlockers` 閘：若 `blocked_by` 清單中有任何未解任務，強制阻擋 `resolve`。
  3. `QA` 閘：若參與者清單中有 `role=qa` 成員，且結單操作者非該 QA 人員，必須顯式帶 `--arg qa_note="<覆核簽核說明>"` 方能放行。
- **防偷推守衛**：`op=update` **嚴格禁止**直接將狀態推至 `done` 或 `cancelled`，所有結單必須走 `resolve`。
- **QA 驗收退回返工守衛（Tim 2026-08-25 拍板）**：任務在驗收過程中若發現不符標準或瑕疵，**一律走退回返工（`op=update --arg status=in_progress`）並在該 Task 留言提供量測讀數與重現步驟，嚴禁另開 Bug 單！**（BugReport 系統是針對已結案/已發布之線上缺陷）。

---

## 3. 資料模型 (`UCL_TaskModels.cs`)

### 3.1 任務實體模型 (`UCL_TaskEntry`)

```csharp
public class UCL_TaskEntry
{
    public int index = 0;
    public string id = "";                         // 如 "TASK-0001"
    public string type = "feature";
    public string priority = "normal";
    public string status = "todo";
    public string title = "";
    public string description = "";
    public string acceptance_criteria = "";        // 驗收標準（QA 驗證依據）
    
    public string reporter = "";                   // 開單人 persona
    public List<UCL_TaskParticipant> participants = new(); // 多參與者身分矩陣（persona 唯一）
    
    public string milestone = "";                  // 所屬里程碑（已支援篩選與更新）
    public string epic_id = "";                    // 所屬 Epic ID（已支援 op=list --arg epic=）
    public List<int> subtask_indices = new();      // 子任務 Index 清單（已支援 op=link subtask_of）
    public List<int> blocked_by = new();           // 被哪些任務阻塞
    public List<int> blocks = new();               // 阻塞了哪些任務（雙向聯動）
    public List<int> related_to = new();           // 關聯任務
    
    public string memory_topic = "";               // 工作記憶錨點（單值字串，TASK-0015）
    public string memory_archived_commit = "";     // 歸檔時的 commit SHA
    
    public List<string> tags = new();              // 標籤（已支援 op=list --arg tag=）
    public List<string> commit_shas = new();       // 關聯之 Commit SHA
    
    public string created_at = "";                 // UTC ISO8601
    public string updated_at = "";                 // UTC ISO8601 (用於 Stale 判斷)
    public string closed_at = "";                  // 結單時間
    public string resolution_note = "";            // 結單說明 / 成果報告
    
    public bool IsClosed() => status == "done" || status == "cancelled";
}
```

---

## 4. 磁碟結構與現況功能 (`UCL_TaskIO.cs`)

```text
AgentCommands/Tasks/ (Submodule -> github.com/tim099/Tasks)
  ├── _index.txt                  # 當前配發之最大 Index（單調遞增，初始 0）
  └── tasks/                      # 任務實體 (一單一檔)
      ├── 0001.md
      ├── 0002.md
      └── ...
```

> [!NOTE]
> **現況功能完整度（實跑讀數對照）**：
> - ✅ **`milestone` 里程碑**：`create`、`update`、`list --arg milestone=` 全面生效。
> - ✅ **`op=sweep` 逾期認領釋放**：已上線（逾期自動退回 `todo` 釋放認領）。
> - ✅ **`tags` 標籤過濾**：`op=list --arg tag=` 已生效。
> - ✅ **`epic_id` 與 `subtask_indices`**：`op=link subtask_of|has_subtask` 與 `op=list --arg epic=` 已全面生效。
> - ✅ **`op=update` 6 大欄位**：`status`, `priority`, `title`, `milestone`, `memory_topic`, `memory_archived_commit` 均已實跑驗收。
> - ✅ **`memory_topic` 記憶錨點**：`op=show` 讀取端、五種狀態呈現（主題在 / 全部退場 / 已歸檔 / 已刪除 / 連結壞了）、晚安對帳已全面生效。
> - ✅ **`work_memory.py archive`**：已由 TASK-0017 上線交付（支援 submodule Git 乾淨前置檢查；墓碑寫入端待進一步驗收）。

---

## 5. 操作指令 (`Cmd_Task.cs` 13 個 OP)

| 操作 `op` | 說明 | 關鍵參數 |
|---|---|---|
| `create` | 開立新任務 | `--arg title= --arg type= --arg priority= [--arg milestone=] [--arg memory_topic=] [--arg tags=] [--arg criteria=]` |
| `list` | 查詢清單 | `[--arg status=] [--arg assignee=] [--arg milestone=] [--arg tag=] [--arg epic=]` |
| `show` | 查閱單檔詳情與記憶接回 | `--arg index=<N>` |
| `claim` | 認領任務（智能角色語意） | `--arg index=<N> [--arg role=dev]` |
| `update` | 屬性更新（吃 6 欄位；⛔ 嚴禁直接推 done/cancelled） | `--arg index=<N> [--arg status=] [--arg priority=] [--arg title=] [--arg milestone=] [--arg memory_topic=] [--arg memory_archived_commit=]` |
| `assign` | 追加/修改參與者（不改狀態） | `--arg index=<N> --arg target_persona= --arg role=` |
| `unassign` | 移除參與者 | `--arg index=<N> --arg target_persona=` |
| `comment` | 追加討論/進度（同步發酒館） | `--arg index=<N> --arg body=<筆記內容>` |
| `link` | 建立依賴或階層關聯（雙向聯動） | `--arg index=<A> --arg op_link=blocks|blocked_by|subtask_of|has_subtask|related --arg target=<B>` |
| `resolve` | 結單關閉（受 3 道閘檢驗，提示回寫記憶） | `--arg index=<N> [--arg status=done|cancelled] --arg note= --arg confirm=1 [--arg qa_note=]` |
| `sweep` | 逾期認領自動釋放回 todo | `[--arg days=14] --arg confirm=1` |
| `commit` | Commit 推進閉環（內部/腳本呼叫） | `--arg sha=<sha> --arg mode=fixes|refs` |
| `kanban` | 看板輸出 | 終端機格式化輸出各狀態欄位 |
