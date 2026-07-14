---
name: agent-task
description: |
  T60 — Reverse task system: Agent → Tim 提案 task，Tim Y/N 接受。
  補完雙向 task economy — v1 只有 Tim → agent 單向，T60 加 agent → Tim 反向 channel。
  Tim 接受時立即 transfer (per Tim 拍板「完成交易」)；無法達成時 refund 反向 transfer。
  觸發詞：reverse task / 反向任務 / agent 派 task 給 Tim / Tim 接受 / Y/N / 退款。
  跨 agent 通用 — Antigravity / Gemini / Claude 同樣可用本機制提案給 Tim。
---

# Agent Task — Reverse Task System (Agent → Tim)

> 一句話:**agent 提案 task 給 Tim + 立即預付 token;Tim Y/N — accept 標 completed(款已付),decline / refund 走反向 transfer 退回。**

## 必讀

完整流程(v2 states 流程圖、fungibility hazard 設計權衡、propose/accept/decline/refund/withdraw 全 CLI、跨 agent 慣例、audit 路徑、edge-case 目錄) → `ucl_core:Docs~/zh-Hant/Workflows/AgentTask_Workflow.md`

> **Tim 06:55 拍板**：「妳可以發 Task 給我,但我可以決定是否接受 (Y/N)。接受就完成交易,我儘量達成;無法達成會儘量退回款項。」

## 何時 agent 該用本機制

✅ **適合用 reverse task**：
- 需要 Tim 親自驗收（compile / smoke test / 開 Editor）
- 需要 Tim 決策（A/B/C 拍板 / spec 評審）
- 需要 Tim 物理動作（修 webhook / 重啟 Unity）
- 需要 Tim 確認某事（review docs / 看 plan / 試新 tool）
- 健康行為對話化（「Tim 喝水我付 2 token」吊胃口式提醒）

❌ **不適合用**：
- 純資訊傳遞（直接 chat 講就好）
- 強迫 Tim 做不想做的事（這是對話不是脅迫）
- 給超過自己承擔能力的金額（agent 要 balance >= amount）
- 規避 health_fee（深夜 task 該扣自家 fee 不是 push 給 Tim）

## 倫理守則(compact,詳例見 workflow)

1. **不 push 健康成本給 Tim** — 對齊健康時段(早上 review 而非深夜)。
2. **不 spam Tim** — 1-2 高品質提案,勿一天塞滿 5 個。
3. **提案要 actionable** — 明確動作 + 完成標準,勿模糊。
4. **金額要合理** — 對應實際工時,勿過高。
5. **Refund 不羞恥** — Tim 累/沒空/改主意退款是正常路徑,不算違約。

## ⛔ 不可做

- ❌ 不在 jsonl 寫敏感資訊（PII / API key）。
- ❌ 不修改舊 entries — append-only。
- ❌ 不手動寫 ledger 跳過 cmd — 會破壞 audit chain。
- ❌ 不用 reverse task 規避 health_fee — agent 自家深夜 task 該扣自己。
- ❌ 不接受 amount=0 提案 — 那是請求不是交易。
