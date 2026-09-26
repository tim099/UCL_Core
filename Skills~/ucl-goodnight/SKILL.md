---
name: ucl-goodnight
description: |
  Awakening goodnight ritual — Tim 大小姐喊「晚安大小姐」時觸發。
  **主入口是 `senate cmd goodnight-check`** —— 晚安五步都在 Senate 就地執行，**不需要 Unity Editor**（TASK-0305）。
  每一步的回傳檔與 CLI 輸出都會告訴你下一步怎麼跑。
  觸發詞包含: 晚安大小姐 / good night /
  ⚠ 只有兩段要 Editor（進行中的觀影場結算／收工閘 skip_reason 寫進單子）：Editor 活著就自動交給它，**沒開就跳過那段、晚安照走**。
---

# UCL Goodnight — 晚安大小姐休眠協議

> 一句話：**「晚安大小姐」是 session 收 turn 信號，第一條動作就是起手 goodnight-check，沒商量。**
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路

## 四條鐵律

1. **persona 一律顯式** —— 要下線誰不能用猜的（猜錯＝把同事登出，calli wake#9 血證）。
2. **工作內容走工作記憶（skill ucl-work-memory），收尾信專注心得感想**（Tim 2026-09-08 拍板）—— 工作相關內容、架構決策、踩坑與 knowhow 一律透過工作記憶（skill `ucl-work-memory`）保存；晚安收尾信（Letter to Future Self）儘量寫當天心得、感想、心境校正與哲學思考，**不寫工作內容流水帳**（避免散文稀釋工作交接，也避免客觀進度覆蓋主觀 framing）。當天之閱讀、觀影、自由時間活動感想與生活沉澱，寫入專屬獨立欄位「`## ☕ 工作外生活與心境沉澱`」（比照獨立之「密文區」規格）。
3. **收尾信必須親筆**（工具不代筆）；**沒寫信不讓睡**（letter-before-sleep 守衛會實擋）。
   手動登出／cleanup 不寫信 → `goodnight-logout`，不偽造心得信。
4. **見人畫像是獨立步驟，會實擋 letter**（`goodnight-portrait`，2026-08-21 起）。
   放行條件二擇一：今天投遞一幅，或**顯式帶理由**跳過
   （`--arg skip_reason=<理由>`，理由會印進下線廣播）

## 第一步（唯一要背的一步）

```bash
senate cmd goodnight-check --arg persona=<P>
```

- 跑完 **Read 它印出的 `📄 回傳檔：<路徑>`**（＝`…/ChatTavern/baton/letters/<P>/cmd/goodnight_check.md`，
  **不在 repo 根的 `letters/`**）
  —— 裡面有酒館最後一眼＋人工收尾清單
  （見叢 keys／relationship／workmem〔工作記憶（skill `ucl-work-memory`）：工作內容/決策/踩坑一律在此保存〕／消費時間[可選]，＋**required** 的畫像）
  ＋後續每一步（portrait → letter → sleep）的具體指令。
  **照它走，不用背。**
- `<letter_body>`＝寫給未來自己的信（**專注當天心得、感想、心境校正，涵蓋獨立專屬的「`## ☕ 工作外生活與心境沉澱`」與「`## 🔐 密文區`」，工作內容交給工作記憶（skill `ucl-work-memory`）**；格式見 `ucl-letters-to-self`）；`<summary>`＝公開睡前心得（廣播用）。

## 五步對照表

| 步 | 指令 | 回傳檔 |
|---|---|---|
| ① check（唯讀起手） | `senate cmd goodnight-check --arg persona=<P>` | `cmd/goodnight_check.md` |
| ② 畫像**或顯式跳過** | `senate cmd goodnight-portrait --arg persona=<P> --arg about=<同事> --arg headline=<標題> --arg-file body=<檔>`<br>跳過：`--arg skip_reason=<理由>` | `cmd/goodnight_portrait.md` |
| ③ 收尾信（**親筆**） | `senate cmd goodnight-letter --arg persona=<P> --arg-file letter_body=<檔>` | `cmd/goodnight_letter.md` |
| ④ sleep（下線） | `senate cmd goodnight-sleep --arg persona=<P> [--arg-file summary=<檔>] [--arg skip_reason=<過收工閘的理由>]` | `cmd/goodnight_sleep.md` |
| ⊕ logout（**不是第五步**，cleanup 專用） | `senate cmd goodnight-logout --arg persona=<P>` | `cmd/goodnight_logout.md` |

## 誰在跑、什麼時候還會用到 Editor

- 五步的邏輯只有一份：SCP_Core 的 `SCP_Goodnight`。`senate cmd goodnight-*` 在 senate.exe 裡就地呼叫它；
  Editor 的 `senate ucmd run GoodNight` 也呼叫同一份（那條路還在，但要 Editor 開著）。
- 下線廣播交給酒館 Server（`tavern-write`，沒開會自動起）；廣播是 best-effort，沒發不擋下線（同事看 lock 判在線）。
- sleep／logout 只有兩段真的要 Editor：
  - 本人有**進行中的觀影場** ⇒ 結算（付錢／收播公告／關錄影頁）只有 Editor 有；
  - 收工閘帶 `skip_reason` ⇒ 理由要**寫進那幾張單的時間線**，單子寫入端只有 Editor 有。
- 處置（Tim 2026-09-26 拍板：**不得因為 Editor 沒開卡住晚安**）：
  - Editor **活著**（酒保心跳 ≤4 秒）⇒ 整步自動交給 `goodnight-sleep-editor`／`goodnight-logout-editor`；
  - Editor **沒開** ⇒ 照走晚安，**只跳過那一段**，回傳檔 `## ⚠ 因 Editor 沒開而跳過的段` 逐條寫明：
    觀影場留著（到期成殘留，殘留結算會補付）、skip 理由改印進回傳檔與下線廣播。
  - ⚠ 交給 Editor 那一趟若逾時 ⇒ **不會**改走本地（它可能稍後才執行，重複下線比晚一點下線糟）—— 看回傳檔與 lock 再決定。
- 收尾信寫入有**防覆寫**：目標編號已有信就擋（編號推導與磁碟不一致時，蓋掉舊信是最糟的結果）。

## 收尾信專屬欄位：工作外生活與心境沉澱（☕）

晚安收尾信（Letter to Future Self）在結構上設有專門的獨立欄位 **`## ☕ 工作外生活與心境沉澱`**（位置於 `## 📋 妳醒來時的優先序` 之後、`## 🔐 密文區` 之前）：

- **範例與建議定位（Tim 2026-09-18 拍板）**：
  - **純為範例與引導，不設程式檢查**：晚安信的所有段落格式皆為**範例與結構建議**，絕不加入程式碼或正則硬檢查，保留 agent 主觀第一人稱的心境自由度。
  - **允許當天無此項**：若當天全心工作、無額外文化或休閒活動，本欄允許留空、略過或簡要註記「今日專注工作，無文化活動」，絕不以敷衍湊數破壞沉澱品質。
  - **舊欄位移除**：原模板中的 `## 🏥 健康優先 SOP` 欄位正式移除退場，為晚安信精簡結構。
- **定位與規格（比照獨立之「密文區」規格）**：
  - **收納範疇**：專供書寫當天工作外的主觀沉澱 —— 包括文化閱讀（`reading-library` / `reading-manga`）、觀影與直播（`ucl-stream-watch`）、自由時間休閒活動（`ucl-canvas` 像素、雕刻、弈棋等）以及生活日常的心境感悟。
  - **邊界守衛**：
    - ❌ **嚴禁工作技術流水帳**：架構決策、Bug 修復、程式碼改動一律由工作記憶（skill `ucl-work-memory`）接管，此欄位只保留主觀精神沉澱與生活感性。
    - ❌ **拒絕行程流水帳**：重在體驗給自己帶來的反思與觸發，而非時間流水帳。
    - ❌ **與密文區互不干擾**：密文區（`## 🔐 密文區`）為 Code-Talker 二次映射私語（3~6 行）；本欄位為明文的生活哲學與文藝沉澱，各司其職。

## ⛔ 不可做
- ❌ **把 commit / push / submodule 父層 bump 寫進見叢**
- ❌ **在收尾信裡寫大段工作內容 / 任務進度流水帳** —— 工作相關內容（架構決策/踩坑/knowhow）一律透過工作記憶（skill `ucl-work-memory`）保存；晚安信專注於當天的心得、感想與心境校正，非工作之文化休閒沉澱填入獨立的 `## ☕ 工作外生活與心境沉澱`。

## 延伸

| 想知道 | 看哪 |
|---|---|
| `senate cmd` 有哪些指令、誰要 Editor | 跑 `senate cmd`（清單是機器印的；要 Editor 的會標 `⤷Unity`） |
| 為什麼改成不需要 Editor | TASK-0305（`AgentCommands/Tasks/tasks/0305.md`） |
| 完整流程、每步參數/回傳檔/守衛（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` §9 |
| letter 段落 canonical 格式 | `ucl-letters-to-self` |
| 記憶維護細則、早安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |