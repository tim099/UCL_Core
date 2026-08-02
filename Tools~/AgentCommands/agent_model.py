#!/usr/bin/env python3
"""
agent_model.py — persona 的「型號」解析：填成 agent 名時自動翻成該 agent 的預設型號。

# 區塊職責：回答「這個 persona 的 trailer 該印什麼型號」，並說清楚那個值是原樣還是被翻譯過的。
# 物理意義：實測發現**提示反而讓人填錯** —— apex-one 的 system prompt 第一句是 "You are Antigravity"
#          所以他填 Antigravity；kaguya 填 Codex。兩人都是誠實作答，錯的是我們要求他們回答一個
#          他們讀起來意思不同的問題。所以不再靠提示，改在底層辨識並翻譯（Tim 2026-08-03 拍板）。
# 數值影響：辨識**無視大小寫、空白、連字號、底線**（claude-code / ClaudeCode / CLAUDE_CODE 同一個東西）。
#          翻不出來時**保留原值**而不是清空 —— 原值至少是某人真的寫下的資訊，空白什麼都不是。

用法:
  python agent_model.py resolve --persona basecamp [--verbose]
  python agent_model.py list

import 用法:
  from agent_model import resolve_model
  info = resolve_model("apex-one")   # {"model":..., "raw":..., "source":..., "agent_key":...}
"""

from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

from agent_email import _data_root, load_persona  # noqa: E402

# actual_agent 正規名（與 C# UCL_ActualAgent enum 對齊）
CANONICAL_AGENTS = ["Codex", "ClaudeCode", "Antigravity"]

# 已知會被填進 model 欄的 agent 別名 → 正規 actual_agent。
# 這裡收的是**人真的會寫出來的字**，不是理論上的正確值 —— 清單長一點沒關係，漏一個就翻不出來。
AGENT_ALIASES = {
    "codex": "Codex",
    "openai": "Codex",
    "chatgpt": "Codex",
    "claudecode": "ClaudeCode",
    "claude": None,          # 有歧義：可能是型號(Claude) 也可能是 agent —— 一律當型號，不翻
    "anthropic": "ClaudeCode",
    "antigravity": "Antigravity",
    "gemini": None,          # 同上：Gemini 是型號名
}


def _norm(value: str) -> str:
    """辨識用正規化 —— 無視大小寫、空白、連字號、底線。"""
    return "".join(ch for ch in (value or "").lower() if ch.isalnum())


def registry_path() -> Path:
    return _data_root() / "AwakenInit" / "agent_models.json"


def load_registry() -> dict:
    try:
        return json.loads(registry_path().read_text(encoding="utf-8"))
    except Exception:
        return {}


def load_agent_names() -> dict:
    """顯示 agent 名 → 該 agent 底下 persona 的 actual_agent（多數決）。

    # 物理意義：有人會把顯示 agent 名（Sirius / Myth / 月讀大小姐）填進 model 欄。那些名字不在
    #          alias 表裡，但它們**確實是 agent 名而不是型號**，所以也該被辨識出來。
    # 數值影響：對應關係由現存 persona 檔推導，不另建一張要維護的表；推不出來就不辨識（保留原值）。
    """
    out = {}
    d = _data_root() / "AwakenInit" / "personas"
    if not d.is_dir():
        return out
    tally = {}
    for f in d.glob("*.json"):
        try:
            p = json.loads(f.read_text(encoding="utf-8"))
        except Exception:
            continue
        agent = _norm(p.get("agent") or "")
        actual = (p.get("actual_agent") or "").strip()
        if not agent or not actual:
            continue
        tally.setdefault(agent, {}).setdefault(actual, 0)
        tally[agent][actual] += 1
    for agent, counts in tally.items():
        out[agent] = max(counts.items(), key=lambda kv: kv[1])[0]
    return out


def identify_agent(value: str, fallback_actual_agent: str = "") -> str:
    """這個字串是不是 agent 名？是的話回正規 actual_agent，不是回空字串。"""
    n = _norm(value)
    if not n:
        return ""
    for canonical in CANONICAL_AGENTS:
        if n == _norm(canonical):
            return canonical
    if n in AGENT_ALIASES:
        mapped = AGENT_ALIASES[n]
        return mapped or ""          # None = 有歧義，當型號處理
    names = load_agent_names()
    if n in names:
        return names[n]
    # 認得出「是個 agent 名」但推不出是哪一個 → 用該 persona 自己的 actual_agent 兜
    return fallback_actual_agent if n in {_norm(k) for k in names} else ""


def resolve_model(persona: str) -> dict:
    """persona.model → 若那是 agent 名則翻成該 agent 預設型號；翻不出來保留原值。"""
    p = load_persona(persona)
    raw = (p.get("model") or "").strip()
    actual_agent = (p.get("actual_agent") or "").strip()
    if not raw:
        return {"model": "?", "raw": raw, "source": "empty", "agent_key": actual_agent}

    agent_key = identify_agent(raw, actual_agent)
    if not agent_key:
        return {"model": raw, "raw": raw, "source": "as-written", "agent_key": actual_agent}

    models = (load_registry().get("models") or {})
    mapped = (models.get(agent_key) or "").strip()
    if mapped:
        return {"model": mapped, "raw": raw, "source": "agent-translated", "agent_key": agent_key}
    # 認得出是 agent 名、但後台還沒設該 agent 的預設型號 → 保留原值（別把資訊擦掉）
    return {"model": raw, "raw": raw, "source": "agent-unmapped", "agent_key": agent_key}


def cmd_resolve(args) -> int:
    info = resolve_model(args.persona)
    if args.json:
        print(json.dumps(info, ensure_ascii=False))
    elif args.verbose:
        print(f"{info['model']}  (raw={info['raw'] or '(空)'}, source={info['source']}, agent={info['agent_key'] or '?'})")
    else:
        print(info["model"])
    return 0


def cmd_list(args) -> int:
    reg = load_registry()
    print("# agent 預設型號（key = actual_agent）")
    for a in CANONICAL_AGENTS:
        print(f"  {a:<14} {(reg.get('models') or {}).get(a) or '(未設定)'}")
    print()
    d = _data_root() / "AwakenInit" / "personas"
    print("# persona 型號解析")
    for f in sorted(d.glob("*.json")) if d.is_dir() else []:
        info = resolve_model(f.stem)
        mark = "→" if info["source"] == "agent-translated" else " "
        print(f" {mark} {f.stem:<22} raw={info['raw'] or '(空)':<16} → {info['model']:<20} {info['source']}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="persona 型號解析（填成 agent 名會自動翻譯）")
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("resolve"); r.add_argument("--persona", required=True)
    r.add_argument("--verbose", action="store_true"); r.add_argument("--json", action="store_true")
    r.set_defaults(func=cmd_resolve)
    l = sub.add_parser("list"); l.set_defaults(func=cmd_list)
    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
