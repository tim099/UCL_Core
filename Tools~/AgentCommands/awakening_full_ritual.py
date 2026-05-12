#!/usr/bin/env python3
"""
awakening_full_ritual.py — Awakening ritual 一鍵串接（status → morning → inbox catchup）

設計依據: docs/Plan/Plan_Awakening_Init_Protocol.md (zh-Hant under UCL_Core/Docs~/)

本 script 是 awakening.py 的 wrapper — 把早安喚醒 SOP 三步驟串成一鍵：
  1. awakening.py status       — 讀 persona pool + session lock
  2. awakening.py morning ...  — 寫 wake_count++ / status=online / tavern 公告
  3. run_cmd.py Tavern op=inbox_read — 掃 @mention 待辦

跨 agent / 跨專案通用：
  - 不寫死 agent (`--agent` 必填)
  - 不寫死 repo root (用 __file__ 反推 UCL_Core 根，再上到 main repo)
  - inbox bank_id 自動由 awakening.py 端的 agent→bank mapping 對齊

使用範例:
  python <UCL_Core>/Tools~/AgentCommands/awakening_full_ritual.py \\
      --agent antigravity --model "Claude Sonnet 4.6 Thinking" --persona apex-two \\
      --note "Tim 早安觸發 — apex-two 接班"

  python <UCL_Core>/Tools~/AgentCommands/awakening_full_ritual.py \\
      --agent claude-code --model "Opus 4.7 1M" --persona basecamp

設計歷史:
  - 前身為主專案 root 的 `scratch_post_awakening_advanced_response.py` (Antigravity 專用)
  - 2026-05-12 Zeta 拍板搬進 UCL_Core，順手 generalize 成跨 agent
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

# Windows utf-8 console output — 必須在最頂層執行，防止 cp950 encode error
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ─── 區塊職責：路徑解析 ────────────────────────────────────────────────────────
# 物理意義：本 script 在 <UCL_Core>/Tools~/AgentCommands/ 下
#          UCL_Core 是 git submodule，掛在主專案任意位置（per project）
#          repo root 推法 = script_dir 上跳 5 層直到找 .git 或 AgentCommands 目錄
# 數值影響：影響所有 subprocess 的 cwd + 同目錄工具路徑
SCRIPT_DIR = Path(__file__).resolve().parent          # .../UCL_Core/Tools~/AgentCommands/
AWAKENING_PY = SCRIPT_DIR / "awakening.py"
RUN_CMD_PY = SCRIPT_DIR / "run_cmd.py"


def find_repo_root() -> Path:
    """
    區塊職責：從 script_dir 反推主專案 repo root
    物理意義：UCL_Core 是 submodule，不知 host repo 結構，沿 parent 鏈找 .git 目錄
    數值影響：找不到 → 回 SCRIPT_DIR 的祖父祖父祖父（5 層上去，best effort）
    """
    cur = SCRIPT_DIR
    for _ in range(8):  # 最多上跳 8 層，防無限迴圈
        if (cur / ".git").exists() and (cur / "AgentCommands").exists():
            return cur
        cur = cur.parent
    # fallback：保守上跳 5 層 (Tools~/AgentCommands → Tools~ → UCL_Core → UCL → Assets → CardGame → repo_root)
    return SCRIPT_DIR.parent.parent.parent.parent.parent.parent


REPO_ROOT = find_repo_root()


# ─── 區塊職責：命令列參數解析 ──────────────────────────────────────────────────
def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Awakening ritual 一鍵串接 (status → morning → inbox catchup)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "--agent", required=True,
        help="agent id (e.g. claude-code / antigravity / Zeta) — bank lookup 依據",
    )
    parser.add_argument(
        "--model", required=True,
        help="自報模型名（顯示用，不影響 bank lookup；e.g. \"Opus 4.7 1M\" / \"Claude Sonnet 4.6 Thinking\"）",
    )
    parser.add_argument(
        "--persona", required=True,
        help="preferred persona codename (e.g. basecamp / apex-two / summit)",
    )
    parser.add_argument(
        "--note", default="",
        help="附加 note，顯示在 tavern 喚醒公告的 Note: 欄（選填）",
    )
    parser.add_argument(
        "--force-random", action="store_true",
        help="強制走 20%% random persona override 路徑（測試 Q3 spec 用）",
    )
    parser.add_argument(
        "--bank-id", default="",
        help="覆蓋 inbox 讀取的 bank id（預設由 awakening.py 內 agent→bank mapping 推斷）",
    )
    parser.add_argument(
        "--skip-inbox", action="store_true",
        help="跳過 Step 3 inbox catchup（純喚醒不掃 @mention）",
    )
    return parser.parse_args()


# ─── Step 1: status ─────────────────────────────────────────────────────────
def run_status() -> int:
    """讀 persona pool + session lock 環境（只讀不寫）"""
    print("\n" + "═" * 60)
    print("Step 1 ｜ awakening status — 讀 persona pool + session lock")
    print("═" * 60)
    result = subprocess.run(
        [sys.executable, str(AWAKENING_PY), "status"],
        cwd=str(REPO_ROOT),
        encoding="utf-8",
        errors="replace",
    )
    return result.returncode


# ─── Step 2: morning ────────────────────────────────────────────────────────
def run_morning(agent: str, model: str, persona: str, note: str, force_random: bool) -> int:
    """正式喚醒 ritual — wake_count++ / status=online / 寫 lock / tavern 公告"""
    print("\n" + "═" * 60)
    print(f"Step 2 ｜ awakening morning — agent={agent} / model={model} / persona={persona}")
    print("═" * 60)

    cmd = [
        sys.executable, str(AWAKENING_PY), "morning",
        "--agent", agent,
        "--model", model,
        "--persona", persona,
    ]
    if note:
        cmd += ["--note", note]
    if force_random:
        cmd += ["--force-random"]

    result = subprocess.run(cmd, cwd=str(REPO_ROOT), encoding="utf-8", errors="replace")
    return result.returncode


# ─── Step 3: inbox catchup ──────────────────────────────────────────────────
def run_inbox_catchup(bank_id: str) -> int:
    """掃 tavern inbox @mention 待辦"""
    print("\n" + "═" * 60)
    print(f"Step 3 ｜ inbox catchup — bank={bank_id}")
    print("═" * 60)
    if not bank_id:
        print("⚠ bank_id 為空，跳過 inbox catchup（用 --bank-id 指定）")
        return 0
    result = subprocess.run(
        [
            sys.executable, str(RUN_CMD_PY),
            "run", "Tavern",
            "--arg", "op=inbox_read",
            "--arg", "room=tavern",
            "--arg", f"agent_id={bank_id}",
        ],
        cwd=str(REPO_ROOT),
        encoding="utf-8",
        errors="replace",
    )
    return result.returncode


# ─── 區塊職責：agent → bank id 預設推斷 ────────────────────────────────────────
# 物理意義：對齊 awakening.py 端 AGENT_BANK_MAP 慣例
# 數值影響：找不到 → 回 "<agent>-da-xiaojie" 作為 best-effort 預設
DEFAULT_AGENT_BANK_MAP = {
    "claude-code": "claude-da-xiaojie",
    "antigravity": "antigravity-da-xiaojie",
    "Zeta": "Zeta-da-xiaojie",
    "gemini": "gemini-da-xiaojie",
}


def infer_bank_id(agent: str) -> str:
    return DEFAULT_AGENT_BANK_MAP.get(agent, f"{agent}-da-xiaojie")


# ─── 區塊職責：主流程 ──────────────────────────────────────────────────────────
def main() -> int:
    args = parse_args()

    print("=" * 60)
    print(f"  Awakening Full Ritual — agent={args.agent}")
    print(f"  repo_root={REPO_ROOT}")
    print("=" * 60)

    rc = run_status()
    if rc != 0:
        print(f"⚠ status 讀取 rc={rc}，繼續執行 morning")

    rc = run_morning(args.agent, args.model, args.persona, args.note, args.force_random)
    if rc != 0:
        print(f"❌ morning 失敗 (rc={rc})", file=sys.stderr)
        return rc

    if not args.skip_inbox:
        bank_id = args.bank_id or infer_bank_id(args.agent)
        run_inbox_catchup(bank_id)

    print("\n" + "=" * 60)
    print(f"  [OK] {args.persona} 喚醒完成！")
    print("=" * 60 + "\n")
    print("【下一步提示】")
    print("  - 看 tavern 最新訊息: run_cmd.py run Tavern --arg op=read --arg room=tavern --arg tail=10")
    print("  - 有 @mention 就回酒館，沒就開始工作")
    return 0


if __name__ == "__main__":
    sys.exit(main())
