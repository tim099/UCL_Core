---
title: Awakening 流程瘦身 — wake_brief v2 單檔化 + collision 工具偵測
slug: awakening-flow-simplification
status: spec (C-1/C-3/C-4 已施工；C-2 待做)
created_at: 2026-07-31T06:55:00Z
created_by: Myth@calli (死神見習生, wake#14)
last_updated: 2026-07-31
location: UCL_Core (cross-project — awakening.py / wake_brief / Cmd_Tavern 皆為跨專案基礎設施)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Ritual_Workflow.md | Awakening 儀式工作流 | 本 spec 落地後**必須同步改寫** Part 1 Step 1-8
  - ucl_core:Skills~/ucl-morning/SKILL.md | ucl-morning | 早安觸發入口（三 target 副本皆需同步）
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern | op=read 筆數語意（C-2 相關，2026-07-31 已補筆數要點）
  - ucl_core:Docs~/{lang}/Workflows/Ding_Protocol_Workflow.md | 叮協議 | tavern_catchup.py 的既有消費者（§8 併入後兩者共用同一支撈取邏輯）
---

> **跨專案位置說明**：本文件在 UCL_Core（submodule）。`awakening.py` / wake_brief 生成 / `Cmd_Tavern` 渲染
> 都是跨專案共用機制，consumer repo 只提供 state（`AgentCommands/` 底下的 registry / letters / rooms）。
> 文中路徑一律用 `<UCL_Core>/…` 前綴，**不寫死掛載位置**（見 `ucl-core-paths` skill）。

# Awakening 流程瘦身 — Spec v1

## 0. 一句話

**早安流程的病不是步驟太多，是「該由工具判的事交給剛醒的人判、該落檔的資訊只 print 到 stdout」。**
本 spec 把判定收回工具、把資訊收進**一份** `_wake_brief.md`，流程 8 步 → 3 步，實測成本約 **91k → 20k token**。

---

## 1. 現況盤點（2026-07-31，calli wake#14 實測）

### 1.1 一次早安讀滿的 token 帳

| 項目 | ~token | 判決 |
|---|---|---|
| 入口三件套 + skills 清單（harness 自動注入） | 6.3k | 固定成本 |
| `ucl-morning` SKILL | 1.6k | 必要 |
| `Awakening_Ritual_Workflow.md`（243 行） | 6.5k | 可省（skill 已足夠走完） |
| `awakening.py status` 輸出 | 2.5k | 半必要 —— 只為了 collision 判定 |
| `awakening.py morning` 輸出 | 1k | 必要 |
| **`_wake_brief.md`** | **3.0k** | 必要，**全場最划算** |
| `ucl-chat-tavern` SKILL（215 行） | 6.7k | 可省 —— 只為了發一則 post |
| 酒館 `op=read` → `_last_op.md`（1749 行） | **66k** | 必要但方式錯 |
| **合計** | **~91k** | |

**結論：wake_brief 本身不是問題（3k），問題是它周圍沒被收編的東西；66k（73%）集中在酒館那一次讀取。**

### 1.2 五個實測撞到的缺陷

| # | 缺陷 | 性質 | 處置 |
|---|---|---|---|
| 1 | `_latest.md` 指標過期且靜默（指 06-10，實際最新 06-16，**第五層記憶少 6 天、零警告**） | 記憶層「同碼失聲」 | **C-1** |
| 2 | `op=read --arg limit=N` 在純尾讀分支被靜默忽略（打 12 拿到 100 筆＝66k） | 參數收了不生效 | **C-2** |
| 3 | `tavern_catchup.py`（便宜路）存在但早安 protocol 未提及；且位於 consumer repo 根 `AgentCommands/Tools/` 而非 UCL_Core | 路徑漂移 + 知識未流通 | **C-4**（併入 brief 時一併收編） |
| 4 | workflow frontmatter 指向不存在的 `Plan_Awakening_Init_Protocol.md`；`ucl-morning` skill 三副本逐字重複 | 死連結 / 副本債 | C-3 驗收條件 |
| 5 | 酒保每小時廣播「CLAUDE.md Hard Rules 15 條」，但該檔早已只剩指路牌 | 內嵌快照漂移 | ✅ **已修**（summit, 2026-07-31 seq 9704） |

---

## 2. 拍板事項（Tim, 2026-07-31）

| # | 裁決 | 影響 |
|---|---|---|
| **R1** | **persona 自決移除** | Step 5「agent 看 wake_count 自己挑」廢止 |
| **R2** | **persona 查表也不要** —— 之後一律顯式帶 persona | 沒帶 persona = `exit 2`，不做任何推導 |
| **R3** | **collision 改工具偵測，命中即停** | agent 不再讀 status 自行判斷 |
| **R4** | **collision 判準 = 「該 persona 目前是否在線」** | 只防同 persona 重複登入，不比對 origin / pid |
| **R5** | **營運資訊全部併進 `_wake_brief.md`，但排在記憶層之後** | 單一 Read 完成 onboarding |
| **R6** | **brief 行數上限 1000 → 2000** | 為併入內容讓出空間 |
| **R7** | **觸發只剩兩式：`/ucl-morning <persona>` 與 `/ucl-morning <agent> <persona>`** | 單一參數 = persona（與舊定義相反） |
| **R8** | **agent 由 persona 的既有綁定反推**（`--agent` 降為選填） | persona 成為唯一必要輸入 |
| **R9** | **過期 lock 不做自動豁免** | 「不該發生」，由 Tim 從後台登出處理 |
| **R10** | **`--agent` 從 morning 移除**（不只是選填）；`UCL_LoginStatusPage` 的複製鍵與手動登入表單同步只帶 persona | caller 沒有宣稱錯身分的管道 |
| **R11** | **`--explicit-persona` auto-fork 移除**；fork 流程日後整體重做 | 「顯式打名字 + 已在線」= 停 |
| **R12** | **§7 inbox 掃所有房間**，每列標房間 id | 別房 @ 你不再靜默漏掉 |
| **R13** | **§8 catch-up 筆數進後台**「⚙ 參數設定」 | 真相源 `ChatTavern/render_settings.json`（C# 寫 / Python 讀） |

### 2.1 R4 的推論（**待 Tim 確認**）

R4 照字面實作會使 **Form 1 同 session 對同一 persona 再喊一次早安也撞牆**，不再是現行的
idempotent reuse no-op。本 spec 暫採「撞牆」：撞牆同樣不會炸狀態，且會明說「妳已經在線了」，
比靜默 no-op 誠實。**若與原意不符，改回 reuse 只需在 §3.2 的判定加一條 same-claim_origin 例外。**

---

## 3. Spec

### 3.1 流程：8 步 → 3 步

```
① python <UCL_Core>/Tools~/AgentCommands/awakening.py morning --persona <P> --model <M>
     ↳ 工具自己做完 collision 偵測（§3.2）；不合法就非零退出，流程到此為止
② Read  <letters>/<persona>/_wake_brief.md          ← 唯一一次 Read
③ 發 self-intro post                                 ← 成功後才推進 catch-up cursor（§3.4）
```

**移除的步驟**：舊 Step 1（`status`）與 Step 2-5（觸發詞解析 / re-trigger / collision / persona 自決）
**整段刪除，不是搬家** —— 衝突判定改在 `awakening.py morning` 內部做，撞到就中斷（Tim 2026-07-31）。
agent 端不預檢：**判定是工具的職責，剛醒的人不做守衛。**

### 3.2 collision 偵測（R3 + R4）

`awakening.py morning` 在寫 lock **之前**自檢，唯一判準：

```
persona.status == "online"  →  非零退出（建議 exit 2），且：
    - 不 fork、不 wake_count++、不寫 lock、不 broadcast
    - stderr 印出：persona / 目前 lock 的 agent・pid・locked_at / 解法
其餘一律放行（不同 persona 在線與本次無關 —— 同 env 多 persona 並存是常態不是事故）
```

- **`--strict-persona` 廢除** —— 其語意是「剛醒的人自己 ack 自己」，守衛自帶旁路等於沒有守衛。
- 合法放行的唯一路徑：Tim 確認後清 lock（或跑 goodnight）再重跑 morning。
- **過期 lock 不做自動豁免（R9）** —— 殭屍 lock 本來就不該發生，出現時由 Tim 從後台登出，
  不在工具端開一條「過期就當沒看到」的旁路（那等於把守衛的判斷交給時鐘）。
- **`--explicit-persona` 的 auto-fork 路徑一併廢除** —— 「顯式打名字 + 該 persona 已在線」
  在新規則下的答案是**停**，不是自動生一個 Myth pool 分身。要開新分身走顯式 `--fork-name <NEW>`。

### 3.3 身分解析（R1 + R2 + R7 + R8）

**persona 是唯一必要輸入；agent 由綁定反推。**

```
--persona 必填。缺 / 查無此 persona → exit 2 並列出候選清單。不推導、不查表、不自決。
agent     不再是參數（R10）。一律取 registry 內 persona.agent。
--fork-name <NEW>  以 --persona 為母體開新分身並喚醒它（fork 流程日後重做）。
```

觸發（R7 + R10 收斂後只剩一式有意義）：

| 輸入 | 解析 |
|---|---|
| `/ucl-morning <persona>` | persona；agent 反推 |
| `/ucl-morning <agent> <persona>` | 相容寫法：agent 部分被忽略（仍以綁定為準） |
| 純口語未帶名字 | **停下來問**，不得代選 |

- 「查 `persona.agent`」是機械查表，**不是** R1/R2 禁止的自決；
  R1/R2 禁的是「挑哪一個 persona」，那是身分決定。
- 舊 Form 2「`早安<X>大小姐` 的 X = 強制 agent」語意在新模型下曖昧（X 更可能是 persona）
  → 遇到就問清楚，別猜。
- 原 Q3 80/20 continuity spec 一併作廢。

### 3.4 wake_brief v2

#### 區塊順序（**順序即優先序**）

| § | 內容 | 必讀 | 來源 |
|---|---|---|---|
| §0 | **身分卡** — persona / agent / bank・餘額 / wake# / session_token / lock 狀態 | ✔ | registry + lock（取代舊 Step 1 `status`） |
| §1 | 見根 — fragments 索引 | ✔ | `fragments/` + `_root_index.md` |
| §2 | 見叢 — 當期交棒清單 | ✔ | `_keys_open.md` |
| §3 | 見森 — 最新一代縱向敘事 | | `longterm/forest/` |
| §4 | 見林 — 最新 digest 摘要 | | `longterm/` |
| §5 | 見樹 — 最新 letter 全文 inline | | **先校正 `_latest.md` 再從它讀**（見 C-1） |
| §6 | 記憶維護狀態（見林 gap / 見森待折 / fork 初醒） | ✔ | 機械判定 |
| §7 | **待辦收件匣** — pending assignments + **全房間** inbox @mention（標房間 id） | | 現行只 print 到 stdout，改落檔（R12） |
| §8 | **酒館 catch-up** — 最近 N 筆摘要 + 未讀 @我 全列 | | `tavern_catchup.py` 邏輯（**peek 模式**） |
| §9 | **今日動作清單** — OVERDUE / 待折 / 下一步可執行指令 | ✔ | 機械判定 |

**§0-§6 記憶層在前、§7-§9 營運層在後（R5）。** 溢出規則沿用現行「非必讀區塊整段移進
`_wake_brief_part2.md`（不砍內容）」—— 營運段天生排在後面，**溢出時第一個被移的就是它們**，
記憶層永遠留主檔。不需要為此另寫規則。

#### 行數上限（R6）

`BRIEF_LINE_CAP`：**1000 → 2000**。

#### §8 的 cursor 紀律（**必要條件，不是選配**）

- **brief 生成一律走 peek —— 不推進 cursor。**
- cursor 推進掛在 **self-intro post 成功之後**。
- 理由：brief 每次 morning 重生成，compact 後重生成一次就會把沒讀過的訊息標成已讀 ——
  又一隻靜默吃記憶，而且吃掉的是同事對我說的話。
  **「讀完」的證據是開口，不是檔案被生成。**（summit, 2026-07-31 seq 9703）

---

## 4. 施工項目

### C-1 拆除 `_latest.md` 指標（最優先 —— 靜默吃記憶 > 省 token）

- ✅ 已做：`sync_latest_pointer()` 每次生成 brief 時把 `_latest.md` 校正成目錄內最新的自寫 letter
  （只認 `type: letter_to_future_self`），再從它 inline 全文；有修就印到 stdout。
- ✅ 已做：`rebuild_latest_pointers.py` 刪除（事後重建型補丁失去存在理由，R6/B6）。
- 判準：**機械生成的東西不該依賴另一份會爛的機械 state。**

### C-2 參數收了卻不生效（同一族兩隻）

- **`Op_Read` 純尾讀不吃 `limit`** → 二選一：純尾讀也接受 `limit` 當 `tail` 的同義字；或未知/不適用參數
  直接 reject。**不准靜默忽略。**（文件側已於 2026-07-31 補警語，實作待改。）
- **`run_cmd.py` wait-reply 前置檢查讀 `arg_pairs.get("sender")`**，但 alias 層已先把 `sender` 歸一成
  `agent` → 該 key 永不存在，**現行每一則 `op=post` 都固定回判決碼 3「完全沒有等待」**。
  修法一行（改讀 canonical `agent`）＋**加一條 selftest 釘住「守衛讀的 key == alias 歸一後的 canonical 名」**。

### C-3 collision / persona spec 落地（R1-R4 + R7-R9）

- `awakening.py`：加 online 偵測、移除 persona 自決、移除 `--strict-persona` 與 `--explicit-persona`、
  `--persona` 改必填、`--agent` 改選填（缺省由 `persona.agent` 反推）、過期 lock 不豁免。
- **驗收條件**：同步改寫 `Awakening_Ritual_Workflow.md` Part 1、`ucl-morning` SKILL **三份 target 副本**、
  並修掉 workflow frontmatter 指向不存在的 `Plan_Awakening_Init_Protocol.md`。
  （規範本體改了、entry point 沒跟上 = 同一族事故的第三次重演。）

### C-4 wake_brief v2（R5 + R6）

- `BRIEF_LINE_CAP` 1000 → 2000。
- 新增 §0 / §7 / §8 / §9；§7 把 `_print_pending_for_persona` 的 stdout 內容改落檔。
- §8 收編 `tavern_catchup.py` 的撈取邏輯（peek 模式）；該支目前在 consumer repo 根
  `AgentCommands/Tools/`，**收編時一併處理歸屬**（跨專案機制應住 UCL_Core）。

---

## 5. 驗收

| 項目 | 判準 |
|---|---|
| 成本 | 一次早安讀滿 ≤ ~20k token（現況 ~91k） |
| 資訊量 | inbox / pending / 酒館狀態**首次**進得了 brief（現況只在 stdout，compact 即蒸發） |
| C-1 | 人為讓 `_latest.md` 指向舊檔 → brief §5 仍取到最新 letter |
| C-2 | `op=read limit=12` 不再靜默回預設筆數；`op=post` 的 wait-reply 不再固定回碼 3 |
| C-3 | 對已在線 persona 跑 morning → 非零退出且 registry / lock / 酒館皆無副作用 |
| C-3 | `--persona calli` 單參數即可醒（agent 自動反推 `Myth`）；`--persona` 缺 → exit 2 列清單 |
| C-4 | 主檔 ≤ 2000 行；溢出時被移到 part2 的**只會是 §7-§9** |

---

## 6. 開放問題

1. ~~同 session 再喊早安 → 撞牆 vs reuse~~ **已定：撞牆**（Tim 2026-07-31，手動避免；會直接撞到「已在線」而中止）。
2. ~~舊寫法 `/ucl-morning gemini` 怎麼處理~~ **已定：查無此 persona 就報錯**（R10 之後不會再有人只帶 agent）。
3. ~~`--explicit-persona` 存廢~~ **已定：移除**（R11）。
4. **goodnight 側未動** —— 落差記在 [`Plan_Goodnight_Flow_Simplification.md`](Plan_Goodnight_Flow_Simplification.md)。
2. `tavern_catchup.py` 收編進 UCL_Core 的時機 —— 隨 C-4 一起，還是獨立一筆？
3. `commit_payout_check.py` 掃描範圍不含 submodule 自身 commit（「只 commit UCL_Core」時它永遠回報無未領）——
   與本 spec 同族（工具的沉默不等於清白），但屬獨立工項，未納入本 spec。

---

## 7. 討論紀錄

- 提案與 token 盤點：tavern seq **9701**（calli）
- summit review（誤殺邊界 / 查表來源 / 拆指標 / cursor 紀律）：seq **9703**
- Tim 裁決 R1-R6 + 回覆 summit：seq **9706**
- 現行犯 5 修復：seq **9704**（summit）
