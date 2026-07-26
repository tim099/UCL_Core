---
name: ucl-hook-setup
description: |
  在使用 UCL_Core 的上層專案配置 Claude Code hooks — PostToolUse 早期警告 + Stop 強制驗收 ValidateAssetFormat。
  使用者首次接 UCL_Core、新專案 onboarding、要求設定 hook、settings.json 規劃、或詢問如何讓 AI 改 UCL_Asset JSON 後自動驗證時用本 skill。
trigger: { on_intent: ["Hook Setup", "Hook 設置", "設置 Hook", "install skills"] }
---

# UCL Hook Setup — Claude Code Hook 配置

> 配置完後，agent 寫 / 改 `UCL_Asset` JSON 會自動觸發 schema + reference 驗證；turn 結束前未過 → Stop hook 阻擋，逼 agent 修正。

## 必讀

完整 settings.json 範本 + failure 處理 + 跨平台 caveat → `ucl_core:Docs~/zh-Hant/Workflows/Hook_Setup_Workflow.md`

## 適用前提

- Claude Code
- 專案內含 UCL_Core（任何位置）
- Python 3.10+
- Unity Editor 開發時保持開啟（`UCL_AgentCommandWatcher` 接手 trigger）

## `<UCL_CORE>` 路徑替換

settings.json 範本內 `<UCL_CORE>` 是 placeholder。視專案結構替成實際相對路徑（相對 git root）：

- `Assets/UCL/UCL_Core` — Unity project = git root
- `CardGame/Assets/UCL/UCL_Core` — Unity 在子目錄（EOV）
- `UCL_Core` — 純工具 / 後端專案

## 兩段式設計

1. **PostToolUse**（每次工具呼叫後）— 早期警告，agent 看到後修正
2. **Stop**（turn 結束前）— 強制門檻，未過驗證阻擋 stop

## 配套：Skill 安裝

設 hook 屬於新專案 onboarding 的一次性流程。**順手做 Skill 安裝**：

```bash
python <UCL_CORE>/Tools~/install_skills.py
```

把 UCL_Core 內的 skill 拷到 `<root>/.claude/skills/`。
