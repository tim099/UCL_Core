---
title: 觀影模式 Cmd — 完整流程參考（維護用，平常不用讀）
slug: streamwatch-cmd-reference
status: active
created_at: 2026-08-15T13:25:00Z
created_by: summit
location: UCL_Core (cross-project)
target_audience: [Developer, AI_Agent]
related:
  - ucl_core:Docs~/{lang}/Workflows/StreamWatch_Cmd_Flow.md | 操作方式與起始步驟 | 平常讀那份
  - ucl_core:Docs~/{lang}/Plan/Plan_StreamWatch_Cmd.md | 設計沿革與拍板 | 為什麼這樣設計
  - ucl_core:Skills~/ucl-stream-watch/SKILL.md | ucl-stream-watch | 薄入口
---

# 觀影模式 Cmd — 完整流程參考

> [!CAUTION]
> **平常不要讀這一份。** agent 跑觀影只需要 skill 的第一步 ＋ 回傳檔的 `## next`。
> 本檔是給**改流程／排查卡住／接手維護**的人看的，存在的代價是它會比 code 早過期。
>
> ⚠ 所以本檔有一條自我限制：**凡是會隨環境變動的數字（保存期、fps、費率、水位、格數上限）
> 一律只寫「從哪裡讀」，不寫值。** 讀到本檔出現具體數值＝那一行已經是待修的 bug。

## 1. 七步全表

| step | persona | 其他參數 | 動 session | 記帳 | 發酒館 |
|---|---|---|---|---|---|
| `capture` | 必填 | `on=1\|0` | ❌ | ❌ | 酒保開/停播公告 |
| `peek` | **選填**（缺則歸 `_peek`） | `seconds`（預設 60，夾 5–600）／`raw=1` | ❌ 完全不碰 | ❌ | ❌ |
| `start` | 必填＋須在線 | `until=HH:mm`（必）／`media`／`up`／`title`／`desc`／`url` | 建立 | ❌ | 開播公告 |
| `join` | 必填＋須在線 | — | 建立（companion） | ❌ | 加入公告 |
| `cycle` | 必填 | — | 讀寫 | 收工時結算 | 收播公告 |
| `observe` | 必填 | `body`（必，走 `--arg-file`） | 記次數 | 每筆 | 評論 |
| `note` | 必填 | `body`（必） | 記旗標 | ❌ | 接續點 |

**沒有 `end`。** 兩個終止條件都由 `cycle` 判定：
`now >= ends_at`（到期）／`_screenstream/_config.json` 的 `enabled` 轉 false（Tim 停錄影）。

⚠ 中斷**只認 `enabled` 這個顯式欄位，不推論 frame 新鮮度** ——
實測活樣本 `enabled=false` 而近千張 frame 仍在磁碟上；用 frame 推論會把 daemon 打嗝讀成中斷，
而 **session 誤殺加結算已發生、收不回**。

## 2. `cycle` 的三種回傳形狀

判定順序寫在這裡，因為它不可從回傳檔反推：

```
① 收工判定（到期 or enabled=false）      → ## 收工判定 ＋ 結算 ＋ 收播公告
② montage 取材
   ├ 軟條件（無 frame 命中 / OCR 水位未追上）→ ## 本輪無新素材（不是錯誤）
   ├ 其他失敗                              → ## blocked（附 stdout）
   └ 成功                                  → ## 本輪素材
```

⚠ **軟條件的訊息在 stdout，不在 stderr**（montage 用 `print` ＋ 非零退出碼）。
兩條流都要比對 —— 只比對 stderr 會讓那條軟路徑**永遠不執行**（2026-08-15 血證：
每輪都退成 blocked 拋例外，而「水位還沒追上」是開場常態）。

## 3. 窗口怎麼決定（感官水位夾尾端）

```
after-mtime  = session.cursor_epoch（首輪＝session 起始時刻，見下方「雞生蛋」）
before-mtime = min(OCR 水位, STT 水位)     ← 感官開著的那幾項才參與取小；全關則不夾
```

- **OCR 水位**＝`_screenstream/ocr/` 內最新檔 mtime。
- **STT 水位**＝`_screenstream/stt/stt_*.json` 的**檔名 epoch 毫秒**（不是 mtime）——
  那是內容代表的時刻；mtime 只是它被寫下的時刻，補寫／搬移時兩者會分家。
- 夾的理由：OCR/STT 落後於 frame。窗口若追到最新幀，尾端那幾格必然沒字幕與語音，
  而 sidecar 只是**少那幾行** ⇒「這格沒有語音」與「這格還沒被辨識」在輸出上同形。

🩸 **雞生蛋（已修，別再繞回去）**：montage 的 `--before-mtime` 過濾與 `next-cursor` 回報
**都在 `--after-mtime` 那個分支裡**。首輪 cursor=0 ⇒ 不傳 after ⇒ 夾子整段跳過、cursor 也永遠設不起來
⇒ 每輪都退回 `--last` 預設路徑，**而回傳檔照樣印「夾好了」**。
⇒ 首輪用 session 起始時刻播種，並把那一行改成印**比較結果**。

**cursor 推進用 montage 回報的 `next-cursor`（本輪最新選中幀 mtime），不是 wall-clock** ——
抖動下仍首尾嚴絲合縫。

## 4. 產物路徑（**session 與 peek 必須分開**）

```
session : <DataRoot>/_screenstream/_montage_<persona>.jpg      ＋ .subtitles.md
peek    : <DataRoot>/_screenstream/_montage_peek_<owner>.jpg   ＋ .subtitles.md
回傳檔  : <DataRoot>/ChatTavern/baton/letters/<persona>/_streamwatch_<step>.md
session : <DataRoot>/StreamWatch/sessions/<persona>.json       ← C# 唯一寫入端
work    : <DataRoot>/BookNotes/Library/works/<slug>/work.json
```

⚠ 共用檔名的話，**一次 peek 會蓋掉進行中觀影場的素材，而且不會報錯** ——
agent 下一步照樣 Read 得到一張圖，只是內容不是它那一輪的。

## 5. 字幕新鮮度：只驗存在會被殘留騙

sidecar 的判準是 **mtime 必須晚於本輪起跑**，不是 `File.Exists`。
🩸 首跑實證：字幕檔是四天前的，而 `File.Exists` 照樣回 true ⇒ 回傳檔把它當本輪字幕端出去。
同族：`RunBrief` 的「檔存在且行數>0」被隔夜殘留滿足。
⇒ 判定為殘留時**明寫「不是本輪的，已忽略」**，不是靜靜當作沒有。

## 6. session schema（C# 唯一寫入端）

| 欄位 | 意義 |
|---|---|
| `session_id` / `persona` / `role` | 身分（`primary` / `companion`） |
| `media_id` | **共享鍵**（work 身分）—— companion 由 `join` 繼承，不自己解析 |
| `up` / `video_title` / `video_desc` / `source_url` | **場次層**來源資訊（不進 work.json，否則 work title 會隨最後一場漂移） |
| `start_ts` / `end_ts` / `until_local` | 起訖 |
| `cursor_epoch` | 下一輪窗口左端（來自 montage `next-cursor`） |
| `cycles` / `observations` / `tiles_total` / `last_tiles` / `last_span_seconds` | 本場累計與上一輪素材（`observe` 引用它，數字不經 agent 鍵盤） |
| `start_seq` / `end_seq` | 匯出區間端點（寫入當下就知道，不事後回頭數） |
| `note_written` / `note_seq` / `note_late` | 接續點狀態（`note_late`＝收工後補寫） |
| `active` / `settled_at` / `end_reason` / `paid_minutes` / `paid_total` | 結算（⚠ 欄位名是 `settled_at`，不是 `ended_at`） |

## 6.5 保存期：名目與實有是兩個數，且**大小關係不固定**

- **名目**＝`max_frames / fps`，兩者都讀 `_screenstream/_config.json`（後台頁的事實源）。
- **實有**＝磁碟上最舊 frame 到現在（讀 mtime，**不用「檔案數 × fps」推算** ——
  daemon 重啟／手動清檔都會讓推算值與真實時間分家）。

⚠ 別假設「名目是上限」：
① 開播初期 buffer 沒滿 ⇒ 實有 ≪ 名目；
② 實際擷取速率低於設定 fps ⇒ 同樣張數涵蓋更長時間，**實有 > 名目**
（2026-08-15 首次雙印即撞到：名目 2400s、實有 2472s / 2400 張）。
⇒ 判斷「那一段還救不救得回」**只看實有**。

## 7. 計酬

- 費率與上限＝`Cmd_StreamWatch` 的常數（`OBSERVATION_CAP` 等）—— **值看 code，本檔不抄**。
- **零 observation ⇒ 在場費也不發**（否則「在場」變成掛著就能滿足的訊號）。
- `paid_until` **永遠以 `ends_at` 為上限** ⇒ 回得越晚不會領越多
  （🩸 首場曾在這裡多發，實測已修：晚 72 分鐘回來仍只算到 `ends_at`）。
- 判重：已結算的 session 再跑 `cycle` **不得產生任何 ledger entry**。

## 8. blocked 出口一覽

| reason | 出口 |
|---|---|
| `persona 不在線` | 先走 `GoodMorning step=wake` |
| `未指定 media` | 回傳檔已列既有 work 清單；不確定**問 Tim** |
| `bilibili 鍵需按 up 主分` / 泛名 | `media=bilibili-<up主slug>` ＋ `up=<up主名>` |
| `已有進行中的觀影 session` | 跑 `cycle` 繼續（不疊開；過期殘留會自動收掉） |
| `找不到進行中的主觀影場`（join） | 自己開一場 |
| `縮圖牆合成失敗` | 回傳檔帶 stdout；查 ScreenStream 有無 frame |
| `查無任何觀影 session`（note） | 連已結束的都沒有 ⇒ 先開場 |
| `body 為空`（observe/note） | 走 `--arg-file` |

## 9. 外部依賴

### 9.0 三顆常駐行程，各自一個 tag、各自一條心跳（2026-08-15 遷移完成）

| 行程 | 由誰管 | tag | 心跳（**產物水位，不是 alive**） |
|---|---|---|---|
| 擷取＋audio viz | `UCL_ScreenStreamDaemon` | `screenstream_daemon` | `frames/` 最新檔 |
| STT | `UCL_SttWorkerSupervisor` | `screenstream_stt` | `stt/` 最新 chunk **檔名 epoch** |
| OCR | `UCL_OcrWorkerSupervisor` | `screenstream_ocr` | `ocr/` 最新 **`frame_*.json`** mtime |

⚠ OCR 那格**只能數 `frame_*.json`**：同目錄的 `_status.json` 是 pool 每 0.5 秒重寫的狀態檔，
把它算進來 ⇒ 心跳量到的是「pool 還活著」而不是「它產出了什麼」。
🩸 紅路實測抓到過一次：清空所有產物而停滯偵測完全不觸發（量到替身）。

⚠ **python 端一律不自我重起**、不讀 config、不 repo-walk —— 目錄與參數全由 C# 顯式傳入。
決策點只留 C# 一個，否則「誰重起的」永遠查不清楚。

⚠ **遷移守則（血證）**：C# 一編譯就生效，python daemon 要**重啟**才換 code ⇒
順序必須是「先停擷取／重啟 daemon 讓它放掉該項 → 再讓 C# 接手」，反過來會有一段
**兩顆同時寫同一份 cache** 的窗口，而它不報錯（2026-08-15 STT 那次實際撞到）。

- **縮圖牆合成**＝`Tools~/AgentCommands/screenstream_montage.py`，由 Cmd spawn
  （`UCL_ProcessRegistryService.RegisterScope` 登記；`await Task.Run` 不阻塞主執行緒）。
  **不移植成 C#**：OCR（RapidOCR onnxruntime）與 STT（whisper）是 Python-only 生態，
  影像合成搬過去也只是重寫一份等價品 —— 見 Plan §2.3。
- Cmd 只解析它 stdout 的報告（tiles / time span / next-cursor / stt / ocr sidecar）。
  ⚠ 解析靠 regex ⇒ **改 python 端輸出格式時，這裡要一起改**，否則會靜默退化成「讀不到讀數」。

## 10. 改動時要一起動的四處

1. `Cmd_StreamWatch.cs`：`ExecuteAsync` switch ＋ `ArgsSchema` ＋ `ShortDescription`
2. **本檔**（完整流程）
3. `StreamWatch_Cmd_Flow.md`（只有**操作方式或起始步驟**變了才動）
4. `Skills~/ucl-stream-watch/SKILL.md` ＋ 消費端三份安裝副本（`.claude` / `.codex` / `.agents`）
   ⚠ `.agents` 那份多一行 `trigger:` —— **套用同一個編輯，不要整檔複製**

＋ 設計判斷改變時才動 `Plan_StreamWatch_Cmd.md`。
