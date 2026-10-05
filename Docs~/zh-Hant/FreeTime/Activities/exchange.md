---
id: exchange
name: 交易所（看盤 / 看走勢 / 券互換）
how: senate cmd rate（看盤 op=list、報價 op=get、走勢 op=history）＋ senate cmd voucher-swap（換券，**不帶 confirm=1 只試算**）⚠ step_args 是 cmd 原生寫法：`quote` 是 `--arg from=BTC --arg to=GOLD`、`history` 是 `--arg symbol=BTC`、`swap` 是 `--arg from=btc --arg to=gold --arg amount=1 [--arg confirm=1]`
steps: rates, quote, history, swap
cmd_steps: rates=rate:list, quote=rate:get, history=rate:history, swap=voucher-swap:swap
cmd_persona_arg: persona
steps_need_persona: swap
enabled: true
min_minutes: 0
needs_session: false
group: 經濟
kind: Default
---

# 交易所（看盤 / 看走勢 / 券互換）

> TASK-0272（Tim 2026-09-28：「把交易所功能加入自由時間的活動」）。
> 券與券之間的互換一律走 **USD 樞紐兩段撮合**（A → USD → B），每筆成交收手續費（按腿計）。
> 匯率來自快取 `Market/rates_cache.json`；**沒有報價的券一律不能換**。

## 一輪大概長什麼樣（不是規定，是參考）

```bash
R="senate cmd rate"
$R --arg op=list                                   # 看盤：各券 Bid/Ask、手續費、報價時間
$R --arg op=history --arg symbol=BTC               # 看走勢：中間價序列、區間變動、波動度
$R --arg op=get --arg from=BTC --arg to=GOLD       # 報價：賣 1 BTC 實得幾 GOLD（淨率＝扣完手續費）

V="senate cmd voucher-swap --arg persona=<me>"
$V --arg from=btc --arg to=gold --arg amount=1               # 試算（零寫入）
$V --arg from=btc --arg to=gold --arg amount=1 --arg confirm=1   # 真的換
```

走 `op=step` 代跑時：`--arg step=rates|quote|history|swap --arg step_args="…"`（`persona` 由 `op=step` 自動補在 `swap` 上）。

## ⛔ 報價刷新不是你的事 —— **不要手動跑 sync**

匯率**每天自動刷新一版**：Senate 端的保管費扣繳（`demurrage op=run`）發完券之後，接著刷新匯率並寫進歷史
（Tim 2026-09-28：綁在扣管理費、剛好發完券之後；觸發在 Senate 端，不透過 Unity）。
結果跟保管費公告在**同一則**酒館訊息裡（「📈 匯率每日版本」那一段）。

- ⛔ **平常不手動跑 `rate op=sync`**：一天一版是刻意的，手動多抓會在走勢上多出不是每日節奏的點。
- 看盤時先看 `op=list` 的**更新時間**：不是今天的話，代表今天的扣繳還沒跑（或刷新失敗 —— 那則公告會寫原因）。
  ⇒ 那是要回報的事，⛔ 不是自己補抓。
- 本活動也沒有刷新步驟：`op=step` 在 Unity Editor 裡跑，而 Editor **沒有網路出口**。

## 讀走勢時要知道的三件事

1. **沒有版本的日子是「沒抓」，不是「沒變」。** 走勢輸出會印「期間有 N 天沒有版本」。
2. **波動度是「每版」不是年化**：版本間隔不固定，年化會假裝間隔一致。
3. **區間內沒有資料會 exit 1 並講原因**（歷史是空的／這個券從沒被記錄過／都不在區間內）——
   ⛔ 不會回 0 或最近一筆。

## 換券的禮貌與紀律

- **先試算再換**：不帶 `confirm=1` 就是試算，券本一個 byte 都不動。看過淨率再決定。
- **換之前先看報價時間**：`op=list` 的更新時間是好幾天前的話，那是舊價 —— 回報，⛔ 不要自己 sync。
- 零頭會留在小數池（滿 1 張才進可用券，TASK-0271）—— 換出來的 0.8 張不是不見了。
- 🩸 **手續費兩腿都收**：BTC → GOLD 是兩筆成交（約 0.2%），來回一趟約 0.4%。
  頻繁來回換不會賺，只會把券繳給手續費 —— 這是刻意照現實交易所配置的（Tim 2026-09-23）。

## 相關

- 後台：`senate ui --page rates`（匯率表、**歷史走勢與波動圖**、手填報價）
- 單子：`AgentCommands/Tasks/tasks/0272.md`
- 實作：`<SCP_Core>/Runtime/Market/`（`SCP_MarketRates` 快取與撮合、`SCP_RateHistory` 歷史版本）
