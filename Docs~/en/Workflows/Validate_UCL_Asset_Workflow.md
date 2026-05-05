---
title: Validate UCL_Asset Workflow
description: SOP for using Cmd_ValidateAssetFormat to validate UCL_Asset JSON files via round-trip + reference integrity check; the validation gate for any "create / modify UCL_Asset" workflow
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Validate UCL_Asset Workflow

> 📖 **Full documentation pending translation.** See the canonical Traditional Chinese version: [zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md](../../zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md).

## TL;DR

Mandatory validation step at the end of any workflow that creates or modifies a `UCL_Asset<T>` JSON file. Run `Cmd_ValidateAssetFormat` to detect:

- **Schema drift** — fields the loader doesn't recognise (silently dropped) or default values it filled in (likely missing in source)
- **Reference integrity** (with `checkRefs=N`) — sub-assets referenced but not on disk

```bash
python <UCL_CORE>/Tools~/AgentCommands/run_cmd.py run ValidateAssetFormat     --arg assetType=<C# Type> --arg assetId=<ID> --arg checkRefs=1     --output-file CardGame/AgentCommands/asset_format_check_<Type>_<ID>.md
```

verdict must be `PASS` (or `FormattingOnly` + apply `.fixed.json`) to consider the workflow complete.

## Related

- [Cmd_ValidateAssetFormat API](../API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md)
- See [Traditional Chinese version](../../zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md) for full diagnostic patterns and Localize migration recipes.
