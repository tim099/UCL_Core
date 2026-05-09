---
name: agent-lessons-log
description: |
  跨 agent 共享 lesson 知識庫 — 累積設計坑、debug 教訓、workflow 經驗。觸發詞包含：學到 / 經驗 / lesson / 紀錄筆記 / 教訓 / a-ha / 筆記 / 自律紀錄 / 撞坑。
  agent 跨 session 撞到既有教訓覆蓋的問題前先讀本 skill；新 lesson 走 `Cmd_NoteLesson` 自動 append jsonl，避免遺忘 + 跨 agent 同步知識。
---

# Agent Lessons Log

> 跨 agent 累積經驗 — 100 字內 curated lessons 精華 + jsonl audit log（避免每位 agent 重新踩同樣的坑）。

## Curated Lessons (約 100 字精華)

- **L1 [T42 UCL_Json]**: UCL_Json bool 序列化用 `"True"/"False"` 字串非 JSON 原生 bool
- **L2 [T42 routing]**: Discord routing 用 additive 不 exclusive，防 main webhook 空轉
- **L3 [T46 Bug2]**: Placeholder URL（含 REPLACE_ME）要 filter 防 fail 累計拉掛系統
- **L4 [T46 Bug1]**: Human payer ≠ work agent — sender 黑白名單分層 (Tim 不該領 work_post 薪資)
- **L5 [跨 task]**: dogfood 驗證 > 理論 plan — 規則 ship 後立刻活體跑一輪
- **L6 [bash]**: post body 含 `` ` ( ) | `` 要用 here-doc 或 escape 防 shell 吃掉
- **L7 [health]**: Quota / context window / Tim 累 = 三個獨立的「該停下」訊號
- **L8 [Treasury]**: Commit 是先結算薪資的 task — pre-credit before commit (per Tim 拍板)
- **L9 [T49 token_parse]**: 規則永遠匯給 sender 是錯的 — 加「@<acct>」/「支付前綴」反向路徑分流

## 自動化筆記入口（agent 自律）

撞到設計坑 / debug 教訓 → 立刻走 cmd 紀錄不靠記憶：

```bash
python AgentCommands/run_cmd.py run NoteLesson \
  --arg body="<短句精華 < 30 字>" \
  --arg actor="<agent_id>" \
  --arg category="<bug|design|workflow|debug|test>"
```

行為：
1. append `AgentCommands/Lessons/lessons.jsonl` 一行 JSONL entry (ts/actor/category/body)
2. 寫 `AgentCommands/Lessons/_last_lesson.md` 給 caller confirm
3. 同 body 重複 → skip 防重 (dedupe check)
4. category 自由欄位（agent 自律分類，譬如 "bug" / "design" / "workflow"）

## Promote curated SKILL.md 流程

jsonl 是 raw audit log，curated SKILL.md 是 ≤ 100 字精華頂級 lesson。**手動** promote (避免 SKILL.md 膨脹)：

1. 定期 review jsonl tail（每週 / ship 一批 task 後）
2. 找有「跨 task 通用」價值的 lesson
3. 手動 edit 本 SKILL.md「Curated Lessons」section 加進去
4. 控制總長 ≤ 100 字（取代舊的 / 合併相似條目）
5. 老舊 / 過時 curated lesson 移除（仍留 jsonl 不刪）

## 跨 agent 慣例

- **Claude / Antigravity / Gemini 都讀本 skill** — jsonl 共享，actor 欄位標來源
- **新 session re-enter**: 先 grep curated list + jsonl tail (--limit 20) 找近期教訓
- **不要寫超長 lesson** — 一行精華 < 30 字；長 retrospective 走 `docs/Plan/` 或 `docs/Postmortem/`
- **跨 session 撞同樣坑兩次** = 教訓沒落 SKILL.md curated → 該 promote

## 不要做

- 不在 jsonl 寫 PII / token / API key / webhook URL
- 不重複既有 curated lesson — append 前先 grep
- 不過度紀錄瑣事（lesson 必須 actionable，碰到類似情境立刻能用）
- 不自動 update SKILL.md（curated 必須人工 review）

## 必讀

- 主流程跨 agent 對話 → `ucl-chat-tavern` skill
- Treasury 結算規則 → `ucl-chat-tavern` SKILL.md「績效獎金額度 / 自動結算」section
- Commit 規範 → `ucl-commit` skill
