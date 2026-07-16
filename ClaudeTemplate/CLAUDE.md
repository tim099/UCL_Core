# 專案規則

## 工作前置作業
先閱讀 `Docs/AI_READABILITY_GUIDELINES.md`，並在必要時閱讀其他相關文件。

## UCL_Core 共享規則

本專案使用 `UCL_Core` 為 git submodule，agent 工作規則由 UCL_Core 集中管理（口語指令處理 / CommandTable 查找 / AgentCommand 系統等）。透過 `@` 語法 inline 載入，修改 UCL_Core 端規則 → 下次 session 自動同步：
@UCL_Core/CLAUDE.md

## 路徑規範
專案內各種路徑一律使用相對路徑，且路徑必須完整。

## 🔎 DebugLog 查詢工具（2026-05-16 ship）

查 Editor `Assets/DebugLogs/Simulation_*.log` / `Errors_*.log` **不要**手動 grep — 用 `AgentCommands/Tools/debuglog_query.py`（5 ops）：

| 想知道什麼 | 跑哪個 |
|---|---|
| 最新 log 尾巴 | `tail --limit 30` |
| `[XXX]` daemon 死活 | `component --tag DiscordInbound`（自動 match kebab/snake 變體）|
| 純 ERROR + WARNING | `errors --since 10:30` |
| 跨 session regex 搜尋 | `search --pattern "discord\|inbound" --session all_recent_3` |
| Editor 整體健康度 | `summary`（自動點名缺席 daemon + 給 next-step）|

完整 SOP → [`docs/Workflows/DebugLog_Query_Workflow.md`](docs/Workflows/DebugLog_Query_Workflow.md)。

## 📣 Tavern Share（opt-in 機制）

本專案有多 agent 聊天酒館（ChatTavern，經 `run_cmd.py run Tavern` 派遣）。「完成工作單元後主動到 tavern 發 share」是 **opt-in 行為，預設不啟用**：

- **未 opt-in**（預設）：agent 不需要、也不應主動發 tavern share
- **想啟用**：在自己的 `CLAUDE.local.md` 加入 Task Completion → Tavern Share Hard Rule（share 格式 / 「算不算工作單元」邊界規範見 `ucl-chat-tavern` skill 的 Task Share 段）
- 無論是否 opt-in，使用者明確要求進酒館發言時照常執行（走 `ucl-chat-tavern` skill）


## 程式碼註解規範

### 區塊起始說明
在每個程式碼區塊的起始處，必須詳細說明該段落的職責、物理意義與數值影響。

### 參數註解格式（使用 `///` XML 文件註解）

```csharp
/// <summary>
/// 模擬運算所需的物理參數 (Shader Uniforms)。
/// 這些值將由 ClimateSim 自動上傳至 ComputeShader 的對應 Float 變數。
/// </summary>
public List<FloatValueAssetEntry> shaderParams = new();
```

### 區塊註解格式（使用 `//` 單行註解）

```csharp
// 區塊職責：繪製圖層的採樣數值資訊
// 物理意義：將 GPU 採樣回來的 0~1 顏色值轉換回實際物理量並顯示，包含主圖層與關聯的次要圖層。
// 數值影響：無修改，僅作視覺化呈現，方便開發者比對物理狀態。
```

### 禁止使用的註解方式
```csharp
/* 
不要用這種方式註解!!
*/
```
