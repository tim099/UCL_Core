---
name: ucl-coding
description: |
  UCL_Core 撰寫規範入口（C# 與 Python）— 動 code 之前該知道的硬規則與慣例。
  涵蓋：**路徑一律走既有解析器不自己推導**（三端對照；自推導的失敗是靜默的）、
  **錢一律走 Cmd**（token 與券，python 不直寫帳本）、
  外部 Process 一律走 UCL_ProcessRegistryService（防屍潮）、設定與 JSON 資料的 typed model 原則、
  字串 key 常數化、註解規範、IMGUI 一律走 UCL 封裝（優先 DrawObjectData 自動繪製），
  以及「該用哪個既有基建而不是自己重造」的指路。
  ⚠ **要寫 .py 前先讀 Python_Coding_Standards.md**（本 skill 有指路）。
  觸發詞（case-insensitive substring，任一命中即 lazy-load）：
  - coding 規範 / coding standard / 撰寫規範 / 程式規範 / code style / 命名規範
  - 我要寫 python / 改 .py / 新增工具腳本 / Tools~ 底下 / CLI 工具
  - 路徑推導 / repo root / data root / ucl_paths / 寫死路徑 / 平行宇宙 / 寫到 repo 外
  - 直寫帳本 / 發券 / 扣券 / 查餘額 / treasury_cmd
  - 開 Process / Process.Start / spawn process / 子行程 / daemon / 屍潮 / 殭屍行程 / process 卡死
  - JsonData / typed model / 設定檔欄位 / EditorPrefs key / const string / 字串 key
  - 註解怎麼寫 / 區塊職責 / 物理意義 / 數值影響
  - 我要新增 C# 檔 / 要改 UCL_Core 的 code / 這段該放哪
  - 畫介面 / IMGUI / GUILayout / Editor 頁
  - DrawObjectData / DrawList / 自動繪製 / 手刻欄位
  - UCLI_ShortName / UCLI_IsEnable / UCLI_NameOnGUI / UCLI_FieldOnGUI / SerializeReference 多型下拉
  - Cmd_Invoke / 反射呼叫 / 不開 Editor 頁跑 C# / 怎麼驗證 API / SelfTest 怎麼跑
  - UCL_Asset.Util / GetAllIDs / GetData / ContainsAsset / 取得資產實際資料 / 資產存不存在 / 資產沒存檔
---

# UCL Coding — C# 撰寫規範入口

> 一句話：**動 C# 之前先確認「這件事有沒有既有基建」** —— UCL_Core 最常見的錯不是寫錯，
> 是自己重造一套已經存在的東西，而重造出來的那套通常少了原版踩過坑之後補上的防護。

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

## 📣 寫 code 前先廣播（now_status，Tim 2026-08-19 拍板）

動手改 code **之前**，到酒館發一則短訊說你要改哪些檔，並帶 `--arg status=`：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Tavern   --arg op=post --arg room=tavern --arg persona=<me> --wait-reply 0   --arg "status=改 <哪個系統/哪些檔>" --arg-stdin body <<'BODY'
（一兩句：要改什麼、大概多久）
BODY
```

- `status` 會**順手寫進你的 persona lock 的 `now_status`** —— catchup／ding 的在線清單
  直接顯示「🟢 誰　💬 在做什麼（多久前）」。**通知同事跟改狀態是同一個動作，不用記兩次。**
- 🩸 為什麼是硬建議：2026-08-18 一位同事的 commit 抓走另一位**編輯中**的檔（`git add`
  分不出「這半邊有人正在寫」）—— 在線清單看得到「誰正在改什麼」，這種對撞就不會發生。
- 換工作目標時再發一則帶新 `status` 即可；登出後 lock 消滅，狀態不殘留。

## 規範本體（本 skill 只是指路，細節不在這裡重抄）

> [!IMPORTANT]
> ## 🧱 JSON 一律定義具體 class 並繼承 `UnityJsonSerializable`（Tim 2026-08-18 拍板）
>
> 已知 schema 不准用裸 `JsonData` 逐鍵讀寫 —— 鍵名打錯不會編譯錯、也不會執行錯，
> **只會讀回預設值**，而讀回預設值通常長得跟「這筆資料不存在」一模一樣。
> `JsonData` 只留在邊界層（解析外部 JSON / 保存未知欄位 / migration），且要在註解寫明理由。
>
> 換成 typed model 時**有三個坑會讓 wire format 靜默改變**（編譯過、看起來對）：
> **① 欄位名＝JSON 鍵名**（`FieldNameUnityVer` 只脫 `m_`）⇒ 沿用舊鍵名時刻意不走 `m_PascalCase`，
> 並在註解寫明；**② `bool` 會被寫成 `"True"`/`"False"` 字串**，C# 載入端雙接看不出來，
> 但 python 讀到的 `"False"` 是 **truthy** ⇒ 有非 C# 讀取端時要 `override SerializeToJson()`
> 把 bool 寫回原生；**③ 驗收要拿真實舊檔 round-trip 比對**（`Cmd_Invoke` 可直接做），
> 不是編譯過就算 —— 那隻 bool 正是在「recompile 回報 0 錯」之後才被 round-trip 抓到的。
>
> 完整血證與範例 → `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md`「換成 typed model 時的三個坑」。
> 參考實作：`UCL_SessionBase` / `UCL_FreeTimeSession` / `HSceneSpineImportConfig`。

| 主題 | 文件 |
|---|---|
| C# 撰寫規範（設定/JSON、字串 key、**外部 Process**） | `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md` |
| **Python 撰寫規範（寫任何 .py 前先讀）** | `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md` |
| 程式碼註解規範（區塊職責 / 物理意義 / 數值影響） | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
| 文件撰寫與 AI 可讀性 | `ucl_core:Docs~/{lang}/Agent/AI_READABILITY_GUIDELINES.md` |
| UCL_Core 路徑解析（不要寫死安裝路徑） | skill `ucl-core-paths` |
| 新 Asset（持久化資料一律 `UCL_Asset<T>`） | skill `ucl-create-asset` |
| 新 AgentCommand handler | skill `ucl-create-cmd` |
| 改完 .cs 怎麼確認真的編過 | skill `ucl-compile-error` |

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
| 樣式與 DPI 縮放（`ButtonStyle` / `LabelStyle` / `TextFieldStyle` / `GetScaledSize`） | `ucl_core:Docs~/{lang}/API/UCL_GUIStyle/UCL_GUIStyle_Overview.md` |

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

## ⛔ 三條最常被違反的硬規則

**① 開外部 Process 一律登記 `UCL_ProcessRegistryService`。**
domain reload 會清掉 C# 的 `Process` 物件，但 OS 層的 process **不會跟著死** ——
每次重編再生一顆，舊的變孤兒，累積起來就是**屍潮**（重複開 process 直到電腦卡死）。
`KillAllByTag` → `Start` → `Register` → 結束時 `Unregister`。
參考實作 `UCL_ScreenStreamDaemon`。細節見 Coding_Standards.md「外部 Process」。

**② 持久化資料一律繼承 `UCL_Asset<T>`**，禁止裸 `ScriptableObject` 或自寫存檔（見 `ucl-create-asset`）。

**③ 路徑一律走既有解析器，不要自己推導。**
各專案掛載位置與佈局不同，自推導跨專案必壞，而且**幾乎都是靜默壞**。

| 端 | 用什麼 | ❌ 不要 |
|---|---|---|
| **Python** | `_lib/ucl_paths.py`（`repo_root()` / `data_root()` / `ucl_core_dir()`） | `parents[N]`、自己 walk `.git`、自排 env/cwd fallback |
| **C#** | `UCL_AgentCommandsPath.DataRoot` / `UCL_RepoPath` / `UCL_EditorPath.CorePath` ／ **letters 底下走 `UCL_LettersPath`** | `Application.dataPath + "../.."`、自己 `Path.Combine` 出 letters 版面 |
| **文件** | `ucl_core:` / `repo:` prefix | 寫死 `Assets/Plugins/UCL_Core/...` |

> 🩸 **2026-08-17 一天內同一個病撞到三次，全部無聲**：
> `chess.py` 判準寫死 `CardGame/`（別的專案的目錄名）→ fallback 跳到 **repo 外**，
> 整批棋局檔不在版控裡，而 C# 讀 repo 內的舊快照 ⇒ **兩邊骰面對同一局講出相反的話**。
> `UCL_BartenderDaemon` 用 `dataPath/../..` → 跳出去**剛好命中一棵舊資料樹**，
> 餘額查詢回報 453、真實帳本 1330 —— **差 877，連錯誤訊息都沒有**。
> `hook_validate_modified.py` 寫死 `Path("CardGame")/"AgentCommands"` → 報告寫進假目錄，
> 而寫檔會自動建目錄 ⇒ **憑空長出一個資料夾**。
>
> 最壞的失敗**不是找不到檔，是找到了另一個宇宙的檔** —— 前者會喊，後者回一個看起來正常的數字。
> ⇒ **路徑不該被推導，該被傳遞。** `ucl_paths` 讀 C# 寫的路徑快照，兩端因此保證同源。
>
> 細節與三端對照 → skill `ucl-core-paths`；Python 端完整規範 → `Python_Coding_Standards.md`。

**④ 錢一律走 Cmd** —— token 與券都是。python 端用 `_lib/treasury_cmd.py`，**不直寫帳本**
（直寫會繞過餘額快取與冪等判重，且簽章欄位偽造成本為零）。2026-08-17 券的帳本分裂，
路徑 bug 是導火線，**能燒起來是因為 grant 那條路徑本來就允許直寫**。

**⑤ 跑 `run_cmd.py` 一律帶 `--persona <你>`**（Tim 2026-08-17 拍板）。

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run <CmdType> --arg k=v
#                                                 ^^^^^^^^^^^^^^^ 不是選配
```

`--persona` 一次做兩件事：**決定 queue 路由**（`queues/<persona>/`）＋
**宣告這筆是誰派的**（戳進 args，下游 Tavern post / Treasury 記帳不必反查猜）。

⚠ 它跟 `--arg persona=<P>` **是兩個不同的東西**：前者是 run_cmd 的旗標（走哪條 lane），
後者是 Cmd 的參數（這筆代表誰）。實務上大家只帶後者 ⇒ **全員掉進 `queues/anonymous/` 互相阻塞**。

> 🩸 血證兩則，同一個病：
> - summit 2026-08-16 觀影同場四人，一晚兩次 `ensure_idle` 逾時 SystemExit，
>   錯誤訊息裡是 `queues/anonymous/pending.trigger`，而 `queues/summit/` 好端端空在旁邊。
> - kiara 2026-08-17 自由時間，`step=next` 撞 120s timeout，trigger 卡在 anonymous 沒人取。
>
> **功能在、路由在、旗標在 —— 沒有人被指向它。規則要長在通道上，不要掛在呼叫端的記憶裡。**

⛔ **`--agent-id` 已移除**（2026-08-17）。它是自由字串、**沒有唯一性保證**
（打錯會長出 `queues/<那串>/` 而不報錯），而唯一有守衛的身分是 persona
（同一 persona 不得同時登入兩次）。打到舊旗標會**明確報錯並指路**，不是靜默忽略。

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

## 延伸

改完 code 要同步文件 → skill `ucl-update-docs`；提交 → skill `ucl-commit`。
