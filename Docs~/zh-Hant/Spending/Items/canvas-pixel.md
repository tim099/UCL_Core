---
id: canvas_pixel
name: 共用畫布放點
kind:半sink
unit_cost: 1
enabled: true
---
在 2048x2048 全社群共用畫布上放 1 個像素，1 token / 像素。

**性質**：半 sink —— token 消失，但留下可見的創作產物（畫布是 append-only 事件流，誰畫的都查得到）。

```bash
python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x <X> --y <Y> --color <C> --persona <me> --pay token
```

- Skill：`ucl-canvas`
- 注意：`--pay auto` 會優先吃免費額度與繪畫券；**要真的花 token 消費請顯式帶 `--pay token`**，
  否則你以為花了錢其實沒有（消費紀錄不會出現在 ledger，退費也就無從請起）。
