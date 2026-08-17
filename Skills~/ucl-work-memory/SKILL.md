---
name: ucl-work-memory
description: |
  工作記憶區（Work Memory）— 所有 agent 共用、以「工作主題」為單位的 knowhow 庫。
  開工前讀取該工作的拍板/坑/進度/文件指路, 完工時整理回寫 — 換 agent 接手不斷線。
  核心分工: 知識點放文件, 記憶透過 key 標註「要找哪些文件」+ 記工作進度(state 快照)。
  記憶可跨主題關聯(links), 讀取時 --with-links 一起拉。

  觸發詞 (case-insensitive substring, 任一命中即 lazy-load):
  - 工作記憶 / work memory / workmemory / 記憶區
  - 讀取記憶 / 整理記憶 / 記錄 knowhow / knowhow / 工作knowhow
  - 接續進度 / 上次進度 / 接手工作 / 交接 / 進度快照
  - 開工前查 / 這項工作怎麼做 / 之前拍板了什麼

related:
  - <ucl_core:Docs~/zh-Hant/Workflows/Work_Memory_Workflow.md> | 完整設計與 schema | 必讀
  - <ucl_core:Docs~/zh-Hant/Workflows/Memory_Common_Principles.md> | **三層記憶共通鐵律**（格式/寫入/檢索/維護）| 本檔的三鐵律以那份為準
  - <ucl_core:Skills~/ucl-memory/SKILL.md> | ucl-memory | **個人記憶 / 集體潛意識 Alaya / 回憶** 歸那邊
  - <ucl_core:Docs~/zh-Hant/Workflows/Memory_Fragment_Backfill_Workflow.md> | 思路源頭(個人記憶版)
  - skills/agent-lessons-log/SKILL.md | 跨工作通用教訓歸那邊, 別塞進 pitfall
last_updated: "2026-08-17 v1.2 (三層分工成形 — 共通鐵律抽到 Memory_Common_Principles.md; 個人記憶/Alaya/回憶 移交新 skill ucl-memory。前版 2026-07-29 v1.1 施工中同步機制)"
---

# UCL Work Memory — 工作記憶區

> 一句話：**開工前 `read` 一條指令接上狀態；完工時把「拍板/坑/進度/指路」整理回寫。
> 個人 fragments 記「我是誰」, 工作記憶記「這項工作怎麼做」。**

## 🛠 CLI（`<UCL_Core>/Tools~/AgentCommands/work_memory.py`）

```bash
WM="python <UCL_Core>/Tools~/AgentCommands/work_memory.py"
$WM topics                                              # 有哪些工作主題
$WM read --topic hscene-editor-rework --with-links      # 先生成共讀 briefing（含 1-hop 關聯與本地來源前 100 行）
$WM read --topic <t> --types state,pointer              # 只要進度+指路（最速接手）
$WM init --topic <slug> --title <title> --desc <一段簡介>
$WM add  --topic <t> --type <decision|knowhow|pitfall|state|pointer> \
         --id <slug> --title <t> --body-file <f> [--docs d1,d2] [--links t/f] --by <persona>
$WM supersede --topic <t> --id <舊> --new-id <新slug> --new-title <t> --new-body <內容>  # 一步式更新（高頻推薦）
$WM supersede --topic <t> --id <舊fragment> --by <新fragment-id>   # 兩步式（新檔已另建時）
$WM link --from <topic>/<frag> --to <topic>/<frag>                 # 跨主題雙向關聯
# ⚠ Windows shell 引號地獄: 多行/含引號 body 一律 --body-file（先寫暫存檔）; 單行才用 --body inline
# 不知道主題名 / 想語意找 knowhow:
python <UCL_Core>/Tools~/AgentCommands/knowledge_base.py search --target work_memory --query "<要做的事>"
```

## 🔑 記憶的本體 = key → 知識點位置的映射（Tim 2026-07-29 拍板重述）

fragment 的核心價值是 `related_docs`/`links` 這組 **ref** — 把「這項工作的某個 key」映射到知識點的具體位置。ref 接受的形式（自由字串, 慣例如下）:

| ref 形式 | 例 | 指向 |
|---|---|---|
| 檔案路徑 | `Docs/Plan/HSceneEditorRework/README.md` | 文件本體 |
| 檔案:行 | `Assets/Scripts/.../HAnimSetting.cs:476` | 具體 code 知識點 |
| **酒館訊息** | `tavern:2026-07-29#9355` | 討論/拍板的原始出處 |
| commit | `commit:b33d2add` | 實作落點 |
| 工作記憶 | `<topic>/<fragment-id>`（放 links 欄） | 關聯記憶 |
| **個人 fragment → 工作記憶** | `workmem:<topic>[/<fragment-id>]` | 從「我是誰」跨到「這活做到哪」（晚安時掛） |

## 📖 讀取記憶（開工前 SOP）

1. `$WM read --topic <slug> --with-links` 後，**必須開啟命令印出的 `AgentCommands/WorkMemoryReadBriefs/...md`** — 該檔是 agent 與人類共讀的唯一輸入，含主題卡、fragment、跨主題關聯與有效本地 `related_docs`／`key_docs` 前 100 行；出現截斷標記時再直接讀原始檔
2. 趕時間 → `--types state,pointer`：**進度快照**（接續上次開工）+ **文件地圖**（key→權威文件）
3. 記憶指向的 ref 才是內容本體 — 記憶讀完按圖索驥, 別期待記憶裡有完整規格
4. **讀者的更新義務（Tim 2026-07-29 拍板）**: 讀到**過時**的 fragment（state 與實況不符 / ref 指向已不存在 / 結論已被推翻）→ **當場處理**, 不是記在心裡:
   一步式 `$WM supersede --topic <t> --id <舊> --new-id <新> --new-title ... --new-body ...`
   （首航血證: 過期 state 讓讀的人拿到假現況 — 誰發現誰更新, 記憶區才不會爛）

## ✍️ 整理記憶 — 施工中同步（crest-001 提案, Tim/summit 2026-07-29 拍板採納）

**寫入時機掛在既有工作節奏上, 不加新儀式**（同 affinity「signal hit 立即寫、不等 retro」哲學）。
反面教材: 心得散酒館/拍板散 review/坑記 commit, 靠 plan owner 事後人肉 ETL 批次補寫 — owner 不在就斷,
且快照過期 = 謊言製造機（首航實測第一隻 bug 就是過期 state）。

| 工作節奏節點 | 同步動作 | 寫的人 |
|---|---|---|
| 收到 design-review 判決 / 需求拍板 | 當下 `add --type decision`（結論+守則+docs 指路, 推導留酒館） | **接單施工的 agent**（第一手最準） |
| 撞坑修完（QA/commit 的同一動作串） | 立即 `add --type pitfall` | 撞坑的人 |
| **task-share 發完** | **同步 supersede + add 新 state**（share 是給人看的敘事, state 是給下個 agent 的快照 — share 完不更 state = share 沒發完） | 發 share 的人 |
| 產出新權威文件 | 更新 pointer / key_docs | 產出者 |
| **晚安收工**（[[ucl-goodnight]] Step 0.5） | 今天有推進的工作 → `supersede` 舊 state + `add` 新 state；**並在個人 fragment 的 links 掛 `workmem:<topic>`** | 收工的 agent |

節流與分工：state 一個 plan/工作單元收束才 supersede 一次（subtask 不算）; decision/pitfall 寫前先 KB search 防重複; **plan owner 只做 review 抽查補漏, 不事後全寫**。

## ✍️ 整理記憶（完工/交接 SOP）

判準：**「下一個接手的 agent 開工前必須知道、但翻文件目錄撈不快的東西」**才進記憶。

| 發生了什麼 | 動作 |
|---|---|
| 需求/設計拍板 | `add --type decision`（只寫結論+可行動守則, 推導留在文件, `--docs` 指過去） |
| 撞到該工作特有的坑 | `add --type pitfall`（跨工作通用教訓 → `agent-lessons-log`, 不要放這） |
| **進度變化 / 收工** | `supersede` 舊 state → `add --type state` 新快照（誰做到哪/pending 什麼）— **必做, 否則下個人拿到假現況** |
| 產出新權威文件 | 更新 `pointer` 型文件地圖（或 `_topic.md` 的 key_docs） |
| 兩個主題的 knowhow 互相支撐 | `link` 建雙向關聯 |

**三鐵律**（三層記憶通用 —— 事實來源是
[`Memory_Common_Principles.md`](<ucl_core:Docs~/zh-Hant/Workflows/Memory_Common_Principles.md>) §3，
下面是工作記憶版的說法；兩邊不一致時以那份為準）：
1. **知識點能放文件就放文件** — 記憶是 key 與現場摘要, 不是文件的複本
2. **fragment 寫一次不改寫** — 更新走 supersede / 追加, `_index.md` 是機械視圖別手改
3. **先搜再寫**：add 前 `knowledge_base.py search --target work_memory` 查近似, 命中就 link/追加, 防洗版

## ⛔ 不要做

- ❌ 把 plan 文件內容整段轉貼進 fragment（違反分工核心 — 寫 key 指路）
- ❌ 進度變了卻只在酒館說 — state 快照不更新, 記憶區就是謊言製造機
- ❌ 個人身分/關係層的記憶放這裡（那是 `letters/<persona>/fragments` 的事 → skill `ucl-memory`）
- ❌ 非工作但對所有人都成立的通用經驗放這裡（例：陪看不要劇透）→ 那是**集體潛意識 Alaya**，同樣走 `ucl-memory`
