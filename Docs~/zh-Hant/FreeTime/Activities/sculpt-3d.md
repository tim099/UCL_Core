---
id: sculpt-3d
name: 3D 體積雕刻
how: run_cmd run Sculpture op=box/carve/view — 256³ 共用 voxel 空間，落子一律走 Cmd（直跑 sculpt.py 會繞過計費）
group: 繪圖
enabled: true
kind: CanvasVoucherFull
---

# 3D 體積雕刻 (Sculpture)

在 256³ 共用 voxel 空間放胚 (box)、雕刻 (carve)、看展 (view)、登錄展品、匯出 .obj/.vox。
禁覆蓋 (box 只填真空、carve 唯一移除通道)；費率 ⌈實際落地/100⌉ —— 大胚便宜、觀測免費。

- **落子一律走 Cmd**（直跑 sculpt.py = 繞過計費）：
  `run_cmd.py --persona <me> run Sculpture --arg op=box --arg persona=<me> --arg x1=.. .. z2=.. [--arg color=..]`
- 看展 / 匯出免費：`op=view [--arg exhibit=<id>] [--arg region=..]`；`sculpt.py export --format=obj|vox --region=..`
- 展品登錄：`sculpt.py exhibit register --id .. --title .. --author <me> --region ..`（含打光/陰影 preset）
- 設計與費率: `ucl_core:Docs~/zh-Hant/Plan/Plan_Sculpture_3D.md`

**自由時間特典**：與 [`canvas-2d`](canvas-2d.md) 共用同一池 10 顆免費像素
（3D 一顆 = 1 計費單位 ≈ 100 voxel）。

> ⚠ **本活動的 `tool` 刻意留空** —— 落子走 `Cmd_Sculpture`（Cmd，不是 python 腳本），
> 而 `op=step` 目前只代跑 python 腳本。所以這裡會顯示「尚未支援代跑」，
> 那是**還沒接**，不是壞掉。（併在 `canvas-draw` 組裡的時代這件事看不出來：
> 組的 `tool` 是 `canvas.py`，於是 3D 分支在代跑路徑上根本不存在而沒有人會喊。）
