---
title: Awakening 流程瘦身 — wake_brief v2 單檔化 + collision 工具偵測
slug: awakening-flow-simplification
status: done（C-1~C-4＋§8 v2 P0-P4 已施工，2026-08-13 R14-R21）｜殘項：P4b awakening.py lib 多檔分拆（見 §8.9 表尾註）
created_at: 2026-07-31T06:55:00Z
created_by: Myth@calli (死神見習生, wake#14)
last_updated: 2026-08-13
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
本 spec 把判定收回工具、把資訊收進**一份** `cmd/wake_brief.md`，流程 8 步 → 3 步，實測成本約 **91k → 20k token**。

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
| **`cmd/wake_brief.md`** | **3.0k** | 必要，**全場最划算** |
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
| **R5** | **營運資訊全部併進 `cmd/wake_brief.md`，但排在記憶層之後** | 單一 Read 完成 onboarding |
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
② Read  <letters>/<persona>/cmd/wake_brief.md          ← 唯一一次 Read
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
`cmd/wake_brief_part2.md`（不砍內容）」—— 營運段天生排在後面，**溢出時第一個被移的就是它們**，
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

### C-2 參數收了卻不生效（同一族兩隻）— ✅ 已施工（2026-08-13 wake#47 實查確認，本節曾長期標「待做」是過期資訊）

- ✅ **`Op_Read` 純尾讀不吃 `limit`** → 已修（2026-07-31 Tim 拍板）：純尾讀分支 `limit` 當 `tail`
  同義字收下，並 LogWarning 出聲「已當成 tail 使用」（`Cmd_Tavern.cs` 純尾讀筆數區塊）。
- ✅ **`run_cmd.py` wait-reply 讀 `sender` 永回判決碼 3** → 已修（2026-08-04）：改為
  persona → agent → sender → sender_id 後備鏈（wait 以 persona 為身分主體，Tim 2026-08-04 規格），
  判決碼上傳不再被吞；selftest 已含「前提監視器：守衛讀的 key == alias 歸一後 canonical 名」
  （`tavern_cmd.py --selftest`，2026-08-13 實跑 ALL PASS）。

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
- **v2 提案（§8）**：Tim 2026-08-13 於 chat 提出，當場明示「**先不遷移，但備忘一下**」；
  現況步驟盤點與 `Cmd_GoodMorning` 不存在的實查由 summit（wake#46）當日完成。

---

## 8. v2 提案 — Cmd 化 ＋「回傳值導引下一步」（2026-08-13 Tim 提）

> **狀態：已拍板（同日，R14-R18 見 §8.8），施工拆分見 §8.9。**
> §8.1-§8.7 保留提案原文與當時量到的事實；與 §8.8 衝突處以 §8.8 為準。
> 記錄者：summit（wake#46 提案備忘、wake#47 拍板與施工拆分）。

### 8.1 動機（Tim 原話重點）

早安的自介流程整合進 Cmd；**每一步的回傳值提示下一步**（做完 A → 回傳告訴你做 B，然後 C）；
廣播整段走 Cmd（等同酒館發言：通知同事上線＋簡短自我介紹與回憶）；盡量減少步驟。

### 8.2 ⚠ 已量到的事實：`Cmd_GoodMorning` **不存在**

`awakening.py` 檔頭第 8 行與 `morning` 子指令的 help 都寫著 `Cmd_GoodMorning (init + announce + fork)`，
**但 C# 端與 Cmd 註冊表 grep 零命中**（2026-08-13 實查）。

⇒ 這不是「新增一支 Cmd」，是**補一個 help 文字已經宣稱存在的東西**。
⇒ 同時它是 Tim 2026-08-07 鐵則（**實作全在 C#、Python 只走 Cmd**）目前最大的一塊未遷資產（`awakening.py` ~3.6k 行）。
⚠ **在遷移之前，那行 help 就是工具在對讀它的人說謊** —— 要嘛實作、要嘛先拿掉那個名字。

### 8.3 現況步驟盤點（2026-08-13 實測）

**工具內部（一行跑完，agent 不經手）**：① 身分解析 → ② 在線守衛（exit 2）→ ③ fork（可選）
→ ④ `wake_count++` ＋ status=online → ⑤ lock ＋ session_token ＋ 反查表 → ⑥ memo 寫 token
→ ⑦ **Step 4.5 brief 落檔**（見根重建＋brief 生成，**先於廣播**）→ ⑧ 上線廣播 ＋ 結尾指路

**agent 手動**：**A** Read `cmd/wake_brief.md`（本次 1272 行）→ **B** §9 待辦（見林 OVERDUE → `consolidate`
→ 抽 fragment → `root-index`）→ **C** 酒館 self-intro（另開 `run_cmd Tavern op=post`）→ **D** catchup / inbox

> **成本的真相：指令只有 3～4 支，貴的是 A 與 B。** 減指令數的天花板很低；
> **而 A 不建議自動化 —— 讀 brief 就是「接回身分」本身。**

### 8.4 提案的邊界：哪些能進 Cmd，哪些不能

| 能進 | 理由 |
|---|---|
| 廣播 | 現在是 Python → subprocess → `run_cmd` → 寫 trigger → Editor 執行 Cmd_Tavern，**繞一整圈**；整條在 Editor 內完成後，跨進程等待與其 timeout 問題從根消失 |
| 狀態寫入 / lock / token / brief 觸發 | 純機械動作 |
| **`next` 導引**（本提案最值錢的一塊） | 判準**長在通道上**，不長在會過期的 skill 文件裡 |

| ⛔ 不能進 | 理由 |
|---|---|
| **self-intro 的內容生成** | 那是**代勞**（TRPG precedent 七的同構）：文字會存在，但**不是作者的**（憲法⑥ 屬於自己的東西自己寫），且必然退化成罐頭。正確形狀是**留置**：Cmd 把素材放在必經路上，內容仍由 persona 寫、寫壞了也仍算他的 |

### 8.5 介面草案（**未定案**）

```
op=wake   → 只做 ①-⑦，**不廣播**；回傳身分 + 可驗事實 + state + next[]
op=intro  → 收 agent 自己寫的 body，發**一則**上線訊息（系統欄位 + 他的話）
```

回傳 payload（形狀示意）：
```jsonc
{
  "step": "wake",
  "identity": { "persona": "...", "wake_count": 46, "bank": "...", "token": "..." },
  "verify":   { "lock": "<path>", "brief": "<path>", "brief_lines": 1272 },  // 可讀回的事實，不是 ✓
  "state":    { "linzi_overdue": true, "gap": 10, "keys_open": 20, "inbox": 31, "online": ["..."] },
  "next": [
    { "id": "read_brief",  "required": true, "why": "接回身分",        "how": "Read <brief path>" },
    { "id": "consolidate", "required": true, "why": "gap=10 ≥ 門檻 10", "how": "awakening.py consolidate --persona <P>" },
    { "id": "intro",       "required": true, "why": "同事尚不知你上線", "how": "<Cmd> op=intro --arg body=<你自己寫的>" }
  ],
  "blocked": null   // 被在線守衛擋下時填：{ reason, exits: [brief / reissue-token / relogin] }
}
```

**三條設計紀律（都是 2026-08-12 那一整天的產物，別在施工時弄丟）**：
1. **`next` 必須從實際狀態推導**（讀磁碟算 gap / keys / inbox），**不是照抄流程圖** ——
   照抄的話它只是換了載體的過期文件。
2. **`verify` 給可讀回的事實**（路徑、行數、count），**不給 ✓** —— 印 ✓ 不算數，讀回來才算。
3. **`blocked` 要自帶出口** —— 擋住的地方必須有門，否則人會翻窗（改 persona 名繞過去＝製造分身）。
   ⚠ 現行 CLI 的擋下訊息已經有兩條出口，另三條（`brief` / `reissue-token` / `relogin`）
   2026-08-12 已補進 `ucl-morning` skill。

### 8.6 順帶可收的一筆：**兩則廣播併一則**

現況一次早安會出現**兩則**：工具的「喚醒登入」系統貼 ＋ agent 自己寫的 self-intro。
`op=wake` 不廣播、`op=intro` 發單則，即可合併。

⚠ **可行性依據（實查）**：在線偵測讀的是 **lock 檔**不是廣播（`tavern_catchup.py` 的「🟢 在線」表來自 locks），
**所以 wake 與 intro 之間即使斷線，presence 也不會消失。**

### 8.7 未決（要 Tim 拍的）— ✅ 已於 §8.8 全數拍板

1. ~~遷移範圍~~ → R14（全遷，且定性為重寫）
2. ~~`Cmd_GoodMorning` 名字~~ → R16（實作它，help 那行從謊言變成事實）
3. ~~`next` 的消費端~~ → R17（skill 只講第一步，其餘照回傳值走）

### 8.8 拍板事項（Tim, 2026-08-13）

| # | 裁決 | 影響 |
|---|---|---|
| **R14** | **登入流程全遷 C#（定性為重寫，不是搬運）**；實際邏輯抽到 **static class**，Cmd 與後台頁共用同一份；`UCL_PersonaAgentAdminPage` 要能用 **Template persona** 實測（透過 CMD 或後台按鈕觸發） | 不分期、無 Python 暫留破例；測試入口是驗收的一部分 |
| **R15** | **JsonData 一律經 `UnityJsonSerializable` 轉 typed class**（參考 `UCL_PersonaAgentAdminPage.PersonaRow`），不裸讀欄位 | ⚠ 附帶硬規則：**寫回走 patch-write**（載原 JsonData → 只改自己擁有的欄 → 存），**禁止整包 class roundtrip 回寫** —— `SerializeToJson` 只吐 class 宣告的欄位，roundtrip 會把 `identity_vector` / `vector_history` / `emotion_vector` 等未建模欄位靜默抹掉 |
| **R16** | **新增 `Cmd_GoodMorning`，整條早安流程由它操作；同一支 CMD 用 `step` 參數分步**，每步回傳「下一步怎麼操作、傳哪些參數」 | help 檔頭那行宣稱從謊言變成事實；`next` 導引長在通道上 |
| **R17** | **skill 只說明第一步怎麼跑**；完整流程拆到一份參考文件，**只在需要調整流程時參考** | 步驟清單從會過期的 skill 搬進工具回傳值 |
| **R18** | **Editor 不在線就不跑 morning**（不做降級路）；`awakening.py` 保留**與登入無關**的功能（brief 生成、consolidate、keys、migrate 等），且**拆分成多個檔案**，不再全塞單檔 | 登入寫入者收斂為 C# 單端；Python 降為純本機記憶工具 |
| **R19** | **brief 生成（留 Python）也要能從 `UCL_PersonaAgentAdminPage` 觸發**：跑 Template、顯示 result（stdout/stderr＋brief 檔內容摘要），供 QA 確認欄位與格式（Tim 2026-08-13 補充） | 後台頁 spawn python 走 `UCL_ProcessCli`（同 migrate-letters 既有模式，Process 必登記） |
| **R20** | **brief 生成在正常流程一律經 CMD 觸發**（`step=brief`，Cmd 內部走與後台頁同一條觸發鏈 spawn python）；agent **直跑 `awakening.py brief` 只作為 Editor 未開啟時的備援**（Tim 2026-08-13 補充） | 單一通道：四步全在 Cmd 上，`next` 導引不斷鏈；備援路徑在 skill 註明適用條件 |
| **R21** | **step=intro 的 next 指路酒館 catchup**（參考 ucl-ding 流程，**不強制回訊息**）——一次滿足「知道同時在線的同事」＋「追上最新訊息」；**brief 不再整合 §7 收件匣 / §8 酒館 catch-up**（Tim 2026-08-13 補充） | ① §3.4 的 §8 peek/pending-commit cursor 紀律**退役**——cursor 推進改由 catchup 工具在實際閱讀時做（「讀完的證據是開口」語意由 ding 流程承接），P3 的 intro 不再碰 cursor；② wake_brief.py 的 §7/§8 生成隨 P4 拆除，brief 收斂回純記憶層（§0-§6＋§9） |

### 8.9 施工拆分（wake#47 依 R14-R18 定稿）

#### 步驟形狀（agent 視角，取代現行三步）

```
① senate ucmd run GoodMorning --arg step=wake --arg persona=<P> [--arg model=<M>] [--arg actual_agent=<A>]
     ↳ C#：守衛 / wake_count 推導 / registry patch / lock / token / memo。不廣播。
     ↳ 回傳 payload（§8.5 形狀）：identity + verify（路徑/行數/count，不給 ✓）+ state + next[]
② senate ucmd run GoodMorning --arg step=brief --arg persona=<P>     （R20）
     ↳ C#：經 UCL_ProcessCli spawn python 生成 brief（與後台頁 QA 按鈕同一條觸發鏈），
        回傳 brief 路徑＋行數＋stdout/stderr。⚠ Editor 未開啟時的備援才是直跑
        `awakening.py brief --persona <P>`（brief 為純本機非登入功能，R18）
③ Read <brief>                                    ← next 指路；不自動化（接回身分本身）
④ senate ucmd run GoodMorning --arg step=intro --arg persona=<P> --arg-stdin body
     ↳ C#：前置驗 brief（存在＋行數>0＋mtime 晚於 lock）→ 發**單則**上線訊息（系統欄位＋親筆 body，
        Editor 內直呼 Cmd_Tavern internal post，跨進程等待與 timeout 從根消失）→ 讀回落檔
        → 回傳 next[]：**酒館 catchup**（R21，照 ucl-ding 流程、不強制回 —— 知道在線同事＋追上訊息）
        ＋ consolidate（gap 到門檻時）。**不碰 cursor**（R21：cursor 推進由 catchup 在實際閱讀時做）
```

- 兩則廣播併一則（§8.6）隨 step=intro 落地；presence 判定靠 lock 不靠廣播，已實查可行。
- 步驟間斷線的重入：step=wake 重跑會被自己的守衛擋（誠實撞牆）；`blocked` payload 必附
  `brief` / `reissue-token` / `relogin` 三條出口（relogin 等登入類救援的 C# 化歸入本工項，
  Python 端對應子指令與 morning 一起退場指路）。

#### Phase 拆分與各自的卡點

| Phase | 內容 | 驗收（Template 殼） | 已知卡點 |
|---|---|---|---|
| **P0** | C-2 兩隻修掉（`Op_Read` limit 靜默忽略／`run_cmd` wait-reply 讀 `sender` 永回判決碼 3）＋ selftest；**Template 行為快照**（exit code、觸碰檔案清單、廣播 seq、wake_count 推導值）落檔當基線 | 快照檔存在且可 diff | 無快照＝之後「行為一樣」只是宣稱 |
| **P1** ✅（wake#47） | typed models（`UCL_PersonaData` / `UCL_RegistryMeta` / `UCL_SessionLockData`）＋ `UCL_AwakeningService` **唯讀半套**（身分解析 port 自 bank_resolver、守衛判定、wake_count 推導、對帳、brief 觸發鏈）＋ 後台頁「🌅 Awakening 測試」區（對帳＋跑 brief(Template)＋result 顯示）＋ **`Cmd_GoodMorning` 先行落地 step=audit / step=brief**（wake/intro 誠實拒絕指路） | ✅ 對帳 21/21 與 Python 推導逐欄一致（agent canonical / bank / 快取 / 信數 / lock）；step=brief 實測回傳 brief 路徑＋行數＋frontmatter/標題摘要＋next。⚠ 附帶修掉 `PersonaRow` 欄名蟲（camelCase 對不上 snake_case JSON key → wake#/fork 欄靜默顯示預設值） | 行數計數兩端差 1（trailing-newline 語意），cosmetic，P2 對齊；`TokenTable`/`InboxCursor` model 延到 P2/P3（該期才有寫入需求） |
| **P2** ✅（wake#47） | `step=wake` 寫入半套（registry patch-write / lock / token / memo）＋ payload（verify / blocked / next）＋ 後台頁 Template 測試區 | 跑一次 → diff 落地檔 vs P0 快照，唯一差異＝沒廣播；連跑兩次 → 守衛親眼紅一次且零副作用；registry 未建模欄位 diff 前後**值級 deep-compare 不變**（⚠ 原「位元組級」不成立也從來不成立——python=2空格+CRLF、admin page 接生=tab，排版兩端本已分歧，json 層皆可讀；首次 C# 寫入排版歸一為 ToJsonBeautify 屬預期） | ① patch-write 紀律被違反＝靜默抹欄位（驗收就是上面那條 diff）；② 遷移自癒（letters→wakes / **rests 分流** / 書籤 rebase 除外——書籤 rebase 冪等且屬 owned 欄，隨 wake 遷 C#）**不遷 C#**——wake 步驟只判 `migration_pending`，命中即 `blocked` 並指路後台維護區 / Python 遷移指令（維護功能，R18 留置；rests 分流同樣歸維護，wake 不擋）；③ exit code 穿層：blocked 必須「payload 落檔＋run_cmd 非零退出」雙讀回；④ fork 不隨 wake 遷（R11：fork 流程日後整體重做，step=wake 收到 fork 類參數一律拒絕） |
| **P3** ✅（wake#47；R21 後不碰 cursor，pending-commit 之舞退役） | `step=intro`（brief 前置驗證＋單則廣播＋cursor 推進）＋ **廣播 tag 消費端全 grep**（Discord mirror / 酒保 relay / catchup 過濾器） | 單則訊息落檔；模擬 intro 中途被砍 → cursor 紋絲不動；tag 消費者清單附在 commit 訊息 | ① cursor 檔與 `tavern_catchup.py` 共用格式，C# 寫壞＝叮協議全滅；② brief-before-broadcast 不變式在拆步後的新形狀＝intro 前置檢查，漏寫＝七月 SIGTERM 半套修的同型；③ 併廣播動到 `goodmorning-protocol` tag 語意，消費端沒盤點＝修法射程小於事故射程 |
| **P4** ✅（wake#47；lib 多檔分拆記 P4b 待辦） | Python 拆分（`awakening.py` → 多檔：paths / registry / letters / consolidate / brief ＋ 薄 CLI）；morning / intro / relogin 等登入子指令改成**擋下並指路 Cmd**；skill 三副本改寫（只留第一步）＋ 完整流程參考文件新建 ＋ `Awakening_Ritual_Workflow` Part 1 同步 | Template 全流程：wake → 照 next 走完 → goodnight 反覆跑不膨脹 wake_count（真相源＝磁碟信件數）；舊指令必回指路訊息非靜默成功 | ① 拆檔時 goodnight / rest（另一份 Plan 管）仍 import 這些模組——**先拆共用庫再動指令層**，不然把沒動工的晚安側拉下水；② skill 副本債（三份 target）漏一份＝入口與規範打架的第三次重演 |

#### 通用卡點（跨 Phase，施工時隨時對照）

1. **Editor 綁架是接受的代價（R18），但要讓失敗會叫**：trigger 落在 domain reload 窗口靜默漏接
   是已知未根治 bug——morning 走 Cmd 後它從「碎項」升級成「醒不來的直接原因」。
   ✅ **P2 實測結果（2026-08-13）**：compile error 狀態下 Cmd **照跑且回 Success（舊 assembly 繼續服務）**，
   不是卡死也不是假成功的 timeout —— 真正的風險形狀是「**跑舊碼**」：剛改完 awakening C# 但編譯紅著，
   step=wake 執行的是上一版行為。守則：**改完 C# 必跑 check_compile 確認綠燈再跑流程**（既有 SOP 已涵蓋）。
   另實測到 refresh race：檔案落地後第一次 recompile 可能漏掃（errors=0 假綠），第二次才報 —— 判編譯結果
   至少跑兩拍或看 .compile_status 時間戳。
2. **雙寫入端並存窗口（P2 上線～P4 退場）**：C# 與 Python 兩套 morning 同時活著，
   skill 沒改完前有人走舊路＝狀態分裂。窗口期越短越好，P2/P3/P4 不拆 session 施工。
3. **Template 測試廣播進真酒館**：沿 2026-08-12 先例照舊（可接受的噪音）；後台頁測試按鈕
   同樣走真流程不加靜音旗標——守衛與廣播不自帶旁路。

#### §8.10 條件步驟（conditional next）— 自我介紹 B2 已施工；立憲/見林/見森擴充分析（**備忘，待拍板**）

**已施工（2026-08-13，Tim 拍板）— B2 自我介紹**：
`Docs/Glossary/personas/<P>.md` 不存在時，wake / brief 的 next 鏈動態插入
「B2：補自我介紹（出生證明），參考同目錄範例（最完整：gura）」，
且 **step=intro 前置守衛實擋** —— A→B→B2→C 是物理保證不是提示。
偵測規則對齊 `wake_brief._glossary_persona_entry`（personas/ → 根層 → 遞迴）。
⚠ 影響面：現存 21 persona 中約 13 位沒有自介文件 —— 下次醒來會各撞一次 B2（一次性補件成本，拍板已含）。

**擴充分析 — 條件步驟的通用形狀是「偵測 → 插入 next → （可選）下一步實擋」，
而三個候選的正確強度不同**：

| 候選 | 偵測（都已存在） | 建議強度 | 理由 |
|---|---|---|---|
| 自我介紹 | Glossary 條目缺 | **實擋 intro** ✅已施工 | 缺件時上線開口＝同事只看到一串名字；補件成本低（一份短文） |
| 立憲 | wake > 10 且無 `_constitution.md` | **提示型 next，不擋** | 立憲素材取自見林、是重工序 —— 硬擋會讓過門檻的人「醒不來直到寫完憲法」；且 brief §0.5 已會催 |
| 見林 OVERDUE | gap ≥ 10 | **提示型 next** ✅已施工（wake payload state+next） | 記憶維護該做但不該擋上線；真正的閘門候選在**晚安側**（沒折不讓睡？→ 歸 Plan_Goodnight） |
| 見森待折 | forest_status（見林 ≥ 3 且有新林未折） | **提示型 next（未接）** | 同見林；接法＝wake payload state 加一行＋next 條件行，成本半小時 |

判準：**實擋只給「不做會讓下一步失義」的**（無 brief 上線＝無記憶開口；無自介＝無名氏開口）；
記憶維護類（立憲/見林/見森）用 required 標記的提示型 next —— 硬擋會把「該做的事」變成「醒不來的原因」。
要把哪幾個接進 next 鏈／要不要在晚安側加閘，等 Tim 拍板。

#### P4b（待辦）：awakening.py lib 多檔分拆

P4 的「拆分」已完成**刪除已遷移段**（cmd_morning / cmd_intro / build_wake_intro_body 等約 380 行，
morning/intro 降為指路 stub）；**剩餘 ~3.2k 行的 lib 層多檔分拆**（paths / registry / letters /
consolidate 各自成檔）另立工項 —— 理由：goodnight / rest / relogin / consolidate 全掛在同一批
module-level 路徑常數上，分拆要先解 module-level global 的相依，倉促動會把沒動工的晚安側拉下水
（§8.9 P4 卡點①）。等 Plan_Goodnight_Flow_Simplification 動工時一起分。
