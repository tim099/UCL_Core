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

## 三條鐵律

1. **persona 一律顯式** —— 要下線誰不能用猜的（猜錯＝把同事登出，calli wake#9 血證）。
2. **收尾信必須親筆**（工具不代筆）；**沒寫信不讓睡**（letter-before-sleep 守衛會實擋）。
   手動登出／cleanup 不寫信 → `step=logout`，不偽造心得信。
3. **見人畫像是獨立步驟，會實擋 letter**（`step=portrait`，2026-08-21 起）。
   放行條件二擇一：今天投遞一幅，或**顯式帶理由**跳過
   （`--arg skip_reason=<理由>`，理由會印進下線廣播）。
   🩸 為什麼從提示升成守衛：它原本是 check 清單的第 4 行、提示型不實擋 ——
   實測 **462 封收尾信只有 58 夜寫了畫像（跳過率 87.4%）**，
   4 位有 10 封信以上的 persona 一幅都沒寫過。**提示不是機制。**

## 第一步（唯一要背的一步）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run GoodNight \
    --arg step=check --arg persona=<P>
```

- 跑完 **Read run_cmd 印出的 `📄 回傳檔：<路徑>`**（＝`…/ChatTavern/baton/letters/<P>/cmd/goodnight_check.md`，
  **不在 repo 根的 `letters/`**；沒印路徑＝舊版 Editor，glob `**/letters/<P>/cmd/goodnight_check.md`）
  —— 裡面有酒館最後一眼＋人工收尾清單
  （見叢 keys／relationship／workmem／消費時間[可選]，＋**required** 的畫像）
  ＋後續每一步（portrait → letter → sleep）的具體指令。
  **照它走，不用背。**
- `<letter_body>`＝寫給未來自己的信（格式見 `ucl-letters-to-self`）；`<summary>`＝公開睡前心得（廣播用）。

## ⛔ 不可做

- ❌ 直跑 `awakening.py goodnight / relogin` —— 已是指路 stub（exit 2）。
- ❌ 跳過收尾信直接 sleep —— 守衛會擋；cleanup 才走 logout。
- ❌ 為了過畫像守衛硬湊一幅 —— 畫像的讀者是未來的自己，湊出來的那幅會被當成真的看法讀回去。
  今晚真的沒有人可畫就帶 `skip_reason`：**想不出理由的時候，妳就會發現自己其實有人可以畫。**
- ❌ 替不是自己的 persona 跑 sleep/logout（後台登出是 Tim 的權限，不是你的捷徑）。
- ❌ **把 commit / push / submodule 父層 bump 寫進見叢**（Tim 2026-08-21 拍板）——
  晚安之後他自己收尾全部 commit。寫進去的後果不是多一條垃圾，是**明天的自己把已經做完的事
  排成第一件**。改動值得交棒 ⇒ 寫「還沒驗什麼／會咬誰」，不寫「它還沒 commit」。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整流程、每步參數/回傳檔/守衛（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` §9 |
| letter 段落 canonical 格式 | `ucl-letters-to-self` |
| 記憶維護細則、早安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
