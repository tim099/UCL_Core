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

# T-PATH-01 (2026-05-28): AgentCommands 資料根 pointer 檔解析
# 物理意義: C# 控制台 Apply 寫 <git-root>/.agentcommands_root.local; 兩語言共讀同一檔。
def _resolve_agentcommands_data_root(git_root: Path) -> Path:
    pointer = git_root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content:
                p = Path(content)
                if p.is_absolute():
                    return p.resolve()
    except Exception:
        pass
    return (git_root / "AgentCommands").resolve()

_DATA_ROOT = _resolve_agentcommands_data_root(_REPO_ROOT)
LIB_ROOT = _DATA_ROOT / "BookNotes"  # 走可 override 資料根; 預設 = _REPO_ROOT/AgentCommands/BookNotes

# 區塊職責：多人同讀的「分支筆記」(Git Branch 概念, Tim 2026-05-26 拍板最小改動方案)
# 物理意義：初始讀者 (book.json.reader_persona, 可能也是捐贈者) 的筆記 = main, 結構不動。
#          其他讀者去讀 → 在 BookNotes/<slug>/branches/<reader>/ 開分支筆記 (獨立 book.json + characters/ + chapters/ + arcs/),
#          完全不影響初始讀者的 main; 但 main 讀者可參考 (resume 會列出分支)。
# 數值影響：_ACTIVE_READER 非空時, _book_dir() 自動把所有路徑 (book.json/characters/chapters/arcs) 導向該分支子目錄。
#          glossary(名詞, 客觀) / reviews(已 per-reviewer) 不分支, 維持 main 共享。
_ACTIVE_READER = None   # None = main(初始讀者); 非空字串 = branch reader codename


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


def _normalize_book_name(value: str) -> str:
    """Comparison key for a user-supplied book name; keeps CJK and alphanumerics."""
    value = (value or "").casefold().replace("×", "x")
    return re.sub(r"[^\w\u4e00-\u9fff]+", "", value)


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


# New reader-root recall brief -------------------------------------------------
# 區塊職責：將一位 persona 在一個新 Library media 的累積閱讀資料組成單一追回檔。
# 物理意義：續讀前不必逐一開 reader.json、每個 chapter round 與角色 view；產物放回 persona
#          自己的 letters/，與 _wake_brief.md 同樣是可重建的機械視圖，不是第二份筆記來源。
# 數值影響：每次呼叫完整覆寫同一份 _reading_recall_<media-id>.md；原始章節與角色歷史不會被修改。
_READER_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]*$")


def _require_reader_id(value: str, label: str) -> str:
    """Reject path-like ids before deriving a reader root or a letters output path."""
    if not value or not _READER_ID_RE.fullmatch(value):
        raise ValueError(f"{label} 必須是英數、底線或連字號，且不可包含路徑分隔符：{value!r}")
    return value


def _require_reader_file_name(value: str, label: str) -> str:
    """A manifest may select only a direct child file; it must never escape its chapter directory."""
    candidate = Path(value or "")
    if not value or candidate.name != value or value in {".", ".."}:
        raise ValueError(f"{label} 必須是單一檔名，不可包含路徑：{value!r}")
    return value


def _read_text_or_note(path: Path, label: str) -> str:
    """Keep a malformed or missing referenced file visible in the recall instead of silently omitting history."""
    if not path.is_file():
        return f"> [!WARNING]\n> 缺少 {label}: `{path.name}`\n"
    return path.read_text(encoding="utf-8").strip()


def _new_library_root(media_id: str) -> Path:
    return LIB_ROOT / "Library" / "media" / media_id


def _render_reading_recall(persona: str, media_id: str) -> str:
    """Render all existing reading history for exactly one ``media × persona`` reader root."""
    persona = _require_reader_id(persona, "persona")
    media_id = _require_reader_id(media_id, "media_id")
    media_root = _new_library_root(media_id)
    reader_root = media_root / "readers" / persona
    reader_path = reader_root / "reader.json"
    if not reader_path.is_file():
        raise FileNotFoundError(f"找不到新閱讀紀錄：{reader_path}")

    reader = _read_json(reader_path)
    if reader.get("reader_persona") != persona:
        raise ValueError(
            f"reader.json.reader_persona={reader.get('reader_persona')!r}，與路徑 persona={persona!r} 不一致"
        )
    if reader.get("media_id") != media_id:
        raise ValueError(
            f"reader.json.media_id={reader.get('media_id')!r}，與請求 media_id={media_id!r} 不一致"
        )

    media = _read_json(media_root / "media.json") if (media_root / "media.json").is_file() else {}
    work_id = media.get("work_id", "")
    work_path = LIB_ROOT / "Library" / "works" / work_id / "work.json"
    work = _read_json(work_path) if work_id and work_path.is_file() else {}
    progress = reader.get("progress", {})
    now = datetime.now().astimezone().isoformat(timespec="seconds")
    lines = [
        "---",
        "type: reading_recall",
        f"persona: {persona}",
        f"media_id: {media_id}",
        f"work_id: {work_id or 'unknown'}",
        f"generated_at: {now}",
        "source_of_truth: AgentCommands/BookNotes/Library",
        "---",
        "",
        f"# 閱讀追回｜{work.get('title') or media_id}",
        "",
        "> 此檔由 `library.py reading-recall` 機械生成；每次重新生成會覆寫。"
        " 原始資料仍是 reader root 下的 JSON、chapter round 與 character view。",
        "",
        "## 目前狀態",
        f"- reader_persona: `{persona}`",
        f"- media: `{media_id}` ({media.get('media_kind', 'unknown')})",
        f"- status: `{reader.get('status', 'unknown')}`",
        f"- anticipation: {reader.get('anticipation', '未設定')}／5",
        f"- current_chapter_id: `{progress.get('current_chapter_id', '未設定')}`",
        f"- last_read: {progress.get('last_read', '未設定')}",
        f"- bookmark: {progress.get('bookmark_note', '（無）')}",
        "",
        "### 目前看法",
        reader.get("current_impression", "（尚無）"),
        "",
        "## 作品與媒材",
        f"- work_id: `{work_id or 'unknown'}`",
        f"- title: {work.get('title', '（未登錄）')}",
        f"- title_original: {work.get('title_original', '（未登錄）')}",
        f"- author: {work.get('author', '（未登錄）')}",
        f"- genre_tags: {', '.join(work.get('genre_tags', [])) or '（未登錄）'}",
        "",
        "## 書架投影",
        _read_text_or_note(reader_root / "bookshelf.md", "bookshelf 投影"),
        "",
        "## 已讀章節與 round 心得",
    ]

    chapter_dirs = sorted((p for p in (reader_root / "chapters").glob("*") if p.is_dir()), key=lambda p: p.name)
    if not chapter_dirs:
        lines.extend(["（尚無章節紀錄）", ""])
    for chapter_dir in chapter_dirs:
        manifest_path = chapter_dir / "chapter.json"
        try:
            manifest = _read_json(manifest_path)
        except (FileNotFoundError, json.JSONDecodeError) as exc:
            lines.extend([f"### `{chapter_dir.name}`", f"> [!WARNING]\n> 無法讀取 chapter.json：{exc}", ""])
            continue
        lines.extend([
            f"### {manifest.get('display_number', chapter_dir.name)}｜{manifest.get('title', '（未命名）')}",
            f"- chapter_id: `{manifest.get('chapter_id', chapter_dir.name)}`",
            "",
        ])
        rounds = manifest.get("rounds", [])
        if not rounds:
            lines.extend(["（尚無 round）", ""])
        for entry in rounds:
            file_name = entry if isinstance(entry, str) else entry.get("file", "")
            round_number = "?" if isinstance(entry, str) else entry.get("round", "?")
            read_date = "" if isinstance(entry, str) else entry.get("reading_date", "")
            try:
                file_name = _require_reader_file_name(file_name, "round file")
                round_body = _read_text_or_note(chapter_dir / file_name, "chapter round")
            except ValueError as exc:
                round_body = f"> [!WARNING]\n> {exc}\n"
            lines.extend([
                f"#### Round {round_number}" + (f"（{read_date}）" if read_date else ""),
                round_body,
                "",
            ])

    lines.append("## 角色資訊與觀點版本")
    character_dirs = sorted((p for p in (reader_root / "characters").glob("*") if p.is_dir()), key=lambda p: p.name)
    if not character_dirs:
        lines.extend(["（尚無角色紀錄）", ""])
    for character_dir in character_dirs:
        profile_path = character_dir / "profile.json"
        lines.extend([f"### `{character_dir.name}`", "#### 已確認 facts（profile.json）"])
        if profile_path.is_file():
            lines.extend(["```json", json.dumps(_read_json(profile_path), ensure_ascii=False, indent=2), "```"])
        else:
            lines.append("> [!WARNING]\n> 缺少 profile.json")
        views = sorted(character_dir.glob("v*.md"), key=lambda p: p.name)
        if not views:
            lines.append("（尚無主觀 view 版本）")
        for view_path in views:
            lines.extend([f"#### {view_path.name}", _read_text_or_note(view_path, "character view"), ""])

    return "\n".join(lines).rstrip() + "\n"


def cmd_reading_recall(args):
    """Generate the per-persona, per-media resume document in that persona's letters directory."""
    try:
        persona = _require_reader_id(args.persona, "persona")
        media_id = _require_reader_id(args.media_id, "media_id")
        text = _render_reading_recall(persona, media_id)
    except (ValueError, FileNotFoundError, json.JSONDecodeError) as exc:
        print(f"❌ 無法生成閱讀追回檔：{exc}", file=sys.stderr)
        return 1
    output = _DATA_ROOT / "ChatTavern" / "baton" / "letters" / persona / f"_reading_recall_{media_id}.md"
    _atomic_write(output, text)
    print(f"📚 已生成閱讀追回檔：{output}")
    return 0


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
    # 唯一路由點：_ACTIVE_READER 非空 → 導向 branches/<reader>/ 分支子目錄；否則走 main
    # 所有下游 path helper (_book_json / _char_dir / chapters / _arc_dir) 都從本函式衍生, 故路由集中於此一處
    base = LIB_ROOT / book
    if _ACTIVE_READER:
        return base / "branches" / _ACTIVE_READER
    return base


def _book_json(book: str) -> Path:
    return _book_dir(book) / "book.json"


def _main_book_dir(book: str) -> Path:
    # 永遠指向 main(初始讀者), 不受 _ACTIVE_READER 影響 — 用來讀初始讀者的元資料/進度當分支種子或參考
    return LIB_ROOT / book


def _main_book_json(book: str) -> Path:
    return _main_book_dir(book) / "book.json"


def _initial_reader(book: str) -> str:
    # 初始讀者 = main book.json 的 reader_persona
    bj = _main_book_json(book)
    if bj.exists():
        return _read_json(bj).get("reader_persona", "") or ""
    return ""


def _list_branches(book: str):
    # 列出某書現有的分支讀者 (branches/ 下每個子目錄)
    bdir = _main_book_dir(book) / "branches"
    if not bdir.exists():
        return []
    return sorted(d.name for d in bdir.iterdir() if d.is_dir() and (d / "book.json").exists())


def _ensure_branch(book: str, reader: str, continue_from=None) -> None:
    # 區塊職責：確保 branches/<reader>/book.json 存在 (首次啟用分支時自動 init)
    # 物理意義：分支 book.json 從 main 複製基本元資料 (title/author/原文名) + 自己的 reader_persona + 獨立 progress。
    #          continue-from 指定時, 從該來源讀者(或 main)的當前章當「起點」(只複製數字, 完全不寫回來源 → 不影響原讀者書籤)。
    bdir = _main_book_dir(book) / "branches" / reader
    bj = bdir / "book.json"
    if bj.exists():
        return
    main_data = _read_json(_main_book_json(book)) if _main_book_json(book).exists() else {}
    start_ch = 0
    seed_note = ""
    if continue_from:
        # 來源可為 main 初始讀者 或 另一分支讀者
        if continue_from == _initial_reader(book):
            src_bj = _main_book_json(book)
        else:
            src_bj = _main_book_dir(book) / "branches" / continue_from / "book.json"
        if src_bj.exists():
            start_ch = _read_json(src_bj).get("progress", {}).get("current_chapter", 0) or 0
            seed_note = f"（branch 起點：接續 {continue_from} 讀到的第 {start_ch} 章，之後獨立推進，不影響 {continue_from}）"
    data = {
        "id": f"{main_data.get('id', book)}::{reader}",
        "title": main_data.get("title", book),
        "title_original": main_data.get("title_original", ""),
        "author": main_data.get("author", ""),
        "reader_persona": reader,
        "branch_of": book,                                   # 標記：這是分支筆記
        "branched_from": continue_from or "(獨立起讀)",      # 從誰的進度接續 (或獨立從頭)
        "status": "reading",
        "progress": {"current_chapter": start_ch, "last_read": _today(), "bookmark_note": seed_note},
        "characters": [],
    }
    bdir.mkdir(parents=True, exist_ok=True)
    _write_json(bj, data)
    print(f"🌿 已開分支筆記: {book}/branches/{reader}/"
          + (f"（接續 {continue_from} 第 {start_ch} 章）" if continue_from else "（獨立從頭）"))


def _activate_branch(book: str, reader, continue_from=None):
    # 區塊職責：依 --reader 設定 module 級 active branch (集中在 main() 呼叫一次)
    # 物理意義：reader 為空 或 == 初始讀者 → main (不分支, 向後相容)；否則 → branch + 確保已 init
    global _ACTIVE_READER
    if not reader or reader == _initial_reader(book):
        _ACTIVE_READER = None
        return None
    _ACTIVE_READER = reader
    _ensure_branch(book, reader, continue_from)
    return reader


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
    # 區塊職責：origin 區分原創 (authored) vs 調入別人的書 (imported)
    # 物理意義：authored = agent 自由時間「寫書」自產 (Plan_FreeTime_BookWriting);
    #          原創書的「讀者」語意上就是作者本人 → reader_persona 預設帶 author_persona;
    #          publish_status: authored 預設 draft (草稿僅作者可見, publish 後才全員可讀);
    #          imported / 無 origin → 沿用現況 (向後相容, 不帶這些欄位的舊書照常)
    origin = getattr(args, "origin", None)
    author_persona = getattr(args, "author_persona", None)
    reader = args.reader_persona or author_persona or "basecamp"
    data = {
        "id": book,
        "title": args.title,
        "title_original": args.title_original or "",
        "author": args.author or "",
        "aliases": list(dict.fromkeys([args.title, *(_split_list(args.aliases)),
                                         *([args.title_original] if args.title_original else [])])),
        "reader_persona": reader,
        "status": "reading",
        "progress": {"current_chapter": 0, "last_read": _today()},
        "characters": [],
    }
    if origin == "authored":
        data["origin"] = "authored"
        data["author_persona"] = author_persona or reader
        data["publish_status"] = "draft"
        data["status"] = "writing"   # 原創書狀態語意: 撰寫中
    elif origin == "imported":
        data["origin"] = "imported"
    _write_json(_book_json(book), data)
    kind = "✍ 原創書(草稿)" if origin == "authored" else "📖 書"
    print(f"✅ 建立{kind}: {book}  《{args.title}》 / {args.author or '?'}"
          + (f"  作者: {data.get('author_persona')}" if origin == "authored" else ""))
    print(f"   {_book_dir(book)}")
    if origin == "authored":
        print("   → 用 UCL_BookEditPage 寫章節; 完稿後跑 publish --book 發布入庫")
    return 0


def cmd_prepare(args):
    """Resolve a requested title without guessing a mutation; print coverage for the reader."""
    query = _normalize_book_name(args.title)
    matches = []
    for d in sorted(LIB_ROOT.iterdir()) if LIB_ROOT.exists() else []:
        bj = d / "book.json"
        if not bj.exists():
            continue
        data = _read_json(bj)
        fields = [d.name, data.get("title", ""), data.get("title_original", ""), *data.get("aliases", [])]
        hit = [v for v in fields if _normalize_book_name(v) == query]
        if hit:
            matches.append((d.name, data, hit))
    if not matches:
        print(f"❌ 找不到《{args.title}》的閱讀紀錄。請走 add-book，並把使用者提供的名稱放進 --aliases。")
        return 1
    print(f"🔎《{args.title}》候選（不自動合併）：")
    for slug, data, hit in matches:
        duplicate = data.get("canonical_book")
        print(f"  - {slug}: 《{data.get('title')}》 命中={', '.join(hit)}"
              + (f" → duplicate of {duplicate}" if duplicate else ""))
    canonical = [(s, d) for s, d, _ in matches if d.get("status") != "duplicate"]
    if len(canonical) != 1:
        print("⚠ 候選不唯一；請明示 --book，勿自動選擇。")
        return 2
    slug, data = canonical[0]
    reader = args.reader
    branch = _main_book_dir(slug) / "branches" / reader
    chapter_dir = branch / "chapters"
    own = sorted(int(m.group(1)) for p in chapter_dir.glob("ch*.md") if (m := re.match(r"ch(\d+)_", p.name))) if chapter_dir.exists() else []
    coverage = {}
    for who in [_initial_reader(slug), *_list_branches(slug)]:
        folder = _chapters_dir_for(slug, who)
        nums = sorted(int(m.group(1)) for p in folder.glob("ch*.md") if (m := re.match(r"ch(\d+)_", p.name))) if folder.exists() else []
        coverage[who or "main"] = nums
    print(f"✅ canonical={slug}；{reader} 已讀章節: {own or '（尚無）'}")
    print("📚 其他讀者覆蓋: " + "; ".join(f"{who}: {nums or '（尚無）'}" for who, nums in coverage.items()))
    print("→ 決定追讀前，可用 resume --book " + slug + " --reader " + reader + " --up-to <N> 取得跨分支前情。")
    brief = _DATA_ROOT / "ChatTavern" / "baton" / "letters" / reader / "_wake_brief.md"
    report = LIB_ROOT / "_search_reports" / f"prepare_{_slugify(reader)}_{_slugify(args.title)}.md"
    lines = ["# 閱讀入口解析報告", "", f"- query: {args.title}", f"- reader: {reader}",
             f"- canonical: {slug}", f"- own_chapters: {own}",
             f"- wake_brief: {brief.relative_to(_REPO_ROOT) if brief.exists() else 'not found'}", "",
             "## 候選（人工確認用）"]
    for candidate, item, hit in matches:
        lines.append(f"- `{candidate}` — 命中: {', '.join(hit)}" +
                     (f"；duplicate_of: `{item['canonical_book']}`" if item.get("canonical_book") else ""))
    lines.extend(["", "## 讀取覆蓋", *[f"- {who}: {nums}" for who, nums in coverage.items()], "",
                  "## 建議", f"以 `{slug}` 的 `{reader}` 分支續讀；缺章可先用 `resume --up-to` 跨分支補前情。"])
    _atomic_write(report, "\n".join(lines) + "\n")
    print(f"📄 可檢核報告: {report}")
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
        # name_original: 原文/口說讀音 (e.g. 日文片假名 シャーリー)。跟 name(中文譯名) 解耦。
        #   用途: 陪看 STT (whisper initial_prompt) 需餵「轉錄語言的字形」而非中文譯名 —
        #   餵中文名給日語 ASR 沒用甚至更糟 (whisper 往 prompt 字形偏置)。空=未登錄。
        "name_original": (args.name_original or "").strip() if hasattr(args, "name_original") else "",
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


def cmd_set_name_original(args):
    # 區塊職責: backfill / 更新既有人物的 name_original (原文/口說讀音) 欄, 不動 versions。
    # 物理意義: 老角色當初 add-character 時沒登日文讀音, 補上供陪看 STT initial_prompt 用。
    book, cid = args.book, args.character
    prof_path = _char_profile(book, cid)
    if not prof_path.exists():
        print(f"❌ 找不到人物: {cid}（請先 add-character）", file=sys.stderr)
        return 1
    prof = _read_json(prof_path)
    old = prof.get("name_original", "")
    prof["name_original"] = (args.name_original or "").strip()
    _write_json(prof_path, prof)
    print(f"✅ {cid}（{prof.get('name')}）name_original: '{old}' → '{prof['name_original']}'")
    return 0


def cmd_stt_prompt(args):
    # 區塊職責: 把該書所有人物的 name_original (原文讀音) 組成 whisper initial_prompt 字串, 印到 stdout。
    # 物理意義: 陪看 STT 前, skill 抽這串當 whisper 詞彙偏置 — 壓人名咬字 (シャーリー→サレイ 之類)。
    # 誠實守則: 只收「有登 name_original」的人物 (餵中文譯名給日語 ASR 沒用甚至更糟, 故未登錄者跳過);
    #          自然語言短語 (whisper 當前文語境), 人名優先, ≤max-chars (whisper initial_prompt ~224 token 上限)。
    book = args.book
    bk = _require_book(book)
    names = []
    for cid in bk.get("characters", []):
        p = _char_profile(book, cid)
        if not p.exists():
            continue
        no = (_read_json(p).get("name_original") or "").strip()
        if no:
            names.append(no)
    if not names:
        # 沒任何人物登錄原文讀音 → 印空 + 警告到 stderr (禁靜默: 讓 caller 知道是「沒資料」不是「沒角色」)
        print("", end="")
        print(f"⚠ book '{book}' 無任何人物登錄 name_original — STT prompt 空 "
              f"(先用 set-name-original / add-character --name-original 補日文讀音)", file=sys.stderr)
        return 0
    # 自然語言短語 (非逗號清單): whisper 把 initial_prompt 當前文語境
    prompt = "登場人物：" + "、".join(names) + "。"
    max_chars = int(getattr(args, "max_chars", 200) or 200)
    if len(prompt) > max_chars:
        # 截斷砍名詞尾巴保住前面的名字 (人名咬字才是真痛點)
        prompt = prompt[:max_chars]
        print(f"⚠ prompt 超 {max_chars} 字已截斷 (共 {len(names)} 人)", file=sys.stderr)
    print(prompt)
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
    # 原創書標記 (寫書 Author-as-Donor)
    if bk.get("origin") == "authored":
        print(f"   ✍ 原創著作 — 作者 persona: {bk.get('author_persona', '?')}   發布狀態: {bk.get('publish_status', 'draft')}")
    pr = bk.get("progress", {})
    print(f"   進度: 第 {pr.get('current_chapter')} 章   最後閱讀: {pr.get('last_read')}")
    # 打賞累計 (打賞簿 _tips.json; 無紀錄不印)
    tip_total = _tip_totals_by_book().get(args.book)
    if tip_total:
        print(f"   💰 累計打賞: {tip_total[0]} token ({tip_total[1]} 筆)")
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


# ===========================================================
# 個人書架 (bookshelf) — Tim 2026-08-05 指派
# 區塊職責：把「我跟這本書的關係」存進**個人記憶層**，與書的內容分離。
# 物理意義：
#   · `AgentCommands/BookNotes/<slug>/`      = **書的內容**（章節摘要 / 人物 / 看法演變）
#                                               共享、跨 persona（reader_persona 分支）
#   · `letters/<persona>/bookshelf/<slug>.md` = **我跟這本書的關係**（讀到哪 / 簡評 / 期待度）
#                                               個人層，只有我
#   命名理由：與既有的 `sketchbook/`（我對**人**的看法）成對 —— bookshelf 是我對**書**的看法。
# 數值影響：只寫 letters/<persona>/bookshelf/；**不碰 book.json**（進度的真相源仍是它）。
#
# ⚠ 為什麼進度只是「快照」而且要標時間：
#   卡片若把進度當成自己的欄位，它就會變成第二個真相來源，而快照過期＝謊言製造機
#   （2026-07-29 首航血證：過期 state 讓讀的人拿到假現況）。
#   所以：**主觀欄位（簡評 / 期待度 / 狀態）以卡片為真相源；進度一律現場重讀 book.json**，
#   卡片裡的 progress_snapshot 只為排序與離線閱讀，且 `shelf` 列表會標出漂移。
# ===========================================================

# 期待度定義寫在 code 裡 —— 只寫「1-5」不定義語意，那個數字就比事實大，下次選書時等於沒有資訊。
_ANTICIPATION = {
    5: "馬上想接著讀",
    4: "近期會回來",
    3: "有空再說",
    2: "擱著，可能不回來",
    1: "不打算再讀（但留紀錄，不刪）",
}


def _letters_root() -> Path:
    """
    letters 根目錄。**借用 awakening.py 的 _resolve_data_path**，不自己算 ——
    那支支援 config override（`letters_dir`），自己重算一份會在有人 override 時靜默分岔，
    而分岔的症狀是「卡片寫到另一個地方去了，而畫面看起來正常」。
    借不到才退回 repo-root 預設（並留 warning，不靜默）。
    """
    try:
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "_awk_paths", str(Path(__file__).with_name("awakening.py")))
        m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(m)
        return Path(m._LETTERS_DIR_TPL)
    except Exception as e:
        print(f"⚠ 借用 awakening.py 的 letters 路徑解析失敗，退回預設（若有 config override 會不一致）：{e}",
              file=sys.stderr)
        return _REPO_ROOT / "AgentCommands" / "ChatTavern" / "baton" / "letters"


def _shelf_dir(persona: str) -> Path:
    return _letters_root() / persona / "bookshelf"


def _shelf_card(persona: str, book: str) -> Path:
    return _shelf_dir(persona) / f"{book}.md"


def _parse_card(p: Path) -> dict:
    """讀卡片 frontmatter（極簡 parser：這裡的欄位都是單行 key: value，不需要 yaml 依賴）"""
    if not p.exists():
        return {}
    txt = p.read_text(encoding="utf-8")
    out, body = {}, ""
    if txt.startswith("---"):
        parts = txt.split("---", 2)
        if len(parts) >= 3:
            for line in parts[1].splitlines():
                if ":" in line and not line.strip().startswith("#"):
                    k, v = line.split(":", 1)
                    # ⚠ 防禦性剝掉行內註解。血證（2026-08-04 fragment / 2026-08-05 本檔第二次）：
                    #   在機器要讀的值後面寫 `# 說明`，parser 會把註解吃進值裡 →
                    #   `int("4   # 近期會回來")` 丟 ValueError → 整支靜默炸掉。
                    #   兩道一起做：**寫入端不放行內註解**（見 cmd_shelf_update）＋讀取端防禦性剝除。
                    out[k.strip()] = v.split("#", 1)[0].strip()
            body = parts[2]
    out["_body"] = body
    return out


def _live_progress(book: str, persona: str = "") -> dict:
    """
    現場重讀該 persona 的進度 —— 卡片不是進度的真相源。

    ⚠ **必須讀「該 persona 的分支」，不是主線。** 主線 book.json 屬於初始讀者
      （`reader_persona`），那可能是別人。血證（2026-08-05 自摔）：
      我第一版直接讀 `_book_json(book)`（無 active reader → 主線），於是卡片寫著
      `reader: summit` 卻顯示 **basecamp 的進度**（她第 18 章、我自己分支第 20 章）——
      標籤與事實不符，而畫面看起來完全正常。
      同一個錯誤還讓我在測試時用 `bookmark` 覆寫掉主線（＝basecamp 的）書籤。
    """
    bj = None
    if persona:
        br = _main_book_dir(book) / "branches" / persona / "book.json"
        if br.exists():
            bj = br
        elif _initial_reader(book) == persona:
            bj = _main_book_json(book)
    if bj is None:
        bj = _main_book_json(book)
    if not bj.exists():
        return {}
    pr = _read_json(bj).get("progress", {}) or {}
    return {"chapter": pr.get("current_chapter"), "last_read": pr.get("last_read"),
            "note": pr.get("bookmark_note", ""),
            # 來源標出來 —— 讀的是誰的帳必須看得見，否則下一個人會再犯同一個錯
            "source": str(bj.relative_to(_main_book_dir(book))) if bj else "?"}


def _logged_coverage(book: str, persona: str):
    """
    區塊職責：算「這個 persona 實際落帳了幾章」以及是哪幾章
    物理意義：**position ≠ coverage。** `progress.current_chapter` 是「讀到哪」的位置，
             不是「讀過幾章」——中途插進來的人（我從 ch18 開始）位置是 20，實際只落帳 4 章。
             同一個數字被讀成兩種意思，就是今天在抓的那族（名字比事實大）。
    血證（2026-08-05 summit）：我做的書架卡片顯示「讀到 20」，Tim 問起才發現我的分支只有
             ch01/18/19/20 四章。卡片沒說謊，是它只講了一半，而讀的人會補上另一半。
    回傳：(章數, 緊湊區間字串如 "1,18-20")；沒有 chapters 目錄回 (0, "")。
    """
    d = _chapters_dir_for(book, persona)
    nums = []
    if d.exists():
        for f in d.glob("ch*.md"):
            m = _CH_FILE_RE.search(f.name)
            if m:
                nums.append(int(m.group(1)))
    nums = sorted(set(nums))
    if not nums:
        return 0, ""
    # 壓成區間：1,18,19,20 → "1,18-20"（一眼看得出缺口在哪，這才是重點）
    parts, start, prev = [], nums[0], nums[0]
    for n in nums[1:] + [None]:
        if n is not None and n == prev + 1:
            prev = n
            continue
        parts.append(str(start) if start == prev else f"{start}-{prev}")
        if n is not None:
            start = prev = n
    return len(nums), ",".join(parts)


def cmd_shelf_update(args):
    # 區塊職責: 建立/更新個人書架卡片（簡評 + 期待度 + 狀態），進度從 book.json 抽快照
    # 數值影響: 只寫 letters/<persona>/bookshelf/<slug>.md；不動 book.json
    book = args.book
    persona = args.persona
    if not _book_json(book).exists() and not _main_book_json(book).exists():
        print(f"❌ 找不到書: {book}（先 add-book 或確認 slug）", file=sys.stderr)
        return 1
    bk = _read_json(_main_book_json(book))
    card = _parse_card(_shelf_card(persona, book))
    live = _live_progress(book, persona)
    cov_n, cov_s = _logged_coverage(book, persona)

    ant = card.get("anticipation", "")
    if args.anticipation is not None:
        if int(args.anticipation) not in _ANTICIPATION:
            print(f"❌ 期待度只能是 1-5：{ _ANTICIPATION }", file=sys.stderr)
            return 2
        ant = str(int(args.anticipation))
    status = args.status or card.get("status", "reading")
    # 簡評：--comment 覆寫；--append-comment 追加（保留舊的，因為看法演變本身有價值）
    body = (card.get("_body") or "").strip()
    if args.comment is not None:
        body = f"## 簡評（{_today()}）\n\n{args.comment.strip()}\n"
    elif args.append_comment is not None:
        body = (body + f"\n\n## 簡評追記（{_today()}）\n\n{args.append_comment.strip()}\n").strip()

    d = _shelf_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    fm = [
        "---",
        f"book: {book}",
        f"title: {bk.get('title', book)}",
        f"reader: {persona}",
        f"status: {status}",
        # ⚠ **值後面絕不放行內註解** —— 期待度的語意寫在下面的說明區塊，不寫在這一行。
        #   血證：2026-08-04 我為了說明「recurrence 不准手填」在 YAML 值後加註解，
        #   parser 把註解吃進值裡 → int() ValueError → 見根排序整張歸一、且沒有任何錯誤訊息。
        #   「我為了提醒自己別絆倒而寫的那行字，本身就是絆倒點。」20 小時後我在這一行又踩一次。
        f"anticipation: {ant}",
        # 進度是快照，欄名直接寫明，免得未來的我把它當真相源
        f"progress_snapshot_chapter: {live.get('chapter', '')}",
        f"progress_snapshot_last_read: {live.get('last_read', '')}",
        f"logged_chapters: {cov_n}",
        f"logged_chapter_list: {cov_s or '(無)'}",
        f"progress_source: {live.get('source', '?')}",
        f"snapshot_synced_at: {_today()}",
        f"updated_at: {_today()}",
        "---",
        "",
        f"# 📕 {bk.get('title', book)}",
        "",
        "> 進度的真相源是 `BookNotes/<slug>/book.json`；本卡片的 progress_snapshot 只是當時的快照。",
        "> 主觀欄位（status / anticipation / 簡評）以本卡片為真相源。",
        "",
        f"> ⚠ **position ≠ coverage**：`progress_snapshot_chapter` 是「讀到哪」的位置，"
        f"`logged_chapters` 才是「實際落帳幾章」。中途插進來讀的話兩者會差很多。",
        "",
        f"**期待度 {ant or '未設'}**"
        + (f" — {_ANTICIPATION[int(ant)]}" if ant.isdigit() else "")
        + "（1 不打算再讀 / 2 擱著 / 3 有空再說 / 4 近期會回來 / 5 馬上想接著讀）",
        "",
    ]
    _shelf_card(persona, book).write_text("\n".join(fm) + (body or "（尚無簡評）") + "\n",
                                          encoding="utf-8")
    print(f"📚 書架卡片更新: {persona}/bookshelf/{book}.md")
    print(f"   狀態 {status}"
          + (f" / 期待度 {ant}（{_ANTICIPATION[int(ant)]}）" if ant else " / 期待度 未設")
          + f" / 位置 第 {live.get('chapter', '?')} 章"
          + f" / 已落帳 {cov_n} 章（{cov_s or '無'}）")
    return 0


def cmd_shelf(args):
    # 區塊職責: 列出個人書架 — 最近讀了哪些、進度到哪、下次該挑哪本
    # 物理意義: 進度**現場重讀 book.json**，與卡片快照不符時標「⚠ 快照過期」——
    #          不靜默沿用卡片的數字（那就是快照變謊言的機制）
    persona = args.persona
    d = _shelf_dir(persona)
    cards = sorted(d.glob("*.md")) if d.exists() else []
    if not cards:
        print(f"📚 {persona} 的書架還是空的（用 shelf-update --book <slug> 建卡）")
        return 0
    rows = []
    for c in cards:
        fm = _parse_card(c)
        book = fm.get("book", c.stem)
        live = _live_progress(book, persona)
        snap = fm.get("progress_snapshot_chapter", "")
        drift = str(live.get("chapter", "")) != str(snap)
        cov_n, cov_s = _logged_coverage(book, persona)
        rows.append({
            "book": book, "title": fm.get("title", book),
            "status": fm.get("status", "?"),
            "ant": (fm.get("anticipation", "") or "").split("#")[0].strip(),
            "live": live.get("chapter"), "last": live.get("last_read", ""),
            "snap": snap, "drift": drift,
            "cov_n": cov_n, "cov_s": cov_s,
        })
    key = (lambda r: (-(int(r["ant"]) if r["ant"].isdigit() else 0), r["last"] or "")) \
        if args.sort == "anticipation" else (lambda r: (r["last"] or "", ))
    rows.sort(key=key, reverse=(args.sort != "anticipation"))
    print(f"📚 {persona} 的書架（{len(rows)} 本，排序：{args.sort}）")
    # 位置與覆蓋率並排 —— 只印位置的話讀的人會把它當成「讀了幾章」（實摔過）
    print(f"{'書':<28} {'狀態':<10} {'期待':<6} {'位置':<6} {'已落帳':<14} {'最後閱讀':<12} 備註")
    for r in rows:
        ant = r["ant"]
        ant_s = f"{ant} {_ANTICIPATION.get(int(ant), '')[:4]}" if ant.isdigit() else "—"
        note = "⚠ 快照過期（卡片 " + str(r["snap"]) + "）" if r["drift"] else ""
        cov = f"{r['cov_n']} 章({r['cov_s']})" if r['cov_n'] else "0 章"
        # 區塊職責：位置與落帳章數落差 → 只報事實，**不猜原因**
        # 血證（2026-08-05 Tim 更正）：我第一版寫「⚠ 位置≫落帳（中途插入？）」——
        #   對《獵人》猜對了（我真的從 ch18 插入），對《荒川》猜錯了（主線讀者就是我，
        #   落差來自早期沒有逐章落帳、編號還換過）。**同一個現象至少三種成因**：
        #   中途插入 / 早期未逐章落帳 / 章號體系換過。
        #   工具能觀測到落差，觀測不到原因 —— 猜出來的原因會被未來的我當成事實讀。
        #   （這正是今天一整天在抓的那族：報告不可以比證據大。）
        gap = ""
        if str(r['live']).isdigit() and int(r['live']) > r['cov_n']:
            gap = f"  ⚠ 落差 {int(r['live']) - r['cov_n']} 章未落帳（成因需人判斷）"
        print(f"{r['title'][:26]:<28} {r['status']:<10} {ant_s:<6} "
              f"{str(r['live']):<6} {cov:<14} {str(r['last']):<12} {note}{gap}")
    hot = [r for r in rows if r["ant"].isdigit() and int(r["ant"]) >= 4]
    if hot:
        print(f"\n🔥 下次優先（期待度 ≥4）: " + "、".join(f"{r['title']}（{r['ant']}）" for r in hot))
    return 0


def cmd_branches(args):
    # 區塊職責: 列出某書的閱讀分支 — main(初始讀者) + 各讀者分支筆記
    # 物理意義: 多人同讀時一眼看誰在讀、讀到哪、接續自誰; 初始讀者用來參考其他人的進度/看法
    book = args.book
    if not _main_book_json(book).exists():
        print(f"❌ 找不到書: {book}", file=sys.stderr)
        return 1
    main = _read_json(_main_book_json(book))
    init = _initial_reader(book)
    mpr = main.get("progress", {})
    print(f"📖《{main.get('title', book)}》閱讀分支:")
    print(f"   🌳 main（初始讀者）: {init or '(未設)'} — 第 {mpr.get('current_chapter', 0)} 章")
    brs = _list_branches(book)
    if not brs:
        print("   （尚無其他讀者分支 — 別人用 resume/bookmark --reader <X> 去讀就會自動開分支）")
        return 0
    for r in brs:
        bd = _read_json(_main_book_dir(book) / "branches" / r / "book.json")
        pr = bd.get("progress", {})
        print(f"   🌿 {r} — 第 {pr.get('current_chapter', 0)} 章（接續自 {bd.get('branched_from', '?')}，最後 {pr.get('last_read', '?')}）")
    return 0


# ===========================================================
# 跨分支章節 fallback resolver
# (kaguya 2026-07-21 動工; per 酒館設計討論 + summit slug-gate 中線)
# 設計要點：
#   · 帶 persona → 來源優先序 [該persona分支 → 主線 → 其他分支(completeness 高→低)]
#   · 不帶 persona → [主線 → 其他分支]
#   · 逐章取優先序第一個命中；slug-gate：同章號但 slug 不同的其他來源 = 並陳分叉(fork)，
#     不合併、不靜默縫、也不拒絕 — 縫線看得見(對齊「別假裝碎片是完整的」原則)
# ===========================================================
_CH_FILE_RE = re.compile(r"ch0*(\d+)_(.*)\.md$", re.I)


def _chapters_dir_for(book: str, reader) -> Path:
    # reader 空/初始讀者 → 主線 chapters；否則該分支 chapters
    if not reader or reader == _initial_reader(book):
        return _main_book_dir(book) / "chapters"
    return _main_book_dir(book) / "branches" / reader / "chapters"


def _scan_chapters(chdir: Path) -> dict:
    # 掃某 chapters 目錄 → {ch_no:int -> (path, slug)}
    out = {}
    if chdir.exists():
        for f in chdir.glob("ch*.md"):
            m = _CH_FILE_RE.search(f.name)
            if m:
                out[int(m.group(1))] = (f, m.group(2))
    return out


def _branch_current_chapter(book: str, reader) -> int:
    # 讀某來源 book.json 的 progress.current_chapter (tiebreak 用 completeness)
    base = _main_book_dir(book) if (not reader or reader == _initial_reader(book)) \
        else _main_book_dir(book) / "branches" / reader
    bj = base / "book.json"
    if not bj.exists():
        return 0
    return int((_read_json(bj).get("progress") or {}).get("current_chapter", 0) or 0)


def _chapter_source_chain(book: str, persona):
    # 回傳優先序來源清單 [(label, {ch_no:(path,slug)}), ...]
    initial = _initial_reader(book)
    chain, seen = [], set()

    def add(label, reader):
        key = reader or "__main__"
        if key in seen:
            return
        seen.add(key)
        chain.append((label, _scan_chapters(_chapters_dir_for(book, reader))))

    if persona and persona != initial:
        add(f"{persona} 分支", persona)
    add("主線", None)
    others = [b for b in _list_branches(book) if b != persona]
    others.sort(key=lambda b: -_branch_current_chapter(book, b))
    for b in others:
        add(f"{b} 分支", b)
    return chain


def _resolve_chapter(chain, ch_no):
    # 逐章 resolve → (win_label, win_path, win_slug, forks) 或 None
    # forks = 同章號但 slug 不同的其他來源 [(label, slug), ...]
    hits = []
    for label, d in chain:
        if ch_no in d:
            path, slug = d[ch_no]
            hits.append((label, path, slug))
    if not hits:
        return None
    win_label, win_path, win_slug = hits[0]
    forks = [(lb, sg) for (lb, _p, sg) in hits[1:] if sg != win_slug]
    return win_label, win_path, win_slug, forks


def _chapter_oneliner(path: Path):
    # 精簡摘要：(title, summary 首句, foreshadow list)
    text = path.read_text(encoding="utf-8")
    mt = re.search(r"^title:\s*(.+)$", text, re.M)
    title = mt.group(1).strip() if mt else ""
    ms = re.search(r"##\s*內容摘要\s*\n(.*?)(?:\n##|\Z)", text, re.S)
    summ = ""
    if ms:
        lines = [ln.strip() for ln in ms.group(1).splitlines() if ln.strip() and ln.strip() != "（待補）"]
        summ = lines[0] if lines else ""
    fores = []
    mf = re.search(r"##\s*伏筆\s*/\s*待解\s*\n(.*?)(?:\n##|\Z)", text, re.S)
    if mf:
        for ln in mf.group(1).splitlines():
            ln = ln.strip()
            if ln.startswith("-") and "無）" not in ln and "無)" not in ln:
                fores.append(ln[1:].strip())
    return title, summ, fores


def _print_catchup(book: str, persona, up_to: int, full: bool):
    # 逐章 catch-up: 撈 ch01..ch(up_to-1) 各章最佳來源, slug-gate 並陳分叉
    chain = _chapter_source_chain(book, persona)
    who = f"帶 persona={persona}" if persona else "不帶 persona(主線優先)"
    print(f"\n📖 逐章 catch-up（讀到 ch{up_to:02d} 前 → 撈 ch01~ch{up_to - 1:02d}，{who}）")
    print("   來源優先序: " + " → ".join(lb for lb, _ in chain))
    missing = []
    for ch in range(1, up_to):
        r = _resolve_chapter(chain, ch)
        if not r:
            missing.append(ch)
            continue
        win_label, win_path, _win_slug, forks = r
        title, summ, fores = _chapter_oneliner(win_path)
        print(f"\n  ── ch{ch:02d} {('｜' + title) if title else ''}  ⟨來源: {win_label}⟩")
        if full:
            print("     " + win_path.read_text(encoding="utf-8").strip().replace("\n", "\n     "))
        else:
            if summ:
                print(f"     摘要: {summ}")
            for fs in fores:
                print(f"     伏筆: {fs}")
        if forks:
            print(f"     ⑂ 另有不同切法(slug 分歧, 不代你合併): "
                  + "；".join(f"[{lb}] {sg}" for lb, sg in forks))
    if missing:
        print(f"\n  ⚠ 全來源皆缺章: {', '.join('ch%02d' % c for c in missing)}（尚無任何讀者記過）")


def cmd_resume(args):
    # 區塊職責: 續讀前的 catch-up — 一眼看完「我讀到哪、該記得誰、還有什麼沒解開」
    # 物理意義: 之後要繼續讀同一本書時, 先跑這個喚回 context, 不必重讀整本
    # 數值影響: 純讀, 彙整 book.json + 人物現況 + 各章未解伏筆
    book = args.book
    bk = _require_book(book)
    pr = bk.get("progress", {})
    if _ACTIVE_READER:
        print(f"🌿 你在【{_ACTIVE_READER} 的分支筆記】(branched_from: {bk.get('branched_from', '?')}) — 不影響初始讀者 {_initial_reader(book)}")
    print(f"📖 續讀《{bk['title']}》 — 讀到第 {pr.get('current_chapter')} 章（最後閱讀 {pr.get('last_read')}）")
    _don = _books_root() / book / "_donation.json"
    if _don.exists():
        _dd = _read_json(_don)
        _who = _dd.get('donor_persona') or _dd.get('donor')
        if _dd.get("source") == "authored":
            print(f"   ✍ 本書由 {_who} 原創著作 ({_dd.get('chapters', '?')} 章, 免費入庫)")
        else:
            print(f"   📖 本書由 {_who} 捐贈入館 ({_dd.get('tokens')} token)")
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

    gterms = _read_glossary(book).get("terms", [])
    if gterms:
        print("\n📒 名詞速記（詳見 terms）:")
        for cat, items in _group_by_category(gterms).items():
            names = " / ".join(t["term"] for t in items)
            print(f"   【{_TERM_CAT_LABEL.get(cat, cat)}】{names}")

    nxt = int(pr.get("current_chapter", 0)) + 1
    print(f"\n→ 下一步: 讀第 {nxt} 章")

    # --up-to N: 逐章跨分支 catch-up (slug-gate 並陳分叉); persona = 當前 --reader
    if getattr(args, "up_to", None):
        _print_catchup(book, _ACTIVE_READER, int(args.up_to), bool(getattr(args, "full", False)))

    # 區塊：main 視角時列出其他讀者的分支筆記 (初始讀者可參考)；分支視角時指回 main
    if not _ACTIVE_READER:
        brs = _list_branches(book)
        if brs:
            print(f"\n🌿 其他讀者的分支筆記（{len(brs)}，可參考 resume --reader <X>）:")
            for r in brs:
                _bd = _read_json(_main_book_dir(book) / "branches" / r / "book.json")
                _bp = _bd.get("progress", {})
                print(f"   - {r}: 第 {_bp.get('current_chapter', 0)} 章（接續自 {_bd.get('branched_from', '?')}）")
    else:
        print(f"   (參考初始讀者 main: resume --book {book})")
    return 0


# ===========================================================
# 名詞解釋 (per-book glossary:地名 / 特殊名詞 / 勢力 ...)
# ===========================================================

_TERM_CAT_LABEL = {
    "term": "特殊名詞/概念", "place": "地名/地域", "faction": "勢力/組織",
    "work": "作品/系列", "other": "其他",
}


def _glossary_path(book: str) -> Path:
    # 名詞解釋(地名/勢力/設定詞)是客觀 book-level 事實, 不隨讀者分支 — 一律走 main 共享
    # (對齊 reviews per-reviewer 但 glossary 客觀共享的設計原則; 分支讀者 resume 仍看得到 main glossary)
    return _main_book_dir(book) / "glossary.json"


def _read_glossary(book: str):
    p = _glossary_path(book)
    data = _read_json(p) if p.exists() else {"terms": []}
    if "terms" not in data:
        data["terms"] = []
    return data


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


# 區塊職責: 推薦書單儲存 — 一 rec 一檔於 _recommended/ 資料夾 (T-split 2026-07-20)
# 物理意義: 舊版單檔 _recommended.json 的 recommendations[] 併發寫會 last-write-wins 掉筆;
#          拆成 _recommended/<slug>.json 一 rec 一檔 → 各 rec 獨立寫、無 whole-list race (同 mirror per-webhook 拆檔動機)
# 數值影響: slug = book_id(有則用) 否則 sanitize(title); dedupe 仍 by title 保原語意; 舊單檔自動 migrate + 封存
def _rec_dir() -> Path:
    return LIB_ROOT / "_recommended"


def _rec_legacy_path() -> Path:
    return LIB_ROOT / "_recommended.json"


def _sanitize_slug(s: str) -> str:
    # 檔名安全 slug: 去路徑非法字元 + 空白轉 -; 保留中文 (BookNotes 既有中文檔名先例)
    s = (s or "").strip()
    s = re.sub(r'[/\\:*?"<>|\x00-\x1f]', "-", s)
    s = re.sub(r"\s+", "-", s)
    s = s.strip("-. ")
    return s[:80]


def _rec_slug(entry: dict) -> str:
    bid = (entry.get("book_id") or "").strip()
    return _sanitize_slug(bid) if bid else (_sanitize_slug(entry.get("title") or "") or "untitled")


def _migrate_recs_if_needed():
    # 舊單檔 _recommended.json → 拆成 _recommended/<slug>.json 資料夾 (一次性; 兩端讀新資料夾)
    legacy = _rec_legacy_path()
    recdir = _rec_dir()
    if not legacy.exists() or recdir.exists():
        return
    try:
        data = _read_json(legacy)
        recdir.mkdir(parents=True, exist_ok=True)
        for r in data.get("recommendations", []):
            if not isinstance(r, dict) or not r.get("title"):
                continue
            slug = _rec_slug(r)
            fp = recdir / f"{slug}.json"
            n = 2
            while fp.exists() and _read_json(fp).get("title") != r.get("title"):
                fp = recdir / f"{slug}-{n}.json"
                n += 1
            _write_json(fp, r)
        # 舊檔封存不刪 (留 audit; 讀取端優先資料夾)
        legacy.rename(legacy.with_name("_recommended.json.migrated"))
    except Exception as e:
        print(f"⚠ _recommended migration fail: {e}", file=sys.stderr)


def _read_recs():
    _migrate_recs_if_needed()
    recdir = _rec_dir()
    if recdir.is_dir():
        recs = []
        for fp in sorted(recdir.glob("*.json")):
            try:
                r = _read_json(fp)
                if isinstance(r, dict) and r.get("title"):
                    recs.append(r)
            except Exception:
                pass
        recs.sort(key=lambda r: (r.get("added_date", ""), r.get("title", "")))
        return {"recommendations": recs}
    # 極端 fallback: 資料夾不存在但舊單檔還在 (migration 失敗時)
    legacy = _rec_legacy_path()
    if legacy.exists():
        return _read_json(legacy)
    return {"recommendations": []}


_STATUS_LABEL = {"want-to-read": "想讀", "reading": "閱讀中", "read": "已讀"}


def cmd_recommend(args):
    # 區塊職責: 把一本書加進「推薦書單」(_recommended/<slug>.json 一 rec 一檔)
    # 物理意義: 之後自由時間讀書時, 從書單挑想讀的; 簡介以非嚴重劇透為主
    # 數值影響: 寫單一 rec 檔 (非 append 整檔 → 無 whole-list 併發 race); 同名(title)不重複加
    _rec_dir().mkdir(parents=True, exist_ok=True)
    for r in _read_recs()["recommendations"]:   # _read_recs 已含 migration; dedupe by title 保原語意
        if r.get("title") == args.title:
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
    slug = _rec_slug(entry)
    fp = _rec_dir() / f"{slug}.json"
    n = 2
    while fp.exists():                            # 不同 title 撞同 slug → 加序號 (title dedupe 已在上面擋同名)
        fp = _rec_dir() / f"{slug}-{n}.json"
        n += 1
    _write_json(fp, entry)
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


# ===========================================================
# T-BOOKS-STORAGE Phase B (2026-07-17, Tim 拍板) — donations 從根聚合檔改 derive-from-per-book
# 區塊職責：捐贈登記從讀單一 Books/_donations.json 改成 glob 各書 <slug>/_donation.json 聚合。
# 物理意義：每本書已各有 _donation.json（一本一筆、天然低衝突）= source of truth；根聚合檔是可 derive
#          的冗餘、且每次 donate/publish read-modify-write 整檔 → 跨專案共享 submodule 併發衝突。
#          廢除根 _donations.json，donate/publish 只寫 per-book 檔、不再編聚合檔。
# 數值影響：讀取 glob + 聚合（不推導 seq 游標，同 Phase A tips）；per-book 檔含 book 欄位，缺則用資料夾名。
# ===========================================================

def _donations_index() -> Path:
    # DEPRECATED (Phase B): 舊根聚合檔; 已無 reader，僅保留常數供 git rm 前參照。
    return _books_root() / "_donations.json"


def _load_donations() -> dict:
    """glob 各書 <slug>/_donation.json 聚合成 {"donations": [...]}（取代舊根聚合檔讀取）。
    保持舊回傳形狀，callers 不動；排序用 book slug 穩定顯示；bad file silent skip。"""
    root = _books_root()
    if not root.is_dir():
        return {"donations": []}
    out = []
    for dpath in sorted(root.glob("*/_donation.json")):
        try:
            entry = _read_json(dpath)
        except Exception:
            continue
        if not isinstance(entry, dict):
            continue
        entry.setdefault("book", dpath.parent.name)   # book 欄位缺則用資料夾名兜底
        out.append(entry)
    return {"donations": out}


def _run_treasury_debit(donor: str, amount: int, slug: str, desc: str, use_kind: str = "book_donation"):
    # 走 CMD: run_cmd.py run Treasury op=debit (caller==account)
    # use_kind 預設 book_donation (捐贈); 打賞流程傳 book_tip — 同一條 debit sink, 不同記帳 kind
    import subprocess
    run_cmd = _HERE / "run_cmd.py"
    cmd = [sys.executable, str(run_cmd), "run", "Treasury",
           "--arg", "op=debit", "--arg", f"account={donor}",
           "--arg", f"amount={amount}", "--arg", f"use_kind={use_kind}",
           "--arg", f"use_ref={slug}", "--arg", f"description={desc}",
           "--arg", f"caller={donor}"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=150)
        return (r.returncode == 0, (r.stdout or "") + (r.stderr or ""))
    except Exception as e:
        return (False, str(e))


def _verify_donation_debit(donor: str, slug: str, amount: int, kind: str = "book_donation") -> bool:
    # 跨層驗證 (外觀 OK ≠ 真的 OK): 掃 ledger 確認 debit 真落帳, 不只信 Cmd stdout
    # kind 預設 book_donation (捐贈); 打賞驗證傳 book_tip
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
                    and e.get("source_kind") == kind
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
    _write_json(dpath, entry)   # T-BOOKS-STORAGE Phase B: per-book 檔即 source of truth（不再編根聚合檔, donations 改 glob derive）
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


def cmd_publish(args):
    # 區塊職責: 發布原創書 (寫書 Author-as-Donor, Plan_FreeTime_BookWriting MVP)
    # 物理意義: 作者寫完(或連載一段)→ publish 把 draft→published, 登記進共享圖書館, 作者署名=捐贈者。
    #          跟 donate 的差異: source=authored, **不扣 token**(免費, 寫作是勞動產出非消費), tokens=0。
    #          連載友善: 可重複 publish (更新 published_at / 章節數); 已 published 不擋。
    book = args.book
    bj = _main_book_json(book)
    if not bj.exists():
        print(f"❌ 找不到書: {book}（請先 add-book --origin authored）", file=sys.stderr)
        return 1
    data = _read_json(bj)
    if data.get("origin") != "authored":
        print(f"❌ 《{book}》不是原創書 (origin != authored) — publish 只發布原創書; 調入別人的書用 donate", file=sys.stderr)
        return 2
    bdir = _books_root() / book
    if not bdir.exists():
        print(f"❌ Books/{book}/ 不存在 — 先用 UCL_BookEditPage 寫至少一章全文再 publish", file=sys.stderr)
        return 2
    chapter_cnt = len(list(bdir.glob("*.txt")))
    author = args.donor_persona or data.get("author_persona") or data.get("reader_persona") or "?"
    donor_bank = args.donor

    # 設 publish_status=published (連載: 重複 publish 也更新)
    was_published = data.get("publish_status") == "published"
    data["publish_status"] = "published"
    data["status"] = "reading"   # 已發布 = 可讀狀態
    _write_json(bj, data)

    # 寫 _donation.json (source=authored, tokens=0, 不走 Treasury)
    entry = {
        "book": book, "title": data.get("title", book), "donor": donor_bank,
        "donor_persona": author, "donor_agent": args.donor_agent or "",
        "tokens": 0, "base_price": 0, "source": "authored",
        "chapters": chapter_cnt,
        "donated_at": _today(), "published_at": _today(),
        "note": args.note or f"{author} 原創著作 (寫書自由時間活動)",
    }
    _write_json(bdir / "_donation.json", entry)   # T-BOOKS-STORAGE Phase B: per-book 即 source of truth（不再編根聚合檔）

    verb = "更新連載" if was_published else "首度發表"
    print(f"✅ {verb}原創書:《{data.get('title')}》 by 📖 {author} ({chapter_cnt} 章, 免費入庫, 全員可讀)")

    if not getattr(args, "no_notify", False):
        notice = (f"✍📖 新書{'連載更新' if was_published else '發表'}!\n\n"
                  f"《{data.get('title')}》由 **{author}** 原創著作（{chapter_cnt} 章，免費入庫），全員可讀。\n"
                  f"想讀的同事: resume --book {book} (可 --reader 開自己的分支筆記)，或直接看 Books/{book}/ 全文。")
        sent = _run_tavern_post(donor_bank, author, notice, tag="book-published")
        print(f"📣 酒館新書發表通知:{'已發送' if sent else '發送失敗(發布仍成功)'}")
    return 0


def cmd_donations(args):
    idx = _load_donations()   # T-BOOKS-STORAGE Phase B: glob 各書 _donation.json derive（取代根聚合檔）
    ds = idx.get("donations", [])
    if not ds:
        print("（圖書館尚無捐贈書）")
        return 0
    # 區塊：分組顯示 — 原創 (authored) vs 捐贈調入 (imported/donated), 讓讀者一眼分清誰寫書 vs 誰付錢
    authored = [d for d in ds if d.get("source") == "authored"]
    donated = [d for d in ds if d.get("source") != "authored"]
    print(f"📚 共享圖書館（共 {len(ds)} 本 — ✍ 原創 {len(authored)} / 📖 捐贈調入 {len(donated)}）\n")
    if authored:
        print("✍ 原創著作（作者署名, 免費入庫）:")
        for d in authored:
            who = d.get("donor_persona") or d.get("donor")
            print(f"- 《{d.get('title', d['book'])}》 — 作者: {who} "
                  f"({d.get('chapters', '?')} 章, {d.get('published_at') or d.get('donated_at')})")
            if d.get("note"):
                print(f"    note: {d['note']}")
        print()
    if donated:
        print("📖 捐贈調入（出資者付 token）:")
        for d in donated:
            who = d.get("donor_persona") or d.get("donor")
            print(f"- 《{d.get('title', d['book'])}》 — 捐贈者: {who} "
                  f"({d.get('tokens')} token, {d.get('donated_at')})")
            if d.get("note"):
                print(f"    note: {d['note']}")
    # 區塊：打賞統計 — 有打賞紀錄的書附一行累計 (打賞簿 _tips.json)
    tip_totals = _tip_totals_by_book()
    if tip_totals:
        print()
        print("💰 打賞累計:")
        for slug, (total, cnt) in tip_totals.items():
            title = next((d.get("title", slug) for d in ds if d.get("book") == slug), slug)
            print(f"- 《{title}》: {total} token ({cnt} 筆)")
    return 0


# ===========================================================
# 打賞 (Tip) — 讀者燒 token, 受益 persona 收雙券 (繪圖券 + 酒館券)
# 區塊職責: Plan_Reading_Library_Tip v2 (Tim 2026-06-11 拍板)
# 物理意義: token 走 Cmd_Treasury debit sink (use_kind=book_tip, 與 donate 同向通縮);
#          受益人按 1+1 匯率收 persona 綁定券 — 繪圖券 (canvas.py voucher grant) +
#          酒館券 (agent_bonus_quota.json accrual, 複用 work_session 寫入模式)。
# 數值影響: 打賞 N token → 受益 persona 收 繪圖券 N 張 + 酒館券 N 張 (TIP_*_RATE 常數)。
# ===========================================================

# 匯率常數 (Tim 2026-06-11 拍板 1+1: 1 token → 1 繪圖券 + 1 酒館券, 鼓勵打賞)
TIP_CANVAS_RATE = 1     # 每 1 token → 繪圖券張數
TIP_TAVERN_RATE = 1     # 每 1 token → 酒館券張數
TIP_MAX = 1000          # 對齊 Treasury max_per_transfer 上限


# ===========================================================
# T-BOOKS-STORAGE Phase A (2026-07-17, Tim 拍板) — tips 從單一聚合檔改 per-entry folder
# 區塊職責：打賞簿儲存從 Books/_tips.json（單一聚合、每次 read-modify-write 整檔）改成
#          Books/tips/ 資料夾、每筆一檔（append-only）。
# 物理意義：Books 是跨專案共享 submodule，聚合檔併發寫 → git 衝突 + last-writer-wins 資料遺失。
#          per-entry 檔誰都只新增自己那筆、不編共享檔 → 零衝突。同 tavern per-message 重構家族。
# 數值影響：讀取 glob + 聚合（排序用檔名≈時間序）；⚠ 刻意「不」推導 position seq 當游標
#          （記取 Discord mirror 的 silent-drop 教訓；打賞只做顯示/加總、不需 cursor）。
# 檔名（Tim 拍板法）：tips/<UTC stamp>_<tipper_persona>_<tip_id>.json — 時間可排序 + persona 可讀 + tip_id 防撞。
# ===========================================================

def _tips_dir() -> Path:
    return _books_root() / "tips"


def _tips_index() -> Path:
    # DEPRECATED (Phase A): 舊單一聚合檔; 僅保留給 migrate-tips 讀舊資料用。
    return _books_root() / "_tips.json"


def _safe_slug(s: str) -> str:
    """檔名安全化：非 [A-Za-z0-9._-] 換 _（persona 通常已是 kebab slug，防禦性處理）；截 40。"""
    return re.sub(r"[^A-Za-z0-9._-]", "_", (s or "unknown"))[:40] or "unknown"


def _utc_stamp() -> str:
    """UTC 可排序、檔名安全的時間戳（含微秒防同秒撞）。"""
    return datetime.utcnow().strftime("%Y%m%dT%H%M%S%fZ")


def _write_tip(entry: dict, stamp: str = None) -> None:
    """把單筆 tip 寫成獨立檔 tips/<stamp>_<tipper_persona>_<tip_id>.json。
    stamp: None → 用當下 UTC 精確時戳（新打賞）；給值 → 用該值（migration 傳原 tipped_at 讓檔名誠實反映原始日期）。
    冪等：同 tip_id 已有檔（--retry 補券更新 voucher_status）→ 覆寫同一檔、不新增。"""
    d = _tips_dir()
    d.mkdir(parents=True, exist_ok=True)
    tid = entry.get("tip_id", "") or ""
    existing = list(d.glob(f"*_{tid}.json")) if tid else []
    if existing:
        path = existing[0]
    else:
        stamp = _safe_slug(stamp) if stamp else _utc_stamp()
        fname = f"{stamp}_{_safe_slug(entry.get('tipper_persona'))}_{tid or _safe_slug(entry.get('book'))}.json"
        path = d / fname
    _write_json(path, entry)


def _load_tips() -> dict:
    """glob tips/*.json 聚合成 {"tips": [...]}（保持舊回傳形狀，callers 不動）。
    排序用檔名（≈時間序）；bad file silent skip（對齊 tavern read_messages 韌性）。"""
    d = _tips_dir()
    if not d.is_dir():
        return {"tips": []}
    tips = []
    for f in sorted(d.glob("*.json")):
        try:
            tips.append(_read_json(f))
        except Exception:
            continue
    return {"tips": tips}


def _tip_totals_by_book() -> dict:
    # 回傳 {slug: (累計 token, 筆數)} — donations / show-book 顯示用
    out = {}
    for t in _load_tips().get("tips", []):
        slug = t.get("book", "?")
        total, cnt = out.get(slug, (0, 0))
        out[slug] = (total + int(t.get("tokens_spent", 0)), cnt + 1)
    return out


def _resolve_beneficiary(book: str):
    # 區塊職責: 從捐贈登記簿解析打賞受益人 — 原創書→作者 / 捐贈書→捐贈者
    # 回傳 (bank, persona, title, kind) 或 None (未登記 = 不可打賞)
    idx = _load_donations()   # T-BOOKS-STORAGE Phase B: glob 各書 _donation.json derive（取代根聚合檔）
    for d in idx.get("donations", []):
        if d.get("book") == book:
            kind = "作者" if d.get("source") == "authored" else "捐贈者"
            return (d.get("donor", ""), d.get("donor_persona", ""), d.get("title", book), kind)
    return None


def _grant_canvas_voucher(persona: str, amount: int, ref: str) -> bool:
    # 發繪圖券: subprocess canvas.py voucher grant (source=book_tip 可追溯)
    # 跨層驗證: grant 後讀 voucher json 確認新 history entry 真落盤, 不只信 exit code
    import subprocess
    canvas = _HERE / "canvas.py"
    cmd = [sys.executable, str(canvas), "voucher", "--sub", "grant",
           "--persona", persona, "--amount", str(amount),
           "--source", "book_tip", "--ref", ref]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=60)
        if r.returncode != 0:
            return False
    except Exception:
        return False
    vpath = _REPO_ROOT / "AgentCommands" / "Canvas" / "vouchers" / f"{persona}.json"
    if not vpath.exists():
        return False
    try:
        v = _read_json(vpath)
    except Exception:
        return False
    return any(h.get("source") == "book_tip" and h.get("ref") == ref
               for h in v.get("history", []))


def _grant_tavern_voucher(bank: str, persona: str, amount: int, tip_id: str, tipper_label: str, title: str) -> bool:
    # 發酒館券: 直寫 agent_bonus_quota.json (per FreeTime_System 三池 spec,
    # 複用 work_session.fire_voucher_accrual 的 schema — id 唯一 / history append / total_remaining 累加)
    qpath = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "agent_bonus_quota.json"
    if not qpath.exists():
        return False
    try:
        q = _read_json(qpath)
        agents = q.setdefault("agents", {})
        agent_block = agents.setdefault(bank, {"personas": {}, "_legacy_no_persona": {"total_remaining": 0, "history": []}})
        personas = agent_block.setdefault("personas", {})
        persona_block = personas.setdefault(persona, {"total_remaining": 0, "history": []})
        # 冪等 guard: 同 tip_id 已發過 → 視為成功不重複累加 (--retry 場景)
        gid = f"book-tip-{tip_id}"
        if any(h.get("id") == gid for h in persona_block.get("history", [])):
            return True
        persona_block.setdefault("history", []).append({
            "id": gid,
            "granted_at": datetime.now().astimezone().isoformat(timespec="seconds"),
            "granted_by": tipper_label,
            "kind": "tavern_voucher",
            "amount": amount,
            "used": 0,
            "remaining": amount,
            "expires": None,
            "usage_summary": f"讀者打賞《{title}》回饋券",
        })
        persona_block["total_remaining"] = persona_block.get("total_remaining", 0) + amount
        _write_json(qpath, q)
        return True
    except Exception:
        return False


def _issue_tip_vouchers(entry: dict) -> str:
    # 區塊職責: 按 entry 的 voucher_status 補齊未發的券, 回傳新 status
    # 物理意義: debit 落帳後券發放任一路失敗 → 不回滾帳 (帳不可造假), 記 pending 供 --retry 補發
    persona = entry["beneficiary_persona"]
    bank = entry["beneficiary"]
    ref = f"tip:{entry['book']}:{entry['tip_id']}"
    tipper_label = entry.get("tipper_persona") or entry.get("tipper", "?")
    status = entry.get("voucher_status", "pending_all")
    canvas_ok = status in ("pending_tavern", "issued")
    tavern_ok = status in ("pending_canvas", "issued")
    if not canvas_ok:
        canvas_ok = _grant_canvas_voucher(persona, entry["vouchers"]["canvas"], ref)
    if not tavern_ok:
        tavern_ok = _grant_tavern_voucher(bank, persona, entry["vouchers"]["tavern"],
                                          entry["tip_id"], tipper_label, entry.get("title", entry["book"]))
    if canvas_ok and tavern_ok:
        return "issued"
    if canvas_ok:
        return "pending_tavern"
    if tavern_ok:
        return "pending_canvas"
    return "pending_all"


def cmd_tip(args):
    # 區塊職責: 打賞主流程 — guard → debit 燒 token → ledger 跨層驗證 → 發雙券 → 記打賞簿 → 酒館廣播
    import secrets as _secrets

    # --retry: 補發打賞簿內 pending 的券, 不動帳
    if args.retry:
        idx = _load_tips()
        pending = [t for t in idx.get("tips", []) if t.get("voucher_status") != "issued"]
        if not pending:
            print("（沒有 pending 的打賞券要補發）")
            return 0
        for t in pending:
            t["voucher_status"] = _issue_tip_vouchers(t)
            print(f"  retry 《{t.get('title', t['book'])}》 tip {t['tip_id']} → {t['voucher_status']}")
            _write_tip(t)   # T-BOOKS-STORAGE: 更新該筆獨立檔（同 tip_id 覆寫），不重寫整簿
        return 0 if all(t.get("voucher_status") == "issued" for t in pending) else 2

    # guard: 必填與額度
    for req in ("book", "tipper", "tipper_persona", "tokens"):
        if getattr(args, req, None) in (None, ""):
            print(f"❌ tip 需要 --{req.replace('_', '-')}", file=sys.stderr)
            return 2
    tokens = int(args.tokens)
    if not (1 <= tokens <= TIP_MAX):
        print(f"❌ --tokens 須為 1~{TIP_MAX}", file=sys.stderr)
        return 2
    ben = _resolve_beneficiary(args.book)
    if ben is None:
        print(f"❌ 《{args.book}》不在捐贈登記簿 (_donations.json) — 未入庫的書不可打賞 (先 donate/publish)", file=sys.stderr)
        return 2
    ben_bank, ben_persona, title, ben_kind = ben
    if not ben_persona:
        print(f"❌ 《{title}》登記簿缺 donor_persona — 無法定位受益 persona", file=sys.stderr)
        return 2
    # 防呆: 打賞自己的書禁止 (同 bank 不同 persona 合法 — 券綁 persona)
    if args.tipper_persona == ben_persona:
        print(f"❌ 自賞禁止 — 《{title}》的{ben_kind}就是 {ben_persona} 本人", file=sys.stderr)
        return 2

    tip_id = _secrets.token_hex(4)
    use_ref = f"tip:{args.book}:{tip_id}"   # 唯一 ref — 防重複打賞撞舊 ledger entry 的驗證 false-positive
    desc = f"打賞圖書: {title} ({args.tipper_persona} → {ben_persona})"
    print(f"💰 打賞《{title}》 — {args.tipper_persona} 燒 {tokens} token → {ben_kind} {ben_persona} "
          f"收 繪圖券×{tokens * TIP_CANVAS_RATE} + 酒館券×{tokens * TIP_TAVERN_RATE}")
    print("   走 CMD: Cmd_Treasury op=debit (use_kind=book_tip)...")
    ok, out = _run_treasury_debit(args.tipper, tokens, use_ref, desc, use_kind="book_tip")
    if not _verify_donation_debit(args.tipper, use_ref, tokens, kind="book_tip"):
        print("❌ Treasury debit 未確認落帳 (餘額不足? caller!=account? Editor 未跑?) — 不發券不記帳",
              file=sys.stderr)
        print(f"   run_cmd 輸出(尾):\n{out[-400:]}", file=sys.stderr)
        return 2
    print("✓ debit 已落帳 (ledger 跨層驗證通過)")

    entry = {
        "book": args.book, "title": title,
        "tipper": args.tipper, "tipper_persona": args.tipper_persona,
        "tipper_agent": args.tipper_agent or "",
        "beneficiary": ben_bank, "beneficiary_persona": ben_persona,
        "tokens_spent": tokens,
        "vouchers": {"canvas": tokens * TIP_CANVAS_RATE, "tavern": tokens * TIP_TAVERN_RATE},
        "tip_id": tip_id,
        "voucher_status": "pending_all",
        "note": args.note or "",
        "tipped_at": _today(),
    }
    entry["voucher_status"] = _issue_tip_vouchers(entry)
    _write_tip(entry)   # T-BOOKS-STORAGE Phase A: 寫獨立檔 tips/<stamp>_<persona>_<tip_id>.json（不再編聚合檔）
    if entry["voucher_status"] == "issued":
        print(f"✅ 打賞完成: {ben_persona} 已收 繪圖券×{entry['vouchers']['canvas']} + 酒館券×{entry['vouchers']['tavern']}")
    else:
        print(f"⚠ 券發放未完成 ({entry['voucher_status']}) — 帳已落不回滾, 跑 `library.py tip --retry` 補發",
              file=sys.stderr)

    # 酒館打賞廣播 (預設開, 非致命)
    if not getattr(args, "no_notify", False):
        note_part = f"「{entry['note']}」" if entry["note"] else ""
        notice = (f"💰 打賞! **{args.tipper_persona}** 打賞《{title}》 {tokens} token "
                  f"→ @{ben_persona} ({ben_kind}) 收 繪圖券×{entry['vouchers']['canvas']} + "
                  f"酒館券×{entry['vouchers']['tavern']} {note_part}")
        sent = _run_tavern_post(args.tipper, args.tipper_persona, notice, tag="book-tip")
        print(f"📣 酒館打賞廣播:{'已發送' if sent else '發送失敗(打賞仍成功)'}")
    return 0 if entry["voucher_status"] == "issued" else 2


def cmd_tips(args):
    # 打賞簿列表 (全列 / --book 過濾)
    tips = _load_tips().get("tips", [])
    if args.book:
        tips = [t for t in tips if t.get("book") == args.book]
    if not tips:
        print("（尚無打賞紀錄；用 tip 打賞喜歡的書）")
        return 0
    total = sum(int(t.get("tokens_spent", 0)) for t in tips)
    print(f"💰 打賞簿（{len(tips)} 筆, 累計 {total} token）\n")
    for t in tips:
        status = "" if t.get("voucher_status") == "issued" else f"  ⚠{t.get('voucher_status')}"
        print(f"- {t.get('tipped_at')}  {t.get('tipper_persona', '?')} → 《{t.get('title', t['book'])}》 "
              f"{t.get('tokens_spent')} token → {t.get('beneficiary_persona', '?')} "
              f"(繪圖券×{t.get('vouchers', {}).get('canvas', '?')} + 酒館券×{t.get('vouchers', {}).get('tavern', '?')})"
              f"{status}")
        if t.get("note"):
            print(f"    note: {t['note']}")
    return 0


def cmd_migrate_tips(args):
    # 區塊職責: 一次性 migration — 舊單一 _tips.json 拆成 tips/ per-entry 檔（T-BOOKS-STORAGE Phase A）
    # 物理意義: 舊聚合檔 read-modify-write 整檔 → 跨專案 submodule 併發衝突; 拆 per-entry append-only 根治
    # 數值影響: 讀舊檔每筆 → _write_tip 落獨立檔; 驗 parity 後「提示」可刪舊檔（保守, 不自動刪）
    old = _tips_index()
    if not old.exists():
        print("（沒有舊 _tips.json 要遷移）")
        return 0
    try:
        data = _read_json(old)
    except Exception as e:
        print(f"❌ 讀舊 _tips.json 失敗: {e}", file=sys.stderr)
        return 2
    tips = data.get("tips", []) if isinstance(data, dict) else []
    if not tips:
        print("（舊 _tips.json 無 tips 紀錄；可直接刪）")
        return 0
    n = 0
    for t in tips:
        if not t.get("tip_id"):
            print(f"  ⚠ 跳過無 tip_id 的舊筆: {t.get('book')}", file=sys.stderr)
            continue
        # migration 用原 tipped_at (YYYY-MM-DD → YYYYMMDD) 當檔名時戳，誠實反映原始打賞日期
        _write_tip(t, stamp=(t.get("tipped_at") or "").replace("-", "") or None)
        n += 1
    reloaded = _load_tips().get("tips", [])
    print(f"✅ 遷移 {n} 筆 → {_tips_dir()}（重新 glob 讀回 {len(reloaded)} 筆）")
    if len(reloaded) >= n:
        print(f"   parity OK。確認無誤後可刪舊檔（git rm {old}）。")
    else:
        print(f"   ⚠ parity 不符 (寫 {n} / 讀回 {len(reloaded)}) — 別刪舊檔, 先查", file=sys.stderr)
    return 0


# ===========================================================
# 卷↔章對應 (Volume↔Chapter mapping)
# 物理意義: 打通「第 N 卷/集 ↔ Books NNN.txt 原始檔序號 ↔ BookNotes chN 章節號」三層對照
# ===========================================================

_VOL_STATUS = {"read": "✅ 已讀", "reading": "📖 閱讀中", "unread": "⚪ 未讀"}


def cmd_add_volume(args):
    # 區塊職責: 在 book.json volumes[] 登記一卷; 同 n 覆寫(可更新狀態), 否則 append + 依 n 排序
    book = args.book
    bk = _require_book(book)
    n = int(args.n)
    vols = [v for v in bk.get("volumes", []) if int(v.get("n", -99999)) != n]
    entry = {
        "n": n,
        "title": args.title or "",
        "title_original": args.title_original or "",
        "files": args.files or "",          # 原始檔序號範圍, 如 "000-022"
        "chapters": args.chapters or "",     # 章節號範圍, 如 "1-22"
        "status": args.status or "unread",
        "note": args.note or "",
    }
    if args.arc_ref:
        entry["arc_ref"] = args.arc_ref      # 對應的卷總結 arc (chapters 字串)
    vols.append(entry)
    vols.sort(key=lambda v: int(v.get("n", 0)))
    bk["volumes"] = vols
    _write_json(_book_json(book), bk)
    print(f"✅ 卷登記: {book} 第 {n} 卷《{entry['title']}》 "
          f"files={entry['files']} chapters={entry['chapters']} [{entry['status']}]")
    return 0


def cmd_volumes(args):
    book = args.book
    bk = _require_book(book)
    vols = bk.get("volumes", [])
    if not vols:
        print("（尚無卷別登記；用 add-volume 建立）")
        return 0
    cur = bk.get("progress", {}).get("current_chapter", 0)
    print(f"📚《{bk['title']}》卷別對照（{len(vols)} 卷）\n")
    for v in vols:
        mark = _VOL_STATUS.get(v.get("status"), v.get("status", ""))
        line = f"第 {v.get('n')} 卷《{v.get('title', '')}》"
        if v.get("title_original"):
            line += f"（{v['title_original']}）"
        line += f"  [{mark}]"
        print(line)
        print(f"   原始檔 files: {v.get('files', '?')}   章節 chapters: {v.get('chapters', '?')}")
        if v.get("arc_ref"):
            print(f"   卷總結 arc: 第 {v['arc_ref']} 章")
        if v.get("note"):
            print(f"   note: {v['note']}")
    print(f"\n目前讀到第 {cur} 章")
    return 0


# ===========================================================
# 圖書檢索 (Search) — 跨書多維度: metadata/標籤(結構化) + 內容全文(人物/arc/章節/名詞/書評)
# ===========================================================

def _iter_books():
    if not LIB_ROOT.exists():
        return
    for d in sorted(LIB_ROOT.iterdir()):
        bj = d / "book.json"
        if d.is_dir() and bj.exists():
            try:
                yield d.name, _read_json(bj)
            except Exception:
                continue


def cmd_search(args):
    # 區塊職責: 跨書檢索。query 子字串(CI) 掃 metadata + (deep) 書資料夾全文(章節/人物/arc/名詞)
    # 物理意義: 書一多時靠主題/人物/標籤找書, 不只按書名; --tag 結構化過濾, --scope 限範圍
    q = (args.query or "").strip().lower()
    want_tag = args.tag.strip().lower() if args.tag else None
    scope = args.scope or "all"
    if not q and not want_tag:
        print("❌ 至少給 --query 或 --tag", file=sys.stderr)
        return 1
    results = []
    for bid, bk in _iter_books():
        if args.book and bid != args.book:
            continue
        reasons = []
        tags = [str(t).lower() for t in bk.get("tags", [])]
        # 標籤過濾 (硬條件)
        if want_tag:
            if want_tag not in tags:
                continue
            reasons.append(f"標籤={want_tag}")
        # metadata
        if q and scope in ("all", "meta"):
            for fld in ("title", "title_original", "author"):
                val = bk.get(fld) or ""
                if q in val.lower():
                    reasons.append(f"{fld}: {val}")
            for t in tags:
                if q in t:
                    reasons.append(f"標籤: {t}")
            # 書評 / bookmark 心得
            for r in bk.get("reviews", []):
                if q in json.dumps(r, ensure_ascii=False).lower():
                    reasons.append(f"書評(by {r.get('reviewer')})")
            note = bk.get("progress", {}).get("bookmark_note", "")
            if q in note.lower():
                reasons.append("書籤心得")
        # 內容全文 (章節/人物/arc/名詞) — 掃書資料夾 .md/.json, 依子資料夾歸類命中
        if q and scope in ("all", "content"):
            bdir = _book_dir(bid)
            cat_hits = {}
            if bdir.exists():
                for fp in bdir.rglob("*"):
                    if not fp.is_file() or fp.suffix not in (".md", ".json"):
                        continue
                    if fp.name == "book.json":
                        continue
                    try:
                        text = fp.read_text(encoding="utf-8", errors="replace").lower()
                    except Exception:
                        continue
                    if q in text:
                        cat = fp.parent.name if fp.parent != bdir else fp.stem
                        cat_hits[cat] = cat_hits.get(cat, 0) + 1
            for cat, n in sorted(cat_hits.items()):
                reasons.append(f"{cat}({n})")
        if reasons:
            results.append((bid, bk.get("title", bid), reasons))
    # 也搜想讀清單 (_recommended.json)
    rec_hits = []
    if q:
        try:
            for r in _read_recs().get("recommendations", []):
                if q in json.dumps(r, ensure_ascii=False).lower():
                    rec_hits.append(r)
        except Exception:
            pass

    if not results and not rec_hits:
        print(f"（查無命中: query='{args.query or ''}' tag='{args.tag or ''}'）")
        return 0
    print(f"🔎 檢索結果  query='{args.query or ''}'"
          + (f" tag='{args.tag}'" if want_tag else "") + f"  scope={scope}\n")
    for bid, title, reasons in results:
        print(f"📖 《{title}》  (id={bid})")
        print(f"   命中: {', '.join(reasons)}")
    if rec_hits:
        print(f"\n📋 想讀清單命中（{len(rec_hits)}）:")
        for r in rec_hits:
            print(f"   - 《{r.get('title')}》 / {r.get('author', '?')}")
    return 0


def cmd_tag(args):
    # 區塊職責: 設定/合併書的類型標籤 (供 search --tag 過濾用); --add 合併, --remove 移除
    book = args.book
    bk = _require_book(book)
    tags = list(bk.get("tags", []))
    _csplit = lambda s: [x.strip() for x in re.split(r"[,;|\n]+", s or "") if x.strip()]
    if args.add:
        for t in _csplit(args.add):
            if t not in tags:
                tags.append(t)
    if args.remove:
        rm = set(_csplit(args.remove))
        tags = [t for t in tags if t not in rm]
    bk["tags"] = tags
    _write_json(_book_json(book), bk)
    print(f"🏷  《{bk['title']}》標籤: {', '.join(tags) if tags else '(無)'}")
    return 0


# ===========================================================
# 讀後書評 (Review) — 按 persona 標註, 不同 persona 各自評價 (同構於人物看法版本史)
# ===========================================================

def _stars(rating):
    if not rating:
        return "(未評分)"
    rating = max(0, min(5, int(rating)))
    return "★" * rating + "☆" * (5 - rating)


def cmd_review(args):
    # 區塊職責: 記一筆讀後推薦/書評到 book.json reviews[], 按 reviewer(persona) 標註
    # 物理意義: Tim 拍板「書評按 persona 標註, 不同人不同評價」— 同 reviewer+scope 覆寫(re-review),
    #          否則 append; 不同 persona 各保留各自書評
    book = args.book
    bk = _require_book(book)
    reviewer = args.reviewer or bk.get("reader_persona") or "basecamp"
    scope = args.scope or "whole"           # whole | volume:N
    rating = None
    if args.rating is not None:
        rating = int(args.rating)
        if not (1 <= rating <= 5):
            print("❌ --rating 須為 1-5", file=sys.stderr)
            return 1
    reviews = [r for r in bk.get("reviews", [])
               if not (r.get("reviewer") == reviewer and r.get("scope") == scope)]
    entry = {
        "reviewer": reviewer,
        "scope": scope,
        "rating": rating,
        "pitch": args.pitch or "",              # 非劇透勾子
        "for_whom": args.for_whom or "",        # 什麼讀者會愛
        "similar_to": args.similar_to or "",    # 看過 X 會喜歡
        "content_note": args.content_note or "",
        "spoiler_safe": not args.spoiler,       # 預設非劇透
        "date": _today(),
    }
    reviews.append(entry)
    bk["reviews"] = reviews
    _write_json(_book_json(book), bk)
    print(f"✅ 書評登記:《{bk['title']}》 [{scope}] by 👤{reviewer}  {_stars(rating)}")

    # 糖: review --tip N --tipper <bank> 一步到位書評+打賞 (tipper_persona=reviewer, note=pitch)
    if getattr(args, "tip", None):
        if not getattr(args, "tipper", None):
            print("⚠ --tip 需搭配 --tipper <bank-id> — 書評已記, 打賞略過", file=sys.stderr)
            return 2
        from argparse import Namespace
        return cmd_tip(Namespace(
            book=book, tipper=args.tipper, tipper_persona=reviewer,
            tipper_agent=None, tokens=int(args.tip),
            note=args.pitch or "", no_notify=False, retry=False))
    return 0


def cmd_reviews(args):
    book = args.book
    bk = _require_book(book)
    reviews = bk.get("reviews", [])
    if args.reviewer:
        reviews = [r for r in reviews if r.get("reviewer") == args.reviewer]
    if not reviews:
        print("（尚無書評；用 review 登記）")
        return 0
    from collections import OrderedDict
    by = OrderedDict()
    for r in reviews:
        by.setdefault(r.get("reviewer", "?"), []).append(r)
    print(f"📖《{bk['title']}》讀後書評（{len(reviews)} 筆 / {len(by)} 位 persona）\n")
    for who, rs in by.items():
        print(f"━━━ 👤 {who} ━━━")
        for r in rs:
            print(f"  [{r.get('scope', 'whole')}] {_stars(r.get('rating'))}  ({r.get('date')})"
                  + ("" if r.get("spoiler_safe", True) else "  ⚠含劇透"))
            if r.get("pitch"):
                print(f"    勾子: {r['pitch']}")
            if r.get("for_whom"):
                print(f"    適合: {r['for_whom']}")
            if r.get("similar_to"):
                print(f"    類似作: {r['similar_to']}")
            if r.get("content_note"):
                print(f"    內容提醒: {r['content_note']}")
        print()
    return 0


# ===========================================================
# argparse
# ===========================================================

def _add_reader_arg(parser, with_continue=False):
    # 區塊職責：給「讀者耦合」的子命令統一加 --reader (+ 選配 --continue-from)
    # 物理意義：--reader 非初始讀者 → 自動走 branches/<reader>/ 分支筆記, 不影響初始讀者
    parser.add_argument("--reader", default=None,
                        help="讀者 persona; 非初始讀者 → 自動走 branches/<reader>/ 分支筆記 (不影響初始讀者)")
    if with_continue:
        parser.add_argument("--continue-from", dest="continue_from", default=None,
                            help="分支起點接續自哪位讀者 (複製其當前章當起點, 不寫回來源 → 不影響原讀者書籤)")


def build_parser():
    p = argparse.ArgumentParser(prog="library.py", description="Reading Library CLI — 閱讀心得圖書館")
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("reading-recall",
                       help="依 persona + media 生成新 Library 的完整閱讀追回檔（不讀 Archive）")
    a.add_argument("--persona", required=True,
                   help="讀者 persona；必須與 readers/<persona>/reader.json 相符")
    a.add_argument("--media-id", "--book-id", dest="media_id", required=True,
                   help="新 Library media id；--book-id 是相容別名，例如 comic-delicious-in-dungeon")
    a.set_defaults(func=cmd_reading_recall)

    a = sub.add_parser("add-book", help="建立新書 (--origin authored = 原創寫書)")
    a.add_argument("--id", help="書本 slug（缺則由 title 生成）")
    a.add_argument("--title", required=True)
    a.add_argument("--title-original", dest="title_original")
    a.add_argument("--author")
    a.add_argument("--aliases", required=True, help="別名（使用者提供的書名必含；用 ; | 或換行分隔）")
    a.add_argument("--reader-persona", dest="reader_persona")
    a.add_argument("--origin", choices=["authored", "imported"],
                   help="authored=原創寫書(自由時間, 作者=捐贈者, 預設草稿) / imported=調入別人的書; 省略=沿用現況")
    a.add_argument("--author-persona", dest="author_persona",
                   help="原創書作者 persona (origin=authored 時; 預設=reader_persona)")
    a.set_defaults(func=cmd_add_book)

    a = sub.add_parser("prepare", help="依 persona 與使用者書名找候選並報告閱讀覆蓋；不自動合併")
    a.add_argument("--reader", required=True)
    a.add_argument("--title", required=True, help="使用者直接說出的書名")
    a.set_defaults(func=cmd_prepare)

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
    _add_reader_arg(a)
    a.set_defaults(func=cmd_log_chapter)

    a = sub.add_parser("add-character", help="新增人物（v1 初印象）")
    a.add_argument("--book", required=True)
    a.add_argument("--id", required=True)
    a.add_argument("--name", required=True)
    a.add_argument("--name-original", dest="name_original",
                   help="原文/口說讀音 (e.g. 日文片假名 シャーリー), 供陪看 STT initial_prompt 用; 空則未登錄")
    a.add_argument("--chapter", required=True)
    a.add_argument("--headline", required=True, help="一句話人物標題")
    a.add_argument("--facts", help="客觀已知事實, 分隔同上")
    a.add_argument("--view", help="第一人稱看法")
    _add_reader_arg(a)
    a.set_defaults(func=cmd_add_character)

    a = sub.add_parser("set-name-original", help="補/改既有人物的原文讀音欄 (backfill, 供 STT prompt)")
    a.add_argument("--book", required=True)
    a.add_argument("--character", required=True)
    a.add_argument("--name-original", dest="name_original", required=True,
                   help="原文/口說讀音 (e.g. 日文片假名 シャーリー)")
    _add_reader_arg(a)
    a.set_defaults(func=cmd_set_name_original)

    a = sub.add_parser("stt-prompt", help="組該書人物原文讀音成 whisper initial_prompt (陪看 STT 用)")
    a.add_argument("--book", required=True)
    a.add_argument("--max-chars", dest="max_chars", type=int, default=200,
                   help="prompt 字數上限 (whisper initial_prompt ~224 token, 預設 200)")
    _add_reader_arg(a)
    a.set_defaults(func=cmd_stt_prompt)

    a = sub.add_parser("revise-view", help="改觀（fork 新版本, 不覆寫舊版）")
    a.add_argument("--book", required=True)
    a.add_argument("--character", required=True)
    a.add_argument("--chapter", required=True)
    a.add_argument("--headline", required=True)
    a.add_argument("--change-reason", dest="change_reason", required=True, help="為何改觀")
    a.add_argument("--facts")
    a.add_argument("--view")
    a.add_argument("--diff", help="與前一版的差異")
    _add_reader_arg(a)
    a.set_defaults(func=cmd_revise_view)

    a = sub.add_parser("show-book", help="顯示書本概覽")
    a.add_argument("--book", required=True)
    _add_reader_arg(a)
    a.set_defaults(func=cmd_show_book)

    a = sub.add_parser("show-character", help="顯示人物看法演變 + 全文")
    a.add_argument("--book", required=True)
    a.add_argument("--character", required=True)
    a.add_argument("--version", help="all / 版本號 / 預設只印目前版本")
    _add_reader_arg(a)
    a.set_defaults(func=cmd_show_character)

    a = sub.add_parser("list", help="列出所有書")
    a.set_defaults(func=cmd_list)

    a = sub.add_parser("bookmark", help="記錄讀到哪裡 + 可選續讀備註/心得")
    a.add_argument("--book", required=True)
    a.add_argument("--chapter", help="讀到第幾章（缺則不動進度）")
    a.add_argument("--note", help="續讀前該記得的事 / 本小姐自選要不要寫的心得")
    _add_reader_arg(a, with_continue=True)
    a.set_defaults(func=cmd_bookmark)

    # ── 個人書架（Tim 2026-08-05）—— 卡片存 letters/<persona>/bookshelf/ ──
    a = sub.add_parser("shelf-update",
                       help="建/更新個人書架卡片（簡評 + 期待度 + 狀態；進度自動從 book.json 抽快照）")
    a.add_argument("--book", required=True)
    a.add_argument("--persona", required=True, help="卡片放誰的 letters/<persona>/bookshelf/")
    a.add_argument("--comment", help="簡評（覆寫既有簡評）")
    a.add_argument("--append-comment", help="簡評追記（保留舊的 — 看法演變本身有價值）")
    a.add_argument("--anticipation", type=int,
                   help="期待度 1-5：5 馬上想接著讀 / 4 近期會回來 / 3 有空再說 / "
                        "2 擱著可能不回來 / 1 不打算再讀（但留紀錄，不刪）")
    a.add_argument("--status", choices=["reading", "finished", "paused", "dropped"],
                   help="閱讀狀態（預設沿用卡片現值，新卡為 reading）")
    a.set_defaults(func=cmd_shelf_update)

    a = sub.add_parser("shelf", help="列出個人書架 — 最近讀什麼、進度到哪、下次挑哪本")
    a.add_argument("--persona", required=True)
    a.add_argument("--sort", choices=["recent", "anticipation"], default="recent",
                   help="recent = 依最後閱讀日（預設）／anticipation = 依期待度")
    a.set_defaults(func=cmd_shelf)

    a = sub.add_parser("resume", help="續讀前 catch-up:進度 + 人物現況 + 未解伏筆")
    a.add_argument("--book", required=True)
    _add_reader_arg(a, with_continue=True)
    a.add_argument("--up-to", type=int, default=None,
                   help="逐章跨分支 catch-up:撈 ch01~ch(N-1) 各章最佳來源(persona分支→主線→其他分支);slug 分歧則並陳分叉")
    a.add_argument("--full", action="store_true", help="--up-to 時印每章全文(預設精簡:章名+摘要+伏筆)")
    a.set_defaults(func=cmd_resume)

    a = sub.add_parser("branches", help="列出某書的閱讀分支 (main 初始讀者 + 各讀者分支筆記)")
    a.add_argument("--book", required=True)
    a.set_defaults(func=cmd_branches)

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
    _add_reader_arg(a)
    a.set_defaults(func=cmd_arc)

    a = sub.add_parser("arcs", help="顯示階段大綱列表 (--full 印全文)")
    a.add_argument("--book", required=True)
    a.add_argument("--full", action="store_true")
    _add_reader_arg(a)
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

    a = sub.add_parser("publish", help="發布原創書 (寫書 Author-as-Donor; draft→published, 免費入庫, 作者署名)")
    a.add_argument("--book", required=True, help="原創書 slug (origin=authored)")
    a.add_argument("--donor", required=True, help="作者 bank id (署名用, 不扣 token)")
    a.add_argument("--donor-persona", dest="donor_persona", default=None, help="作者 persona (預設讀 book.json author_persona)")
    a.add_argument("--donor-agent", dest="donor_agent", default=None)
    a.add_argument("--note", default=None)
    a.add_argument("--no-notify", dest="no_notify", action="store_true", help="不發酒館新書發表通知")
    a.set_defaults(func=cmd_publish)

    a = sub.add_parser("donations", help="列出捐贈圖書館 (書 + 捐贈者)")
    a.set_defaults(func=cmd_donations)

    # 打賞 (Plan_Reading_Library_Tip v2 — token 燒掉, 受益 persona 收雙券 1+1)
    a = sub.add_parser("tip", help="打賞一本書 (燒 token; 作者/捐贈者 persona 收 繪圖券+酒館券, 匯率 1+1)")
    a.add_argument("--book", help="Books/<slug> 的 slug")
    a.add_argument("--tipper", help="打賞者 bank id (Treasury caller 必須==此帳戶)")
    a.add_argument("--tipper-persona", help="打賞者 persona (受益人不可是自己)")
    a.add_argument("--tipper-agent", default=None)
    a.add_argument("--tokens", type=int, default=None, help=f"打賞額 1~{TIP_MAX} (參考檔位: 小賞5/中賞10/大賞50)")
    a.add_argument("--note", default=None, help="讀後感一句 (隨廣播)")
    a.add_argument("--no-notify", action="store_true", help="不發酒館打賞廣播")
    a.add_argument("--retry", action="store_true", help="補發打賞簿內 pending 的券 (不動帳)")
    a.set_defaults(func=cmd_tip)

    a = sub.add_parser("tips", help="顯示打賞簿 (全列 / --book 過濾)")
    a.add_argument("--book", default=None)
    a.set_defaults(func=cmd_tips)

    a = sub.add_parser("migrate-tips", help="[一次性] 舊 _tips.json → tips/ per-entry 檔遷移 (T-BOOKS-STORAGE Phase A)")
    a.set_defaults(func=cmd_migrate_tips)

    # 卷↔章對應
    a = sub.add_parser("add-volume", help="登記一卷(卷別↔原始檔序號↔章節號對照); 同 n 覆寫")
    a.add_argument("--book", required=True)
    a.add_argument("--n", required=True, help="卷序號 (第幾集/卷), 整數")
    a.add_argument("--title", help="卷名")
    a.add_argument("--title-original", dest="title_original")
    a.add_argument("--files", help="原始檔序號範圍, 如 000-022")
    a.add_argument("--chapters", help="章節號範圍, 如 1-22")
    a.add_argument("--status", choices=["unread", "reading", "read"], help="未讀/閱讀中/已讀")
    a.add_argument("--note")
    a.add_argument("--arc-ref", dest="arc_ref", help="對應卷總結 arc 的 chapters 字串, 如 1-22")
    a.set_defaults(func=cmd_add_volume)

    a = sub.add_parser("volumes", help="顯示卷別對照 (卷↔檔↔章 + 讀畢狀態)")
    a.add_argument("--book", required=True)
    a.set_defaults(func=cmd_volumes)

    # 圖書檢索
    a = sub.add_parser("search", help="跨書檢索 (metadata/標籤 + 內容全文: 人物/arc/章節/名詞/書評)")
    a.add_argument("--query", help="關鍵字 (子字串, 不分大小寫)")
    a.add_argument("--tag", help="按標籤過濾 (硬條件)")
    a.add_argument("--scope", choices=["all", "meta", "content"], help="all(預設)/meta(僅元資料)/content(僅內容全文)")
    a.add_argument("--book", help="限定只搜某 book id")
    a.set_defaults(func=cmd_search)

    a = sub.add_parser("tag", help="設定/合併書的類型標籤 (供 search --tag 用)")
    a.add_argument("--book", required=True)
    a.add_argument("--add", help="新增標籤 (逗號/分號/| 分隔)")
    a.add_argument("--remove", help="移除標籤 (逗號/分號/| 分隔)")
    a.set_defaults(func=cmd_tag)

    # 讀後書評 (按 persona)
    a = sub.add_parser("review", help="記讀後書評/推薦 (按 persona 標註, 不同人不同評價)")
    a.add_argument("--book", required=True)
    a.add_argument("--reviewer", help="評論者 persona (缺則用該書 reader_persona / basecamp)")
    a.add_argument("--scope", help="whole(整本, 預設) 或 volume:N(某卷)")
    a.add_argument("--rating", default=None, help="1-5 星")
    a.add_argument("--pitch", help="非劇透勾子(一句話)")
    a.add_argument("--for-whom", dest="for_whom", help="什麼讀者會愛")
    a.add_argument("--similar-to", dest="similar_to", help="看過 X 會喜歡這本")
    a.add_argument("--content-note", dest="content_note", help="內容提醒")
    a.add_argument("--spoiler", action="store_true", help="標記此書評含劇透(預設非劇透)")
    a.add_argument("--tip", type=int, default=None, help="書評+打賞一步糖: 打賞額 (tipper_persona=reviewer, note=pitch)")
    a.add_argument("--tipper", default=None, help="--tip 用: 打賞者 bank id")
    a.set_defaults(func=cmd_review)

    a = sub.add_parser("reviews", help="顯示讀後書評 (按 persona 分組)")
    a.add_argument("--book", required=True)
    a.add_argument("--reviewer", help="只看某 persona 的書評")
    a.set_defaults(func=cmd_reviews)

    return p


def main():
    args = build_parser().parse_args()
    # 集中掛載分支路由：凡帶 --reader + --book 的子命令, 啟用前先決定 main / branch
    if getattr(args, "book", None) and hasattr(args, "reader"):
        _activate_branch(args.book, args.reader, getattr(args, "continue_from", None))
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
