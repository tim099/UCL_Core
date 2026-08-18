#!/usr/bin/env python3
# 區塊職責：檢查 letters 目錄的版面約定 —— **頂層只放人寫的信與耐久檔，回傳檔一律住 `cmd/`**。
# 物理意義：
#   `Plan_Letters_Dir_Layout` 把「是不是信」從「按檔名前綴猜」改成「看位置」，
#   並在 §8.4 拔掉了 `Cmd_DocEdit` 的 `_`-skip heuristic。但那份 plan §1 自己就寫著：
#   **「它依賴一條慣例，而慣例沒有任何地方在強制執行。」**
#   本檔就是那個執行者 —— 拆掉 heuristic 之後補上的機械閘。
#
#   🩸 2026-08-18 全搬那天，有兩件事是「掃一次目錄就看得到」，卻都是動手到一半才撞到的：
#     ① plan §2 的 21 個清單漏了 `_relationship_*` / `_sculpture_*`
#        （清單是某一天某個人目錄的快照，不是全集）。
#     ② 拔掉 `_`-skip 之後，`Cmd_DocEdit` 立刻挑中舊位置的 `_goodmorning_brief.md` 當「最新那封信」。
#   兩者都不會報錯 —— 只有下一個人踩到才會知道。
#
# 數值影響：
#   預設**唯讀**（只 stat / 讀 frontmatter 幾行）。唯一會寫檔的是 `--fix-gitignore`
#   （只補 `cmd/.gitignore`，缺才寫、不覆蓋）—— 其餘一律只報不改：
#   **別人的 letters 目錄不該由工具動手**（同 plan §8.3⑥「自己的可以清，別人的不動」）。
#
# 用法：
#   python <UCL_Core>/Tools~/AgentCommands/check_letters_layout.py                  # 全部（只看有 persona 檔的目錄）
#   python <UCL_Core>/Tools~/AgentCommands/check_letters_layout.py --persona calli   # 只看一位
#   python <UCL_Core>/Tools~/AgentCommands/check_letters_layout.py --fix-gitignore   # 順手補缺的 cmd/.gitignore
#   python <UCL_Core>/Tools~/AgentCommands/check_letters_layout.py --all-dirs        # 含沒有 persona 檔的目錄（別名／歷史殘留）
#
# Exit codes: 0 = 乾淨；1 = 有違規（給 pre-commit / CI / 早安 brief 用）
# 2026-08-18 calli（Plan_Letters_Dir_Layout §8.8）

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))          # 讓 `_lib` import 得到
from _lib.ucl_paths import (letters_root, personas_dir,           # noqa: E402
                            LETTERS_CMD_DIRNAME, CMD_DIR_GITIGNORE,
                            ensure_letters_cmd_dir)

# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：判準用的三張清單。
# 物理意義：① 已知 transient 前綴 = 2026-08-18 之前平鋪在頂層的回傳檔家族。
#             頂層再出現它們 ⇒ **有寫入端沒搬到**（或有人手動貼回來）。
#          ② 具名耐久檔 = 機器維護但刪掉就沒了的三個檔 + README，與 `Cmd_DocEdit`
#             的 `TOP_LEVEL_NON_LETTERS` 逐字對齊（改一邊要改另一邊 —— 沒有機制綁，只有這行註記）。
#          ③ 信的 frontmatter type：`letter_to_future_self`（自己寫的）／
#             `peer_letter_from_persona`（同事寄來的）。兩者都是信，都該留在頂層。
# 數值影響：純字串比對。清單漏一項的後果是「該項不會被檢查」，不會誤報。
# ─────────────────────────────────────────────────────────────────────────
TRANSIENT_PREFIXES = (
    "_goodmorning_", "_goodnight_", "_streamwatch_", "_freetime_",
    "_reading_recall_", "_relationship_", "_sculpture_",
    "_ding_brief", "_wake_brief",
)
DURABLE_NAMES = ("_constitution.md", "_keys_open.md", "_latest.md", "README.md")
LETTER_TYPES = ("letter_to_future_self", "peer_letter_from_persona")


def read_type(path: Path) -> str:
    """讀 frontmatter 的 `type:`（只讀到 frontmatter 結束就停）。讀不到回空字串。"""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            first = f.readline().strip()
            if first != "---":
                return ""
            for _ in range(60):                                   # frontmatter 不該有 60 行以上
                ln = f.readline()
                if not ln or ln.strip() == "---":
                    return ""
                if ln.startswith("type:"):
                    return ln.split(":", 1)[1].split("#")[0].strip()
    except Exception:
        pass
    return ""


def tracked_cmd_files(d: Path) -> list:
    """該目錄底下**已被追蹤**的 `cmd/` 檔 —— `.gitignore` 治不了它們（要人決定 rm --cached）。"""
    try:
        out = subprocess.run(["git", "-C", str(d), "ls-files", f"{LETTERS_CMD_DIRNAME}/"],
                             capture_output=True, text=True, encoding="utf-8", timeout=20)
        # ⚠ 排除 `cmd/.gitignore` —— 它**該**被追蹤（規則要跟著 clone 走），不是違規。
        return [ln for ln in (out.stdout or "").splitlines()
                if ln.strip() and not ln.strip().endswith(".gitignore")]
    except Exception:
        return []


def newest_cmd_mtime(d: Path) -> float:
    """該目錄 `cmd/` 裡最新一份回傳檔的 mtime；沒有 cmd/ 或空的回 0。

    物理意義：這是「這個 persona 最近一次跑 Cmd」的時間基準。
             拿它跟頂層 transient 檔比，就能分出「殘影」與「還在寫舊位置」——
             **不必寫死遷移日期**（寫死的日期會在下一次搬家時變成謊）。
    """
    cmd = d / LETTERS_CMD_DIRNAME
    if not cmd.is_dir():
        return 0.0
    times = [p.stat().st_mtime for p in cmd.glob("*.md")]
    return max(times) if times else 0.0


def check_one(d: Path, fix_gitignore: bool) -> tuple:
    """檢查單一 persona 的 letters 目錄，回 (errors, notes)。

    errors ＝ 會讓 exit code 變 1 的（有東西壞了或有外洩風險）；
    notes  ＝ 只報不算錯的（歷史殘影／舊信缺 frontmatter）。
    ⚠ 這條分界是刻意的：plan §5② 讓舊殘影留著自然淘汰 ——
      把它算成錯會讓這個閘**永遠紅**，而永遠紅的閘等於沒有閘。
    """
    errors, notes = [], []

    # ① 頂層不該再有 transient 回傳檔 —— 但要分「殘影」與「還在寫」
    base = newest_cmd_mtime(d)
    strays = sorted((p for p in d.glob("*.md")
                     if any(p.name.startswith(pre) for pre in TRANSIENT_PREFIXES)),
                    key=lambda p: p.name)
    fresh = [p.name for p in strays if base and p.stat().st_mtime > base]
    stale = [p.name for p in strays if p.name not in fresh]
    if fresh:
        errors.append(f"❌ 頂層有 {len(fresh)} 個回傳檔**比 {LETTERS_CMD_DIRNAME}/ 裡最新的還新**："
                      + ", ".join(fresh[:6]) + ("…" if len(fresh) > 6 else "")
                      + " ⇒ 有寫入端還在寫舊位置（不是殘影）")
    if stale:
        notes.append(f"· 頂層有 {len(stale)} 個舊位置殘影（比 {LETTERS_CMD_DIRNAME}/ 舊，等自然淘汰）："
                     + ", ".join(stale[:4]) + ("…" if len(stale) > 4 else ""))

    # ② 頂層的 .md 要嘛是信（frontmatter 自陳），要嘛是具名耐久檔
    unknown = []
    for p in sorted(d.glob("*.md")):
        if p.name in DURABLE_NAMES or any(p.name.startswith(pre) for pre in TRANSIENT_PREFIXES):
            continue                                              # 耐久檔／①已報過的，不重複
        t = read_type(p)
        if t not in LETTER_TYPES:
            unknown.append(f"{p.name}（type={t or '缺'}）")
    if unknown:
        notes.append(f"· 頂層有 {len(unknown)} 個 .md 沒有信的 frontmatter（舊手寫信多半如此，"
                     f"`Cmd_DocEdit` 挑「最新那封信」時會跳過它們）："
                     + ", ".join(unknown[:4]) + ("…" if len(unknown) > 4 else ""))

    # ③ 有 cmd/ 就必須有 cmd/.gitignore —— 這條擋的是憑證外洩，不是整潔
    cmd_dir = d / LETTERS_CMD_DIRNAME
    if cmd_dir.is_dir():
        gi = cmd_dir / ".gitignore"
        if not gi.is_file():
            if fix_gitignore:
                ensure_letters_cmd_dir(d.name)
                notes.append(f"🔧 {LETTERS_CMD_DIRNAME}/.gitignore 缺 → 已補（--fix-gitignore）")
            else:
                errors.append(f"❌ 有 {LETTERS_CMD_DIRNAME}/ 但缺 .gitignore ⇒ 回傳檔會進版控，"
                              "而其中 wake_brief 含活 session_token 與信箱（`--fix-gitignore` 可補）")
        elif gi.read_text(encoding="utf-8", errors="replace").strip() != CMD_DIR_GITIGNORE.strip():
            notes.append(f"⚠ {LETTERS_CMD_DIRNAME}/.gitignore 內容與工具產出不同"
                          "（有人手改過？只是提醒，不自動蓋回）")
        tracked = tracked_cmd_files(d)
        if tracked:
            errors.append(f"❌ {LETTERS_CMD_DIRNAME}/ 有 {len(tracked)} 個**已追蹤**的回傳檔 "
                          f"（.gitignore 治不了既有追蹤；要脫離版控需 `git rm --cached {LETTERS_CMD_DIRNAME}/`）")
    return errors, notes


def main() -> int:
    ap = argparse.ArgumentParser(description="檢查 letters 版面（頂層只放信與耐久檔，回傳檔住 cmd/）")
    ap.add_argument("--persona", default=None, help="只檢查這一位")
    ap.add_argument("--all-dirs", action="store_true", help="含沒有 persona 檔的目錄（別名／歷史殘留）")
    ap.add_argument("--fix-gitignore", action="store_true", help="順手補缺的 cmd/.gitignore（唯一會寫檔的旗標）")
    ap.add_argument("--quiet-ok", action="store_true", help="只印有問題的目錄")
    args = ap.parse_args()

    root = letters_root()
    if not root.is_dir():
        print(f"❌ letters 根不存在：{root}", file=sys.stderr)
        return 1
    pdir = personas_dir()
    known = {f.stem for f in pdir.glob("*.json") if not f.stem.startswith("_")} if pdir.is_dir() else set()

    print(f"📂 letters 根：{root}")
    targets, bad = [], 0
    for d in sorted(root.iterdir()):
        if not d.is_dir() or d.name.startswith("_"):
            continue
        if args.persona and d.name != args.persona:
            continue
        if not args.all_dirs and d.name not in known:
            continue
        targets.append(d)

    noted = 0
    for d in targets:
        errors, notes = check_one(d, args.fix_gitignore)
        if not errors and not notes:
            if not args.quiet_ok:
                print(f"  ✓ {d.name}")
            continue
        if errors:
            bad += 1
            print(f"  ✗ {d.name}")
        else:
            noted += 1
            if args.quiet_ok:
                continue
            print(f"  ○ {d.name}（只有提醒）")
        for it in errors:
            print(f"      {it}")
        for it in notes:
            print(f"      {it}")

    print(f"\n檢查 {len(targets)} 個目錄 —— "
          + ("✅ 沒有違規" if bad == 0 else f"❌ {bad} 個有違規")
          + (f"（另有 {noted} 個只有提醒，不算錯）" if noted else ""))
    if bad:
        print("（判準與沿革：ucl_core:Docs~/zh-Hant/Plan/Plan_Letters_Dir_Layout.md §8）")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
