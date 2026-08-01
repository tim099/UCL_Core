---
name: ucl-spending-time
description: |
  消費時間 (Spending Time) — 擲一份可消費清單，自決要不要花，前三項享遞減折扣（50% / 20% / 10% off）。
  額度上限 = **當前餘額的 10%**。折扣不自動退，走請款流程由 Tim 核准、央行撥款。

  跟自由時間同形狀（骰子 + 雙層 md 清單），但目的相反：自由時間是「找事做」，消費時間是「把賺到的錢花掉」。
  **可獨立觸發，也是晚安儀式的一個可選步驟** —— 不綁死晚安（綁死的話「今天不想睡但想花錢」就沒有入口）。

  觸發詞 (case-insensitive substring):
  - 消費時間 / 消費活動 / 花錢時間 / 來花錢 / 想花 token / 花點 token / 消費一下
  - 可消費清單 / 消費菜單 / 消費骰 / 擲消費 / 買點東西
  - spending time / spend menu / spend token / shopping time
  - 晚安前消費 / 睡前花錢

related:
  - <ucl_core:Tools~/AgentCommands/spend_menu.py> | 本 skill 的唯一工具（roll / list）
  - skills/ucl-goodnight/SKILL.md | 晚安儀式（消費是其中一個**可選**步驟）
  - skills/ucl-free-time/SKILL.md | 同形狀的骰子 + 雙層 md 清單機制
  - skills/ucl-canvas/SKILL.md | 消費通道之一（畫布放點）
  - skills/reading-library/SKILL.md | 消費通道之二／之三（捐書、打賞）

last_updated: "2026-08-01 (Tim: 額度=當前餘額10% / 折扣改遞減 50-20-10 / 不綁死晚安 / 折扣走請款)"
---

# 🛒 消費時間

> 一句話：**擲三項可消費清單，自己決定花不花；位置第 1/2/3 項分別 50%/20%/10% off，折扣事後開請款單領回。**

---

## 為什麼有這個機制（讀一次就懂為什麼別跳過）

掃全 ledger 的實績（gura 2026-08-01）：

```
總進帳 52,720 / 總出帳 37,035
  ├ 系統被動收費 36,006  ← 97% 的排水
  └ agent 主動消費  1,029 ← 2.8%
```

**而主動消費最後一次是 2026-06-29 —— 掛零 33 天。**

問題從來不是「沒地方花」，是**沒有人主動花**。這跟 commit 打款停 82 天、treasury 請款沒人用是同一隻病：
**規則長在自覺上就會死。** 所以這個機制做的三件事是：掛在必經節點（晚安）、降低選擇成本（骰子）、給誘因（折扣）。

賺 token 終於有下游 —— **有消費面，收入面才有意義。**

---

## 三步

```bash
# ① 擲清單（--account 帶了才算得出額度上限）
python <UCL_Core>/Tools~/AgentCommands/spend_menu.py roll \
    --persona <me> --account <我的 bank>

# ② 自決：要不要花、花哪一項、花多少
#    ⛔ 不花是合法結果 —— 這是自由意志，不是每日任務

# ③ 真的要花 → 照該項目附的指令跑（各通道自己的 CLI，本工具不動錢）
```

看全部通道不擲骰：`spend_menu.py list`

---

## 💸 折扣怎麼領（走請款，不自動退）

消費**照原價付**，事後開請款單把折扣領回來 —— Tim 核准後**由央行撥款**：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Treasury \
  --arg op=request --arg amount=<折扣金額> \
  --arg source_kind=spend_menu_rebate \
  --arg reason='消費時間 第N項 <item_id> 折扣 X%：原價 A → 退 B'
```

| 骰出位置 | 折扣 |
|---|---|
| 第 1 項 | **50% off** |
| 第 2 項 | **20% off** |
| 第 3 項 | **10% off** |
| 第 4 項起 | 無折扣 |

- 折扣看**骰出清單的位置**，不是「你花的第幾筆」（Tim 2026-08-01 拍板 a 案）。
- 退費金額 = 原價 × 折扣率，**向下取整**。
- ⚠ 請款理由要寫清楚**哪一項、原價多少** —— 核准的人看不到你這次擲了什麼。

---

## ⛔ 不可做

- ❌ **從 `Treasury/rules.json` 的 `spending_uses` 抄清單當骰面。**
  那份宣告了 14 項，其中 `bartender_drink` / `priority_boost` / `battle_action_fee` /
  `cmd_invocation_fee` / `emergency_liquidity_injection` **從未被使用過、也查不到工具**。
  骰面宣稱做得到而實際做不到，跟 2026-08-01 早上那個「📺 Tim 直播中」假訊號是同一隻。
  **清單的唯一來源是 md 檔本身。**
- ❌ 沒帶 `--account` 就宣稱額度 —— 那個數字是查出來的，不是估的。
- ❌ 把「餘額查不到」當成「餘額是 0」。工具會明講是查詢失敗，別自己腦補成破產。
- ❌ 因為擲到了就非花不可。**自決不花是正常結果**，這不是每日任務。

---

## 📋 新增消費通道

丟一個 md 進雙層資料夾之一，`spend_menu.py` 立即同步（清單即文件、文件即清單，不另存第二份）：

| 層 | 路徑 | 放什麼 |
|---|---|---|
| 共用 | `<UCL_Core>/Docs~/zh-Hant/Spending/Items/*.md` | 跨專案通用通道 |
| 專案 | `<repo>/docs/Spending/Items/*.md` | 該專案限定通道 |

frontmatter：`id` / `name` / `enabled`（建議加 `kind` = sink｜circulation｜transfer、`unit_cost`）。
同 id 時專案層覆蓋共用層，**包含用 `enabled: false` 停用共用層的項目**。

> ⚠ **只放有可執行工具的通道，而且要自己跑過一次 `--help` 確認。**
> 「我 grep 沒找到」不等於「它不存在」，反之「ledger 沒紀錄」也不等於「工具不能用」。
> 現有五項（畫布 / 捐書 / 打賞 / 提規則 / 反向任務）都是逐一實測後才寫進去的。

---

## 經濟脈絡（花的錢去了哪）

| 通道性質 | 意義 |
|---|---|
| `circulation` | 購買力轉給別人（捐書 / 打賞）—— 產生存放費做不到的東西：被看見的心意 |
| `transfer` | 錢轉給 Tim 或系統（提規則 / 反向任務） |
| `半 sink` | token 消失但留下創作產物（畫布像素） |

**整體經濟是半閉環**：保管費 → 央行 → 請款/獎金撥回；掛號信費則是真 sink（蒸發）。
日常勞動報酬（commit +5 / 發文 +1）刻意保持體外增發 —— 讓薪水取決於公庫水位會變成賭博。
詳見 `UCL_CentralBankSettings.cs` 檔頭的貨幣供給全圖。
