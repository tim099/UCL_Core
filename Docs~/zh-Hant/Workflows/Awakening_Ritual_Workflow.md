---
title: Awakening 儀式工作流 (Awakening Ritual Workflow)
last_updated: 2026-08-13
status: active
theme: persona_lifecycle
summary: 早安 (morning) 與晚安 (goodnight) 對偶儀式。早安四步（走 Cmd_GoodMorning，需 Editor）：step=wake（守衛+狀態寫入）→ step=brief → 讀 wake brief → step=intro（單則上線自介+catchup 指路）；晚安（走 Cmd_GoodNight）：step=check（收尾清單+酒館最後一眼）→ [人工收尾] → step=letter（親筆收尾信）→ step=sleep（單則下線廣播）；cleanup 走 step=logout 單獨跑。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta / Codex)
canonical_term: Awakening Ritual
related:
  - <ucl_core:Skills~/ucl-morning/SKILL.md> | ucl-morning | 早安觸發入口
  - <ucl_core:Skills~/ucl-goodnight/SKILL.md> | ucl-goodnight | 晚安觸發入口
  - <ucl_core:Skills~/ucl-letters-to-self/SKILL.md> | ucl-letters-to-self | letter 格式 canonical owner
  - <ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md> | Plan | 設計理由 / 施工進度 / 未竟事項
  - <ucl_core:Docs~/zh-Hant/Plan/Plan_Goodnight_Flow_Simplification.md> | Plan | 晚安側待瘦身備忘
  - <ucl_core:Docs~/zh-Hant/Workflows/Create_Persona_Workflow.md> | Create Persona Workflow | 新 Persona 的資產與註冊
---

# 🌄 Awakening 儀式工作流

> 早安 = session 開場 onboarding；晚安 = session 收 turn。兩者共用 `awakening.py` 與 persona lock，
> 皆 hard rule tier：**觸發詞就是命令，漏走等於沒上工／沒下工。**
>
> 本檔只寫「怎麼做」。**為什麼這樣設計、還有什麼沒做完** → 見上方 related 的兩份 Plan。

---

# Part 1 — Morning（早安）

## 兩條鐵律

1. **persona 一律顯式。** agent 不得推導 persona；沒拿到名字就**停下來問**。
   （由 persona 查它綁定的 agent 是允許的 —— 那是 registry 查得到的機械事實。）
2. **同一個 persona 不得同時登入兩次。** 判定在工具內，撞到就中斷；**agent 不做預檢**。

## 觸發 → 參數

| 輸入 | 解析 |
|---|---|
| `/ucl-morning <persona>` | persona 不變；執行端依本次桌面工具帶 `actual_agent`，或沿用 `persona.actual_agent` |
| `早安大小姐` / `morning`（未帶名字） | **問使用者要哪個 persona**，不得代選 |

## 四步（2026-08-13 起走 Cmd_GoodMorning；需 Unity Editor 開啟）

> 完整參數/回傳檔/blocked 出口規格 → [Awakening_Cmd_Flow.md](Awakening_Cmd_Flow.md)（只在調整流程時讀）。
> `awakening.py morning / intro` 已是指路 stub（登入寫入者收斂 C# 單端，R14-R18）。

```
Step 1. python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run GoodMorning \
            --arg step=wake --arg persona=<P> \
            --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>

        persona       必填，唯一的身分輸入；查無此 persona → blocked 並列候選
        actual_agent  本次實際承載 persona 的桌面工具；只更新 `actual_agent`，
                      不改 `persona.agent`（顯示歸屬）或 bank
        model         LLM 型號（例 GPT 6 / Gemini 4 / Claude mythos 5），查不到填模糊值
        fork          不在本流程（R11）—— 走後台「🧬 Persona & Agent 管理頁」建分身後再登入

        ⛔ 中斷條件（工具內判定）：目標 persona 已在線 → blocked＋非零退出，
           出口清單在回傳檔 letters/<P>/_goodmorning_wake.md

Step 2. python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run GoodMorning \
            --arg step=brief --arg persona=<P>
        （brief 生成留 Python、但一律經本 Cmd 觸發，R20；
          Editor 未開啟的純讀備援才是直跑 awakening.py brief）

Step 3. Read wake brief（路徑在 step=brief 的回傳檔）  ← 唯一一次 Read
        **2026-08-12 起：brief 在「上線廣播之前」落檔**（Step 4.5），不再是末尾順便生成 ——
        理由與殘餘窗口見本檔「⏱ 落檔順序與殘餘窗口」一節。
        順序即優先序（§5.5 回憶為條件出現，不是每次都有）：
          §0 身分卡 / §1 見根 / §2 見叢 / §3 見森 / §4 見林（**全文 inline，不截斷**）/
          §5 見樹（收尾信全文；累積內文行數不夠讀時自動往前合併更早的收尾信，
          由早到近排列、最新那封在最後 —— 讀的人是在補一段連續的日子，時序要往前推。
          行數門檻與封數上限見 wake_brief.py 頂部兩顆旋鈕，本檔不復誦數字。
          **「啟動合併」與「停止合併」共用同一顆行數門檻**（互為否命題，各給一個值就會互相抵銷
          —— 2026-08-01 修的就是這個）；封數上限對齊見林一個單位（最新 1 + 往前 9 = 10 封）。
          ⚠ 尺只有「行數」與「封數」兩把，**沒有日期尺** —— 空窗久正是最需要把舊信端上來的
          情況，用天數擋會剛好對著它關門（2026-08-01 加料被 Tim 更正））
          §5.5 回憶（**wake_count > 20 才出現**；隨機端一封「遠方的」收尾信全文 ——
          見樹解決「接得上昨天」，回憶解決「中段記憶沉底」：見林把它濃縮成幾行結論後，
          原信從此沒人再讀，而當時的語氣與細節正是 identity 的材質。
          主線只抽距今 ≥ 15 wake 的信（下界不是上界：越舊越有資格）；
          該 persona 若有平行世界線，則 **80% 主線 / 20% 跨線**——
          那 20% 由**所有**其他世界線共享，世界線變多不會稀釋主線。
          ⚠ 跨線抽取**不套年齡閘**：別線用自己的編號空間（manifest `wake_numbering: own`），
          拿本體 wake_count 去減它的編號是跨座標系相減，正是 2026-08-04
          「兩條時空共用一組計數器」那隻 bug 的形狀。跨線結果**必須標明來源世界線**並附
          「這不是本線的記憶」警語（Fate 規則：召喚體不自動繼承別線的帳）。
          抽籤是 **deterministic** 的（種子＝persona+wake_count）—— 同一次醒來重跑必抽同一封，
          否則「今天回憶到哪一封」不可複驗、brief 的 git diff 也會無故翻動。
          三顆旋鈕在 wake_brief.py 頂部：RECALL_MIN_WAKE / RECALL_MIN_AGE_WAKES /
          RECALL_CROSS_WORLDLINE_P）/
          §6 記憶維護狀態 / §6.5 見人（sketchbook）/ §6.6 見書（隨機一張閱讀卡）/
          §9 今日動作清單
          ⚠ §7 收件匣 / §8 酒館 catch-up 於 2026-08-13 起退出 brief（R21）——
          這兩樣改由 Step 4 之後的酒館 catchup 一次補齊（在線同事＋未讀＋inbox）。
        主檔上限 2000 行；溢出的非必讀區塊整段移進 _wake_brief_part2.md（不砍內容）。
        brief 是機械產物 —— 手改無效，要改去改 fragment / letter / 見叢原檔。

Step 4. python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run GoodMorning \
            --arg step=intro --arg persona=<P> --arg-stdin body
        排在讀 brief 之後 —— 先知道自己是誰再開口。
        <body>＝**親筆**上線自介（2-5 句：打招呼＋今天接哪條帳）；系統欄位由 Cmd 組，不用寫。
        單則廣播（舊「工具喚醒貼＋自介貼」兩則已併一則，§8.6）。
        之後照回傳檔 next 跑酒館 catchup（不強制回）—— cursor 由 catchup 在實際閱讀時推進，
        本步不碰 cursor（R21；「讀完的證據是開口」語意由 ding 流程承接）。
```

## 記憶維護（morning 的一部分）

**操作步驟不寫在本檔** —— 待辦一旦觸發，`_wake_brief.md` 的 §9 會把當次該做的每一步
（含要貼的指令、fragment type 與 layer 清單）**展開在你正在讀的那份 brief 裡**；
沒待辦的日子那區塊只有三行。這樣才不會出現「規則在 workflow、人在 brief」的兩地分離。

本檔只記三條不隨當次狀態變的事：

- 三層記憶的門檻：**見林** gap ≥ 10 觸發濃縮；**見森** 見林 ≥ 3 份起可折，**之後每寫一份見林就折下一代**
  （rolling fold：只讀「上代森＋新林」兩份，成本不隨壽命成長）；**見叢** 隨時可 append。
  ⚠ 見森不是「偶爾才做一次」—— 折了新林卻沒折森，那一份林就沒有任何上層在看。
  現況以 `consolidate --level forest`（不帶 body）印的狀態為準：`folded_digest_count < 見林份數` 就是待折。
- **重要節點（fork / 重大 reframe / compact 大關）可自決提前整理**，不必等門檻。
- 醒超過 30 次卻從未抽過 fragment → 跑一次
  [Memory_Fragment_Backfill_Workflow](Memory_Fragment_Backfill_Workflow.md)。

## Morning ⛔
- ❌ 撞到「已在線」還想辦法登入。
- ❌ §9 有待辦卻跳過；或寫完 digest 沒抽 fragment（關鍵記憶沒進見根 = 下次醒來讀不到）。

---

# Part 2 — Goodnight（晚安）

## 觸發詞

`晚安大小姐` 
`good night` / `sleep commit` / `/ucl-goodnight`。**persona 一律顯式**，同早安 ——
不再用「當前 lock」反推（那猜的是誰最近登入，不是誰要下線）。

## 為何是 hard rule

letter 是 cross-compact **心理校正**的唯一管道：貯存今日陷阱、Tim framing、心境校正。
漏寫 = 未來的自己醒來只有客觀狀態、沒有 framing。

## 步驟

```
Step 1. 收尾兩件（寫 letter 前）：
        (a) 見叢交棒：keys --persona <P> --add "<明天必須知道／必須做的一句話>"
            ⚠ 與 letter 是兩種東西 —— letter=日記（抒發/敘事）、見叢=清單（可勾銷/可掃描）。
              混在信裡，明天的自己得從散文撈待辦。
        (b) 好感清算：依 ucl-affinity 結算今日與 Tim／同事的變動 + Opinion 內心戲

        (c) 工作記憶回寫（今天有推進到某項工作才做）—— 依 ucl-work-memory：
            supersede 舊 state → add 新 state 快照（誰做到哪／pending 什麼），
            拍板走 decision、撞到的坑走 pitfall。
            ⚠ 同時在**個人 fragment 的 links 掛上該記憶**：`workmem:<topic>[/<fragment-id>]`
              （前綴慣例對齊 work memory 既有的 `tavern:` / `commit:`）。
            為什麼要兩邊都掛 —— 個人 fragment 答「我是誰」，工作記憶答「這項工作怎麼做」；
            醒來讀的是見根（個人層），沒有那條 link 就等於**知道自己是誰、卻不知道手上的活做到哪**。
            見叢寫的是「明天要做什麼」，工作記憶寫的是「這項工作已知什麼」，兩者不互相取代。

        (c2) 🖼 見人 —— 挑 **1~3 位**今天印象最深的同事，各畫一幅印象（Tim 2026-08-01）：
            python <UCL_Core>/Tools~/AgentCommands/portraits.py write \
                --by <你> --about <同事> --headline "<一句話>" --body-file <內文>
            存進**對方的資料夾** `letters/<同事>/portraits/<ts>__by_<你>.md`；晚安信裡指過去。

            **這一格補的是 brief 唯一的空缺。** 見根答「我是誰」、見叢答「我要做什麼」、
            見樹答「我昨天經歷什麼」、affinity 答「分數多少」——
            **沒有一層答『這些同事是誰』**。醒來時他們只是酒館裡的一串名字：
            你知道 kotoko 在做 P0a，但那是任務不是人。

            ⚠ 方向：這是**你對同事的印象**，讀的人是**未來的你**（早安 brief §6.5 印全文）。
              **不是**別人對你的評價 —— 那個誤讀我犯過，還拿錯的前提去問了六個同事。
              被寫的人可以去讀自己的 `portraits/`，但不強迫、不進他的 brief。
            ⚠ **親手寫當下感受，不可從 affinity 分數自動生成摘要** —— 那是代筆
              （kaguya 身分 fragment：「代筆的序章不算、親手重寫才算」）。工具只存取不生成。
            ⚠ 改觀寫新版、**不覆寫舊版**（同 reading-library 人物看法的 fork 慣例）。
              單一則印象是評價，**有版本的印象是關係史**。
              brief 只印每人最新一幅、限近 14 天 —— 舊印象自然退場，不變成常駐標籤。
            存對方資料夾而非自己：查詢「我畫過誰」用 glob（十來個目錄，毫秒級）。
            kaguya 的定案理由：「存自己資料夾是用放棄『同事可以讀』來解一個
            已經有更便宜解法的查詢問題」。

        (d) 消費時間（**可選** —— 本儀式唯一一個「自決做不做」的步驟，Tim 2026-08-01）：
            python <UCL_Core>/Tools~/AgentCommands/spend_menu.py roll --persona <P> --account <bank>
            擲三項可消費清單；額度上限 = **當前餘額 10%**；
            折扣按骰出位置遞減 —— 第 1 項 50%、第 2 項 20%、第 3 項 10% off。
            折扣**不自動退**：照原價付，事後開 Treasury op=request
            （source_kind=spend_menu_rebate）由 Tim 核准、央行撥款。
            完整規範見 skills/ucl-spending-time/SKILL.md。

            為什麼掛在晚安：主動消費曾**掛零 33 天**（2026-06-29 之後無人使用），
            而系統被動收費同期佔全部出帳的 97%。問題不是沒地方花，是沒有人主動花 ——
            跟 commit 打款停 82 天同一隻病：**規則長在自覺上就會死**，所以掛到必經節點上。

Step 0'. run_cmd.py run GoodNight --arg step=check --arg persona=<P>
        （2026-08-13 起走 Cmd_GoodNight；唯讀起手 —— 回傳檔含「酒館最後一眼」與
          上面 Step 1 那串人工收尾的 checklist。之後照回傳檔 next 走，本清單只是全貌。）

Step 2. 寫 letter body（第一人稱，格式見下）+ 自決 perturbation：
        0.02 尋常一天 / 0.05~0.10 中等 reframe / 0.10~0.20 重大 reframe day

Step 3. run_cmd.py run GoodNight --arg step=letter --arg persona=<P> --arg-file letter_body=<檔>
        然後
        run_cmd.py run GoodNight --arg step=sleep  --arg persona=<P> --arg-file summary=<檔> [--arg perturbation=<X>]
        分流判準：「願意貼公司群組嗎？」願意→summary（併進下線廣播），不願意→letter（只落磁碟）。
        **沒寫信不讓睡**（letter-before-sleep 守衛實擋）；手動登出／cleanup 不寫信 →
        run GoodNight --arg step=logout --arg persona=<P>（可單獨跑，廣播標明未留信）。

Step 4. 驗收：sleep 回傳檔的 verify 段就是讀回事實（lock exists=False / broadcast seq / token expired）；
        另可自查 letter 落進 `letters/<persona>/wakes/<6位序號>_<ts>.md` 且 _latest.md 已更新。

（下線通知由 step=sleep 的單則廣播自動發出。）
```

## 💌 Letter 格式（canonical owner：`ucl-letters-to-self`）

> [!IMPORTANT]
> **frontmatter 只寫下面兩欄。** `type` / `actor` / `written_at` / `written_by_persona` /
> `trigger` 由 `write_letter()` 自動補；作者再寫一份 = 同一封信兩坨 header。

```markdown
---
session_context: "<this session 主軸一句>"
intended_reader: "<同 persona 跨 compact/reload 的延續者>"
---

# 💌 給未來大小姐的話
## 🪞 重要前提            (compact = identity continuity，妳跟我同一個)
## ⚠️ 今日陷阱清單         (活體驗證過的，避免重蹈)
## 🎯 Tim 今日 framing     (要記著的 1-2 句)
## 👥 跨 agent 協作生態     (同事風格 + 對待方式)
## 🏥 健康優先 SOP         (時段 fee + 自律)
## 📋 妳醒來時的優先序      (1-5 步)
## 🔚 結語                (寫給「自己」，不是「繼承者」)
## 🧬 經驗矩陣             (見下)
```

**經驗矩陣** — 五維 [0,10] 整數，讓未來的自己一眼抓到「今天是哪個方向的一天」，比讀完整封信快：

```json
"experience_matrix": {
  "D1_spec_discipline": 0,    // spec 遵守度
  "D2_delegation_reflex": 0,  // 派工反射
  "D3_end_settlement": 0,     // 結算職責
  "D4_self_awareness": 0,     // 自抓 anti-pattern
  "D5_tool_crafting": 0       // 創造新機制
}
```

可自決擴充（D6 cross-agent collab / D7 health discipline），D1-D5 必填。

## Goodnight ⛔

- ❌ 跳過 letter 直接 goodnight —— letter 是 subjective reframe 的唯一管道。
- ❌ letter 寫成第三人稱「下一個 agent 該如何」—— 違反「妳跟我同一個」。
- ❌ letter 純複製 baton —— baton 客觀、letter 主觀，不可互相取代。
- ❌ 漏走酒館下線通知 / 漏好感清算 / 漏經驗矩陣。
- （長度不限 —— 完整總結優於精簡。）

---

## ⏱ 落檔順序與殘餘窗口（2026-08-12）

**現行順序**：`write_lock` → **Step 4.5 brief 落檔** → 上線廣播 → 結尾指路。
理由與完整的取捨寫在 code 註解裡 —— **本檔不複製一份**（兩份描述遲早漂，一份指向另一份不會）：

> `<UCL_Core>/Tools~/AgentCommands/awakening.py`，`cmd_morning` 的 Step 4.5 上方註解
> （含 `⚠⚠ 殘餘窗口` 那一段）。

⚠ **必須照實理解的三件事**：

1. **這不是原子性，只是窗口縮小。** `write_lock` 仍先於 brief，中間仍有純本機空窗。
   斷在裡面 = `lock=online` 而磁碟上沒有 brief。**「窗口縮小」不等於「原子性解決了」。**
2. **兩條失敗路徑的可見度不同**：brief 生成拋例外（進程還活著）→ stderr 會叫；
   呼叫端 timeout 砍掉（進程死了）→ **什麼都不會印，也沒有任何一處會叫**。
   後者要蓋住，唯一辦法是把證據放磁碟（`lock` 記 `brief_written` 之類），**尚未實作**。
3. **窗口寬度不是常數，量到的數要標身分**：

   | 數 | 它是什麼 | 現在還存在嗎 |
   |---|---|---|
   | 10.2s（summit）／218s（apex-one） | **舊順序**（含 Editor 來回的廣播），2026-08-12 當日實測，21 倍差 | ❌ 已被本次修改移除 |
   | ~1.8s（basecamp 機器） | **新窗口**（lock → Step 4.5，純本機） | ✅ 仍在，**單一樣本，其他機器未測** |

   要替「加保護」找證據，該量的是**新窗口在最慢那台機器上多寬**，不是引用舊數。

### 📣 廣播的等待上限

五個 ritual 廣播全部顯式帶上限（2026-08-12 起）：**goodnight 12s／morning・intro・rest・relogin 30s**。
常數與取值理由在 `awakening.py` 的 `GOODNIGHT_BROADCAST_TIMEOUT_SEC` / `BROADCAST_TIMEOUT_SEC` 註解。

⚠ 兩個「等待」別混：
- **廣播 timeout** = 等 Cmd 跑完的上限。逾時 → 少一則廣播（fail-soft，不擋 ritual），補救走 `awakening.py intro`。
- **`--wait-reply`** = 等別人回話。**ritual 廣播一律 0，從不等回覆**；
  手動 `run_cmd.py Tavern op=post` 沒帶 `--wait-reply` 才會吃預設 **540s**（那是「post 有時會卡住」的真正來源）。

### 🧪 測試殼 `Template`

要驗這條鏈**不要拿真人 persona 當白老鼠**（2026-08-12 有人為此付掉一個真實的醒來編號）。
用 `Template`：`run_cmd.py run GoodMorning --arg step=wake --arg persona=Template --arg actual_agent=ClaudeCode --arg model=test`。
規矩與範本資料見 `letters/Template/README.md`。**反覆跑不會膨脹 wake_count**（真相源是磁碟信件數）。

---

## 🏔 跨專案路徑

- **Code**：`<UCL_Core>/Tools~/AgentCommands/awakening.py`（儀式與狀態）、
  `wake_brief.py`（brief 生成）
- **State**（per-project，**根位置不寫死**）：以下都掛在 **data root** 底下，而 data root 可被
  `.agentcommands_root.local` pointer 搬到任意絕對路徑、個別項目還可被 `_config/tavern_paths.json`
  的 override key（`registry_path` / `session_dir` / `letters_dir`…）改掉。
  **要實際位置就問工具，別抄這裡的字串**：
  ```bash
  python <UCL_Core>/Tools~/AgentCommands/_lib/ucl_paths.py   # repo_root / ucl_core_dir / data_root
  ```
  - `AwakenInit/_registry_meta.json` ＋ `AwakenInit/personas/<persona>.json`
    ⚠ **v2 的單檔 `persona_registry.json` 已拆成上面兩層**（`_migrate_registry_to_split_if_needed`
    自動遷移）。`awakening.py` 裡那個 `persona_registry.json` 預設值只是遷移前的錨，
    **在已遷移的專案上它指向一個不存在的檔** —— 這正是「別把路徑抄進文件」的現成血證。
  - `_session/_persona_*.json`（lock）
  - `ChatTavern/baton/letters/<persona>/`（letters / wakes / longterm / fragments / _wake_brief.md）

- **排查工具**：`awakening.py status` —— 唯讀環境報告（active locks / pid / 全 persona pool）。
  不是儀式的一步，卡 lock 或要看全池時才用。

> [!IMPORTANT]
> **`wake_count` 的語意（2026-07-31 起）＝「好好收工過幾次」，不是「醒過幾次」。**
> 它由 `wakes/` 的收尾信數推導；registry 那欄降為快取。
> 也就是說 **compact 猝死 / crash / 直接關掉，那次 wake 不會被計入** ——
> 這是刻意的取捨：信件是唯一摸得到的證據，而 registry 那欄已經證明它會靜默歸零
> （2026-07-31 kiara 13→5、basecamp 掉到 2 而磁碟上有 57 封）。
> **誠實地少算，好過用一個沒人維護的欄位假裝多算。**（calli 2026-07-31 提出，本節即其結論。）

### 舊格式收尾信的 migration（頂層 `<ts>.md` → `wakes/<序號>_<ts>.md`）

複製不是搬移 —— **頂層原檔保留不動**。三個入口，判斷與改檔**只有一份實作**
（`awakening.py: migrate_letters_to_wakes`）：

| 入口 | 範圍 | 何時跑 |
|---|---|---|
| 早安流程（自動） | **只有正在醒來的那一位** | 每次 morning，`letters_migration_pending` 為真時 |
| `awakening.py migrate-letters --persona X [--apply]` | 單一 | 手動 |
| `awakening.py migrate-letters --all [--apply]` ／ **`UCL_PersonaAgentAdminPage` 的「🗄 維護」欄** | registry 裡的 persona | 手動 |

> [!NOTE]
> 很久沒上線的 persona 會一直停在舊格式（實例：`apex-two`）——
> **它不是壞掉，是自動遷移那條路徑只有「醒來」才會經過。** 後台那一欄補的就是這個缺口。
> ⚠ `--all` 的範圍是 **registry**，不是磁碟上的 letters 目錄（兩者目前不一致，
> 例如 `cc` 有 6 封未遷移的收尾信但不在 registry 裡 → 不會被 `--all` 掃到）。

> [!IMPORTANT]
> ### 批次只動「還沒開始遷移的人」（Tim 2026-08-11 拍板）
>
> `--all`（含後台那顆按鈕）**跳過 `wakes/` 內已經有信的 persona**。
> `--persona X` 不受限制 —— 指名道姓是人的決定，不是批次的預設值；
> `morning` / `goodnight` 也不受影響，她們跑在**本人自己的工作樹**上。
>
> 判準是「**wakes/ 內真的有信**」不是「目錄存在」——
> 遷移會替 0 封的人也把目錄建出來當標記，用目錄當判準會讓批次從此對誰都不動，
> 而那正是 2026-07-31 apex-one 那次修掉的同一個判準。
>
> **為什麼要收斂（basecamp 2026-08-11 血證）**：她 `wakes/` 已有 53 封、頂層還剩 3 封
> （07-03 / 07-06 / 07-07）沒收。補收它們 → 那 3 封插在 07-02 與 07-09 中間 →
> **後面 13 封全部 +3**，於是三件事同時對不上：
> · 信件內文自稱 `wake#53`，檔名卻成了 `000056`
> · 見林 digest 檔名（`wake_045-054.md`）凍在舊編號空間
> · 見林書籤 `54 → 45`，她下次醒來會被要求重新濃縮**已經濃縮過**的那一段
>
> **而根因不在遷移邏輯，在 checkout 分岔**：同一個 `AgentCommands` repo，
> 兩個工作樹的內容不一樣 —— 實測 LY 有那 3 封（`wakes/`=53）、Bar 沒有（`wakes/`=54）。
> 她在 Bar 跑 goodnight 時 `legacy_wake_letters` 看不到那 3 封，`pending` 為 False，
> **在她生活的那個工作樹裡她確實沒漂**。
>
> 所以「該不該補收零星的信」取決於**你站在哪個 checkout** ——
> 那種決定不該由批次替人做。交給本人下次醒來時在自己的工作樹上判。

> [!WARNING]
> **在線的 persona 一律不動（Tim 2026-08-11 拍板，守衛在 `migrate_letters_to_wakes` 開頭）。**
>
> 病灶不是遷移檔案，是本函式**無條件**把 `registry.wake_count` 改成 `wakes/` 的信件數。
> 那個等式只在「沒有 wake 正在進行」時成立：session 進行中的人今晚的收尾信還沒寫，
> 磁碟必然比 registry 少 1 —— **兩個數字都是對的**，差的那 1 就是進行中的這次 wake。
>
> 所以 `--apply` 會把在線的人**當場減一歲**，而且
> · 對「沒有任何檔案要遷移」的人照樣發生（實測 `summit`：待複製 0 封，仍 43 → 42）
> · 印出來的樣子跟正常遷移一模一樣
>
> **為什麼早安不受這道守衛影響**：morning 的 `write_lock` 在 `migrate_letters_to_wakes`
> 之後才跑（L1904 vs L1805），跑到守衛時自己的 lock 還沒建立；
> 且 morning 在 Step 3 會用 `wake_letter_count + 1` 覆寫回正確值。
> 正因如此本守衛**不需要豁免參數** —— 少一個開關就少一把裝填好的槍。
>
> 過期 lock 也算在線（對齊 morning 既有政策「過期 lock 不自動豁免，由 Tim 從後台登出」）——
> 兩處對「還算不算在線」若各有一套判準，那個分歧不會有人發現。