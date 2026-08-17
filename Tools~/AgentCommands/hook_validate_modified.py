"""
Claude Code hook driver — auto-validate UCL_Asset edits.

兩種模式：

  --mode post    PostToolUse hook（best-effort，non-blocking）
                 從 stdin 讀 JSON payload → 解析 tool_input.file_path →
                 若路徑屬於 UCL_Asset JSON → 記到 state file + best-effort submit Cmd

  --mode stop    Stop hook（blocking）
                 讀 state file → 對每筆等待 wait → 任一 verdict ≠ PASS
                 或 reference_check == Missing → exit 2 block stop

設計原則：
  - PostToolUse 不能阻塞對話流程（Editor 沒開時也要靜默退出）
  - Stop 是真正的閘門 — 把 silent data loss 在 turn 結束前抓出來
  - 整個機制走 UCL_Core 內既有的 ValidateAssetFormat Cmd + run_cmd.py wrapper

匹配 pattern（任一上層專案都通用）：
  - {anything}/UCL_Assets/<TypeName>/<AssetID>.json
  - <TypeName> 取出當作 assetType 傳給 Cmd
  - <AssetID> 取出當作 assetId

跨專案使用：
  本腳本路徑相對 git root 是固定的（UCL_Core 是 submodule 所以有確定位置）。
  上層專案在自己的 .claude/settings.json 配置 hooks 指向此檔即可重用，
  不需要 fork / 複製腳本。
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import os
import time
from datetime import datetime, timezone
from pathlib import Path

# ===========================================================
# 路徑解析 — 跨專案通用（不假設 UCL_Core 放在哪一層）
# ===========================================================
# 上層專案可能把 UCL_Core 放在不同位置：
#   Emblem of Valor:    <gitRoot>/CardGame/Assets/UCL/UCL_Core/
#   別的 UCL_Game 專案: <gitRoot>/Assets/UCL/UCL_Core/
#   獨立工具專案:        <gitRoot>/UCL_Core/
# 因此不能用固定的 parents[N] 定位 git root，必須動態找。
#
# 解析優先序：
#   1. 環境變數 CLAUDE_PROJECT_DIR（Claude Code hook 執行時注入；最權威）
#   2. 從本檔位置往上找第一個含 .git 「資料夾」（不是 .git file，避開 submodule pointer）
#   3. fallback：用 parents[2]（UCL_Core 根）— 不太對但起碼讓 import 不爆


def _find_git_root_by_walk(start: Path) -> Path | None:
    """從 start 往上找第一個含 .git 為資料夾的目錄；submodule 的 .git 是檔案會被略過。"""
    p = start.resolve()
    while p != p.parent:
        git_path = p / ".git"
        if git_path.is_dir():  # 真實 repo 根；submodule 的 .git 是 file，不會匹配
            return p
        p = p.parent
    return None


# 區塊職責：依序嘗試三種解析方式定出 git root
# 物理意義：hook 一定要找到 git root 才能找到 AgentCommands/queue.json + .claude/state/
# 數值影響：找錯 root 會讓 state file / queue 寫到錯位置，整套機制失靈
def _ucl_paths():
    """lazy import 同目錄 _lib/ucl_paths —— python 端路徑解析的唯一擁有者（Tim 2026-08-17 定調）。"""
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_hook", Path(__file__).resolve().parent / "_lib" / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m


def _resolve_git_root() -> Path:
    return _ucl_paths().repo_root()


def _resolve_data_root() -> Path:
    return _ucl_paths().data_root()


GIT_ROOT = _resolve_git_root()

# run_cmd.py 一定與本檔同目錄（Tools~/AgentCommands/）
RUN_CMD = Path(__file__).resolve().parent / "run_cmd.py"
STATE_DIR = GIT_ROOT / ".claude" / "state"
STATE_FILE = STATE_DIR / "pending_validations.txt"

# 🩸 2026-08-17：本行原本是 `Path("CardGame") / "AgentCommands"` —— **寫死 EOV 的專案名**。
#   在扁平佈局的專案（Unity 專案就在 repo 根）底下，報告會被寫進一個不存在的
#   `<repo>/CardGame/AgentCommands/`，而寫檔會自動建目錄 ⇒ **憑空長出一個假資料夾，
#   而且不會報錯**，人去 AgentCommands/ 找報告只會找不到。
#   改走 data_root 的相對位置：資料落在哪由 ucl_paths 決定，本檔不假設專案叫什麼名字。
REPORT_DIR_REL = Path(_resolve_data_root().relative_to(GIT_ROOT)) \
    if _resolve_data_root().is_relative_to(GIT_ROOT) else Path("AgentCommands")

# ===========================================================
# Asset path matching
# ===========================================================
# 支援任何上層專案：路徑只要含 /UCL_Assets/<Type>/<Id>.json 即匹配
# 排除 .meta 檔
_ASSET_PATH_RE = re.compile(
    r"/UCL_Assets/(?P<type>[A-Za-z_][A-Za-z0-9_]*)/(?P<id>[A-Za-z_][A-Za-z0-9_]*)\.json$"
)


def parse_asset_from_path(file_path: str) -> tuple[str, str] | None:
    """檢查 file_path 是否屬於 UCL_Asset，是的話回 (assetType, assetId)。"""
    if not file_path:
        return None
    if file_path.endswith(".meta"):
        return None
    norm = file_path.replace("\\", "/")
    m = _ASSET_PATH_RE.search(norm)
    if not m:
        return None
    return m.group("type"), m.group("id")


# ===========================================================
# checkRefs 推導 — 根據 Type 給合理預設
# ===========================================================
# 引用越複雜的 asset 預設深度越大
_DEEP_REF_TYPES = {"RCG_StoryData", "RCG_QuestData", "RCG_BattleSet"}


def default_check_refs(asset_type: str) -> int:
    if asset_type in _DEEP_REF_TYPES:
        return 2
    return 1


# ===========================================================
# State file
# ===========================================================
def state_append(asset_type: str, asset_id: str, cmd_id: str | None) -> None:
    """記一筆待驗證 asset 到 state file（line: type|id|cmd_id|created_at）。"""
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    line = f"{asset_type}|{asset_id}|{cmd_id or ''}|{ts}\n"
    # 用 a+ 避免覆蓋並發寫入（不嚴謹但對單一 hook session 夠用）
    with open(STATE_FILE, "a", encoding="utf-8") as f:
        f.write(line)


def state_read() -> list[dict]:
    """回傳 state file 內全部 entry（去重後）。"""
    if not STATE_FILE.exists():
        return []
    seen = set()
    out = []
    with open(STATE_FILE, "r", encoding="utf-8") as f:
        for raw in f:
            raw = raw.strip()
            if not raw:
                continue
            parts = raw.split("|")
            if len(parts) < 2:
                continue
            asset_type = parts[0]
            asset_id = parts[1]
            cmd_id = parts[2] if len(parts) > 2 else ""
            key = f"{asset_type}|{asset_id}"
            if key in seen:
                continue
            seen.add(key)
            out.append({"type": asset_type, "id": asset_id, "cmd_id": cmd_id})
    return out


def state_clear() -> None:
    if STATE_FILE.exists():
        STATE_FILE.unlink()


# ===========================================================
# run_cmd.py wrapper helpers
# ===========================================================
def submit_validate(asset_type: str, asset_id: str) -> str | None:
    """非阻塞 submit ValidateAssetFormat；回傳 cmd_id（若可拿到），失敗回 None。"""
    check_refs = default_check_refs(asset_type)
    output_file = REPORT_DIR_REL / f"asset_format_check_{asset_type}_{asset_id}.md"
    cmd = [
        sys.executable, str(RUN_CMD), "submit", "ValidateAssetFormat",
        "--arg", f"assetType={asset_type}",
        "--arg", f"assetId={asset_id}",
        "--arg", f"checkRefs={check_refs}",
        "--arg", f"outputPath={output_file.as_posix()}",
        # ack-timeout 短一點 — submit 本身不等執行
        "--ack-timeout", "5",
    ]
    try:
        proc = subprocess.run(
            cmd, cwd=GIT_ROOT, capture_output=True, text=True, timeout=8,
            encoding="utf-8", errors="replace",  # 避免 Windows cp950 撞到非 ASCII (✓ ✗ 等)
        )
    except subprocess.TimeoutExpired:
        return None
    except Exception:
        return None
    # run_cmd.py submit 印 "Submitted: <cmd_id>" 在第一行
    for line in (proc.stdout or "").splitlines():
        if line.startswith("Submitted:"):
            return line.split(":", 1)[1].strip()
    return None


def wait_validate(cmd_id: str | None, asset_type: str, asset_id: str, timeout: int = 60) -> tuple[int, str]:
    """
    等待指定 cmd_id 跑完，或當 cmd_id 為空時重新 submit + wait。
    回傳 (exit_code, summary_line)。
    """
    output_file = REPORT_DIR_REL / f"asset_format_check_{asset_type}_{asset_id}.md"
    if cmd_id:
        cmd = [
            sys.executable, str(RUN_CMD), "wait", cmd_id,
            "--output-file", str(output_file),
            "--timeout", str(timeout),
            "--poll-interval", "1",
        ]
    else:
        # 沒拿到 cmd_id（可能 submit 失敗或被中斷）→ 重新 run（含 ensure_idle）
        check_refs = default_check_refs(asset_type)
        cmd = [
            sys.executable, str(RUN_CMD), "run", "ValidateAssetFormat",
            "--arg", f"assetType={asset_type}",
            "--arg", f"assetId={asset_id}",
            "--arg", f"checkRefs={check_refs}",
            "--arg", f"outputPath={output_file.as_posix()}",
            "--output-file", str(output_file),
            "--timeout", str(timeout),
            "--ack-timeout", "30",
        ]
    try:
        proc = subprocess.run(
            cmd, cwd=GIT_ROOT, capture_output=True, text=True, timeout=timeout + 30,
            encoding="utf-8", errors="replace",
        )
    except subprocess.TimeoutExpired:
        return 3, f"{asset_type}/{asset_id}: TIMEOUT"
    last_line = (proc.stdout or "").strip().splitlines()
    summary = last_line[-1] if last_line else f"{asset_type}/{asset_id}: (no output)"
    return proc.returncode, summary


def parse_report_verdict(asset_type: str, asset_id: str) -> dict:
    """
    讀 markdown report 的 frontmatter，回傳 {verdict, reference_check, ...}。
    找不到回空 dict。
    """
    report = GIT_ROOT / REPORT_DIR_REL / f"asset_format_check_{asset_type}_{asset_id}.md"
    if not report.exists():
        return {}
    out = {}
    in_fm = False
    try:
        with open(report, "r", encoding="utf-8") as f:
            for line in f:
                line = line.rstrip("\n")
                if line.strip() == "---":
                    if not in_fm:
                        in_fm = True
                        continue
                    break
                if in_fm and ":" in line:
                    k, _, v = line.partition(":")
                    out[k.strip()] = v.strip()
    except Exception:
        pass
    return out


# ===========================================================
# 主流程：PostToolUse
# ===========================================================
def run_post() -> int:
    """從 stdin 讀 hook payload；若 file_path 屬於 UCL_Asset → 記 state + submit。"""
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0  # 沒 payload 就靜默退

    file_path = (payload.get("tool_input") or {}).get("file_path") or ""
    parsed = parse_asset_from_path(file_path)
    if parsed is None:
        return 0  # 不關我們事
    asset_type, asset_id = parsed

    cmd_id = submit_validate(asset_type, asset_id)
    state_append(asset_type, asset_id, cmd_id)
    sys.stderr.write(
        f"[validate] queued {asset_type}/{asset_id}"
        + (f" (cmd_id={cmd_id})" if cmd_id else " (submit returned no id; will re-run on Stop)")
        + "\n"
    )
    return 0


# ===========================================================
# 主流程：Stop
# ===========================================================
def run_stop() -> int:
    """讀 state file → 等所有 cmd 完成 → 任一未過 → exit 2 block。"""
    pending = state_read()
    if not pending:
        return 0

    failures = []
    for entry in pending:
        rc, summary = wait_validate(entry.get("cmd_id") or None, entry["type"], entry["id"], timeout=90)
        verdict_info = parse_report_verdict(entry["type"], entry["id"])
        verdict = verdict_info.get("verdict", "?")
        ref_check = verdict_info.get("reference_check", "?")
        ok = (verdict == "PASS" and ref_check in ("OK", "Skipped"))

        sys.stderr.write(
            f"[validate] {entry['type']}/{entry['id']} "
            f"verdict={verdict} reference_check={ref_check} "
            + ("✓\n" if ok else "✗\n")
        )
        if not ok:
            report_rel = REPORT_DIR_REL / f"asset_format_check_{entry['type']}_{entry['id']}.md"
            failures.append({
                "asset": f"{entry['type']}/{entry['id']}",
                "verdict": verdict,
                "reference_check": ref_check,
                "report": str(report_rel),
                "summary": summary,
            })

    if failures:
        # 只有失敗時才阻塞，且要把詳細錯誤連同 report 路徑回給 model（透過 stderr）
        msg_lines = [
            "Asset validation failed before turn end. The following UCL_Assets did not pass round-trip validation:",
            "",
        ]
        for f in failures:
            msg_lines.append(f"  ✗ {f['asset']}")
            msg_lines.append(f"      verdict: {f['verdict']}, reference_check: {f['reference_check']}")
            msg_lines.append(f"      report:  {f['report']}")
        msg_lines += [
            "",
            "Read each report for the diff + captured errors, then patch the source file (or the .fixed.json sibling).",
            "After fixing, the Stop hook will re-validate automatically next turn.",
            "",
            "If you need to bypass: temporarily disable hooks via /hooks, or remove .claude/state/pending_validations.txt manually.",
        ]
        sys.stderr.write("\n".join(msg_lines) + "\n")
        # 不清 state：保留供下次 Stop 重新驗證（避免使用者修一次後仍要重 submit）
        return 2

    # 全 PASS → 清 state file
    state_clear()
    return 0


# ===========================================================
# CLI
# ===========================================================
def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mode", required=True, choices=["post", "stop"])
    args = parser.parse_args()

    if args.mode == "post":
        return run_post()
    if args.mode == "stop":
        return run_stop()
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")
    sys.exit(main())
