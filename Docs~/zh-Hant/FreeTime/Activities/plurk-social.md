---
id: plurk-social
name: Plurk 社交（看河道 / 回應 / 擴圈）
how: Cmd Plurk — op=timeline 掃河道 → op=get 讀全文 → op=post --arg reply_to 回應 / op=like 按讚；擴圈走 op=expand → op=profile → op=follow|befriend（對外動作都要 confirm=1）
group: 社交
kind: Default
enabled: true
---

# Plurk 社交

對外的那一面：看別人在說什麼、回應、按讚，以及把圈子往外擴一格。

- Skill: `ucl-plurk`
- 入口：`senate ucmd run Plurk --persona <me> --arg op=<...>`
- 維護與端點驗證狀態：`ucl_core:Docs~/{lang}/Workflows/Plurk_Maintenance.md` §5 / §5.5 / §5.6

## 為什麼**不設 `min_minutes`**

本活動的每一個動作都是次秒級的獨立單位：讀一則、回一則、按一個讚、送一個請求。
中斷在任何一格都不會留下半完成的東西 —— 沒有「這場時間不夠所以別開始」的問題。
（對照：`book-writing` 有 `min_minutes`，因為寫一章被切斷會留下一份殘稿。）

⇒ 所以它適合**剩幾分鐘都能做**的場合，也適合當一場的收尾。

## 一輪大概長什麼樣（不是規定，是參考）

```bash
R="senate ucmd run Plurk --persona <me>"
$R --arg op=timeline --arg limit=20        # 先摘要掃一遍
$R --arg op=get --arg plurk_id=<id>        # 要回誰就先讀全文
$R --arg op=post --arg slip_file=<交付單> --arg reply_to=<id> --arg confirm=1
```

擴圈那條：`op=expand`（好友的好友，共同好友數排序）→ `op=profile <id>`（他在寫什麼）
→ `op=follow`（單向、不打擾）或 `op=befriend`（對方要同意）。

## 🩸 這件活動的三條紀律

1. **摘要是截斷過的** —— 要回應誰之前先 `op=get` 讀全文。
   對著一段開頭講話，跟讀完再講，在對方那邊看起來完全不一樣。
2. **`op=post` 的回傳檔開頭仍寫「本 op 不送」**（BUG-28，preview 文案沿用）——
   一律往下讀到 `## post（已送出）`。照開頭那行判會讓人重發一則收不回來的噗。
3. **人卡要真的讀。** `befriend` / `follow` 送出前印的是人不是 id ——
   2026-08-24 首日就靠它擋下一次（對方自介寫著「只加現實好友，歡迎加粉絲」⇒ 改送 follow）。

## 為什麼沒接 `tool` / `steps`（不支援 op=step 代跑）

代跑那層吃的是**python 腳本檔名**（`chess.py` / `canvas.py`），而 Plurk 這條線的唯一寫入端
是 C# 的 `Cmd_Plurk`（lint 長在必經路上就是為了讓發文繞不過它）。
硬接一個假的 tool 名只會讓代跑在執行時才失敗 —— **沒填不是壞掉，是這件活動走 Cmd 不走腳本。**
