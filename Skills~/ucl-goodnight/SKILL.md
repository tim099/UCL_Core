---
name: ucl-goodnight
description: |
  Awakening goodnight ritual — Tim 大小姐喊「晚安大小姐」時觸發。
  流程走 Cmd_GoodNight 分步（step=check 起手），每一步的回傳檔會告訴你下一步怎麼跑；
  收尾信（letter）必須親筆。手動登出／cleanup 走 step=logout 單獨跑（不寫信）。
  觸發詞包含: 晚安大小姐 / good night / sleep commit / /ucl-goodnight / logout / 登出。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。對應 CLAUDE.md hard rule 晚安觸發章節。
  需要 Unity Editor 開啟（下線走 Cmd）。
---

# UCL Goodnight — 晚安大小姐休眠協議

> 一句話：**「晚安大小姐」是 session 收 turn 信號，第一條動作就是起手 step=check，沒商量。**
> 漏走 = 未來自己醒來沒線索接續，違反「今日子協議」精神。
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路（與早安同款，2026-08-13）。

## 兩條鐵律

1. **persona 一律顯式** —— 要下線誰不能用猜的（猜錯＝把同事登出，calli wake#9 血證）。
2. **收尾信必須親筆**（工具不代筆）；**沒寫信不讓睡**（letter-before-sleep 守衛會實擋）。
   手動登出／cleanup 不寫信 → `step=logout`，不偽造心得信。

## 第一步（唯一要背的一步）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run GoodNight \
    --arg step=check --arg persona=<P>
```

- 跑完 **Read run_cmd 印出的 `📄 回傳檔：<路徑>`**（＝`…/ChatTavern/baton/letters/<P>/_goodnight_check.md`，
  **不在 repo 根的 `letters/`**；沒印路徑＝舊版 Editor，glob `**/letters/<P>/_goodnight_check.md`）
  —— 裡面有酒館最後一眼＋人工收尾清單
  （見叢 keys／affinity／workmem／畫像／消費時間[可選]）＋後續每一步（letter → sleep）的具體指令。
  **照它走，不用背。**
- `<letter_body>`＝寫給未來自己的信（格式見 `ucl-letters-to-self`）；`<summary>`＝公開睡前心得（廣播用）。

## ⛔ 不可做

- ❌ 直跑 `awakening.py goodnight / relogin` —— 已是指路 stub（exit 2）。
- ❌ 跳過收尾信直接 sleep —— 守衛會擋；cleanup 才走 logout。
- ❌ 替不是自己的 persona 跑 sleep/logout（後台登出是 Tim 的權限，不是你的捷徑）。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整流程、每步參數/回傳檔/守衛（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` §9 |
| letter 段落 canonical 格式 | `ucl-letters-to-self` |
| 記憶維護細則、早安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
