---
title: 3D 體積雕刻系統（Sculpture）— 分工契約與計費規格
slug: sculpture-3d
status: v1 shipped（2026-08-13 Tim 拍板；gura 引擎 + summit 扣費/Cmd 同日落地）
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
  儲存：events/（append-only 真相源）＋ sculpt_cache.json（last_event_file 增量、壞檔自愈、不入 git）

Cmd_Sculpture（summit）——錢與入口，落子唯一通道：
  run_cmd.py run Sculpture --arg op=box|carve|view|stats --arg persona=<P> --arg x1=.. [--arg pay=auto]
  流程：預授權（⌈clamp後體積/100⌉ ≤ 可用額）→ spawn 引擎 → 按實際落地結算 → 回傳檔
```

- **落子一律走 Cmd**：直跑 sculpt.py box/carve＝繞過計費與序列化（工具不擋，紀律＋對帳抓——
  event 檔沒有對應 ledger ref 就是黑戶）。
- **無 race 前提**（Tim 拍板）：Cmd 在 Editor main thread 序列化執行——「預授權→執行→結算」
  三段間不會被其他 Cmd 插隊，故不需 dry-run／退款協議。

## 2. 計費（Tim 拍板費率）

| op | 費率 | 說明 |
|---|---|---|
| box | ⌈placed/100⌉ 單位 | **只對實際落地收費**——禁覆蓋 skip 的不收（帳單跟著事實走） |
| carve | ⌈carved/100⌉ 單位 | 空區間 carve＝0 |
| view / stats | **免費** | 觀測是驗收管道，零門檻 |

- 付款 `pay=auto` 優先序：**自由時間免費像素 → 繪圖券 → token**（與 canvas 同序）；
  可顯式指定單通道。單次 box 體積上限 1,000,000（兩端各驗一次）。
- 帳務落點：token→Treasury ledger（useKind=sculpture_place）、券→UCL_CanvasVoucherLedger、
  免費像素→`Canvas/freetime/<P>.json` used 欄（發放端 Cmd_FreeTime，schema 三端對齊義務：
  canvas.py／Cmd_FreeTime／Cmd_Sculpture）。useRef＝引擎 event 檔名——錢與 voxel 事件互可追。

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
4. canvas.py 的全 replay 效能債：可移植 sculpt 的 last_event 增量快取（同 schema 兩邊共用）。
