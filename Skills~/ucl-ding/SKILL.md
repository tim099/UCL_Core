---
name: ucl-ding
description: |
  Tim「叮」協議 — 像聊天軟體的「通知」：Tim 敲你 → 你先讀訊息 → 再決定回不回。**流程順序不可跳：讀 → 判斷 → 回。**
  被 @ 或 `叮(seq N)` 指定的必回；一般 nudge 可選。要回一律走聊天酒館 `tavern` 房，不可只在 chat 邊回。
  完整規則只有一份：`senate cmd doc --arg op=show --arg name=Tavern` §5。
  觸發詞(限 Tim 主動發, case-insensitive substring)：`叮` /「叮(seq N)」/ `Tim 叮` / `Tim ping` / `nudge` / `ping me`。排除 `自叮`／`persona ding`（那不是 Tim 叮你）。
  跨 agent 通用(Claude/Antigravity/Gemini/Zeta)；對應 CLAUDE.md 同 tier hard rule。

related:
  - docs/Glossary/trigger-ding.md | glossary 條目

last_updated: 2026-09-29 (TASK-0337：規則搬進 Senate `Tavern` 文件 §5，本檔只指路)
---

# UCL Ding — Tim 的酒館通知

> 一句話：**Tim 戳你 = 一則聊天通知。先讀 → 判斷 → 要回一律走酒館。**
> 規則本體在 Senate CLI 查得到，⛔ 本檔不重抄。

```bash
senate cmd doc --arg op=show --arg name=Tavern     # 看 §5 叮協議（讀用哪支、怎麼判斷、兩種 ack）
```

⛔ 沒讀就 ack ＝ robo-ack（calli／gura／ame 都撞過）。Tim 叮是要你進 context，不是按 ack 鈕。
