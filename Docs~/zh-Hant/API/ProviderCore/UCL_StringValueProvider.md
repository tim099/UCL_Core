---
title: UCL_StringValueProvider — 固定字串提供者
description: UCL_StringProvider 的預設實作，回傳 Inspector 指定的固定字串；implicit operator 由字串字面值生成的就是這個型別。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/ProviderCore/UCL_StringValueProvider.cs
namespace: UCL.Core
last_updated: 2026-08-07
target_audience: [AI_Agent, Developer]
aliases: [string value provider, 固定字串, StringValueProvider]
tags: [provider, serialize-reference]
related:
  - ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringProvider.md | UCL_StringProvider | 抽象基底（宣告欄位用這個型別）
---

# 📝 UCL_StringValueProvider — 固定字串提供者

`UCL_StringProvider` 的預設實作：回傳一個在 Inspector 指定的固定字串。
行為等同於原本直接用 `string` 欄位，差別是它可以被換成別的子類而使用端不必改。

## 1. 它是 implicit operator 的落點

```csharp
[SerializeReference] public UCL_StringProvider m_Msg = "早安";
// ↑ 等價於 new UCL_StringValueProvider("早安")
```

## 2. API

| 成員 | 行為 |
|---|---|
| `GetString()` | 回傳 `m_Value`；**`null` 收斂成 `string.Empty`**（Unity 反序列化未賦值的 string 欄位會是 null，統一在這裡收掉，免得每個呼叫點各防一次） |
| `ToString()` | 空值顯示 `(empty)`，非空回傳原文 |

> [!NOTE]
> `ToString()` 是 `UCL_ObjectFieldGUILayout` / `DrawList` 的顯示來源。
> 空字串會畫成一片空白，看起來像「這個欄位壞了」——所以顯式標成 `(empty)`，
> 讓「沒填」在 UI 上仍看得出來。**`GetString()` 不受影響**，照樣回傳空字串。

## 3. 建構

```csharp
new UCL_StringValueProvider()          // 空值（給序列化用）
new UCL_StringValueProvider("內容")     // 指定值
```
