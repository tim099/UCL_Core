---
title: PropertyEditor 探針
description: 查 Odin「UpdateOdinEditors → ClearEditorsAndRebuild」NRE 的觸發點；含 2026-09-15 的完整破案紀錄。
last_updated: 2026-09-15
target_audience: [AI_Agent, Gameplay_Programmer, Tech_Artist]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MissingReferencePage.md | Missing Reference 排查頁 | 另一條線：真的斷掉的引用（本案**不是**它）
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Invoke.md | Cmd_Invoke | 本探針的驅動通道
---

# UCL_PropertyEditorProbe

`UCL.Core.EditorLib.Diagnostics.UCL_PropertyEditorProbe`（Editor-only，static）

---

## 1. 三支入口（都走 `Cmd_Invoke`，都回傳**報告檔路徑**）

```bash
senate ucmd run Invoke --persona <me> \
    --arg type=UCL.Core.EditorLib.Diagnostics.UCL_PropertyEditorProbe \
    --arg member=<DumpToFile | DumpNullFieldsToFile | TryRebuildToFile>
```

| member | 做什麼 | 安全性 |
|---|---|---|
| `DumpToFile` | 每顆 PropertyEditor 家族視窗的狀態（tracker／追蹤對象／鎖／activeEditors） | **純讀** |
| `DumpNullFieldsToFile` | 每顆視窗的 **null／已銷毀** 欄位清單 —— 用對照組逼出嫌疑欄位 | **純讀** |
| `TryRebuildToFile` | ⚠ **刻意重現**：用跟 Odin 同一條反射路徑呼叫 `ClearEditorsAndRebuild` | 會真的重建 Inspector |

報告落在 `<專案>/Library/UCL_PropertyEditorProbe.md`（gitignore 內 ⇒ 天生不會被誤 commit）。

> [!IMPORTANT]
> ## 🩸 為什麼一定要落檔
> `Cmd_Invoke` 的回傳值**只進 `Debug.Log`，到不了呼叫端**（TASK-0172）——
> 「Success」與「拿到讀數」在 CLI 那一端**同形**。
> ⇒ 所以每支入口都自己寫檔並回傳**路徑**：路徑是短字串，就算只剩 `Debug.Log` 也讀得到；
> 而內容在磁碟上，agent 撈得走。

---

## 2. 🎯 2026-09-15 破案紀錄（本探針的第一個案子）

### 症狀

```
NullReferenceException
UnityEditor.PropertyEditor.ClearEditorsAndRebuild () (at <3852b49eca73473da3e3282226b08bd7>:0)
  …
Sirenix.OdinInspector.Editor.InspectorConfig.UpdateOdinEditors ()
Sirenix.OdinInspector.Editor.<>c:<.cctor>b__0_0()          ← 靜態建構子的 lambda
Sirenix.OdinInspector.Editor.<>c__DisplayClass14_0:<DelayAction>b__4()
UnityEditor.EditorApplication:Internal_CallUpdateFunctions()
```

`<.cctor>` ＝ 靜態建構子 ⇒ **不是使用者點出來的**，是 domain reload 後排進 Odin `DelayAction` 的。

### 結論

> **`PropertyEditor.ClearEditorsAndRebuild()` 假設自己被呼叫在一顆「UI 已經建好的 Inspector」上。
> 而 `PreviewWindow` 是 `PropertyEditor` 的子類，它從來不建那套 UI。**
>
> Unity 自己不會對 `PreviewWindow` 叫這支；**Odin 的反射呼叫會掃到每一個 `PropertyEditor` 實例**，
> 於是掃到了 Preview 視窗。

⇒ **這是 Odin ↔ Unity 的整合問題，不是專案資料壞掉。**
⇒ **繞法：把 `Preview` 視窗關掉。**

### 憑據（逐步，全部可重跑）

**① 重現 —— `TryRebuildToFile`，逐顆視窗各叫一次**

```
- [0] Preview   ：🔴 重現了 NullReferenceException
                   at UnityEditor.PropertyEditor.ClearEditorsAndRebuild () [0x00007]
                   in <3852b49eca73473da3e3282226b08bd7>
- [1] Inspector ：✅ 沒有丟例外
```

⭐ assembly hash 與 offset 跟回報的那條 stack **逐字相同** ⇒ 同一支方法、同一個點。
⭐ **逐顆各叫一次**是關鍵設計：只回報「有沒有炸」的話，分不出是哪一顆 —— 而那正是要找的東西。

**② 定位欄位 —— `DumpNullFieldsToFile` 的對照組**

| 欄位 | Preview（🔴） | Inspector（✅） |
|---|---|---|
| **`m_EditorsElement`**（`VisualElement`） | **null** | 非 null |
| `m_ScrollView` | null | 非 null |
| `m_SplitView` / `m_PreviewAndLabelElement` / `m_VersionControlElement` / `m_MultiEditLabel` | 全 null | 全非 null |
| `InspectorWindow.m_TrackerResetter` | null | 非 null |

⇒ Preview 缺的**全部是 Inspector 的 UI 骨架**（`VisualElement` 那一票）。
`ClearEditorsAndRebuild` 在 `[0x00007]` —— 方法極早期 —— 去碰其中一個，就是那個 NRE。

**③ 反向對照（排除掉的假說）**

| 假說 | 讀數 | 判定 |
|---|---|---|
| 「被檢視物件被刪掉」 | `m_InspectedObject` 在**兩顆視窗上都是已銷毀的假 null**，而只有 Preview 炸 | ⛔ **出局** |
| 「視窗鎖在死掉的物件上」 | 兩顆 `m_LockTracker.isLocked` 皆 `False` | ⛔ 出局 |
| 「`m_Tracker` 是 null」 | 兩顆都 ✅ 有 | ⛔ 出局 |
| 「`DefaultVolumeProfile` 的 5 個缺腳本子資產」 | 它是真的、值得清，但與本案無因果（見上一列） | ⛔ **本案出局**，另案處理 |

> [!CAUTION]
> ## ⛔ 那 5 個缺腳本子資產仍然存在，只是**不是這條 NRE 的成因**
> `Assets/Settings/DefaultVolumeProfile.asset` 裡有 5 個
> `Unity.RenderPipelines.Core.Editor.Tests` 的 VolumeComponent（測試組件不編 ⇒ 腳本永遠解析不到）。
> 那是另一條線，走 [Missing Reference 排查頁](UCL_MissingReferencePage.md)。
> 📌 早期把它當成「頭號嫌疑」是**還沒量就先排名** —— 留著這句是為了記得這個順序錯了。

---

## 3. 🩸 探針自己被實機讀數打臉一次（2026-09-15）

初版把 `m_LastInspectedObjectInstanceID == -1` 判成 🔴「解不出物件」。

實機第一次跑就打臉：兩顆視窗都是 `-1` 且 `activeEditors` 為 **0 個** ⇒ 那是**「現在沒選任何東西」**的正常狀態。

⇒ 這正是本探針要防的那個錯的**反面**：把「乾淨的沒有」報成「壞的」。
**一份會亂叫的報告跟沒有報告一樣沒用，而它看起來更權威。**

已修正為：`0` 與 `-1` 皆視為哨兵。
⚠ 定語：Unity 的 instanceID **負數是合法的**（場景物件就是負的）——
所以 `-1` 是**慣例上的哨兵**，不是型別保證。⛔ 沒有把「所有負數」當哨兵。

---

## 4. 設計上的兩個「不同形」

| 兩件事 | 為什麼不能同形 |
|---|---|
| 「找不到 `UnityEditor.PropertyEditor` 型別」 vs 「找到了但沒有視窗」 | 前者代表 Unity 換版、**整支探針的讀數都不能信**；後者只是現在沒開視窗 |
| 「讀不到某個欄位」 vs 「欄位是 null」 | 兩者都回 null 的話，一份「全是 null」的報告看起來像「全壞了」，而真相可能是探針整個對不上這個 Unity 版本。⇒ `ReadMember` 帶 `out bool oFound` |

⛔ 另外：**手動重現不出來也是一個讀數**，不是「沒問題」。
本探針跑的時刻跟 Odin 的靜態建構子不是同一刻；那條路的嫌疑時機是
「reload 後、視窗還沒 `OnEnable`」，而那一刻探針進不去。

---

## 5. 已知邊界

- Odin 是 **DLL**（專案內只有 1 個 `.cs`）⇒ `UpdateOdinEditors` 的內文讀不到，
  本檔對它的描述全部是**從 stack 與時機推的**，⛔ 沒有第二個證人。
- `ClearEditorsAndRebuild` 是 internal，簽章由 Unity 決定 ——
  探針同時找 static 與 instance 多載，找不到會明說（⛔ 不靜默跳過）。
- 報告固定檔名、每次覆寫，⛔ 不做輪替（輪替會長出一堆沒有人會回頭看的檔）。
