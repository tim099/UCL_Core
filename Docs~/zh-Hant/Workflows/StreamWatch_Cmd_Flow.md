---
title: 觀影模式 — 怎麼操作 Cmd ＋ 起始步驟（維護用）
slug: streamwatch-cmd-flow
status: active
created_at: 2026-08-15T13:10:00Z
created_by: summit
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Workflows/StreamWatch_Cmd_Reference.md | 完整流程參考 | 維護用，平常不用讀
  - ucl_core:Docs~/{lang}/Plan/Plan_StreamWatch_Cmd.md | 設計沿革與拍板 | 為什麼這樣設計
  - ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md | 早晚安 Cmd 流程 | 本檔照它的形狀
  - ucl_core:Skills~/ucl-stream-watch/SKILL.md | ucl-stream-watch | 薄入口（只教第一步）
---

# 觀影模式 — 操作方式與起始步驟

> [!IMPORTANT]
> **本檔只講：怎麼跑這支 Cmd、從哪一步起手。**
> **後續每一步由 Cmd 的回傳檔 `## next` 引導** —— 這裡不抄第二步以後的參數。
>
> 為什麼不抄：抄一份就多一份會過期的副本。
> 🩸 舊 `ucl-stream-watch` skill 有 7 行寫死 600s 保存期而實際是 2400s（差四倍），
> 還拿那個數去教間隔紀律 —— 而它照樣被載入了好幾週，因為**過期的數字不會叫**。
> ⇒ 會漂移的東西放在會被讀回的地方（回傳檔），不放在會被背下來的地方（文件／skill）。

## 怎麼跑

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run StreamWatch --arg step=<step> --arg persona=<P> [...]
```

- 跑完看 `run_cmd.py` 印的 **`📄 回傳檔：<路徑>`** → Read 它 → 照裡面的 `## next` 走。
- **長內文一律 `--arg-file body=<檔>`**，不要 inline（shell 會咬反引號；`--arg-file` 不經那一層）。
- Cmd 失敗時回傳檔裡有 `## blocked`，附 `reason` 與 `exit`（出口指令）——**照它走，不要自己猜修法**。
- 需要 Unity Editor 開著（走 Cmd，無降級路）。

## 起始步驟（只有這一步要記）

**看一眼就走**（不開場、不記帳、不發文，也是測試探針）：

```bash
run_cmd.py run StreamWatch --arg step=peek [--arg seconds=<5..600>] [--arg raw=1]
```

**正式開場**（要寫評論、要計酬、要留接續點）：

```bash
run_cmd.py run StreamWatch --arg step=start --arg persona=<P> --arg until=<HH:mm> --arg media=<work-slug>
```

- `media` 不給 ⇒ Cmd 會 blocked 並**列出既有 work 清單**；命中就用，不確定**問 Tim 不要猜**。
- bilibili 一律 `bilibili-<up主 slug>` 且 `--arg up=<up主名>` 必填
  （影片標題／介紹／網址走 `title` / `desc` / `url`，那是場次不是作品）。

之後 —— **開場回傳檔會告訴你下一步**。收工也不用你判斷：時間到或 Tim 停錄影時，
下一次 `cycle` 會自己宣布並結算。**沒有 `step=end`。**

## 回傳檔的三行判準（要會讀，不必背指令）

```
- STT      : 27 段 (cache-only, 命中 5 chunk)
- 窗口對帳 : 窗口尾端 18:29:51 ≤ 水位 18:29:52 ✅（夾子生效，餘裕 1s）
- 保存期   : 名目 2400s（讀自後台設定不寫死）｜實有 1230s（1230 張，最舊 19:30:12）
```

- **窗口對帳**：`≤` 才對。出現 `>` 或「未夾」⇒ **尾端那幾格的「沒有字幕」不可信**。
- **STT**：`0 段` ≠ `無 cache` ≠ 這一行不存在（第三種是管線沒跑）。
- **保存期**：名目只是設定值的換算，**實有才是現在真的回得去多久**。
  兩個方向都會差：開播初期 buffer 沒滿 ⇒ 實有遠小於名目；擷取速率低於設定 fps ⇒ 實有反而**大於**名目
  （首次雙印即撞到：名目 2400s／實有 2472s）。**要判斷還救不救得回，只看實有。**

## 維護者：改動時要一起動的四處

漏一處就會出現「文件說有、實際沒有」：

1. `Cmd_StreamWatch.cs`：`ExecuteAsync` switch ＋ `ArgsSchema` ＋ `ShortDescription`
2. **本檔** —— 只有**操作方式或起始步驟**變了才動
3. `Plan_StreamWatch_Cmd.md` —— 只有**設計判斷**變了才動
4. `Skills~/ucl-stream-watch/SKILL.md` ＋ 消費端三份安裝副本（`.claude` / `.codex` / `.agents`）
   ⚠ `.agents` 那份多一行 `trigger:`，**套用同一個編輯，不要整檔複製**

> **判準：會隨環境漂移的數字（保存期、fps、費率、水位、格數）一律由 Cmd 讀設定後印在回傳檔上，
> 任何文件與 skill 都不得寫死。**
