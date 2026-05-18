---
title: BuildPlayerCheck 修復 Loop Workflow — Player Build CS Error 自動驗證 + 修復循環
description: agent 用 Cmd_BuildPlayerCheck 跑 Player Script Compile (走當前 Build Profile) → 抓 Editor compile 看不出的 CS error → 分類 root cause → 套對應 fix family → 重跑 verify → 直到 0 error 出得了 Build。對應 T18 family (Mono preprocessor) / Treasury family (#if guard 跨檔不一致) / Editor-only type referenced 等 Player-Build-only family 的 systematic 處理。
last_updated: 2026-05-18
target_audience: [AI_Agent, Tim, Tools_Maintainer]
related:
  - ucl_core:Docs~/{lang}/Workflows/Edit_Recompile_Loop_Workflow.md | Editor Compile Loop | Edit → Recompile (Editor 端 check_compile.py) 對偶 loop
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | Compile Error 排查 | Editor 端 CS error 8 大常見類別 + asmdef 陷阱
  - ucl_core:Docs~/{lang}/Tools/Python_Tools_Index.md | Python Tools | check_compile.py / run_cmd.py 等 CLI
---

# 🏗 BuildPlayerCheck 修復 Loop Workflow

> 一句話: **Editor compile 0 error ≠ Player Build 0 error**。本 workflow 用 `Cmd_BuildPlayerCheck` 跑當前 Build Profile 的 scripts-only build, 抓 Player-Build-only family error, 套對應 fix family, loop 直到 0 error。

---

## 🎯 為何需要 (痛點)

| 現象 | 細節 |
|---|---|
| Editor recompile 0 errors | `check_compile.py` 永遠綠燈 |
| Player Build 卻 fail | Tim 跑 Addressables Build 才暴露 CS error |
| Lag time | 通常一輪修復 cycle: Tim 跑 build → 拿 error log 給 agent → agent 看 → fix → Tim 再跑 build |
| 根因類別 | 多半屬「Editor 有 define `UNITY_EDITOR` 但 Player 沒有」family — `#if UNITY_EDITOR` guard 不一致, Mono preprocessor verbatim string bug, Editor-only type 被 Player asmdef code 引用 等 |

**解法**: Agent 自己跑 `Cmd_BuildPlayerCheck`, 不必等 Tim 手動 build。30s 一輪 fix-verify cycle, agent 自閉環。

---

## 🛠 完整 Loop SOP

```
┌──────────────────────────────────────────────────────────┐
│ Step 0 — 觸發條件                                         │
│   - Tim 跑 Addressables Build fail (Errors_*.log)        │
│   - 或主動: 改 UCL_Core / 跨 asmdef code 後 preventive    │
│   - 或 ship Editor compile 0 error 但要 release 前         │
└──────────────────────────────────────────────────────────┘
                          ↓
┌──────────────────────────────────────────────────────────┐
│ Step 1 — 跑 Cmd_BuildPlayerCheck                          │
│   python <UCL_Core>/Tools~/AgentCommands/run_cmd.py \    │
│     run BuildPlayerCheck --arg mode=scripts_only         │
│                                                          │
│   等 30-60s (BuildOptions.BuildScriptsOnly fast path)     │
└──────────────────────────────────────────────────────────┘
                          ↓
┌──────────────────────────────────────────────────────────┐
│ Step 2 — 讀 _last_op.md 解析 errors                       │
│   cat AgentCommands/ChatTavern/_last_op.md               │
│                                                          │
│   結果:                                                   │
│   - Result: `Succeeded` → 跳 Step 5 ✅ exit               │
│   - Result: `Failed` → Errors 列表進 Step 3              │
└──────────────────────────────────────────────────────────┘
                          ↓
┌──────────────────────────────────────────────────────────┐
│ Step 3 — 分類 error root cause (走下方 family table)      │
└──────────────────────────────────────────────────────────┘
                          ↓
┌──────────────────────────────────────────────────────────┐
│ Step 4 — 套對應 fix family + 動工                         │
│   - 改檔                                                  │
│   - check_compile.py 確認 Editor 還是 0 errors            │
└──────────────────────────────────────────────────────────┘
                          ↓
                  ┌───── ↺ ─────┐
                  │ 回 Step 1   │ ← loop 直到 Result=Succeeded
                  └─────────────┘
                          ↓
┌──────────────────────────────────────────────────────────┐
│ Step 5 — Commit (三層 bump if 改 UCL_Core)                │
│   - 走 ucl-commit skill                                  │
│   - tavern task-share 給 Tim 看 fix 摘要                  │
└──────────────────────────────────────────────────────────┘
```

---

## 📋 Error Family 分類表 (按出現頻率 2026-05-18 ship 經驗)

### Family A: `#if UNITY_EDITOR` 跨檔 guard 不一致 (CS0246 type missing)

**症狀**: `CS0246: The type or namespace name 'XXX' could not be found`

**Root cause**:
- consumer file 沒 guard (compile 兩平台都有)
- type 定義 file 有 `#if UNITY_EDITOR` guard
- → Player Build: consumer 找不到 type 定義 → CS0246

**範例**: T19 Treasury (2026-05-18)
- `UCL_TreasuryLedger.cs` 已 strip guard
- `UCL_TreasuryModels.cs` (含 `TreasuryLedgerEntry` / `TreasuryEntryType`) 仍 wrap → 8 處 CS0246

**Fix**: 同 namespace 內 strip / add guard 必須一致:
- 純 data + IO + 不依賴 UnityEditor 的 → strip guard (兩平台都 compile)
- 真有 Editor 依賴的 (e.g. `Cmd_Treasury` 用 AssetDatabase) → 保留 guard

**Sanity check**:
```bash
grep -nE "using UnityEditor|EditorApplication|AssetDatabase" <file>
# 0 hit → 可以 strip guard
# 非 0 hit → 必須保留 guard
```

### Family B: Mono preprocessor verbatim-string bug (CS1024)

**症狀**: `CS1024: Preprocessor directive expected` 在 string literal 行

**Root cause**:
- 檔案有 `#if UNITY_EDITOR ... #endif` 包整 namespace
- 含 `$@"..."` verbatim string, string 內 column-0 (含 leading whitespace) 有 `#` (e.g. markdown `## H2`)
- Player Build (Mono preprocessor) 進 conditional-strip 模式掃 `#endif`, **不正確 track verbatim string state** → 把字串內 `#` 誤判為 directive

**範例**: T18 series (2026-05-18 BartenderDaemon)
- T18.0 失敗: 加 leading space (whitespace 仍被當 directive prefix)
- T18.2 work: 用 `" + @"##` 拼接 break source line 開頭
- **T18.3 終極**: 字串搬 UCL_CodeLocalize, source 沒 `$@""` 跟 `##` → bug 三條件斷一條

**Fix 優先序**:
1. **(最佳) 搬走** — UCL_CodeLocalize regular string with `\n` escape
2. (workaround) string concat 讓 source line 不以 `#` 開頭

**Editor 為何 0 errors**: Roslyn 正確 track string state; Mono 不會。`check_compile.py` 永遠看不出此 bug, 必須跑 Player Build。

### Family C: Editor-only API in Player code (CS0234)

**症狀**: `CS0234: The type or namespace name 'XXX' does not exist in the namespace 'UnityEditor' (are you missing an assembly reference?)`

**Root cause**:
- code 用 `UnityEditor.EditorUtility` / `UnityEditor.AssetDatabase` 等
- 但檔案沒 `#if UNITY_EDITOR` guard / 也不在 Editor asmdef
- Player Build: `UnityEditor` namespace 不存在 → CS0234

**範例**: `UCL_DiscordChannelRoutingPage.cs:87` (本次 ship 抓到)
- `EditorUtility.OpenFilePanel(...)` 在 Page 內被引用
- 但 page 整檔沒 `#if UNITY_EDITOR` 包

**Fix**: 兩選一
1. **加 `#if UNITY_EDITOR` 包整段** (含 using 跟 namespace block)
2. **改用 runtime API** (若邏輯該是 runtime)

判定: 該 code 是否真該在 Player 跑? 若是 IMGUI Editor page → 走 (1) guard. 若 runtime UI → 走 (2) refactor.

### Family D: Editor-only type referenced (CS0103)

**症狀**: `CS0103: The name 'UCL_XXXPage' does not exist in the current context`

**Root cause**: 同 Family C 但更微妙 — 引用的不是 `UnityEditor.*` 而是專案內 Editor-only 的 type (例如某個 IMGUI page class 自己 wrap 在 `#if UNITY_EDITOR` 內)。

**範例**: 本次 ship 抓到 2 個:
- `UCL_EditorMenuPage.cs:82` 用 `UCL_AgentSkillManagerPage` (應該 Editor-only)
- `UCL_PersonaInspectorPage.cs:518` 用 `UCL_MarkdownViewerPage` (應該 Editor-only)

**Fix**: 跟 Family C 同 — 把 caller code 也加 guard, 或重新評估「該 type 是否該 Editor-only」。

---

## 🐍 Cmd / CLI 速查

| 動作 | 命令 |
|---|---|
| 跑 Player Build check | `python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run BuildPlayerCheck --arg mode=scripts_only` |
| 讀結果 | `cat AgentCommands/ChatTavern/_last_op.md` |
| Editor 端 quick check | `python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only` |
| Refresh + recompile | `python ... run Recompile` |

---

## ⚠ 邊角 / 注意事項

1. **Build profile 必須有 active** — 若沒設 active profile, Cmd 走 `EditorUserBuildSettings.activeBuildTarget` fallback。建議 Tim 至少設一個 default profile。
2. **Scenes 至少 1 個** — profile.scenes 或 EditorBuildSettings.scenes 必須有 enabled scene, 否則 Cmd reject。
3. **`scripts_only` mode 限制** — 抓得到 CS error, 但抓不到 asset-level error (e.g. shader compile / addressables 配置錯誤)。要驗 asset 級用 `mode=full` (慢) 或實際 Addressables Build。
4. **Cmd 跑期間 Editor blocking** — 30-60s 期間 Editor UI 凍結。Cmd 內部跑 `BuildPipeline.BuildPlayer` sync。
5. **Output path** — 用 system temp folder (`%TEMP%/UCL_AgentBuildCheck/<timestamp>/`), 不污染專案目錄, Unity 自己清。
6. **Domain reload race** — BuildPlayer 內可能觸發 script compile → domain reload。Cmd async 後續 code 可能被殺。實測 reload 發生在 build 結束後, sync return 前一刻, 影響不大但要警覺。

---

## 📋 套本 Workflow 的時機

| 場景 | 該跑? |
|---|---|
| 改 UCL_Core 內 `#if UNITY_EDITOR` 相關 code | ✅ 強烈建議 |
| 改 cross-asmdef code (Editor ↔ Runtime 界線) | ✅ |
| 加新 namespace import (UnityEditor.* 等) | ✅ |
| Tim 跑 Build 撞 CS error 派 task | ✅ (本 workflow 主場) |
| 改 ChatTavern 訊息 / docs / glossary | ❌ 不必 |
| 只改 .json / asset 沒動 .cs | ❌ |

---

## 📚 相關文件

- [Edit_Recompile_Loop_Workflow.md](Edit_Recompile_Loop_Workflow.md) — Editor 端對偶 loop (check_compile.py)
- [CompileError_Diagnose_Workflow.md](CompileError_Diagnose_Workflow.md) — Editor CS error 8 大類 + asmdef 陷阱
- [Tools/Python_Tools_Index.md](../Tools/Python_Tools_Index.md) — Python CLI 索引
- `Cmd_BuildPlayerCheck.cs` (UCL_Core/EditorCore/UCL_AgentCommands/CMD/) — 本 workflow host

---

## 🏷 Ship History

| 版本 | 日期 | 作者 | 動作 |
|---|---|---|---|
| T19 v1 | 2026-05-18 | gura | 初版 ship — Cmd + workflow 對齊。Family A/B/C/D 四類來自連續 2 輪 (T18 BartenderDaemon + Treasury) 修復經驗 |
