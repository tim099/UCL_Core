#!/usr/bin/env python3
"""
mirror_parity_readback.py — T8 能證偽驗收：從 Discord 讀回頻道訊息，對 sentinel 序列做 diff。

區塊職責：T8 harness 的「反向讀回」端 — 給定 webhook URL（推導 channel_id）+ bot token，
         GET 頻道最近訊息，抽出 sentinel 標籤索引，回報 斷號 / 重複 / 亂序 / 計數。
物理意義：kiara 的能證偽驗收設計 — 不信 HTTP 204（那只是 Discord 接受、非渲染確認），
         直接從 Discord API 讀回實際落地的訊息當 ground truth。
數值影響：sentinel 格式約定 body 內含 "[<label>-<index>/<total>]"（例 [pyb-3/8]）；
         diff 結果 missing=[] 且 dup=[] 且 order=OK 才算 parity 過。

用法：
  python mirror_parity_readback.py --label pyb --expect 8
  （--webhook-file 預設 _smoke_test_webhook.txt；--limit 預設 100）
"""
import argparse
import json
import pathlib
import re
import sys
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent

# 區塊職責：路徑解析 — 專案資料端（webhook secret / bot token）住主專案 AgentCommands，
#          本腳本住 UCL_Core Tools~；經 AgentCommands._lib.tavern_paths（repo_root 探測）
#          拿專案根，跨掛載位置不寫死（同 notify_discord.py 慣例）。
def _probe_repo_root(start):
    p = start
    while True:
        if (p / "AgentCommands" / "_lib" / "tavern_paths.py").exists():
            return p
        if p.parent == p:
            break
        p = p.parent
    return pathlib.Path.cwd()


_repo_root = _probe_repo_root(pathlib.Path.cwd().resolve())
if str(_repo_root) not in sys.path:
    sys.path.insert(0, str(_repo_root))
from AgentCommands._lib import tavern_paths as _tp  # noqa: E402
from AgentCommands._lib import ucl_paths as _up  # noqa: E402

STATE_DIR = _tp.PROMPT_QUEUE_DIR
# 資料夾名走唯一解析點（ucl_paths.secrets_dir）—— 不寫死 _secrets
SECRETS_DIR = _up.secrets_dir()

UA = "Mozilla/5.0 UCL-MirrorParityHarness/1.0"


def _get_json(url, headers=None):
    """GET → dict/list；帶 UA（Cloudflare 擋預設 UA，07-19 教訓）。"""
    req = urllib.request.Request(url, headers={"User-Agent": UA, **(headers or {})})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode("utf-8"))


def resolve_channel_id(webhook_url):
    """GET /webhooks/{id}/{token} → channel_id（webhook URL 本身即憑證，免 bot token）。"""
    info = _get_json(webhook_url)
    return info["channel_id"]


def read_channel_messages(channel_id, bot_token, limit):
    """GET /channels/{id}/messages — 需 bot token（inbound daemon 同款憑證）。新→舊排序。"""
    url = f"https://discord.com/api/v10/channels/{channel_id}/messages?limit={limit}"
    return _get_json(url, headers={"Authorization": f"Bot {bot_token}"})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", required=True, help="sentinel 標籤（body 內 [<label>-N/M] 的 label 部分）")
    ap.add_argument("--expect", type=int, required=True, help="期望的 sentinel 總數 M")
    ap.add_argument("--webhook-file", default="_smoke_test_webhook.txt",
                    help="webhook secret 檔名（相對 PromptQueue；預設測試 webhook）")
    ap.add_argument("--limit", type=int, default=100, help="讀回訊息數上限（Discord 單次 max 100）")
    ap.add_argument("--json", action="store_true", help="輸出 machine-readable JSON")
    args = ap.parse_args()

    webhook_url = (STATE_DIR / args.webhook_file).read_text(encoding="utf-8").strip()
    bot_token = (SECRETS_DIR / "discord_bot_token.txt").read_text(encoding="utf-8").strip()

    channel_id = resolve_channel_id(webhook_url)
    msgs = read_channel_messages(channel_id, bot_token, args.limit)

    # 抽 sentinel：新→舊反轉成時間正序，逐訊息找 [<label>-N/M]
    pat = re.compile(re.escape("[" + args.label + "-") + r"(\d+)/(\d+)\]")
    seen_indices = []          # 時間正序出現的 index 序列（含重複）
    for m in reversed(msgs):
        content = m.get("content", "") or ""
        for hit in pat.finditer(content):
            seen_indices.append(int(hit.group(1)))

    expect = set(range(1, args.expect + 1))
    got = set(seen_indices)
    missing = sorted(expect - got)
    dup = sorted({i for i in seen_indices if seen_indices.count(i) > 1})
    # 亂序判定：去重後的首次出現順序應為單調遞增
    first_seen = []
    for i in seen_indices:
        if i not in first_seen:
            first_seen.append(i)
    in_order = first_seen == sorted(first_seen)

    verdict = "PASS" if (not missing and not dup and in_order) else "FAIL"
    # at-least-once 語意下 dup 可容忍：dup-only 標 PASS_WITH_DUP（漏送才是 FAIL）
    if verdict == "FAIL" and not missing and in_order and dup:
        verdict = "PASS_WITH_DUP"

    result = {
        "label": args.label, "expect": args.expect,
        "found": len(seen_indices), "unique": len(got),
        "missing": missing, "duplicates": dup, "in_order": in_order,
        "channel_id": channel_id, "scanned_messages": len(msgs),
        "verdict": verdict,
    }
    if args.json:
        print(json.dumps(result, ensure_ascii=False))
    else:
        print(f"# T8 read-back diff — label={args.label}")
        for k, v in result.items():
            print(f"  {k}: {v}")
    sys.exit(0 if verdict.startswith("PASS") else 1)


if __name__ == "__main__":
    main()
