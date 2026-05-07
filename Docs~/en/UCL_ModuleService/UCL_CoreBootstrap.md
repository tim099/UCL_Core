---
title: UCL_Core Asset Bootstrap Mechanism
description: Auto-populate default Assets when UCL_Core is installed into a fresh project — Templates~ source / version marker / UI edit mode
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
tags: [bootstrap, infra, module-system]
aliases: [bootstrap, defaults, templates, UCL_CoreAssetBootstrap]
---

# UCL_Core Asset Bootstrap

When UCL_Core is installed as a submodule into a fresh Unity project, users typically have to manually create a bunch of `.BuiltinModules/...` JSON Assets before the framework works. The Bootstrap mechanism automates this.

## Three layers

| Layer | Role | File |
|---|---|---|
| Template data | Source of truth for default Assets | `UCL_Core/Templates~/Assets/...` |
| Bootstrap controller | `[InitializeOnLoadMethod]` auto-fill missing + Tools menu | `UCL_Core/Editor/UCL_CoreAssetBootstrap.cs` |
| UI editing | Edit Templates~ directly via `UCL_ModuleServiceEditPage` | `UCL_ModuleEditType.Template` |

## 1. Templates~ Layout

```
UCL_Core/Templates~/
└── Assets/                         ← Mirrors project-root Assets/
    └── .BuiltinModules/...         ← 1:1 mirror of the destination
```

- The `~` suffix tells Unity to skip importing this folder, so it ships with UCL_Core but doesn't pollute the consumer's Asset tree.
- **No manifest file**: the bootstrap recursively walks `Assets/` — drop new files in and they're picked up.

### Adding new defaults

| Goal | Steps |
|---|---|
| Add one default file | (1) Drop it in `Templates~/Assets/...` (2) Bump `TemplatesContentVersion` const in `UCL_CoreAssetBootstrap.cs` |
| Add an entire module | Drag the folder into `Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/` and bump version |
| Edit via UI | Switch `EditType = Template` (see §3) |

## 2. Bootstrap Logic

```csharp
[InitializeOnLoadMethod]
static void OnEditorLoad() => EditorApplication.delayCall += AutoApplyIfNeeded;
```

Flow:
```
1. ReadMarker (ProjectSettings/UCL_CoreBootstrap.version)
   └─ marker >= TemplatesContentVersion ? early-return ★hot path
2. Recursively scan Templates~/Assets/, list files missing in dest
3. 0 pending → write marker, exit
4. applied == 0 (first install) → auto-apply, no dialog
5. applied > 0 (upgrade) → DisplayDialogComplex {Apply / Later / Don't ask}
```

The version constant `TemplatesContentVersion` is bumped manually by the maintainer when new templates are added or meaningfully changed.

### Tools menu

| Menu | Purpose |
|---|---|
| `Tools/UCL/Bootstrap/Apply Missing Defaults` | Manual top-up |
| `Tools/UCL/Bootstrap/Diff Against Templates` | Read-only diff report (Console) |
| `Tools/UCL/Bootstrap/Force Re-Apply (Overwrite!)` | Overwrites all matching files; confirm dialog |

## 3. Template Edit Mode

`UCL_ModuleEditType` gains a third value:

| EditType | Path | Use case |
|---|---|---|
| `Builtin` | `Application.dataPath/.BuiltinModules/...` | Dev-time source (ships into StreamingAssets at build) |
| `Runtime` | `Application.persistentDataPath/...` | Player-side writable (mods / customization) |
| **`Template`** | `<UCL_Core>/Templates~/Assets/.BuiltinModules/...` | **Edit the bootstrap default templates themselves** (Editor-only) |

Workflow:
1. Open `UCL_ModuleServiceEditPage`
2. EditType dropdown → `Template`
3. Edit Assets → Save → writes directly to `Templates~/...`
4. Commit → other consumers' bootstrap picks up changes after `TemplatesContentVersion` bump

### Editor-only

`UCL_AssetPath.GetPath(TemplateModules)` returns `string.Empty` in builds. `UCL_ModuleService` forces `Runtime` outside the Editor, so `Template` cannot be selected at runtime.

### Path resolution

`UCL_AssetPath.GetPath(TemplateModules)` uses `AssetDatabase.FindAssets("UCL_CoreEditor")` to locate the UCL_Core root, then appends `Templates~/Assets/.BuiltinModules`. Result is cached in a static field until domain reload.

## FAQ

**Q. I cloned UCL_Core into a new project — what do I do?**
A. Nothing. Open Unity. Bootstrap runs, fills missing files, writes the marker. Console logs `[UCL_Core Bootstrap] First-time install — applied N default asset(s).`

**Q. I deleted a default Asset. Will Bootstrap recreate it?**
A. No. As long as the marker is up-to-date, Bootstrap won't even scan. It only reactivates when `TemplatesContentVersion` is bumped.

**Q. I edited a default Asset. Will an upgrade overwrite it?**
A. No. Bootstrap is `create_if_missing` only. Force overwrites require `Tools/UCL/Bootstrap/Force Re-Apply` (with confirm dialog).

**Q. Adding a new default for everyone?**
A. Switch to `EditType = Template`, edit/add via UI (or drop file in `Templates~/Assets/...`), bump `TemplatesContentVersion`, commit. Consumers' bootstrap detects and prompts.

## Related files

- Controller: [`UCL_Core/Editor/UCL_CoreAssetBootstrap.cs`](../../Editor/UCL_CoreAssetBootstrap.cs)
- EditType enum: [`UCL_ModuleService.cs`](../../UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs)
- AssetType + path: [`UCL_AssetPath.cs`](../../UCL_Core_Scripts/AssetCore/UCL_AssetPath.cs) / [`UCL_Module.cs`](../../UCL_Core_Scripts/AssetCore/UCL_Module.cs)
- DevLog: [00011_2026-05-07_core-bootstrap-templates](../../DevLogs~/00011_2026-05-07_core-bootstrap-templates.md)
