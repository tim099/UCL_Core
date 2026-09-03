---
title: UCL Agent Command 系統整體架構
description: AI agent 與 Unity Editor 的跨 process 指令系統 — 自動發現 / 反射註冊 / async 執行 / 多種觸發方式（UI / queue.json / Python / batchmode）
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-13 (§4.3 result 檔新增 outputs 欄 —— 回傳檔路徑隨 verdict 印出，caller 不再靠 skill 背路徑)
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🤖 UCL Agent Command 系統整體架構

> **一句話**：讓 AI agent（在 Unity 外）對 Unity Editor 內的工具呼叫 RPC — agent 寫 `queue.json`、人類（或 batchmode）按 Run Pending、async runner 確認模組系統就緒後依序分發到對應 Handler。

---

## 1. 系統定位

UCL Agent Command 解決的問題：**AI agent 沒有 Unity 環境**，但需要呼叫 Unity 內的工具（編輯器頁面 / 模組系統 / Asset 資料庫）才能完成許多開發工作（解析 asset 依賴、匯出 markdown 目錄、執行批次資產處理⋯）。

**設計取捨**：
- ✅ 跨 process 通訊用**檔案系統**（`queue.json`）— 最簡單、可審計、可離線編輯
- ✅ Handler 由**反射自動發現** — 新增指令零樣板（一個 class 即可）
- ✅ Async 執行 + 等待模組系統就緒 — 避免 race condition
- ❌ 不做 socket / IPC — 部署複雜度爆炸
- ❌ 不做 schedule（cron）— 重複任務改用 Repeatable + 使用者觸發

---

## 2. 元件圖

```
┌─────────────────────────────────────────────────────────────────────┐
│  AI Agent (Claude / GPT / human)                                    │
│      │                                                              │
│      │ 1) 寫指令到 queue                                              │
│      ↓                                                              │
│   AgentCommands/queues/<persona>/queue.json                         │
│      │                                                              │
│      ↓ 2) 觸發（4 種方式）                                            │
└──────┬──────────────────────────────────────────────────────────────┘
       │
       │   ┌──────────────────────────────────────────────────┐
       ├──→│ a) UCL_AgentCommandsPage（Editor IMGUI）          │
       ├──→│ b) Tools/UCL/Agent Commands/Run Pending（Menu）    │
       ├──→│ c) Tools~/AgentCommands/run_cmd.py（Python CLI）   │
       └──→│ d) Unity batchmode -executeMethod（headless CI）  │
           └──────────────────────────────────────────────────┘
                                │
                                ↓
                   UCL_AgentCommandRunner.Menu_RunPending()
                                │
                                │ 3) await UCL_ModuleService.WaitUntilInitialized
                                ↓
                   依序處理 queue.Commands
                                │
                                │ 4) 依 Type 查 Registry
                                ↓
                   UCL_AgentCommandRegistry.Get(type)
                                │
                                │ 5) 呼叫對應 Handler
                                ↓
                   handler.ExecuteAsync(args, token)
                                │
                                │ 6) 寫回 queue（OneShot 移除 / Repeatable RunCount++ / 失敗記錯誤）
                                ↓
                   AgentCommands/queues/<persona>/queue.json （更新後）
```

---

## 3. 核心類別速查

| 類別 | 路徑 | 角色 |
|---|---|---|
| `UCL_AgentCommand` | `UCL_AgentCommand.cs` | 單一指令的資料模型（`Id` / `Type` / `Mode` / `Args` / `LastRunResult` / 等）— 對應 queue.json 一筆 |
| `UCL_AgentCommandQueue` | `UCL_AgentCommandQueue.cs` | queue.json 的讀寫 helper（`Load(id)` / `Save(data, id)` / `GetQueuePath(id)` / `GetDeclaredPersona(id)` — id 形狀為 `<persona>` 或 `<persona>/<lane>`，null → anonymous，見 §8.1）|
| `UCL_AgentCommandRunner` | `UCL_AgentCommandRunner.cs` | 主執行器；含 `[MenuItem] Tools/UCL/Agent Commands/Run Pending` 入口 |
| `UCL_AgentCommandRegistry` | `UCL_AgentCommandRegistry.cs` | 反射發現所有 `UCL_AgentCommandHandlerBase` 子類；`Get(type)` / `ListHandlers()` |
| `UCL_AgentCommandHandlerBase` | `UCL_AgentCommandHandlerBase.cs` | **新增指令的擴充點** — 抽象基底，子類覆寫 `CommandType` + `ExecuteAsync()` |
| `UCL_AgentCommandsPage` | `UCL_EditorMenuPages/UCL_AgentCommandsPage.cs` | Editor IMGUI 頁面（人類友善 UI）|

---

## 4. 指令生命週期

### 4.1 OneShot（預設）

```
[1] Agent 寫進 queue → Executed=false, LastRunResult=null
[2] Run Pending 觸發 → runner 跑 ExecuteAsync
[3a] 成功 → 寫 _cmd_results/<id>.json (Success) → 從 queue 移除
[3b] 失敗 → 寫 _cmd_errors/<id>.md + _cmd_results/<id>.json (Failed) → 也從 queue 移除（§4.3）
```

### 4.2 Repeatable

```
[1] Agent 寫進 queue → RunCount=0
[2] Run Pending 觸發 → 跑一次
[3a] 成功 → RunCount++ ，留在 queue 裡，LastRunResult="Success"
[3b] 失敗 → 錯誤報告與 verdict 檔照寫，但 Repeatable 留在 queue（語意本來就是反覆跑）
[4] 下次 Run Pending → 又跑一次（RunCount++）
```

### 4.3 失敗的 OneShot 自動出隊（2026-08-07 起；成對改）

**舊行為**（失敗留在 queue）已廢除 —— 殘留的失敗 OneShot 會被之後**每一批重跑**
（副作用重放：Tavern post 重發、轉帳重轉），掛住型失敗還會讓每批多等一次 per-cmd
timeout → `ensure_idle` 60 秒放棄 → 「後續指令無法執行」。

現行為（Runner 端 / run_cmd 端**成對**，缺一邊都是災難）：

| 端 | 行為 |
|---|---|
| Editor Runner | 失敗（含 unknown type）→ 寫 `_cmd_errors/<id>.md`（stack 全文）＋ `_cmd_results/<id>.json`（機器可讀 verdict）→ **OneShot 直接出隊**；Repeatable 照舊留著（語意本來就是反覆跑） |
| 等待端（`senate ucmd run`） | cmd 從 queue 消失 → **先讀 `_cmd_results/<id>.json` 判定**（Failed → 印錯誤簡報＋詳細錯誤檔路徑，exit 2）；無 result 檔才 fallback 舊推論（舊版 Editor）。⇒ 判定成功時印的是 `✓ Cmd completed → Success（result 檔判定，非推論）` —— **括號那句就是「走哪條路判的」的讀數** |

成功也寫 result 檔 —— 失敗會出隊之後「消失」同時可能是成功或失敗，
「沒有檔」不能再當判定依據。result 檔 3 天自動清（純 handshake）；
`_cmd_errors/` 永久保留（回溯用，已 gitignore）。

**`outputs` 欄（2026-08-13 起）**：handler 落回傳檔時經
`UCL_AgentCommandRunner.ReportOutputFile(path)` 回報，Runner 寫進 result 檔的
`outputs`（string 陣列，成功與失敗都寫 —— blocked 也是先落 payload 再 throw，
出口清單就在那個檔裡）；`run_cmd.py` 隨 verdict 一起印 `📄 回傳檔：<路徑>`。
動機：回傳檔位置（如 letters root）跨專案會漂，caller 靠 skill 文字背路徑會讀錯
（wake#48 血證）——路徑由落檔的那隻手回報，天生不漂。舊版 result 檔沒有此欄，
python 端靜默跳過。目前接上的 handler：Cmd_GoodMorning / Cmd_GoodNight（`WritePayload`）。

失敗後要重試：讀 `_cmd_errors/<id>.md` 修好參數，**重新 submit 一筆** ——
不再有「改 queue 裡那筆繼續嘗試」的模式。

---

## 5. 自動發現 Handler

`UCL_AgentCommandRegistry` 的 static ctor 透過 `AssemblyExtensions.GetAllSubclass(typeof(UCL_AgentCommandHandlerBase))` 掃描全部 assembly，反射建立每個非抽象子類的 instance。**新增指令零樣板** — 寫一個 class 繼承基底就會被自動註冊。

```csharp
public class Cmd_MyCustom : UCL_AgentCommandHandlerBase
{
    public override string CommandType => "MyCustom";
    public override string ShortDescription => "Description shown in UI dropdown.";
    public override string ArgsSchema => "key1=描述\nkey2=描述";
    public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MyCustom.md";

    public override async UniTask ExecuteAsync(Dictionary<string,string> args, CancellationToken token)
    {
        // ... 你的邏輯
        await UniTask.CompletedTask;
    }
}
```

**重要**：
- `CommandType` 必須唯一（相同會 LogError 並覆蓋既有）
- `CommandType` 大小寫不敏感（queue.json 寫成 `"myCustom"` 也會 match）
- 撞名 → registry 會 LogError + 用後寫入者覆蓋前者

### 5.1 🔄 新增／修改 Cmd 後請同步 schema（Tim 2026-07-29 拍板）

Python client (`run_cmd.py`) 在送出前會做**參數預檢**（少帶必填、別名歸一），依據是
`<RepoRoot>/AgentCommands/commands_schema.json` —— 那份產物由 C# 反射 handler 的
`ArgsSpec` 生成。**改動 Cmd 之後請同步一次**，三個入口任選：

| 入口 | 怎麼做 |
|---|---|
| 控制台 | `UCL_ControlPanelPage` → **🧾 Cmd 後台** → 開啟管理頁 → 「重新生成 commands_schema.json」 |
| Cmd（給 agent） | `senate ucmd run ExportCmdSchema` |
| 自動 | 編譯完成時檢查，**每台機器每天最多自動觸發一次**（節流時間戳存 EditorPrefs，不入 git） |

三者呼叫同一個 `UCL_CmdSchemaExporter.Export()`，產出逐字相同；內容未變則不寫檔。

**想讓 client 幫忙擋參數 → 覆寫 `ArgsSpec`**（`UCL_CmdArgsSpec`；有子 op 的填 `Ops`）。
不覆寫完全合法，意思是「這個 Cmd 不需要 client 預檢」，只是少一層提早回饋。

> ⚠ **忘了同步不會壞掉**：Python 端比對 `source_hash`（**內容雜湊，不是檔案時間** —— git 不存 mtime，
> clone 後全部檔案時間都是當下，用 mtime 判會在最該生效的場景擲骰子）。
> 不符 → **參數預檢自動降級為不擋**，把判斷權交還 Editor。
> 未知 op 同理一律放行 —— 便利性功能不該有能力擋掉正確性
> （血證 2026-07-29：`create_trpg_room` 在 C# 完整實作，卻因 Python 手抄表漏抄而被擋死）。
>
> 完整設計：[`Plan_AgentCmd_Schema_Reflection_Export`](../../Plan/Plan_AgentCmd_Schema_Reflection_Export.md)

---

## 6. 內建指令（持續擴充中）

> 完整最新清單請走 [`Cmd_ExportCommandCatalog`](Cmd_ExportCommandCatalog.md) 自動產出 → `AgentCommands/commands_catalog.md`

| Cmd Type | Mode | 用途 | 來源 |
|---|---|---|---|
| `DebugLog` | Repeatable | 印 `Args["msg"]` 到 Console（連線測試 / 範例）| UCL_Core |
| `ResolveAssetReferences` ⭐ | OneShot | 批次解析 UCL_Asset 連動鏈（BFS + 反射 + maxDepth + 去重）| UCL_Core |
| `ExportCommandCatalog` ⭐ | OneShot | 匯出當前所有已註冊 Handler 到 markdown 目錄 | UCL_Core |
| `ExportEquipmentNotes` | OneShot | 匯出 Equipment Note 到 docs/Catalogs/ | RCG (專案層) |
| `ExportCardNotes` | OneShot | 匯出 Card Note | RCG |
| `ExportItemNotes` | OneShot | 匯出 Item Note | RCG |
| `ExportAllNotes` | OneShot | 上面三個一次跑完 | RCG |
| `Ping` | Repeatable | 印 `Args["msg"]`（與 DebugLog 平行的 RCG 端範例）| RCG |

**架構分層**：
- **UCL_Core 層**（本文件覆蓋範圍）— 框架本身 + 通用指令（DebugLog / ResolveAssetReferences / ExportCommandCatalog）
- **RCG 專案層** — 專案特定指令（Export*Notes / Ping）住在 `Assets/Scripts/RCG_Scripts/RCG_AgentCommands/`

---

## 7. 觸發方式對照

| # | 方式 | 自動化 | 適用 | 啟動延遲 |
|---|---|---|---|---|
| 1 | `UCL_AgentCommandsPage` UI 內 **Run Pending** 按鈕 | 半 | 人類 | 即時 |
| 2 | `Tools/UCL/Agent Commands/Run Pending` Editor 選單 | 半 | 人類 | 即時 |
| 3 | 直接編輯 `queue.json` + 上面任一觸發 | 半 | Agent + 人類點按鈕 | 即時 |
| 4 | **Python 包裝器** [`Tools~/AgentCommands/run_cmd.py`](../../../../Tools~/AgentCommands/run_cmd.py) ⭐ | 半（Editor 必須開）| **Agent CLI 推薦** | 即時 |
| 5 | **Unity Batchmode** `-batchmode -executeMethod` | **全** | CI / 排程 | ~30 秒（啟 Unity）|

### Python 包裝器範例

```bash
# submit + wait（適合 Agent CLI）
senate ucmd run ResolveAssetReferences \
    --arg assetType=RCG_StoryData --arg assetIds=AbandonedTemple \
    --arg maxDepth=3 --arg format=md \
    --output-file CardGame/AgentCommands/asset_refs_AbandonedTemple.md

# 列 queue
senate ucmd status

# 產生／更新指令目錄（產物：<資料根>/commands_catalog.md）
senate ucmd run ExportCommandCatalog
```

### Unity Batchmode 範例（CI / 排程）

```powershell
"C:\Program Files\Unity\Hub\Editor\6000.0.60f1\Editor\Unity.exe" `
    -batchmode -nographics `
    -projectPath "D:\Unity\EmblemOfValor" `
    -executeMethod UCL.Core.EditorLib.AgentCommands.UCL_AgentCommandRunner.Menu_RunPending `
    -quit -logFile -
```

---

## 8. queue.json Schema

```json
{
  "Commands": [
    {
      "Id": "yyyyMMdd-HHmmss-uuid-typeslug",
      "Type": "<CommandType>",
      "Mode": "OneShot | Repeatable",
      "Executed": false,
      "Args": { "key": "value", ... },
      "CreatedAt": "ISO 8601 UTC",
      "LastRunAt": null,
      "LastRunResult": null,
      "LastRunError": null,
      "Description": "agent 提供的人類友善註解",
      "RunCount": 0
    }
  ]
}
```

完整欄位語意見 [UCL_AgentCommand API](UCL_AgentCommand.md)。

---

## 8.1 Multi-Queue：persona 資料夾制（Tim 2026-08-01 拍板，取代 2026-05-13 的平鋪檔名制）

為解決「單 cmd 卡死阻塞整 pipeline + 多 agent 並行 submit 撞 race」問題（quest `agent-command-pipeline-parallelize` 方案 A），系統支援 **per-persona queue 隔離**。

> **2026-08-01 改版**：舊制把 queue 平鋪成 `queues/queue-<persona>-<lane>.json`，「這筆是誰派的」得從**檔名字串反推** —— 而 `queue-ame-design` 無法判定「`-design` 是用途還是名字的一部分」，`chess-0` 更是完全沒有 persona。
> 改成資料夾之後，**身分（資料夾）與通道（檔名後綴）在檔案系統層就分開了**，不必解析任何字串。
> 這也讓身分解析階梯的 tier 2「queue 反推」從「推論」降級成**單純讀路徑**。

### 路徑對照

| Mode | Queue 檔 | Trigger | Running |
|---|---|---|---|
| **本命** `<persona>` | `AgentCommands/queues/<persona>/queue.json` | `…/<persona>/pending.trigger` | `…/<persona>/pending.trigger.running` |
| **子通道** `<persona>/<lane>` | `AgentCommands/queues/<persona>/queue-<lane>.json` | `…/<persona>/pending-<lane>.trigger` | `…/<persona>/pending-<lane>.trigger.running` |
| **匿名**（未宣告身分） | `AgentCommands/queues/anonymous/queue.json` | `…/anonymous/pending.trigger` | `…/anonymous/pending.trigger.running` |

- **最外層共用 `AgentCommands/queue.json` 已廢除**（連同根層 `pending.trigger`）。每筆派遣都住在某個資料夾底下，掃描規則因此是一條**沒有例外**的「資料夾名 = 身分」。
- **`anonymous` 是保留字，不是 persona** —— 身分解析讀到它必須回「本層沒有答案」，**不可回字串 `"anonymous"`**。
  這不是潔癖：`bank_resolver` 對認不出的身分有命名慣例 fallback（`{name}-da-xiaojie`，隱含開新 bank），
  讓 `anonymous` 流進記帳層等於替一個不存在的人開戶。C# 端取值走 `UCL_AgentCommandQueue.GetDeclaredPersona()`，它對匿名回 `null`。
- `queues/anonymous/` 的流量**自己就是「還有多少未署名派遣」的儀表** —— 不需要有人記得去統計。

### CLI（`senate ucmd`）

```bash
# 本命 queue（一般用法）
senate ucmd run <Type> --persona <你的persona> --arg key=val

# 不帶身分 → queues/anonymous/（能跑，但沒署名）
senate ucmd run <Type> --arg key=val

# 子通道（同 persona 並行）——⚠ senate ucmd 目前沒有等價旗標，見下節
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <你的persona> --lane <通道> run <Type> --arg key=val
```

**`--agent-id` 已移除（Tim 2026-08-17 拍板）—— 打到會被明確擋下並指路（`exit 2`）。**
理由：它是自由字串、沒有唯一性保證（打錯會長出 `queues/<那串>/` 而不報錯）；
**唯一有守衛的身分是 persona**（同一 persona 不得同時登入兩次）。⇒ 一律改用 `--persona <名字>`。

### 同 Persona 並行子通道 `--lane` / `--parallel`（T06, 2026-06-07 summit）

> ⚠ **射程：本節仍走 `run_cmd.py`，`senate ucmd` 沒有等價旗標。**
> 全專案已改走 `senate ucmd`（TASK-0107），而 lane 分流是**唯一還沒搬過去的能力** ——
> 那一格記在 TASK-0107 §一④，未解。⇒ 需要並行子通道時仍用下面這幾行；
> 其餘一切走 `senate ucmd run <Type> --persona <p>`。
> 📌 這一段刻意**不轉譯成 senate 寫法** —— 轉譯出來的指令跑不動，
> 而「跑不動的範例」跟「還沒搬」長得不一樣：前者浪費下一個人二十分鐘，後者一眼看得出。

**問題**：同一 persona 的本命 queue 是**串行**的 —— per-agent IsRunning 擋重入（防同一 queue.json 的 write race）。所以「同 persona 的下一筆 cmd 會等前一筆跑完」。

**解**：`--lane <name>` 在自己的 persona 資料夾內開一個**獨立子通道檔**，queue id = `<persona>/<lane>`，走獨立 queue + running-lock → 與本命 queue **並行不阻塞**。

> 分隔符是 `/` 而不是舊制的 `~`：它現在對應**真實的目錄層級**，不是編碼在字串裡的假層級。
> 這是「身分 vs 通道」兩種語意分家的落點 —— 舊制 `--agent-id` 一個欄位同時扛兩者，
> 疊到第三種語意就沒人解得開（kotoko 2026-07-31 建議②）。

```bash
# 前一筆長 cmd 在跑 (e.g. 啟動遊戲 / 進 PlayMode), 本命 queue 被佔住
senate ucmd run RCG_StartNewGame --persona summit --arg from=reset

# 同 persona 同時插一筆快 cmd (讀畫面), 走 lane 不必等上面那筆結束
python run_cmd.py --persona summit --lane read run BattleSnapshot --arg observer=zeta
#   → queue id = 'summit/read' → queues/summit/queue-read.json (獨立並行)

# --parallel 是 --lane parallel 的捷徑
python run_cmd.py --persona summit --parallel run BattleSnapshot --arg observer=zeta
```

- **business identity 不變**：lane 只影響「走哪條 queue」（routing key），cmd 的 `caller` / `sender` / treasury / tavern 身分仍由 `--arg` 決定，跟 lane 無關。
- **並行的真相**：兩條 queue 各自 `Runner.RunAsync`，在 Editor 主執行緒上以 async UniTask 協同調度 —— 長 cmd 在 `await`（等 PlayMode / boot）時讓出，lane 的快 cmd 趁隙跑完。**對 await-heavy cmd 有效；CPU-heavy 仍序列**（同 §8.1 主執行緒 bottleneck）。
- ⚠ **跨 PlayMode 注意**：base cmd 若觸發 **domain reload**（Disable Domain Reload 沒開）會清掉**全部** in-memory runner state，lane cmd 也會被波及。需先關 Domain Reload（見 §8.2）才能穩定並行。

### C# API overload

`UCL_AgentCommandQueue` / `UCL_AgentCommandTrigger` / `UCL_AgentCommandRunner` 全 path / state methods 加 `string agentId = null` overload：

- `GetQueuePath(agentId)` / `GetTriggerPath(agentId)` / `GetRunningTriggerPath(agentId)`
- `Load(agentId)` / `Save(data, agentId)` / `EnsureDir(agentId)`
- `Trigger.PendingExists(agentId)` / `RunningExists(agentId)` / `MarkRunning(agentId)` / `Clear(agentId)`
- `Runner.RunAsync(agentId, token)`
- `Runner.IsRunningForAgent(agentId)` — per-agent flag，多 agent 各自獨立

null → legacy default 路徑（行為跟改動前完全相同）。

### Watcher 多 trigger scan

`UCL_AgentCommandWatcher.OnEditorUpdate`（1Hz throttled）改成兩段掃：

1. `TryDispatchAgent(null)` — 匿名落點（`queues/anonymous/pending.trigger`）
2. `foreach (var agentId in UCL_AgentCommandQueue.ListAgentIds())` — 掃 `queues/<persona>/queue*.json`，
   本命回 `"<persona>"`、子通道回 `"<persona>/<lane>"` → 各自 `TryDispatchAgent(agentId)`

多 trigger 同時存在會並行 dispatch（per-agent Runner 互不阻塞，Runner 端用 `HashSet<string> s_RunningAgents + lock` 防同 agent 重入）。

### 為何要分？

- **隔離卡死**：Zeta 卡 30 秒，Claude / Gemini / Antigravity 同時 submit 不受影響
- **避免 write race**：每 agent 寫自家 queue，`load → modify → save` 不再撞別 agent
- **對齊既有設計**：letters / baton / affinity / agent_bonus_quota 全 per-actor 分檔，cmd queue 該對齊

### Known limitations / Backlog（2026-05-13 review）

完整設計備註見 [`docs/Notes/AgentCommandPipeline_Parallelize_Analysis.md`](../../../../../../docs/Notes/AgentCommandPipeline_Parallelize_Analysis.md) §8。重點摘要：

- ✅ **Per-cmd timeout** — Shipped 2026-05-13。`UCL_AgentCommandHandlerBase.TimeoutSeconds` virtual property default 1200s (20 min)；子類 override + caller args `_timeout_sec=N` per-call 覆寫。Runner `UniTask.WhenAny + Delay + Cancel` wrap。
- **Editor 主執行緒 bottleneck**：multi-queue **不解多核並行**，CPU-heavy cmd 仍序列；IO-heavy async cmd 才有效
- **Cancel ≠ Timeout**：handler 多數沒 honor `CancellationToken`，timeout fire 後 cmd 仍跑到自己結束（Runner 不被卡死，但真實取消失敗）— audit / retrofit 待做
- ~~**Migration plan**：legacy queue.json fallback 仍支援 60 天…~~ → **已作廢**。2026-08-01 改 persona 資料夾制時
  直接切換、**不做相容層**：切換前點清 36 個舊 queue 全為空、0 筆在途 cmd，沒有需要搬運的狀態，
  因此不寫遷移碼也不雙讀。（原本提過「自動遷移」方案，前提是「舊 queue 可能躺著別人在途的工作」——
  Tim 指出並經點清確認**沒有在途工作**，該前提不成立，方案連同遷移腳本一併作廢。
  雙軌讀寫本身就是我們一路在治的鏡像債：同一個身分有兩個合法位置，不一致時不會有人喊。）

---

## 8.2 卡住排查 / 繞行 Recovery（2026-06-07 summit 補，血換）

**症狀**：送了 cmd 但 `_last_op.md` 一直不更新、`run_cmd.py` timeout、queue 裡某筆 `LastRunError` 顯示 `Interrupted by PlayMode transition, waiting for self-healing resumption...`。

**根因**：cmd 在「進 PlayMode 那刻」被 **domain reload** 打斷 → in-memory runner 的 UniTask state 被清掉 → 該筆卡在 `.running` orphan lock，**堵住同一條 queue 後續所有 cmd**。

> ⚠ **Domain Reload 是元兇**：跨 PlayMode 的 cmd（`PlayMode action=enter` / `RCG_StartNewGame from=reset` 等）**要求 Editor 設定 Disable Domain Reload**（Project Settings → Editor → Enter Play Mode Settings，options bit0）。Domain reload 開著時，進 play 會殺掉 runner async → 必卡。EOV committed 預設就是 `options=3`（DisableDomainReload + DisableSceneReload）。

### 繞行：換一條 queue（最快，不必重啟 Editor）

**一條 queue 卡住 ≠ 全系統卡** —— 每條 queue 各自獨立 running lock（§8.1）。換一條 queue 即可繞過：

```bash
# 本命 queue 卡住時 → 改走同 persona 的獨立子通道
#   ⚠ 這條仍走 run_cmd.py：senate ucmd 沒有 lane 旗標（TASK-0107 §一④ 未解）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <你的persona> --lane rescue run <Type> --arg key=val

# 連該子通道也卡了 → 換一個全新沒用過的 lane 名（全新 queue 必乾淨）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <你的persona> --lane rescue2 run <Type> --arg key=val
```

> 🩸 **這一節原本教的是 `--agent-id`，而那個旗標 2026-08-17 就被擋掉了（`exit 2`）** ——
> 文件教了十七天一個打下去必定失敗的繞行方法，**而繞行是給正在卡住的人看的**：
> 他最沒有餘裕去分辨「這條路壞了」跟「我又踩到另一個坑」。
> ⇒ 指路牌會比它指的路活得更久，**而急救用的指路牌壞掉的代價最大。**

> 注意：**不進 PlayMode 的 cmd**（BattleSnapshot / BattleAction / Tavern 等，純讀寫不觸發 domain reload）走 per-agent queue 可正常完成；**會進 PlayMode 的 cmd** 換 queue 也救不了根因，要先把 Disable Domain Reload 設定打開。

### 清 stuck lock（手動排乾淨）

```bash
# 1. 刪 orphan running lock (default 或 per-agent)
rm AgentCommands/pending.trigger.running
rm AgentCommands/queues/<persona>/pending[-<lane>].trigger.running
# 2. 移除卡死的 queue 條目（編 queues/<persona>/queue[-<lane>].json，刪該筆 Command）
```

Watcher 端有 **orphan-lock 自癒**：偵測「`.running` 檔存在但 in-memory runner idle」→ 自動 `Resuming runner!`（log 可見）。進 PlayMode 後 `OnPlayModeStateChanged` 會 `ResetRunningAgents()` 清記憶體 flag 鋪路。但若 in-memory runner 已被 domain reload 搞到半死，自癒不一定救得回 → 此時靠「換 queue 繞行」或 Editor reset（exit play / recompile / 重啟）。

### Editor reset（最後手段）

換 queue 也救不回（in-memory runner 全壞）→ 退 PlayMode / 觸發一次 Recompile / 重啟 Editor，watcher + runner 重新 init 即恢復。

---

## 8.3 ⚠ 暫行手動解堵 SOP（2026-08-06 summit 補；根治前的臨時規程，Tim 指示）

> [!IMPORTANT]
> **順序不可顛倒：先確認真因，再清。** `run_cmd.py` timeout 時印的
> 「Editor not running, or UCL_AgentCommandWatcher disabled?」是**猜測**，
> 2026-08-06 連兩次真因都不是那兩個（Editor 活著、watcher 也沒關）。
> 附了猜測的警示會讓人不去查真因 —— 照著猜測亂清，會把還沒跑的 cmd 一起刪掉。

### 第一步：查三個可驗證的事實（別跳過）

```bash
# ① Editor 還活著嗎（心跳＝Editor 端最近寫過的檔）
find AgentCommands -maxdepth 2 -newermt "-90 seconds" -type f
#    有輸出 → Editor 在跑，問題不在「沒開」

# ② 該筆 cmd 的 RunCount（真相在這裡）
python -c "import json;d=json.load(open('AgentCommands/queues/<persona>/queue.json',encoding='utf-8'));[print(c['Id'],c['RunCount'],(c['LastRunError'] or '(none)')[:100]) for c in d['Commands']]"

# ③ trigger 還在不在
ls AgentCommands/queues/<persona>/
```

### 第二步：依真因對症

| 事實組合 | 真因 | 處置 |
|---|---|---|
| `RunCount=0` + trigger 還在 + Editor 有心跳 | **trigger 落在 domain reload 窗口被靜默漏接**（watcher 的 FileSystemWatcher 被重載，事件沒了但檔還在） | **重寫 trigger 補一次事件**（見下），或人工按 ▶ Run Pending |
| `RunCount>0` + `LastRunError` 有值 | cmd 真的失敗了，而**失敗會留在 queue**（§4.3）→ 後續批次被 `run_cmd` 的 pending 預檢擋住 | 讀 `AgentCommands/_cmd_errors/<cmd_id>.md` **確認錯誤內容後**，手動移除該筆 |
| `.running` 檔存在但無心跳 | orphan lock（見 §8.2） | 刪 `.running`，必要時 Editor reset |
| Editor 無心跳 | 真的沒開 / 凍結 | 人工介入，別動 queue |

```bash
# 【A】漏接 trigger → 重寫補事件（不動 queue 內容，最安全）
python -c "import json,datetime;json.dump({'createdAt':datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%SZ'),'submittedBy':'manual re-notify'},open('AgentCommands/queues/<persona>/pending.trigger','w'),indent=2)"

# 【B】已確認失敗的那筆 → 依 cmd_id 移除（其餘不動）
python -c "
import json,sys
p='AgentCommands/queues/<persona>/queue.json'; cid='<cmd_id>'
d=json.load(open(p,encoding='utf-8'))
keep=[c for c in d['Commands'] if c['Id']!=cid]
assert len(keep)==len(d['Commands'])-1, '找不到該 cmd_id，先確認再清'
d['Commands']=keep; json.dump(d,open(p,'w',encoding='utf-8'),ensure_ascii=False,indent=2)
print('removed',cid)"
```

### 硬規則

- **只清自己送的 cmd。** queue 是多租戶共用的；清別人的那筆等於中斷同事正在等的工作
  （對照 §8.1 的「資料夾名＝身分」）。移除前先看 `Args` 裡的 `persona` / `_caller_env_marker` 是不是你。
- **先讀 `_cmd_errors/<cmd_id>.md` 再移除。** 移掉之後那筆的 `LastRunError` 就沒了，
  錯誤報告是唯一留下來的證據。
- **不要用「queue 空了」當成功的證據。** `run_cmd.py` 目前以「cmd 從 queue 消失」推論成功；
  手動移除失敗的那筆之後，同一個 cmd_id 在 `run_cmd` 眼裡會長得像成功。
  手動清完請自己去看 `_last_op.md` / 產物是否真的落地。

> **根治已上線**（2026-08-07 summit 實作，Tim 2026-08-06 拍板）：失敗的 OneShot
> ① 錯誤報告落檔 ② 寫 `_cmd_results/<id>.json` verdict ③ **直接從 queue 移除不堵塞**；
> `run_cmd.py` **同步**改為「以 result 檔判定」而非「以消失推論成功」（見 §4.3）。
> 本節的手動 SOP 保留給兩種殘餘情境：**trigger 落在 domain reload 窗口被漏接**
>（那是另一條病，見上表第一列）、以及舊版 Editor 沒有 result 檔的 fallback。

---

## 9. Async 執行與模組系統

`UCL_AgentCommandRunner.Menu_RunPending()` 走 async：

```csharp
await UCL_ModuleService.WaitUntilInitialized();
// 從 queue 取一筆
foreach (var cmd in queue.Commands)
{
    try {
        var handler = UCL_AgentCommandRegistry.Get(cmd.Type);
        await handler.ExecuteAsync(cmd.Args, cancelToken);
        // 成功 → 寫回 queue
    } catch (Exception ex) {
        // 失敗 → LastRunError = ex.ToString()
    }
}
```

**為什麼要 await ModuleService**：
- UCL Modules（包含 `RCG_*Data` 等資產系統）需要時間掃描磁碟、註冊 type metadata
- Editor 啟動 / Domain reload 後第一次跑指令 → 模組可能還沒註冊
- WaitUntilInitialized 確保所有 `UCL_Asset<>.Util` 可用 → handler 內可放心呼叫

---

## 10. 設計擴充點

### 10.1 加新指令
寫一個 class 繼承 `UCL_AgentCommandHandlerBase`（[第 5 節](#5-自動發現-handler)）。建議：
- 放 UCL_Core 層 → 通用指令（如資產解析、目錄匯出）
- 放 RCG 專案層 → 專案特定（如 Export*Notes）

### 10.2 加新觸發方式
目前有 5 種；可擴充：
- File watcher：檢測 queue.json 變動 → 自動 RunPending（無須點按鈕）
- HTTP endpoint：Editor 開啟時起一個本機 HTTP server 接收 cmd
- WebSocket：雙向通訊（agent 即時收到 stdout / Debug.Log）

### 10.3 加新輸出 sink
目前 Cmd 把結果寫到檔案系統（`AgentCommands/<output>`）。可擴充：
- 寫到 stdout / stderr 由 batchmode log 接收
- 寫到 Editor PlayerPrefs（讓下個 Cmd 接力）

---

## 11. 已知限制

| 限制 | 解法 / 替代方案 |
|---|---|
| Editor 必須開著才能執行 | Batchmode（慢但全自動）|
| 不支援指令間相依（must-run-after） | 靠 `Commands[]` 順序保證 |
| `Args` 只支援 `Dictionary<string,string>` | 複雜物件用 JSON 字串塞進 value，handler 內自行 parse |
| 無排程（cron-like） | 重複任務改 Repeatable + 使用者觸發 |
| Domain reload 後 Registry 重建 | static ctor 每次 reload 都跑，自動處理 |
| 同名 CommandType 後者覆蓋 | LogError 提醒；確保命名唯一 |

---

## 12. 相關文件

### API 細節
- [UCL_AgentCommand](UCL_AgentCommand.md) — 資料模型
- [Cmd_DebugLog](Cmd_DebugLog.md) — 最簡範例
- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — 資產解析
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md) — Cmd 目錄匯出
- [Cmd_ExportDocsCatalog](Cmd_ExportDocsCatalog.md) — 全 Markdown 文件靜態索引（含 aliases 模糊搜尋）
- [Cmd_SearchDocs](Cmd_SearchDocs.md) — 全 Markdown 即時搜尋（live scan + ranking + 同義詞展開；不依賴 catalog）

### 編輯器頁面
- [UCL_AgentCommandsPage](../../UCL_EditorPage/UCL_AgentCommandsPage.md) — IMGUI UI

### 工作流（專案層）
- [`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../../docs/Workflows/AgentCommands_Workflow.md) — 專案層工作流（含完整觸發方式對照、新增指令 SOP、命名空間踩雷紀錄）

### 工具
- [`Tools~/AgentCommands/run_cmd.py`](../../../../Tools~/AgentCommands/run_cmd.py) — Python CLI 包裝器
