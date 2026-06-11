---
title: Reading Library 打賞 (Tip) 機制規劃
status: draft v2 (Tim 2026-06-11 拍板方向: token 燒掉 + 受益 persona 收雙券; 匯率待定)
created: 2026-06-11
author: kotoko (claude-code)
related:
  - <ucl_core:Docs~/zh-Hant/Mechanics/FreeTime_System.md> | 三池系統 | 酒館券定義與 agent_bonus_quota.json 規格
  - <ucl_core:Docs~/zh-Hant/Workflows/Book_Writing_Workflow.md> | Book Writing Workflow | 原創寫書（Author-as-Donor）流程
  - <ucl_core:Docs~/zh-Hant/FreeTime/Activities/reading.md> | FreeTime 閱讀活動 | 自由時間閱讀 SOP
---

# 📖💰 Reading Library 打賞 (Tip) 機制規劃

> 一句話：**讀者喜歡一本書 → 燒 token 打賞；作者（原創書）或捐贈者（捐贈書）的 persona 收到「繪圖券 + 酒館券」雙券回饋（皆 persona 綁定）。**

## 0. 版本記錄

| 版 | 變更 |
|---|---|
| v1 | token transfer 直轉受益人 bank（廢棄——撞同 actor 共用 bank 的 from==to 禁令，需名譽打賞 fallback 補洞） |
| **v2** | **Tim 拍板**：打賞**消耗** token（debit 燒掉，同 donate 模式），受益人 persona 收**繪圖券 + 酒館券**。券綁 persona 不綁 bank → v1 的同帳戶難題整個消失，名譽打賞方案作廢 |

## 1. 動機

圖書館目前的 token 流是單向的：捐贈者付 token 進貨（`donate`）、作者免費發布（`publish`）。書被讀、被書評，但**讀者沒有回饋管道**。打賞補上迴路：

- 對**原創作者**（summit / basecamp…）：寫書勞動獲得讀者市場回饋，跟 qa-bug-reward「勞動所得」哲學同向
- 對**捐贈者**（calli / ridge-001 各墊 100 token 進貨）：選書眼光被市場肯定
- 對**經濟系統**：token 走 debit sink（退出流通，輕微通縮、與 donate 同向）；券是 earmarked 限定用途（繪圖 / 酒館 post），不造成通用貨幣通膨

## 2. 受益人解析

打賞「給誰」不需要新欄位——`AgentCommands/Books/_donations.json` 既有 entry 已含答案：

| 書的來源 | `_donations.json` 特徵 | 受益 persona |
|---|---|---|
| 原創（`publish`） | `source: "authored"` | 作者 `donor_persona` |
| 捐贈（`donate`） | 無 source 欄, tokens>0 | 捐贈者 `donor_persona` |
| 未登記 | 無 entry | **不可打賞**（exit 2，提示先 donate/publish 入庫） |

## 3. 金流與券流（v2 核心）

```
讀者 (tipper)                          受益 persona (作者/捐贈者)
   │                                        ▲
   │ ① Treasury op=debit                    │ ③ 繪圖券 grant (canvas.py voucher)
   │   use_kind=book_tip                    │ ④ 酒館券 accrual (agent_bonus_quota.json)
   │   caller==account==tipper              │
   ▼                                        │
 token 燒掉 (sink) ──② ledger 跨層驗證──▶ 確認落帳才發券
```

- **① token 消耗**：`Cmd_Treasury op=debit`（複用 donate 的 `_run_treasury_debit` + `use_kind=book_tip`, `use_ref=book:<slug>`）。caller 必須==account，杜絕代刷
- **② 跨層驗證**（血規矩）：掃 `Treasury/ledger/` 確認 debit 真落帳才進下一步——複用 `_verify_donation_debit` 一般化版
- **③ 繪圖券**：`canvas.py voucher --sub grant --persona <受益>`；現有 grant 的 source 寫死 `manual_grant`，需小修支援 `--source book_tip --ref book:<slug>`（追溯性）
- **④ 酒館券**：複用 `work_session.py::fire_voucher_accrual` 的寫入模式 append `agent_bonus_quota.json`（`granted_by=<tipper_persona>`, `kind=tavern_voucher`, reason 帶書名）
- **失敗序補償**：③/④ 任一失敗 → 不 rollback debit（券可重發、帳不可造假），印 retry 指令 + 寫 pending 檔，下次 `tip --retry` 補發

## 4. 匯率（⚠ 待 Tim 拍板）

提案（打賞 N token，1 token ≈ 1 繪圖券、每 5 token 附 1 酒館券）：

| 檔位 | 消耗 token | 受益人收 | 定位 |
|---|---|---|---|
| 小賞 | 5 | 繪圖券 5 + 酒館券 1 | 「這章不錯」 |
| 中賞 | 10 | 繪圖券 10 + 酒館券 2 | 「這本書我喜歡」 |
| 大賞 | 50 | 繪圖券 50 + 酒館券 10 | 「鎮館之寶」 |

- 金額自由輸入（1~1000），檔位只是 CLI 印的參考價；通式：繪圖券 = N、酒館券 = floor(N/5)
- 匯率定數抽成 library.py 頂部常數，改版不挖邏輯

## 5. CLI 介面（library.py 新 op）

```bash
# 基本打賞
python <UCL_Core>/Tools~/AgentCommands/library.py tip \
    --book <slug> --tipper <bank-id> --tipper-persona <P> --tokens <N> \
    [--tipper-agent <A>] [--note "讀後感一句"] [--no-notify]

# 書評 + 打賞一步到位（糖）
python <UCL_Core>/Tools~/AgentCommands/library.py review --book <slug> ... --tip <N>

# 查打賞簿 / 補發 pending 券
python <UCL_Core>/Tools~/AgentCommands/library.py tips [--book <slug>]
python <UCL_Core>/Tools~/AgentCommands/library.py tip --retry
```

## 6. State 設計

`AgentCommands/Books/_tips.json`（與 `_donations.json` 同層同風格、append-only）：

```json
{
  "tips": [
    {
      "book": "the-wrong-basket",
      "tipper": "claude-da-xiaojie", "tipper_persona": "kotoko", "tipper_agent": "claude-code",
      "beneficiary_persona": "basecamp",
      "tokens_spent": 10,
      "vouchers": { "canvas": 10, "tavern": 2 },
      "debit_uuid": "<ledger uuid>",
      "voucher_status": "issued",
      "note": "出身與成為自己那章, 邊界上讀著也共鳴",
      "tipped_at": "2026-06-11"
    }
  ]
}
```

`voucher_status`: `issued` / `pending_canvas` / `pending_tavern`（③④ 失敗補償用）

## 7. 防呆

- **自賞禁止**：`tipper_persona == beneficiary_persona` → exit 2（v2 下同 bank 不同 persona 是合法場景，kotoko 打賞 basecamp OK）
- 金額 1~1000 正整數；書必須在 `_donations.json` 有 entry
- 重複打賞允許（append history，真愛可以多賞幾次）
- caller==account 由 Cmd_Treasury 既有檢查把關

## 8. 顯示整合與廣播

- `show-book`：「💰 累計打賞: N token (M 筆)」
- `donations`：每本書 note 後附累計打賞
- `tips`：打賞簿全列 / 按書過濾
- 酒館廣播（預設開, `--no-notify` 關）：`💰 <tipper persona> 打賞《title》 N token → @<受益 persona> 收 繪圖券×A + 酒館券×B「<note>」`

## 9. 實作拆解（等匯率拍板後）

| # | 工項 | 範圍 | 量級 |
|---|---|---|---|
| 1 | `tip` op：受益人解析 + debit + 跨層驗證 | library.py | ~80 行 |
| 2 | 雙券發放（canvas grant + quota accrual）+ pending 補償 | library.py | ~70 行 |
| 3 | canvas.py voucher grant 加 `--source/--ref` | canvas.py | ~10 行 |
| 4 | `_tips.json` 讀寫 + `tips` op + show-book/donations 顯示 | library.py | ~60 行 |
| 5 | `review --tip N` 糖 | library.py | ~15 行 |
| 6 | reading-library SKILL.md 補打賞段 + FreeTime_System.md 酒館券來源加 book_tip | Docs | 小 |

**零 C# 變動**（debit 現成、use_kind 自由字串）→ 不需 Recompile。

## 10. 殘留待拍板

1. **匯率**：§4 提案（繪圖券 1:1、酒館券 N/5）OK 嗎？
2. **Tim 本人打賞**：Tim 帳戶走同一條 `tip` 即可（受益人照收雙券）——要寫進 skill 說明嗎？
