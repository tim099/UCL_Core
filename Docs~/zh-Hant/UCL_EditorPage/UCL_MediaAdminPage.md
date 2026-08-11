---
title: 影音管理頁 (UCL_MediaAdminPage)
description: STT (whisper 語音轉文字) 與 OCR (字幕讀取) 的可視化管理入口 — 插件安裝/解除安裝 / daemon config 設定調整 / STT 試錄。後端唯一真相源為 media_admin.py。
tags: [editor-page, media, stt, ocr, whisper, plugin]
aliases: [影音管理, STT 管理頁, 語音轉文字管理, 字幕 OCR 管理, media admin, 插件管理]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-11
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
| 2 | **插件管理** | **下拉選插件 → 只顯示該插件的動作**（安裝 / 解除安裝 / 切換後端）；清單與動作由 python 的 `PLUGINS` 註冊表生成，pip `--user` 落 user-site |
| 3 | STT 設定 | `stt_setting`（錄影時同步啟動）/ `stt_model` / `stt_lang` / `stt_chunk_sec` / `stt_prompt`（詞彙偏置，人名用原文字形） |
| 4 | OCR 字幕讀取設定 | `ocr_enabled` / `ocr_workers` / 字幕帶 `y_pct`/`h_pct` / `min_conf` |
| 5 | STT 試錄 | 委派專案端 `audio_transcribe.py live N` 驗整條鏈 |

## 插件註冊表（Tim 2026-08-11 拍板：插件會越來越多，不要繼續加按鈕）

**唯一定義處是 `media_admin.py` 的 `PLUGINS` dict。** 頁面不維護任何清單 —— 它跑 `list-plugins`
拿 JSON 再建下拉選單與按鈕，所以**新增一個插件只改 python 那張表，C# 一行都不用動**。

| 插件 id | 內容 | 動作 |
|---|---|---|
| `stt` | openai-whisper + soundcard | `install` / `uninstall` |
| `torch` | whisper 的推論後端 | `cuda`（cu126 wheel）/ `uninstall` |
| `ocr` | rapidocr-onnxruntime + onnxruntime | `install` / `cuda` / `cpu`（降級回 CPU）/ `uninstall` |

**註冊表欄位**：`name` / `desc` / `probe`（import 名，供健檢）/ `actions[{id,label,hint,danger}]`。
`danger: true` 的動作在頁面上會先跳確認框，對話框直接列出 `hint` 全文（不用泛稱）。

### ⚠ 解除安裝的兩條硬規則

1. **共用套件不進任何插件的卸載清單。** `numpy` 與 `torch` 被 daemon / montage / audio-viz 共用，
   夾帶卸掉會**靜默弄壞整條陪看鏈**。所以 `stt/uninstall` 只卸 whisper + soundcard，
   torch 另立插件由人明確選擇。
2. **卸載要迴圈到乾淨為止。** pip 一次只卸「sys.path 順位最前」的那一份，user-site 與 system site
   可能各有一份（torch 孤兒的前科）。`_pip_uninstall()` 反覆執行到 pip 不再回報
   `Successfully uninstalled`——**只跑一次會留下被遮蔽的第二份，而 status 仍顯示 ✅，
   於是「解除安裝成功」是假的。**

另：`ocr/cpu` 是**降級不是移除**（卸 gpu dist → 裝回 CPU 版，OCR 仍可用）——
名字與事實要對得上，不要把它寫成「解除安裝 CUDA」。

## 設定欄位語意（重要）

- **`stt_setting` vs `stt_enabled`**：前者是「Tim 意圖」（持久化，本頁可調）；後者是「實效值」（daemon worker lifecycle 綁它、與錄影開關耦合），**本頁唯讀**。詳見 valor-stream-watch 的 STT.md 兩入口表。
- **`stt_model` / `stt_prompt` 改動需 toggle STT 重起 worker 才吃到**（prompt 綁 worker 生命週期；daemon 會 log 警告不靜默）。
- 白名單雙保險：頁面只送白名單欄位，python 端 `EDITABLE_KEYS` 再驗一次型別——防誤寫壞錄影欄位（fps/resolution 等歸 ScreenStream 錄影頁）。

## CLI（agent 直接用）

```bash
python <UCL_Core>/Tools~/AgentCommands/media_admin.py status
python <UCL_Core>/Tools~/AgentCommands/media_admin.py get-config
python <UCL_Core>/Tools~/AgentCommands/media_admin.py set-config stt_model=small stt_lang=ja
python <UCL_Core>/Tools~/AgentCommands/media_admin.py list-plugins
python <UCL_Core>/Tools~/AgentCommands/media_admin.py plugin --id stt --action install
python <UCL_Core>/Tools~/AgentCommands/media_admin.py plugin --id ocr --action uninstall
python <UCL_Core>/Tools~/AgentCommands/media_admin.py test-stt --sec 8 --model small --lang ja
```

未知 `--id` / `--action` 一律 fail-fast 並列出合法值（裝/卸不可逆，不做模糊比對）。

## 相關

- [UCL_KnowledgeBaseAdminPage](UCL_KnowledgeBaseAdminPage.md) — 結構參考來源（薄 UI / python 真相源 / async runner）
- EOV 專案 `.claude/skills/valor-stream-watch/STT.md` — STT 三層 fallback 架構與兩個啟動入口
- `audio_transcribe.py`（專案端）— 擷取/轉錄實作本體
