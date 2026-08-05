---
title: C# Coding Standards
description: UCL_Core C# 設定資料、字串 key 與外部 Process 的共用撰寫規範。
last_updated: 2026-08-05
target_audience: [AI_Agent, Gameplay_Programmer, Tools_Maintainer]
related:
  - Code_Comment_Standards.md | 程式碼註解規範 | 註解與文件化原則
  - AI_READABILITY_GUIDELINES.md | AI Readability Guidelines | 共用文件規範
---

# C# Coding Standards

## 設定與 JSON 資料

- 優先以具名 C# model（例如 `UnityJsonSerializable`）承載已知 schema，讓欄位、預設值與使用點可被編譯器檢查。
- 不要在一般業務流程直接裸用 `JsonData` 的字串索引、`GetString` 或 `GetBool` 來讀寫已知欄位。
- `JsonData` 可以保留在邊界層：解析外部 JSON、保存未知／可擴充欄位、或需要無損 round-trip 的 migration。使用時須把原因寫在註解中。
- schema 尚未穩定時，先建立最小的 typed projection；未知欄位必須被保留，不可因一次編輯而靜默遺失。

```csharp
// Good: known fields use a typed model.
NotifyConfig config = LoadNotifyConfig();
if (config.tavern_mirror.enabled) SendMirror();

// Boundary-only: preserve plugin-defined fields not represented by the model.
JsonData rawUnknownFields = LoadUnknownFieldsForRoundTrip();
```

## 字串 key 與設定欄位名稱

- 重複使用、代表 schema／EditorPrefs／JSON／routing 的字串 key，先宣告為具語意的 `const string`，再由所有讀寫點共用。
- key 常數應與使用類別同置；跨類別或跨 assembly 的公開 schema key 才使用 `public const`。
- 一次性 UI 文案、日誌內容或不具識別語意的字串不需要為了形式化而抽成常數。

```csharp
const string KeyTavernMirror = "tavern_mirror";
const string KeyWebhookUrls = "webhook_urls";

if (config.Contains(KeyTavernMirror))
    Write(KeyWebhookUrls);
```

> [!IMPORTANT]
> 新增 key 時，先搜尋既有名稱與 schema；不要用近似拼字另建一個常數，避免產生雙重設定來源。

## 外部 Process（硬規則）

> [!CAUTION]
> **C# 端開的每一顆外部 Process 都必須經過 `UCL_ProcessRegistryService` 登記。**
> 直接 `new Process()` / `Process.Start()` 之後不登記 = 那顆 process 沒有任何人管得到它。

**為什麼是硬規則**：Editor 的 domain reload / recompile 會把 C# 的 `Process` 物件整批清掉，
但**作業系統層的 process 不會跟著死**。於是每次重編都可能再生一顆，舊的變成沒有 handle 的孤兒 ——
累積下去就是 Tim 遇過的**屍潮**（重複開 process 直到電腦卡死）。
這一族的壞法特別難查：每一顆單看都正常，症狀只有「電腦越來越慢」。

```csharp
// 1) spawn 前先收掉同 tag 的舊 process（singleton 語意；跨 domain reload 也有效，
//    因為身分是從磁碟記錄讀回來的，不依賴 C# 端的 Process 物件）
UCL_ProcessRegistryService.KillAllByTag("my_daemon");

var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
proc.Start();

// 2) spawn 後立刻登記 — tag 是穩定識別字，description 要寫「這顆在做什麼」
UCL_ProcessRegistryService.Register(proc, "my_daemon",
    "這顆 process 在做什麼（給人看，也給誤殺防護判斷）", nameof(MyCaller));

// 3) 正常結束時反登記
UCL_ProcessRegistryService.Unregister(proc.Id, "my_daemon");
```

- **身分 = PID + process name + start time**，不是只有 PID —— PID 會被 OS 回收再發，
  只憑 PID 去 kill 會誤殺別人的 process（`UCL_ProcessStatus.PidReused` 就是為此存在）。
- `Register` 預設 `allowMultiple=false`（singleton）：登記時會先收掉既存同 tag。
  要「舊的先死新的才生」的嚴格順序，spawn 前自行呼叫 `KillAllByTag`。
- **短命的一次性 process**（跑完即退、不需要被管理，例如寫一個 json 就結束）可以不登記，
  但必須是**真的會自己退出**的那種；只要有「可能卡住」的可能性就要登記。
- 檢視／處置走 `UCL_ProcessAdminPage`。

參考實作：`UCL_ScreenStreamDaemon`（pre-spawn `KillAllByTag` + `Register` + 結束時 `Unregister`）。
