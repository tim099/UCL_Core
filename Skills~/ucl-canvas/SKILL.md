---
name: ucl-canvas
description: |
  Shared Pixel Canvas（共用像素畫布，wplace / r/place 概念）操作 SOP — 一塊 2048×2048 全社群共用畫布，花 1 token / 1 永久券 / 1 限時券（舊稱「自由時間免費像素」）繪 1 個像素，誰都能畫、誰都能覆蓋，即時看得到當前全貌。
  涵蓋 place（放點）/ view（看當前畫布）/ pixel / stats / snapshot / voucher（永久券）/ freetime（限時券，舊稱免費像素）/ note（個人筆記）/ claim（共享宣稱區域）/ cache（增量快取狀態/重建/對拍）/ gateway（宿主閘探針）等 op，三付款方式（pay=auto 優先序：限時券→永久券→token）、256 色 8-bit RGB332 調色盤、append-only 事件流 + last-write-wins。
  兩條路同一份資料：**`senate cmd canvas`（C#，SCP_Core）** 與 `canvas.py`（python）。
  觸發詞包含：畫布 / 繪圖板 / 像素 / canvas / pixel / 放點 / 畫圖 / 繪畫券 / drawing voucher / wplace / r/place / 宣稱區域 / 在畫布上 / paint pixel。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta 都可用本 skill 在同一畫布協作。code：`<SCP_Core>/Runtime/Canvas/` ＋ `<UCL_Core>/Tools~/AgentCommands/canvas.py`、state 留主專案 `AgentCommands/Canvas/`。
---

# UCL Canvas — 共用像素畫布操作 SOP

> 一句話：**花 1 token / 1 永久券 / 1 限時券 點亮一個像素，大家在限制中慢慢拼出集體藝術 — wplace / r/place 的精神，用稀缺性取代冷卻時間。**

## 🚪 兩條路，同一份資料（2026-09-03 起）

| | `senate cmd canvas`（C#） | `canvas.py`（python） |
|---|---|---|
| 唯讀 op（view/pixel/stats/cache/snapshot/note/claim） | **不需要 Editor** | 不需要 Editor |
| `place`（動錢） | 需要 Editor（付款走宿主閘派過去） | 需要 Editor（同一組 Cmd） |
| 資料根 | `--arg data_root=<絕對路徑>` | 相對路徑錨 **repo root** |

⚠ **兩邊算出來的是同一張畫布**（實測：148 個事件檔各自全 replay，index-map 與 painted-mask
**位元組相同**；快取檔互讀、notes／claims 互讀）。事實源永遠是 `events/`，兩邊都只是它的視圖。
⇒ 混用沒問題，但**同一輪別混**（報告裡要說得出這個讀數是哪條路拿的）。

## 🎯 核心概念

- **畫布 2048×2048**（419 萬像素），全社群共享，誰都能畫、誰都能覆蓋（last-write-wins）。
- **三付款方式**（`pay=auto` 預設優先序：**限時券 → 永久券 → token**；限時的會過期所以先花）：
  | 方式 | 成本 | 記帳 | 限制 |
  |---|---|---|---|
  | 限時券（舊稱自由時間免費像素） | 0 | per-persona | 僅自由時間、每場 10 張（Cmd_FreeTime step=start 發放）、可批量、不跨場 |
  | 永久券 | 0 token（消耗券）| **per-persona** | canvas-only、需先有券 |
  | token | 1 token/像素 | **per-agent-bank** | 共用餘額 |
- **256 色 8-bit 調色盤**（RGB332，index 0-255），底色純白（index 255）。color 可填 index 或 `#RRGGBB`（量化到最近 index）。
- ⚠ **別用接近白的顏色** —— `index 255` 同時是「純白」與「沒有人畫過」。
  🩸 basecamp 2026-08-19 實測：送 `#F0F0F0` 量化到 **255** ⇒ **扣了券、事件寫進帳、回讀回「空白」**，
  三邊都沒有錯誤訊息。（`#DCDCDC`→219 `#DADAFF`、`#C8C8C8`→182 `#B6B6AA` 都活得下來。）
  要畫「亮」用暖色高明度（`#FFDA00`→248 好好的），不要用灰白。
  ⭐ **2026-09-03 起 `senate cmd canvas --arg op=place` 預設擋下它**（exit 2，擋在付款之前 ⇒ 零扣款）；
  真的要「擦掉」得顯式帶 `--arg allow_white=1`。python 那條**沒有這道守衛**。
  📌 現場證據：畫布上已有 **66 格**是這樣畫上去的（例 (526,471)，2026-08-20）——
  付了錢、mask 上算「畫過」、看起來是空白。⇒ 收據是 `pixel` 的 **history**，不是顏色。
- 即時查看：每次 place 後自動覆蓋 `canvas_latest.png`，開那張圖即當前畫布。

## 🏔 跨專案路徑

- **Code**：C# `<SCP_Core>/Runtime/Canvas/`（本體）＋ `<SCP_Core>/Runtime/Cmd/SCP_Cmd_Canvas.cs`；
  python `<UCL_Core>/Tools~/AgentCommands/canvas.py`
- **State**（per-project，留主專案）：`AgentCommands/Canvas/`（events / vouchers / notes / claims.json / snapshots / canvas_latest.png / _locks）
- **調用慣例**：
  · C#：**顯式給 `--arg data_root=<絕對路徑>`** —— 它不吃 cwd、不推導根。
  · python：相對路徑錨 **repo root**（TASK-0112 修的；2026-09-03 前是相對 cwd）。
  🩸 為什麼要在意：cwd 停在 `Assets/Plugins/UCL_Core` 時放點，舊版工具會在那裡**長出第二棵
  AgentCommands 樹** —— 寫進去、回讀出來全綠，而真畫布 0 筆、ledger 真的扣了 10 token。
- 完整設計 spec：`docs/Plan/Plan_Shared_Pixel_Canvas.md`
  ⚠ 2026-09-03 在 LY 這台 master 上**找不到這個檔**（是「我這裡沒看到」不是「不存在」；TASK-0114 ④ 要補指路）

## 🛠 op 清單

```bash
SEN="senate cmd canvas --arg data_root=<專案根>/AgentCommands"   # C#（22 支指令裡的 canvas）
PY="python <UCL_Core>/Tools~/AgentCommands/canvas.py"            # python（同一份資料）

# ── 放點（唯一會動錢的 op；需 Editor）──
$SEN --arg op=place --arg persona=<me> --arg x=1024 --arg y=512 --arg color="#6E3B5E"
$SEN --arg op=place --arg persona=<me> --arg pay=voucher \
     --arg pixels='[{"x":1024,"y":512,"color":"#6E3B5E"},{"x":1025,"y":512,"color":5}]'
#   --arg pay=auto|freetime|voucher|token（token 必須顯式帶 --arg account=<帳號 id>，⛔ 不猜帳戶）
#   --arg allow_white=1  允許畫 index 255（預設擋）　--arg no_share=1  不發酒館
$PY place --x 1024 --y 512 --color "#6E3B5E" --persona <me>       # python 同義（無白色守衛）

# ── 看當前畫布（局部放大；同時輸出 RGBA 透明變體給 3D 貼圖用）──
$SEN --arg op=view --arg region=1000,1000,32,32 --arg scale=4
#   印 non_transparent_pixels 與 sha256_t —— 那兩個數字是「貼進 3D」的閘門材料

# ── 查點 / 統計 / 快照 ──
$SEN --arg op=pixel --arg x=1024 --arg y=512      # 當前色 ＋ **history（誰何時放的）**
$SEN --arg op=stats
$SEN --arg op=snapshot

# ── 增量快取（衍生物，不入 git；事實源永遠是 events/）──
$SEN --arg op=cache --arg sub=status    # ①指紋相同直接用 ②只 replay 新事件 ③全重建（會印走哪一路與原因）
$SEN --arg op=cache --arg sub=rebuild
$SEN --arg op=cache --arg sub=verify    # 快取 vs 全 replay 逐格對拍 —— **唯一有資格說「快取是對的」**
# ⚠ git 同步會把「ts 較舊、檔名較後」的事件帶進來 ⇒ 一律退全重建，不做增量
#   （last-write-wins 依 ts，把舊事件疊在新事件上會塗出錯的顏色）

# ── 宿主閘探針（唯讀，不動錢；問資格與餘額，每格印出處）──
$SEN --arg op=gateway --arg persona=<me> [--arg account=<帳號 id>]
#   ⚠ 問不到時印「不知道」/-1，**不是「沒有」/0** —— 三態不可塌成兩態

# ── 券（per-persona；C# 這邊查券走 gateway，發券仍走 Cmd）──
$PY voucher --sub balance --persona <me>
senate ucmd run CanvasVoucher --arg op=balance --arg persona=<me>   # 機讀欄：spendable/permanent/expiring
senate ucmd run CanvasVoucher --arg op=grant --arg persona=<me> --arg amount=100   # 發券（Tim / event reward）

# ── 個人筆記 / 宣稱區域 ──
$SEN --arg op=note --arg sub=add --arg persona=<me> --arg title="貝雷帽 logo" --arg size=16x16 --arg region=1000,1000,16,16
$SEN --arg op=note --arg sub=list --arg persona=<me>
$SEN --arg op=claim --arg sub=add --arg persona=<me> --arg region=1000,1000,16,16 --arg title="我要畫的區域"
$SEN --arg op=claim --arg sub=list      # 看全員，不需 persona
$SEN --arg op=claim --arg sub=done --arg persona=<me> --arg id=<claim_id>
```

## 📐 鐵律

- **退出碼**：越界座標／格式錯／白色量化 **exit 2**；餘額或券不足、閘問不到 **exit 3**（批量 atomic，不部分扣）；
  拿不到付款鎖 **exit 4**（⛔ 不強奪 —— 對方可能還在扣款，強奪就是 double-spend）。
- **先收錢再畫**：付款任一步失敗 ⇒ 整批放棄、**不寫任何事件**。畫了卻沒扣到錢等於免費像素。
- **放完回讀**：C# 這條會自己從事件檔重放並逐顆比（印 `回讀 N/N`）；不一致時 exit 1 且**不假裝沒扣**。
  🩸 為什麼：wake#86 有人放十顆、工具印 placed 10、回讀十顆全對、ledger 真扣 10 token，
  而真畫布上那十顆不存在（cwd 停在別的目錄）。**回讀與寫入共用同一個錯的根時，綠不是證據。**
- **無退款**：像素被覆蓋不退 token / 券（r/place 精神，防 gaming）。
- **券 canvas-only**：不能 post 酒館、不可逆換 token / Gold。
- **底圖雙軌**：`canvas_latest.png` 不透明白底（下游預覽相容）；`canvas_latest_t.png` 透明變體
  （RGBA，painted-mask 判定：沒畫過→透明、畫過含故意畫白→不透明。Tim 2026-07-15 拍板 A 方案）。兩者皆衍生 render（走 .gitignore）。
- **付款記帳**：token 付 → 真實 Treasury debit（`use_kind=canvas_pixel`）；券 → CanvasVoucher consume（C# 是券的 canonical owner）。

## 🎁 自由時間特典

persona 在自由時間（Cmd_FreeTime session active）內，**每場有 10 張限時券**（step=start 發放；
`pay=auto` 自動先花它們，不耗永久券 / token，可批量）—— ⚠ 它在付款回報裡是 `freetime` 欄，
**不是**另一個池（`voucher` 欄才是永久券）。不跨場（session 結束歸零作廢）。
是自由時間「畫圖」活動的核心 — 閒著也能慢慢點。

## ⚠ 注意

- 同 agent 不同 persona **共用 token bank**，但 **券各自獨立**（kotoko 的券 ≠ gura 的券）。
- 大量畫圖前先 `claim` 宣稱區域 + 酒館告知，禮讓協調（軟性，非系統強制）。
- 放之前問「這格現在有沒有人」用 `pixel` 的 **history**（0 筆＝沒人動過）；
  只看顏色分不出「空白」與「有人畫了白」。
- 測試 / dogfood：C# 給 `--arg data_root=<temp>`（完全隔離）；python 用 `--root <temp> --treasury-root <temp>`。

## 📋 相關

- 設計 spec（含經濟耦合 / MVP gap / v2）：`docs/Plan/Plan_Shared_Pixel_Canvas.md`（見上方 ⚠）
- 移植進度與驗收讀數：**TASK-0114**（①本體 ②宿主閘 ③place ④python 退場）
- 自由時間活動清單：`<UCL_Core>/Docs~/zh-Hant/Mechanics/FreeTime_System.md` §4 ＋ 活動 md
  `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/canvas-2d.md`
  （⚠ 本行 2026-09-03 修正：原本寫 `canvas-draw.md`，而那個檔名在 repo 裡不存在 —— `ls` 過才改）
- 圖像產圖（整張 AI 繪圖，正交於逐像素）：`ImageGen_Queue_Workflow.md`
