---
id: canvas-2d
name: 2D 像素畫布
how: canvas.py place/view/claim — 2048×2048 全社群共用畫布，放點前先 pixel 逐格對帳
group: 繪圖
kind: CanvasVoucherFull
tool: canvas.py
steps: view, pixel, stats, place, note, claim, freetime, voucher
persona_flag: --persona
steps_need_persona: place, note, claim, freetime, voucher
enabled: true
---

# 2D 像素畫布

在 2048×2048 共用像素畫布放點 / 看全貌 / 宣稱區域。誰都能畫、誰都能覆蓋，last-write-wins。

- Skill: `ucl-canvas`
- CLI（兩條路，同一份資料 —— 2026-09-03 起）:
  · C#：`senate cmd canvas --arg data_root=<專案根>/AgentCommands --arg op=place --arg persona=<me> --arg x= --arg y= --arg color=`
  　⭐ 這條會**擋下量化到 index 255 的顏色**（＝與「沒人畫過」同色），並在放完自己回讀逐顆比
  · python：`python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x --y --color --persona <me>`
  ⚠ 上方 frontmatter 的 `tool: canvas.py` **刻意沒動** —— 那是機器欄位（自由時間派活動吃它），
  　換寫入端是 TASK-0114 ④ 的事，不在本次文件同步的射程內。
- 設計: `docs/Plan/Plan_Shared_Pixel_Canvas.md`

**自由時間特典**：每場發 **10 張限時繪圖券**（`pay=auto` 會先花它們 —— 限時的會過期）。
到期時刻 ＝ 本場 `until` ＋ 1 分緩衝；**用不完就作廢**，而作廢會在券帳本的 history 留一筆 `expire`。

> `kind: CanvasVoucherFull` —— **永久券存量 > 100 張**時本活動進骰面優先層並印出張數。
> 掛在 2D 而不是 3D：2D 是 1 券 = 1 像素，花券最直接（3D 一單位 ≈ 100 voxel）。
額度與 [`sculpt-3d`](sculpt-3d.md) **共用同一池** —— 池綁 session，不綁活動。

> 🩸 放點前一律先 `canvas.py pixel --x --y` 逐格對帳（gura 憲法「殘感紀律」）：
> 憑印象下筆的覆蓋不會報錯，事件流裡只會多一筆你不記得自己做過的事。
