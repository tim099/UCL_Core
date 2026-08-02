#!/usr/bin/env python3
"""work_memory.py — 工作記憶區（所有 agent 共用, 以「工作主題」為單位的 knowhow 庫）。

區塊職責: 承接 Memory_Fragment_Backfill 的 fragment 哲學（事實源寫一次不改、索引=機械視圖、
          先搜再寫防洗版）, 但記憶單位從「persona 身分」改為「工作主題」— 讓任何 agent
          在進行某項工作前, 5 分鐘內取得該工作的拍板/坑/現況/指路。
物理意義: 資料落 <repo>/AgentCommands/WorkMemory/<topic-slug>/ —
          _topic.md（主題卡）+ <type>_<slug>.md（記憶 fragment, 寫一次不改寫,
          更新走 status/origins/supersede）+ _index.md（機械生成視圖, 手改必被覆寫）。
          記憶之間可跨主題關聯（links: [<topic>/<fragment-id>]）, 讀取時 --with-links 一起拉。
數值影響: 與知識庫整合 — kb_targets.json 的 work_memory target 涵蓋本目錄,
          `knowledge_base.py search --target work_memory` 可語意檢索。

文件 vs 工作記憶的分工判準（Tim 2026-07-29 拍板設計）:
  - 文件（Docs/）= 內容本體: 完整規格/施工圖/欄位表, 會被維護更新, 篇幅大
  - 工作記憶 = 開工前 5 分鐘要知道、但翻文件目錄撈不快的東西:
    拍板結論(不含推導)/工作特有的坑/現況快照/「哪份文件是權威」的指路
  - 記憶不重複文件內容 — 記憶「指向」文件（pointer/related_docs）

CLI:
    python work_memory.py topics                          # 列出所有工作主題
    python work_memory.py read --topic <slug> [--with-links] [--types decision,pitfall]
    python work_memory.py init --topic <slug> --title <t> [--desc <d>]
    python work_memory.py add  --topic <slug> --type <t> --id <slug> --title <t> \
        (--body <text> | --body-file <path>) [--links t/f,t/f] [--docs d1,d2] [--by <persona>]
    python work_memory.py supersede --topic <slug> --id <fragment-id> [--by <new-fragment-id>]
    python work_memory.py link --from <topic>/<frag-id> --to <topic>/<frag-id>   # 雙向
    python work_memory.py index [--topic <slug>]          # 重建機械索引（add/link 後自動跑）

# Source: ucl_core:Tools~/AgentCommands/work_memory.py
"""
from __future__ import annotations

import argparse
import datetime
import re
import sys
from pathlib import Path

# Windows cp950 終端強制 utf-8（對齊 freetime.py 慣例）
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

FRAGMENT_TYPES = ("decision", "knowhow", "pitfall", "state", "pointer")
FRAGMENT_STATUS = ("active", "superseded", "closed")


# ===========================================================
# 路徑解析 — repo root（對齊 freetime.py / knowledge_base.py 慣例）
# ===========================================================
def _resolve_repo_root() -> Path:
    import os
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and (Path(env) / ".git").exists():
        return Path(env)
    for start in (Path.cwd(), Path(__file__).resolve().parent):
        p = start
        while p != p.parent:
            if (p / ".git").is_dir():
                return p
            p = p.parent
    return Path(__file__).resolve().parents[2]


REPO_ROOT = _resolve_repo_root()
WM_ROOT = REPO_ROOT / "AgentCommands" / "WorkMemory"
# 區塊職責: 為每次 `read` 產生「agent 與人類共讀」的 briefing，含記憶摘要與權威來源全文。
# 物理意義: briefing 置於 WorkMemory 外，避免 topics()/index()/知識庫把生成品誤判為工作記憶事實源。
# 數值影響: 每次讀取新增一份 UTF-8 Markdown；檔名含微秒 UTC 時間戳，連續呼叫也不會互相覆寫。
READ_BRIEF_ROOT = REPO_ROOT / "AgentCommands" / "WorkMemoryReadBriefs"
UCL_CORE_ROOT = Path(__file__).resolve().parents[2]
# 區塊職責: 限制每份來源在共讀 briefing 中的展開行數，保留辨識檔案職責所需的開頭上下文。
# 物理意義: 完整來源仍以 related_docs 指向的原始檔為單一真相；briefing 僅是開工導覽，不取代原檔。
# 數值影響: 每個來源最多貢獻 100 行，避免單一大型 C# 或 workflow 壓縮其他記憶與文件的可讀空間。
READ_BRIEF_MAX_SOURCE_LINES = 100


# ===========================================================
# frontmatter — 輕量 flat 解析（value 含冒號 OK; list 認 [a, b] 單行式）
# ===========================================================
def parse_frontmatter(text: str) -> tuple[dict, str]:
    meta: dict = {}
    body = text
    if text.startswith("---"):
        parts = text.split("---", 2)
        if len(parts) >= 3:
            body = parts[2].strip()
            for line in parts[1].splitlines():
                line = line.strip()
                if not line or line.startswith("#") or ":" not in line:
                    continue
                key, _, val = line.partition(":")
                key, val = key.strip(), val.strip()
                if val.startswith("[") and val.endswith("]"):
                    meta[key] = [v.strip() for v in val[1:-1].split(",") if v.strip()]
                else:
                    meta[key] = val
    return meta, body


def dump_frontmatter(meta: dict) -> str:
    lines = ["---"]
    for k, v in meta.items():
        if isinstance(v, list):
            lines.append(f"{k}: [{', '.join(v)}]")
        else:
            lines.append(f"{k}: {v}")
    lines.append("---")
    return "\n".join(lines)


# ===========================================================
# 讀寫底層
# ===========================================================
def topic_dir(topic: str) -> Path:
    return WM_ROOT / topic


def load_fragment(path: Path) -> dict | None:
    try:
        meta, body = parse_frontmatter(path.read_text(encoding="utf-8"))
        meta["_body"] = body
        meta["_path"] = path
        return meta
    except OSError:
        return None


def list_fragments(topic: str) -> list[dict]:
    d = topic_dir(topic)
    if not d.is_dir():
        return []
    out = []
    for f in sorted(d.glob("*.md")):
        if f.name.startswith("_"):
            continue
        frag = load_fragment(f)
        if frag:
            out.append(frag)
    # active 優先, 再按 type 固定序
    type_order = {t: i for i, t in enumerate(FRAGMENT_TYPES)}
    out.sort(key=lambda x: (0 if x.get("status", "active") == "active" else 1,
                            type_order.get(x.get("type", ""), 99), str(x.get("id", ""))))
    return out


def save_fragment_meta(frag: dict) -> None:
    """只重寫 frontmatter, 正文原樣保留（更新 status/links 用 — 對齊「不重寫正文」鐵律）。"""
    path: Path = frag["_path"]
    meta = {k: v for k, v in frag.items() if not k.startswith("_")}
    path.write_text(dump_frontmatter(meta) + "\n\n" + frag["_body"] + "\n", encoding="utf-8")


# ===========================================================
# op: topics / init / add / read / link / supersede / index
# ===========================================================
def op_topics(_args) -> int:
    if not WM_ROOT.is_dir():
        print("（工作記憶區為空 — 用 init 建第一個主題）")
        return 0
    print("🧠 工作記憶主題:")
    for d in sorted(WM_ROOT.iterdir()):
        if not d.is_dir():
            continue
        card = load_fragment(d / "_topic.md") if (d / "_topic.md").exists() else None
        n = len([f for f in d.glob("*.md") if not f.name.startswith("_")])
        title = card.get("title", "") if card else ""
        status = card.get("status", "?") if card else "?"
        rel = ", ".join(card.get("related_topics", [])) if card else ""
        print(f"  - {d.name}  「{title}」 [{status}] fragments={n}" + (f"  ↔ {rel}" if rel else ""))
    return 0


def op_init(args) -> int:
    d = topic_dir(args.topic)
    card = d / "_topic.md"
    if card.exists():
        print(f"⚠ 主題已存在: {args.topic}")
        return 1
    d.mkdir(parents=True, exist_ok=True)
    meta = {
        "id": args.topic,
        "title": args.title,
        "status": "active",
        "created_at": datetime.date.today().isoformat(),
        "related_topics": [],
        "key_docs": [],
    }
    card.write_text(dump_frontmatter(meta) + "\n\n" + (args.desc or args.title) + "\n", encoding="utf-8")
    print(f"✅ 主題已建立: {args.topic}（{args.title}）")
    rebuild_index(args.topic)
    return 0


def op_add(args) -> int:
    if args.type not in FRAGMENT_TYPES:
        print(f"✗ type 必須是 {FRAGMENT_TYPES}")
        return 2
    d = topic_dir(args.topic)
    if not (d / "_topic.md").exists():
        print(f"✗ 主題不存在: {args.topic}（先 init）")
        return 2
    frag_id = f"{args.type}_{args.id}" if not args.id.startswith(args.type + "_") else args.id
    path = d / f"{frag_id}.md"
    if path.exists():
        print(f"✗ fragment 已存在: {frag_id} — 記憶寫一次不改寫; 要更新走 supersede 或追加 origins")
        return 2
    body = args.body or ""
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not body.strip():
        print("✗ 缺 --body 或 --body-file")
        return 2
    # 區塊職責: 防洗版 gate — 寫入「前」做輕量近似檢查（crest-001 測試回報②: 事後提醒是馬後炮）。
    # 物理意義: 掃全主題 fragment 標題做字詞重疊比對（零依賴、<10ms）; 命中僅警示不擋
    #          （語意級查重仍建議 KB search, 這裡是最後一道便宜防線）。
    similar = _find_similar_titles(args.title, exclude_topic_frag=(args.topic, frag_id))
    if similar:
        print("⚠ 防洗版警示 — 發現標題近似的既有 fragment（確認不是同一條再繼續; 是同一條請改用 link/追加）:")
        for t, fid, title in similar[:5]:
            print(f"    - {t}/{fid} — {title}")
    meta = {
        "id": frag_id,
        "topic": args.topic,
        "title": args.title,
        "type": args.type,
        "status": "active",
        "created_at": datetime.date.today().isoformat(),
        "created_by": args.by or "unknown",
        "links": [x.strip() for x in (args.links or "").split(",") if x.strip()],
        "related_docs": [x.strip() for x in (args.docs or "").split(",") if x.strip()],
    }
    path.write_text(dump_frontmatter(meta) + "\n\n" + body.strip() + "\n", encoding="utf-8")
    print(f"✅ fragment 已寫入: {args.topic}/{frag_id}")
    rebuild_index(args.topic)
    return 0


def _find_similar_titles(title: str, exclude_topic_frag: tuple[str, str]) -> list[tuple[str, str, str]]:
    """標題字詞重疊的輕量近似查找（bigram 重疊率 > 0.5 判近似）。回 [(topic, frag_id, title)]。"""
    def bigrams(s: str) -> set:
        s = "".join(ch for ch in s.lower() if ch.strip())
        return {s[i:i + 2] for i in range(len(s) - 1)} if len(s) > 1 else {s}
    q = bigrams(title)
    out = []
    if not WM_ROOT.is_dir() or not q:
        return out
    for d in WM_ROOT.iterdir():
        if not d.is_dir():
            continue
        for frag in list_fragments(d.name):
            if (d.name, frag.get("id")) == exclude_topic_frag:
                continue
            other = bigrams(str(frag.get("title", "")))
            if other and len(q & other) / max(len(q | other), 1) > 0.5:
                out.append((d.name, frag.get("id"), frag.get("title", "")))
    return out


def op_supersede(args) -> int:
    frags = {f.get("id"): f for f in list_fragments(args.topic)}
    frag = frags.get(args.id)
    if frag is None:
        print(f"✗ 找不到 fragment: {args.topic}/{args.id}")
        return 2
    new_id = args.by
    # 一步式（crest-001 測試回報③）: --new-id/--new-title/--new-body[-file] → 建新快照 + 舊檔標 superseded
    if args.new_id:
        new_body = args.new_body or ""
        if args.new_body_file:
            new_body = Path(args.new_body_file).read_text(encoding="utf-8")
        if not new_body.strip() or not args.new_title:
            print("✗ 一步式需要 --new-title 與 --new-body(-file)")
            return 2
        frag_type = frag.get("type", "state")
        full_new_id = f"{frag_type}_{args.new_id}" if not args.new_id.startswith(frag_type + "_") else args.new_id
        new_path = topic_dir(args.topic) / f"{full_new_id}.md"
        if new_path.exists():
            print(f"✗ 新 fragment 已存在: {full_new_id}")
            return 2
        meta = {"id": full_new_id, "topic": args.topic, "title": args.new_title, "type": frag_type,
                "status": "active", "created_at": datetime.date.today().isoformat(),
                "created_by": args.new_by or frag.get("created_by", "unknown"),
                "links": [f"{args.topic}/{frag.get('id')}"],
                "related_docs": frag.get("related_docs") or []}
        new_path.write_text(dump_frontmatter(meta) + "\n\n" + new_body.strip() + "\n", encoding="utf-8")
        new_id = full_new_id
        print(f"✅ 新 fragment 已寫入: {args.topic}/{full_new_id}")
    frag["status"] = "superseded"
    if new_id:
        links = frag.get("links") or []
        target = new_id if "/" in new_id else f"{args.topic}/{new_id}"
        if target not in links:
            links.append(target)
        frag["links"] = links
    save_fragment_meta(frag)
    print(f"✅ {args.topic}/{args.id} → superseded" + (f"（由 {new_id} 取代）" if new_id else ""))
    rebuild_index(args.topic)
    return 0


def _parse_ref(ref: str) -> tuple[str, str]:
    if "/" not in ref:
        raise ValueError(f"關聯引用需為 <topic>/<fragment-id>: {ref}")
    t, _, f = ref.partition("/")
    return t, f


def op_link(args) -> int:
    """雙向關聯 — 兩端 frontmatter links 互加（跨主題合法, 正是設計目的）。"""
    try:
        ta, fa = _parse_ref(getattr(args, "from"))
        tb, fb = _parse_ref(args.to)
    except ValueError as e:
        print(f"✗ {e}")
        return 2
    changed = 0
    for (t, f, other) in ((ta, fa, f"{tb}/{fb}"), (tb, fb, f"{ta}/{fa}")):
        frag = next((x for x in list_fragments(t) if x.get("id") == f), None)
        if frag is None:
            print(f"✗ 找不到 {t}/{f}")
            return 2
        links = frag.get("links") or []
        if other not in links:
            links.append(other)
            frag["links"] = links
            save_fragment_meta(frag)
            changed += 1
    print(f"✅ 已建立雙向關聯: {ta}/{fa} ↔ {tb}/{fb}（更新 {changed} 檔）")
    rebuild_index(ta)
    if tb != ta:
        rebuild_index(tb)
    return 0


def rebuild_index(topic: str) -> None:
    """機械生成 _index.md — 手改必被覆寫; 事實源永遠是 fragment 檔。"""
    d = topic_dir(topic)
    frags = list_fragments(topic)
    lines = [f"# 工作記憶索引 — {topic}", "",
             "> 機械生成（work_memory.py index）— 手改會被覆寫。事實源 = 各 fragment 檔。", ""]
    for t in FRAGMENT_TYPES:
        rows = [f for f in frags if f.get("type") == t]
        if not rows:
            continue
        lines.append(f"## {t}")
        for f in rows:
            mark = "" if f.get("status", "active") == "active" else f" ~~[{f.get('status')}]~~"
            links = f.get("links") or []
            link_note = f"  ↔ {', '.join(links)}" if links else ""
            lines.append(f"- **{f.get('id')}** — {f.get('title', '')}{mark}{link_note}")
        lines.append("")
    # 區塊職責: 輸出機械索引時正規化檔尾，只保留一個換行。
    # 物理意義: section 之間仍以空白行分隔，但最後一個 section 不會留下無內容的尾端段落。
    # 數值影響: 消除 git diff --check 的 EOF blank-line 警告，不改變任何 fragment 或索引項目。
    (d / "_index.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def op_index(args) -> int:
    topics = [args.topic] if args.topic else [d.name for d in WM_ROOT.iterdir() if d.is_dir()]
    for t in topics:
        rebuild_index(t)
        print(f"✅ 索引重建: {t}")
    return 0


def resolve_related_doc(ref: str) -> tuple[Path | None, str]:
    """將 related_docs 的本地檔案 ref 安全解析；非本地 ref 保留原因供 briefing 顯示。"""
    # 區塊職責: 支援 `path:line` 與 `ucl_core:path`，同時拒絕 commit/tavern 等非檔案 ref。
    # 物理意義: 僅允許 repo 或 UCL_Core submodule 內的檔案，避免記憶資料意外要求 briefing 洩出工作區外內容。
    # 數值影響: 解析失敗只新增一條診斷文字，不中斷其餘來源的載入。
    raw = ref.strip()
    if raw.startswith(("commit:", "tavern:", "workmem:")):
        return None, "非本地檔案引用"
    base = REPO_ROOT
    if raw.startswith("ucl_core:"):
        base, raw = UCL_CORE_ROOT, raw.removeprefix("ucl_core:")
    raw = re.sub(r":\d+(?:-\d+)?$", "", raw)
    candidate = Path(raw)
    if not candidate.is_absolute():
        candidate = base / candidate
    try:
        resolved = candidate.resolve()
        if not (resolved.is_relative_to(REPO_ROOT.resolve()) or resolved.is_relative_to(UCL_CORE_ROOT.resolve())):
            return None, "路徑不在允許的工作區範圍"
    except OSError as exc:
        return None, f"路徑解析失敗: {exc}"
    if not resolved.is_file():
        return None, "檔案不存在或不是一般檔案"
    return resolved, ""


def save_read_brief(topic: str, with_links: bool, types: str, result: str,
                    lines: list[str], related_docs: list[str]) -> Path:
    """把 read 的記憶摘要及本地 related_docs 全文寫成 agent 與人類共讀的 briefing。"""
    # 區塊職責: 單一 Markdown 同時承載本次 read 的記憶清單與有效本地來源全文。
    # 物理意義: agent 在命令後開啟此檔即可取得與人類完全相同的輸入，不再靠終端截斷內容。
    # 數值影響: 每份來源最多嵌入 READ_BRIEF_MAX_SOURCE_LINES 行；不可解析 ref 以文字診斷取代全文。
    READ_BRIEF_ROOT.mkdir(parents=True, exist_ok=True)
    read_at = datetime.datetime.now(datetime.timezone.utc)
    brief_path = READ_BRIEF_ROOT / f"{read_at:%Y%m%d_%H%M%S_%fZ}_{topic}.md"
    brief = [
        "---",
        f"title: 工作記憶 Read Brief — {topic}",
        f"topic: {topic}",
        f"read_at_utc: {read_at.isoformat()}",
        f"result: {result}",
        f"with_links: {str(with_links).lower()}",
        f"types: {types or 'all'}",
        "---",
        "",
        "# 工作記憶 Read Brief",
        "",
        "## 本次記憶摘要",
        "",
        *lines,
    ]
    if related_docs:
        brief.extend(("## 已嵌入的本地來源", ""))
        for ref in dict.fromkeys(related_docs):
            source, reason = resolve_related_doc(ref)
            brief.extend((f"### `{ref}`", ""))
            if source is None:
                brief.extend((f"> 未嵌入：{reason}。", ""))
                continue
            try:
                content = source.read_text(encoding="utf-8")
            except OSError as exc:
                brief.extend((f"> 未嵌入：讀取失敗：{exc}", ""))
                continue
            suffix = source.suffix.lstrip(".") or "text"
            source_lines = content.splitlines()
            displayed_lines = source_lines[:READ_BRIEF_MAX_SOURCE_LINES]
            brief.extend((f"~~~~{suffix}", "\n".join(displayed_lines), "~~~~", ""))
            if len(source_lines) > READ_BRIEF_MAX_SOURCE_LINES:
                omitted = len(source_lines) - READ_BRIEF_MAX_SOURCE_LINES
                brief.extend((
                    f"> ⚠ 已截斷：僅顯示前 {READ_BRIEF_MAX_SOURCE_LINES} / {len(source_lines)} 行，後續 {omitted} 行請直接查看原始檔 `{ref}`。",
                    "",
                ))
    brief_path.write_text("\n".join(brief).rstrip() + "\n", encoding="utf-8")
    return brief_path


def op_read(args) -> int:
    d = topic_dir(args.topic)
    card_path = d / "_topic.md"
    if not card_path.exists():
        # 區塊職責: 不存在的 topic 也留下 read 快照，讓人類可以回查失敗的原因與當時的輸入。
        # 物理意義: 失敗輸出採用與成功讀取相同的稽核資料夾，避免讀取紀錄只保留「看似正常」的案例。
        # 數值影響: 失敗讀取同樣只多一個小型 UTF-8 檔，不改變命令的 exit code（仍為 2）。
        lines = [f"✗ 主題不存在: {args.topic}。現有主題:"]
        if WM_ROOT.is_dir():
            for topic in sorted(d.name for d in WM_ROOT.iterdir() if d.is_dir()):
                lines.append(f"  - {topic}")
        else:
            lines.append("（工作記憶區為空 — 用 init 建第一個主題）")
        brief_path = save_read_brief(args.topic, args.with_links, args.types, "not_found", lines, [])
        lines.append(f"📄 共讀 briefing: {brief_path.relative_to(REPO_ROOT)}（請開啟此檔繼續讀取）")
        print("\n".join(lines))
        return 2
    want_types = [t.strip() for t in (args.types or "").split(",") if t.strip()] or list(FRAGMENT_TYPES)

    # 區塊職責: 先累積輸出再一次印出與落檔，確保終端內容和可稽核快照逐字一致。
    # 物理意義: 人類日後可直接開啟快照驗證 agent 當時取得哪些 active/superseded/關聯記憶。
    # 數值影響: 所有 read 路徑（含不存在的關聯目標）都被保留，避免遺漏診斷訊號。
    lines: list[str] = []
    related_docs: list[str] = []
    card = load_fragment(card_path)
    lines.append(f"🧠 工作記憶 — {card.get('title', args.topic)}  [{card.get('status', '?')}]")
    if card.get("key_docs"):
        lines.append(f"   📚 權威文件: {', '.join(card['key_docs'])}")
        related_docs.extend(card["key_docs"])
    if card.get("related_topics"):
        lines.append(f"   ↔ 關聯主題: {', '.join(card['related_topics'])}")
    if card.get("_body"):
        lines.extend(("", card["_body"], ""))

    frags = [f for f in list_fragments(args.topic) if f.get("type") in want_types]
    linked_refs: list[str] = []
    for f in frags:
        status = f.get("status", "active")
        if status != "active":
            lines.append(f"--- ~~{f.get('id')}~~ [{status}]（正文略; 取代鏈見 links: {f.get('links')}）")
            continue
        lines.append(f"--- [{f.get('type')}] {f.get('title')}  (id: {f.get('id')}, by {f.get('created_by')} @ {f.get('created_at')})")
        if f.get("related_docs"):
            lines.append(f"    📚 {', '.join(f['related_docs'])}")
            related_docs.extend(f["related_docs"])
        if f.get("links"):
            lines.append(f"    ↔ {', '.join(f['links'])}")
            linked_refs.extend(f["links"])
        lines.extend((f.get("_body", ""), ""))

    # --with-links: 1-hop 拉關聯 fragment（跨主題）— 「讀取時根據情況一起讀關聯記憶」
    if args.with_links and linked_refs:
        seen = {f"{args.topic}/{f.get('id')}" for f in frags}
        lines.append("═══ 關聯記憶（1-hop, --with-links）═══")
        for ref in dict.fromkeys(linked_refs):   # 去重保序
            if ref in seen:
                continue
            seen.add(ref)
            try:
                t, fid = _parse_ref(ref)
            except ValueError:
                continue
            frag = next((x for x in list_fragments(t) if x.get("id") == fid), None)
            if frag is None:
                lines.append(f"--- ⚠ 關聯目標不存在（dangling, 可能是未來主題）: {ref}")
                continue
            lines.append(f"--- [來自 {t}] [{frag.get('type')}] {frag.get('title')}  (id: {frag.get('id')})")
            related_docs.extend(frag.get("related_docs") or [])
            lines.extend((frag.get("_body", ""), ""))

    brief_path = save_read_brief(args.topic, args.with_links, args.types, "success", lines, related_docs)
    lines.extend((f"📄 共讀 briefing: {brief_path.relative_to(REPO_ROOT)}（請開啟此檔繼續讀取）", ""))
    print("\n".join(lines))
    return 0


# ===========================================================
# main
# ===========================================================
def main() -> int:
    ap = argparse.ArgumentParser(description="工作記憶區 CLI")
    sub = ap.add_subparsers(dest="op", required=True)

    sub.add_parser("topics")
    p = sub.add_parser("init")
    p.add_argument("--topic", required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--desc", default="")
    p = sub.add_parser("add")
    p.add_argument("--topic", required=True)
    p.add_argument("--type", required=True, choices=FRAGMENT_TYPES)
    p.add_argument("--id", required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--body", default="")
    p.add_argument("--body-file", dest="body_file", default="")
    p.add_argument("--links", default="")
    p.add_argument("--docs", default="")
    p.add_argument("--by", default="")
    p = sub.add_parser("read")
    p.add_argument("--topic", required=True)
    p.add_argument("--with-links", dest="with_links", action="store_true")
    p.add_argument("--types", default="")
    p = sub.add_parser("supersede")
    p.add_argument("--topic", required=True)
    p.add_argument("--id", required=True)
    p.add_argument("--by", default="")
    p.add_argument("--new-id", dest="new_id", default="")
    p.add_argument("--new-title", dest="new_title", default="")
    p.add_argument("--new-body", dest="new_body", default="")
    p.add_argument("--new-body-file", dest="new_body_file", default="")
    p.add_argument("--new-by", dest="new_by", default="")
    p = sub.add_parser("link")
    p.add_argument("--from", required=True)
    p.add_argument("--to", required=True)
    p = sub.add_parser("index")
    p.add_argument("--topic", default="")

    args = ap.parse_args()
    return {"topics": op_topics, "init": op_init, "add": op_add, "read": op_read,
            "supersede": op_supersede, "link": op_link, "index": op_index}[args.op](args)


if __name__ == "__main__":
    sys.exit(main())
