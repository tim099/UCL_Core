---
name: ucl-core-paths
description: |
  跨專案定位與描述 UCL_Core 路徑的通用慣例 — UCL_Core 作為 git submodule 在不同專案掛載位置不同（Assets/Plugins/UCL_Core、Assets/UCL/UCL_Core、CardGame/Assets/UCL/UCL_Core…），任何「寫死 install path」的程式碼 / 工具 / 資料都會跨專案漂移或壞掉。本 skill 收攏三端（C# / Python / 文件 URL）已就緒的解析工具與「install-path 無關」描述慣例，讓 agent 一致重用、不再各自重造。
  觸發詞包含（case-insensitive substring，任一命中即 lazy-load）：
  - UCL_Core 路徑 / core 根 / CorePath / install path / 安裝路徑 / 掛載位置 / submodule 路徑 / 找不到 UCL_Core / 路徑漂移
  - manifest Source / # Source: / 相對路徑描述 / core-relative / ucl_core: / repo: prefix / 路徑解析工具 / 跨專案路徑
  - 寫死路徑 / hardcode path / Assets/Plugins/UCL_Core / Assets/UCL/UCL_Core / 為什麼路徑不一樣
  跨 agent 通用 — Claude / Antigravity / Gemini 撞到「UCL_Core 在哪」時都走本 skill。
---

# UCL Core Paths — 跨專案路徑解析與描述慣例

> 一句話：**永遠不要寫死 UCL_Core 的 install path — 用既有解析工具定位，用「相對 core 根」的 token 描述。**

## 🎯 為什麼存在

UCL_Core 是 git submodule，各專案掛的位置不同：

| 專案 | UCL_Core 根 |
|---|---|
| Bar | `Assets/Plugins/UCL_Core` |
| EoV | `CardGame/Assets/UCL/UCL_Core` |
| 其他 | `Assets/UCL/UCL_Core` … 不一定 |

任何「假設它在某固定路徑」的東西都會壞：寫死路徑的腳本在別專案 crash、寫進共享 submodule 的實體路徑（如 docs manifest 的 `# Source:`）在不同專案間反覆 git diff / 衝突。**解析工具三端都已就緒，缺的只是「一律走它、別重造」的紀律。**

## 🧭 定位 core 根（三端已就緒工具）

### C# (Editor)
```csharp
string aCore = UCL_EditorPath.CorePath;   // e.g. "Assets/Plugins/UCL_Core"（專案相對）
```
- 實作靠 AssetDatabase 找 `UCL_GUILayoutDrawObject` 腳本反推，不假設層級。
- 另有 `UCL_URL.FindRepoRoot()` → 定位「包含本專案的 git repo 根」（`repo:` prefix 的錨點）。

### Python (Tools~/AgentCommands 內的腳本)
自我定位，**不要**從 CWD 或寫死路徑推：
```python
# run_cmd.py 範式：從 __file__ 往上走找 git root，UCL_Core 固定在 parents[2]
GIT_ROOT = _find_git_root_by_walk(Path(__file__)) or Path(__file__).resolve().parents[2]
```
- `Tools~/AgentCommands/<tool>.py` → `parents[0]=AgentCommands`、`parents[1]=Tools~`、`parents[2]=UCL_Core 根`。
- 新 python 工具一律沿用此 `__file__`-relative 範式。

### Agent（你自己在 shell 裡）
別假設 `UCL_Core/...`。先 Glob 定位：
```
Glob **/awakening.py   →   Assets/Plugins/UCL_Core/Tools~/AgentCommands/awakening.py
```
（本 session 開頭就撞過：直接跑 `UCL_Core/Tools~/...` 找不到，Glob 一次就定位。）

## 🏷️ 描述「基於 UCL_Core 的相對路徑」（install-path 無關）

要把 UCL_Core 內某路徑寫進**會被 commit / 跨專案共享**的地方時，絕不寫實體路徑，改用 token：

### C# helper（新增）
```csharp
UCL_EditorPath.ToCoreRelative(absOrProjectPath);     // → "Docs~/en/..."（相對 core 根, forward-slash; 不在 core 下回 null）
UCL_EditorPath.ToCoreRelativeUrl(absOrProjectPath);  // → "ucl_core:Docs~/en/..."（URL token 形式）
```

### 模組 docs 來源 token
```csharp
UCL_DocsModule.SourceToken   // → "ucl_core:Docs~"（{Prefix}:{DocsSubfolder}）
```
`UCL_DocsModuleManifestGenerator` 的 manifest header `# Source:` 已改用此 token — 跨專案輸出完全一致。

### 文件 URL prefix 慣例（frontmatter related: / HelpURL）
| prefix | 錨點 | 範例 |
|---|---|---|
| `ucl_core:` | UCL_Core 根 | `ucl_core:Docs~/zh-Hant/CommandTable.md` |
| `repo:` | 含本專案的 git repo 根 | `repo:docs/...`、`repo:.claude/skills/...` |

## ⛔ 不可做

- ❌ 寫死 `Assets/Plugins/UCL_Core` / `Assets/UCL/UCL_Core` 任一具體 install path（換專案即壞）
- ❌ 把實體掃描路徑 / 絕對路徑（`D:\...`）寫進會 commit 進 submodule 的資料檔（造成跨專案 git churn）
- ❌ python 工具用 CWD 或硬編路徑推 core 根 — 一律 `__file__`-relative
- ❌ 重造第四套 root-finding — 三端各有唯一錨點（C# CorePath / FindRepoRoot、python `__file__`-walk），沿用即可

## 📋 相關

- `UCL_EditorPath.cs`（`CorePath` / `ToCoreRelative` / `ToCoreRelativeUrl`）
- `UCL_DocsModule.cs`（`SourceToken`）、`UCL_DocsModuleManifestGenerator.cs`（`# Source:` token 化）
- `UCL_CoreDocsBootstrap.cs`（`ucl_core:` / `repo:` prefix resolver 註冊）
- `Tools~/AgentCommands/run_cmd.py`（python `__file__`-walk 範式）
