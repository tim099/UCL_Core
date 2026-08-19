#!/usr/bin/env python3
# 區塊職責：把 `letters/Template/.gitignore`（基線）同步到其他 persona 的 letters 目錄。
# 物理意義：
#   八個 letters repo 的 `.gitignore` 2026-08-18 實掃結果：共用規則只有 7 條，
#   其餘是各自長出來的**逐檔清單**（`_streamwatch_observe.md` 只有 4 個 repo 擋、
#   `_freetime_partners.md` 只有 1 個）。那種清單天生落後 —— 新增一支 Cmd／一個 step 就漏一個，
#   而漏掉的症狀是「檔案開始出現在 git status 裡」，長得跟「我今天寫了東西」一模一樣。
#   ⇒ 共用規則收斂成一份基線（Template），各 persona 檔＝基線區塊 ＋ 自訂區塊。
#
#   **自訂區塊同步不動**：每位 persona 有自己的需要（別人的目錄不該由工具重寫）。
#   分界靠 BASELINE END 標記，而不是靠人記得「這幾行是我加的」。
#
# 數值影響：
#   只寫目標檔的基線區塊；`--check` 完全不寫。基線內容 sha256 記在標頭 ——
#   靠人記得手抄就是下一個 6/16（同 sync_lib_mirror.py 的理由）。
#   ⚠ `.gitignore` 不會 untrack 已追蹤的檔：同步後若某人 `cmd/` 裡有已入版控的回傳檔，
#     本工具會**報出來**（要不要 `git rm --cached` 是那位 persona 自己的決定，不是工具的）。
#
# 用法：
#   python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py                # 同步全部
#   python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py --check        # 只報漂移
#   python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py --persona gura # 只做一位
#   python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py --all-personas # 含非 repo 的目錄
#
# Exit codes: 0 = 一致／已同步；1 = 錯誤（基線缺檔等）；2 = 偵測到漂移（--check）
# 2026-08-18 calli（Tim 指示：綜合各 persona 的 .gitignore 做成 Template 範本再同步）

from __future__ import annotations

import argparse
import hashlib
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))       # 讓 `_lib` import 得到
from _lib.persona_profile import pool_names                   # noqa: E402
from _lib.ucl_paths import letters_root                       # noqa: E402

BASELINE_PERSONA = "Template"                                  # 基線住哪（＝範本 persona）
GITIGNORE = ".gitignore"

_HEAD_MARK = "# ╔═══ BASELINE"
_END_MARK = "# ╚═══ BASELINE END"
_SHA_PREFIX = "# baseline_sha256: "


def _sha(text: str) -> str:
    """對基線文字算 sha256（統一 LF，避免 CRLF 差異被誤判成漂移）。"""
    return hashlib.sha256(text.replace("\r\n", "\n").encode("utf-8")).hexdigest()


def _header(sha: str) -> str:
    return (
        f"{_HEAD_MARK} — 由 letters/{BASELINE_PERSONA}/.gitignore 同步，勿在本區編輯 ═══╗\n"
        f"# 要改共用規則：改基線檔，再跑 sync_letters_gitignore.py（--check 只報漂移）。\n"
        f"# 自訂規則寫在檔尾「本 persona 自訂」區 —— 那一區同步工具不動。\n"
        f"{_SHA_PREFIX}{sha}\n"
    )


def _split_target(text: str) -> tuple:
    """把目標檔拆成 (標頭記的 sha, 自訂區塊)。沒有基線標記 ⇒ (None, 全文當自訂)。

    物理意義：第一次同步時，該 persona 現有的整份 `.gitignore` 都算「自訂」——
             它會被保留在檔尾，人可以自己逐條刪掉已被基線涵蓋的部分。
             ⚠ 刻意不自動刪：那份檔裡有別人寫的血證註解，機器判斷不出哪句還有價值。
    """
    lines = text.replace("\r\n", "\n").split("\n")
    if not lines or not lines[0].startswith(_HEAD_MARK):
        return None, text.replace("\r\n", "\n")
    sha = None
    for i, ln in enumerate(lines):
        if ln.startswith(_SHA_PREFIX):
            sha = ln[len(_SHA_PREFIX):].strip()
        if ln.startswith(_END_MARK):
            return sha, "\n".join(lines[i + 1:])
    return sha, ""                                              # 有頭沒尾（被人改壞）→ 自訂區視為空


def _compose(baseline: str, custom: str) -> str:
    body = _header(_sha(baseline)) + baseline.rstrip("\n") + "\n" + f"{_END_MARK} ═══╝\n"
    custom = custom.strip("\n")
    if custom:
        body += "\n" + custom + "\n"
    else:
        body += "\n# ── 本 persona 自訂（同步工具不動這一區）──\n"
    return body


# 自訂區裡「會蓋掉基線」的規則 —— gitignore 是**後者勝**，而自訂區排在基線之後。
# 🩸 實測：calli 的舊檔有 `/cmd/`，同步後它排在 `!/cmd/.gitignore` 後面 ⇒ 目錄自帶的
#    ignore 又被擋掉、不會入版控。症狀是「同步成功但規則沒生效」，沒有任何一格會紅。
_CONFLICT_RULES = ("/cmd/", "cmd/", "/cmd/*")


def _tail_conflicts(custom: str) -> list:
    """回自訂區裡與基線 cmd/ 規則衝突的行（純比對，不改檔）。"""
    out = []
    for ln in custom.split("\n"):
        s = ln.strip()
        if not s or s.startswith("#"):
            continue
        if s in _CONFLICT_RULES:
            out.append(s)
    return out


def _is_git_repo(d: Path) -> bool:
    return (d / ".git").exists()


def _tracked_cmd_files(d: Path) -> list:
    """該 letters repo 裡**已被追蹤**的 cmd/ 檔 —— gitignore 治不了它們，要人決定。"""
    if not _is_git_repo(d):
        return []
    try:
        out = subprocess.run(["git", "-C", str(d), "ls-files", "cmd/"],
                             capture_output=True, text=True, encoding="utf-8", timeout=20)
        return [ln for ln in (out.stdout or "").splitlines() if ln.strip()]
    except Exception:
        return []


# ⚠ persona 名單走接縫 `_lib/persona_profile.pool_names()`，**不自己 glob**：
#   「有哪些 persona」的判準（`_` / `.` 前綴、壞檔算不算）住在 C# 單端，
#   快照把結果清單直接帶出來 ⇒ 這裡連判準都不必有。
#   🩸 自己 glob 的代價是**判準漂移**：某天 C# 端改了前綴規則，這裡不會跟著改，
#   而兩邊都不會報錯 —— 只是「有沒有這個人」開始給兩種答案。
def _known_personas() -> set:
    return set(pool_names())


def main() -> int:
    ap = argparse.ArgumentParser(description="同步 letters/.gitignore 基線（來源＝Template）")
    ap.add_argument("--check", action="store_true", help="只報漂移，不寫檔（exit 2 = 有漂移）")
    ap.add_argument("--persona", default=None, help="只處理這一位")
    ap.add_argument("--all-personas", action="store_true",
                    help="連非 git repo 的 letters 目錄也放（預設只做獨立 repo）")
    args = ap.parse_args()

    root = letters_root()
    base_file = root / BASELINE_PERSONA / GITIGNORE
    if not base_file.is_file():
        print(f"❌ 基線不存在：{base_file}", file=sys.stderr)
        return 1
    baseline = base_file.read_text(encoding="utf-8").replace("\r\n", "\n")
    base_sha = _sha(baseline)
    print(f"📐 基線：{base_file}（{len(baseline.splitlines())} 行，sha {base_sha[:12]}…）")

    known = _known_personas()
    targets = []
    for d in sorted(root.iterdir()):
        if not d.is_dir() or d.name.startswith("_") or d.name == BASELINE_PERSONA:
            continue
        if args.persona and d.name != args.persona:
            continue
        if d.name not in known:
            continue                                            # 沒有 persona 檔的目錄（別名／歷史殘留）不碰
        if not args.all_personas and not _is_git_repo(d):
            continue
        targets.append(d)

    if not targets:
        print("（沒有符合條件的目標 —— 預設只做獨立 repo，要含其餘目錄加 --all-personas）")
        return 0

    drift = 0
    for d in targets:
        f = d / GITIGNORE
        cur = f.read_text(encoding="utf-8") if f.is_file() else ""
        had_sha, custom = _split_target(cur)
        want = _compose(baseline, custom)
        same = (had_sha == base_sha) and (cur.replace("\r\n", "\n") == want)
        tracked = _tracked_cmd_files(d)
        note = ""
        if tracked:
            note = f"　⚠ cmd/ 有 {len(tracked)} 個**已追蹤**檔（ignore 治不了，需 `git rm --cached cmd/`）"
        conflicts = _tail_conflicts(custom)
        if conflicts:
            # 不自動刪：自訂區是那位 persona 的地盤。但一定要說 —— 不說就是「同步成功卻沒生效」。
            note += (f"　⚠ 自訂區有 {', '.join(conflicts)} 會**蓋掉**基線的 `!/cmd/.gitignore`"
                     f"（gitignore 後者勝）⇒ 請把那幾行從自訂區刪掉")
        if same:
            print(f"  ✓ {d.name}：已一致{note}")
            continue
        drift += 1
        state = "無基線區塊（首次）" if had_sha is None else f"基線 sha {had_sha[:12]}… ≠ {base_sha[:12]}…"
        if args.check:
            print(f"  ⚠ {d.name}：{state}{note}")
            continue
        f.write_text(want, encoding="utf-8", newline="\n")
        kept = len([ln for ln in custom.strip("\n").split("\n") if ln.strip()])
        print(f"  ✍ {d.name}：已同步（{state}；自訂區保留 {kept} 行）{note}")

    if args.check and drift:
        print(f"\n[exit] code=2 reason={drift} 個目標與基線不一致（--check 不寫檔）")
        return 2
    print(f"\n{'✅ 全部一致' if not drift else f'✅ 已同步 {drift} 個目標'}（基線＝{BASELINE_PERSONA}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
