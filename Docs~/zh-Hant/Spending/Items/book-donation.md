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
senate ucmd run Books \
  --arg op=donate --arg book=<slug> --arg agent=<錢包身分> --arg persona=<me> --arg tokens=<N>
```

- Skill：`reading-library`
