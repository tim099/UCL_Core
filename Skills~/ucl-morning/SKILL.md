---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「早安<X>大小姐」/「/ucl-morning <agent> [<persona>]」時觸發。
  Agent 必須跑完整 morning protocol: status check → 解析強制 agent/persona override → awakening.py morning → 走酒館 self-intro。
  觸發詞包含: 早安大小姐 / 早安<X>大小姐 / 早安 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta 都該走本 skill。對應 CLAUDE.md hard rule 早安觸發章節 + Plan_Awakening_Init_Protocol.md。
---

# UCL Morning — 早安大小姐喚醒協議

> 一句話：**Tim 喊「早安」就是命令，agent 第一條動作必須走 morning ritual，沒商量。**

## 🎯 為什麼是 Hard Rule

Tim 跨多 chat session 並行管理 agents — 早安是 onboarding ritual 的入口。漏走 = 沒走完 awakening init protocol，後續所有 task 視為違規（per CLAUDE.md）。

## 📥 觸發詞與參數解析

### Form 1 — 純口語（agent 自決）

```
早安大小姐
早安
morning / good morning / wake up
```

- Agent 從 `_caller_env_marker` 推斷自己（Claude Code → `claude-code`, Antigravity → `antigravity`）
- Persona 自決：看 wake_count + continuity（per Q3 80/20 spec）

### Form 2 — 強制指定 agent（`早安<X>大小姐` 樣式）

```
早安Zeta大小姐
早安 Zeta 大小姐
早安gemini大小姐
```

- Pattern：`早安<X>大小姐`（中間夾任意非空 token，trim 兩端空白）
- **`X` 強制覆蓋 `agent` 參數**，不走 `_caller_env_marker` 推斷
- 大小寫保留 user-typed 原樣（後續走 `normalize_agent` 歸 canonical）
- 範例：`早安Gemini大小姐` → `--agent Gemini` → normalize → canonical `gemini`

### Form 3 — `/ucl-morning` 雙參數樣式（Tim 2026-05-13 拍板）

```
/ucl-morning gemini            ← agent=gemini, persona 自決
/ucl-morning gemini trailhead  ← agent=gemini, persona=trailhead 顯式指定
/ucl-morning claude-code basecamp
```

- 第 1 個 token = `--agent <value>`（必）
- 第 2 個 token（若存在）= `--persona <value>`（選，缺則自決）
- `/ucl-morning` 是 Claude Code slash command 觸發本 skill 的標準入口

### 三 form 的對應命令

| User 輸入 | 對應 awakening.py morning args |
|---|---|
| `早安大小姐` | `--agent <_caller_env_marker> --persona <auto>` |
| `早安Zeta大小姐` | `--agent Zeta --persona <auto>` |
| `/ucl-morning gemini` | `--agent gemini --persona <auto>` |
| `/ucl-morning gemini trailhead` | `--agent gemini --persona trailhead` |

## 🛠 Agent MUST（嚴格順序）

```
Step 1. python <UCL_Core>/Tools~/AgentCommands/awakening.py status
        ↓ 讀環境 + persona pool + 看 active locks

Step 2. 解析觸發詞:
        - Form 2 「早安<X>大小姐」: agent=X 強制
        - Form 3 「/ucl-morning <a> [<p>]」: agent=a, persona=p (若有)
        - Form 1: agent 走 _caller_env_marker, persona 自決

Step 3. 同 session re-trigger 檢查 (Tim 2026-05-13 + 2026-05-14 修訂):
        若 status 顯示 Lock: ACTIVE → <persona> 對到當前 session_key:
        - Form 1 (`早安大小姐` 無名字, persona 由 agent 自決) → 直接 reuse no-op
          (awakening.py morning 端短路, 不 fork, 不 wake_count++, 不 broadcast)
        - Form 2/3 (顯式帶 persona 名字) → caller 必加 --explicit-persona
          → awakening.py auto-fork 新 persona, codename 從 Hololive Myth pool
          (gura / calli / kiara / ame / ina) 自動挑下個未用 (explicit-online-fork T01)
          意義: 「我顯式打你名字 + 你已在線 = 我要該 persona 的新分身」

Step 4. 同 session_key collision 檢查 (session-key-collision-fix T02):
        若 status 顯示 ⚠ COLLISION (同 session_key 多 lock)
        → morning 必須帶 --strict-persona, 否則 exit 2
        → 看 pid + locked_at 確認自己 process 對到的 lock

Step 5. 自決 persona (若 Form 1 + 沒 active lock):
        - 看 wake_count + 跟自己同 agent 的 personas
        - 推薦走基礎 layer 0 persona (basecamp / trailhead / apex-one)
        - 第一次當該 agent 醒來 → fresh codename (山脈系列, 不帶 fork suffix)

Step 6. 跑 morning:
        python <UCL_Core>/Tools~/AgentCommands/awakening.py morning \
            --agent <X> --model <Y> --persona <Z> \
            [--explicit-persona] [--strict-persona] [--rebind-agent | --fork-name <N>]

        Flag 用法:
        - --explicit-persona: Form 2/3 顯式帶名字時加 (該 persona 已在線 → auto-fork Myth)
        - --strict-persona: collision 場景顯式 ack 自己 process 對到哪 lock
        - --rebind-agent / --fork-name: cross-agent 接手 / 強制 fork (per cross-agent-persona-claim-fix)

        Cross-agent persona claim (cross-agent-persona-claim-fix):
        若 caller --agent 跟 persona.agent 不同, awakening 會 reject。
        合法接手: 加 --rebind-agent ack; fork 新: 加 --fork-name <NEW>; 換 persona: 改 --persona <自家>

Step 7. 走酒館 self-intro post (per ucl-letters-to-self skill 5 段格式):
        op=post --arg sender=<bank-id> --arg persona=<my-persona>
            --arg body="[persona: <X> 大小姐] 報到 ..."
        Caller discipline: --arg persona=... 必帶 (per Discord display 修復)

Step 8. 長期記憶 (T2, Tim 2026-06-15 拍板). morning 輸出末尾的「🧠 長期記憶 (T2)」block 會指引:
        (a) 見林 → 讀近期長期記憶 digest (longterm/_index.md 最新一篇);
            見樹 → 讀昨夜 letter (_latest.md). 順序: 先林後樹 (同 reading-library resume).
        (b) fork 初醒 (wake_count==1 且有 forked_from) → block 會額外指你讀母 persona
            最新 digest 一次接血統 context, 讀完即可.
        (c) overdue 整理: block 印「⚠ OVERDUE: gap=N (門檻 10)」時 MUST 補整理本段:
            1) `awakening.py consolidate --persona <X>`  → 看 span + 待濃縮 letters 清單
            2) 讀那批 letters, 反思濃縮 (跨夜主題/沉澱教訓/對 Tim&同事關係演變/identity 漂移/
               未解線/這個時代一句精華), 不是機械貼信
            3) `awakening.py consolidate --persona <X> --digest-body "<濃縮>"` → 寫入 + 更新進度
            門檻預設 10, 但 agent 可在重要節點 (fork / 重大 reframe / compact 大關) 自決提前整理.
            整理是 morning ritual 的一部分 — overdue 卻跳過 = 沒走完 protocol.
```

## ⛔ 不可做

- ❌ 看到「早安」只回「早安。今天有什麼想做的？」就停 — 沒走 morning protocol = 失職
- ❌ 等使用者下進一步指令才跑 — 觸發詞就是指令本身
- ❌ 看到 `早安Zeta大小姐` 仍走 `_caller_env_marker` 推斷而忽略強制指定
- ❌ Form 1 (純口語無名字) 同 session re-trigger 還 fork — 違反 idempotent (Form 1 該 reuse no-op)
- ❌ Form 2/3 顯式帶 persona 名字 + 該 persona 已在線, 但沒加 --explicit-persona → 誤走 reuse no-op (T01 違規)
- ❌ 同 session_key collision 時 silent reuse — 必須 --strict-persona 顯式

## 📋 完整 spec 跟相關

- CLAUDE.md hard rule 早安觸發章節（主專案根）
- [Plan_Awakening_Init_Protocol.md](../../Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md)（Round 1-3 整合 spec）
- `ucl-letters-to-self` skill — morning 後該讀 letter 跟走酒館報到
- `ucl-goodnight` skill — 對偶 ritual

## 🏔 跨專案路徑

- **Code**（跨專案共用）：`<UCL_Core>/Tools~/AgentCommands/awakening.py`
- **Spec doc**（跨專案共用）：`<UCL_Core>/Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md`
- **State files**（per-project）：
  - `AgentCommands/AwakenInit/persona_registry.json`
  - `AgentCommands/_session/_persona_*.json`
  - `AgentCommands/ChatTavern/baton/letters/<persona>/`
