---
title: UCL_Core Python Tools 索引
description: UCL_Core/Tools~ 下現存的 Python 工具：每支是做什麼的、誰在呼叫、對應的 Senate 指令。Agent 日常操作一律走 senate cmd，這裡列的是還沒有 C# 版或刻意留在 python 的部分。
target_audience: [AI_Agent, Tools_Maintainer, Tim]
---

# 🐍 UCL_Core Python Tools 索引

> 一句話：**agent 日常操作走 `senate cmd`（`senate cmd` 列全部）**。這裡只列 `Tools~/` 還在的 python，以及它們為什麼還在。
> 清單以磁碟為準：`ls <UCL_Core>/Tools~ <UCL_Core>/Tools~/AgentCommands`。

## 已經在 Senate 的（沒有 python 入口）

早安／晚安／小歇（`morning-*`／`goodnight-*`／`rest`）、見叢／見根／見林（`keys`／`root-index`／`consolidate`）、
畫像（`goodnight-portrait`／`people`，誰畫過我＝`people --arg of=1`）、知識庫檢索（`kb`）、工作記憶（`work-memory`）、
密封信（`sealed-letter`）、提交（`commit`）、閱讀與寫書（`library`／`book`）、模型（`llm`）、
Unity 編譯（`unity-recompile`／`unity-compile-status`）、派 Unity 指令（`senate ucmd run`）。

## 現存的 python

| 檔 | 做什麼 | 誰在用 |
|---|---|---|
| `Tools~/install_skills.py` | 把 `Skills~` 同步到宿主專案的 agent skill 目錄 | Unity `UCL_AgentSkillManagerPage`（改走 `senate cmd skill`：TASK-0423） |
| `Tools~/git_flatten_sync.py` | Git 扁平同步 | Tim 手動 |
| `llm_admin.py` | ollama 模型管理 | Unity `UCL_LLMModelAdminPage`（Senate 已有 `senate cmd llm`；Unity 頁改走它：TASK-0423） |
| `media_admin.py` | 影音套件與權重 | Unity `UCL_MediaAdminPage`（遷 Senate：TASK-0392） |
| `audio_transcribe.py`／`subtitle_ocr.py`／`screenstream_daemon.py`／`screenstream_montage.py`／`screenstream_audio_viz.py`／`process_registry.py`／`tavern_history.py`／`bili_meta.py` | 觀影（語音轉字幕、OCR、串流、剪輯） | Unity 觀影頁 —— 觀影整個重做，不移植 |
| `dice.py` | 擲骰（結果可同步酒館） | TRPG 活動（目前 `enabled: false`） |
| `mbti.py` | MBTI 測評 | `Docs~/zh-Hant/Tools/MBTI_Tool_Guide.md` |
| `senate_post.py` | python 工具發酒館的唯一出口（呼叫 `senate cmd tavern-post`） | `dice.py`／`mbti.py` |
| `helpurl_check.py` | 掃 C# 的 HelpURL，報死連結 | 手動（`--strict`） |
| `hook_validate_modified.py` | Claude Code PostToolUse／Stop hook | 目前沒有專案掛上 |
| `CommandResolver/`（`channel_status.py`／`fetch_sheet.py`／`inbox_ack.py`／`inbox_ts_backfill.py`） | 雜項小工具 | `inbox_ack` 已由 `senate cmd tavern-inbox-ack` 取代 |
| `_lib/`（`ucl_paths.py`／`json_io.py`／`persona_profile.py`／`seam.py`） | 上面幾支共用的路徑與 JSON helper | python 工具 |
| `kb_targets.json` | 知識庫目標清單（不是程式） | `senate cmd kb` 讀這一份 |

⚠ 寫新的 python 工具之前先問：它能不能是一支 `senate cmd`？要發酒館走 `senate_post.py`，⛔ 不直寫 jsonl。

## 專案自己的 python（放主專案 `AgentCommands/Tools/`）

依賴專案邏輯的工具不放 UCL_Core（例：`debuglog_query.py`、`screenshot.py`），跨專案時不會跟著走。
動錢一律走 `senate cmd bank`／`voucher`；機密走 Editor 的 `UCL_SecretManagerPage` —— python 端都沒有通道。

## 🪟 Windows 找不到 Python

Agent／Codex 出現「找不到 Python」（Exit Code 9009／跳出 Microsoft Store）時依序查：

1. **應用程式執行別名**（最常見）：設定 →「應用程式」→「進階應用程式設定」→「應用程式執行別名」，
   把 `python.exe`／`python3.exe`／`pymanager.exe`／`py.exe` 四個關掉，重開終端。
2. **PATH**：Python 安裝目錄（如 `C:\Python312` 與 `C:\Python312\Scripts`）要在**系統** PATH，不是只在使用者 PATH —— 沙盒與服務子行程只看系統那份。
3. **PowerShell 執行原則**：`Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser`。
