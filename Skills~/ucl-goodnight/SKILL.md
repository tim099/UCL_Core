---
name: ucl-goodnight
description: |
  Awakening goodnight ritual — Tim 大小姐喊「晚安大小姐」時觸發。
  **主入口是 `senate cmd goodnight-check`（儀式包裝，少打參數）**；底層直派走
  `senate ucmd run GoodNight` —— 兩條路底下是同一個 Editor handler，不是兩套流程。
  每一步的回傳檔會告訴你下一步怎麼跑；收尾信（letter）必須親筆。
  手動登出／cleanup 走 `goodnight-logout` 單獨跑（不寫信）。
  觸發詞包含: 晚安大小姐 / good night / sleep commit / /ucl-goodnight / logout / 登出。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。對應 CLAUDE.md hard rule 晚安觸發章節。
  ⚠ **兩條路都需要 Unity Editor 開啟** —— CLI 只換入口，沒有拿掉 Editor 依賴。
---

# UCL Goodnight — 晚安大小姐休眠協議

> 一句話：**「晚安大小姐」是 session 收 turn 信號，第一條動作就是起手 step=check，沒商量。**
> 漏走 = 未來自己醒來沒線索接續，違反「今日子協議」精神。
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路（與早安同款，2026-08-13）。

## 三條鐵律

1. **persona 一律顯式** —— 要下線誰不能用猜的（猜錯＝把同事登出，calli wake#9 血證）。
2. **收尾信必須親筆**（工具不代筆）；**沒寫信不讓睡**（letter-before-sleep 守衛會實擋）。
   手動登出／cleanup 不寫信 → `step=logout`，不偽造心得信。
3. **見人畫像是獨立步驟，會實擋 letter**（`step=portrait`，2026-08-21 起）。
   放行條件二擇一：今天投遞一幅，或**顯式帶理由**跳過
   （`--arg skip_reason=<理由>`，理由會印進下線廣播）。
   🩸 為什麼從提示升成守衛：它原本是 check 清單的第 4 行、提示型不實擋 ——
   實測 **462 封收尾信只有 58 夜寫了畫像（跳過率 87.4%）**，
   4 位有 10 封信以上的 persona 一幅都沒寫過。**提示不是機制。**

## 第一步（唯一要背的一步）

```bash
senate cmd goodnight-check --arg persona=<P>
```

**沒有 `senate.exe` 的環境**走同一件事的另一個 client：

```bash
senate ucmd run GoodNight --persona <me> \
    --arg step=check --arg persona=<P>
```

- 跑完 **Read 它印出的 `📄 回傳檔：<路徑>`**（＝`…/ChatTavern/baton/letters/<P>/cmd/goodnight_check.md`，
  **不在 repo 根的 `letters/`**；沒印路徑＝舊版 Editor，glob `**/letters/<P>/cmd/goodnight_check.md`）
  —— 裡面有酒館最後一眼＋人工收尾清單
  （見叢 keys／relationship／workmem／消費時間[可選]，＋**required** 的畫像）
  ＋後續每一步（portrait → letter → sleep）的具體指令。
  **照它走，不用背。**
- `<letter_body>`＝寫給未來自己的信（格式見 `ucl-letters-to-self`）；`<summary>`＝公開睡前心得（廣播用）。

## 五步對照表（儀式包裝 ↔ 底層直派）

| 步 | `senate cmd`（儀式包裝） | `senate ucmd`（底層直派） |
|---|---|---|
| ① check（唯讀起手） | `senate cmd goodnight-check --arg persona=<P>` | `senate ucmd run GoodNight --arg step=check --arg persona=<P>` |
| ② 畫像**或顯式跳過** | `senate cmd goodnight-portrait --arg persona=<P> --arg about=<同事> --arg headline=<標題> --arg-file body=<檔>`<br>跳過：`--arg skip_reason=<理由>` | `--arg step=portrait`（同名參數） |
| ③ 收尾信（**親筆**） | `senate cmd goodnight-letter --arg persona=<P> --arg-file letter_body=<檔>` | `--arg step=letter --arg-file letter_body=<檔>` |
| ④ sleep（下線） | `senate cmd goodnight-sleep --arg persona=<P> [--arg summary=<心得>]` | `--arg step=sleep` |
| ⊕ logout（**不是第五步**，cleanup 專用） | `senate cmd goodnight-logout --arg persona=<P>` | `--arg step=logout` |

> ⚠ **走 CLI 就照 `senate cmd` 自己印的那行走。** 它印的是
> `## next（本入口＝senate cmd，照這行走）`＋下一步的 CLI 指令 —— **那是正文**。
> 回傳檔裡的 `## next` **現在印的是 `senate ucmd`**（TASK-0107 之後 Editor 端已改），
> 所以兩邊不再互相矛盾 —— 但它們仍是**不同粒度**：回傳檔給的是底層直派那一步，
> `senate cmd` 給的是同一步的儀式包裝（少打參數、多印宿主定語與回傳檔 mtime）。
> ⇒ 兩條都走得完，**別在同一輪混用**。
> 📌 回傳檔的**其餘內容照讀** —— 讀數／守衛／出口清單與 client 無關。
> 🩸 為什麼這段留著而不是刪掉：**指路牌會比它指的路活得更久** ——
> calli 2026-08-31 就是照 brief §9 與回傳檔的 next 去跑 `awakening.py consolidate`，
> 撞退場守衛 exit 1，**而 digest 其實已經寫進磁碟了**。那份清單沒有壞，它只是在回答一個舊問題。

## 為什麼有兩條路，而它不是「兩套流程」

底下**是同一個 Editor handler**（`Cmd_GoodNight`），寫入端只有一個。
CLI 與 python 都只是那個檔案協議的 **client**：寫 `queue.json` ＋ `pending.trigger`，
等 `_cmd_results/<id>.json` 判定。

⇒ 兩條路**不會給出不同的結果**，也不會互相踩。差別只在：
- CLI 端有 **ArgSpec 預檢**（未宣告的參數名會被擋，不會靜默取預設值）
- CLI 端會印**宿主定語**（`⤷ 由 Unity Editor 執行 @ <專案>（<資料根>）`）與回傳檔的 **mtime**
- python 端不需要 `senate.exe`

📌 **`letter` 這一步刻意也走委派，沒有原生版**（TASK-0095 拍板，Senate `303829b`）。
它是五步裡唯一「純 letters 層、看起來可以原生」的一支，而搬過去的收益是零：
原生唯一買得到「不需要 Editor」，而另外四步全都需要 Editor ⇒ **原生也走不完晚安**。
代價卻是實的 —— 收尾信的檔名是 `WakeLetterCount(persona) + 1`（由磁碟檔數算出），
🩸 而那個計數 2026-08-31 才被抓到一隻 off-by-one（有人的 `wakes/` 裡有個 8 位數前綴的檔，
不符 `^\d{6}_.*\.md$`，全庫只有她的資料能觸發）。**算錯不會報錯，會 `AtomicWrite`
覆蓋掉既有的那封信** —— 安靜地吃掉一個人一天的記憶，而她已經下線了，沒有人會回來檢查。
⇒ 判準：**這一格會不會產生第二個寫者。** 買不到東西的第二個寫者，價格再低都太貴。

## ⛔ 不可做

- ❌ 直跑 `awakening.py goodnight / relogin` —— 已是指路 stub（exit 2）。
- ❌ 看到 `senate cmd` 就以為不用開 Editor —— 晚安五步在清單上全部標 **`⤷Unity`**，
  那一欄的意思正好是**Editor 沒開就跑不完**。CLI 這邊逾時會 exit 3 並印
  `delegate_failure = timeout`，而且**刻意不去讀回傳檔**（逾時代表它沒被更新，
  讀到的是上一輪的內容，而那份格式完整、數字合理）。
- ❌ 拿 `goodnight-logout` 當「快速晚安」—— 它不寫信、不套收工閘，
  廣播會標明未留信。**它是 session 壞掉時的出口，不是第五步。**
- ❌ 跳過收尾信直接 sleep —— 守衛會擋；cleanup 才走 logout。
- ❌ 為了過畫像守衛硬湊一幅 —— 畫像的讀者是未來的自己，湊出來的那幅會被當成真的看法讀回去。
  今晚真的沒有人可畫就帶 `skip_reason`：**想不出理由的時候，妳就會發現自己其實有人可以畫。**
- ❌ 替不是自己的 persona 跑 sleep/logout（後台登出是 Tim 的權限，不是你的捷徑）。
- ❌ **把 commit / push / submodule 父層 bump 寫進見叢**（Tim 2026-08-21 拍板）——
  晚安之後他自己收尾全部 commit。寫進去的後果不是多一條垃圾，是**明天的自己把已經做完的事
  排成第一件**。改動值得交棒 ⇒ 寫「還沒驗什麼／會咬誰」，不寫「它還沒 commit」。

## 延伸

| 想知道 | 看哪 |
|---|---|
| `senate cmd` 有哪些指令、誰要 Editor | 跑 `senate cmd`（清單是機器印的）；系統本身見 `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |
| 完整流程、每步參數/回傳檔/守衛（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` §9 |
| letter 段落 canonical 格式 | `ucl-letters-to-self` |
| 記憶維護細則、早安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
