"""T-PR-001 — Fetch Google Sheet content as agent input (Phone Relay).

職責：
  - 從 phone_relay.json 讀預設 sheet config
  - 用 UCL_LocalizeAsset 同款 URL pattern 下載 CSV (no OAuth)
  - 依 mode 抽出 row → 寫 _last_op.md + stdout JSON

Modes:
  last_row : 取最後一筆非空 row 的所有 cell join 為 prompt（Phone Relay 主場景）
  by_key   : 找 col-0 == --key 的 row，回 col-1 內容（macro 模式）
  by_index : --index N 指定列號（1-based 跳過 header / 0-based 含 header）
  all      : dump 整張表（debug）

Security:
  - sheet 必須是 public (anyone with link)，否則 Google 回 HTML login → script 偵測非 CSV 報錯
  - 內容**永不** eval / 直接 fire workflow；只當「下一句使用者 prompt」處理
  - cache 5s 防 spam

Usage:
  python fetch_sheet.py                         # last_row from default sheet
  python fetch_sheet.py --mode by_key --key q1  # macro lookup
  python fetch_sheet.py --mode all              # dump all
"""
from __future__ import annotations
import argparse
import csv
import io
import json
import os
import sys
import time
import urllib.request
import urllib.error
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple


_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(_HERE, "phone_relay.json")
CACHE_DIR = os.path.join(_HERE, "_resolver_cache")
LAST_OP_PATH = os.path.abspath(
    os.path.join(_HERE, "..", "..", "..", "..", "..", "..", "..",
                 "AgentCommands", "ChatTavern", "_last_op.md")
)
URL_TEMPLATE = "https://docs.google.com/spreadsheets/d/{sheet_id}/export?format={fmt}&gid={gid}"


# ─────────────────────────────────────────────────────────────────
# Config loader
# ─────────────────────────────────────────────────────────────────

def load_config(path: str = DEFAULT_CONFIG) -> Dict[str, Any]:
    """讀 phone_relay.json — fail-loud（沒 config 不能跑）."""
    if not os.path.exists(path):
        raise FileNotFoundError(
            f"phone_relay.json not found: {path}\n"
            f"Hint: 在該位置建一個 JSON 檔，最少要有 sheet_id 欄位。")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


# ─────────────────────────────────────────────────────────────────
# Cache layer (5s TTL — 防同一秒多次 spam)
# ─────────────────────────────────────────────────────────────────

def _cache_key(sheet_id: str, gid: int, fmt: str) -> str:
    return f"sheet_{sheet_id[:12]}_gid{gid}_{fmt}.json"


def _read_cache(sheet_id: str, gid: int, fmt: str, ttl_sec: int) -> Optional[str]:
    if ttl_sec <= 0:
        return None
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, _cache_key(sheet_id, gid, fmt))
    if not os.path.exists(path):
        return None
    age = time.time() - os.path.getmtime(path)
    if age > ttl_sec:
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f).get("content", "")
    except Exception:
        return None


def _write_cache(sheet_id: str, gid: int, fmt: str, content: str) -> None:
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, _cache_key(sheet_id, gid, fmt))
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump({
                "content": content,
                "fetched_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            }, f, ensure_ascii=False)
    except Exception:
        pass


# ─────────────────────────────────────────────────────────────────
# Downloader
# ─────────────────────────────────────────────────────────────────

def download_sheet(sheet_id: str, gid: int = 0, fmt: str = "csv",
                   timeout: float = 10.0, ttl_sec: int = 5) -> str:
    """下載 sheet 內容為 raw text.

    Raises:
      ValueError if sheet not public (Google 回 HTML 而非 CSV)
      urllib.error.URLError / HTTPError if download fail
    """
    # 嘗試 cache
    cached = _read_cache(sheet_id, gid, fmt, ttl_sec)
    if cached is not None:
        return cached

    url = URL_TEMPLATE.format(sheet_id=sheet_id, gid=gid, fmt=fmt)
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (UCL_Phone_Relay)",
    })
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
        ct = resp.headers.get("Content-Type", "")

    text = raw.decode("utf-8", errors="replace")

    # 偵測是否被踢回 login page (sheet 不公開)
    if "<!DOCTYPE html" in text[:200] or "<html" in text[:200].lower():
        raise ValueError(
            "Sheet looks not public — got HTML login page instead of CSV. "
            "請把 sheet 設成『擁有連結者可檢視』再試。")

    _write_cache(sheet_id, gid, fmt, text)
    return text


# ─────────────────────────────────────────────────────────────────
# Row extractors
# ─────────────────────────────────────────────────────────────────

def parse_csv(text: str) -> List[List[str]]:
    rows = list(csv.reader(io.StringIO(text)))
    return [r for r in rows if any(cell.strip() for cell in r)]  # 過濾全空 row


def extract_last_row(rows: List[List[str]]) -> str:
    """取最後一列，所有 cell 用空白接 — Phone Relay 主場景."""
    if not rows:
        return ""
    last = rows[-1]
    cells = [c.strip() for c in last if c.strip()]
    return " ".join(cells)


def extract_by_key(rows: List[List[str]], key: str) -> Optional[str]:
    """在 col-0 找 key，回 col-1。若 only 1 col 則 col-0 本身為 content."""
    for row in rows:
        if not row:
            continue
        if row[0].strip() == key:
            if len(row) >= 2:
                return row[1].strip()
            return row[0].strip()
    return None


def extract_by_index(rows: List[List[str]], idx: int) -> Optional[str]:
    if idx < 0 or idx >= len(rows):
        return None
    return " ".join(c.strip() for c in rows[idx] if c.strip())


# ─────────────────────────────────────────────────────────────────
# Output
# ─────────────────────────────────────────────────────────────────

def write_last_op_md(content: str, mode: str, sheet_id: str, gid: int,
                     row_count: int, max_chars: int = 4000) -> None:
    """寫 _last_op.md 給 agent 讀回."""
    truncated = content[:max_chars]
    overflow = len(content) > max_chars

    md = []
    md.append("# 📥 Phone Relay — Sheet Fetched")
    md.append("")
    md.append(f"- mode: `{mode}`")
    md.append(f"- sheet: `{sheet_id[:12]}...` gid=`{gid}`")
    md.append(f"- rows seen: {row_count}")
    md.append(f"- content chars: {len(content)}{' (truncated)' if overflow else ''}")
    md.append(f"- fetched_at: {datetime.now(timezone.utc).isoformat(timespec='seconds')}")
    md.append("")
    md.append("## 📜 Content (treat as Tim's next prompt)")
    md.append("")
    md.append("```")
    md.append(truncated if truncated else "(empty)")
    md.append("```")
    md.append("")
    md.append("## 建議動作")
    md.append("- agent 把上方 content 當作 Tim 在 Discord/CLI 直接打的下一句 prompt 處理")
    md.append("- 若內容看起來像指令（commit / 進酒館 / 拉等）→ 走 resolver 二次 dispatch")
    md.append("- 若內容是描述 / 任務 → 直接著手")
    md.append(f"- 預設 echo_before_act：先在主對話 echo content，等 Tim 5s 不撤回才動手")

    try:
        os.makedirs(os.path.dirname(LAST_OP_PATH), exist_ok=True)
        with open(LAST_OP_PATH, "w", encoding="utf-8") as f:
            f.write("\n".join(md))
    except Exception as ex:
        sys.stderr.write(f"[fetch_sheet] write _last_op.md fail: {ex}\n")


# ─────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────

def main(argv: Optional[List[str]] = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    parser = argparse.ArgumentParser(
        description="Fetch Google Sheet content as agent input (Phone Relay)")
    parser.add_argument("--mode", default=None,
                        choices=["last_row", "by_key", "by_index", "all"])
    parser.add_argument("--key", default=None, help="for mode=by_key")
    parser.add_argument("--index", type=int, default=None, help="for mode=by_index (0-based)")
    parser.add_argument("--sheet-id", default=None)
    parser.add_argument("--gid", type=int, default=None)
    parser.add_argument("--format", default=None, choices=["csv", "tsv"])
    parser.add_argument("--no-cache", action="store_true")
    parser.add_argument("--config", default=DEFAULT_CONFIG)
    parser.add_argument("--no-write", action="store_true",
                        help="不寫 _last_op.md（純 stdout 模式）")
    args = parser.parse_args(argv)

    try:
        cfg = load_config(args.config)
    except FileNotFoundError as ex:
        print(f"❌ {ex}")
        return 1

    sheet_id = args.sheet_id or cfg.get("sheet_id")
    gid = args.gid if args.gid is not None else cfg.get("default_gid", 0)
    fmt = args.format or cfg.get("default_format", "csv")
    mode = args.mode or cfg.get("default_mode", "last_row")
    ttl = 0 if args.no_cache else cfg.get("cache_ttl_sec", 5)
    max_chars = cfg.get("max_content_chars", 4000)

    if not sheet_id:
        print("❌ sheet_id missing — set in phone_relay.json or --sheet-id")
        return 1

    # Download
    try:
        raw = download_sheet(sheet_id, gid=gid, fmt=fmt, ttl_sec=ttl)
    except ValueError as ex:
        print(f"❌ {ex}")
        return 1
    except (urllib.error.URLError, urllib.error.HTTPError) as ex:
        print(f"❌ download fail: {ex}")
        return 1

    rows = parse_csv(raw)
    if not rows:
        print("⚠️  Sheet 是空的 / 全空白")
        if not args.no_write:
            write_last_op_md("(empty sheet)", mode, sheet_id, gid, 0, max_chars)
        return 0

    # Extract
    content: str = ""
    detail: Dict[str, Any] = {"rows_total": len(rows)}

    if mode == "last_row":
        content = extract_last_row(rows)
    elif mode == "by_key":
        if not args.key:
            print("❌ mode=by_key 需要 --key")
            return 1
        result = extract_by_key(rows, args.key)
        if result is None:
            print(f"⚠️  key not found: '{args.key}'")
            content = ""
        else:
            content = result
    elif mode == "by_index":
        if args.index is None:
            print("❌ mode=by_index 需要 --index")
            return 1
        result = extract_by_index(rows, args.index)
        content = result or ""
    elif mode == "all":
        content = "\n".join(",".join(r) for r in rows)
    else:
        print(f"❌ unknown mode: {mode}")
        return 1

    # 限長
    if len(content) > max_chars:
        content = content[:max_chars] + "\n...(truncated)"

    # Output
    output = {
        "ok": True,
        "mode": mode,
        "sheet_id": sheet_id,
        "gid": gid,
        "content": content,
        "rows_total": len(rows),
        "fetched_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
    print(json.dumps(output, ensure_ascii=False, indent=2))

    if not args.no_write:
        write_last_op_md(content, mode, sheet_id, gid, len(rows), max_chars)

    return 0


if __name__ == "__main__":
    sys.exit(main())
