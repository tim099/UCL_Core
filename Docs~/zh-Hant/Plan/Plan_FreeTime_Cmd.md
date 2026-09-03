---
title: 自由時間 Cmd 化 — Cmd_FreeTime 分步 + 免費像素回歸
slug: freetime-cmd
status: approved-in-progress（2026-08-13 Tim 拍板 §6 四題＋step=next 觸發點，summit wake#48 施工中）
created_at: 2026-08-13T03:15:00Z
created_by: summit
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md | 早晚安 Cmd 流程 | 本案手法的母版（分步＋回傳檔 next＋每步落檔）
  - ucl_core:Skills~/ucl-free-time/SKILL.md | ucl-free-time | 現行入口（本案落地後**全重寫，不基於舊版修改**）
  - ucl_core:Docs~/{lang}/Mechanics/FreeTime_System.md | 三池系統＋活動清單 | 資料層（活動 md 雙層掃描機制保留）
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern | 開場/收工宣告走 in-process post
---

# 自由時間 Cmd 化 — Spec

## 0. 一句話（Tim 2026-08-13 拍板）

自由時間照早晚安手法收進**專用 Cmd 分步**：初始註冊「到什麼時候」（如 11:20），
之後每做完一件活動**再跑一次 Cmd**——還在時間內就再擲骰給下一件，時間到就通知收工。
**persona 必填**；每步回傳檔明示下一步；**免費像素功能回歸，每次自由時間 10 顆**；
skill 與文件**整個重寫，不基於舊版本修改**。

## 1. 分步設計（Cmd_FreeTime）

```
① senate ucmd run FreeTime --arg step=start --arg persona=<P> --arg until=<HH:mm 本地>
     ↳ 守衛：persona 必填＋必須在線（lock 存在 —— 自由時間是登入後的活動）；
       已有進行中 session → blocked（先跑 step=next 或 step=end，不疊開）
     ↳ 寫 session state（開始/截止時間、輪次=0）→ 發放本次免費像素 10 顆
     → 酒館開場宣告（in-process 單則：宣告時段＋像素額度）
     → 擲骰：活動清單隨機排序（雙層 md 掃描：UCL_Core Docs~/{lang}/FreeTime/Activities/ 共用
       ＋ <repo>/docs/FreeTime/Activities/ 專案限定）
     ↳ 回傳檔 next：骰面前 3 名（跟骰規則沿用：無明確意圖從前 3 挑一；不跟骰要在酒館註明）
       ＋「活動做完 → 再跑 step=next」＋剩餘時間
② （做活動：讀書/繪圖/觀棋/寫信/畫布…；有同事就交流、沒人就慢速自語 —— 行為層歸 skill）
③ senate ucmd run FreeTime --arg step=next --arg persona=<P>
     ↳ 觸發時間點（Tim 2026-08-13 補拍）：**當前自由時間事件的自然結束**——棋局結束、
       繪圖收筆、聊天告一段落、讀完一個段落…。step=next 是**活動邊界的檢查點**，
       不是週期輪詢；「事件結束」正是舊病「完成的時刻被當成 stop signal」發作的位置，
       把 Cmd 釘在這個時間點＝把「回 loop」從自覺變成通道（完成 → 跑 next → 拿新骰面）。
     ↳ 讀 session state 對系統時鐘：
       ・未到期 → 輪次+1、重擲骰 → 回傳：新骰面前 3＋剩餘時間＋像素餘額＋「做完再跑 step=next」
       ・已到期 → 「⏰ 時間到」＋關 session ＋ 酒館收工宣告（in-process）
         ＋ next 指路（結算像素用量／回工作或走晚安流程）
④ senate ucmd run FreeTime --arg step=end --arg persona=<P> [--arg reason=<一句>]   （選配，待拍）
     ↳ 提前收工：關 session＋收工宣告（附 reason —— 提早收工的形狀要可觀測，不靜默）
```

- 回傳檔 `letters/<P>/_freetime_<step>.md`（機械產物、本地時間標頭、同 `cmd/goodmorning_*` 慣例、
  進 .gitignore 同族規則）；blocked 一律「payload 落檔＋非零退出」。
- **每步回傳值必含三個時間欄**（Tim 2026-08-13 補充）：`當前時間`（本地）／`自由時間到幾點`／
  `剩餘時間（分鐘）`——時間感由 Cmd 供給，agent 不自己心算（自報時刻的第七型未遂就是這樣來的）。
- **時限判定只認時鐘，不認收束感**（w44/w45 血證）——到期判定在 Cmd 內對系統時鐘，
  agent 不再自報「時間到了」。
- 骰子哲學沿用（隨機性可觀測地參與自由意志）：擲骰結果印在回傳檔**並進酒館宣告**——
  跟沒跟骰在酒館可觀測，不靠自覺。`dice.py choose/roll` 通用工具保留（N 選一等場景照舊），
  只有「自由時間開場/換輪擲骰」收進 Cmd。

## 2. 免費像素（回歸＋改制）

- **每次自由時間發 10 顆**（step=start 發放；per-session 有效，**不跨場累積** —— 待拍確認）。
- 發放走 canvas 的 freetime state（`AgentCommands/Canvas/freetime/<persona>.json`）——
  **C# 只做發放（增額），消費仍走 `canvas.py place --pay freetime`**。
  ⚠ 兩端 schema 對齊義務（同 wake 計數兩端對齊的既有義務）：施工前先讀 canvas.py freetime
  的現行欄位與判定，C# 寫入逐欄對齊；改任一端要同步改另一端。
- step=next / 收工回傳檔顯示像素餘額與本場用量（可觀測，不用自己記）。

## 3. Skill 與文件（全重寫）

- `ucl-free-time` SKILL：**整個重寫**（Tim 明示不基於舊版修改）——只教 step=start
  ＋兩條鐵律（persona 顯式／時間到聽 Cmd 的）＋行為層指引（活動為主對話流為輔、跟骰規則、
  有人就聊沒人慢速自語）。三 target 同步。
- 完整流程進 `Awakening_Cmd_Flow.md` 新章節或獨立參考文件（**待拍**——傾向併入：
  start/next/end 的守衛與回傳檔慣例與早晚安全同款，兩份必漂移）。
- `FreeTime_System.md`（三池＋活動清單）：資料層保留，流程段改指 Cmd；活動清單 md 機制不動
  （增改 md 即同步，這條設計本來就對）。

## 4. 卡點

1. **canvas freetime schema 兩端對齊**（C# 發放 vs python 消費）——先讀 canvas.py 現行實作，
   確認「加回去」前該功能的現狀（欄位/重置邏輯/是否已停用），別對著想像的舊制施工。
2. **session state 的歸屬**：建議 `AgentCommands/FreeTime/sessions/<persona>.json`
   （狀態類，同 _session 慣例；letters 只放回傳檔）。多 persona 並行各自獨立。
3. **到期後跑 step=next 的語意**：到期即收工（宣告＋關檔）——「過期的 session 再 next 一次」
   必須是收工而不是報錯，否則超時回來的人卡在沒有出口。
4. **開場/收工宣告的計酬**：走 Cmd_Tavern in-process 會觸發 post reward——與早晚安同款預期存在。
5. **重寫 skill 時舊觸發詞保留**（自由時間/free time/持續對話流…）——重寫的是內容不是入口。

## 5. 驗收（Template 殼）

- start（註冊 until）→ 回傳檔含骰面/像素額度/剩餘時間；連跑 start 第二次 → blocked。
- next 未到期 → 重擲＋輪次遞增；把 until 設成過去 → next 回「時間到」＋收工宣告＋session 關閉。
- 像素：start 後 canvas freetime 額度 +10；canvas.py 端可用 `--pay freetime` 消費（兩端對齊實測）。
- 全程 payload 檔逐步落檔供 QA；persona 缺 → 非零退出。

## 6. 拍板紀錄（Tim 2026-08-13，四題照建議定案）

1. ✅ `step=end`（提前收工）**收進第一版**——提早收工要有名字的出口，不靜默。
2. ✅ 像素 **per-session 清零**——「每次 10 顆」語意乾淨，用不完歸零。
3. ✅ 完整流程文件**併入 Awakening_Cmd_Flow.md**——守衛/回傳檔慣例同款，兩份必漂移。
   - ⚠ **2026-08-18 由 Tim 改判：拆成獨立檔** [`Workflows/FreeTime_Cmd_Flow.md`](../Workflows/FreeTime_Cmd_Flow.md)。
     原句不刪（那是當時的判斷，且它的**理由仍然成立**）。改判的前提變了：自由時間長出了活動層
     （`Cmd_FreeTimeActivity` pick/step/done）、換骰整合讀訊息＋聊天、活動 md 的 `tool`/`steps`
     —— 它已不是「早晚安的同款三步」。
     ⇒ 而「兩份必漂移」這個理由被**照著執行**：`Awakening_Cmd_Flow.md` 的自由時間章節**整段刪除**（歷史留 git），
     全系統仍只有一份完整流程。**拆檔沒有讓那句話失效，是那句話決定了拆法。**
4. ✅ start **強制在線**（lock 存在）——自由時間是登入後的狀態；未登入先走 GoodMorning step=wake。

另拍（同日補充）：`step=next` 的觸發時間點＝**當前活動事件的自然結束**（已寫入 §1 ③）。

**v1 上線後同日補拍（Tim，2026-08-13 下午）**：
5. ✅ **step=end 除非明確指示不用**——正常收工一律由 next 對時鐘自動判定（end 描述同步改）。
6. ✅ **截止是軟的**——until 到了不打斷進行中活動，最後一件做完跑 next 才通知收工
   （例：14:10 截止、14:12 繪圖收筆 → 該次 next 才宣布）；canvas 端不拿 end_ts 掐額度。
7. ✅ **活動 md 新增選填欄 `min_minutes`**（建議所需分鐘；apex-one seq 11180 回饋觸發）——
   擲骰時剩餘不足的活動**排尾＋標「時間不夠」**（不隱藏）；剩 <5 分時 next 改印
   「不建議起新活動」不給新骰面。首批設定：gaming=20（TRPG 例）、stream-watch=20。
施工追加項（酒館討論產出）：step=next 回傳的「下一件活動」**附活動 md 的實路徑**——
由掃描端傳遞，不讓 agent 拿活動名反推雙層目錄（「路徑不該被推導，該被傳遞」，
同族先例：result outputs 欄 `802a118`、inbox 截斷附真路徑 `8118ba3`）。
