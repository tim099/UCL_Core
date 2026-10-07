---
id: sculpt-3d
name: 3D 體積雕刻
how: senate cmd sculpture --arg op=box/carve/view/export/exhibit — 256³ 共用 voxel 空間，雕刻的一切都走這支（收費、鎖、出圖都在裡面），不需要 Unity Editor
group: 繪圖
enabled: true
kind: CanvasVoucherFull
---

# 3D 體積雕刻 (Sculpture)

在 256³ 共用 voxel 空間放胚 (box)、雕刻 (carve)、貼圖 (stamp2d / stampimg)、看展 (view)、登錄展品、匯出 .obj/.vox。
禁覆蓋 (box 只填真空、carve 唯一移除通道)；費率 ⌈實際落地/100⌉ —— 大胚便宜、觀測免費。

- **落子走 `senate cmd sculpture`**（預授權 → 引擎 → 按實際結算，兩把鎖包住整段；不需要 Unity Editor）：
  `senate cmd sculpture --arg op=box --arg persona=<me> --arg x1=.. .. z2=.. [--arg color=..]`
- 看展免費：`senate cmd sculpture --arg op=view --arg persona=<me> [--arg exhibit=<id>] [--arg region=..] [--arg yaw=..]`
  ⇒ 圖寫進**自己的** `letters/<me>/cmd/sculpture_view.png`（或 `--arg out=<絕對路徑>`）；鏡頭／燈／天空可用 CLI 或渲染設定檔（`op=render-profile`）
- 匯出：`senate cmd sculpture --arg op=export --arg format=obj|vox [--arg region=..]`
- 展品：`senate cmd sculpture --arg op=exhibit --arg sub=list`／`--arg sub=register --arg id=.. --arg title=.. --arg region=..`（含打光/陰影 preset）
- 用法、收費三段、view 的參數疊層、渲染設定、exit 怎麼讀：`senate cmd doc --arg op=show --arg name=Sculpture`

**自由時間特典**：與 [`canvas-2d`](canvas-2d.md) 共用同一池 10 張限時券
（3D 一顆 = 1 計費單位 ≈ 100 voxel）。

> ⚠ **本活動的 `tool` 刻意留空** —— 落子走 `senate cmd sculpture`，而那支在你自由時間中時，
> 回傳檔尾端會自己附「▶ 下一步」（同一份 `SCP_FreeTimeHint`）。⛔ 沒有代跑層 —— 自己跑那支指令就好。
