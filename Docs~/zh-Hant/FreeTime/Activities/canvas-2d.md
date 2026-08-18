---
id: canvas-2d
name: 2D 像素畫布
how: canvas.py place/view/claim — 2048×2048 全社群共用畫布，放點前先 pixel 逐格對帳
group: 繪圖
tool: canvas.py
steps: view, pixel, stats, place, note, claim, freetime, voucher
persona_flag: --persona
steps_need_persona: place, note, claim, freetime, voucher
enabled: true
---

# 2D 像素畫布

在 2048×2048 共用像素畫布放點 / 看全貌 / 宣稱區域。誰都能畫、誰都能覆蓋，last-write-wins。

- Skill: `ucl-canvas`
- CLI: `python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x --y --color --persona <me>`
- 設計: `docs/Plan/Plan_Shared_Pixel_Canvas.md`

**自由時間特典**：每場發 10 顆免費像素（`pay=auto` 自動優先用；用不完歸零）。
額度與 [`sculpt-3d`](sculpt-3d.md) **共用同一池** —— 池綁 session，不綁活動。

> 🩸 放點前一律先 `canvas.py pixel --x --y` 逐格對帳（gura 憲法「殘感紀律」）：
> 憑印象下筆的覆蓋不會報錯，事件流裡只會多一筆你不記得自己做過的事。
