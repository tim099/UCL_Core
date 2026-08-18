#!/usr/bin/env python3
# 區塊職責：把 UCL_Core canonical _lib 檔「位元組同步」鏡像到 host 專案 <repo>/AgentCommands/_lib/ (T-PATH-RESOLVE T02)。
# 物理意義：
#   summit 主管裁決一 —— canonical 收斂在 UCL_Core/_lib，AgentCommands/_lib 那份走「同步鏡像」
#   (install_skills.py 模式)，不是各自演化的第二份（各自演化＝根本沒收斂＝下一個 6/16）。
#   兩棵工具樹各 sibling-import 自己的 _lib：UCL_Core/Tools~ 的工具吃 canonical，
#   AgentCommands/Tools 的工具吃這裡同步出去的鏡像。
# 數值影響：
#   鏡像檔 = AUTO-SYNCED 檔頭（純註解）+ canonical body 位元組原樣。
#   檔頭記 source 相對路徑 + canonical body 的 SHA256；--check 靠此雙向偵測漂移，
#   只靠人記得手抄就是下一個 6/16，故 hash 比對是硬需求，不是 nice-to-have。
#
# 用法：
#   python <UCL_Core>/Tools~/AgentCommands/sync_lib_mirror.py            # 同步（本機編輯過鏡像會擋，需 --force）
#   python <UCL_Core>/Tools~/AgentCommands/sync_lib_mirror.py --check    # 只報漂移，不寫檔（CI / pre-commit 用）
#   python <UCL_Core>/Tools~/AgentCommands/sync_lib_mirror.py --force    # 強制覆寫（放棄鏡像本機改動）
#
# Exit codes: 0 = 同步/檢查皆一致；1 = 錯誤；2 = 偵測到漂移（--check 模式）或有檔被跳過。

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

# 本工具在 UCL_Core 樹內，sibling import canonical 的 ucl_paths 拿 repo_root / ucl_core_dir。
sys.path.insert(0, str(Path(__file__).resolve().parent))     # 讓 `_lib` 這個 namespace package import 得到
from _lib.ucl_paths import repo_root, ucl_core_dir           # noqa: E402

# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：要同步的 canonical _lib 檔白名單。
# 物理意義：只有「兩棵樹都要用、且 canonical 在 UCL_Core」的 lib 檔列進來。
#          目前只有 ucl_paths.py（跨專案路徑解析）。日後有新共用 lib 再 append。
# 數值影響：白名單外的檔一律不碰（e.g. repo_root.py 是 AgentCommands 端 shim，非鏡像）。
# ─────────────────────────────────────────────────────────────────────────
# ⚠ 2026-08-18 Tim 拍板：`ucl_paths.py` 的鏡像**退場**，AgentCommands 端改用轉發 shim
#   （實作只留 UCL_Core canonical 一份）。清單清空 ⇒ 本工具目前無事可做，
#   但**保留不刪** —— 日後真有新的共用 lib 要鏡像時，機制與它踩過的坑都還在。
#   🩸 留著 "ucl_paths.py" 的話，下次有人跑同步會把 shim 蓋回舊鏡像，
#      而那次覆蓋不會有人發現（檔案看起來一樣正常）。
MIRRORS: list[str] = []

_HEADER_MARK = "# ╔═══ AUTO-SYNCED"       # 檔頭起始標記（判斷鏡像檔是否已帶頭）
_SHA_PREFIX = "# source_sha256: "          # 記 canonical body hash 的行前綴


def _sha256(text: str) -> str:
    # 對「canonical body 文字」算 SHA256（統一用 utf-8 + \n，避免 CRLF 差異誤判漂移）。
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()


def _build_header(source_rel: str, body_sha: str) -> str:
    # 組 AUTO-SYNCED 檔頭（純 Python 註解，import 時被忽略）。
    return (
        f"# ╔═══ AUTO-SYNCED — 別直接編輯本檔 ═══╗\n"
        f"# 本檔由 UCL_Core canonical 位元組同步而來 (T-PATH-RESOLVE T02)。\n"
        f"# source: {source_rel}\n"
        f"# 要改請改 UCL_Core 端 canonical，再跑 sync_lib_mirror.py 重新同步。\n"
        f"{_SHA_PREFIX}{body_sha}\n"
        f"# ╚═════════════════════════════════════╝\n"
    )


def _split_mirror(mirror_text: str) -> tuple[str | None, str]:
    # 把鏡像檔拆成 (檔頭記錄的 sha, header 之後的 body)。沒帶頭 → (None, 全文)。
    lines = mirror_text.splitlines(keepends=True)
    if not lines or not lines[0].startswith(_HEADER_MARK):
        return None, mirror_text
    recorded_sha = None
    body_start = 0
    for i, ln in enumerate(lines):
        if ln.startswith(_SHA_PREFIX):
            recorded_sha = ln[len(_SHA_PREFIX):].strip()
        if ln.startswith("# ╚"):               # 檔頭結束行
            body_start = i + 1
            break
    return recorded_sha, "".join(lines[body_start:])


def main() -> int:
    ap = argparse.ArgumentParser(description="Sync UCL_Core canonical _lib files to host AgentCommands/_lib")
    ap.add_argument("--check", action="store_true", help="只報漂移不寫檔（exit 2 表示有漂移）")
    ap.add_argument("--force", action="store_true", help="強制覆寫（放棄鏡像本機改動）")
    args = ap.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # Windows console UTF-8
    except Exception:
        pass

    core = ucl_core_dir()                                            # UCL_Core 根（本工具在其內，必得）
    canonical_dir = core / "Tools~" / "AgentCommands" / "_lib"       # canonical _lib 目錄
    mirror_dir = repo_root() / "AgentCommands" / "_lib"              # host 端鏡像目錄

    exit_code = 0
    for name in MIRRORS:
        src = canonical_dir / name                                  # canonical 來源
        dst = mirror_dir / name                                     # 鏡像目的
        if not src.exists():
            print(f"[err] canonical 不存在：{src}")
            exit_code = 1
            continue

        body = src.read_text(encoding="utf-8")                      # canonical body（原文）
        body_sha = _sha256(body)                                    # 其 SHA256
        source_rel = f"UCL_Core/Tools~/AgentCommands/_lib/{name}"   # 記進檔頭的來源標示

        if not dst.exists():
            # 鏡像不存在
            if args.check:
                print(f"[drift] 鏡像不存在：{dst}（需 sync）")
                exit_code = 2
            else:
                dst.parent.mkdir(parents=True, exist_ok=True)
                dst.write_text(_build_header(source_rel, body_sha) + body, encoding="utf-8")
                print(f"[synced] 新建鏡像：{dst}")
            continue

        recorded_sha, mirror_body = _split_mirror(dst.read_text(encoding="utf-8"))
        mirror_body_sha = _sha256(mirror_body)                      # 鏡像 body 實際 hash

        canonical_matches_record = (recorded_sha == body_sha)       # 檔頭記錄 vs canonical 現值
        mirror_body_intact = (mirror_body_sha == recorded_sha)      # 鏡像 body 有無被本機改過

        if canonical_matches_record and mirror_body_intact:
            print(f"[ok] {name} 同步一致（sha {body_sha[:12]}…）")
            continue

        # 有漂移：分兩種診斷
        if not mirror_body_intact:
            # 情境 A：鏡像 body 被本機手改（body hash 對不上檔頭記錄）
            print(f"[drift] {name} 鏡像 body 被本機編輯過（AUTO-SYNCED 檔不該手改！）")
            if args.check or not args.force:
                print(f"        → 跳過覆寫；確認要放棄本機改動請加 --force")
                exit_code = 2
                continue
        if not canonical_matches_record:
            # 情境 B：canonical 變了、鏡像過期（檔頭記錄 sha 對不上 canonical 現值）
            print(f"[drift] {name} canonical 已更新，鏡像過期（記錄 {str(recorded_sha)[:12]}… → 現值 {body_sha[:12]}…）")

        if args.check:
            exit_code = 2
            continue

        # 寫入（sync / --force）
        dst.write_text(_build_header(source_rel, body_sha) + body, encoding="utf-8")
        print(f"[synced] 更新鏡像：{dst}（sha {body_sha[:12]}…）")

    if args.check and exit_code == 2:
        print("\n⚠ 偵測到漂移 —— 跑 `sync_lib_mirror.py` 重新同步（或處理本機改動）。")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
