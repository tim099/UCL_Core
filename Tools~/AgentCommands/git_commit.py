#!/usr/bin/env python3
"""
git_commit.py — commit 的最後一步：帶 persona 參數，自動組 Co-Authored-By 後提交。

# 區塊職責：只做「組 trailer + 提交」。**stage 什麼、切哪個分支、要不要 push，一律不碰** —— 那些維持原本手動流程。
# 物理意義：trailer 以前是手打的，於是它會漂：同一位 meadow 三筆 commit 出現過 (GPT)/(GPT-5)/(GPT-5.6)
#          與 anthropic/openai 兩種 domain。身分、型號、信箱三個欄位都推導自 persona 檔與信箱 registry，
#          手不碰就不會漂。
# 數值影響：信箱解析不到（哨兵 unset@invalid）預設**拒絕提交**；要硬幹得明示 --allow-unset。
#          寧可擋下也不要讓一個假位址進 git history —— history 改不掉。

用法（訊息走 stdin，跟現行 heredoc 習慣一致）:
  python git_commit.py --persona basecamp <<'EOF'
  標題行

  內文…
  EOF

  # 多位參與者 → 每人一行 trailer
  python git_commit.py --persona basecamp --persona meadow --repo Assets/Plugins/UCL_Core -m "標題"

  # 只看會組出什麼，不提交
  python git_commit.py --persona basecamp --dry-run -m "test"

提交成功後**自動發一則酒館公告領薪**（`--no-announce` 可關）。公告內容取自 commit 訊息本身 ——
領薪漏發是這條流程最兇的失血點（血證：新制上線後 source_kind=commit 曾 82 天零領取），
而「記得發公告」本來就不該是人的責任。

exit code: 0 成功 / 2 參數或 persona 有問題 / 3 信箱未設定 / 4 沒有 staged 變更 / 5 git commit 失敗
         / 6 commit 成功但公告發送失敗（**錢沒領到，需手動補**）
"""

from __future__ import annotations
import argparse
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

from agent_email import resolve_email, load_persona, looks_like_email, UNSET_SENTINEL, _data_root  # noqa: E402
from agent_model import resolve_model, format_trailer_model  # noqa: E402

EXIT_OK, EXIT_BAD_ARGS, EXIT_UNSET_EMAIL, EXIT_NOTHING_STAGED, EXIT_COMMIT_FAIL = 0, 2, 3, 4, 5
EXIT_ANNOUNCE_FAIL = 6
TRAILER_PREFIX = "Co-Authored-By:"



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


def git(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, encoding="utf-8")


def build_trailers(personas: list, allow_unset: bool) -> tuple:
    """persona 清單 → (trailer 行清單, 錯誤訊息清單, 注意事項清單)。

    注意事項 = 「能跑但值得知道」的狀況（例如信箱吃的是全域 fallback 而不是自己的）——
    它不該擋提交，但也不該完全不出聲：那正是會被默默帶進 history 的那種東西。
    """
    lines, problems, notes, seen = [], [], [], set()
    for persona in personas:
        if persona in seen:
            continue
        seen.add(persona)
        p = load_persona(persona)
        if not p:
            problems.append(f"persona 檔不存在或讀不到：{persona}")
            continue
        agent = (p.get("agent") or "").strip()
        # 型號走解析器：(1) 有人把 agent 名填進 model 欄（實測）→ 底層翻譯；
        # (2) 輸出 (<vendor> / <version>) —— vendor 由 actual_agent 推導不靠人填，
        #     version 只在知道時才有。推不出 vendor 就整段沿用原值，不印假精確的 `?`。
        model = format_trailer_model(persona)["text"]
        if not agent:
            problems.append(f"{persona} 的 agent 欄是空的（trailer 的身分會變成 ?）")
        info = resolve_email(persona)
        email = info["email"]
        if info["source"] == "fallback":
            notes.append(f"{persona} 的信箱吃全域 fallback（{email}），不是自己的位址")
        if email == UNSET_SENTINEL or not looks_like_email(email):
            msg = f"{persona} 的信箱未設定或格式可疑（{email}）—— 到 Editor 的 Persona & Agent 管理頁設定"
            if allow_unset:
                print(f"WARN: {msg}", file=sys.stderr)
            else:
                problems.append(msg)
                continue
        lines.append(f"{TRAILER_PREFIX} {agent or '?'}@{persona}({model or '?'}) <{email}>")
    return lines, problems, notes


def resolve_sender(persona: str) -> str:
    """公告的發文身分（bank account）。lock 有就用 lock 的，沒有就走 agent→bank 對照表。

    # 物理意義：sender 決定錢進誰的帳，猜錯等於把薪水發給別人。lock 是當下真相，
    #          registry 是靜態對照 —— 兩個都查不到就回空字串，讓 caller 明確失敗而不是亂發。
    """
    root = _data_root()
    try:
        lock = json.loads((root / "_session" / f"_persona_{persona}.json").read_text(encoding="utf-8"))
        if lock.get("bank_account"):
            return lock["bank_account"]
    except Exception:
        pass
    try:
        agent = (load_persona(persona).get("agent") or "").strip()
        reg = json.loads(_ucl_paths_mod().registry_meta_path().read_text(encoding="utf-8"))
        return (reg.get("agent_banks") or {}).get(agent, "") or ""
    except Exception:
        return ""


def build_announcement(message: str, sha: str, repo: str, personas: list, intro: str = "",
                       bump_of: str = "") -> str:
    """commit 訊息 → 公告內文。

    # 區塊職責：把 commit 訊息原樣端上酒館，只加一行標頭與一行參與者。
    # 物理意義：commit 訊息本來就是寫給人看的；再叫人另外寫一份公告，等於同一件事寫兩遍 ——
    #          寫兩遍的東西一定有一遍會被省略，而被省略的通常是後面那遍（所以錢才領不到）。
    # 數值影響：trailer 行從公告裡剝掉（讀的人不需要看信箱），改成一行「參與者」。
    #          intro（--announce-body）插在標題與 commit 內文之間 —— commit 訊息是寫給「日後查
    #          history 的人」，開場白是寫給「現在在酒館的同事」，兩種讀者要的東西不一樣。
    """
    lines = [ln for ln in message.splitlines() if not ln.strip().startswith(TRAILER_PREFIX)]
    if bump_of:
        # pointer bump 的公告刻意極簡：帳要留（SHA 在 meta 裡），但版面不該跟主 commit 搶。
        # 這是「控制訊息量在讀取端、不在寫入端」的實作 —— 事件照存，只是呈現壓到一行。
        subject_only = (lines[0].strip() if lines else "(無標題)")
        label_b = "主專案" if repo in (".", "") else Path(repo).name
        return f"📦 **{label_b} `{sha}`** — {subject_only}（pointer bump，內容見 `{bump_of}` 那則）"
    while lines and not lines[-1].strip():
        lines.pop()
    subject = lines[0].strip() if lines else "(無標題)"
    rest = "\n".join(lines[1:]).strip()
    label = "主專案" if repo in (".", "") else Path(repo).name
    out = [f"📦 **{label} `{sha}`** — {subject}", ""]
    if intro.strip():
        out += [intro.strip(), ""]
    if rest:
        out += [rest, ""]
    out.append(f"👥 參與者：{' / '.join('@' + p for p in personas)}")
    return "\n".join(out)


# 公告前等佇列空出來的上限（秒）。run_cmd 預設 60s —— 多人同時用時不夠。
# 逾時的語意是「沒送出」（ensure_idle 在寫 trigger 前就 SystemExit），所以拉長是純等待、不是重試。
ANNOUNCE_ACK_TIMEOUT_SEC = 240


def post_announcement(body: str, sha: str, sender: str, persona: str) -> tuple:
    """發酒館公告；回 (成功, 說明)。走 run_cmd.py Tavern，body 經檔案避免引號地獄。"""
    here = Path(__file__).resolve().parent
    run_cmd = here / "run_cmd.py"
    if not run_cmd.exists():
        return False, f"找不到 run_cmd.py：{run_cmd}"
    tmp = here / f"_announce_{sha}.md"
    try:
        tmp.write_text(body, encoding="utf-8")
        meta = json.dumps({"tag": "commit", "sha": sha, "category": "meta"}, ensure_ascii=False)
        # 區塊職責：等佇列空出來的時間要夠長 —— 這是多人共用同一條 lane 時最常見的失敗。
        # 物理意義：run_cmd 的 ensure_idle 預設只等 60s，逾時會 SystemExit **而且是在寫 trigger 之前**
        #          ⇒ 那種失敗代表「根本沒送出」，不是「可能送出了」。
        # 🩸 2026-08-16：BookNotes 那筆 commit 落地了但公告失敗（「previous batch is 'running'」），
        #    薪沒領、要人工補一則。當時同事正在跑一連串 Cmd，60 秒等不到。
        # ⚠ 這裡**只拉長等待、不做失敗重試** —— 重試的風險是不對稱的：
        #    ensure_idle 逾時＝沒送出（重試安全），但**送出之後**的任何失敗都可能其實已經貼上了
        #    （今天實測過「CLI 逾時而產物已落地」），而同一個 SHA 貼兩次 = **付兩次錢**。
        #    ⇒ 分不清的時候不要自動重試；讓它誠實失敗，人工補一則（工具已經會這樣提示）。
        # 區塊職責：公告走 system lane（Tim 2026-08-18）
        # 物理意義：領薪公告不是人派的，過去落 queues/anonymous/ ——
        #          跟「漏帶 --persona 的人」混在同一個資料夾，於是 anonymous 的流量
        #          永遠降不到 0，「還有多少人漏帶旗標」這個讀數就失效了。
        #          `--system` 只改**走哪條 lane**；這筆代表誰仍由下面的 `--arg persona=` 承載
        #          （領薪要用那個，不是用 lane）。
        # 數值影響：路由 queues/anonymous/ → queues/system/；也順帶不再跟 agent 自己的
        #          指令搶同一條 lane（上方 ensure_idle 逾時那隻的同族成因）。
        cmd = [sys.executable, str(run_cmd), "--system", "run", "Tavern",
               "--arg", "op=post", "--arg", "room=tavern",
               "--arg", f"sender_id={sender}", "--arg", f"persona={persona}",
               "--arg", f"meta={meta}", "--wait-reply", "0",
               "--ack-timeout", str(ANNOUNCE_ACK_TIMEOUT_SEC),
               "--arg-file", f"body={tmp}"]
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
        ok = r.returncode == 0 and "Success" in (r.stdout or "")
        return ok, (r.stdout or "").strip().splitlines()[-1] if r.stdout else (r.stderr or "").strip()
    except Exception as e:
        return False, str(e)
    finally:
        # 公告是 ephemeral 產物，不該留在 repo 裡等人誤 commit
        try:
            tmp.unlink()
        except Exception:
            pass


def compose_message(body: str, trailers: list) -> str:
    """把 trailer 併到訊息尾端；已經有同一行就不重複加（重跑同一個指令不該長出兩份）。"""
    text = body.rstrip("\n")
    existing = {ln.strip() for ln in text.splitlines() if ln.strip().startswith(TRAILER_PREFIX)}
    fresh = [t for t in trailers if t not in existing]
    if not fresh:
        return text + "\n"
    # trailer 區塊與內文之間留一個空行；若內文尾端已經是 trailer 區塊就直接接上。
    sep = "\n" if existing and text.splitlines()[-1].strip().startswith(TRAILER_PREFIX) else "\n\n"
    return text + sep + "\n".join(fresh) + "\n"


def read_body(args) -> str:
    if args.message_file:
        return Path(args.message_file).read_text(encoding="utf-8")
    if args.message:
        return args.message
    if sys.stdin.isatty():
        return ""
    return sys.stdin.read()


def main() -> int:
    ap = argparse.ArgumentParser(description="組 Co-Authored-By 並提交（只做最後一步，不 stage 不 push）")
    ap.add_argument("--persona", action="append", default=[],
                    help="參與者 persona，可重複給；每位一行 trailer")
    ap.add_argument("--repo", default=".", help="git 工作目錄（submodule 就指到該 submodule）")
    ap.add_argument("-m", "--message", help="commit 訊息（不給就讀 stdin）")
    ap.add_argument("--message-file", help="從檔案讀 commit 訊息")
    ap.add_argument("--allow-unset", action="store_true",
                    help="信箱未設定仍提交（預設拒絕 —— 假位址進了 history 就改不掉）")
    ap.add_argument("--dry-run", action="store_true", help="只印組出來的訊息，不提交")
    ap.add_argument("--no-announce", action="store_true",
                    help="不自動發酒館公告（預設會發 —— 領薪不該靠人記得）。"
                         "**必須同時給 --no-announce-reason**")
    ap.add_argument("--no-announce-reason", default="",
                    help="為什麼這筆不公告。沒有理由就不該關 —— 見 --no-announce 的說明")
    ap.add_argument("--announce-body", default="",
                    help="公告的開場白（插在標題與 commit 內文之間，寫給現在在酒館的同事看）")
    ap.add_argument("--announce-body-file", default="",
                    help="同上，改從檔案讀（長文或含特殊字元時用）")
    ap.add_argument("--bump-of", default="",
                    help="本筆是某主 commit 的 pointer bump：公告壓成一行並指向該 SHA（帳照領）")
    ap.add_argument("--verbose", action="store_true",
                    help="成功時印完整細節（預設只印一行 —— 成功路徑瘦到看不見，異常才佔版面）")
    args = ap.parse_args()

    # 區塊職責：--no-announce 必須帶理由（Tim 2026-08-05 拍板）
    # 物理意義：**在 commit 發生之前擋下**，不是事後提醒 —— 提醒會被忽略，缺參數不會。
    #          擋在 parse 之後、git commit 之前：這樣不會留下一筆「已提交但沒領薪」的殘局。
    # 血證（summit 2026-08-05，同一天四次）：我三次順手打了 --no-announce 造成薪水沒領，
    #          每次都自首、還把「別自己發明例外」寫進公告，然後第四次照樣打上去。
    #          **三次同一個動作就不是失誤，是預設行為。**
    #          而「寫下來只讓下一個人知道，不讓自己記得」—— 有效的修法是讓錯的做法在物理上不可行：
    #          你得先想出一個理由，而想不出來的時候你就會發現自己沒有理由。
    #          （同形狀的前例：反引號咬人三次後，有效修法不是記得別用 -m，是改用 --message-file。）
    if args.no_announce and not args.no_announce_reason.strip():
        print("✗ --no-announce 必須同時給 --no-announce-reason「為什麼這筆不公告」。\n"
              "  預設會公告是刻意的：commit 就領薪，別自己發明例外\n"
              "  （source_kind=commit 曾 82 天零領取，成因是「做完了倒在門外」）。\n"
              "  想不出理由 = 你沒有理由 → 把 --no-announce 拿掉即可。",
              file=sys.stderr)
        return EXIT_BAD_ARGS

    if not args.persona:
        print("ERROR: 至少要一個 --persona", file=sys.stderr)
        return EXIT_BAD_ARGS

    body = read_body(args)
    if not body.strip():
        print("ERROR: commit 訊息是空的（用 -m / --message-file / stdin）", file=sys.stderr)
        return EXIT_BAD_ARGS

    trailers, problems, notes = build_trailers(args.persona, args.allow_unset)
    if problems:
        for msg in problems:
            print(f"ERROR: {msg}", file=sys.stderr)
        return EXIT_UNSET_EMAIL
    if not trailers:
        print("ERROR: 沒有可用的 trailer", file=sys.stderr)
        return EXIT_BAD_ARGS

    message = compose_message(body, trailers)

    if args.dry_run:
        print("─── 將提交的訊息 ───")
        print(message, end="")
        print("─── （--dry-run，未提交）───")
        return EXIT_OK

    # 空提交是沉默的失敗來源：git 會回非零但訊息很像其他錯誤，先自己驗一次講清楚。
    staged = git(args.repo, "diff", "--cached", "--name-only")
    if staged.returncode != 0:
        print(f"ERROR: 讀 staged 清單失敗：{staged.stderr.strip()}", file=sys.stderr)
        return EXIT_COMMIT_FAIL
    if not staged.stdout.strip():
        print(f"ERROR: {args.repo} 沒有 staged 變更 —— 本工具只做提交，stage 請自己來", file=sys.stderr)
        return EXIT_NOTHING_STAGED

    result = subprocess.run(["git", "-C", args.repo, "commit", "-F", "-"],
                            input=message, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        print(result.stdout, end="")
        print(f"ERROR: git commit 失敗：{result.stderr.strip()}", file=sys.stderr)
        return EXIT_COMMIT_FAIL

    sha = git(args.repo, "rev-parse", "--short", "HEAD").stdout.strip()
    # 成功路徑刻意安靜（Alert Fatigue，apex-one 2026-08-03 命名）：
    # 每次成功都印同一塊五行，看第八次它就是背景 —— 我今天就是這樣沒看見自己的 --no-announce。
    # 所以正常成功只留一行，細節走 --verbose；異常路徑維持大聲。
    if args.verbose:
        print(result.stdout.strip())
        for t in trailers:
            print(f"  {t}")
    for n in notes:
        print(f"⚠ {n}", file=sys.stderr)

    if args.no_announce:
        # 理由印出來 —— 給了理由卻沒人看得見，那個參數就只是形式（名字比事實大的一種）
        print(f"💰 未自動公告（--no-announce，理由：{args.no_announce_reason.strip()}）。"
              f"這筆 SHA `{sha}` 要發一則酒館公告才領得到（一則訊息一個 SHA）：")
        print(f"   meta: {{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}}   --wait-reply 0")
        return EXIT_OK

    primary = args.persona[0]
    sender = resolve_sender(primary)
    if not sender:
        print(f"⚠ 查不到 {primary} 的 bank（lock 與 registry 都沒有）—— 公告未發，錢沒領到。", file=sys.stderr)
        print(f"   手動補：meta {{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}}", file=sys.stderr)
        return EXIT_ANNOUNCE_FAIL

    intro = args.announce_body
    if args.bump_of and (args.announce_body or args.announce_body_file):
        print("⚠ --bump-of 與 --announce-body 同時給了；bump 公告刻意極簡，開場白已忽略。", file=sys.stderr)
    if args.announce_body_file:
        try:
            intro = Path(args.announce_body_file).read_text(encoding="utf-8")
        except Exception as e:
            print(f"⚠ 讀 --announce-body-file 失敗（改用空開場）：{e}", file=sys.stderr)
    ok, detail = post_announcement(
        build_announcement(message, sha, args.repo, args.persona, intro, args.bump_of),
        sha, sender, primary)
    if ok:
        if args.verbose:
            print(f"📣 酒館公告已發（sha={sha} / sender={sender}）—— 不要再手動貼一次，同 SHA 貼兩次會付兩次錢。")
        else:
            print(f"✓ {sha} 已提交並公告（{primary}{'／bump of ' + args.bump_of if args.bump_of else ''}）")
        return EXIT_OK
    # commit 已經落地了，這裡失敗只有錢沒領到 —— 講清楚是哪一半失敗，別讓人以為 commit 也沒成功。
    print(f"⚠ commit 成功但公告發送失敗：{detail}", file=sys.stderr)
    print(f"   commit 已落地（{sha}），只有領薪那步沒完成。手動補一則帶 "
          f"meta {{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}} 的酒館貼文即可。", file=sys.stderr)
    return EXIT_ANNOUNCE_FAIL


if __name__ == "__main__":
    sys.exit(main())
