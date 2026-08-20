#!/usr/bin/env python3
"""
agent_email.py — agent 預設信箱 + persona override 的唯一解析點（python 端）。

# 區塊職責：回答「這個 persona 該用哪個信箱」，並說清楚那個值**從哪來**。
# 物理意義：預設表以 actual_agent（Codex / ClaudeCode / Antigravity）為 key —— 封閉集合，設一次不必再管；
#          顯示 agent（Sirius / Myth / 月讀大小姐…）是開放的，每多一位同事就多一格要填，漏填會靜默 fallback。
#          override 跟著 persona 檔走，因為它本來就是「這個人的」屬性。
# 數值影響：查不到回哨兵 `unset@invalid` 而不是空字串 —— 空字串在 trailer 裡長得像「還沒填」，
#          哨兵長得像「壞了」。**只有後者會被人看見。**

唯一設定入口是 Editor 的 UCL_PersonaAgentAdminPage；本檔預設只讀，不提供寫入 CLI。

用法:
  python agent_email.py resolve --persona basecamp          # 只印信箱（給 script 取用）
  python agent_email.py resolve --persona basecamp --verbose # 印來源與 actual_agent
  python agent_email.py list                                 # 全部 persona 一覽（含來源）
  python agent_email.py trailer --persona basecamp           # 印完整 Co-Authored-By 行

import 用法:
  from agent_email import resolve_email
  info = resolve_email("basecamp")   # {"email":..., "source":..., "actual_agent":...}
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

UNSET_SENTINEL = "unset@invalid"
# actual_agent 的封閉集合 —— 與 C# UCL_ActualAgent enum 對齊（None 不列）。
KNOWN_ACTUAL_AGENTS = ["Codex", "ClaudeCode", "Antigravity"]


# ⚠ 路徑一律委派 _lib/ucl_paths.py（Tim 2026-08-17 拍板）。
# 🩸 本檔原本自算 `<含 .git 的 ancestor>/AgentCommands`，**完全不看 .agentcommands_root.local
#   pointer 檔** —— 於是設了資料根 override 的機器上，本檔跟其他所有工具讀不同目錄，
#   而兩邊都不會報錯（信箱查不到就落哨兵 unset@invalid，看起來像「這人沒設信箱」）。
import importlib.util as _ilu_paths


def _load_ucl_paths():
    spec = _ilu_paths.spec_from_file_location(
        "_ucl_paths_for_agent_email", Path(__file__).resolve().parent / "_lib" / "ucl_paths.py")
    mod = _ilu_paths.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_paths = _load_ucl_paths()


_PP_MOD = None


def _persona_profile():
    """persona 讀取接縫（走 `_lib/seam` 共用 loader，全行程一份）。

    # 物理意義：接縫的 per-process 快取（`_STATE`）決定「這個行程發幾次 Cmd」，
    #          而那個快取住在**模組實例**上。
    # 🩸 BUG-17（2026-08-20 實測）：舊版**每次呼叫**都 `exec_module` 一份新模組
    #    （三次呼叫三個不同 id、`sys.modules` 裡零筆）⇒ 快取等於不存在
    #    ⇒ 不帶 `UCL_PP_SKIP_CMD` 時是「每次 `load_persona` 一趟 Cmd」。
    #    而 `load_persona` 在 `build_trailer` 裡是每位 persona 一次 ——
    #    症狀只是慢，慢會被歸因到「Editor 忙」，所以它不會叫。
    """
    global _PP_MOD
    if _PP_MOD is None:
        import importlib.util as _ilu
        from pathlib import Path as _P
        _spec = _ilu.spec_from_file_location(
            "_ucl_seam_loader_agent_email", _P(__file__).resolve().parent / "_lib" / "seam.py")
        _seam = _ilu.module_from_spec(_spec); _spec.loader.exec_module(_seam)
        _PP_MOD = _seam.persona_profile()
    return _PP_MOD


def _data_root() -> Path:
    return _paths.data_root()


def registry_path() -> Path:
    return _paths.awaken_init_dir() / "agent_emails.json"


# ⛔ `persona_path()` 已移除（2026-08-19）：本檔改走接縫之後它就沒有呼叫端了，
#   而留一支「直接指到 legacy 檔」的路徑組裝 = 邀請下一個人再走直讀那條路。
#   真的需要那個路徑請用 `_lib/ucl_paths.persona_file()`（唯一解析點）。


def load_registry() -> dict:
    """讀預設表；檔案缺 / 壞掉都回空表（fail-soft —— 解析仍走得完，只是落到哨兵）。"""
    try:
        return json.loads(registry_path().read_text(encoding="utf-8"))
    except Exception:
        return {}


# ═══════════════════════════════════════════════════════════════════
# 區塊職責：讀一位 persona 的欄位 —— **本檔（含 agent_model / git_commit / commit-msg hook）
#          唯一的 persona 讀取口**。
# 物理意義：原本這裡是 `json.loads(persona_path(persona).read_text())` —— 直讀
#          `AwakenInit/personas/<p>.json`，繞過 §8.7 的單端解析接縫。
#          🩸 退場案 Phase 1 之後那是**會給錯答案**的：identity 欄（含 `email`）的真相
#          已經在 `letters/<p>/profile/`，而 legacy 只出不進、永遠停在遷移那一刻的值。
#          實測（2026-08-19 kiara）：把 email 寫進新結構後 ——
#            profile/email.md → divergence-probe@test.invalid
#            C# 接縫／快照     → divergence-probe@test.invalid
#            **本函式（舊版）  → basecamp05122026@gmail.com（agent 預設，因為它讀 legacy 看不到）**
#          而這個值會被寫進 **commit trailer** ⇒ 錯的信箱進 git history，改不掉。
#          「寫入成功、讀出來是舊的、沒有任何一格會紅」—— 本案要殺的就是這個形狀。
# ⇒ 改走 `_lib/persona_profile.get_raw()`：Cmd → 快照 → local-parse 三段 fallback，
#   Editor 沒開也讀得到，而且**只有一個解析器**。
# ⚠ 不留「接縫失敗就退直讀」的後路：那條後路就是第二個解析器，
#   而它只在出事的時候才會跑 —— 沒人驗過的路配上最壞的時機。
#   接縫本身的第三段（local-parse）已經是那個後路，且回傳值自帶 `_source` 標記。
# 數值影響：回傳可能多帶 `_source` / `_snapshot_at` / `_field_sources` 等底線前綴推導欄
#          （非本體欄位）；本檔與下游一律用 `.get("<欄名>")` 取值，不受影響。
# ═══════════════════════════════════════════════════════════════════
def load_persona(persona: str) -> dict:
    try:
        return _persona_profile().get_raw(persona) or {}
    except Exception as e:
        print(f"⚠ [agent_email] persona '{persona}' 讀取失敗（接縫）：{e}", file=sys.stderr)
        return {}


def resolve_email(persona: str) -> dict:
    """persona.email → defaults[actual_agent] → fallback → 哨兵。回值一律含 source。"""
    p = load_persona(persona)
    actual_agent = (p.get("actual_agent") or "").strip()
    own = (p.get("email") or "").strip()
    if own:
        return {"email": own, "source": "persona-override", "actual_agent": actual_agent}

    reg = load_registry()
    defaults = reg.get("defaults") or {}
    by_agent = (defaults.get(actual_agent) or "").strip() if actual_agent else ""
    if by_agent:
        return {"email": by_agent, "source": "agent-default", "actual_agent": actual_agent}

    fallback = (reg.get("fallback") or "").strip()
    if fallback:
        return {"email": fallback, "source": "fallback", "actual_agent": actual_agent}
    return {"email": UNSET_SENTINEL, "source": "unset", "actual_agent": actual_agent}


def looks_like_email(value: str) -> bool:
    """粗篩：只擋明顯不是位址的東西（缺 @ / 多個 @ / 沒有網域點 / 含空白）。不做 RFC 級驗證。"""
    v = (value or "").strip()
    if not v or " " in v:
        return False
    if v.count("@") != 1:
        return False
    local, _, domain = v.partition("@")
    return bool(local) and "." in domain and not domain.startswith(".") and not domain.endswith(".")


def build_trailer(persona: str) -> str:
    """組 Co-Authored-By 行 —— 身分與型號取自 persona 檔，信箱走解析，全部不手打。"""
    p = load_persona(persona)
    agent = p.get("agent") or "?"
    model = p.get("model") or "?"
    info = resolve_email(persona)
    return f"Co-Authored-By: {agent}@{persona}({model}) <{info['email']}>"


def cmd_resolve(args) -> int:
    info = resolve_email(args.persona)
    if args.json:
        print(json.dumps(info, ensure_ascii=False))
    elif args.verbose:
        print(f"{info['email']}  (source={info['source']}, actual_agent={info['actual_agent'] or '?'})")
    else:
        print(info["email"])
    # 沒設定 / 不像位址 → 非零退出，讓 caller 有機會停下來而不是把哨兵寫進 commit
    if info["source"] in ("unset",) or not looks_like_email(info["email"]):
        print(f"WARN: {args.persona} 的信箱未設定或格式可疑（{info['email']}）—— "
              f"到 Editor 的 Persona & Agent 管理頁設定", file=sys.stderr)
        return 3
    return 0


def cmd_list(args) -> int:
    # persona 名單走 persona_profile 接縫（Phase 0）—— 不自己 glob
    rows = []
    for name in _persona_profile().pool_names():
        info = resolve_email(name)
        rows.append((name, info["actual_agent"] or "-", info["email"], info["source"]))
    if args.json:
        print(json.dumps([{"persona": r[0], "actual_agent": r[1], "email": r[2], "source": r[3]}
                          for r in rows], ensure_ascii=False, indent=2))
        return 0
    reg = load_registry()
    print("# agent 預設表（key = actual_agent）")
    for a in KNOWN_ACTUAL_AGENTS:
        v = (reg.get("defaults") or {}).get(a) or "(未設定)"
        print(f"  {a:<14} {v}")
    print(f"  {'(fallback)':<14} {reg.get('fallback') or '(未設定)'}")
    print()
    print(f"# persona 解析結果（{len(rows)} 位）")
    for name, agent, email, source in rows:
        mark = "⚠" if source in ("unset",) or not looks_like_email(email) else " "
        print(f" {mark} {name:<16} {agent:<14} {email:<34} {source}")
    return 0


def cmd_trailer(args) -> int:
    print(build_trailer(args.persona))
    info = resolve_email(args.persona)
    return 0 if looks_like_email(info["email"]) else 3


def main() -> int:
    ap = argparse.ArgumentParser(description="agent 預設信箱 + persona override 解析（唯一設定入口是 Editor 後台）")
    sub = ap.add_subparsers(dest="cmd", required=True)

    r = sub.add_parser("resolve", help="解析單一 persona 的信箱")
    r.add_argument("--persona", required=True)
    r.add_argument("--verbose", action="store_true")
    r.add_argument("--json", action="store_true")
    r.set_defaults(func=cmd_resolve)

    l = sub.add_parser("list", help="列出預設表與全部 persona 的解析結果")
    l.add_argument("--json", action="store_true")
    l.set_defaults(func=cmd_list)

    t = sub.add_parser("trailer", help="印 Co-Authored-By 行（身分/型號/信箱全部由檔案推導）")
    t.add_argument("--persona", required=True)
    t.set_defaults(func=cmd_trailer)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
