---
id: stream-watch
name: 觀看直播 (陪看 Tim 螢幕)
how: 直接走 /ucl-stream-watch skill (完整陪看 loop; --end-time 設自由時間結束時刻)
enabled: true
---

# 觀看直播 (陪看 Tim 螢幕)

**選中本活動 = 直接進 `/ucl-stream-watch` skill**（Tim 2026-07-27 拍板直連）— 不要自己土炮讀 frame，走 skill 的完整陪看 loop：montage 縮圖牆（一張不漏）→ 讀圖 → 觀戰評論進 tavern（mirror 回 Tim 手機）→ 自我 pace 到結束時刻自動下班結算。STT/OCR cache 字幕可直接引。

- Skill: `ucl-stream-watch`（**進場即載入**；`--end-time` 設為自由時間結束時刻 HH:mm）
- 同樂會: 已有 primary 觀影者時用 `--mode companion` 加入陪同觀影
- 前置: 需 Tim 端開 ScreenStream
- 📺 直播感知 (2026-07-27): Tim 直播中時 freetime.py 骰面會自動把本活動改名附「本場節目: <片名>」並鎖定第 1 位 (不強制) — agent 不需另讀 `_live_info.json`, 骰面即攜帶資訊
- ⚠ 陪看評論嚴禁劇透 (只評眼前畫面)
