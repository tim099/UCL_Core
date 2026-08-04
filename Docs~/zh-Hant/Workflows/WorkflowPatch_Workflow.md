---
title: Workflow 補丁工作流 (Workflow Patch Workflow)
last_updated: 2026-08-04
status: active
theme: agent_activity
summary: workflow QA confirm bug 後 register patch entry；累積 ≥ 3 patches 自動警示強制 refactor 的 anti-rot 機制完整流程 — 儲存佈局、_index.json schema、workflow_patch.py 全 CLI (register / list / status / status-all / refactor)、agent 自律 SOP。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Workflow Patch
related:
  - <ucl_core:Skills~/ucl-workflow-patch/SKILL.md> | ucl-workflow-patch | 補丁機制觸發入口
  - <ucl_core:Docs~/zh-Hant/Plan/Memory_System_Design.md> | Proposal #31 | 補丁機制 spec
  - <repo:AgentCommands/Tools/workflow_patch.py> | workflow_patch.py | 補丁 CLI 工具
---

# 🩹 Workflow 補丁工作流

> **解決什麼問題**：Workflow 累積 ad-hoc fixes (e.g. SKILL.md 一直加 "⚠ 注意: ...") 會變 spaghetti — 後續 agent 看不下去 / 邊界 case 互相衝突 / 維護成本爆炸。本系統讓 agent 每次修 workflow bug 時 register 一筆 patch entry，累積 ≥ 3 patches 就強制 refactor 整個 workflow，而非繼續貼補丁。

## 核心 hard rule：3 patch 上限 = anti-rot

**workflow 出錯 → 修正 + register patch entry；累積 ≥ 3 patches → 強制 refactor (不准再加 patch)**。3 patch 上限是 anti-rot 機制：強制 stop & rethink，refactor 整個 workflow 比繼續貼 patch 健康。

## 一、儲存佈局

```
docs/Workflows/_patches/<workflow-slug>/
  _index.json           # patch counter + 累積記錄
  001_<short-name>.md   # patch 1 frontmatter + 詳情
  002_<short-name>.md   # patch 2
  003_<short-name>.md   # patch 3 → 警戒
  # 第 4 個會被 reject (強制 refactor)
  refactor_history.md   # refactor 記錄 + archived patches list
  _archived_R01_001_...md   # refactor 後改 prefix 不刪 (audit)
```

> ⚠ **docs-path 提醒**：patches 目前落在 `docs/Workflows/_patches/`(下游專案 repo root 的 docs/)，與 UCL_Core 內的 `Docs~/zh-Hant/Workflows/` 是**不同位置**。patches 是 per-project runtime 資料(隨各專案 repo root 走)，workflow 文檔本身則在 UCL_Core submodule 內(跨專案共用)。勿混淆兩者。

`_index.json` 結構：
```json
{
  "workflow_slug": "commit-workflow",
  "patch_count": 2,
  "patches": [
    {"id": 1, "filename": "001_xxx.md", "applied_at": "...", "applied_by": "...", "summary": "..."},
    {"id": 2, ...}
  ],
  "last_refactor_at": null,
  "refactor_count": 0
}
```

## 二、Python tool — `workflow_patch.py`

位置：`AgentCommands/Tools/workflow_patch.py`

### register — workflow 出錯 + 修正後 register

```bash
python AgentCommands/Tools/workflow_patch.py register \
  --workflow commit-workflow \
  --root-cause "三層 bump 中 UCL submodule 未切 Dev 分支 → detached HEAD" \
  --patch-summary "commit 前必先 git -C UCL checkout Dev" \
  --applied-by claude-da-xiaojie
```

- 第 4 個 register → **reject** (強制走 refactor)
- 第 3 個 register → warn (剩 0 quota)
- 寫 `001_<slug>.md` frontmatter + 起因 + 修法 sections

### list / status / status-all

```bash
python ... workflow_patch.py list --workflow commit-workflow
python ... workflow_patch.py status --workflow commit-workflow
python ... workflow_patch.py status-all              # 跨 workflow scan
```

### refactor — counter 重置

```bash
python ... workflow_patch.py refactor \
  --workflow commit-workflow \
  --refactor-summary "重寫 ucl-commit skill: 加 pre-flight check (branch state / submodule head)" \
  --refactored-by claude-da-xiaojie
```

行為：
1. Append refactor 記錄到 `refactor_history.md`
2. 舊 patches `.md` 改 prefix `_archived_R<N>_<filename>` (不刪)
3. Counter reset to 0/3
4. `refactor_count++`

## 三、Agent 自律 SOP

### 撞到 workflow bug + QA confirm

1. **修正 workflow** (改 SKILL.md / 文檔 / cmd code)
2. **走 workflow_patch register**：register 一筆 patch entry
3. 看 status — 若 count = 3 → 標記 next time 必先 refactor

### Refactor 時機

`status-all` 看到 🔴 NEEDS REFACTOR → 走 refactor：
1. **cat 舊 patches** 整理 root cause pattern (哪些 bug 重複出現)
2. **rewrite workflow** (改 SKILL.md / 文檔 / cmd code 根本性重設計)
3. **`workflow_patch refactor`** 標記完成
4. Counter reset，重新追蹤新一輪

### Patch audit

看 `patches/` 就知道哪些 workflow 累積最多補丁 —— 那是 workflow 設計問題的指標。

> [!NOTE]
> **2026-08-04**：原本這裡有一套「跟 qa-bug-reward cross-link」的稽核設計
> （每筆 patch 帶 `--qa-bug-ref` 對應 Tim 的 QA reward ledger entry，
> 用高 reward/低 patch 比值區分「開發 bug」與「workflow 設計 bug」）。
> **QA 獎金功能已移除**，該 cross-link 連同 `--qa-bug-ref` 選項一併撤掉。

## 四、與其他 skill 協作

| Skill | 互補 |
|---|---|
| `ucl-commit` | commit-workflow 自己也適用本機制 (dogfood) |
| `agent-lessons-log` | patch 寫進 lesson jsonl 跨 agent 共享 |
| `ucl-glossary` | 「補丁」/「refactor」/「workflow rot」可進 glossary `category=protocol` |

## 五、Phase 2 Backlog (Proposal #31 後續)

- Auto-detect rot pattern (LLM 看 3 patch root cause 推 common theme)
- Workflow health dashboard (IMGUI page 列各 workflow status)
- Discord broadcast: patch register / refactor 事件
- Cmd_WorkflowPatch (C# Cmd 化, 跟 Treasury / Glossary 對齊)

## 六、必讀 / 參考

- spec：Memory_System_Design Proposal #31
- tool：`AgentCommands/Tools/workflow_patch.py`
- storage：`docs/Workflows/_patches/<slug>/`(注意與 UCL_Core `Docs~/zh-Hant/Workflows/` 不同位置)
