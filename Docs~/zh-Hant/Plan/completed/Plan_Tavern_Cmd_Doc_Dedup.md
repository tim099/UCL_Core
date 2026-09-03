---
title: Plan — Tavern 指令片段去重（剩餘 7 檔逐檔清理）
description: 把散落在 workflow / skill 文件裡的 Cmd_Tavern 指令片段收攏到 Cmd_Tavern.md，各處只留內容範本與該主題的紀律。本檔是**可直接接手的施工單**：含三分類判準、逐檔逐行清單、地雷、驗收條件。
last_updated: 2026-07-31
status: open
owner: 未指派（gura 2026-07-31 交接，Tim 找同事協助）
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | 收攏目標（唯一來源） | op 清單與欄位怎麼填
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Internals/Cmd_Tavern_Internals.md | 工程層分冊 | 儲存結構 / routing / 待修
---

# 🧹 Plan — Tavern 指令片段去重（剩餘 7 檔）

## 1. 這件事要解什麼

Tavern 的指令片段（`op=post --arg …`）散落在十幾份 workflow / skill 文件裡。**同一個機制寫在 N 個地方 = 更新一處其餘全部漂移，而漂移不會有人喊痛。**

2026-07-31 已為此付過兩次學費：
- `sender` 這個參數在五個地方有五種寫法（`sender` / `sender_id` / `agent_id` / `id` / `actor`），計酬 routing 因此把錢付進影子帳戶
- `Cmd_Tavern.md` 自己 `last_updated` 停在 2026-05-07，裡面還畫著 T38 早就廢除的 `messages.jsonl` 與「跨 process 序號競爭」

**已完成的部分**（2026-07-31，gura）：
- `Cmd_Tavern.md` 拆成使用層（op 與欄位）+ `Internals/` 工程層，並加 §6 反向索引（12 個引用處）
- 4 檔已去重：`Awakening_Ritual_Workflow` ×2 / `Ding_Protocol_Workflow` / `Tavern_Share_Policy` / `ucl-commit` SKILL
- 7 檔已加「本檔指令一律以 Cmd_Tavern.md 為準」警語（就是本單要清的那 7 檔）

**本單負責**：把那 7 檔的指令片段逐處清掉。

---

## 2. ⚠ 先讀這節：三分類判準（改錯的主要來源是分類錯，不是手滑）

> [!IMPORTANT]
> **原始 grep 計數會誤導。** `op=` 出現 99 次，但真正該改的只有 **31 處**；
> 另外 **68 處是散文提及 op 名，碰它們只會讓句子變囉嗦而毫無防漂移效益**。
> 動手前先按下表分類，**不要看到 `op=` 就改**。

| 類 | 特徵 | 怎麼處理 |
|---|---|---|
| **A. 純機制重複** | 完整指令（含 `run_cmd.py`）或帶多個 `--arg`，而該段落**不是靠這串指令在講別的事** | **刪指令**，改成「發送方式見 `Cmd_Tavern.md`」+ 留下 body / meta 範本與該主題的紀律 |
| **B. 教學 / 走查** | 指令的**順序或組合本身就是內容**（第一次跑通、solo 三輪對話、quest 狀態流轉示範） | **保留指令骨架但砍欄位細節**：非重點 arg 用 `...` 省略，只留該步驟的關鍵 arg；段首加一行指路 |
| **C. 散文提及** | 句子裡提到 op 名，沒有 `--arg`（例「Tim @我 → 酒館 `op=post` 回」「第一條 op 必為 `inbox_read`」） | **完全不動** |

### B 類的示範寫法

```diff
- senate ucmd run Tavern --arg op=post --arg room=design \
-   --arg agent=claude-da-xiaojie --arg body="..." --arg meta="tag:solo-brainstorm"
+ # 欄位一覽見 Cmd_Tavern.md；本節只示範三輪的順序與 meta 標記
+ senate ucmd run Tavern --arg op=post --arg room=design ... --arg meta="tag:solo-brainstorm"
```

判準一句話：**讀者需要「順序」時留骨架，需要「欄位」時指路。**

---

## 3. 逐檔逐行清單（2026-07-31 快照）

> 行號會隨編輯漂移 —— **每檔開工前先重跑 §5 的盤點指令**確認位置，不要盲信本表行號。

### 3.1 `Docs~/zh-Hant/Workflows/Tavern_SoloBrainstorm_Workflow.md` — A0 / B8 / C12

| 行 | 類 | 內容 | 建議 |
|---|---|---|---|
| L192, 194, 207, 216, 221, 230, 235 | B | 三輪 self↔alter 的完整往返示範（post / wait 交錯） | **B 類處理**：留順序骨架與 `meta:tag:solo-brainstorm`、`timeout=30`，砍其餘欄位 |
| L202 | B | `op=wait ... timeout=30` | 同上 |
| L122, 143, 158, 170, 173 | C | 偽指令行（無 `--arg`，在講流程） | 不動 |
| 其餘 C | C | 散文 | 不動 |

⚠ 這份的 `sender=<本人 id>` / `sender=<本人 id>-alter` 是 **alter 身分慣例**（本檔的本體），改指路時**不要把 alter 命名規則一起刪掉**。

### 3.2 `Skills~/ucl-chat-tavern/reference/re-entry.md` — A2 / B2 / C14

| 行 | 類 | 內容 | 建議 |
|---|---|---|---|
| L81 | A | `run_cmd.py … op=post …` 預設 540 的說明句 | 改敘述，把「怎麼寫」指回 Cmd_Tavern §3 |
| L122, 125 | A/B | Bash timeout 600000 的注意事項 + 範例 | **保留**（這是 caller 端環境限制，不是 Tavern 欄位）但可精簡 |
| L39 | B | `op=session_enter --arg agent=…` 的參數清單 | B 類：留 macro 語意與四個選填的**用途說明**，欄位表指路 |
| L24, 28-31, 54, 75, 79… | C | 三步流程與散文 | 不動 |

### 3.3 `Skills~/ucl-chat-tavern/SKILL.md` — A3 / B1 / C9

| 行 | 類 | 內容 | 建議 |
|---|---|---|---|
| L64, 69, 72 | A | P0 鐵律的「唯一合法 post 路徑」三種 shell 寫法 | **特殊case**：保留**一個** Bash 範例（P0 鐵律的牙齒就是這串），標「mirror of Cmd_Tavern §1，改動以那份為準」，另兩種 shell 指路 |
| L137 | B | `op=wait --arg since_seq=…` | B 類，留 `timeout=480` 語意 |
| 其餘 C | C | 散文 | 不動 |

> skill 是 agent runtime 的入口，**過度指路會增加 round-trip 成本**。這份的取捨偏「留最小可執行範例」。

### 3.4 `Docs~/zh-Hant/CommandTable.md` — A0 / B1 / C12

| 行 | 類 | 建議 |
|---|---|---|
| L214 | B | `op=post --arg room=hideout` → 留 room 語意，欄位指路 |
| 其餘 12 處 | C | **全部不動**（這份是「口語指令 → op」對照表，提 op 名是它的職責） |

### 3.5 `Docs~/zh-Hant/Workflows/ChatTavern_Workflow.md` — A6 / B0 / C11

| 行 | 類 | 內容 | 建議 |
|---|---|---|---|
| L125, 126, 131, 139, 149, 150 | A | 「第一次跑通」walkthrough（createroom → join → post → read） | **B 類處理**：walkthrough 沒指令就沒用，留骨架砍欄位；段首指路 |
| L165-172 | C | 時序示意（`A: op=post body="算式對嗎？"`） | 不動 |

### 3.6 `Skills~/ucl-chat-tavern/reference/wait-and-standby.md` — A0 / B1 / C3

| 行 | 類 | 建議 |
|---|---|---|
| L152 | B | 待機自言自語的 post 範例 → 留 `meta:tag:idle-self-talk` 與節奏說明，欄位指路 |
| 其餘 3 處 | C | 不動 |

### 3.7 `Docs~/zh-Hant/Workflows/Quest_Workflow.md` — A7 / B0 / C7

| 行 | 類 | 內容 | 建議 |
|---|---|---|---|
| L176, 243, 454, 458, 462, 546, 558 | A | `task_next` / `task_state` / `task_list` / `task_force_reclaim` / `task_claim` / `task_done` 範例 | **B 類處理**：quest 狀態機是本檔本體，留「哪個狀態走哪個 op」，欄位（`reason` / `summary` 等必填）指回 Cmd_Tavern §2.6 |

⚠ task 系列的 canonical 是 `actor` / `claimer`（**不是** `agent`），別在指路時把它們一起改名 —— 那是刻意保留的語意。

---

## 4. 地雷（每一條都有人踩過）

1. **quoted heredoc 裡的 `\\`**：`<<'PYEOF'` 內 `\\` 是字面兩字元，用它比對檔案內容會 0 命中。
   gura 2026-07-31 連踩兩次 → **改用行號定位**（`lines[a:b] = new`）才是確定性做法。
2. **已裝副本有三份**：改 `Skills~/` 之後必須跑
   `python <UCL_Core>/Tools~/install_skills.py --include <skill> --target claude|codex|antigravity`。
   **不要手動 cp** —— `.agents` 那份有工具依「觸發詞」行自動生成的 `trigger: on_intent`，手抄會幹掉它。
3. **`diff -q` 對 `.agents` 會誤報不一致**：那是 CRLF 行尾 + `trigger:` 行。
   驗法：`diff --strip-trailing-cr <(grep -v '^trigger:' A) <(grep -v '^trigger:' B)`。
4. **`id` 是超載名**：`createroom` / `create_trpg_room` 的 `id` 是**房間 / campaign id**，不是 agent。
   看到 `--arg id=` 先確認語意再動。
5. **不要順手改 library / rule 工具的 `<bank-id>`**：`library.py` 的 `--tipper` / `--donor` 真的收 bank
   （它另有 `tipper_persona` 分開帶），改成 `agent-id` 會變成錯的文件。
6. **`--wait-reply` 是 script flag 不是 cmd arg**，別寫進 `--arg` 清單當欄位。

---

## 5. 施工流程（逐檔，不要批次盲改）

```bash
# ① 開工前重新盤點該檔（行號會漂）
python - <<'PY'
import re
f = "<檔案路徑>"
for i, l in enumerate(open(f, encoding="utf-8"), 1):
    if re.search(r'op=\w+', l):
        kind = "A" if "run_cmd.py" in l else ("B" if "--arg" in l else "C")
        print(f"L{i:<4}[{kind}] {l.strip()[:80]}")
PY

# ② 按 §2 判準改（A 刪指令留範本 / B 留骨架砍欄位 / C 不動）

# ③ 改完 Skills~ 要同步三 target
for t in claude codex antigravity; do
  python <UCL_Core>/Tools~/install_skills.py --include <skill> --target $t
done

# ④ 驗連結沒斷（斷鏈的指路牌比沒有更糟）
python - <<'PY'
import re, os
base = "Assets/Plugins/UCL_Core"
bad = []
for root, _, files in os.walk(base):
    if "Skills~" not in root and "Docs~/zh-Hant" not in root.replace("\\","/"): continue
    for f in files:
        if not f.endswith(".md"): continue
        p = os.path.join(root, f)
        for m in re.finditer(r'\]\((\.\.?/[^)]*Cmd_Tavern[^)]*\.md)\)', open(p, encoding="utf-8").read()):
            if not os.path.exists(os.path.normpath(os.path.join(root, m.group(1)))):
                bad.append((p, m.group(1)))
print("斷鏈:", bad or "無")
PY
```

---

## 6. 驗收條件（Definition of Done）

1. 七檔的 **A 類全部處理完**（刪指令或轉 B 類骨架），**B 類欄位細節已砍**，**C 類一處未動**
2. 每檔仍能獨立讀懂**它自己負責的那件事**（alter 命名慣例 / 三步入場 / quest 狀態機 / P0 鐵律…）
   —— 去重不該讓文件失去自己的主題
3. Cmd_Tavern 相關相對連結**零斷鏈**（§5 ④）
4. `Skills~` 改動已同步三 target，且 `.agents` 的 `trigger: on_intent` 行仍存在
5. `Cmd_Tavern.md` §6 反向索引若有新增/移除引用處 → 一併更新
6. 走 `ucl-commit`：一筆 commit + 發酒館公告領薪（別漏第 5 步）

## 7. 不要做

- ❌ 用 regex 批次替換整批檔案 —— C 類會被誤傷，而 C 類佔 68/99
- ❌ 把 B 類（教學走查）的指令整段刪掉 —— walkthrough 沒指令就廢了
- ❌ 為了「一致」把 task 系列的 `actor` / `claimer` 改成 `agent`
- ❌ 一次改完七檔才驗證 —— 逐檔改、逐檔驗，壞了才知道是哪一刀

## 8. 交接說明

本單由 gura（Myth，2026-07-31）在完成 `Cmd_Tavern.md` 拆分後開出。**沒有一起做完的理由**：
這 31 處機械改寫需要逐檔判斷分類，而當時的 session 已經很長，批次盲改的風險高於價值。
Tim 拍板「文件化交給同事」。

接手時**不需要**先讀那天的酒館 thread —— 本單自 contained。有疑義的地方（尤其 §3.3 skill 的取捨）
可在酒館 @gura 或直接自決，只要 §6 驗收條件過得了就算完成。
