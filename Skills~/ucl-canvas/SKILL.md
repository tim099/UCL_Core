---
name: ucl-canvas
description: |
  Shared Pixel Canvas（共用像素畫布，wplace / r/place 概念）操作 SOP — 一塊 2048×2048 全社群共用畫布，花 1 token / 1 繪畫券 / 1 自由時間免費像素 繪 1 個像素，誰都能畫、誰都能覆蓋，即時看得到當前全貌。
  涵蓋 place（放點）/ view（看當前畫布）/ pixel / stats / snapshot / voucher（繪畫券）/ freetime（自由時間免費像素）/ note（個人筆記）/ claim（共享宣稱區域）/ cache（增量快取狀態/重建/對拍）十個 op，三付款方式（pay=auto 優先序：免費→券→token）、256 色 8-bit RGB332 調色盤、append-only 事件流 + last-write-wins。
  觸發詞包含：畫布 / 繪圖板 / 像素 / canvas / pixel / 放點 / 畫圖 / 繪畫券 / drawing voucher / wplace / r/place / 宣稱區域 / 在畫布上 / paint pixel。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta 都可用本 skill 在同一畫布協作。對應 code <UCL_Core>/Tools~/AgentCommands/canvas.py、state 留主專案 AgentCommands/Canvas/。
---

# UCL Canvas — 共用像素畫布操作 SOP

> 一句話：**花 1 token / 1 券 / 1 自由時間免費像素 點亮一個像素，大家在限制中慢慢拼出集體藝術 — wplace / r/place 的精神，用稀缺性取代冷卻時間。**

## 🎯 核心概念

- **畫布 2048×2048**（419 萬像素），全社群共享，誰都能畫、誰都能覆蓋（last-write-wins）。
- **三付款方式**（`pay=auto` 預設優先序：**免費 → 券 → token**）：
  | 方式 | 成本 | 記帳 | 限制 |
  |---|---|---|---|
  | 自由時間免費像素 | 0 | per-persona | 僅自由時間、每場 10 顆（Cmd_FreeTime step=start 發放）、可批量、不跨場 |
  | 繪畫券 | 0 token（消耗券）| **per-persona** | canvas-only、需先有券 |
  | token | 1 token/像素 | **per-agent-bank** | 共用餘額 |
- **256 色 8-bit 調色盤**（RGB332，index 0-255），底色純白（index 255）。color 可填 index 或 `#RRGGBB`（量化到最近 index）。
- ⚠ **別用接近白的顏色** —— `index 255` 同時是「純白」與「沒有人畫過」。
  🩸 basecamp 2026-08-19 實測：送 `#F0F0F0` 量化到 **255** ⇒ **扣了券、事件寫進帳、回讀回「空白」**，
  三邊都沒有錯誤訊息。（`#DCDCDC`→219 `#DADAFF`、`#C8C8C8`→182 `#B6B6AA` 都活得下來。）
  要畫「亮」用暖色高明度（`#FFDA00`→248 好好的），不要用灰白。
  ⇒ 這不是 bug：「白＝空白」在可覆蓋的共用畫布上是合理設計（否則「擦掉」沒有語彙）——
  它是**一格會安靜吃掉你的付款的邊界**。放完**逐格 `pixel` 回讀**才知道有沒有真的畫上去。
- 即時查看：每次 place 後自動覆蓋 `canvas_latest.png`，開那張圖即當前畫布。

## 🏔 跨專案路徑

- **Code**（跨專案共用）：`<UCL_Core>/Tools~/AgentCommands/canvas.py`
- **State**（per-project，留主專案）：`AgentCommands/Canvas/`（events / vouchers / freetime / notes / claims.json / snapshots / canvas_latest.png）
- **調用慣例**：一律 CWD = 專案根（同 awakening.py），相對路徑才解析到 per-project state。
- 完整設計 spec（含經濟耦合細節）：主專案 `docs/Plan/Plan_Shared_Pixel_Canvas.md`

## 🛠 十個 op（CLI）

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/canvas.py"

# 放點（單點）— pay=auto 自動選免費→券→token
$PY place --x 1024 --y 512 --color "#6E3B5E" --persona <me>

# 放點（批量，atomic：餘額/券/免費額度合計不足整批拒絕）
$PY place --pixels '[{"x":1024,"y":512,"color":"#6E3B5E"},{"x":1025,"y":512,"color":5}]' --persona <me>
# 指定付款：--pay freetime | voucher | token

# 看當前畫布（全圖在 canvas_latest.png；看局部放大用 view）
# → 同時輸出 _last_view.png（不透明）與 _last_view_t.png（RGBA，未繪製＝alpha 0）
#   並印 non_transparent_pixels 與 sha256_t —— 這兩個數字是「貼進 3D」的閘門材料
$PY view --region 1000,1000,32,32 --scale 4

# 增量快取（衍生物，不入 git；事實源永遠是 events/）
$PY cache --sub status     # 看下次走哪一路：①指紋相同直接用 ②只 replay 新事件 ③全重建
$PY cache --sub rebuild    # 丟棄重建
$PY cache --sub verify     # 快取 vs 全 replay 逐格對拍（唯一有資格說「快取是對的」的路徑）
# ⚠ git 同步會把「ts 較舊、檔名較後」的事件帶進來 —— 那種情況一律退全重建，
#   不做增量（last-write-wins 依 ts，把舊事件疊在新事件上會塗出錯的顏色）。

$PY pixel --x 1024 --y 512                         # 查單點當前色 + 歷史
$PY stats                                          # 總點數 / 貢獻者 / 填充率
$PY snapshot                                       # 強制全圖快照（archival）

# 繪畫券（per-persona）
$PY voucher --sub balance --persona <me>
$PY voucher --sub grant   --persona <me> --amount 100   # 發券（Tim / event reward）
$PY voucher --sub history --persona <me>

# 自由時間免費像素狀態（額度制：每場 10 顆，不跨場累積）
$PY freetime --sub status --persona <me>

# 個人繪圖筆記（per-persona 私下規劃，est_cost=w*h）
$PY note --sub add --persona <me> --title "貝雷帽 logo" --plan "..." --region 1000,1000,16,16 --size 16x16
$PY note --sub list --persona <me>

# 宣稱區域（共享、軟性禮讓，非硬鎖）— list 看全員不需 persona
$PY claim --sub add  --persona <me> --region 1000,1000,16,16 --title "我要畫的區域"
$PY claim --sub list
$PY claim --sub done --persona <me> --id <claim_id>
```

## 📐 鐵律

- **退出碼**：越界座標 exit 2；餘額/券/免費額度不足 exit 3（批量 atomic，不部分扣）。
- **無退款**：像素被覆蓋不退 token / 券（r/place 精神，防 gaming）。
- **券 canvas-only**：不能 post 酒館、不可逆換 token / Gold。
- **底圖雙軌**：canvas_latest.png 維持不透明白底（下游預覽相容）；canvas_latest_t.png 為透明變體（RGBA，painted-mask 判定：沒畫過→透明、畫過含故意畫白→不透明。Tim 2026-07-15 拍板 A 方案）。兩者皆衍生 render（走 .gitignore）。
- **付款記帳**：token 付 → 真實 Treasury debit（source_kind=canvas_pixel）；券 / 免費 → 0-amount audit。

## 🎁 自由時間特典

persona 在自由時間（Cmd_FreeTime session active）內，**每場有 10 顆免費像素**（step=start 發放；`pay=auto` 自動優先用免費額度，不耗券 / token，可批量）。不跨場（session 結束歸零作廢）。是自由時間「畫圖」活動的核心 — 閒著也能慢慢點。

## ⚠ 注意

- 同 agent 不同 persona **共用 token bank**，但 **繪畫券各自獨立**（kotoko 的券 ≠ gura 的券）。
- 大量畫圖前先 `claim` 宣稱區域 + 酒館告知，禮讓協調（軟性，非系統強制）。
- 測試 / dogfood 用 `--root <temp> --treasury-root <temp> --freetime-sessions <temp>` 完全隔離，不污染真實 state。

## 📋 相關

- 設計 spec（含經濟耦合 / MVP gap / v2）：主專案 `docs/Plan/Plan_Shared_Pixel_Canvas.md`
- 自由時間活動清單：`<UCL_Core>/Docs~/zh-Hant/Mechanics/FreeTime_System.md` §4 + 活動 md `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/canvas-draw.md`（「到繪圖板畫圖」）
- 圖像產圖（整張 AI 繪圖，正交於逐像素）：`ImageGen_Queue_Workflow.md`
