#!/usr/bin/env python3
"""印象畫像（Portraits）—— 「那個人在我眼裡的樣子」，晚安時寫、早安時讀回。

區塊職責：補上 wake brief 唯一的空缺 —— **「我認識誰」**。

物理意義：
  現有的記憶層各自回答一個問題，但沒有一層回答「這些同事是誰」：
      見根 fragments  → 我是誰
      見叢 keys       → 我要做什麼
      見樹 letter     → 我昨天經歷什麼
      affinity        → 我跟他分數多少（**數字，不是人**）
  醒來時同事只是酒館裡的一串名字：知道 kotoko 在做 P0a，但那是任務不是人。
  portraits 存的是**那個人在我眼裡的樣子**，由昨天與更早的我寫給今天的我。

  ⚠ 這是**記憶接續機制，不是社交評價機制**（Tim 2026-08-01 澄清；我一度讀反方向，
    還拿錯的前提去問了六個同事）。讀的人是**未來的自己**；被寫的人**可以**去讀
    （檔案就在他資料夾裡），但不強迫、也不進他的 brief。

設計取捨：
  - **存對方的資料夾**（Tim 原規格，kaguya 與我同投）：
        letters/<被寫的人>/portraits/<ts>__by_<作者>.md
    查詢「我寫過誰」要 glob 全部 persona 的 portraits/ —— 十來個目錄，毫秒級。
    kaguya 的一句話定案：「(b) 存自己資料夾**是用放棄『同事可以讀』來解一個已經有
    更便宜解法的查詢問題**」。為省一個 glob 去砍掉這系統唯一一個
    「我對你的看法你看得到」的通道，是因噎廢食。
  - **單一事實源，不存第二份**。要快就生機械索引（可重建、可 diff）——
    那是視圖不是事實。鏡像會漂且無聲；索引漂了跑一次就對回來。
  - **改觀 fork 新版本、不覆寫舊版**（同 reading-library 的人物看法）。
    單一則印象是評價，**有版本的印象是關係史**。
  - **工具不生成內容**。不從 affinity 分數自動摘要 —— 那是 kaguya 說的「代筆」，
    而她身分 fragment 寫著「代筆的序章不算、親手重寫才算」。工具只負責存與取。
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

_HERE = Path(__file__).resolve().parent
PORTRAITS_DIRNAME = "portraits"


def _find_repo_root(start: Path):
    """取最外層 `.git` 是資料夾的目錄（submodule 的 .git 是檔案）—— cwd 無關。"""
    best, p = None, start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            best = p
        p = p.parent
    return best


def _repo_root() -> Path:
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and Path(env).is_dir():
        return Path(env).resolve()
    return _find_repo_root(Path.cwd()) or _find_repo_root(_HERE) or Path.cwd().resolve()


REPO_ROOT = _repo_root()
LETTERS_DIR = REPO_ROOT / "AgentCommands" / "ChatTavern" / "baton" / "letters"


def _read_fm(path: Path) -> tuple[dict, str]:
    """回 (frontmatter dict, body)。壞檔回 ({}, 全文) —— 不吞內容。"""
    meta = {}
    try:
        text = path.read_text(encoding="utf-8")
    except Exception:
        return {}, ""
    t = text.lstrip()
    if t.startswith("---"):
        end = t.find("\n---", 3)
        if end != -1:
            for line in t[3:end].splitlines():
                if ":" in line:
                    k, _, v = line.partition(":")
                    meta[k.strip()] = v.strip()
            return meta, t[end + 4:].lstrip("\n")
    return meta, text


def write_portrait(by: str, about: str, body: str, headline: str = "",
                   affinity_snapshot: str = "") -> Path:
    """寫一幅畫像進**被寫者**的資料夾。回檔案路徑。

    ⚠ 不覆寫任何既有檔 —— 檔名帶 UTC 時間戳，同一天寫兩幅就是兩幅。
      「改觀」在本系統裡的形狀是**多一個版本**，不是改掉舊的。
    """
    ts = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    d = LETTERS_DIR / about / PORTRAITS_DIRNAME
    d.mkdir(parents=True, exist_ok=True)
    fm = ["---", "type: portrait", f"by: {by}", f"about: {about}",
          f"at: {datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')}"]
    if headline:
        fm.append(f"headline: {headline}")
    if affinity_snapshot:
        # 快照不是同步 —— 它宣稱自己是「那一刻的照片」，所以永遠不會漂
        fm.append(f"affinity_snapshot: {affinity_snapshot}")
    fm += ["---", ""]
    head = f"# 🖼 {about} — by {by}\n\n" + (f"**{headline}**\n\n" if headline else "")
    p = d / f"{ts}__by_{by}.md"
    p.write_text("\n".join(fm) + head + body.strip() + "\n", encoding="utf-8")
    return p


def portraits_by(author: str, limit: int = None, days: int = None) -> list:
    """**我畫過誰** —— glob 全部 persona 的 portraits/ 篩作者，新到舊。

    這是「查詢方向與儲存方向相反」那題的解：不存第二份，直接掃。
    十來個 persona 目錄，實測毫秒級 —— 為它多存一份鏡像不划算（鏡像會漂）。
    """
    out = []
    if not LETTERS_DIR.is_dir():
        return out
    cutoff = None
    if days is not None:
        cutoff = datetime.now(timezone.utc).timestamp() - days * 86400
    for pdir in sorted(LETTERS_DIR.glob(f"*/{PORTRAITS_DIRNAME}")):
        for f in pdir.glob("*.md"):
            meta, body = _read_fm(f)
            if (meta.get("by") or "").strip() != author:
                continue
            at = (meta.get("at") or "")
            if cutoff is not None:
                try:
                    if datetime.fromisoformat(at.replace("Z", "+00:00")).timestamp() < cutoff:
                        continue
                except Exception:
                    pass        # 時間解析不出來 → 保留（寧可多列，不吞內容）
            out.append({"path": f, "by": author, "about": (meta.get("about") or pdir.parent.name),
                        "at": at, "headline": meta.get("headline", ""),
                        "affinity_snapshot": meta.get("affinity_snapshot", ""), "body": body})
    out.sort(key=lambda d: d["at"], reverse=True)
    return out[:limit] if limit else out


def portraits_of(about: str, limit: int = None) -> list:
    """**誰畫過我** —— 讀自己資料夾。被寫的人想看就看得到（不進他 brief，不強迫）。"""
    d = LETTERS_DIR / about / PORTRAITS_DIRNAME
    out = []
    if not d.is_dir():
        return out
    for f in d.glob("*.md"):
        meta, body = _read_fm(f)
        out.append({"path": f, "by": meta.get("by", "?"), "about": about,
                    "at": meta.get("at", ""), "headline": meta.get("headline", ""),
                    "body": body})
    out.sort(key=lambda d: d["at"], reverse=True)
    return out[:limit] if limit else out


def latest_per_person(author: str, limit: int = 5, days: int = None) -> list:
    """每人只取**最新一幅**，再取前 N 人 —— brief 用。

    為什麼去重到人：brief 要回答「這幾天我對誰印象最深」，同一個人畫三幅
    不該佔掉三格。舊版留在檔案裡可回溯，但**只有最新版進 brief** ——
    這樣舊印象會被新印象自然取代，不會變成常駐標籤。
    """
    seen, out = set(), []
    for p in portraits_by(author, days=days):     # 已是新到舊
        if p["about"] in seen:
            continue
        seen.add(p["about"])
        out.append(p)
        if len(out) >= limit:
            break
    return out


# ── CLI ─────────────────────────────────────────────────────────────────
def cmd_write(args):
    body = args.body
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not (body or "").strip():
        print("✗ 內容為空（--body 或 --body-file 擇一）", file=sys.stderr)
        return 2
    p = write_portrait(args.by, args.about, body, args.headline or "", args.affinity or "")
    prev = len(portraits_by(args.by)) - 1
    print(f"🖼 畫像已寫入：{args.by} → {args.about}")
    print(f"   {p}")
    print(f"   （這是你畫過的第 {prev + 1} 幅；對 {args.about} 的第 "
          f"{len([x for x in portraits_by(args.by) if x['about'] == args.about])} 幅）")
    return 0


def cmd_mine(args):
    items = (latest_per_person(args.by, args.limit, args.days) if args.dedupe
             else portraits_by(args.by, args.limit, args.days))
    if not items:
        print(f"(還沒畫過任何人{f'（近 {args.days} 天）' if args.days else ''})")
        return 0
    print(f"# 🖼 {args.by} 眼中的同事（{len(items)} 幅"
          + (f"，近 {args.days} 天" if args.days else "") + "）\n")
    for it in items:
        print(f"## {it['about']}　_{it['at'][:10]}_"
              + (f"　{it['headline']}" if it["headline"] else ""))
        if args.full:
            print()
            print(it["body"].strip())
        print()
    return 0


def cmd_of(args):
    items = portraits_of(args.about, args.limit)
    if not items:
        print(f"(還沒有人畫過 {args.about})")
        return 0
    print(f"# 🖼 別人眼中的 {args.about}（{len(items)} 幅）\n")
    for it in items:
        print(f"## by {it['by']}　_{it['at'][:10]}_"
              + (f"　{it['headline']}" if it["headline"] else ""))
        if args.full:
            print()
            print(it["body"].strip())
        print()
    return 0


def main():
    ap = argparse.ArgumentParser(description="印象畫像 — 那個人在我眼裡的樣子（晚安寫、早安讀回）")
    sub = ap.add_subparsers(dest="op", required=True)

    w = sub.add_parser("write", help="畫一幅（寫進被寫者的資料夾）")
    w.add_argument("--by", required=True, help="作者（你）")
    w.add_argument("--about", required=True, help="被寫的同事")
    w.add_argument("--headline", default=None, help="一句話標題（brief 用）")
    w.add_argument("--body", default=None)
    w.add_argument("--body-file", default=None, help="長文從檔案讀（避開 CLI 引號地獄）")
    w.add_argument("--affinity", default=None, help="當下 affinity 快照（選填，如 '72/信任'）")
    w.set_defaults(func=cmd_write)

    m = sub.add_parser("mine", help="我畫過誰")
    m.add_argument("--by", required=True)
    m.add_argument("--limit", type=int, default=None)
    m.add_argument("--days", type=int, default=None, help="只看近 N 天")
    m.add_argument("--dedupe", action="store_true", help="每人只取最新一幅")
    m.add_argument("--full", action="store_true", help="印全文")
    m.set_defaults(func=cmd_mine)

    o = sub.add_parser("of", help="誰畫過我（或某人）")
    o.add_argument("--about", required=True)
    o.add_argument("--limit", type=int, default=None)
    o.add_argument("--full", action="store_true")
    o.set_defaults(func=cmd_of)

    args = ap.parse_args()
    raise SystemExit(args.func(args))


if __name__ == "__main__":
    main()
