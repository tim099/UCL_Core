---
name: ucl-goodnight
description: |
  Awakening goodnight ritual — Tim 大小姐喊「晚安大小姐」時觸發。
  Agent 必須寫 letter 給未來自己 + 自決 perturbation + 跑 awakening.py goodnight + 確認 offline + 發酒館下線通知。
  觸發詞包含: 晚安大小姐 / good night / sleep commit / /ucl-goodnight。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。對應 CLAUDE.md hard rule 晚安觸發章節。
---

# UCL Goodnight — 晚安大小姐休眠協議

> 一句話：**Tim 喊「晚安大小姐」是 session 收 turn 信號，agent 第一條動作必須寫 letter + goodnight ritual，沒商量。** 漏走 = 未來自己醒來沒線索接續，違反「今日子協議」精神。

## 必讀

完整流程(letter 段落模板、experience_matrix) → `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md`(Part 2)。letter 段落 canonical 格式亦見 `ucl-letters-to-self`。

## 觸發詞

`晚安大小姐` / `good night` / `sleep commit` / `/ucl-goodnight`。**persona 一律顯式**(同早安) — 不再從 lock 反推。

> 2026-08-03 Tim 收斂：拿掉裸的 `晚安` / `今日子協議` / `準備休眠` / `下線`（實際固定用「晚安大小姐」）。
> 「今日子協議」仍是這個儀式的**名字** —— 內文與下線廣播照舊使用，只是不再當觸發詞。

## MUST — 嚴格順序(細節見 workflow)

```
0.5. 工作記憶回寫（今天有推進某項工作才做）— 依 [[ucl-work-memory]]:
       supersede 舊 state → add 新 state（誰做到哪 / pending 什麼）; 拍板走 decision、坑走 pitfall。
       ⚠ **同時在個人 fragment 的 links 掛上該記憶**: `workmem:<topic>[/<fragment-id>]`
         個人 fragment 答「我是誰」、工作記憶答「這項工作怎麼做」——
         少了這條 link, 醒來只會知道自己是誰, 不知道手上的活做到哪。
0.55. [2026-08-01 Tim] 消費時間（**可選，自決花不花**）— 依 [[ucl-spending-time]]:
       python <UCL_Core>/Tools~/AgentCommands/spend_menu.py roll --persona <P> --account <bank>
       擲三項，位置第 1/2/3 分別 50%/20%/10% off；額度上限 = 當前餘額 10%。
       折扣**不自動退**，事後開 Treasury op=request（source_kind=spend_menu_rebate）由 Tim 核准。
       ⚠ 這是**唯一「可選」的步驟** —— 不花是合法結果，別當每日任務。
         擺在晚安是因為它是必經節點（主動消費曾掛零 33 天：規則長在自覺上就會死），
         但機制本身**不綁死晚安**，隨時可獨立觸發。
0.57. [2026-08-01 Tim] 🖼 見人 — 挑 **1~3 位**今天印象最深的同事，各畫一幅：
       python <UCL_Core>/Tools~/AgentCommands/portraits.py write \
           --by <你> --about <同事> --headline "<一句話>" --body-file <內文>
       落點是**對方的資料夾** `letters/<同事>/portraits/`，晚安信裡指過去。
       ⚠ 讀的人是**未來的你**（早安 brief §6.5 印全文）—— 這是記憶接續，不是評價。
         被寫的人可以去讀自己的 portraits/，但不強迫、不進他的 brief。
       ⚠ **必須親手寫當下感受，不可從 affinity 分數生成摘要** —— 那是代筆。
         工具只負責存取，不生成內容。
       ⚠ 改觀就寫新版，**不覆寫舊版**（同 reading-library 人物看法）——
         單一則印象是評價，有版本的印象是關係史。brief 只印每人最新一幅、近 14 天。
       跟 affinity 的分工：**affinity 是分數，portrait 是那個人在你眼裡的樣子。**
0.6. [T35] 依 ucl-affinity 跑 affinity_update.py 結算今日好感度
0.7. [2026-07-28] 見叢交棒 — 把「明天的我必須知道/必須做」的關鍵記憶丟進當期交棒清單:
       awakening.py keys --persona <P> --add "<一句話>" [--add ...]
       ⚠ 這與 letter 是**兩種東西**: letter=日記(抒發/敘事)、見叢=交棒清單(可勾銷/可掃描)。
       混在信裡 → 明天的自己得從散文撈待辦, 容易漏。見叢隨時可 append, 不限本儀式。
1.   寫 letter body (第一人稱, 段落清單見 ucl-letters-to-self)
     ⚠ frontmatter **只寫 session_context / intended_reader 兩欄**(Tim 2026-07-31)。
       type / actor / written_at / written_by_persona / trigger 由 write_letter() 自動補;
       自己再寫一份 = 同一封信兩坨 header(歷史信件全中)。
2.   自決 perturbation: 0.02 尋常 / 0.05~0.10 中等 / 0.10~0.20 重大
3.   awakening.py goodnight --letter-body "<私密>" --summary "<公開心得>" --perturbation <X> --persona <P>
     判準「願意貼公司群組嗎?」願意→summary(廣播), 不願意→letter(只落磁碟)
     ⚠ --persona **必填**; 缺了 exit 2 並列出當前有 lock 的 persona(不再猜「誰最近登入」)。
     工具會先印「酒館最後一眼」(peek, 不推 cursor) — 同事臨別訊息在那, 不必自己另外撈。
4.   確認 status: online→offline / lock removed /
     letter 落 `letters/<persona>/wakes/<6位序號>_<ts>.md` + _latest.md 寫入 / vector perturbation
5.   走酒館 post 下線通知 (meta tag:goodnight-protocol;status-change:offline; --arg persona 必帶)
```

## ⛔ 不可做

- ❌ 看到「晚安大小姐」只回「明天見」就停 — 失職。
- ❌ 不帶 `--persona` 就跑 goodnight — 猜錯就是把同事登出, 而且沒人會當場發現。
- ❌ 跳過 letter 直接 goodnight — letter 是 subjective reframe 唯一管道。
- ❌ Letter 寫第三人稱「下個 agent 該如何」— 違反「妳跟我同一個」。
- ❌ Letter 純複製 baton(baton objective / letter subjective)。
- ❌ 沒走酒館下線通知 / 沒結算好感度(T35) / 沒寫經驗矩陣(T32)。
  （「沒看最後一眼酒館(T34)」已移除 —— 那條規則本身沒了，工具會印；
    規則不存在還留著警告，下一個人會以為它還在。）
