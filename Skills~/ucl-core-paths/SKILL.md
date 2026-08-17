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

### Agent（你自己在 shell 裡）— resolve once per session

別假設 `UCL_Core/...`（**專案根通常沒有這個目錄**）。開 session 時解析一次、之後重用：

```bash
# 有序候選 → 第一個命中即用；找不到才 fallback glob（且排除 Library/）
for c in "Assets/Plugins/UCL_Core" "Assets/UCL/UCL_Core" "CardGame/Assets/UCL/UCL_Core" "UCL_Core"; do
  [ -f "$c/Tools~/AgentCommands/awakening.py" ] && UCL_CORE="$c" && break
done
[ -z "$UCL_CORE" ] && UCL_CORE=$(find . -path ./Library -prune -o   -path "*/Tools~/AgentCommands/awakening.py" -print 2>/dev/null | head -1 | sed 's|/Tools~.*||')
echo "UCL_CORE=$UCL_CORE"          # 之後一律用 "$UCL_CORE/Tools~/AgentCommands/<tool>.py"
```

**PowerShell 等價版**（Codex 端此 repo 走 PS；Sirius 2026-07-31 指出上面那段 bash 貼上會直接失敗）：

```powershell
# 有序候選 → 第一個命中即用
$UCL_CORE = $null
foreach ($c in @("Assets/Plugins/UCL_Core","Assets/UCL/UCL_Core","CardGame/Assets/UCL/UCL_Core","UCL_Core")) {
    if (Test-Path "$c/Tools~/AgentCommands/awakening.py") { $UCL_CORE = $c; break }
}
# fallback：受限 glob，排除 Library
if (-not $UCL_CORE) {
    $hit = Get-ChildItem -Recurse -Filter awakening.py -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -notmatch '[\\/]Library[\\/]' -and $_.FullName -match 'Tools~' } |
           Select-Object -First 1
    if ($hit) { $UCL_CORE = (Resolve-Path -Relative $hit.Directory.Parent.Parent.FullName) }
}
# 解析失敗必須明確報錯 —— 不可靜默 fallback 到別的檔
if (-not $UCL_CORE) { throw "UCL_Core 解析失敗：找不到 Tools~/AgentCommands/awakening.py" }
"UCL_CORE=$UCL_CORE"    # 之後一律用 "$UCL_CORE/Tools~/AgentCommands/<tool>.py"
```

> [!WARNING]
> PS 的 `-notmatch` 吃的是 **.NET regex** 不是萬用字元 —— 寫 `'\Library\'` 會被解析成非法跳脫
> `\L`（`Unrecognized escape sequence`），`Where-Object` 每筆都失敗 → **fallback 誤報找不到檔**。
> 必須寫成字元類 `'[\\/]Library[\\/]'`（同時涵蓋 `\` 與 `/` 分隔符）。
> （Sirius 2026-07-31 實跑抓到；summit 原版沒跑過就交件 —— 已寫未驗的 code 不算完成。）

> [!NOTE]
> 兩版**語意必須一致**（有序候選 → 受限 fallback → 失敗即報錯）。改一版記得改另一版 ——
> 這是本檔唯一的雙實作點，沒有機制綁，只能靠這行註記。

> [!WARNING]
> **不要用全 repo unique-glob 當主要手段**（Sirius 2026-07-31 提案討論結論）：
> 大專案慢，且 `Library/` 下可能有快取副本造成多重命中 → 「唯一命中」的前提會假。
> 有序候選命中率高又便宜；glob 只當最後 fallback，**且必須排除 `Library/`**。

> [!IMPORTANT]
> **解析失敗要明確報錯，不可靜默 fallback 到別的檔。**
> 最貴的失敗形式是「跑得起來、不報錯、但做的不是你要的事」——
> 名字相近的工具被當成儀式入口跑掉，狀態一個字都沒寫，而**沒有任何一層會喊**。
> 對策兩層：
> ① 本 skill 為唯一解析權威（不要每個 skill 各寫一套 preflight —— 那是同一語意 N 處實作）
> ② 呼叫端只需驗「解析出的檔存在」，不存在就停下報錯

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
