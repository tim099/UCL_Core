#!/usr/bin/env python3
# 區塊職責：酒館歷史書 **Phase A**（機械）—— 把某一天的酒館訊息匯出成「當日全文工作稿」。
# 物理意義：產物是**工作稿不是書**，落在 <data_root>/TavernHistory/drafts/<room>/，
#          刻意不寫 Books/ —— 書要由編者在 Phase B 逐則取捨後才生得出來。
# 數值影響：本工具**一則都不丟**。它唯一會做的判定是「這則是不是機器代組的」，
#          其餘全部標成 pending 交給人 —— 因為「原文照收 vs 摘要」是語意判斷，
#          寫死進工具就等於假裝機器讀得懂創作。
#
# 為什麼不直接用 library.py 的 export-watch：
#   那支的職責是「把一段觀影 seq 直接寫成書的一章」，所以它**過濾**（--exclude-tags 丟掉公告）
#   並且**落點就是 Books/**。歷史書要的相反 —— Phase A 要完整（含公告），
#   落點是草稿區；過濾與取捨留到 Phase B 由人做。兩者共用的只有
#   「清自動附掛」與「未收錄逐筆列出」這兩條經驗，那兩條在本檔重寫成同語意。
#
# Tim 2026-08-19 拍板：原文照收僅限創作／散文等人工判定的部分，其餘生成摘要 ——
# 否則產物就跟 export-watch 沒有差別（見酒館 seq 12252）。

import argparse
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")


# ===========================================================
# 區塊職責：路徑 —— 一律走 _lib/ucl_paths.py，不自己推導
# 物理意義：ucl_paths 讀 C# 寫的路徑快照，兩端因此保證同源
# 數值影響：自推導的失敗是靜默的（會找到另一棵資料樹並回一個看起來正常的數字）
# ===========================================================
def _ucl_paths():
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_tavernhistory", Path(__file__).resolve().parent / "_lib" / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m


REPO_ROOT = _ucl_paths().repo_root()
DATA_ROOT = _ucl_paths().data_root()


def _rooms_dir() -> Path:
    return DATA_ROOT / "ChatTavern" / "rooms"


def _drafts_dir(room: str) -> Path:
    return DATA_ROOT / "TavernHistory" / "drafts" / room


def _rel(p: Path) -> str:
    try:
        return str(p.relative_to(REPO_ROOT))
    except ValueError:
        return str(p)


# ===========================================================
# 區塊職責：自動附掛區塊清除
# 物理意義：Cmd_Glossary 會在訊息尾端追加「📖 本回提到的新詞」，那是**工具寫的**不是說話的人寫的
# 數值影響：不清掉 ⇒ 工作稿裡混進機器產文，編者會把它當成當事人的話收進書裡
# ===========================================================
_AUTO_ATTACH_RE = re.compile(r"[\r\n]+---[\r\n]+\s*📖\s*\*\*本回提到的新詞\*\*.*\Z", re.S)

# 區塊職責：可被機器判定為「機器代組」的 meta.tag
# 物理意義：這些訊息的內文由 Cmd 拼出來（commit 公告 / 保管費 / 早晚安協議格式），沒有作者的筆跡
# 數值影響：只影響「建議」欄；Phase B 仍可推翻。**free-time 刻意不在此列** ——
#          自由時間貼文常常是創作，把它自動打成附錄會誤殺
APPENDIX_TAGS = {
    "commit",
    "bartender-relay",
    "bartender-rule-announce",
    "goodmorning-protocol",
    "goodnight-protocol",
}

# 區塊職責：**不是人**的發話端 —— 系統元件，不是有筆跡的作者。
# 物理意義：酒保是排程廣播；`_quest_system` 是任務系統的事件流（建任務／認領／進度／完成），
#          內文由 Cmd 拼出來，一則一個動作。
# 數值影響：只影響「建議」欄。⚠ **`discord:` 開頭的不算系統** —— 那是 Tim 從 Discord
#          說的真話，只是經由另一條通道進來（2026-05-16 那天他從 Discord 發了 Hellow world）。
SYSTEM_SENDERS = {"酒保", "bartender", "tavern-keeper", "_quest_system"}

# 處置代碼 —— Phase B 由編者逐則填進 triage.json 的 disposition
# ⚠ `drop` ＝ **不收進書，但仍在處置總表上留一行**（Tim 2026-08-19：有些訊息可以過濾掉）。
#   它跟「無聲消失」的差別就是那一行 —— 讀者查得到「這則被濾掉了，理由是什麼」。
#   純事件流（quest 建任務／認領、酒保時間提醒）走這條；有內容的機械公告（commit）仍走 appendix。
DISPOSITIONS = ("raw", "summary", "appendix", "drop")


def _load_day(room: str, date: str):
    """讀 messages/<date>/ 底下全部訊息，回傳 [(seq, msg_or_None, path)]（seq 由檔名決定）。

    物理意義：日期資料夾是**寫入當天的 UTC 日期**（實測 2026-08-11 夾內 ts 為
             00:08Z–09:19Z ＝ 本地 +08 的 08:08–17:19）。所以「一天」的邊界是 UTC，
             不是本地午夜 —— 本工具不替使用者換算，只如實印出兩種時間。
    """
    base = _rooms_dir() / room / "messages" / date
    if not base.is_dir():
        return None
    out = []
    for f in sorted(base.glob("*.json")):
        try:
            seq = int(f.stem)
        except ValueError:
            continue
        try:
            out.append((seq, json.loads(f.read_text(encoding="utf-8")), f))
        except Exception as e:
            # fail-soft 要出聲：讀不動的那則仍佔一個位置，不靜默消失
            print(f"⚠ 讀不動 {f.name}: {e}（本筆仍列入清單，標為讀檔失敗）", file=sys.stderr)
            out.append((seq, None, f))
    out.sort(key=lambda t: t[0])
    return out


def _suggest(persona: str, tag: str) -> str:
    """機器唯一敢下的判定：這則是不是機器代組的。

    數值影響：回 'appendix' ＝ 有把握；回 'pending' ＝ **機器不知道**，要人看。
             刻意不區分 raw / summary —— 那需要讀懂內容，工具做不到，
             硬做出來的建議會被當成判斷結果照抄（那正是本工具要避免的事）。
    """
    if persona in SYSTEM_SENDERS:
        return "appendix"
    if tag in APPENDIX_TAGS:
        return "appendix"
    return "pending"


def cmd_export_day(args):
    room, date = args.room, args.date
    rows = _load_day(room, date)
    if rows is None:
        print(f"❌ 找不到 {_rel(_rooms_dir() / room / 'messages' / date)} —— "
              f"確認房名與日期（日期資料夾是 UTC 日）。", file=sys.stderr)
        return 1
    if not rows:
        print(f"❌ {date} 資料夾存在但沒有訊息檔。", file=sys.stderr)
        return 1

    out_dir = Path(args.out_dir) if args.out_dir else _drafts_dir(room)
    md_path = out_dir / f"{date}_raw.md"
    tri_path = out_dir / f"{date}_triage.json"
    if md_path.exists() and not args.force:
        print(f"❌ {_rel(md_path)} 已存在 —— 拒絕覆寫（Phase B 的 triage 可能已經填了一半）。\n"
              f"   確定要重出：--force", file=sys.stderr)
        return 1

    stripped = 0
    items, unreadable = [], []
    for seq, msg, f in rows:
        if msg is None:
            unreadable.append(seq)
            items.append({"seq": seq, "ts": "", "persona": "?", "bank": "",
                          "tag": "", "subtag": "", "chars": 0,
                          "suggested": "pending", "body": "", "error": "讀檔失敗"})
            continue
        meta = msg.get("meta") or {}
        if not isinstance(meta, dict):
            meta = {}
        persona = msg.get("sender_persona") or msg.get("sender_id") or "?"
        tag = str(meta.get("tag", "") or "")
        body, n = _AUTO_ATTACH_RE.subn("", msg.get("body") or "")
        stripped += n
        body = body.strip()
        items.append({
            "seq": seq,
            "ts": msg.get("ts", "") or "",
            # sender_name 是**銀行**不是人 —— 兩欄都留著，編者才不會把 bank 當成作者署名
            "persona": persona,
            "bank": msg.get("sender_name", "") or "",
            "tag": tag,
            "subtag": str(meta.get("subtag", "") or ""),
            "chars": len(body),
            "suggested": _suggest(persona, tag),
            "body": body,
        })

    # 🩸 抄自 export-watch 的血證：清除數 0 通常代表 regex 沒對上（\r\n 混排是慣犯），
    #    而它的症狀跟「這批真的沒有附掛」一模一樣。要嘛擋下，要嘛請人明說。
    if stripped == 0 and not args.allow_zero_stripped:
        print("❌ 自動附掛清除數 = 0 —— 這個欄位存在的唯一理由就是防靜默過濾，"
              "回報 0 通常代表 pattern 沒對上。\n"
              "   確認過這一天真的沒有附掛區塊 → 加 --allow-zero-stripped 明說。", file=sys.stderr)
        return 1

    by_persona, by_tag, by_sugg = {}, {}, {}
    total_chars = 0
    for it in items:
        by_persona[it["persona"]] = by_persona.get(it["persona"], 0) + 1
        by_tag[it["tag"] or "(無)"] = by_tag.get(it["tag"] or "(無)", 0) + 1
        by_sugg[it["suggested"]] = by_sugg.get(it["suggested"], 0) + 1
        total_chars += it["chars"]
    span = f"{items[0]['seq']}–{items[-1]['seq']}"
    ts_lo = next((i["ts"] for i in items if i["ts"]), "")
    ts_hi = next((i["ts"] for i in reversed(items) if i["ts"]), "")

    L = []
    L.append(f"# 酒館當日全文工作稿 — {date}（room `{room}`）")
    L.append("")
    L.append("> ⚠ **這是 Phase A 的機械工作稿，不是書，不入 `Books/`。**")
    L.append("> 內容為該日資料夾內**全部**訊息原文，僅移除自動附掛區塊（Cmd_Glossary 的新詞區）。")
    L.append("> 手改會被下次匯出覆寫；要改內容請改酒館訊息本身。")
    L.append("> 「建議」欄只回答一件事：**這則是不是機器代組的**。"
             "`pending` ＝ 工具不知道，要編者判斷原文照收／摘要。")
    L.append("")
    L.append("## 當日讀數")
    L.append("")
    L.append("| | |")
    L.append("|---|---|")
    L.append(f"| 日期資料夾 | `{date}`（**UTC 日**；本地 +08 會落在隔日凌晨以後） |")
    L.append(f"| seq 區間 | {span} |")
    L.append(f"| 時間範圍 | {ts_lo} → {ts_hi} |")
    L.append(f"| 訊息數 | **{len(items)} 則**／正文合計 **{total_chars:,} 字元** |")
    L.append(f"| 清掉自動附掛 | {stripped} 處 |")
    if unreadable:
        L.append(f"| ⚠ 讀檔失敗 | {len(unreadable)} 則（seq {', '.join(str(s) for s in unreadable)}） |")
    L.append("")
    L.append("### 發話人（`sender_persona`；括號內為銀行 `sender_name`，不是作者）")
    L.append("")
    for p, n in sorted(by_persona.items(), key=lambda kv: -kv[1]):
        banks = sorted({i["bank"] for i in items if i["persona"] == p and i["bank"]})
        L.append(f"- **{p}** {n} 則（{'／'.join(banks) if banks else '—'}）")
    L.append("")
    L.append("### meta.tag")
    L.append("")
    for t, n in sorted(by_tag.items(), key=lambda kv: -kv[1]):
        L.append(f"- `{t}` {n} 則")
    L.append("")
    L.append("### 機器建議（**不是裁決**）")
    L.append("")
    for s, n in sorted(by_sugg.items(), key=lambda kv: -kv[1]):
        why = "機器代組（tag 或 sender 可判定）" if s == "appendix" else "工具讀不懂，交人判斷"
        L.append(f"- `{s}` {n} 則 —— {why}")
    L.append("")
    L.append(f"⇒ Phase B 要處置的是 **{by_sugg.get('pending', 0)} 則**；"
             f"逐則填 `{tri_path.name}` 的 `disposition`（`raw` / `summary` / `appendix`），"
             f"再跑 `tavern_history.py verify --date {date}` 對帳。")
    L.append("")
    L.append("---")
    L.append("")
    L.append("## 全文")
    L.append("")
    for it in items:
        hhmm = (it["ts"] or "")[11:16]
        head = f"### [seq {it['seq']}] {hhmm}Z · {it['persona']}"
        if it["tag"]:
            head += f" · `{it['tag']}`"
        if it["subtag"]:
            head += f" · {it['subtag']}"
        L.append(head)
        L.append("")
        L.append(f"<sub>建議 `{it['suggested']}`｜{it['chars']} 字元"
                 f"｜bank `{it['bank'] or '—'}`</sub>")
        L.append("")
        if it.get("error"):
            L.append(f"> ⚠ {it['error']} —— 本則內文不可得，但它**存在**，不許當成沒發生過。")
        else:
            L.append(it["body"] if it["body"] else "*（空內文）*")
        L.append("")

    out_dir.mkdir(parents=True, exist_ok=True)
    md_path.write_text("\n".join(L).replace("\r\n", "\n"), encoding="utf-8")

    # triage：Phase B 的填表對象。disposition 留空＝還沒處置，verify 會抓出來。
    tri = {
        "room": room,
        "date": date,
        "seq_span": span,
        "generated_from": _rel(_rooms_dir() / room / "messages" / date),
        "raw_draft": _rel(md_path),
        "dispositions": list(DISPOSITIONS),
        "items": [{"seq": i["seq"], "ts": i["ts"], "persona": i["persona"], "tag": i["tag"],
                   "chars": i["chars"], "suggested": i["suggested"],
                   # 機器建議 appendix 的先填好（可推翻）；pending 一律留空逼人看
                   "disposition": "appendix" if i["suggested"] == "appendix" else "",
                   "note": ""} for i in items],
    }
    if tri_path.exists() and not args.force:
        print(f"⚠ {_rel(tri_path)} 已存在 —— 保留既有 triage（沒有 --force 就不動已填的處置）。")
    else:
        tri_path.write_text(json.dumps(tri, ensure_ascii=False, indent=2), encoding="utf-8")

    # 印 ✓ 不算數 —— 回讀落地的檔案再報數字
    back = md_path.read_text(encoding="utf-8")
    print(f"✅ 工作稿 {_rel(md_path)}")
    print(f"   {len(items)} 則／{total_chars:,} 字元／清掉附掛 {stripped} 處／seq {span}")
    print(f"   回讀驗證：{len(back.splitlines())} 行、{len(back):,} 字元、"
          f"全文段 {back.count('### [seq ')} 則")
    if back.count("### [seq ") != len(items):
        print(f"   ❌ 回讀段數與訊息數不符（{back.count('### [seq ')} ≠ {len(items)}）", file=sys.stderr)
        return 1
    print(f"📋 triage  {_rel(tri_path)}"
          f"（待處置 {by_sugg.get('pending', 0)} 則）")
    if unreadable:
        print(f"   ⚠ {len(unreadable)} 則讀檔失敗，已列進工作稿與 triage（不靜默跳過）")
    return 0


def cmd_verify(args):
    """區塊職責：對帳 —— 每一則是否都有處置。

    物理意義：歷史書對讀者的承諾是「一則都不許無聲消失」。那句話只有在
             『每個 seq 都能查到去向』時才成立，而這支就是那個查法。
    數值影響：有未處置或非法值 ⇒ exit 1。不印「✓ 通過」以外的安慰話。
    """
    room, date = args.room, args.date
    tri_path = Path(args.triage) if args.triage else _drafts_dir(room) / f"{date}_triage.json"
    if not tri_path.is_file():
        print(f"❌ 找不到 triage：{_rel(tri_path)} —— 先跑 export-day。", file=sys.stderr)
        return 1
    tri = json.loads(tri_path.read_text(encoding="utf-8"))
    items = tri.get("items") or []
    if not items:
        print(f"❌ {_rel(tri_path)} 沒有 items。", file=sys.stderr)
        return 1

    pending, bad, counts = [], [], {}
    for it in items:
        d = (it.get("disposition") or "").strip()
        if not d:
            pending.append(it["seq"])
        elif d not in DISPOSITIONS:
            bad.append((it["seq"], d))
        else:
            counts[d] = counts.get(d, 0) + 1

    print(f"# 對帳 {date}（room {room}）— 共 {len(items)} 則")
    for d in DISPOSITIONS:
        note = "（不收進書，但仍列在處置總表）" if d == "drop" else ""
        print(f"  {d:<9} {counts.get(d, 0)} 則{note}")
    print(f"  {'未處置':<8} {len(pending)} 則")
    # stdout / stderr 是兩條管線，不 flush 的話錯誤會插在表格前面 —— 讀起來像是先失敗才對帳
    sys.stdout.flush()
    if bad:
        print(f"\n❌ 非法 disposition（只准 {'/'.join(DISPOSITIONS)}）：", file=sys.stderr)
        for seq, d in bad:
            print(f"   seq {seq} = {d!r}", file=sys.stderr)
    if pending:
        head = ", ".join(str(s) for s in pending[:20])
        more = f"…另 {len(pending) - 20} 則" if len(pending) > 20 else ""
        print(f"\n❌ 尚未處置：seq {head}{more}", file=sys.stderr)
    if pending or bad:
        return 1
    print("\n✅ 每一則都有處置 —— 「一則都不許無聲消失」這句話現在有憑據了。")
    return 0


def cmd_days(args):
    """列出某房有哪些日子可匯出（以及是否已有工作稿）。"""
    base = _rooms_dir() / args.room / "messages"
    if not base.is_dir():
        print(f"❌ 找不到 {_rel(base)}", file=sys.stderr)
        return 1
    ddir = _drafts_dir(args.room)
    days = sorted([p for p in base.iterdir() if p.is_dir()], key=lambda p: p.name)
    total = len(days)
    # 截斷要出聲：只印最近 N 天卻報「共 N 天」，讀起來跟「全部就這些」一模一樣
    if args.limit:
        days = days[-args.limit:]
    shown = f"共 {total} 天" if len(days) == total else f"共 {total} 天，只列最近 {len(days)} 天"
    print(f"# room `{args.room}` 可匯出日（{shown}，草稿區 {_rel(ddir)}）\n")
    for p in days:
        n = len(list(p.glob("*.json")))
        done = "📄 已匯出" if (ddir / f"{p.name}_raw.md").is_file() else "—"
        print(f"  {p.name}  {n:>4} 則  {done}")
    return 0


def main():
    ap = argparse.ArgumentParser(
        description="酒館歷史書 Phase A —— 當日全文工作稿匯出（機械）。"
                    "Phase B（原文照收／摘要／附錄的取捨與導讀）由編者親筆，不在本工具內。")
    sub = ap.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("export-day", help="把某一天的酒館訊息匯出成全文工作稿 + triage 表")
    a.add_argument("--date", required=True, help="日期資料夾名（YYYY-MM-DD，UTC 日）")
    a.add_argument("--room", default="tavern")
    a.add_argument("--out-dir", dest="out_dir", default=None,
                   help="輸出目錄（預設 <data_root>/TavernHistory/drafts/<room>/）")
    a.add_argument("--force", action="store_true", help="覆寫既有工作稿與 triage（會蓋掉已填的處置）")
    a.add_argument("--allow-zero-stripped", dest="allow_zero_stripped", action="store_true",
                   help="明說這一天真的沒有自動附掛區塊（否則清除數 0 會被擋下）")
    a.set_defaults(func=cmd_export_day)

    a = sub.add_parser("verify", help="對帳：每一則是否都有處置（raw / summary / appendix）")
    a.add_argument("--date", required=True)
    a.add_argument("--room", default="tavern")
    a.add_argument("--triage", default=None, help="triage.json 路徑（預設由 room/date 推）")
    a.set_defaults(func=cmd_verify)

    a = sub.add_parser("days", help="列出某房有哪些日子可匯出")
    a.add_argument("--room", default="tavern")
    a.add_argument("--limit", type=int, default=14, help="只印最近 N 天（0＝全部）")
    a.set_defaults(func=cmd_days)

    args = ap.parse_args()
    return args.func(args) or 0


if __name__ == "__main__":
    sys.exit(main())
