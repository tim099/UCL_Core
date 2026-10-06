---
name: ucl-compile-error
description: |
  Unity compile error 排查。當改完 .cs 後懷疑編譯有錯、agent 改了腳本要驗收、或使用者問「編譯有錯嗎」「CS0103 / CS0117 / CS1503 / CS0246」「assembly / asmdef」相關問題時用本 skill。
  主入口是 Senate CLI：`senate cmd unity-recompile`（觸發＋等到那一趟編譯結束）／
  `senate cmd unity-compile-status`（只讀現況，不需要 Editor）。
  ⚠ 狀態檔不存在時只說「沒有讀數」（不解 Editor.log）；問「Editor 在不在 tick」直接 stat 心跳檔，見下。
trigger: { on_files: ["*.cs"], on_intent: ["編譯錯", "compile error", "CS0103", "CS0117", "CS1503", "CS0246", "asmdef", "assembly"] }
---

# UCL Compile Error 排查

> 解的問題：改了 .cs → Unity 編譯失敗 → Cmd 系統跟著掛（assembly 載不進來 → handler 不在 Registry）→ 「最需要查錯的時候沒有 Cmd 可用」。

## 必讀

完整 SOP + 8 大常見錯誤類型對照 → `ucl_core:Docs~/zh-Hant/Workflows/CompileError_Diagnose_Workflow.md`

## 速查指令（主入口＝Senate CLI）

```bash
# ⭐ 改完 .cs 之後就走這條：觸發重編 ＋ 等到**那一趟**結束才印
senate cmd unity-recompile --arg persona=<me>

# 只想知道「現在磁碟上那份狀態說什麼」—— 不觸發、**不需要 Editor**
senate cmd unity-compile-status
```

⚠ **兩支回答的是不同問題，別互相代替**：
`unity-recompile` 回答「**我這次改動編譯過了嗎**」（基準＝送出觸發的那一刻）；
`unity-compile-status` 回答「**現在磁碟上那份說什麼**」，而它會自己講明它不知道那是不是你的改動。

⛔ **兩支都只量 Unity assemblies，不涵蓋 `senate.exe`** —— 那條走 `dotnet build` ／ `build.sh`。
🩸 2026-09-07 血證：同一份 `SCP_Cmd_Keys.cs`，Unity 印 **0 errors**、`dotnet build` **CS8603 紅燈**
（Unity 那側 LangVersion 9、nullable 沒開；Senate 那側 nullable 開著且警告當錯誤）。
**兩個宿主的尺不同形，而且不可以合成一把。**

### 讀數邊界

- **狀態檔不存在** ⇒ `unity-compile-status` 印「**沒有讀數**」（`compile_verdict=no_reading`，exit 2），⛔ 不印 0 errors、也不解 Editor.log —— 答案是「沒有量到」。
- **`unity-recompile` 逾時** ⇒ `compile_verdict=timeout`（exit 4），⛔ 不退回印上一趟的快照。
- **`stale_sources`**（兩支都印）：有幾個 `.cs` 比磁碟上的組件新；沒量到印 `unmeasured`，不寫 0。
  Unity 沒東西要編時也會寫一份新狀態（errors=0、晚於基準），跟真的編過同形 ⇒ `clean` 但 `stale_sources>0` 時別收工。
- 等待條件**不能只看 `in_progress=false`** —— 觸發還沒開始時它已經是 false，會直接回上一次的快照。
  `unity-recompile` 以**送出那一刻**（檔案 mtime）為基準，只收晚於它的那一份。
- 狀態早於你最近一次 `.cs` 改動 ⇒ 那份不是你的結論。**對時間戳才看得出來。**

> [!WARNING]
> 停跳台帳證明「Editor 凍過」，**不證明「編譯過」** —— domain reload / 資產匯入 /
> 主執行緒長工 / Editor 關閉期間都會停跳。而且停跳只有在**恢復的那一拍**才寫得出來：
> 進行中的凍結沒有紀錄，Editor 死掉不再回來則永遠不寫。**沒有條目 ≠ 沒有停跳。**

## 💓 「Editor 在不在 tick」 — stat 心跳檔

```bash
# 心跳檔（UCL_EditorHeartbeat hook 在 EditorApplication.update，每 0.5s 寫一拍）
stat -c %y <data_root>/ChatTavern/bartender/_heartbeat.txt   # 或 ls -la
#   距今 <= 1.5s ⇒ Editor 在 tick／> 1.5s ⇒ 沒在 tick／檔案不存在 ⇒ 判不出來（daemon 沒跑過）
# 最近的停跳台帳（心跳只答「此刻」，這個答「什麼時候凍過、凍多久」）：
#   <data_root>/ChatTavern/bartender/_heartbeat_stalls.jsonl
```

⚠ 另一條路也成立、但比較貴：**`senate cmd unity-recompile` 是否逾時**（逾時會印 `delegate_failure = timeout`，
且**刻意不去讀上一輪的回傳檔** —— 逾時代表它沒被更新，讀到的會是上一輪那份「格式完整、數字合理」的舊快照）。
⇒ 那是**送一支 Cmd 去探**，Editor 忙的時候要等到逾時。**能 stat 就不要送 Cmd。**

用途：**「現在叫 Editor 做事會不會等」**。編譯 / domain reload 期間整個 update 迴圈不跑 → 心跳自然停。
比送一支 Cmd 探針快得多（探針要 2s 空閒 / 13s 編譯中）。要「什麼時候凍過、凍多久」就讀 `_heartbeat_stalls.jsonl`。

> [!CAUTION]
> **它答的是「此刻活不活」，不是「我的改動編了沒」——這兩題差很遠。**
> 心跳是瞬時值。Unity 常把外部改檔的重編**遞延到視窗重獲焦點**，那段期間 Editor 一直在 tick，
> 於是「✅ 正在 tick，沒有卡在編譯」字面為真，卻會被讀成「編譯沒問題」。
>
> 🩸 2026-08-05：我就是這樣被騙 40 分鐘 —— 兩次 `RequestScriptCompilation()` 都被受理
> （Editor.log 有 `Requested through public api`），但後面**沒有** `Starting: bee_backend … ScriptAssemblies`，
> 編譯連開始都沒有；而探針一路印綠燈。
> **要問「我的改動編了沒」跑 `senate cmd unity-recompile`（它拿送出時刻當基準，等到那一趟結束才印；
> 另有 `stale_sources` 答「有幾個 .cs 比組件新」），不是看心跳。**

## 順序

1. 跑 `senate cmd unity-recompile --arg persona=<me>`
2. 0 errors 且 `stale_sources=0` → 收工（runtime 錯是另一回事，看專案的 `DebugLogs/Errors_latest.log`）
3. 有錯 → 對照 workflow 文件的「8 大常見錯誤類型」找模式
4. 改完 → 再跑一次 `unity-recompile` 驗收

## 不要做

- 在編譯還有錯時跑 runtime（沒意義）
- 用 `Recompile` AgentCommand 取代本工具（compile error 時 Cmd 本身可能掛）
- 只看 `Simulation_*.log` 不看 `.compile_status.json`（前者混雜 Warning 雜訊）
- **只信任何 client 一次回報的 `errors=N` 就收工** — 它可能讀到 stale / intermediate `.compile_status.json` 而 **under-report `errors=0`**。改完 .cs **務必**用 `senate cmd unity-recompile` 確認（它等的是你那一趟）。
  **compile 層 ≠ runtime 層 ≠ Cmd 回報層**，三層別混（對應「跨層次驗證」family）。

## 🧪 runtime 行為驗證（不跑遊戲）— Cmd_Invoke reflection

compile 0 error 只證「語法／型別對」，不證「邏輯對」。要驗**真正的 C# 執行結果**又不想開場景跑整個遊戲，用 `Cmd_Invoke`（reflection）直接觸發 public static 方法——比另寫 Python 鏡像實作更真（跑的就是那份 C#）。

**做法**：
1. 給待測邏輯加一個 `public static string SelfTest()`（內部跑斷言：全過回摘要字串、任一失敗 `throw`），或直接 invoke 目標方法。
2. 觸發：
   ```bash
   # 先確保 Editor 載入最新編譯（domain reload）
   senate ucmd run Recompile --persona <me>
   # reflection 呼叫 static 方法（args/storeAs 鏈式 instance 呼叫見 Cmd_Invoke.md）
   senate ucmd run Invoke --persona <me> \
     --arg type=<Namespace.Type.FullName> --arg member=<StaticMethod>
   ```

**驗真實結果——別只信 client 印的「Success」**（跨層陷阱：cmd 在 handler 拋例外後可能 auto-removed、stdout 照印 `✓ Success`）：
- 回傳值 / 例外進 Unity console → 抓 `Editor.log` grep `[AgentCmd:Invoke]`：`OK (Type) = <值>` 才是真通過；`FAILED: <err>` = 真失敗。
- SelfTest 的斷言 `throw` → Cmd_Invoke 轉 `throw` → Cmd 標 Failed + log 有 `FAILED`。
- Editor.log 路徑：`%LOCALAPPDATA%/Unity/Editor/Editor.log`（Win）。

🩸 血證（2026-07-22）：`UCL_SecretCrypto` 全切 C#（AES-256-CBC+HMAC+PBKDF2）後，靠 `run Invoke member=SelfTest` 驗到「4 round-trip 案例 + 錯密碼拒絕 + 竄改偵測」全過——ground-truth 是 Editor.log 回的 `OK (System.String) = OK: UCLS1 self-test passed...` 字串，不是 `senate ucmd run` 的 Success（後者是「跨層次驗證」family 要防的假綠）。不必寫測試場景、不必 Python 鏡像。

> 適用面：任何「純函式／可 static 觸發」的 C# 邏輯（crypto / parser / resolver / 資料轉換…）。有 Unity 生命週期依賴（MonoBehaviour / 場景物件）的才需要真的跑遊戲。

## 後續

`recompile 0 errors` ≠ runtime 0 errors。改完 code 跑遊戲後仍要看專案的 `DebugLogs/Errors_latest.log` — 這歸 RuntimeError_Diagnose_Workflow（下游專案端）。runtime 行為（非 MonoBehaviour 依賴）可先用上方 **Cmd_Invoke reflection** 驗，不必開場景。
