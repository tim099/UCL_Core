#!/usr/bin/env python3
# 區塊職責：wake brief 的**唯一生成點** —— 把五層記憶（見根→見樹）與營運層（收件匣 / 酒館 /
#          動作清單）組裝成一份 `_wake_brief.md`，讓 agent 醒來只 Read 一份就完成 onboarding。
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
# 短信往前合併的三個參數（Tim 2026-07-31）
#   物理意義：一封 3 行的「今天沒什麼事，晚安」不足以撐起明天的接續 —— 那不是記憶，是簽到。
#            與其讓醒來的人讀完一句話就沒了，不如把前幾天一起端上來。
#   數值影響：只影響 §5 顯示，不動任何檔案；合併來源一律是收尾信本身，不重新詮釋內容。
SHORT_LETTER_LINES = 10      # 少於這個行數 → 判定「太短」，啟動往前合併
MERGE_MAX_EXTRA = 4          # 最多往前再撈幾封（不含最新那封）
MERGE_STOP_LINES = 100       # 累積超過這個行數就停 —— 目的是補足，不是把整本日記搬過來


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


def _recent_self_letters(aw, persona, limit=None):
    """該 persona 的自寫信，**新到舊**排序（頂層 + wakes/，去重）。"""
    d = aw._LETTERS_DIR_TPL / persona
    if not d.exists():
        return []
    toplevel = {f.name for f in d.iterdir() if f.is_file() and f.suffix == ".md"}
    items = []
    for f in list(d.iterdir()) + aw.list_wake_letters(persona):
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
    for f in list(d.iterdir()) + aw.list_wake_letters(persona):
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


# ─── §0 / §7 / §8 / §9 區塊 ─────────────────────────────────────────────
def _identity_card_lines(aw, persona: str, p: dict) -> list:
    """§0 身分卡 — 取代舊 morning Step 1 的 `awakening.py status` 輸出。"""
    lock = aw.read_lock(persona)
    bank = (lock or {}).get("bank_account") or p.get("bank_account") or ""
    lines = [
        f"- **persona**：`{persona}` — wake #{p.get('wake_count', 0)}",
        f"- **agent**：`{p.get('agent', '?')}`（由 persona 綁定反推）",
    ]
    if bank:
        try:
            lines.append(f"- **bank**：`{bank}`（餘額 {aw.get_treasury_balance(bank)} tavern_token）")
        except Exception:
            lines.append(f"- **bank**：`{bank}`")
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
    out = [f"> peek 模式：**不推進 cursor**（cursor 目前在 `{cursor_ts or '(未設)'}`，"
           f"推進掛在 self-intro post 之後）。他人訊息近 {len(msgs)} 筆"
           f"（另濾掉 {dropped} 筆系統噪音／自己發的）：", ""]
    for m in msgs:
        ts = (m.get("ts") or "")[11:19]
        who = m.get("sender_id", "?")
        pn = m.get("sender_persona") or ""
        tag = (m.get("meta") or {}).get("tag") or ""
        unread = "🆕 " if (cursor_ts and (m.get("ts") or "") > cursor_ts) else ""
        out.append(f"- {unread}`{ts}` **{who}{'@' + pn if pn else ''}**" + (f" «{tag}»" if tag else ""))
        out.append(f"    {mod.compact_body(m.get('body', ''), 200)}")
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
        "- **下一步**：讀完本 brief → 走酒館 self-intro post（`--arg persona` 必帶）；"
        "post 成功後才推 §8 的 catch-up cursor。",
        "- 本檔是機械產物，**手改無效**（下次覆寫）—— 要改去改 fragment / letter / 見叢原檔。",
    ]
    return out


# ─── 組裝 ────────────────────────────────────────────────────────────────
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
            f"wake_count: {p.get('wake_count', 0)}", f"generated_at: {aw.utcnow_iso()}",
            "generated: mechanical   # morning 每次重生成 — 手改會被覆寫；事實來源見各層原檔",
            "---", "",
            f"# 🌅 Wake Brief — {persona} wake #{p.get('wake_count', 0)}", "",
            "> 讀這一份即完成 onboarding：**§0 身分 → §1-6 記憶（見根→見樹）→ §7-9 營運**。",
            "> 順序即優先序；主檔溢出時先被移進續讀檔的是後面的營運層。",
            "> 各層原檔路徑都附在區塊標題後，需要細節再點進去。", ""]

    sections = []   # (title, lines, essential)

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

    # §4 見林 — 摘要 + 路徑（全文太長，細節已被 fragment 抽走）
    dg = aw.latest_longterm_digest(persona)
    if dg is not None:
        raw = _demote_headings(
            _strip_frontmatter(dg.read_text(encoding="utf-8")).strip().split("\n"))
        head_part = raw[:24]
        tail_note = (f"…（全文 {len(raw)} 行，其餘見 `{dg.relative_to(aw._REPO_ROOT)}`）"
                     if len(raw) > 24 else "")
        sections.append((f"🌳 §4 見林（`{dg.name}`）",
                         head_part + ([tail_note] if tail_note else []), False))
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
        # 最新那封太短 → 往前合併更早的收尾信（Tim 2026-07-31）。
        # 「今天沒什麼事，晚安」這種簽到式的信撐不起明天的接續，補到夠讀為止。
        letters = _recent_self_letters(aw, persona)
        if letters and _letter_body_lines(aw, letters[0]) < SHORT_LETTER_LINES:
            # ① 先決定要撈哪幾封（由新往舊逐封累積，行數決定停在哪）
            used = [letters[0]]
            total = _letter_body_lines(aw, letters[0])
            for older in letters[1:1 + MERGE_MAX_EXTRA]:
                used.append(older)
                total += _letter_body_lines(aw, older)
                if total > MERGE_STOP_LINES:
                    break     # 補足即止 —— 目的是讓明天讀得下去，不是搬整本日記
            # ② 倒序重組成「最早 → 最新」再寫（Tim 2026-07-31）：
            #    讀的人是在補一段連續的日子，時序推進才讀得順；
            #    由新往舊倒帶會讓因果反過來（先看到結果、再看到起因）。
            merged = []
            for i, f in enumerate(reversed(used)):
                when = (aw._read_frontmatter_field(f, "written_at") or f.name)[:10]
                is_newest = (i == len(used) - 1)
                tag = "最新一封" if is_newest else "往前補"
                if merged:
                    merged += ["", "---", ""]
                merged += [f"### 📅 {when}（{tag}）", ""]
                merged += _demote_headings(
                    _strip_all_frontmatter(f.read_text(encoding="utf-8")))
            body = merged
            title5 = (f"🍃 §5 見樹 — 最新 letter 太短，已往前合併 {len(used)} 封"
                      f"（由早到近；最新那封在最後）")
        sections.append((title5, body, False))

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
    sections.append(("📋 §6 記憶維護狀態", todo6, True))

    # ── 營運層（§7-§9）：Tim 2026-07-31 R5 —— 併進同一份，但排在記憶層之後 ──
    sections.append(("📥 §7 待辦收件匣", _inbox_lines(aw, persona, p), False))
    sections.append(("🍺 §8 酒館 catch-up（peek，不推進 cursor）",
                     _tavern_catchup_lines(aw, persona), False))
    sections.append(("🎯 §9 今日動作清單", _next_actions_lines(persona, st, fst, threshold), True))

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
