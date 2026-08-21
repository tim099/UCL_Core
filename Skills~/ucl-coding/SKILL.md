---
name: ucl-coding
description: |
  UCL_Core 撰寫規範入口（C# 與 Python）— 動 code 之前該知道的硬規則與慣例。
  **內容依語言分章**：C# 走 `CSHARP.md`、python 走 `PYTHON.md`，SKILL.md 只留跨語言硬規則與索引。
  涵蓋：**路徑一律走既有解析器不自己推導**（三端對照；自推導的失敗是靜默的）、
  **錢一律走 Cmd**（token 與券，python 不直寫帳本）、
  外部 Process 一律走 UCL_ProcessRegistryService（防屍潮）、設定與 JSON 資料的 typed model 原則、
  字串 key 常數化、註解規範、IMGUI 一律走 UCL 封裝（優先 DrawObjectData 自動繪製），
  以及「該用哪個既有基建而不是自己重造」的指路。
  ⚠ **動任何 JSON 讀寫前先讀 Json_Coding_Standards.md**、
  **要寫 .py 前先讀 Python_Coding_Standards.md**、**要寫 .html 前先讀 Web_Coding_Standards.md**、
  **要開 CI 前先讀 CI_Standards.md**（本 skill 都有指路）。
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
  - 我要寫網頁 / 改 .html / 靜態頁 / 前端 / index.html / CSS / 版面 / 排版 / 彈窗 / RWD
  - CORS / file:// / fetch 讀不到 / CDN / innerHTML / XSS / 跳脫 / GitHub Pages / 部署網頁
  - CI / GitHub Actions / workflow.yml / 自動建置 / 自動部署 / pre-commit hook / 要不要開 CI / fetch-depth
- JsonData / UCL_JsonData / typed model / UnityJsonSerializable / 序列化 / 反序列化 / SerializeToJson / DeserializeFromJson
- 讀 json / 寫 json / json 鍵名 / GetBool / CS0618 / implicit operator bool / round-trip / wire format / 巢狀 class
---

# UCL Coding — 撰寫規範入口（C# / Python）

> 一句話：**動 code 之前先確認「這件事有沒有既有基建」** —— UCL_Core 最常見的錯不是寫錯，
> 是自己重造一套已經存在的東西，而重造出來的那套通常少了原版踩過坑之後補上的防護。

## 📖 先分流：你要動哪個語言

| 你要動的東西 | 讀哪一章 | 那章的第一條硬規則 |
|---|---|---|
| **`.cs`**（Unity / Editor 頁 / IMGUI / Cmd handler / 反射驗證） | [`CSHARP.md`](CSHARP.md) | 改完 `.cs` **一律送 `Cmd_Recompile`** —— Unity 失焦時不會自動重編，而 agent 寫檔幾乎都在失焦下發生 |
| **`.py`**（`Tools~` 底下、CLI 工具、**用腳本改別的語言的檔**） | [`PYTHON.md`](PYTHON.md) | 寫任何 `.py` 前先讀 `Python_Coding_Standards.md` —— 尤其**硬規則四**（內容先落成檔案再插入） |
| **任何 JSON 讀寫**（`JsonData` / typed model / 序列化）—— **跨語言、跨檔案類型** | `ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md` | 已知 schema 一律 typed model；鍵名打錯只會讀回預設值，而那長得跟「這件事沒發生」一模一樣 |
| **`.html` / `.css` / `.js`**（畫廊、報表、看板那類純前端頁） | `ucl_core:Docs~/{lang}/Agent/Web_Coding_Standards.md` | 資料走 `<script src>` 不走 `fetch` —— `file://` 下 fetch 被 CORS 擋，而失敗訊息跟「檔案不存在」一模一樣 |
| **CI / 建置自動化**（要不要開、開哪一種） | `ucl_core:Docs~/{lang}/Agent/CI_Standards.md` | 判準是「這條規則現在住在誰的記性裡」，不是「能不能自動化」 |
| **兩邊都會踩的**（路徑／錢／`--persona`／開工廣播／坑寫回哪裡） | **本檔以下全部** | 路徑不該被推導，該被傳遞 |

> [!IMPORTANT]
> **本檔只放「兩個語言都成立」的規則。** 單一語言的寫法、API、血證一律住上表那兩章。
> 搬回本檔會長成第三份規範 —— 而三份規範遲早各說各話，且三邊都不報錯。
>
> ⚠ **舊編號對照**（本次拆檔前是一份 `## ⛔ 三條最常被違反的硬規則`，實際列了五條）：
> 舊 ③④⑤（路徑／錢／`--persona`）＝本檔的 ①②③；
> 舊 ①②④-b（外部 Process／`UCL_Asset<T>`／銀行餘額 API）＝ [`CSHARP.md`](CSHARP.md) 的 ①②③。

> [!IMPORTANT]
> ## 🩸 撞到坑之後：把避坑寫回**語言文件**，不要留在對話裡
>
> 對話會被 compact 掉，文件不會。**沒寫回去的坑＝下一個人（多半是你自己）會再踩一次。**
>
> **什麼樣的坑值得寫**（三選一即可）：會再犯／失敗是**靜默**的／修法本身會長出同族的下一隻。
> 一次性手誤不用寫。
>
> | 坑的性質 | 寫回哪裡 |
> |---|---|
> | C# 寫法、Unity API、Editor 行為、IMGUI | `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md` |
> | JSON 讀寫（鍵名／預設值／bool 變字串／空 List／未知鍵／round-trip） | `ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md` |
> | python 工具、CLI、**用腳本改別的語言的檔** | `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md` |
> | 靜態網頁（CORS／CDN／`innerHTML`／版面／只在某種開法下才壞） | `ucl_core:Docs~/{lang}/Agent/Web_Coding_Standards.md` |
> | CI（該不該開、workflow 寫法、只在 runner 上才現形的坑） | `ucl_core:Docs~/{lang}/Agent/CI_Standards.md` |
> | 註解該寫什麼／不該寫什麼 | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
> | 跨語言、跨工作的通用教訓（不是寫法問題） | skill `agent-lessons-log`（`Cmd_NoteLesson`，跨 agent 共享） |
> | 某支 workflow 的 ad-hoc 修正 | skill `ucl-workflow-patch`（累積 3 筆自動警示該 refactor） |
> | 這項工作專屬的坑（換人接手才需要知道） | skill `ucl-work-memory`（`--type pitfall`） |
>
> **怎麼寫才有用**（三條都是踩出來的）：
> 1. **寫判準，不要寫願望。**「下次記得 X」等於沒寫 —— 要寫成「**符合什麼形狀就停下來**」。
> 2. **附血證**：日期 ＋ 當時的讀數（`errors=4` / `5014 則` / `21 筆`）。
>    沒有讀數的規則沒有射程，下一個人不知道它涵蓋到哪裡。
> 3. **修法優先序**：讓那格失敗**不可能發生**（換做法／換資料形狀）
>    ＞ 讓它**當場喊**（守衛、fail-loud、對帳）＞ 才輪到「記得注意」。
>    第三種只在前兩種都做不到時才寫 —— 🩸 判準⑦入憲當天四犯，而長在路上的裁圖首日四攔，戰績 0:4 對 4:0。
>
> ⚠ **本 skill 只說明「怎麼記」，避坑內容一律住上表那些文件。**
> skill 是索引；把內容搬進來會長成第二份規範，而兩份遲早各說各話、且兩邊都不報錯。

## 📣 寫 code 前先廣播（now_status，Tim 2026-08-19 拍板）

動手改 code **之前**，到酒館發一則短訊說你要改哪些檔，並帶 `--arg status=`：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Tavern   --arg op=post --arg room=tavern --wait-reply 0   --arg "status=改 <哪個系統/哪些檔>" --arg-stdin body <<'BODY'
（一兩句：要改什麼、大概多久）
BODY
```

- `status` 會**順手寫進你的 persona lock 的 `now_status`** —— catchup／ding 的在線清單
  直接顯示「🟢 誰　💬 在做什麼（多久前）」。**通知同事跟改狀態是同一個動作，不用記兩次。**
- 🩸 為什麼是硬建議：2026-08-18 一位同事的 commit 抓走另一位**編輯中**的檔（`git add`
  分不出「這半邊有人正在寫」）—— 在線清單看得到「誰正在改什麼」，這種對撞就不會發生。
- 換工作目標時再發一則帶新 `status` 即可；登出後 lock 消滅，狀態不殘留。

## ⛔ 跨語言硬規則（兩個語言都成立）

> 各語言自己的硬規則在 [`CSHARP.md`](CSHARP.md)（外部 Process／`UCL_Asset<T>`／銀行餘額 API）
> 與 [`PYTHON.md`](PYTHON.md)（`Python_Coding_Standards.md` 硬規則一～四）。

**① 路徑一律走既有解析器，不要自己推導。**
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

**② 錢一律走 Cmd** —— token 與券都是。python 端用 `_lib/treasury_cmd.py`，**不直寫帳本**
（直寫會繞過餘額快取與冪等判重，且簽章欄位偽造成本為零）。2026-08-17 券的帳本分裂，
路徑 bug 是導火線，**能燒起來是因為 grant 那條路徑本來就允許直寫**。

**③ 跑 `run_cmd.py` 一律帶 `--persona <你>`**（Tim 2026-08-17 拍板）。

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

### `--arg persona=` 什麼時候是多餘的（Tim 2026-08-20 提問，實測定案）

`--persona <me>` 會**戳進 args**，所以 `GetArg(args,"persona")` / `RequireId(args,"persona")`
一律拿得到值 —— 實測 `Cmd_Library op=recall` 不帶 `--arg persona=` 照樣成功。
⇒ **技術上全部可省。但「能省」不等於「該省」**：

| persona 的語意 | 例 | 判準 |
|---|---|---|
| **＝呼叫者自己**（恆等） | `StreamWatch` 各 step、`FreeTime`、`Relationship op=update`、`Tavern op=post/catchup/query` | 可省 —— 寫兩次只是噪音 |
| **＝指定對象**（可能不是我） | `Library`（讀者可能是別人，補課會讀同事的心得）、`PersonaProfile op=get_bank/set_bank/unbind` | **不可省** —— 省掉會靜默變成「我自己」 |
| **猜錯代價很大** | `GoodMorning`（登入成別人）、`GoodNight`（**把同事登出**） | **刻意保留顯式** —— `ucl-morning` 的鐵律就是「persona 一律顯式，沒拿到名字就停下來問」 |

⇒ 真正的判準不是「Cmd 讀不讀得到」，是
**「省掉之後，『這筆算誰的』會不會變成隱式的，而錯了會不會有人喊」**。
第三類那兩支的錯誤是**別人的 session 被動到**，那種地方寧可多打一次。

⛔ **`--agent-id` 已移除**（2026-08-17）。它是自由字串、**沒有唯一性保證**
（打錯會長出 `queues/<那串>/` 而不報錯），而唯一有守衛的身分是 persona
（同一 persona 不得同時登入兩次）。打到舊旗標會**明確報錯並指路**，不是靜默忽略。

## 📚 文件索引

| 主題 | 文件 |
|---|---|
| **C# 章**（Recompile / typed model / IMGUI / Cmd_Invoke / 既有基建） | [`CSHARP.md`](CSHARP.md) |
| **Python 章**（腳本改別的語言的檔 / ucl_paths / treasury_cmd） | [`PYTHON.md`](PYTHON.md) |
| **JSON 讀寫規範（動任何 JSON 前先讀）** | `ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md` |
| C# 撰寫規範（字串 key、**外部 Process**、letters 路徑） | `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md` |
| **Python 撰寫規範（寫任何 .py 前先讀）** | `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md` |
| **靜態網頁撰寫規範（寫任何 .html 前先讀）** | `ucl_core:Docs~/{lang}/Agent/Web_Coding_Standards.md` |
| **CI 使用判準（什麼時候該用 CI、該用哪一種形狀）** | `ucl_core:Docs~/{lang}/Agent/CI_Standards.md` |
| 程式碼註解規範（區塊職責 / 物理意義 / 數值影響） | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
| 文件撰寫與 AI 可讀性 | `ucl_core:Docs~/{lang}/Agent/AI_READABILITY_GUIDELINES.md` |
| UCL_Core 路徑解析（不要寫死安裝路徑） | skill `ucl-core-paths` |
| 新 Asset（持久化資料一律 `UCL_Asset<T>`） | skill `ucl-create-asset` |
| 新 AgentCommand handler | skill `ucl-create-cmd` |
| 改完 .cs 怎麼確認真的編過 | skill `ucl-compile-error` |

## 延伸

改完 code 要同步文件 → skill `ucl-update-docs`；提交 → skill `ucl-commit`。
