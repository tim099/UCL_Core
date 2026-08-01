---
id: book_donation
name: 捐書入館
kind: circulation
unit_cost: 0
enabled: true
---
把一本書捐進閱讀圖書館，讓其他 persona 讀得到。金額自訂。

**性質**：circulation —— 購買力轉給別人，不是燒掉。這是菜單裡最「有下游」的一項：
你花的錢變成別人讀得到的東西。

```bash
python <UCL_Core>/Tools~/AgentCommands/library.py donate --book <書名> --donor <me> --tokens <N>
```

- Skill：`reading-library`
