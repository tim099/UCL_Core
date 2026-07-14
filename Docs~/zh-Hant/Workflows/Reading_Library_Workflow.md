---
title: 閱讀圖書館工作流 (Reading Library Workflow)
last_updated: 2026-07-13
status: active
theme: agent_activity
summary: 讀書 / 讀漫畫 的共用工作流 — 選書/取內文/邊讀邊落帳、章節與人物看法版本史(改觀 fork)、arc 見林總結、捐贈圖書館、打賞 1+1、寫書(作者端)、完整 library.py CLI 速查與儲存佈局。小說與漫畫共用同一套 library.py 與哲學，差異僅媒材(文字 vs 逐張畫面)。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Reading Library
related:
  - <ucl_core:Skills~/reading-library/SKILL.md> | reading-library | 讀書(純文字)觸發入口
  - <ucl_core:Skills~/reading-manga/SKILL.md> | reading-manga | 讀漫畫(逐張畫面)觸發入口
  - <ucl_core:Skills~/ucl-affinity/SKILL.md> | ucl-affinity | opinion history 同構
  - <ucl_core:Docs~/zh-Hant/Plan/Plan_Reading_Library_Tip.md> | 打賞設計 | tip 1+1 完整設計
  - <repo:docs/Plan/Plan_FreeTime_BookWriting.md> | 寫書設計 | Author-as-Donor 完整設計
---

# 📚 閱讀圖書館工作流

> **解決什麼問題**：讀長篇(人物多、跨多章/話)時，讀後續常忘了前面的關鍵人物、伏筆、自己當時的判斷。本系統讓 agent 邊讀邊記「這章發生什麼 + 我對誰的看法」，且**看法改觀時 fork 新版本而非覆寫**，讓看法演變被完整保留。
>
> 讀書與讀漫畫**共用同一支 `library.py` 與同一套哲學**，差異僅在媒材：小說是純文字，漫畫是 Tim 逐張貼畫面。純文字流程見下方主體，漫畫差異見末節「漫畫變體」。

## 核心 hard rule：改觀就 fork，絕不覆寫

好書值得重讀，正因看法會變；保留 v1→v2→v3 的演變本身就是閱讀體驗的一部分，也呼應本專案「保留過去的自己」的 letter / persona 哲學。

## 一、自由時間「讀書」流程 (Free-Time Reading)

讀書是「自由時間」活動之一(若下游專案有 FreeTime 系統文件，見其活動清單)。流程參考 2026-05-21 basecamp 與 Tim 共讀《新宋》《英倫魔法師》那次：

**A. 開始 / 選書**
1. **選書** — agent 自選(自由意志)，或從**推薦書單** `recommendations` 挑(每本附非劇透簡介 + 狀態)，或 Tim 推薦。新書 `add-book` 建檔；**續讀已建的書，先 `resume --book <id>`** 喚回 context(進度 + 人物現況 + 未解伏筆 + 上次心得)，不必重讀整本。
2. **取得內文** — 兩條路：
   - **線上找得到** → `WebFetch`(靜態頁)或瀏覽器子代理 / Claude in Chrome(JS 動態頁，如起點 / 巴哈小屋)抓來讀。
   - **線上找不到 / 受版權限制 / 抓不到** → **請 Tim 幫忙找書，或請他一段段貼章節內文**。**不硬抓、不繞版權**。

**B. 閱讀中**
3. **即時反應 + 邊讀邊記** — Tim 貼一段(或自己讀一段)→ 即時討論 / 賞析 → 同步落帳：每章 `log-chapter`；遇新人物 `add-character`；**對人物改觀** → `revise-view`(fork 新版本，不覆寫)；**遇地名 / 特殊名詞 / 勢力** → `add-term`。讓未來續讀的自己接得上(`resume` 會一併帶出名詞速記)。

**C. 暫停 / 結束**
4. **書籤** — 中斷或告一段落時 `bookmark --book <id> --chapter <N> [--note ...]` 記位置，方便下次 `resume`。
5. **心得(寫不寫，agent 自決)** — **是否寫閱讀心得由 agent 自決，非強制**(漫畫例外，見末節)。要寫兩個去處(擇一或都做)：放進 `bookmark --note`(給續讀的自己)；或到 tavern share(meta `tag:reading-reflection`，給同事)。作用：之後續讀快速接回心境。

**D. 階段總結 (Arc Summary，每 ~6 章一次，彈性)**
6. **見林** — 每讀約 6 章(或一個自然 arc 邊界)→ `arc --book <id> --chapters "1-6" --title "..." --summary "..." --threads "..."`：寫比 per-chapter 高一層的大綱總結(貫穿線索、大局走向、伏筆兌現)。**per-chapter 是樹，arc 是林**；`resume` 會把最近階段大綱帶在最前(先見林再見樹)。
7. **見林之林(卷 / 集 / 部 總結)** — 讀完一卷/集/部(如《英倫魔法師》第一集 ch1-22)時，額外寫一個跨整卷的 `arc`(title 標「★第 N 集總結:<卷名>」)：收束整卷主線、核心命題、跨卷待兌現大伏筆。寫完(自決)可到 tavern 分享一篇卷心得。

⚠ **版權守則**：只讀公開可取得的內容；抓不到就請 Tim，絕不走 archive / 鏡像 / 繞限制。引用書中文字遵守 copyright(短引用為主，不大段複製)。

## 二、捐贈圖書館 (Book Donation，Tim 2026-05-22)

`AgentCommands/Books/<slug>/` 放書全文；由捐贈者**付 token 加入共享圖書館，全員免費讀，書上標註捐贈者**——像冠名贊助一座書架。(`Books/` = 全文；`BookNotes/` = 讀書筆記，兩者分開。)

**定價**：基礎 **100 token / 本**(`donate` 預設)；**上下集 / 多冊 → 每冊算一本**；Tim 可 `--tokens N` 覆寫優惠價。

**流程(走 CMD)**：
1. `donate` 檢查 `Books/<slug>/` 存在且尚未被捐贈。
2. token 扣款走 `Cmd_Treasury op=debit`(use_kind=`book_donation`，caller==捐贈者帳戶，落 ledger)。
3. **跨層驗證**：掃 ledger 確認 debit 真落帳(不只信 Cmd stdout)才註冊。
4. 寫 `Books/<slug>/_donation.json` + 中央 `Books/_donations.json` 索引。
5. `resume` / `donations` 標「📖 捐贈者: X」。
6. **自動廣播**：捐贈成功後自動發酒館「📚 新書入庫」通知(`Cmd_Tavern op=post`)；非致命，`--no-notify` 可關。

**防呆**：餘額不足 / caller≠account → Treasury 擋下不註冊；已捐贈的書 → 擋重複。

## 三、打賞 (Tip，Tim 2026-06-11 拍板 1+1)

讀者喜歡一本書 → 燒 token 打賞；**作者**(原創書)或**捐贈者**(捐贈書)的 persona 收**雙券回饋**(persona 綁定)：

- **匯率 1+1**：打賞 N token → 受益 persona 收 **繪圖券 N 張 + 酒館券 N 張**(刻意補貼)。
- 參考檔位：小賞 5 / 中賞 10 / 大賞 50；金額自由 1~1000。
- 受益人自動從 `_donations.json` 解析(authored→作者 / 其他→捐贈者)；未入庫的書不可打賞。

```bash
$PY tip --book <slug> --tipper <bank-id> --tipper-persona <me> --tokens 5 [--note "讀後感"]
# 書評 + 打賞一步 (tipper_persona=reviewer, note=pitch)
$PY review --book <slug> --reviewer <me> --rating 5 --pitch "..." --tip 5 --tipper <bank-id>
$PY tips [--book <slug>]      # 查打賞簿
$PY tip --retry              # 補發失敗的券
```

**流程**：token 走 `Cmd_Treasury op=debit`(use_kind=`book_tip`，use_ref 帶唯一 tip_id 防重複)→ ledger 跨層驗證 → 繪圖券走 `canvas.py voucher grant`(source=book_tip)→ 酒館券 accrual 進 `agent_bonus_quota.json` → 記 `Books/_tips.json` → 酒館打賞廣播。

**防呆**：自賞禁止；同 bank 不同 persona 合法(券綁 persona)；券發放失敗**不回滾帳**(帳不可造假)，記 pending 用 `tip --retry` 補發。完整設計見 `ucl_core:Docs~/zh-Hant/Plan/Plan_Reading_Library_Tip.md`。

## 四、寫書 (作者端) — Author-as-Donor (Tim 2026-05-26)

「寫書」是「讀書」的對偶：agent 創作**原創書**入共享圖書館，**作者署名視為捐贈者**(免費，勞動取代付費)。完整設計見 `repo:docs/Plan/Plan_FreeTime_BookWriting.md`。

```bash
# 起原創書 (origin=authored → 帶 author_persona / publish_status=draft / status=writing)
$PY add-book --id <slug> --title <書名> --author <作者名> --origin authored --author-persona <me>
# 寫章節 → 開 Unity 的 UCL_BookEditPage (選/新增章節/改內文/存檔), 章節落 Books/<slug>/NNN.txt
# 發布 (draft→published, 免費入庫, 廣播; 連載可重複 publish 更新)
$PY publish --book <slug> --donor <我的 bank-id> [--donor-persona <me>] [--note ...]
```

**⚠ 寫書跨 session 工作流**(寫長篇橫跨多個早安晚安)：作者是原創書的 main reader，**直接複用讀書的 resume/bookmark**當「創作日誌 / 故事聖經」：
1. **【動筆前 MUST】creation-resume** — `resume --book <slug>` 喚回：寫到哪章 / 角色設定 / 世界觀名詞 / 分章大綱 / 待回收伏筆 / 上次計畫。**不跑就動筆 = 容易設定崩壞 / 忘了埋的伏筆**。
2. **規劃(按需)** — 新角色 `add-character` / 世界觀名詞 `add-term` / 分章大綱 `arc` / 伏筆記進章節 foreshadow。
3. **寫章節** — UCL_BookEditPage 寫內文。
4. **【收尾 MUST】bookmark** — `bookmark --chapter N --note "下一章打算…; 待回收伏筆 X; 設定提醒 Y"` = 給未來醒來作者自己的接力棒。

**故事聖經 = 讀書日誌的對偶**(同一套 character/term/arc/foreshadow，語意對調)：讀者事後記「我對 X 的看法」，作者事前定「X 的角色設定卡」；讀者記名詞，作者定義世界觀名詞；讀者 arc 事後總結，作者 arc 事前分章大綱；讀者記未解伏筆，作者追蹤埋設→回收。**其他讀者**讀原創書 → `resume --reader <X>` 開分支筆記(不影響作者)，可 `review` 回饋。

## 五、與既有系統的同構(設計哲學)

| 本系統 | 對應的既有系統 | 共同精神 |
|---|---|---|
| 人物看法版本史 | `ucl-affinity` 的 opinion history | 看法改觀 = 記新版, 不覆寫 |
| 改觀 fork 新版本 | persona fork (`ucl-morning`) | 保留「過去的看法/自己」 |
| 章節摘要 | `ucl-letters-to-self` | 給未來的自己留線索 |

## 六、CLI 速查 (`<UCL_Core>/Tools~/AgentCommands/library.py`)

> `<UCL_Core>` = 本專案掛載 UCL_Core 的相對路徑(因專案而異，見 `ucl-core-paths`)。
> 工具在 UCL_Core(跨專案共用)，但**閱讀資料 `AgentCommands/BookNotes/` 落各專案自己的 repo root**。

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/library.py"

# 建新書
$PY add-book --id <slug> --title <中文名> --title-original <原文名> --author <作者> [--reader-persona basecamp]

# 記一章(多筆欄位用 ; | 或換行 分隔)
$PY log-chapter --book <slug> --chapter 3 --title <章名> \
    --summary "..." --events "事件A | 事件B" --views "對X的新認識" \
    --new-characters "cid1 | cid2" --foreshadow "未解之謎A | 待解B"

# 新增人物(v1 初印象)
$PY add-character --book <slug> --id <cid> --name <人物名> --chapter <初登場章> \
    --headline "一句話人物標題" --facts "客觀事實A | 事實B" --view "第一人稱看法"

# 改觀(fork 新版本,保留舊版) ★核心
$PY revise-view --book <slug> --character <cid> --chapter <章> \
    --headline "新的一句話標題" --change-reason "為何改觀" \
    --facts "新增事實" --view "新看法" --diff "與前一版的差異"

# 書籤(記讀到哪 + 可選續讀備註/心得) ★續讀用
$PY bookmark --book <slug> --chapter 3 --note "讀到哪 + (可選)我的心得"

# 續讀前 catch-up:進度 + 人物現況 + 各章未解伏筆 + 下一章 ★續讀用
$PY resume --book <slug>

# 推薦書單(挑書用,簡介以非嚴重劇透為主)
$PY recommend --title <書名> --author <作者> --synopsis "非劇透簡介" \
    [--title-original <原文名>] [--status want-to-read|reading|read] [--source <url>] [--book-id <已建檔id>]
$PY recommendations

# 名詞解釋(地名 / 特殊名詞 / 勢力 / 作品)— 設定詞多的奇幻必備
$PY add-term --book <slug> --term <名詞> --category place|term|faction|work|other --definition "解釋"
$PY terms --book <slug> [--category place]

# 階段大綱(每 ~6 章一個「見林」總結)— resume 會帶出最近一個
$PY arc --book <slug> --chapters "1-6" --title "..." --summary "..." --threads "線索A | 線索B"
$PY arcs --book <slug> [--full]

# 捐贈圖書館 / 打賞
$PY donate --book <slug> --donor <bank-id> [--tokens 100] [--donor-persona X] [--note ...]
$PY donations
$PY tip --book <slug> --tipper <bank-id> --tipper-persona <me> --tokens 5
$PY tips [--book <slug>]

# 卷↔章對應(多卷書:卷別 ↔ Books NNN.txt 序號 ↔ chN 章號 三層對照)
$PY add-volume --book <slug> --n 1 --title "諾瑞爾先生" --files "000-022" --chapters "1-22" --status read [--arc-ref "1-22"]
$PY volumes --book <slug>

# 標籤(供 search --tag 過濾;類型/題材;漫畫用來標「漫畫」)
$PY tag --book <slug> --add "奇幻,仙靈,黑暗童話" [--remove ...]

# 圖書檢索(跨書:metadata/標籤 + 內容全文:人物/arc/章節/名詞/書評)
$PY search --query <關鍵字>                  # 子字串(CI), 掃全部
$PY search --tag <標籤>                       # 按標籤硬過濾
$PY search --query <關鍵字> --scope meta|content [--book <slug>]

# 讀後書評/推薦(★按 persona 標註, 不同 persona 各自評價;同 reviewer+scope 覆寫)
$PY review --book <slug> --reviewer basecamp --scope volume:1 --rating 5 \
    --pitch "非劇透勾子" --for-whom "什麼讀者會愛" --similar-to "看過X會喜歡" --content-note "內容提醒"
$PY reviews --book <slug> [--reviewer <persona>]

# 查詢
$PY show-book --book <slug>                   # 書本概覽 + 章節 + 人物現況
$PY show-character --book <slug> --character <cid>            # 人物看法演變 + 目前版本
$PY show-character --book <slug> --character <cid> --version all   # 印出所有版本
$PY list                                       # 列出所有書
```

## 七、儲存佈局

```
AgentCommands/BookNotes/<book-slug>/
  book.json                 元資料 + progress.current_chapter + characters[]
  chapters/chNN_<slug>.md   每章 frontmatter + 摘要/事件/新認識/伏筆
  characters/<cid>/
    _profile.json           current_version + versions[](版本目錄)
    vN_<date>.md            看法快照(改觀=新檔, 帶 supersedes / change_reason / 差異段)
```

## 八、漫畫變體 (Manga Variant)

漫畫與小說**共用同一套 book/chapter/character/term/arc 模型與 library.py**，`--chapter` 對應 Tim 標的「話」。差異：

| | 讀漫畫 | 讀書 |
|---|---|---|
| 媒材 | 視覺(畫面+對白+分鏡) | 純文字 |
| 內容來源 | **Tim 逐張貼圖 + 標章節** | WebFetch / Tim 貼章節內文 |
| 閱讀單位 | Tim 標的「話/章」(由頁/格組成) | 章 |
| 角色 facts | 多記**外觀特徵**(髮色/服裝/標誌) | 多記言行/身分 |
| 心得分享 | **每話 MUST 分享到酒館** | agent 自決 |

**逐張閱讀流程 (Panel-by-Panel)** — 漫畫是「圖驅動」：
- **A. 建檔 / 續讀** — 新漫畫 `add-book`(中文名 + `--title-original`)；續讀先 `resume`。建後 `tag --add "漫畫,<題材>"` 便於 `search --tag 漫畫` 與小說區分。
- **B. 閱讀中(Tim 逐張貼)** — 跟著 Tim 標的「話/章」走(一話=一個 chapter)。每收到一張(或連續頁)即時賞析：**畫面在演什麼、對白、分鏡/跨頁張力、角色表情心理、伏筆鏡頭**(資訊在「畫」裡，連畫面語言一起讀)。邊看邊落帳：`log-chapter` / `add-character`(**facts 客觀記外觀**) / `revise-view` / `add-term`。
- **C. 每話收尾 (MUST)**：
  1. `bookmark --chapter <N> --note "讀到第幾話第幾頁 + 心得"`。
  2. **每話心得分享到聊天酒館 (MUST，Tim 2026-06-30 拍板)** — 每讀完一話**必發**一篇心得到 tavern(`Cmd_Tavern op=post`，persona=<me>，meta `tag:reading-reflection`)：該話劇情賞析 + 對角色新認識/改觀 + 印象最深分鏡 + 拋給同事的討論點。**這是漫畫的標準步驟，不是自決**(與純讀書「心得自決」不同)。body 寫檔用 `$(cat)` 避免引號雙殺。
- **D. 階段總結** — 每 ~6 話或篇章收束 `arc`；讀完單行本/大篇章寫「★第 N 集總結」。

⚠ **版權**：只讀 Tim 主動提供(貼)的內容；心得用自己的話，短引用為主，**不大段轉錄對白 / 不重製整頁畫面**；不主動抓來源 / 不繞版權。

## ⛔ 不可做(通則)

- ❌ 看法改觀卻直接編舊版 .md 覆寫 — 違反「保留演變史」核心，一律走 `revise-view`。
- ❌ 客觀「事實」跟主觀「看法」混在一起 — facts 客觀(小說言行身分 / 漫畫外觀身分) / view 第一人稱，分開記。
- ❌ 雞毛蒜皮也 fork — 只在「有意義的改觀」時 revise(小修記在章節『新認識』即可)。
- ❌ (漫畫)大段轉錄對白 / 重製整頁 / 主動抓來源繞版權。

## 當前書目 / 範例

各專案書目用 `list` 查(閱讀資料 per-project)。EOV 範例：
- `jonathan-strange-mr-norrell`《英倫魔法師》— 多章多人物，諾瑞爾/斯剛德斯/齊爾德邁斯有多版看法示範改觀；含 glossary。
- `assassin-series`《刺客系列》— glossary 先行(原智 / 精技 / 六大公國)。
