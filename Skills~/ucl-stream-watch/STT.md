# Stream Watch STT — 語音轉文字（觀影第三感官）

> 一句話：**縮圖牆給眼睛、OCR 給字幕、STT 給耳朵** — whisper 把直播的系統音訊轉成逐句日/中/英文字，補上 OCR 抓不到的語氣、獨白與畫外音。
>
> 本檔是 [`SKILL.md`](SKILL.md) 的 STT 專章（自 SKILL.md Hard Rule 拆出）。改 STT 相關工具（`audio_transcribe.py` / daemon STT worker / montage `--stt`）記得同步本檔。

## 🏗 最終架構：分層 fallback（2026-07-09 summit 拍板，basecamp/apex-one/gura 討論收斂）

所有路徑都寫/讀**同一份標準 cache**（`_screenstream/stt/stt_<epoch_ms>.json` 帶絕對 epoch + `_status.json` 水位），montage 端統一走 `read_stt_cache(after, until)` 依 epoch 重疊篩。三層自動選路：

```
① daemon cache（全覆蓋，Tim 本機 Editor）
   screenstream_daemon.py SttCacheWorker 常駐連續錄 → 寫 cache
   └ 只在「非容器」Editor 起得來（見已知坑#1 的 MSIX 隔離）

② montage --stt-live（容器場 agent 驅動，即戰力）★本輪 ship
   montage --stt 讀 cache；若窗口無 cache → 在 agent shell (看得到 whisper) 同步現抓一段
   → write_stt_chunk 寫成標準 cache (真實 epoch) → 照舊 read_stt_cache 讀到
   └ 覆蓋% 從「實測 epoch span」算(非請求秒數, gura 磚1 防虛報); 一 cycle pipeline 延遲(下輪起對齊)

③ 容器外常駐 recorder（100% 全覆蓋，Tim 起一次）
   audio_transcribe.py cache-worker <秒數> —— 從「一般終端機/開機項」跑(不在容器裡→看得到 whisper,
   不是 agent 孩子→不吃 teardown)。gura 磚3: 音訊是 WASAPI 系統層, 不甩 Unity 也不甩容器, 解耦即解放。
   montage 完全不動(照樣 cache-only 讀到全覆蓋 cache)。

audio_transcribe.py live <sec> —— 純 stdout 單發, debug/手動抓「現在這 N 秒」用, 不寫 cache。
```

**共用寫入**：`write_stt_chunk(cache_dir, start, end, segs, model)` 是 module-level 函式，SttCacheWorker（①）跟 montage `--stt-live`（②）跟 cache-worker（③）都走它，避免格式漂移。

**選路邏輯（montage --stt --stt-live）**：cache 有蓋到窗口 → 讀它（①/③ 的產物）；沒有 → 現抓寫入（②）。所以同一條 montage 指令在三種環境自動選對路。stream-watch cycle 的 montage_cmd 開了 STT 就自動附 `--stt-live`。

### 🔗 T-STT-AutoAttach（Tim 2026-07-10 拍板「不必帶 --stt, 啟動 STT 就自動打包」）

montage 的 STT 段觸發條件對齊**酒館 ride 在 `--ocr` 上**的 opt-out 語意：**顯式 `--stt` 或 daemon `_config.json` 的 `stt_enabled=true` 任一為真即附掛**。意即 Tim 從 UCL_ScreenStreamPage(過渡期舊名 RCG_ScreenStreamPage) / config 開了 STT，觀影 agent 的 montage **不必記得帶 `--stt`** 也會自動接上 STT 段——補上「Page 開關 ↔ agent montage」原本解耦的縫隙。

- **純 config 自動觸發**：走 **cache-only**（讀 daemon/常駐 recorder 已餵的 cache），model/lang **沿用 config 的 `stt_model`/`stt_lang`**（誠實對齊 daemon 實際轉錄設定）。stdout / sidecar stats 標 `[config auto]`。
- **貴的 `--stt-live` 現抓仍須顯式 opt-in**：自動觸發**不會**變重（不強制現抓 ~20s 音訊）——容器場要即戰力仍得顯式帶 `--stt --stt-live`，或走 ③ 容器外常駐 recorder 餵 cache 讓自動附掛讀到。
- **顯式 `--stt`**：model/lang 用 CLI 的 `--stt-model`/`--stt-lang`（觀影 agent 意圖優先於 config）。
- 實作：`screenstream_montage.py` 的 `read_daemon_stt_config()` + STT 區塊 gate `stt_on = stt_explicit or cfg_stt_enabled`。

## 🎛 兩個啟動入口（都寫同一份 `_config.json`）

STT 有兩個獨立入口，都是切 daemon config 的 `stt_enabled`（daemon 每 loop reload → worker lifecycle 綁此 toggle）：

| 入口 | 誰用 | 語意 | `stt_enabled` 實效值 |
|---|---|---|---|
| **UCL_ScreenStreamPage(過渡期舊名 RCG_ScreenStreamPage) 開關**（T-STT-PageToggle, 2026-07-09）| Tim 在 Editor GUI 手動 | 「🎙 錄影時同步啟動語音轉錄」勾選 → 按「開始錄影」時 STT 同步起、「停止錄影」時同步停 | `錄影中(enabled) && stt_setting` |
| **`step=capture --arg on=1`**（Cmd_StreamWatch）| agent 自助開播 | 串 `UCL_ScreenStreamPage.SetRecordingEnabled` → 錄影開關**連動 `stt_enabled`** | 與 GUI 那顆按鈕同一條規則 |

**兩者不衝突**（設計上互補）：Page 的「錄影」是 daemon 截圖總開關（frames 來源），agent 陪看的前提本來就是 Tim 已在錄影。所以正常時序是 Tim 先用 Page 開錄影（+STT），agent 才 watch；`start --stt` 再切一次 true 是**冪等**，其 end 還原只在 prev=false 時關 → Page 開著的話 prev=true、不會被 agent 收播誤關。

Page 的 `stt_setting`（Tim 意圖，持久化）與 `stt_enabled`（實效，daemon 讀）分兩欄位：STT 嚴格耦合錄影，**停錄影自動停 STT**，whisper GPU ~460MB 不空轉。

## 🚀 開播與 STT 的連動

STT **嚴格耦合錄影**：錄影開關一動，`stt_enabled` 跟著動（whisper GPU ~460MB 不空轉）。
所以「讓 STT 起來」的手勢就是「開錄影」，沒有第二個開關：

- **Tim 手動**：Editor 的 `UCL_ScreenStreamPage` 勾「🎙 錄影時同步啟動語音轉錄」＋按「開始錄影」。
- **agent 自助開播**：
  ```bash
  python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run StreamWatch \
      --arg step=capture --arg persona=<me> --arg on=1
  ```
  它**不自己寫 config**，而是串 `UCL_ScreenStreamPage.SetRecordingEnabled` ——
  跟 GUI 那顆按鈕走同一條規則（戳時刻／連動 `stt_enabled`／發酒保公告／要求 daemon 同步）。
  ⇒ 「Cmd 開的播」與「人開的播」在酒館裡長得一樣，而同一件事**只有一個寫入端**。

- **fail-soft**：config 同步失敗只印警告不擋開播（STT 是加值不是硬依賴）。
- ⚠ **現況（2026-07-09）**：AutoStart 正確切 `stt_enabled` true，但容器場 daemon worker 起不來（已知坑#1）→ 這條 cache 空。**但不再是阻塞** —— cycle 的 montage 自動帶 `--stt-live`（layer ②），cache 空時 montage 端現抓寫 cache，容器場照樣有 STT。Tim 本機 Editor 則 daemon 正常、AutoStart 直接生效（layer ①）。

## 🎛 參數速查

| 參數 | 落點 | 預設 | 說明 |
|---|---|---|---|
| `--stt` | session start / montage | off | opt-in（whisper 較重，不強制所有觀影者）。★montage 端 T-STT-AutoAttach：不帶時若 config `stt_enabled=true` 也自動附掛（cache-only, model/lang 沿用 config）|
| `--stt-model` | 同上 | `small` | tiny/base/small/medium/large-v3；small 是品質vs速度甜蜜點，RTX4080 跑 large-v3 也行 |
| `--stt-lang` | 同上 | 空=自動 | **看日番一律給 `ja`**（自動偵測會飄；指定可加速+穩定）|
| `stt_chunk_sec` | daemon `_config.json` | 15 | cache 每 chunk 音訊秒數 |
| `--stt-live` | montage | off | ★本輪 ship：cache 空時同步現抓寫 cache 再讀（容器場 fallback）；覆蓋% 從實測 epoch 算 |
| `--stt-seconds` | montage | 20 | `--stt-live` 現抓秒數，上限 30，阻塞抓滿才回 |
| `cache-worker <秒>` | audio_transcribe.py | 45 | ③容器外常駐 recorder；Tim 一般終端機/開機項跑，全覆蓋 |
| `--stt-prompt` | session start | 空 | 🆕 whisper initial_prompt 詞彙偏置（壓人名咬字）；寫進 daemon config `stt_prompt`，worker 起手吃 |
| `stt_prompt` | daemon `_config.json` | 空 | 同上，daemon 端實際落點；改動需 toggle off→on 重起（daemon log 會警告不靜默）|

### 🆕 T-STT-Prompt — 登場人物名詞彙偏置（summit 2026-07-10, RFC2）

whisper 的 `initial_prompt` 能把「前文語境」餵進去做詞彙偏置——最痛的用途是**壓人名咬字**（シャーリー→サレイ、ケイト→ケート 之類，換 medium 模型都沒解）。

- **資料源 = 新 reading-library session 的人物 `name_original` 欄（片假名）**：新 API 完成後由同 persona／同 media 的人物資料組成 whisper prompt。**MUST 日文字形**——餵中文譯名（夏麗/凱特）給日語 ASR 沒用甚至更糟（whisper 往 prompt 字形偏置）。重做期間不可用 legacy `library.py stt-prompt` 回讀 Archive。
- **管道**：`stream_watch start --stt-prompt "<抽出的字串>"` → 寫進 daemon `_config.json` 的 `stt_prompt` → daemon 起 `SttCacheWorker(prompt=…)` → `transcribe(initial_prompt=…)` → `model.transcribe(initial_prompt=…)`。轉錄在 **daemon 端**做（不是 montage），故 prompt 必須經 config 傳到 daemon。
- **生命週期**：綁 worker，跟 model/lang 一樣**改動需 toggle `stt_enabled` off→on 重起才生效**；daemon 偵測到 `stt_prompt` 改了卻沒重起會 log 一行警告（反靜默失效，別讓「設了沒吃到」默默發生）。
- **上限**：whisper initial_prompt ~224 token，`stt-prompt` 預設截 200 字、砍名詞尾巴保住人名。
- ⚠ **靜音幻覺**：有 initial_prompt 時，whisper 在**純靜音**片段可能 echo prompt 內容（吐出人物名）。真音訊沒這問題；若靜音段冒假人名，交叉 OCR 驗、標待確認（同 ASR 誠實守則）。

## 🛠 陪看 cycle 實戰 SOP

1. **開播**：start 帶 `--stt --stt-lang ja` → cycle 回的 `montage_cmd` **自動附 `--stt --stt-live`**（分層 fallback 自動選路，不必手動）。
2. **每輪**：跑 montage → Read sidecar，「🎙 語音轉錄 (STT·cache)」段列 `[時間] 逐句轉錄`。有 daemon/常駐 cache 就讀它；沒有就 montage 當場現抓寫 cache（`--stt-live`），sidecar 會標 `⚠ live 取樣: 實測 Xs / 窗口 Ys ≈ 覆蓋 Z%`。
3. **一 cycle pipeline 延遲**：`--stt-live` 現抓的音訊 epoch 落在窗口尾端**之後**（capture 在建圖後才跑）→ 本輪不命中、**下輪起命中對齊**。首輪無音正常。要衝全覆蓋走 ③ 常駐 recorder。
4. **手動單發（debug）**：`audio_transcribe.py live 15 --model small --lang ja` 現錄現轉印 stdout（不寫 cache），臨時抓「現在這 15 秒」用。
5. **評論引用守則**：STT 是 ASR，人名/專名會咬字（例：シャーリー→「サレイ」）。按語意還原 + 拿 OCR 字幕交叉驗證，單源不確定標「待確認」（同 OCR 誠實守則）。
6. **健檢**：`audio_transcribe.py check` — 印 whisper/torch 可用性、device、loopback 喇叭。

### ③ 容器外常駐 recorder（Tim 要 100% 全覆蓋時）
從**一般終端機**（不是經 Claude/Unity）跑一支長時 recorder，montage 照舊讀 cache 即全覆蓋：
```bash
python <UCL_Core>/Tools~/AgentCommands/audio_transcribe.py cache-worker 3600 --model small --lang ja   # 錄 1 小時
```
它連續錄 15s chunk 寫標準 cache。因為在容器外（看得到 whisper）+ 是 Tim 的行程（不吃 agent teardown），解掉容器隔離＋teardown 兩病根（gura 磚3）。**未來強化**（gura 磚2，尚未實作）：lease/heartbeat 自清 + 孤兒 reap + 真 detached spawn，讓它能被 agent 安全託管而非只由 Tim 手動起。

## 🧨 已知坑（血淚帳）

1. **daemon 子行程完全看不到 user-site（2026-07-09 查明 root cause；已用 ②`--stt-live` 繞過，不再是阻塞）**：
   - 現象：whisper/torch 裝在 user-site（`%APPDATA%\Python\Python310\site-packages`），Unity Editor spawn 的 daemon 子行程 `No module named 'whisper'`；同一支 python.exe、shell 端 import 正常。
   - **真因（層次混淆 family 新成員）**：Unity Editor 是從 **Claude Code 的 MSIX app-container** 內啟動的，daemon 是它的孫行程。app-container 對 `%APPDATA%\Roaming\Python` 做了**檔案系統虛擬化隔離** — 診斷實錄：daemon 子行程 `os.path.isdir(r"C:\Users\Tim\AppData\Roaming\Python")` 回 **False**，但同路徑 shell 端回 True（WMI 派生子行程實測確認）。所以 `_ensure_user_site()` 想 append 的路徑在子行程眼裡**根本不存在**，補不進去。這不是 sys.path 排序問題，是路徑可見性問題。
   - **已做的緩解**：(1) `is_available()` import 失敗時補 user-site 進 sys.path（插 system site 前）再重試 → 修好 **shell/live 路徑**；(2) ★**montage `--stt-live`**：daemon cache 空時 montage 在 shell 端現抓寫 cache → 容器場照樣有 STT，**daemon 起不來不再是阻塞**。
   - **要 100% 全覆蓋的根治**：gura 磚3 —— 音訊是 WASAPI 系統層、不甩 Unity 也不甩容器，把 recorder 拆出來從**容器外**跑（`audio_transcribe.py cache-worker`，見上 SOP ③）。避開容器隔離＋teardown 兩病根，montage 不動。
   - 次要：Tim 本機 Editor（無容器）daemon 本來就正常，layer ① 全覆蓋自動生效。
   - ⚠ agent 別試 WMI/容器逃逸自行安裝到 system site — 會被安全機制擋（也應該擋），是 Tim 的決定。
2. **system site-packages 有殘缺 torch 孤兒**：`<Python310>\Lib\site-packages\torch{,gen,functorch}`、`numba`、`tiktoken` 是沒 dist-info 的裝機殘骸（`import torch` 後連 `__version__` 都沒有），要在 system site 重裝 torch 前得先清掉，否則新裝的裝不進 / 被殘本蓋掉。清除屬 out-of-project 破壞性操作，待 Tim 手動或明確授權。
3. **model/lang 改動需 worker 重起**：daemon 端 model/lang 變更要 `stt_enabled` toggle off→on 才生效（worker lifecycle 綁 toggle）。
4. **VRAM**：whisper small 常駐 GPU ~460MB；不看直播時別讓 worker 空轉（AutoStart 的 end 還原就是為此）。

## 📚 相關

- 本體 SOP：[`SKILL.md`](SKILL.md)（cycle 流程 / OCR sidecar / 酒館同步）
- 工具：`<UCL_Core>/Tools~/AgentCommands/audio_transcribe.py`（whisper 封裝 + live CLI）、`<UCL_Core>/Tools~/AgentCommands/screenstream_daemon.py`（SttCacheWorker lifecycle ~line 681）、`<UCL_Core>/Tools~/AgentCommands/screenstream_montage.py`（`--stt` cache 讀取）
- 音訊視覺化（無耳朵時的另一感官）：`docs/Workflows/Audio_Viz_Reading_Guide.md`
