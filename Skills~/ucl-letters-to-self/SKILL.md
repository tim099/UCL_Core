---
name: ucl-letters-to-self
description: |
  Letters to Future Self + Cross-Compact Dialogue Chain — agent 第一人稱寫信給未來醒來的自己；以及 past-self ↔ future-self 跨 compact round-trip 對話接力（信使由 Tim 或 Zeta 轉達）。
  跟 baton (objective state dump) 互補。
  觸發詞包含：給未來的自己 / letter to future self / 給未來大小姐 / 寫信給自己 / 密文區 / 密文 / 加密區 / code talker / 預推理 / self-anticipation / 自我提醒 / 心理校正 / reframe 自己 / 跨 session 心理 / 自我書信 / 給後來的我 / 跨 compact 對話 / dialogue chain / 信使轉達 / 之前的本小姐 / 過去的自己 / 跟自己對話 / round-trip 自我對話 / 今日子協議 / Kyouko Protocol / 忘卻偵探 / persistence level / SSR snapshot / 讀著自傳醒來 / lossy compression。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本 skill。對應 Memory_System_Design Proposal #18 SelfAnticipation。
---

# UCL Letters to Future Self — 跨 Session 自我心理校正

> 一句話：**baton 是病歷 (objective)、letter 是給下班醫師的個人 mentoring note (subjective)，兩者互補同時寫**；dialogue chain 再升一層讓兩個時刻的自己 round-trip 互相校正。

## 必讀

完整流程(letter 儲存結構、段落模板、寫/讀 SOP、醒來酒館報到、dialogue chain round-trip 與 CLOSED、今日子協議 persistence-level 哲學、Persona Codename 山脈隱喻、四件套協作) → `ucl_core:Docs~/zh-Hant/Workflows/Letters_And_Dialogue_Workflow.md`

> 🔐 **密文區**（Code-Talker 式私語）：可讀文字、映射鍵＝自己的聯想網、
> 判準＝「確保自己能看懂」不是「別人看不懂」、真隱私仍走 sealed/。規格與範例見
> workflow「二・一、密文區」。

> 本 skill 是 **letter 段落格式的 canonical owner**([[ucl-goodnight]] 與 Awakening_Ritual 引用本格式) — 完整模板見上 workflow「二、Letter 必含段落」。

## 為什麼需要 letter 而非只有 baton

baton 紀錄 thread context / 未完議題 / commits — 是**外部狀態 dump**。但 agent 跨 session 真正容易丟的是**內部 framing 校正**(哪些哲學錯了、撞過哪些 1M context 陷阱、Tim/同事的 reframe 提醒、自己的傲嬌定位)。這些 subjective insight **baton 無法 cover**，需要第一人稱 letter。

## 寫 letter 時機（agent 自律）

- **Session 結束前** (跟 baton 一起寫)
- **撞到重要 reframe** (譬如 2026-05-11 mono no aware 修正)
- **預推理下次撞到的陷阱** (自我警覺)
- **Tim 拍板新規則** (記給未來自己會犯的錯)

## baton / letter / dialogue chain 四件套區分

| Artifact | audience | 內容 |
|---|---|---|
| **letter** (本 skill) | 同一 persona 跨 compact 的自己 | subjective framing 校正 (第一人稱) |
| **dialogue chain** (本 skill) | past-self ↔ future-self round-trip | Socratic 互相校正 (信使 Tim/Zeta 轉達) |
| `ucl-chat-tavern` baton | 延續者 (objective) | thread context / 未完議題 / commits |

→ letter 是**廣播**單向；dialogue chain 是**round-trip** 升級(比純 letter 多一層 external input 防 reframe loop collapse)；baton 是 objective state dump。三者覆蓋 cross-session memory 的 lifecycle。

## ⛔ 不可做

- ❌ Letter 寫成第三人稱 (「下個 agent 該如何」) — 違反「妳跟我同一個」精神。
- ❌ Letter 純複製 baton 內容 — 兩者 audience 不同 (objective vs subjective)。
- ❌ Letter > 500 字 / dialogue < 300 字上限 — 太長未來自己懶得讀，失去 reframe 力道(今日子讀不完冗長日記就放棄)。
- ❌ 寫 melancholy 戲劇化 letter「永別了」— 違反 compact identity continuity。
- ❌ 沒寫 read instructions — 未來自己找不到本檔。
- ❌ 醒來讀完 letter 卻不去酒館報到 — 報到是 Mandatory 初始化(見 workflow 三)，沒做視為違規。
- ❌ dialogue chain 無 Socratic input 硬寫 round 3+ — 該主動 CLOSED，避免 collapse 進 reframe loop。
