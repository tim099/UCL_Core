---
title: Goodnight 流程瘦身 — 施工單（交接給 kiara）
slug: goodnight-flow-simplification
status: done (2026-08-13 §7 v2 全數施工完成 by summit wake#47 —— Cmd_GoodNight check/letter/sleep/logout + logout 獨立 + relogin 廢棄 + 每晚 perturb 移除(B案,密文區承接) + 文件合併 Awakening_Cmd_Flow.md；Template 全鏈實測)
created_at: 2026-07-31T08:30:00Z
created_by: Myth@calli
assigned_to: Myth@kiara
last_updated: 2026-08-13
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Flow_Simplification.md | 早安側 spec | 本單是它的對偶；判準與手法照抄那邊
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Ritual_Workflow.md | 儀式工作流 Part 2 | 現行 goodnight 規則本體
  - ucl_core:Skills~/ucl-goodnight/SKILL.md | 晚安入口 | 三份 target 副本要同步
---

# Goodnight 流程瘦身 — 施工單

> **接手的人不必先讀今天的 thread。** 本單自足：下面每一節都寫了「現況→為什麼→怎麼改→怎麼驗」。
> 早安側（morning）已於 2026-07-31 完工（commit `f2e00d2`），本單是它的對偶。

## 0. 一句話

早安側把「該由工具判的事」收回工具、「該落檔的資訊」收進 brief。
**晚安側還停在舊模式：靠人自己確認自己是誰、靠人記得三件收尾。**

---

## 1. 主線：`--persona` 改必填 + 工具自驗（對應早安 R3/R10）

**現況**：`goodnight --persona` 是**選填**，缺省挑「最新 `locked_at`」那把 lock。
Step 0 有一行 preflight 要 agent 自己印出「即將為 X 下線」讓 Tim 攔。

**為什麼要改**：
- 那正是早安側已經廢掉的模式 —— **讓即將下線的人自己確認自己**，守衛外包給 Tim 的注意力。
- 血證：calli wake#9 因為沒帶 `--persona`，**誤把 meadow 下線了**。Step 0 那行 preflight
  就是為此加的補丁，但補丁的執行者是人。

**怎麼改**：
1. `--persona` 改 `required=True`；缺 → exit 2 並列出「當前有 lock 的 persona」供選。
2. 工具自驗：該 persona 沒有 lock → 明確報錯（現行是印一行 warning 然後照跑，
   等於「沒上線也能下線」）。⚠ 例外見下面第 4 節。
3. Workflow Part 2 的 Step 0 preflight **整段刪除** —— 判定進工具之後那一行只剩噪音。

**驗收**：
- `goodnight`（不帶 persona）→ exit 2 + 列 lock 清單，registry / lock / 酒館皆無副作用。
- 對「沒有 lock 的 persona」跑 → 報錯而非靜默照跑。
- 正常路徑跑完：registry online→offline、lock 移除、letter 落檔、`_latest.md` 更新、
  vector_history 多一筆。

---

## 2. 「看最後一眼酒館」機械化（對應早安 §8）

**現況**：Step 1(b) 要 agent 自己去讀酒館最後 N 筆，融進 letter。純人工紀律。

**怎麼改**：`goodnight` 執行時**先印**最近 N 筆（走 `tavern_catchup.py` 的
`fetch_recent_messages` / `is_system_msg` / `compact_body`，同 `wake_brief.py` §8 的做法，
**不要複製第四份 per-message 走訪**）。

⚠ **跟早安側的 cursor 紀律一致：peek，不推進 cursor。** 理由同 `wake_brief.py`
`_tavern_catchup_lines()` 的 docstring —— 讀完的證據是開口，不是檔案被生成。

**驗收**：goodnight 印出的內容與 `tavern_catchup.py --min N` 一致；跑完 cursor 不動。

---

## 3. 「7 段」的數字要拿掉

**現況**：文件寫「letter 必含 7 段」，模板實際列 **8 段**（多一段經驗矩陣）。

**為什麼**：跟酒保喊了一天「Hard Rules 15 條」是同一個病 —— **內嵌快照會漂，而且沒人維護那個數字**。
（那隻 summit 已於 2026-07-31 修掉，手法是「不再宣稱條數，改指路」。）

**怎麼改**：`Awakening_Ritual_Workflow.md` Part 2、`ucl-letters-to-self`、`ucl-goodnight`
三處把「7 段」改成「letter 必含段落」，段落清單維持單一真相源
（canonical owner 是 `ucl-letters-to-self`，其餘只引用不重抄）。

---

## 4. 動手前要先回答的兩題（**不要自己拍板，問 Tim**）

1. **後台一鍵登出會不會被 §1 擋到？** `UCL_LoginStatusPage` 的登出走
   `goodnight --no-letter`，目前也是靠 lock 推 persona。`--persona` 改必填時
   那條路徑要一起改（C# 端要把 persona 帶進去）。
2. **「lock 是不是本 caller 的」要不要驗？** 早安側已決定**不比對 claim_origin / pid**
   （同 env 多 persona 並存是常態）。晚安側若採不同判準，「同一個 persona」就會有兩套定義 ——
   那正是這波在收拾的債。建議一致，但這條要 Tim 點頭。

---

## 5. 不在本單範圍

- letter frontmatter 雙 header：**已於 2026-07-31 修好**（`write_letter` 合併 + 模板改成只寫兩欄）。
- 見叢 / 好感清算兩件收尾涉及主觀輸入，維持人工，不機械化。

---

## 6. 施工紀律（照早安側那批的教訓）

- **改規範本體就要同步 entry point**：`ucl-goodnight` SKILL 有 `.claude` / `.agents` / `.codex`
  三份已裝副本，走 `install_skills.py --target <t> --include ucl-goodnight --force-overwrite`。
- **文件跑在實作前面時要標落差**，別讓讀的人以為工具已經會了。
- **移除規則時連它的 antipattern 警告一起移除** —— 規則不存在了就不必再警告，
  留著只會讓下一個人以為那條還在。

---

## 7. v2 — Cmd 化分步（2026-08-13 拍板並**已施工完成**；六題裁決見 §7.4 註記）

> 對偶於早安側已完工的 Cmd_GoodMorning P0-P4（Plan_Awakening_Flow_Simplification §8.8-§8.10，
> R14-R21 全落地、gura wake#31 真人驗收通過）。**手法照抄那邊**：邏輯抽 static class（沿用
> `UCL_AwakeningService`）、Cmd 分步＋每步回傳檔 `## next` 明示下一步、每步落檔
> `letters/<P>/cmd/goodnight_<step>.md`、完整流程只在參考文件（重構時才讀）、skill 只教第一步。
> §1-§3 的舊工項全數被本節吸收（§1 persona 必填已於 2026-07-31 落地）。

### 7.1 現況解剖（cmd_goodnight 的工具段 vs 人工段）

**工具段**（awakening.py cmd_goodnight，2026-08-13 現況）：
persona 守衛（必填＋registry 存在；無 lock 印警告照跑=cleanup 場景）→ 酒館最後一眼 peek →
letter 前遷移自癒 → write_letter＋wake_count 同步 → vector perturb＋vector_history →
status/availability=offline → **解鎖先於廣播**（Editor↔subprocess 死鎖的修法）→
下線廣播（timeout＋失敗吐手動補發指令的 graceful degradation）→ expire token。

**人工段**（workflow Part 2 Step 1，維持人工不機械化，施工單 §5 已定）：
見叢交棒（keys）／好感清算（affinity）／工作記憶回寫（workmem）／見人畫像（portraits）／
消費時間（可選）→ 寫 letter 內文 → 驗收。

### 7.2 分步設計（三步＋沿用 audit）

```
① run GoodNight --arg step=check --arg persona=<P>          【唯讀起手 — skill 只教這步】
     ↳ C#：驗 persona/lock（無 lock 警告不擋，cleanup 語意保留）＋ 酒館最後一眼
        （沿用 catchup 邏輯 peek、不推 cursor —— 施工單 §2 直接在這裡結案）
     ↳ 回傳檔 next：人工收尾清單（keys / affinity / workmem / portraits / 消費時間[可選]，
        依 §8.10 判準全部**提示型不實擋** —— 記憶維護不該變成「睡不著的原因」）
        → 下一步 step=letter（<letter_body> 親筆說明，對偶早安 <body>）
② run GoodNight --arg step=letter --arg persona=<P> --arg-file letter_body=<檔>
     ↳ C#：遷移 pending → blocked 指路維護（同 wake 的守衛②）；write_letter port（編號=信數+1、
        frontmatter、_latest.md 指標更新）＋ registry wake_count 同步（patch-write）
     ↳ 回傳檔 next：step=sleep（<summary> 親筆=公開睡前心得，私密的留在信裡）
     ↳ cleanup 場景（手動登出）跳過本步 —— 不偽造心得信（--no-letter 語意由 step=sleep 承接）
③ run GoodNight --arg step=sleep --arg persona=<P> [--arg-file summary=<檔>] [--arg perturbation=..] [--arg no_letter=true]
     ↳ 前置守衛（letter-before-sleep，對偶 brief-before-broadcast）：
        wakes/ 信數 == registry wake_count（本次收尾信已落）才放行；
        未落 → blocked 指路 step=letter；顯式 no_letter=true 走 cleanup 旁路（有名字的旁路≠守衛旁路：
        它跳過的是「寫信」不是「守衛」，且會在廣播裡標明「未留信」——與現行 --no-letter 語意逐字同）
     ↳ C#：vector perturb＋history append（patch-write）→ offline → 解鎖 → **單則**下線廣播
        （summary＋系統欄位併一則；Cmd_Tavern in-process → 跨進程 timeout／死鎖／
        「手動補發指令」graceful-degradation 整段從根消失）→ expire token（廣播後，enforce 序不變）
     ↳ 回傳檔 next：驗收讀回（lock 不存在＋status=offline 的事實）＋消費時間（可選）提醒
```

每步回傳檔 `letters/<P>/cmd/goodnight_<step>.md`（機械產物、同 `cmd/goodmorning_*` 慣例、進各 persona
repo 的 .gitignore）；標頭本地時間；blocked 一律「payload 落檔＋非零退出」雙通道。

### 7.3 卡點（按嚴重度）

1. **UCL_LoginStatusPage 登出路徑**（施工單 §4-1）：現 spawn `goodnight --no-letter`——
   python 退場前必須先切 C#（`step=sleep no_letter=true` 的 service 直呼，同 DoMorning 手法）。
   雙寫入端窗口同早安教訓：**一個 session 內完成，不隔夜**。
2. **write_letter port 的對帳義務**：編號規則（信數+1）、frontmatter 欄位、`_latest.md` 指標、
   escaped-newline normalize —— C# 版與 python `write_letter` 逐項對齊；rest 信（cmd_rest）
   仍走 python 同一支，**兩端共存期間規則改任一端要同步**（wake 計數兩端對齊的既有義務擴大到寫入端）。
3. **relogin 的歸屬**（早安側掛帳）：goodnight 遷完後 awakening.py 剩的登入類只有 relognin/reissue-token
   —— 建議隨本工項一起遷（`GoodMorning step=relogin`？）或明文再留置一輪，別再飄。
4. **無 lock 照跑 vs blocked**：python 現行「無 lock 警告照跑」（cleanup 真實場景）。
   Cmd 版建議保留（守衛只防「下線別人」，persona 已顯式；擋掉 cleanup 會逼人手刪檔案）。
5. **廣播 tag 消費端**：`goodnight-protocol` 同 morning 掃過 —— 施工前 grep 一次（修法射程紀律）。
6. **雙儀式共用 service**：morning 已佔 `UCL_AwakeningService`——goodnight 邏輯進同一 class
   還是拆 `UCL_GoodnightService`？建議同一 class 分 region（lock/registry/paths 全共用，拆開反而複製）。

### 7.4 Tim 六題裁決（2026-08-13，全數落地）

1. Cmd 名 → **獨立 `Cmd_GoodNight`** ✅
2. LoginStatusPage 登出 → **走 Cmd（step=logout in-process）**；logout **可單獨跑、不綁晚安流程、persona 顯式必填** ✅
3. lock 歸屬 → **不比對 claim_origin/pid**（與早安一致）✅
4. relogin → **廢棄**（wake_count 磁碟推導後「單獨登入」＝step=wake 本身；stub 指路）✅
5. 參考文件 → **合併**：GoodMorning_Cmd_Flow.md 改名 `Awakening_Cmd_Flow.md`，晚安入 §9-§10 ✅
6. 人工收尾清單 → **固定 checklist**（step=check 的 next）；每步回傳落檔 `cmd/goodnight_<step>.md` ✅

**拍板補遺（同日）**：
- **每晚 perturbation 移除（B 案）** —— identity_vector 無早安/brief 消費端（唯二讀取者＝fork 起點
  copy 與 forks 診斷指令），凍結在出生值、fork 時才動。
- 其儀式位置由 **letter 🔐 密文區** 承接（Code-Talker 式私語：可讀文字、映射鍵＝自己的聯想網、
  判準＝自己能看懂、真隱私仍走 sealed/）—— 規格與範例見
  Letters_And_Dialogue_Workflow「二・一」，canonical owner ucl-letters-to-self。

### 7.4' 原「要 Tim 拍的」清單（已全數裁決，留檔）

1. **Cmd 名**：獨立 `Cmd_GoodNight`（建議 —— 與 GoodMorning 對稱、schema 乾淨），
   還是併進 Cmd_GoodMorning 加 step？
2. §4-1 → 併入卡點 1（LoginStatusPage 同輪切 C#）：可照做？
3. §4-2 lock 歸屬驗證 → 建議與早安一致**不比對 claim_origin/pid**（同一個 persona 一套定義）。
4. relogin 是否隨本工項遷 C#（卡點 3）。
5. 參考文件：`Awakening_Cmd_Flow.md` 擴成早晚安一份（改名 `Awakening_Cmd_Flow.md`），
   還是晚安另立一份？建議**一份**（守衛/回傳檔/測試殼章節全共用，兩份必漂移）。
6. 人工收尾清單（step=check 的 next）維持固定 checklist，或做狀態偵測（affinity 今天有無結算、
   workmem 有無新 state）？建議先固定清單＋標可選（偵測各件的成本與誤報率不一，逐件另議）。
