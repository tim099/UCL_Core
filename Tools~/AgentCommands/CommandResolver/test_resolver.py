"""T-LVC-007 smoke eval — 跑一批 case 看召回率 + latency.

跑法：python test_resolver.py
"""
from __future__ import annotations
import sys
import time
from resolver import Resolver, format_markdown, CONF_AUTO, CONF_PROMPT


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    # (input, expected_entry_id_substr_or_None, description)
    # expected = None → 預期 no_match
    # expected = "X" → 預期 top-1 entry_id 含 X
    cases = [
        # === 預期 substring exact ===
        ("進酒館", "tavern", "exact substring"),
        ("聊天酒館", "tavern", "exact substring"),
        ("commit", "commit", "exact substring english"),
        ("提交", "commit", "exact substring chinese"),
        ("幫我 commit 一下", "commit", "mixed CJK + en"),
        ("拯救 gemini", "antigravity", "rescue gemini"),
        ("CS0103", "compile", "compile error code"),
        ("editor 排查編譯", "compile", "exact compile word"),
        ("自言自語", "brainstorm", "solo brainstorm exact"),
        ("待機模式", "t34", "standby mode exact"),
        ("translate doc", "translate", "english translate"),
        ("更新文件", "update", "update docs exact"),
        ("install ucl skills", "ucl_skill", "install skill"),
        ("scriptable object", "asset", "create asset trigger"),

        # === 預期 vector fuzzy 命中 ===
        ("把酒館看一下", "tavern", "fuzzy: 把X看一下 → 進酒館"),
        ("新增一個 cmd", "agentcommand", "fuzzy: cmd → AgentCommand"),
        ("查 runtime 錯", "runtime", "fuzzy: 查 runtime"),

        # === 預期 no_match ===
        ("今天天氣真好", None, "no_match: weather"),
        ("我要吃披薩", None, "no_match: food"),
        ("hello world", None, "no_match: generic greeting"),
    ]

    resolver = Resolver()
    print(f"📊 Loaded {len(resolver.triggers)} entries / {len(resolver.vocab)} vocab grams\n")

    n_pass = 0
    n_fail = 0
    latencies = []

    for inp, expected_substr, desc in cases:
        t0 = time.time()
        r = resolver.resolve(inp)
        elapsed = (time.time() - t0) * 1000.0
        latencies.append(elapsed)

        # 評分
        if expected_substr is None:
            # 預期 no_match
            ok = r.mode == "no_match"
            top1 = "(no_match)"
        else:
            # 預期 命中
            if r.candidates:
                top = r.candidates[0]
                top1 = f"{top.entry_id} (conf={top.conf:.2f}, mode={r.mode})"
                ok = expected_substr.lower() in top.entry_id.lower() \
                     or expected_substr.lower() in top.display_name.lower() \
                     or expected_substr.lower() in (top.workflow_path or "").lower()
            else:
                top1 = "(no_match — but expected hit)"
                ok = False

        marker = "✅" if ok else "❌"
        if ok:
            n_pass += 1
        else:
            n_fail += 1
        print(f"  {marker} {desc:38s} | input='{inp}'")
        print(f"     → {top1} ({elapsed:.1f}ms)")

    # Latency stats
    latencies.sort()
    p50 = latencies[len(latencies) // 2]
    p95 = latencies[int(len(latencies) * 0.95)] if len(latencies) > 1 else latencies[0]
    avg = sum(latencies) / len(latencies)

    print(f"\n📈 Stats")
    print(f"  Pass:   {n_pass}/{len(cases)} ({100.0 * n_pass / len(cases):.0f}%)")
    print(f"  Fail:   {n_fail}/{len(cases)}")
    print(f"  Latency avg / p50 / p95: {avg:.1f}ms / {p50:.1f}ms / {p95:.1f}ms")
    print(f"  Latency target p95 < 50ms: {'PASS ✅' if p95 < 50.0 else 'FAIL ❌'}")
    print(f"  Recall target ≥ 70%: {'PASS ✅' if n_pass / len(cases) >= 0.70 else 'FAIL ❌'}")

    return 0 if n_pass / len(cases) >= 0.70 else 1


if __name__ == "__main__":
    sys.exit(main())
