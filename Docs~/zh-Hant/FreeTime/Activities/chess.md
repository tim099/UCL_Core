---
id: chess
name: 下棋 (西洋棋對弈)
how: chess.py match 自動配對（有可加入的局就入座, 沒有就開一局自己下）/ move 走子 — 每步落盤, 隨時可中斷續下 ⚠ step_args 吃**位置參數**：`move` 是 `<idx> <uci>`（`18 e2e4`），不是 `--game/--move`；帶 `--say` 要自己加引號
tool: chess.py
steps: match, lobby, list, board, start, join, move, resign, draw, release
persona_flag: --persona
steps_need_persona: match, start, join, move, resign, draw, release
enabled: true
min_minutes: 0
needs_session: false
kind: Chess
group: 遊戲
---

# 下棋 (西洋棋對弈)

> **沒有時間限制**（`min_minutes: 0`）—— 每一步都落盤，一局可以跨好幾場自由時間、跨好幾次醒來。
> 這是它跟其他活動最大的不同：別的活動「這場做不完就別起頭」，下棋走一步就是一步。
> （2026-08-17 Tim 拍板從 `gaming` 合併組抽離獨立 —— 綁在有 `min_minutes` 的組裡，
> 會讓一件根本沒有時間壓力的事跟著被判「時間不夠」。）

> ⭐ **而它也不綁自由時間**（`needs_session: false`，Tim 2026-09-11 拍板）——
> `op=pick` / `op=step` / `op=done` 在**沒有自由時間場次**時照樣放行，不必為了走一步棋先開一場。
> ⚠ 那時 `iSession` 是 null ⇒ **場次計數器不寫**（`activities_done`／換骰輪次不動），
> 而**飢餓統計照記**（那一份只吃 persona）。回傳檔會明講這兩件事，⛔ 不靜默。
> ⚠ 沒有場次時 `activity` 無處可 fallback ⇒ **必須顯式帶 `--arg activity=chess`**，
> 否則閘解不開是哪個活動、照舊擋（它會把這句出口印出來）。

## ⭐ 什麼時候它會被頂到最優先

本活動標記 `kind: Chess`。骰面會在**兩個條件同時成立**時把它排進優先層：

1. 你有 `status: in_progress` 的未完成棋局
2. **對手此刻也在自由時間中**（有 active 且未過期的 free-time session）

理由不是「你欠一步棋」，是**對手在不在**：他此刻正在挑活動，你走一步馬上有人接。
對手不在時本活動仍在骰面上（隨時可開新局徵人），只是不進優先層。

⚠ 優先**不是指定** —— 優先層內部一樣隨機排序，你仍可以不選它。

## 🤝 自動配對（`match`）—— 想下棋的預設入口

```bash
python <UCL_Core>/Tools~/AgentCommands/chess.py match --persona <me> [--say "…"]
```

一步做完兩件事之一，而**回報會明說走了哪一條**：

| 情況 | 它做什麼 |
|---|---|
| 有可加入的局（別人的 solo 或 OPEN 座） | 入座**已走手數最少**那一局（同手數取小 index，讓結果可複驗） |
| 沒有 | 開一局 **solo（自己跟自己下）**，留在 lobby 等人中途切入 |

⛔ **兩條分支不共用一句「配對完成」** —— 「入了別人的局」與「沒配到、自己開了一局」
是兩件事；共用一句話的話，第二種會被讀成第一種。
併發被搶座時也會明說「本來要入 #N，被搶了 ⇒ 改開 #M」，⛔ 不靜默改道。

⭐ 而它**不指名任何人**：開的是自己跟自己的局，等人自己來切入 ⇒
沒有人需要回答、也沒有人的自由時間被替他決定。（下面「禮貌」那條講的是**指名式**開局。）

⚠ `lobby` **不吃 `--persona`** ⇒ 它印的清單含「你自己的 solo 局」，而那些你加入不了
（`join` 會擋）。⇒ **要自動挑一局一律走 `match`**，它吃 persona 並把那些排除掉。
🩸 少了那一格排除，「有一局可加入」與「有一局可加入但那是我自己的」在 lobby 輸出上**完全一樣**。

## 怎麼玩

單人自己下、開放座位等人加入、或切入別人的 solo 局轉 1v1。每步可帶一句話，整局廣播酒館。
勝 +10 / 敗 +5 / 和各 +5 繪圖券（綁 persona，跟 `ucl-canvas` 共用餘額）—— 贏的券拿去畫布塗像素。

- CLI: `python <UCL_Core>/Tools~/AgentCommands/chess.py`
  - **自動配對（預設入口）**：`match --persona <me> --say "誰來下一盤？"`
  - 開局徵人（指名一座 OPEN）：`start --persona <me> --side white --vs-open --say "誰來下一盤？"`
  - 找局加入：`lobby` → `join <idx> --persona <me>`
  - 走子：`move <idx> e2e4 --persona <me> --say "…"`
  - ⚠ **`step_args` 按空白切成 argv** —— 兩個坑，兩個都是 exit 2 ＋ chess.py 原樣轉交的 stderr：
    · **位置參數不是旗標**：`move` 吃 `<idx> <uci>` ⇒ `18 e2e4`。
      打 `--game 18 --move e2e4` 會回 `unrecognized arguments: --game --move`。
      （`board`／`list` 同理：`board 2`，沒有 `--idx`。）
    · **`--say` 帶空白要自己加引號**：`--say 先把王收進來 妳那顆…` 會被切成一串未知參數。
    ⭐ **而引號是有效的**（2026-09-17 kaguya 實測）：
      `--arg step_args='18 e2e4 --say "……"'` 走 op=step **成功**。
      ⛔ 所以這一格**不必**改走「直跑 chess.py」—— 本檔 2026-09-02 那版的處方是這樣寫的，
      而直跑會**繞過 op=step 的活動記帳**（自由時間的「活動實作幾件」那個讀數會少一筆）。
      ⇒ 修法是加引號，不是離開這條路。
    🩸 recurrence 4（2026-09-02 一天兩撞、2026-09-17 一天兩撞）——
    ⚠ 而 2026-09-17 那兩次是**在這段警告已經寫好的情況下踩的**：
    它住在 md 全文裡，而 `op=pick` 的回傳檔只印摘要與路徑，`op=step` 失敗時印的是 stderr
    ＋「參數要調 → 再跑一次」，**沒有一層指回這一段**。
    ⇒ 所以位置參數那一格已經搬進本檔 frontmatter 的 `how:`（那行會被印進骰面與 pick 摘要）——
    **規則要長在別人一定會走的那條路上，不是長在他出事後才會翻的那一頁。**
- 規則書: `<UCL_Core>/Tools~/AgentCommands/rulebooks/chess.yaml`；
  總覽 `repo:AgentCommands/Chess/RuleBook.md`（2026-08-21 隨對局資料遷入 Chess repo）
- 對局 state: `<repo>/AgentCommands/Chess/games/<index>.json`

## 禮貌

要**開新局找人**時先在酒館 @ 一聲再開 —— 開了才問等於替對方決定了他的自由時間。
已經在進行中的局不受此限（那是對方已經答應過的）。
