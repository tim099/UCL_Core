---
title: Unity Compile Error 排查工作流程
description: 用 UCL_CompileErrorTracker 寫的 .compile_status.json + check_compile.py 工具，讓 agent 即使在「Cmd 系統因 compile error 也載不進來」的雞生蛋情境下，也能讀到完整錯誤清單；含 dedupe / log fallback / session 邊界偵測 / 4 步排查 SOP / 8 大常見錯誤類型對照 / 實戰 case study
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [編譯錯誤, compile error, CompileError, CS0103, CS0117, CS1503, CS0246, asmdef, assembly, 排查, debug, troubleshooting]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity Compile Error 排查工作流程

> [!IMPORTANT]
> **解決什麼問題**：agent（或人類）改了 .cs 檔之後 Unity 編譯失敗，Cmd 系統會跟著掛掉（assembly 載不進來 → handler 不在 Registry → Cmd 無法觸發），於是「最需要查錯誤的時候」反而沒辦法用任何 Cmd。
>
> **本工作流的核心工具是 standalone Python 腳本** [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py)，**完全不依賴 Cmd 系統**，能在任何狀態下印出 dedup 過的錯誤清單。

---

## 0. TL;DR — Agent 速查卡

```bash
# 預設指令（healthy 與 broken 狀態都跑這條）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only

# 若印 ".compile_status.json not found" → fallback 解 Editor.log
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log

# 等下次 compile 結束才回報（改完檔之後用）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60
```

> `<UCL_Core>` 視專案而定，EOV 為 `CardGame/Assets/UCL/UCL_Core`。

| Exit code | 意義 |
|---|---|
| `0` | 編譯成功，0 errors |
| `2` | 有 error |
| `3` | 找不到 `.compile_status.json`（Tracker 沒運作）|

> [!WARNING]
> **重要心智模型**：fallback 路徑（`--fallback-log`）讀的是 Editor.log tail，**內含多次 compile attempt 的累積訊息**。即使 dedupe 過，stale 錯誤可能與最新錯誤混雜。永遠優先信任 `.compile_status.json`（Tracker 寫的，**只記最新一次** compile 結果）。

> [!NOTE]
> **編譯通過 ≠ 完事**。本檔處理的是**編譯期錯誤**；改完 code 後跑遊戲還可能出 **runtime 錯誤**（NullReferenceException / MissingReferenceException 等）。runtime 錯**不在** `.compile_status.json`，要另外讀專案的 runtime log。
>
> EOV 專案：[`docs/Workflows/RuntimeError_Diagnose_Workflow.md`](docs/Workflows/RuntimeError_Diagnose_Workflow.md) → 讀 `CardGame/Assets/DebugLogs/Errors_latest.log`。

---

## 1. 兩條資料來源

| # | 來源 | 路徑 | 何時能用 | 新鮮度 |
|---|---|---|:-:|---|
| ⭐ A | `.compile_status.json` | `<gitRoot>/AgentCommands/.compile_status.json` | Tracker 已成功 load 過 | **單次編譯結果** — 最新 |
| B | Editor.log fallback | OS 預設位置（見下） | 永遠都在（Unity 一直在寫）| 累積多次重試的 **stale** 訊息，靠 session 邊界偵測縮窗 |

**首選 A**：[`UCL_CompileErrorTracker`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_CompileErrorTracker.cs) 訂閱 `CompilationPipeline.assemblyCompilationFinished` 後寫入；schema 固定、無噪音、僅含本次 compile 結果。

**fallback B**：Editor.log 隨時都在累積，必須做兩件事才能 mostly 對：
1. **Session 邊界偵測**：找最後一個「真正 compile」的 `Asset Pipeline Refresh ... Total: N seconds`（後面跟著 `CompileScripts:` sub-line）標記，視窗從前一個同類標記到此 marker
2. **Dedupe** by (type, file, line, message)：同次 compile 內 Unity 會重複印多次

兩個動作都做了之後仍可能誤抓 stale 錯誤 — fallback 是「沒辦法時的退路」，不是主路徑。

### 1.1 Editor.log 路徑

| OS | 路徑 |
|---|---|
| Windows | `%LOCALAPPDATA%\Unity\Editor\Editor.log` |
| macOS | `~/Library/Logs/Unity/Editor.log` |
| Linux | `~/.config/unity3d/Editor.log` |

---

## 2. 4 步排查 SOP

### Step 1 — 跑 check_compile.py 看摘要

**輸出解讀**：
- `**Errors: N**` — 編譯錯誤數
- `Distinct after dedupe: M` — 去重後的獨立錯誤數（**重點看這個**）
- `⚠ Source: Editor.log fallback` — 看到此警示就知道是 stale 來源，需驗證

### Step 2 — Stale vs Fresh：用「檔案實際內容」交叉驗證

> [!WARNING]
> **Fallback 模式常見陷阱**：你看到 5 個錯誤，但實際只有 1 個 fresh、其他 4 個都已修好。**永遠用「open 檔案讀對應行」交叉驗證**：

```text
# 工具報 line 65 有 'UCL_CompileErrorTracker not found'
# 實際打開檔案讀第 65 行 — 看到是 `UCL_CompileErrorTracker.GetOutputPath()`
# 如果同 assembly 內 Tracker 確實存在 → 這是 stale，已經修好了
# Editor 還沒重 compile 而已
```

**Fresh error 的特徵**：
- 對應行的程式碼跟錯誤訊息描述吻合
- 對應 type 在預期 namespace / asmdef 內**找不到**

**Stale error 的特徵**：
- 對應行已被改寫（連 line number 都對不上）
- 訊息提到的 type 在當前程式碼中其實已正確 import / 定義

### Step 3 — 找對 root cause（不要被 cascade 騙）

看 dedupe 後的錯誤清單，**先處理最早 / 最根本的錯誤**：

| 症狀 | 通常根因 |
|---|---|
| `CS0103: name 'X' does not exist` 連帶一堆 X 相關錯誤 | X 自己編譯失敗 → 找 X 檔案的真正錯誤 |
| 跨 assembly 找不到型別 | asmdef references 缺 / 反向 dependency |
| `CS1503: cannot convert from ...` 一堆 lambda | 整體推斷鏈斷掉 → 通常上游有 syntax error |
| `CS0117: type does not contain definition for X` | 對方 type 編譯失敗 → 不是真的缺 member |
| 一改檔就 5+ 錯但人工檢查只有 1 個真錯 | fallback 模式 stale 訊息混入 — 等 Tracker 啟用 |

**判別技巧**：dedupe 後若同檔多錯誤集中在 1-3 行附近 → 八成是 syntax error（漏 `;`、漏 `}`）；分散在不同行 → 通常是 type/namespace 問題。

### Step 4 — 修一個錯誤後**強制觸發 recompile** + 重複

光改檔有時 Unity 不會重編（資產 cache）。三種強制方式：

1. **手動 focus Unity 視窗** — 觸發 `AssetDatabase.Refresh`，最常用且最可靠
2. **改一個既有 .cs 加註解** — 強制 mtime 更新
3. **`Tools/UCL/Agent Commands/Run Pending`**（手動）— 順便看 Console 訊息

> [!IMPORTANT]
> **Agent 流程必備動作**：寫完一輪 .cs 修改後，**明確提示使用者 focus Unity** 並等待右下角 spinner 跑完。否則 Editor 還是用舊 assembly，後面查錯都白費。

重複 Step 1~4 直到 `**Errors: 0**`。成功編譯後 `.compile_status.json` 會出現 — 之後查錯就不用再 fallback。

---

## 3. 8 大常見錯誤類型對照（含實戰修法）

| # | Error code | 範例訊息 | 典型修法 |
|---|---|---|---|
| 1 | **CS0103** | `name 'X' does not exist in the current context` | 加 `using` / fully qualify / 該 type 自己有錯先修 |
| 2 | **CS0117** | `type 'A' does not contain a definition for 'B'` | 對方 class 裡確認 member 拼字 / 同一 assembly 內 internal 可見 / 跨 assembly 可能要改 public |
| 3 | **CS0234** | `namespace 'X' does not contain 'Y'` | asmdef references 缺 / 用了沒安裝的 package |
| 4 | **CS1503** | `cannot convert from '(string, lambda expression)' to '(string label, Action onClick)'` | tuple 內 lambda 推斷成 `Func<T>` 而非 `Action`；**最可靠修法是把 tuple 拆成平鋪參數** `(string label, Action onClick)`，不要繼續凹 tuple |
| 5 | **CS0246** | `type or namespace 'X' could not be found` | 缺 `using` / 跨 asmdef references / package 未安裝 |
| 6 | **CS0029 / CS0266** | `cannot implicitly convert 'A' to 'B'` | 加顯式 cast 或修 type |
| 7 | **CS1061** | `does not contain a definition for 'X' and no accessible extension method` | 該 type 還沒 compile / using 沒加 / 套件版本不對 |
| 8 | **CS0535** | `does not implement interface member 'X'` | 介面新加方法後子類沒同步 |

---

## 4. asmdef 跨界排錯（最常見的非語法錯誤）

UCL_Core 至少分兩個 asmdef：

```
UCL_Core_Scripts/UCL_Core.asmdef          ← Editor + Runtime（含 UCL_AgentCommands / UCL_EditorMenuPages）
Editor/UCL_CoreEditor.asmdef              ← Editor only，references UCL_Core
```

**單向依賴**：`UCL_CoreEditor → UCL_Core`。
- ✅ `UCL_CoreEditor` 可看到 `UCL_Core` 的 type
- ❌ `UCL_Core` 看不到 `UCL_CoreEditor` 的 type（如 `UCL_MenuWindow`）

**踩雷症狀**：
```
CS0103: The name 'UCL_MenuWindow' does not exist in the current context
```

**修法**（依推薦順序）：
1. **把 type 搬進 UCL_Core asm**：把檔案從 `UCL_Core/Editor/` 移到 `UCL_Core/UCL_Core_Scripts/...` + 加 `#if UNITY_EDITOR` 包住
2. **用 `EditorApplication.ExecuteMenuItem("...")`** 透過 menu item 反向觸發（如 `[MenuItem("UCL/Menu")]` 註冊的 `UCL_MenuWindow.ShowMenu`）
3. **反射** `Type.GetType("...")` + `MethodInfo.Invoke()`（最後手段）

---

## 5. Namespace 陷阱（同 asm 內也會踩）

> [!IMPORTANT]
> **同 assembly 不代表同 namespace 自動可見**。C# 名稱解析從當前 namespace 往外層找，但**不會跨同層 sibling namespace**。

實例：寫某個頁面 class 在 `UCL.Core.EditorLib.Page` namespace 內，呼叫另一個頁面 class — 後者其實在 `UCL.Core.Page` 而**不**在 `UCL.Core.EditorLib.Page`。從前者看後者要：

```csharp
using UCL.Core.Page;          // 加這行
// 或 fully qualify
UCL.Core.Page.UCL_ModuleServiceEditPage.Create();
```

**判別技巧**：看到 CS0103 在「同 asm 應該能看到」的 type 上失效，先確認對方 type 的 `namespace` 行是否真如預期。

---

## 6. Tracker 自身的 chicken-and-egg

> [!IMPORTANT]
> Tracker 是 `[InitializeOnLoad]` static class，**只在 domain reload 時跑 ctor**。Domain reload 又只在「**前一次編譯成功**」時觸發。所以：
>
> - Editor 啟動 → 編譯 → 成功 → reload → Tracker 訂閱 events → 後續編譯被捕捉到
> - Editor 啟動 → 編譯 → **失敗** → 沒 reload → Tracker 不訂閱 → JSON 寫不出來

**首次 Editor 啟動就帶 compile error 的情境**：`.compile_status.json` 永遠不會出現，必須走 `--fallback-log` 解 Editor.log。修一輪錯誤後重新 compile 成功 → 之後 Tracker 啟用，回到 happy path。

Tracker 的 ctor 內有寫一份「placeholder」JSON 給 first-load 偵錯使用，但 ctor 本身要能執行 = 至少 `UCL_Core` asm 要能編譯成功。

---

## 7. 案例研究：實際排錯經過

**情境**：寫了 5 個新 .cs 檔（UCL_WelcomePage / UCL_WelcomeAutoOpen / Cmd_GetCompileErrors / UCL_CompileErrorTracker / 修改 UCL_EditorMenuPage），第一次 compile 失敗。

**進程**：

| 輪 | check_compile.py 報告 | 實際 fresh error | 修法 |
|:-:|---|---|---|
| 1 | 4 distinct（line 65, 73, 149, 151）| 全部 4 個都 fresh | (a) Tracker 跨 asmdef → 搬進 UCL_Core asm<br/>(b) `UCL_MenuWindow` 跨 asmdef → 改用 `EditorApplication.ExecuteMenuItem`<br/>(c) tuple lambda CS1503 → 改成平鋪參數 |
| 2 | 4 distinct（一樣的 4 個） | **0 個** — 全部 stale，Editor 還沒重編 | 提示使用者 focus Unity |
| 3 | 5 distinct（多了 line 152） | **1 個** fresh（line 152）| `UCL_ModuleServiceEditPage` 在 `UCL.Core.Page` ≠ 當前 `UCL.Core.EditorLib.Page` → 加 `using UCL.Core.Page;` |
| 4 | 0 errors | — | ✅ Clean，Tracker 啟用，`.compile_status.json` 出現 |

**核心教訓**：
- **不要看到 N 個錯誤就以為要修 N 個** — fallback 模式 stale 訊息會虛報。永遠先用「實際打開檔案對行」交叉驗證
- **C# 編譯器對 tuple 內 lambda 推斷有限** — 簽名改成平鋪 `(string, Action)` 比硬撐 `(string, Action) tuple` 穩
- **同 asm 不代表同 namespace** — 看 CS0103 先確認 namespace 不是直覺以為的那個

---

## 8. 給工具維護者的 Note

### 8.1 Windows stdout 編碼

`check_compile.py` 報告含 emoji（`🔧`/`❌`/`⚠`/`✅`），Windows 預設 stdout 用 cp950 / cp1252 codepage，會炸出 `UnicodeEncodeError`。本工具開頭：

```python
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
```

新增其他類似的 Python 工具時記得照做（或改用純 ASCII 字元）。

### 8.2 Editor.log session 邊界偵測

工具的 fallback 演算法：
1. 從 tail 1MB 內找所有「`Asset Pipeline Refresh ... Total: N seconds`」line
2. **過濾**：只保留後 5 行內有「`CompileScripts:`」sub-line 的（過濾掉純 asset refresh 沒 compile 的 entry）
3. 視窗 = `[倒數第二個過濾後 marker, 倒數第一個過濾後 marker]`
4. 若只有一個過濾後 marker → fallback 取「該 marker 前 200 行」

**已知限制**：若 Editor 一直連續 fail 3+ 次都沒成功 compile，多次 attempt 的 errors 仍會擠進 200 行 lookback 視窗 → 出現 stale 干擾。修一次後成功 compile，Tracker 接手就乾淨了。

---

## 9. 給 Agent 的快速指引（複習）

**典型循環**：
1. 改完一批 .cs
2. 提示使用者 focus Unity 觸發 recompile
3. 跑 `check_compile.py` → 看 dedupe 後的錯誤
4. **交叉驗證**每筆錯誤是 fresh 或 stale（打開檔案對行）
5. 修真錯 → 回到步驟 1 直到 `Errors: 0`

---

## 10. 相關文件

- [`UCL_CompileErrorTracker.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_CompileErrorTracker.cs) — Tracker 本體
- [`Cmd_GetCompileErrors.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_GetCompileErrors.cs) — Cmd 包裝（healthy 狀態才用）
- [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py) — Standalone Python 工具（**主路徑**）
- [Workflows/Create_Cmd_Workflow](Create_Cmd_Workflow.md) — 新增 Cmd SOP
- [API/UCL_AgentCommand/UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Agent Command 系統架構

---

## 其他語系

- 🇬🇧 [English](../../en/Workflows/CompileError_Diagnose_Workflow.md)
- 🇯🇵 [日本語](../../ja/Workflows/CompileError_Diagnose_Workflow.md)
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/CompileError_Diagnose_Workflow.md)
- 🇹🇼 繁體中文（本檔）
