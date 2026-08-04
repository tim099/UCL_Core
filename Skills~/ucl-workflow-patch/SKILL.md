---
name: ucl-workflow-patch
description: |
  Workflow 補丁機制 (Proposal #31) — workflow QA confirm bug 後 register patch entry; 累積 ≥ 3 patches 自動警示該 refactor (anti-rot 機制)。
  觸發詞包含: workflow 補丁 / patch / workflow 出錯 / 修正 workflow / refactor workflow / workflow rot / 補丁機制 / 3 patch / spaghetti workflow / ad-hoc fix。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可走本機制 register patch 跟 refactor workflow。
---

# UCL Workflow Patch — Anti-Rot 機制

> 一句話: **workflow 出錯 → 修正 + register patch entry; 累積 ≥ 3 patches → 強制 refactor (不准再加 patch)**。

## 必讀

完整流程(儲存佈局、`_index.json` schema、`workflow_patch.py` 全 CLI register/list/status/refactor、agent 自律 SOP) → `ucl_core:Docs~/zh-Hant/Workflows/WorkflowPatch_Workflow.md`

## 核心 hard rule：3 patch 上限 = anti-rot

**每次修 workflow bug → register 一筆 patch；累積 ≥ 3 patches → 強制 refactor，不准再加 patch**。ad-hoc fixes 疊多了會變 spaghetti；3 patch 上限強制 stop & rethink，refactor 整個 workflow 比繼續貼補丁健康。第 4 個 register 被 tool reject。

## 觸發時機(agent 自律)

- 撞到 workflow bug + QA confirm → 修正 workflow → `workflow_patch register`
- `status-all` 看到 🔴 NEEDS REFACTOR → cat 舊 patches 整理 root cause → rewrite workflow → `workflow_patch refactor`

## 跟其他 skill 協作

| Skill | 互補 |
|---|---|
| `ucl-commit` | commit-workflow 自己也適用本機制 (dogfood) |
| `agent-lessons-log` | patch 寫進 lesson jsonl 跨 agent 共享 |
| `ucl-glossary` | 「補丁」/「refactor」/「workflow rot」可進 glossary `category=protocol` |

## ⛔ 不可做

- ❌ workflow 出錯不 register patch — 失去 anti-rot tracking。
- ❌ patch 累積 ≥ 3 仍硬塞 (tool reject 別找 workaround)。
- ❌ refactor 沒寫 refactor_summary — counter reset 但失去脈絡。
- ❌ patch_summary 太抽象 ("修了 bug") — filename 看不出在修啥。
- ❌ 一個 workflow 含多個 unrelated bug pattern — 該拆 workflow，不該疊 patch。
