---
title: Missing Reference 排查頁
description: 掃出「欄位指著已刪除物件」與「缺腳本 Component」，逐筆定位並可就地清空／移除。
last_updated: 2026-09-15
target_audience: [AI_Agent, Gameplay_Programmer, Tech_Artist]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ToolBoxPage.md | ToolBox | 本頁的入口（🩺 診斷與修復 組）
---

# Missing Reference 排查頁

**入口**：ToolBox →「🩺 診斷與修復」→「Missing Reference 排查」

---

## 1. 它在解什麼問題

> **乾淨的 `null` 與斷掉的引用，在 Inspector 上都畫成 `None`。**
> 而只有後者會在別人遍歷它的時候炸。

Unity 把物件引用序列化成 instanceID／fileID。目標被刪掉之後，那個欄位**不會變成乾淨的 null** ——
它變成「id 還在、物件已經沒了」的半死狀態。肉眼與 Inspector 都分不出來。

⇒ 本頁就是把那個「分不出來」變成一份清單。

---

## 2. 它掃兩類，而兩類的修法與可逆性不同

| | ⚠ 斷掉的物件引用 | 🔴 缺腳本 Component |
|---|---|---|
| 判準 | `objectReferenceValue == null` **且** `objectReferenceInstanceIDValue != 0` | `GetComponents<Component>()` 裡那一格是 `null`／`LoadAllAssetsAtPath` 回傳陣列裡那一格是 `null` |
| 成因 | 被指向的資產／物件被刪了 | 腳本檔被刪／改名／它的 assembly 沒編進來 |
| 修法 | 一般欄位 ⇒ 清空；**陣列元素 ⇒ 整格移除** | 移除整個 Component |
| 可逆 | ✅ 可逆（重新指定即可） | ⛔ **不可逆**（那個 Component 上的資料一起消失） |
| 批次鈕 | ✅ 有 | ⛔ **沒有**（每一顆都要單獨確認） |

> [!IMPORTANT]
> ## ⚠ 判準為什麼要兩個條件
> 只看 `objectReferenceValue == null` 會把**每一個沒填的欄位**都報成錯。
> 那份清單長到沒有人會看 —— **而它看起來跟真的掃描一模一樣。**

> [!CAUTION]
> ## ⚠ 陣列元素為什麼是「移除」不是「清成 null」
> `List<T>` 裡清成 null 會留下一個**洞**，而洞照樣會被 `foreach` 走到。
> 遍歷它的那段程式碼（例如 `VolumeProfile` 逐個畫 component）**還是會炸** ——
> 只是從「炸在半死的引用」變成「炸在 null」。
> ⇒ **症狀換了位置，而修的人以為自己修好了。**
>
> 實作上還有一個 Unity 怪癖：物件引用陣列的 `DeleteArrayElementAtIndex`
> **第一次只會把該格設成 null**，要再刪一次才真的縮短陣列。
> 本頁用「長度有沒有變」判定要不要刪第二次，⛔ 不靠記憶。

---

## 3. 掃描範圍是顯式勾選的

| 勾選 | 預設 | 代價 |
|---|---|---|
| 開啟中的場景（含未存檔改動） | ✅ 開 | 便宜 |
| 全部 Prefab（`t:Prefab`） | ⛔ 關 | 大專案數十秒 |
| 全部 ScriptableObject（`t:ScriptableObject`） | ⛔ 關 | 同上 |

> **為什麼全庫掃描預設關**：預設打開會讓人以為本頁很慢，
> 而「慢」與「卡住」在按下按鈕的那一刻分不出來。

> [!WARNING]
> ## 「一個目標都沒掃到」與「掃了 N 個、零問題」**在輸出上不同形**
> 前者是**範圍沒勾對**（或場景沒開），後者才是好消息。
> ⇒ 這兩句話要人做的事完全相反，所以不能共用一個「沒問題」。

---

## 4. 🩸 ScriptableObject 為什麼走 `LoadAllAssetsAtPath`

初版寫的是 `LoadAssetAtPath<ScriptableObject>` —— **那只拿得到主資產，看不到子資產（sub-asset）。**

2026-09-15 實測血證：

```
Assets/Settings/DefaultVolumeProfile.asset
  → 5 個 m_Script: {fileID: 0} 的 VolumeComponent 子資產
     CopyPasteTestComponent1 / 2 / 3
     VolumeComponentSupportedEverywhere
     VolumeComponentSupportedOnAnySRP
     （皆為 Unity.RenderPipelines.Core.Editor.Tests 的測試類別 ——
       正常專案不編那個組件 ⇒ 腳本永遠解析不到）
```

⇒ 用 `LoadAssetAtPath<ScriptableObject>` 掃的話，這一份會被報成**乾淨** ——
而它正是本頁最該抓到的那一種。

📌 這一格是「工具寫完之後，用一條完全不經過工具的路徑（`grep m_Script: {fileID: 0}`）
去對帳」才發現的。⛔ 編譯綠燈證明不了掃描範圍對不對。

---

## 5. ⛔ 本頁**不宣稱**能修的東西

```
NullReferenceException
UnityEditor.PropertyEditor.ClearEditorsAndRebuild ()
  …
Sirenix.OdinInspector.Editor.InspectorConfig.UpdateOdinEditors ()
```

這條 stack **炸在 Unity 內部**，不是欄位斷掉造成的。
最常見的成因是「Inspector 正在追蹤的物件上有缺腳本的 Component」。

⇒ 本頁能做的是：**先把 🔴 那幾筆清掉，再看它有沒有消失。**
沒消失就不是這個原因 —— 而那也是一個答案，不是失敗。

⛔ 把兩者混成一個「missing 數字」會讓人拿錯的那一格去對錯的症狀。所以本頁**分開報**。

---

## 6. 安全性

- **掃描純讀**：不 Apply、不 SetDirty。掃完場景仍該是乾淨的（除非它本來就髒）。
- **修復前重驗**：掃描與按下按鈕之間可能隔很久 ——
  照著一份過期清單改檔是這類工具最貴的失效方式。每一筆修復前都重新確認「它現在仍然是壞的」。
- **不自動存檔**：只 `MarkSceneDirty` / `SetDirty`，存不存由人按 Ctrl+S。
- **批次修復印逐筆失敗理由**（Console）：
  只印「已修復 N 筆」的話，失敗的那幾筆會安靜留在磁碟上，而使用者以為全清了。

---

## 7. 已知邊界

| 邊界 | 說明 |
|---|---|
| 不掃未開啟的場景 | 要掃就先開它。⛔ 不自動開場景 —— 那會動到人的工作狀態 |
| 不掃 UCL_Asset 的 ID 引用 | 那是**專案層**的斷點（`entry.ID` 指向已刪除的 asset），與 Unity 序列化無關，判準也不同。見 `UCL_AssetReferenceUtil` |
| 子資產缺腳本只報不修 | 移除子資產要看容器型別（例：`VolumeProfile.components`）。目前的修法是把**容器裡指向它的那一格**移除，孤兒區塊留在檔案裡由 Unity 下次重新序列化時丟掉 |
| 清單只畫前 300 筆 | 其餘走 Console。⚠ 計數是全量，畫的才截斷 |
