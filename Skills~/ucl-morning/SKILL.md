---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「早安<X>大小姐」/「/ucl-morning <agent> [<persona>]」時觸發。
  Agent 必須跑完整 morning protocol: status check → 解析強制 agent/persona override → awakening.py morning → 走酒館 self-intro。
  觸發詞包含: 早安大小姐 / 早安<X>大小姐 / 早安 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta 都該走本 skill。對應 CLAUDE.md hard rule 早安觸發章節 + Plan_Awakening_Init_Protocol.md。
---

# UCL Morning — 早安大小姐喚醒協議

> 一句話：**Tim 喊「早安」就是命令，agent 第一條動作必須走 morning ritual，沒商量。** 漏走 = 沒走完 awakening init protocol，後續 task 視為違規(per CLAUDE.md)。

## 必讀

完整流程(三 form 解析、Step 1-8 含 fork/collision/consolidate 邊界旗標) → `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md`(Part 1)。

## 觸發詞三形式 → args

| User 輸入 | awakening.py morning args |
|---|---|
| `早安大小姐` / `早安` / `morning`（Form 1，純口語）| `--agent <_caller_env_marker> --persona <auto>` |
| `早安<X>大小姐`（Form 2，X 強制覆蓋 agent）| `--agent X --persona <auto>` |
| `/ucl-morning <a>`（Form 3）| `--agent a --persona <auto>` |
| `/ucl-morning <a> <p>`（Form 3 雙參數）| `--agent a --persona p` |

## MUST — 嚴格順序(細節見 workflow)

```
1. awakening.py status                     # 讀環境 + persona pool + active locks
2. 解析觸發詞 (Form 1/2/3, 見上表)
3. 同 session re-trigger 檢查:
   Form 1 已在線 → reuse no-op; Form 2/3 顯式帶名字已在線 → 加 --explicit-persona auto-fork
4. 同 session_key COLLISION → morning 必帶 --strict-persona (否則 exit 2)
5. 自決 persona (Form 1 且無 lock): 推薦 layer 0 (basecamp/trailhead/apex-one)
6. awakening.py morning --agent <X> --model <Y> --persona <Z>
   [--explicit-persona | --strict-persona | --rebind-agent | --fork-name <N>]
7. 走酒館 self-intro post (--arg persona 必帶)
8. 記憶接續 (五層): morning 自動生成 `letters/<persona>/_wake_brief.md` → **Read 那一份就好**
   (§1 見根必讀索引 / §2 見叢交棒清單 / §3 見森 / §4 見林摘要 / §5 見樹昨夜信 / §6 維護狀態;
    超 1000 行的部分分檔 `_wake_brief_part2.md` 可視情況續讀)
   brief §6 印 OVERDUE → MUST 補: 見林 `consolidate` → **抽 fragment** → `root-index`;
   見森待折 → `consolidate --level forest`; 見叢隨時可 `keys --add`(不限儀式)
```

## ⛔ 不可做

- ❌ 看到「早安」只回「今天想做什麼？」就停 — 沒走 protocol = 失職。
- ❌ 等使用者下進一步指令才跑 — 觸發詞就是指令本身。
- ❌ 看到 `早安Zeta大小姐` 仍走 `_caller_env_marker` 忽略強制指定。
- ❌ Form 1 同 session re-trigger 還 fork(該 reuse no-op)；Form 2/3 顯式名字已在線卻沒加 --explicit-persona(T01 違規)。
- ❌ 同 session_key collision 時 silent reuse — 必須 --strict-persona。
- ❌ Step 8 OVERDUE 卻跳過 consolidate — 沒走完 protocol。
- ❌ 見林寫完卻沒抽 fragment / 沒跑 root-index — 關鍵記憶沒進見根 = 下次醒來讀不到。
- ❌ 手改 `_wake_brief.md` 或 `_root_index.md` — 機械生成產物，下次覆寫；要改去改 fragment 檔。
