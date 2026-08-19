---
title: UCL_LLMModelAdminPage — 本地 LLM 模型管理頁
description: 管理本機大語言模型（ollama）：環境狀態、策展目錄、安裝／解除安裝、試跑驗收。入口在 ToolBox。
source_files: |
  UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_LLMModelAdminPage.cs
  UCL_Core_Scripts/EditorCore/UCL_AgentCommands/LLMAdmin/UCL_LLMAdminRunner.cs
  UCL_Core_Scripts/EditorCore/UCL_AgentCommands/LLMAdmin/UCL_LLMAdminData.cs
  Tools~/AgentCommands/llm_admin.py
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-19 (顯存門檻改為 nvidia-smi 自動偵測 + 手動覆寫；試跑參數持久化)
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
| 顯存門檻 | 偵測讀數（卡名／total／已用／可用）、門檻來源，以及**手動覆寫欄位** |
| 試跑一句 | 用可編輯的提示詞實跑一次，回輸出與耗時（參數會存，活過 domain reload） |
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
5. **顯存門檻真的去讀卡**（`nvidia-smi`），預設拿 **可用(free)** 而非總量，且**來源印在數字旁**；
   讀不到就明講是保底值，並可手動填。詳見下面「顯存門檻」一節。

## 顯存門檻（自動偵測 ＋ 手動覆寫）

門檻決定「模型目錄預設列哪幾顆」。它**不是常數** —— 有三個來源，優先序 **手動 > 偵測 > 保底**：

| 來源 | 什麼時候 | 畫面上的標籤 |
|---|---|---|
| `manual` | 勾了「手動指定」且填 > 0 | 手動指定 |
| `gpu_free` | 自動、判準選 free（預設） | 偵測·可用(free) |
| `gpu_total` | 自動、判準選 total | 偵測·總量(total) |
| `fallback` | `nvidia-smi` 讀不到 | ⚠ 保底值（沒量到） |

偵測走 `nvidia-smi --query-gpu=name,memory.total,memory.free,memory.used`，
找法比照 ollama：PATH 優先，找不到再查 `%SYSTEMROOT%\System32` 與 NVSMI 安裝位置
（**驅動剛裝完時 Editor 這個行程的 PATH 是舊的**，症狀跟「這台沒有 NVIDIA 卡」一模一樣）。

⚠ 幾條踩過的判準：

- **來源永遠印在數字旁邊。** 只印數字的話，「量到你可用 8.2GB」與「我沒量到，隨便給你 6.0」
  在畫面上同形。偵測失敗時頁面會出 warning，不會靜默用保底值。
- **`free` 是會變動的量** —— Unity 開了什麼、有沒有模型還駐留在顯存都會改它
  （實測同一台機器 20:44 是 4.93GB、模型卸載後 20:47 是 8.19GB）。所以偵測值旁邊一律附 `used`／`total`。
- **手動填 ≤ 0 時仍走自動偵測。** 傳 0 過去會把整份目錄濾空，而畫面「全被濾掉就自動放行」，
  看起來完全正常 —— 那是最難查的一種。
- 門檻只影響**列不列**，不影響能不能安裝。放不下時 ollama **不報錯**，只把層數丟給 CPU。

🩸 **為什麼會有這一節**：這條門檻原本寫死 `VRAM_BUDGET_GB = 6.0`，
而同一支檔案上一行的註解自己寫著「判準永遠是 nvidia-smi 的 free 欄」——
**註解寫了一條紀律，實作從沒執行過它**；UI 那行字又寫「只列這張卡放得下的」。
於是一台 12GB 的 4080 Laptop 被當成 6GB 卡，`qwen3:8b`（中文 5/5）預設藏起來不出現。
（Tim 2026-08-19 問「這個預算是真的去讀 GPU 還是寫死」）

## 試跑參數會存起來

六個參數（提示詞／人設 prompt／顯示思考／生成上限／等待上限／閒置卸載）＋顯存門檻設定
存在 `UCL_ProjectEditorPrefs` 的 `UCL_LLMModelAdmin.Settings`，**改了就存**（不必按鈕）。

🩸 存起來的意義不是省打字：這些值原本是裸欄位，domain reload 或關頁就回硬編預設，
而「回到硬編預設」跟「我沒改過」在畫面上長得一樣 ⇒ 每次都退回一組**沒驗過**的值。
（初版預設上限 300 且不傳 `--timeout`，4b 在 python 端 60s 逾時 ⇒ 畫面「什麼都跑不出來」。）
⇒ 真正要存的是「**上次驗過會過的那一組**」。

## 目錄怎麼改

改 `llm_admin.py` 的 `CATALOG`（一顆一個 dict：`id` 是 ollama tag、`size_gb` 是 Q4 權重估值、
`zh` 是中文能力 0-5 的策展評分、`recommend` 控制 ★）。**C# 端不維護清單** —— 兩份清單必漂。

## CLI（agent 走同一條路）

```bash
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py status --format json
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py list
# 顯存門檻：不給 ＝ 自動偵測；--vram-basis 選 free(預設)/total；--vram-budget 手動覆寫
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py list --vram-basis total
python <UCL_Core>/Tools~/AgentCommands/llm_admin.py list --vram-budget 10.5
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
