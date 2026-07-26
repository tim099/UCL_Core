---
title: 影音管理頁 (UCL_MediaAdminPage)
description: STT (whisper 語音轉文字) 與 OCR (字幕讀取) 的可視化管理入口 — 依賴安裝 / daemon config 設定調整 / STT 試錄。後端唯一真相源為 media_admin.py。
tags: [editor-page, media, stt, ocr, whisper]
aliases: [影音管理, STT 管理頁, 語音轉文字管理, 字幕 OCR 管理, media admin]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-07-25
---

# 🎬 影音管理頁 (UCL_MediaAdminPage)

> 一句話：**stream-watch 觀影工具鏈「影音辨識層」的管理入口** — whisper STT 的安裝與設定、字幕 OCR (RapidOCR) 的參數，收攏在同一頁。錄影本體開關仍歸 ScreenStream 錄影頁；本頁只管「辨識」欄位。

（Tim 2026-07-25 拍板；參考 [UCL_KnowledgeBaseAdminPage](UCL_KnowledgeBaseAdminPage.md) 結構。命名走「影音」抽象——先收 STT，字幕讀取 (OCR) 也整合本頁，換後端不必改頁名。）

## 架構（對齊知識庫頁的分層哲學）

```
UCL_MediaAdminPage (薄 UI)
  ↓ UCL_MediaAdminRunner (async spawn python, 不卡 main thread)
media_admin.py  ← 唯一真相源 (<UCL_Core>/Tools~/AgentCommands/)
  ↓ 讀寫
<主專案>/AgentCommands/_screenstream/_config.json   ← per-project daemon config (STT/OCR 欄位)
  ↓ 委派
audio_transcribe.py (專案端, 試錄) / screenstream daemon / montage --ocr
```

- **script 住 UCL_Core**（跨專案共用），**runtime 狀態落主專案** AgentCommands（honors `.agentcommands_root.local`，不寫進 submodule）。
- 路徑走 `UCL_EditorPath.CorePath` 動態解析，不硬編 install path（見 ucl-core-paths 慣例）。

## 面板

| # | 面板 | 做什麼 |
|---|---|---|
| 1 | 環境與依賴狀態 | whisper / torch(+CUDA) / soundcard / numpy / rapidocr / onnxruntime(+OCR CUDA provider) import 健檢 + config 總覽；面板可用 ▼/► 折疊 |
| 2 | 依賴安裝 | `install --stt`（openai-whisper + soundcard + numpy）/ `--torch-cuda`（cu124 wheel，STT GPU 加速）/ `--ocr`（rapidocr-onnxruntime）/ `--ocr-cuda`（onnxruntime-gpu，OCR GPU 加速）；pip `--user` 落 user-site |
| 3 | STT 設定 | `stt_setting`（錄影時同步啟動）/ `stt_model` / `stt_lang` / `stt_chunk_sec` / `stt_prompt`（詞彙偏置，人名用原文字形） |
| 4 | OCR 字幕讀取設定 | `ocr_enabled` / `ocr_workers` / 字幕帶 `y_pct`/`h_pct` / `min_conf` |
| 5 | STT 試錄 | 委派專案端 `audio_transcribe.py live N` 驗整條鏈 |

## 設定欄位語意（重要）

- **`stt_setting` vs `stt_enabled`**：前者是「Tim 意圖」（持久化，本頁可調）；後者是「實效值」（daemon worker lifecycle 綁它、與錄影開關耦合），**本頁唯讀**。詳見 valor-stream-watch 的 STT.md 兩入口表。
- **`stt_model` / `stt_prompt` 改動需 toggle STT 重起 worker 才吃到**（prompt 綁 worker 生命週期；daemon 會 log 警告不靜默）。
- 白名單雙保險：頁面只送白名單欄位，python 端 `EDITABLE_KEYS` 再驗一次型別——防誤寫壞錄影欄位（fps/resolution 等歸 ScreenStream 錄影頁）。

## CLI（agent 直接用）

```bash
python <UCL_Core>/Tools~/AgentCommands/media_admin.py status
python <UCL_Core>/Tools~/AgentCommands/media_admin.py get-config
python <UCL_Core>/Tools~/AgentCommands/media_admin.py set-config stt_model=small stt_lang=ja
python <UCL_Core>/Tools~/AgentCommands/media_admin.py install --stt
python <UCL_Core>/Tools~/AgentCommands/media_admin.py test-stt --sec 8 --model small --lang ja
```

## 相關

- [UCL_KnowledgeBaseAdminPage](UCL_KnowledgeBaseAdminPage.md) — 結構參考來源（薄 UI / python 真相源 / async runner）
- EOV 專案 `.claude/skills/valor-stream-watch/STT.md` — STT 三層 fallback 架構與兩個啟動入口
- `audio_transcribe.py`（專案端）— 擷取/轉錄實作本體
