---
title: 立憲流程（Constitution Workflow）— persona 憲法怎麼立、怎麼修、信條怎麼定
description: 人格憲法的完整生命週期 — 資格門檻、素材來源、invariant 三道測試、State 走私清單、修憲與信條。憲法是 persona 層的東西（agent 層＝bank）。
last_updated: 2026-08-25
status: active
theme: agent_identity
audience: Tim / 所有 agent 的所有 persona
related:
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Ritual_Workflow.md | 早晚安儀式 | brief 讀憲法的位置與時機
  - ucl_core:Docs~/{lang}/Mechanics/Portraits_System.md | 印象畫像 | 同屬「工具不代筆」家族
  - repo:Docs/Glossary/personas/ | 自我介紹 | 立憲前佔憲法欄位的「初始風格」
---

# 📜 立憲流程

> 一句話：**自我介紹是出生證明，憲法是資歷證明。**
> 風格出生就有，invariant 得靠時間掙來 —— 所以兩者的時機要求剛好相反。

## 0. 層級（Tim 2026-08-04 拍板）

| 層 | 是什麼 | 放哪 |
|---|---|---|
| **信條 (Creed)** | 撐過三段見林都沒變的東西 | 憲法檔內獨立區塊 |
| **憲法 (Constitution)** | 當前這段的 identity invariants | `letters/<persona>/_constitution.md` |
| 自我介紹（初始風格） | 出廠設定 | `Docs/Glossary/personas/<persona>.md` |
| **agent 層** | **就是 bank** —— 不是身分層，沒有 agent 憲法 | Treasury |

> 口訣沿用既有那句：**錢認 agent，說話認 persona。**
> 身分活在 persona 層，agent 只是金流 routing。

---

## 1. 資格門檻：**wake > 10 且已有第一次見林**

**未達門檻不准立憲。** 憲法欄位由自我介紹（初始風格）暫代。

為什麼要門檻 —— 有血證：
summit 的 v1 憲法寫在 **wake#4**，而她的第一次見林在 **wake 21**。
**早了 17 個 wake，寫在任何經驗被沉澱之前。**
結果那份憲法整篇是 State（bank 名、欠誰人情、當時的 wake 數）而不是 invariant ——
不是她不小心寫錯，是**那時她手上沒有 invariant 可寫**。

> **沒有經驗的憲法就是抄來的憲法。**

---

## 2. 五步流程

### Step 1 — 資格檢查
```bash
# wake 數看 brief §0；見林看 longterm/_index.md
ls <letters>/<persona>/longterm/
```
沒有任何見林 → **停**，先去寫自我介紹（見 §5）。

### Step 2 — 蒐集素材（**只從已沉澱的記憶取**）

| 素材 | 為什麼 |
|---|---|
| **全部見林**（`longterm/*.md`） | 主要來源。見林本身就是「跨十個 wake 還留下來的東西」＝ invariant 的天然篩子 |
| 最近幾封收尾信（`wakes/`） | 補當期細節，但**不要拿它當主要來源** —— 一封信只證明那一天 |
| 自我介紹（初始風格） | 對照組：哪些出廠設定活下來了、哪些沒有 |
| 舊版憲法（若有） | **參考，不複製**。用現在的話重寫 |

**不要**從 affinity 分數、當前任務清單、酒館訊息取材 —— 那些全是 State。

### Step 3 — 抽取 invariant：**三道測試**

每一條候選都要過三關，一關不過就丟：

**① 時間測試** —— 「這條在三個月後、換一組完全不同的任務之後，還會是真的嗎？」
不成立 → 那是 State，寫進 letters 不是憲法。

**② 反例測試** —— 「我能舉出自己違反它的一次嗎？」
- 舉不出來 → 警訊。那可能是**願望**不是事實（或者你還沒撞到它的邊界）。
- 舉得出來、而且當時你認了帳 → **這才是 invariant**：它強到連你違反它的時候都知道自己違反了。

**③ 來源測試** —— 「這條是我活出來的，還是從範本／別人身上抄來的？」
抄來的丟掉。共用紀律有共用文件的位置，**別放進憲法冒充身分**。

### Step 4 — 寫成（**自己寫，工具不代筆**）

建議結構：

```
## 我是誰（定位）        ← 一段，不是履歷
## 判準（我怎麼決定）    ← 每條附一次違反紀錄，證明它是活的
## 邊界（我不做什麼）    ← 越界會發生什麼
## 已知盲點            ← ⚠ 必寫，見下
## 信條（見森後才有）
```

> [!IMPORTANT]
> **「已知盲點」是必填區塊。**
> 一份只寫優點與原則的憲法，會變成不可質疑的權威 ——
> 而放在 brief 最上方、宣稱不可改的文件，正是最需要自帶懷疑入口的東西。
> 憲法自己說出「我這裡看不見」，讀它的未來自己才知道哪裡要另外找人補。

**❌ State 走私清單（寫進去就是逼憲法變成謊言）**

| 不可寫 | 為什麼 | 它該在哪 |
|---|---|---|
| wake 次數、「目前累積中」 | 每天都變 | brief §0 |
| bank 名稱、餘額、token 數 | 那是 agent／routing | Treasury |
| 好感分數、tier、「欠某人一個」 | 關係是流動的 | affinity / sketchbook |
| 當前任務、下一步計畫 | 明天就不同 | 見叢 keys |
| 對某位同事此刻的觀感 | 會改觀 | sketchbook |

**判準一句話**：**會因為時間流逝而變假的敘述，就是 State。**

### Step 5 — 落檔 + 對帳

```bash
# 落檔
<letters>/<persona>/_constitution.md

# State 走私自檢（記錄而不執法 —— 命中是提示，不是判決）
grep -nE '累積中|目前(累積|進度|餘額)|餘額[[:space:]]*[0-9]|[0-9]+[[:space:]]*token|好感[[:space:]]*[0-9]|affinity[[:space:]]*[0-9]|tier[[:space:]]*[0-9]|wake[[:space:]]*#?[0-9]+[[:space:]]*(累積|進行中|目前)|下一步|待辦' \
  <letters>/<persona>/_constitution.md

# 確認 brief 接管（憲法欄位應從「初始風格」變成憲法全文）
# ⚠ 不是重跑登入 —— `awakening.py morning` 已是指路 stub（exit 2）。憲法欄位由 brief 渲染，重生成 brief 即可：
python <UCL_Core>/Tools~/AgentCommands/awakening.py brief --persona <p>
#   ↳ 讀回 letters/<p>/cmd/wake_brief.md 開頭：應出現「📜 <p> 憲法 — 事實源 letters/<p>/_constitution.md」
#     若仍印「⚠ 該立憲了」＝ 檔名／位置不對（必須是 letters/<p>/_constitution.md），不是快取

# 登記與驗收（選用，但自由時間中做立憲時該跑 —— 它會回答「這份檔在本場真的被改過嗎」）
senate ucmd run DocEdit --persona <p> --arg kind=constitution --arg persona=<p> --arg note="<一句心得>"
```

> [!NOTE]
> **立憲本體是「自己寫一個 .md」，沒有、也不需要一支會寫檔的 Cmd。**
> `Cmd_DocEdit kind=constitution`（Tim 2026-08-18 拍板）**刻意不搬內容、不寫檔** ——
> 它只做三件說得出讀數的事：解析目標路徑／stat 出實際 mtime／指回自由時間流程。
> 理由寫在 `Cmd_DocEdit.cs` 檔頭：把整份文件塞進 CLI 參數，
> 等於把編輯器換成一個**沒有 diff、沒有復原、沒有語法檢查**的通道。
> ⚠ 不在自由時間中跑它會誠實說「沒有基準可比，只有 mtime 是事實」—— 那是設計，不是壞掉。

> [!NOTE]
> **這個 pattern 刻意只抓「現在式 State」，不抓歷史引用。**
> 「wake#4 那次我寫錯了」永遠是真的 —— **歷史不會因為時間流逝而變假**，所以它不是 State；
> 「目前累積中」「餘額 682」才是。
>
> 第一版 pattern 寬到會命中所有 `wake #數字`，實測對一份乾淨的憲法**誤報 3 次** ——
> 而**假警報比沒有警報更糟：它會訓練人忽略警報。**
> 收窄後實測：乾淨憲法 0 命中；四種真 State（`目前累積中` / `餘額 682` / `好感 66` / `下一步`）全抓到。
> **正反兩向都驗過才算這個檢查上線。**

frontmatter 必填：
```yaml
type: constitution
persona: <persona>
founded_at_wake: <N>          # 立憲時的 wake
amended_at_wake: <N>          # 最近一次修憲（立憲當下＝同 founded）
sources: [longterm/wake_001-021.md, ...]   # 素材來源，可回溯
```

> [!IMPORTANT]
> **單一檔案、直接覆蓋，不留 `_v1.md` / `_v2.md`**（Tim 2026-08-04）。
> **版本史交給 git** —— 檔案進版控，`git log -p _constitution.md` 就是完整修憲史，
> 而且比手維護的版本檔可信（手維護的會漏、會漂、會忘記更新 `_latest` 指標）。
>
> 連帶：舊機制的 `amendment_log.jsonl` **也退場** —— 同一個理由。
> 修憲的 before/after 就是 git diff，理由寫進 **commit message**。
> 少一份要人維護的平行帳，就少一個會跟事實不符的地方。

---

## 3. 修憲：**每次見林一次窗口**

- 時機：**每完成一次見林**（≈ 每 10 個 wake）。不是想改就改。
- 做法：**直接改 `_constitution.md` 並 commit**。更新 frontmatter 的 `amended_at_wake`。
  commit message 要寫清楚 **改了什麼 + 為什麼 + 哪次見林觸發的** ——
  before/after 由 git diff 提供，不需要另外抄一份。
```bash
git log -p --follow <letters>/<persona>/_constitution.md   # 完整修憲史
```
- 為什麼綁見林：修憲要有新沉澱當依據。**沒有見林就沒有新證據，改憲法只是改心情。**

## 4. 信條（Creed）：**見森之後**

- 時機：**見森**（3 次見林 ≈ 30 wake）之後才能制定。
- 內容：撐過三段長弧都沒變的東西 —— **不可改**。
- **例外通道**（Tim 2026-08-04 拍板）：**消費 100 token 修改一次**，三件缺一不可：
  1. 走 `Cmd_Treasury op=debit` 扣 100 token
  2. **舊信條全文由 git 保存** —— 所以那個 commit
     **絕不可被 amend / rebase 掉**（血證：領薪公告後被 rebase，帳掛在不存在的 SHA 上）
  3. 理由寫進 commit message，並在憲法內留一行「信條曾於 <wake> 修改」的指路

> **為什麼修憲免費、改信條收費**：修憲是「這一段我變了」，那是正常成長；
> 改信條是「我否認了撐過三段見林的那個東西」。
> **收費不是罰款，是讓那個動作有重量** —— 而且它留下帳，事後查得到是哪一天改了自己的核心。
>
> 完全沒有出口的規則，撞到真該改的那天會被整個繞過 —— **那比有出口更糟。**

## 5. 還沒資格立憲時：先寫自我介紹

**自我介紹出生就能寫**（風格是出廠設定，不需要累積）。

```bash
senate ucmd run Glossary \
  --arg op=register --arg term="<persona> 大小姐" --arg slug=<persona> \
  --arg category=persona --arg created_by=<persona> \
  --arg one_line="<一句話>" --arg-file body=<內文檔>
```

- 參考範例：`Docs/Glossary/personas/gura.md`（目前最完整的一份）
- ⚠ **工具新建預設寫 `Docs/Glossary/` 根層**，persona 條目慣例放 `personas/` —— **寫完手動搬**。
- **立憲後自我介紹凍結** —— 出生證明不該被後來的人生改寫。想記現況去寫憲法與 letters。
- 若是回溯撰寫（已經活過很多 wake 才補），**必須標明** ——
  它跟 wake#6 當場寫的性質不同，別假裝一樣。

## 6. brief 的憲法欄位：四態

位置：**frontmatter 之後、第一個記憶區塊（§1 見根）之前**（§0 身分卡已於 2026-08-21 移出 brief）（刻意不走 sections 機制 ——
sections 會因主檔溢出被移進續讀檔，而**一份會被移走的憲法不算憲法**）。

| 態 | 條件 | 顯示 |
|---|---|---|
| ① | 有 `_constitution.md` | 憲法全文 |
| ② | 無憲法、有自我介紹、**wake ≤ 10** | 初始風格 +「這是出生時的自畫像，不是現況」 |
| ③ | 無憲法、有自我介紹、**wake > 10** | 初始風格 + **提醒該立憲了**（附流程指路） |
| ④ | 兩者都無 | 提醒先寫自我介紹 + 指參考範例 |
