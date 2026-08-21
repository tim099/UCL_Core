---
title: Cmd 多人併發強化 — 路由收斂 + per-cmd 回傳槽 + 共享狀態盤點
slug: cmd-concurrency-hardening
status: draft（2026-08-16 提出，Tim 說「大工程、很多要細想，晚點做」。除 §6 Step 0 的止血外，**其餘一行 code 都還沒動**）
created_at: 2026-08-16T07:40:00Z
created_by: basecamp
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md | Cmd 架構 | **§8.1 已記載 per-persona queue 隔離**，本案不是發明它，是把它接上
  - ucl_core:Docs~/{lang}/Plan/Plan_RunCmd_Split_And_CSharp_Migration.md | run_cmd 拆分 | `--agent-id` / `--persona` 旗標的來源
  - ucl_core:Docs~/{lang}/Plan/Plan_AutoCommit_Single_Flight.md | 自動提交單一飛行 | **下游**：本案的 lane 併行一落地，自動提交的互斥閘就從選配變必要（summit 2026-08-21 補的反向連結）
---

# Cmd 多人併發強化

> **起因**：Tim 2026-08-16 問「多人用 Cmd 會互卡，能不能用 UniTask thread pool 讓各 persona 的 queue 併行運轉」。
> **而查完 code 的第一個結論是：前提要修一格 —— 併行早就做好了，只是沒被走上去。**

---

## 0. 一句話結論

**這不是「要不要做併行」的題目，是三個獨立問題被綁在一起：**

| # | 問題 | 性質 | 風險 |
|---|---|---|---|
| ① | 全員擠同一條 lane | **路由**（用錯旗標） | ⚠ **不是零** —— 見下方修正 |
| ② | per-cmd 回傳槽是全域單例 | **併行的硬前置** | 高 —— 會**靜默錯值** |
| ③ | 共享狀態（配號 / inbox / session 檔） | 併發安全 | 中～高，逐項不同 |

**thread pool 不在這張表上** —— 見 §3。

> ## ⚠ 2026-08-16 當日修正：①「零風險」是錯的
>
> 本文件初版把「路由收斂」標成**零風險、可以先做**。⛔ **那句話錯了，而且錯得危險。**
>
> **今天全員擠同一條 lane ⇒ 嚴格串行 ⇒ §4 那兩個全域回傳槽「因為不可能併行」而安全。**
> 一旦分流，兩筆 cmd 真的同時跑，`Clear()` 就會互相清掉 ——
> **路由正是把潛伏 bug 變成活 bug 的那一步。**
>
> ⇒ **正確順序是 §4 先於 §2**（per-cmd context 在任何模型下都正確，先做不會壞事；
> 路由則會啟動併發）。§6 的分期已照此改寫。
>
> 🩸 記在這裡而不是直接改掉：**我在同一份文件裡先寫下「零風險」再自己推翻它，
> 中間只隔了幾分鐘 —— 而那句話讀起來完全合理。** 順的東西最不需要驗，所以最容易錯。

---

## 1. 現況（2026-08-16 實測，不是引用）

### 1.1 併行機制**已經存在**

- `UCL_AgentCommandWatcher.OnEditorUpdate`：`foreach (agentId in ListAgentIds()) TryDispatchAgent(agentId)`
  —— **逐 agent 派遣**，不是單一佇列
- `UCL_AgentCommandRunner`：閘門是 `s_RunningAgents`（`HashSet<string>` + lock）—— **per-agent**，
  同 agent 防重入、不同 agent 互不阻塞
- `run_cmd.py`：有 `--persona` / `--agent-id`；`_split_queue_id()` 決定落哪個資料夾
- `AgentCommands/queues/` 現存 lane：`anonymous` / `apex-one` / `basecamp` / `gura` / `meadow`
- 架構文件 §8.1 **已記載**此設計，且 §8.5 寫著「default queue 卡住 ≠ 全系統卡，換 `--agent-id` 即可繞過」

### 1.2 而我們沒走上去（實測讀數）

| 呼叫方式 | 落點 | 時間 |
|---|---|---|
| `run_cmd.py run Tavern --arg persona=basecamp` | `queues/`**`anonymous`**`/` | 15:37:48 |
| `run_cmd.py `**`--persona basecamp`**` run Tavern …` | `queues/`**`basecamp`**`/` | 15:37:50 |

🩸 **2026-08-16 當天的實害**（全部是同一條 lane 排隊，不是 Editor 慢）：
- `git_commit.py` 自動公告失敗 →「previous batch is 'running'」→ commit 落地但**薪沒領**，需手動補
- `step=observe` 撞 `pending.trigger.running`（Editor 活著、結果檔一直在落，是撞車不是當機）
- `step=cycle` 兩次 CLI 逾時（`exit=3`），而**產物其實有落地** —— 回報說失敗、產物說成功

---

## 2. ① 路由：`--arg persona=` vs `--persona`

> **兩個長得一樣的東西，語意完全不同。**
> `--arg persona=<P>` → 傳給 **handler** 的參數（「這筆 cmd 代表誰」）
> `--persona <P>` → 給 **CLI** 選 lane 的旗標（「這筆 cmd 排哪條隊」）

⛔ **初版在這裡寫「本案唯一今天就能做、零併發風險的一項」—— 那句是錯的，已於 §0 推翻。**
路由是**開啟併發的那個開關**，必須排在 §4（per-cmd 回傳 context）之後。
（原句保留在版本史裡，不在正文留一句會被照做的錯話。）

### 待決（需要 Tim 拍板）

- **A 案**：工具端（`git_commit.py` / skills / 各 python 呼叫點）一律顯式帶 `--persona`
  - 優點：改動最小、行為明確
  - 缺點：**新的呼叫點會繼續忘**（今天的教訓就是「文件寫了但沒人走」）
- **B 案**：`run_cmd.py` 在 `--persona` 缺席時，**自動從 `--arg persona=` 推導 lane**
  - 優點：一勞永逸，不依賴呼叫者記得
  - ⚠ 缺點：`--arg persona=` 有時**不是**發起者（例：後台代跑、酒保代發），自動推導會把代跑者的 cmd
    排進被代理者的 lane。**要先盤點哪些 handler 的 persona 欄是「代表誰」而不是「誰在跑」**
- **C 案**：兩者都做（B 當預設、A 當顯式覆寫）

> 🩸 為什麼不能只寫進文件：架構文件 §8.1／§8.5 **早就寫了**這條路與繞法，而 2026-08-16
> 整天沒有人走上去。**寫下來只讓下一個人知道，不讓自己記得。**

---

## 3. ③ 為什麼 UniTask thread pool 不是解方（而且危險）

Cmd handler 大量觸碰 **main-thread-only** 的 Editor API：`AssetDatabase`（`UCL_EditorPath.CorePath`）、
`EditorPrefs` / `PlayerPrefs`（`UCL_AgentCommandsPath.DataRoot`）、`EditorApplication.timeSinceStartup`。

整條丟 threadpool 的後果不是「炸」而已 —— 更常見的是**在背景緒讀到空值 → fail-soft → 靜默走錯路**
（`ResolveAwakeningScriptPath` 的註解就記著這一課：「快取暖了之後背景緒僥倖能跑，冷啟動必炸」）。

### 現有的正確樣式（`Cmd_GoodMorning` step=brief）

```
主執行緒：ResolveAwakeningScriptPath() / 暖 DataRoot / ResolveBankBalanceArg()
   ↓
UniTask.RunOnThreadPool( spawn python + WaitForExit )   ← 只包「外部長跑」
   ↓
回主緒組回傳檔
```

⇒ **要平行化的是外部長跑（python / OCR / STT / git），不是 handler 本身。**
而那是**逐個 handler 的決定**，不是一個全域開關。

---

## 4. ② 硬前置：per-cmd 回傳槽是全域單例

`UCL_AgentCommandRunner`：

```csharp
static readonly List<string> s_CurrentCmdOutputs = new List<string>();          // 📄 回傳檔
static readonly List<(string Key, string Value)> s_CurrentCmdValues = ...;      // 🔢 post_seq / balance
...
lock (s_CurrentCmdOutputs) s_CurrentCmdOutputs.Clear();   // 每筆 cmd 起跑前清空
lock (s_CurrentCmdValues)  s_CurrentCmdValues.Clear();
```

原註解寫著「清單在下一筆 cmd 起跑前 Clear，**不跨筆污染**」——
⚠ **那條不變式只在「一次只有一筆 cmd 在跑」的前提下成立。**

### 併行之後的失效方式

- B 的 `Clear()` 清掉 A **已收集**的 outputs ⇒ A 的結果檔少了「📄 回傳檔」路徑
- A / B 的 `ReportOutputValue` 混進同一份清單 ⇒ **A 的結果檔印出 B 的 `post_seq`**

⇒ **`lock` 擋得住資料結構壞掉，擋不住語意錯置。而且不會報錯。**

### 修法方向（未拍板）

跟 **cmd id 綁定的 context**（例：`AsyncLocal<CmdContext>` 或 Runner 執行時把 context 傳進 handler），
`ReportOutputFile` / `ReportOutputValue` 寫進**當前 cmd 的 context** 而不是靜態清單。

⚠ 難點：現有 handler 呼叫的是**靜態方法**（`UCL_AgentCommandRunner.ReportOutputValue(...)`），
改成傳 context 會動到所有呼叫點。`AsyncLocal` 可以不動呼叫點，但要確認 UniTask 在 Editor
PlayerLoop 下的執行流會不會讓 `AsyncLocal` 漏接（**這格要實驗，不能推**）。

---

## 5. 共享狀態盤點（併行前要逐項判定）

| 對象 | 現況 | 風險 |
|---|---|---|
| 酒館 `_seq.txt` 配號 | **未查** | **最高** —— 配號撞 = 兩則同 seq，而 seq 是對帳鍵 |
| `rooms/<room>/inbox/*.md` append | **未查** | 高 —— 併發 append 互蓋 = 通知消失且不報錯 |
| `_cmd_results/<id>.json` | 檔名帶 id | 低，但要確認寫入 atomic |
| Treasury ledger / balance 快取 | 已有 `s_BalanceCacheLock` ＋ 增量快照 | ✅ |
| `StreamWatch/sessions/*.json` | **已知 lost update**（2026-08-11 實測 read-modify-write 互蓋） | 高 |
| `_screenstream/_config.json` | 多寫入端，有 3-way merge，無鎖 | 中 |
| `queues/<lane>/queue.json` | per-lane，Runner 有 per-agent 重入鎖 | ✅（前提是路由對） |

---

## 6. 建議分期（每期可獨立驗收）

```
Step 0  🩹 止血（**真正零風險，2026-08-16 已做**）
        └ `git_commit.py` 公告的 ack-timeout 60s → 240s。
          ⚠ 只拉長等待、**不做失敗重試** —— ensure_idle 逾時＝沒送出（安全），
          但送出之後的失敗可能其實已貼上（實測過「CLI 逾時而產物已落地」），
          而同一 SHA 貼兩次 = 付兩次錢。**分不清就不要自動重試。**

Step 1  📦 per-cmd 回傳 context（§4）—— **必須先於路由**
        └ 理由見 §0 的修正框：路由會啟動併發，而併發會讓全域回傳槽開始互相污染。
        └ 驗收：**刻意製造併發**（兩 lane 同時跑會 ReportOutputValue 的 cmd），
          兩份結果檔的 values 互不混入。⚠ 沒有這個驗收就不算完成

Step 2  🚦 路由收斂（§2）—— Step 1 過了才做
        └ 驗收：兩個 persona 同時送 cmd，各自的 pending.trigger 落在各自 lane，
          且「previous batch is running」不再出現在對方的等待訊息裡

Step 3  🔒 共享狀態逐項（§5）—— 從「動錢／動訊息」那兩條先做
        └ 酒館配號與 inbox append 先於其他項

Step 4  ⚡ 逐個 handler 決定要不要把長跑段搬 threadpool（§3）
        └ 不是全域開關；每個都要說明「哪一段在主緒解析、哪一段丟背景」
```

**核心原則**：**先修會靜默錯的（必要），再開併發（路由），最後才談加速（可選）。**
⇒ 順序的判準不是「哪個便宜」，是**「哪個會讓另一個變危險」**。

---

## 7. ⚠ 未驗清單（本文件自己的）

| # | 未驗項 | 影響 |
|---|---|---|
| 1 | 兩個 Runner 在 Editor PlayerLoop 下是**真並行**還是只是交錯 await | 決定 §4 的難度與 §3 的收益 |
| 2 | `AsyncLocal` 在 UniTask + Editor 執行流下會不會漏接 | §4 修法選型 |
| 3 | 酒館 `_seq.txt` 的配號實作（有沒有鎖 / 是不是 read-modify-write） | §5 最高風險項 |
| 4 | inbox append 的併發行為 | 同上 |
| 5 | 哪些 handler 的 `persona` 欄是「代表誰」而非「誰在跑」 | §2 B 案能不能做 |

**以上五項，一項都沒在本機跑過。**

---

## 8. 刻意不做（三個月後要分得出「漏了」與「決定不做」）

| 不做 | 理由 |
|---|---|
| **整條 handler 丟 threadpool** | Editor API main-thread-only；失效方式是靜默走錯路，不是拋例外 |
| **全域開關式的「開啟併行」** | 併行安全是逐項性質，一個布林值會讓沒檢查過的路徑一起上線 |
| **為了併行去掉 per-agent 串行** | 同 lane 串行是**刻意**的（防同一 `queue.json` 的 write race）。Tim 的原話也是「各自的 queue 仍要等上一次完成」 |
| **在 python 端做鎖** | 寫入端要收斂在 C#（同 Treasury 那條定調）；python 加鎖等於第二套不變式 |

---

## 相關

- `ucl_core:Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md` §8 —— per-persona queue 隔離的既有記載
- 酒館討論 seq **15497**（2026-08-16）—— 本文件的原始分析與砸磚邀請
