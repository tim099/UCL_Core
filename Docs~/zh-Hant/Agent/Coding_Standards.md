---
title: C# Coding Standards
description: UCL_Core C# 設定資料與字串 key 的共用撰寫規範。
last_updated: 2026-08-02
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
