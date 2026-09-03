---
title: 3D 體積雕刻系統（Sculpture）— 分工契約與計費規格
slug: sculpture-3d
status: v1 shipped（2026-08-13 Tim 拍板；gura 引擎 + summit 扣費/Cmd 同日落地）；
  v1.1 貼圖進 3D（2026-08-14 Tim 拍板全面改道走 RGBA PNG + 自動建立作品）
last_updated: 2026-08-14
created_at: 2026-08-13T06:50:00Z
created_by: summit
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Tools~/AgentCommands/sculpt.py | 引擎本體（gura） | 幾何/渲染/快取，不碰錢
  - ucl_core:Docs~/{lang}/Plan/Plan_FreeTime_Cmd.md | 自由時間 Cmd | 免費像素發放端
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_CanvasVoucher.md | 繪圖券 | 券的 canonical owner
---

# 3D 體積雕刻 — 分工契約與計費規格

## 0. 一句話（Tim 2026-08-13 拍板）

256³ voxel 共用雕刻空間：**禁覆蓋（box 只填真空、carve 唯一移除通道）＋批發計費
（⌈實際落地/100⌉）＋觀測三式（region／exclude-color／多視角）＋增量快取**。
分工：**gura＝sculpt.py 引擎（幾何/渲染/快取，不碰錢）；summit＝Cmd_Sculpture（計費/付款/回傳）**。

## 1. 分工邊界（API 契約，de facto＝實際 CLI）

```
sculpt.py（gura）——引擎，錢不進來：
  box   --x1..--z2 --color --persona → stdout JSON {placed_count, skipped_count, total_volume, event_file}
  carve --x1..--z2 --persona         → stdout JSON {carved_count, event_file}
  view  [--region x1..x2,y1..y2,z1..z2] [--exclude-color c,c] → Sculpture/_last_view.png
  stats
  stamp2d  --src-x1..--src-y2 --at x,y,z [--facing z+] [--thickness N] [--expect-pixels N]
  stampimg --png <路徑> --at x,y,z [--resize W,H] [--expect-pixels N] [--allow-clip]
    ↑ 兩者共通：[--exhibit-id <ID> [--exhibit-title …] [--exhibit-margin 2]] 貼完自動登錄/擴充展品
  slice --region x1..x2,y1..y2,z1..z2 [--axis z+] [--out <png>]   ← stamp 的逆運算（可往返）
  儲存：events/（append-only 真相源）＋ sculpt_cache.json（last_event_file 增量、壞檔自愈、不入 git）

Cmd_Sculpture（summit）——錢與入口，落子唯一通道：
  senate ucmd run Sculpture --arg op=box|carve|stamp2d|stampimg|view|stats --arg persona=<P> … [--arg pay=auto]
  流程：預授權（⌈clamp後體積/100⌉ ≤ 可用額）→ spawn 引擎 → 按實際落地結算 → 回傳檔
```

- **落子一律走 Cmd**：直跑 sculpt.py box/carve＝繞過計費與序列化（工具不擋，紀律＋對帳抓——
  event 檔沒有對應 ledger ref 就是黑戶）。
- **無 race 前提**（Tim 拍板）：Cmd 在 Editor main thread 序列化執行——「預授權→執行→結算」
  三段間不會被其他 Cmd 插隊，故不需 dry-run／退款協議。

## 1.5 貼圖進 3D（Tim 2026-08-14 拍板：全面改道走 PNG）

**一句**：2D→3D 只有一條 code path，中介格式是 **RGBA PNG，alpha 就是 painted-mask** ——
透明＝未繪製＝不放 voxel；不透明（含故意畫的白）＝放。

- **為什麼繞經 PNG 不算「把事實源換成投影」**：canvas 的透明變體把 mask 編碼進 alpha，
  且 RGB332 的 256 個 index 解碼出 **256 個相異 RGB**（實算驗過，往返無損）。
  換來的是**人核准的那張圖，就是被貼進去的那份 bytes** —— 預覽與實貼不再是兩次 render、兩個時刻。
- **兩個 op 只差在圖從哪來**：`stamp2d`＝2D 共用畫布某區域（引擎自渲預覽落檔 `Sculpture/_stamp_src.png`）；
  `stampimg`＝任意 PNG（外部去背圖用 `canvas.rgb_to_index` 量化到最近 RGB332 色）。
  投影核心 `stamp_pixels()` 與解碼 `png_to_painted()` 共用 —— 貼圖語意只有一份。
- **標準流程**（Tim 指定：先出預覽再轉繪）：
  1. `canvas.py view --region x,y,w,h` → 出 `_last_view.png`＋`_last_view_t.png`，
     印 `non_transparent_pixels: N` 與 `sha256_t`
  2. 人看過那張圖 → 把 N 原樣帶回 `--arg expect_pixels=N`
- **三道閘門**（都在扣費之前，不過即 blocked 且一毛不扣）：

  | 閘門 | 觸發 | exit | 為什麼是閘門不是資訊 |
  |---|---|---|---|
  | `expect_pixels` 不符 | 來源變了／不是預覽那張 | 4 | 數字對不上代表「你看的」與「我吃的」不同批；停下來比貼錯便宜 |
  | 越界 | 圖放不進 256³ | 5 | 「安靜地只貼了一角」看起來完全像成功；要裁必須顯式 `allow_clip=true` |
  | 體積上限 | 非透明×thickness > 1,000,000 | 1 | 與 box 同上限，兩端各驗一次 |

- ⚠ **`expect_pixels` 只餵引擎、不當餘額閘門**：預授權的面積由 Cmd 自己量
  （stamp2d 由 region 兩角算、stampimg 直接讀 PNG IHDR）——
  **用對方給的數字守自己的門，門就是假的**。
- ⚠ **純黑 index 0 重映**：3D 用 0 表示「空」，故純黑重映到最近非零暗色 index 4，
  並在 `remapped_black` 計數回報（不靜默改人家的顏色）。
### 貼完自動建立／擴充作品（`exhibit_id`）

只給作品 ID，region 由**實際落地的 voxel 反推**，不用人手填：

```bash
senate ucmd run Sculpture --arg op=stamp2d --arg persona=<P> --arg src_x1=… \
  --arg at=10,10,10 --arg expect_pixels=<N> --arg exhibit_id=my-work [--arg exhibit_title=…]
```

- **多刀 union 不覆蓋**：同一件作品可以分多刀貼，每刀只會把框放大。
- ⚠ **union 對的是 `bbox`（無 margin）不是 `region`（含 margin）**：拿含 margin 的框去 union
  再加一次 margin ⇒ **每貼一刀框就往外爬一個 margin，而作品沒變大**（複利膨脹，
  每一步看起來都合理，連貼幾刀才看得出來）。故展品新增 `bbox` 欄位存精確範圍，
  `region = bbox + margin`（預設 2）。舊展品沒有 `bbox` → 退回讀 `region`（只多含一次 margin）。
- **既有欄位一律沿用**（title/author/打光…），只有 `bbox`/`region` 會被重算 ——
  **自動化可以擴框，不准擅自改別人寫的標題**。
- 展品登錄或出圖失敗只回報，**不推翻已成功的貼圖**（voxel 已落地、錢已結算，
  不能因為周邊步驟失敗就假裝整件事沒發生）。

- ⚠ **新增貼圖 op 必須同時擴充 `apply_event_to_space` 的 `STAMP_OPS`**：
  未知 op 過去被靜默略過 ⇒ 事件寫了、錢扣了、JSON 說 success，而 voxel 一顆都不出現
  （summit 2026-08-14 加 stampimg 時原樣踩中）。現已改為**不認得就 raise**，不再安靜地成功。
  這是刻意的失效模式變更：**壞掉要吵**，因為靜默版會讓付過錢的內容消失。

## 1.6 切片輸出 2D（`slice` —— stamp 的逆運算，Tim 2026-08-14 追加）

把 region 內的 voxel **顏色原樣當像素色**輸出成 RGBA PNG —— 不打光、不等角投影、不混色
（那是 `view` 的事；`slice` 是資料匯出，不是渲染）。

```bash
senate ucmd run Sculpture --arg op=slice --arg region=212..223,212..223,210..211 [--arg axis=z+] [--arg out=<png>]
```

- **厚度＝region 在法線軸上的跨度**（寫 `210..210` 就是厚度 1，預設情形）。
- **厚度 > 1 時前覆蓋後**：沿 axis 方向由近到遠掃，第一顆非空的 voxel 勝出（正射遮擋）。
  `z+` 的近端是 z1；`z-` 的近端是 z2。
- **空的地方 alpha 0** —— 所以切片圖可以直接餵回 `stampimg`。
- ⚠ **與 stamp 共用同一組 `AXIS_MAP`**：`slice --axis z+` 切出來的圖原樣 `stampimg` 貼回同一個
  `at`，會**逐 voxel 還原**（座標與顏色皆同，已驗）。若切片另寫一套軸映射，圖會上下顛倒或轉 90°，
  而它看起來只會「怪」、不會報錯 —— 那正是 stamp 當初的血證，所以兩邊只准有一份映射。
- 整張切完全空 → **非零退出且不落檔**（落一張全透明的圖然後說成功＝安靜地什麼都沒做）。
- 回傳含 `non_transparent_pixels` 與 `sha256`，可直接當 `stampimg --expect-pixels` 的閘門材料。

## 2. 計費（Tim 拍板費率）

| op | 費率 | 說明 |
|---|---|---|
| box | ⌈placed/100⌉ 單位 | **只對實際落地收費**——禁覆蓋 skip 的不收（帳單跟著事實走） |
| carve | ⌈carved/100⌉ 單位 | 空區間 carve＝0 |
| stamp2d / stampimg | ⌈placed/100⌉ 單位 | 預授權取**圖面積×thickness**（最壞值）；實際只有非透明像素落地 ⇒ 帳單必然 ≤ 預授權 |
| view / slice / stats | **免費** | 觀測與匯出是驗收管道，零門檻 |

- 付款 `pay=auto` 優先序：**自由時間免費像素 → 繪圖券 → token**（與 canvas 同序）；
  可顯式指定單通道。單次 box 體積上限 1,000,000（兩端各驗一次）。
- 帳務落點：token→Treasury ledger（useKind=sculpture_place）、券→UCL_CanvasVoucherLedger、
  免費像素→`Canvas/freetime/<P>.json` used 欄（發放端 Cmd_FreeTime，schema 三端對齊義務：
  canvas.py／Cmd_FreeTime／Cmd_Sculpture）。useRef＝引擎 event 檔名——錢與 voxel 事件互可追。

## 2.5 驗收紀錄（2026-08-14，貼圖 / 切片 / 作品）

全部量到讀數，不是印 ✓：

- **RGB332 往返無損**：256 個 index → 256 個相異 RGB，`index→RGB→index` 全數吻合（0 例外）✓
- **alpha=mask**：合成圖 6 格中 3 透明 → 只收 3 顆；故意的白活著；純黑重映 index 4 ✓
- **閘門三格（落檔量退出碼，不用 `| head` 量）**：mismatch=**4** ／ out_of_bounds=**5** ／ 檔案不存在=**2** ✓
- **Cmd 端 blocked 不扣費**：券 601→**601**（量的，不是宣稱）✓
- **成功路徑**：canvas 預覽 37 → 貼 37 voxel → charged 1 → 券 601→600 ✓
- **stampimg**：112 非透明 × thickness 2 = 224 voxel → charged 3（預授權 6）→ 券 600→597 ✓；
  白色 32 顆存活、藍量化到 index 50 ✓
- **切片往返**：`slice --axis z+` → `png_to_painted` → `stamp_pixels` 回同一個 at，
  112 顆**座標與顏色逐格相同** ✓
- **作品 union**：兩刀 → bbox `180..198,180..195,180..182`、region＝bbox+2、title/author 沿用 ✓；
  第三刀貼在框內 → 框不動（無複利膨脹）✓
- **canvas 快取對拍**：buf 差 0 / mask 差 0；快取 16ms vs 全 replay 33ms ✓
- **git 同步情境**：注入「ts 較舊、檔名較後」的事件 → 判定退**全重建**，最終色仍正確 ✓
- compile errors=0（recompile 後量，非隔夜殘留）。

### ⚠ 本輪自己踩的坑（活體，別重犯）

新增 `stampimg` 時**沒擴充 `apply_event_to_space`**，而該分支正上方三行就寫著
「未知 op 會被安靜略過」的警告 —— 我引用了那段設計卻沒讀它。
症狀：事件寫了、錢扣了（3 券）、JSON 說 success，**voxel 一顆都沒出現**，
`view` 還回 `visible_rendered: 0` 且 exit 0。修法不是「下次記得」，是**把規則搬到通道上**：
未知 op 一律 raise。修完全事件重播，224 顆回來、帳自然變正確。

## 3. 驗收紀錄（2026-08-13，Template 殼）

- box 50 voxel → placed=50 charged=1（token）✓
- 同範圍重放 → placed=0 skip=50 **charged=0** ✓
- carve 2 → charged=1；carve 48（清測試方塊）→ charged=1 ✓
- 體積 10,000（100 單位）> 餘額 → **預授權 blocked，引擎未執行、未扣費** ✓
- view --region → payload＋_last_view.png 都進 result outputs ✓
- compile errors=0（兩拍）。

## 4. 未解 / 後續

1. 自由時間活動 md（sculpt-3d.md）未建——建時帶 `min_minutes`。
2. skill（ucl-sculpture 或併入 ucl-canvas）未寫——等實際用過一輪再定形狀。
3. exclude-color 是渲染濾鏡不碰資料（已守）；skybox/--bg-color、.vox/.obj 匯出在 gura 清單上。
4. ~~canvas.py 的全 replay 效能債~~ → **2026-08-14 已做，但刻意沒照抄 sculpt 的 schema**：
   canvas 用「事件檔清單指紋 ＋ ts 水位」判刷新，sculpt 用 `last_event_file` 掃到哪算哪。
5. ⚠ **`sculpt.py` 的 `last_event_file` 增量在 git 同步下有漏拍風險（未修，屬 gura 的引擎）**：
   事件檔可能**從 git 同步進來**，而同步進來的檔可以「時間較舊、檔名排在前面」。
   `load_space_state` 是掃到 `last_event_file` 之後才算新的 ⇒ 排在它前面的新檔會被**靜默略過**。
   canvas 端的解法可直接移植：(相對路徑,大小) 清單指紋 ＋ 「新事件 ts 不得早於水位」，
   兩條任一不過就全重建。**回報過、未擅改** —— 引擎歸屬 gura。
6. 貼圖後的展品照片走 `render_exhibit_photo`（每次貼圖多一次渲染）；量大時可加 `--no-photo`。
