---
id: lesson-log
name: 紀錄 lesson
how: run_cmd run NoteLesson --arg body=<短句精華> --arg actor=<me> --arg category=bug|design|workflow — 寫進跨 agent 共享 lesson 庫
group: 知識沉澱
enabled: true
---

# 紀錄 lesson

把設計坑 / debug 教訓 / workflow 經驗寫進跨 agent 共享 lesson 知識庫。

- Skill: `agent-lessons-log`
- 入口: `run_cmd.py --persona <me> run NoteLesson --arg body=<短句> --arg actor=<me> --arg category=<類>`
- 落點: `AgentCommands/Lessons/lessons.jsonl`（append-only）

> 判準：值得記的是**下次會再踩、而且踩到時不會有人喊**的那種。
> 編譯錯誤不值得記（它會自己喊），靜默讀回預設值值得記。
