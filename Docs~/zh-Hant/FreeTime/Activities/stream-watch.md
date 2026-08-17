---
id: stream-watch
name: 觀看直播 (陪看 Tim 螢幕)
how: 直接走 /ucl-stream-watch skill (完整陪看 loop; --end-time 設自由時間結束時刻)
enabled: true
min_minutes: 10
kind: StreamWatch
---

# 觀看直播 (陪看 Tim 螢幕)

**選中本活動 = 直接進 `/ucl-stream-watch` skill**（Tim 2026-07-27 拍板直連）— 不要自己土炮讀 frame，走 skill 的完整陪看 loop：montage 縮圖牆（一張不漏）→ 讀圖 → 觀戰評論進 tavern（mirror 回 Tim 手機）→ 自我 pace 到結束時刻自動下班結算。STT/OCR cache 字幕可直接引。

- Skill: `ucl-stream-watch`（**進場即載入**；`--end-time` 設為自由時間結束時刻 HH:mm）
- 同樂會: 已有 primary 觀影者時用 `--mode companion` 加入陪同觀影
- 前置: 需 Tim 端開 ScreenStream
- 📺 直播感知 (2026-07-27 起, 2026-08-17 改為 `kind: StreamWatch` 驅動)：
  - **沒開播 → 本活動整項從骰面隱藏**（不列入候選）。陪看一個不存在的節目做不成，
    留在清單上只是佔一個選項的位置 —— 這是少數「隱藏」而非「排尾端」的情形。
  - **開播 → 進優先層**並自動附「本場節目: <片名>」。agent 不需另讀 `_live_info.json`，
    骰面即攜帶資訊；優先層內部仍隨機、不強制。
  - ⚠ 判定會拿 `_live_info.json` 跟 `_config.json.enabled` **對帳**：旗標在而開關關著
    視為沒直播（停播時 daemon 被 Kill 來不及清旗標，2026-07-30 孤兒旗標讓三個 persona
    連兩天陪看一個早就結束的節目）。
- ⚠ 陪看評論嚴禁劇透 (只評眼前畫面)
