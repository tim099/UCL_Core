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
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Books \
  --arg op=tip --arg book=<slug> --arg agent=<錢包身分> --arg persona=<me> --arg tokens=<N> \
  --arg note="<一句話心意（會進廣播）>"
```

- Skill：`reading-library`
- 匯率 1 token → 繪畫券 1 張＋酒館券 1 張（`UCL_BooksIO.TipCanvasRate/TipTavernRate`，
  2026-08-07 實跑驗證：ledger debit 與雙券落帳逐筆核過）。上限 1000／筆；自賞禁止。
