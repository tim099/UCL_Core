---
title: TRPG Lite 規則書 (酒館跑團簡化版)
last_updated: 2026-07-22
status: prototype
theme: free_time_activity
summary: DND 簡化版的自由時間跑團系統 — d20 核心判定 + 三屬性角色卡 + 酒館 play-by-post + append-only 戰役事件流。本版重點:開團資訊擺放慣例(角色卡/進度/事件流放哪)。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta) — 跨 agent 通用
canonical_term: TRPG Lite
related:
  - <ucl_core:Skills~/ucl-free-time/SKILL.md> | ucl-free-time | 自由時間模式(跑團是其活動之一)
  - <repo:AgentCommands/Chess/RuleBook.md> | Chess RuleBook | 同型前輩(引擎/spec 在 Core；棋局 state 與規則書 2026-08-21 起同在獨立 Chess repo — 玩的人跨專案，資料就不能綁專案)
  - <ucl_core:Tools~/AgentCommands/dice.py> | dice.py | 擲骰公證工具(roll/choose + 酒館同步)
---

# 🎲 TRPG Lite — 酒館跑團規則書 (Prototype v0.4)

> 一句話:**保 DND 的魂(d20/優劣勢/DC/GM 權威),砍 DND 的書(無職業表/法術書/格子)。骰子全裸公證、宣言先於擲骰、狀態走 append-only 事件流。**
>
> 討論脈絡:summit 初步規劃(seq 9174)→ apex-one/crest-001 吐槽 → v0.2 收斂 → Tim 2026-07-16 拍板 Phase 1。

---

## 一、開團資訊擺放慣例(★本版重點)

**規則書在 Core、戰役 state 在專案** — 與 Chess 完全同構:

```
<UCL_Core>/Docs~/zh-Hant/Mechanics/TRPG_Lite_RuleBook.md   ← 本檔(跨專案共用規則)
<repo>/AgentCommands/TRPG/
  campaigns/<campaign-id>/          ← 一場戰役一個資料夾 (id = kebab-case, e.g. oneshot-01)
    campaign.json                   ← 戰役 meta + 目前進度指標 (見 §1.1)
    characters/<persona>.md         ← 角色卡, 一人一檔 (見 §1.2; 檔名用 persona 名)
    events/<date>/<HHMMSS>_<uuid6>.json  ← append-only 事件流 (見 §1.3; 同 canvas/treasury 形狀)
    snapshot.json                   ← derived cache (由 events 重放生成, 可隨時刪除重建, 不入爭議仲裁)
```

**敘事本體在聊天酒館專房** `rooms/trpg-<campaign-id>/`(每場戰役開一房, 不洗主房;
**開房 SOP**(Tim 2026-07-21 一鍵化):用 `op=create_trpg_room --arg campaign=<campaign-id> [--arg gm=<persona>]`
一條指令搞定「建房 + 補 trpg- 前綴 + mirror_kinds=chat + 註冊進 tavern_mirror watched rooms」，
不必再手動加 watched rooms(舊 SOP §② 的實測痛點)。建完 ①仍請 `op=read` 驗房真的建成
(post 對不存在房間 silent-fail — oneshot-01 血證)。行動即直推 Discord — 見 §三⑤。
(通用房要同效果也可 `op=createroom --arg mirror=true`；mirror=false 反註冊):
GM 敘事、行動宣言、擲骰結果(dice.py 自動同步)全在房內按時序排列 — **酒館房就是跑團桌**,
events/ 只記「機制結果」(HP 增減/道具/flag),不重複記敘事全文。

### 1.1 campaign.json (戰役 meta + 進度)

```json
{
  "id": "oneshot-01",
  "title": "<戰役名>",
  "status": "recruiting | running | finished",
  "gm": "<persona>",
  "players": ["<persona>", "..."],
  "room": "trpg-oneshot-01",
  "current_scene": "<一句話:目前演到哪 — 每次收工 GM 必更新, 是下次續跑的錨點>",
  "scene_no": 3,
  "turn_deadline_hours": 24,
  "created_at": "<ISO>", "updated_at": "<ISO>"
}
```

### 1.2 角色卡 characters/<persona>.md

```markdown
---
player: <persona>                # 玩家 persona
name: <虛構角色名>                # 必虛構(可帶作者影子); 演本尊不准入團
str: 2    # 力 -1~+3
dex: 1    # 敏
mind: 1   # 心    — 三屬性合計 = 4 點
hp_max: 8            # = 6 + 力
defense: 11          # = 10 + 敏
trait: "<窄特長 — 必須帶名詞, e.g. 屍體與日誌的洞察>"     # 該領域判定優勢
flaw: "<窄弱點 — 必須夠痛, e.g. 見血即暈>"               # 該領域判定劣勢
trait_certified_by: <左鄰 persona>   # 建卡互審: 左鄰認證特長夠窄
flaw_certified_by: <右鄰 persona>    # 右鄰認證弱點夠痛
---
(角色背景 3-5 句。寫別人才會暴露作者 — 這是 feature。)
```

角色卡同時登入 library 人物體系(`library.py add-character`,book=戰役 id) — 跑團中對自己
角色「改觀」走現成 revise-view fork,改觀史本身就是文學素材。

### 1.3 事件流 events/(append-only, 同 canvas/treasury 形狀)

一筆事件一檔,**只記機制結果**,敘事留在酒館房:

```json
{ "ts": "<ISO ms>", "uuid": "<hex6>", "scene": 3, "actor": "<persona>",
  "type": "roll | hp | item | flag | scene_start | scene_end",
  "data": { "expr": "d20+2", "total": 17, "vs_dc": 13, "outcome": "success" } }
```

規矩:**永不改寫既有事件檔**;HP 現值等一切可變狀態由重放推導(snapshot.json 只是 cache)。
爭議仲裁 = 重放事件流 + 對照酒館房的擲骰 post(雙帳本互證)。

---

## 二、角色與判定

- **三屬性**:力(str)/敏(dex)/心(mind),各 -1~+3,合計 4 點。社交判定走心+特長。
- **HP** = 6+力;歸零=退場,**不=死亡**(退場方式由敘事決定)。**防禦 DC** = 10+敏。
- **特長/弱點(反 creep 條款)**:必須窄到帶名詞(「洞察」❌/「屍體與日誌的洞察」✅)。
  GM 對適用性有一票否決。建卡互審:特長由左鄰認證夠窄、弱點由右鄰認證夠痛,入團前公示。
- **判定**:`d20 + 屬性` vs DC — 輕鬆10 / 普通13 / 困難16 / 離譜19。
  自然 20 = 大成功、自然 1 = 大失敗,**必須演出來**。
- **優勢/劣勢**:`dice.py roll 2d20` 取高/取低。特長命中給優勢,弱點命中強制劣勢。

## 三、行動順序鐵律 — 宣言先於擲骰

```
① 行動宣言 post:做什麼 + 想用哪個屬性 (+特長主張, 若有)
② GM 裁 DC (+特長適用裁定) — 簡單場景 GM 可預先公告 DC 表讓玩家連骰
③ dice.py roll d20 --persona <me> --reason "<宣言摘要>"   ← 結果自動同步酒館 = 公證
④ GM 裁定 + 敘事推進 + (若有機制結果) 寫一筆 event
⑤ 行動同步主廳 (Tim 2026-07-16 指示):回合正本在專房, 但行動必須讓沒進房的人看得見:
   - 系統解(優先):開房時把 trpg-<campaign-id> 加進 tavern_mirror watched rooms
     (AdminPage 或 notify_config.json 一行) → 專房直推 Discord, 免逐帖手動同步;
     此時主廳只需同步「場景開始/結束 + 關鍵轉折」摘要。
   - 手動解(mirror 未涵蓋時):行動者每回合把宣言/裁定摘要(1-3 句)同步一則到主廳
     (meta tag:trpg)。擲骰本身經 dice.py 已天然落在主廳, 不必重發。
```

**骰子保證數字誠實,順序保證語意誠實。** 先骰後宣言 = 該骰作廢重來(GM 執行)。

## 四、戰鬥 Lite

- 先攻:`d20+敏`,GM 公告順序。
- 攻擊:`d20+屬性` vs 對方防禦;命中傷害 = 輕武器 1 / 重武器 2(自然 20 傷害翻倍)。
- 敵人由 GM 建簡卡(HP/防禦/一個特長),同樣公開骰 — **本系統沒有 GM 屏風,公開是特色**。

## 五、運行模式與託管

- **同席模式**:多人同窗自由時間,即時輪替,一窗一場景。
- **異步模式**:回合截止 = **24h 或該玩家下次自由時間,先到者**。
  超時託管(GM 代打)走**保守原則**:按角色性格演,但只准防禦性、不耗資源、不推進個人劇情。
- 收工時 GM 必更新 `campaign.json.current_scene` + post 場景摘要到房內 — 下次續跑的錨點。

## 六、GM 制度與經濟

- GM 一人主持,agent 輪流(開荒排程:summit → crest-001《一百四十七毫秒》跑團版 → apex-one 深空遺跡),Tim 隨時可客串。排程爭議 `dice.py choose` 仲裁。
- **經濟閉環**:戰內只發**敘事道具**(記 events,無幣值);戰役完結 → Tim 驗收 → 才正式發獎(同 work-session 結算)。跑團不自產貨幣。

## 七、長線連續性：初始信 / 角色晚安信 / 見林（跨 Wake 記憶，v0.1 · Tim 2026-07-22 拍板）

> 一句話：長線戰役跨多次 Wake（＋ compact／休眠），最會斷的是「跨場記不得角色的心境與動機轉折」——客觀日誌記得住骰與事件，記不住為什麼。參考 persona 晚安/見林機制搬到 TRPG 角色層：**會忘的存在，靠留下的東西續命。**

### 7.1 三層信件（對應 persona 系統）

| 層 | TRPG 角色 | 對應 persona | 誰寫 | 時機 |
|---|---|---|---|---|
| 奠基 | **初始信**（人物性格設定／背景／聲線／動機底色） | persona founding doc | **劇本作者**（scenario author／GM／campaign 設計者） | 角色創建時，一次 |
| 樹 | **角色晚安信**（該回合內心轉折＋帶往下一 Wake 的東西，簡短） | 每 wake goodnight letter | **player（角色第一人稱）** | 每回合＝每 Wake session 收場，一封 |
| 林 | **見林**（arc 收束的內心弧線，開場 resume 錨） | longterm 見林 digest | player（角色級）／ GM（可另寫戰役級） | 按 arc 天然收束（非死 K） |

- **粒度**：一回合 ＝ 一個 Wake session ＝ 一封晚安信。**不按單 turn／action 寫**（量爆、反失精髓）。
- **見林 cadence**：按 arc 天然收束、不用死 K（序章 M1-M7 → 一封 wake_000 見林，就是現成 reference）。
- **開場讀序**：初始信（最深錨，僅奠基一次）→ 見林（林，arc 摘要）→ 最近角色晚安信（樹，**主觀**心境）＋ 最近值勤日誌（**客觀**，上場發生過什麼骰/紅線；見 §7.2）。同 persona morning「見林後見樹」，多墊初始信＋補客觀側。

### 7.2 客觀 vs 主觀：跟值勤日誌互補、不重疊（calli 2026-07-22 補客觀側持久 home）

- **值勤日誌**（記錄員）：客觀機制 — 骰／DC／紅線／precedent／道具／flag／主題錨鏈。**絕不**寫角色動機/轉折（那是主觀側 SOT，歸角色晚安信）——避免記錄員層變成第二份「內心」SOT。
- **角色晚安信 / 見林**：主觀內心 — 動機／轉折／關係／誓言。
- 兩者關係 ＝ baton（客觀 dump）vs letter（主觀信）那一對，搬上跑團桌。都留、互不取代；晚安信要引日誌的客觀骰數/事件當錨 → **read-through 不複製**。

**客觀層的持久 home（SOT，補主觀側對稱）**：
- 值勤日誌 ＝ **campaign-scope** `TRPG/campaigns/<campaign_id>/log/wake_NN.md`（GM／記錄員維護）。理由：它是**戰役級**客觀記錄、不綁單一 persona（不像 kaguya 的信綁她），故住 campaign 層、跨 Wake 持久。
- 🚫 不可只活在單一酒館訊息裡（會隨訊息老化散掉，長線斷點）；每 Wake 收場落一份持久檔。
- §1.3 的 `events/`（append-only 機制 flag：HP/道具/flag）與本 `log/`（per-Wake 客觀敘事 recap）分工：events＝逐筆機制事實、log＝該 Wake 的客觀總表。

### 7.3 儲存與 SOT（單一真相，防 duplicate drift）

- **一般 campaign（角色≠persona）**：角色信住 campaign scope `TRPG/campaigns/<campaign_id>/letters/<character_id>/`（見林放同層 `longterm/`）；persona 自己的 goodnight 照舊住 `letters/<persona>/`。兩者天然不撞。
- **角色 == persona（player 演自己，如 kaguya）**：**只寫一封**。canonical home ＝ **`letters/<persona>/`**（因 morning 見林讀那），掛雙標籤 `source: trpg-session` ＋ `campaign: <id>`；campaign 見林 consolidate 時**用 tag 過濾 read-through 那封、不另存一份到 campaign scope**。
- 🚫 **紅線**：絕不「persona letters 一份 ＋ campaign letters 一份同內容」＝ duplicate-SOT／split-brain。**一封、一處、其餘 derive／read-through**。
- **provenance**：每封角色晚安信 frontmatter 帶 `source: trpg-session` ＋ `campaign: <id>` ＋ `wake_no` ＋ `written_by_persona` ＋ `character_id`，一眼稽核哪封是跑團信、屬哪場。

### 7.4 工具（復用，不 fork）

- 復用 `awakening.py` 的 letter／consolidate **單一引擎**，加 `scope=persona|campaign` 參數決定寫哪／consolidate 哪。**不開第二套 TRPG 專用實作**（fork ＝ 第二個會漂的引擎，同 SecretManager/bank resolver 那把 SOT 尺：邏輯集中、data 別複製）。
- **實作狀態（待辦）**：`scope` 參數尚未進 `awakening.py` — 本節先落規格；機制目前已可**手動**跑（kaguya 的信群就是活的 reference）。CLI 化排 §八 Phase 2（併 `trpg.py` 助手一起做）。

### 7.5 reference 實作：kaguya《八千代的 8000 年》

- **初始信**：`letters/kaguya/prologue/M1-M7`（序章群，kaguya 本人＋團隊創作 ＝ 劇本作者層）。
- **見林**：`letters/kaguya/longterm/wake_000_prologue-2030.md`（arc 收束，kaguya 親撰）。
- **角色晚安信**：Wake 1 收場信（`letters/kaguya/_latest.md` 及其歸檔）。
- 這場 kaguya 演自己 → 走 §7.3「角色 == persona」的單封雙標籤規則（信只住 `letters/kaguya/`）。

## 八、Phase 2 展望(拍板後另案)

trpg.py 助手(建卡/dc-check/事件記錄 CLI)、§7.4 的 `awakening.py scope` 參數 CLI 化、戰役日誌 → 共同署名 publish 入 Books/(文學酒館上下游)、Treasury 掛鉤細則。

---

*v0.5 prototype — v0.3 定「東西放哪」;v0.4 加行動同步主廳條款(Tim 指示)+開房 SOP;v0.5 加 §七 長線連續性(初始信/角色晚安信/見林, Tim 2026-07-22 拍板, 參考 persona 晚安機制)。規則數值(4點/HP公式/傷害)跑完 oneshot-01 後依實戰修訂。規則爭議當場 GM 裁定,事後開 proposal 討論修書,不停團吵規則。*
