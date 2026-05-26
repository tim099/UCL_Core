---
name: ucl-compile-error
description: |
  Unity compile error 排查。當改完 .cs 後懷疑編譯有錯、agent 改了腳本要驗收、或使用者問「編譯有錯嗎」「CS0103 / CS0117 / CS1503 / CS0246」「assembly / asmdef」相關問題時用本 skill。
  核心工具是 standalone Python 腳本 check_compile.py，完全不依賴 Cmd 系統，能在 Cmd 因 compile error 失效時也印錯誤清單。
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
```

## 順序

1. 跑上面的 `--errors-only`
2. 0 errors → 收工（runtime 錯是另一回事，看 `CardGame/Assets/DebugLogs/Errors_latest.log`）
3. 有錯 → 對照 workflow 文件的「8 大常見錯誤類型」找模式
4. 改完 → `--watch` 等下一輪驗收

## 不要做

- 在編譯還有錯時跑 runtime（沒意義）
- 用 `Recompile` AgentCommand 取代本工具（compile error 時 Cmd 本身可能掛）
- 只看 `Simulation_*.log` 不看 `.compile_status.json`（前者混雜 Warning 雜訊）
- **只信 `run_cmd.py recompile` 子命令回報的 `errors=N` 就收工** — 它可能讀到 stale / intermediate `.compile_status.json` 而 **under-report `errors=0`**。改完 .cs **務必**用 `check_compile.py --errors-only` 二次確認。
  > 🩸 2026-05-22 血證:apex-two 的 `item.Data.name`(CS1061)被 `recompile` 子命令漏報成 `errors=0`,而 `Errors_latest.log`(runtime 層)也乾淨 → basecamp 誤判成「domain reload 沒生效」,繞一大圈才靠 `check_compile.py` 確診。**compile 層 ≠ runtime 層 ≠ recompile-cmd 回報層**,三層別混(對應「跨層次驗證」family)。

## 後續

`recompile 0 errors` ≠ runtime 0 errors。改完 code 跑遊戲後仍要看 `Errors_latest.log` — 這歸 RuntimeError_Diagnose_Workflow（EOV 端）。
