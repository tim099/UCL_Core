---
title: UCL_StringProvider — 字串提供者
description: 讓「一個 string 欄位」可以被替換成任意求值策略的抽象基底；預設實作 UCL_StringValueProvider 回傳固定字串。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/ProviderCore/UCL_StringProvider.cs
namespace: UCL.Core
last_updated: 2026-08-07
target_audience: [AI_Agent, Developer]
aliases: [string provider, 字串提供者, StringProvider]
tags: [provider, serialize-reference, json]
related:
  - ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringValueProvider.md | UCL_StringValueProvider | 預設實作（回傳固定字串）
  - ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringBookRecommendProvider.md | UCL_StringBookRecommendProvider | 子類實例（隨機推薦藏書，Editor-only）
  - ucl_core:UCL_Core_Scripts/InterfaceCore/UCL_PolymorphicHelper.cs | UCL_PolymorphicHelper | 多型判定 SSOT（[SerializeReference] 是唯一觸發訊號）
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderTimeRulePage.md | UCL_BartenderTimeRulePage | 第一個消費端（時間規則的多行提醒內文）
---

# 🔤 UCL_StringProvider — 字串提供者

## 1. 這是什麼

把「一個字串欄位」從**固定值**升級成**求值策略**。使用端只認 `GetString()`，
不必知道那個字串是寫死的、查表來的、還是依時間算出來的。

這是 Provider 模式的字串版本。同型的做法可套用到任何值型別（float / int / bool…）——
基底負責契約與轉換，子類負責「值從哪來」。

```
UCL_StringProvider        (abstract)  ← 使用端宣告這個型別
├── UCL_StringValueProvider           ← 預設實作：回傳 Inspector 指定的固定字串
└── UCL_StringBookRecommendProvider   ← 隨機推薦 N 本藏書（Editor-only，住在 Books 旁邊）
```

## 2. 怎麼用

### 宣告欄位

```csharp
// 單一欄位
[SerializeReference] public UCL_StringProvider m_Title = "預設標題";   // implicit → UCL_StringValueProvider

// 清單（一個元素一行之類的用法）
[SerializeReference] public List<UCL_StringProvider> m_Lines = new List<UCL_StringProvider>();
```

> [!WARNING]
> **`[SerializeReference]` 不可省略。** 它是 UCL 判定「這個欄位要多型」的**唯一訊號**
> （見 `UCL_PolymorphicHelper.IsPolymorphicField`）。少了它，序列化只會存下宣告型
> `UCL_StringProvider`、丟掉子類資料，**而且不會報錯** —— 讀回來是空的或預設值。

### 取值

```csharp
string a = m_Title.GetString();   // 明確
string b = m_Title;               // implicit operator（null 時回 string.Empty，不會 NRE）
```

### 在 Editor 頁編輯

**不要自己刻 TextArea 陣列** —— 用 UCL 內建：

```csharp
UCL_GUILayout.DrawList(m_Lines, iDataDic.GetSubDic("Lines"), "內文（每個元素一行）", true);
```

`DrawList` 自帶新增 / 刪除 / 搬移與**多型子類下拉**。自己刻的話，日後新增的
`UCL_StringProvider` 子類會編得過、但在那一頁選不到。

## 3. 兩個刻意的設計取捨

| 取捨 | 理由 |
|---|---|
| implicit `operator string` 在 **null 時回 `string.Empty`** | 「null provider 回傳型別預設值」是本家族的共同慣例，使用端不必每個取值點防 NRE。**代價**：「沒有 provider」與「provider 回傳空字串」在這個轉換後分不出來 —— 需要分辨的呼叫端請自行判 `provider == null` 再呼叫 `GetString()`。 |
| **不引入第三方 Inspector 套件的顯示用 attribute** | UCL_Core 是**跨專案共用模組**，消費端專案未必安裝那些套件；一旦引入，缺套件的專案會直接編不過。類別說明一律走 `<summary>`。 |

## 4. 序列化

`UCL_StringProvider` 繼承 `UCL.Core.JsonLib.UnityJsonSerializable`，
存讀走 UCL 內建的 `JsonConvert.SaveFieldsToJsonUnityVer` / `LoadFieldFromJsonUnityVer`。

含多型元素的清單存出來長這樣（ClassName 是還原子類的依據）：

```json
"reminder_lines": {
  "ClassName": "System.Collections.Generic.List`1[[UCL.Core.UCL_StringProvider, UCL_Core, ...]], mscorlib, ...",
  "ClassData": [
    { "ClassName": "UCL.Core.UCL_StringValueProvider, UCL_Core, ...",
      "ClassData": { "Value": "第一行" } }
  ]
}
```

> [!NOTE]
> Unity 內建的 `JsonUtility` **也**支援 `[SerializeReference]`（會產生 `rid` + `references` 區塊，
> 2026-08-07 實測確認），所以兩條路都走得通。
> UCL_Core 統一走 `JsonData`，理由是與其他 UCL 資料同一套 idiom、且 `CloneObject()` 等
> 既有工具直接可用 —— 不是因為 JsonUtility 做不到。

## 5. 要新增子類時

繼承 `UCL_StringProvider`、實作 `GetString()`，就會自動出現在所有
`[SerializeReference]` 欄位的下拉選單裡（`UCL_PolymorphicHelper.GetConcreteSubtypes` 掃得到），
**既有頁面一行都不用改**。

```csharp
public class UCL_StringTimeProvider : UCL_StringProvider
{
    [SerializeField] private string m_Format = "HH:mm";
    public override string GetString() => System.DateTime.Now.ToString(m_Format);
    public override string ToString() => $"Now({m_Format})";
}
```

> [!TIP]
> `GetString()` **不保證每次回傳相同結果**（隨機 / 時間相關的子類是合法的）。
> 呼叫端若需要同一幀內一致，請自行取一次存起來，不要重複呼叫當快取用。
