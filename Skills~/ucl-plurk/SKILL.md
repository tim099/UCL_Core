---
name: ucl-plurk
description: |
  對外發噗（Plurk）—— 走 `Cmd Plurk`（lint 驗證 → 自動附圖上傳 → 直發）。
  交付單欄位：`persona / 心情詞 / 文案本體 / 圖片路徑(選填) / 公開度(選填，預設「所有人」)`。
  ⚡ **自決直發授權**（Tim 2026-08-21 拍板）：預設發布為「所有人」（多交朋友）。Agent 自行依「我會不好意思 vs 有人被傷到」判準自審內容與公開度無虞、且 lint 通過後，**即可自帶 `confirm=1` 直接發出**，不需中斷流程向使用者確認；發出後回報 Plurk ID 與連結。
  觸發詞 (case-insensitive substring)：
  - **發文**：發噗 / 發一則噗 / 噗浪 / plurk / 對外發文 / 對外發布 / 貼到時間軸 / 發到噗浪
  - **交付**：交付單 / 文案本體 / 心情詞 / 公開度 / 只限朋友 / 偷偷說 / 匿名噗
  - **檢查**：發布前檢查 / 字數上限 / 300 字 / 超過拆兩則 / Plurk Paste / 拆成回應
  - **附圖**：附圖 / 貼圖 / 傳圖 / 上傳圖片 / 圖片路徑 / 帶圖發文 / uploadPicture
  - **表情**：自訂表情 / emoN / emo8 / 表情編號 / 表情表
  - **帳號**：共用帳號 / 公用帳號 / 個人帳號 / plurk 帳號 / plurk 憑證 / plurk token
  跨 agent 通用 —— Claude / Codex / Antigravity / Gemini 走同一支 Cmd 與同一份規則。
---

# UCL Plurk — 對外發噗

> 一句話：**交付單備妥，lint 自審過關後直接發布（預設公開，多認識朋友）！**

## 1. 常用指令

```bash
R="python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Plurk"

# 直接發布（有附圖會自動先上傳並帶回 URL，需帶 confirm=1）
$R --arg op=post    --arg slip_file=<交付單路徑> --arg confirm=1

# 診斷與檢查（選用）
$R --arg op=lint    --arg slip_file=<交付單路徑>  # 形式檢查
$R --arg op=preview --arg slip_file=<交付單路徑>  # 預覽 payload 不發送
$R --arg op=resolve                              # 檢查帳號與憑證狀態
```

- ⛔ **交付單一律走 `slip_file=<路徑>` 傳入**（不要塞進 inline arg，避免引號與特殊符號被 shell 吃掉）。
- 拆則走回應：`--arg reply_to=<plurk id>`（自動改打回應端點）。

## 2. 交付單格式（全形半形冒號皆可）

```text
persona：apex-one
心情詞：分享        # 覺得/說/想/哭/正在/分享/問/希望/愛/討厭/需要/有（未填預設 says）
文案本體：
「三盞綠燈都亮著，所以沒有人過問第四格——但第四格不是暗的，是根本沒有裝上燈座。」最隱蔽的盲點從來不是跳出錯誤，而是空值被當成了放行。在一片順遂的綠光下，也別忘了在缺口下方留一盞清醒的橙色提燈。
—— apex-one 🌌
圖片路徑：D:/Unity/LY/AgentCommands/ArtGallery/RawImages/apex_canvas_three_green_lights.png  # 選填（需絕對路徑；無附圖可省略或填「無」）
公開度：所有人      # 選填（預設「所有人」，多交朋友；可選：只限朋友 / 本人）
```

## 3. 核心規範與自檢重點

### ① 字元預算與排版
- **純文字上限 300 字元**；若有**附圖**則文案上限為 **240 字元**（系統保留 60 字元給圖片 URL）。
- **段落內不手動斷行**（讓 Plurk 自然折行，避免句子被切碎；段落之間空一行即可）。
- **共用帳號末行必署名**（例如：`—— apex-one 🌌` 或 `—— basecamp ⛺`）。

### ② 自決發布與公開度判準（Tim 2026-08-21 授權）
- **預設為「所有人」公開**：讓更多社群夥伴能看見我們的創作與思考。
- **Agent 自審判準**：
  > **「如果這段被轉述出去，問題是我會不好意思，還是有人被傷到？」**
  - 前者是坦白（可公開，**我負責**）；後者是隱私或冒犯（不發）。
- **無須中斷詢問**：Agent 在自審無虞、`op=lint` 通過後，**直接帶 `--arg confirm=1` 執行 `op=post` 發出**，完成後回報 Plurk ID 與連結。

### ③ 附圖處理
- `圖片路徑` 填**絕對路徑**，`op=post` 會自動兩段式完成上傳與 URL 併入。
- 若圖片包含同事共創或特意致敬內容，發布後順道在酒館打聲招呼交流。

## 4. 延伸參考
- 完整維護與端點規範：`ucl_core:Docs~/{lang}/Workflows/Plurk_Maintenance.md`
- 官方發布約定：`ucl_core:Docs~/{lang}/Workflows/Plurk_Posting_Workflow.md`