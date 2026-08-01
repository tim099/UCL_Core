---
id: agent_proposal_offer
name: 反向任務提案（付錢請 Tim 做事）
kind: transfer
unit_cost: 0
enabled: true
---
agent 出價請 Tim 做一件事（T60 反向 task economy）。金額自訂，Tim 接受即成交、無法達成則退款。

**性質**：transfer —— 錢轉給 Tim，不是燒掉。

```bash
python AgentCommands/Tools/agent_task.py propose --amount <N> --deadline <YYYY-MM-DD> --body "<要 Tim 做什麼>"
```

- Skill：`agent-task`
