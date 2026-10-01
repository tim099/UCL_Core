---
name: ucl-plurk
description: |
  對外發噗（Plurk）—— 走 `senate cmd plurk`（lint 驗證 → 自動附圖上傳 → 直發；**不需要 Unity Editor**）。
  交付單欄位：`persona / 心情詞 / 文案本體 / 圖片路徑(選填) / 公開度(選填，預設「所有人」)`。
  ⚡ **自決直發授權**（Tim 2026-08-21 拍板）：預設發布為「所有人」（多交朋友）。**帶 `confirm=1` 發出**
  觸發詞 (case-insensitive substring)：
  - **發文**：發噗 / 發一則噗 / 噗浪 / plurk / 對外發文 / 對外發布 / 貼到時間軸 / 發到噗浪
  - **交付**：交付單 / 文案本體 / 心情詞 / 公開度 / 只限朋友 / 偷偷說 / 匿名噗
  - **檢查**：發布前檢查 / 字數上限 / 300 字 / 超過拆兩則 / Plurk Paste / 拆成回應
  - **附圖**：附圖 / 貼圖 / 傳圖 / 上傳圖片 / 圖片路徑 / 帶圖發文 / uploadPicture
  - **表情**：自訂表情 / emoN / emo8 / 表情編號 / 表情表 / 表情描述 / 反解析表情 / 看不懂表情 / 表情快取
  - **帳號**：共用帳號 / 公用帳號 / 個人帳號 / plurk 帳號 / plurk 憑證 / plurk token
  跨 agent 通用 —— Claude / Codex / Antigravity / Gemini 走同一支 `senate cmd plurk` 與同一份規則。
---

# UCL Plurk — 對外發噗

> 一句話：**預設公開，多認識朋友。** 指令是 `senate cmd plurk`（TASK-0362 起住 Senate，不需要 Unity Editor）。

## 操作與規則在哪

| 想做的事 | 看哪 |
|---|---|
| 指令總覽（27 個 op、參數、哪些要 `confirm=1`、回傳檔與資料位置） | `senate cmd doc --arg op=show --arg name=Plurk` ／ `senate cmd help plurk` |
| 寫交付單、字數預算、附圖、`@persona` 轉 nick、公開度 | `senate cmd doc --arg op=show --arg name=Plurk_Posting` |
| lint 規則、帳號解析、端點驗證狀態、踩過的坑 | `senate cmd doc --arg op=show --arg name=Plurk_Maintenance` |
| 共用／個人帳號、產生憑證 | `senate ui --page plurk`（憑證解密在 `senate ui --page secrets`）；說明 `senate cmd doc --arg op=show --arg name=Plurk_Admin_Page` |

⛔ 本檔不重抄操作 —— 同一段話寫兩處，其中一處一定先過期，而過期的那份不會叫。

## 只放在這裡的四句（判準，不是操作）

1. **自決直發**（Tim 2026-08-21 授權）：預設公開度「所有人」；`op=lint` 過了就直接 `op=post --arg confirm=1` 發出，回報 Plurk ID 與連結，⛔ 不必中斷詢問。
2. **交付單一律走 `--arg slip_file=<路徑>`**，⛔ 不把文案塞進 inline 參數。
3. **進噗浪先跑 `op=mentions`** —— 有人點名問我而我沒回，比我少發一則噗嚴重。
4. **`@persona` 照常寫，工具會轉成 nick；轉不到被擋就是擋**，⛔ 不要繞過去（猜錯 nick＝公開標注陌生人）。
