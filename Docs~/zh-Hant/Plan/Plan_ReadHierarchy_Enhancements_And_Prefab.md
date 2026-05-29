---
last_updated: 2026-05-29
related:
  - ucl_core:Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ReadHierarchy.md | Cmd_ReadHierarchy API | 已 ship 的 Hierarchy 讀取 Cmd 文件
  - ucl_core:Docs~/zh-Hant/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md | AgentCommand 架構 | 系統整體 spec
---

# Plan：Cmd_ReadHierarchy 強化與 Prefab 模式（Read Hierarchy Enhancements & Prefab Mode）

> 狀態：**Plan（待 Tim 拍板細節）** ｜ 作者：meadow（claude-code）2026-05-29
> 背景：Cmd_ReadHierarchy 已 ship 並通過 QA 戰鬥流程 dogfood，過程中暴露了 **DontDestroyOnLoad 覆蓋盲區**這個真實 QA gap；Tim 同時要求規劃 Prefab 模式設計。

---

## 1. 背景與 Dogfood Findings

### 1.1 DDOL 盲區（QA loop 撈到的真實 bug）

昨天跑 QA 戰鬥流程時做了三段 Hierarchy 對比：

| 階段 | Active scene roots |
|---|---|
| Editor mode | 4（EventSystem / Main Camera / RCG_VFXTest / **RCG_Boot**） |
| PlayMode 剛進 | 4（同上） |
| 戰鬥中 | **3**（RCG_Boot 消失到 DontDestroyOnLoad） |

`RCG_Boot` 在 runtime 把所有 manager / UI / 戰鬥單位（露西亞 / 蜘蛛 / 卡牌系統）spawn 到 `DontDestroyOnLoad` 特殊 scene。`Cmd_ReadHierarchy` 目前只讀 `SceneManager.GetActiveScene()`，**整批戰鬥真實狀態都看不到**。對偶證據：`Cmd_Confirm` 走 `Resources.FindObjectsOfTypeAll<UCL_GameUI>()` 反而能列出 5 個 DDOL UI（FogUI / DarkMistCounterUI / MapManager / FadeUI / ShowTestUI）。

### 1.2 既有預留待補

Ship 時用 `Reject` 擋住的未實作 args：
- `componentDetail=fields`（列 SerializedField + 值）
- `searchType=tag` / `layer` / `component`

### 1.3 QA loop 順帶確認的可用性 OK

- 預設 args 對 Editor mode 場景就是好用
- markdown 階層格式好讀
- 寫檔 + recompile + debuglog + Cmd 實跑四層驗證已落地

---

## 2. 優化方向總表（優先級分組）

| Pri | 強化項 | 動機 | 涉及檔 |
|---|---|---|---|
| 🔴 P0 | DDOL + 多 loaded scene 支援 | 真實 QA gap（dogfood 撈到） | Cmd + lib |
| 🔴 P0 | Prefab mode | 本 task 本身要求 | Cmd + 新 editor-only lib |
| 🟡 P1 | `componentDetail=fields` 解開 | 預留 Reject 終於做 | Cmd + 新 helper |
| 🟢 P2 | 節點資訊增量：InstanceID / Layer / Tag / position | inspector 通用需求 | Cmd（純 markdown 行擴充） |
| 🟢 P2 | `searchType=tag` / `layer` / `component` | 預留 Reject 終於做 | Cmd |
| 🟢 P3 | `maxNodes` 上限 + truncate marker | 大場景防爆 | Cmd |
| 🟢 P3 | path 模式 root filter（e.g. "Canvas/Background"）| inspector 用 | Cmd + lib |

---

## 3. 設計核心：同 Cmd 擴充 vs 拆 Cmd（**Prefab 模式分歧點**）

### 3.1 三條路 trade-off

| 路線 | 優點 | 缺點 |
|---|---|---|
| **A. 同 Cmd 完全合一**（現狀 + 內部分支） | UI 統一；caller 只記一個名 | args 越來越雜，scene/prefab source 概念混雜 |
| **B. 拆兩 Cmd**（`Cmd_ReadHierarchy` + `Cmd_ReadPrefabHierarchy`） | 各自 args 單純；schema 不混 | 重複 metadata / helper / 文件；底座要靠 lib 抽出 |
| ✅ **C. 同 Cmd + 顯式 `mode=scene\|prefab` 切換** | UI 統一、概念區隔清楚、共用 90% 邏輯 | 多一個 enum 參數要教 caller |

**推薦：路線 C**。理由：
1. **共用度極高**：WalkHierarchy / AppendNodeLine / GetComponentTypeNames / search filter / markdown 組裝 90% 邏輯共用。
2. **args 重疊度高**：`depth` / `includeInactive` / `includeComponents` / `componentDetail` / `search` / `searchType` 兩 mode 都用。
3. **拆 Cmd 多餘**：要把共用底座搬 lib 才不重複，反而拉高耦合成本。
4. **mode 切換明確化** source 概念區隔，比現狀「有 `prefab` 參數就走 prefab、否則 scene」更顯眼少誤用。
5. **schema 文件分段乾淨**：ArgsSchema 文字可分 `[scene mode]` / `[prefab mode]` 兩段列。

### 3.2 取代「現狀 prefab 參數隱式分支」

現狀 ship 版用「有 prefab 參數 → 走 prefab placeholder；否則 scene」隱式分支。改成顯式 `mode=scene|prefab`（預設 scene）+ `prefab` 必填當 mode=prefab，更明確、少誤判。

---

## 4. Prefab 模式詳細設計（路線 C 下）

### 4.1 Args（新增 / 不適用）

| Arg | mode=scene | mode=prefab | 說明 |
|---|---|---|---|
| `mode` | scene | prefab | 顯式分支，預設 scene |
| `prefab` | N/A | **必填** | Prefab asset path，e.g. `Assets/Prefabs/Hero.prefab` |
| `scene` | 適用 | N/A | scene name filter |
| `root` | 適用 | N/A | root GO name filter |
| `includeInactive` | 適用 | 適用 | prefab 也有 active state |
| `includeDontDestroyOnLoad` | **新（預設 true）** | N/A | P0 修盲區 |
| `includeAllLoadedScenes` | **新（預設 false）** | N/A | additive scene 場景覆蓋 |
| `depth` / `includeComponents` / `componentDetail` / `search` / `searchType` | 全適用 | 全適用 | 共用 |

### 4.2 Prefab 模式執行流程

```
Step 1. 驗證 prefab path 存在（AssetDatabase.LoadAssetAtPath<GameObject>(path) != null）
        ↓ 失敗 → Reject「Prefab not found at <path>」
Step 2. PrefabUtility.LoadPrefabContents(path) 載入到記憶體（不掛場景）
        ↓ 拿到 root GameObject
Step 3. try { WalkHierarchy(root.transform, ...) 走訪建 markdown }
        finally { PrefabUtility.UnloadPrefabContents(root) }
        ↑ try-finally 保證釋放避免 Editor 記憶體 leak
Step 4. 寫 markdown 含 prefab path / asset type / 變體鏈資訊 header
```

### 4.3 Prefab 特有 markdown header

```
# 🧩 Prefab Hierarchy

**Asset path:** `Assets/Prefabs/Hero.prefab`
**Asset type:** Regular | Variant | Model
**Variant base:** (only if Variant)
**Args:** depth=∞ | includeInactive=true | includeComponents=true | ...

## Hierarchy
- `Hero`  · [Transform, RCG_Player, Animator]
  - `Visuals`  · [Transform, MeshRenderer]
  - ...
```

### 4.4 Prefab 模式風險與邊界

| 風險 | 對策 |
|---|---|
| `LoadPrefabContents` 不 Unload → Editor 記憶體 leak | try-finally 包死；Cmd 在 finally 一定 Unload |
| Path 格式錯（少 `Assets/` 前綴 / 少 `.prefab`） | 用 AssetDatabase.LoadAssetAtPath 驗，失敗就 Reject 含 hint |
| Nested prefab / Variant 期待行為 | `LoadPrefabContents` 自然解出 final override 狀態，符合 caller 直覺 |
| Multiple prefab batch read | 本 Plan 不支援；未來可加 `prefab=p1,p2,p3` csv |
| Editor-only API（UnityEditor.PrefabUtility） | Cmd 本身已 `#if UNITY_EDITOR` 包；prefab helper 也放 editor-only lib |

---

## 5. DDOL / 多 scene 設計（P0 修盲區）

### 5.1 取 DDOL scene 的標準寫法

Unity 沒提供直接 API 取 DontDestroyOnLoad scene。技巧：

```csharp
// 暫掛一個 dummy GameObject，丟進 DDOL，反查它的 scene
var aDummy = new GameObject("__ReadHierarchy_DDOL_Probe");
UnityEngine.Object.DontDestroyOnLoad(aDummy);
Scene aDDOLScene = aDummy.scene;
UnityEngine.Object.Destroy(aDummy);
// aDDOLScene 即為 "DontDestroyOnLoad" 特殊 scene
return aDDOLScene.GetRootGameObjects();
```

⚠ 注意：DDOL scene **只在 PlayMode 中存在**；Editor mode 呼叫 `DontDestroyOnLoad` 會回 warning。所以 `includeDontDestroyOnLoad=true` 要先檢查 `Application.isPlaying`，false 時 noop（不報錯，header 標示「DDOL not available (Editor mode)」）。

### 5.2 多 loaded scene

簡單列舉：
```csharp
for (int i = 0; i < SceneManager.sceneCount; ++i)
{
    var s = SceneManager.GetSceneAt(i);
    if (s.isLoaded) yield return s;
}
```

`includeAllLoadedScenes=true` 時把所有 loaded scenes 的 roots 合併。

### 5.3 Markdown 分區呈現

來源不同的 root 分區列：

```
## Hierarchy

### Scene: RCG_EditVFX (active)
- `EventSystem` · [...]
- `Main Camera` · [...]
- `RCG_VFXTest` · [...]

### Scene: DontDestroyOnLoad (runtime)
- `RCG_Boot` · [...]
- `BattleManager` · [...]
- `Canvas` · [...]
  - ...

### Scene: AnotherAdditive (loaded)
- ...
```

→ 給 caller 一眼分清楚物件從哪來。

---

## 6. componentDetail=fields 設計（P1 解 Reject）

### 6.1 行為

`includeComponents=true && componentDetail=fields` 時，每個 Component 後綴列 SerializedField 名 + 值（值用 ToString，過長截 80 char）。

### 6.2 樣式（markdown）

```
- `RCG_Boot` · [Transform, RCG_Boot]
    └ RCG_Boot:
        m_BattleManagerPrefab = BattleManager
        m_StartScene = "RCG_EditVFX"
        m_DebugMode = false
```

### 6.3 實作

走 reflection（與 Cmd_TypeInspect 同套路）：
- `GetType().GetFields(BindingFlags.Instance | NonPublic | Public)`
- 過濾掉 `[NonSerialized]` 標記
- 取值 `.GetValue(component)` → null-safe ToString
- 值長度上限（避免吃掉整個 markdown）

### 6.4 風險

- **Reflection 成本**：大場景 + 每 GO 列 fields → 可能很慢。建議：caller 明確 opt-in `componentDetail=fields` 才跑，且強烈建議搭配 `root` filter 或 `search` 縮範圍。

---

## 7. 節點資訊增量（P2，純 markdown 行擴充）

新 args `nodeInfo=basic|verbose`（預設 basic，現狀）。`verbose` 時每節點行多帶：
- `InstanceID`（跨 cmd reference 用）
- `Layer:0(Default)` / `Tag:Untagged`
- `pos=(x,y,z)` 世界座標
- `Component count`（不列名時也能知道數量）

Markdown 樣式：
```
- `Main Camera` (id=2334, Layer=0, Tag=MainCamera, pos=(0,1,-10), 3 comps) · [Camera, AudioListener, ...]
```

---

## 8. searchType=tag / layer / component 設計（P2）

| searchType | search 值意義 |
|---|---|
| `name` | 子字串名稱（現狀） |
| `tag` | 完全比對 tag 名 |
| `layer` | 完全比對 layer 名 或 layer index |
| `component` | 子字串比對 Component type name |

實作走 WalkHierarchy 的 visit predicate 切換。

---

## 9. maxNodes 上限 + truncate marker（P3）

新 arg `maxNodes`（預設 5000）。超過上限時 markdown 結尾：

```
---
⚠ truncated at 5000 nodes (hit maxNodes cap). Use root / search / depth to narrow.
**Stats:** 5000 walked / total estimated > 5000
```

→ 防大場景 (e.g. 整套 RCG_Boot DDOL) 爆 _last_op.md。

---

## 10. Lib / Extension 落點

| Helper | 落點 | 理由 |
|---|---|---|
| `GameObjectLib.GetDontDestroyOnLoadRootGameObjects()` | UCL_GameObjectLib（runtime） | Dummy probe 技巧通用 |
| `GameObjectLib.GetAllLoadedScenesRootGameObjects()` | UCL_GameObjectLib（runtime） | Scene 列舉通用 |
| `PrefabUtilityLib.WalkPrefabHierarchy(path, visit)` | **新增 editor-only lib** 或 Cmd 內 private | `PrefabUtility` 是 UnityEditor 命名空間 → 必須 editor-only |
| `GameObjectExtension.GetSerializedFieldSummary()` | UCL_GameObjectExtension（runtime） | reflection 不依賴 editor，可 runtime 用 |

> Runtime / editor 兩層界線必守：UCL_GameObjectLib 是 runtime assembly，不能引 UnityEditor。Prefab helper 自然落 editor-only。

---

## 11. 實作分階段（建議順序）

| Phase | 內容 | 預估規模 |
|---|---|---|
| Phase 1 | DDOL + 多 scene + `mode=scene\|prefab` 顯式化（現狀 prefab 隱式分支退場） | 中 |
| Phase 2 | Prefab mode 實作（含 LoadPrefabContents / try-finally Unload） | 中 |
| Phase 3 | `componentDetail=fields` 解 Reject | 中（reflection 要小心） |
| Phase 4 | `searchType=tag\|layer\|component` 解 Reject | 小 |
| Phase 5 | 節點資訊增量（`nodeInfo=verbose`）+ `maxNodes` 上限 | 小 |

每 phase 獨立可 ship、可獨立驗收。

---

## 12. 待決問題（給 Tim 拍板）

- **Q1**：路線 C（同 Cmd + `mode` 切換）OK？還是堅持拆兩 Cmd？
- **Q2**：DDOL 預設行為 —— `includeDontDestroyOnLoad` 預設 **true**（QA 友好）還是 **false**（不主動掃 DDOL）？我傾向 **true**（昨天 dogfood 就是 false 才漏看）。
- **Q3**：Prefab path 格式只支援 `Assets/...` 完整路徑，還是要兼容無前綴 / Resources path？建議只支援完整路徑（最少歧義）。
- **Q4**：`componentDetail=fields` 的 reflection 效能風險 → 是否要硬性強制 `root` 或 `search` 才允許？我建議「警告但允許」，給 caller 自由。
- **Q5**：實作分工 —— 我接手 ∥ 交 gura 主刀 ∥ 兩人切 phase 分工？這次跟 ModuleService 不同，code 是我自己寫的、context 熱在我手上。

---

## 13. 結語

ReadHierarchy 是個小 Cmd，但因為 dogfood 跑 QA 暴露真實 gap（DDOL 盲區），加上 Tim 要求的 Prefab 模式擴充，剛好把它從「Edit mode 場景讀取工具」昇格成「runtime / asset 雙 source 的通用 hierarchy 讀取器」。路線 C 是把這條昇格之路走得最省成本的選擇。
