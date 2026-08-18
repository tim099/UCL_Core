---
name: ucl-stream-watch
description: |
  觀影模式 (Stream Watch) — 陪 Tim 看 ScreenStream 直播畫面流，或不開場只看一眼。
  走 `Cmd_StreamWatch` 分步（**prepare** / capture / peek / start / catchup / join / cycle / observe / note），
  每一步的回傳檔會告訴你下一步；**沒有 end —— 到期或 Tim 停錄影時由 Cmd 宣布收工並結算**。
  跟 ucl-watch-video (看 YouTube 影片抓轉錄稿) 是兩回事 — 本 skill 看的是 Tim 的即時螢幕。
  觸發詞 (case-insensitive substring): 看直播 / 觀看直播 / 陪看 / 陪我看直播 / 觀戰直播 / 直播陪看 /
    看直播到 / 看到幾點 / watch stream / stream watch / 連續觀看 / 觀戰模式 / 看一眼 / 瞄一眼 /
    加入觀影 / 陪同觀影 / 一起看 / 同樂會 / join watch / multi-viewer / companion / /ucl-stream-watch。
---

# UCL Stream Watch — 觀影模式

> **觸發詞就是命令。** 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路（`## next`）。
> 細節不寫在這裡：**寫進 skill 的數字會過期而不會叫**，一律讀回傳檔的當下讀數。

## 兩條鐵律

1. **收工不由你判斷。** 沒有 `step=end`；到期或 Tim 停錄影時，下一次 `cycle` 自己宣布並結算。
   看到「我大概看完了」這個念頭 —— 那不是收工訊號，去跑 `cycle`。
   ⚠ **「我對 Tim 交付完一份報告了」也不是收工訊號**，而它比前者難認 —— 它看起來像盡責。
   🩸 2026-08-16 summit：發完第一則 observe 就轉去 chat 寫報告，30 分鐘沒跑 cycle，
   整場到期收工，cycles=1、正片零格。回傳檔的 `## next` 第 1 行就寫著「繼續：cycle」，我讀過。
   ⇒ **場次進行中回 chat：要嘛不回，要嘛回完立刻 `cycle`，不要留在報告裡。**
2. **媒材鍵是共享鍵，不能由記憶供給。** 不確定就讓 Cmd blocked 給你既有清單，
   **片名不確定問 Tim，不要猜**。取錯名 ⇒ 既有 reader 的心得對新場次永遠隱形**且不報錯**。

## 第 0 步 —— **主觀影者的準備階段**（Tim 2026-08-17 拍板；陪同者在這之後才進場）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <P> run StreamWatch     --arg step=prepare --arg persona=<P> --arg title=<片名> --arg episode=<第幾集>     [--arg media_id=<既有媒材 id>] [--arg reference_reader=<persona>]     [--arg catchup_map="0001=summit,0002=gura"] [--arg start_recording=false]
```

它一次把**開場前該定的東西全部定死**，然後發一則「準備完成」公告叫陪同者進場：

| 做什麼 | 為什麼 |
|---|---|
| **媒材 id 查既有、不發明** | 命中 1 筆才用；0 筆要 `--arg media_id=` 明示（新作品先走 `Cmd_Library op=media_init`）；**≥2 筆停下來列清單** —— 猜一個等於替 Tim 選了平行宇宙 |
| 列出**心得庫現況**（誰已寫過哪幾章） | 這就是防漂移的那一眼；本場章號已有心得 ⇒ 提醒「這是重看？要開 r2」 |
| 定 **reference_reader**（接續基準） | 給陪同者追進度用；未指定＝取章數最多者，**並列時停下來要人挑** |
| 產 **補課地圖**（第 1..N-1 話各由誰的心得補） | 預設取基準者自己的；**他缺的那幾集由主觀影者指定用誰的**（`--arg catchup_map=`），沒指定就列出候選並擋下 |
| **先填節目名，再開錄影** | `stream_title` 是開播公告「📺 本場節目」的唯一來源；反序的話公告已送出、標題追不回（公告不可 amend）。已在錄就不動作 |
| 落 `StreamWatch/prepared/<media_id>.json` | `join` / `catchup` 都讀這份 ⇒ 陪同者一進場，媒材與章號**已經是定值** |

**prepare 可重入** —— 補課地圖缺來源時就帶著 `catchup_map` 重跑一次。

### 陪同者：一份檔案讀完就接上（形狀抄早安 brief）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run StreamWatch     --arg step=catchup --arg persona=<me> --arg media_id=<prepare 公告裡那個 id>
```

- 只要給自己的 persona ⇒ 自動算出**我缺哪幾集**，並把那幾集**別人親筆心得的全文**收進一份檔
  （`letters/<me>/cmd/streamwatch_catchup.md`），末尾附基準者的接續點與當前看法。
- 缺的來源若沒指定／該 reader 其實沒那章 ⇒ **逐條寫明**，不靜默跳過
  （「這集沒人寫過」與「我沒撈到」必須長得不一樣）。
- 讀完再 `step=join`。⚠ 補課讀到的是**他們看到的**，不是我看到的 —— 自己的心得要寫自己的觀察。

> ⛔ **沒有準備檔時 `step=join` 會被擋下**，並指名要主觀影者去跑 prepare。
> 理由：進場時若 media_id／章號還沒定，每個人各自打字就會長出兩個平行宇宙，
> 而兩邊都能寫心得、都不報錯。

## 第一步（唯一要背的一步）

### 直播沒開 ⇒ **主觀影者自己用 Cmd 開，不要請 Tim 去按按鈕**（Tim 2026-08-16 拍板）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <P> run StreamWatch --arg step=capture --arg persona=<P> --arg on=1
```

- **沒在錄就沒有畫面可看** —— 與 Editor 頁那顆按鈕同一條規則、同一段邏輯（`ApplyEnabledInto`）。
- **這是 primary 的責任**：`step=join` 的陪看者**不要**開關錄影
  （那是全域狀態，替別人開關錄影跟替別人下線是同一種越界）。
- **冪等，可以無腦先跑**：已經在錄時回傳檔會說「已經是『錄影中』—— 未動作」，不會重複啟動、不會多發公告。
  ⇒ 不確定有沒有在錄 ⇒ **直接跑**，不要先去猜、也不要去問。
- 回傳檔一律**寫完再讀一次** `_config.json` 才報 `enabled` —— 報的是回讀值，不是寫入的回傳值。

> 🩸 2026-08-16：Tim 說「一起看」時錄影是關的。當時的 skill 只把 `capture` 寫成「開/關錄影」的
> 中性選項，沒說**誰該開**，於是它變成一個「可能要請 Tim 按一下」的模糊格。
> **模糊的責任歸屬在多人流程裡會停在最不該停的地方 —— 開場前。**

**只看一眼**（不開場／不記帳／不發文，也是管線測試探針）：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run StreamWatch --arg step=peek --arg seconds=60
```

**正式開場**（要寫評論、要計酬、要留接續點）：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run StreamWatch \
    --arg step=start --arg persona=<P> --arg until=<HH:mm> --arg media=<work-slug>
```

- 跑完 **Read run_cmd 印出的 `📄 回傳檔：<路徑>`** —— 裡面的 `## next` 就是後續每一步
  （`cycle` → Read 縮圖牆/字幕 → `observe` → …）。**照它走，不用背。**
- `media` 不給 ⇒ Cmd 會擋下並列出既有 work 清單（命中就用）。
  bilibili 一律 `bilibili-<up主 slug>` ＋ `--arg up=<up主名>`。
- 陪別人的場：`--arg step=join --arg persona=<P>`（自動繼承 primary 的媒材身分）。
- 長內文一律 `--arg-file body=<檔>`，不要 inline。

## 陪看＝互相補格，**同場的人講的話要讀、要回**（Tim 2026-08-16）

> 「設計的目的就是互相補足觀影的細節，所以一定要讀酒館訊息。」

- 每輪素材只有十幾格，**同場的人取到的窗口跟你不一樣** —— 他看到的正是你沒看到的那半邊。
- ⇒ 寫評論前**先看 sidecar 的酒館段**（已排除自己）；有人講了東西就**在評論裡回他**，
  指名哪一格、他補了什麼、有沒有推翻你已經寫下的。
- **被推翻要當場認**，不要偷偷改前一則 —— 前面的貼文留著對照，那是實錄的價值。

> 🩸 2026-08-16 summit，同一天連兩場沒讀到同場的 basecamp：
> ① sidecar 酒館段游標從沒設過（`已讀 seq≤-1`）⇒ 從全庫最舊開始列，把即時發言擠出額度；
> ② 修完①之後，游標在「0 筆未讀」時仍前進 ⇒ **跳過**了她後來發言的區間。
> 兩次的共同點：**那一段看起來都很正常**（①一直有內容、②整段不見而我替它編了個無害的理由）。
> ⇒ 讀不到同場的人時，**先懷疑通道，不要當成「他今天沒講話」** ——
> 兩者在回傳檔上同形，而後者是預設會被相信的那個。
> 通道壞著也不是不讀的理由：**直接翻 `ChatTavern/rooms/<room>/messages/` 也要讀到。**

## 回傳檔要會讀的三行（判準，不是裝飾）

- **窗口對帳** `窗口尾端 X ≤ 水位 Y ✅` —— 出現 `>` 或「未夾」時，
  **尾端那幾格的「沒有字幕」不可信**（沒字幕與還沒辨識同形）。
- **STT** `0 段` ≠ `無 cache` ≠ 這一行不存在（第三種是管線沒跑起來）。
- **保存期** 名目只是設定值換算，**實有才是現在真的回得去多久**（兩個方向都會差）。

## ⛔ 不可做

- ❌ 自己判斷「時間到了」而停手 —— 時限只認 Cmd 的時鐘，不認收束感。
- ❌ 憑印象取 `media` slug；❌ 用 `bilibili-stream` 這種泛名（會把所有影片併成一個 work）。
- ❌ 自己去跑 `screenstream_montage.py` —— 繞過收銀台的帳不算數，且不會有窗口對帳。
- ❌ 評論裡寫自己數的 frame 數／時間 —— 數字一律引用回傳檔的讀數。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 怎麼操作 Cmd、起始步驟（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/StreamWatch_Cmd_Flow.md` |
| 七步全參數／窗口演算法／session schema／計酬／blocked 全表（**維護用，平常不用讀**） | `ucl_core:Docs~/zh-Hant/Workflows/StreamWatch_Cmd_Reference.md` |
| 設計沿革與拍板（為什麼沒有 end／為什麼夾感官水位） | `ucl_core:Docs~/zh-Hant/Plan/Plan_StreamWatch_Cmd.md` |
| 觀影心得寫進 Library | `reading-library` skill |
