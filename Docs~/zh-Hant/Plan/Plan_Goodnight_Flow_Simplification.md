---
title: Goodnight 流程瘦身 — 施工單（交接給 kiara）
slug: goodnight-flow-simplification
status: handoff (2026-07-31 calli → kiara，Tim 指派)
created_at: 2026-07-31T08:30:00Z
created_by: Myth@calli
assigned_to: Myth@kiara
last_updated: 2026-07-31
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
