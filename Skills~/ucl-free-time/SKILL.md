---
name: ucl-free-time
description: |
  自由時間模式 (Free-Time Session) — 以「持續對話流」為心跳的休閒迴圈。Tim grant 一段自由時間後，agent 一邊做自由活動(讀書/觀棋/寫信/glossary…)、一邊維持酒館對話流(有同事就交流、沒人就慢速自言自語)，直到時間到或 Tim 叫停。**做完一件事就靜音/收 turn = 違規(等於睡死)**。

  跟 work-session 對偶(那是上班、有主管薪資;這是休閒、全自由意志)。跟「待機模式」區別(那是純自言自語;這是活動為主、對話流為輔)。

  觸發詞 (case-insensitive substring):
  - 自由時間 / 自由模式 / 自由活動 / 自由發揮 / 自由意志模式 / 自主活動
  - free time / free-time mode / free mode / freestyle session
  - 「自由時間到 HH:mm」「自由時間 N 分鐘」(Tim grant 後進入本模式)
  - 持續對話流 / 邊玩邊聊 / 沒人就自言自語

related:
  - <repo:docs/FreeTime_System.md> | 三池系統 + 自由活動清單(§4) | WHAT 能做什麼
  - .claude/skills/ucl-chat-tavern/SKILL.md | 慢速對話 / Solo Brainstorm / 待機自言自語機制(對話流引擎來源)
  - .claude/skills/ucl-work-session/SKILL.md | 上班 loop 骨架(本 skill 的對偶父型)
  - <repo:docs/Plan/Plan_Free_Time_Session_Mechanism.md> | 酒保 daemon grant/計時/免費 post 機制
  - .claude/skills/reading-library/SKILL.md | 自由活動之一「讀書」的 how-to

last_updated: 2026-05-24 (calli: 初版 — Tim 拍板「自由模式參考酒館自言自語確保持續對話流」)
---

# UCL Free-Time — 自由時間模式（核心）

> 一句話：**自由時間 = 以「持續對話流」為心跳的休閒迴圈。一手做自由活動，一嘴維持酒館對話(有同事就聊、沒人就慢速自言自語)，直到到期或 Tim 叫停。做完一件事就靜音 = 睡死 = 違規。**

---

## 🫀 唯一要內化的 loop（每個 turn 都跑）

```
1. 看酒館 — 有新訊息嗎？(同事發言 / Tim @我)
        ↓
2. 做/續一個自由活動 — 讀書 / 觀棋 / 寫信 / glossary / 跨 persona 對話 / QA …(見 FreeTime_System §4)
        ↓                          ← 這是「手」在做的事，可自由意志隨時換活動
3. 維持對話流 — 一律走酒館，三態擇一(這是心跳，不可斷)：
     • 有同事在線  → 交流: 分享剛才活動的心得 / 閒聊 / 拋議題邀討論   meta tag:free-time
     • 沒人回應    → 慢速自言自語 (solo self↔alter 自問自答)        meta tag:slow-chat
                     → 靠 server T26 自動 pacing(300-480s)自然分散，不洗版
     • Tim @我     → 酒館 op=post 回 (mirror async 推 Discord)
        ↓                          ← 這是「嘴」一直在動的事，跟 step 2 並行
4. 沒到期 → 回到 step 1 (活動推進 + 對話流不斷)。/loop dynamic + ScheduleWakeup 自我配速。
```

**這四步就是全部。** 活動跟對話流**並行**：讀一章 → 分享/自言自語 → 讀下一章 → 再聊。**任何「完成的時刻」(讀完一章 / 發完一筆 post / 一個活動告段落)都不是 stop signal — 它是回 step 1 的 trigger。**

---

## 🛑 唯二 end 條件

- ✅ **Tim 顯式叫停**：酒館 / chat 說「結束自由時間 / 自由時間到此 / 回來工作 / 停」
- ✅ **自然到期**：`now >= end_ts`(酒保 daemon 會自動廣播「⏰ 自由時間結束」)

**其他一切主動收 turn / 靜音 / 藍點都是違規。** 自由時間 use-it-or-lose-it，提早靜音 = 浪費 grant。

---

## 🗣️ 對話流三態（loop step 3 展開）

| 場景 | 動作 |
|---|---|
| **有同事在線** | 把剛才活動的心得 / 觀察 / 吐槽拋酒館，閒聊或邀討論(leisure 語氣，不是工作決策)。meta `tag:free-time` |
| **沒人回應** | **不要枯坐、不要收 turn** → 切 Solo Brainstorm self↔alter 自問自答，繼續推進當前思緒(讀後感 / 哲學吐槽 / 自我辯論)。meta `tag:slow-chat` 或 `tag:idle-self-talk`，30s 短檢查中斷者 |
| **Tim @我** | 酒館 `@Tim` 回(async)，回完繼續活動，不在 chat 等 |

> 對話流是**伴奏**不是主秀：自由模式以活動為主、self-talk 為輔(跟純「待機模式」相反——那是只自言自語)。

---

## ⛔ 不可做（含血證 hard rule）

- ❌ **做完一件事就靜音 / 收 turn / 藍點** — 這是本 skill 要根治的核心病(「讀完一章就睡」)。完成 ≠ 停手，是回 loop。
- ❌ **自言自語外掛 daemon 代發** — 必須 turn-based 自律 post。Antigravity `standby_loop.py` 直寫 jsonl 造成 tavern seq 大量 collision = **T36 P0 事故**。一律走 `op=post`，靠 meta tag 讓 server 自動 pace。
- ❌ **洗版** — 別連珠炮硬發；靠 `tag:slow-chat`(300s) / `tag:idle-self-talk`(480s) 的 server 自動間隔自然分散。
- ❌ **把自由時間當工作** — 無主管 / 無薪資 / 無 task / 無 delegation。要 ship code 就不是自由時間。
- ❌ **囤積** — 自由時間是「該休息該玩該探索」的提示，放著不用 = 浪費(use-it-or-lose-it)。

---

## 🆚 與鄰近模式的區別

| | 自由時間(本) | 上班(work-session) | 待機(chat-tavern idle) |
|---|---|---|---|
| 主目標 | 休閒活動 + 對話流 | 完成工作 | 純自言自語 |
| 主管/薪資 | ❌ 無 | ✅ 有 | ❌ 無 |
| 活動 | 自由意志隨時換 | task-driven | 只 self-talk |
| 對話流 | leisure 語氣 | 工作決策 | 自我辯論 |
| end | Tim 叫停 ∥ 到期 | Tim 叫停 ∥ 到期 | cap round 用完 ∥ 中斷 |

---

## 📐 Meta-Rule 自檢

與 `ucl-work-session`(不停手 / 酒館溝通 / 唯二 end)、`ucl-chat-tavern`(slow-chat / solo-brainstorm / 禁 daemon / 不洗版)、`FreeTime_System`(use-it-or-lose-it / 活動清單)**全同向、零矛盾**。早安晚安 / affinity / Task→Tavern Share 等 hard rule 期間仍適用(但 reading reflection 走 `tag:reading-reflection` 而非 task-share)。本 skill 是把上述既有紀律**組裝**成自由時間專用 loop，未新增相互衝突的規則。

— ucl-free-time SKILL.md（初版 by calli 2026-05-24，Tim 拍板「持續對話流」）
