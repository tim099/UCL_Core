---
title: Remote Persona OCR Routing
description: 定義酒保後台以實際桌面 agent 切換視窗、OCR 尋找 persona token 並只移動游標的遠端協作流程。
last_updated: 2026-08-02
target_audience: [AI_Agent, Developer]
tags: [bartender, ocr, remote]
aliases: [remote window, persona OCR, actual agent]
related:
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_RemoteWindowControl.cs | RemoteWindowControl | Win32 視窗切換、游標移動與使用者操作護欄
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_RemotePersonaLocator.cs | RemotePersonaLocator | 切視窗→OCR→移游標的 C# 指揮端
  - ucl_core:Tools~/AgentCommands/persona_ocr_locate.py | PersonaOcrLocate | 純判讀端，回 token 螢幕座標
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/AwakenInit/UCL_ActivePersonaLocks.cs | ActivePersonaLocks | 在線 persona 清單（判準是 lock 檔不是 status 欄）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderAdminPage.cs | BartenderAdmin | 後台測試與設定入口
  - ucl_core:Tools~/AgentCommands/screenstream_daemon.py | ScreenStream | 既有 PIL ImageGrab 擷取慣例
  - ucl_core:Tools~/AgentCommands/subtitle_ocr.py | RapidOCR | 既有 OCR engine 與執行緒限制
---

# Remote Persona OCR Routing

## 目標

在遠端模式中，以在線 persona 的 `actual_agent` 切換至對應桌面工具，OCR 尋找 session token `##persona##`，
將滑鼠移到文字中心；視設定可再按左鍵選起該 session、輸入一段文字。**永不送出（不按 Enter）。**

> [!IMPORTANT]
> **原文是「不點擊、不輸入、不送出」三者全禁，2026-08-02 由 Tim 放行前兩者、保留第三者。**
> 這不是把護欄拆了，是把它移到真正該在的位置：**送出**才是不可逆的那一步（訊息一旦發出去就收不回），
> 移游標與打字都還留在「人看得到、可以自己刪掉」的階段。
> 所以 `TryTypeText` 不是「預設不送 Enter」，而是**整支檔沒有送出 Enter 的路徑**，
> 文字裡夾帶的 `\r` / `\n` 也會被主動濾掉 —— 不讓「輸入內容」變成「送出的觸發條件」。

## 資料分工

| 欄位 | 意義 | 不可混用 |
|---|---|---|
| `agent` | persona 顯示歸屬 | 不決定桌面視窗 |
| `bank_account` | 帳務／sender 歸屬 | 不決定桌面視窗 |
| `actual_agent` | `Codex` / `ClaudeCode` / `Antigravity` 的實際桌面承載者 | 不改前兩者 |

## 流程

1. Bartender Admin 從 Active Persona Lock 下拉選在線 persona。
2. 讀 lock 的 `actual_agent`；缺值即停止並要求在 LoginStatusPage 套用。
3. `UCL_RemoteWindowControl` 以 Win32 切換目標視窗；前景驗證失敗即停止。
4. 以 ScreenStream 慣例 `PIL.ImageGrab` 擷取全螢幕，再交給既有 `subtitle_ocr.py` RapidOCR。
5. 比對 `##<persona>##`：名字逐字相等，兩側各要求至少一個分隔字元（`# * + ＃`）。0 命中即停止並寫診斷。
6. 命中恰為 1 個 → 直接用它；命中多個 → 依 `--select` 政策選定，預設 **leftmost（取最靠畫面左側）**。
7. 將游標移到選定 box 中心。
8.（可選，預設關閉）按左鍵選起該 session；再（可選）輸入一段文字。**不送 Enter。**

> [!IMPORTANT]
> **§5/§6 是 2026-08-02 實測後改寫的，原規格的兩條假設都不成立：**
> - 原文要求「token 完整相等」。實測 `##Basecamp##`：OCR 回 `#Basecamp##Bsr`（跟旁邊的 `Bar` 標籤
>   併成同一個 text box、吃掉一個 `#`）與 `+#Basecamp*`（項目符號讀成 `+`、結尾 `##` 讀成 `*`）。
>   要求整塊完整相等 → 恆 0 命中，功能等於不存在。
> - 原文要求「唯一命中」。**同一個 session 在畫面上本來就會出現兩次**：上方標題列一次、側邊 session
>   清單一次；對話內容提到自己名字還會再多幾次。把多重命中當異常 → 恆停止。
>   多重命中是常態，要處理不是要拒絕（Tim 2026-08-02 拍板：**取最左**，因為 session 清單貼在視窗左緣，
>   標題列與對話區都在它右邊；實測側邊 x=407 / 標題列 x=971）。

## 掃描範圍與重試（2026-08-02 實測後加入）

| 參數 | 意義 | 為什麼需要 |
|---|---|---|
| `--monitor` | `all` / `primary` / 實體 index | 雙螢幕下掃全桌面 = 掃 6400×2160，慢且不必要 |
| `--region x,y,w,h` | **矩形**範圍，0~1 比例，相對選定螢幕 | session 清單固定在視窗左側；這是 rect 不是字幕帶的橫帶 |
| `--initial-delay` | 第一次擷取前等幾秒 | 視窗剛被帶到前景時還沒重繪完，第一張常常是舊畫面 |
| `--attempts` / `--attempt-delay` | 重擷取＋重 OCR 的次數與間隔 | 同 process 內重試，模型只載入一次，每次邊際成本 ~0.3-1s |
| `--select` | `leftmost`（預設）/ `topmost` / `strict` | 多重命中是常態；strict 保留給「我要親眼看候選」的除錯場 |
| `--index N` | 明示指定第幾個（0-based，順序由上到下） | 蓋過 `--select`；後台候選清單點選即帶入 |

後台設定（螢幕／矩形／延遲／重試／政策／上次測試 persona）按「💾 保存設定」寫入
`<bartender>/remote_persona_locate_config.json`，開頁自動讀回。**明示按鈕才寫檔** ——
設定調到一半自動存，等於把試錯過程也存成「決定」。

> [!TIP]
> **限制範圍不只是省時間，是直接提升辨識率。** 同一塊畫面：掃全桌面（6400×2160）OCR 讀成
> `#Basecamp##Bsr` 與 `+#Basecamp*`；只掃左側 1/3（1305×2160）讀成 `##Basecamp##Bar` 與
> **`##Basecamp##`（完全正確）**。RapidOCR 會把輸入縮放後推論，圖越大小字被壓得越糊。

預覽：`--preview <path>` 只擷取不跑 OCR（不載模型，秒級回應），後台拿它當 rect 調整的底圖。
底圖一律是**整塊螢幕**而非 rect 內容 —— 拿裁好的圖去調裁切範圍，永遠看不到自己漏掉了什麼。

> [!WARNING]
> **DPI 宣告必須早於任何螢幕座標查詢。** 本檔實作時踩過：monitor 列舉先跑、`SetProcessDpiAwareness`
> 後跑 → 列舉拿到虛擬化座標（2560 寬回報成 1707），擷取卻是實體像素，拼出來的 bbox 是歪的，
> 而且看起來很像「螢幕真的只有那麼大」。現在宣告放在 module 載入時（`DPI_AWARE`）。

## Per-agent 輸入前置（`UCL_RemoteAgentInput`）

「點完 session 之後，焦點會不會自己落到輸入框」是**各 app 各自的行為，不是通則**。所以最後一段做成
per-agent 的表，加新桌面工具＝加一個 case，其餘流程不動。

| actual_agent | 前置動作 | 依據 |
|---|---|---|
| `Codex`（ChatGPT 桌面版） | 無 | Tim 2026-08-02 實測會自動 focus |
| `ClaudeCode` | 無 | Tim 2026-08-02 實測會自動 focus |
| `Antigravity` | OCR 找輸入框 placeholder「Ask anything」→ 點它 → 等 `FocusDelaySec` | 無 Auto Focus；**`Ctrl+L` 提案經 Tim 2026-08-02 實測無效已放棄** |
| 表內查不到 | 無（維持舊行為） | 漏加設定不該讓整條線壞掉 |

三種模式：`None`（自動 focus）/ `Hotkey`（送快捷鍵）/ `LocatePlaceholder`（找輸入框自己的提示文字再點）。

> [!TIP]
> **為什麼最後選 LocatePlaceholder 而不是 Hotkey**：placeholder 是輸入框自己畫出來的字，找到它＝找到輸入框，
> 而且**失敗會有畫面證據**（near-miss 留在結果裡）。快捷鍵送出成功卻沒生效是**靜默**的 ——
> 同一天我們已經被「`SendInput` 回 true 但 app 沒反應」騙過一次，不必再被騙第二次。
> 選 `bottommost`：對話區也可能出現同一段文字（例如有人把它貼進訊息），而輸入框永遠在視窗最下面。

> [!WARNING]
> **聚焦快捷鍵不可做成全域預設。** `Ctrl+L` 在不同 app 語意差很多（終端機系＝清畫面、瀏覽器系＝跳網址列），
> 猜錯就是把人家的畫面清掉。每一條都必須是**實測**填進去的 —— Antigravity 這條就是實測後被推翻的。

比對方式（`--match`）：`delimiter`（`##name##` 用，兩側要有分隔符，防聊天內容誤中）/
`contains`（找 UI 固定文字用，包含即可 —— UI 文字沒有分隔符可依，且常被 OCR 斷成半句）。

輸入一律走 `SendInput` 的 `KEYEVENTF_UNICODE` 逐字送，**不用剪貼簿** —— `pyperclip.copy()` 這類做法會
覆蓋使用者當下的剪貼簿內容，那是個安靜的副作用，出事時沒人會聯想到是酒保幹的。

> [!NOTE]
> **通知文字預設 `/ucl-ding`，不要改成「叮」。** Tim 手動戳打「叮」、酒保自動戳送 `/ucl-ding`
> （Tim 2026-08-02 定的慣例）—— 兩邊用同一個字，收到的人就分不出這次是人在叫還是機器在叫。

## 自動通知（酒保 ding）

定期掃在線 persona 的收信匣，挑一個去戳。**這是全系統唯一會按 Enter 的流程** —— 它的目的就是替使用者送出。

| 步驟 | 規則 |
|---|---|
| 掃描 | 每 `interval`（預設 30s）；只掃**在線** persona（有未過期 lock），只讀 `rooms/*/inbox/<persona>.md`，不讀 `_archive`（歸檔＝看過了） |
| 入池 | 該 persona 有 `seq > last_notified_seq` 的條目才入池（否則同一批 @ 會每 30 秒重戳一次同一個人） |
| 權重 | `新 @ 次數 × 10`（Tim 2026-08-02 給的尺：2 次→20、1 次→10） |
| 平手 | 比 `last_notified_at`，**越舊越優先**；從未被通知者排最前 |
| 執行 | 每輪只通知**一個**人：切視窗 → OCR 定位 → 移游標 → 點擊 → 輸入 → 送出 |
| 記帳 | **只有真的走完才推進 `last_notified_seq`** —— 失敗也推進的話那批 @ 永遠不會再被通知，而且失敗是靜默的 |

護欄：
- 自動流程走 `TryActivate`（**遵守「使用者剛動過鍵鼠就不搶焦點」**），手動按鈕才走 `TryActivateExplicitly`。
- 點擊前、輸入前、**送出前**各驗一次前景視窗仍是目標；任何一次不符即中止。
  送出前那次特別重要 —— 那是唯一一個「錯了就收不回」的動作。
- `UCL_RemoteWindowControl.Enabled` 每次 domain reload 必回關閉，所以重開 Editor 後自動通知不會自己動起來。
- 後台的通知池顯示要掃所有房間的 inbox 檔，**節流成每 2 秒一次** —— OnGUI 每次重繪都掃會拖著整個 Editor 做磁碟 IO。

## Python 與 process 管理

- OCR Python 必須由 C# runner 啟動，設定 UTF-8 stdout/stderr、timeout 與非阻塞讀取。
- 每個啟動的 PID 必須登錄到 `UCL_ProcessRegistryService`；完成、逾時或例外都要標記結束。
- 延用 `subtitle_ocr.py` 的受限 ONNX thread engine，禁止另建零參數 `RapidOCR()`。

## 護欄

- RemoteWindowControl 預設關閉，且每次 Editor / domain reload 後皆關閉。
- 「偵測使用者操作後暫停」預設開啟；測試時可 runtime 關閉。
- 名字本身逐字相等，不做編輯距離／不接受沒有分隔符包夾的裸命中（聊天裡提到 persona 名不會命中）。
- 多重命中不自動挑選；候選順序固定為「由上到下、再由左到右」，讓 index 在同一畫面下可重現。
- 每次測試記錄 agent、persona、命中數、box、移動座標與失敗原因；不保存完整截圖，除非使用者另行開啟診斷保存。
- 點擊與輸入預設關閉；**每一步之前都重新確認前景視窗仍是剛切過去的那個**，焦點被搶走即中止 ——
  不做這個檢查，最壞情況是把文字打進別人的聊天框並留下痕跡。
- 送出（Enter）不提供、不做成選項。要送出永遠是人自己按。
