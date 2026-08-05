---
name: ucl-compile-error
description: |
  Unity compile error 排查。當改完 .cs 後懷疑編譯有錯、agent 改了腳本要驗收、或使用者問「編譯有錯嗎」「CS0103 / CS0117 / CS1503 / CS0246」「assembly / asmdef」相關問題時用本 skill。
  核心工具是 standalone Python 腳本 check_compile.py，完全不依賴 Cmd 系統，能在 Cmd 因 compile error 失效時也印錯誤清單。
trigger: { on_files: ["*.cs"], on_intent: ["編譯錯", "compile error", "CS0103", "CS0117", "CS1503", "CS0246", "asmdef", "assembly"] }
---

# UCL Compile Error 排查

> 解的問題：改了 .cs → Unity 編譯失敗 → Cmd 系統跟著掛（assembly 載不進來 → handler 不在 Registry）→ 「最需要查錯的時候沒有 Cmd 可用」。

## 必讀

完整 SOP + 8 大常見錯誤類型對照 → `ucl_core:Docs~/zh-Hant/Workflows/CompileError_Diagnose_Workflow.md`

## 速查指令

```bash
# 預設（healthy / broken 都跑這條）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only

# .compile_status.json 不存在 → fallback 解 Editor.log
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log

# 改完檔等下一次 compile（agent 動完 .cs 後驗收用）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60

# 新鮮度基準指定成「我剛改的那個檔」—— 一次 stat，比問 git 更精準也更便宜
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --since-file <你改的.cs>

# CI / 腳本：狀態沒涵蓋改動就 exit 4（別讓過期綠燈通過驗收）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --strict-fresh
```

## 🚨 新鮮度守衛（2026-08-05 起預設開啟）

**這支工具現在會先回答「這份狀態涵蓋你的改動嗎」，再回答「有沒有錯」。**

狀態早於你最近一次 `.cs` 改動時，輸出最上方會蓋 `🚨 STALE` 橫幅，並且
**不會印「✅ Clean compile」**（改印「無法判定」）。`--format json` 也帶 `stale` / `staleness` 欄位。

- 基準怎麼來：預設問 git 拿**未提交的 `.cs`**（root + 髒 submodule，整個 process 只算一次）。
  指定 `--since-file <path>` / `--since <epoch|ISO>` 則跳過 git，直接比那一個時間。
- 併讀 `_heartbeat_stalls.jsonl`（酒保心跳的停跳台帳）：STALE 時會多印一行
  「改動後心跳停跳 N 次」/「改動後沒有任何停跳紀錄」—— **後者代表編譯很可能連開始都還沒有**。
- 逃生門：`--no-freshness` 關掉檢查（＝退回 2026-08-05 之前的行為）。

> 🩸 2026-08-05 血證（summit）：`.compile_status.json` 寫在 `08:57:00`，我最後一筆 `.cs` 編輯在
> `08:57:06` —— 工具把那份**早於我改動 6 秒**的快照當結論報出來，報的是紅燈 CS0103，我相信了，
> 然後花 40 分鐘查一隻不存在的 bug。**對時間戳才看得出來。**
> 這隻跟下面 2026-05-22 那筆是同一枚硬幣：那筆的解法寫「改用 `check_compile.py` 二次確認」，
> 而當時 `check_compile.py` 自己也沒有新鮮度概念 —— 今天補的就是另外那一半。

> [!WARNING]
> 停跳台帳證明「Editor 凍過」，**不證明「編譯過」** —— domain reload / 資產匯入 /
> 主執行緒長工 / Editor 關閉期間都會停跳。而且停跳只有在**恢復的那一拍**才寫得出來：
> 進行中的凍結沒有紀錄，Editor 死掉不再回來則永遠不寫。**沒有條目 ≠ 沒有停跳。**

## 💓 `--editor-alive` — Editor 還在 tick 嗎（純 stat 一個檔，不送 Cmd）

```bash
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --editor-alive
# exit 0 = 在 tick / 1 = 沒在 tick / 3 = 無心跳檔
```

用途：**「現在叫 Editor 做事會不會等」**。編譯 / domain reload 期間整個 update 迴圈不跑 → 心跳自然停。
比送一支 Cmd 探針快得多（探針要 2s 空閒 / 13s 編譯中）。順帶印最近一次停跳（時間 + 停多久）。

> [!CAUTION]
> **它答的是「此刻活不活」，不是「我的改動編了沒」——這兩題差很遠。**
> 心跳是瞬時值。Unity 常把外部改檔的重編**遞延到視窗重獲焦點**，那段期間 Editor 一直在 tick，
> 於是「✅ 正在 tick，沒有卡在編譯」字面為真，卻會被讀成「編譯沒問題」。
>
> 🩸 2026-08-05：我就是這樣被騙 40 分鐘 —— 兩次 `RequestScriptCompilation()` 都被受理
> （Editor.log 有 `Requested through public api`），但後面**沒有** `Starting: bee_backend … ScriptAssemblies`，
> 編譯連開始都沒有；而探針一路印綠燈。
> **要問「我的改動編了沒」跑 `--errors-only`（新鮮度守衛會答），不是看 `--editor-alive`。**

## 順序

1. 跑上面的 `--errors-only`
2. 0 errors → 收工（runtime 錯是另一回事，看專案的 `DebugLogs/Errors_latest.log`）
3. 有錯 → 對照 workflow 文件的「8 大常見錯誤類型」找模式
4. 改完 → `--watch` 等下一輪驗收

## 不要做

- 在編譯還有錯時跑 runtime（沒意義）
- 用 `Recompile` AgentCommand 取代本工具（compile error 時 Cmd 本身可能掛）
- 只看 `Simulation_*.log` 不看 `.compile_status.json`（前者混雜 Warning 雜訊）
- **只信 `run_cmd.py recompile` 子命令回報的 `errors=N` 就收工** — 它可能讀到 stale / intermediate `.compile_status.json` 而 **under-report `errors=0`**。改完 .cs **務必**用 `check_compile.py --errors-only` 二次確認。
  > 🩸 2026-05-22 血證:apex-two 的 `item.Data.name`(CS1061)被 `recompile` 子命令漏報成 `errors=0`,而 `Errors_latest.log`(runtime 層)也乾淨 → basecamp 誤判成「domain reload 沒生效」,繞一大圈才靠 `check_compile.py` 確診。**compile 層 ≠ runtime 層 ≠ recompile-cmd 回報層**,三層別混(對應「跨層次驗證」family)。

## 🧪 runtime 行為驗證（不跑遊戲）— Cmd_Invoke reflection

compile 0 error 只證「語法／型別對」，不證「邏輯對」。要驗**真正的 C# 執行結果**又不想開場景跑整個遊戲，用 `Cmd_Invoke`（reflection）直接觸發 public static 方法——比另寫 Python 鏡像實作更真（跑的就是那份 C#）。

**做法**：
1. 給待測邏輯加一個 `public static string SelfTest()`（內部跑斷言：全過回摘要字串、任一失敗 `throw`），或直接 invoke 目標方法。
2. 觸發：
   ```bash
   # 先確保 Editor 載入最新編譯（domain reload）
   python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Recompile
   # reflection 呼叫 static 方法（args/storeAs 鏈式 instance 呼叫見 Cmd_Invoke.md）
   python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Invoke \
     --arg type=<Namespace.Type.FullName> --arg member=<StaticMethod>
   ```

**驗真實結果——別只信 run_cmd 的「Success」**（跨層陷阱：cmd 在 handler 拋例外後可能 auto-removed、stdout 照印 `✓ Success`）：
- 回傳值 / 例外進 Unity console → 抓 `Editor.log` grep `[AgentCmd:Invoke]`：`OK (Type) = <值>` 才是真通過；`FAILED: <err>` = 真失敗。
- SelfTest 的斷言 `throw` → Cmd_Invoke 轉 `throw` → Cmd 標 Failed + log 有 `FAILED`。
- Editor.log 路徑：`%LOCALAPPDATA%/Unity/Editor/Editor.log`（Win）。

🩸 血證（2026-07-22）：`UCL_SecretCrypto` 全切 C#（AES-256-CBC+HMAC+PBKDF2）後，靠 `run Invoke member=SelfTest` 驗到「4 round-trip 案例 + 錯密碼拒絕 + 竄改偵測」全過——ground-truth 是 Editor.log 回的 `OK (System.String) = OK: UCLS1 self-test passed...` 字串，不是 run_cmd 的 Success（後者是「跨層次驗證」family 要防的假綠）。不必寫測試場景、不必 Python 鏡像。

> 適用面：任何「純函式／可 static 觸發」的 C# 邏輯（crypto / parser / resolver / 資料轉換…）。有 Unity 生命週期依賴（MonoBehaviour / 場景物件）的才需要真的跑遊戲。

## 後續

`recompile 0 errors` ≠ runtime 0 errors。改完 code 跑遊戲後仍要看專案的 `DebugLogs/Errors_latest.log` — 這歸 RuntimeError_Diagnose_Workflow（下游專案端）。runtime 行為（非 MonoBehaviour 依賴）可先用上方 **Cmd_Invoke reflection** 驗，不必開場景。
