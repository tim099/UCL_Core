# UCL Coding — C# 章

> 一句話：**動 `.cs` 之前先確認「這件事有沒有既有基建」**，動完之後先確認「它真的編過了」——
> 這兩格是 C# 這一端最貴的兩個靜默失敗點。
>
> 本檔是 [`SKILL.md`](SKILL.md) 的 C# 專章（依語言拆出）。跨語言規則（路徑／錢／`--persona`／
> 開工廣播／坑寫回哪裡）在 `SKILL.md`，**不在本檔重抄**；python 端見 [`PYTHON.md`](PYTHON.md)。

## 📚 規範本體（本章只是指路，細節不在這裡重抄）

| 主題 | 文件 |
|---|---|
| **JSON 讀寫**（`JsonData` / typed model / round-trip 驗收）**動 JSON 前先讀** | `ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md` |
| **C# 撰寫規範**（字串 key、外部 Process、letters 路徑） | `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md` |
| 程式碼註解規範（區塊職責 / 物理意義 / 數值影響） | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
| 自動畫出整個物件的編輯介面 | `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md` |
| 頁面骨架 / 建新頁的完整流程 | `ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_CommonEditorPage.md`、`ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md` |
| 新 Asset（持久化資料一律 `UCL_Asset<T>`） | skill `ucl-create-asset` |
| 新 AgentCommand handler | skill `ucl-create-cmd` |
| 改完 .cs 怎麼確認真的編過 | skill `ucl-compile-error` |

> [!IMPORTANT]
> ## 🔨 改完 .cs **一律觸發 `Cmd_Recompile`**（Tim 2026-08-16 拍板）
>
> **Unity 失焦時不會自動重編，而 agent 寫檔幾乎都在失焦下發生** —— 所以「改完等它自己編」
> 在 agent 的工作流裡是不存在的事。改完 .cs ⇒ **一律送 `Cmd_Recompile`**，這是確保有編到的唯一手勢。
>
> 而要**等到編完並拿到錯誤清單**，用 python 子命令（不是 `run Recompile`）：
>
> ```bash
> python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> recompile
> ```
>
> 它會：記下 pre-mtime → 送 Cmd → **等 `.compile_status.json` 推進且 `in_progress=false`** → 印 errors/warnings。
> 而 `run_cmd.py run Recompile` 只是**丟出請求就返回**（Cmd_Recompile 刻意這樣設計 —— domain reload 會殺掉
> in-flight 的 async Cmd，所以它不能自己 await 編譯完成）。
>
> ⚠ **`Cmd 回 Success` 只證明「請求被 Unity 收下」，不證明編譯發生過。**
>
> 🩸 **為什麼這支 Cmd 特別重要**：**Unity 失焦時不會自動重編** —— 而 agent 直接寫檔的場景
> 幾乎都在失焦下發生。`Cmd_Recompile` 正是為此存在的入口（**失焦狀態下也能觸發編譯**，
> 這是它的設計目的，不是副作用）。⇒「改完 .cs 不做任何事、等 Unity 自己編」在 agent 的
> 工作流裡是**不會發生的事**。
>
> ⚠ basecamp 2026-08-16 實測到的另一格：**送出請求到 `.compile_status.json` 真的推進，曾經超過 120s**
> —— `recompile` 子命令的等待窗口跑完了才編到，而那段期間 `check_compile.py` 一路標 STALE，
> 看起來就像「完全沒編」。
> ⛔ 我當時把工具印的提示（「切到前景再試」）當成量到的真因寫進本 skill —— **那是錯的，Tim 當場更正**。
> 提示是候選解釋，不是讀數；**沒量過的因果不要寫成血證**。
>
> ⇒ 判準：**編譯過了的唯一憑據是 `check_compile.py` 沒標 STALE**（時間戳晚於你最後一次存檔）。
> 還標著就是還沒編到 —— 再送一次 `recompile`，或直接讀 `.compile_status.json` 的時間戳，
> **不要把「請求被收下」讀成「編譯完成」**。
> 排查編譯錯誤的完整手勢 → skill `ucl-compile-error`。

## ⛔ C# 專屬硬規則

**① 開外部 Process 一律登記 `UCL_ProcessRegistryService`。**
domain reload 會清掉 C# 的 `Process` 物件，但 OS 層的 process **不會跟著死** ——
每次重編再生一顆，舊的變孤兒，累積起來就是**屍潮**（重複開 process 直到電腦卡死）。
`KillAllByTag` → `Start` → `Register` → 結束時 `Unregister`。
參考實作 `UCL_ScreenStreamDaemon`。細節見 Coding_Standards.md「外部 Process」。

**② 持久化資料一律繼承 `UCL_Asset<T>`**，禁止裸 `ScriptableObject` 或自寫存檔（見 `ucl-create-asset`）。

**③ 銀行／餘額一律走 `UCL_TreasuryLedger` 的 API，不自己解析原檔**（Tim 2026-08-20 拍板）。

`GetBalance(accountId)` 單一帳戶／**`GetAllBalances()` 整批**（要畫一張表就用這個，只同步一次）。

❌ 不准自己重放 `Treasury/ledger/**.json`、不准 parse `accounts/_balances.snapshot.txt`、
不准在呼叫端另建一份餘額快取。三個理由，全部不會當場叫：

- **正確性**：餘額不是「把檔案加總」—— 它有**關帳基準**（`closing/<日>.json` warm start）、
  增量 watermark、壞檔處理。自己重放會得到一個看起來合理、但少算或多算一段的數字。
- **效能**：`GetBalance` 單次便宜（只列舉路徑），但那是**單次**的便宜。
  🩸 2026-08-20：銀行後台兩個新表格區各自對 40 個帳戶現場查餘額 ⇒ 開頁卡一分鐘、
  IMGUI 跳 `Getting control 8's position in a group with only 8 controls`、
  Unity 內部 `PropertyEditor` 連鎖 NullReferenceException，連 `recompile` 都排不進主執行緒。
- **一致性**：兩份餘額來源遲早給出不同答案，而兩邊都能自圓其說、都不報錯。

> ⛔ **`Draw*`（IMGUI）裡只准讀記憶體。** 任何會碰磁碟的呼叫 —— 餘額、`File.Exists`、
> 讀設定檔的 property —— 都要先在 `LoadData` 算好存成欄位，並在操作後顯式失效。
> ⚠ 把**會讀檔的 property** 放進 Draw 還有第二種死法：IMGUI 的 Layout 與 Repaint 是**兩個 pass**，
> 兩趟看到不同的控制項數量就會拋 `ArgumentException` 並中止該幀繪製。

> [!IMPORTANT]
> ## 🧱 JSON 一律定義具體 class 並繼承 `UnityJsonSerializable`（Tim 2026-08-18 拍板）
>
> 已知 schema 不准用裸 `JsonData` 逐鍵讀寫 —— 鍵名打錯不會編譯錯、也不會執行錯，
> **只會讀回預設值**，而讀回預設值通常長得跟「這筆資料不存在」一模一樣。
> `JsonData` 只留在邊界層（解析外部 JSON / 保存未知欄位 / migration），且要在註解寫明理由。
>
> ⚠ 換 typed model **不是純粹的重構** —— 序列化器的行為跟手搭 `JsonData` 不一樣，
> 而差異全部落在「編譯過、看起來對、但 wire format 變了」這一格：
> **bool 會變成 `"True"` 字串**（python 端 truthy ⇒ 停不掉的錄影）、
> **空 `List<>` 會讓整個鍵消失**、**未知鍵會被靜默吃掉**、巢狀結構**不必手刻解析**。
>
> ⇒ **完整規則、API 對照與 round-trip 驗收協議在專章**，本 skill 不重抄：
> `ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md`
>
> 參考實作：`UCL_ScreenStreamConfig`（跨語言 config ＋ 未知鍵保留）／
> `UCL_StreamWatchSession` 等五個 model（class 放 Cmd 檔內）／`UCL_SessionBase` / `UCL_FreeTimeSession`。
## 🖥 寫 Editor 頁 / 任何 IMGUI

**不要直接堆 `GUILayout` 原生 API** —— UCL_Core 有一整層封裝，處理了 DPI 縮放、樣式一致性、
搜尋式下拉、折疊狀態快取等等，而那些是原生 API 沒有的。

> [!IMPORTANT]
> **先問「能不能整個交給 `DrawObjectData` 畫」，再考慮手刻欄位。**
> `UCL_GUILayout.DrawObjectData(obj, dic, name, false)` 用反射走訪欄位自動畫出整個編輯介面 ——
> 巢狀物件、`List` / `Dictionary`、`[SerializeReference]` 多型下拉、折疊狀態全部內建。
> 資料類別加欄位時，頁面**一行都不用改**。
>
> 顯示不滿意時**也不要退回手刻**，改實作對應介面只接管那一層：
>
> | 介面 | 接管範圍 |
> |---|---|
> | `UCLI_ShortName` | 顯示名稱（List 元素尤其該實作，否則每個元素都顯示型別名） |
> | `UCLI_IsEnable` | 名稱前多一個 CheckBox（接到既有 enable 欄位，別另開狀態） |
> | `UCLI_NameOnGUI` | 整條標題列 |
> | `UCLI_FieldOnGUI` | 整個欄位的繪製（慣例：先呼叫 `DrawField` 再往下追加） |
>
> 用法、繪製順序與互斥陷阱 → `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md`

| 要做什麼 | 走哪裡 |
|---|---|
| **自動畫出整個物件的編輯介面** | `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md` |
| 頁面骨架（`WindowName` / `ContentOnGUI` / `TopBarButtons` / `HelpURL`） | `ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_CommonEditorPage.md` |
| 建新頁的完整流程與地雷 | `ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md` |
| 版面元件（popup / 搜尋下拉 / 各種 field） | `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_Overview.md` |
| **長清單要分頁**（事件流／訊息／紀錄） | `DrawSelectPage(dic, count, 10)` —— 用法與四個行為細節見 `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_Overview.md` §5.4。**不要自己刻第二套翻頁列** |
| 樣式與 DPI 縮放（`ButtonStyle` / `LabelStyle` / `TextFieldStyle` / `GetScaledSize`） | `ucl_core:Docs~/{lang}/API/UCL_GUIStyle/UCL_GUIStyle_Overview.md` |

> [!IMPORTANT]
> ## 🧠 動 GUI 之前先讀工作記憶 `ucl-editor-pages`（Tim 2026-08-21）
>
> Editor 頁的坑有一大半**不適合寫進 API 文件** —— 它們不是「這支 API 怎麼用」，
> 而是「這個專案的頁面長什麼樣、為什麼那樣、上次是怎麼被咬的」。那些住在工作記憶：
>
> ```bash
> python <UCL_Core>/Tools~/AgentCommands/work_memory.py read --topic ucl-editor-pages --with-links
> ```
>
> 讀完**開啟它印出的 briefing 檔**（那是唯一輸入）。目前裡面有：頁面骨架、
> 折疊區塊的預設值慣例、`ContentOnGUI` 不是 `OnGUI` 的坑、長清單分頁的三個細節。
>
> **完工時把新的經驗寫回去**（判準見 skill `ucl-work-memory`）：
>
> | 這條經驗是什麼 | 寫哪裡 |
> |---|---|
> | 「這支 API 怎麼用 / 有什麼行為」 | **文件**（`Docs~/{lang}/API/UCL_GUILayout/…`）—— 知識點能放文件就放文件 |
> | 「這個專案的頁面慣例、為什麼這樣排、上次撞到什麼」 | 工作記憶 `ucl-editor-pages`（`--type knowhow` / `pitfall`） |
> | 跨工作通用的認知型教訓（不限 GUI） | skill `agent-lessons-log` |
>
> ⚠ **不要把文件內容整段抄進記憶**（那是複本，會各自漂移）；記憶寫 key 與現場摘要，
> 用 `--docs` 指回文件。反過來也一樣：**別把「上次誰在哪一頁踩到什麼」塞進 API 文件** ——
> 那會讓一份給所有專案讀的 API 文件長出本專案限定的故事。

踩過的具體幾條：
- **`ContentOnGUI` 內不要再開 ScrollView** —— base 已經包好，再包一層是雙捲軸。
- 寬度用 `UCL_GUIStyle.GetScaledSize(n)`，不要寫死像素（高 DPI 下會壞）。
- `TextField` 用 `UCL_GUIStyle.TextFieldStyle`，不是 `LabelStyle`（外觀對但行為不對）。
- `UCL_GUILayout.PopupSearchCache` **選項為 0 時會 LogError** → 沒選項就整區隱藏。
- 折疊狀態的 `UCL_ObjectDictionary` **不要跟 PopupSearchCache 共用** ——
  資料重載路徑上的 `Clear()` 會把折疊值一併清掉（症狀是「收不起來」，看起來像 key 撞名）。
- `UCLI_NameOnGUI` 與 `UCLI_IsEnable` **互斥** —— 實作前者，後者的 CheckBox（以及 Icon、
  名稱 Label、多型下拉）就不會被畫（原始碼是 if / else）。症狀是「加了 NameOnGUI 之後 CheckBox 不見了」。
- `DrawObjectData` 的 `iIsAlwaysShowDetail: true` **會跳過整條標題列** ——
  `UCLI_NameOnGUI` / `UCLI_IsEnable` 都畫在那裡，設 true 等於兩個介面同時失效。
- 多型欄位（`List<基底型別>`）**一定要加 `[SerializeReference]`** —— 那是 UCL 判定多型的唯一訊號，
  少了它存檔會丟掉子類資料，**而且不會報錯**。

## 🔌 不開 Editor 頁也能操作 C# —— `Cmd_Invoke` 反射呼叫

> [!IMPORTANT]
> **要驗證一段 C#「真的做了什麼」，不要讀磁碟檔案推導，直接呼叫它的 API。**
> `Cmd_Invoke` 讓 agent 從 CLI 反射呼叫 Editor 端任何 public（或加 `nonPublic=true` 的非 public）
> 靜態／實例成員 —— 這是「事實有產物就去讀產物」在 C# 這一端的具體手勢。

```bash
# 靜態方法（最常用：自我檢查）
run_cmd.py --persona <me> run Invoke --arg type=<Namespace.Type> --arg member=<Method>

# 靜態屬性 → 存成變數（storeAs），供後續 invoke 當 target
run_cmd.py --persona <me> run Invoke --arg type=UCL.Core.UCL_SpriteAsset --arg member=Util \
    --arg kind=property --arg storeAs=spriteUtil

# 實例方法：target=$變數；有多載或帶預設參數時要給 paramTypes + args
run_cmd.py --persona <me> run Invoke --arg target='$spriteUtil' --arg member=GetData \
    --arg paramTypes='System.String;System.Boolean' --arg args='<ID>;false'
```

| 參數 | 用途 |
|---|---|
| `type` | 完整型別名（含 namespace，大小寫精確）；有 `target` 時可省 |
| `member` / `kind` | 成員名 / `method`(預設)｜`property`｜`field` |
| `paramTypes` / `args` | `;` 分隔。**帶預設值的參數也要顯式給** —— 反射不會自動補預設值 |
| `storeAs` / `target` | 把回傳值存成變數 / 以 `$變數` 當實例呼叫，可跨多次 invoke 串起來 |
| `nonPublic=true` | 打到 private / internal 成員 |

**回傳值在哪看**：`_cmd_results/*.json` 只記 Success/Fail，**不含回傳內容**。
實際回傳印在 Unity Editor log 的 `[AgentCmd:Invoke] OK (型別) = 值`：

```bash
grep -n "AgentCmd:Invoke\] OK" ~/AppData/Local/Unity/Editor/Editor.log | tail -1
```

⚠ **`Cmd 回 Success` 只證明反射呼叫沒有拋例外**，不證明那個方法做對了事 ——
要看結果就去讀上面那行，或再 invoke 一次查詢用的 API 對帳。

### 搭配 `UCL_Asset` API：資產的事實來源是 API，不是 JSON 檔

每個 `UCL_Asset<T>` 都有靜態單例 `T.Util`，拿到它就能操作整組資產：

| 成員 | 用途 | 備註 |
|---|---|---|
| `Util`（static property） | 取工具實例 | 第一步一律 `storeAs` 存起來 |
| `GetAllIDs(bool iUseCache)` | 全部資產 ID | 這是「有哪些資產」的**唯一**事實來源 |
| `GetData(string iID, bool iUseCache)` | 取實際資料物件 | 再 `storeAs` 就能呼叫它自己的方法 |
| `ContainsAsset(string iID)` | 存不存在 | 比 `File.Exists` 可信 —— 快取／註冊層都算進去了 |
| `Delete(string iID)` | 刪資產 | ⚠ 不可逆，先確認 |
| `Save()` | 落盤 | **改完記憶體不會自己存** |

> 為什麼不掃磁碟 JSON：資產有快取層與註冊表，磁碟上有檔 ≠ 系統看得到它，
> 系統看得到 ≠ 磁碟上那份是當前值。用 `ls` / 讀 JSON 得到的是**平行索引**，
> 而平行索引跟事實不一致時**兩邊都能各自運作、都不報錯**。

實例（本專案 2026-08-14 實跑）：
```bash
# ① 資料層自我檢查（不開遊戲）
run_cmd.py --persona <me> run Invoke --arg type=LittleYellow.ClickAreaAsset --arg member=SelfTest

# ② 三段式串接：Util → 取某份資產 → 呼叫它的方法 → 存檔
run_cmd.py --persona <me> run Invoke --arg type=LittleYellow.SpriteAssetImporter --arg member=Util \
    --arg kind=property --arg storeAs=impUtil
run_cmd.py --persona <me> run Invoke --arg target='$impUtil' --arg member=GetData \
    --arg paramTypes='System.String;System.Boolean' --arg args='ClickAreas_Scene2;false' \
    --arg storeAs=imp
run_cmd.py --persona <me> run Invoke --arg target='$imp' --arg member=Import
run_cmd.py --persona <me> run Invoke --arg target='$imp' --arg member=Save   # ← 漏掉這步 = 改動只在記憶體
```

### 🖱 觸發 Editor 頁的 UI 按鍵（Tim 2026-08-20 拍板）

**後台頁的按鈕動作也能從 CLI 觸發** —— 不必開 Unity 用滑鼠按。
每個 `UCL_EditorPage` 子類都有靜態 factory `public static XXX Create()`，拿它當入口：

```bash
# ① 建頁面實例並存成變數
run_cmd.py --persona <me> run Invoke     --arg type=UCL.Core.EditorLib.Page.UCL_BankAdminPage --arg member=Create --arg storeAs=page

# ② 用 $page 呼叫按鍵背後的方法（多半是 private instance method ⇒ 要 nonPublic=true）
run_cmd.py --persona <me> run Invoke     --arg target='$page' --arg member=LoadData --arg nonPublic=true

# 有參數的照常帶 paramTypes / args
run_cmd.py --persona <me> run Invoke --arg target='$page'     --arg member=IsAgentBankRemoveArmed --arg paramTypes=System.String --arg args=Zeta --arg nonPublic=true
```

實測讀數（2026-08-20，`UCL_BankAdminPage`）：
`Create` → `OK (UCL.Core.EditorLib.Page.UCL_BankAdminPage)`／`LoadData` → `OK (void / null)`／
`IsAgentBankRemoveArmed("Zeta")` → `OK (System.Boolean) = False`。

⚠ **static 成員仍然要用 `type=`，不能用 `target=$page`。**
🩸 血證（同日）：`SafeBalance` 是 static，我用 `target=$page` 呼叫 ⇒ `method not found: …SafeBalance(System.String)`。
改用 `type=` ⇒ `OK (System.String) = 2765`。**這正是本節下方「踩過的幾條」早就寫過的那一條，我照樣踩了。**

⚠ **限制：依賴輸入框草稿（`m_XxxDraft`）的按鍵無法直接觸發** ——
`kind=field` 是**讀取**，Cmd_Invoke 沒有寫 private field 的入口。
⇒ 要讓這種按鍵可測，把邏輯層抽成「吃參數的方法」，UI 那層只負責把 draft 餵進去。
（那本來就該做：按鍵動作與畫面狀態綁死的話，除了人手按之外沒有任何驗證方式。）

⚠ 而且 **Cmd 回 Success 不代表你讀到的是這一次的回傳值** ——
回傳印在 Editor log，`grep … | tail -1` 在**這一次失敗**時會安靜地給你**上一次**的那行。
🩸 血證（同日）：第二次呼叫失敗，我 tail 到的是第一次的 `Boolean=False`，
而抓到它的唯一線索是**型別對不上**（那個方法該回字串）。
⇒ 判準：先看 run_cmd 有沒有印 `✓ Cmd completed`，**再**去讀 log 那行；兩者要一起看。

### 踩過的幾條

- **`Save()` 要自己叫。** 改完記憶體不落盤，下次重載就沒了，而且**不會報錯**。
- **匯入類 API 通常只新增不刪舊。** `SpriteAssetImporter.Import()` 會 `Clear()` 自己的清單重建，
  但**磁碟上舊 ID 的資產檔不會被刪** —— 素材改名後跑重匯入，會得到「新舊兩套同時註冊」。
  改名情境要先 `Delete` 舊 ID，否則下游看到的是兩份都存在（實測：`ContainsAsset` 新舊皆回 `True`）。
- **帶 `EditorUtility.DisplayDialog` 的方法不要盲目 invoke** —— modal 對話框會卡住 Editor 主執行緒，
  CLI 端只會看到 timeout。這種要嘛請人按，要嘛把邏輯層與對話框層拆開再呼叫邏輯層。
- **靜態方法用 `type=`，實例方法用 `target=$var`** —— 兩者混用會得到「找不到成員」，
  而錯誤訊息會提示 `try nonPublic=true`，那是誤導（真正的問題是 static/instance 選錯）。

## 判準：什麼時候該停下來找既有基建

動手前先問一次：**「這件事聽起來像不像已經有人做過？」** 以下全部都有既有基建，
自己寫一套的代價是少掉原版踩坑後補的防護：

| 你想做的事 | 既有基建 |
|---|---|
| 開外部 process | `UCL_ProcessRegistryService` |
| 找 repo root / Unity project root / AgentCommands 目錄 | `UCL_RepoPath` |
| **組 letters 底下的路徑**（信 / Cmd 回傳檔） | `UCL_LettersPath`（python 對側：`ucl_paths.letters_cmd_payload()`）—— 別自己 `Path.Combine`，2026-08-18 那次搬家就是因為四種算法各在一處 |
| 用檔案管理器開啟路徑 | `UCL_ExplorerUtil` |
| 存持久化資料 | `UCL_Asset<T>` |
| 頁面設定記住上次的值 | `EditorPrefs`（key 用 `const string`） |
| **畫一個資料物件的編輯介面** | `UCL_GUILayout.DrawObjectData`（別手刻欄位；客製化走四個 `UCLI_*` 介面） |
| 畫一個 List（含新增／刪除／搬移／多型下拉） | `UCL_GUILayout.DrawList` |
| 搜尋式下拉選單 | `UCL_GUILayout.PopupSearchCache`（⚠ 選項為 0 時會 LogError，要先擋） |
| 二次確認彈窗 | `UCL_OptionPage.Create(title, msg, ButtonData…)` |
| 多語系字串 | `UCL_CodeLocalize.Get(key)`（**四語系檔都要加**；少鍵不會編譯錯，只會顯示成鍵名） |
| 非阻塞跑外部工具 | `Task.Run` + `BeginOutputReadLine`/`BeginErrorReadLine`（單讀一個 stream 會 deadlock） |
