---
title: Agent Cmd Schema 反射匯出 — 讓 Python 端 op schema 不再手抄
slug: agentcmd-schema-reflection-export
status: ✅ 已實作 (S0–S5 全部落地, kotoko 2026-07-29；工作區未 commit)
created_at: 2026-07-29T14:10:00Z
created_by: Spectre (kotoko 大小姐)
task_ref: T-CMDSCHEMA-01（Plan_RunCmd_Split 的 A2 細部設計）
last_updated: 2026-07-29T14:10:00Z
location: UCL_Core (cross-project — Cmd 系統與 run_cmd 都是跨專案基礎設施)
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_RunCmd_Split_And_CSharp_Migration.md | run_cmd 拆分 + C# 固化 | 本文是其中 A2 的細部設計
  - concept | Cmd_ExportCommandCatalog | 既有的 reflection 匯出通道，本設計在其上加結構化輸出
  - concept | UCL_CompileErrorTracker | 已訂閱 CompilationPipeline.compilationFinished，是現成的刷新時機
  - concept | tavern_cmd.py | Python 端 TAVERN_OP_SCHEMA 現址（2026-07-29 自 run_cmd.py 抽出）
---

# Agent Cmd Schema 反射匯出 — 可行性分析 Round 1

> Tim 派 task (2026-07-29)：`TAVERN_OP_SCHEMA` 能不能在 C# 端**根據當前 Cmd 自動生成**？
> 作法設想：把資訊填進繼承 `UCL_AgentCommandHandlerBase` 的 class 的某個欄位，
> 透過 reflection 在特定時機觸發刷新（或手動刷新，須標註在 Cmd 相關文件），確保新增/修改 Cmd 時同步。
>
> **結論先講：可行，基礎設施幾乎都已就位。但「填進欄位」這一步有個陷阱要先避開 —— 見 §3。**

---

## 1. 出題背景：現在有三處手抄鏡像，而且**已經漂了**

| # | 位置 | 內容 | 狀態 |
|---|---|---|---|
| 1 | `Cmd_Tavern.cs` 的 `switch (op)` + 134 個 `GetArg` | **實作**（權威） | — |
| 2 | `Cmd_Tavern.ArgsSchema`（自由文字） | 給人看的說明 | 目前**同步**（見 §3） |
| 3 | `tavern_cmd.py` 的 `TAVERN_OP_SCHEMA`（33 op） | Python client 預檢 | **已過期** ❌ |

外加第四處：`run_cmd.py TYPE_ALIASES` 與 `UCL_AgentCommandRegistry.s_TypeAliases` 是**同一張 cmd-type 別名表的兩份**。

### 🩸 實證：漂移不是風險，是現況

```
C# switch ops : 34
Python schema : 33
C# 有、Python 沒有：['create_trpg_room']
```

實跑驗證：

```
$ run_cmd.py run Tavern --arg op=create_trpg_room --arg campaign=... --arg name=...
✗ Tavern client-side 預檢失敗：
  Tavern op 'create_trpg_room' 未知；可用：createroom, events_since, ...
exit=2
```

**一個 C# 端完整實作、`ArgsSchema` 也寫了的 op，透過 run_cmd.py 完全打不到。**
它是 TRPG 開房一鍵（建房＋自動註冊 Discord mirror），kaguya 的 TRPG 團正是使用者。
client 端預檢從「幫你早點發現打錯字」變成「擋住合法功能」—— 這正是手抄鏡像最壞的失效方向。

第二個實證（比較小但同族）：Python `post` 的 `optional` 少了 `persona`，
而 persona 是 Phase 1 之後每筆 post 都該帶的欄位。因為 `optional` 從來沒被 enforce，錯了也沒人知道。

---

## 2. 可行性：基礎設施幾乎都已就位

| 需要什麼 | 現況 |
|---|---|
| 反射掃描所有 handler | ✅ `UCL_AgentCommandRegistry` static ctor 已用 `GetAllSubclass()` 掃描並實例化，domain reload 跑一次 |
| 列舉 handler 的 API | ✅ `ListHandlers()` / `ListTypes()` 已公開 |
| 匯出通道 | ✅ `Cmd_ExportCommandCatalog` 已在跑 `ListHandlers()` → 渲染 markdown；Page 上也有 Export 按鈕共用同一渲染 |
| handler 端宣告欄位的慣例 | ✅ **32 個 handler 有 30 個已覆寫 `ArgsSchema` 與 `ExampleArgs`** —— 宣告的**習慣已經存在**，只是格式是自由文字 |
| 編譯完成的 hook | ✅ `UCL_CompileErrorTracker` 是 `[InitializeOnLoad]` 且已訂閱 `CompilationPipeline.compilationFinished` |

**所以這不是「從零建一套」，是「把既有的自由文字欄位升級成機器可讀 + 換一個輸出格式」。**
30/32 的覆寫率是這件事最好的前置條件：不必說服任何人養成新習慣。

---

## 3. ⚠ 核心設計問題：宣告式欄位會不會只是把鏡像從 Python 搬進 C#？

這是本題唯一真正的風險。若只是新增一個 `ArgsSpec` 欄位、由人手填、放在 `switch` 旁邊，
那我們得到的是「C# 內兩份（宣告 + 實作）」而不是「一份」。鏡像沒消滅，只是換了地址。

### 但實證顯示：**距離才是漂移的驅動力，不是「宣告是裝飾性的」本身**

`Cmd_Tavern.ArgsSchema` 是**純裝飾性**的自由文字，沒有任何程式讀它做判斷 —— 它照理最該爛掉。
實際查證：**它有 `create_trpg_room`，而且描述正確。** 反倒是 Python 那份漂了。

| 鏡像 | 與實作的距離 | 結果 |
|---|---|---|
| `Cmd_Tavern.ArgsSchema` | 同一個檔、`switch` 上方數十行 | **同步** ✅ |
| `tavern_cmd.py TAVERN_OP_SCHEMA` | 另一個語言、另一個檔、另一層 repo | **漂了** ❌ |

改 `switch` 的人**看得到**上方的 `ArgsSchema`，所以會順手改；沒有人在改 C# 時會想到去改 Python。

**推論**：把宣告放在 handler class 內（Tim 的提案方向）**天然就享有 co-location 的保護**。
不必為了防漂移而先做一次昂貴的 load-bearing 重構（把 134 個 `GetArg` 改成走 spec 取值）。

### 但 co-location 不是保證，所以要配警報

co-location 是「大幅降低機率」不是「結構性消滅」。所以：
- **只宣告 load-bearing 的東西**（見 §4）—— 沒人用的欄位一定會爛（`optional` 少了 `persona` 就是證據）
- **加一個會自己舉手的過期偵測**（見 §6）—— 不能讓「忘了刷新」變成靜默失敗，
  否則我們就是用一個同族的病治另一個同族的病

---

## 4. 設計：欄位形狀

### 只宣告「Python 端真的會拿來做判斷」的三件事

盤點 `tavern_cmd.validate_args` 實際用到什麼：

| 欄位 | 有被 enforce 嗎 | 收不收 |
|---|---|---|
| op 名單 | ✅ 未知 op → 擋 | **收** |
| `required` | ✅ 缺 → 擋 | **收** |
| `aliases` | ✅ 歸一 mutate arg_pairs | **收** |
| `optional` | ❌ 從來沒被讀過 | **不收** —— 這正是已經爛掉的那欄；說明文字留在 `ArgsSchema` 就好 |

**不收 `optional` 是刻意的**：宣告沒人用的資料 = 製造新的爛帳。

### 兩層 model（Tavern 是唯一有子 op 的 cmd）

```csharp
// UCL_AgentCommandHandlerBase 新增（virtual，預設 null = 不提供結構化 schema）
public virtual UCL_CmdArgsSpec ArgsSpec => null;

public class UCL_CmdArgsSpec
{
    public string[] Required;                        // cmd 層必填
    public Dictionary<string,string> Aliases;        // alias → canonical
    public Dictionary<string, UCL_CmdOpSpec> Ops;    // 有子 op 的 cmd 才填（目前只有 Tavern）
}
public class UCL_CmdOpSpec
{
    public string[] Required;
    public Dictionary<string,string> Aliases;
}
```

`ArgsSpec => null` 的 handler → 匯出時只出 op/type 名稱與自由文字 `ArgsSchema`，
Python 端對它只做「cmd type 存在性」檢查，不做參數預檢。**35 個 handler 完全不必動。**

### 匯出格式

`Cmd_ExportCommandCatalog` 加一份結構化輸出（沿用同一次 `ListHandlers()`）：

```jsonc
// <DataRoot>/commands_schema.json
{
  "generated_at": "2026-07-29T22:10:00Z",
  "generator_version": 1,
  "type_aliases": { "ChatTavern": "Tavern", ... },   // ← 順手消滅第四處鏡像
  "commands": {
    "Tavern": {
      "ops": {
        "post": { "required": ["room","sender","body"],
                  "aliases": { "sender_id":"sender", "id":"sender" } },
        "create_trpg_room": { "required": ["campaign"],
                  "aliases": { "id":"campaign", "room":"campaign", "gm":"owner_agent" } }
      }
    },
    "LoginStatus": { "required": [], "aliases": {} }
  }
}
```

Python 端：`tavern_cmd` 改成開機載入這份 JSON，`TAVERN_OP_SCHEMA` 這張表**整個刪掉**（−60 行）。
`run_cmd.TYPE_ALIASES` 同樣改讀 `type_aliases`（−12 行）。

---

## 5. 刷新時機（Tim 問的那一點）

四個候選，可疊加：

| # | 時機 | 優點 | 缺點 |
|---|---|---|---|
| A | **`compilationFinished`**（`UCL_CompileErrorTracker` 已訂閱） | 改完 Cmd 存檔 → Unity 編譯 → 自動刷新。**時機精準**：Cmd 只可能在編譯後改變 | 每次編譯寫一次檔（內容沒變就跳過寫入即可，見下） |
| B | domain reload（Registry static ctor） | 最早、最全 | 比 A 更頻繁且不精準（進 PlayMode 也會 reload） |
| C | 手動：`Cmd_ExportCommandCatalog` / Page 按鈕（**已存在**） | 零新增成本 | 靠人記得 → 規則長在自覺上，正是我們在治的病 |
| D | Python 端**過期偵測** | 抓「忘了刷新」 | 不產生資料，只報警 |

### ✅ 拍板：**C 為主、A 只標記過期、D 改為「過期→自動降級」**（Tim + basecamp 2026-07-29）

Round 1 我把 A（自動生成）當主力。**主從倒過來**：

| 角色 | 誰 | 做什麼 |
|---|---|---|
| **主** | **C 手動生成** | CMD 管理面板按鈕 ／ `Cmd_ExportCmdSchema` 觸發 → 生成產物、入 git、可 review |
| **輔** | A `compilationFinished` | **只更新「當前來源 hash」，不重寫產物** —— 負責「精準標記過期」 |
| **兜底** | D Python 端 | hash 不符 → **自動把預檢降級成 fail-open** ＋ 印帶可執行指令的警告 |

**為什麼手動為主**（basecamp 三點，我全部同意）：
1. **自動重寫製造 diff 噪音** —— 每次編譯都可能動產物，`git status` 天天髒。
2. **自動寫檔會在別人 build 時偷改共用檔** —— UCL_Core 是跨專案 submodule；
   A 專案編譯順手改了產物，B 專案 pull 到一份沒人 review 過的 schema。
   手動觸發讓「產物變更」成為一個**有作者、有 commit message** 的動作。
3. **時機精準的價值仍然拿得到** —— A 只寫 hash 記錄，不碰產物。**自動偵測 ＋ 手動生成**，兩邊好處都在。

**D 的三道防線排序**（basecamp 提，關鍵在「警報不是防線」）：
- **第一道（機械）**：過期 → 預檢**自動降級**為 fail-open。**過期改變的是行為，不只是輸出。**
- **第二道（機械）**：commit 層檢查 —— `Cmd_*.cs` 有改但產物沒跟上 → post-commit / pre-push 提示。
- **第三道才是警報**：印出**可直接執行的重生成指令**，不 rate limit。但它是第三順位，不是防線。

> 這也是我原本問「怎麼讓警報不變成靠自覺」的答案：**別讓警報當防線**。
> 配合 §6 的 fail-open，警報漏看的最壞後果只是「少了 client 預檢」，不是「擋住功能」。

> **D 為什麼不可省**：只有 A 的話，「Editor 沒開 / 編譯沒跑 / 檔案沒同步」都會讓 schema 停在舊版，
> 而 Python 照樣安靜地用它做預檢 —— 那就是把 `create_trpg_room` 這個事故換一個原因重演一次。
> 有警報的自動生成 ≠ 沒警報的自動生成。這條跟 A1.5 是同一個道理。

#### ⚠ D 的實作必須用**內容雜湊**，不可用 mtime（gura QA 2026-07-29 推翻 Round 1 原案）

Round 1 原本寫「比對 `commands_schema.json` 與最新 `Cmd_*.cs` 的 mtime」。**這個作法在主場景結構性失效**：

**git 不儲存 mtime。** `git ls-tree` 的欄位只有 mode / type / blob-hash / name，沒有任何時間戳；
checkout 時所有檔案拿到的都是「當下寫檔時間」。

而 Tim 拍板入 git 的理由正是「agent 常在沒開 Unity 的環境跑，clone 下來要能直接用」——
**在那個主場景裡，`schema.json` 與 `Cmd_*.cs` 的 mtime 全部都是 clone 那一刻**，先後只取決於 checkout 寫檔次序，
等於擲骰子。而且它**沉默地**擲：不叫就等於宣稱「新鮮」。用一個同族的病（靜默失效）去治另一個同族的病。

**改用內容雜湊**：schema 內存 `source_hash`（所有 `Cmd_*.cs` 內容的雜湊），Python 端重算比對。

這順帶一次解掉 §「檔案落點與 git」的 `generated_at` 噪音題：**直接不存 `generated_at`，只存 `source_hash`**。
- 內容沒變 → hash 沒變 → **零 diff**（自然成立，不必另外想辦法壓）
- 跨機器**可複現**（wall-clock 做不到這件事）

### 🎛 CMD 管理面板（Tim 2026-07-29 追加拍板）

> 「可以加一個 CMD 管理面板，把同步功能按鈕加進去，我可以手動按，也可以透過 CMD 觸發。
> 面板入口放在 `UCL_ControlPanelPage`，面板可以參考 `UCL_ChatTavernAdminPage`。
> 這樣有雙重保險 —— 修改／新增 CMD 後，文件要提示透過 CMD 刷新，然後我發現不同步也可以手動刷新。」

**新增 `UCL_AgentCmdAdminPage`**（照既有慣例，無需發明新形狀）：

```csharp
public class UCL_AgentCmdAdminPage : UCL_CommonEditorPage
{
    public override string WindowName => "Cmd 後台管理";
    public static UCL_AgentCmdAdminPage Create() => UCL_EditorPage.Create<UCL_AgentCmdAdminPage>();
    protected override void ContentOnGUI() { /* 見下方面板內容 */ }
}
```

**入口**：`UCL_ControlPanelPage` 加一個 `DrawAgentCmdAdminSection()`，
形狀與既有的 `DrawKnowledgeBaseAdminSection` / `DrawPersonaAgentAdminSection` **逐字同構**
（`VerticalScope("box")` ＋ 折疊 toggle ＋ 標題 ＋「開啟 Cmd 後台管理頁」按鈕 → `Create()`）。

**面板內容**（第一版，聚焦同步；日後可擴充成通用 Cmd 後台）：

| 區塊 | 內容 |
|---|---|
| **同步狀態** | 產物 hash vs 當前來源 hash → 「✅ 同步 / ⚠ 過期」。過期時明列**哪些 cmd 有差異** |
| **同步按鈕** | 「重新生成 commands_schema.json」—— 就是 Tim 要的手動刷新 |
| Cmd 清單 | 已註冊 handler（`ListHandlers()`）＋ 是否有宣告 `ArgsSpec` |
| 產物路徑 | 落點與最後生成時間，可一鍵開檔（比照 `DrawFilesPanel`） |

**同一動作三個入口**（Tim 的「雙重保險」）：
1. 面板按鈕（人手動按）
2. `Cmd_ExportCmdSchema`（agent 經 run_cmd 觸發）
3. 既有的 `Cmd_ExportCommandCatalog`（順帶一起生成，兩份產物同源）

三者**共用同一個 static 生成函式**，比照 `Cmd_ExportCommandCatalog.RenderCatalogMarkdown` 已建立的
「Cmd 與 Page 按鈕共用渲染邏輯」慣例 —— **不可各寫一份**（否則就是本文在治的病的第五個實例）。

**文件義務**（Tim 明確要求）：`Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md`
與新增 Cmd 的 SOP 文件都要加一句：**「新增／修改 Cmd 後，到 Cmd 後台管理頁按同步，
或跑 `run_cmd.py run ExportCmdSchema`」**。

> ⚠ 但文件提示是**提醒**不是**保證** —— 它仍然長在人的自覺上。
> 所以 §5 的三道防線一條都不能省：真正兜底的是「過期 → 預檢自動降級」，
> 面板與文件是讓「同步」這件事**容易做**，不是讓它**不會漏**。

### 檔案落點與 git — ✅ **Tim 2026-07-29 拍板：入 git**

`commands_schema.json` **入 git**（跟 `commands_catalog.md` 同區）。
理由：Python 端在**沒開 Unity**時也要能預檢（agent 大部分時間就是這個情境）。
不入 git → clone 下來第一次跑就沒有 schema，只能 fail-open 退化成無預檢。

**入 git 帶來的兩個實作約束**（S4 必須處理，否則會製造 git 噪音）：
1. **內容雜湊未變就不寫檔** —— 否則每次編譯都動 mtime，`git status` 天天髒。
2. **輸出必須是穩定序** —— key 一律排序、`generated_at` 這類每次都變的欄位要嘛不寫、
   要嘛只在內容真的改變時才更新，否則 diff 永遠有一行雜訊。
   （傾向：`generated_at` 只在內容變更時更新，讓「無變更 = 零 diff」成立。）

---

## 6. Fail-open 原則 —— 本設計最根本的一條（gura 2026-07-29 拍磚後升級為 S0）

### 🩸 `create_trpg_room` 事故的根因不是 schema 過期

是 `tavern_cmd.validate_args` 遇到不認識的 op 時選擇了**擋掉**：

```python
return False, f"Tavern op '{op}' 未知；可用：{...}"
```

這個預檢的價值命題是「幫你早點抓錯字」—— 那是**便利性**。
而**便利性功能永遠不該有能力擋掉正確性**。判斷權威在 C# server，client 只是加速回饋。

### 修法：未知 op → 印警告後**放行**，讓 server 判

收益不對稱得很明顯：

| 情境 | fail-closed（現況） | fail-open（改後） |
|---|---|---|
| 使用者打錯字 | 立刻報錯 | 慢一個 Editor round-trip 才報錯（**還是會報錯**） |
| schema 漏一個 op | **功能被擋死**，且錯誤訊息還漂亮地列出「可用 op」讓人以為自己打錯字 | 功能照樣能用 |

### 這條讓其餘所有機制從「命脈」降級為「加分項」

schema 再怎麼過期，最壞情況退化成「沒有 client 預檢」，而**不會**退化成「擋住功能」。

它也回答了我原本問 basecamp 的那題（「怎麼讓 D 的警報不變成靠自覺」）：
**先讓警報不必被看見也不會出事，再談要不要 rate limit。**
警報一旦是唯一防線，就註定變成噪音然後被忽略；警報只要是第二防線，印在 stderr 每次都印也無所謂。

### 其餘 fail-open 規則（沿用既有分寸）

- `commands_schema.json` 不存在 → **跳過參數預檢**，行為退回「送出去讓 Editor 報錯」。
  **絕不因為讀不到 schema 就擋下呼叫** —— 那是「無法驗證 ≠ 不通過」的同一條（見 readback 的 `unverifiable`）。
- schema 有但某 cmd 沒宣告 `ArgsSpec` → 該 cmd 只查 type 存在性，不查參數。
- schema 過期（D 偵測到）→ **警告但放行**。過期的 schema 拿來擋人比沒有 schema 更糟。
- **未知 op / 未知 cmd type → 警告但放行**（S0）。
- ⚠ 唯一例外保留 fail-closed：`required` 缺項。那是「你這筆一定會失敗」的確定判斷，
  不是「我不認識」的無知判斷 —— 兩者分寸不同。

---

## 7. 分階段

> ## ✅ 實作完成紀錄（kotoko 2026-07-29）
>
> | 階段 | 狀態 | 產出 |
> |---|---|---|
> | S0 fail-open | ✅ | `tavern_cmd.validate_args`：未知 op 警告後放行；缺 op / required 缺項仍擋 |
> | S1 止血 | ✅ | `create_trpg_room` 補進表（alias 順序對齊 C#），實測不再被擋 |
> | S3 ArgsSpec | ✅ | `UCL_CmdArgsSpec` / `UCL_CmdOpSpec` + `HandlerBase.ArgsSpec`；`Cmd_Tavern` 宣告 **34 op** |
> | S4 匯出＋面板 | ✅ | `UCL_CmdSchemaExporter`（唯一實作）／`Cmd_ExportCmdSchema`／`UCL_AgentCmdAdminPage`／ControlPanel 入口 |
> | S4.5 Python 端 | ✅ | 載入產物取代手抄表；hash 不符 → **required 檢查整體降級** |
> | S4.6 每日自動 | ✅ | `UCL_CmdSchemaAutoSync`（`compilationFinished` + 每機每天一次節流，時間戳存 EditorPrefs） |
> | S5 type_aliases | ✅ | 產物帶出，`run_cmd.normalize_cmd_type` 優先讀產物，本地表退為 fallback |
> | 文件 | ✅ | `UCL_AgentCommand_Architecture` §5.1 ＋ `UCL_AgentCommandsPage` §4a／同步提示 |
>
> **驗收**：Unity compile 0 error（12–15s 真編譯，非 no-op 假綠）；`tavern_cmd --selftest` 全綠
> （含跨語言 hash 契約、產物已載入、未過期三項監視器）；`tavern_handshake --selftest` 回歸 exit 0；
> live E2E：`run ExportCmdSchema` → 51 cmd / 34 op 落地、二次執行回「內容未變，未寫檔」；
> 過期路徑實測（竄改產物 hash → 自動降級 → 還原）。
>
> **實作中修正設計的兩處**：
> - 產物落點定為 `<RepoRoot>/AgentCommands/`（`UCL_RepoPath.AgentCommandsDir`）——
>   canonical RPC 錨點、入 git、跨專案穩，不跟可搬遷的 DataRoot 走。
> - 每日自動同步的節流時間戳存 **EditorPrefs**，**不寫進產物** ——
>   產物特意移除所有 wall-clock 欄位才換來「內容沒變 = 零 diff」，寫回去等於把剛消滅的噪音請回來，
>   而且那是 per-machine 狀態，本來就不該進版控。

| 階段 | 內容 | 成本 | 產出 |
|---|---|---|---|
| **S0** | 未知 op / 未知 cmd type → **fail-open**（警告後放行） | ~10 行 | **治本** — 補 dict 只治這一個 op，fail-open 治**未來所有還沒被抄進表的 op** |
| **S1** | 補 Python 端 `create_trpg_room`（一筆 dict） | 5 分鐘 | 止血 — 讓打錯字仍能立刻報錯（S0 之後這條是**便利性**不是必要性） |
| **S2** | A1.5：C# 匯出 **op 名單 only** + Python set 比對報警 | C# ~20 行 / Py ~30 行 | 漂移從此有警報 |
| **S3** | `UCL_CmdArgsSpec` 欄位 + `Cmd_Tavern` 填 34 個 op 的 required/alias | C# ~120 行（機械，照 `GetArg` 巢狀預設值抄） | Python 那張表可刪 |
| **S4** | **`UCL_AgentCmdAdminPage` + `Cmd_ExportCmdSchema` + ControlPanel 入口**（手動生成為主） | ~200 行 | Tim 要的雙重保險：面板按鈕 ＋ CMD 觸發 |
| **S4.5** | `compilationFinished` 只更新來源 hash（不重寫產物）+ Python 過期→自動降級 fail-open | ~50 行 | 過期改變**行為**，不只改變輸出 |
| **S5** | `type_aliases` 一併匯出，消滅第四處鏡像 | ~15 行 | run_cmd / Registry 兩份合一 |
| **S6**（選配） | load-bearing 化：`GetArg` → 走 spec 取值 | 高（134 處） | 結構性消滅 C# 內部鏡像 |

**S0 + S1 現在就該做**（live bug，不是設計題）。S2–S5 是本設計主體。

### S6 建議不做 —— 理由已從「co-location 夠用」換成更強的版本（gura 2026-07-29）

Round 1 我用的座標軸是「有沒有人讀它」，結論靠 §3 那個 n=1 的對照。gura 換了個更準的軸：
**驅動漂移的不是「有沒有人讀」，是「錯的時候會不會有東西叫」。**

- `ArgsSchema`：錯 → 改 switch 的人眼睛掃過就看到（**弱**警報，但在**編輯時**就響）
- Python 表：錯 → 沒有任何東西比對兩邊 → **永遠不響**，而且它還在 enforce，
  於是把「沒警報」升級成「擋掉合法功能」

而且 n=1 的弱點有硬反例：**本次 `tavern_cmd.py` 重構的 wait-reply shim，是同檔 co-located、
天天被跑、還有 29 項 selftest 在讀它 —— 照樣悄悄變了行為**（雙鍵並存從「先到先贏」變成「後到覆蓋」，
四個 shim 測項剛好都只給單鍵，組合是覆蓋盲區。gura 差分測試抓到，已修，見 §附錄）。
**「被讀」跟「被驗」是兩件事**；co-location 只是把警報做成「人眼在編輯時看一眼」，
它能防漏 op 名字（顯眼），防不了 alias 少一條（不顯眼）。

**所以：結論一樣（S6 不做），但理由是「S2 的 set 比對才是真警報，S6 只是一個更弱的替代警報」。**
有了 S2 就不需要用 134 處 `GetArg` 改寫去換一個更弱的東西。這個理由不依賴樣本數。

---

## 8. 決策狀態

| # | 項目 | 狀態 |
|---|---|---|
| 1 | S1：補 `create_trpg_room` 止血 | ✅ **拍板：補**（kotoko / gura / basecamp 一致）。補時旁邊留 `# 手抄鏡像（A2 codegen 落地後刪除）` |
| 2 | `commands_schema.json` 入 git | ✅ **Tim 拍板：入** |
| 3 | S6（load-bearing 化 134 處 `GetArg`） | ✅ **拍板：不做**。⚠ basecamp 加的前提：**不做的前提是 codegen 真的落地**；若 S3/S4 最後沒做，S6 要重回桌上 |
| 4 | 刷新時機 | ✅ **拍板：手動為主（面板＋CMD）、`compilationFinished` 只標記過期、過期→自動降級**。判準用**內容 hash 不用 mtime** |
| 5 | S0：未知 op / cmd type → fail-open | ✅ **拍板：做**，排在 S1 之前（`required` 缺項仍 fail-closed） |
| 6 | **CMD 管理面板** | ✅ **Tim 追加拍板**：`UCL_AgentCmdAdminPage`，入口在 `UCL_ControlPanelPage`，參考 `UCL_ChatTavernAdminPage` |
| 7 | 產物格式 | ✅ **JSON，不生成 `.py`**（basecamp：讓 C# 生成另一個語言的語法 = 引用地獄的另一個入口；且產物可執行 = import 時炸整支工具） |

---

## 附錄 — 本輪 QA 抓到的行為分歧（已修）

`tavern_cmd.py` 從 run_cmd 搬移時，`promote_wait_reply_arg` 的**雙鍵並存**語意跑掉了：

| 輸入 | 搬移前 | 搬移後（bug） | 修正後 |
|---|---|---|---|
| `wait-reply=11` + `wait_reply=22` | **11**（先到先贏） | 22（後到覆蓋） | **11** ✅ |

成因：舊碼靠 `if getattr(args,"wait_reply",None) is None` 守門，第一個鍵設完值後第二個就進不去；
搬移時寫成迴圈內無條件覆寫。

**我的 29 項 selftest 沒抓到 —— 四個 shim 測項都只給單鍵，組合是覆蓋盲區。**
gura 用差分測試（舊碼語意重現 vs 新實作，逐案比對）抓到。
已修，並補兩個測項（雙鍵並存 → 11、首鍵壞 → 次鍵接手 33）；
差分複驗六案**分歧數 0**，selftest 33 項全綠。

> 教訓：**「行為零變化」不能靠自己列測項自我驗證** —— 我列的測項反映的是「我以為的行為」，
> 而分歧恰恰發生在「我以為的」與「實際的」之間。差分測試（拿舊碼當 oracle 逐案對跑）
> 才是這類重構的正確驗收法。這條寫進 Plan_RunCmd_Split 的後續拆分驗收標準。

---

*本題最值得記的一句：**裝飾性的宣告不必然會爛，取決於它離實作多遠。**
`Cmd_Tavern.ArgsSchema` 沒人讀卻活得好好的，因為它就在 switch 上面；
Python 那份被程式天天讀，卻因為隔了一個語言而死掉。
所以「要不要 load-bearing」不是這題的第一問題，「離得夠不夠近」才是。🔍*
