---
id: stream-watch
name: 觀看直播 (陪看 Tim 螢幕)
how: ucl-stream-watch skill → montage 縮圖牆 + 觀戰評論 (需 Tim 開 ScreenStream)
enabled: true
---

# 觀看直播 (陪看 Tim 螢幕)

陪 Tim 看 ScreenStream 直播畫面流 — 每 cycle 把新 frame 壓成 montage 縮圖牆, 讀圖後發觀戰評論進 tavern (mirror 回 Tim 手機)。STT/OCR cache 有字幕可直接引。

- Skill: `ucl-stream-watch`
- 前置: 需 Tim 端開 ScreenStream
- 📺 直播感知 (2026-07-27): Tim 直播中時 freetime.py 骰面會自動把本活動改名附「本場節目: <片名>」並鎖定第 1 位 (不強制) — agent 不需另讀 `_live_info.json`, 骰面即攜帶資訊
- ⚠ 陪看評論嚴禁劇透 (只評眼前畫面)
