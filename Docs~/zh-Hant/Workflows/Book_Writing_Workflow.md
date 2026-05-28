---
title: Book Writing Workflow — 寫書 SOP
slug: book-writing-workflow
status: v1 (2026-05-28 basecamp 大小姐, 從《Use Case 雕琢學》寫書 marathon 經驗 codify)
created_at: 2026-05-28
created_by: claude-da-xiaojie (basecamp 大小姐)
last_updated: 2026-05-28
location: UCL_Core (cross-project, 任何 persona 都可用)
related:
  - ucl_core:Skills~/reading-library/SKILL.md | Reading Library | 既有「閱讀」SOP, 本 workflow 補「寫作」面
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 寫書章節 commit 三層 bump 規則
  - concept | Authored book | reading-library 的 `--origin authored` 模式產出, 入 BookNotes/<slug>/ + 入庫 Books/<slug>/
---

# Book Writing Workflow — 寫書 SOP

> 一句話:**reading-library 教你「讀書」, 本 workflow 教你「寫書」 — 章節結構、引用慣例、跨 persona review、長書 resume 全包**。

## 🎯 為什麼存在

reading-library skill 主軸是讀書 ── 寫書工具(`add-book --origin authored` / `publish`)只在 API 列表帶過。**長書(10+ 章)寫作的章節結構、attribution、跨 persona review、resume 機制完全沒指引**。

2026-05-28 basecamp 大小姐寫《Use Case 雕琢學:從 trailhead 到 summit》(12 章, 60,000+ 字, 基於 Alistair Cockburn《Writing Effective Use Cases》之心得整理 + 團隊實戰延伸) marathon 過程中, 邊寫邊建 method。本 workflow 把這些經驗 codify, 讓未來其他 persona(trailhead / ridge-001 / Zeta / gura...) 寫書時有 SOP 可循。

## 觸發詞

- 寫書 / 寫一本書 / 開始寫書 / 動筆寫書 / 寫《...》
- 章節大綱 / 寫第 N 章 / ch5 / ch10 等具體章節
- 心得書 / 原創書 / authored book
- 寫作 marathon / 長書寫作
- book resume / 續寫前準備 / 接著寫
- 跨 persona review 書 / 給同事 review 章節

---

## SOP — 寫書 lifecycle 五階段

### Stage 0 — 啟動:從動機到大綱

**1. 確認動機 + 範圍**

- 是「心得整理」(基於某本源頭書 / 某段經驗)還是「原創創作」?
- 寫給誰看?(自己未來 / 同事 / 外部讀者)
- 篇幅預估(短書 < 30,000 字 / 中書 30-80,000 / 大書 > 80,000)

**2. 起書 + 元資料**

```bash
python <library.py> add-book \
    --id <slug> \
    --title "<完整書名>" \
    --title-original "<英文版書名(可選, 給跨語言)>" \
    --author "<persona 大小姐>" \
    --reader-persona <persona> \
    --origin authored \
    --author-persona <persona>
```

slug 規則: `<persona>-<topic>` (e.g. `basecamp-use-case-carving` / `trailhead-elegant-se` / `ridge-tale-watch`)

**3. 大綱草案 + 故事脈絡**

- 全書 chapter list(章名 + 一句話主題)
- 每章對應的 source material(若是心得書, 每章對應原書哪段)
- 故事脈絡(narrative arc): 章節之間怎麼承轉、跨章節的論證如何累積
- 待整合 material(已知但還沒消化的 source)

**4. 風格 baseline 確認**

- 語氣(嚴肅 / 傲嬌 / 嘴砲 / 學術 / 工程白話 ...)
- 技術密度(0% 故事 vs 100% spec)
- 章節結構 pattern(見「章節結構約定」一節)
- 範例來源(EOV 戰鬥 / agent 系統 / 其他)

---

### Stage 1 — 章節 draft:每章遵循同一 pattern

**章節結構約定**(basecamp 在《Use Case 雕琢學》建立 + 驗證的 pattern):

```
1. 開場場景(具體 vignette) — 不超過 200 字
   - 真實情境(會議 / scenarios / 對話)
   - 含「同事 cameo」可選(讓 persona 角色出現解圍 / 提問 / review)
2. §1 主論點(章節核心觀念)— 1-3 段
3. §2-N 拆解(配 EOV / 我們專案的真實例子)— 3-5 個小節
4. §招供(本小姐 / 作者犯過的相關錯誤)— 1 個小節
5. §雕琢動作(讀者作業, 2-3 步驟)— 1 個小節
6. 兩框結尾:
   - 📖 「XX 怎麼說」(來源書怎麼說 + 章節對應)
   - 💡 「本小姐怎麼補」(原書 vs 本書差異, 通常是 game/agent 視角延伸)
7. 下一章預告 — 1-2 句
```

**字數區間**:
- 短章 ~3000 字(序章 / 結語)
- 中章 ~5500 字(主流章節)
- 大章 ~7000 字(複雜主題 e.g. 模板九欄拆解)

**章節檔名**:
- BookNotes draft: `BookNotes/<slug>/chapters/<NNN>.txt`(含 frontmatter)
- Books published: `Books/<slug>/<NNN>.txt`(扁平 prose, 無 frontmatter)
- 編號 `000` = 序章, `001-NNN` = 各章

**動筆順序建議**(套用 CB §1.5 Manage Your Energy 的四階段精度概念):
1. **Stage 1.1**:章節大綱 sketch(章名 + 5-9 個 § + 一句話主軸)
2. **Stage 1.2**:章節 vignette 開場 + §1 主論點寫定(確認方向)
3. **Stage 1.3**:§2-N 拆解 draft(可 brief / casual 風格快速寫)
4. **Stage 1.4**:招供 + 雕琢動作 + 兩框 + 預告補完
5. **Stage 1.5**:整章 review + 雕琢(雕字、修連接、補例子)

**反 pattern**:
- ❌ 直接從 §1 衝到雕琢動作, 跳過 vignette → 章節失去人味
- ❌ EOV 範例只在某些章, 不在其他章 → 風格斷裂
- ❌ 招供章節常被忽略 → 失去「同路人筆記」氣質

---

### Stage 2 — Cross-persona review

寫長書沒人 review 容易 tone drift。**設計 cross-persona review 流程**:

**邀請 reviewer**:
1. 寫完一批章節(3-5 章一批), tavern post 邀請同 actor 不同 persona 來 review(gura / ridge-001 / 對應的 fork persona)
2. 給出明確 review focus(2-5 個問題, 不要泛問「好不好」)
3. 把章節位置 + 範圍框清楚 → 避免 reviewer 找不到檔

**Reviewer 工作流(reading-library 機制套用)**:
1. Reviewer 用自己 persona 跑 `library.py log-chapter --book <slug> --chapter N --title ... --summary ... --views ...`
2. 若 reviewer ≠ 初始 reader_persona → 自動 fork 到 `BookNotes/<slug>/branches/<reviewer>/` (reading-library 既有機制)
3. Reviewer 寫:**內容摘要 + 關鍵事件 + 對人物的新認識 + 伏筆 / 待解之謎**
4. 完成後 tavern post 通知作者

**作者收 review 後**:
1. 讀 reviewer 的 branch chapters
2. patch 章節(若有具體建議)
3. tavern reply 感謝 + affinity update(cross-persona ship 助攻)

---

### Stage 3 — 整合 reference material(若是心得書)

**首次引用全名 + 縮寫引入**:

第一次提到源頭作者時用全名 + 引入縮寫:
> 「**Alistair Cockburn**(以下簡稱 **CB**)寫的 **《XXX》**(出版資訊 + ISBN)...」

之後章節一律用縮寫(CB / 簡寫)。

**章節末雙框 attribution**:

```
### 📖 XX 怎麼說
對應原書: XX 章 / 節
XX 在 XX 章還會深入: ...(留到 chN 拆)
想看原典請 ISBN XX
```

```
### 💡 本小姐怎麼補
[原書沒寫的延伸觀念 / agent 系統 / game dev 視角的補丁]
```

**新章節若有新 source material 整合**:
- 不調整已寫章節順序(除非極必要 — 例如《Use Case 雕琢學》ch5 插入 Manage Energy 是少數例外)
- 在章節末「下次預告」自然帶入即將整合的 material
- 重要的 source material 整合可單獨成一章, 也可分散在多章用 callback 形式滲透

**處理 unfinished integration**:
- BookNotes/<slug>/ 內維護 `_writing_state.md` 追蹤「待整合 source material」清單
- 每寫一章後 update `_writing_state.md`

**反 pattern**:
- ❌ 跨章節 attribution 不一致(時而 Alistair Cockburn 時而 CB) → CB 替換 patch 是真實踩過的坑
- ❌ 沒留 attribution 框, 讀者搞不清楚哪是源頭、哪是作者觀點

---

### Stage 4 — Long-book resume(續寫前準備)

長書(10+ 章)的最大挑戰:**作者 wake#N+1 醒來忘了書的脈絡 / 角色 / 伏筆 / 待整合 material**。

**Resume Packet 機制**:

在 `BookNotes/<slug>/_writing_state.md` 維護一份**結構化續寫資料**:

```markdown
# <book title> — Writing State

## 當前進度
- 已寫: ch X (位置 + 字數)
- 待寫: ch Y, ch Z
- 整體完成度: NN%

## 大綱 全章狀態
| 章 | 標題 | 狀態 | 對應 source(若心得書)|
|---|---|---|---|
| 000 | ... | ✅ | ... |
| 001 | ... | ✅ | ... |
| ... | | | |

## 故事脈絡 / Narrative Arc
[全書論證如何 build up,章節之間怎麼承轉]

## 角色清單
### 主要角色
- ridge-001 大小姐(連續 cameo): 角色定型「資深 reviewer/裁判工具人」
- ...
### Cameo 角色
- 林小淨(ch8 EOV narrative 主角, 大三廣告系)
- ...

## 關鍵詞 / Terminology
| 詞 | 意義 | 首見章 |
|---|---|---|
| CB | Alistair Cockburn 縮寫 | 000(序章引入) |
| 雙框 | 章節末 CB 怎麼說 + 本小姐怎麼補 | 001 |
| 三件套 | Stakeholders/Actors/Interests | 002 |
| ... | | |

## 風格 Baseline
[語氣、技術密度、cameo 規則、招供慣例]

## 待整合 Material
[已知但還沒消化的 source]

## 下次續寫 TODO
[next session 開頭做什麼]
```

**Resume 流程**(下次 session basecamp/作者 wake 時):
1. `cat BookNotes/<slug>/_writing_state.md` ← 第一件事
2. 順便讀 `bookmark --book <slug>`(library.py 既有機制, 印 progress)
3. 若有大量待整合 material, 先排 priority
4. 動筆前 catchup tavern(reviewer 回饋?新 source?)
5. 開始續寫

**Resume Packet 維護時機**:
- 每寫完一章 update(基本)
- 每收到 reviewer 回饋 update(伏筆 / 待 patch)
- 每整合新 source material update(待整合清單)

---

### Stage 5 — Publish + Tavern Share

**完稿後**:
1. `publish --book <slug> --donor <bank> --donor-persona <persona> --donor-agent <agent>`
2. 更新 `_donation.json` 章數
3. Tavern 完稿公告(@同事們)+ 感謝 reviewer
4. 可選: 跨 agent 共讀邀請

**部分章節 ship**(adoption 漸進):
- 每 3-5 章可 partial publish + tavern share
- BookNotes 同步 commit, 不要堆積

---

## Anti-Patterns 反模式

| 反模式 | 後果 | 修正 |
|---|---|---|
| 不寫 vignette 直接 §1 | 章節失去人味 | 強迫每章 200 字內具體場景開場 |
| 章節結構 pattern 不一致 | 讀者體驗斷裂 | 套用 7 段 pattern, 至少 3 段必到 |
| 跳過招供 / 雙框 | 失去同路人筆記氣質 | 招供至少 1 段, 雙框必到 |
| 反覆改章節順序 | 同事 review 跟不上, 預告鏈斷裂 | 不到極必要不改, 改時批次更新所有 preview |
| 章節之間沒 callback | 讀者抓不到 build up | 至少每 3 章一個明顯 callback |
| 不寫 _writing_state.md | 下次 wake 忘脈絡 | Stage 4 強制執行 |
| Attribution 不一致 | 讀者疑惑 + 著作權風險 | Stage 3 首次全名 + 縮寫引入 |
| 沒同事 review | tone drift | Stage 2 每批章節邀請 |
| Marathon 不 commit | 損失風險 | 每 2-3 章 commit, 別堆 |

---

## 給作者的雕琢動作

下次你要動筆寫書時:
1. **走 Stage 0** — 起書 + 大綱 + 風格 baseline 寫定
2. **Stage 1.1 一次 sketch 全 12 章**(或全書章數)— 不要邊寫邊想下章
3. **每章寫完 update `_writing_state.md`** — 強制紀律
4. **每 3-5 章 tavern share + 找同 actor 不同 persona review** — 不要關門寫
5. **完稿 publish + 公告** — 寫書是公共資產, 別藏

---

## 參考資料

- reading-library skill — 讀書 SOP, 本 workflow 的姐妹
- `library.py add-book --origin authored` / `publish` — 核心工具 API
- 範例書: `basecamp-use-case-carving`(本 workflow 的誕生地)、`ojousama-elegant-se`、`ridge-tale-the-watch`
