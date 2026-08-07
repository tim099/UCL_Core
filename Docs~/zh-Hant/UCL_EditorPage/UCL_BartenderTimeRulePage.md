---
title: UCL_BartenderTimeRulePage — 時間規則編輯頁
description: 從酒保管理頁抽離的 TimeRule 專用編輯器 — 每條規則可就地修改時間與多行內文（UCL_StringProvider 清單），顯式存檔（沒按存檔不寫回 json）。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderTimeRulePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-07
target_audience: [AI_Agent, Developer, Designer]
aliases: [time rule editor, 時間規則編輯, 酒保報時編輯]
tags: [chat-tavern, bartender, editor]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderAdminPage.md | UCL_BartenderAdminPage | 入口父頁（時間規則區的「✏️ 開啟時間規則編輯頁」按鈕）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderIO.cs | UCL_BartenderIO | time_rules.json 的唯一讀寫點
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderDaemon.cs | UCL_BartenderDaemon | 觸發端 — 呼叫 GetReminderBody() 照稿廣播、格式錯誤的 time_hhmm 靜默跳過
  - ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringProvider.md | UCL_StringProvider | 內文每一行的型別（多型，可換求值策略）
  - ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md | DrawObjectData | 本頁的自動繪製機制與四個客製化介面
---

# ⏰ UCL_BartenderTimeRulePage — 時間規則編輯頁

入口：酒保管理頁 → 時間規則區 →「✏️ 開啟時間規則編輯頁」。

## 職責（與 AdminPage 的分工）

| 頁 | 能做什麼 |
|---|---|
| AdminPage 時間規則區 | 唯讀總覽 + 跳轉本頁；「🕐 報時」批次開關（Daemon 區） |
| **本頁** | 每條規則就地編輯 **time_hhmm** 與 **reminder_lines（`UCL_StringProvider` 清單，一元素一行）**、enabled 開關、刪除、新增、改變順序 |

## 存檔語意（本頁的核心設計）

- **所有編輯只動記憶體工作副本**（time_rules.json 的 deep copy）。
- **按 TopBar「💾 存檔」才寫回 json**；沒按就不寫（標題顯示 `*未存檔`）。
- 「↻ 重新載入」捨棄未存修改重讀檔案。
- 有未存修改按 Back → 彈三選一：存檔離開 / 取消 / 捨棄修改離開 — 丟失必須是看得見的選擇。

## 存檔前驗證（擋在寫檔前的理由）

daemon 端 `TryParseHHmm` 對格式錯誤的時間**靜默跳過**（規則永不觸發、不報錯）——所以本頁在寫檔前擋：

| 檢查 | 不過的後果（若放行） |
|---|---|
| `time_hhmm` 必為合法 `HH:mm` | 規則悄悄死掉（daemon 跳過, 零訊息） |
| `id` 非空、全清單不重複 | `fired_today` 去重靠 id, 重複 id 互吃觸發 |
| `GetReminderBody()` 非空白 | 廣播空訊息（**判空看組裝後結果** —— 有 provider 但每個都求值成空字串，播出去一樣是空的） |

任一不過 → **整份不寫**、紅字定位到規則 id。

## 資料格式與遷移（2026-08-07）

`reminder_msg`（單一 string）已淘汰，改為 `reminder_lines`（`List<UCL_StringProvider>`，一元素一行）。

| | 舊 | 新 |
|---|---|---|
| 欄位 | `reminder_msg: string` | `reminder_lines: List<UCL_StringProvider>`（`[SerializeReference]`） |
| 序列化 | `JsonUtility` | **UCL.Core.JsonLib**（`JsonData` / `JsonConvert`）—— 多型需要存 ClassName |
| 廣播 | 直接取字串 | `GetReminderBody()` 逐行求值後以 `
` 串接 |

**遷移是自動的、反序列化時就地發生** —— 實作在 `UCL_BartenderTimeRule.DeserializeFromJson()` 的 override：

```csharp
public override void DeserializeFromJson(JsonData iJson)
{
    base.DeserializeFromJson(iJson);
    if (iJson.Contains(NewReminderLinesKey)) return;      // 已是新格式 → 不動
    if (!iJson.Contains(LegacyReminderMsgKey)) return;    // 兩邊都沒有 → 沒東西可遷
    reminder_lines.Add(new UCL_StringValueProvider(iJson.GetString(LegacyReminderMsgKey, "")));
}
```

- **觸發判準是「新欄位在不在」**，不是「舊欄位在不在」—— 新欄位缺席才是「這是舊檔」的充分訊號；
  反過來判會漏掉兩欄並存的中間態檔案。
- **為什麼放在型別自己身上而不是 IO 層**：override 的參數就是「這一條規則的原始 JSON」，
  舊欄位當場看得到、內部欄位當場寫得進去。放在 IO 層則要把反序列化結果與來源陣列按索引配對回去，
  而那個配對是一條沒有防護的隱含假設（順序一致），schema 一動就會靜默錯位。
  IO 只負責「檔案 ↔ 物件」，**形狀相容是型別自己的事**。
- 偵測到舊 `reminder_msg` → 轉成**一個** `UCL_StringValueProvider`。
- **不對舊字串做 `
` 拆行** —— 拆了就改變作者原本的分行意圖，而且拆錯沒人看得出來。
- **讀檔不回寫**：遷移結果留在記憶體，等下一次存檔才落盤 —— 開個頁面不該默默改寫使用者的資料檔。
- 存檔後該檔即為純新格式，**不保留舊欄位**（兩個欄位並存正是「同一事實兩個來源」的起點）。

> [!NOTE]
> 2026-08-07 實測（混合格式檔：8 條舊格式 + 1 條新格式）：
> **恰好 8 筆遷移**（新格式那條正確跳過）、9 條全數載入、存檔後 `reminder_msg` 歸零，
> 8 條原始內文與遷移前**逐字相同**。

> [!WARNING]
> **序列化器必須存讀成對。** `triggers` 與 `time_rules` 兩者的 Load/Save 都走 `JsonData`；
> `state` / `assignments` 兩者都走 `JsonUtility`。
> 一組 Load/Save 用不同序列化器 = 同一份資料兩種形狀，而它**不會報錯**。

## 已移除的欄位與機制（2026-08-07）

Tim 決定移除 HP penalty 整套機制與 `target_id`：

| 移除項 | 原用途 | 現在怎麼做 |
|---|---|---|
| `target_id` | 廣播開頭自動加 `@<target>` | 直接寫進 `reminder_lines`（內文的一部分） |
| `grace_minutes` | penalty 寬限期 | — |
| `penalty_enabled` / `penalty_interval_minutes` / `penalty_target` | 逾時後每 N 分鐘廣播累積 HP 扣血 | — |

連帶移除：daemon 的 penalty 掃描（Pass 3）與 `FirePenaltyWarning`、
inline 規則語法的 `target=` / `grace=` / `penalty=` / `penalty_interval=`、
`op=time_add` 的對應參數、`op=time_list` 表格的對應欄位、
`RegisterTimeRule()` 的五個參數，以及兩個管理頁的 penalty 標籤。

`fired_today_keys` 因此只剩 `"YYYY-MM-DD::rule_id"` 一種格式（原本還有 `::penalty::N`）。

> [!NOTE]
> 2026-08-07 實測：移除後 9 條規則全數正常載入、存檔後五個欄位在 json 中歸零、內文完好 9/9。

## 注意

- 清單依 `time_hhmm` 排序**僅為顯示**，存檔保持底層順序（diff 穩定）。
- 新增的規則帶佔位內文，建立後直接在上方卡片編輯；同樣等存檔才落地。
- 內文由 `reminder_lines` 逐行求值後**以換行串接**，組裝只有 `UCL_BartenderTimeRule.GetReminderBody()` 一個入口 —— daemon 與本頁預覽共用，不會出現「預覽看到的」與「播出去的」不一致。
- 預設子類 `UCL_StringValueProvider` 是靜態字串，daemon 照稿廣播（**動態組裝的只剩 ⏰ 標頭**）。內嵌「條數 / 清單快照」類內容會漂移——寫指路不寫復誦（2026-07-31 Hard Rules 幽靈廣播血證）。
- **要 @ 誰請直接寫進 `reminder_lines`** —— 那是內文的一部分，不該是另一個欄位（2026-08-07 移除 `target_id` 的理由）。
- **整頁介面由 `UCL_GUILayout.DrawObjectData` 反射自動繪製**（`ContentOnGUI` 只有一行），
  不是手刻欄位 —— 規則新增欄位時本頁零改動。顯示上的客製化全靠 `UCL_BartenderTimeRule` 實作的兩個介面：

  | 介面 | 實作 | 畫面效果 |
  |---|---|---|
  | `UCLI_ShortName` | `GetShortName() => ToString()`，`ToString() => $"[{time_hhmm}]:{id}"` | 清單顯示 `(0) [23:50]:default-sleep-2350`，不必展開就認得出是哪條 |
  | `UCLI_IsEnable` | `IsEnable { get => enabled; set => enabled = value; }` | 名稱前一個 CheckBox，就地開關（**接到既有 `enabled` 欄位**，不另開狀態） |

  內文清單（`reminder_lines`）由 `DrawObjectData` 內部的 `DrawList` 畫，自帶新增／刪除／搬移＋多型子類下拉；
  日後新增 `UCL_StringProvider` 子類本頁一行都不用改。細節見
  [DrawObjectData](../API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md)。
