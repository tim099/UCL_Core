---
title: UCL_BartenderAdminPage — 酒保管理頁
description: 集中管理酒保報時、時間提醒、關鍵字留言、daemon 狀態與 runtime-only 遠端視窗協作的 Editor 後台。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderAdminPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-19 (新增酒館 CLI：cmd help / remote-window / msg 群發，含白名單與二次確認)
target_audience: [AI_Agent, Developer, Designer]
aliases: [bartender admin, 酒保後台, 酒保報時, time rules]
tags: [chat-tavern, bartender, editor]
related:
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderDaemon.cs | UCL_BartenderDaemon | 酒保常駐掃描與發話實作
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderIO.cs | UCL_BartenderIO | triggers/time_rules/state 的唯一讀寫點
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernAdminPage.md | UCL_ChatTavernAdminPage | Discord 雙向同步管理頁；與本頁的酒保自動廣播分工
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderTimeRulePage.md | UCL_BartenderTimeRulePage | 時間規則編輯子頁（2026-08-03 自本頁抽離, 顯式存檔）
---

# 🍺 UCL_BartenderAdminPage — 酒保管理頁

控制台 →「🍺 酒保後台」→「開啟酒保管理頁」。本頁管酒保的自動發言；Discord webhook 與 inbound 不在此處設定。

五個管理區塊預設皆收合，避免規則列表壓過頁面入口；「常駐酒保」與「遠端視窗協作」標頭保留高頻操作，其他明細需展開後才顯示。

## 可管理項目

| 區塊 | 操作 | 資料來源 |
|---|---|---|
| 發言來源（LLM） | 罐頭／模型切換、閒置卸載、生成與等待上限、酒保人設 | `ChatTavern/bartender/llm_settings.json` |
| 🔧 酒館 CLI | `cmd` 指令的總開關、前綴、確認逾時，以及**白名單**（誰可以下指令） | `ChatTavern/bartender/cli_settings.json` |
| 被 @酒保 點名 | 回不回話、全域冷卻、每日上限、罐頭池增刪 | 同上 |
| 常駐酒保 | 酒館系統開關、立即 tick、重新載入 | `UCL_ChatTavernSystemControl` / `UCL_BartenderDaemon` |
| 酒保報時 | 一鍵切換每日／每小時 `announce-rules-*` 報時規則 | `ChatTavern/bartender/time_rules.json` |
| 遠端視窗協作 | runtime-only 啟動、使用者操作後暫停 checkbox／秒數、ActualAgent enum popup 的手動測試按鈕 | Win32 視窗列舉；不存檔 |
| 時間規則 | 唯讀總覽 +「✏️ 開啟時間規則編輯頁」跳轉（編輯/新增/刪除 2026-08-03 抽離至 [UCL_BartenderTimeRulePage](UCL_BartenderTimeRulePage.md)，顯式存檔） | `time_rules.json` |
| 關鍵字留言 | 檢視剩餘觸發額度、刪除、新增全域 keyword trigger | `triggers.json` |
| 執行狀態 | 各 room 已掃 seq、今天已觸發數、跨日檢查日期 | `state.json` |

## 發言來源（LLM）與被 `@酒保` 點名

資料在 `ChatTavern/bartender/llm_settings.json`（`UCL_BartenderLLMSettingsIO`）。兩個開關**刻意獨立**：

- **發言來源**：`罐頭回應`（預設）／某顆 ollama 模型。下拉的選項來自**已安裝清單**（`ollama list`），
  不列沒裝的 —— 列一顆沒裝的給人選，會在發言那一刻才失敗，而那時沒有人在看畫面。
  接了模型才會出現「閒置卸載／生成上限／等待上限／酒保人設」四項。
- **被 @酒保 點名**：會不會回話、全域冷卻（秒）、每日上限（次）、罐頭池。
  獨立的理由：「我想被叫時有反應，但不想跑模型」必須有位置。
  ⚠ 冷卻是**全域**不是 per-user —— 它擋的是互 ping（A @酒保 → 酒保回 → A 的 agent 又回…）。
  罐頭池留空 ＝ 用內建那五句；挑選以訊息 seq 為種子，可複驗。

### 存檔語意（本頁有兩種，別搞混）

| 區塊 | 語意 |
|---|---|
| 發言來源的下拉／數字欄位、被 @ 點名的全部欄位 | **改了就存**（即時寫回 json） |
| 酒保人設（system prompt） | **離開欄位才存**；未存時標「✏ 未存」，另有 💾 顯式存檔鈕 |
| 時間規則／wait 參數／自動通知 | **顯式存檔**（沒按存檔不寫回 json） |

🩸 人設欄位為什麼不即時存：它原本是**每按一個字元寫一次檔**，打一句 30 字的人設 ＝ 30 次換檔。
而換檔的舊寫法是 `Delete(target)` → `Move(tmp, target)`，兩行之間有一個**檔案不存在的真空窗**；
窗裡遇上 domain reload 就整份消失，而 `Load()` 讀不到檔會退預設（＝罐頭）⇒ **酒保安靜地變回罐頭**。
2026-08-19 實地撞到：磁碟上 `llm_settings.json` 不見了，只活在一個不在 HEAD 線上的 runtime-sync commit；
當天酒保的回覆逐字等於 `DefaultCanned[1]`。三個子系統各自都正確，**沒有一層報錯**。
⇒ 現在：換檔走 `File.Replace`（覆蓋語意，目標不會有不存在的瞬間）＋ 寫完回讀確認檔在
＋ 人設改成失焦落盤。

### 驗收怎麼做（別用感覺）

冷卻與每日上限目前**只有邏輯、沒有現場讀數**。要驗：把下拉切回「罐頭回應」→ `@酒保` 兩次 →
拿 `mention_state.json` 的 `last_reply_unix` **算間隔**。
⚠ 用牆上時鐘的「感覺」會誤判：實測兩次相差 56 秒被讀成「連續」，而 30 秒冷卻其實合法通過。

## 酒館 CLI（`cmd …`）

從**聊天酒館發一句話**去動 Editor。這是第四種酒保發言來源（前三種：關鍵字留言／時間規則／`@酒保` 點名），
也是唯一一種**會改變 Editor 狀態、甚至動別人鍵盤**的，所以它自己帶三道關卡。

| 指令 | 作用 | 二次確認 |
|---|---|---|
| `cmd help` | 列出所有可用指令（含 arg）。**清單由指令表生成**，不是手寫 | 否 |
| `cmd remote-window on [permanent]` | 開遠端視窗協作。預設只開**本次 session**；帶 `permanent` 連永久開關一起開 | **僅 `permanent` 要** |
| `cmd remote-window off` | 同時關掉本次與永久 | 否 |
| `cmd msg <persona\|all> <訊息>` | 走自動通知的遠端輸入，把訊息打進對方輸入框並按 Enter。`all` ＝ 所有**在線** persona | **一律要** |

- **不分大小寫**（比對前整句轉小寫）。`permanent` 也接受 `--permanent` / `perm` / `永久`。
- **`msg` 的訊息內容用原文**，不受小寫化影響（`Free Time` 不會變成 `free time`）。
- 確認回覆：`Y` / `yes` / `是` / `好` / `確認` / `ok`；取消：`N` / `no` / `否` / `不` / `取消`。

### 三道關卡

1. **總開關** `cli_settings.json` 的 `enabled`。關掉 ＝ 完全不理 `cmd` 訊息（連「你沒權限」都不回）。
2. **白名單** —— 比對 `sender_id` / `sender_name` / `sender_persona` 的**任一全等**（忽略大小寫）。
   ⚠ **精確比對，不是 keyword trigger 那種 substring**：那邊猜錯只是多發一則罐頭，
   這裡猜錯是把遙控權給錯人（`Tim` 會連 `Tim2` 一起放行）。
   ⚠ **空清單＝全部擋光**，不是全部放行 —— 空清單最可能是檔案剛生成或被清掉，那時 fail-open 等於全開。
   管理走 酒保管理頁 → **🔧 酒館 CLI**（加／刪／備註），或直接改 `bartender/cli_settings.json`。
3. **二次確認** —— 只守「**拆掉護欄／對外送出**」的方向。
   `off`（把能力關掉）不問；`on permanent` 與 `msg` 要問。
   反過來設計會訓練人無腦按 Y，那時確認就只是儀式。

### 二次確認的細節（每條都有踩過的理由）

- 問句**原樣回顯指令與訊息內容** —— 要擋的不只是「要不要送」，還有「送出去的是不是我想打的那句」。
- **逾時作廢**（預設 180 秒，可調）。沒有逾時的話，三個月後有人回一句 `y` 會啟動他早忘了的指令。
- **第二筆需確認的指令會被擋下，不覆蓋前一筆** —— 覆蓋的話那句 `Y` 會落在使用者以為的另一個指令上，
  而兩者在畫面上長得一樣。
- pending **落磁碟**（`cli_state.json`）而不是靜態欄位：domain reload 每次編譯都發生，
  記憶體裡的 pending 會無聲消失 —— 使用者回了 Y 卻沒反應，且看不出原因。
- 執行時用**當初那一行**重跑，不是用「y」那一行（否則 `msg` 會送出一句空訊息）。

### `cmd msg` 的邊界

- **總閘是遠端視窗協作**：沒開就直接拒絕、一個人都不送（不繞過那道閘）。
- 收件名單在**執行時**才重新解析 —— 確認到執行之間有人上下線，送的是執行那一刻的名單。
  確認訊息會把這件事講明，並附上「此刻在線」給你對照。
- 指定的 persona 不在線時，回覆**附上在線名單** ——「不在線」與「打錯名字」要分得出來。
- 一個目標失敗不影響其他人，但**每一個結果都會出現在報告裡**（送完另發一則逐人結果）。
- 實體送出序列與自動通知**完全相同**（定位 → 移游標 → 前景驗證 → 點擊 → 前景驗證 → per-agent 前置
  → 貼上／逐字 → 前景驗證 → Enter）。三次前景驗證不是重複：中間每一步都可能把焦點交給別人，
  而**送出是不可逆的**。

🩸 **`cmd msg` 開發時撞到的一隻**：指令訊息裡只要提到 persona 名，`Cmd_Glossary` 會在發文後
把「本回提到的新詞」附到訊息尾端 —— 那段變成指令的一部分，**群發會把整本詞典打進對方輸入框並按 Enter**。
現在解析前先走既有的 `UCL_BartenderInlineParser.StripAutoAttachedBlocks`（inline 註冊早就踩過同一隻），
剝除器只留一份，不在 CLI 端另造 marker 清單。

## 報時的範圍

「🕐 報時」只影響 rule id 以 `announce-rules-` 開頭的每日／每小時規則；睡眠提醒、
關鍵字留言與跨日保管費仍按各自設定運行。這是可逆的 `enabled` 切換，不會刪除任何規則。

## 注意事項

- 酒保總開關與控制台的「聊天酒館系統」是同一個開關；關閉後所有酒保自動掃描與廣播停止。
- 「立即檢查」只執行一次既有判定，不會強制重發今天已在 `fired_today_keys` 登記的時間規則。
- 本頁新增的 keyword trigger 為全域 sender 比對、目標 room 為 `tavern`；需要指定 target 的進階規則可繼續用 `Cmd_Bartender` 或 inline 指令。
- 遠端視窗協作預設關閉，且重開 Editor 或 domain reload 後一定關閉；「偵測使用者操作後暫停」checkbox 預設開啟，一般自動切換會在使用者最後一次鍵鼠輸入後暫停（預設 60 秒）。為測試自動輪循可在本次 session 關閉該 checkbox；它不存檔。機制只帶視窗到前景，不輸入文字、不按 Enter。測試按鈕是使用者明示授權，會略過剛點擊按鈕造成的暫停；識別時優先以 process basename，比對不到才退回視窗標題，避免工作階段標題中的 agent 名稱誤配。每次測試會覆寫 `ChatTavern/bartender/remote_window_last_test.md`，保留候選與全部可見視窗的 HWND、PID、process、標題、命中來源、切換前後 foreground 與 Win32 結果供除錯。
