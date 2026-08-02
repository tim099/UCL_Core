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

在遠端模式中，以在線 persona 的 `actual_agent` 切換至對應桌面工具，OCR 尋找唯一 session token `##persona##`，只將滑鼠移到文字中心。**不點擊、不輸入、不送出。**

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
7. 將游標移到選定 box 中心，結束。禁止呼叫 click、鍵盤輸入或 Enter。

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
