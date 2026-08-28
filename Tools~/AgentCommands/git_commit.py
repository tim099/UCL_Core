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

提交成功後**一律發一則酒館公告領薪**。公告內容取自 commit 訊息本身 ——
領薪漏發是這條流程最兇的失血點（血證：新制上線後 source_kind=commit 曾 82 天零領取），
而「記得發公告」本來就不該是人的責任。

⚠ 本工具只收**有作者的產出**（code / 文件 / 她寫的信）。機器生成、沒有作者的檔
（帳本 / 訊息 / cursor / 狀態快照）走 `Cmd AutoCommit` —— 那條路是純 git commit，
不掛 trailer、不公告、不領薪（掛誰的名字領誰的薪都是假帳）。

exit code: 0 成功 / 2 參數或 persona 有問題（含 --expect-files 不符）/ 3 信箱未設定
         / 4 沒有 staged 變更 / 5 git commit 失敗
         / 6 commit 成功但公告發送失敗（**錢沒領到，需手動補**）
"""

from __future__ import annotations
import argparse
import json
import re
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


# ⛔ `resolve_sender()` 已移除（2026-08-20，Tim 拍板「框架統一認 persona，
#    其餘身分資訊透過 persona 走統一解析入口」）。
#
# 它原本回 **bank account** 當公告的 `sender_id`，理由寫著「sender 決定錢進誰的帳」——
# **那個前提在 `c103e1f`（2026-08-14）之後就失效了**：計酬改由 persona 反解，
# `sender_id` 從此純粹是**顯示身分**。
#
# 🩸 而它沒跟著改的代價是 BUG-22 的另一半：`Cmd_Tavern` 的顯示身分推導修好了
#    （`ResolveDisplaySenderId`：persona → 綁定的 agent），但本檔**顯式帶 sender_id**
#    ⇒ commit 公告整條路繞過那個修法，而 commit 公告是最大的呼叫端。
#    症狀是 `cc` 那家 bank 的所有 persona 在酒館都顯示成 `crest-001`
#    （identities.json 裡 `cc` 的 display_name 是個 persona 名）。
#    ⚠ 我自己看不出來，因為我的 bank 名剛好等於 agent 名（都是 `Myth`）——
#      **修法在自己身上永遠看起來完整**，這是同一個形狀第三次。
#
# ⇒ 現在**不傳 sender_id**：Cmd_Tavern 從 `persona` 推導顯示身分（單一推導點）。
#   錢的部分本來就走 persona（`TryAutoCreditPostReward`），本檔從頭到尾沒碰過。


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


# 區塊職責：commit 訊息裡的 `Fixes BUG-<n>` → 自動把那幾張單關掉。
# 物理意義：**這是 BugReport 系統兩條防死機制的其中一條**（另一條是早安 brief 的 stale 讀數）。
#          `status: open` 的失效方式是沉默的 —— 一張沒人回來按 resolve 的單，
#          跟一張還真的壞著的單長得一模一樣，而且它會主動誤導。
#          修好東西的人本來就要 commit，所以把關單掛在**他一定會走的那條路**上，
#          不要另外要求他記得再跑一支指令（「記得」正是這套系統不能依賴的東西）。
# 數值影響：一張單一次 Cmd 呼叫；失敗只警告不影響 commit 與領薪（關單失敗不該讓 commit 看起來失敗）。
# ⚠ 刻意放在**公告成功之後**才跑：commit 與領薪是主線，關單是附帶效果。
def resolve_fixed_bugs(message: str, sha: str, persona: str) -> None:
    idxs = []
    # ⚠ **頂格錨定**（`^Fixes`，不允許行首空白）而不是 `\bFixes`。
    # 🩸 2026-08-24 summit：我在 commit 訊息裡**引述**上一筆的 `Fixes TASK-n`（描述那一筆發生過什麼），
    #   而 regex 分不出「這一筆要關」與「我在講那一筆」⇒ 兩張單被重複掛上這一筆 sha。
    #   trailer 的定義本來就是「獨占一行」（文件寫的是「在 commit 訊息裡寫一行就好」）,
    #   所以錨定行首不是收緊規則，是**把規則寫成它本來的形狀**。
    # 🩸 2026-08-28 BUG-8 補刀：`^[ \t]*` 仍放行**縮排引用** —— 而 `git log` 的輸出
    #   正好是四空白縮排，貼一段 git log 進 bump 訊息就會誤觸。trailer 頂格寫，
    #   縮排的那行在定義上是引用，不是宣告。
    for m in re.finditer(r"^Fixes[ \t]+BUG-(\d+)\b", message,
                         re.IGNORECASE | re.MULTILINE):
        n = m.group(1)
        if n not in idxs:
            idxs.append(n)
    if not idxs:
        return
    run_cmd = Path(__file__).with_name("run_cmd.py")
    for n in idxs:
        cmd = [sys.executable, str(run_cmd), "--persona", persona, "run", "BugReport",
               "--arg", "op=resolve", "--arg", f"index={n}",
               "--arg", "resolution=fixed", "--arg", f"commit_sha={sha}",
               "--arg", f"note=由 git_commit.py 自動關單（commit 訊息含 Fixes BUG-{n}）",
               "--wait-reply", "0"]
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=180)
            if r.returncode == 0 and "Success" in (r.stdout or ""):
                print(f"🐛 BUG-{n} 已自動關單（{sha}）")
            else:
                # 大聲但不致命 —— commit 已經落地，這裡失敗只是那張單還開著
                print(f"⚠ BUG-{n} 自動關單失敗，單子還開著：手動補 "
                      f"run BugReport --arg op=resolve --arg index={n} --arg commit_sha={sha}",
                      file=sys.stderr)
        except Exception as e:
            print(f"⚠ BUG-{n} 自動關單失敗（{e}）—— 單子還開著，需手動 resolve。", file=sys.stderr)


# 區塊職責：commit 訊息裡的 `Fixes TASK-<n>` / `Refs TASK-<n>` → 推進那幾張任務單。
# 物理意義：跟 `Fixes BUG-<n>` 同一條理由 —— 把狀態推進掛在**修東西的人一定會走的路**上。
#          舊的 AgentTasks（2026-05）死因就是「狀態要有人專程回來推」，而沒有人會專程回來。
# ⚠ **狀態機不在這裡**：這支只抓單號與 mode，剩下的判斷（有 blocker 不推進 / 有 QA 推 in_review /
#   沒 QA 才 done）全在 `Cmd_Task op=commit`。複製一份到 python 就是兩份產線 ——
#   兩邊都不報錯，而它們遲早各說各話（🩸 2026-08-21 一天撞五次同族）。
# 數值影響：一張單一次 Cmd 呼叫；失敗只警告不影響 commit 與領薪。
# ⚠ 同樣刻意放在**公告成功之後**：commit 與領薪是主線，推進任務是附帶效果。
def advance_tasks(message: str, sha: str, persona: str) -> None:
    # (index, mode) 保序去重 —— 同一張單同時寫 Fixes 與 Refs 時，**Fixes 優先**（它是較強的宣告）
    seen: dict = {}
    for kw, mode in (("Fixes", "fixes"), ("Refs", "refs")):
        # 頂格錨定 —— 理由同 resolve_fixed_bugs（引述別人的 trailer 不該觸發推進；
        # 縮排＝引用，git log 貼上是四空白，BUG-8）
        for m in re.finditer(rf"^{kw}[ \t]+TASK-(\d+)\b", message,
                             re.IGNORECASE | re.MULTILINE):
            n = str(int(m.group(1)))          # TASK-0001 與 TASK-1 是同一張單
            if n not in seen:
                seen[n] = mode
    if not seen:
        return
    run_cmd = Path(__file__).with_name("run_cmd.py")
    for n, mode in seen.items():
        cmd = [sys.executable, str(run_cmd), "--persona", persona, "run", "Task",
               "--arg", "op=commit", "--arg", f"index={n}",
               "--arg", f"sha={sha}", "--arg", f"mode={mode}",
               "--wait-reply", "0"]
        # ===========================================================
        # 區塊職責：把「訊號有沒有送出去」與「怎麼把它講出來」**分成兩段**（TASK-0043）。
        #
        # 🩸 血證（2026-08-25）：舊版把兩者放在同一個 try 裡，而 success 分支的指路字串
        #   用了 `{n:04d}` —— 而 `n` 來自 `TASK-(\d+)` 的 regex group，**是字串**。
        #   ⇒ 推進**已經成功之後**才丟 TypeError，被外層 except 接住，印成
        #     「⚠ 推進失敗（…）—— **單子狀態沒動**，需手動補。」
        #   而單子那時已經是 `in_review` 了。手動補跑一次印出「（不變）」才拆穿它。
        #
        # 📌 三本帳同時錯，而最貴的是第二句：**它斷言了一個它不知道的事實**，
        #   然後叫人去做一個不需要的手動補 —— 而手動補的副作用是這句假話造成的。
        # ⇒ 一般形：**`except` 只知道「這裡炸了」，它不知道「炸之前做完了什麼」。
        #   失敗處理不可以替它不知道的事作答。**
        # ===========================================================
        sent, why = False, ""
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=180)
            sent = (r.returncode == 0 and "Success" in (r.stdout or ""))
            if not sent:
                why = f"run_cmd 回 {r.returncode}"
        except Exception as e:
            why = f"{type(e).__name__}: {e}"     # 這一格的失敗才是真的**沒送出去**

        if not sent:
            print(f"⚠ TASK-{n} **沒有送出去**（{why}）—— 單子狀態沒動，手動補 "
                  f"run Task --arg op=commit --arg index={n} --arg sha={sha} --arg mode={mode}",
                  file=sys.stderr)
            continue

        # 送到了。以下純輸出 —— 這裡再炸也**不可以**被讀成推進失敗。
        # 不在這裡宣告「已完成」：真正的落點是 in_review 還是 done 由 Cmd 判，它印在回傳檔裡。
        try:
            print(f"📋 TASK-{n} 已收到 commit（{mode} / {sha}）—— 落到哪一格讀單檔："
                  f"AgentCommands/Tasks/tasks/{int(n):04d}.md")
        except Exception as e:
            print(f"⚠ TASK-{n} 的**回報**出錯（{type(e).__name__}: {e}）—— "
                  f"⚠ **訊號已經送出去了，單子可能已經推進**，去讀 "
                  f"AgentCommands/Tasks/tasks/ 底下那張單確認，不要盲目手動補。",
                  file=sys.stderr)


def post_announcement(body: str, sha: str, persona: str) -> tuple:
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
               # ⚠ 刻意**不帶 sender_id** —— 顯示身分由 Cmd_Tavern 從 persona 推導
               # （見上方 resolve_sender 移除的理由）。多傳一個就是多一個會漂的來源。
               "--arg", f"persona={persona}",
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
    ap.add_argument("--expect-files", type=int, default=None,
                    help="宣告這一筆應該收幾個檔；與實際 staged 數不符就擋下不提交"
                         "（不帶＝不檢查。防「git add <目錄> 把別人的檔一起收走」）")
    ap.add_argument("--announce-body", default="",
                    help="公告的開場白（插在標題與 commit 內文之間，寫給現在在酒館的同事看）")
    ap.add_argument("--announce-body-file", default="",
                    help="同上，改從檔案讀（長文或含特殊字元時用）")
    ap.add_argument("--bump-of", default="",
                    help="本筆是某主 commit 的 pointer bump：公告壓成一行並指向該 SHA（帳照領）")
    ap.add_argument("--verbose", action="store_true",
                    help="成功時印完整細節（預設只印一行 —— 成功路徑瘦到看不見，異常才佔版面）")
    args = ap.parse_args()

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

    # ===========================================================
    # 區塊職責：`--expect-files N` —— staged 檔數與宣告不符就**擋下**，不提交。
    #
    # 🩸 2026-08-24 summit 一天三次同族「讀數印出來了而我沒讀」：
    #   ① commit 訊息只講兩張單而 `--name-only` 印了五個檔（我看到了，照樣送出）
    #   ② `git add <目錄>` 用目錄當清單，收走同事正在寫的兩張單
    #   ③ 回傳檔印了「已建單 TASK-0010」而我對 index=8 下手，取消掉同事的主 Task
    #   三次那個正確的讀數**都已經在畫面上**。⇒ 「下次記得看」是願望（第二三次都發生在我
    #   寫下第一次的修法之後）；有效的只有兩種形狀：把清單縮短，或把手勢換掉。
    #
    # 本旗標屬後者：它把「我以為我在提交幾個檔」變成一個**必須先算過**的數字 ——
    # 跟 `sculpt.py --expect-pixels` 同一個形狀（本 repo 已有的慣例，不是新發明）。
    #
    # ⚠ 不帶旗標時**行為完全不變** —— 強制它會讓既有呼叫端全部壞掉，而那會讓人改去繞過本工具。
    # 數值影響：不符時 exit 2（參數層問題），**且在 git commit 之前**返回 ⇒ 沒有東西落地。
    # ===========================================================
    staged_files = [l for l in staged.stdout.splitlines() if l.strip()]
    if args.expect_files is not None and args.expect_files != len(staged_files):
        print(f"ERROR: --expect-files={args.expect_files} 但實際 staged **{len(staged_files)}** 個檔"
              f" ⇒ 擋下，沒有提交。", file=sys.stderr)
        print("  完整清單（這就是那個「印出來了而沒被讀」的讀數）：", file=sys.stderr)
        for f in staged_files:
            print(f"    - {f}", file=sys.stderr)
        print("  ⇒ 要嘛改 --expect-files 的數字（先確認每一個檔都是你要收的），"
              "要嘛 unstage 不該收的（別人正在寫的檔不會有任何一層喊）。", file=sys.stderr)
        return EXIT_BAD_ARGS

    result = subprocess.run(["git", "-C", args.repo, "commit", "-F", "-"],
                            input=message, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        print(result.stdout, end="")
        print(f"ERROR: git commit 失敗：{result.stderr.strip()}", file=sys.stderr)
        return EXIT_COMMIT_FAIL

    sha = git(args.repo, "rev-parse", "--short", "HEAD").stdout.strip()
    # 成功路徑刻意安靜（Alert Fatigue，apex-one 2026-08-03 命名）：
    # 每次成功都印同一塊五行，看第八次它就是背景 —— 於是真正要看的那一行也被跳過。
    # 所以正常成功只留一行，細節走 --verbose；異常路徑維持大聲。
    if args.verbose:
        print(result.stdout.strip())
        for t in trailers:
            print(f"  {t}")
    for n in notes:
        print(f"⚠ {n}", file=sys.stderr)

    primary = args.persona[0]
    # ⛔ 這裡原本先解析 bank 當 sender，查不到就擋下公告（EXIT_ANNOUNCE_FAIL）。
    #    兩件事都退場：解析交給 Cmd（統一入口），而「查不到 bank 就不發言」也不再成立 ——
    #    c103e1f 拍板「不計酬不擋發言」：發言權與收款權是兩回事。
    #    ⇒ 沒有 bank 的 persona 現在照樣公告得出去，只是那則不計酬（由 Cmd 端決定）。

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
        sha, primary)
    if ok:
        if args.verbose:
            print(f"📣 酒館公告已發（sha={sha} / persona={primary}）—— 不要再手動貼一次，同 SHA 貼兩次會付兩次錢。")
        else:
            print(f"✓ {sha} 已提交並公告（{primary}{'／bump of ' + args.bump_of if args.bump_of else ''}）")
        resolve_fixed_bugs(message, sha, primary)
        advance_tasks(message, sha, primary)
        return EXIT_OK
    # commit 已經落地了，這裡失敗只有錢沒領到 —— 講清楚是哪一半失敗，別讓人以為 commit 也沒成功。
    print(f"⚠ commit 成功但公告發送失敗：{detail}", file=sys.stderr)
    print(f"   commit 已落地（{sha}），只有領薪那步沒完成。手動補一則帶 "
          f"meta {{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}} 的酒館貼文即可。", file=sys.stderr)
    return EXIT_ANNOUNCE_FAIL


if __name__ == "__main__":
    sys.exit(main())
