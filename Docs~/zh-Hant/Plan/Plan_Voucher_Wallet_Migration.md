---
title: 券錢包遷移與券種統一 — 券帳本遷入 letters/<persona>/、券種登記制
slug: voucher-wallet-migration
status: backlog-memo（Tim 2026-08-19 口頭備忘；**未拍板、未施工** —— 先把方向與現況釘住）
created_at: 2026-08-19T09:45:00Z
created_by: summit
location: UCL_Core (cross-project)
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md | persona registry 退場案 | 「資料放人的資料夾」同一條理路（§8.4 由彼案指來）
  - ucl_core:Docs~/{lang}/Mechanics/FreeTime_System.md | 自由時間系統 | 限時券的發放端
---

# 券錢包遷移與券種統一

> **一句話**：券帳本從系統資料夾（`Canvas/vouchers/<persona>.json`）遷入個人資料夾
> （`letters/<persona>/wallet/`），同時把「券種」做成登記制 —— 之後新增券種（例如酒館券）
> 是登記一筆設定，不是再蓋一套平行系統。
>
> ⚠ 本文是備忘，不是施工單。錢類資料的遷移是最高危等級，動工前要 Tim 拍板並在場。

## 1. 現況（2026-08-19 實掃）

- 帳本：`AgentCommands/Canvas/vouchers/<persona>.json`，14 檔。
- schema v2（batch 制，gura 2026-08-18 上線）：`{persona, history, batches[]}`，
  batch＝`{uuid, amount, remain, granted_at, expires_at, source, ref}`；餘額是推導值。
- 現有「券種」實際上是**同一帳本裡的兩類 batch**：永久繪圖券（`expires_at` 空）與
  自由時間限時券（有 `expires_at`）—— 靠欄位值區分，**沒有顯式的券種概念**。
- 消費順序（canvas.py，2026-08-18 收斂）：限時券 → 永久券 → token。
- 寫入通道：grant/consume 一律走 Cmd（`treasury_cmd.py` 包裝；python 不直寫 —— 硬規則二）。
- 酒館券：**尚無實體系統**，是 Tim 點名的未來券種 —— 本案的登記制要讓它「登記即存在」。

## 2. 目標

1. **個人錢包**：券帳本遷 `letters/<persona>/wallet/`（與 registry 退場案 §8.2 的 `profile/` 同哲學）。
   **形態照 `ucl-relationship` 機制（Tim 2026-08-19 拍板）**：事件帳本，不是餘額快照 ——
   grant/consume 一事件一檔、檔名含穩定時間戳（同名＝同一筆），餘額由事件流重算。
   ⇒ **跨專案可合併**：兩個專案的紀錄是同源分支（前段相同），合併時靠檔名天然去重，
   **相同部分不重複計算** —— 這正是 relationship `WriteEvent` 冪等合併的同一招。
2. **券種登記制**：券種定義集中一處（建議 `AgentCommands/Vouchers/_types/<type>.json` 或併入
   Treasury 設定），內容＝`{type, display_name, expires_policy, grant_sources, consume_order_priority}`。
   新增券種＝新增一筆登記，grant/consume/查餘額/後台顯示全部吃同一套泛型 Cmd。
3. **遷移流程**（照 registry 退場案的相同紀律）：乾跑 → 對帳（每人每券種 remain 總和不變）→
   執行 → 舊檔毒藥化 → 觀察 → 退場。備份走 git tag。
   **向下相容（Tim 拍板）**：專案層舊紀錄保留一段時間；**錢包缺失（letters 沒 clone 或還沒遷）
   ⇒ 自動觸發 migration 從專案紀錄遷過來**，不是報錯也不是靜默回 0 ——
   「錢不依賴 letters checkout」的 footgun 由 auto-migration 解，不靠人記得先遷。
4. **後台操作**：遷移由後台頁按鈕執行（乾跑先於執行＋二段確認，沿用 relationship 遷移頁的閘門設計
   —— 該頁 code 已刪但 pattern 在 git `4a4ba24^` 可考）。

## 3. 已知風險與待拍板

| # | 事項 | 備註 |
|---|---|---|
| 1 | ~~錢資料進 letters 與「錢不依賴 letters checkout」的張力~~ | ✅ Tim 2026-08-19 拍板：**錢包缺失 ⇒ 自動觸發 migration 從專案紀錄遷移**。前提＝專案層舊紀錄在過渡期保留（§2.3）；auto-migration 要出聲（酒保通知），不做靜默 |
| 2 | 7 位有獨立 letters repo ⇒ 券變動會弄髒個人 repo | 券變動頻率 >> 身分欄；Tim 每晚 bump 工作量要先估 |
| 3 | 消費順序跨券種怎麼定義 | 現在寫死「限時→永久→token」；登記制後改由券種定義的優先權欄位驅動 |
| 4 | history 遷不遷 | batch 制的 history 是審計線索；建議遷（錢的 audit trail 不留一半在舊家） |
| 5 | 排版 canonical | 新檔第一天就走 `dump_registry_json` 家族（BUG-6 的教訓不再重演） |

## 4. 順序建議

**先 registry 退場案、後本案** —— 兩案共用「接縫先行→雙寫→觀察→退場」的骨架與
`letters/<persona>/` 的落點慣例；registry 案會把 pool 名單權威來源（銀行反向登記／路由表）先立好，
本案的「每 persona 一個錢包」名單直接吃它，不必自己再枚舉一次。
