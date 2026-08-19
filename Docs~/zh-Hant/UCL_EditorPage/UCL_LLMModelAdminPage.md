---
title: UCL_LLMModelAdminPage — 本地 LLM 模型管理頁
description: 管理本機大語言模型（ollama）：環境狀態、策展目錄、安裝／解除安裝、試跑驗收。入口在 ToolBox。
source_files: |
  UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_LLMModelAdminPage.cs
  UCL_Core_Scripts/EditorCore/UCL_AgentCommands/LLMAdmin/UCL_LLMAdminRunner.cs
  UCL_Core_Scripts/EditorCore/UCL_AgentCommands/LLMAdmin/UCL_LLMAdminData.cs
  Tools~/AgentCommands/llm_admin.py
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-19
target_audience: [AI_Agent, Tools_Maintainer]
tags: [editor-page, llm, ollama, admin]
---

# UCL_LLMModelAdminPage

**入口**：ToolBox →「本地 LLM 模型」。

管理本機大語言模型：查環境、從策展目錄挑一顆裝／移除、跑一句驗收。
建立動機是**酒保自動發言**需要一顆離線可跑的小模型（純聊天、6GB 顯存以下）。

## 分層（誰負責什麼）

```
UCL_LLMModelAdminPage   薄 UI —— 顯示、選取、二次確認
        ↓
UCL_LLMAdminRunner      async spawn python（走 UCL_ProcessCli，登記 Process 註冊中心）
        ↓
llm_admin.py            唯一真相源 —— 目錄（策展）＋ ollama 的結構化包裝
        ↓
ollama                  真正持有模型：下載、量化、磁碟、載入
```

⇒ **換後端**（llama.cpp server / LM Studio）只改 `llm_admin.py`，Editor 頁一行不動。

## 頁面操作

| 區塊 | 說明 |
|---|---|
| 狀態 | ollama 有沒有裝、版本、服務打不打得到、已安裝幾顆。沒裝時給下載頁按鈕 |
| 模型目錄 | 一顆一格：★＝純聊天推薦、✅＝已安裝、參數量、權重估值、中文評分、備註 |
| 安裝／解除安裝 | 都走 `UCL_OptionPage` 二次確認（會動磁碟，且解除安裝不可逆） |
| 試跑一句 | 用可編輯的提示詞實跑一次，回輸出與耗時 |
| 報告 | 每次操作的完整 stdout（成功與否看 `exit code`，不是看有沒有輸出） |

## 五個刻意的決定

1. **不由本頁啟動 ollama 服務。** 那是常駐 process —— domain reload 會清掉 C# 端的控制權，
   而 OS 層的它不會死（屍潮）。服務沒跑就**只指路**：`ollama serve`。
2. **「已安裝」以 `ollama list` 對帳為準**，不掃磁碟、不信本頁快取。
   磁碟上有檔 ≠ ollama 註冊得到，而兩者不一致時**兩邊都不會報錯**。
3. **安裝完不自動試跑，另給一顆鈕。** `pull` 成功只證明檔案下載完，
   **不證明它在這台機器跑得動**（顯存不夠會退 CPU 或直接失敗）。兩件事分兩顆鈕，帳才分得開。
4. **變體 tag 會標出來。** 目錄寫 `qwen3:4b` 而磁碟上是 `qwen3:4b-instruct-q4_K_M` 時算「已安裝」，
   但頁面會註明那是**同族變體**不是精確 tag —— 不註明的話「已安裝」會誤導。
5. **顯存判準寫在頁面上**：估值是 **Q4 權重**、**不含 KV cache**，而且顯存是跟 Unity 共用的。
   ⇒ 看的是**可用顯存**不是總顯存。

## 目錄怎麼改

改 `llm_admin.py` 的 `CATALOG`（一顆一個 dict：`id` 是 ollama tag、`size_gb` 是 Q4 權重估值、
`zh` 是中文能力 0-5 的策展評分、`recommend` 控制 ★）。**C# 端不維護清單** —— 兩份清單必漂。

## CLI（agent 走同一條路）

```bash
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py status --format json
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py list
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py install   --model qwen3:4b
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py uninstall --model qwen3:4b
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py test      --model qwen3:4b --prompt "..."
```

失敗一律用 **exit code** 說（1＝操作失敗、2＝參數缺）—— 只把錯誤印在 stdout 的話，
呼叫端會把它當成正常輸出。

## ⚠ 對側契約

`llm_admin.py` 的 JSON 欄位 ⇄ `UCL_LLMAdminData.cs` 的 typed model **欄位名逐字對應**
（`JsonConvert` 的 Unity 模式只脫 `m_`，不做 snake_case 轉換）。
⇒ 兩端要一起改；只改一端時 C# **只會讀回預設值**，畫面看起來像「沒有模型」。
唯一例外是 `params`（C# 關鍵字）→ 欄位叫 `params_`，在 `LLMAdminParse.Catalog` 裡手動補一手。
