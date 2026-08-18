---
title: C# Coding Standards
description: UCL_Core C# 設定資料、字串 key 與外部 Process 的共用撰寫規範。
last_updated: 2026-08-18
target_audience: [AI_Agent, Gameplay_Programmer, Tools_Maintainer]
related:
  - Code_Comment_Standards.md | 程式碼註解規範 | 註解與文件化原則
  - AI_READABILITY_GUIDELINES.md | AI Readability Guidelines | 共用文件規範
---

# C# Coding Standards

## 設定與 JSON 資料

- 優先以具名 C# model（例如 `UnityJsonSerializable`）承載已知 schema，讓欄位、預設值與使用點可被編譯器檢查。
- 不要在一般業務流程直接裸用 `JsonData` 的字串索引、`GetString` 或 `GetBool` 來讀寫已知欄位。
- `JsonData` 可以保留在邊界層：解析外部 JSON、保存未知／可擴充欄位、或需要無損 round-trip 的 migration。使用時須把原因寫在註解中。
- schema 尚未穩定時，先建立最小的 typed projection；未知欄位必須被保留，不可因一次編輯而靜默遺失。

```csharp
// Good: known fields use a typed model.
NotifyConfig config = LoadNotifyConfig();
if (config.tavern_mirror.enabled) SendMirror();

// Boundary-only: preserve plugin-defined fields not represented by the model.
JsonData rawUnknownFields = LoadUnknownFieldsForRoundTrip();
```

### 換成 typed model 時的三個坑（2026-08-18 實測，Cmd_FreeTime session）

改用 `UnityJsonSerializable` **不是純粹的重構** —— 序列化器的行為跟手搭 `JsonData` 不一樣，
而差異全部落在「編譯過、看起來對、但 wire format 變了」這一格。

**① 欄位名就是 JSON 鍵名。**
`UnityJsonSerializable` 走 `FieldNameUnityVer`，它**只脫 `m_` 前綴**，其餘原樣輸出。
所以要沿用既有檔的鍵名（例：`session_id`），欄位就得叫 `session_id` ——
這時**刻意不走 `m_PascalCase` 慣例**，而且必須在 class 註解裡寫明為什麼，
否則下一個人會把它「修正」成 `m_SessionId`，然後鍵名跟著改。

**② `bool` 會被寫成 `"True"` / `"False"` 字串，不是原生 JSON bool。**
UCL_Json 的舊慣例如此（`UCL_JsonLib` 序列化端 `aValue.ToString()`），
**C# 載入端雙接所以看不出差別** —— 但跨語言讀取端看得出：
python `json.loads` 拿到字串 `"False"`，而它在 Python 裡是 **truthy**。

> 🩸 實測：`FreeTime/sessions/*.json` 原本是 `new JsonData(true)`（原生 bool），
> 改 typed model 後變成 `"active":"False"`。後果不是解析失敗（那會喊），
> 是 `freetime.py` 的 `if not s.get("active")` 通過 ⇒ **提前收工的人會被判成還在自由時間**。
>
> ⇒ 檔案有**非 C# 讀取端**時，在 model 裡 `override SerializeToJson()` 把 bool 改回原生：
> ```csharp
> public override JsonData SerializeToJson()
> {
>     var aData = base.SerializeToJson();
>     aData["active"] = new JsonData(active);   // 原生 bool，不是 "True"/"False"
>     return aData;
> }
> ```
> 純 C# 內部使用的資料不必這樣做（載入端雙接）。**判準是「有沒有別的語言在讀」。**

**③ 驗收不是「編譯過」，是把既有檔 round-trip 一次比對。**
拿一份真實的舊檔讀進 model 再吐回 JSON，逐鍵比對鍵名、型別、值。
`Cmd_Invoke` 可以直接做（不必開頁面、不必跑完整流程）：

```bash
run_cmd.py --persona <me> run Invoke --arg type=<Type> --arg member=<LoadXxx>     --arg nonPublic=true --arg paramTypes='System.String' --arg args='<key>' --arg storeAs=s
run_cmd.py --persona <me> run Invoke --arg target='$s' --arg member=SerializeToJson
```

⚠ 順序有陷阱：**改完 .cs 送 recompile 之後，回傳的 `errors=0` 可能是舊快照** ——
上面那隻 bool 就是在「recompile 回報 0 錯」之後才被 round-trip 抓到的（當時跑的還是舊組件，
真正的編譯錯誤躺在 ErrorLog 裡）。判準見 `ucl-compile-error`：
**`check_compile.py` 沒標 STALE 且 ErrorLog 對帳一致，才算編過。**

**④ 欄位順序會變。** base/derived 拆開後，衍生類欄位可能排到最前面。
鍵序對兩端都不重要（都按鍵取值），但 diff 會整片變 —— 別把它誤讀成內容變了。

## letters 目錄底下的路徑（硬規則）

**任何 `letters/…` 底下的路徑一律走 `UCL_LettersPath`，不要自己 `Path.Combine`。**

| 要什麼 | 用哪個 |
|---|---|
| letters 根 | `UCL_LettersPath.Root`（它委派 `UCL_AwakeningService.LettersDir` —— **override 語意的唯一擁有者**） |
| 某人的信目錄 | `UCL_LettersPath.PersonaDir(persona)` |
| Cmd 回傳檔目錄 | `UCL_LettersPath.CmdDir(persona)` |
| 一份 Cmd 回傳檔 | `UCL_LettersPath.CmdPayload(persona, cmd, step)` |

```csharp
// ✅
string aPath = UCL_LettersPath.CmdPayload(iPersona, "freetime", iStep);

// ❌ 以下每一種都在 repo 裡出現過
Path.Combine(UCL_AwakeningService.LettersDir, iPersona, $"_{iCmd}_{iStep}.md")   // 自己組版面
Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "baton", "letters")   // 連根都自己推
```

> 🩸 **為什麼是硬規則**：2026-08-18 之前 `Cmd_FreeTime` / `Cmd_Sculpture` / `Cmd_StreamWatch`
> 各自組一份回傳檔路徑，其中 `Cmd_StreamWatch` 連 letters 根都自己推 ——
> **同一個目錄的第四種算法**。於是 Tim 要求「回傳檔搬進 `cmd/` 子目錄」時，
> 那件事從「改一行」變成「12 處各改一次」，而**漏掉一處不會報錯**
> （寫檔會自動建目錄 ⇒ 那支的回傳檔靜靜留在舊位置，看起來完全正常）。

⚠ **對側契約**：python 端等價入口是 `_lib/ucl_paths.py` 的
`letters_root()` / `letters_cmd_dir()` / `letters_cmd_payload()`。
**兩端要一起改** —— 只改一端的後果是兩邊各看各的目錄，而**兩邊都不會報錯**。

## 字串 key 與設定欄位名稱

- 重複使用、代表 schema／EditorPrefs／JSON／routing 的字串 key，先宣告為具語意的 `const string`，再由所有讀寫點共用。
- key 常數應與使用類別同置；跨類別或跨 assembly 的公開 schema key 才使用 `public const`。
- 一次性 UI 文案、日誌內容或不具識別語意的字串不需要為了形式化而抽成常數。

```csharp
const string KeyTavernMirror = "tavern_mirror";
const string KeyWebhookUrls = "webhook_urls";

if (config.Contains(KeyTavernMirror))
    Write(KeyWebhookUrls);
```

> [!IMPORTANT]
> 新增 key 時，先搜尋既有名稱與 schema；不要用近似拼字另建一個常數，避免產生雙重設定來源。

## 外部 Process（硬規則）

> [!CAUTION]
> **C# 端開的每一顆外部 Process 都必須經過 `UCL_ProcessRegistryService` 登記。**
> 直接 `new Process()` / `Process.Start()` 之後不登記 = 那顆 process 沒有任何人管得到它。

**為什麼是硬規則**：Editor 的 domain reload / recompile 會把 C# 的 `Process` 物件整批清掉，
但**作業系統層的 process 不會跟著死**。於是每次重編都可能再生一顆，舊的變成沒有 handle 的孤兒 ——
累積下去就是 Tim 遇過的**屍潮**（重複開 process 直到電腦卡死）。
這一族的壞法特別難查：每一顆單看都正常，症狀只有「電腦越來越慢」。

```csharp
// 1) spawn 前先收掉同 tag 的舊 process（singleton 語意；跨 domain reload 也有效，
//    因為身分是從磁碟記錄讀回來的，不依賴 C# 端的 Process 物件）
UCL_ProcessRegistryService.KillAllByTag("my_daemon");

var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
proc.Start();

// 2) spawn 後立刻登記 — tag 是穩定識別字，description 要寫「這顆在做什麼」
UCL_ProcessRegistryService.Register(proc, "my_daemon",
    "這顆 process 在做什麼（給人看，也給誤殺防護判斷）", nameof(MyCaller));

// 3) 反登記 —— **不是** 只在正常路徑呼叫。優先用 RegisterScope（見下），
//    手寫時一律放 finally
UCL_ProcessRegistryService.Unregister(proc.Id, "my_daemon");
```

### 首選寫法：`RegisterScope`（`using` 宣告）

**登記／反登記必須成對，而成對最常見的破法是例外路徑。** 手寫 `try/finally` 時很容易
只寫在正常路徑上，留下一筆已死的 PID 記錄。包成 `IDisposable` 之後，成對性由語言保證：

```csharp
p.Start();
using var _ = UCL_ProcessRegistryService.RegisterScope(p, TAG, "在做什麼", nameof(MyPage));
// …既有的 ReadToEnd / WaitForExit(timeout) / 輪詢迴圈照舊，不必動任何括號…
```

C# 8 的 `using` 宣告在**離開所在區塊時**自動 Dispose ——
正常結束、逾時 kill、使用者按 Cancel、丟例外，**四條路都會反登記**。
它同時的好處是**加登記不必改動既有的括號結構**（一行插入），
所以替既有程式碼補登記時風險最低。

**fire-and-forget 型**（開檔案總管、用預設程式開檔 —— 呼叫端不等它，沒有 `finally` 可放）
走另一支：

```csharp
UCL_ProcessRegistryService.StartAndRegister(psi, TAG, "在做什麼", nameof(MyPage));
```

它是 `allowMultiple`（**不是** singleton）—— 使用者可以同時開好幾個檔案總管，
套 `KillAllByTag` 會把上一個關掉。記錄由 `CleanupStale()` 回收，
而那支掛在 `[InitializeOnLoad]`（每次 domain reload 自動跑一次）。

> [!NOTE]
> **「不會卡住」不是不登記的理由**（Tim 2026-08-06 拍板全面登記）。
> 不卡住不等於不會累積；而更重要的是：**一份有例外的登記表，
> 最危險的不是漏掉那幾筆，是它讓人停止懷疑。**
> `UCL_ProcessAdminPage` 的存在隱含它是完整的 —— 一旦有一批 spawn 按政策被排除，
> 它回答的就不再是「Editor 開過什麼」，而是「我們選擇顯示什麼」。

### 跑一次性外部工具的完整骨架（Editor 頁面呼叫 python / CLI）

上面三步是**登記**的最小形；實際在頁面上跑工具還有四個一定要一起做的動作，
少任何一個都會出現「外觀正常但壞掉」的結果。骨架：

```csharp
Task.Run(() =>                                  // ① 不在主執行緒跑
{
    var so = new StringBuilder(); var se = new StringBuilder();
    int exit = -1, pid = -1;
    try
    {
        using (var p = new Process())
        {
            p.StartInfo.FileName = "python";
            p.StartInfo.Arguments = $"\"{script}\" {args}";
            p.StartInfo.WorkingDirectory = UCL_RepoPath.RepoRoot;   // ② 子行程需要的 cwd
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError  = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;      // ③ 編碼
            p.StartInfo.StandardErrorEncoding  = Encoding.UTF8;
            p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };

            UCL_ProcessRegistryService.KillAllByTag(TAG);
            p.Start();
            UCL_ProcessRegistryService.Register(p, TAG, "在做什麼", nameof(MyPage));
            pid = p.Id;
            p.BeginOutputReadLine();                                  // ④ 兩條都要非阻塞讀
            p.BeginErrorReadLine();
            if (!p.WaitForExit(TIMEOUT_MS)) se.AppendLine("逾時 — 已放棄等待（行程可能仍在跑）");
            else exit = p.ExitCode;
        }
    }
    catch (Exception e) { se.AppendLine(e.ToString()); }
    finally
    {
        if (pid > 0) UCL_ProcessRegistryService.Unregister(pid, TAG);  // ⑤ 一定放 finally
    }
    EditorApplication.delayCall += () => { /* 回主執行緒更新 UI */ };   // ⑥
});
```

每一項的理由（都是踩過才寫下來的）：

- **① 背景執行緒**：在主執行緒 `WaitForExit` 會凍住整個 Editor，連 AgentCommand watcher 一起卡死。
- **② `WorkingDirectory`**：子行程若要跑 `git`（或任何依賴 cwd 的工具），沒設就會在 Unity 的
  工作目錄執行，錯誤訊息通常長成「找不到檔案」而**指不到真正的原因**。
- **③ 編碼三件套**：漏掉 `StandardXxxEncoding` 或 `PYTHONIOENCODING`，中文輸出會變亂碼，
  而**亂碼看起來像工具壞了**，實際上工具是對的。
- **④ 兩條 stream 都要非阻塞讀**：只讀一條時，子行程寫另一條把 buffer 填滿 →
  子行程卡在 write、呼叫端卡在讀 → **永久 deadlock，沒有任何錯誤訊息**。
- **⑤ `Unregister` 放 `finally`**：只寫在正常路徑的話，例外路徑會在記錄檔留下一個已死的 PID。
  那不會誤殺（`KillAllByTag` 會做身分驗證判 Dead 而跳過），但殘檔會讓
  `UCL_ProcessAdminPage` 顯示不存在的 process —— **監控畫面說謊比沒有監控更糟**。
- **⑥ 回主執行緒才碰 UI**：Unity 的 API 不是 thread-safe；背景執行緒直接改 UI state 會偶發炸掉，
  而它的偶發性正好讓它躲過測試。

**逾時值不是「怕它跑太久」，是「多久算異常」。** 全量掃描本來就慢，
所以門檻要訂在「命中代表真的出事」那個量級（例：攤平同步 30 分鐘），
而不是訂在「使用者會不耐煩」那個量級。

> [!IMPORTANT]
> **`WaitForExit(timeout)` 是預設，不是唯一合法解。**
> 需要「可取消」時，自寫輪詢迴圈（每 N ms 檢查 `HasExited` + cancel token／進度條 Cancel）
> 是**正確的**，不該為了統一而改掉 —— `WaitForExit` 沒有取消能力，換過去等於刪功能。
> 現行三處刻意保留輪詢：`UCL_KnowledgeBaseRunner`、`UCL_MediaAdminRunner`（吃
> `CancellationToken`）、`UCL_BartenderDaemon`（進度條 Cancel）。
>
> 那三處**真正缺的從來不是逾時，是登記**：它們的 `Kill()` 只在 C# 的 `Process` 物件還活著時
> 有效，domain reload 一來就失去對象 —— 而它們看起來是「已經處理過逾時」的那種，
> **最容易讓人以為安全**。補上 `RegisterScope` 之後防護才跨得過 domain reload。

- **身分 = PID + process name + start time**，不是只有 PID —— PID 會被 OS 回收再發，
  只憑 PID 去 kill 會誤殺別人的 process（`UCL_ProcessStatus.PidReused` 就是為此存在）。
- `Register` 預設 `allowMultiple=false`（singleton）：登記時會先收掉既存同 tag。
  要「舊的先死新的才生」的嚴格順序，spawn 前自行呼叫 `KillAllByTag`。
- ~~短命的一次性 process 可以不登記~~ —— **2026-08-06 作廢，Tim 拍板全部都要登記。**
  舊條文的判準是「會不會卡住」，而那個判準有兩個洞：不卡住不等於不會累積；
  更重要的是它讓登記表變成一份**有例外的清單**（理由見上方 NOTE）。
  短命的走 `StartAndRegister`，記錄由 `CleanupStale()` 自動回收，成本接近零。
- 檢視／處置走 `UCL_ProcessAdminPage`。

參考實作：
- **常駐型** `UCL_ScreenStreamDaemon`（pre-spawn `KillAllByTag` + `Register` + 結束時 `Unregister`）
- **一次性工具型（首選範本）** `UCL_GitFlattenSyncPage` — tag `git_flatten_sync`，
  上方六件事做齊的一份完整實作
- **`RegisterScope` 用法** `UCL_AgentSkillManagerPage` / `UCL_LoginStatusPage` /
  `UCL_LibraryManagePage` / `UCL_BartenderDaemon`（2026-08-06 全面補登記那批）
- **fire-and-forget** `UCL_ExplorerUtil` — tag `explorer_open`

> [!TIP]
> 一次性工具還有一條**不屬於 Process 但常一起漏**的規則：**腳本路徑與資料路徑都不可寫死**。
> 走 `UCL_EditorPath.CorePath` / `UCL_RepoPath` / 該子系統自己的解析器（例如
> `UCL_ChatTavernIO.GetRoomsRoot()`），否則換一個專案就找不到檔 ——
> 而 `File.Exists` 失敗後若 fail-soft return，那是**連 warning 都沒有的靜默失效**。
> 解析失敗時要把**解析結果印出來**，讓人看得到它找去了哪裡。
