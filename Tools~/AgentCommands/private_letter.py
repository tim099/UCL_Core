#!/usr/bin/env python3
"""密封信件（Sealed Letters）—— 寫進各 persona letters repo 的 `private` 分支，不切分支、不經過公開的 master。

區塊職責：把「真正私密的內容」（含晚安密文區的**明文答案**）放進只推到私有 remote 的 `private` 分支。

物理意義（為什麼需要這支工具，而不是 `git add` + `git commit`）：
  每個 persona 的 letters repo 都有兩個世界：
      origin         → github.com/...     **公開**
      gitlab.private → gitlab.com/...     私有
  `master` 追 origin。所以**任何進 master 的東西都是公開的** —— 在 master 上 `git add`
  一封密封信 = 把它推上公開網路，而且 git history 刪不掉（事後刪檔只是再加一個 commit）。

  但工作區只有一份、而且 checkout 的是 master。要把檔案送進 `private` 又不切分支
  （切分支會把整個工作區換掉，而 daemon / 各種工具正在寫檔），只剩一條路：
  **繞過 index 與工作區，直接用 plumbing 造 commit。**

數值影響：
  - 只寫物件庫 + 移動 `refs/heads/private`。**不動 HEAD、不動工作區、不動 master。**
  - 預設**不 push**（推送是對外動作，要顯式 `--push`）。

邊界 / 已知代價（別事後才發現）：
  - **完全繞過 hooks**：這不是 `git commit`，pre-commit / commit-msg 都不會跑。
  - **沒有領薪公告**：`git_commit.py` 走 `git commit`，這支不相容。密封信是私事，不是工作 commit。
  - `private` 與 `master` 是平行歷史，長期會分岔。**刻意如此** —— 它們本來就不該合。
  - 密封信的工作區檔案靠 master 的 `.gitignore` 擋住。**那行 ignore 是唯一一道自動防線**，
    所以寫入類 op 開頭會先驗它存在；不存在就拒跑，不是印警告。

血統：本檔是 `letters/summit/tools/private_letter.py`（summit 2026-08-04 首航）的通用化搬遷版
  —— 加 `--persona` 解析 repo、加 private 分支自動建立、加密文封緘/對帳兩個 op。
  四個 plumbing 細節（`hash-object --path` / `core.quotePath=false` / `update-ref` 帶舊值 /
  Windows `mkstemp` 要 close）全部照收，那些是她踩過才知道的，不是風格選擇。
  ⚠ 她 repo 內那份**不動**（Tim 2026-08-18 拍板）—— 換不換由她自己決定。

⚠ 本工具自己在 UCL_Core（**公開**）—— 工具不是秘密，內容才是。
  別把秘密寫進本檔的註解或範例裡。
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

_HERE = Path(__file__).resolve().parent

SEALED_DIR = "sealed"                              # 只存在 private 分支
PRIVATE_BRANCH = "private"
PUBLIC_BRANCH = "master"
PRIVATE_REMOTE = "gitlab.private"
# 密文答案檔的檔名尾綴 —— verify-cipher 靠它辨識，不靠 frontmatter 掃全檔。
CIPHER_ANSWER_SUFFIX = "cipher-answer"


# ── 路徑解析（委派 _lib/ucl_paths，python 端唯一擁有者）────────────────────
def _letters_root() -> Path:
    """委派 `_lib/ucl_paths.letters_root()` —— 不自己推導。

    物理意義：letters 根可被 path config override（各專案掛載位置不同），
    自推導的失敗是**靜默的**（會算到另一棵資料樹，然後回一個看起來正常的數字）。
    """
    import importlib.util as _ilu
    spec = _ilu.spec_from_file_location(
        "_ucl_paths_private_letter", _HERE / "_lib" / "ucl_paths.py")
    mod = _ilu.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.letters_root()


REPO: Path = None          # letters/<persona>/ —— main() 依 --persona 填


def _resolve_repo(persona: str) -> Path:
    root = _letters_root()
    repo = root / persona
    if not repo.is_dir():
        raise RuntimeError(
            f"✗ 找不到 persona '{persona}' 的 letters repo：{repo}\n"
            f"  （letters 根 = {root}）")
    if not (repo / ".git").exists():
        raise RuntimeError(f"✗ {repo} 不是 git repo —— 密封信需要 private 分支")
    return repo


def git(*args, check=True, env=None) -> str:
    """跑 git，回 stdout（strip 過）。cwd 固定在本 persona 的 repo —— 不受呼叫端 cwd 影響。"""
    e = dict(os.environ)
    if env:
        e.update(env)
    # ⚠ core.quotePath=false 是必須的，不是美觀問題：預設 true 時 git 會把非 ASCII 檔名
    #   轉義成 "\350\207\252..." 並**在開頭加一個引號**，於是 `startswith("sealed/")`
    #   這類前綴比對會全數落空 —— 症狀是「寫進去了但 list 說沒有」（summit 實測踩過）。
    #   放在 git() 這一處＝所有呼叫端一次修好，不會有第二種讀法。
    r = subprocess.run(["git", "-c", "core.quotePath=false", *args], cwd=str(REPO),
                       capture_output=True, text=True, encoding="utf-8", env=e)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} 失敗：{r.stderr.strip() or r.stdout.strip()}")
    return (r.stdout or "").strip()


def _slug(s: str) -> str:
    s = re.sub(r"[^\w一-鿿-]+", "-", (s or "").strip())
    return re.sub(r"-+", "-", s).strip("-")[:60] or "untitled"


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ── 防線 ──────────────────────────────────────────────────────────────────
def assert_master_ignores_sealed():
    """master 的 .gitignore 必須擋住 sealed/ —— 這是唯一一道自動防線。

    ⚠ 拒跑而不是印警告：警示的有效性取決於「接收方剛好有空看」，
      而這條漏掉的後果是私密內容上公開網路、且 history 刪不掉。
    """
    gi = REPO / ".gitignore"
    lines = []
    if gi.is_file():
        lines = [l.strip() for l in gi.read_text(encoding="utf-8").splitlines()]
    if f"{SEALED_DIR}/" not in lines and SEALED_DIR not in lines:
        raise RuntimeError(
            f"✗ 目前 checkout 的分支 .gitignore 沒有 `{SEALED_DIR}/` —— 拒絕繼續。\n"
            f"  沒有那行的話，密封信會以 untracked 出現在 {PUBLIC_BRANCH} 的 git status，\n"
            f"  下一個 `git add -A` 就會把它推上公開 remote（history 刪不掉）。\n"
            f"  修法：在 {gi} 加一行 `{SEALED_DIR}/`")


def assert_not_on_public(paths: list):
    """驗 master 的 tree 完全沒有這些路徑 —— 寫入後的事後對帳，不是寫入前的假設。"""
    tracked = set(git("ls-tree", "-r", "--name-only", PUBLIC_BRANCH).splitlines())
    leaked = [p for p in paths if p in tracked]
    if leaked:
        raise RuntimeError(f"✗ 這些路徑竟然在 {PUBLIC_BRANCH} 上（公開）：{leaked}")


def ensure_private_branch() -> bool:
    """`private` 不存在就從當前 master 建（回 True 表示這次新建）。

    物理意義：八個 persona repo 裡有人只有 remote 沒有分支（實測 kiara）。
    沒有這一步，那個人第一次寫密封信就會炸在 `rev-parse private`，
    而錯誤訊息長得像工具壞了 —— 實際上只是分支還沒出生。
    """
    try:
        git("rev-parse", "--verify", f"refs/heads/{PRIVATE_BRANCH}")
        return False
    except RuntimeError:
        base = git("rev-parse", PUBLIC_BRANCH)
        git("update-ref", f"refs/heads/{PRIVATE_BRANCH}", base)
        return True


# ── plumbing 寫入 ─────────────────────────────────────────────────────────
def existing_sealed_entries() -> list:
    """`private` tip 上已有的 sealed/ 檔案 → [(mode, sha, path)]。

    ⚠ 這支是 B 方案的**防資料遺失關鍵**：基底換成 master 之後，
      若不主動把既有密封信帶進新 tree，它們會從 tip 消失
      （history 還在，但 tip 沒有 = checkout / 備份都拿不到）。
    """
    out = []
    try:
        listing = git("ls-tree", "-r", PRIVATE_BRANCH, "--", f"{SEALED_DIR}/")
    except RuntimeError:
        return out
    for line in listing.splitlines():
        if not line.strip():
            continue
        info, _, path = line.partition("\t")
        parts = info.split()
        if len(parts) >= 3 and parts[1] == "blob":
            out.append((parts[0], parts[2], path))
    return out


def commit_to_private(rel_paths: list, message: str) -> str:
    """把工作區的檔案 commit 進 `private`，不切分支。回新 commit sha。

    **B 方案（Tim 2026-08-04 拍板）：`private` = 當前 master + sealed/**。
    基底取**當前 master** 而不是舊的 private tree，所以 `private` 永遠是完整超集：
    公開內容與私密內容都在，`git diff master private` 永遠只剩 `sealed/`。

    A 方案（錨在舊 private）的問題首航就照出來了：`private` 上連寫入工具本身都沒有，
    而且落後幅度只會單調成長 —— 那種「備份」備份不到自己的信。

    做法（plumbing）：暫存 index ← **master** 的 tree
                    → 帶回既有 sealed/（防遺失）→ 塞新 blob → write-tree
                    → commit-tree（父 = private + master，merge commit）→ update-ref。
    """
    parent = git("rev-parse", PRIVATE_BRANCH)
    base = git("rev-parse", PUBLIC_BRANCH)
    fd, idx = tempfile.mkstemp(prefix="sealed_idx_")
    os.close(fd)
    os.unlink(idx)                      # git 要求檔案不存在或是合法 index
    env = {"GIT_INDEX_FILE": idx}
    try:
        git("read-tree", PUBLIC_BRANCH, env=env)
        # 既有密封信先帶回來 —— 順序在新檔之前，同名時讓新檔覆蓋
        for mode, sha, path in existing_sealed_entries():
            git("update-index", "--add", "--cacheinfo", f"{mode},{sha},{path}", env=env)
        for rel in rel_paths:
            src = REPO / rel
            if not src.is_file():
                raise RuntimeError(f"✗ 檔案不存在：{src}")
            # --path 讓 .gitattributes 的換行 / filter 規則生效。
            # 不帶的話物件庫裡的 blob 會跟 checkout 出來的不一致 —— 而且是靜默不一致。
            sha = git("hash-object", "-w", f"--path={rel}", str(src), env=env)
            git("update-index", "--add", "--cacheinfo", f"100644,{sha},{rel}", env=env)
        tree = git("write-tree", env=env)
    finally:
        if os.path.exists(idx):
            os.unlink(idx)

    # ⚠ mkstemp 回傳的 fd 一定要關 —— Windows 上檔案還開著就 unlink 會噴
    #   WinError 32（檔案正由另一個程序使用）。POSIX 上不會，所以這是平台差異坑。
    mfd, mpath = tempfile.mkstemp(prefix="sealed_msg_", suffix=".txt")
    os.close(mfd)
    mf = Path(mpath)
    try:
        mf.write_text(message, encoding="utf-8")
        # 兩個父：private（延續密封信歷史）+ master（宣告「這份包含了到此為止的公開內容」）。
        # 帶 master 當第二父不是形式 —— 沒有它，git 看不出 private 已涵蓋 master，
        # `git log private..master` 會一直有東西，落後幅度就無法對帳。
        args = ["commit-tree", tree, "-p", parent]
        if base != parent and base not in git("rev-list", parent).splitlines():
            args += ["-p", base]
        new = git(*args, "-F", str(mf))
    finally:
        mf.unlink(missing_ok=True)

    git("update-ref", f"refs/heads/{PRIVATE_BRANCH}", new, parent)   # 帶舊值 = 防併發覆寫
    return new


def _write_sealed(rel: str, title: str, body: str, extra_fm: dict = None) -> Path:
    """把一份密封內容寫進工作區的 sealed/（被 .gitignore 擋著，master 看不到）。"""
    dst = REPO / rel
    dst.parent.mkdir(parents=True, exist_ok=True)
    fm = ["---", "type: sealed_letter", f"title: {title}",
          f"at: {_now().isoformat().replace('+00:00', 'Z')}",
          "visibility: private-branch-only"]
    for k, v in (extra_fm or {}).items():
        fm.append(f"{k}: {v}")
    fm += ["---", ""]
    dst.write_text("\n".join(fm) + f"# 🔐 {title}\n\n" + body.strip() + "\n",
                   encoding="utf-8")
    return dst


def _push_note(args) -> None:
    if getattr(args, "push", False):
        git("push", PRIVATE_REMOTE, f"{PRIVATE_BRANCH}:{PRIVATE_BRANCH}")
        print(f"   ⬆ 已推到 {PRIVATE_REMOTE}/{PRIVATE_BRANCH}")
    else:
        print(f"   ⚠ **未 push**（推送是對外動作，要顯式 --push）")


# ── 密文對帳的共用件 ──────────────────────────────────────────────────────
def _norm_cipher(text: str) -> str:
    """密文正規化 —— hash 前統一換行與行尾空白。

    物理意義：hash 要對的是「密文的字」，不是「密文的檔案格式」。
    CRLF/LF、尾端空白、檔尾換行都不該讓對帳失敗，否則守衛會**假紅**，
    而假紅久了就會被關掉（那比沒有守衛更糟）。
    """
    lines = [l.rstrip() for l in (text or "").replace("\r\n", "\n").replace("\r", "\n").split("\n")]
    return "\n".join(lines).strip()


def _cipher_sha(text: str) -> str:
    return hashlib.sha256(_norm_cipher(text).encode("utf-8")).hexdigest()


def _sealed_answers() -> list:
    """private 上的密文答案檔（新到舊）→ [路徑字串]。"""
    names = [n for n in git("ls-tree", "-r", "--name-only", PRIVATE_BRANCH).splitlines()
             if n.startswith(f"{SEALED_DIR}/") and CIPHER_ANSWER_SUFFIX in n]
    return sorted(names, reverse=True)


def _read_fm(text: str) -> tuple[dict, str]:
    """回 (frontmatter dict, body)。壞檔回 ({}, 全文) —— 不吞內容。"""
    if not text.startswith("---"):
        return {}, text
    parts = text.split("---", 2)
    if len(parts) < 3:
        return {}, text
    meta = {}
    for line in parts[1].splitlines():
        k, _, v = line.partition(":")
        if k.strip():
            meta[k.strip()] = v.strip()
    return meta, parts[2].lstrip("\n")


CIPHER_FENCE = "```cipher"


def _extract_fenced_cipher(body: str) -> str:
    """從答案檔 body 撈出 ```cipher 區塊的原文（找不到回空字串）。"""
    m = re.search(r"```cipher\n(.*?)```", body, re.S)
    return m.group(1) if m else ""


def _wake_letter_texts() -> list:
    """master 上 wakes/ 的收尾信全文（新到舊）→ [(檔名, 全文)]。

    邊界：讀的是**工作區**的 wakes/（那是 master 的內容，密封信不在裡面）。
    """
    d = REPO / "wakes"
    if not d.is_dir():
        return []
    out = []
    for p in sorted(d.glob("*.md"), reverse=True):
        try:
            out.append((p.name, p.read_text(encoding="utf-8")))
        except Exception:
            continue
    return out


# ── 子命令 ────────────────────────────────────────────────────────────────
def cmd_write(args):
    assert_master_ignores_sealed()
    created = ensure_private_branch()
    body = args.body
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not (body or "").strip():
        print("✗ 內容為空（--body 或 --body-file 擇一）", file=sys.stderr)
        return 2

    ts = _now().strftime("%Y%m%dT%H%M%SZ")
    rel = f"{SEALED_DIR}/{ts}__{_slug(args.title)}.md"
    _write_sealed(rel, args.title, body)

    sha = commit_to_private([rel], args.message or f"密封信：{args.title}")
    assert_not_on_public([rel])

    if created:
        print(f"🌱 `{PRIVATE_BRANCH}` 分支首次建立（基底＝當前 {PUBLIC_BRANCH}）")
    print(f"🔐 密封信已寫入 `{PRIVATE_BRANCH}`：{sha[:8]}")
    print(f"   {rel}")
    print(f"   工作區檔案存在但被 .gitignore 擋住 —— {PUBLIC_BRANCH} 看不到、不會被 add 走")
    print(f"   HEAD 仍在：{git('rev-parse', '--abbrev-ref', 'HEAD')}（沒有切分支）")
    _push_note(args)
    return 0


def cmd_seal_cipher(args):
    """封緘晚安密文的**明文答案** —— 密文留在公開的信裡，答案只進 private。

    物理意義：這一對的價值在**不對稱** —— 題目公開、答案私有。
    早安讀 brief 會再看到密文（brief 含見樹區），答案在另一條分支上拿不到，
    於是「先自己解一次」不是靠自律，是靠拿不到答案。

    ⚠ 封緘後密文不得再改一字：frontmatter 記 `cipher_sha256`，
      verify-cipher 會拿它對信裡的密文。summit wake#48 就是封緘後又加句，
      造成對照答案裡的一行懸置在半空 —— 那次的教訓變成這裡的欄位。
    """
    assert_master_ignores_sealed()
    created = ensure_private_branch()

    cipher = Path(args.cipher_file).read_text(encoding="utf-8")
    plain = Path(args.plain_file).read_text(encoding="utf-8")
    if not _norm_cipher(cipher):
        print("✗ 密文為空 —— 沒有題目就不必封答案", file=sys.stderr)
        return 2
    if not plain.strip():
        print("✗ 明文答案為空（工具不代筆；答案本來就只有妳寫得出來）", file=sys.stderr)
        return 2

    digest = _cipher_sha(cipher)
    wake = args.wake or "?"
    title = f"wake{wake}-{CIPHER_ANSWER_SUFFIX}"
    ts = _now().strftime("%Y%m%dT%H%M%SZ")
    rel = f"{SEALED_DIR}/{ts}__{_slug(title)}.md"

    body = (
        "## 協議\n"
        "明早讀收尾信 🔐 密文區 → **先憑記憶網解密、寫下解讀** → 再開本檔比對：\n"
        f"`private_letter.py --persona {args.persona} verify-cipher --guess-file <我的解讀>`\n"
        "（工具只做機械對帳與並排，命中與否由妳判 —— 解不開＝出題爛，不是記性爛。）\n\n"
        f"## 密文（原文，與信中逐字一致；sha256={digest[:16]}…）\n"
        f"{CIPHER_FENCE}\n{_norm_cipher(cipher)}\n```\n\n"
        "## 逐句明文\n"
        f"{plain.strip()}\n"
    )
    _write_sealed(rel, title, body, extra_fm={
        "kind": "cipher_answer",
        "wake": wake,
        "cipher_sha256": digest,
    })

    sha = commit_to_private([rel], args.message or f"密封：{title}")
    assert_not_on_public([rel])

    if created:
        print(f"🌱 `{PRIVATE_BRANCH}` 分支首次建立（基底＝當前 {PUBLIC_BRANCH}）")
    print(f"🔐 密文答案已封緘 → `{PRIVATE_BRANCH}`：{sha[:8]}")
    print(f"   {rel}")
    print(f"   cipher_sha256 = {digest}")
    print(f"   ⚠ 封緘完成 —— **信裡的密文從現在起不准再改一字**（改了明早對帳會紅）")
    _push_note(args)
    return 0


def cmd_verify_cipher(args):
    """早安對帳：先交解讀，才給答案。

    流程（順序即防線）：
      1. 讀妳的 --guess-file —— 沒有它就不往下走（拿不到答案＝不會偷看）
      2. 取最近一封（或 --wake N）封緘答案，驗 cipher_sha256 與檔內密文一致
      3. 驗信裡的密文與封緘時逐字一致 —— 不一致代表**題目被改過**，這次對帳無效
      4. 並排印出：密文 / 我的解讀 / 封緘答案
    工具**不判命中**（語意判定不是機械能做的事）；只給一個明確標示為粗糙的字元重疊率。
    """
    guess = Path(args.guess_file).read_text(encoding="utf-8")
    if not guess.strip():
        print("✗ 解讀為空 —— 先寫再開答案，這個順序就是整個機制", file=sys.stderr)
        return 2

    answers = _sealed_answers()
    if not answers:
        print(f"(`{PRIVATE_BRANCH}` 上還沒有密文答案 —— 晚安時跑 seal-cipher 才會有)")
        return 1
    path = None
    if args.wake:
        want = f"wake{args.wake}-{CIPHER_ANSWER_SUFFIX}"
        path = next((n for n in answers if want in n), None)
        if not path:
            print(f"✗ 找不到 wake{args.wake} 的封緘答案；現有：\n  "
                  + "\n  ".join(answers), file=sys.stderr)
            return 1
    else:
        path = answers[0]

    text = git("show", f"{PRIVATE_BRANCH}:{path}")
    meta, body = _read_fm(text)
    sealed_cipher = _extract_fenced_cipher(body)
    sealed_sha = meta.get("cipher_sha256", "")

    print(f"# 🔐 密文對帳 — {path}")
    print(f"- 封緘於：{meta.get('at', '?')}　wake={meta.get('wake', '?')}")

    # 檢查一：答案檔自身一致（密文區塊 vs frontmatter 的 hash）
    self_ok = bool(sealed_cipher) and _cipher_sha(sealed_cipher) == sealed_sha
    print(f"- 答案檔自身一致（區塊 vs frontmatter hash）：{'✅' if self_ok else '❌'}")

    # 檢查二：信裡的密文有沒有被改過 —— 這才是「封緘後不准改一字」的實際量測
    letter_hit, letter_name = None, None
    for name, full in _wake_letter_texts():
        if _norm_cipher(sealed_cipher) and _norm_cipher(sealed_cipher) in _norm_cipher(full):
            letter_hit, letter_name = True, name
            break
    if letter_hit:
        print(f"- 信中密文逐字一致：✅（{letter_name}）")
    else:
        print(f"- 信中密文逐字一致：⚠ 沒在 wakes/ 找到逐字相同的密文")
        print(f"    可能一：封緘後改了信（那這次對帳是在對一份被改過的題目）")
        print(f"    可能二：密文在信裡被排版拆行 —— 讀一眼再判，別直接當成竄改")

    # 粗糙讀數：字元集合重疊。**這不是命中率**，只是「有沒有完全離題」的下限訊號。
    a = set(_norm_cipher(guess))
    b = set(re.sub(r"\s+", "", body))
    overlap = (len(a & b) / len(a)) if a else 0.0
    print(f"- 字元重疊（粗糙讀數，**不是命中率**）：{overlap:.0%}")

    print("\n## 密文（題目）\n")
    print(sealed_cipher or "(答案檔裡沒有 ```cipher 區塊)")
    print("\n## 我的解讀（解封前寫的）\n")
    print(guess.strip())
    print("\n## 封緘答案\n")
    print(body.strip())
    print("\n---\n判定由妳自己下：逐句對，錯的那句記下**斷在哪個詞**——"
          "\n斷點通常是單位或新造詞（summit wake#48：猜 token 實為 commit）。"
          "\n修法是新慣例先在明文用兩次再進密文，不是把密文寫簡單。")
    return 0


def cmd_list(args):
    names = [n for n in git("ls-tree", "-r", "--name-only", PRIVATE_BRANCH).splitlines()
             if n.startswith(f"{SEALED_DIR}/")] if _branch_exists() else []
    if not names:
        print(f"(`{PRIVATE_BRANCH}` 上還沒有密封信)")
        return 0
    print(f"# 🔐 密封信（{len(names)} 封，在 `{PRIVATE_BRANCH}` 分支）\n")
    for n in sorted(names, reverse=True):
        print(f"- {n}")
    return 0


def _branch_exists() -> bool:
    try:
        git("rev-parse", "--verify", f"refs/heads/{PRIVATE_BRANCH}")
        return True
    except RuntimeError:
        return False


def cmd_show(args):
    # 用 `git show` 直讀物件庫 —— **不碰 index、不碰工作區**。
    # 刻意不用 `git checkout <branch> -- <path>`：那會把檔案塞進 master 的 index，
    # 下一次 commit 就把私密信帶上公開分支。
    print(git("show", f"{PRIVATE_BRANCH}:{args.path}"))
    return 0


def cmd_restore(args):
    """把 private 上的密封信還原到工作區（例如新 clone 之後）。"""
    assert_master_ignores_sealed()
    if not _branch_exists():
        print(f"(本地還沒有 `{PRIVATE_BRANCH}` 分支 —— 先跑 sync 或寫第一封)")
        return 0
    names = [n for n in git("ls-tree", "-r", "--name-only", PRIVATE_BRANCH).splitlines()
             if n.startswith(f"{SEALED_DIR}/")]
    n_new = 0
    for rel in names:
        dst = REPO / rel
        if dst.exists() and not args.overwrite:
            continue
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_text(git("show", f"{PRIVATE_BRANCH}:{rel}") + "\n", encoding="utf-8")
        n_new += 1
    print(f"🔐 還原 {n_new} 封（共 {len(names)} 封在分支上）"
          + ("" if args.overwrite else "；已存在的跳過，要蓋過去用 --overwrite"))
    return 0


def cmd_resync(args):
    """不寫新信，只把 `private` 的基底追上當前 master（B 方案的維護動作）。

    什麼時候要跑：master 有新 commit、但這期間沒寫密封信 —— 那 private 就會落後。
    跑完 `git diff master private` 應該只剩 sealed/。
    """
    ensure_private_branch()
    behind = [l for l in git("log", "--oneline", f"{PRIVATE_BRANCH}..{PUBLIC_BRANCH}").splitlines() if l]
    if not behind:
        print(f"✓ `{PRIVATE_BRANCH}` 已涵蓋 {PUBLIC_BRANCH}，不需 resync")
        return 0
    print(f"`{PRIVATE_BRANCH}` 落後 {PUBLIC_BRANCH} {len(behind)} 筆：")
    for l in behind:
        print(f"  - {l}")
    if args.dry_run:
        print("（--dry-run，沒有真的動 ref）")
        return 0
    sha = commit_to_private([], f"resync: private 基底追上 {PUBLIC_BRANCH}（追 {len(behind)} 筆）")
    print(f"✓ {sha[:8]} —— private 現在 = {PUBLIC_BRANCH} + {SEALED_DIR}/")
    return 0


def cmd_sync(args):
    """把私有 remote 上的密封信同步回本地（新機器 / 換裝置時用）。

    順序：fetch 私有 remote → 把遠端 private 併進本地 ref → 還原檔案到工作區。
    fetch 是唯讀動作（不推任何東西出去）。
    """
    assert_master_ignores_sealed()
    print(f"⬇ fetch {PRIVATE_REMOTE} …")
    git("fetch", PRIVATE_REMOTE, PRIVATE_BRANCH, check=False)
    remote_ref = f"refs/remotes/{PRIVATE_REMOTE}/{PRIVATE_BRANCH}"
    try:
        remote_sha = git("rev-parse", remote_ref)
    except RuntimeError:
        print(f"  （遠端還沒有 {PRIVATE_BRANCH} 分支 —— 第一次要先 "
              f"`git push -u {PRIVATE_REMOTE} {PRIVATE_BRANCH}`）")
        remote_sha = None

    if remote_sha:
        if not _branch_exists():
            git("update-ref", f"refs/heads/{PRIVATE_BRANCH}", remote_sha)
            print(f"  🌱 本地 {PRIVATE_BRANCH} 由遠端建立 → {remote_sha[:8]}")
        else:
            local_sha = git("rev-parse", PRIVATE_BRANCH)
            if remote_sha == local_sha:
                print("  本地與遠端同一個 commit，無需更新")
            elif local_sha in git("rev-list", remote_sha).splitlines():
                # 遠端是本地的後代 → 安全快進
                git("update-ref", f"refs/heads/{PRIVATE_BRANCH}", remote_sha, local_sha)
                print(f"  ⏩ 本地 {PRIVATE_BRANCH} 快進到 {remote_sha[:8]}")
            else:
                # 分岔了就住手 —— 自動合併私密信件史是「幫倒忙」的典型
                print(f"  ⚠ 本地與遠端**分岔**（local={local_sha[:8]} remote={remote_sha[:8]}）"
                      f"—— 不自動合併，請人工判斷。")
                return 1

    return cmd_restore(args)


# pre-push hook 本體 —— 版控內的結構性防線（血統：summit 2026-08-04，通用化沿用）。
# 為什麼放 tools/githooks 而不是 .git/hooks：.git/hooks 不進版控，換機器 clone 就沒有防線。
PRE_PUSH_HOOK = """#!/bin/sh
# 區塊職責：擋下「private 分支被推到非私有 remote」——結構性防線，不靠人記得。
# 物理意義：本 repo 的 master 追公開 GitHub、private 只該去私有 GitLab。
#          密封信一旦上了公開 remote，history 刪不掉（事後刪檔只是再加一個 commit）。
# 邊界：hook 判斷的是**目標 remote 的 URL**，不是 remote 名字 —— 名字可以被改，URL 才是事實。
# 安裝：由 <UCL_Core>/Tools~/AgentCommands/private_letter.py install-hook 寫入並設 core.hooksPath。
ALLOWED_HOST='gitlab.com'
remote_name="$1"
remote_url="$2"

while read -r local_ref local_sha remote_ref remote_sha; do
    case "$local_ref$remote_ref" in
        *refs/heads/private*)
            case "$remote_url" in
                *"$ALLOWED_HOST"*) ;;   # 私有 GitLab，放行
                *)
                    echo "✗ 拒絕推送：private 分支只能推到 $ALLOWED_HOST（私有）。" >&2
                    echo "  目標 remote: $remote_name  → $remote_url" >&2
                    echo "  private 含密封信；推上公開 remote 後 history 刪不掉。" >&2
                    exit 1
                    ;;
            esac
            ;;
    esac
done
exit 0
"""


def cmd_install_hook(args):
    """裝上 pre-push 防線（寫 tools/githooks/pre-push + 設 core.hooksPath）。

    ⚠ 這支是 `.gitignore sealed/` 之外的**第二道**防線，兩道守的是不同的洞：
      - .gitignore   擋「密封信被 add 進公開分支」
      - pre-push     擋「private 分支整條被推上公開 remote」
    只有第一道的話，private 上的密封信仍可能被一個 `git push origin --all` 送出去。

    邊界：`core.hooksPath` 是 per-clone 的 git config，**不進版控** ——
    換機器 clone 之後要再跑一次。verify 會把它當成讀數印出來。
    """
    hook = REPO / "tools" / "githooks" / "pre-push"
    existed = hook.is_file()
    if existed and not args.force:
        print(f"✓ hook 已存在：{hook}（要覆寫用 --force）")
    else:
        hook.parent.mkdir(parents=True, exist_ok=True)
        hook.write_text(PRE_PUSH_HOOK, encoding="utf-8", newline="\n")
        os.chmod(hook, 0o755)
        print(f"{'♻ 覆寫' if existed else '🛡 寫入'} hook：{hook}")
    cur = git("config", "core.hooksPath", check=False)
    if cur != "tools/githooks":
        git("config", "core.hooksPath", "tools/githooks")
        print(f"🛡 core.hooksPath: {cur or '(未設)'} → tools/githooks")
    else:
        print("✓ core.hooksPath 已是 tools/githooks")
    print("   ⚠ hook 檔本身要 commit 進 master 才會跟著 clone 走；"
          "core.hooksPath 是本機設定，換機器要再跑一次本指令")
    return 0


def cmd_verify(args):
    """對帳：master 上不該有任何密封信 + 三道防線的現況讀數。"""
    tracked = [n for n in git("ls-tree", "-r", "--name-only", PUBLIC_BRANCH).splitlines()
               if n.startswith(f"{SEALED_DIR}/")]
    gi_ok = True
    try:
        assert_master_ignores_sealed()
    except RuntimeError as e:
        gi_ok = False
        print(e)
    hook_path = REPO / "tools" / "githooks" / "pre-push"
    hooks_cfg = git("config", "core.hooksPath", check=False)
    print(f"- {PUBLIC_BRANCH} 上的密封信：{len(tracked)} 個 " + ("✅" if not tracked else f"❌ {tracked}"))
    print(f"- .gitignore 防線：" + ("✅" if gi_ok else "❌"))
    hook_ok = hook_path.is_file()
    path_ok = hooks_cfg == "tools/githooks"
    print(f"- pre-push hook 檔：" + ("✅" if hook_ok else "❌ 缺（private 可被推上公開 remote）"))
    print(f"- core.hooksPath：" + (f"✅ {hooks_cfg}" if path_ok else
                                  f"❌ {hooks_cfg or '未設'} —— 上面那個檔不會被執行"))
    print(f"- private 分支：" + ("✅" if _branch_exists() else "⚠ 尚未建立（第一次寫入時自動建）"))
    ok = not tracked and gi_ok and hook_ok and path_ok
    if not ok:
        # 紅燈要附修法 —— 只報症狀不給動作的守衛，下一步就是被關掉。
        print(f"\n修法：private_letter.py --persona {args.persona} install-hook")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(
        description="密封信件 — 寫進 persona letters repo 的 private 分支，不切分支、不經過公開的 master")
    # ⚠ --persona 顯式必填：多 persona 環境下，猜「現在是誰」會靜默寫到別人的 repo。
    ap.add_argument("--persona", required=True, help="要操作誰的 letters repo（顯式，不猜）")
    sub = ap.add_subparsers(dest="op", required=True)

    w = sub.add_parser("write", help="寫一封密封信（只進 private 分支）")
    w.add_argument("--title", required=True)
    w.add_argument("--body", default=None)
    w.add_argument("--body-file", default=None, help="長文從檔案讀（避開 CLI 引號地獄）")
    w.add_argument("--message", default=None, help="commit 訊息（預設用標題）")
    w.add_argument("--push", action="store_true", help="順便推到私有 remote（預設不推）")
    w.set_defaults(func=cmd_write)

    sc = sub.add_parser("seal-cipher", help="封緘晚安密文的明文答案（純自願）")
    sc.add_argument("--cipher-file", required=True, help="密文原文（與信裡逐字一致）")
    sc.add_argument("--plain-file", required=True, help="逐句明文答案（親筆）")
    sc.add_argument("--wake", default=None, help="這是第幾次 wake（檔名與對帳用）")
    sc.add_argument("--message", default=None)
    sc.add_argument("--push", action="store_true")
    sc.set_defaults(func=cmd_seal_cipher)

    vc = sub.add_parser("verify-cipher", help="早安對帳：先交解讀，才給答案")
    vc.add_argument("--guess-file", required=True, help="解封前寫下的解讀")
    vc.add_argument("--wake", default=None, help="指定對哪一封（預設最近一封）")
    vc.set_defaults(func=cmd_verify_cipher)

    l = sub.add_parser("list", help="列出 private 分支上的密封信")
    l.set_defaults(func=cmd_list)

    s = sub.add_parser("show", help="讀一封（直讀物件庫，不碰 index / 工作區）")
    s.add_argument("path", help="例：sealed/20260804T...__xxx.md")
    s.set_defaults(func=cmd_show)

    r = sub.add_parser("restore", help="把密封信還原到工作區（新 clone 後用）")
    r.add_argument("--overwrite", action="store_true")
    r.set_defaults(func=cmd_restore)

    rs = sub.add_parser("resync", help="不寫新信，只把 private 基底追上當前 master")
    rs.add_argument("--dry-run", action="store_true")
    rs.set_defaults(func=cmd_resync)

    sy = sub.add_parser("sync", help="從私有 remote 同步密封信回本地（fetch + 還原）")
    sy.add_argument("--overwrite", action="store_true", help="工作區已存在也蓋過去")
    sy.set_defaults(func=cmd_sync)

    v = sub.add_parser("verify", help="對帳：master 上不該有密封信 + 三道防線讀數")
    v.set_defaults(func=cmd_verify)

    ih = sub.add_parser("install-hook", help="裝上 pre-push 防線（private 只准推私有 host）")
    ih.add_argument("--force", action="store_true", help="hook 已存在也覆寫")
    ih.set_defaults(func=cmd_install_hook)

    args = ap.parse_args()
    global REPO
    try:
        REPO = _resolve_repo(args.persona)
        return args.func(args)
    except RuntimeError as e:
        print(e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
