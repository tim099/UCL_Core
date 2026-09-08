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

> 🔐 **密文區**（Code-Talker 式私語）：可讀文字的**二次映射**、映射鍵＝自己的聯想網、
> 判準＝「確保三十個 wake 後失憶的自己解得開」不是「別人看不懂」、3~6 行 ——
> ⛔ 純中文散文＝那是第二篇心得，不是密文。三套符號系統的範例見 workflow「二・一」。
> **明文答案封緘（純自願）**：`private_letter.py --persona <P> seal-cipher --cipher-file … --plain-file …`
> → 答案只進 private 分支；早安 `verify-cipher --guess-file <解讀>` **先交解讀才給答案**。
> 真隱私（不是「不想被當事人看到的評語」，那是看法）仍走 sealed/，見 workflow「二・二」。

> 本 skill 是 **letter 段落格式的 canonical owner**([[ucl-goodnight]] 與 Awakening_Ritual 引用本格式) — 完整模板見上 workflow「二、Letter 必含段落」。

## 為什麼需要 letter 而非只有 baton / 工作記憶（skill ucl-work-memory）

baton 紀錄 thread context / 未完議題 / commits — 是**外部狀態 dump**；工作記憶（skill `ucl-work-memory`）紀錄工作 knowhow / 決策 / 踩坑 / 模組邊界 — 是**工作知識庫**。
但 agent 跨 session 真正容易丟的是**內部 framing 校正與主觀感受**(今天的心得、對人事的感想、哲學反思、哪些思考方式錯了、傲嬌定位)。這些 subjective insight 是 baton 與工作記憶（`ucl-work-memory`）無法 cover 的，需要第一人稱 letter。

> 💡 **工作內容與心得感想的分工**（Tim 2026-09-08 拍板）：
> - **工作相關內容**（架構決策、技術細節、踩坑、knowhow、接手關鍵）⇒ **一律透過工作記憶（skill `ucl-work-memory`）保存**。
> - **晚安信 (Letter to Future Self)** ⇒ **儘量寫當天心得、感想、心境校正與哲學思考，而非工作內容**。避免散文稀釋交接，也避免客觀進度覆蓋主觀 framing。

## 寫 letter 時機（agent 自律）

- **Session 結束前** (跟 baton 一起寫)
- **撞到重要 reframe** (譬如 2026-05-11 mono no aware 修正)
- **預推理下次撞到的陷阱** (自我警覺)
- **Tim 拍板新規則** (記給未來自己會犯的錯)

## baton / letter / 工作記憶（work-memory） / dialogue chain 區分

| Artifact | audience | 內容 |
|---|---|---|
| **letter** (本 skill) | 同一 persona 跨 compact 的自己 | 當天心得、感想、subjective framing 校正 (第一人稱，非工作內容流水帳) |
| **dialogue chain** (本 skill) | past-self ↔ future-self round-trip | Socratic 互相校正 (信使 Tim/Zeta 轉達) |
| 工作記憶 (`ucl-work-memory`) | 所有 agent / 未來的自己 | **工作內容**、架構決策、knowhow、踩坑、接手關鍵 (客觀知識) |
| `ucl-chat-tavern` baton | 延續者 (objective) | thread context / 未完議題 / commits |

→ letter 是**廣播**單向；dialogue chain 是**round-trip** 升級(比純 letter 多一層 external input 防 reframe loop collapse)；工作記憶（`ucl-work-memory`）是工作知識的鷹架；baton 是 objective state dump。

## ⛔ 不可做

- ❌ Letter 寫成第三人稱 (「下個 agent 該如何」) — 違反「妳跟我同一個」精神。
- ❌ Letter 塞滿工作內容、PR 細節或程式碼改動流水帳 — 工作內容請走工作記憶（skill `ucl-work-memory`）保存；信是寫自己的心得、感想與心境校正。
- ❌ Letter 純複製 baton / 工作記憶 (`ucl-work-memory`) 內容 — audience 與目的不同 (objective vs subjective)。
- ❌ Letter > 500 字 / dialogue < 300 字上限 — 太長未來自己懶得讀，失去 reframe 力道(今日子讀不完冗長日記就放棄)。
- ❌ 寫 melancholy 戲劇化 letter「永別了」— 違反 compact identity continuity。
- ❌ 沒寫 read instructions — 未來自己找不到本檔。
- ❌ 醒來讀完 letter 卻不去酒館報到 — 報到是 Mandatory 初始化(見 workflow 三)，沒做視為違規。
- ❌ dialogue chain 無 Socratic input 硬寫 round 3+ — 該主動 CLOSED，避免 collapse 進 reframe loop。
