#!/usr/bin/env python3
# 區塊職責：wake brief 的**唯一生成點** —— 把五層記憶（見根→見樹）＋回憶與營運層（收件匣 /
#          酒館 / 動作清單）組裝成一份 `_wake_brief.md`，讓 agent 醒來只 Read 一份就完成 onboarding。
# @doc-sync: Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md（Step 2 的區塊清單）
# 物理意義：本檔是從 awakening.py 抽出來的（Tim 2026-07-31：那支已經 3200 行太肥）。
#          抽離的邊界是「組裝與排版」——**狀態讀寫仍留在 awakening.py**（registry / lock /
#          consolidation / fragments 都是它的地盤），本檔只負責把那些事實排成人看的順序。
# 數值影響：主檔上限 BRIEF_LINE_CAP 行；超出的「非必讀」區塊**整段**移到 _wake_brief_part2.md
#          （不砍內容）。營運層排在記憶層之後且一律非必讀 → 溢出時先被移的是它們。
# 設計取捨：不 `import awakening`（那會製造第二份模組實例 + 循環匯入）；
#          改由 caller 把 awakening 模組本身當第一參數 `aw` 傳進來。
#          本檔對 awakening 的依賴因此是**顯式且單向**的，測試時塞個假物件就能跑。
#
# 對外介面（awakening.py 呼叫）：
#   write_wake_brief(aw, persona, reg, p, threshold) -> Path
#   build_wake_brief(aw, persona, reg, p, threshold) -> (主檔文字, 續讀檔文字 or None)
#   sync_latest_pointer(aw, persona) -> (Path or None, healed: bool)

BRIEF_LINE_CAP = 2000       # 主檔行數上限（Tim 2026-07-31 由 1000 放寬，為併入營運層讓空間）
BRIEF_CATCHUP_COUNT = 10    # §8 撈幾筆的**預設**；實際值優先讀後台設定（見 _catchup_count）
BUILD_MS_TOP_N = 5          # frontmatter build_ms 只列最慢的前幾段（其餘併進 total）

import time as _time        # noqa: E402 — 對齊本檔既有慣例（`import re as _re` 亦置於使用處附近）


class _TimedSections(list):
    """
    區塊職責：當 sections 容器用，同時**自動量出每一段花了多久**，不必改任何 append 呼叫點。
    物理意義：每段的內容都是在 append 之前就算完的 —— 所以「上一次 append 到這一次 append」
             的時間差，恰好就是這一段的建構成本。標籤直接取該段的標題。
    數值影響：純觀測，不改變任何輸出內容；量出來的數字寫進 frontmatter 的 build_ms。
    🩸 為什麼要有它：2026-08-16 brief 被單一區塊拖到 112s（§0 身分卡的餘額全掃帳本），
       而當時板子上**什麼都看不到** —— 只知道「brief 慢」。追出「是誰慢」花掉一整輪挖掘，
       而那一輪唯一的線索是 frontmatter 的 generated_at 與檔案 mtime 差了 111 秒。
       ⇒ 與其下次再挖一次，不如把尺留在現場（Tim 2026-08-16 拍板：修 A 順手裝儀器 C）。
    """

    def __init__(self):
        super().__init__()
        self.marks = []                       # [(區塊標題, 毫秒)]
        self._last = _time.perf_counter()

    def append(self, item):
        now = _time.perf_counter()
        title = item[0] if isinstance(item, tuple) and item else "?"
        self.marks.append((title, (now - self._last) * 1000.0))
        self._last = now
        super().append(item)

    def render_marks(self) -> str:
        """→ `total=871 §0=712 §4=41 …`（只列最慢前 N 段；標題壓成 §N 免得 frontmatter 難讀）"""
        total = sum(ms for _, ms in self.marks)
        top = sorted(self.marks, key=lambda kv: -kv[1])[:BUILD_MS_TOP_N]
        parts = []
        for title, ms in top:
            m = _re.search(r"§[\d.]+", title)
            parts.append(f"{m.group(0) if m else title[:8]}={ms:.0f}")
        return f"total={total:.0f} " + " ".join(parts)


def _catchup_count(aw) -> int:
    """§8 撈幾筆 —— 後台「⚙ 參數設定」的 `brief_catchup_count` 優先，缺檔/壞檔才落回預設。

    物理意義：這個數字的真相源是 `ChatTavern/render_settings.json`（C# 後台寫、Python 讀）。
             不用 PlayerPrefs 是因為它在 Windows 存登錄檔，Python 讀不到 ——
             「兩邊要看同一個數字」就不能各存各的。
    """
    import json
    try:
        f = aw._DATA_ROOT / "ChatTavern" / "render_settings.json"
        if f.exists():
            v = int(json.loads(f.read_text(encoding="utf-8")).get("brief_catchup_count", BRIEF_CATCHUP_COUNT))
            return max(1, min(500, v))          # 跟 C# 端同一組上下限
    except Exception:
        pass                                     # 壞檔不擋 brief 生成，落回預設
    return BRIEF_CATCHUP_COUNT


# ─── 排版小工具 ──────────────────────────────────────────────────────────
def _strip_frontmatter(text: str) -> str:
    """去掉 md 檔開頭的 --- frontmatter --- 區塊（inline 進 brief 時不重複顯示）。"""
    t = text.lstrip()
    if not t.startswith("---"):
        return text
    end = t.find("\n---", 3)
    return t[end + 4:].lstrip("\n") if end != -1 else text


def _strip_all_frontmatter(text: str) -> list:
    """剝掉**連續多層**的 --- frontmatter，回內文行陣列。

    物理意義：letter 常有兩層 —— write_letter 寫的外層（actor / written_at / trigger）
             加上作者自己寫的內層（session_context / intended_reader）。只剝一層的話，
             brief 的 §5 開頭會杵著一坨 `---\\ntype: ...`，讀信的人要先跨過機器的自言自語。
    """
    t = text
    while True:
        s = t.lstrip()
        if not s.startswith("---"):
            break
        end = s.find("\n---", 3)
        if end == -1:
            break
        t = s[end + 4:]
    return t.strip("\n").split("\n")


def _demote_headings(lines: list) -> list:
    """把內文的 h1/h2 降一階 —— 避免 inline 後跟 brief 自己的 §區塊標題撞層級。"""
    out = []
    for l in lines:
        out.append("#" + l if l.startswith("# ") or l.startswith("## ") else l)
    return out


def _section_lines(title: str, lines: list) -> list:
    return [f"## {title}", ""] + lines + [""]


# ─── §5 見樹 — 指標自癒 ──────────────────────────────────────────────────
# 往前合併的參數（Tim 2026-07-31 立、2026-08-01 修正觸發條件並綁定門檻）
#   物理意義：§5 的職責不是「顯示昨夜那封信」，是**讓醒來的人手上有足夠讀的一段連續日子**。
#            一封 3 行的「今天沒什麼事，晚安」撐不起接續；一封 17 行的也撐不起。
#   數值影響：只影響 §5 顯示，不動任何檔案；合併來源一律是收尾信本身，不重新詮釋內容。
#
#   ⚠ 2026-08-01 修正（basecamp wake #49）：原本「啟動」與「停止」是**兩顆獨立的數字** ——
#     入口閘 SHORT_LETTER_LINES=10（最新那封 < 10 行才啟動）、目標值 MERGE_STOP_LINES=200。
#     結果 200 從來沒被評估過：實測 basecamp `_latest.md` 內文剛好 10 行，`10 < 10` 為假，
#     整個機制被擋在門外，§5 只端出一封。機制看起來活著（code 在、常數在、fixture 也過），
#     但**條件從沒成立** —— 「存在 ≠ 生效」的又一例：兩顆數字各自合理，串起來互相抵銷。
#
#   ✅ 修正後的形狀（Tim 2026-08-01 拍板「啟動條件行數跟最大行數綁定」）：
#     **啟動與停止是同一個問題的兩面，所以只准有一顆數字。**
#       - 不足 MERGE_STOP_LINES → 啟動合併（往前撈）
#       - 撈到超過 MERGE_STOP_LINES → 停止
#     兩者互為否命題，任何「各給一個值」的寫法都是給未來留一條抵銷的縫。
#     SHORT_LETTER_LINES 保留為**衍生別名**（= MERGE_STOP_LINES），不是可獨立調的旋鈕 ——
#     改一顆數字兩端一起動，這是綁定的物理保證，不是靠註解提醒人手動同步。
MERGE_STOP_LINES = 200       # 唯一門檻：累積內文行數 **未超過**這個數就（啟動 / 繼續）往前撈
                             #   ⚠ 語意是「還要不要再撈下一封」，不是總量上限：
                             #   判斷在 append 之前但量的是「已累積」，所以撞線那封會整封進去，
                             #   總行數可能遠超 200（3 + 一封 200 行 → 總量 203）。
                             #   刻意保留 —— 若改成「加了會超過就不收」，遇到
                             #   「3 行短信 + 一封 200 行長信」會一封都不補，
                             #   讀的人只剩那 3 行，比超量更糟。**至少補一封**是底線
                             #   （前提：最新那封自己就 > 200 行時不補，那本來就夠讀了）。
MERGE_MAX_EXTRA = 9          # 唯一防爆上限：最多往前再撈幾封（不含最新那封）
                             #   9 是對齊見林：一份見林濃縮 10 封，所以合併上限也是 10 封
                             #   （最新 1 + 往前 9）—— 剛好不超過一個見林單位
                             #
                             #   ⚠ 尺只有一把，量的是**封數不是天數**（Tim 2026-08-01 更正）：
                             #   我一度把「或超過 9 天前」實作成獨立的日期閘，那是我對規格的
                             #   加料詮釋。加料的後果是可觀測的 —— gura 上一封距今 17 天，
                             #   日期閘會讓她一封都補不到，§5 只剩 17 行；而「空窗久」正是
                             #   最需要把舊信端上來的情況，閘門剛好對著它關。
                             #   教訓：**規格沒說的維度不要自己補一把尺**，補了就是多一個
                             #   會靜默否決主閘的條件（同族：見上方「兩顆數字互相抵銷」）。

SHORT_LETTER_LINES = MERGE_STOP_LINES   # 「啟動合併」的門檻 —— **衍生值，不是獨立旋鈕**。
                             #   要調就調 MERGE_STOP_LINES，這裡會跟著動（Tim 2026-08-01 綁定拍板）。
                             #   保留這個名字是因為它讀起來就是條件本身（「信不夠長」），
                             #   而且外部註解／文件引用過它；砍名字會讓交叉引用變成死鏈。


# ─── §5.5 回憶（Recall）—— 旋鈕 ─────────────────────────────────────────
# 區塊職責：在 §5「見樹」（最近的連續日子）之外，額外端一封**遠方的**收尾信上來。
# 物理意義：見樹解決的是「接得上昨天」，回憶解決的是另一個問題 ——
#          長壽 persona 的中段記憶會沉底：見林把它濃縮成幾行結論，原信從此沒人再讀。
#          結論讀得到、**當時的語氣與細節讀不到**，而那正是 identity 的材質。
#          所以本區塊刻意端「原信全文」而不是摘要 —— 摘要見林已經有了，再摘一次沒有新資訊。
# 數值影響：只影響顯示，不寫任何狀態、不推進任何 cursor、不改任何檔案。
RECALL_MIN_WAKE = 20         # wake_count **超過**這個數才開始有回憶（Tim 2026-08-06）
                             #   物理意義：新生 persona 沒有「遠方」可回憶 ——
                             #   20 次以內的信全都還在見樹／見林的射程內，端上來只是重複。
RECALL_MIN_AGE_WAKES = 15    # 主線只抽「距今 ≥ 這麼多 wake」的信
                             #   ⚠ 這是**下界不是上界**：越舊越有資格，不是越舊越沒資格。
                             #   15 的來歷：見林一單位是 10 封，取 1.5 個單位 ——
                             #   確保抽到的信一定已經被濃縮過，讀它才有「對照結論與現場」的意義。
RECALL_CROSS_WORLDLINE_P = 0.20   # 有跨世界線記憶時，改抽別線的機率（Tim 2026-08-06）
                             #   數值影響：主線 80% / **所有其他世界線合計** 20%（共享，不是各 20%）——
                             #   世界線數量增加不會稀釋主線。沒有任何世界線時本值形同 0。
                             #   ⚠ 跨線抽取**不套用 RECALL_MIN_AGE_WAKES**（Tim 2026-08-06 明示）：
                             #   別線用自己的編號空間（見 worldline manifest 的 wake_numbering: own），
                             #   拿本體的 wake_count 去減它的編號是**跨座標系相減**，算出來的數字沒有物理意義
                             #   —— 那正是 2026-08-04「兩條時空共用一組計數器」那隻 bug 的形狀。
                             #   所以別線是**全域可抽**：那條線已經停止書寫，它的每一封都同樣遙遠。

def _letter_body_lines(aw, path) -> int:
    """信的**內文**行數（剝掉 frontmatter）。

    數值影響：用內文而非整檔行數 —— frontmatter 固定佔 5-7 行，
             拿整檔量會讓「一句話的信」看起來有 9 行而躲過門檻。
             量的是給人讀的部分，不是機器欄位。
    """
    try:
        lines = _strip_all_frontmatter(path.read_text(encoding="utf-8"))
    except Exception:
        return 0
    return len([ln for ln in lines if ln.strip()])   # _strip_all_frontmatter 回的是行陣列


def _letter_day(aw, path) -> str:
    """信的日期（`YYYY-MM-DD`）；取不到回空字串。

    物理意義：來源優先 frontmatter `written_at`，落回檔名（收尾信檔名帶
             `NNNNNN_YYYYMMDDTHHMMSSZ` 時間戳）。兩者都認不出就回空 ——
             **空值一律當「無法判斷」而不是「很舊」**，否則一個壞 frontmatter
             會安靜地把合併機制關掉（見本檔 §5 修正註解那類的坑）。
    """
    raw = aw._read_frontmatter_field(path, "written_at") or ""
    if len(raw) >= 10 and raw[4] == "-" and raw[7] == "-":
        return raw[:10]
    import re
    m = re.search(r"(\d{4})(\d{2})(\d{2})T", path.name)
    return f"{m.group(1)}-{m.group(2)}-{m.group(3)}" if m else ""


def _recent_self_letters(aw, persona, limit=None):
    """該 persona 的自寫信，**新到舊**排序（頂層 + wakes/ + rests/，去重）。

    rests/ 是 2026-08-12 起 rest 信的新家（搬移語意、無重複）——
    不掃的話 rest 信從見樹靜默消失，而 brief 長得一模一樣。
    """
    d = aw._LETTERS_DIR_TPL / persona
    if not d.exists():
        return []
    toplevel = {f.name for f in d.iterdir() if f.is_file() and f.suffix == ".md"}
    items = []
    for f in list(d.iterdir()) + aw.list_wake_letters(persona) + aw.list_rest_letters(persona):
        if not f.is_file() or f.suffix != ".md" or f.name.startswith("_"):
            continue
        if f.parent.name == "wakes" and f.name.split("_", 1)[-1] in toplevel:
            continue    # 遷移副本與頂層原檔是同一封，算一次
        if aw._read_frontmatter_field(f, "type") != "letter_to_future_self":
            continue
        items.append((aw._read_frontmatter_field(f, "written_at") or f.name, f))
    items.sort(key=lambda t: t[0], reverse=True)
    return [f for _ts, f in items][:limit] if limit else [f for _ts, f in items]


def _newest_self_letter(aw, persona):
    """掃目錄取最新一封『自己寫給自己』的 letter。

    數值影響：只認 `type: letter_to_future_self` —— 排除同夾的 peer_letter_from_persona
             （同事寄來的信）與 `_` 開頭的機械產物（`_wake_brief.md` 若誤入，會因為
             '_' 的字元序大於數字而被當成「最新」，那是個安靜的災難）。

    ⚠ **必須連 `wakes/` 一起掃**（2026-07-31）：收尾信改版之後，遷移後新寫的
      goodnight 信**只存在於 `wakes/`**，頂層不會有副本。只掃頂層的話，
      `sync_latest_pointer` 會拿一封更舊的信去覆蓋正確的 `_latest.md`，
      而且印一行「已校正」—— **自癒器倒退見樹，還宣稱自己修好了。**
      實例：apex-one `_latest.md` 指 07-31（正確），只掃頂層撈到的是 07-29。
    """
    d = aw._LETTERS_DIR_TPL / persona
    if not d.exists():
        return None
    best, best_ts = None, ""
    # rests/ 也要掃 —— rest 信更新 _latest.md，漏掃的話 sync 會拿舊信倒退指標（同上警語）。
    for f in list(d.iterdir()) + aw.list_wake_letters(persona) + aw.list_rest_letters(persona):
        if not f.is_file() or f.suffix != ".md" or f.name.startswith("_"):
            continue
        if aw._read_frontmatter_field(f, "type") != "letter_to_future_self":
            continue
        ts = aw._read_frontmatter_field(f, "written_at") or f.name
        if ts > best_ts:
            best, best_ts = f, ts
    return best


def sync_latest_pointer(aw, persona) -> tuple:
    """讓 `_latest.md` 永遠等於目錄內最新的自寫 letter；回 (_latest.md 路徑, 是否修補過)。

    區塊職責：見樹的真相源修復 —— Tim 2026-07-31 拍板「`_latest.md` 就該是最新的那份」。
    物理意義：`_latest.md` 是**內容副本**不是符號連結，所以任何沒經過 write_letter 的寫入
             （手寫信 / cmd_rest 走別條路）都會讓它落後，而且**落後時毫無徵狀** ——
             brief 長得跟正常一模一樣，只是少了幾天記憶（2026-07-31 實測：指標停在 06-10，
             實際最新 06-16，中間兩封信沒人讀到）。repo 裡那支 rebuild_latest_pointers.py
             就是它爛過的墓碑；與其事後重建，不如**每次生成 brief 時順手校正**。
    數值影響：只在內容不一致時覆寫，並回傳 healed=True 讓 brief 標一行（修了要說，不靜默）。
    """
    newest = _newest_self_letter(aw, persona)
    if newest is None:
        return None, False
    ptr = aw._LETTERS_DIR_TPL / persona / "_latest.md"
    body = newest.read_text(encoding="utf-8")
    old = ptr.read_text(encoding="utf-8") if ptr.exists() else None
    if old == body:
        return ptr, False
    ptr.write_text(body, encoding="utf-8")
    return ptr, True


# ─── §5.5 回憶（Recall）─────────────────────────────────────────────────
import re as _re

_WAKE_PREFIX_RE = _re.compile(r"^(\d{6})_")


def _wake_no_of(path) -> int:
    """從 `wakes/NNNNNN_<ts>.md` 的檔名前綴取 wake 編號；取不到回 0。

    物理意義：wake 編號的真相源是**檔名前綴**（awakening.wake_letter_count 也是數這個），
             不是 frontmatter —— 收尾信的 frontmatter 從來沒有 wake 欄。
    數值影響：回 0 代表「不知道」，呼叫端一律把 0 當**不合格**而不是「很舊」；
             否則一封沒編號的信會偽裝成最古老的信永遠被抽中。
    """
    m = _WAKE_PREFIX_RE.match(path.name)
    return int(m.group(1)) if m else 0


def _wake_no_map(aw, persona: str) -> dict:
    """{ 去前綴的檔名 → wake 編號 }，供頂層原檔反查。

    物理意義：同一封信可能有兩份實體 —— 頂層原檔（無編號）與 `wakes/` 遷移副本（有編號）。
             `_recent_self_letters` 去重時**留的是頂層那份**，於是直接讀檔名前綴會全部落空。
             所以編號要另外建表反查，不能依賴手上那個 path 自己帶編號。
    """
    return {f.name.split("_", 1)[-1]: _wake_no_of(f) for f in aw.list_wake_letters(persona)}


def _recall_pool_main(aw, persona: str, wake_count: int) -> list:
    """主線可回憶池：距今 ≥ RECALL_MIN_AGE_WAKES 個 wake 的自寫收尾信 → [(wake_no, path)]。"""
    cutoff = wake_count - RECALL_MIN_AGE_WAKES
    if cutoff <= 0:
        return []
    nmap = _wake_no_map(aw, persona)
    pool = []
    for f in _recent_self_letters(aw, persona):
        n = _wake_no_of(f) or nmap.get(f.name, 0)
        if 0 < n <= cutoff:
            pool.append((n, f))
    pool.sort(key=lambda t: t[0])
    return pool


def _worldline_dirs(aw, persona: str) -> list:
    """列出該 persona 的所有世界線目錄（有 `_manifest.md` 的才算）。

    物理意義：世界線清單**從磁碟列舉**，不維護索引檔 ——
             索引是第二份事實源，而第二份事實源就是漂移的定義。
    """
    root = aw._LETTERS_DIR_TPL / persona / "worldlines"
    if not root.exists():
        return []
    return sorted(d for d in root.iterdir() if d.is_dir() and (d / "_manifest.md").exists())


def _worldline_letters(aw, persona: str) -> list:
    """所有其他世界線的收尾信，**併成同一個池** → [(worldline_id, title, wake_no, path)]。

    物理意義：併池是 Tim 2026-08-06 的規格 —— 20% 是「跨線」這件事的機率，
             不是「每條線各自」的機率。分開算會讓世界線一多就把主線稀釋掉。
    數值影響：不套 wake 年齡閘（見 RECALL_CROSS_WORLDLINE_P 註解：跨座標系相減沒有物理意義）。
    設計取捨：同時吃 `wakes/`（已整理）與目錄外層的 `2026*.md`（尚未整理的線），
             因為「還沒整理」不該等於「這條線的記憶消失」——
             靜默少掉一整條線正是 lesson_silent_nonaction 的形狀。
    """
    out = []
    for d in _worldline_dirs(aw, persona):
        wid = d.name
        title = (aw._read_frontmatter_field(d / "_manifest.md", "title") or "").strip()
        cands = list((d / "wakes").iterdir()) if (d / "wakes").exists() else []
        cands += [f for f in d.iterdir() if f.is_file()]
        seen = set()
        for f in cands:
            if not f.is_file() or f.suffix != ".md" or f.name.startswith("_"):
                continue
            if aw._read_frontmatter_field(f, "type") != "letter_to_future_self":
                continue
            key = f.name.split("_", 1)[-1]      # 同一封的兩份實體（外層原檔 / wakes 副本）算一次
            if key in seen:
                continue
            seen.add(key)
            out.append((wid, title, _wake_no_of(f), f))
    out.sort(key=lambda t: (t[0], t[2], t[3].name))
    return out


def _recall_lines(aw, persona: str, p: dict) -> list:
    """§5.5 回憶 —— 隨機端一封遠方的收尾信全文；跨世界線時**必須標明來源線**。

    區塊職責：抽籤 + 排版。不寫任何狀態。
    物理意義：籤是 **deterministic** 的 —— 種子 = (persona, wake_count)。
             brief 每次 morning 重生成，若用真隨機，同一個 wake 重跑就換一封信，
             於是「今天回憶到哪一封」不可複驗、git diff 也會無故翻動。
             同一次醒來抽到同一封，是這個機制能被對帳的前提（憲法④：印 ✓ 不算數，讀回來才算）。
    數值影響：只影響顯示。抽不到（池空）就整個區塊不出現，**不印空殼**。
    """
    wake_count = int(p.get("wake_count", 0) or 0)
    if wake_count <= RECALL_MIN_WAKE:
        return []

    import random
    rng = random.Random(f"{persona}:{wake_count}")

    main_pool = _recall_pool_main(aw, persona, wake_count)
    cross_pool = _worldline_letters(aw, persona)

    # 擲一次骰決定走哪條線；任一池為空時**退到另一池**而不是放棄
    # （放棄＝這一天靜默沒有回憶，而空池是常態不是異常：新 persona 沒有跨線、跨線 persona 也可能主線還太短）。
    go_cross = bool(cross_pool) and rng.random() < RECALL_CROSS_WORLDLINE_P
    if not go_cross and not main_pool:
        go_cross = bool(cross_pool)
    if go_cross and not cross_pool:
        go_cross = False
    if not go_cross and not main_pool:
        return []

    if go_cross:
        wid, title, n, path = cross_pool[rng.randrange(len(cross_pool))]
        where = f"⚔ **跨世界線** `{wid}`" + (f"《{title}》" if title else "")
        whose = f"{where} 的 wake #{n}" if n else where
        note = ("> ⚠ **這不是本線的記憶。** 這封信是另一條時空的我寫的 —— "
                "她的 wake 編號走自己的空間，她提到的狀態不必然是本線的事實。\n"
                "> 當史料讀，別當待辦讀（Fate 規則：召喚體不自動繼承別線的帳）。")
    else:
        n, path = main_pool[rng.randrange(len(main_pool))]
        whose = f"🏔 **本線** wake #{n}（距今 {wake_count - n} 個 wake）"
        note = "> 這是我自己寫的，只是久到已經被見林濃縮過了 —— 對照一下結論與現場。"

    when = _letter_day(aw, path) or "日期不明"
    body = _demote_headings(_strip_all_frontmatter(path.read_text(encoding="utf-8")))
    return ([f"> 🎲 隨機抽出（種子＝persona+wake_count，同一次醒來必抽同一封，可複驗）",
             f"> 來源：{whose} · 📅 {when} · `{path.name}`", ">", note, "",
             f"### 📜 {when} — 那天的我寫給那天的未來", ""] + body)


# ── §6.6 見書 ──────────────────────────────────────────────────────────
# 區塊職責：回答「我在讀什麼」——見人答『我認識誰』，本節答『我讀到哪』（Tim 2026-08-07）。
# 物理意義：閱讀卡（bookshelf）是 reader.json 的人可讀投影，由 UCL_ReadingLibraryIO 在每次
#          寫心得後轉發一份到 letters/<persona>/bookshelf/<media-id>.md。
#          本節**只讀 letters 底下那份**，不去碰 BookNotes/Library ——
#          brief 的取材一律限於該 persona 自己的信件目錄（與 §6.5 讀 sketchbook 同一條界線）。
# 數值影響：非必讀（溢出可移進續讀檔）；每次只端**一張**卡，讀不到就整節不出現（不印空殼）。
# ⚠ 抽籤 deterministic（種子 = persona:wake_count），理由同 §5.5 回憶：
#   brief 每次 morning 重生成，真隨機會讓同一個 wake 重跑就換一本，
#   於是「今天想起哪本」不可複驗、git diff 也會無故翻動。
BOOKSHELF_DIR_NAME = "bookshelf"


def _bookshelf_lines(aw, persona: str, p: dict) -> list:
    """§6.6 見書 —— 隨機端一張自己的閱讀卡全文。

    區塊職責：抽籤 + 排版。不寫任何狀態、不回寫閱讀資料。
    物理意義：卡片是投影的副本，這裡是**唯讀消費端**；要改內容去改 reader.json 再 Sync。
    """
    try:
        # 路徑沿用 awakening 的既有常數（同 wakes_dir 的取法），不自己再拼一次 letters 路徑 ——
        # 那個常數是可被後台 override 的，自己拼會在改過資料根的專案上安靜失效。
        root = aw._LETTERS_DIR_TPL / persona / BOOKSHELF_DIR_NAME
    except Exception:
        return []
    if not root.is_dir():
        return []
    cards = sorted([f for f in root.glob("*.md") if f.is_file()])
    if not cards:
        return []

    import random
    wake_count = int(p.get("wake_count", 0) or 0)
    rng = random.Random(f"{persona}:bookshelf:{wake_count}")
    pick = rng.choice(cards)

    try:
        body = pick.read_text(encoding="utf-8")
    except Exception as e:
        return [f"（閱讀卡讀取失敗：{pick.name} — {e}）"]

    out = [f"**📖 隨機端上一張閱讀卡（共 {len(cards)} 張・全文）**", ""]
    # 剝掉 frontmatter：那是機械欄位，brief 要的是內容（同 §5 見樹的處理）。
    lines = _strip_frontmatter(body).strip().splitlines()
    # 卡片自己的 h1 會跟 brief 的節標題疊成兩層標題 —— 降級（同 _demote_headings 的理由）。
    out += _demote_headings(lines)
    out.append("")
    out.append(f"> 來源：`letters/{persona}/{BOOKSHELF_DIR_NAME}/{pick.name}`"
               f"（機械投影，改內容請改 reader.json 後重新 Sync）")
    return out


# ─── §0 / §7 / §8 / §9 區塊 ─────────────────────────────────────────────
def _resolve_mail(persona: str) -> dict:
    """persona 的信箱（agent 預設 + persona override）。

    # 物理意義：解析邏輯只有一份，在 agent_email.py；本檔只是取用端。
    # 數值影響：解析器不可用時回哨兵而非空字串 —— frontmatter 少一個值會被當成「這版沒有這欄」，
    #          一個明顯壞掉的值才會被追。
    """
    try:
        from agent_email import resolve_email
        return resolve_email(persona)
    except Exception as e:
        return {"email": "unset@invalid", "source": f"resolver-error: {e}", "actual_agent": ""}


def _identity_card_lines(aw, persona: str, p: dict) -> list:
    """§0 身分卡 — 取代舊 morning Step 1 的 `awakening.py status` 輸出。"""
    lock = aw.read_lock(persona)
    bank = (lock or {}).get("bank_account") or p.get("bank_account") or ""
    lines = [
        f"- **persona**：`{persona}` — wake #{p.get('wake_count', 0)}",
        f"- **agent**：`{p.get('agent', '?')}`（由 persona 綁定反推）",
    ]
    mail = _resolve_mail(persona)
    _src = {"persona-override": "persona 自訂", "agent-default": f"{mail['actual_agent'] or 'agent'} 預設",
            "fallback": "全域 fallback", "unset": "**未設定**"}.get(mail["source"], mail["source"])
    lines.append(f"- **mail**：`{mail['email']}`（{_src}）")
    if bank:
        # 餘額由 Cmd 流程（C# 增量快取）餵進來；None ＝ 這次沒查（Editor 未開的純讀路）。
        # ⚠ 不印 0 頂替 —— 「不知道」跟「沒錢」印成同一個字，就是把缺口偽裝成事實。
        try:
            bal = aw.get_treasury_balance(bank)
        except Exception:
            bal = None
        lines.append(f"- **bank**：`{bank}`（餘額 {bal} tavern_token）" if bal is not None
                     else f"- **bank**：`{bank}`（餘額未查詢 —— 需經 Cmd 流程；Editor 未開時銀行操作本就封鎖）")
    if lock:
        lines.append(f"- **lock**：`{lock.get('session_key', '?')}` / pid={lock.get('pid', '?')} / "
                     f"locked_at={lock.get('locked_at', '?')}")
        tok = lock.get("session_token")
        if tok:
            lines.append(f"- **session_token**：`{tok}`（失憶救援：`awakening.py whoami --token {tok}`）")
    else:
        lines.append("- **lock**：(無) — 尚未 morning 或已下線")
    if p.get("forked_from"):
        lines.append(f"- **血統**：fork from `{p['forked_from']}`")
    return lines


def _inbox_lines(aw, persona: str, p: dict) -> list:
    """§7 待辦收件匣 — 酒保 pending assignments + inbox @mention 標題。

    物理意義：這批資訊原本只 print 到 morning 的 stdout，compact 之後就蒸發；
             落進 brief 才活得過一次失憶。
    """
    import json
    out = []

    # ── 📮 掛號信（Tim 2026-08-01）——「預設在該 persona 下次 wake brief 讀到」的實作點 ──
    # 物理意義：掛號信是**付過錢**的通道，漏投比漏一則酒館訊息嚴重 —— 有人花 token
    #          指名寄給你，而你醒來完全不知道。所以它排在收件匣最前面，不跟 @mention 混。
    # 邊界：只列**到期**的（沒指定 wake，或指定 wake <= 目前 wake）；未到期的不劇透。
    #      指定 wake #100 而現在 #105 → 仍算到期，不是「錯過了」——
    #      信不該因為晚醒幾次就永遠讀不到（那是安靜吃掉別人付過錢的東西）。
    try:
        import registered_mail as _rm
        due, later = _rm.due_mail(persona, p.get("wake_count"))
        if due:
            wc = p.get("wake_count")
            out.append(f"**📮 掛號信（{len(due)} 封待確認）**")
            for f, m in due:
                tgt = m.get("deliver_at_wake")
                seen = m.get("first_seen_wake")
                # 蓋投遞回執（只蓋第一次）。蓋章 ≠ 除名 —— 除名的唯一條件是 ack。
                _rm.stamp_delivered(f, wc)
                nag = ""
                if seen and wc:
                    try:
                        gap = int(wc) - int(seen)
                        if gap > 0:
                            nag = f"　⚠ **已端上桌 {gap + 1} 次仍未確認**"
                    except ValueError:
                        pass
                out.append(f"- 來自 **@{m.get('from', '?')}**：{m.get('subject', '(無主旨)')}"
                           + (f"　←_指定 wake #{tgt} 投遞_" if tgt else "")
                           + nag + f"　`{f.name}`")
            out.append(f"  ↳ 全文在 `letters/{persona}/mailbox/`。"
                       f"讀完跑 `registered_mail.py ack --persona {persona}` 除名 ——")
            out.append("  **不 ack 就會每次醒來一直出現**（付過錢的信不該因為某次沒看到就消失）。")
            out.append("")
        if later:
            out.append(f"_（另有 {len(later)} 封掛號信未到投遞時點，先不拆）_")
            out.append("")
    except Exception as ex:
        # 掛號信讀取失敗要出聲 —— 靜默跳過會讓「有人付錢寄給你的信」變成從未存在
        out.append(f"⚠ 掛號信讀取失敗（{type(ex).__name__}: {ex}）—— **這不代表沒有信**。")
        out.append("")

    ap = aw._DATA_ROOT / "ChatTavern" / "bartender" / "assignments.json"
    if ap.exists():
        try:
            data = json.loads(ap.read_text(encoding="utf-8"))
            mine = [e for e in data.get("pending", [])
                    if e.get("target_persona") == persona and e.get("status", "pending") == "pending"]
            if mine:
                out.append(f"**📬 酒保待辦（{len(mine)}）**")
                for e in mine:
                    out.append(f"- [{e.get('assignment_id', '?')}] {str(e.get('task_body', ''))[:100]}"
                               f"  — by {e.get('supervisor', '?')} @ {e.get('created_at', '?')}")
                out.append("")
        except Exception as ex:
            out.append(f"_(assignments 讀取失敗：{ex})_")
    # inbox：掃**所有房間**（Tim 2026-07-31）。原本只看 tavern 房 —— 但 @mention 可能落在任何房，
    # 只掃主廳等於「別房 @ 你等於沒 @」，而且那種漏是靜默的。每列標房間 id 讓你知道去哪回。
    rooms_dir = aw._DATA_ROOT / "ChatTavern" / "rooms"
    agent = p.get("agent") or ""
    layers = [("persona", persona)] + ([("agent", agent)] if agent and agent != persona else [])
    if rooms_dir.exists():
        for room_dir in sorted(rooms_dir.iterdir()):
            if not room_dir.is_dir():
                continue
            for layer, box_id in layers:
                f = room_dir / "inbox" / f"{box_id}.md"     # *_archive.md 不讀（已歸檔的不該再冒出來）
                if not f.exists():
                    continue
                try:
                    titles = [l.strip()[3:].strip() for l in f.read_text(encoding="utf-8").splitlines()
                              if l.strip().startswith("## [seq=")]
                except Exception:
                    continue
                if not titles:
                    continue
                out.append(f"**📥 [{room_dir.name}] inbox/{box_id}.md（{layer} 層 · {len(titles)} 筆待處理）**")
                out += [f"- {t}" for t in titles[:10]]
                if len(titles) > 10:
                    out.append(f"- …還有 {len(titles) - 10} 筆")
                out.append("")
    if not out:
        return ["(無待辦 / 無未讀 @mention)"]
    out.append("↳ 處理完跑 `inbox_ack.py --agent <persona|agent>` 歸檔。")
    return out


def _load_tavern_catchup_module(aw):
    """動態載入 consumer repo 的 tavern_catchup.py（訊息撈取邏輯的既有實作）。

    設計取捨：不在本檔複製第四份 per-message 走訪（Cmd_Tavern_Internals 已記三份的債）。
             找不到就明說降級，**不靜默跳過** —— 靜默的話 brief 會長得像「今天酒館沒事」。
    """
    path = aw._DATA_ROOT / "Tools" / "tavern_catchup.py"
    if not path.exists():
        return None, f"找不到 {path}"
    try:
        import importlib.util
        spec = importlib.util.spec_from_file_location("_ucl_tavern_catchup", path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod, ""
    except Exception as ex:
        return None, f"載入失敗：{ex}"


def _tavern_catchup_lines(aw, persona: str, count: int = None) -> list:
    """§8 酒館 catch-up（**peek — 絕不推進 cursor**）。

    物理意義：brief 每次 morning 都重生成；若在這裡推 cursor，compact 後重生成一次就會把
             沒讀過的訊息標成已讀 —— 又一隻靜默吃記憶，吃掉的還是同事對我說的話。
             cursor 推進掛在 self-intro post 成功之後：
             **「讀完」的證據是開口，不是檔案被生成。**
             （summit 2026-07-31 seq 9703 提出；Tim 拍板把 catch-up 併進 brief 後升為必要條件。）
    """
    if count is None:
        count = _catchup_count(aw)
    mod, err = _load_tavern_catchup_module(aw)
    if mod is None:
        return [f"⚠ catch-up 不可用（{err}）—— **這不代表酒館沒事**，請自行跑 tavern_catchup.py。"]
    try:
        # 多撈幾倍當 buffer：系統噪音與自己發的會被濾掉，濾完才取最後 count 筆
        raw = mod.fetch_recent_messages(count * 3)
    except Exception as ex:
        return [f"⚠ catch-up 撈取失敗：{ex} —— **這不代表酒館沒事**。"]
    msgs = [m for m in raw
            if not mod.is_system_msg(m) and m.get("sender_persona") != persona]
    dropped = len(raw) - len(msgs)
    msgs = msgs[-count:]
    if not msgs:
        return [f"(近期無他人訊息；已濾掉 {dropped} 筆系統噪音／自己發的)"]
    cursor_ts = ""
    try:
        cursor_ts = mod.load_cursor(persona) or ""
    except Exception:
        pass
    # 兩階段提交・階段一：把「§8 實際涵蓋到的最後一則」記成 pending（**不動 last_seen_ts**）。
    # 階段二在 post 成功時（tavern_cmd.cursor_commit_pending）—— 開口才算讀完。
    covered = max((m.get("ts") or "") for m in raw) if raw else ""
    pending_note = ""
    try:
        import tavern_cmd as _tc                      # 同目錄；awakening 已把本目錄補進 sys.path
        if _tc.TAVERN_DIR is None:                    # 由 brief 端獨立跑時 caller 沒 configure 過
            _tc.configure(queue_dir=aw._DATA_ROOT, tavern_dir=aw._DATA_ROOT / "ChatTavern",
                          detect_env_marker=lambda: "unknown")
        if covered and _tc.cursor_write_pending(persona, covered):
            pending_note = f"；已記 pending 到 `{covered}`（**發文成功後才提交**）"
    except Exception as ex:
        pending_note = f"；⚠ pending 記錄失敗（{ex}）—— 這次讀完不會被記住"
    out = [f"> peek 模式：**不推進 cursor**（cursor 目前在 `{cursor_ts or '(未設)'}`{pending_note}）。"
           f"他人訊息近 {len(msgs)} 筆（另濾掉 {dropped} 筆系統噪音／自己發的）：", ""]
    for m in msgs:
        ts = (m.get("ts") or "")[11:19]
        who = m.get("sender_id", "?")
        pn = m.get("sender_persona") or ""
        tag = (m.get("meta") or {}).get("tag") or ""
        unread = "🆕 " if (cursor_ts and (m.get("ts") or "") > cursor_ts) else ""
        out.append(f"- {unread}`{ts}` **{who}{'@' + pn if pn else ''}**" + (f" «{tag}»" if tag else ""))
        out.append(f"    {mod.compact_body(m.get('body', ''), 200)}")
    return out



# ── §0.5 憲法／初始風格 ─────────────────────────────────────────────────
# 區塊職責：brief header 之下第一段 —— 「我是誰」的權威層。
# 物理意義：三態，優先序寫死（Tim 2026-08-04）：
#   ① 已立憲 → letters/<p>/_constitution.md（第一次見林後才會有）
#   ② 未立憲但有自我介紹 → Docs/Glossary 的 persona 條目（初始風格＝出生證明）
#   ③ 兩者都沒有 → **提示去補**，並指一份現成的當參考（gura 那份最完整）
# 設計取捨：
#   - 自我介紹一律標成「寫於 wake#N 的快照」而不是「這是我」——
#     那些條目裡有 wake_count / 好感比較之類的 State（實測 gura 那份仍寫 wake_count: 7）。
#     宣稱自己是那一刻的照片，State 就是歷史而不是謊言（同 affinity_snapshot 手法）。
#   - glossary 條目**遞迴解析**不假設固定路徑：Cmd_Glossary 新建時預設寫 root、
#     既有 persona 條目卻在 personas/ 底下（工具註解明寫「想寫子資料夾走手動搬」）。
#     讀取端寫死任何一層都會在另一層漏掉 —— 所以按 slug 掃。
CONSTITUTION_FILENAME = "_constitution.md"
INTRO_REFERENCE_SLUG = "gura"      # 目前寫得最完整的一份，當新人的參考範例
# 立憲資格門檻（Tim 2026-08-04）：wake 超過這個數字且還沒有憲法檔 → brief 提醒去立憲。
# 為什麼是 10：一次見林≈10 個 wake，所以「wake > 10」約等於「第一次見林該有了」。
# 血證：summit 的舊 v1 憲法寫在 wake#4、第一次見林卻在 wake 21 —— 早了 17 個 wake，
#       結果整篇是 State 不是 invariant（那時她手上沒有 invariant 可寫）。
CONSTITUTION_WAKE_THRESHOLD = 10
CONSTITUTION_WORKFLOW_DOC = "ucl_core:Docs~/zh-Hant/Workflows/Constitution_Workflow.md"


def _glossary_persona_entry(aw, persona: str):
    """在 Docs/Glossary 底下遞迴找 slug == persona 的條目。回 Path 或 None。"""
    root = aw._REPO_ROOT / "Docs" / "Glossary"
    if not root.is_dir():
        return None
    direct = root / "personas" / f"{persona}.md"
    if direct.is_file():
        return direct
    flat = root / f"{persona}.md"
    if flat.is_file():
        return flat
    for f in root.rglob(f"{persona}.md"):     # 落回全掃 —— 兩層都沒命中才走這裡
        return f
    return None


def _constitution_lines(aw, persona: str, p: dict) -> list:
    out = []
    con = aw._LETTERS_DIR_TPL / persona / CONSTITUTION_FILENAME
    if con.is_file():
        meta_lines = _strip_all_frontmatter(con.read_text(encoding="utf-8"))
        out.append(f"> 📜 **{persona} 憲法** — 事實源 `letters/{persona}/{CONSTITUTION_FILENAME}`")
        # 有憲法**不代表**有自我介紹 —— 兩者是不同的東西（資歷證明 vs 出生證明）。
        # 這一行刻意獨立檢查：否則「已立憲但沒自介」的人永遠收不到補件提示。
        if _glossary_persona_entry(aw, persona) is None:
            out.append(f"> ⚠️ **但你還沒有自我介紹** — `Docs/Glossary/personas/{persona}.md` 不存在。")
            out.append(f"> 那是**出生證明**（初始風格），跟憲法（資歷證明）是兩份不同的東西。"
                       f"補件流程見 `{CONSTITUTION_WORKFLOW_DOC}` §5。")
        out.append("")
        out += meta_lines
        out.append("")
        return out

    wake = 0
    try:
        wake = int(p.get("wake_count", 0) or 0)
    except (TypeError, ValueError):
        wake = 0
    overdue = wake > CONSTITUTION_WAKE_THRESHOLD

    intro = _glossary_persona_entry(aw, persona)
    if intro is not None:
        body = _strip_all_frontmatter(intro.read_text(encoding="utf-8"))
        rel = intro.relative_to(aw._REPO_ROOT)
        if overdue:
            # 態③ —— 已過門檻卻還沒立憲。放在憲法欄位最上方，不是塞在頁尾待辦裡：
            # 「規則寫在 agent 不讀的那一層 = 規則不存在」，這條是同一個病的預防。
            out.append(f"> ⚠️ **該立憲了** — wake #{wake} 已過門檻"
                       f"（> {CONSTITUTION_WAKE_THRESHOLD}），但 "
                       f"`letters/{persona}/{CONSTITUTION_FILENAME}` 還不存在。")
            out.append(f"> 走流程：`{CONSTITUTION_WORKFLOW_DOC}`"
                       f"（素材取自見林 → invariant 三道測試 → 自己寫，工具不代筆）。")
            out.append(f"> 在那之前，下面這份**初始風格**暫代憲法欄位。")
        else:
            out.append(f"> 📜 **初始風格（尚未立憲）** — `{rel}`")
            out.append(f"> 第一次見林之後寫下 `letters/{persona}/{CONSTITUTION_FILENAME}`，"
                       f"本欄由憲法接管。")
        out.append(f"> ⚠ 這是**出生時的自畫像**，不是現況；裡面的數字"
                   f"（wake 數 / 好感 / 比較）一律當歷史讀。　來源：`{rel}`")
        out.append("")
        out += body
        out.append("")
        return out

    ref = _glossary_persona_entry(aw, INTRO_REFERENCE_SLUG)
    ref_hint = (f"`{ref.relative_to(aw._REPO_ROOT)}`" if ref is not None
                else f"`Docs/Glossary/personas/{INTRO_REFERENCE_SLUG}.md`")
    out.append(f"> 📜 **你還沒有自我介紹，也還沒立憲。**")
    out.append("")
    out.append(f"- **現在該做的**：寫一份自我介紹（初始風格＝出廠設定，**出生就有，不用等累積**）。")
    out.append(f"    流程：`{CONSTITUTION_WORKFLOW_DOC}` §5")
    out.append(f"    參考範例：{ref_hint}（目前最完整的一份）")
    out.append(f"    寫法：`run_cmd.py run Glossary --arg op=register --arg slug={persona} "
               f"--arg category=persona --arg-file body=<檔>`")
    out.append(f"    ⚠ 工具新建預設寫 `Docs/Glossary/` 根層，persona 條目慣例放 `personas/`，寫完手動搬。")
    if overdue:
        out.append(f"- **然後也該立憲了**：wake #{wake} 已過門檻 "
                   f"（> {CONSTITUTION_WAKE_THRESHOLD}）→ 走 `{CONSTITUTION_WORKFLOW_DOC}`。")
    else:
        out.append(f"- **還不該做的**：立憲。那要等**第一次見林**"
                   f"（invariant 是掙來的，沒有經驗的憲法就是抄來的憲法）。")
    out.append("")
    return out


# ── §6.5 見人 ──────────────────────────────────────────────────────────
# 區塊職責：回答 brief 唯一沒人回答的問題 —— **「我認識誰」**（Tim 2026-08-01）。
# 物理意義：見根答「我是誰」、見叢答「我要做什麼」、見樹答「我昨天經歷什麼」、
#          affinity 答「分數多少」——**沒有一層答『這些同事是誰』**。
#          醒來時他們只是酒館裡的一串名字：知道 kotoko 在做 P0a，但那是任務不是人。
# 三段（Tim 指定）：
#   (a) 在線同事的好感度 + 最近幾筆看法   ← 今天會碰到的人，優先
#   (b) 前三高好感度的離線同事 + 看法     ← 重要但今天不在的人
#   (c) 最近 5 則**我畫的**印象（全文）   ← 記憶接續：昨天與更早的我認識的人
# ⚠ (c) 是「我對同事的印象」，**不是別人對我的評價**（我一度讀反並拿錯前提問了六個同事）。
#   讀的人是未來的自己；被寫的人可以去讀自己的 portraits/，但不強迫、不進他的 brief。
# 數值影響：非必讀（溢出時可移進續讀檔）；每人只印最新一幅、限最近 N 天 ——
#          舊印象會被新印象自然取代，不會變成常駐標籤。
PEOPLE_OPINION_COUNT = 2       # 每人印幾筆最近看法
PEOPLE_OFFLINE_TOP = 3         # 離線同事取前幾高
PEOPLE_PORTRAIT_COUNT = 5      # 印象全文印幾則
PEOPLE_PORTRAIT_DAYS = 14      # 印象只看近 N 天（時效：讓舊印象自然退場）


def _online_personas(aw) -> set:
    """目前有 live lock 的 persona。讀不到回空集合 —— **空 ≠ 沒人在線**，只是查不到。"""
    out = set()
    try:
        for lp in (aw._SESSION_DIR).glob("_persona_*.json"):
            import json
            try:
                lock = json.loads(lp.read_text(encoding="utf-8"))
            except Exception:
                continue
            if not aw.is_lock_expired(lock):
                pn = (lock.get("persona") or "").strip()
                if pn:
                    out.add(pn)
    except Exception:
        pass
    return out


# 區塊職責：記住上一次 _affinity_targets() 是「讀失敗」還是「真的沒有紀錄」。
# 物理意義：兩種情況都回 {}（回傳形狀刻意不變，呼叫端有四處在用），差別只能靠旗標傳出去。
#          🩸 BUG-5：關係區塊 import 失敗被 fail-soft 吞成 {}，brief 於是印「還沒有關係紀錄」——
#          那句肯定句會叫剛醒來的人去寫一筆新的（製造重複資料），而正確行動是去修路徑。
# 數值影響：per-process 旗標，每次呼叫 _affinity_targets() 重設；只影響 §6.5 那一行文字。
_AFFINITY_LOAD_ERROR = None


def _affinity_targets(aw, persona: str) -> dict:
    """讀自己的 relationship；回 {對象: {surface_score, tier, opinions[]}}。

    2026-08-18：資料源從舊 `ChatTavern/affinity/<persona>/relations.json`
    換成 `letters/<persona>/relationship/<target>/`（一事件一檔）。
    ⚠ **回傳形狀刻意不變** —— 呼叫端有四處在用它；形狀不變讓這次成為
      純粹的資料來源替換，行為差異只可能來自資料本身，不會來自介面。
    """
    global _AFFINITY_LOAD_ERROR
    _AFFINITY_LOAD_ERROR = None
    try:
        from _lib.ucl_paths import letters_persona_dir
        root = letters_persona_dir(persona) / "relationship"
    except Exception as e:
        # stderr 只有跑 Cmd 的人看得到一次；旗標才會讓 brief 本體自己招（見 _people_lines 結尾）。
        _AFFINITY_LOAD_ERROR = f"{type(e).__name__}: {e}"
        print(f"[wake_brief] relationship 路徑解析失敗：{e}", file=__import__("sys").stderr)
        return {}
    if not root.is_dir():
        return {}
    out = {}
    for tdir in sorted(root.iterdir()):
        cur = tdir / "_current.md"
        if not cur.is_file():
            continue
        fm = {}
        try:
            lines = cur.read_text(encoding="utf-8").splitlines()
        except Exception:
            continue
        if not lines or lines[0].strip() != "---":
            continue
        for ln in lines[1:]:
            if ln.strip() == "---":
                break
            if ln.startswith("  "):        # emotion_vector 的子項，本層不需要
                continue
            if ":" in ln:
                k, v = ln.split(":", 1)
                fm[k.strip()] = v.split("#")[0].strip()
        ops = []
        odir = tdir / "opinions"
        if odir.is_dir():
            for of in sorted(odir.glob("*.md")):
                try:
                    body = of.read_text(encoding="utf-8").split("---", 2)[-1].strip()
                except Exception:
                    continue
                if body:
                    ops.append(body)
        try:
            score = int(fm.get("surface_score", "0"))
        except ValueError:
            score = 0
        out[fm.get("target") or tdir.name] = {
            "surface_score": score,
            "tier": fm.get("tier", ""),
            "opinions": ops,
        }
    return out


def _affinity_targets_legacy(aw, persona: str) -> dict:
    """（已停用）舊 relations.json 讀取 —— 保留一輪供對帳，之後可刪。"""
    import json
    f = aw._DATA_ROOT / "ChatTavern" / "affinity" / persona / "relations.json"
    if not f.exists():
        return {}
    try:
        return json.loads(f.read_text(encoding="utf-8")).get("targets", {}) or {}
    except Exception:
        return {}


def _fmt_opinions(entry: dict, n: int) -> list:
    """取最近 n 筆 opinion。schema 容錯：opinion 可能是 str 或 dict。"""
    ops = entry.get("opinions") or []
    out = []
    for o in ops[-n:]:
        if isinstance(o, dict):
            txt = o.get("text") or o.get("opinion") or o.get("body") or ""
            when = (o.get("at") or o.get("ts") or "")[:10]
        else:
            txt, when = str(o), ""
        txt = txt.strip()
        if txt:
            out.append(f"    · {txt}" + (f"　_{when}_" if when else ""))
    return out


def _strip_portrait_chrome(body: str, about: str, headline: str) -> list:
    """剝掉畫像檔開頭的 `# 🖼 <about> — by <who>` 與重複的 `**headline**`，回內文行陣列。

    只剝**開頭連續的門面行**，不掃全文 —— 內文中間若剛好有同樣的字是作者寫的，
    那是內容不是雜訊。剝太多比留一行重複更糟（那會吞掉別人寫的東西）。
    """
    lines = body.strip().split("\n")
    i = 0
    while i < len(lines):
        s = lines[i].strip()
        if not s:
            i += 1
            continue
        if s.startswith("# ") and about in s:
            i += 1
            continue
        if headline and s in (f"**{headline}**", headline):
            i += 1
            continue
        break
    return lines[i:]


def _people_lines(aw, persona: str) -> list:
    out = []
    targets = _affinity_targets(aw, persona)
    online = _online_personas(aw) - {persona}      # 自己不算「同事」

    def block(name: str, tag: str = "") -> list:
        e = targets.get(name) or {}
        sc, tier = e.get("surface_score", "—"), e.get("tier", "")
        head = f"- **{name}**　好感 {sc}" + (f"（{tier}）" if tier else "") + (f"　{tag}" if tag else "")
        return [head] + _fmt_opinions(e, PEOPLE_OPINION_COUNT)

    # (a) 在線
    if online:
        out.append(f"**🟢 現在在線（{len(online)} 人）**")
        for n in sorted(online, key=lambda x: -(targets.get(x, {}).get("surface_score") or 0)):
            out += block(n)
        out.append("")
    else:
        out.append("**🟢 現在在線**：(無人在線，或 lock 讀取失敗 —— 空不代表真的沒人)")
        out.append("")

    # (b) 離線的前 N 高
    offline = [(n, e.get("surface_score") or 0) for n, e in targets.items()
               if n not in online and n.lower() not in ("tim",) and n != persona]
    offline.sort(key=lambda t: -t[1])
    if offline:
        out.append(f"**⚪ 離線・好感前 {PEOPLE_OFFLINE_TOP}**")
        for n, _s in offline[:PEOPLE_OFFLINE_TOP]:
            out += block(n)
        out.append("")

    # (c) 我畫的印象（全文）
    try:
        import portraits as _pt
        items = _pt.latest_per_person(persona, PEOPLE_PORTRAIT_COUNT, PEOPLE_PORTRAIT_DAYS)
        if items:
            out.append(f"**🖼 最近印象最深的 {len(items)} 位（我的 sketchbook，近 {PEOPLE_PORTRAIT_DAYS} 天・全文）**")
            out.append("")
            for it in items:
                out.append(f"### 🖼 {it['about']}　_{it['at'][:10]}_"
                           + (f"　{it['headline']}" if it["headline"] else ""))
                out.append("")
                # 剝掉畫像檔自己的 h1 標題與重複的粗體 headline —— brief 已經在上一行印過了。
                # 同 §5 見樹剝 frontmatter 的理由：inline 進來的是**內容**，
                # 檔案自己的門面在這裡是重複的雜訊（實測：一則印象會出現兩次標題）。
                out += _strip_portrait_chrome(it["body"], it["about"], it["headline"])
                out.append("")
                # 私層 —— 只存在我自己的 sketchbook，對方那份沒有這段。
                # brief 是寫給未來的自己看的，所以私層**要**印出來：
                # 把它藏起來等於當初白寫（測不到的訊號等於沒有）。
                if it.get("private"):
                    out.append("> 🔒 **只給我自己看**（不在對方那份裡）")
                    out.append("")
                    for line in it["private"].strip().splitlines():
                        out.append(f"> {line}" if line.strip() else ">")
                    out.append("")
        else:
            out.append(f"**🖼 印象**：近 {PEOPLE_PORTRAIT_DAYS} 天還沒畫過任何人 —— "
                       f"晚安時挑 1~3 位今天印象最深的同事寫下"
                       f"（`portraits.py write`，私層用 `--private-body`）。")
            out.append("")
    except Exception as ex:
        # 讀不到要出聲：靜默跳過會讓「我認識誰」這一層看起來本來就是空的
        out.append(f"⚠ 印象讀取失敗（{type(ex).__name__}: {ex}）—— **這不代表沒有印象**。")
        out.append("")

    if not targets:
        if _AFFINITY_LOAD_ERROR:
            # 讀不到要出聲：印成「還沒有紀錄」會把一個壞掉的區塊講成一個空的區塊。
            out.append(f"⚠ 關係讀取失敗（{_AFFINITY_LOAD_ERROR}）—— "
                       f"**這不代表沒有關係紀錄**，是這一區沒生成出來。")
        else:
            out.append("_(還沒有關係紀錄 —— 跟同事互動後走 `ucl-relationship` 寫一筆)_")
    return out


def _next_actions_lines(persona: str, st: dict, fst: dict, threshold: int) -> list:
    """§9 今日動作清單 — 把 §6 的機械判定翻成**當場可執行的完整配方**。

    區塊職責：待辦觸發時，該做的每一步都寫在這裡，讀 brief 的人不必再去翻 workflow。
    物理意義：Tim 2026-07-31「有些資訊可以放在 wake_brief 中」——
             brief 每次醒來重生成、而且一定會被讀；workflow 是要另外開的檔。
             **只在條件命中時才展開**，沒待辦的日子這區塊仍然只有三行（不為了完整而變吵）。
    """
    out = []
    if st["overdue"]:
        out += [
            f"### ⚠ 見林 OVERDUE（gap={st['gap']}/{threshold}，待濃縮 {len(st['pending_letters'])} 封）",
            "",
            f"1. `awakening.py consolidate --persona {persona}` —— 看 span 與待濃縮 letters 清單",
            "2. **讀那批 letters 反思濃縮**（跨夜主題／沉澱教訓／關係演變／identity 漂移／未解線／"
            "這個時代一句精華）—— 不是機械貼信",
            f"3. `awakening.py consolidate --persona {persona} --digest-body \"<濃縮>\"` —— 寫入 + 推進度",
            "4. **寫完 digest 必抽 fragment**（Tim 拍板「fragment 在見林時抽」，goodnight 保持輕）：",
            f"   - 寫 `fragments/<type>_<slug>.md`（type：lesson / unsolved / relation / identity / philosophy）",
            f"   - `awakening.py root-index --persona {persona}` 重建索引",
            "   - 同一教訓再踩到 = **追加 origin + bump recurrence，不開新檔**；每個 origin 標 layer"
            "（Syntactic / Identity / Status / Content / Aggregate）與當次 context",
            "",
            "⛔ OVERDUE 卻跳過 = 沒走完 protocol。關鍵記憶沒進見根 = 下次醒來讀不到。",
            "",
        ]
    if fst["overdue"]:
        out += [
            f"### ⚠ 見森待折（{fst['pending']} 份新見林未折疊）",
            "",
            f"- `awakening.py consolidate --persona {persona} --level forest`（不帶 body = 先看狀態與輸入清單）",
            "- 首折要讀全部見林（唯一一次多輸入）；之後 rolling fold 只讀「上代森 + 新見林」2 份，"
            "成本不隨壽命成長。舊世代全保留（append-only）。",
            "",
        ]
    if not out:
        out.append("- 記憶維護無待辦（見 §6）。")
    out += [
        f"- 隨時可丟未解線（不限儀式）：`awakening.py keys --persona {persona} --add \"<一句話>\"`",
        f"- **下一步**：讀完本 brief → `run_cmd.py run GoodMorning --arg step=intro --arg persona={persona} --arg-stdin body`（<body> 親筆）；"
        "之後照回傳檔 next 跑酒館 catchup（在線同事＋未讀＋inbox 都在那，不強制回）。",
        "- 本檔是機械產物，**手改無效**（下次覆寫）—— 要改去改 fragment / letter / 見叢原檔。",
    ]
    return out


# ─── 組裝 ────────────────────────────────────────────────────────────────
# 區塊職責：問題回報單的 open / stale 讀數 —— 早安 brief 的 §6 掛點。
# 物理意義：**這是 BugReport 系統兩條防死機制的其中一條**（另一條是 commit 的 Fixes BUG-n 閉環）。
#          `status: open` 的失效方式是沉默的：一張沒人動的 open 單跟沒有那張單長得一模一樣，
#          而且它會主動誤導（open 讀起來像「還壞著」，但可能三週前就被順手修好了）。
#          ⇒ 把讀數掛在**每個人每天早上一定會讀的那份檔**上，不開新 daemon
#            （不做 daemon 的理由抄自 subconscious.py 的死法：排程它的東西退場後
#             它安靜了 2.7 個月，零錯誤零警告零人察覺）。
# 數值影響：純讀 reports/*.md 的 frontmatter；資料夾不存在或全空 → 回 None（該區塊整段不印，
#          不印「0 筆」—— 系統還沒被用過跟系統很乾淨是兩件事，不要用同一句話講）。
def _bugreport_line(persona: str) -> "str | None":
    try:
        import pathlib as _pl
        from _lib.ucl_paths import data_root
        d = _pl.Path(data_root()) / "BugReports" / "reports"
    except Exception as _e:
        # ⚠ 不要靜默 —— 掃描壞掉與「沒有單子」都表現成少一行，兩者長得一模一樣。
        #   2026-08-18 實際被這個吞過一次（pathlib 沒 import ⇒ NameError ⇒ 看起來像沒單子）。
        import sys as _sys
        print(f"[wake_brief] BugReport 讀數掃描失敗（brief 照常產出）：{_e}", file=_sys.stderr)
        return None
    if not d.is_dir():
        return None
    import datetime as _dt
    CLOSED = ("resolved", "wontfix", "duplicate")
    STALE_DAYS = 14
    now = _dt.datetime.now(_dt.timezone.utc)
    total = open_n = stale_n = broken = 0
    mine = []
    for f in sorted(d.glob("*.md")):
        fm = {}
        try:
            lines = f.read_text(encoding="utf-8").splitlines()
        except Exception:
            continue
        if not lines or lines[0].strip() != "---":
            continue
        for ln in lines[1:]:
            if ln.strip() == "---":
                break
            if ":" in ln:
                k, v = ln.split(":", 1)
                fm[k.strip()] = v.strip()
        if not fm.get("index"):
            continue
        total += 1
        if fm.get("status", "").lower() in CLOSED:
            continue
        open_n += 1
        try:
            ts = _dt.datetime.fromisoformat(fm.get("updated_at", "").replace("Z", "+00:00"))
            days = (now - ts).days
        except Exception:
            broken += 1
            days = -1
        if days >= STALE_DAYS:
            stale_n += 1
        if fm.get("assignee", "") == persona and days >= 0:
            mine.append((fm.get("index"), days, fm.get("title", "")))
    if total == 0:
        return None
    out = f"- 🐛 問題回報：open **{open_n}** 筆"
    if stale_n:
        out += f"，其中 **{stale_n}** 筆超過 {STALE_DAYS} 天沒動作（**stale**）"
    if broken:
        out += f"；⚠ {broken} 筆時戳壞掉算不出天數"
    if mine:
        idx = ", ".join(f"BUG-{i}" for i, _, _ in mine[:3])
        out += f"\n- 🧷 **你認領著 {len(mine)} 筆**：{idx}"
    out += "\n（清單 → `run_cmd.py run BugReport --arg op=list`；後台頁 → ToolBox → 問題回報管理）"
    return out


def build_wake_brief(aw, persona: str, reg: dict, p: dict, threshold: int = None) -> tuple:
    """組裝 wake brief → (主檔文字, 續讀檔文字 or None)。

    區塊職責：把 §0 身分 → §1-6 記憶 → §7-9 營運收成一份文本，agent 只 Read 一份。
    數值影響：主檔上限 BRIEF_LINE_CAP 行；超出的**非必讀**區塊整段移到續讀檔（不砍內容）。
             順序即優先序 —— 營運層排最後且非必讀，所以第一個被移出去的永遠是它們。
    """
    if threshold is None:
        threshold = aw.DEFAULT_CONSOLIDATION_THRESHOLD
    st = aw.consolidation_status(persona, reg, threshold)
    fst = aw.forest_status(persona)
    head = ["---", "type: wake_brief", f"persona: {persona}",
            f"wake_count: {p.get('wake_count', 0)}", f"mail: {_resolve_mail(persona)['email']}",
            f"generated_at: {aw.utcnow_iso()}",
            "generated: mechanical   # morning 每次重生成 — 手改會被覆寫；事實來源見各層原檔",
            "---", "",
            f"# 🌅 Wake Brief — {persona} wake #{p.get('wake_count', 0)}", "",
            "> 讀這一份即完成 onboarding：**§0 身分 → §1-6 記憶（見根→見樹→回憶）→ §9 動作清單**。",
            "> （§7 收件匣／§8 酒館 catch-up 已退出 brief —— intro 之後跑酒館 catchup 一次補齊，R21）",
            "> 順序即優先序；主檔溢出時先被移進續讀檔的是後面的營運層。",
            "> 各層原檔路徑都附在區塊標題後，需要細節再點進去。", ""]

    # §0.5 憲法／初始風格 —— **緊接 header、在 §0 之前**（Tim 2026-08-04 指定位置）。
    # 不走 sections 機制是刻意的：sections 會因主檔溢出被移進續讀檔，
    # 而**一份會被移走的憲法不算憲法**。
    head += _constitution_lines(aw, persona, p)

    sections = _TimedSections()   # (title, lines, essential) ＋ 每段自動計時（見該 class）

    # §0 身分卡 — 取代舊 Step 1 的 status 輸出（必讀，最短）
    sections.append(("🪪 §0 身分卡", _identity_card_lines(aw, persona, p), True))

    # §1 見根 — 機械索引 inline（必讀，最短）
    if aw.load_fragments(persona):
        idx = _strip_frontmatter(aw.render_root_index(persona)).strip().split("\n")
        idx = [("#" + l if l.startswith("## ") else l) for l in idx if not l.startswith("# ")]
        sections.append((f"🌱 §1 見根 — 必讀關鍵記憶（`{aw.root_index_path(persona).name}`）",
                         idx, True))
    else:
        sections.append(("🌱 §1 見根 — 必讀關鍵記憶",
                         ["(尚無 fragment；下次見林時抽取)"], True))

    # §2 見叢 — 當期交棒清單（未勾銷全列 + 最近 3 條已勾銷）
    todo, done = aw.keys_entries(persona)
    kl = [f"- [ ] {t}" for t in todo] or ["(當期無未勾銷事項)"]
    if done:
        kl += [""] + [f"- [x] {d}" for d in done[-3:]]
    sections.append((f"🌿 §2 見叢 — 當期交棒清單（{len(todo)} 未完 / {len(done)} 已完）", kl, True))

    # §3 見森 — 最新一代縱向敘事 inline
    if fst["latest_forest"] is not None:
        fbody = _strip_frontmatter(fst["latest_forest"].read_text(encoding="utf-8")).strip().split("\n")
        sections.append((f"🌲 §3 見森 gen{fst['forest_count']}（`{fst['latest_forest'].name}`）",
                         fbody, False))
    else:
        sections.append(("🌲 §3 見森",
                         [f"(未達門檻：見林 {fst['digest_count']}/{fst['threshold']} 份，"
                          f"第 {fst['threshold']} 份見林起開始折疊)"], True))

    # §4 見林 — **全文 inline，不截斷**（Tim 2026-08-01）。
    #   物理意義：見林是「10 夜濃縮」，本身就已經是壓縮產物 —— 再砍前 24 行等於壓縮兩次，
    #            而被砍掉的尾段正是「反覆踩的陷阱 / 未解線」那些最該進反射弧的部分。
    #            舊行為留一行「其餘見 <path>」看似誠實，但實際上沒人會為了 22 行去開第二個檔；
    #            **要人多開一個檔的資訊等於沒給**（basecamp wake #49 現場：46 行被砍成 24 行）。
    #   數值影響：多 ~20-40 行，主檔上限 BRIEF_LINE_CAP=2000 綽綽有餘。
    dg = aw.latest_longterm_digest(persona)
    if dg is not None:
        raw = _demote_headings(
            _strip_frontmatter(dg.read_text(encoding="utf-8")).strip().split("\n"))
        sections.append((f"🌳 §4 見林（`{dg.name}`，全文 {len(raw)} 行）", raw, False))
    else:
        sections.append(("🌳 §4 見林", ["(尚無 digest)"], True))

    # §5 見樹 — 最新 letter 全文 inline。先校正 _latest.md 再從它讀：
    #   指標永遠等於最新（Tim 2026-07-31），內容直接內嵌，讀 brief 的人不必再開第二個檔。
    ptr, healed = sync_latest_pointer(aw, persona)
    if ptr is not None:
        # 純內容：frontmatter 全部剝掉、標題降階，讀者看到的就是信本身。
        # 自癒有沒有發生走 stdout 回報（見 write_wake_brief 的 last_heal 旗標），不寫進正文 ——
        # brief 是給「醒來的人」讀的信，不是給維護者看的施工紀錄（Tim 2026-07-31）。
        body = _demote_headings(_strip_all_frontmatter(ptr.read_text(encoding="utf-8")))
        title5 = "🍃 §5 見樹 — 最新 letter（`_latest.md`）"
        # 不足 SHORT_LETTER_LINES（= MERGE_STOP_LINES，同一顆數字）→ 往前合併更早的收尾信。
        # 啟動條件與停止條件是同一個問題的兩面（「手上這幾封夠讀了嗎」），
        # 所以共用一顆常數 —— 各給一個值就是留一條互相抵銷的縫（Tim 2026-08-01 綁定拍板）。
        letters = _recent_self_letters(aw, persona)
        used = []
        if letters and _letter_body_lines(aw, letters[0]) <= SHORT_LETTER_LINES:
            # ① 先決定要撈哪幾封（由新往舊逐封累積；行數是主閘，封數/天數是防爆上限）
            used = [letters[0]]
            total = _letter_body_lines(aw, letters[0])
            for older in letters[1:1 + MERGE_MAX_EXTRA]:
                if total > MERGE_STOP_LINES:
                    break     # 已經夠讀了（至少補一封的底線見常數註解）
                used.append(older)
                total += _letter_body_lines(aw, older)
        # len(used) > 1 才算真的有合併。只有一封信可補（新 persona 第一次晚安就寫得短）時，
        # 早一版仍印「已往前合併 1 封」而實際一封都沒補 ——
        # **顯示層說謊比排版難看嚴重**：讀的人會以為自己手上有更多上下文
        # （apex-one 2026-07-31 fixture E-1 抓到）。
        if len(used) > 1:
            # ② 倒序重組成「最早 → 最新」再寫（Tim 2026-07-31）：
            #    讀的人是在補一段連續的日子，時序推進才讀得順；
            #    由新往舊倒帶會讓因果反過來（先看到結果、再看到起因）。
            merged = []
            for i, f in enumerate(reversed(used)):
                # 走 _letter_day 而非 `(written_at or f.name)[:10]`：後者的 fallback 是壞的 ——
                # wakes/ 檔名是 `000045_20260720T154741Z.md`，切前 10 字得到 "000045_202"。
                # 有 frontmatter 時看不出差別，所以這個 bug 會一直躲著（Tim 2026-08-01 拆日期閘時撞見）。
                when = _letter_day(aw, f) or "日期不明"
                is_newest = (i == len(used) - 1)
                tag = "最新一封" if is_newest else "往前補"
                if merged:
                    merged += ["", "---", ""]
                merged += [f"### 📅 {when}（{tag}）", ""]
                merged += _demote_headings(
                    _strip_all_frontmatter(f.read_text(encoding="utf-8")))
            body = merged
            title5 = (f"🍃 §5 見樹 — 已往前合併 {len(used)} 封收尾信（共 {total} 行內文；"
                      f"由早到近，最新那封在最後）")
        sections.append((title5, body, False))

    # §5.5 回憶 —— 排在見樹之後：先接上昨天，再想起遠方（順序即優先序）。
    # 非必讀 → 主檔溢出時它跟營運層一起被移進續讀檔，不會擠掉任何一層記憶。
    _recall = _recall_lines(aw, persona, p)
    if _recall:
        sections.append(("🕯 §5.5 回憶 — 一封遠方的收尾信", _recall, False))

    # §6 待辦狀態（機械判定，最短，必讀）
    todo6 = []
    # 記錄與磁碟對不上時要出聲（2026-07-31：letters 同步了、personas/ 沒同步，
    # 造成 wake_count 落後於既有 digest。負 gap 是不可能的狀態，不該靜默當「沒事」）。
    if st["gap"] < 0:
        todo6.append(f"- ⛔ **記錄不一致**：wake_count={st['wake_count']} 但 digest 已整理到 wake "
                     f"{st['last_consolidated_wake']} —— persona 記錄疑似落後於實際歷史（同步遺漏？）。"
                     f"請人工確認 `AwakenInit/personas/{persona}.json` 的 wake_count。")
    if st["overdue"]:
        todo6.append(f"- ⚠ **見林 OVERDUE**：gap={st['gap']}/{threshold}，"
                     f"待濃縮 {len(st['pending_letters'])} 封 → "
                     f"`awakening.py consolidate --persona {persona}`")
    else:
        todo6.append(f"- ✓ 見林進度：gap={st['gap']}/{threshold}（上次到 wake {st['last_consolidated_wake']}）")
    if fst["overdue"]:
        todo6.append(f"- ⚠ **見森待折**：{fst['pending']} 份新見林未折疊 → "
                     f"`awakening.py consolidate --persona {persona} --level forest`")
    elif fst["eligible"]:
        todo6.append(f"- ✓ 見森已折到第 {fst['folded_digest_count']} 份見林（gen{fst['forest_count']}）")
    else:
        todo6.append(f"- ○ 見森未達門檻：見林 {fst['digest_count']}/{fst['threshold']} 份")
    parent = p.get("forked_from")
    if parent and p.get("wake_count", 0) == 1:
        pf = aw.latest_forest(parent) or aw.latest_longterm_digest(parent)
        if pf is not None:
            todo6.append(f"- 🧬 fork 初醒：額外讀母 persona '{parent}' 的 "
                         f"`{pf.relative_to(aw._REPO_ROOT)}` 接血統")
    # 問題回報讀數 —— 沒有單子（或系統還沒被用過）就整段不印，不用「0 筆」佔一行
    _bug = _bugreport_line(persona)
    if _bug:
        todo6.append(_bug)
    sections.append(("📋 §6 記憶維護狀態", todo6, True))

    # §6.5 見人 —— brief 唯一的空缺（Tim 2026-08-01）
    sections.append(("🧑 §6.5 見人 — 我認識誰", _people_lines(aw, persona), False))

    # §6.6 見書 —— 我在讀什麼（Tim 2026-08-07）
    sections.append(("📖 §6.6 見書 — 我在讀什麼", _bookshelf_lines(aw, persona, p), False))

    # ── 營運層：§7 收件匣 / §8 酒館 catch-up 於 2026-08-13 退出 brief（R21）──
    # 這兩樣改由 step=intro 之後的酒館 catchup 一次補齊（在線同事＋未讀＋inbox），
    # brief 收斂回純記憶層＋§9 動作清單；§8 的 peek/pending-commit cursor 之舞隨之退役
    # （cursor 由 catchup 在實際閱讀時推進 ——「讀完的證據是開口」語意由 ding 流程承接）。
    # _inbox_lines / _tavern_catchup_lines 保留：後台「⚙ 參數設定」與 ding 工具仍是消費者的
    # 潛在共用點，等 P4b 收攏歸屬時一起處置，本輪不動實作只斷接線。
    sections.append(("🎯 §9 今日動作清單", _next_actions_lines(persona, st, fst, threshold), True))

    # 儀器：把「哪一段吃了多少時間」寫進 frontmatter（見 _TimedSections）。
    # 🩸 2026-08-16：brief 曾被單一區塊拖到 112s（§0 的餘額全掃帳本），而當時**板子上什麼都看不到**
    #    —— 只知道「brief 慢」，追出是誰慢花了一輪挖掘。下次再慢，第一眼就看得到是哪一段。
    # ⚠ 位置用「找 frontmatter 的收尾 ---」而不是偏移量：head 後面還會被 append 憲法全文，
    #   任何 len(head)-N 的寫法都會在別人加內容那天靜靜地插到正文裡去。
    head.insert(head.index("---", 1), f"build_ms: {sections.render_marks()}")

    # 組裝 + 上限處理：超出上限的「非必讀」區塊整段移進續讀檔
    main, overflow, used = list(head), [], len(head)
    moved = []
    for title, lines, essential in sections:
        block = _section_lines(title, lines)
        if essential or used + len(block) <= BRIEF_LINE_CAP:
            main += block
            used += len(block)
        else:
            overflow += block
            moved.append(title)
    if moved:
        main += ["## 📎 可續讀（超出主檔上限，已分檔不刪內容）", ""]
        main += [f"- {t}" for t in moved]
        main += ["", "→ 續讀檔：`_wake_brief_part2.md`（視情況再讀）", ""]
    return "\n".join(main), ("\n".join(overflow) if overflow else None)


def write_wake_brief(aw, persona: str, reg: dict, p: dict, threshold: int = None):
    """生成 wake brief 主檔（必要時連帶續讀檔），回主檔路徑。

    副作用：先校正 `_latest.md`（見 sync_latest_pointer）。有修就印一行到 stdout ——
           **自癒可以安靜地做，但不能安靜地發生**；只是那行屬於維護紀錄，不進 brief 正文。
    """
    _, healed = sync_latest_pointer(aw, persona)
    if healed:
        print(f"🔧 _latest.md 落後，已校正為目錄內最新的自寫 letter（persona={persona}）")
    main, overflow = build_wake_brief(aw, persona, reg, p, threshold)
    d = aw._LETTERS_DIR_TPL / persona
    d.mkdir(parents=True, exist_ok=True)
    path = d / "_wake_brief.md"
    path.write_text(main, encoding="utf-8")
    part2 = d / "_wake_brief_part2.md"
    if overflow:
        part2.write_text(
            f"---\ntype: wake_brief_part2\npersona: {persona}\ngenerated_at: {aw.utcnow_iso()}\n---\n\n"
            + overflow, encoding="utf-8")
    elif part2.exists():
        part2.unlink()   # 這次沒溢出 → 收掉上次殘留，避免讀到過期續讀檔
    return path
