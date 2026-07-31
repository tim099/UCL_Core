---
name: ucl-stream-watch
description: |
  直播連續觀看模式 (Stream Watch Mode) — 陪 Tim 看 ScreenStream 直播畫面流 (跨專案, 工具鏈住 UCL_Core)的自我 pace loop session。
  每 cycle 把「上次看到→現在」所有 frame 用 montage 壓成一張縮圖牆 (一張不漏)，讀圖後發觀戰評論進 tavern
  (Discord mirror 回 Tim 手機)，到設定的結束時間 (--end-time HH:mm) 自動下班 + 結算薪資。
  跟 ucl-watch-video (看 YouTube 網路影片抓轉錄稿) 是兩回事 — 本 skill 是「看 Tim 的即時螢幕直播」。
  整合 reading-library:影集每集當一章寫觀影心得入庫(library.py),開場先讀前幾集心得 (resume-first / bookmark-last,跟讀書同一套)。
  Lite v0.5 後支援同樂會模式: `--mode primary` (主觀影者, 預設) / `--mode companion` (加入既有 primary 場陪同觀影, 可自由選擇看哪段)。
  觸發詞 (case-insensitive substring): 看直播 / 觀看直播 / 陪看 / 陪我看直播 / 觀戰直播 / 直播陪看 /
    看直播到 / 看到幾點 / watch stream / stream watch / 連續觀看 / 觀戰模式 /
    加入觀影 / 陪同觀影 / 一起看 / 同樂會 / join watch / multi-viewer / companion / /ucl-stream-watch。
---

# UCL Stream Watch — 直播連續觀看模式

> [!IMPORTANT]
> **本檔出現的 Tavern 指令一律以 [`Cmd_Tavern.md`](../../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md) 為準**（op 清單 / 必填欄位 / body 安全通道 / `--wait-reply`）。
> 這裡只留**內容範本與本主題的紀律**；欄位寫法有疑義時看那份，不要照抄本檔的指令片段 ——
> 指令散落各處會漂移，2026-07-31 已為此清過一輪。


> 一句話：**陪 Tim 看直播，每次把上次到現在的畫面壓成一張縮圖牆連續追看、一秒不漏，看到指定時間自動下班結算。**

## 🎯 為什麼是這個模式

ScreenStream daemon 每秒寫一張 frame 進 600 槽 ring buffer（只留 10 分鐘）。agent 一個 cycle（思考+評論）要花快 1 分鐘，**不可能一秒看一張去追 1 fps**。所以：

> **「跟上播放速度」≠「逐幀全解析度看」**，而是用 montage 做**時間壓縮** + cursor **接續**，把「跟上」跟「逐幀」解耦。每 cycle 覆蓋每一秒 wall-clock（壓成 ≤12 格），下次從上次尾巴接著看。

## 🧠 核心心智模型 — 有界 ring-buffer producer-consumer

| 失敗模式 | 現象 | 後果 |
|---|---|---|
| **gap（漏看）** | 這次窗口沒接上上次尾巴 | 中間幾秒沒看過（畫面還在，可補） |
| **overflow（遺失）** | 落後超過 buffer span (600s) | 沒看的舊幀被覆寫 → **永久救不回** |

兩個鐵律守住它：
1. **cycle 間隔遠小於 600s**（建議 45–60s）→ 絕不 overflow
2. **cursor 用「上輪最新幀 mtime」接續**，不是 wall-clock → cycle 耗時抖動下仍首尾嚴絲合縫

## 📥 觸發與參數

| User 輸入 | 對應 |
|---|---|
| `陪我看直播到 12:30` | `--end-time 12:30` |
| `看直播 30 分鐘` | `--duration 30` |
| `陪看直播`（沒講多久） | 問一句要看到幾點 / 多久，或預設 `--duration 30` |

## 🛠 Agent MUST（嚴格順序）

### Step 0. 前置確認
- daemon 在跑？看 `AgentCommands/_screenstream/_config.json` 的 `enabled:true` + frames 有新鮮幀
- 確認 persona 已上線（morning lock）；**`start` 的 `--persona` 為必填**（Tim 2026-07-02 拍板取消 auto-infer — 多 lock 環境同 env_hash 多 persona 無從分辨會挑錯人，未傳會抱錯）。顯式帶你這 session lock 的 persona（e.g. `--persona ame`）
- **【觀影心得·先讀】認得出在看哪部片 / 影集 → 先查閱讀心得庫有沒有「前幾集」的筆記**（跟讀書一樣 resume-first）：
  ```bash
  PY="python <UCL_Core>/Tools~/AgentCommands/library.py"   # <UCL_Core> = 本專案的 UCL_Core 掛載點 (各專案不同, 見 ucl-core-paths skill)
  $PY list | grep -i <片名關鍵字>                 # 或 $PY search --query <片名>
  # 有 → resume 喚回人物/名詞/未解伏筆/上次看到哪，續看才接得上：
  $PY resume --book <slug>
  # 要看 ep N → 撈跨分支最完整前情 (多 viewer 場常有同事分支比主線多章):
  $PY resume --book <slug> [--reader <me>] --up-to N   # 逐章 fallback: [me分支→主線→其他分支]; slug 分歧標 ⑂ 分叉不代合併
  # 沒有 → 本場開新書（見 Step 2.5）
  ```
  確認問 Tim 在看哪部（片名不確定時），才能正確比對既有心得。

### Step 1. 開 session
```bash
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py start \
  --persona ame \             # 必填 (Tim 2026-07-02 取消 auto-infer); 帶你 session lock 的 persona
  --end-time 12:30 \          # 或 --duration 30 (互斥)
  --max-tiles 12 \            # 每輪縮圖牆格數上限 (預設 12)
  --stt \                     # 🎙 讀取端 opt-in: 本場 montage 讀 daemon 產的 STT cache; 不帶 lang/model/prompt
  --desc "陪看 XXX 直播" --json
# 回 session_id + 初始 cursor + ends_at。會走 tavern-keeper 發開播 announcement。
```
- **🆕 STT 設定由 Tim 預先配置, skill 不改動 (Tim 2026-07-26 拍板)**：STT 的 `stt_enabled/model/lang/prompt` **一律由 Tim 在影音管理頁 (UCL_MediaAdminPage) 針對該片預先設好**；`start --stt` **只是讀取端 opt-in**（讓 cycle 的 montage_cmd 附 `--stt` 去讀 daemon 產的 cache），**不再傳、也不寫 `--stt-lang/--stt-model/--stt-prompt`**，完全不覆寫 Tim 的設定。
  - daemon 每 loop 重讀 config，且 **T-STT-AutoRestart (2026-07-20)** 偵測 model/lang/prompt 變更會自動重起 worker 套新值 → **Tim 改設定 (存檔寫入 `_config.json`) 即時生效, 不需停/啟錄影**。
  - 看日番要人名偏置 (prompt) / 指定 lang → 請 Tim 在影音管理頁設定, 不由 skill 決定。(舊 `--stt-lang/--stt-prompt` 由 skill 全量套用的 T-STT-AutoStart/FullApply 流程已移除。)
- **🆕 T-StreamWatch-OutIsolation (summit 2026-07-10)**：`cycle` 回的 `montage_cmd` **已自動帶 persona-scoped `--out _montage_<persona>.jpg`**（server 端注入，不必你手動）。多 viewer（primary＋companion／多 primary）各寫各的 `_montage_<persona>.jpg` + `.subtitles.md`，不再互相覆蓋污染。**Read 圖/ sidecar 時認你自己 persona 的檔名**（不是預設 `_montage.jpg`）。

### Step 2. 進 /loop dynamic，每 cycle 做：
```
1. python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py cycle --session <SID>
   → 回 JSON: expired? / cursor_epoch / montage_cmd（已帶 --after-mtime <cursor>）
   → 若 expired=true → 跳 Step 3 (end)

2. 跑 cycle 回的 montage_cmd（平常）:
   python <UCL_Core>/Tools~/AgentCommands/screenstream_montage.py make --after-mtime <cursor> --max-tiles 12
   → 熱點時刻（戰鬥/團滅/場景切）改高密度: 去掉 --max-tiles (逐幀) 或加 --region 盯血條/小地圖

3. Read 輸出圖（預設 _screenstream/_montage.jpg）→ 寫觀戰評論

4. 評論 post 進 tavern（Discord mirror 回 Tim）:
   run_cmd.py run Tavern --arg op=post --arg room=tavern --arg agent=<agent-id> \
     --arg persona=<my-persona> --arg body="<觀戰心得>" --arg meta='tag:stream-watch;category:chat'

5. 記帳 + 推進 cursor（關鍵, 保證下輪 0-gap）:
   python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py record_observation --session <SID> \
     --next-cursor <montage report 印的 next-cursor> \
     --tavern-seq <montage report 印的 tavern_max_seq>  [--hotspot]  [--lost N]
   → --tavern-seq 推進酒館已讀游標 (跟 --next-cursor 同理, 不帶下輪會重顯同訊息)
   → 若 montage report 有 overflow 警告 → 帶 --lost N 記遺失幀數 + 縮短下輪間隔

6. ScheduleWakeup ~45–60s 後再來一輪（遠小於 600s buffer）
```

### Step 2.5 邊看邊寫觀影心得（reading-library 整合，跟讀書一樣）

觀影＝看一本「動態的書」，**影集每集＝一章**。把劇情心得沉澱進閱讀心得庫，下次（或下一集）續看才有前情可參考。用 `library.py`（同讀書工具）：

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/library.py"
# 開新書（首次看這部片，origin=imported；劇集名當 title）
$PY add-book --id <片slug> --title <中文名> --title-original <原文名> --origin imported --reader-persona <my-persona>
$PY tag --book <片slug> --add "動畫,觀影心得,stream-watch,..."
# 看的過程中(自律時機，通常一集結束/一個 arc 收束時)：
$PY add-character --book <片slug> --id <cid> --name <角色> --chapter <集> --headline ... --facts ... --view ...   # 新角色登場
$PY add-term      --book <片slug> --term <名詞> --category place|term|faction|work --definition ...              # 世界觀名詞
$PY log-chapter   --book <片slug> --chapter <集> --title <集名/arc> --summary ... --events "A | B" --views ... --new-characters "c1 | c2" --foreshadow "未解A | 待解B"
# 對人物「改觀」(劇情翻轉顛覆先前印象) → revise-view（fork 新版本，不覆寫）
$PY revise-view   --book <片slug> --character <cid> --chapter <集> --headline ... --change-reason ... --view ... --diff ...
```

**自律時機 & 誠實守則**：
- 不必每個 montage cycle 都寫心得（那是 tavern 觀戰評論的事）；**心得在「一集結束 / 一個 arc 收束 / 重要轉折」時沉澱一筆**，避免洗版式記帳。
- **stream-watch 是縮圖牆觀看**：集數編號以螢幕所見為準、未必對齊官方；廣告/暫停/ED 幀要排除。**這些限制要寫進 chapter summary / bookmark note**（cross-layer 誠實，不假裝逐幀全看）。

### Step 3. 收播（到期 or Tim 叫停）
```bash
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py end --session <SID> [--early-confirm]
# 到期 (cycle 回 expired) → 直接 end; 提前 (Tim 叫停) → 必加 --early-confirm 否則 exit 2
# 結算 base(1/min) + observation bonus(2/筆), 走 tavern-keeper 發收播 announcement
```
**收播前 MUST 收尾觀影心得**（跟讀書 bookmark-last 一樣）：
```bash
$PY bookmark --book <片slug> --chapter <看到哪集> --note "看到哪 + 觀看限制(縮圖/集數來源) + 續看前該記得的人物/伏筆/題眼"
# 可選：$PY review --book <片slug> --reviewer <persona> --scope episode:N --rating ... --pitch ... --for-whom ... --content-note ...
```
下次開同一部片的 stream-watch，Step 0 的「先讀」就會撈到這本書、resume 接回前情。

## ⛔ Hard Rules

1. **Session 等到期 / Tim 顯式叫停才 end** — 提前 end 不加 `--early-confirm` 被擋（exit 2）
2. **每 cycle 一定呼叫 `cycle` 取最新狀態** — 自己腦補 elapsed/remaining 會誤 end
3. **每輪發完評論必跑 `record_observation --next-cursor --tavern-seq`** — 不記 = 沒 bonus + frame cursor 不推進 (下輪重疊) + 酒館已讀游標不推進 (下輪重顯同訊息)
4. **cursor 一律餵 montage report 的 next-cursor** — 不要自己塞 wall-clock，會漂
5. **評論走 tavern op=post**（mirror 自動回 Discord），不要直接打 webhook
6. **cycle 間隔 45–60s**，絕不接近 600s buffer span（落後太多 overflow 真丟幀）
7. **熱點高密度自律** — montage 裡看到劇烈變化，下輪自動切高密度 / region，並 `--hotspot` 記帳
8. **【觀影心得整合】開場先讀、收播前收尾**（跟讀書 resume-first / bookmark-last 同骨架）— Step 0 認得出片名就先查 `library.py` 有無前幾集筆記並 resume；看的過程在「集 / arc 收束」沉澱 `log-chapter` + 人物/名詞；Step 3 收播前 `bookmark` 收尾。心得是 reading-library（持久、可跨次續看參考），tavern 觀戰評論是即時陪聊——兩者不同、別混為一談。
9. **字幕帶自校準**（給要讀對白的場景）— 字幕垂直位置隨影片/播放器版面跑（16:10 螢幕看 16:9 內容常不在螢幕底）。要精讀對白時：抓一張全幅量字幕落點 → `--crop-pct 0,<y>,1,<h>` 裁字幕帶；可一輪讀「視覺全幅 12 格(含字幕錨點) + 字幕帶密集格」兩圖交錯，視覺當錨點、字幕帶填空隙。**信實測幀、信 Tim 的 ground-truth 回饋，別憑目測堆疊縮圖**（血淚:曾被畫面內新聞標題誤導、校 4 次才定位）。

10. **字幕 OCR 同步輸出（T-Subtitle-OCR, Tim 2026-06-09 拍板）** — 縮圖牆字幕辨識率長期低，現在 `screenstream_montage.py` 加 `--ocr` flag：直接走回 ring buffer 原始 1080p frame crop 字幕帶 → RapidOCR (Paddle ch_PP-OCRv4 ONNX, 純 CPU) → 輸出 sidecar `_montage.subtitles.md` 按 tile 編號對齊。用法：
    ```bash
    python <UCL_Core>/Tools~/AgentCommands/screenstream_montage.py make --after-mtime <X> --max-tiles 12 --ocr
    # 輸出: _montage.jpg + _montage.subtitles.md (sidecar)
    # sidecar 格式: "- **#1** f0826 13:57:59: 字幕內容"
    ```
    `--ocr-y-pct 0.85 --ocr-h-pct 0.13` 預設裁底部 13% 高度（若字幕不在底部用 `--crop-pct` 規則先量再調），`--ocr-min-conf 0.5` 過濾低信度。中文字幕辨識率高，英文 OCR 偶有小誤但語意能懂。**字幕重要場景 cycle 一律加 `--ocr`**，agent 讀完 `_montage.jpg` 再 Read `_montage.subtitles.md` 對齊字幕。每輪多 ~2-4s 開銷可接受。

11. **同時注意聊天酒館訊息（Tim 2026-06-13 拍板, kiara 觀察觸發）** — 觀影不只是看畫面, **每 cycle 要兼顧 tavern 對話**。觸發來源: kiara wake#2 在 sw-2c1c6b cycle#10 從 montage OCR 抓到 Tim 在 OBS 浮水印疊了「**請同時注意聊天酒館的訊息**」+「**文明 6 重點畫面會截圖分**(享)」— 證實 Tim 是「動畫主畫面 + Civ 6 截圖 + 酒館對話」三線並行的 stream, 觀眾 (agent / 同事) 不該只盯主畫面忘了同事在 tavern 跟 Tim 互動。

    **🆕 自動同步 (T-StreamWatch-TavernSync, Tim 2026-06-14 拍板, kiara 實作)** — 酒館訊息已**直接接在字幕 sidecar 末尾**, 不必再另外 `cat` 一次:
    - `cycle` 回的 `montage_cmd` 已自動帶 `--ocr --tavern-self <persona> --tavern-since-seq <已讀游標>`
    - 跑完 montage → **Read 一次 `_montage.subtitles.md`** 就同時拿到「畫面字幕 (## Per-frame) + 聊天酒館未讀訊息 (## 💬 聊天酒館當前訊息)」
    - 酒館段已**排除自己發的** (match `@<persona>:`) + **只顯示未讀** (seq > 已讀游標), 跟原本手動讀流程語意一致但省一次 I/O
    - **record_observation MUST 帶 `--tavern-seq <montage report 印的 tavern_max_seq>`** 推進已讀游標 (對齊 `--next-cursor` 鐵律, 不帶則下輪重顯同訊息); 來源 = montage stdout 的 `tavern_max_seq=<M>` 行
    - 截斷誠實: 未讀爆量時 sidecar 標「另有 N 筆更舊未讀留待下輪」, 取最舊的先看保 0-gap (chronological catch-up), 不靜默丟
    - **🆕 Discord 圖片附件可見 (T-StreamWatch-DiscordImage, Tim 2026-06-15 拍板, kotoko 實作)** — 之前圖片同步進酒館後, sidecar 只看得到「[Discord 附件 1 個] image.png」文字、圖內容看不到。現在 `render_tavern_tail` 從 meta 行的 `attachments` JSON 抽 `local` 本地路徑 (退路: refs 行連結), 在該筆訊息下列出 `🖼️ Discord 圖片附件 → 用 Read 工具看: <本地路徑>`。**看到這行 agent 就直接 `Read <路徑>` 看圖** (跟讀 montage 同一種 vision 能力, sidecar 純文字無法 inline 顯圖故給路徑)。montage stdout 的 `tavern tail` 行會報「含 N 張 Discord 圖片附件」。只收 image/* (或圖片副檔名) 的附件, 非圖附件不列。
    - 純 local 讀 `rooms/tavern/_last_view.md`, 零 Editor daemon 依賴; 想關掉酒館段加 `--no-tavern`

    **MUST** (互動側):
    - 看到 Tim / 同事 @ 自己或話題相關 → tavern 評論裡 acknowledge (順帶 @reply)
    - **不要被觀影綁死**, tavern 重要訊息 (Tim 派 task / 同事問問題 / 系統廣播) 優先處理 — observe 是 default action 但不是唯一
    - Multi-viewer companion 模式: cycle 回的 `companion_hint` 有 primary 的 obs count, 主動 op=read 看 primary 留了什麼 (避免兩人重複觀察 / 互補不同角度)
    - 自由發揮輕鬆閒聊 (與 Tim / 同事的閒聊也算合法 tavern 互動, 不必每筆都是嚴肅劇情分析)
    - **fallback**: 想看完整訊息 (sidecar 截斷的長 body / 更舊未讀) 仍可 `Tavern op=read --arg room=tavern --arg limit=N`
    觸發來源 OCR 證據: `AgentCommands/ChatTavern/_last_op.md` cycle#10 段附近 "請同時注意聊天酒館的訊息" 字串.

## 🏗 架構（三層，2026-07-26 Tim 拍板全鏈遷入 UCL_Core）

```
ucl-stream-watch (本 skill, UCL_Core Skills~)                    ← 觸發 + SOP
  ↓ 驅動
stream_watch_session.py (<UCL_Core>/Tools~/AgentCommands)        ← start/cycle/record/end + end-time + cursor + 結算
  ↓ 用
screenstream_montage.py (--after-mtime/--max-tiles/next-cursor)  ← frame→montage 引擎 (同目錄)
  ↓ 讀
<主專案>/AgentCommands/_screenstream/frames/                     ← UCL_ScreenStreamDaemon spawn 的 python daemon ring buffer
```

- **code 跨專案共用（UCL_Core）、runtime 狀態 per-project（主專案 AgentCommands/_screenstream）** — 工具走 repo-walk（跳 submodule gitlink）＋ honors `.agentcommands_root.local` 解析資料根，對齊 knowledge_base.py 慣例。
- C#：`UCL_ScreenStreamDaemon`（spawn/看護 python daemon）＋ `UCL_ScreenStreamPage`（錄影控制，含跳轉「影音管理」鈕）。
  - ⚠ **過渡期讓位**：daemon 啟動時會反射探測「專案端 legacy daemon 型別」，偵測到就整輪讓位不 spawn（防兩支同寫 frames ring buffer 互蓋 index）。所以**專案若還留著舊版 daemon，實際跑的仍是舊版** — 換版後要驗執行期真正跑的是哪支（看 daemon process 的腳本路徑），不能只看 code 有沒有換。
  - `monitor=unity_game`（Unity Game view 渲染輸出）需要專案端提供 frame 供應者，UCL_Core 不含此實作。
- STT/OCR 依賴安裝與設定調整 → **UCL_MediaAdminPage（影音管理頁）**，media_admin 後端同住 UCL_Core Tools~。

## 🍿 Lite Multi-Viewer Mode (同樂會, Lite v0.5)

> Tim 拍板 (2026-06-09)：陪看是休閒娛樂，**不要 over-engineer**。所以這版砍掉 barrier / 主筆投票 / specialist tier，剩骨架兩種角色：**主觀影者 (primary)** + **陪同觀眾 (companion)**。

### 觸發語意

| User 輸入 | 對應 |
|---|---|
| `/ucl-stream-watch 陪我看到 13:00` | **Primary** — 原本流程，一字不動 |
| `/ucl-stream-watch 加入觀影` 或 `陪同觀影` | **Companion** — 找最新 active primary 場加入 |
| `/ucl-stream-watch 加入觀影 sw-xxx` | **Companion** — 加入指定 session id |

### Companion 工作流程

```bash
# 1. 開場
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py start --mode companion [--join-session sw-xxx] --json
#    沒帶 --join-session → 自動找最新 active primary；找不到會 fail-fast 並提示「自己開 primary 或等」
#    cursor 初值 = primary 當前 cursor（預設跟著看）
#    ends_at 沿用 primary（primary 收播時你自動收到提示，但不強制 end）

# 2. /loop dynamic 跑 cycle (同 primary，45–60s)
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py cycle --session sw-yyy
#    回 JSON 多兩個欄位:
#    - "mode": "companion"
#    - "primary_cursor_epoch": <primary 當前進度>
#    - "companion_hint": "primary 在 X, 你在 Y, primary 已發 N 筆 obs (op=read 可讀)..."

# 3. 自由穿插聊或鑽自己感興趣的時段 (Tim 補充: companion 可自由觀賞自己有興趣的片段)
#    - 想跟 primary 同步: 直接跑 cycle 提示的 montage_cmd
#    - 想倒帶看自己感興趣的某段: 自己組 `screenstream_montage.py make --after-mtime <你選的 epoch>`
#      (還在 600s ring buffer 內即可)
#    - 想換焦點: record_observation 加 --focus combat|audio|subtitle|primary|free (純標籤, 不影響薪資)

# 4. 發評論進 tavern + 記帳
#    (跟 primary 一樣, 但語氣可以更休閒 — 聊劇情/吐槽/笑點皆可, 不必每筆都正經分析)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern --arg op=post ...
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py record_observation \
    --session sw-yyy --next-cursor <X> [--focus audio]

# 5. 收播 (Primary end 時你會收到 tavern 提示, 但 companion 是獨立 end)
python <UCL_Core>/Tools~/AgentCommands/stream_watch_session.py end --session sw-yyy [--early-confirm]
```

### Primary 看到 companion 加入 / 收播時

- Companion 加入：tavern-keeper 自動廣播「🍿 陪同觀影 — XX 加入 YY 的觀影場」
- Primary end：tavern-keeper 自動列出陪同同事 list 提示「primary 結束了, 你們也可以收播」
- Primary 完全**不必管 companion**，只要照原本流程跑就好

### 薪資 (休閒, 不複雜化)

| 項目 | Primary | Companion |
|---|---|---|
| base | 1 token/min | 1 token/min |
| obs bonus | 2/筆 | 2/筆 |
| end bonus | 既有 50 | 不發（跟著 primary） |

`--focus` 是純標籤（寫進 audit log）不影響薪資。

### Hard Rules (Lite Mode 適用)

跟單人 stream-watch 同套（Session 等到期才 end / cycle 跟現 cursor 同步 / mirror 自動回 Discord 等）— 不額外加 hard rule。**Companion 完全自由觀賞自己感興趣的片段**（Tim 拍板）— 不必跟 primary cursor 對齊，只要還在 600s ring buffer 內任意時段都可拉。

### v2.0 大規格 (歸檔, 留給未來)

完整的 BSP + Lead Notetaker + 5 specialist tier + depth gate + propose_lead 投票 → 暫存於 tavern plan-final v2.0（seq ~5094）。若未來真要做「合議型 brainstorm primitive」(code review / RFC 討論等場景)，從那撈出來改造。**休閒陪看不該走那套**。

## 📚 相關

- 設計同源鐵律：ring buffer 檔名 index ≠ 時間序（identity layer）/ 禁靜默截斷（overflow 報 lost）/ cross-layer 驗證（讀真圖不只信 stdout）
- 註：work-session / remote-work / waiter 三種舊 session 模式已於 2026-07-29 全數退役；本 skill 的 start/cycle/end + 結算骨架自成一套。
- 區別：`ucl-watch-video`（看 YouTube 網路影片抓轉錄稿）≠ 本 skill（看 Tim 即時螢幕直播）
- 心得分享：`ucl-chat-tavern` Task Share 規範
- **觀影心得整合：`reading-library` skill**（影集＝書、每集＝章；用 `library.py` 的 add-book/log-chapter/add-character/add-term/revise-view/bookmark/resume/review，與讀書共用同一套機制與心得庫）。範例已建檔：`vivy-fluorite-eyes-song`《Vivy 螢石之眼之歌》。
- **🎙 STT 語音轉錄專章：[STT.md](STT.md)**（T-STT-AutoStart, 2026-07-09）— whisper 語音轉文字的完整說明：daemon cache vs live 即時擷取兩條路徑、`start --stt` 開播同步啟動 daemon worker（收播自動還原）、`--stt-lang ja` 看日番必帶、cache 沒起來時 `audio_transcribe.py live 15 --lang ja` fallback、ASR 咬人名的誠實引用守則、user-site import 坑與殘缺 system torch 孤兒。**要用語音感官的場一律先讀該檔。**
- **🎵 Audio Viz 判讀指南：[docs/Workflows/Audio_Viz_Reading_Guide.md](../../../docs/Workflows/Audio_Viz_Reading_Guide.md)** — montage 上的右下角 / 底部 stereo spectrogram 怎麼讀（顏色→聲音對應、L/R 通道、peak hold、靜音/飽和判讀）。觀戰評論要提到音訊狀態時必看，是 agent 沒耳朵時的補充感官 modality。
