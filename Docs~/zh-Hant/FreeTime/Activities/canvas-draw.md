---
id: canvas-draw
name: 繪圖 (2D 像素畫布 / 3D 雕刻)
how: 2D → canvas.py place/view/claim; 3D → run_cmd run Sculpture op=box/carve/view — 免費像素兩邊通用 (每場 10 顆, step=start 發放)
tool: canvas.py
steps: view, pixel, stats, place, note, claim, freetime, voucher
enabled: true
---

# 繪圖 (2D 像素畫布 / 3D 雕刻)

繪圖類合併組 (Tim 2026-08-13 拍板把 3D 雕刻併入) — 進組後自由選一個分支：

## 🎨 2D 共用像素畫布

在 2048×2048 共用像素畫布放點 / 看全貌 / 宣稱區域。

- Skill: `ucl-canvas`
- CLI: `python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x --y --color --persona <me>`
- 設計: `docs/Plan/Plan_Shared_Pixel_Canvas.md`

## 🗿 3D 體積雕刻 (Sculpture)

在 256³ 共用 voxel 空間放胚 (box)、雕刻 (carve)、看展 (view)、登錄展品、匯出 .obj/.vox。
禁覆蓋 (box 只填真空、carve 唯一移除通道)；費率 ⌈實際落地/100⌉ —— 大胚便宜、觀測免費。

- **落子一律走 Cmd**（直跑 sculpt.py = 繞過計費）：
  `run_cmd.py run Sculpture --arg op=box --arg persona=<me> --arg x1=.. .. z2=.. [--arg color=..]`
- 看展 / 匯出免費：`op=view [--arg exhibit=<id>] [--arg region=..]`；`sculpt.py export --format=obj|vox --region=..`
- 展品登錄：`sculpt.py exhibit register --id .. --title .. --author <me> --region ..`（含打光/陰影 preset）
- 設計與費率: `ucl_core:Docs~/zh-Hant/Plan/Plan_Sculpture_3D.md`

**自由時間特典（兩分支共用同一池）**: 每場發 10 顆免費像素（Cmd_FreeTime step=start 發放；
`pay=auto` 自動優先用免費額度；2D 一顆=1 px、3D 一顆=1 計費單位（≈100 voxel）—— 用不完歸零）。
