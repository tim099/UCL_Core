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


def _norm(value: str) -> str:
    """辨識用正規化 —— 無視大小寫、空白、連字號、底線。"""
    return "".join(ch for ch in (value or "").lower() if ch.isalnum())


def _persona_profile():
    import importlib.util as _ilu
    from pathlib import Path as _P
    _spec = _ilu.spec_from_file_location(
        "_ucl_persona_profile_agent_model", _P(__file__).resolve().parent / "_lib" / "persona_profile.py")
    _m = _ilu.module_from_spec(_spec); _spec.loader.exec_module(_m)
    return _m


def registry_path() -> Path:
    return _ucl_paths_mod().awaken_init_dir() / "agent_models.json"


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
    tally = {}
    # persona 內容走 persona_profile 接縫（Phase 0）—— 不自己 glob＋parse
    for _name, p in _persona_profile().iter_raw():
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


def load_vendors() -> dict:
    """actual_agent → 廠牌名（Codex→GPT / ClaudeCode→Claude / Antigravity→Gemini）。

    # 物理意義：vendor 是**可驗的必填身分** —— 由 actual_agent 推導，不靠人填；
    #          version 才是「知道就寫、不知道就留白」的那一半（meadow 2026-08-03：
    #          「少一段版本不是資料不完整，而是明確保留『此刻不知道』的事實」）。
    """
    return (load_registry().get("vendors") or {})


def format_trailer_model(persona: str) -> dict:
    """trailer 的 (<vendor> / <version>) 字串。回 {"text":..., "vendor":..., "version":..., "source":...}

    規則（2026-08-03 三票拍板）：
      - vendor 由 actual_agent 推導；**推不出來就整段沿用 persona.model 原值**，不印假精確的 `?`
      - version 取 persona.model（經 agent 名翻譯後的值）
      - version 與 vendor 相同 → 只印 vendor（那代表這人只知道廠牌，沒有版本）
      - **不剝 version 開頭的 vendor 前綴**：`GPT-5.6 Luna` 照印成 `GPT / GPT-5.6 Luna`。
        冗餘只是難看，剝字串是猜測 —— 兩位同事都選了難看那個。
    """
    info = resolve_model(persona)
    raw = info["model"]
    actual_agent = (load_persona(persona).get("actual_agent") or "").strip()
    vendor = (load_vendors().get(actual_agent) or "").strip() if actual_agent else ""
    if not vendor:
        return {"text": raw, "vendor": "", "version": raw, "source": "no-vendor:" + info["source"]}
    if _norm(raw) == _norm(vendor) or not raw or raw == "?":
        return {"text": vendor, "vendor": vendor, "version": "", "source": "vendor-only"}
    return {"text": f"{vendor} / {raw}", "vendor": vendor, "version": raw, "source": "vendor+version"}


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
    print("# agent 廠牌（key = actual_agent）")
    for a in CANONICAL_AGENTS:
        print(f"  {a:<14} {(reg.get('vendors') or {}).get(a) or '(未設定)'}")
    print()
    print("# persona trailer 型號欄")
    for name in _persona_profile().pool_names():
        t = format_trailer_model(name)
        raw = resolve_model(name)["raw"] or "(空)"
        print(f"   {name:<22} raw={raw:<20} → ({t['text']})   {t['source']}")
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
