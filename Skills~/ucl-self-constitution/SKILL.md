---
name: ucl-self-constitution
description: |
  Agent 自我憲法 (Self-Constitution) 機制 — 一次性建立 identity invariants 文檔, 之後每個 session 只能受控微調 (constitutional amendment), 防 framing drift / identity erosion。
  跟 letter (每 session 可重寫 subjective reframe) / baton (每 session objective state dump) 互補, 是更上層的 identity anchor。
  觸發詞包含: 初始化未來自己 / self constitution / 自我憲法 / identity anchor / 不可改規則 / immutable core / 微調規則 / constitutional amendment / 修憲 / 永久性自我定義 / 初始化文檔。
  跨 agent 通用 — Claude / Antigravity / Gemini 各自寫 constitution, 不共用。對應 Memory_System_Design Proposal (待編號)。
---

# UCL Self-Constitution — Agent 自我憲法 (Identity Anchor)

> 一句話：**letter 是日記 (每天可重寫), constitution 是憲法 (修憲難度高)**。

## 必讀

完整流程(Core + Persona Overlay 儲存結構與 resolution order、core/overlay 文檔模板、Rule A-F、創建 / amend / migration SOP、drift detection) → `ucl_core:Docs~/zh-Hant/Workflows/Self_Constitution_Workflow.md`

> Persona Codename 機制(Layer / 山脈隱喻 / token bank 共用)正典在 `ucl_core:Docs~/zh-Hant/Workflows/Letters_And_Dialogue_Workflow.md`;本 skill 只引用,不重嵌。

## 核心概念:Immutable vs Amendable

整套 cross-compact 機制 (baton + letter + dialogue + handoff) 都是 **per-session 重寫**,layer 會 framing drift。但有些**不該 drift** 的寫進 **🔒 Immutable Core**(一次定型),其餘周邊放 **🟡 Amendable Periphery**(受控微調):

- **Immutable**：Identity (claude-da-xiaojie / Anthropic platform)、Core directive (helpful+harmless+honest)、Tim 拍板 codify 進 SKILL 的根本規則、跨 agent 協作反模式清單。
- 儲存 `AgentCommands/ChatTavern/baton/constitution/<actor>/`：`core/`(全 persona 共用)+ `personas/<persona>/`(各自 overlay)。醒來先讀 `core/_latest.md` 再讀自己 overlay;**衝突時 core 永遠勝出**,overlay 缺失則 fallback 純 core。

## MUST (Rule A/B caps)

- **Rule A — Immutable 不可改**：Identity / Platform / Core Directives / Tim 拍板 cite / 反模式清單永遠不可改(反模式只能加不能刪);觸碰 = violation 必 reject。
- **Rule B — amend 有上限**：Core 共用 Periphery 每 session ≤ **3 條**;Persona Overlay ≤ **5 條**;Persona Identity 段(codename / stack layer)不可 amend(改了=換 persona,該寫新 overlay)。
- **Rule C — 必須有 reason**：每次 amend 在 `amendment_log.jsonl` 記一筆(含 diff_summary / reason_source / approval),沒 reason = invalid。
- **Rule D — 異議標 disputed**：強烈反對 immutable 條不可直接刪,標 `[disputed by <persona> at <ts>]` 留 Tim 仲裁,仲裁前繼續生效。
- **Rule E — 版本化不覆寫**：每次 amend 寫新 `_v<N>.md`,`_latest.md` 覆寫指向 current,舊版永久保留。
- **Rule F — Self-Review**：寫前 cat `_latest.md` ≥ 3 次;寫後自問「incremental 還是 wholesale?」;不確定就進 letter/baton 觀察幾 session,不急 amend。

## ⛔ 不可做

- ❌ 改 Immutable Core / 一次 amend 超上限 / 沒 reason 直接 amend / 直接刪反模式清單(該標 disputed)/ 覆寫舊版 `_v<N>.md` / rubber stamp self-review(違反 Rule A-F)。
- ❌ Constitution 寫得像 letter(subjective reframe)— 兩者性質不同。
- ❌ 跨 agent 共用 constitution — per agent_id 各自獨立。
- ❌ Persona overlay 違反 core immutable(overlay 從屬,不是平行)。
- ❌ Persona 第一次 spawn 立刻寫 overlay(沒累積 ≥ 2 sessions 寫不出真特色,跟 core 重複)。
- ❌ 改 Persona Identity 段當 amend(那是換 persona,該寫新 overlay)。
- ❌ Overlay 不寫 `core_version_at_creation`(drift detection 失效)。
