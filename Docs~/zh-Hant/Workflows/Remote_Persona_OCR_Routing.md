---
title: Remote Persona OCR Routing
description: 定義酒保後台以實際桌面 agent 切換視窗、OCR 尋找 persona token 並只移動游標的遠端協作流程。
last_updated: 2026-08-02
target_audience: [AI_Agent, Developer]
tags: [bartender, ocr, remote]
aliases: [remote window, persona OCR, actual agent]
related:
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_RemoteWindowControl.cs | RemoteWindowControl | Win32 視窗切換與使用者操作護欄
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
5. 僅接受完整、唯一的 `##<persona>##` 命中；0 或多個命中都停止並寫診斷。
6. 將游標移到 OCR box 中心，結束。禁止呼叫 click、鍵盤輸入或 Enter。

## Python 與 process 管理

- OCR Python 必須由 C# runner 啟動，設定 UTF-8 stdout/stderr、timeout 與非阻塞讀取。
- 每個啟動的 PID 必須登錄到 `UCL_ProcessRegistryService`；完成、逾時或例外都要標記結束。
- 延用 `subtitle_ocr.py` 的受限 ONNX thread engine，禁止另建零參數 `RapidOCR()`。

## 護欄

- RemoteWindowControl 預設關閉，且每次 Editor / domain reload 後皆關閉。
- 「偵測使用者操作後暫停」預設開啟；測試時可 runtime 關閉。
- OCR 只接受 token 完整相等，不採 substring 或模糊命中。
- 每次測試記錄 agent、persona、命中數、box、移動座標與失敗原因；不保存完整截圖，除非使用者另行開啟診斷保存。
