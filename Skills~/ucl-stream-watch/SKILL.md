---
name: ucl-stream-watch
description: |
  觀影模式 (Stream Watch) — 陪 Tim 看 ScreenStream 直播畫面流，或不開場只看一眼。
  走 `Cmd_StreamWatch` 分步（capture / peek / start / join / cycle / observe / note），
  每一步的回傳檔會告訴你下一步；**沒有 end —— 到期或 Tim 停錄影時由 Cmd 宣布收工並結算**。
  跟 ucl-watch-video (看 YouTube 影片抓轉錄稿) 是兩回事 — 本 skill 看的是 Tim 的即時螢幕。
  觸發詞 (case-insensitive substring): 看直播 / 觀看直播 / 陪看 / 陪我看直播 / 觀戰直播 / 直播陪看 /
    看直播到 / 看到幾點 / watch stream / stream watch / 連續觀看 / 觀戰模式 / 看一眼 / 瞄一眼 /
    加入觀影 / 陪同觀影 / 一起看 / 同樂會 / join watch / multi-viewer / companion / /ucl-stream-watch。
---

# UCL Stream Watch — 觀影模式

> **觸發詞就是命令。** 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路（`## next`）。
> 細節不寫在這裡：寫進 skill 的數字會過期而不會叫
> （🩸 舊版寫死 600s 保存期、實際 2400s，差四倍，還拿它教間隔紀律）。

## 兩條鐵律

1. **收工不由你判斷。** 沒有 `step=end`；到期或 Tim 停錄影時，下一次 `cycle` 自己宣布並結算。
   看到「我大概看完了」這個念頭 —— 那不是收工訊號，去跑 `cycle`。
2. **媒材鍵是共享鍵，不能由記憶供給。** 不確定就讓 Cmd blocked 給你既有清單，
   **片名不確定問 Tim，不要猜**。取錯名 ⇒ 既有 reader 的心得對新場次永遠隱形**且不報錯**。

## 第一步（唯一要背的一步）

**開/關錄影**（沒在錄就沒有畫面可看 —— 與 Editor 頁那顆按鈕同一條規則）：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run StreamWatch --arg step=capture --arg persona=<P> --arg on=1
```

**只看一眼**（不開場／不記帳／不發文，也是管線測試探針）：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run StreamWatch --arg step=peek --arg seconds=60
```

**正式開場**（要寫評論、要計酬、要留接續點）：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run StreamWatch \
    --arg step=start --arg persona=<P> --arg until=<HH:mm> --arg media=<work-slug>
```

- 跑完 **Read run_cmd 印出的 `📄 回傳檔：<路徑>`** —— 裡面的 `## next` 就是後續每一步
  （`cycle` → Read 縮圖牆/字幕 → `observe` → …）。**照它走，不用背。**
- `media` 不給 ⇒ Cmd 會擋下並列出既有 work 清單（命中就用）。
  bilibili 一律 `bilibili-<up主 slug>` ＋ `--arg up=<up主名>`。
- 陪別人的場：`--arg step=join --arg persona=<P>`（自動繼承 primary 的媒材身分）。
- 長內文一律 `--arg-file body=<檔>`，不要 inline。

## 回傳檔要會讀的三行（判準，不是裝飾）

- **窗口對帳** `窗口尾端 X ≤ 水位 Y ✅` —— 出現 `>` 或「未夾」時，
  **尾端那幾格的「沒有字幕」不可信**（沒字幕與還沒辨識同形）。
- **STT** `0 段` ≠ `無 cache` ≠ 這一行不存在（第三種是管線沒跑起來）。
- **保存期** 名目只是設定值換算，**實有才是現在真的回得去多久**（兩個方向都會差）。

## ⛔ 不可做

- ❌ 自己判斷「時間到了」而停手 —— 時限只認 Cmd 的時鐘，不認收束感。
- ❌ 憑印象取 `media` slug；❌ 用 `bilibili-stream` 這種泛名（會把所有影片併成一個 work）。
- ❌ 直跑 `stream_watch_session.py`（**舊 prototype，已停用**）或自己去跑 `screenstream_montage.py`
  —— 繞過收銀台的帳不算數，且不會有窗口對帳。
- ❌ 評論裡寫自己數的 frame 數／時間 —— 數字一律引用回傳檔的讀數。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 怎麼操作 Cmd、起始步驟（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/StreamWatch_Cmd_Flow.md` |
| 七步全參數／窗口演算法／session schema／計酬／blocked 全表（**維護用，平常不用讀**） | `ucl_core:Docs~/zh-Hant/Workflows/StreamWatch_Cmd_Reference.md` |
| 設計沿革與拍板（為什麼沒有 end／為什麼夾感官水位） | `ucl_core:Docs~/zh-Hant/Plan/Plan_StreamWatch_Cmd.md` |
| 觀影心得寫進 Library | `reading-library` skill |
