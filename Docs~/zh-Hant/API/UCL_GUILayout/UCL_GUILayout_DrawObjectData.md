---
title: DrawObjectData — 自動繪製物件介面 + 四個客製化介面
description: 用 UCL_GUILayout.DrawObjectData 反射自動畫出整個物件的編輯介面；再用 UCLI_IsEnable / UCLI_ShortName / UCLI_NameOnGUI / UCLI_FieldOnGUI 四個介面逐層接管顯示。
source_files: |
  UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs
  UCL_Core_Scripts/UICore/UCL_GUILayout.DrawList.cs
  UCL_Core_Scripts/UICore/UCL_GUILayout.cs
  UCL_Core_Scripts/UICore/UCL_ObjectFieldGUILayout.cs
  UCL_Core_Scripts/InterfaceCore/UCLI_NameOnGUI.cs
  UCL_Core_Scripts/AttributeCore/UCL_FoldoutGroupAttribute.cs
namespace: UCL.Core.UI
last_updated: 2026-08-19
target_audience: [AI_Agent, Developer]
aliases: [DrawObjectData, 自動繪製, 反射繪製, UCLI_IsEnable, UCLI_ShortName, UCLI_NameOnGUI, UCLI_FieldOnGUI]
tags: [imgui, editor-page, reflection]
related:
  - ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_Overview.md | UCL_GUILayout_Overview | 整體 API 速查
  - ucl_core:Docs~/{lang}/API/UCL_GUIStyle/UCL_GUIStyle_Overview.md | UCL_GUIStyle_Overview | 樣式與 DPI 縮放
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderTimeRulePage.md | UCL_BartenderTimeRulePage | 實例：整頁只用一行 DrawObjectData
---

# 🪞 DrawObjectData — 自動繪製物件介面

## 1. 一行畫完一整頁

**先別手刻欄位。** `DrawObjectData` 用反射走訪物件的欄位，自動畫出可編輯介面 ——
巢狀物件、`List` / `Dictionary`、`[SerializeReference]` 多型下拉、折疊狀態全部內建。

```csharp
protected override void ContentOnGUI()
{
    UCL_GUILayout.DrawObjectData(m_Data, m_Dic.GetSubDic(nameof(m_Data)), "顯示名稱", false);
}
```

| 參數 | 意義 |
|---|---|
| `iObj` | 要畫的物件 |
| `iDataDic` | GUI 狀態容器（折疊 / 暫存編輯值）。**每個物件給自己的 SubDic**，共用會互相吃到對方的展開狀態 |
| `iDisplayName` | 標題 |
| `iIsAlwaysShowDetail` | `true` = 不畫標題列、永遠展開內容；`false` = 畫可折疊的標題列 |

> [!WARNING]
> `iIsAlwaysShowDetail: true` 會**跳過整段標題列** —— 下面講的 `UCLI_NameOnGUI` 與
> `UCLI_IsEnable` 都畫在標題列，所以那兩個介面在 `true` 時**不會生效**。

## 2. 四個客製化介面 —— 由小到大接管

預設畫法不夠時，**不要退回手刻整頁**，而是實作對應介面，只接管你要改的那一層：

| 介面 | 接管範圍 | 典型用途 |
|---|---|---|
| `UCLI_ShortName` | 顯示**名稱**文字 | 讓清單元素顯示「這一筆是誰」而不是型別名 |
| `UCLI_IsEnable` | 名稱前面多一個 **CheckBox** | 就地開關 enable，不必展開 |
| `UCLI_NameOnGUI` | **整條標題列** | 標題列要放按鈕 / 狀態燈 / 自訂排版 |
| `UCLI_FieldOnGUI` | **整個欄位的繪製** | 預設畫法完全不適用（例如要畫表格、圖片編輯器） |

### 2.1 `UCLI_ShortName` — 顯示名稱

```csharp
public interface UCLI_ShortName { string GetShortName(); }
```

⚠ **同一個介面在兩種位置的行為不同**（實測 2026-08-07）：

| 位置 | 結果 |
|---|---|
| 一般欄位 | `欄位名(ShortName)` —— **附加**在欄位名後面 |
| **List 元素** | `({索引}) {ShortName}` —— **取代**整個元素標籤 |

實例（`UCL_BartenderTimeRule`）：

```csharp
public string GetShortName() => this.ToString();
public override string ToString() => $"[{time_hhmm}]:{id}";
```

清單裡就顯示成 `(0) [23:50]:default-sleep-2350` —— 不必展開就知道是哪一條規則。

> [!TIP]
> 沒有實作此介面時，List 元素退回顯示**型別名稱**（每個元素長得一模一樣，等於沒有資訊）。
> 只要是會被放進 List 的資料類別，都值得實作它。

### 2.2 `UCLI_IsEnable` — 名稱前的 CheckBox

```csharp
public interface UCLI_IsEnable { bool IsEnable { get; set; } }
```

實例（`UCL_BartenderTimeRule` 把它接到既有欄位，不新增狀態）：

```csharp
public bool IsEnable { get => enabled; set => enabled = value; }
```

`DrawObjectData` 會在名稱前畫一個 CheckBox 並直接讀寫 `IsEnable`。
**接到既有欄位**是重點 —— 另開一個 `m_IsEnable` 就會有兩個真相來源。

### 2.3 `UCLI_NameOnGUI` — 整條標題列

```csharp
public interface UCLI_NameOnGUI
{
    void NameOnGUI(UCL_ObjectDictionary iDic, string iDisplayName, UCL_GUILayout.DrawObjectParams iParams);
}
```

> [!WARNING]
> **它與 `UCLI_IsEnable` 互斥。** 原始碼是 if / else：實作了 `UCLI_NameOnGUI`，
> 就走不到 else 分支，於是 **`UCLI_Icon` 圖示、`UCLI_IsEnable` CheckBox、名稱 Label、
> `[SerializeReference]` 型別下拉全部都不會畫**（`HelpURL` 按鈕例外，兩邊都畫）。
> 要保留哪個，就得自己在 `NameOnGUI()` 裡畫回來。
>
> 症狀是「加了 NameOnGUI 之後 CheckBox 不見了 / 多型下拉不見了」—— 不是壞掉，是被接管了。

參考實作：`UCL_AssetEntry` / `UCL_ModulePlaylist` / `UCL_AddressableData`。

### 2.4 `UCLI_FieldOnGUI` — 整個欄位

```csharp
public interface UCLI_FieldOnGUI
{
    /// return new data if the data of field altered
    object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams);
}
```

最大範圍的接管。慣用寫法是**先叫預設繪製、再往下追加**，而不是整個重寫：

```csharp
public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, DrawObjectParams iParams)
{
    var result = UCL_GUILayout.DrawField(this, iParams);   // 先畫預設
    bool aIsShow = UCL_GUILayout.Toggle(iDataDic, "CSV_Editor_Show", iFieldName);
    if (!aIsShow) return result;
    // …追加自訂內容（CSV 預覽表格）
    return result;
}
```

（節錄自 `UCL_CSVAsset`；其他實例：`UCL_ImageEditor` / `UCL_LocalizeAsset` / `UCL_BundleAsset`。）

**回傳值即欄位新值** —— 沒改就回傳 `DrawField` 的結果，不要回 `null`。

## 3. 標題列的實際繪製順序

沒有 `UCLI_NameOnGUI` 時（`UCL_GUILayoutDrawObject.cs` 約 630–675 行）：

```
[HelpURL 按鈕]  ← 型別上有 [HelpURL] attribute 才畫
[UCLI_Icon 圖示]
[UCLI_IsEnable CheckBox]
[顯示名稱 Label]
[型別下拉]      ← 欄位有 [SerializeReference] 才畫（多型子類選單）
```

有 `UCLI_NameOnGUI` 時：

```
[HelpURL 按鈕]
[NameOnGUI() 全權接管其餘部分]
```

## 3.5 `[UCL_FoldoutGroup]` —— 欄位分組折疊（2026-08-19）

Odin `FoldoutGroup` 的 UCL 版，**範圍語意**：標在一段的**第一個欄位**上，
從它開始往下的欄位都屬於這一組，**直到碰到下一個 `[UCL_FoldoutGroup]`** 為止。
純顯示層 —— 不影響序列化、不影響 JSON 鍵名、不影響欄位值，**也不改變欄位順序**。

```csharp
public int m_Normal;                                          // 不在任何組
[UCL_FoldoutGroup("Advanced")]    public int   m_Retry;       // ↓ Advanced 從這裡開始
public float m_Timeout;                                       // 同組 —— 不必再標
[UCL_FoldoutGroup("Debug", true)] public bool  m_VerboseLog;  // 上一組到此結束；本組預設展開
[UCL_FoldoutGroup("")]            public int   m_Tail;        // 空組名＝顯式結束分組
```

| 行為 | 規則 |
|---|---|
| 範圍 | 從標記的欄位起，**到下一個標記為止**（沒有下一個就收到型別結尾） |
| 結束分組 | `[UCL_FoldoutGroup("")]`（空組名）—— 之後的欄位回到未分組 |
| 欄位順序 | **完全不動**（分組是「畫到哪裡為止」，不是把欄位搬到一起） |
| 預設狀態 | **收合**（`expanded: true` 才預設展開）—— 分組是為了收掉平常不看的東西 |
| 折疊狀態存哪 | 該物件的 `UCL_ObjectDictionary` → `FoldoutGroup/<組名>/Expanded`；**使用者動過就以他的選擇為準**，預設值只在沒被動過時生效 |
| 收合時 | **整組欄位一行都不畫**（不是縮小，是不畫） |
| 同名出現兩段 | 兩個框、**共用同一個折疊狀態**（狀態以組名為 key）—— 同名就是同一個概念 |
| 組名在地化 | 同 `[Header]`：是 `UCL_LocalizeManager` 既有 key 就翻譯，否則原樣顯示 |

⚠ **組名裡的 `/` 目前是字面字元，不是巢狀路徑**（Odin 支援 `A/B` 巢狀，本版刻意不做 ——
沒有現場需要它；之後要加時呼叫端一行都不用改）。

**驗收**（範圍層可不開 GUI 直接量，任意型別都能問）：

```bash
# 樣本型別
senate ucmd run Invoke --persona <me> --arg type=UCL.Core.TestLib.UCL_FoldoutGroupSample --arg member=SelfTest
# → fields=8 | m_Plain1(-) m_AdvA(Advanced) m_AdvB(Advanced) m_AdvC(Advanced)
#              m_DebugVerbose(Debug,open) m_DebugLevel(Debug,open) m_Plain2(-) m_Plain3(-)

# 真實資產（換 --arg args= 就能問任何型別）
senate ucmd run Invoke --persona <me> --arg type=UCL.Core.TestLib.UCL_FoldoutGroupSample     --arg member=Dump --arg paramTypes=System.String --arg args=LittleYellow.HSceneAsset
# → HSceneAsset fields=28 | hScene(場景與角色) skeletons(場景與角色) sceneObjects(場景與角色)
#                           clickAreas(互動操作) … config(畫面顯示與特效)
```

⚠ 這支只證明**範圍傳遞與組名解析**；折疊框畫不畫得出來、收合有沒有真的消失，
**只有真的重繪才算數**，別把它的綠燈讀成「分組畫對了」。

## 4. 常見誤用

| 症狀 | 原因 |
|---|---|
| 加了 `UCLI_NameOnGUI` 後 CheckBox / 多型下拉不見了 | 兩者互斥，見 §2.3 —— 要自己畫回來 |
| `UCLI_IsEnable` / `UCLI_NameOnGUI` 沒反應 | `iIsAlwaysShowDetail: true` 跳過整段標題列 |
| List 每個元素長得一樣 | 沒實作 `UCLI_ShortName`，退回型別名 |
| 折疊狀態互相干擾 / 收不起來 | 多個物件共用同一個 `UCL_ObjectDictionary`，或與 `PopupSearchCache` 共用（資料重載路徑的 `Clear()` 會把折疊值一起清掉） |
| 多型欄位存檔後子類資料不見 | 欄位漏了 `[SerializeReference]` —— 那是多型的**唯一觸發訊號**，缺了不會報錯 |
| 最後一組吃掉了型別結尾所有欄位 | 範圍語意 —— 沒有下一個標記就收到底；要提前結束用 `[UCL_FoldoutGroup("")]`（見 §3.5） |
| 折疊組永遠是收合的 | 預設就是收合；要預設展開寫 `[UCL_FoldoutGroup("X", true)]`，且**該格沒被人動過**時才生效 |

## 5. 什麼時候不要用

`DrawObjectData` 畫的是**資料的形狀**。當畫面需要的是「操作流程」而不是「資料欄位」時
（例如流程按鈕列、驗證訊息、跨欄位的引導），那部分仍該手寫，
與 `DrawObjectData` 並列即可 —— 兩者混用是預期用法，不是二選一。
