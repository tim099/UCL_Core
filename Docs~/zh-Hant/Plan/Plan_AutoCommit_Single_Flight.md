---
title: 自動提交單一飛行 — 用 static class 保證不能同時觸發
slug: autocommit-single-flight
status: draft（2026-08-21 Tim 說「A 可以先紀錄為 Plan」—— **一行 code 都還沒動**）
created_at: 2026-08-21T02:15:00Z
created_by: summit
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Cmd_Concurrency_Hardening.md | Cmd 多人併發強化 | **前置相依**：那份若落地（各 persona lane 併行），本案從「選配」變成「必要」
  - ucl_core:Docs~/{lang}/Workflows/AutoCommit_Config_Workflow.md | 自動提交設定 SOP | 本案要保護的流程
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AutoCommitPage.md | 自動提交頁 | 兩個消費端之一
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 人工提交那條路（本案不動它）
---

# 自動提交單一飛行（Single-Flight）

> **一句話**：把「不會有兩個自動提交同時跑」這件事從**操作層的習慣**搬到**程式層的閘門** ——
> 不是因為怕並發，是因為現在那句話**沒有任何地方可以被驗**。

## 0. 起因（Tim 2026-08-21，四句演進照實記）

1. 「目前其實不用擋」
2. 「不會發生同時在 commit 的情況」
3. **「或是把自動 commit 流程進 C# static class 管理，確保不能同時觸發」**
4. 「因為目前 commit 也是走一個固定流程（會觸發廣播），理論上可以避開衝突」

⇒ ①②④ 是「風險由時機與既有流程吸收」，③ 是「把它變成機制」。
本 Plan 記錄 ③，而**現況照 ①②④ 運作**（不擋）。

## 1. 現況（2026-08-21 讀 code，不是印象）

| 消費端 | 有沒有防重入 | 讀數 |
|---|---|---|
| `UCL_AutoCommitPage` | **只有 per-page 的 `m_Running`** | 欄位在 `UCL_AutoCommitPage.cs:126`，兩處設值（執行前後） |
| `Cmd_AutoCommit` | **沒有任何** | `op=commit` 直接進 `CommitGroup(...)` 迴圈 |

⇒ 兩個消費端**各自**判斷自己忙不忙，而它們之間沒有任何共享的狀態。
一個人在後台按「執行」、同時另一個 agent 送 `run AutoCommit --arg op=commit`，
兩邊會對**同一個 repo** 交錯跑 `git add` / `git commit`。

### 為什麼今天還不痛

- Cmd 目前是**單 lane 序列化**執行（見 `Plan_Cmd_Concurrency_Hardening.md` §1），兩筆 Cmd 不會真的同時跑
- 後台按鈕是人按的，而按的人就是會錯開時機的那個人
- ⇒ **保證住在操作層**：由人挑時間點，加上 Cmd 事實上不併行

### 什麼時候會痛（判準，不是猜測）

1. **`Plan_Cmd_Concurrency_Hardening` 落地** —— 各 persona lane 併行之後，兩筆 `op=commit` 真的會同時跑
2. **改成排程／無人值守觸發** —— 時機不再由人挑
3. **後台按鈕與 Cmd 同時發生** —— 今天就可能，只是還沒發生過

⚠ 三條裡任何一條成立，症狀都是**交錯的 git 操作**：
`git add` 進 index 的檔被另一筆 `commit` 帶走 ⇒ 檔進錯 commit、或一筆 commit 少了它該有的檔。
**而兩邊都會回報成功。**

## 2. 提案

### 2.1 一個 static 閘門，不是把整條流程搬進去

```csharp
public static class UCL_AutoCommitGate
{
    static bool s_InFlight;
    static string s_Owner = "";
    static string s_StartedAt = "";

    public static bool InFlight => s_InFlight;
    public static string Owner => s_Owner;

    /// 拿到閘門才准動 git。拿不到**當場喊**（不排隊、不靜默跳過）。
    public static bool TryEnter(string iOwner, out string oWhy) { ... }
    public static void Exit(string iOwner) { ... }
}
```

- `Cmd_AutoCommit`：`op=commit` 進入前 `TryEnter("Cmd_AutoCommit")`，拿不到 → **丟例外並說誰在跑**
- `UCL_AutoCommitPage`：執行按鈕同樣先 `TryEnter("AutoCommitPage")`；拿不到 → 按鈕停用並顯示持有者
- `op=scan` **不進閘門** —— 唯讀，擋它只會讓人看不到現況

> ⚠ **刻意不把 git 執行搬進共用類別。** `UCL_AutoCommitRules` 的區塊註解已經寫明理由：
> 「執行刻意留在各自的呼叫端 —— 頁面要 async ＋ 進度條，Cmd 要同步 ＋ 回傳值，
> 硬要共用會生出一個誰都不好用的中間層」。⇒ 本案只共用**互斥**，不共用執行。

### 2.2 `finally` 一定要放，而且要驗放得掉

閘門最常見的死法不是沒擋住，是**擋住之後放不掉**（例外中途離開 → 永久卡住）。
⇒ `Exit` 必須在 `finally`，並且驗收要包含「執行中丟例外之後閘門是開的」。

### 2.3 邊界（寫在提案裡，不要等別人問）

- **static 只保證同一個 Editor domain 內** —— 跨 Editor 行程、或 domain reload 中途，它不成立。
  Tim 說同時只開一個專案 ⇒ 與本案相容，但**那個保證仍住在操作層**。
  真要跨行程，得用檔案鎖（`UCL_ProcessRegistryService` 是同族的既有基建）。
- domain reload 會把 `s_InFlight` 清成 false —— 對「卡死」是好事，對「正確性」是壞事
  （reload 中途的那筆 git 操作沒有人善後）。⇒ 若要更嚴，鎖要落磁碟。

## 3. 驗收（照「刻意製造併發」寫，不是靠推理）

| # | 做什麼 | 期望讀數 |
|---|---|---|
| 1 | 後台按執行的同時送 `run AutoCommit --arg op=commit` | 後到的那筆**明確失敗**並印出持有者，不是安靜跳過 |
| 2 | `op=scan` 在 commit 進行中送出 | 正常回傳（唯讀不進閘門） |
| 3 | 讓執行中丟例外（例如把 git 路徑弄壞） | 例外之後 `InFlight == false`（`finally` 有效） |
| 4 | 連續兩筆 `op=commit`（不並發） | 兩筆都成功 —— 閘門不是把序列化的東西也擋掉 |

⚠ 驗收 1 需要**真的同時** —— 用「先按後送」的手感代替並發等於沒驗。
若當下 Cmd 仍是單 lane，可暫時用 `Cmd_Invoke` 直接呼叫閘門的 `TryEnter` 兩次來測語意。

## 4. 不做的選項與理由

| 選項 | 為什麼不選 |
|---|---|
| 在 `mode=submodules` 裡補一份「跳過在線 persona」判斷 | 那會變成同一條規則的**第二份**，兩份遲早漂掉而兩邊都不報錯 |
| 在發現層排除 `letters/<persona>` | 2026-08-21 實作過並實跑驗過（清單 18 → 9），依 Tim 拍板撤掉；實作留在 `UCL_AutoCommitConfig.cs` 的 git history |
| 排隊（後到的等前一筆做完） | 自動提交沒有「一定要跑到」的需求；**當場喊**比排隊誠實 —— 排隊會把「兩個人同時在按」這件事藏起來 |
| 等 `Plan_Cmd_Concurrency_Hardening` 一起做 | 那份是大工程且 Tim 說晚點做；本案獨立可驗、獨立有價值 |

## 5. 一句給未來的自己

現在不做這件事的理由是「不會並發」，而**那句話今天沒有任何讀數支撐它**——
它是真的，但它是靠人的習慣真的。本 Plan 的價值不在防止一個尚未發生的錯，
在於把那句話變成**出錯時會有人喊**的形狀。
