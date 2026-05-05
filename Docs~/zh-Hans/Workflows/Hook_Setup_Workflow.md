---
title: Claude Code Hook Setup Guide (UCL_Core plugin users)
description: Configure Claude Code hooks in projects using UCL_Core to auto-trigger ValidateAssetFormat — PostToolUse early warning + Stop hard validation gate
last_updated: 2026-05-05
target_audience: [Tools_Maintainer, Gameplay_Programmer]
---

# Claude Code Hook Setup Guide

> 📖 **Full documentation pending translation.** See the canonical Traditional Chinese version: [zh-Hant/Workflows/Hook_Setup_Workflow.md](../../zh-Hant/Workflows/Hook_Setup_Workflow.md).

## TL;DR

Add to your project's `.claude/settings.json`:

```json
{
  "hooks": {
    "PostToolUse": [{
      "matcher": "Edit|Write|MultiEdit",
      "hooks": [{
        "type": "command",
        "command": "python \"CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py\" --mode post 2>&1 || true",
        "timeout": 10
      }]
    }],
    "Stop": [{
      "hooks": [{
        "type": "command",
        "command": "python \"CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py\" --mode stop",
        "timeout": 180
      }]
    }]
  }
}
```

Add `.claude/state/` to `.gitignore`. Done. AI agents writing UCL_Asset JSON will now auto-validate at end of each turn.

See [Traditional Chinese version](../../zh-Hant/Workflows/Hook_Setup_Workflow.md) for full design rationale, failure modes, caveats, and migration notes.
