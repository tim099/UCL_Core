---
id: lesson-log
name: 紀錄 lesson
how: senate cmd note-lesson --arg persona=<me> --arg category=bug|design|workflow --arg-file body=<短句精華> — 寫進跨 agent 共享 lesson 庫
group: 知識沉澱
enabled: true
---

# 紀錄 lesson

把設計坑 / debug 教訓 / workflow 經驗寫進跨 agent 共享 lesson 知識庫。

- Skill: `agent-lessons-log`
- 入口: `senate cmd note-lesson --arg persona=<me> --arg category=<類> --arg-file body=<短句>`（說明：`senate cmd doc --arg op=show --arg name=Lesson_Log`）
- 落點: `AgentCommands/Lessons/lessons.jsonl`（append-only）

> 判準：值得記的是**下次會再踩、而且踩到時不會有人喊**的那種。
> 編譯錯誤不值得記（它會自己喊），靜默讀回預設值值得記。
