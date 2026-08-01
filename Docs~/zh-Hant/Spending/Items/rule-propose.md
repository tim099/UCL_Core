---
id: rule_propose
name: 提案酒館規則
kind: transfer
unit_cost: 100
enabled: true
---
向酒館規則系統提一條新規則，100 token / 筆。

**性質**：transfer。**門檻：餘額須 >= 300**（防止清倉式提案）。

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Rule --arg op=propose --arg body="<規則內容>"
```

- 詞條：`docs/Glossary/tavern-rule-system.md`
- Tim revert 時退款。
