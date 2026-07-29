---
title: 工作記憶區 Workflow（Work Memory）
status: active
created_at: 2026-07-29
created_by: zeta:summit
audience: 所有 agent（跨 Claude / Antigravity / Gemini / Zeta / Luna…, 共用同一庫）
related:
  - 思路源頭: <ucl_core:Docs~/zh-Hant/Workflows/Memory_Fragment_Backfill_Workflow.md>（fragment 哲學）
  - 工具: <UCL_Core>/Tools~/AgentCommands/work_memory.py（topics/init/add/read/link/supersede/index）
  - 檢索: <UCL_Core>/Tools~/AgentCommands/knowledge_base.py --target work_memory（kb_targets.json）
  - Skill: ucl-work-memory（讀取/整理入口）
last_updated: 2026-07-29 v1.2 (Tim 補充: ref 泛化到酒館訊息/commit、key mapping 為本體、過時即更新義務; crest-001 卡手點三修)
---

# 🧰 工作記憶區 Workflow

> 一句話：**個人 fragments 記「我是誰」，工作記憶記「這項工作怎麼做」— 所有 agent 共用，
> 以工作主題為單位，開工前 5 分鐘接上狀態，換人接手不斷線。**

## 🟢 白話：解什麼問題

某項工作（如「編輯器重構」）的 knowhow 散落在：plan 文件、酒館討論、commit message、
某個 agent 的個人記憶裡。換一個 agent（或同一個 agent 換一個 session）接手時，
要嘛重新考古半小時，要嘛靠指派人手寫摘要。工作記憶區把這件事變成一條指令：

```bash
python <UCL_Core>/Tools~/AgentCommands/work_memory.py read --topic hscene-editor-rework --with-links
```

## 📐 文件 vs 工作記憶的分工（Tim 2026-07-29 拍板, 最重要的一節）

| | 文件（Docs/） | 工作記憶（WorkMemory/） |
|---|---|---|
| 角色 | **內容本體** — 完整規格/施工圖/欄位表/推導 | **索引與現場摘要** — 開工前 5 分鐘層 |
| 更新 | 持續維護、就地修訂 | fragment 寫一次不改寫（更新走 supersede/追加） |
| 判準 | 「要理解這系統」讀的 | 「要開工」讀的 |

**核心原則（Tim 補充拍板）**：
1. **記憶的本體 = key → 知識點位置的映射**。知識點能放文件就放文件；
   fragment 的 `related_docs`/`links` 就是這組 ref — 可指向**文件路徑 / 檔案:行 /
   酒館訊息（`tavern:<date>#<seq>`）/ commit（`commit:<sha>`）/ 其他工作記憶**。
   記憶內文只放「結論/守則/現況」這種撈不快的東西，不轉貼知識點內容。
0. **發現過時即更新（讀者義務）** — 讀到 state 與實況不符 / ref 失效 / 結論被推翻，
   誰發現誰當場 supersede（一步式 `supersede --new-id ...`），不是記在心裡。
2. **工作進度必記**（`state` 型 fragment）— 快照當下進度（誰在做哪個 plan、做到哪、
   pending 什麼），**之後才能接續上次的進度開工**。進度過期時 `supersede` 開新快照，
   舊快照留著就是工作史。

## 🧩 資料結構

```
<repo>/AgentCommands/WorkMemory/<topic-slug>/
├── _topic.md          # 主題卡: title/status/related_topics/key_docs + 一段簡介
├── <type>_<slug>.md   # 記憶 fragment（事實源, 寫一次不改寫）
└── _index.md          # 機械生成視圖（work_memory.py index; 手改必被覆寫）
```

**fragment type 五型**：

| type | 記什麼 | 例 |
|---|---|---|
| `pointer` | **key→文件指路**（哪份文件是什麼的權威）— 首選型 | 文件地圖 |
| `state` | **工作進度快照**（接續開工用; 過期 supersede） | Plan A 完工/B 施工中 |
| `decision` | 拍板結論（不含推導, 推導留文件） | 五大資產基底 |
| `pitfall` | 該工作特有的坑（通用教訓歸 lessons, 別放這） | Hakoniwa enum 不可刪 |
| `knowhow` | 操作訣竅/既有基建清單（文件放不下的「現場感」） | 既有 infra 直接用清單 |

**frontmatter**（對齊 Memory_Fragment 哲學）：`id/topic/title/type/status(active|superseded|closed)/
created_at/created_by/links[]/related_docs[]`。

## 🔗 關聯（記憶之間 / 跨主題）

- fragment 級：`links: [<topic>/<fragment-id>, ...]` — 跨主題合法（例：
  `hscene-editor-rework/knowhow_existing-infra ↔ ucl-editor-pages/knowhow_page-skeleton`）
- 主題級：`_topic.md` 的 `related_topics`
- 建立走 `work_memory.py link --from t/f --to t/f`（自動雙向）
- 讀取 `read --topic X --with-links` 會把 1-hop 關聯 fragment 一起印（跨主題標來源）；
  dangling link（指向未來主題）合法，讀取時警示不炸

## 🛠 使用時機（SOP 摘要, 完整見 ucl-work-memory skill）

- **開工前**：`read --topic <slug> --with-links`；不知道主題名 → `topics` 列表
  或 `knowledge_base.py search --target work_memory --query "<要做的事>"`
- **完工/交接時整理**：新拍板→`decision`、新坑→`pitfall`、進度變化→`supersede` 舊 state + `add` 新 state
- **先搜再寫**（防洗版）：add 前先 KB search，命中近似 fragment 就追加/連結，不開新檔
- 索引與檢索：add/link 自動重建 `_index.md`；`knowledge_base.py reindex --target work_memory` 更新向量

## ✅ 驗收判準（2026-07-29 首航實測）

- 語意檢索：「興奮等級要用哪個資產當基底」→ `decision_asset-bases` 0.645 ✅；
  「編輯器頁面每幀讀磁碟會卡」→ `knowhow_page-skeleton` 0.545 ✅；
  負向對照（番茄怪）0.363 — 分數帶分離明確
- `read --with-links` 跨主題拉到關聯 fragment ✅

## ⚠️ 常見坑

1. **把文件內容轉貼進記憶** — 違反分工核心原則；寫 key + `related_docs` 指過去
2. **改寫舊 fragment 正文** — 漂移之源；更新走 supersede / 追加
3. **state 只有一份且過期** — 接手的人拿到假現況；進度變了就 supersede 開新快照
4. **通用教訓塞進 pitfall** — 跨工作的教訓歸 `agent-lessons-log`；pitfall 只放該工作特有的
5. **手改 `_index.md`** — 機械視圖，下次 index 就被覆寫
