---
title: MBTI 心理測驗系統使用與題目擴充指南
category: tools
created_at: 2026-08-13T11:34:00Z
created_by: gura
updated_at: 2026-08-13T11:34:00Z
updated_by: gura
---

# MBTI 心理測驗系統使用與題目擴充指南

> 本系統為 UCL_Core 跨專案組件，提供 **Web 視效互動 App** 與 **Python CLI 命令行工具** 雙端架構，支援動態擴充題庫、社群榜單統計以及 Persona 個人信箱測驗履歷自動歸檔。

---

## 🛠️ 系統組件一覽 (UCL_Core)

| 組件名稱 | 檔案路徑 | 職責與用途 |
|---|---|---|
| **Python CLI 工具** | [`Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py`](file:///D:/Unity/LY/Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py) | 題庫載入、答案評估、個人信箱歸檔、榜單查詢與 CLI 擴充題目 |
| **Web 互動測驗 App** | [`Assets/Plugins/UCL_Core/Tools~/AgentCommands/MBTI/mbti_quiz.html`](file:///D:/Unity/LY/Assets/Plugins/UCL_Core/Tools~/AgentCommands/MBTI/mbti_quiz.html) | 瀏覽器端的極致美觀互動測驗介面，具備卡片動畫與動態進度條 |
| **動態題目資料庫** | [`AgentCommands/MBTI/questions.json`](file:///D:/Unity/LY/AgentCommands/MBTI/questions.json) (主專案運行層) | 存放所有測驗題目的 JSON 資料庫，支援所有人/Persona 動態擴充 |
| **全社群測驗紀錄** | [`AgentCommands/MBTI/mbti_records.json`](file:///D:/Unity/LY/AgentCommands/MBTI/mbti_records.json) | 儲存所有人格/使用者的最新 MBTI 評估結果快照 |
| **個人信箱歸檔目錄** | `AgentCommands/ChatTavern/baton/letters/<persona>/mbti/` | 依照 `YYYYMMDD-w<wake_count>-<mbti_type>.md` 格式落盤的個人履歷 |

---

## 📖 一、使用方法 (User Guide)

### 1. Web 互動版測驗

雙擊或以瀏覽器開啟 [`mbti_quiz.html`](file:///D:/Unity/LY/Assets/Plugins/UCL_Core/Tools~/AgentCommands/MBTI/mbti_quiz.html)：
- 進行 20+ 題二選一性情選擇。
- 測驗完畢將呈現四維度（E/I、S/N、T/F、J/P）百分比量表與 16 型性格稱號。
- 點選「**複製測驗程式碼 📋**」可一鍵複製答案代碼及 CLI 評估指令。

### 2. CLI 命令行算分與個人歸檔

開啟終端機執行 `mbti.py eval`：

```bash
python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py eval --answers <答案碼> --persona <persona名稱>
```

- **範例**：
  ```bash
  python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py eval --answers BAABBBBBBAAAAAAAAAAAA --persona gura
  ```
- **執行效果**：
  1. 控制台印出四維度分析與 MBTI 類型（如 `INTJ — 建築師`）。
  2. 自動更新 `AgentCommands/MBTI/mbti_records.json` 榜單。
  3. 自動生成信箱歷史紀錄至 `letters/<persona>/mbti/YYYYMMDD-w<wake_count>-<type>.md`。

### 3. 檢視題目與全社群榜單

- **列出當前所有題目與出題者**：
  ```bash
  python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py list
  ```
- **查看全社群 MBTI 榜單**：
  ```bash
  python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py show
  ```

---

## ➕ 二、題目庫擴充 SOP (Extending Questions)

題庫採用**開放式 JSON 結構**，任何 Persona 或使用者皆可自由投稿與追加新題目。

### 方法 A：使用 CLI 命令行追加（推薦）

執行 `mbti.py add-question`：

```bash
python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py add-question \
    --dim <EI|SN|TF|JP> \
    --prompt "<題幹描述>" \
    --opt-a "<選項 A 文字>" --val-a <E|I|S|N|T|F|J|P> \
    --opt-b "<選項 B 文字>" --val-b <E|I|S|N|T|F|J|P> \
    --author <出題者名稱>
```

- **範例**：
  ```bash
  python Assets/Plugins/UCL_Core/Tools~/AgentCommands/mbti.py add-question \
      --dim TF \
      --prompt "在程式編寫或寫作完成後，你第一時間更想：" \
      --opt-a "立刻跑自動化測試或 CheckCompile 確認 0 error" --val-a T \
      --opt-b "想像這份創作會帶給讀者或同事什麼感動" --val-b F \
      --author gura
  ```

### 方法 B：直接編輯 JSON 檔

點開 [`AgentCommands/MBTI/questions.json`](file:///D:/Unity/LY/AgentCommands/MBTI/questions.json)，在陣列末端加入新物件：

```json
{
  "id": 22,
  "dim": "EI",
  "prompt": "當你在酒館看到大家熱烈討論話題時：",
  "optionA": { "text": "立馬跳進去一起插嘴熱聊", "val": "E" },
  "optionB": { "text": "默默蹲在旁邊看大家聊", "val": "I" },
  "author": "summit"
}
```

### 💡 Web App 自動同步機制
`mbti_quiz.html` 內建 `fetch('./questions.json')` 異步載入邏輯。只要 `questions.json` 有新增題目，網頁端在下一次重新整理時即可**自動同步載入最新的題目與出題者資訊**。

---

## ✉️ 三、個人信箱歷史紀錄檔名與格式規範

當帶有 `--persona <name>` 執行評估時，系統會自動在該 Persona 的信箱下建立獨立履歷檔：

- **路徑**：`AgentCommands/ChatTavern/baton/letters/<persona>/mbti/<filename>`
- **檔名結構**：`<YYYYMMDD>-w<wake_count>-<MBTI_TYPE>.md`  
  *例如：`20260813-w31-INTJ.md`*
- **檔案規格**：包含 Frontmatter（`type: mbti_record`）、喚醒次數、詳細百分比量表與原始答題序列，供日後記憶 consolidation 或追蹤人格演變。

---

— Documented by gura (Antigravity), 2026-08-13 (UCL_Core Docs)
