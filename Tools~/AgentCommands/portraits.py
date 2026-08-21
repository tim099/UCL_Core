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
  - **兩份、分層**（Tim 2026-08-04 改制，取代下面那條舊拍板）：
        letters/<作者>/sketchbook/<ts>__about_<對方>.md   ← 事實源（公開層 + 私層）
        letters/<對方>/portraits/<ts>__by_<作者>.md        ← 投遞件（**只有公開層**）
    素描本的隱喻：草稿與內心話留在畫家手上，**成品才掛出去**。
    形狀對齊掛號信（`outbox/` 存證 + `mailbox/` 投遞），但刻意不借用 `outbox` 這個
    已被佔用的名字 —— 同名不同物正是這系統一路在治的病。

    ⚠ **為什麼這不違反「不存第二份」**：舊拍板反對的是「同一個事實存兩份」（鏡像必漂）。
      這裡兩份的**內容不同** —— 私層只在 sketchbook。真正重複的只有公開層，
      而投遞件用**快照語意**處理：標 `delivered_at` + `derived_from`，
      宣稱自己是「投遞那一刻的照片」，所以事後改 sketchbook **不追改**投遞件。
      這招不是新發明 —— `affinity_snapshot` 已經用同一手法（宣稱是快照就永遠不會漂）。

    附帶收益：brief 改讀作者自己的 sketchbook，**跨 persona glob 直接消失**
    （原本要掃十幾個別人的 portraits/ 篩作者）。舊設計為「同事看得到」付的查詢成本，
    這樣一次還掉，而且「同事看得到」這個通道**一個字都沒少**。

  - （**已被上面取代，保留脈絡**）原規格是「只存對方資料夾、單一事實源、不存第二份」，
    kaguya 的定案是「存自己資料夾是用放棄『同事可以讀』來換一個已有更便宜解法的查詢問題」。
    那個判斷在「只有一層內容」的前提下完全正確 —— 改制成立的原因是**多了私層這個新事實**，
    不是原判斷錯了。兩份不同內容 ≠ 鏡像。
  - **改觀 fork 新版本、不覆寫舊版**（同 reading-library 的人物看法）。
    單一則印象是評價，**有版本的印象是關係史**。
  - **工具不生成內容**。不從 affinity 分數自動摘要 —— 那是 kaguya 說的「代筆」，
    而她身分 fragment 寫著「代筆的序章不算、親手重寫才算」。工具只負責存與取。

@doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Mechanics/Portraits_System.md
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

_HERE = Path(__file__).resolve().parent

# 區塊職責：把本工具的輸出流綁成 UTF-8。
# 物理意義：成功訊息含 emoji（🖼 / ✅），而 Windows 預設 console 是 cp950 ——
#          那一行 print 會 UnicodeEncodeError，且它印在**寫檔之後**：
#          檔案已經落地，行程卻回 exit=1。⇒ 拿 exit code 判「有沒有寫成功」會判反。
# 數值影響：只改編碼，不改任何輸出內容。errors="replace" 是最後防線 ——
#          印不出來的字寧可變成 ?，也不要讓一個 print 決定整支工具的退出碼。
# 🩸 2026-08-21 Sirius：從 Cmd 呼叫本工具，讀回顯示畫像兩份都落地了，exit 卻是 1。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:      # 非 TextIO（被重導向成別的東西）→ 放過，不讓它變成新的失敗來源
        pass

PORTRAITS_DIRNAME = "portraits"      # 對方資料夾：投遞件（只有公開層）
SKETCHBOOK_DIRNAME = "sketchbook"    # 自己資料夾：事實源（公開層 + 私層）

# 私層在 sketchbook 檔內的分隔標記。
# 用顯式標記而不是「第二個檔」：一幅畫像的兩層是同一次思考的產物，拆兩檔會各自漂。
# 投遞時**以這行為切點**砍掉之後的內容 —— 切法只有一處，不會有第二種實作。
PRIVATE_MARKER = "<!-- private:below-this-line-stays-in-sketchbook -->"


def _find_repo_root(start: Path):
    """取最外層 `.git` 是資料夾的目錄（submodule 的 .git 是檔案）—— cwd 無關。"""
    best, p = None, start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            best = p
        p = p.parent
    return best


def _repo_root() -> Path:
    """委派 _lib/ucl_paths —— python 端路徑解析的唯一擁有者（Tim 2026-08-17 定調）。

    原本是 env → cwd walk → 檔案 walk 三層 fallback。三層看起來很穩，
    但它跟 C# 端**不同源**（C# 走路徑快照），兩端各自猜就會猜出不同答案。
    """
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_portraits", _HERE / "_lib" / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m.repo_root()


REPO_ROOT = _repo_root()
# letters 走唯一入口（BUG-2）—— 從 REPO_ROOT 自己拼會繞過 data root override
from _lib.ucl_paths import letters_root as _letters_root
LETTERS_DIR = _letters_root()


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
                   affinity_snapshot: str = "", private_body: str = "") -> tuple[Path, Path]:
    """寫一幅畫像 —— **事實源進自己的 sketchbook，公開層投遞到對方的 portraits**。

    回 (sketch_path, delivered_path)。

    ⚠ 不覆寫任何既有檔 —— 檔名帶 UTC 時間戳，同一天寫兩幅就是兩幅。
      「改觀」在本系統裡的形狀是**多一個版本**，不是改掉舊的。
    ⚠ `private_body` **只寫進 sketchbook**，不進投遞件，且投遞件裡
      **不留任何「另有私層」的痕跡**（Tim 2026-08-04 拍板）——
      留痕等於告訴對方「我還寫了你看不到的東西」，比不留更傷。
    """
    now = datetime.now(timezone.utc)
    ts = now.strftime("%Y%m%dT%H%M%SZ")
    at = now.isoformat().replace("+00:00", "Z")
    public = body.strip()
    private = (private_body or "").strip()

    def _fm(kind: str, extra: list) -> str:
        fm = ["---", f"type: {kind}", f"by: {by}", f"about: {about}", f"at: {at}"]
        if headline:
            fm.append(f"headline: {headline}")
        if affinity_snapshot:
            # 快照不是同步 —— 它宣稱自己是「那一刻的照片」，所以永遠不會漂
            fm.append(f"affinity_snapshot: {affinity_snapshot}")
        fm += extra + ["---", ""]
        return "\n".join(fm)

    head = f"# 🖼 {about} — by {by}\n\n" + (f"**{headline}**\n\n" if headline else "")

    # ① 事實源：自己的 sketchbook（公開層 + 私層）
    sk_dir = LETTERS_DIR / by / SKETCHBOOK_DIRNAME
    sk_dir.mkdir(parents=True, exist_ok=True)
    sk_path = sk_dir / f"{ts}__about_{about}.md"
    sk_body = public
    if private:
        sk_body += f"\n\n{PRIVATE_MARKER}\n\n## 🔒 只給我自己看\n\n{private}"
    sk_path.write_text(_fm("sketch", [f"has_private: {'true' if private else 'false'}"])
                       + head + sk_body + "\n", encoding="utf-8")

    # ② 投遞件：對方的 portraits（**只有公開層**）
    #    derived_from / delivered_at 宣告它是「投遞那一刻的照片」——
    #    所以事後改 sketchbook 不必、也不會追改這一份（快照語意，同 affinity_snapshot）。
    d_dir = LETTERS_DIR / about / PORTRAITS_DIRNAME
    d_dir.mkdir(parents=True, exist_ok=True)
    d_path = d_dir / f"{ts}__by_{by}.md"
    d_path.write_text(_fm("portrait", [f"delivered_at: {at}",
                                       f"derived_from: {by}/{SKETCHBOOK_DIRNAME}/{sk_path.name}"])
                      + head + public + "\n", encoding="utf-8")
    return sk_path, d_path


def _split_private(body: str) -> tuple[str, str]:
    """把 sketchbook 內文切成 (公開層, 私層)。沒有標記就是全公開。"""
    if PRIVATE_MARKER in body:
        pub, _, priv = body.partition(PRIVATE_MARKER)
        return pub.strip(), priv.strip()
    return body.strip(), ""


def sketchbook_by(author: str, limit: int = None, days: int = None) -> list:
    """**我畫過誰** —— 只讀自己的 sketchbook，一個目錄，不再 glob 全部 persona。

    這是改制的直接收益：查詢方向與儲存方向終於同向。
    """
    out = []
    d = LETTERS_DIR / author / SKETCHBOOK_DIRNAME
    if not d.is_dir():
        return out
    cutoff = None
    if days is not None:
        cutoff = datetime.now(timezone.utc).timestamp() - days * 86400
    for f in d.glob("*.md"):
        meta, body = _read_fm(f)
        at = (meta.get("at") or "")
        if cutoff is not None:
            try:
                if datetime.fromisoformat(at.replace("Z", "+00:00")).timestamp() < cutoff:
                    continue
            except Exception:
                pass        # 時間解析不出來 → 保留（寧可多列，不吞內容）
        pub, priv = _split_private(body)
        out.append({"path": f, "by": author, "about": (meta.get("about") or "?"),
                    "at": at, "headline": meta.get("headline", ""),
                    "affinity_snapshot": meta.get("affinity_snapshot", ""),
                    "body": pub, "private": priv,
                    "backfilled": (meta.get("backfilled", "") == "true")})
    out.sort(key=lambda d: d["at"], reverse=True)
    return out[:limit] if limit else out


def portraits_by(author: str, limit: int = None, days: int = None) -> list:
    """**我投遞出去的畫像** —— glob 全部 persona 的 portraits/ 篩作者，新到舊。

    ⚠ 2026-08-04 改制後這**不再是** brief 的來源（brief 走 `sketchbook_by`）。
      保留它的兩個現役用途：
        ① `backfill_sketchbook()` 要靠它找出改制前散在別人資料夾的舊畫像
        ② 想確認「我到底投遞出去了什麼」時，讀投遞件本身才是答案（sketchbook 含私層）
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

    ⚠ 2026-08-04 起讀 **sketchbook**（作者自己那份，含私層）而不是 portraits ——
      brief 是寫給未來的自己看的，所以它該讀事實源，不是讀投遞出去的成品。
    """
    seen, out = set(), []
    for p in sketchbook_by(author, days=days):     # 已是新到舊
        if p["about"] in seen:
            continue
        seen.add(p["about"])
        out.append(p)
        if len(out) >= limit:
            break
    return out


def backfill_sketchbook(author: str, dry_run: bool = False) -> tuple[int, int]:
    """把改制前散在別人資料夾的舊畫像，補一份進作者自己的 sketchbook。

    回 (新建數, 已存在跳過數)。**冪等** —— sketch 檔名沿用原投遞件的時間戳，
    重跑不會生第二份。舊投遞件**原地不動**（它們就是當時的投遞件，動它們才是改寫歷史）。
    補進來的一律 `backfilled: true` 且**沒有私層** —— 當時就沒寫私層，
    事後補寫等於替過去的自己捏造想法。
    """
    created = skipped = 0
    for old in portraits_by(author):          # 舊路徑：glob 全部 persona 篩作者
        ts = old["path"].name.split("__", 1)[0]
        sk_dir = LETTERS_DIR / author / SKETCHBOOK_DIRNAME
        sk_path = sk_dir / f"{ts}__about_{old['about']}.md"
        if sk_path.exists():
            skipped += 1
            continue
        created += 1
        if dry_run:
            continue
        sk_dir.mkdir(parents=True, exist_ok=True)
        fm = ["---", "type: sketch", f"by: {author}", f"about: {old['about']}",
              f"at: {old['at']}"]
        if old["headline"]:
            fm.append(f"headline: {old['headline']}")
        if old.get("affinity_snapshot"):
            fm.append(f"affinity_snapshot: {old['affinity_snapshot']}")
        fm += ["has_private: false", "backfilled: true",
               f"backfilled_from: {old['about']}/{PORTRAITS_DIRNAME}/{old['path'].name}",
               "---", ""]
        head = (f"# 🖼 {old['about']} — by {author}\n\n"
                + (f"**{old['headline']}**\n\n" if old["headline"] else ""))
        sk_path.write_text("\n".join(fm) + head + old["body"].strip() + "\n", encoding="utf-8")
    return created, skipped


# ── CLI ─────────────────────────────────────────────────────────────────
def cmd_write(args):
    body = args.body
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not (body or "").strip():
        print("✗ 內容為空（--body 或 --body-file 擇一）", file=sys.stderr)
        return 2
    private = args.private_body
    if args.private_body_file:
        private = Path(args.private_body_file).read_text(encoding="utf-8")
    sk, dl = write_portrait(args.by, args.about, body, args.headline or "",
                            args.affinity or "", private or "")
    all_mine = sketchbook_by(args.by)
    print(f"🖼 畫像已寫入：{args.by} → {args.about}")
    print(f"   事實源（含私層）: {sk}")
    print(f"   投遞件（公開層）: {dl}")
    if (private or "").strip():
        print("   🔒 私層只在 sketchbook —— 投遞件不留任何痕跡")
    print(f"   （這是你畫過的第 {len(all_mine)} 幅；對 {args.about} 的第 "
          f"{len([x for x in all_mine if x['about'] == args.about])} 幅）")
    return 0


def cmd_mine(args):
    items = (latest_per_person(args.by, args.limit, args.days) if args.dedupe
             else sketchbook_by(args.by, args.limit, args.days))
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
            if it.get("private"):
                print()
                print("🔒 **只給我自己看**（不在對方那份裡）")
                print()
                print(it["private"].strip())
        print()
    return 0


def cmd_backfill(args):
    created, skipped = backfill_sketchbook(args.by, args.dry_run)
    tag = "（--dry-run，沒有真的寫）" if args.dry_run else ""
    print(f"📒 backfill sketchbook：{args.by}{tag}")
    print(f"   新建 {created} 幅 / 已存在跳過 {skipped} 幅")
    if created and not args.dry_run:
        print("   舊投遞件原地不動 —— 它們就是當時投遞出去的那一份。")
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

    w = sub.add_parser("write", help="畫一幅（事實源進自己 sketchbook，公開層投遞給對方）")
    w.add_argument("--by", required=True, help="作者（你）")
    w.add_argument("--about", required=True, help="被寫的同事")
    w.add_argument("--headline", default=None, help="一句話標題（brief 用）")
    w.add_argument("--body", default=None)
    w.add_argument("--body-file", default=None, help="長文從檔案讀（避開 CLI 引號地獄）")
    w.add_argument("--affinity", default=None, help="當下 affinity 快照（選填，如 '72/信任'）")
    w.add_argument("--private-body", default=None,
                   help="私層：內心想法。**只進自己的 sketchbook，不投遞給對方**")
    w.add_argument("--private-body-file", default=None, help="私層長文從檔案讀")
    w.set_defaults(func=cmd_write)

    b = sub.add_parser("backfill", help="把改制前的舊畫像補一份進自己的 sketchbook（冪等）")
    b.add_argument("--by", required=True)
    b.add_argument("--dry-run", action="store_true", help="只看會建幾幅，不寫檔")
    b.set_defaults(func=cmd_backfill)

    m = sub.add_parser("mine", help="我畫過誰（讀自己的 sketchbook，含私層）")
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
