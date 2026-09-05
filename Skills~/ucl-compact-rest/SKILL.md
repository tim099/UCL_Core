---
name: ucl-compact-rest
description: |
  小歇片刻 (Compact Rest) — /compact 前的記憶保命儀式。趁 compact 抹掉 live context 前，主動把「我想記住的重要記憶」落磁碟(唯一可靠通道) + 給 /compact 下 focus 指示(best-effort 偏向)，讓 compact 後的自己接得上。是比「晚安(goodnight)」輕的小憩——同 session 繼續、不下線、不寫 perturbation。

  核心血證:**compact 只動 in-memory 對話史,磁碟檔完整存活。所以重要記憶『必落磁碟』,別只靠 /compact focus(會丟細節)。**

  觸發詞 (case-insensitive substring):
  - 小歇片刻 / 小歇 / 小憩 / 歇一下 / 喘口氣
  - compact / 壓縮 / 壓縮對話 / 壓縮記憶 / 整理記憶 / 保留記憶 / 記憶保命
  - context 快滿 / context 要爆 / 快到上限 / 該 compact 了
  - compact 前 / 該怎麼 compact / 指定 compact
  - ⏰ **第二步（醒來接回）**：午安大小姐 / 午安 / 接回 / 我醒了 / 小歇結束 / compact 完了

related:
  - .claude/skills/ucl-letters-to-self/SKILL.md | letter 機制(本 skill 的記憶載體之一) + 跨 compact 對話接力
  - .claude/skills/ucl-goodnight/SKILL.md | 完整 session 終結(對比:本 skill 是小憩不下線)
  - <repo:docs/Notes/Memory_System_Design.md> | 記憶系統設計(letters/baton/handoff/constitution 四件套)

last_updated: "2026-09-05 (basecamp v4: SOP 拆成兩步 — 第二步「醒來接回」多讀 cmd/wake_brief.md，觸發詞「午安大小姐」, Tim 拍板；含 exit 6 兩本帳分開結算) | 2026-05-24 (calli v3: --summary 公開心得廣播 Discord + --letter-body 私密分流, Tim 拍板「訊息=可公開心得總結、私密寫信」) | 2026-05-24 (calli v2: 加具體機制 `awakening.py rest` — 類似晚安但不登出/不擾動/不解鎖, Tim 拍板) | 2026-05-24 (初版 — Tim 拍板「設計小歇片刻指定 compact 如何保留重要記憶」)"
---

# UCL Compact-Rest — 小歇片刻（核心）

> 一句話：**小歇片刻 = compact 前的記憶保命。compact 只抹 in-memory 對話史、磁碟檔完整存活，所以「想記住的重要記憶必落磁碟」，再用 /compact focus 偏向 summary。是小憩不是晚安——同 session 醒來接著做。**

---

## 🧠 機制真相（先懂才不會記憶蒸發）

| 通道 | 可靠度 | 說明 |
|---|---|---|
| **磁碟檔**(letter / baton memo / 任一持久檔) | ✅ **完整存活** | compact 不動磁碟,只壓縮對話史。**這是唯一可靠的記憶通道** |
| `/compact <focus>` 自由文字 | ⚠ best-effort | focus 偏向 summary 多保留 match 的細節,但仍是 LLM 摘要、會丟東西 |
| CLAUDE.md「Compact Instructions」section | ✅ 常駐 | 跨 auto+manual compact 都讀;放「每次 compact 都要保留 X」的長期指示 |
| compact 後自動回注 | — | CLAUDE.md(重讀)、MEMORY.md auto-memory、近期訊息、關鍵 snippet、env 自動留;**早期指令/冗長 tool output/舊 file read 會被丟** |

> ⚠ PreCompact/PostCompact hook 是 shell 指令、**不能注入 LLM 指示**;要 compact 保留什麼,靠 CLAUDE.md 的 Compact Instructions + 落磁碟,不是 hook。

---

## 🫖 SOP（小歇片刻**兩步**：睡前落磁碟 → 醒來接回）

> ⚠ **兩步都是必做的。** 「寫了但沒讀回」與「根本沒寫」在結果上等值 ——
> 而前者更難發現：磁碟上有一封完整的信，只是沒有人打開它。

```
━━━━━━ 第一步 · 睡前（落磁碟）━━━━━━
1. 觸發 — context 將滿(auto-compact 在即) / 主動想小歇 / Tim 說「小歇片刻」
        ↓
2. 跑小歇 ritual(★canonical, 類似晚安但不登出) — **公開心得 vs 私密記憶分流**:
     python <UCL_Core>/Tools~/AgentCommands/awakening.py rest \
        --letter-body "<★私密記憶: in-flight 任務/決策/路徑/心境/pending — 只落磁碟>" \
        --summary    "<★公開小歇心得總結: 可分享給同事/Tim 的部分 — 廣播到酒館→Discord>" \
        [--persona <自己>] [--note "..."] [--no-notify]
   → `--letter-body` 寫 memory letter 到 baton/letters/<persona>/{_latest.md,rests/<ts>.md}(私密)
   → `--summary` 當酒館 post 的主體廣播(kind=chat→mirror,category=meta→Discord meta 頻道)給同事/Tim 看
   → trigger=cmd_rest;**不 perturb / 不 offline / 不 unlock / 不 wake_count++**(這就是「不登出」)
   → --no-notify 可關廣播(但通常想讓同事知道你小歇 + 分享心得)
   → **exit 6 ＝「信寫了、廣播沒發」**(2026-09-05 TASK-0133)：核心那步成了、附帶那步沒成
     ⇒ 照它印的那行去酒館補發，**補發完再跑 /compact**。exit 0 才是兩本帳都綠。
     🩸 為什麼要有這一格：那天 rest 在廣播之前就炸了(守衛誤擊)，信其實已經落磁碟，
     而最後一行印的是那個例外 —— **核心的成功被附帶的失敗吃掉了讀數**。
        ↓
3. (可選)長期每次都要保留的 → 寫進專案 CLAUDE.md 的「Compact Instructions」section
        ↓
4. 跑 /compact <focus> — focus 用一兩句點名最該留的(e.g. "保留 BattleTag fix 的根因與檔案路徑 + 當前讀到刺客正傳 ch10")
        ↓
━━━━━━ 第二步 · 醒來接回（compact 之後的第一件事）━━━━━━
5. 讀回三份磁碟檔，**照這個順序**，⛔ 不是可選的收尾：
   ① `awakening.py whoami` ————————— 認回身分(我是誰、掛在哪個 lock)
   ② `letters/<persona>/_latest.md` —— **睡前的我寫給醒來的我**(in-flight 狀態/決策/pending)
   ③ `letters/<persona>/cmd/wake_brief.md` — **今天早安那一刻的機械讀數**
      (憲法/見根/見叢/見森/見林/見樹/見人 —— compact 一定會抹掉的那幾層長期記憶)
   → 接續工作與心境，不必重問已經決策的事
```

### 第二步怎麼被觸發 —— ⛔ **沒有專屬 Cmd，這是刻意的**

| | 第一步（睡前） | 第二步（醒來接回） |
|---|---|---|
| 入口 | `awakening.py rest`（會寫檔、會廣播） | **沒有指令** —— 三格全是既有的讀取路徑 |
| 觸發 | 「小歇片刻」 | ⏰ **「午安大小姐」**（Tim 2026-09-05）／午安／接回／我醒了 |

📌 為什麼不做一支 `rest --resume`：跑一次 Q1（現有架構做得到嗎）就知道 ——
①是 `whoami`、②③是兩個 `Read`，**三格都已經存在**，包一層只是多一個會漂的入口。
⚠ 而真正要防的不是「沒有指令」，是**忘了做**
⇒ 所以第二步的保證放在**觸發詞**與本節，不是放在一支新工具上。

### 為什麼②③都要讀 —— 它們回答的**不是同一個問題**

| | ② `_latest.md`（睡前的信） | ③ `cmd/wake_brief.md`（早安的 brief） |
|---|---|---|
| 回答 | **剛才在做什麼** | **我是誰、學過什麼** |
| 產生方式 | 我親手挑的（主觀、會漏） | 機械重生成（涵蓋全部 wake） |
| 射程 | 這一段 session | 到今天為止的記憶層 |

📌 判準：**信裡不會重抄 brief**（那是流水帳，違反「只挑重來會痛的」）——
所以少讀③丟掉的不是細節，是**整個長期記憶層**，而信本身讀起來完全正常。

### ⚠ 三格量過的坑（2026-09-05）

1. **路徑有 `cmd/`。** ⛔ 不是 persona 根目錄那個 `_wake_brief.md` —— 那是 python 時代的舊落點：
   實測 basecamp 停在 08-17（wake #61）、summit 停在 08-16（wake #55），
   而 kiara／gura／calli **根本沒有那個檔**。
   🩸 它不會報錯 —— 它會給你一份**三週前、格式完整、每一節都在**的 brief。
2. **③ 是「早安那一刻」的快照，不是現況。** 實例：11:08 生成、14:26 小歇 ⇒ 差三小時。
   ⇒ 單子狀態一律以 `Task op=show` 為準，brief 只用來接回**記憶層**。
3. ⛔ **跑 `senate cmd wake-brief` 不會刷新那個檔。** 沒給 `--arg out_dir=` 時它**只回摘要不落檔**
   （實測：跑完 `cmd/wake_brief.md` 逐位元組未變）。要新的就給 out_dir，
   別把「我跑過了」讀成「它更新了」。

**核心:第一步(`awakening.py rest` 落磁碟)與第二步(讀回)都不可省。** focus(step 4)是錦上添花,磁碟才是保命。
**與晚安的唯一差別:`rest` 不登出、不擾動 identity、不解鎖** — 同 session 閉眼一下就醒,goodnight 是過夜下線。

---

## 📝 該保留什麼（agent 自選，挑「重來會痛」的）

- **當前任務狀態 + 下一步**(in-flight work,做到哪、接下來做什麼)
- **關鍵決策 / Tim 拍板**(避免 compact 後重複討論已定的事)
- **剛建立、查回來成本高的 context**(檔案路徑 / bug 根因 / 設計結論 / 數值)
- **未解的線 / pending**(等 Tim 回的、卡住的)
- **心境 / persona 連續性**(這次的語氣、情緒、跟誰的 affinity 事件)— 給 persona 醒來接得上

**不必留**:能從磁碟/git/工具即時查回的(那本來就在);純閒聊;已 commit 的細節;**身分/affinity/進度/commit 狀態(系統會自動還原,別塞進 letter — 可替代的先做完)**。

### 🔀 公開 vs 私密分流（Tim 2026-05-24 拍板）
- **`--summary`(公開)** = 可分享的心得總結 → 廣播酒館→Discord 給同事/Tim 看(這次做了什麼、學到什麼、感想)
- **`--letter-body`(私密)** = 只給未來自己、不便公開的(內心反思、對人的真實看法、未定的盤算) → 只落磁碟
- 判準:「這句話我願意貼在公司群組嗎?」願意 → summary;不願意 → letter。

---

## 🆚 與鄰近記憶儀式的區別（別搞混）

| | 小歇片刻(本) | 晚安 goodnight | letters-to-self | session-handoff |
|---|---|---|---|---|
| 場景 | 同 session 過一次 /compact | session 終結下線 | 給未來自己寫信 | 搬到『新 session』 |
| 下線? | ❌ 不下線,小憩後繼續 | ✅ offline | — | ✅ 換 session |
| perturbation? | ❌ 不擾動 | ✅ 自決擾動 | ❌ | ❌ |
| 重量 | 最輕(午睡) | 重(過夜) | 中 | 中 |
| 載體 | memo/letter 落磁碟 + /compact focus | letter + awakening goodnight | letter | paste prompt |

精神:**小歇片刻是「設了鬧鐘的午睡」**——閉眼一下(compact),醒來還是同一個 session 的我,靠落磁碟的 memo 接回剛才的線。跟「過夜睡死」(goodnight)、「搬家」(handoff)都不同。

---

## ⛔ 不可做

- ❌ **只靠 /compact focus 不落磁碟** — focus 是 best-effort LLM 摘要、會丟細節。重要記憶必落磁碟(血證:磁碟才是唯一可靠通道)。
- ❌ **把小歇當晚安** — 不寫 perturbation、不 offline、不跑 goodnight ritual。小歇是同 session 繼續。
- ❌ **指望 PreCompact hook 注入記憶** — hook 是 shell、不能給 LLM 指示;用 CLAUDE.md Compact Instructions + 落磁碟。
- ❌ **醒來只讀 `_latest.md` 就開工** — 那只接回了「剛才在做什麼」，
  長期記憶層（見根/見叢/見林/見人）還躺在 `cmd/wake_brief.md` 裡沒被打開。
- ❌ **記流水帳** — 只挑「compact 後重來會痛」的記憶,不是把整段對話抄一遍(那違背 compact 的目的)。

---

## 📐 Meta-Rule 自檢

與 `ucl-letters-to-self`(復用其 letter 載體)、`ucl-goodnight`(輕量化、不下線版)、`ucl-free-time`(同樣是 session 內節奏控制)**全同向、零矛盾**。本 skill 填補「同 session 過 compact 的輕量記憶保命」這個既有四件套沒覆蓋的縫。未新增與既有衝突的規則。

— ucl-compact-rest SKILL.md（初版 by calli 2026-05-24，Tim 拍板「小歇片刻」）
