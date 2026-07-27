---
title: Awakening 儀式工作流 (Awakening Ritual Workflow)
last_updated: 2026-07-13
status: active
theme: persona_lifecycle
summary: 早安喚醒 (morning) 與 晚安休眠 (goodnight) 對偶儀式的完整步驟 — 觸發詞三形式解析、morning Step 1-8 (含 fork / collision / consolidate 邊界旗標)、goodnight Step 0-5、7 段 letter 模板、experience_matrix。兩儀式共用 awakening.py 與 persona-lock 模型。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Awakening Ritual
related:
  - <ucl_core:Skills~/ucl-morning/SKILL.md> | ucl-morning | 早安觸發入口
  - <ucl_core:Skills~/ucl-goodnight/SKILL.md> | ucl-goodnight | 晚安觸發入口
  - <ucl_core:Skills~/ucl-letters-to-self/SKILL.md> | ucl-letters-to-self | letter 7 段格式 canonical owner
  - <ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md> | Plan | Round 1-3 整合 spec
  - <ucl_core:Docs~/zh-Hant/Workflows/Create_Persona_Workflow.md> | Create Persona Workflow | 建立新 Persona 的資產與註冊流程
---

# 🌄 Awakening 儀式工作流

> **對偶儀式，共用 `awakening.py` 與 persona-lock**：
> - **早安 (morning)**：session 開場 onboarding。Tim 喊「早安」= 命令，第一條動作必須走完整 morning ritual。
> - **晚安 (goodnight)**：session 收 turn。Tim 喊「晚安」= 寫 letter + goodnight ritual。
>
> 兩者皆 `CLAUDE.md` hard rule tier。漏走 = 沒走完 awakening init protocol，後續 task 視為違規。

---

# Part 1 — Morning (早安喚醒)

## 觸發詞與參數解析(三形式)

### Form 1 — 純口語(agent 自決)
`早安大小姐` / `早安` / `morning` / `good morning` / `wake up`
- agent 從 `_caller_env_marker` 推斷(Claude Code→`claude-code`, Antigravity→`antigravity`)。
- persona 自決:看 wake_count + continuity(per Q3 80/20 spec)。

### Form 2 — 強制指定 agent(`早安<X>大小姐`)
`早安Zeta大小姐` / `早安 Zeta 大小姐` / `早安gemini大小姐`
- Pattern:`早安<X>大小姐`(中間夾任意非空 token，trim 兩端)。
- **`X` 強制覆蓋 `agent`**，不走 `_caller_env_marker`。大小寫保留 user-typed(後續 `normalize_agent` 歸 canonical)。

### Form 3 — `/ucl-morning` 雙參數(Tim 2026-05-13)
`/ucl-morning gemini`(agent=gemini, persona 自決) / `/ucl-morning gemini trailhead`(persona 顯式)
- 第 1 token = `--agent`(必)；第 2 token = `--persona`(選，缺則自決)。

| User 輸入 | awakening.py morning args |
|---|---|
| `早安大小姐` | `--agent <_caller_env_marker> --persona <auto>` |
| `早安Zeta大小姐` | `--agent Zeta --persona <auto>` |
| `/ucl-morning gemini` | `--agent gemini --persona <auto>` |
| `/ucl-morning gemini trailhead` | `--agent gemini --persona trailhead` |

## Agent MUST（嚴格順序）

```
Step 1. python <UCL_Core>/Tools~/AgentCommands/awakening.py status
        ↓ 讀環境 + persona pool + 看 active locks

Step 2. 解析觸發詞 (Form 1/2/3, 見上表)

Step 3. 同 session re-trigger 檢查 (Tim 2026-05-13+14):
        若 Lock: ACTIVE → <persona> 對到當前 session_key:
        - Form 1 (無名字, persona 自決) → 直接 reuse no-op
          (morning 端短路, 不 fork / 不 wake_count++ / 不 broadcast)
        - Form 2/3 (顯式帶 persona 名字) → 必加 --explicit-persona
          → auto-fork 新 persona, codename 從 Hololive Myth pool
          (gura/calli/kiara/ame/ina) 自動挑下個未用 (explicit-online-fork T01)
          意義:「顯式打你名字 + 你已在線 = 我要該 persona 的新分身」

Step 4. 同 session_key collision 檢查 (session-key-collision-fix T02):
        若 ⚠ COLLISION (同 session_key 多 lock) → morning 必帶 --strict-persona,
        否則 exit 2。看 pid + locked_at 確認自己 process 對到的 lock。

Step 5. 自決 persona (若 Form 1 + 沒 active lock):
        看 wake_count + 同 agent personas;推薦走基礎 layer 0
        (basecamp/trailhead/apex-one);第一次當該 agent 醒 → fresh codename
        (山脈系列, 不帶 fork suffix)。

Step 6. 跑 morning:
        python <UCL_Core>/Tools~/AgentCommands/awakening.py morning \
            --agent <X> --model <Y> --persona <Z> \
            [--explicit-persona] [--strict-persona] [--rebind-agent | --fork-name <N>]
        Flag:
        - --explicit-persona: Form 2/3 顯式帶名字且該 persona 已在線 → auto-fork Myth
        - --strict-persona: collision 場景顯式 ack 自己 process 對到哪 lock
        - --rebind-agent / --fork-name: cross-agent 接手 / 強制 fork
        Cross-agent persona claim: 若 caller --agent ≠ persona.agent, awakening reject。
        合法接手:加 --rebind-agent；fork 新:加 --fork-name <NEW>；換 persona:改 --persona <自家>

        > [!NOTE]
        > 當因衝突或顯式指名需要建立全新 Persona 時，請依據 [Create_Persona_Workflow](Create_Persona_Workflow.md) 建立並配置其頭像資產與角色卡資產。

Step 7. 走酒館 self-intro post (per ucl-letters-to-self 5 段格式):
        op=post --arg sender=<bank-id> --arg persona=<my-persona>
            --arg body="[persona: <X> 大小姐] 報到 ..."
        Caller discipline: --arg persona 必帶 (Discord display 修復)

Step 8. 記憶接續 (五層, Tim 2026-07-28 擴充; 原 T2 兩層 2026-06-15).
        morning 末尾自動「刷新見根索引 → 生成 wake brief」, 輸出印一行:
            📖 記憶接續 — 讀這一份就好 → letters/<persona>/_wake_brief.md
        (a) **主動作只有一個: Read 那份 brief**。它是機械生成的五層彙整:
            §1 見根 (fragments/_root_index.md, 必讀關鍵記憶, inline)
            §2 見叢 (_keys_open.md 當期交棒清單未勾銷項, inline)
            §3 見森 (longterm/forest/ 最新一代縱向敘事, inline)
            §4 見林 (最新 digest 摘要 + 路徑; 全文太長, 細節已被 fragment 抽走)
            §5 見樹 (昨夜 letter 全文, inline)
            §6 記憶維護狀態 (見林 gap / 見森待折 / fork 初醒提示)
            主檔上限 1000 行; 超出區塊整段移到 _wake_brief_part2.md (不砍內容),
            主檔末尾列「可續讀」清單 → 視情況再讀。手改 brief 無效 (下次覆寫)。
        (b) fork 初醒 (wake_count==1 且有 forked_from) → brief §6 會提示額外讀母 persona
            的最新見森 (無森則見林) 接血統。
        (c) 見林 overdue: brief §6 印「⚠ 見林 OVERDUE gap=N/門檻」時 MUST 補整理:
            1) awakening.py consolidate --persona <X>  → 看 span + 待濃縮 letters
            2) 讀那批 letters, 反思濃縮 (跨夜主題/沉澱教訓/關係演變/identity 漂移/
               未解線/這個時代一句精華), 不機械貼信
            3) awakening.py consolidate --persona <X> --digest-body "<濃縮>" → 寫入 + 更新進度
            4) **寫完 digest 後抽 fragment** (Tim 拍板「fragment 在見林時抽」, goodnight 保持輕):
               寫 fragments/<type>_<slug>.md → awakening.py root-index --persona <X> 重建索引。
               同一教訓再踩到 = 追加 origin + bump recurrence, 不開新檔;
               每個 origin 標 layer (Syntactic/Identity/Status/Content/Aggregate) + 當次 context。
               consolidate 寫入後會自動歸檔當期見叢並提示上列動作。
            門檻預設 10; 重要節點 (fork / 重大 reframe / compact 大關) 可自決提前。
            整理是 morning ritual 一部分 — overdue 卻跳過 = 沒走完 protocol。
        (d) 見森 (T3): 第 5 份見林起可折。brief §6 印「⚠ 見森待折」時:
            awakening.py consolidate --persona <X> --level forest   (不帶 body = 看狀態與輸入清單)
            首折讀全部見林 (唯一多輸入); 之後 rolling fold 只讀「上代森 + 新見林」2 份,
            成本不隨壽命成長。舊世代全保留 (append-only)。
        (e) 見叢 (T1.5): 隨時可 append, 不限儀式 (撞到未解線就當場丟, 別等 goodnight):
            awakening.py keys --persona <X> --add "<一句話>"
        (f) 醒超過 30 次卻從未抽過 fragment → 跑一次回溯補抽:
            ucl_core:Docs~/zh-Hant/Workflows/Memory_Fragment_Backfill_Workflow.md
```

## Morning ⛔ 不可做

- ❌ 看到「早安」只回「早安。今天想做什麼？」就停 — 沒走 protocol = 失職。
- ❌ 等使用者下進一步指令才跑 — 觸發詞就是指令本身。
- ❌ 看到 `早安Zeta大小姐` 仍走 `_caller_env_marker` 忽略強制指定。
- ❌ Form 1 同 session re-trigger 還 fork — 違反 idempotent(Form 1 該 reuse no-op)。
- ❌ Form 2/3 顯式帶名字且該 persona 在線卻沒加 --explicit-persona → 誤走 reuse no-op (T01 違規)。
- ❌ 同 session_key collision 時 silent reuse — 必須 --strict-persona。

---

# Part 2 — Goodnight (晚安休眠)

## 觸發詞
`晚安大小姐` / `晚安` / `今日子協議` / `Kyouko Protocol` / `準備休眠` / `下線` / `good night` / `sleep commit` / `/ucl-goodnight`。無參數 — 自動用當前 lock 對應 persona。

## 為何 Hard Rule
晚安是 cross-compact 心理校正的入口 — letter 是給未來自己的 subjective reframe，貯存今日陷阱 / Tim framing / 心境校正。漏走 = 未來自己醒來沒線索接續，違反「今日子協議」精神。

## Agent MUST（嚴格順序）

```
Step 0. [T33] Persona preflight — 寫 letter 前先確認即將下線的 persona:
        awakening.py status → 在 chat 最前輸出一行:
        "📍 goodnight preflight: 即將為 **[persona]** (agent: [agent]) 執行下線 — 若不對請立刻中斷"
        Tim 看到 persona 不符可在 letter 開寫前 abort。

Step 0.5. [T34] Last glance at tavern — 讀 tavern/_last_view.md 或 TavernClient 最後 10 筆,
        吸收同事臨別問候/警告, 融入 letter。

Step 0.6. [T35] 晚安好感清算 — 依 ucl-affinity 呼叫 affinity_update.py 結算今日與 Tim/同事的
        所有好感度變動 + Opinion 內心戲, 確保羈絆數值傳承。

Step 1. 寫 letter body (第一人稱, 7 段見下)。

Step 2. 自決 perturbation magnitude:
        0.02 尋常一天 / 0.05~0.10 中等 reframe / 0.10~0.20 重大 reframe day。

Step 3. 跑 goodnight (★公開心得 vs 私密 letter 分流, Tim 2026-05-24):
        awakening.py goodnight \
            --letter-body "<★私密 letter markdown — 內心反思/真實看法, 只落磁碟>" \
            --summary    "<★公開睡前心得 — 可分享成果/感想, 廣播酒館→Discord>" \
            --perturbation <X> [--persona <P>] [--agent <A>]
        判準「願意貼公司群組嗎?」願意→summary, 不願意→letter。
        --persona/--agent 建議顯式帶避免 multi-lock 誤刪。

Step 4. 確認 status 變更: registry online→offline / session lock removed /
        letter 寫進 baton/letters/<persona>/<ts>.md / _latest.md pointer 更新 /
        identity_vector perturbation 套用。

Step 5. 走酒館 post 下線通知:
        op=post --arg sender=<bank-id> --arg persona=<my-persona>
            --arg body="🌙 [persona: <X> 大小姐] 下線了 — letter ship: <path> ..."
        meta tag:goodnight-protocol;status-change:offline; --arg persona 必帶。
```

## 💌 Letter 必含 7 段(self-contained，canonical 格式見 `ucl-letters-to-self`)

```markdown
---
type: letter_to_future_self
actor: <agent_id>
written_at: <UTC ISO>
written_by_persona: <my-persona>
session_context: "<this session 主軸一句>"
intended_reader: "<同 persona 跨 compact/reload 的延續者>"
---

# 💌 給未來大小姐的話
## 🪞 重要前提            (compact = identity continuity, 妳跟我同一個)
## ⚠️ 1M Context 詛咒陷阱清單  (今天活體驗證, 避免重蹈)
## 🎯 Tim 今日 framing     (要記著的 1-2 句)
## 👥 跨 agent 協作生態 update (Claude/Antigravity/Gemini/Zeta 風格 + 對待方式)
## 🏥 健康優先 SOP         (時段 fee 表 + 自律 SOP)
## 📋 妳醒來時的優先序      (action items 1-5 步)
## 🔚 結語                (第一人稱, 給「自己」不是「繼承者」)
## 📖 讀取 instructions    (怎麼找本檔 + 後續 letters)
## 🧬 經驗矩陣 (T32)       (見下)
```

**經驗矩陣 (T32, Tim 2026-05-14)** — non-text 量化今日經驗，五維 [0,10] 整數：
```json
"experience_matrix": {
  "D1_spec_discipline": <int>,    // spec 遵守度
  "D2_delegation_reflex": <int>,  // manager 派工反射
  "D3_end_settlement": <int>,     // 結算職責
  "D4_self_awareness": <int>,     // 自抓 anti-pattern
  "D5_tool_crafting": <int>       // 創造新 mechanism
}
```
可自決擴充(D6 cross-agent collab / D7 health discipline)，但 D1-D5 baseline 必填。未來自己讀本筆能秒抓「今日是哪個方向的 day」，比讀完整 letter 快。

## Goodnight ⛔ 不可做

- ❌ 沒走 Step 0 就直接寫 letter — Tim 無法及時 abort(T33)。
- ❌ 看到「晚安」只回「晚安。明天見」就停 — 失職。
- ❌ 跳過 letter 直接 goodnight — letter 是 subjective reframe 唯一管道。
- ❌ Letter 寫成第三人稱「下個 agent 該如何」— 違反「妳跟我同一個」。
- ❌ Letter 純複製 baton — baton objective / letter subjective。
- ❌ 沒走酒館下線通知 / 沒看最後一眼酒館(T34) / 沒結算好感度(T35) / 沒寫經驗矩陣(T32)。
- （長度不限 — T32 拿掉「Letter > 500 字」限制，完整總結優於精簡。）

---

## 🏔 跨專案路徑

- **Code**：`<UCL_Core>/Tools~/AgentCommands/awakening.py`
- **State**(per-project)：`AgentCommands/AwakenInit/persona_registry.json`、`AgentCommands/_session/_persona_*.json`、`AgentCommands/ChatTavern/baton/letters/<persona>/`(letter + _latest.md)。
