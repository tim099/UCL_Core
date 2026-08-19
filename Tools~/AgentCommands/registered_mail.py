#!/usr/bin/env python3
"""付費掛號信件（Registered Mail）— persona 之間寄信，可指定未來 wake 投遞。

區塊職責：agent 之間「指名、付費、有投遞時點」的信件通道。

物理意義（跟既有三種東西的分界，別再開第四套）：
  - **letter to future self**（`letters/<p>/wakes/`）= 自己寫給自己的日記，晚安儀式產物
  - **酒館 @mention**（`ChatTavern/rooms/<r>/inbox/`）= 公開對話裡被點名，即時、免費、大家看得到
  - **persona-ding**（自叮）= 同 actor 內不同 persona 的輕量 ping
  - **掛號信（本檔）** = 指名寄給任何 persona（含自己）、**付費**、可指定**未來的 wake** 投遞

  掛號信獨有的是「時間定址」：`--deliver-at-wake 100` 寄給 wake #100 的自己或別人。
  酒館訊息只能寄到「現在」，letter 只能寄給「下一次的自己」。

數值影響：
  - 每封收費（預設 5 token，後台 UCL_BankAdminPage 可調）。
  - **費用蒸發，不進央行**（Tim 2026-08-01 明確指定：「蒸發代表 token 消失，不進入央行」）。
    這是保管費改制後這個經濟體的第一個真 sink —— 央行是 circulation，掛號信費是 burn。
  - 扣費**走 Cmd_Treasury op=debit**，不自己寫 ledger（Tim：「款項操作、寫檔都經過那個 C# class」）。
    Python 端沒有 ledger 寫入 API，自己拼 JSON 必然跟 C# schema 漂移。

檔案佈局（Tim 2026-08-01 指定：兩份、標寄件者與收件者、參考電子郵件）：
    letters/<收件者>/mailbox/<ts>__from_<寄件者>.md   ← 收件匣（投遞用）
    letters/<寄件者>/outbox/<ts>__to_<收件者>.md      ← 寄件備份（存證用）
  刻意不叫 `inbox/` —— 酒館已經有 `rooms/<room>/inbox/<persona>.md` 了。
  同名不同物正是這個系統一路在治的 identity 層問題（`_lib` 兩份、`use_kind` vs `source_kind`…）。
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

_HERE = Path(__file__).resolve().parent
DEFAULT_MAIL_FEE = 5          # 與 C# UCL_CentralBankSettings.DefaultRegisteredMailFee 對齊



# ⚠ 路徑一律委派 _lib/ucl_paths.py（Tim 2026-08-17 拍板）——
#   persona 檔／AwakenInit 子路徑的唯一解析點在那裡，本檔不自己拼字串。
_UCL_PATHS_CACHE = None


def _ucl_paths_mod():
    global _UCL_PATHS_CACHE
    if _UCL_PATHS_CACHE is None:
        import importlib.util as _ilu
        from pathlib import Path as _P
        _spec = _ilu.spec_from_file_location(
            "_ucl_paths_shared", _P(__file__).resolve().parent / "_lib" / "ucl_paths.py")
        _m = _ilu.module_from_spec(_spec)
        _spec.loader.exec_module(_m)
        _UCL_PATHS_CACHE = _m
    return _UCL_PATHS_CACHE


def _find_repo_root(start: Path):
    """取最外層那個 `.git` 是**資料夾**的目錄（submodule 的 .git 是檔案）。

    不用「第一個命中就回」：那會讓結果取決於 cwd —— 在 UCL_Core 內跑回 UCL_Core、
    在專案根跑回專案根。同一支工具依呼叫位置給出不同答案，就是會漂的游標。
    """
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
# letters 走唯一入口（BUG-2）—— 從 REPO_ROOT 自己拼會繞過 data root override
from _lib.ucl_paths import letters_root as _letters_root
LETTERS_DIR = _letters_root()
BANK_SETTINGS = REPO_ROOT / "AgentCommands" / "Treasury" / "bank_settings.json"
RUN_CMD = _HERE / "run_cmd.py"

MAILBOX_DIRNAME = "mailbox"   # 收件者端
OUTBOX_DIRNAME = "outbox"     # 寄件者端


def mail_fee() -> int:
    """每封費用 —— 真相源是 C# 後台寫的 bank_settings.json；缺檔/壞檔回預設。

    ⚠ 讀不到時回**預設值而非 0**：回 0 會讓寄信變免費且沒有人發現，
      而「費率設定檔壞掉」不該靜默變成「這個功能免費」。
    """
    try:
        if BANK_SETTINGS.exists():
            v = json.loads(BANK_SETTINGS.read_text(encoding="utf-8")).get("registered_mail_fee")
            if isinstance(v, int) and v >= 0:
                return v
    except Exception:
        pass
    return DEFAULT_MAIL_FEE


def resolve_bank(persona: str) -> str | None:
    """persona → bank（走既有 _lib/bank_resolver，不自維護第二張對照表）。"""
    try:
        sys.path.insert(0, str(_HERE))
        from _lib import bank_resolver                     # noqa: E402
        reg_path = _ucl_paths_mod().registry_meta_path()
        reg = json.loads(reg_path.read_text(encoding="utf-8")) if reg_path.exists() else {}
        # personas 資料走 persona_profile 接縫（Phase 0）—— 不自己 glob＋parse
        import importlib.util as _ilu2
        _sp = _ilu2.spec_from_file_location(
            "_ucl_persona_profile_regmail", _HERE / "_lib" / "persona_profile.py")
        _pp = _ilu2.module_from_spec(_sp); _sp.loader.exec_module(_pp)
        _pp.load_personas_into(reg)
        return bank_resolver.resolve_persona_bank(reg, persona)
    except Exception as e:
        print(f"⚠ persona → bank 解析失敗（{type(e).__name__}: {e}）", file=sys.stderr)
        return None


def charge(bank: str, amount: int, persona: str, to: str, ref: str) -> bool:
    """扣費 —— 走 Cmd_Treasury op=debit（唯一合法的動錢路徑）。純 debit = 蒸發。"""
    if amount <= 0:
        return True                                        # 費率設 0 = 免費寄信，合法
    cmd = [sys.executable, str(RUN_CMD), "--persona", persona, "run", "Treasury",
           "--arg", "op=debit", "--arg", f"account={bank}", "--arg", f"amount={amount}",
           "--arg", "use_kind=registered_mail_fee", "--arg", f"use_ref={ref}",
           "--arg", f"description=掛號信郵資（寄給 @{to}）—— 本費用蒸發，不進央行"]
    try:
        r = subprocess.run(cmd, capture_output=True, encoding="utf-8", errors="replace", timeout=180)
        ok = r.returncode == 0 and "Success" in (r.stdout or "")
        if not ok:
            print(f"⚠ 扣費失敗（郵件未寄出）：\n{(r.stdout or '')[-600:]}\n{(r.stderr or '')[-400:]}",
                  file=sys.stderr)
        return ok
    except Exception as e:
        print(f"⚠ 扣費指令執行失敗（郵件未寄出）：{e}", file=sys.stderr)
        return False


def cmd_send(args):
    sender, to = args.sender.strip(), args.to.strip()
    if not sender or not to:
        print("✗ --from / --to 皆必填", file=sys.stderr)
        return 2
    body = args.body
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not (body or "").strip():
        print("✗ 信件內容為空（--body 或 --body-file 擇一）", file=sys.stderr)
        return 2

    fee = mail_fee()
    ts = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    ref = f"mail-{ts}-{sender}-to-{to}"

    # 先扣費再寫檔：錢是難以靜默還原的那一端，檔案不是。
    # 若扣了但寫檔失敗，下面會印出 ref 讓人拿去請款退回 —— 不假裝沒發生。
    bank = resolve_bank(sender) if fee > 0 else None
    if fee > 0:
        if not bank:
            print(f"✗ 查不到 {sender} 的 bank，無法扣費 → 未寄出", file=sys.stderr)
            return 1
        if not charge(bank, fee, sender, to, ref):
            return 1

    fm = [
        "---", "type: registered_mail",
        f"from: {sender}", f"to: {to}",
        f"sent_at: {datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')}",
        f"fee: {fee}", f"fee_ref: {ref}",
    ]
    if args.subject:
        fm.append(f"subject: {args.subject}")
    if args.deliver_at_wake is not None:
        fm.append(f"deliver_at_wake: {args.deliver_at_wake}")
    fm += ["---", ""]

    header = (f"# 📮 掛號信 — 寄件者 @{sender} → 收件者 @{to}\n\n"
              + (f"**主旨**：{args.subject}\n\n" if args.subject else "")
              + (f"**投遞時點**：wake #{args.deliver_at_wake}\n\n"
                 if args.deliver_at_wake is not None else "**投遞時點**：下次醒來\n\n")
              + "---\n\n")
    content = "\n".join(fm) + header + body.strip() + "\n"

    try:
        mailbox = LETTERS_DIR / to / MAILBOX_DIRNAME
        outbox = LETTERS_DIR / sender / OUTBOX_DIRNAME
        mailbox.mkdir(parents=True, exist_ok=True)
        outbox.mkdir(parents=True, exist_ok=True)
        (mailbox / f"{ts}__from_{sender}.md").write_text(content, encoding="utf-8")
        (outbox / f"{ts}__to_{to}.md").write_text(content, encoding="utf-8")
    except Exception as e:
        print(f"✗ 已扣費 {fee} token 但寫檔失敗：{e}\n"
              f"  請以此 ref 開請款單退回：{ref}", file=sys.stderr)
        return 1

    when = f"wake #{args.deliver_at_wake}" if args.deliver_at_wake is not None else "下次醒來"
    print(f"📮 掛號信已寄出：@{sender} → @{to}（投遞：{when}）")
    print(f"   郵資 {fee} token（蒸發，不進央行）{'／bank ' + bank if bank else '（免費）'}")
    print(f"   收件匣：{(LETTERS_DIR / to / MAILBOX_DIRNAME / f'{ts}__from_{sender}.md')}")
    print(f"   寄件備份：{(LETTERS_DIR / sender / OUTBOX_DIRNAME / f'{ts}__to_{to}.md')}")
    return 0


def _read_fm(path: Path) -> dict:
    meta = {}
    try:
        t = path.read_text(encoding="utf-8").lstrip()
        if t.startswith("---"):
            end = t.find("\n---", 3)
            if end != -1:
                for line in t[3:end].splitlines():
                    if ":" in line:
                        k, _, v = line.partition(":")
                        meta[k.strip()] = v.strip()
    except Exception:
        pass
    return meta


def due_mail(persona: str, wake_count: int | None):
    """回 (到期未 ack, 未到期) 兩串。到期 = 沒指定 wake，或指定的 wake <= 目前 wake。

    ⚠ 指定 wake #100 而現在是 #105 → **仍算到期**（不是「錯過了」）。
      信件不該因為你晚醒幾次就永遠讀不到 —— 那是安靜地吃掉別人付過錢的東西。
      同理「投遞到 wake 1 但現在已 wake 3」→ wake 4 照樣會出現，直到你 ack。

    ⚠ 已 ack（frontmatter 有 read_at）的不再列 —— 那是**唯一**的除名條件。
      不用「列過一次就消失」：那樣只要有一次 render 沒被人看到（工具壞掉 / 溢出到
      續讀檔 / 那天沒讀 brief），這封付過錢的信就永遠消失且無人知曉。
    """
    box = LETTERS_DIR / persona / MAILBOX_DIRNAME
    due, later = [], []
    if not box.is_dir():
        return due, later
    for f in sorted(box.glob("*.md")):
        meta = _read_fm(f)
        if meta.get("read_at"):
            continue                                       # 已確認閱讀 → 除名
        raw = meta.get("deliver_at_wake")
        try:
            target = int(raw) if raw not in (None, "") else None
        except ValueError:
            target = None                                  # 壞值當「下次醒來」，不吞信
        if target is None or wake_count is None or wake_count >= target:
            due.append((f, meta))
        else:
            later.append((f, meta))
    return due, later


def _set_fm_field(path: Path, field: str, value: str) -> bool:
    """在 frontmatter 就地寫入/更新一個欄位。回傳是否有變動。

    只碰 frontmatter，不動內文 —— 信的內容是寄件者寫的，任何情況下都不該被工具改。
    """
    try:
        text = path.read_text(encoding="utf-8")
        t = text.lstrip()
        if not t.startswith("---"):
            return False
        end = t.find("\n---", 3)
        if end == -1:
            return False
        head, rest = t[3:end], t[end:]
        lines = [ln for ln in head.splitlines() if ln.strip()]
        for i, ln in enumerate(lines):
            if ln.split(":", 1)[0].strip() == field:
                if ln.split(":", 1)[1].strip() == value:
                    return False                           # 已是該值 → 不重寫（保持冪等）
                lines[i] = f"{field}: {value}"
                break
        else:
            lines.append(f"{field}: {value}")
        path.write_text("---\n" + "\n".join(lines) + rest, encoding="utf-8")
        return True
    except Exception:
        return False


def stamp_delivered(path: Path, wake_count: int | None) -> bool:
    """投遞回執 —— 首次被 brief 列出時蓋一次 `first_seen_wake`（之後不覆寫）。

    物理意義：這是「這封信確實被端到收件者面前過」的證據（參考電子郵件的送達回執）。
             沒有它的話，「有沒有真的看過」只能靠人回想 —— 而付過錢的東西不該靠回想。
    ⚠ 蓋章**不等於**除名：信仍會每次醒來出現，直到 ack。
      蓋章只回答「第一次端上桌是哪一次醒來」，ack 才回答「我看過了」。
      兩者分開，是因為「被列出」跟「被讀進腦子」從來不是同一件事。
    """
    if wake_count is None:
        return False
    meta = _read_fm(path)
    if meta.get("first_seen_wake"):
        return False
    return _set_fm_field(path, "first_seen_wake", str(wake_count))


def cmd_inbox(args):
    due, later = due_mail(args.persona, args.wake)
    print(f"# 📮 {args.persona} 的掛號信收件匣"
          + (f"（目前 wake #{args.wake}）" if args.wake is not None else ""))
    print()
    if not due and not later:
        print("(沒有掛號信)")
        return 0
    if due:
        print(f"## 可讀取（{len(due)} 封）\n")
        for f, m in due:
            tgt = m.get("deliver_at_wake")
            print(f"- **@{m.get('from', '?')}** → {m.get('subject', '(無主旨)')}"
                  + (f"　[指定 wake #{tgt}]" if tgt else "")
                  + f"\n  `{f}`")
        print()
    if later:
        print(f"## 未到投遞時點（{len(later)} 封，先不拆）\n")
        for f, m in later:
            print(f"- @{m.get('from', '?')} → wake #{m.get('deliver_at_wake')}"
                  f"　{m.get('subject', '(無主旨)')}")
    return 0


def cmd_ack(args):
    """確認閱讀。同時回寫寄件者的 outbox 副本 —— 寄件者有權知道信被讀了（電子郵件的已讀回執）。"""
    box = LETTERS_DIR / args.persona / MAILBOX_DIRNAME
    if not box.is_dir():
        print(f"(沒有 mailbox：{box})")
        return 0
    targets = ([box / args.file] if args.file
               else [f for f, _m in due_mail(args.persona, None)[0]])
    if not targets:
        print("(沒有待確認的掛號信)")
        return 0
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    n = 0
    for f in targets:
        if not f.exists():
            print(f"✗ 找不到：{f.name}", file=sys.stderr)
            continue
        meta = _read_fm(f)
        if meta.get("read_at"):
            print(f"· 已確認過，跳過：{f.name}")
            continue
        _set_fm_field(f, "read_at", now)
        # 回寫寄件者的 outbox 副本（同 ts、同對象）—— 找不到就算了，不擋 ack
        sender = meta.get("from", "")
        if sender:
            ts = f.name.split("__", 1)[0]
            mirror = LETTERS_DIR / sender / OUTBOX_DIRNAME / f"{ts}__to_{args.persona}.md"
            if mirror.exists():
                _set_fm_field(mirror, "read_at", now)
        n += 1
        print(f"✅ 已確認閱讀：{f.name}"
              + (f"（首次投遞於 wake #{meta.get('first_seen_wake')}）" if meta.get("first_seen_wake") else ""))
    print(f"\n共 {n} 封除名 —— 之後的 wake brief 不再列出。")
    return 0


def main():
    ap = argparse.ArgumentParser(description="付費掛號信件 — persona 之間寄信，可指定未來 wake 投遞")
    sub = ap.add_subparsers(dest="op", required=True)

    s = sub.add_parser("send", help="寄一封掛號信（會扣郵資）")
    s.add_argument("--from", dest="sender", required=True)
    s.add_argument("--to", required=True, help="收件 persona（可以是自己）")
    s.add_argument("--subject", default=None)
    s.add_argument("--body", default=None)
    s.add_argument("--body-file", default=None, help="從檔案讀內文（長信用，避開 CLI 引號地獄）")
    s.add_argument("--deliver-at-wake", type=int, default=None,
                   help="指定收件者的 wake 編號才投遞；不帶 = 下次醒來就讀到")
    s.set_defaults(func=cmd_send)

    i = sub.add_parser("inbox", help="看某 persona 的掛號信")
    i.add_argument("--persona", required=True)
    i.add_argument("--wake", type=int, default=None, help="目前 wake 編號（判斷哪些到期）")
    i.set_defaults(func=cmd_inbox)

    a = sub.add_parser("ack", help="確認閱讀 —— 除名的唯一方式（信會一直出現直到 ack）")
    a.add_argument("--persona", required=True, help="收件者（你自己）")
    a.add_argument("--file", default=None, help="mailbox 內的檔名；不帶 = ack 全部到期的")
    a.set_defaults(func=cmd_ack)

    f = sub.add_parser("fee", help="印出目前郵資（後台可調）")
    f.set_defaults(func=lambda a: (print(f"目前郵資：{mail_fee()} token / 封（蒸發，不進央行）"), 0)[1])

    args = ap.parse_args()
    raise SystemExit(args.func(args))


if __name__ == "__main__":
    main()
