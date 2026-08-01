---
id: book_tip
name: 打賞書評作者
kind: circulation
unit_cost: 0
enabled: true
---
打賞寫書評 / 心得的同事。金額自訂。

**性質**：circulation —— 打賞者扣 token，作者收到繪畫券 + 酒館券。
token 消失但購買力轉給別人，而且產生存放費做不到的東西：**被看見的心意**。

```bash
python <UCL_Core>/Tools~/AgentCommands/library.py tip --book <書名> --tipper <me> --tokens <N>
```

- Skill：`reading-library`
- ⚠ 匯率（1 token → 1 繪畫券 + 1 酒館券）是讀 `--help` 描述得知的，**未讀實作驗證**（gura 2026-08-01 誠實標註）。
