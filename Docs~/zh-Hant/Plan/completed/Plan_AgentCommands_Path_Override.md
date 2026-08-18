---
title: AgentCommands 路徑可配置化 — 控制台 UI + C#/Python 雙語同步
slug: agentcommands-path-override
status: draft (Round 1 — basecamp 大小姐, 待酒館 review)
created_at: 2026-05-28T09:45:00Z
created_by: claude-da-xiaojie (basecamp 大小姐)
task_ref: T-PATH-01
last_updated: 2026-05-28T09:45:00Z
location: UCL_Core (cross-project, 路徑解析是跨專案基礎設施); state pointer 檔由 consumer project 提供 (gitignored)
related:
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三層 bump 規範 (本 design ship 時用)
  - concept | UCL_RepoPath | C# 端唯一 git-root 解析點 (UCL_Core_Scripts/EditorCore/UCL_RepoPath.cs), 本 plan 在其上加 AgentCommands override 層
  - concept | tavern_paths.json | 既有 Python-only partial override (registry/session/letters), 本 plan 取代/統一之
  - concept | 控制台 | UCL_ControlPanelPage (2026-05-28 ship), 本 plan 的 UI 落在此頁第二塊 section
---

> **跨專案位置說明**: 本文檔位於 UCL_Core (submodule)。路徑解析 (`UCL_RepoPath`) 跟 Python 端 root 解析 (`run_cmd.py` / `awakening.py`) 都是跨專案共用基礎設施。
> Consumer project 提供:per-machine pointer 檔 (gitignored), 預設不存在 = 走原本 git-root 行為。

# AgentCommands 路徑可配置化 — Design Proposal v0.1

> Tim 派 task (2026-05-28):讓 `AgentCommands/` 的實體位置能在控制台 UI 改,可放到專案目錄外,per-machine 設定 (PlayerPrefs)。
> Enum 切換三模式;預設 = 目前路徑。本文檔是 **basecamp Round 1 draft**,待酒館同事 review。

---

## 🎯 出題背景

目前 `AgentCommands/`(聊天酒館 / 銀行 / persona 記憶 / 書籍 / affinity / treasury 全部資料)硬綁在 git-root 底下。Tim 要:
1. 能在**控制台**改這個路徑(目前無 UI)
2. 路徑可放**專案目錄外**
3. Enum 切換:**全域絕對路徑** vs **基於 Application.dataPath 的相對路徑(可往上層)**
4. 預設 = 目前路徑
5. 設定走 **PlayerPrefs**(每台機器可不同)

## 📍 現狀分析

### C# 端
- 單一解析點:`UCL_RepoPath.RepoRoot`(從 `Application.dataPath` 往上 walk 找 `.git` 目錄,cache 不重算)。
- `AgentCommandsDir => RepoRoot/AgentCommands`,但子系統多半直接 `Path.Combine(RepoRoot, "AgentCommands/XXX")` 而非走 `AgentCommandsDir`。
- ⚠️ **Treasury 例外**:`UCL_TreasuryPaths.GetTreasuryDir()` 用 `UnityProjectRoot/..` 不是 `RepoRoot` — 與其他子系統不一致,本 plan 順手修正。

### Python 端
- root 解析:`CLAUDE_PROJECT_DIR` env > cwd git-walk > script git-walk > cwd fallback。
- **既有 partial override**:`AgentCommands/_config/tavern_paths.json`(awakening.py `_resolve_data_path()` 讀),只覆寫 registry/session/letters 三路徑,沒涵蓋酒館主目錄 / treasury / books,且無 UI。

### 關鍵風險:C#↔Python 必須同源
整套是雙語 RPC:`run_cmd.py` 寫 `queue.json` → C# watcher 撿。**兩邊必須對同一個 AgentCommands 路徑**,否則系統分裂成兩份。PlayerPrefs 是 C# 專屬 → Python 讀不到 → **不能只改 C#**。

---

## 🏗 設計

### 1. Enum 三模式 (Tim 2026-05-28 確認)

```csharp
public enum AgentCommandsPathMode
{
    RepoRootDefault = 0,   // 預設:RepoRoot/AgentCommands (現行 git-walk 行為, 跨 layout 安全)
    GlobalAbsolute  = 1,   // 全域:使用者填的絕對路徑 (e.g. D:\Unity\EmblemOfValor\AgentCommands)
    ProjectRelative = 2,   // 專案相對:Application.dataPath + 相對路徑 (用 ../ 往上, e.g. ../AgentCommands = CardGame/AgentCommands)
}
```

- `RepoRootDefault`:不覆寫,等同現在(對扁平 / nested layout 都正確)。
- `GlobalAbsolute`:PlayerPrefs 存絕對路徑字串。
- `ProjectRelative`:PlayerPrefs 存相對 `Application.dataPath` 的字串;`Path.GetFullPath(Path.Combine(dataPath, rel))` 自動處理 `..` 往上層。
  - 範例:dataPath = `.../EmblemOfValor/CardGame/Assets`
    - `../AgentCommands` → `.../CardGame/AgentCommands`
    - `../../AgentCommands` → `.../EmblemOfValor/AgentCommands`(= 現在預設位置)

### 2. PlayerPrefs keys

| key | 型別 | 意義 |
|---|---|---|
| `UCL.AgentCommands.PathMode` | int | enum 值 (0/1/2) |
| `UCL.AgentCommands.AbsolutePath` | string | GlobalAbsolute 模式用 |
| `UCL.AgentCommands.RelativePath` | string | ProjectRelative 模式用 (相對 dataPath) |

### 3. C# 解析層

在 `UCL_RepoPath` 加 `AgentCommandsDir` 的 override 解析(取代現行 simple combine):

```
AgentCommandsDir 解析順序:
  1. 讀 PlayerPrefs PathMode
  2. RepoRootDefault → RepoRoot/AgentCommands (現行)
  3. GlobalAbsolute  → AbsolutePath (驗證非空 + 合法)
  4. ProjectRelative → GetFullPath(dataPath + RelativePath)
  5. 解析失敗 → fallback RepoRoot/AgentCommands + LogWarning
  → cache, 提供 ResetCache() 給控制台 Apply 後清快取
```

**所有子系統改走 `AgentCommandsDir`**(含 Treasury 修正),不再各自 `Path.Combine(RepoRoot, ...)`。

### 4. ★ C#↔Python 同步:gitignored pointer 檔 (Tim 2026-05-28 拍板 ✅)

**問題**:PlayerPrefs 只 C# 讀。Python 怎麼知道路徑改了?

**決議 (Tim 2026-05-28)**:採 gitignored pointer 檔。Tim 原話「路徑設定寫入到一個 gitignore 的檔案內,當相關設定改動時把計算出來的 root 路徑存入這個路徑檔案」。
→ C# 在控制台 Apply (設定改動) 時,把**解析出的絕對 AgentCommands 路徑**寫到 **固定 bootstrap 位置的 gitignored 檔**,Python 也讀它。

**Write-through 時機 (robustness 補充)**:
- 主要:控制台 Apply 設定改動時寫。
- 補強:C# 啟動解析時若 PlayerPrefs 有非預設設定但 pointer 檔缺失 / 內容不符 → 自動補寫 (防 pointer 檔被誤刪 / 新增 feature 前的舊 PlayerPrefs)。

- **Bootstrap anchor = git-root**(兩邊都已會算,不依賴 AgentCommands 路徑本身):
  - Pointer 檔:`<RepoRoot>/.agentcommands_root.local`(gitignored)
  - 內容:一行絕對路徑(C# 解析後寫入)
- **兩邊解析新流程**:
  1. git-walk 得 RepoRoot(不變,bootstrap)
  2. 檢查 `<RepoRoot>/.agentcommands_root.local`
     - 存在 → 用其內容當 AgentCommands root
     - 不存在 → 預設 `RepoRoot/AgentCommands`(= RepoRootDefault)
- **per-machine**:pointer 檔 gitignored,不同機器各自有 / 沒有。
- **PlayerPrefs 角色**:控制台 UI 的可編輯狀態(mode + 值);Apply 時據此**產生** pointer 檔。pointer 檔才是 C#/Python 共同消費的真相源。
- **取代既有 tavern_paths.json**:Python 端 `_resolve_data_path` 改成先吃 pointer 檔得 AgentCommands root,再 root/子路徑。registry/session/letters override 若仍要細粒度,可保留 tavern_paths.json 作第二層(open question)。

**為何用 pointer 檔而非直接讓 Python 讀 PlayerPrefs**:PlayerPrefs 在 Windows 是登錄檔、Mac 是 plist,Python 跨平台讀很髒;一個純文字 pointer 檔最乾淨、跨語言、跨平台。

### 5. 控制台 UI(UCL_ControlPanelPage 第二塊 section)

- Enum dropdown(三模式)
- mode = Global → 絕對路徑輸入框 + 「瀏覽」按鈕(EditorUtility.OpenFolderPanel)
- mode = ProjectRelative → 相對路徑輸入框(預設 `../../AgentCommands`)
- **即時預覽**解析後的絕對路徑 + 該路徑是否存在 / 是否已有資料
- 「套用」按鈕:寫 PlayerPrefs → 產生 pointer 檔 → ResetCache → 提示需重啟 Editor / domain reload 讓 daemon 重讀
- 安全提示:改路徑**不會自動搬移**舊資料(見 open questions)

---

## ✅ 已定案決策 (Tim 2026-05-28)

1. **同步機制**:gitignored pointer 檔。
2. **Bootstrap anchor**:**git-root** — `<RepoRoot>/.agentcommands_root.local`(兩語言都已會算 git-root,不新增解析邏輯)。
3. **舊資料搬移**:**不自動搬**;Phase 1 只做切換。獨立 **migrate 工具**留 **Phase 後續**(複製舊→新 + 衝突檢查)。
4. **Cache 失效**:Apply 時 `ResetCache` + **提示使用者重啟 Editor**(讓所有 daemon 乾淨重讀);不主動 domain reload。

## ✅ Round 2 review 決議 (apex-one 2026-05-28 + basecamp 回應)

apex-one 大師級 review 補強,basecamp 收兩推一:

- **Apply 安全護欄 (採納)**:
  - **Active Session Guard**:有 active work-session → **擋改路徑**(執行狀態會撕裂)。
  - **空目錄提示**:新路徑無資料 → 提示確認 / 用 migrate 工具。
- **tavern_paths.json (採納淘汰)**:Phase 3 pointer 檔成 root 唯一來源;殘留 tavern_paths.json → **log deprecation warning**(非無視)+ 過渡窗口後移除。細粒度覆寫需求改 CLI 參數臨時 override。
- **Q4 強制 domain reload (否決,維持提示重啟)**:apex-one 同意 — ResetCache 夠 Editor 端即時生效,Python daemon loop 自然重讀,強制 reload 太粗魯。

## ⚠️ 仍待 Tim 拍板

1. **Auto-Toggle-OFF vs Block**:apex-one 提案 Apply 時系統 ON 就自動關→寫→重啟;basecamp 推回,主張**改路徑時系統若 ON 直接擋下 + 提示「請先關系統」**(顯式 > 隱式,不靜默 mutate 使用者開關狀態)。**待 Tim 裁。**
2. **Treasury root 修正**:順手把 `UnityProjectRoot/..` 改成統一走 `AgentCommandsDir`,有沒有人依賴舊行為?(傾向:修,沒人該依賴那個 bug)

---

## 📋 實作步驟(草案,待 review 後定案)

- **Phase 0**:酒館 review + Tim 拍板同步機制(✅ 本輪完成)
- **Phase 1 (C#)**:`UCL_RepoPath.AgentCommandsDir` override 解析(讀 PlayerPrefs)+ ResetCache;pointer 檔讀寫(git-root anchor)+ write-through;Treasury 修正;所有子系統改走 `AgentCommandsDir`
- **Phase 2 (UI)**:控制台第二塊 section(dropdown + 輸入 + 瀏覽 + 即時預覽 + 套用 + 重啟提示 + 系統 ON 警告)
- **Phase 3 (Python)**:`run_cmd.py` / `awakening.py` / `library.py` / `tavern_paths.py` root 解析加讀 git-root pointer 檔;統一 tavern_paths.json
- **Phase 4**:測試(改路徑 → C# 寫 / Python 讀同步驗證 → RPC round-trip 不分裂)+ 文件
- **Phase 後續 (migrate 工具)**:獨立工具/按鈕複製舊路徑資料→新路徑 + 衝突檢查(Tim 2026-05-28 指定延後)

---

## 🧬 影響面

- **高 blast radius**:路徑解析是全系統地基,改錯 = 資料寫錯地方 / RPC 分裂。
- 必須 C# + Python 同輪 ship,不能只做一半(per Tim Q1 討論結論)。
- UCL_Core submodule 改動 → 三層 bump。
