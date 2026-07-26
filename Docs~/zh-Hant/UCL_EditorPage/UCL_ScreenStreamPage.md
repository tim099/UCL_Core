---
title: 螢幕直播錄影頁 (UCL_ScreenStreamPage)
description: ScreenStream daemon 控制頁 (UCL_Core 版) — 錄影 toggle (二段確認)、fps/解析度/monitor 設定、即時預覽、STT 錄影同步開關；自 EOV RCG_ScreenStreamPage 遷移。
tags: [editor-page, screenstream, stream-watch, stt]
aliases: [螢幕直播, 錄影頁, ScreenStream, 直播控制]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-07-26
---

# 🎥 螢幕直播錄影頁 (UCL_ScreenStreamPage)

> 一句話：**ScreenStream daemon 的錄影控制入口（UCL_Core 版）** — 讀寫主專案 `AgentCommands/_screenstream/_config.json`，daemon 每 loop reload 即生效。自 EOV `RCG_ScreenStreamPage` 遷移（Tim 2026-07-26 拍板；確認新版可用後移除 RCG 版）。

## 分工

| 頁 | 管什麼 |
|---|---|
| **本頁** | 錄影 toggle（二段確認防誤觸）、fps / max_frames / resolution / quality / monitor、即時預覽、STT 錄影同步開關（stt_setting/model/lang）、stt_prompt 殘留可視化與清除 |
| [UCL_MediaAdminPage（影音管理）](UCL_MediaAdminPage.md) | STT/OCR **依賴安裝**（whisper / torch CUDA / rapidocr / onnxruntime-gpu）、細部設定、試錄 — 本頁右上與 STT 區塊都有跳轉鈕 |

## 配套元件（全鏈 2026-07-26 遷入 UCL_Core）

- `UCL_ScreenStreamDaemon`（EditorCore/UCL_AgentCommands/MediaAdmin/）— [InitializeOnLoad] spawn/看護 `<UCL_Core>/Tools~/AgentCommands/screenstream_daemon.py`。**過渡期守門**：偵測到 legacy `RCG.Editor.RCG_ScreenStreamDaemon` 仍在專案 → 讓位待命（防新舊雙 daemon 併寫 frames ring buffer）；Tim 移除 RCG 版後下次 domain reload 自動接管。
- python 工具鏈（`screenstream_daemon.py` / `stream_watch_session.py` / `screenstream_montage.py` / `audio_transcribe.py`）— 同住 Tools~，repo-walk（跳 submodule gitlink）＋ honors `.agentcommands_root.local` 把 runtime 狀態落主專案 `AgentCommands/_screenstream/`。
- 陪看 skill：`Skills~/ucl-stream-watch`（自 EOV valor-stream-watch 遷移；`install_skills.py --include ucl-stream-watch` 安裝）。

## 承繼的行為釘（與 RCG 版一致）

- **T13** 獨立頁防誤觸＋錄影紅燈警示＋敏感頁自動黑屏 flag；**T14** 多螢幕列舉＋預覽；**T19** unity_game 來源
- **T-STT-PageToggle**：`stt_setting`＝意圖（持久化）、`stt_enabled`＝實效（錄影中 && 開關），停錄影自動停 STT
- **T-STT-StaleFix**：mtime 感知 reload＋可編輯欄位 3-way merge（外部工具改動即時反映、不蓋 Tim 編輯中欄位）；開始錄影自動清空 `stt_prompt` 防跨場幻聽
