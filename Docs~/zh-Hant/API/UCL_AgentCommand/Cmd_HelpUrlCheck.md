---
title: Cmd_HelpUrlCheck API
description: "[HelpURL] 目標存在性的機械檢查 —— 反射掃 attribute、走 UCL_URL.ResolveURL 本人解析後驗檔案存在，缺檔即非零退出。"
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/HelpUrlCheck/Cmd_HelpUrlCheck.cs
namespace: UCL.Core.EditorLib.AgentCommands.HelpUrlCheck
last_updated: 2026-09-22
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_HelpUrlCheck

> 把「要有人記得去點那顆 `?` 鈕」換成一支跑得起來的驗證器。

## 1. 概覽

- **CommandType**：`HelpUrlCheck`
- **原始碼 ShortDescription**：[HelpURL] 目標存在性機械檢查 —— 反射掃 attribute，走 UCL_URL.ResolveURL 本人解析後驗檔案存在

**為什麼有這支**：`[HelpURL]` 指向不存在的檔時，失效樣子是**按鈕點下去沒反應**。
`UCL_GUILayoutDrawObject.TryOpenHelpInMarkdownViewer` 2026-08-17 補了一句 `LogWarning`，
而那句只在**有人去點**的時候才響 —— 「沒有人去點」正是本案的成因（TASK-0257）。

## 2. 參數 (ArgsSchema)

- `max_list=最多列出幾條缺檔`（選填，預設 50；`0` = 不限）
- `list_ok=1` 時連「解得到且存在」的本地條目也逐條列出（選填，預設 0）
- `list_remote=1` 時列出雲端 URL 條目（選填，預設 0；雲端目標**不驗**，只數）

```bash
senate ucmd run HelpUrlCheck --persona <me>
senate ucmd run HelpUrlCheck --persona <me> --arg max_list=0
```

## 3. 讀數的形狀（刻意不是一句「✅ 通過」）

回傳檔同一趟印四個數：

```
- 掃到 N 條 [HelpURL]
- 其中雲端目標 R 條（含 :// ⇒ 不驗，只數）
- 本地目標 L 條 ⇒ 解得到且檔案存在 M 條 ／ 缺 K 條
```

⇒ 判好的布林壞掉時，跟它對的時候長得一模一樣；**兩個數字擺在一起會自己說話**。
`K > 0` 時本 Cmd 先把清單寫進回傳檔，**然後** throw ⇒ 呼叫端拿到非零退出。
順序不可調換：要的是「缺哪幾條」，不是一句「失敗了」。

## 4. ⚠ 射程（本 Cmd 給不了的那幾格）

- ⛔ **不宣稱**那顆 `?` 按鈕點下去會開出 `UCL_MarkdownViewerPage` —— 那是 GUI 行為讀數，本 Cmd 只到「解得到路徑且檔案存在」。
- ⛔ **需要 Unity Editor**。走 `UCL_URL.ResolveURL` 本人的代價就是它必須在 Editor 內跑
  （Unity 型別 ＋ `UCL_LocalizeService.CurLang`）。那不是遺漏，是「不重寫第二把尺」的直接後果 ——
  重寫一份解析規則的話，兩把尺會漂移，而**漂移的樣子正好是「0 個缺檔」**。
- ⛔ **只涵蓋 `[HelpURL]` attribute。** 另有以 `public override string HelpURL =>` property 宣告的
  （@summit 2026-09-22 QA grep 讀數：**至少 47 處**，多數是 AgentCommand handler），
  走同一個 `ucl_core:` 前綴、同一支 `ResolveURL`、同樣會壞，而反射掃 attribute **看不到它們**
  ⇒ 報告那句「零缺檔」**與它們無關**。要不要擴口徑是另一張單。
  🩸 這一格是 QA 退回來的：條文的受詞逐字就是 attribute ⇒ 照字面沒有漏做，
  而**射程小的綠燈跟射程大的綠燈同形** —— 那正是本 Cmd 自己在防的那件事的變體。
- 掃描口徑是 **AppDomain 全部型別**，不是 `Assets/**/*.cs`：後者是自己搭的尺
  （對照：開單時的 grep 把 7 個**註解裡的示範字串**數成了真 attribute，而那批印出來跟真的一模一樣）。
  ⚠ 而「掃全部」**需要**後面那層分桶，⛔ 不能靠 `://` 自己分開：
  Unity 自家型別的 `HelpURL` 很多是**裸 slug**（`class-State`／`NestedStateMachines`），
  不含 `://` ⇒ 會被當成專案相對路徑 → `GetFullPath` → 檔案不存在 → **永遠紅燈**。
  🩸 這就是 `GetProjectAssemblyNames()` 存在的理由；活體讀數：外部宣告 200 條，而雲端桶只有 **4** 條。
  ⛔ 本行的前一版寫著「Unity 自家的 HelpURL 全是 https，會自己落進雲端桶，不必先分類」——
  那句**否證了它自己所在的那份 code 為什麼存在**，而它不會叫（分桶是對的、讀數是對的，只有解釋是反的）。
  ⇒ 它是在寫分桶**之前**寫的，而修完 code 沒有回頭改字：**我寫下的字比它描述的事實活得更久。**
- `{lang}` 的取值與 fallback 完全由 `ResolveURL` 決定 —— 回傳檔會把當前語系印出來，因為
  「缺檔」與「這個語系還沒翻譯」在檔案系統上同形。

## 5. 反向對照（驗這支驗證器自己）

一條驗證器只印綠燈時，看不出它是「真的沒缺」還是「根本沒在看」。
⇒ 塞一條指向不存在檔案的 `[HelpURL]`，重編後再跑：缺檔數必須 `+1` 且該型別出現在清單上、退出碼非零；
移除後回到原讀數。**兩個讀數同時在場才算數。**

## 6. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [建立新的 Agent Command Handler 工作流程](../../Workflows/Create_Cmd_Workflow.md)
