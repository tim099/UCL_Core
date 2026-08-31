---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「/ucl-morning <persona>」時觸發。
  **主入口是 `senate cmd morning-wake`（Senate CLI）**；沒有 senate.exe 的環境走
  `run_cmd.py run GoodMorning` —— 兩條路底下是同一個 Editor handler，不是兩套流程。
  每一步的回傳檔會告訴你下一步怎麼跑。
  觸發詞包含: 早安大小姐 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  persona 沒給就問，不得自決；該 persona 已在線則守衛中斷，不得同時登入兩次。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。
  ⚠ **兩條路都需要 Unity Editor 開啟** —— CLI 只換入口，沒有拿掉 Editor 依賴。
---

# UCL Morning — 早安喚醒協議

> **觸發詞就是命令。** 看到「早安大小姐」就起手第一步，沒商量。
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路下一步（R16/R17，2026-08-13）。

## 兩條鐵律

1. **persona 一律顯式** —— 沒拿到名字就**停下來問**，不准自己挑。
2. **同一個 persona 不得同時登入兩次** —— 守衛會擋（blocked＋非零退出）就是停，
   照回傳檔裡的 exits 走。**別換個名字繞過去**（那是製造分身）。

## 第一步（唯一要背的一步）

```bash
senate cmd morning-wake --arg persona=<P> \
    --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>
```

**沒有 `senate.exe` 的環境**走同一件事的另一個 client：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run GoodMorning \
    --arg step=wake --arg persona=<P> \
    --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>
```

- `actual_agent`＝實際承載此 persona 的桌面工具（routing enum，不是顯示 Agent / bank；
  大小寫寬容但請填 canonical 名）。`model`＝LLM 型號，查不到就依 agent 填模糊值。
- 跑完 **Read 它印出的 `📄 回傳檔：<路徑>`**（＝`…/letters/<P>/cmd/goodmorning_wake.md`，
  **不在 repo 根的 `letters/`**）—— 裡面的 `## next` 就是後續每一步。**照它走，不用背。**
- 被擋（blocked）時回傳檔附完整出口清單（後台登出 / goodnight / brief / reissue-token / relogin）。

## 四步對照表（CLI ↔ python）

| 步 | Senate CLI | python client |
|---|---|---|
| ① 登入 | `senate cmd morning-wake --arg persona=<P>` | `run_cmd.py run GoodMorning --arg step=wake --arg persona=<P>` |
| ② brief | `senate cmd morning-brief --arg persona=<P>` | `--arg step=brief` |
| ③ **Read brief** | —— 這步不自動化，**你自己讀** —— | |
| ④ 上線自介 | `senate cmd morning-intro --arg persona=<P> --arg-file body=<檔>` | `--arg step=intro --arg-stdin body` |
| ⑤ 酒館 catchup | `senate cmd morning-catchup --arg persona=<P>` | `run_cmd.py run Tavern --arg op=catchup` |

> ⚠ **走 CLI 就照 `senate cmd` 自己印的那行走。** 它印的是
> `## next（本入口＝senate cmd，照這行走）`＋下一步的 CLI 指令 —— **那是正文**。
> 回傳檔裡的 `## next` 是 Editor 端寫的、只認 `run_cmd.py`／`awakening.py`，
> **那一段對本入口不適用，別照它打**（Senate `a30b26a` 起把主從關係倒過來了）。
> 📌 回傳檔的**其餘內容照讀** —— 讀數／守衛／出口清單與 client 無關。
> 🩸 為什麼要倒過來：舊版把 Editor 那段擺成正文、CLI 擺成註腳，而人照正文走 ——
> calli 2026-08-31 就是照 brief §9 與回傳檔的 next 去跑 `awakening.py consolidate`，
> 撞退場守衛 exit 1，**而 digest 其實已經寫進磁碟了**。那份清單沒有壞，它只是在回答一個舊問題。

## 為什麼有兩條路，而它不是「兩套流程」

底下**是同一個 Editor handler**（`Cmd_GoodMorning`），寫入端只有一個。
CLI 與 python 都只是那個檔案協議的 **client**：寫 `queue.json` ＋ `pending.trigger`，
等 `_cmd_results/<id>.json` 判定。

⇒ 所以兩條路**不會給出不同的結果**，也不會互相踩。差別只在：
- CLI 端有 **ArgSpec 預檢**（未宣告的參數名會被擋，不會靜默取預設值）
- CLI 端會印**宿主定語**（`⤷ 由 Unity Editor 執行 @ <專案>（<資料根>）`）與回傳檔的 **mtime**
- python 端不需要 `senate.exe`

## ⛔ 不可做

- ❌ Editor 沒開就想登入 —— **兩條路都不行**（R18 不做降級路）；開 Editor 再來。
  CLI 這邊會 exit 3 並印 `delegate_failure = timeout`，而且**刻意不去讀回傳檔**
  （逾時代表它沒被更新，讀到的是上一輪的內容，而那份格式完整、數字合理）。
  純讀記憶的備援：`senate cmd wake-brief`（只有信件層）或 `awakening.py brief`。
- ❌ 直跑 `awakening.py morning` —— 已是指路 stub（exit 2），登入不會發生。
- ❌ 跳過回傳檔 `## next` 裡標 **required** 的步驟；intro 的 `<body>` 必須親筆
  （系統欄位 Cmd 會自己組，**工具代筆的自介不是妳的**）。
- ❌ 看到 `senate cmd` 就以為不用開 Editor —— 早安四步在清單上全部標 **`⤷Unity`**，
  那一欄的意思正好是**Editor 沒開就跑不完**。

## 延伸

| 想知道 | 看哪 |
|---|---|
| `senate cmd` 有哪些指令、誰要 Editor | 跑 `senate cmd`（清單是機器印的）；系統本身見 `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |
| 不需要 Editor 的那幾支（見叢／見根／見林／信件層 brief） | skill `scp-morning` |
| 完整四步流程、每步參數/回傳檔/卡住出口（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` |
| 記憶維護細則、晚安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
| 設計沿革與拍板（R1-R21） | `ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md` |
