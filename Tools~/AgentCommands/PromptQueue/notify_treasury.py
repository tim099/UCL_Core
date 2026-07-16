#!/usr/bin/env python3
"""notify_treasury.py — T41: Treasury ledger entry → Discord 記帳頻道 broadcast.

從 stdin 或 --entry-file 讀一筆 ledger entry JSON → POST embed 到 treasury_mirror webhook。

Usage:
    # 直接 spawn from C# UCL_TreasuryLedger.WriteEntry：
    python notify_treasury.py --entry-file <path-to-ledger-entry.json>

    # 或 stdin：
    cat entry.json | python notify_treasury.py --stdin

設計：
- 走 _lib.discord_webhook.WebhookClient（T36 ship 的 SDK）
- Embed 格式：色塊（credit 綠 / debit 紅 / mismatch 黃）+ 變動金額 + 原因 + 餘額（before→after）
- 失敗 silent skip + log warning（不擋 ledger 主流程）

Tim 拍板的必要欄位：
- 變動金額（credit/debit + amount）
- 原因（source_kind + ref + description）
- 總額（balance_after）
- 自動補：account / agent_signature / ts
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# T36 _lib import
_HERE = Path(__file__).resolve().parent
def _probe_repo_root(iStart):
    p = iStart
    for _ in range(12):
        if (p / "AgentCommands" / "_lib" / "tavern_paths.py").exists():
            return p
        if p.parent == p:
            break
        p = p.parent
    return Path.cwd()
# T-MOVE 2026-07-15: 本檔已搬 UCL_Core Tools~ — repo root 改探測（見 notify_discord.py 同款）
_REPO_ROOT = _probe_repo_root(_HERE)
if str(_REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(_REPO_ROOT))
from AgentCommands._lib import discord_webhook as _dw  # noqa: E402
from AgentCommands._lib import tavern_paths as _tp     # noqa: E402

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ---------------------------------------------------------------------------
# Config + WebhookClient
# ---------------------------------------------------------------------------

def _load_config() -> dict:
    cfg_path = _tp.NOTIFY_CONFIG_PATH
    if not cfg_path.is_file():
        return {}
    try:
        return json.loads(cfg_path.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"[notify_treasury] config load fail: {e}", file=sys.stderr)
        return {}


def _build_client() -> _dw.WebhookClient | None:
    cfg = _load_config()
    tm = cfg.get("treasury_mirror", {}) or {}
    if not tm.get("enabled"):
        print("[notify_treasury] disabled in config", file=sys.stderr)
        return None
    return _dw.from_config_block(tm, label="treasury", webhook_dir=_tp.PROMPT_QUEUE_DIR)


# ---------------------------------------------------------------------------
# Embed builder
# ---------------------------------------------------------------------------

# Discord embed colors (decimal)
_COLOR_CREDIT = 0x57F287   # green — 進帳
_COLOR_DEBIT  = 0xED4245   # red — 出帳
_COLOR_WARN   = 0xFEE75C   # yellow — signature_mismatch


def _resolve_balances(entry: dict):
    """回 (balance_before, balance_after) — 顯示前的「權威路徑」。

    區塊職責: entry 的 balance 欄若為 null (filesystem 直寫沒算), 不顯示 None, 改 event-source 重放算。
    物理意義: 委派 UCL_Core treasury_ledger.compute_balance (單一真相, 跨專案 canonical);
              載入失敗才退用存的欄位 (頂多顯示 None, 不影響真實金額)。
    數值影響: 純讀全 ledger 累加; 天生繞過「同 bank 不同 persona」併發 race (永遠重放, 不信存的 cache)。
    """
    bb = entry.get("balance_before")
    ba = entry.get("balance_after")
    if bb is not None and ba is not None:
        return bb, ba
    account = entry.get("account_id")
    etype = entry.get("type")
    if not account or etype not in ("credit", "debit"):
        return bb, ba
    try:
        import importlib.util as _ilu
        # T-PATH-02: canonical UCL_Core lib 走 layout-agnostic resolver (不再寫死 CardGame/Assets/UCL/UCL_Core)
        canonical = _tp.UCL_LIB_DIR / "treasury_ledger.py"
        # P1 fix (summit QA 2026-07-16): 搬家後 __file__.parents[2] = UCL_Core/Tools~ — 舊假設迴歸。
        # 改走 _tp resolver（DataRoot-aware，同 notify_discord.py 的 ledger 定位標準）。
        ledger_root = _tp.AGENT_COMMANDS_DIR / "Treasury" / "ledger"
        if not canonical.exists():
            return bb, ba
        spec = _ilu.spec_from_file_location("_ucl_treasury_ledger_notify", canonical)
        mod = _ilu.module_from_spec(spec)
        spec.loader.exec_module(mod)
        currency = entry.get("currency", "tavern_token")
        amount = entry.get("amount", 0)
        # ts-aware running balance (非全帳-自己; 追溯顯示舊筆也對)
        before = mod.running_balance_before(ledger_root, account, currency,
                                            entry.get("ts", ""), entry.get("uuid", ""))
        after = before + amount if etype == "credit" else before - amount
        return before, after
    except Exception:
        return bb, ba


def _build_embed(entry: dict) -> dict:
    """
    回 Discord embed dict — Tim 必要欄位（變動金額 / 原因 / 總額）+ 自動補：
      - title: ↑ 進帳 N tavern_token / ↓ 出帳 N tavern_token
      - color: green/red/yellow
      - fields: account / source_kind / source_ref / balance_before/after / actor_signature
      - footer: ts + uuid
      - description: source_description + signature_mismatch warning（如有）
    """
    entry_type = entry.get("type", "?")
    amount = entry.get("amount", 0)
    currency = entry.get("currency", "tavern_token")
    account = entry.get("account_id", "?")
    src_kind = entry.get("source_kind", "?")
    src_ref = entry.get("source_ref", "")
    src_desc = entry.get("source_description", "")
    balance_before, balance_after = _resolve_balances(entry)
    sig_claimed = entry.get("sig_agent_id_claimed", "?")
    sig_env = entry.get("sig_env_marker", "?")
    sig_mismatch = entry.get("signature_mismatch", False)
    ts = entry.get("ts", "")
    uuid = entry.get("uuid", "")

    # 標題 + 顏色
    if entry_type == "credit":
        title = f"↑ 進帳 +{amount} {currency}"
        color = _COLOR_CREDIT
    elif entry_type == "debit":
        title = f"↓ 出帳 -{amount} {currency}"
        color = _COLOR_DEBIT
    else:
        title = f"? 未知類型 {entry_type}"
        color = _COLOR_WARN

    if sig_mismatch:
        color = _COLOR_WARN

    # description（含警告）
    desc_parts = []
    if src_desc:
        desc_parts.append(src_desc)
    if sig_mismatch:
        desc_parts.append(f"⚠ **signature_mismatch** — claimed `{sig_claimed}` but env `{sig_env}`，Tim 可走 audit 查")
    description = "\n\n".join(desc_parts) if desc_parts else None

    fields = [
        {"name": "🏦 帳戶", "value": f"`{account}`", "inline": True},
        {"name": "💰 變動", "value": f"**{'+' if entry_type == 'credit' else '-'}{amount}** {currency}", "inline": True},
        {"name": "📊 餘額", "value": f"{balance_before} → **{balance_after}**", "inline": True},
        {"name": "📂 原因 (kind)", "value": f"`{src_kind}`", "inline": True},
    ]
    if src_ref:
        fields.append({"name": "🔗 ref", "value": f"`{src_ref}`", "inline": True})
    # T+ QA fix (Zeta 報): sig_env_marker == "unknown" 是合法 fallback (caller env detect 失敗 e.g.
    # Antigravity 平台未設 ANTIGRAVITY_SESSION env var), 不是 spoofing 異常。改成 informative 字樣
    # 避免 user 誤判為 bug — MatchesEnvMarker 端對 unknown 直接 return true 不算 mismatch
    if sig_env == "unknown":
        env_display = "`(undetected — wildcard fallback)`"
    else:
        env_display = f"`{sig_env}`"
    fields.append({"name": "🛡️ env_marker", "value": f"{env_display} (claimed `{sig_claimed}`)", "inline": True})

    embed = {
        "title": title,
        "color": color,
        "fields": fields,
        "footer": {"text": f"uuid: {uuid}"},
        "timestamp": ts if ts else None,
    }
    if description:
        embed["description"] = description
    # remove None values
    return {k: v for k, v in embed.items() if v is not None}


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def broadcast_entry(entry: dict) -> tuple[bool, str]:
    """Send one ledger entry as Discord embed. Returns (ok, message)."""
    client = _build_client()
    if client is None:
        return False, "treasury_mirror disabled or no client"

    embed = _build_embed(entry)
    ok, results = client.send(content=None, embeds=[embed])
    if ok:
        return True, f"ok ({len(results)} target(s))"
    else:
        errs = "; ".join(f"{r[0]}: {r[2]}" for r in results if not r[1])
        return False, f"all failed: {errs}"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--entry-file", help="Path to ledger entry .json")
    parser.add_argument("--stdin", action="store_true", help="Read entry JSON from stdin")
    parser.add_argument("--quiet", action="store_true", help="Suppress success log")
    args = parser.parse_args(argv)

    if args.stdin:
        raw = sys.stdin.read()
    elif args.entry_file:
        raw = Path(args.entry_file).read_text(encoding="utf-8")
    else:
        print("[notify_treasury] must specify --entry-file or --stdin", file=sys.stderr)
        return 1

    try:
        entry = json.loads(raw)
    except Exception as e:
        print(f"[notify_treasury] parse fail: {e}", file=sys.stderr)
        return 1

    ok, msg = broadcast_entry(entry)
    if ok and not args.quiet:
        print(f"[notify_treasury] {msg}")
    elif not ok:
        print(f"[notify_treasury] FAIL: {msg}", file=sys.stderr)
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
