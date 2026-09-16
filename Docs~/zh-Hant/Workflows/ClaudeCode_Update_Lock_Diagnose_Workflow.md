---
title: Claude Code 自動更新卡住排查（「其他程式正在用這個檔案」）
description: Claude Code 是 MSIX 套件，自動更新要求舊版行程**全部消失**才註冊得了新版；卡住時的症狀是「Claude Code 自己關閉且無法重啟」＋一個指著 `C:\Program Files\WindowsApps\Claude_<版本>_...` 的「其他程式正在用這個檔案」對話框。含事件日誌查法（事後可查）／resmon 抓持有者（要現場）／為什麼「關掉 Unity Editor」有效而它不是答案／本專案已修掉的一個 process handle 洩漏
last_updated: 2026-09-16
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
aliases: [其他程式正在用這個檔案, 0x80073D02, WindowsApps, MSIX, Claude Code 無法重啟, Claude Code 被關閉, AppXDeploymentServer, 延後註冊, resmon, 關聯的控制代碼]
tags: [claude_code, windows, msix, diagnose, workflow, process_handle]
---

# 🔒 Claude Code 自動更新卡住排查

> [!IMPORTANT]
> **解決什麼問題**：Claude Code **自己關閉**之後**無法重啟**，跳出
> 「`C:\Program Files\WindowsApps\Claude_1.52386.0.0_...` 其他程式正在用這個檔案。」
>
> **一句話成因**：那不是兩個問題，是**同一件事的兩半** ——
> Claude Code 是 **MSIX 套件**，自動更新要求**舊版套件的行程全部消失**才註冊得了新版。
> 舊版結束 ＝ Claude Code 關閉；註冊被擋 ＝ 起不來。

> [!WARNING]
> ## ⛔ 撞到時的第一件事：**不要關掉 Unity Editor**
>
> 關掉 Editor 常常「就好了」——**而那個動作同時消滅了現場**。
> 這一族問題的特徵就是：**現場沒了就永遠查不到持有者是誰**，
> 於是下一次還會再撞一次，而每一次都只留下「我關了 Editor 就好了」這個沒有射程的結論。
>
> ⇒ 先抓持有者（§3），再關。抓一次要一分鐘。

---

## 1. 症狀與底層錯誤碼

| 使用者看到的 | 底層 |
|---|---|
| Claude Code 突然關閉 | MSIX 更新要求舊版套件的行程結束 |
| 重啟時跳「其他程式正在用這個檔案」 | 部署 `Register` 失敗，錯誤 **`0x80073D02`** |
| 對話框上的路徑是 `WindowsApps\Claude_<新版本>_...` | 那是**新版**的套件路徑（正在 stage／等註冊） |

`0x80073D02` 的原文是：**「無法安裝，因為必須先關閉下列應用程式：`Claude_<舊版本>_...`」**
—— ⚠ 它點名要關的是**舊版 Claude 自己**，不是 Unity、不是任何編輯器。

---

## 2. 事後也查得到的那一半：事件日誌（⭐ 先跑這個）

**現場沒了也查得到** —— 日誌留著，這是這張排查表裡唯一「事後仍有讀數」的一格。

```powershell
powershell -NoProfile -Command "Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 2000 | Where-Object { $_.Message -match 'Claude' } | Select-Object TimeCreated,Id,Level | Format-Table -AutoSize"
```

要認的事件 Id：

| Id | Level | 意思 |
|---|---|---|
| **658** | 警告 | 新版**標記為「延後註冊」** —— 因為舊版**正在執行中** |
| **404 / 419 / 401** | 錯誤 | 部署 `Register` **失敗**，附錯誤碼（`0x80073D02` 就在這裡） |
| **400** | 資訊 | 部署 `Register` **成功** |

### 🔬 2026-09-11 的現場讀數（本文件的來源）

```
08:35:47  id=658  警告：Claude_1.52386 標記為延後註冊 —— 因為 Claude_1.49585 正在執行中
09:10:36  id=419  錯誤 0x80073D02：無法安裝，因為必須先關閉 Claude_1.49585
09:10:36  id=404  部署 Register 失敗，錯誤 0x80073D02
10:44:55  id=400  Register 成功
```

⚠ **這不是偶發**：`id=658`（延後註冊）在 **9/4、9/7、9/9、9/11** 各出現一次
⇒ **每次 Claude 更新都會走到這一格**，只是不一定升級成 404 錯誤。
📌 所以「這次特別倒楣」是錯的讀法：常態是延後註冊，**例外是那次延後沒能解開**。

---

## 3. 只有現場才量得到的那一半：誰持有檔案

⇒ 收窄後真正的問題是：**是什麼讓「舊版 Claude 的行程」在 Claude 都關掉之後還沒消失？**

1. `Win+R` → **`resmon`**（資源監視器）
2. **CPU** 分頁 → 展開「**關聯的控制代碼**」
3. 搜尋框打 `Claude`
4. **抄下那一列的「映像」（行程名）與 PID** —— 那就是持有者
5. 再抄它的親代鏈：

```powershell
powershell -NoProfile -Command "$p=<那個PID>; while($p){$x=Get-CimInstance Win32_Process -Filter \"ProcessId=$p\"; if(-not $x){break}; '{0} {1} parent={2}' -f $x.ProcessId,$x.Name,$x.ParentProcessId; $p=$x.ParentProcessId}"
```

> [!CAUTION]
> **`openfiles.exe` 不要用。** 它要系統管理員權限（實測當場被拒），
> 而 `openfiles /local on` 還要**重開機**才生效 —— 重開機同樣會消滅現場。
> `resmon` 是內建、不必裝、不必重開機的那一條。

---

## 4. ⚠ 「關掉 Unity Editor 就好了」**不等於**「Unity 鎖住它」

關 Editor 會順帶結束**它的全部子行程**。
⇒ 犯人可能是 Editor 本身，也可能是它底下某一顆，**而這兩者的修法不同**
（一個要改 Editor 的行為，一個要改那支工具怎麼被 spawn）。
⛔ 在拿到持有者行程名之前，這兩個分不開。

### 一個聽起來很合理、而讀數**否證掉**的假說

> 「Unity 是 Claude Code 開起來的 ⇒ 它繼承了 Claude 的檔案 handle。」

**不成立**，兩條讀數各否一次：

- **親代鏈**：`explorer.exe` → `Unity Hub.exe` → `Unity.exe`，跟 Claude **雙向都沒有親子關係**
  （使用者確認：Unity 是他手動從 Unity Hub 開的）。
- **時間軸**：2026-09-11 那顆 `Unity.exe` 啟動於 **10:49:01**，而 Claude 起來是 **10:48:47**
  ⇒ **它比那次失敗（09:10）晚了一個半小時，當時它還不存在。**

📌 寫進來不是湊字數：那是這題**第一個會被想到的答案**，而它已經被否掉了，下一個人不必再走一次。

---

## 5. 本專案這側已修掉的一個候選（`UCL_Core fa53536f`）

`UCL_RemoteWindowControl.GetProcessName` 原本寫：

```csharp
try { return Process.GetProcessById((int)processId).ProcessName ?? ""; }   // ← 從不 Dispose
```

- `Process.GetProcessById` 回傳的物件**持有一顆 OS process handle**。
- 它在 `EnumWindows` 的 callback 裡**逐一個可見視窗**呼叫一次
  ⇒ **一次掃描洩漏「可見視窗數」顆 handle**，而它們活到 **Editor 關閉**。
- 而這支的工作**就是去找 `claude.exe` 的視窗**（`IsTargetProcess` 拿 `processName == "claude"` 當命中）。

⇒ 一顆還開著的 process handle 會讓**已結束的行程留在「未完全消失」的狀態**，
而那正是 `0x80073D02` 在抱怨的東西 —— 形狀跟「關掉 Editor 之後才更新得了」**吻合**。

📌 同族 5 處 `GetProcessById` 全站掃過，**其餘四處都有 `using`**
（兩支 ProcessRegistry／ScreenStream daemon／ScreenStreamPage），**只有這一處漏**。

> [!IMPORTANT]
> ## ⛔ 吻合不是證明
>
> 上面那個洞**沒有被證實是真兇** —— 證它需要 §3 的現場讀數，而當時現場已經被關掉 Editor 消滅了。
> 修它的理由是**它本來就不該存在**（一個不歸還的 handle），不是「我們找到成因了」。
>
> ⇒ 下次再撞到，**照 §3 抓一次持有者**：
> · 如果是 `Unity.exe` ⇒ 這個洞的修法方向對，可以把這條線收掉；
> · 如果是別的行程 ⇒ **這份文件的 §5 就要改寫**，⛔ 別讓它變成一個沒人回頭驗的故事。

### ⭐ 2026-09-16：上面那句兌現了 —— 是**別的行程**，所以這一節改寫

使用者 2026-09-16 回報（累計第 3+ 次）：**「關掉 senate 之後才能重啟 Claude Code」**，
並補一格關鍵現場：**`senate.exe` 在執行、但視窗沒有開啟**。

| | 讀數 |
|---|---|
| ✅ 同一天又發生一次 | `08:12 id=658 延後註冊` → `09:10 id=419/404 0x80073D02` → `09:48 id=400 註冊成功`（最後那格在使用者關掉 senate 之後） |
| ✅ 不是偶發 | `id=658` 在 9/4、9/7、9/9、9/11、9/16 各一次 ⇒ **每次 Claude 更新都會走到這一格** |
| ✅ 親代鏈（agent 這側實測） | `Claude.exe`（`WindowsApps\Claude_*`，MSIX 封裝）→ `claude-code` → `bash` → **agent 起的任何東西** |
| ✅ 常駐 Server 沒有視窗 | 使用者看得到的只有「Claude Code 起不來」⇒ **「有東西壓著」與「Claude 壞了」在他那側同形** |
| ⛔ **仍然沒拿到** | §3 那格 —— **現場的 `resmon` 持有者讀數**。使用者的觀察是「關掉它就好了」，那是**相關**不是持有證據 |

⇒ **§5 上面那個 `GetProcessName` handle 洩漏的線，到此收掉**：它跟這次的現場對不上
（Unity 那條在 2026-09-11 就被親代鏈與時間軸各否證一次）。修它仍然是對的，但**它不是這隻的成因**。

⇒ **已落的修法**（`Senate 8a8d7dc`）：`ServerAutoStart` 改走 WMI `Win32_Process.Create` ⇒
常駐 Server 的親代是 `WmiPrvSE`，**不在任何人的行程樹底下**（實測可重現）。
⭐ 它對「handle 繼承」與「套件身分繼承」**兩種機制都有效**，所以不必等上面那格 ⛔ 補上才成立。

> ⚠ **下一個讀數在哪**：Claude 的下一次更新。
> 那時如果**有一顆 senate Server 在跑、而更新照樣註冊成功**，⛔ 那格就填上了。
> 反之若仍然失敗 ⇒ 持有者另有其人，回 §3 抓一次 —— 而這一次**不要先關掉任何東西**。

---

## 6. 撞到時的處置順序（照抄）

0. ⚠ **先看有沒有 `senate.exe` 在跑**（2026-09-16 新增）—— 常駐 Server **沒有視窗**，
   所以除了工作管理員以外看不到它，而它是目前最可疑的那一顆。
   ⛔ 但**別急著關**：先做第 1 步抓讀數，不然又只會留下「關掉它就好了」這個沒有射程的結論。
1. ⛔ **先別關 Editor。** 跑 §3 抓持有者（一分鐘）。
2. 抓完之後，**只結束那一顆**（不是關整個 Editor）—— 然後試著啟動 Claude Code。
   ⇒ 成功 ＝ 你剛剛證明了它就是持有者；失敗 ＝ 還有第二顆，回 §3 再看一次。
3. 真的急著要用，再關 Editor（那一定有效，但**它不會留下讀數**）。
4. 事後補跑 §2 的事件日誌，把 `658 → 404 → 400` 的時間軸抄進單子
   —— 那一段**永遠查得到**，別讓它跟著現場一起被丟掉。

---

## 7. 延伸

| 想知道 | 看哪 |
|---|---|
| 這份文件的原始單 | `AgentCommands/Tasks/tasks/0204.md` |
| 修掉那個 handle 洩漏的 commit | `UCL_Core fa53536f` |
| Bartender 遠端視窗控制本身 | [`Bartender_Workflow`](Bartender_Workflow.md) |
