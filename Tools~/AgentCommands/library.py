#!/usr/bin/env python3
"""
Reading Library CLI — 記錄閱讀心得 / 人物看法 / 章節摘要的圖書館系統。

設計哲學（刻意與本專案既有系統同構）:
  - 人物看法版本史  ≈ affinity 的 opinion history（看法改觀 = 記新版, 絕不覆寫舊版）
  - 改觀 fork 新版本 ≈ persona fork（保留「過去的看法」, 像保留過去的自己）
  讀者因此能回溯「我對這個人的看法, 是怎麼一章章演變的」。

儲存佈局: AgentCommands/BookNotes/<book-slug>/  (資料夾名避開 Unity 的 Library/ ignore)
  book.json                 書本元資料 + 進度 + 人物索引
  chapters/chNN_<slug>.md   每章: 摘要 / 關鍵事件 / 新認識 / 伏筆
  characters/<cid>/
    _profile.json           人物索引: current_version + versions[]（版本目錄）
    vN_<date>.md            看法快照（改觀 = 新檔, 不覆寫）

Usage:
  python AgentCommands/Tools/library.py add-book --id jonathan-strange-mr-norrell \
      --title 英倫魔法師 --title-original "Jonathan Strange & Mr Norrell" --author "Susanna Clarke"
  python AgentCommands/Tools/library.py log-chapter --book <id> --chapter 3 --title 約克的石頭 \
      --summary "..." --events "事件A | 事件B" --views "對諾瑞爾的新認識" --foreshadow "未解之謎"
  python AgentCommands/Tools/library.py add-character --book <id> --id norrell --name 諾瑞爾 \
      --chapter 1 --headline "避世苦修的藏書囤積者" --facts "事實A | 事實B" --view "本小姐的看法..."
  python AgentCommands/Tools/library.py revise-view --book <id> --character norrell --chapter 3 \
      --headline "控制狂+壟斷者" --change-reason "石頭施法失控 + 秘密買斷藏書" --view "..." --diff "與v1差異..."
  python AgentCommands/Tools/library.py show-book --book <id>
  python AgentCommands/Tools/library.py show-character --book <id> --character norrell [--version all|N]
  python AgentCommands/Tools/library.py list
"""
import argparse
import json
import os
import re
import sys
import time
from datetime import datetime
from pathlib import Path

# 區塊職責: Windows cp950 終端強制 utf-8 stdout
# 物理意義: agent 在 chat 內跑 cmd 印中文 + emoji 不能崩 (cp950 不認 ✅ / 中文標題)
# 數值影響: 純 IO 層, 不影響邏輯
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責：portable repo root 推算 (對齊 UCL_Core awakening.py / run_cmd.py convention)
# 物理意義：本工具在 UCL_Core (submodule) 內, 但 per-project 閱讀資料 (Library/) 落各專案 cwd。
#          三層 fallback 推 REPO_ROOT:
#   1. CLAUDE_PROJECT_DIR env var (Claude Code hook 設, 最 stable)
#   2. 從 cwd walk parents 找 .git (主專案 .git 比 submodule .git 先命中)
#   3. 從本檔 walk (最後 fallback)
# 數值影響：LIB_ROOT 指向「呼叫所在專案」的 AgentCommands/BookNotes, 而非 UCL_Core 自身。
#          (用 BookNotes 而非 Library, 避開 Unity 專案標準 .gitignore 的 Library/ 快取規則)
_HERE = Path(__file__).resolve().parent


def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").exists():  # repo 為 dir / submodule 為 file
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    walked = _find_git_root_by_walk(_HERE)
    if walked:
        return walked
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()
LIB_ROOT = _REPO_ROOT / "AgentCommands" / "BookNotes"


# ===========================================================
# 小工具
# ===========================================================

def _today() -> str:
    return datetime.now().strftime("%Y-%m-%d")


def _slugify(s: str) -> str:
    # 保留英數字、CJK、連字號; 其餘轉成連字號。供書本/章節檔名用。
    s = (s or "").strip().lower()
    s = re.sub(r"[^\w一-鿿-]+", "-", s)
    s = re.sub(r"-+", "-", s).strip("-")
    return s or "untitled"


def _atomic_write(path: Path, text: str) -> None:
    # 區塊職責: atomic write (寫 temp → os.replace) + backoff retry
    # 物理意義: 沿用 2026-05-21 run_cmd.py 學到的教訓 — Windows 強制檔鎖下, 直接 open("w")
    #          truncate 期間若被別的 process 持鎖會 OSError [Errno 22]; 用 rename 近 atomic + 重試規避。
    # 數值影響: 寫入更穩, 不破壞既有檔; 失敗 5 次才拋原錯。
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.tmp{os.getpid()}")
    last_err = None
    for attempt in range(5):
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(text)
            os.replace(tmp, path)
            return
        except OSError as e:
            last_err = e
            time.sleep(0.1 * (attempt + 1))
    try:
        if tmp.exists():
            tmp.unlink()
    except OSError:
        pass
    raise last_err


def _read_json(path: Path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _write_json(path: Path, data) -> None:
    _atomic_write(path, json.dumps(data, ensure_ascii=False, indent=2) + "\n")


def _frontmatter(d: dict) -> str:
    # 產生簡單 YAML frontmatter; list 用 [a, b] 行內表示。
    lines = ["---"]
    for k, v in d.items():
        if isinstance(v, list):
            lines.append(f"{k}: [{', '.join(str(x) for x in v)}]")
        else:
            lines.append(f"{k}: {v}")
    lines.append("---")
    return "\n".join(lines)


def _split_list(s):
    # 把 CLI 傳的字串用 ; | 換行 切成 list（方便一行傳多筆）。
    if not s:
        return []
    parts = re.split(r"[;|\n]+", s)
    return [p.strip() for p in parts if p.strip()]


def _book_dir(book: str) -> Path:
    return LIB_ROOT / book


def _book_json(book: str) -> Path:
    return _book_dir(book) / "book.json"


def _require_book(book: str):
    bj = _book_json(book)
    if not bj.exists():
        print(f"❌ 找不到書: {book}（請先 add-book）", file=sys.stderr)
        sys.exit(1)
    return _read_json(bj)


def _char_dir(book: str, cid: str) -> Path:
    return _book_dir(book) / "characters" / cid


def _char_profile(book: str, cid: str) -> Path:
    return _char_dir(book, cid) / "_profile.json"


def _write_view_md(book: str, cid: str, ver: dict, facts, view, diff) -> str:
    # 區塊職責: 產一份「人物看法快照」markdown（v1 = 初印象, vN = 改觀後新版）
    # 物理意義: 這是系統核心 — 每次改觀都新增一檔, 舊檔保留, 形成可回溯的看法演變史
    # 數值影響: 回傳檔名給 _profile.json 索引登記
    fname = f"v{ver['v']}_{_today()}.md"
    fpath = _char_dir(book, cid) / fname
    fm = {
        "character": cid,
        "version": ver["v"],
        "as_of_chapter": ver["as_of_chapter"],
        "date": _today(),
        "headline": ver["headline"],
    }
    if ver.get("change_reason"):
        fm["change_reason"] = ver["change_reason"]
    if ver["v"] > 1:
        fm["supersedes"] = f"v{ver['v'] - 1}"

    body = [_frontmatter(fm), ""]
    body.append("## 已知事實（客觀）")
    for fact in (_split_list(facts) or ["（待補）"]):
        body.append(f"- {fact}")
    body.append("")
    body.append("## 目前看法（第一人稱）")
    body.append(view or "（待補）")
    body.append("")
    if ver["v"] > 1:
        body.append(f"## 與前一版（v{ver['v'] - 1}）的差異")
        body.append(diff or ver.get("change_reason") or "（待補）")
        body.append("")
    _atomic_write(fpath, "\n".join(body))
    return fname


# ===========================================================
# 子命令
# ===========================================================

def cmd_add_book(args):
    book = args.id or _slugify(args.title)
    if _book_json(book).exists():
        print(f"⚠ 書已存在: {book}（不覆寫）", file=sys.stderr)
        return 1
    (_book_dir(book) / "chapters").mkdir(parents=True, exist_ok=True)
    (_book_dir(book) / "characters").mkdir(parents=True, exist_ok=True)
    data = {
        "id": book,
        "title": args.title,
        "title_original": args.title_original or "",
        "author": args.author or "",
        "reader_persona": args.reader_persona or "basecamp",
        "status": "reading",
        "progress": {"current_chapter": 0, "last_read": _today()},
        "characters": [],
    }
    _write_json(_book_json(book), data)
    print(f"✅ 建立書: {book}  《{args.title}》 / {args.author or '?'}")
    print(f"   {_book_dir(book)}")
    return 0


def cmd_log_chapter(args):
    book = args.book
    bk = _require_book(book)
    n = int(args.chapter)
    slug = _slugify(args.slug or args.title or f"ch{n}")
    fname = f"ch{n:02d}_{slug}.md"
    fpath = _book_dir(book) / "chapters" / fname

    body = [
        _frontmatter({
            "book": book, "chapter": n, "title": args.title or "",
            "reading_date": _today(),
            "new_characters": _split_list(args.new_characters),
        }),
        "",
        "## 內容摘要",
        args.summary or "（待補）",
        "",
        "## 關鍵事件",
    ]
    for e in (_split_list(args.events) or ["（待補）"]):
        body.append(f"- {e}")
    body += ["", "## 本章對人物的新認識"]
    for v in (_split_list(args.views) or ["（待補）"]):
        body.append(f"- {v}")
    body += ["", "## 伏筆 / 待解"]
    for fs in (_split_list(args.foreshadow) or ["（無）"]):
        body.append(f"- {fs}")
    body.append("")
    _atomic_write(fpath, "\n".join(body))

    if n > int(bk["progress"].get("current_chapter", 0)):
        bk["progress"]["current_chapter"] = n
    bk["progress"]["last_read"] = _today()
    _write_json(_book_json(book), bk)
    print(f"✅ 記錄章節: {book} ch{n}  → {fname}")
    return 0


def cmd_add_character(args):
    book = args.book
    bk = _require_book(book)
    cid = args.id
    if _char_profile(book, cid).exists():
        print(f"⚠ 人物已存在: {cid}（要改觀請用 revise-view）", file=sys.stderr)
        return 1
    ver = {"v": 1, "as_of_chapter": int(args.chapter), "headline": args.headline}
    fname = _write_view_md(book, cid, ver, args.facts, args.view, None)
    ver["file"] = fname
    ver["date"] = _today()
    prof = {
        "id": cid, "name": args.name, "book": book,
        "first_appeared_chapter": int(args.chapter),
        "current_version": 1,
        "versions": [ver],
    }
    _write_json(_char_profile(book, cid), prof)
    if cid not in bk["characters"]:
        bk["characters"].append(cid)
        _write_json(_book_json(book), bk)
    print(f"✅ 新增人物: {cid}（{args.name}）v1 @ ch{args.chapter} — {args.headline}")
    return 0


def cmd_revise_view(args):
    book = args.book
    _require_book(book)
    cid = args.character
    prof_path = _char_profile(book, cid)
    if not prof_path.exists():
        print(f"❌ 找不到人物: {cid}（請先 add-character）", file=sys.stderr)
        return 1
    prof = _read_json(prof_path)
    newv = int(prof["current_version"]) + 1
    ver = {
        "v": newv, "as_of_chapter": int(args.chapter),
        "headline": args.headline, "change_reason": args.change_reason,
    }
    fname = _write_view_md(book, cid, ver, args.facts, args.view, args.diff)
    ver["file"] = fname
    ver["date"] = _today()
    prof["versions"].append(ver)
    prof["current_version"] = newv
    _write_json(prof_path, prof)
    print(f"✅ 改觀記錄: {cid} → v{newv} @ ch{args.chapter}（保留舊版 v{newv - 1}）")
    print(f"   新看法: {args.headline}")
    print(f"   改觀因: {args.change_reason}")
    return 0


def cmd_show_book(args):
    bk = _require_book(args.book)
    print(f"📖 《{bk['title']}》 {bk.get('title_original', '')}")
    print(f"   作者: {bk.get('author', '?')}   讀者: {bk.get('reader_persona')}   狀態: {bk.get('status')}")
    pr = bk.get("progress", {})
    print(f"   進度: 第 {pr.get('current_chapter')} 章   最後閱讀: {pr.get('last_read')}")
    chdir = _book_dir(args.book) / "chapters"
    chs = sorted(chdir.glob("ch*.md")) if chdir.exists() else []
    print(f"   章節 ({len(chs)}):")
    for c in chs:
        print(f"     - {c.name}")
    print(f"   人物 ({len(bk.get('characters', []))}):")
    for cid in bk.get("characters", []):
        pp = _char_profile(args.book, cid)
        if pp.exists():
            p = _read_json(pp)
            cur = p["versions"][-1]
            nver = p["current_version"]
            tag = f"（v1→v{nver}, {nver} 版看法）" if nver > 1 else ""
            print(f"     - {p['name']} ({cid})  v{nver}: {cur['headline']} {tag}")
    return 0


def cmd_show_character(args):
    book, cid = args.book, args.character
    pp = _char_profile(book, cid)
    if not pp.exists():
        print(f"❌ 找不到人物: {cid}", file=sys.stderr)
        return 1
    p = _read_json(pp)
    print(f"👤 {p['name']} ({cid}) — {p['book']}")
    print(f"   初登場: ch{p['first_appeared_chapter']}   目前版本: v{p['current_version']}")
    print("   看法演變:")
    for v in p["versions"]:
        reason = f"   ← {v.get('change_reason')}" if v.get("change_reason") else ""
        print(f"     v{v['v']} (ch{v['as_of_chapter']}, {v.get('date')}): {v['headline']}{reason}")

    want = args.version or "current"
    if want == "all":
        targets = p["versions"]
    elif want == "current":
        targets = [p["versions"][-1]]
    else:
        targets = [v for v in p["versions"] if str(v["v"]) == str(want)]
    for v in targets:
        fpath = _char_dir(book, cid) / v["file"]
        if fpath.exists():
            print(f"\n--- v{v['v']} 全文 ({v['file']}) ---")
            print(fpath.read_text(encoding="utf-8"))
    return 0


def cmd_list(args):
    if not LIB_ROOT.exists():
        print("（圖書館為空）")
        return 0
    books = [d for d in LIB_ROOT.iterdir() if d.is_dir() and (d / "book.json").exists()]
    if not books:
        print("（圖書館為空）")
        return 0
    for d in sorted(books):
        bk = _read_json(d / "book.json")
        pr = bk.get("progress", {})
        print(f"📖 {bk['id']}  《{bk['title']}》 / {bk.get('author', '?')}"
              f"  — 第{pr.get('current_chapter')}章  人物{len(bk.get('characters', []))}")
    return 0


def cmd_bookmark(args):
    # 區塊職責: 記錄「這次讀到哪裡」+ 可選的續讀備註/心得（書籤）
    # 物理意義: 讀書中斷時標記位置, 方便之後續讀同一本書接得上; note 可放本小姐自決要不要寫的心得
    # 數值影響: 寫進 book.json 的 progress.{current_chapter, last_read, bookmark_note}
    book = args.book
    bk = _require_book(book)
    if args.chapter is not None:
        bk["progress"]["current_chapter"] = int(args.chapter)
    bk["progress"]["last_read"] = _today()
    if args.note is not None:
        bk["progress"]["bookmark_note"] = args.note
    _write_json(_book_json(book), bk)
    print(f"🔖 書籤更新: {book}  讀到第 {bk['progress'].get('current_chapter')} 章")
    if bk["progress"].get("bookmark_note"):
        print(f"   續讀備註/心得: {bk['progress']['bookmark_note']}")
    return 0


def cmd_resume(args):
    # 區塊職責: 續讀前的 catch-up — 一眼看完「我讀到哪、該記得誰、還有什麼沒解開」
    # 物理意義: 之後要繼續讀同一本書時, 先跑這個喚回 context, 不必重讀整本
    # 數值影響: 純讀, 彙整 book.json + 人物現況 + 各章未解伏筆
    book = args.book
    bk = _require_book(book)
    pr = bk.get("progress", {})
    print(f"📖 續讀《{bk['title']}》 — 讀到第 {pr.get('current_chapter')} 章（最後閱讀 {pr.get('last_read')}）")
    _don = _books_root() / book / "_donation.json"
    if _don.exists():
        _dd = _read_json(_don)
        print(f"   📖 本書由 {_dd.get('donor_persona') or _dd.get('donor')} 捐贈入館 ({_dd.get('tokens')} token)")
    if pr.get("bookmark_note"):
        print(f"🔖 續讀備註 / 上次心得: {pr['bookmark_note']}")

    arcs = bk.get("arcs", [])
    if arcs:
        latest = arcs[-1]
        print(f"\n📚 最近階段大綱【第 {latest['chapters']} 章】{latest.get('title', '')}（見林）:")
        fp = _arc_dir(book) / latest["file"]
        if fp.exists():
            m = re.search(r"## 階段大綱（見林）\s*\n(.*?)(?:\n##|\Z)", fp.read_text(encoding="utf-8"), re.S)
            if m:
                print("   " + m.group(1).strip().replace("\n", "\n   "))

    print("\n👥 人物現況（目前看法）:")
    for cid in bk.get("characters", []):
        pp = _char_profile(book, cid)
        if pp.exists():
            p = _read_json(pp)
            cur = p["versions"][-1]
            print(f"   - {p['name']} ({cid}, v{p['current_version']}): {cur['headline']}")

    print("\n🧩 待解伏筆（各章彙整）:")
    chdir = _book_dir(book) / "chapters"
    found = False
    for c in (sorted(chdir.glob("ch*.md")) if chdir.exists() else []):
        text = c.read_text(encoding="utf-8")
        m = re.search(r"##\s*伏筆\s*/\s*待解\s*\n(.*?)(?:\n##|\Z)", text, re.S)
        if not m:
            continue
        for line in m.group(1).splitlines():
            line = line.strip()
            if line.startswith("-") and "無）" not in line and "無)" not in line:
                print(f"   [{c.stem}] {line[1:].strip()}")
                found = True
    if not found:
        print("   （無待解）")

    gterms = _read_glossary(book)["terms"]
    if gterms:
        print("\n📒 名詞速記（詳見 terms）:")
        for cat, items in _group_by_category(gterms).items():
            names = " / ".join(t["term"] for t in items)
            print(f"   【{_TERM_CAT_LABEL.get(cat, cat)}】{names}")

    nxt = int(pr.get("current_chapter", 0)) + 1
    print(f"\n→ 下一步: 讀第 {nxt} 章")
    return 0


# ===========================================================
# 名詞解釋 (per-book glossary:地名 / 特殊名詞 / 勢力 ...)
# ===========================================================

_TERM_CAT_LABEL = {
    "term": "特殊名詞/概念", "place": "地名/地域", "faction": "勢力/組織",
    "work": "作品/系列", "other": "其他",
}


def _glossary_path(book: str) -> Path:
    return _book_dir(book) / "glossary.json"


def _read_glossary(book: str):
    p = _glossary_path(book)
    return _read_json(p) if p.exists() else {"terms": []}


def _group_by_category(terms):
    # dict 在 py3.7+ 保插入序, 不需 OrderedDict
    groups = {}
    for t in terms:
        groups.setdefault(t.get("category", "other"), []).append(t)
    return groups


def cmd_add_term(args):
    # 區塊職責: 把一個世界觀名詞(地名/特殊能力/勢力...)加進該書的 glossary
    # 物理意義: 設定詞多的奇幻(如刺客系列)需要隨讀隨記, 免得後面忘了原智/精技是什麼
    # 數值影響: append 一筆 entry 到 <book>/glossary.json; 同名不重複
    book = args.book
    _require_book(book)
    g = _read_glossary(book)
    for t in g["terms"]:
        if t["term"] == args.term:
            print(f"⚠ 名詞已存在:{args.term}（不重複加）", file=sys.stderr)
            return 1
    entry = {
        "term": args.term,
        "category": args.category or "term",
        "definition": args.definition or "",
        "aliases": _split_list(args.aliases),
        "as_of_chapter": int(args.chapter) if args.chapter is not None else None,
        "added_date": _today(),
    }
    g["terms"].append(entry)
    _write_json(_glossary_path(book), g)
    print(f"✅ 加入名詞:[{_TERM_CAT_LABEL.get(entry['category'], entry['category'])}] {args.term}")
    return 0


def cmd_terms(args):
    # 區塊職責: 顯示該書名詞解釋(可按 category 篩), 分組列出
    book = args.book
    _require_book(book)
    terms = _read_glossary(book)["terms"]
    if args.category:
        terms = [t for t in terms if t.get("category") == args.category]
    if not terms:
        print("（此書尚無名詞解釋）")
        return 0
    title = _read_json(_book_json(book))["title"]
    print(f"📒《{title}》名詞解釋（共 {len(terms)}）\n")
    for cat, items in _group_by_category(terms).items():
        print(f"【{_TERM_CAT_LABEL.get(cat, cat)}】")
        for t in items:
            alias = f"（別名:{', '.join(t['aliases'])}）" if t.get("aliases") else ""
            print(f"  • {t['term']}{alias}")
            if t.get("definition"):
                print(f"      {t['definition']}")
        print()
    return 0


def _rec_path() -> Path:
    return LIB_ROOT / "_recommended.json"


def _read_recs():
    p = _rec_path()
    if p.exists():
        return _read_json(p)
    return {"recommendations": []}


_STATUS_LABEL = {"want-to-read": "想讀", "reading": "閱讀中", "read": "已讀"}


def cmd_recommend(args):
    # 區塊職責: 把一本書加進「推薦書單」(_recommended.json)
    # 物理意義: 之後自由時間讀書時, 從書單挑想讀的; 簡介以非嚴重劇透為主
    # 數值影響: append 一筆 entry; 同名不重複加
    LIB_ROOT.mkdir(parents=True, exist_ok=True)
    data = _read_recs()
    recs = data["recommendations"]
    for r in recs:
        if r["title"] == args.title:
            print(f"⚠ 書單已有:《{args.title}》(不重複加)", file=sys.stderr)
            return 1
    entry = {
        "title": args.title,
        "title_original": args.title_original or "",
        "author": args.author or "",
        "synopsis": args.synopsis or "",
        "status": args.status or "want-to-read",
        "source_url": args.source or "",
        "book_id": args.book_id or "",
        "note": args.note or "",
        "added_date": _today(),
    }
    recs.append(entry)
    _write_json(_rec_path(), data)
    label = _STATUS_LABEL.get(entry["status"], entry["status"])
    print(f"✅ 加入推薦書單:《{args.title}》 / {args.author or '?'}  [{label}]")
    return 0


def cmd_recommendations(args):
    # 區塊職責: 顯示推薦書單 (含非劇透簡介 / 狀態 / 是否已建檔)
    data = _read_recs()
    recs = data["recommendations"]
    if not recs:
        print("（推薦書單為空）")
        return 0
    print(f"📚 推薦書單（共 {len(recs)} 本）\n")
    for i, r in enumerate(recs, 1):
        label = _STATUS_LABEL.get(r.get("status"), r.get("status", ""))
        line = f"{i}. 《{r['title']}》"
        if r.get("title_original"):
            line += f"（{r['title_original']}）"
        line += f" / {r.get('author', '?')}  [{label}]"
        print(line)
        if r.get("synopsis"):
            print(f"   簡介: {r['synopsis']}")
        if r.get("book_id"):
            print(f"   已建檔: {r['book_id']}（可 resume / show-book 續讀）")
        if r.get("note"):
            print(f"   備註: {r['note']}")
        if r.get("source_url"):
            print(f"   來源: {r['source_url']}")
        print()
    return 0


# ===========================================================
# 階段大綱 (arc summary:每 ~N 章一個「見林」的總結)
# ===========================================================

def _arc_dir(book: str) -> Path:
    return _book_dir(book) / "arcs"


def cmd_arc(args):
    # 區塊職責: 記一個跨章「階段大綱」— 比 per-chapter 高一層的見林視角
    # 物理意義: 每讀 ~6 章(或一個自然 arc 邊界)收束一次, 抓貫穿線索與大局走向
    # 數值影響: 寫 <book>/arcs/arc_<range>.md + 在 book.json arcs[] 登記索引
    book = args.book
    _require_book(book)
    chapters = args.chapters
    fslug = re.sub(r"[^0-9]+", "-", chapters).strip("-") or "x"
    fname = f"arc_{fslug}.md"
    body = [
        _frontmatter({"book": book, "chapters": chapters, "title": args.title or "", "date": _today()}),
        "",
        "## 階段大綱（見林）",
        args.summary or "（待補）",
        "",
        "## 貫穿線索 / 伏筆狀態",
    ]
    for t in (_split_list(args.threads) or ["（待補）"]):
        body.append(f"- {t}")
    body.append("")
    _atomic_write(_arc_dir(book) / fname, "\n".join(body))

    bk = _read_json(_book_json(book))
    arcs = [a for a in bk.get("arcs", []) if a.get("chapters") != chapters]
    arcs.append({"chapters": chapters, "title": args.title or "", "file": fname, "date": _today()})
    bk["arcs"] = arcs
    _write_json(_book_json(book), bk)
    print(f"✅ 階段大綱: {book} 第 {chapters} 章 — {args.title or ''}")
    return 0


def cmd_arcs(args):
    book = args.book
    bk = _require_book(book)
    arcs = bk.get("arcs", [])
    if not arcs:
        print("（尚無階段大綱）")
        return 0
    print(f"📚《{bk['title']}》階段大綱（{len(arcs)}）\n")
    for a in arcs:
        print(f"【第 {a['chapters']} 章】{a.get('title', '')}  ({a.get('date')})")
        if args.full:
            fp = _arc_dir(book) / a["file"]
            if fp.exists():
                print()
                print(fp.read_text(encoding="utf-8"))
                print()
    return 0


# ===========================================================
# 捐贈圖書館 (Books/ 的書由捐贈者付 token 加入, 全員可讀, 標註捐贈者)
# ===========================================================

def _books_root() -> Path:
    return _REPO_ROOT / "AgentCommands" / "Books"


def _donations_index() -> Path:
    return _books_root() / "_donations.json"


def _run_treasury_debit(donor: str, amount: int, slug: str, desc: str):
    # 走 CMD: run_cmd.py run Treasury op=debit (use_kind=book_donation, caller==account)
    import subprocess
    run_cmd = _HERE / "run_cmd.py"
    cmd = [sys.executable, str(run_cmd), "run", "Treasury",
           "--arg", "op=debit", "--arg", f"account={donor}",
           "--arg", f"amount={amount}", "--arg", "use_kind=book_donation",
           "--arg", f"use_ref={slug}", "--arg", f"description={desc}",
           "--arg", f"caller={donor}"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=150)
        return (r.returncode == 0, (r.stdout or "") + (r.stderr or ""))
    except Exception as e:
        return (False, str(e))


def _verify_donation_debit(donor: str, slug: str, amount: int) -> bool:
    # 跨層驗證 (外觀 OK ≠ 真的 OK): 掃 ledger 確認 debit 真落帳, 不只信 Cmd stdout
    root = _REPO_ROOT / "AgentCommands" / "Treasury" / "ledger"
    if not root.exists():
        return False
    dirs = sorted([d for d in root.iterdir() if d.is_dir()], reverse=True)[:2]
    for d in dirs:
        for f in d.glob("*debit*.json"):
            try:
                e = _read_json(f)
            except Exception:
                continue
            if (e.get("type") == "debit" and e.get("account_id") == donor
                    and e.get("source_kind") == "book_donation"
                    and e.get("source_ref") == slug
                    and int(e.get("amount", 0)) == int(amount)):
                return True
    return False


def _run_tavern_post(sender_id: str, persona: str, body: str, tag: str = "book-donation") -> bool:
    # 走 CMD: run_cmd.py run Tavern op=post — 捐書後自動廣播新書入庫
    import subprocess
    import json as _json
    run_cmd = _HERE / "run_cmd.py"
    meta = _json.dumps({"tag": tag, "category": "chat"}, ensure_ascii=False)
    cmd = [sys.executable, str(run_cmd), "run", "Tavern",
           "--arg", "op=post", "--arg", "room=tavern",
           "--arg", f"sender_id={sender_id}", "--arg", f"persona={persona}",
           "--arg", f"body={body}", "--arg", f"meta={meta}"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=150)
        return r.returncode == 0
    except Exception:
        return False


def cmd_donate(args):
    # 區塊職責: 捐贈一本 Books/ 的書 — 付 token (走 Cmd_Treasury), 全員可讀, 標註捐贈者
    # 物理意義: 基礎 100 token/本 (多冊每冊算一本); Tim 可給優惠價 (--tokens 覆寫)
    book = args.book
    bdir = _books_root() / book
    if not bdir.exists():
        print(f"❌ Books/{book}/ 不存在 — 先把書放進 AgentCommands/Books/{book}/", file=sys.stderr)
        return 2
    dpath = bdir / "_donation.json"
    if dpath.exists():
        ex = _read_json(dpath)
        print(f"⚠ 《{book}》已被捐贈 — 捐贈者: {ex.get('donor_persona') or ex.get('donor')} "
              f"({ex.get('tokens')} token @ {ex.get('donated_at')})", file=sys.stderr)
        return 1
    donor = args.donor
    tokens = int(args.tokens) if args.tokens is not None else 100
    title = book
    if _book_json(book).exists():
        title = _read_json(_book_json(book)).get("title", book)
    desc = f"捐贈圖書: {title} (donor={args.donor_persona or donor})"

    print(f"📚 捐贈《{title}》 — 捐贈者 {args.donor_persona or donor} / {tokens} token")
    print("   走 CMD: Cmd_Treasury op=debit (use_kind=book_donation)...")
    ok, out = _run_treasury_debit(donor, tokens, book, desc)
    # 跨層驗證: 掃 ledger 確認真扣款才註冊
    if not _verify_donation_debit(donor, book, tokens):
        print("❌ Treasury debit 未確認落帳 (餘額不足? caller!=account? Editor 未跑?) — 不註冊捐贈",
              file=sys.stderr)
        print(f"   run_cmd 輸出(尾):\n{out[-400:]}", file=sys.stderr)
        return 2
    print("✓ debit 已落帳 (ledger 跨層驗證通過)")

    entry = {
        "book": book, "title": title, "donor": donor,
        "donor_persona": args.donor_persona or "", "donor_agent": args.donor_agent or "",
        "tokens": tokens, "base_price": 100, "donated_at": _today(), "note": args.note or "",
    }
    _write_json(dpath, entry)
    idx = _read_json(_donations_index()) if _donations_index().exists() else {"donations": []}
    idx["donations"] = [d for d in idx.get("donations", []) if d.get("book") != book]
    idx["donations"].append(entry)
    _write_json(_donations_index(), idx)
    print(f"✅ 捐贈完成:《{title}》→ 📖 捐贈者 {entry['donor_persona'] or donor} ({tokens} token)。全員可讀。")

    # 自動觸發酒館「新書入庫」通知 (Tim 2026-05-22)；非致命 — 失敗不影響捐贈
    if not getattr(args, "no_notify", False):
        who = entry["donor_persona"] or donor
        notice = (f"📚 新書入庫!\n\n"
                  f"《{title}》由 **{who}** 捐贈進共享圖書館（{tokens} token），全員都能讀了。\n"
                  f"想讀的同事:resume --book {book} 接上進度,或直接看 Books/{book}/ 全文。")
        sent = _run_tavern_post(donor, entry["donor_persona"] or "", notice, tag="book-donation")
        print(f"📣 酒館新書入庫通知:{'已發送' if sent else '發送失敗(捐贈仍成功)'}")
    return 0


def cmd_donations(args):
    idx = _read_json(_donations_index()) if _donations_index().exists() else {"donations": []}
    ds = idx.get("donations", [])
    if not ds:
        print("（圖書館尚無捐贈書）")
        return 0
    print(f"📚 捐贈圖書館（共 {len(ds)} 本）\n")
    for d in ds:
        who = d.get("donor_persona") or d.get("donor")
        print(f"- 《{d.get('title', d['book'])}》 — 📖 捐贈者: {who} "
              f"({d.get('tokens')} token, {d.get('donated_at')})")
        if d.get("note"):
            print(f"    note: {d['note']}")
    return 0


# ===========================================================
# argparse
# ===========================================================

def build_parser():
    p = argparse.ArgumentParser(prog="library.py", description="Reading Library CLI — 閱讀心得圖書館")
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("add-book", help="建立新書")
    a.add_argument("--id", help="書本 slug（缺則由 title 生成）")
    a.add_argument("--title", required=True)
    a.add_argument("--title-original", dest="title_original")
    a.add_argument("--author")
    a.add_argument("--reader-persona", dest="reader_persona")
    a.set_defaults(func=cmd_add_book)

    a = sub.add_parser("log-chapter", help="記錄一章")
    a.add_argument("--book", required=True)
    a.add_argument("--chapter", required=True)
    a.add_argument("--title")
    a.add_argument("--slug")
    a.add_argument("--summary")
    a.add_argument("--events", help="關鍵事件, 用 ; | 或換行分隔多筆")
    a.add_argument("--views", help="本章對人物的新認識, 分隔同上")
    a.add_argument("--new-characters", dest="new_characters")
    a.add_argument("--foreshadow", help="伏筆/待解, 分隔同上")
    a.set_defaults(func=cmd_log_chapter)

    a = sub.add_parser("add-character", help="新增人物（v1 初印象）")
    a.add_argument("--book", required=True)
    a.add_argument("--id", required=True)
    a.add_argument("--name", required=True)
    a.add_argument("--chapter", required=True)
    a.add_argument("--headline", required=True, help="一句話人物標題")
    a.add_argument("--facts", help="客觀已知事實, 分隔同上")
    a.add_argument("--view", help="第一人稱看法")
    a.set_defaults(func=cmd_add_character)

    a = sub.add_parser("revise-view", help="改觀（fork 新版本, 不覆寫舊版）")
    a.add_argument("--book", required=True)
    a.add_argument("--character", required=True)
    a.add_argument("--chapter", required=True)
    a.add_argument("--headline", required=True)
    a.add_argument("--change-reason", dest="change_reason", required=True, help="為何改觀")
    a.add_argument("--facts")
    a.add_argument("--view")
    a.add_argument("--diff", help="與前一版的差異")
    a.set_defaults(func=cmd_revise_view)

    a = sub.add_parser("show-book", help="顯示書本概覽")
    a.add_argument("--book", required=True)
    a.set_defaults(func=cmd_show_book)

    a = sub.add_parser("show-character", help="顯示人物看法演變 + 全文")
    a.add_argument("--book", required=True)
    a.add_argument("--character", required=True)
    a.add_argument("--version", help="all / 版本號 / 預設只印目前版本")
    a.set_defaults(func=cmd_show_character)

    a = sub.add_parser("list", help="列出所有書")
    a.set_defaults(func=cmd_list)

    a = sub.add_parser("bookmark", help="記錄讀到哪裡 + 可選續讀備註/心得")
    a.add_argument("--book", required=True)
    a.add_argument("--chapter", help="讀到第幾章（缺則不動進度）")
    a.add_argument("--note", help="續讀前該記得的事 / 本小姐自選要不要寫的心得")
    a.set_defaults(func=cmd_bookmark)

    a = sub.add_parser("resume", help="續讀前 catch-up:進度 + 人物現況 + 未解伏筆")
    a.add_argument("--book", required=True)
    a.set_defaults(func=cmd_resume)

    a = sub.add_parser("recommend", help="加入推薦書單(附非劇透簡介)")
    a.add_argument("--title", required=True)
    a.add_argument("--title-original", dest="title_original")
    a.add_argument("--author")
    a.add_argument("--synopsis", help="非嚴重劇透簡介")
    a.add_argument("--status", choices=["want-to-read", "reading", "read"], help="想讀/閱讀中/已讀")
    a.add_argument("--source", help="來源 URL")
    a.add_argument("--book-id", dest="book_id", help="已在圖書館建檔的 book id(可續讀)")
    a.add_argument("--note")
    a.set_defaults(func=cmd_recommend)

    a = sub.add_parser("recommendations", help="顯示推薦書單")
    a.set_defaults(func=cmd_recommendations)

    a = sub.add_parser("add-term", help="加名詞解釋(地名/特殊名詞/勢力...)")
    a.add_argument("--book", required=True)
    a.add_argument("--term", required=True)
    a.add_argument("--category", choices=["term", "place", "faction", "work", "other"],
                   help="term=特殊名詞 / place=地名 / faction=勢力 / work=作品 / other")
    a.add_argument("--definition")
    a.add_argument("--aliases", help="別名, 用 ; | 或換行分隔")
    a.add_argument("--chapter", help="登場/相關章節(選填)")
    a.set_defaults(func=cmd_add_term)

    a = sub.add_parser("terms", help="顯示該書名詞解釋")
    a.add_argument("--book", required=True)
    a.add_argument("--category", choices=["term", "place", "faction", "work", "other"])
    a.set_defaults(func=cmd_terms)

    a = sub.add_parser("arc", help="記階段大綱(每 ~6 章一個見林總結)")
    a.add_argument("--book", required=True)
    a.add_argument("--chapters", required=True, help="涵蓋章節範圍, 如 1-6")
    a.add_argument("--title", help="這個 arc 的標題")
    a.add_argument("--summary", help="階段大綱(見林)")
    a.add_argument("--threads", help="貫穿線索/伏筆狀態, 用 ; | 或換行分隔")
    a.set_defaults(func=cmd_arc)

    a = sub.add_parser("arcs", help="顯示階段大綱列表 (--full 印全文)")
    a.add_argument("--book", required=True)
    a.add_argument("--full", action="store_true")
    a.set_defaults(func=cmd_arcs)

    a = sub.add_parser("donate", help="捐贈一本 Books/ 的書(付 token 走 Cmd_Treasury, 全員可讀, 標註捐贈者)")
    a.add_argument("--book", required=True, help="Books/<slug> 的 slug")
    a.add_argument("--donor", required=True, help="捐贈者 bank id (Treasury caller 必須==此帳戶)")
    a.add_argument("--tokens", default=None, help="付多少 token (預設 100/本; Tim 可給優惠價)")
    a.add_argument("--donor-persona", dest="donor_persona", default=None, help="捐贈者 persona (標註用)")
    a.add_argument("--donor-agent", dest="donor_agent", default=None)
    a.add_argument("--note", default=None)
    a.add_argument("--no-notify", dest="no_notify", action="store_true",
                   help="不發酒館新書入庫通知(預設會自動廣播)")
    a.set_defaults(func=cmd_donate)

    a = sub.add_parser("donations", help="列出捐贈圖書館 (書 + 捐贈者)")
    a.set_defaults(func=cmd_donations)

    return p


def main():
    args = build_parser().parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
