"""
Agent Command CLI wrapper — 讓 AI agent / 人類用 CLI 提交與等待 Agent Command 執行結果。

實際執行仍由 Unity Editor 完成，但本工具新增 lock-file 自動觸發機制：

  ┌──────────┐ submit + write trigger    ┌─────────────────────────┐
  │  Python  │ ─────────────────────────▶│ AgentCommands/queue.json │
  │ (this)   │                           │ AgentCommands/pending.trigger │
  └──────────┘                           └─────────────────────────┘
                                                  │
                                                  │ EditorApplication.update (1Hz)
                                                  ▼
                                ┌────────────────────────────────────┐
                                │  UCL_AgentCommandWatcher           │
                                │   1. File.Move trigger → .running  │
                                │   2. Runner.RunAsync()             │
                                │   3. finally: Trigger.Clear()      │
                                └────────────────────────────────────┘

子命令：
  1. submit    — 寫 queue.json + 寫 pending.trigger（前置 ensure_idle）
  2. wait      — 輪詢 .running 消失 → 讀 queue.json 判定 cmd 結果
  3. run       — submit + wait（一次跑完）
  4. recompile — 觸發 Unity 重編 + 等到 .compile_status.json 推進（agent 改完 .cs 後逼 Unity 接收）
  5. list      — 列出 queue 內現存指令
  6. catalog   — 顯示 commands_catalog.md

使用範例：
    # 把 ResolveAssetReferences 加進 queue 並等到 Unity 執行完
    # 注意：outputPath 是 Cmd 內部相對 Unity project root（CardGame/）→ 實際檔案在 CardGame/AgentCommands/
    #       --output-file 是 wrapper 相對 git root → 必須帶 CardGame/ 前綴才能找到產物
    python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ResolveAssetReferences \\
        --arg assetType=RCG_StoryData --arg assetIds=AbandonedTemple \\
        --arg maxDepth=3 --arg format=md \\
        --arg outputPath=AgentCommands/asset_refs_AbandonedTemple.md \\
        --output-file CardGame/AgentCommands/asset_refs_AbandonedTemple.md

    # 只 submit 不 wait
    python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py submit ExportCommandCatalog

依賴前提：Unity Editor 開著 + UCL_AgentCommandWatcher 啟用（預設啟用，可在
UCL_AgentCommandsPage 上 toggle）。Watcher 停用時，Python 仍會寫 trigger，但需
人工按 Tools/UCL/Agent Commands/Run Pending 才會執行。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Windows console 預設常為 cp950 / cp1252，遇中文 error message 變亂碼讓 agent 看不懂。
# Python 3.7+ 的 reconfigure() 強制 stdout/stderr 走 utf-8。
if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


def print_fail_verdict(text: str) -> None:
    """
    區塊職責：印出失敗判決行 — stderr 照舊（exit-code 慣例）+ stdout 同文鏡像一份。
    物理意義：caller 經 PowerShell 5.1 以 `2>&1` 呼叫本 script 時，PS 會把 native stderr
             逐行攔成 NativeCommandError ErrorRecord，用 console codepage (cp950) 重寫
             並混入本地化雜訊（「位於 線路:1 字元:1」）— caller 的 utf-8 reader 讀到
             0xa6 等 cp950 lead byte 即拋 UnicodeDecodeError，stderr 上的 ✗ 判決整段被吞
             （實證 2026-07-28：dirty bytes 固定出現在 stdout 結束後的 stderr 區段）。
             native stdout 則不論有無 2>&1 都是 raw passthrough 不經 PS 重編碼，
             判決行鏡像於 stdout 保證失敗案例穩定可見。
    數值影響：正常 caller（Bash / 直接 capture、分流讀）會看到判決兩次（stdout+stderr 各一），
             接受此冗餘換取 PS 2>&1 場景下的可見性。
    """
    print(text, file=sys.stderr)
    sys.stderr.flush()
    print(text, flush=True)


# ===========================================================
# 區塊職責：cmd 失敗時把 Editor 端落檔的詳細錯誤報告印給 caller（Tim 2026-07-29 拍板）
# 物理意義：queue 只留 LastRunError 一行；完整 stack / inner exception 鏈以前只在 Editor
#          console，client（agent）看不到，遇到「外層例外遮罩真兇」的情況根本查不動。
#          Runner 現在會寫 <DataRoot>/_cmd_errors/<cmdId>.md 與 _last_cmd_error.md，
#          本函式在判失敗後把它印出來（截斷 60 行，附完整路徑供細讀）。
# 數值影響：純讀檔輸出；檔案不存在（舊版 Editor / 落檔失敗）→ 靜默跳過，不影響失敗回報。
# ===========================================================
def read_cmd_result(cmd_id: str):
    """讀 Editor 端落的 per-cmd verdict 檔（成對改的 python 半邊，2026-08-07）。

    區塊職責：cmd 從 queue 消失後的**權威判定來源**。
    物理意義：Editor 端現在「失敗的 OneShot 也自動出隊」—— 消失只代表「結束」，
             成功或失敗要看 _cmd_results/<id>.json（Runner 在出隊前寫）。
             舊的「消失＝成功」推論只剩 fallback 地位（舊版 Editor / result 檔寫失敗）。
    數值影響：回傳 dict（含 result / error / error_report）或 None（檔不存在／壞檔）。
    """
    try:
        p = Path(DATA_ROOT) / "_cmd_results" / f"{cmd_id}.json"
        if not p.is_file():
            return None
        data = json.loads(p.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except Exception:
        return None   # 壞檔當沒有 —— fallback 舊推論，不擋判定


def print_cmd_error_report(cmd_id: "str | None", max_lines: int = 60) -> None:
    try:
        candidates = []
        if cmd_id:
            candidates.append(Path(DATA_ROOT) / "_cmd_errors" / f"{cmd_id}.md")
        candidates.append(Path(DATA_ROOT) / "_last_cmd_error.md")
        for p in candidates:
            if not p.is_file():
                continue
            text = p.read_text(encoding="utf-8", errors="replace")
            # _last_cmd_error.md 是「最近一筆」— 若不是本筆 cmd 就別誤導 caller
            if cmd_id and p.name == "_last_cmd_error.md" and cmd_id not in text:
                continue
            lines = text.splitlines()
            print("  ── Editor 端詳細錯誤報告 ──")
            for ln in lines[:max_lines]:
                print(f"  {ln}")
            if len(lines) > max_lines:
                print(f"  …（省略 {len(lines) - max_lines} 行）")
            print(f"  📄 完整報告：{p}")
            return
    except Exception:
        pass   # 報告只是加值，讀不到不該再蓋掉原始錯誤


# ===========================================================
# 路徑解析 — 跨專案通用（不假設 UCL_Core 放在哪一層）
# ===========================================================
# 上層專案可能把 UCL_Core 放在不同位置（CardGame/Assets/UCL/ 或 Assets/UCL/ 或 root）
# 解析優先序：
#   1. 環境變數 CLAUDE_PROJECT_DIR（Claude Code hook 注入；最權威）
#   2. 從本檔位置往上找第一個含 .git 「資料夾」（避開 submodule 的 .git file）
#   3. fallback：用 parents[2]（UCL_Core 根）

import os as _os


def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


_env_root = _os.environ.get("CLAUDE_PROJECT_DIR")
if _env_root and Path(_env_root).is_dir():
    GIT_ROOT = Path(_env_root).resolve()
else:
    _walked = _find_git_root_by_walk(Path(__file__))
    GIT_ROOT = _walked if _walked else Path(__file__).resolve().parents[2]

# QUEUE_DIR = canonical RPC 錨點 (queue.json / pending.trigger), 永遠在 RepoRoot/AgentCommands —
# 跟 C# UCL_RepoPath.AgentCommandsDir 對齊, 不跟資料搬。
QUEUE_DIR = GIT_ROOT / "AgentCommands"

# T-PATH-01 (2026-05-28): AgentCommands 資料根 pointer 檔解析
# 物理意義: C# 控制台 Apply 把絕對資料根寫到 <git-root>/.agentcommands_root.local;
#          本 helper 讀檔得實際資料根, 沒有 → 預設 GIT_ROOT/AgentCommands (與舊行為相同)。
# 數值影響: 跨語言 (C#/Python) 共讀同一檔, per-machine (gitignored)。
def _resolve_agentcommands_data_root(git_root: Path) -> Path:
    pointer = git_root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content:
                p = Path(content)
                if p.is_absolute():
                    return p.resolve()
    except Exception:
        pass
    return (git_root / "AgentCommands").resolve()

# DATA_ROOT = 可 override 的資料根 (給 ChatTavern / _last_op.md / etc.); 預設 = QUEUE_DIR。
DATA_ROOT = _resolve_agentcommands_data_root(GIT_ROOT)

# agent-command-pipeline-parallelize T05: per-agent queue 子目錄
# 物理意義: queue id '<persona>' / '<persona>/<lane>' → queues/<persona>/queue[-<lane>].json + pending[-<lane>].trigger
# null → legacy default 路徑 (queue.json + pending.trigger) 不變 (backward compat)
_AGENT_ID: str | None = None   # set by main() argparse

def set_agent_id(agent_id: str | None) -> None:
    global _AGENT_ID
    _AGENT_ID = agent_id if agent_id else None

# cmd-identity P1: 顯式 persona 宣告（--persona）。與 _AGENT_ID 分開存 ——
# 前者是「我是誰」，後者是「走哪條 queue」；預設情況同值，但帶 --agent-id / --lane 時會分岔。
_PERSONA: str | None = None    # set by main() argparse

def set_persona(persona: str | None) -> None:
    global _PERSONA
    _PERSONA = (persona or "").strip() or None

# 區塊職責: queue / trigger 的路徑樣板 —— **persona 資料夾制**（Tim 2026-08-01 拍板）
#   queues/<persona>/queue.json            ← --persona X
#   queues/<persona>/queue-<lane>.json     ← --persona X --lane Y
#   queues/<persona>/pending[-<lane>].trigger[.running]
#   queues/anonymous/…                     ← 沒帶身分（保留字，不是 persona）
# 物理意義: 舊制平鋪成 queues/queue-<persona>-<lane>.json，「這筆誰派的」要從檔名反推，
#   而 queue-ame-design 無法判定「-design 是用途還是名字的一部分」。改資料夾之後
#   身分（資料夾）與通道（檔名後綴）**在檔案系統層就分開**，不必解析任何字串。
# 數值影響: 切換式改版無相容層 —— 切換時 36 個舊 queue 全空、0 筆在途 cmd（已點清），
#   舊檔直接刪除；最外層共用 queue.json 一併廢除。
#   ⚠ C# 端樣板在 UCL_AgentCommandQueue.cs，兩邊**必須同時改**：任一邊落後，
#     trigger 就寫在對方沒在看的地方，而那種斷線是**靜默**的（cmd 永遠 pending 到 timeout）。
ANONYMOUS_QUEUE_ID = "anonymous"   # 保留字：身分解析讀到它回 None，不可當 persona 用

_SPLIT_CACHE: tuple[str, str, str | None] | None = None   # (原始 id, 資料夾, lane)

def _split_queue_id() -> tuple[str, str | None]:
    """把 _AGENT_ID 拆成 (資料夾, lane)。空值 → anonymous。只切第一個 '/'。

    數值影響：結果快取在 _SPLIT_CACHE —— 四個路徑函式各呼叫一次，不快取的話
             一次派遣會把同一句「不合法 id」的警告印 9 遍。**同一件事說一次**：
             洗版的警告跟沒有警告一樣會被略過。
    """
    global _SPLIT_CACHE
    raw = (_AGENT_ID or "").replace("\\", "/").strip()
    if _SPLIT_CACHE is not None and _SPLIT_CACHE[0] == raw:
        return _SPLIT_CACHE[1], _SPLIT_CACHE[2]
    if not raw:
        result = (ANONYMOUS_QUEUE_ID, None)
    else:
        folder, sep, lane = raw.partition("/")
        lane = lane.replace("/", "-") if sep else ""
        # 路徑穿越防護：這些值來自 CLI，不擋的話是一條寫出 queues/ 之外的路
        bad = any(seg and (seg in (".", "..") or ".." in seg) for seg in (folder, lane))
        if bad:
            print(f"  ⚠ 不合法的 queue id '{raw}' → 落 {ANONYMOUS_QUEUE_ID}。", file=sys.stderr)
            result = (ANONYMOUS_QUEUE_ID, None)
        else:
            result = (folder, (lane or None))
    _SPLIT_CACHE = (raw, result[0], result[1])
    return result

def queue_path() -> Path:
    folder, lane = _split_queue_id()
    return QUEUE_DIR / "queues" / folder / (f"queue-{lane}.json" if lane else "queue.json")

def trigger_path() -> Path:
    folder, lane = _split_queue_id()
    return QUEUE_DIR / "queues" / folder / (f"pending-{lane}.trigger" if lane else "pending.trigger")

def running_path() -> Path:
    return trigger_path().with_name(trigger_path().name + ".running")

def queue_dir_for_writing() -> Path:
    """寫入 queue/trigger 前 mkdir 用的對應 dir（= 該 persona 的資料夾）。"""
    folder, _lane = _split_queue_id()
    return QUEUE_DIR / "queues" / folder

# ===========================================================
# False-success race fix (T02, 2026-05-16 basecamp)
# 區塊職責: cmd 失敗時 C# 端 auto-remove cmd 跑得比 Python 輪詢快 →
#         Python 看到 cmd is None 誤判 success. 本表 + check helper 補 detection.
# 物理意義: 各 cmd_type 寫對應 last_op 檔案; cmd 跑完後 check 該檔 mtime + 內容
#         若 mtime 在 submit 之後且第一行以 ❌ / Failed 開頭 → 報失敗
# 數值影響: cmd is None 路徑前加 fail-detection 分支; 不破壞既有 success path
# ===========================================================
CMD_OUTPUT_FILES = {
    "tavern": "ChatTavern/_last_op.md",
    "treasury": "ChatTavern/_last_op.md",
    "notelesson": "Lessons/_last_lesson.md",
}

def check_cmd_result_file(cmd_type: str, mtime_threshold: float, cmd_id: str | None = None):
    """檢查指定 cmd_type 的 last_op 檔案是否顯示失敗。

    Returns:
        ("failed", err_msg) — 確認失敗（檔有更新 + 開頭含 fail marker）
        ("success", "") — 確認成功（檔有更新 + 開頭含 success marker）
        ("unknown", "") — 無法確認（檔不存在 / mtime 太舊 / 沒有明確 marker / cmd_id 不符）

    僅在 ("failed", ...) 時 caller 應該報失敗; ("unknown", ...) 維持原有 success 推測
    （與舊版行為兼容, 不引入新的偽陽性）。

    cmd_id (T-LastOp-CmdId, 2026-06-12): C# 端 Runner 執行 queue cmd 時會在 last_op 檔
    第二行 stamp `<!-- cmd_id: X -->`。多 session 並發對同一 Editor 發 cmd 時，mtime 在
    submit 之後不代表是本 process 的 cmd 寫的 — 另一 chat 的 cmd 在同窗口失敗會污染判定
    （實證: 2026-06-12 21:27 kiara post 成功被 gura chat 的 T07 fail marker 誤報 exit 2）。
    傳入 cmd_id 且檔內 stamp 存在但不符 → 回 ("unknown", "")（別人的結果，不認帳）。
    檔內無 stamp（舊版 Editor 還沒 stamp）→ 走 legacy mtime-only 判定，向後相容。
    """
    rel_path = CMD_OUTPUT_FILES.get(cmd_type.lower())
    if not rel_path:
        return ("unknown", "")
    path = QUEUE_DIR / rel_path
    if not path.exists():
        return ("unknown", "")
    try:
        mtime = path.stat().st_mtime
    except OSError:
        return ("unknown", "")
    # mtime 必須 >= submit time（容忍 1s 時鐘漂移）才視為「本次 cmd 寫的」
    if mtime < mtime_threshold - 1.0:
        return ("unknown", "")
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            # 只讀前 4KB 足夠抓首行 + 簡短錯誤訊息, 避免讀超大檔
            head = f.read(4096)
    except OSError:
        return ("unknown", "")
    # T-LastOp-CmdId (2026-06-12): 比對檔內 cmd_id stamp — stamp 存在但不是本次 cmd 寫的
    # → 一律 unknown（fail / success 都不認帳），擋多 session 並發污染誤報
    if cmd_id:
        stamp_match = re.search(r"<!--\s*cmd_id:\s*(\S+)\s*-->", head)
        if stamp_match and stamp_match.group(1) != cmd_id:
            return ("unknown", "")
    first_line = head.split("\n", 1)[0].strip()
    # Fail markers — 對齊 Cmd_Tavern / Cmd_Treasury / Cmd_NoteLesson 寫法
    if first_line.startswith("# ❌") or "Cmd Failed" in first_line or "Cmd failed" in first_line:
        # 抓前 5 行當錯誤訊息（濾掉 cmd_id stamp 行 — 那是機器比對用，不是錯誤內容）
        err_lines = head.split("\n", 6)[1:5]
        err_msg = "\n  ".join(L.strip() for L in err_lines
                              if L.strip() and not re.match(r"<!--\s*cmd_id:", L.strip()))
        return ("failed", err_msg or first_line)
    if first_line.startswith("# ✅"):
        return ("success", "")
    return ("unknown", "")

# Legacy module-level constants — kept for any external import; dynamic versions above are canonical.
QUEUE_PATH = QUEUE_DIR / "queue.json"
TRIGGER_PATH = QUEUE_DIR / "pending.trigger"
RUNNING_PATH = QUEUE_DIR / "pending.trigger.running"
TAVERN_DIR = DATA_ROOT / "ChatTavern"  # T-PATH-01: 走可 override 資料根;預設 = QUEUE_DIR/ChatTavern (與舊行為相同)
# 區塊職責：酒館同步握手（wait-reply）+ 酒保 NPC + per-message 讀取層 → 已抽離到兄弟模組
# 物理意義：run_cmd 的職責是「送 cmd 進 Unity 佇列並等它跑完」；「發完訊息後在 client 端等回覆」
#          不碰佇列也不進 Editor，是另一件事。混在一檔會讓本檔膨脹到難維護
#          （Tim 2026-07-29 拍板抽離，本檔 1860 → 1314 行）。旗標檔與台詞庫路徑一併搬過去。
# 數值影響：模組不自行解析資料根 —— 此處 configure() 注入 TAVERN_DIR / GIT_ROOT。
#          override 規則（T-PATH-01）只存在於本檔，複製一份到那邊必然漂移。

import tavern_handshake as _handshake   # 同層模組；run_cmd 以 script 執行時其所在夾即 sys.path[0]

_handshake.configure(tavern_dir=TAVERN_DIR, git_root=GIT_ROOT)

# 區塊職責：Tavern Cmd 的 client 端規則（送前預檢 / wait-reply 政策 / banner）→ 已抽離到兄弟模組
# 物理意義：run_cmd 是**對 36 個 cmd type 一視同仁**的通用 RPC 管線；「Tavern 這一個 cmd 的參數
#          長什麼樣、哪些 op 要等回覆」是單一 cmd 的業務規則，佔了本檔兩成篇幅卻只服務 1/36
#          （Tim 2026-07-29 拍板拆分，本檔 1304 → 1082 行）。
# 數值影響：模組不自行解析路徑、不自行偵測環境 —— 此處注入 QUEUE_DIR / TAVERN_DIR / env-marker 偵測器。
#          env-marker 用 lambda 包一層是**刻意的 late binding**：_detect_caller_env_marker 定義在本檔
#          更下方，此處直接取名會拿到 NameError；lambda 到呼叫時才解析，且維持「configure 緊鄰 import」
#          不留「忘了 configure」的縫。
import tavern_cmd as _tavern_cmd

_tavern_cmd.configure(
    queue_dir=QUEUE_DIR,
    tavern_dir=TAVERN_DIR,
    detect_env_marker=lambda: _detect_caller_env_marker(),
)
# UCL_CompileErrorTracker 寫入：mtime 推進 + errors/warnings 計數 → recompile 子命令的「完成」依據
COMPILE_STATUS_PATH = QUEUE_DIR / ".compile_status.json"
# 區塊職責：解析 commands_catalog.md 的實際位置（跨專案，不寫死單一 repo 佈局）。
# 物理意義：C# Cmd_ExportCommandCatalog 預設寫 <Unity專案根>/AgentCommands/commands_catalog.md。
#          「repo 根＝Unity 專案根」的 repo（LY）落在 QUEUE_DIR；巢狀 Unity 專案的 repo
#          （CardGame/）落在 GIT_ROOT/<子專案>/AgentCommands。舊版寫死 CardGame 路徑，
#          在 LY 天生指錯位置且靜默（task_8ee9fe9f / summit 2026-07-31 血證）。
# 數值影響：優先 QUEUE_DIR；不存在才 glob 一層子目錄找既有產物；都沒有回傳 QUEUE_DIR
#          預設位置（cmd_catalog 會印「不存在＋怎麼生成」的提示，不再指向幻影路徑）。
def _resolve_catalog_path() -> Path:
    primary = QUEUE_DIR / "commands_catalog.md"
    if primary.is_file():
        return primary
    for candidate in sorted(GIT_ROOT.glob("*/AgentCommands/commands_catalog.md")):
        return candidate
    return primary
CATALOG_PATH = _resolve_catalog_path()

# ensure_idle 預設值
DEFAULT_ACK_TIMEOUT = 60.0   # 等前一輪 trigger 消失的最久秒數
DEFAULT_POLL_INTERVAL = 1.0
DEFAULT_RUN_TIMEOUT = 120    # wait 階段整體超時


# ===========================================================
# Trigger 狀態 / ensure_idle
# ===========================================================

def trigger_state() -> str:
    """回傳 'running' / 'pending' / 'idle' (dynamic per --agent-id)."""
    if running_path().exists():
        return "running"
    if trigger_path().exists():
        return "pending"
    return "idle"


def ensure_idle(timeout_sec: float = DEFAULT_ACK_TIMEOUT,
                poll_interval: float = DEFAULT_POLL_INTERVAL) -> None:
    """
    在寫入新 trigger 前 block，直到 pending / running 都不存在。

    若 timeout 內仍有殘留 → SystemExit（強迫使用者人工介入），避免：
      - Editor 沒開但有殘留 trigger → 永遠等不到接手
      - Editor crash 留下 .running → 永遠等不到清除
    """
    deadline = time.time() + timeout_sec
    last_state = None
    first = True
    while time.time() < deadline:
        state = trigger_state()
        if state == "idle":
            if not first:
                print("  ✓ idle, proceeding.")
            return
        if state != last_state:
            print(f"  ... previous batch is '{state}', waiting (timeout {timeout_sec:.0f}s)...")
            last_state = state
        first = False
        time.sleep(poll_interval)

    state = trigger_state()
    raise SystemExit(
        f"[run_cmd] Previous batch still '{state}' after {timeout_sec:.0f}s.\n"
        f"  - Check Unity Editor is open with UCL_AgentCommandWatcher enabled.\n"
        f"  - If Editor crashed or watcher is off, manually delete:\n"
        f"      {trigger_path()}\n"
        f"      {running_path()}"
    )


def write_trigger(note: str) -> None:
    """寫一個 pending.trigger（內含 timestamp + note 給 debug 用; dynamic per --agent-id）。"""
    queue_dir_for_writing().mkdir(parents=True, exist_ok=True)
    body = {
        "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "submittedBy": note,
    }
    with open(trigger_path(), "w", encoding="utf-8") as f:
        json.dump(body, f, indent=2, ensure_ascii=False)
        f.write("\n")


# ===========================================================
# Queue I/O
# ===========================================================

def load_queue() -> dict:
    """讀取 queue.json (dynamic per --agent-id)；不存在或損毀就回空骨架。"""
    qp = queue_path()
    if not qp.exists():
        return {"Commands": []}
    try:
        with open(qp, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict) or "Commands" not in data:
            return {"Commands": []}
        return data
    except Exception as e:
        print(f"[run_cmd] queue.json parse error: {e}", file=sys.stderr)
        return {"Commands": []}


def save_queue(data: dict) -> None:
    # 區塊職責：atomic + retry 寫入 queue.json，避免與 Editor 端 Watcher 並發改寫撞檔鎖
    # 物理意義：Windows 檔鎖為強制性 — 直接 open("w") 會先 truncate，期間若 Editor consumer
    #           正在讀/改寫同一檔 → OSError [Errno 22] / sharing violation，整條 Cmd dispatch 失效
    # 數值影響：改成「寫 temp → os.replace」近乎 atomic 的 rename（取代 truncate-in-place），
    #           撞鎖則 backoff 重試 5 次（0.1s→0.5s），全失敗才拋出原錯讓 caller 看到真因
    qp = queue_path()
    queue_dir_for_writing().mkdir(parents=True, exist_ok=True)
    payload = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    # temp 檔帶 pid 後綴：多個 run_cmd.py 並行時各自獨立，不互相覆寫
    tmp = qp.with_name(f"{qp.name}.tmp{os.getpid()}")
    last_err: OSError | None = None
    for attempt in range(5):
        try:
            # 先把完整內容寫進 temp（即使這步被打斷，也不會破壞既有 queue.json）
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(payload)
            # rename 覆蓋目標：同碟近乎 atomic；目標被 Editor 鎖住時拋 PermissionError(OSError 子類)
            os.replace(tmp, qp)
            return
        except OSError as e:
            last_err = e
            # 線性 backoff，給 Editor 端釋放檔鎖的時間窗
            time.sleep(0.1 * (attempt + 1))
    # 全部重試失敗 → 清掉殘留 temp，避免污染目錄，再拋出最後一次的原始錯誤
    try:
        if tmp.exists():
            tmp.unlink()
    except OSError:
        pass
    raise last_err


def remove_cmd_from_queue(cmd_id: str) -> bool:
    """從 queue.json 移除指定 Id 的 cmd；用於 cmd_wait 偵測到 Failed 時的自動清理。

    回傳 True 表示有移除；False 表示找不到（已被別的 process 移走或 id 拼錯）。
    """
    queue = load_queue()
    cmds = queue.get("Commands", [])
    new_cmds = [c for c in cmds if c.get("Id") != cmd_id]
    if len(new_cmds) == len(cmds):
        return False
    queue["Commands"] = new_cmds
    save_queue(queue)
    return True


def make_id(cmd_type: str) -> str:
    """產生人讀且唯一的 Cmd Id。格式 yyyymmdd-HHMMSS-<short-uuid>-<type-slug>。"""
    ts = datetime.now().strftime("%Y%m%d-%H%M%S")
    short = uuid.uuid4().hex[:6]
    slug = cmd_type.lower()
    return f"{ts}-{short}-{slug}"


# 區塊職責：cmd_type 別名表 — 把常見打錯名稱自動 rewrite 到正確 cmd type
# ⚠ S5（2026-07-29）起本表是 **fallback**，不是主來源：normalize_cmd_type() 優先讀
#   commands_schema.json 的 type_aliases（由 UCL_AgentCommandRegistry.s_TypeAliases 生成）。
#   本表只在產物不存在時生效，僅為離線可用性保留；**新增別名請加在 C# Registry 那邊，不要加這裡**。
# 物理意義：人類 / 跨 agent 容易把 cmd type 跟資料夾名 / skill 名混淆
#          （例：『ChatTavern』是 dir 名 / skill 名，但 cmd type 是『Tavern』）
# 數值影響：rewrite 後印警告但不 abort — 好心 fail-open；正名後跑跟原來一樣
# 安全：別名衝突由先到先得；新增 alias 必須對映到 registered cmd type
TYPE_ALIASES = {
    "chattavern": "Tavern",       # ChatTavern dir/skill 名 → Tavern cmd
    "chat_tavern": "Tavern",
    "chat-tavern": "Tavern",
    "tavernchat": "Tavern",
    "lessons": "NoteLesson",      # NoteLesson skill 簡寫
    "note_lesson": "NoteLesson",
    "lesson": "NoteLesson",
}


def normalize_cmd_type(cmd_type: str) -> str:
    """套用 cmd type 別名 — 找不到就回原樣（fail-open，讓 Editor 端 reject）。

    S5（2026-07-29）：別名表**優先取 C# 生成的 commands_schema.json**，本檔的 TYPE_ALIASES 退為
    產物不存在時的 fallback。本表與 `UCL_AgentCommandRegistry.s_TypeAliases` 原本是同一張表的
    兩份手抄鏡像（本 plan 點名的第四處）；改讀產物後，Registry 是唯一事實來源。
    """
    if not cmd_type:
        return cmd_type
    # lazy 載入產物 —— 只讀一個 ~5KB JSON，不觸發雜湊驗算（新鮮度只在要擋人時才驗）
    _tavern_cmd._ensure_schema_loaded()
    # 產物內的 key 是 C# 原樣大小寫（e.g. "ChatTavern"），本端比對一律小寫化後查
    generated = {k.lower(): v for k, v in (_tavern_cmd.TYPE_ALIASES_FROM_SCHEMA or {}).items()}
    canonical = generated.get(cmd_type.lower()) or TYPE_ALIASES.get(cmd_type.lower())
    if canonical and canonical != cmd_type:
        print(f"  ℹ️  cmd_type '{cmd_type}' → '{canonical}' (auto-aliased — see TYPE_ALIASES in run_cmd.py)")
        return canonical
    # 區塊職責：Cmd_ 前綴剝除 — handler class 命名慣例是 Cmd_<Name>，registry key 是去前綴名。
    # 物理意義：文件與程式碼以 class 名稱呼指令，人與 agent 自然送 class 名（summit 2026-07-31
    #          血證：Cmd_Tavern 連吃兩發 Unknown type）。與 C# Registry.Get Phase 3 對齊，
    #          兩端誰先攔到都能救；剝除後再過一次 alias 表（Cmd_ChatTavern → ChatTavern → Tavern）。
    # 數值影響：rewrite 後印警告不 abort（fail-open，與 alias 同款）；非 Cmd_ 開頭原樣返回。
    if cmd_type.lower().startswith("cmd_"):
        stripped = cmd_type[4:]
        resolved = generated.get(stripped.lower()) or TYPE_ALIASES.get(stripped.lower()) or stripped
        print(f"  ℹ️  cmd_type '{cmd_type}' → '{resolved}' (Cmd_ prefix stripped — registry key 是去前綴的 CommandType)")
        return resolved
    return cmd_type


def precheck_cmd_type(cmd_type: str) -> None:
    """cmd_type 送出前對 schema 產物的已註冊清單預檢 — unknown 直接擋 + did-you-mean。

    區塊職責：把「Unknown command type」從 Editor round-trip（~2s + 失敗清理）搬到 client 端 <0.01s。
    物理意義：與 tavern 參數預檢同一套 fail-open 哲學 — schema 缺席／停用／過期都**不擋**
             （無法驗證 ≠ 不通過），只有「schema 新鮮且查無此 type」才 abort。
             did-you-mean 用 difflib，跟 C# Registry.SuggestTypes（Levenshtein）雙端各自最近鄰。
    數值影響：命中未知 type → SystemExit(2)，省一次注定失敗的 Editor round-trip；
             schema stale → 降級為警告後放行，由 Editor 端（含 Cmd_ 剝除 + did-you-mean）判。
    """
    _tavern_cmd._ensure_schema_loaded()
    if not _tavern_cmd.SCHEMA_STATUS.get("loaded"):
        return                                    # 產物不存在/停用/壞掉 → fail-open（訊息已由載入層印過）
    commands = _tavern_cmd._SCHEMA_RAW.get("commands") or {}
    if not commands:
        return
    if cmd_type.lower() in {k.lower() for k in commands}:
        return
    import difflib
    suggestions = difflib.get_close_matches(cmd_type, list(commands), n=3, cutoff=0.5)
    hint = f"  Did you mean: {' / '.join(suggestions)}?" if suggestions else ""
    # 擋人前才付新鮮度驗算成本；過期的清單不能拿來擋人（新 Cmd 剛加、產物還沒重生成的窗口）
    _tavern_cmd._ensure_freshness_checked()
    if _tavern_cmd.SCHEMA_STATUS.get("stale"):
        print(f"  ⚠ cmd_type '{cmd_type}' 不在 schema 已註冊清單，但 schema 已過期 → 不擋，送 Editor 判。{hint}",
              file=sys.stderr)
        return
    print(f"✗ cmd_type '{cmd_type}' 不在已註冊指令清單（{len(commands)} 個）。{hint}\n"
          f"  完整清單：python run_cmd.py catalog（或 queue 目錄的 commands_schema.json）",
          file=sys.stderr)
    raise SystemExit(2)


def append_cmd(cmd_type: str, mode: str, args: dict, description: str) -> str:
    """append 一筆指令到 queue.json，回傳 cmd_id。"""
    cmd_type = normalize_cmd_type(cmd_type)
    precheck_cmd_type(cmd_type)
    # 區塊職責: caller-side env_marker auto-inject (Tim 2026-05-11 QA bug fix TreasuryEnvMarker)
    # 物理意義: 所有 append_cmd 路徑 (submit / run / recompile) 都該注入, 不止 submit
    # 數值影響: 已帶 _caller_env_marker (test override) → 不覆寫; 沒帶 → 走 _detect_caller_env_marker
    if "_caller_env_marker" not in args:
        args["_caller_env_marker"] = _detect_caller_env_marker()
    # 區塊職責: cmd-identity P1 —— 顯式 --persona 戳進 args（tier 1）
    # 物理意義: 沿用上面 _caller_env_marker 的同一個形狀（caller 端偵測後傳進 args，
    #          2026-05-11 Treasury bug 的修法）。下游拿得到 persona 就不必反查 session lock，
    #          而反查那層（tavern_cmd 的 autofill）**同 claim_origin 多 lock 時是靜默猜的**
    #          （tavern_cmd.py:443 的 max(locked_at)）—— 讓正常路徑根本走不到那行，
    #          比在那行加一句警告更接近「止血」（basecamp 拍板二 seq 14118）。
    # 數值影響: **只在缺席時填**，不覆寫 caller 顯式的 --arg persona=（那是更貼近該 cmd 的宣告）。
    #          兩者不同 → 出聲但照 --arg 走：這是 caller 自己給的兩個矛盾指令，
    #          該讓人知道，不該由工具挑一個安靜執行。
    if _PERSONA:
        _arg_persona = str(args.get("persona") or "").strip()
        if not _arg_persona:
            args["persona"] = _PERSONA
        elif _arg_persona != _PERSONA:
            print(f"  ⚠ 身分宣告衝突：--persona {_PERSONA} vs --arg persona={_arg_persona} "
                  f"→ 依 --arg 值送出（較貼近該 cmd）。請確認哪個才是你要的。", file=sys.stderr)
    # 註：tier 2「queue 反推」**不再寫 queue 頂層欄位** —— persona 資料夾制之後
    #     身分由路徑本身承載（queues/<persona>/…），再存一份欄位就是第二個宣稱點，
    #     兩者哪天不一致沒有任何機制會喊（Tim 2026-08-01 改版，欄位機制當日退役未上線）。
    queue = load_queue()
    cmd_id = make_id(cmd_type)
    queue["Commands"].append({
        "Id": cmd_id,
        "Type": cmd_type,
        "Mode": mode,
        "RunCount": 0,
        "Args": args,
        "CreatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "LastRunAt": None,
        "LastRunResult": None,
        "LastRunError": None,
        "Description": description or None,
    })
    save_queue(queue)
    return cmd_id


# ===========================================================
# 子命令：submit / wait / run / list / catalog
# ===========================================================

def _detect_caller_env_marker() -> str:
    """
    區塊職責: 偵測 caller 端 environment (Claude Code / Antigravity / Gemini / unknown)
    物理意義: Treasury Bug fix (Tim 2026-05-11 QA confirmed TreasuryEnvMarker) — Python caller
              繼承 Claude Code / Antigravity 子程序 env vars; Editor in-process DetectEnvMarker
              在 long-running Editor process 永遠抓不到, 改成 caller-side detect 後傳進 args。
    數值影響: 找到對應 env var → 回 "claude-code" / "antigravity" / "gemini"; 都無 → "unknown"
    """
    import os
    if os.environ.get("CLAUDECODE"):
        return "claude-code"
    if os.environ.get("ANTIGRAVITY_SESSION") or os.environ.get("ANTIGRAVITY_USER_ID"):
        return "antigravity"
    if os.environ.get("GEMINI_API_KEY") or os.environ.get("GEMINI_SESSION"):
        return "gemini"
    return "unknown"


def cmd_submit(args: argparse.Namespace) -> int:
    """submit：ensure_idle → write queue.json → write pending.trigger。"""
    # cmd_type 入口 normalize — 理由同 cmd_run（讓 Tavern 預檢與顯示都吃正名）
    args.cmd_type = normalize_cmd_type(args.cmd_type)
    arg_pairs = parse_kv_pairs(args.arg or [])
    # NOTE: _caller_env_marker auto-inject 已移到 append_cmd 統一處理 (cover submit + run + recompile 三路)

    # --arg-file 展開（讀檔失敗 → 直接擋，不寫 queue）
    ok, err = expand_arg_files(arg_pairs, getattr(args, "arg_file", []))
    if not ok:
        print_fail_verdict(f"✗ --arg-file 展開失敗：\n  {err}")
        return 2

    # --arg-stdin 展開（讀 stdin 全文塞進指定 key；見 expand_arg_stdin 區塊註解）
    ok, err = expand_arg_stdin(arg_pairs, getattr(args, "arg_stdin", None))
    if not ok:
        print_fail_verdict(f"✗ --arg-stdin 展開失敗：\n  {err}")
        return 2

    # ⚠ 預檢**之前**先把 CLI 的 --persona 併進 arg_pairs（2026-08-01 P0b 接線修）：
    #   append_cmd 也會做同一件事，但它跑在預檢**之後**。而 Tavern 預檢裡的身分解析
    #   （tier 1 = 顯式宣告）讀的就是 arg_pairs —— 順序顛倒的話，明明帶了 --persona 的呼叫
    #   會被當成「沒有宣告」，落到 lock 反查，然後在多 persona 在線時被判 ambiguous 擋下。
    #   實測：少了這行，我自己帶 --persona basecamp 的貼文被自己的新解析器誤擋。
    #   兩處都填是刻意的 —— append_cmd 服務所有 cmd type，這裡只為預檢補時序。
    if _PERSONA and not str(arg_pairs.get("persona") or "").strip():
        arg_pairs["persona"] = _PERSONA

    # 區塊職責：Tavern Cmd 的 client-side 預檢
    # 物理意義：Editor round-trip 約 1s 才報錯；client 預檢 < 0.01s 就能擋下 typo / alias 錯
    # 數值影響：失敗 → 立刻 return 2 不寫 queue，不污染 _last_op.md
    if args.cmd_type == "Tavern":
        ok, err = _tavern_cmd.validate_args(arg_pairs)
        if not ok:
            print_fail_verdict(f"✗ Tavern client-side 預檢失敗：\n  {err}")
            return 2

    # 區塊職責：寫入前先確認沒有前一輪殘留的 trigger
    # 物理意義：避免 Python 把新 trigger 蓋到 Editor 還沒處理完的舊批次上，造成混批
    # 數值影響：若 ack_timeout 到還沒 idle → 直接 SystemExit，使用者必須清理才能繼續
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)

    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py submit {args.cmd_type}")

    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {trigger_path().name}")
    return 0


def cmd_wait(args: argparse.Namespace) -> int:
    """wait：等 .running 消失 → 重讀 queue 判定 cmd 結果。"""
    cmd_id = args.id
    output_file = Path(args.output_file) if args.output_file else None
    timeout_sec = args.timeout
    poll_interval = args.poll_interval

    print(f"Waiting for {cmd_id}...")
    if output_file:
        print(f"  Watching output: {output_file}")
    print(f"  Timeout: {timeout_sec}s   Poll: every {poll_interval}s")

    deadline = time.time() + timeout_sec
    saw_running = False

    # 區塊職責：先觀察 trigger 狀態變化（pending → running → idle）
    # 物理意義：
    #   - pending 階段：trigger 寫好但 Watcher 還沒接手（Editor 沒開 / Watcher 關 / 處理中其他批次）
    #   - running 階段：Watcher 已接手，正在執行
    #   - idle（即沒有 trigger 也沒有 running） + cmd 不在 queue → OneShot 成功
    while time.time() < deadline:
        state = trigger_state()
        if state == "running" and not saw_running:
            saw_running = True
            print(f"  ... Editor picked up the trigger (now running)")

        if state == "idle":
            # 一輪結束 → 判定本筆 cmd
            queue = load_queue()
            cmd = find_cmd(queue, cmd_id)
            if cmd is None:
                # 區塊職責：消失後的判定 —— 權威來源是 result 檔（成對改 2026-08-07）。
                # 物理意義：Editor 端失敗的 OneShot 也會自動出隊，「消失」只代表結束；
                #          成功或失敗看 _cmd_results/<id>.json。找不到 result 檔才走舊推論
                #          （舊版 Editor / result 落檔失敗），並把「這是推論」講出來。
                verdict = read_cmd_result(cmd_id)
                if verdict is not None:
                    if verdict.get("result") == "Failed":
                        err = verdict.get("error") or "(no error message)"
                        print_fail_verdict(f"  ✗ Cmd failed（Editor 已自動出隊）: {err}")
                        report_path = verdict.get("error_report")
                        print_cmd_error_report(cmd_id)
                        if report_path:
                            print(f"  📄 詳細錯誤檔：{report_path}")
                        return 2
                    print(f"  ✓ Cmd completed → Success（result 檔判定，非推論）")
                    if output_file:
                        if output_file.exists():
                            print(f"  ✓ Output file exists: {output_file}")
                        else:
                            print(f"  ⚠ Output file NOT found: {output_file}")
                    return 0
                # ── fallback：無 result 檔（舊版 Editor）──
                # T02 race fix (2026-05-16 basecamp): 檢 cmd_type 對應 last_op 檔案的 fail marker.
                cmd_type = getattr(args, "cmd_type", None)
                submit_time = getattr(args, "submit_time", None)
                if cmd_type and submit_time is not None:
                    # T-LastOp-CmdId: 帶本筆 cmd_id 進去比對檔內 stamp — 別的 session
                    # 同窗口寫的 fail marker（stamp 是別人的 id）會被判 unknown 不誤報
                    status, err = check_cmd_result_file(cmd_type, submit_time, cmd_id=cmd_id)
                    if status == "failed":
                        print_fail_verdict(f"  ✗ Cmd disappeared from queue BUT output file shows failure:\n  {err}")
                        print_cmd_error_report(cmd_id)
                        return 2
                print(f"  ✓ Cmd disappeared from queue → Success (推論：無 result 檔的舊版 fallback)")
                if output_file:
                    if output_file.exists():
                        print(f"  ✓ Output file exists: {output_file}")
                    else:
                        print(f"  ⚠ Output file NOT found: {output_file}")
                return 0

            # 還在 queue → 看 LastRunResult
            result = cmd.get("LastRunResult")
            if result == "Success":
                print(f"  ✓ Repeatable cmd ran successfully (RunCount={cmd.get('RunCount', 0)})")
                return 0
            if result == "Failed":
                err = cmd.get("LastRunError") or "(no error message)"
                print_fail_verdict(f"  ✗ Cmd failed: {err}")
                print_cmd_error_report(cmd.get("Id"))
                # 區塊職責：失敗的 OneShot 預設會留在 queue.json（runner 設計如此），
                #          但這會讓「下一次 submit 時整個 batch 把舊的失敗 cmd 一起重跑」
                #          → agent / 人類體感「每次都卡住」。
                # 物理意義：失敗訊息已透過 stderr 印出 + return code 2 通知 caller，
                #          原始記錄不再有保留價值；移除 queue 條目讓下一輪乾淨。
                # 數值影響：寫回 queue.json，刪除單筆。--keep-failed 可關閉此自動清理。
                if not getattr(args, "keep_failed", False):
                    remove_cmd_from_queue(cmd_id)
                    print(f"  ↳ removed failed cmd from queue (use --keep-failed to retain)",
                          file=sys.stderr)
                    sys.stderr.flush()
                return 2

            # 沒 trigger 但 cmd 仍在且 LastRunResult 是 None → trigger 被 Watcher 接走前可能 race，
            # 也可能 Watcher 沒啟動。再等一輪。
            time.sleep(poll_interval)
            continue

        time.sleep(poll_interval)

    print_fail_verdict(f"  ✗ Timeout after {timeout_sec}s — Editor not running, "
                       f"or UCL_AgentCommandWatcher disabled?")
    return 3


def _commit_catchup_cursor_if_post(args, arg_pairs: dict) -> None:
    """兩階段提交・階段二：一則 tavern post 成功 → 把 brief §8 記下的 pending 升成 last_seen_ts。

    區塊職責：實作「**開口＝確認讀完**」（Tim 2026-07-31 拍板，apex-one 形式化為兩階段提交）。
    物理意義：brief §8 只是把訊息攤在你面前（階段一寫 pending，不動 cursor）；
             真正的「我讀了」證據是你開口說話。這裡不推「現在」——
             提交的是 **brief 當時涵蓋到的截止點**，所以發文前三秒同事剛講的話不會被吞掉。
    數值影響：只在 op=post 成功後、且該 persona 有 pending 時動一次；提交是單調的。
             失敗方向刻意選「不提交」——早安半途掛掉 → 明天重看一次（重看不痛，吞掉無感）。
    """
    try:
        if args.cmd_type.lower() != "tavern" or arg_pairs.get("op", "").lower() != "post":
            return
        persona = arg_pairs.get("persona", "")
        if not persona:
            return
        committed = _tavern_cmd.cursor_commit_pending(persona)
        if committed:
            print(f"  📍 catch-up cursor 提交：{persona} → {committed}（開口＝確認讀完）")
    except Exception as e:
        # 不擋主流程，但**不靜默** —— 這條線斷了會讓 🆕 永遠累積，那正是本次要治的病
        print(f"  ⚠ catch-up cursor 提交失敗（post 本身成功）：{e}")


def cmd_run(args: argparse.Namespace) -> int:
    """run = submit + wait（+ Tavern op=post 可選同步握手等回覆）。"""
    # 區塊職責：cmd_type 在入口就 normalize（alias + Cmd_ 前綴剝除），不留到 append_cmd 才做。
    # 物理意義：下游全部吃 args.cmd_type — wait-reply 政策、Tavern 參數預檢（== "Tavern" 比對）、
    #          fail-detection、banner。晚 normalize 會讓 'Cmd_ChatTavern' 繞過 Tavern 客端預檢
    #          白跑一趟 Editor（summit 2026-07-31 實測），且 Submitted 訊息印舊名誤導呼叫端。
    # 數值影響：append_cmd 內的 normalize 保留（對外部 caller 兜底），對已正名的值是冪等 no-op。
    args.cmd_type = normalize_cmd_type(args.cmd_type)
    submit_args = argparse.Namespace(
        cmd_type=args.cmd_type, mode=args.mode, description=args.description,
        arg=args.arg,
        ack_timeout=args.ack_timeout, poll_interval=args.poll_interval,
    )
    # 自行走 submit 流程以便保留 cmd_id
    arg_pairs = parse_kv_pairs(args.arg or [])

    # --arg-file 展開（讀檔失敗 → 直接擋，不寫 queue）
    ok, err = expand_arg_files(arg_pairs, getattr(args, "arg_file", []))
    if not ok:
        print_fail_verdict(f"✗ --arg-file 展開失敗：\n  {err}")
        return 2

    # --arg-stdin 展開（讀 stdin 全文塞進指定 key；見 expand_arg_stdin 區塊註解）
    ok, err = expand_arg_stdin(arg_pairs, getattr(args, "arg_stdin", None))
    if not ok:
        print_fail_verdict(f"✗ --arg-stdin 展開失敗：\n  {err}")
        return 2

    # 區塊職責：wait-reply 決策 —— shim（--arg wait-reply=N 視同 script flag）＋ 預設值政策。
    # 物理意義：「哪些 op 算跟人交流、要等多久」是 Tavern 的**業務規則**不是 RPC 機制，
    #          已抽到 tavern_cmd（見該模組 resolve_wait_reply / promote_wait_reply_arg）。
    # 數值影響：顯式 --wait-reply > shim promote 出來的值 > 依 op/meta 的預設；
    #          進場與查詢類 op 一律強制 0（在 resolve_wait_reply 內覆寫，連顯式值也蓋）。
    _promoted = _tavern_cmd.promote_wait_reply_arg(arg_pairs)
    _explicit = getattr(args, "wait_reply", None)
    if _explicit is None:
        _explicit = _promoted
    args.wait_reply = _tavern_cmd.resolve_wait_reply(args.cmd_type, arg_pairs, _explicit)

    # ⚠ 同 cmd_submit：預檢前先併入 CLI 的 --persona，否則顯式宣告在 tier 1 看不到。
    #   **這個檢查有兩處**（cmd_submit 一處、cmd_run 一處，因為 cmd_run 自己 inline submit）——
    #   我第一次只補了 cmd_submit，於是 `run` 路徑照樣誤擋（而日常都走 run，等於沒修）。
    #   同一段邏輯散在兩個地方就是這種漏改的溫床，收攏是 runcmd 六模組拆分的待辦之一。
    if _PERSONA and not str(arg_pairs.get("persona") or "").strip():
        arg_pairs["persona"] = _PERSONA

    # Tavern client-side 預檢（cmd_run 自己 inline submit，需獨立呼叫一次；
    # cmd_submit 的同名檢查只服務 `submit` 子命令）
    if args.cmd_type == "Tavern":
        ok, err = _tavern_cmd.validate_args(arg_pairs)
        if not ok:
            print_fail_verdict(f"✗ Tavern client-side 預檢失敗：\n  {err}")
            return 2

    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    # T02 race fix (2026-05-16): 記 submit time, cmd_wait 用此判 last_op 檔 mtime 是不是本次寫的
    submit_time = time.time()
    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py run {args.cmd_type}")
    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {trigger_path().name}")
    print(f"  → Auto-Watcher should pick it up within ~1s. "
          f"If not: check Unity Editor is open and Watcher is enabled.")

    wait_args = argparse.Namespace(
        id=cmd_id, output_file=args.output_file,
        timeout=args.timeout, poll_interval=args.poll_interval,
        keep_failed=getattr(args, "keep_failed", False),
        # T02 race fix (2026-05-16): 帶 cmd_type + submit_time 給 cmd_wait 做
        # fail-detection (cmd_is_None 路徑檢對應 last_op 檔)
        cmd_type=args.cmd_type,
        submit_time=submit_time,
    )
    rc = cmd_wait(wait_args)
    if rc != 0:
        return rc

    # T28 work-mode banner plumb to caller stdout (crest-001 QA 2026-05-14) → 實作在 tavern_cmd
    if args.cmd_type.lower() == "tavern" and arg_pairs.get("op", "").lower() == "post":
        _tavern_cmd.print_work_mode_banner(arg_pairs.get("room", ""))
    # 兩階段提交・階段二：post 成功了 → 開口＝確認讀完（見 _commit_catchup_cursor_if_post）
    # ⚠ 掛這裡而不是 cmd_wait 的成功分支：那裡沒有 arg_pairs（實測 NameError），
    #   而且 cmd_wait 是**所有 cmd type 共用**的等待器 —— tavern 專屬邏輯不該長在通用管線裡。
    _commit_catchup_cursor_if_post(args, arg_pairs)

    # ─── Tavern 同步握手（僅 op=post）─────────────────────────────────
    # 區塊職責：A 發完訊息後 client-side polling messages.jsonl 等對方回覆
    # 物理意義：以「同步握手」緩解「agent turn 結束後就聾了」的問題；對方在 wait
    #          window 內回 → A 立刻看到，turn 不結束；timeout 或被使用者中止 → 安靜返回
    # 數值影響：本機 0.5Hz polling，不過 Editor queue；不寫任何 server 端 wait 條目
    if (
        args.cmd_type.lower() == "tavern"
        and arg_pairs.get("op", "").lower() == "post"
        and getattr(args, "wait_reply", 0) > 0
    ):
        room = arg_pairs.get("room", "")
        # 區塊職責：取「我是誰」——**必須讀 alias 歸一後的 canonical 名**。
        # 血證（2026-07-31）：2026-07-31 四名歸一把 sender/sender_id/agent_id 全改成 agent 之後，
        #   這裡還在讀 "sender" → 那個 key 永遠不存在 → 每一則 op=post 都回判決碼 3
        #   「完全沒有等待」。守衛讀錯欄位 = 守衛永遠不成立，而且它「照樣有輸出」所以沒人喊。
        #   保留舊名當 fallback：alias 表若哪天沒歸一到，至少不要整個瞎掉。
        # ⚠ 規格（Tim 2026-08-04）：**wait 相關一律以 persona 為身分主體**。
        #   `agent` / `sender_id` 承載的是 agent_id，而 agent 層基本上只有 bank / token 操作才用。
        #   這裡取的是「誰在等」，語意上是人格不是帳號，所以 persona 優先。
        #   血證：只比 agent 層時，Myth/gura、Altair/apex-one、zeta/summit 這些
        #   「agent 名 ≠ persona 名」的人全部比不中（2026-08-04 Round S 實測）。
        #   後備鏈保留舊欄位：persona 沒帶時不要整條 wait 直接判 3（那是另一種靜默失效）。
        my_sender = (arg_pairs.get("persona")
                     or arg_pairs.get("agent")
                     or arg_pairs.get("sender")
                     or arg_pairs.get("sender_id") or "")
        wait_seconds = float(args.wait_reply)
        sender_filter = getattr(args, "wait_reply_from", None)
        # 區塊職責：判決碼往上傳 —— 舊版把 wait_for_tavern_reply() 的回傳直接丟掉
        # 物理意義：post 成功與「等待有沒有真的發生」是兩件事實，不能揉成同一個 exit code；
        #          但也不能像舊版那樣整個吞掉，否則修好了判決碼也傳不出這層。
        # 數值影響：正常結局（收到 / timeout / 使用者中止）→ 行程 exit 0，因為 post 本身成功；
        #          只有 3（無法判定）→ exit 3，因為「你要我等，我結構性等不成」是**被要求的操作失敗**，
        #          必須讓 caller 的錯誤處理看得到。這是「同碼失聲」的解藥：
        #          可正常結束的三種結局共用 0，唯獨「沒做到」自己一個碼。
        if not room or not my_sender:
            print(
                "  ⚠ wait-reply 無法運作 — 缺 room / sender\n"
                "     ⛔ 本次**完全沒有等待**（判決碼 3 = 無法判定，不是 timeout）\n"
                "     → 補上 --arg room=<X> --arg sender=<id> 再試"
            )
            return _handshake.WAIT_REPLY_UNAVAILABLE
        verdict = _handshake.wait_for_tavern_reply(
            room=room,
            my_sender_id=my_sender,
            timeout_sec=wait_seconds,
            sender_filter=sender_filter,
        )
        # 機器可讀的一行結論 — 讓 caller / 日後的自動檢查不必去解析人話
        verdict_name = _handshake._WAIT_REPLY_VERDICT_NAME.get(verdict, verdict)
        print(f"  [wait-reply] verdict={verdict_name} code={verdict}")
        if verdict == _handshake.WAIT_REPLY_UNAVAILABLE:
            return _handshake.WAIT_REPLY_UNAVAILABLE
    return 0




def cmd_recompile(args: argparse.Namespace) -> int:
    """
    觸發 Unity 重編 + 等到 compile 真正完成。

    流程：
      1. 記錄當前 .compile_status.json mtime（pre-mtime）
      2. submit Cmd_Recompile（Unity 收到後呼叫 CompilationPipeline.RequestScriptCompilation）
      3. 等 cmd 從 queue 消失（=Unity 已接手，但 compile 可能還沒跑完）
      4. poll .compile_status.json 直到 **mtime 推進且 in_progress=false**（= compile 真的跑完）
      5. 讀新 status 的 total_errors / total_warnings，依結果回 exit code

    ⚠ 第 4 步的「且 in_progress=false」是 2026-08-05 補的，別拿掉：
      UCL_CompileErrorTracker 在 **compilationStarted** 也會寫一次 status
      （in_progress=true / duration 0 / messages 清空）。只看 mtime 推進的話會抓到那一筆，
      然後印出「✓ Compile finished (0.0s) — errors=0, warnings=0」——
      **一個時間點正確、數字全假的綠燈**（實摔：真實結果是 7.188s / 37 warnings）。
      這跟 2026-05-22 apex-two 那筆 CS1061 被漏報成 errors=0 是同一隻，
      當時的結論寫「改用 check_compile.py 二次確認」而沒有回頭修這裡。
    """
    pre_mtime = COMPILE_STATUS_PATH.stat().st_mtime if COMPILE_STATUS_PATH.exists() else 0.0
    print(f"Recompile request — pre-compile mtime: {pre_mtime:.3f}")

    # 1) submit + ensure_idle
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    cmd_id = append_cmd(
        cmd_type="Recompile",
        mode="OneShot",
        args={"refresh": "true" if args.refresh else "false"},
        description=args.description or "recompile via run_cmd.py",
    )
    write_trigger(f"recompile {cmd_id}")
    print(f"  Submitted: {cmd_id}")

    # 2) wait for cmd queue removal — 這只代表「Unity 已接手 ExecuteAsync 並返回」
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        time.sleep(args.poll_interval)
        q = load_queue()
        if not find_cmd(q, cmd_id):
            print("  ✓ Recompile request accepted by Unity (Cmd removed from queue).")
            break
    else:
        print(f"  ⚠ Cmd_Recompile didn't leave queue within {args.timeout}s — Unity may not be running.",
              file=sys.stderr)
        return 2

    # 3) wait for compile_status.json：**mtime 推進 + in_progress=false** 才算完成
    #    只看 mtime 會抓到 compilationStarted 那一筆（in_progress=true / duration 0 / 空 messages），
    #    印出「時間點正確、數字全假」的綠燈 —— 見本函式 docstring 的血證。
    print(f"  Waiting for {COMPILE_STATUS_PATH.name} to advance past {pre_mtime:.3f} "
          f"AND report in_progress=false...")
    deadline = time.time() + args.timeout
    seen_start = False   # 只為了印一次「編譯開始了」，讓等待期間看得出進度
    while time.time() < deadline:
        if COMPILE_STATUS_PATH.exists():
            now_mtime = COMPILE_STATUS_PATH.stat().st_mtime
            if now_mtime > pre_mtime + 0.001:  # 容忍 fs 精度
                # 4) 讀新 status 報告
                try:
                    with COMPILE_STATUS_PATH.open("r", encoding="utf-8-sig") as f:
                        st = json.load(f)
                except Exception as e:
                    # 半寫狀態 parse 失敗不該當致命 —— tracker 不做 atomic 寫入，
                    # 下一輪 poll 就會讀到完整的。**這裡 return 3 會把「讀太早」誤報成「壞掉」。**
                    if time.time() < deadline:
                        time.sleep(args.poll_interval)
                        continue
                    print(f"  ⚠ failed to parse compile_status.json: {e}", file=sys.stderr)
                    return 3
                if st.get("in_progress", False):
                    # 編譯正在跑 —— 這一筆是 compilationStarted 寫的，數字還沒定案，繼續等。
                    if not seen_start:
                        seen_start = True
                        print("  … compile started (in_progress=true) — 等它跑完，不採信這一筆的數字")
                    time.sleep(args.poll_interval)
                    continue
                errors = st.get("total_errors", 0)
                warnings = st.get("total_warnings", 0)
                duration = st.get("duration_seconds", 0)
                ts = st.get("timestamp", "?")
                print(f"  ✓ Compile finished at {ts} ({duration}s) — errors={errors}, warnings={warnings}")
                if errors > 0:
                    # 印前幾條訊息給呼叫端看
                    for m in (st.get("messages") or [])[:5]:
                        print(f"    × {m.get('type', '?')} {m.get('file', '')}:{m.get('line', '')} — {m.get('message', '')}")
                    return 1
                return 0
        time.sleep(args.poll_interval)
    # 超時的兩種原因要分開講 —— 修法完全不同，講錯會把人送去查錯的地方：
    #   seen_start=False → 編譯連開始都沒有（Unity 遞延重編，最常見是 Editor 沒有焦點）
    #   seen_start=True  → 開始了但沒跑完（大型編譯／卡在 domain reload）
    if seen_start:
        print(f"  ⚠ compile 已開始但 {args.timeout}s 內沒結束（in_progress 一直是 true）。"
              f"大型編譯或卡在 domain reload —— 加大 --timeout 或稍後用 check_compile.py 查。",
              file=sys.stderr)
    else:
        print(f"  ⚠ {args.timeout}s 內 compile 連開始都沒有（status 沒推進到 in_progress=true）。"
              f"Unity 常把外部改檔的重編遞延到視窗重獲焦點 —— 把 Unity 切到前景再試。"
              f"想確認「這段時間到底有沒有編譯過」看心跳停跳台帳："
              f"check_compile.py 的 STALE 區塊會印。",
              file=sys.stderr)
    return 4


def cmd_list(args: argparse.Namespace) -> int:
    """列出 queue 內現存指令。"""
    queue = load_queue()
    cmds = queue.get("Commands", [])
    print(f"Trigger state: {trigger_state()}")
    print(f"Queue path:    {queue_path()}")
    print()
    if not cmds:
        print("(queue is empty)")
        return 0
    print(f"{'Id':<48s} {'Type':<28s} {'Mode':<12s} {'Result':<12s} {'Last Run At'}")
    print("-" * 130)
    for c in cmds:
        print(f"{(c.get('Id') or '')[:46]:<48s} "
              f"{(c.get('Type') or '')[:26]:<28s} "
              f"{(c.get('Mode') or '')[:10]:<12s} "
              f"{(c.get('LastRunResult') or '-')[:10]:<12s} "
              f"{c.get('LastRunAt') or '-'}")
    return 0


def cmd_catalog(args: argparse.Namespace) -> int:
    """顯示 commands_catalog.md 內容（若存在）。"""
    if not CATALOG_PATH.exists():
        print(f"Catalog not found: {CATALOG_PATH}")
        print("先在 UCL_AgentCommandsPage 加一筆 'ExportCommandCatalog' Cmd，或（依本專案的 UCL_Core 掛載位置）：")
        print("  python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run ExportCommandCatalog")
        return 1
    print(CATALOG_PATH.read_text(encoding="utf-8"))
    return 0


# ===========================================================
# Helpers
# ===========================================================

def find_cmd(queue: dict, cmd_id: str) -> dict | None:
    for c in queue.get("Commands", []):
        if c.get("Id") == cmd_id:
            return c
    return None


def parse_kv_pairs(items: list[str]) -> dict:
    """把 ['key=value', 'k2=v2'] 轉成 dict。"""
    out = {}
    for item in items:
        if "=" not in item:
            print(f"[run_cmd] ignoring malformed --arg (no =): {item}", file=sys.stderr)
            continue
        k, v = item.split("=", 1)
        out[k.strip()] = v.strip()
    return out


# ===========================================================
# Backtick-loss guard（T-Backtick-Guard, Tim 2026-06-12 拍板）
# ===========================================================
# 區塊職責：把 stdin 全文塞進指定的 arg key（--arg-stdin body <<'EOF' … EOF）。
# 物理意義：body 完全不經過 argv/shell 引用 —— 反引號、$、引號、換行都不會被 shell 解讀。
#          這不是「比較安全」，是**沒有出錯的物理路徑**（crest-001 三審用語）。
#          取代 2026-07-29 移除的 backtick-loss guard：與其在下游偵測污染，不如在上游關掉污染管道。
# 跨 shell 注意：heredoc 只在 Bash 類 shell 可用；**PowerShell 沒有 heredoc 管線等價寫法**
#          → PowerShell 環境請改用 --arg-file（先寫檔再讀）。
# 數值影響：stdin 為空 → 視為錯誤（避免靜默送出空 body）；只允許指定一個 key（stdin 單一串流）。
def expand_arg_stdin(arg_pairs: dict, key: "str | None") -> tuple[bool, str]:
    if not key:
        return True, ""
    if sys.stdin is None or sys.stdin.isatty():
        return False, (f"--arg-stdin {key} 需要從 stdin 餵內容，但 stdin 是終端機（沒有 pipe/heredoc）。\n"
                       f"  Bash: --arg-stdin {key} <<'EOF' … EOF   /   PowerShell: 改用 --arg-file {key}=<path>")
    data = sys.stdin.read()
    if not data.strip():
        return False, f"--arg-stdin {key} 讀到空內容（stdin 沒有資料）。"
    arg_pairs[key] = data.rstrip("\n")
    return True, ""


def expand_arg_files(arg_pairs: dict, arg_file_items: list[str]) -> tuple[bool, str]:
    """--arg-file key=path → 讀檔內容塞進 arg_pairs[key]（UTF-8）。回 (ok, err)。"""
    for item in (arg_file_items or []):
        if "=" not in item:
            return False, f"--arg-file 格式錯誤（缺 =）: {item}"
        k, path = item.split("=", 1)
        p = Path(path.strip())
        if not p.is_file():
            return False, f"--arg-file {k.strip()}: 檔案不存在 → {p}"
        try:
            arg_pairs[k.strip()] = p.read_text(encoding="utf-8")
        except Exception as e:
            return False, f"--arg-file {k.strip()}: 讀檔失敗 → {e}"
    return True, ""


# ===========================================================
# CLI
# ===========================================================

def add_common_submit_args(p: argparse.ArgumentParser) -> None:
    p.add_argument("--mode", default="OneShot", choices=["OneShot", "Repeatable"])
    p.add_argument("--arg", action="append", default=[],
                   help="Arg as key=value (repeatable)")
    p.add_argument("--arg-file", action="append", default=[],
                   help="Arg as key=<filepath> — 值從檔案讀 (UTF-8, repeatable)。"
                        "PowerShell 環境傳長文/含反引號內容的推薦通道（PS 無 heredoc）。")
    p.add_argument("--arg-stdin", default=None, metavar="KEY",
                   help="把 stdin 全文當成該 arg 的值。Bash 傳長文的首選："
                        "--arg-stdin body <<'EOF' … EOF —— body 不經 argv，shell 元字符一律不解讀。")
    p.add_argument("--description", default=None)
    p.add_argument("--ack-timeout", type=float, default=DEFAULT_ACK_TIMEOUT,
                   help=f"Seconds to wait for previous batch to finish before submitting "
                        f"(default {DEFAULT_ACK_TIMEOUT:.0f}s)")
    p.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL,
                   help=f"Polling interval (default {DEFAULT_POLL_INTERVAL:.1f}s)")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Agent Command CLI — submit / wait / run / list / catalog (lock-file aware)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    # agent-command-pipeline-parallelize T05: --agent-id 切 per-agent queue/trigger
    # 物理意義: 帶值 → 整串當 persona 資料夾名 → queues/<X>/queue.json（Tim 2026-08-01 選項 b）
    #          沒帶 → queues/anonymous/（最外層共用 queue.json 已廢除）
    # ⚠ 已被 --persona 取代：本旗標不報錯但不做字串轉譯 —— 打 --agent-id ame-sw 會長出
    #   queues/ame-sw/ 這種資料夾，它是**遷移待辦的可見形式**，不是正常用法。
    # 用途: 多 agent 並行 (Claude/Antigravity/Gemini/Zeta) 各自獨立 queue, 互不阻塞
    parser.add_argument("--agent-id", default=None,
                        help="[已被 --persona 取代] 整串當資料夾名 → queues/<X>/queue.json；沒帶 → queues/anonymous/。"
                             "不報錯但不轉譯：看到 queues/ame-sw/ 這種資料夾就表示該 caller 還沒改。")
    # agent-command-pipeline-parallelize T06: 同 persona 內並行子通道
    # 物理意義: 同一 --agent-id (或 default) 的 queue 是串行的 (per-agent IsRunning 防 write race);
    #          --lane 在自己的 persona 資料夾內開獨立子通道檔 → queue id = '<persona>/<lane>' → 與本命 queue 並行不阻塞。
    # 用途: 前一筆長 cmd (e.g. 啟動遊戲) 還在跑, 帶 --lane 送讀畫面等快 cmd 不必等它結束。
    parser.add_argument("--lane", default=None,
                        help="同 persona 並行子通道。queue id = '<persona>/<lane>' → queues/<persona>/queue-<lane>.json 獨立 running-lock, "
                             "與 base / default queue 並行不阻塞。前一筆長 cmd 沒跑完時插一筆快 cmd 用。")
    parser.add_argument("--parallel", action="store_true",
                        help="= --lane parallel 的捷徑 (固定 'parallel' 子通道)。")
    # cmd-identity P1: 顯式身分宣告（Tim 2026-07-31 拍板「建議可以要求帶 persona 參數，
    #   沒有的情況才嘗試解析」；交接 seq 14112 / 拍板 seq 14118）
    # 物理意義: 讓每一筆 Cmd 都知道**是誰派的**。同時做兩件事 ——
    #   ① 決定 queue 路由 —— persona 就是資料夾名（→ queues/<persona>/queue.json）
    #   ② 戳進 cmd args + queue 檔頂層，讓下游（Tavern post / Treasury 記帳）不必反查猜
    # 為何是新欄位而不是改 --agent-id 的語意: --agent-id 現在同時是「身分」與「並行通道」
    #   （--lane 會產生 main~parallel），再往上疊第三種語意就沒人解得開了（kotoko 建議②）。
    parser.add_argument("--persona", default=None,
                        help="顯式宣告這筆 cmd 是誰派的（身分解析階梯 tier 1，最權威）。"
                             "同時決定 queue 路由 → queues/<persona>/queue.json；"
                             "並戳進 cmd args 讓下游不必反查 session lock 去猜。")
    sub = parser.add_subparsers(dest="action", required=True)

    # submit
    p_submit = sub.add_parser("submit", help="Add a cmd to queue.json + write pending.trigger")
    p_submit.add_argument("cmd_type", help="Cmd Type (e.g. ResolveAssetReferences)")
    add_common_submit_args(p_submit)
    p_submit.set_defaults(func=cmd_submit)

    # wait
    p_wait = sub.add_parser("wait", help="Poll trigger + queue.json until cmd completes")
    p_wait.add_argument("id", help="Cmd Id (returned by submit)")
    p_wait.add_argument("--output-file", default=None,
                        help="Optional output file to also check existence")
    p_wait.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"Max seconds to wait (default {DEFAULT_RUN_TIMEOUT})")
    p_wait.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL,
                        help=f"Seconds between polls (default {DEFAULT_POLL_INTERVAL:.1f})")
    p_wait.add_argument("--keep-failed", action="store_true",
                        help="On Failed, keep the cmd entry in queue.json (default: auto-remove "
                             "to prevent the next batch from re-running the dead entry).")
    p_wait.set_defaults(func=cmd_wait)

    # run = submit + wait
    p_run = sub.add_parser("run", help="Submit + wait in one shot")
    p_run.add_argument("cmd_type")
    add_common_submit_args(p_run)
    p_run.add_argument("--output-file", default=None)
    p_run.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT)
    p_run.add_argument("--keep-failed", action="store_true",
                       help="On Failed, keep the cmd entry in queue.json (default: auto-remove).")
    # 同步握手 — 僅對 Tavern op=post 生效；Tavern post 預設 20s 等回覆，其他 cmd 預設 0（關）
    p_run.add_argument("--wait-reply", type=float, default=None,
                       help="Tavern op=post 後 client-side polling messages.jsonl 等對方回覆的秒數 "
                            "(默認 Tavern op=post=20，其他 cmd=0)。0 = 不等。對方在窗口內回覆 → "
                            "立刻印出並退出；timeout / 從酒館頁中止 → 安靜返回。")
    p_run.add_argument("--wait-reply-from", default=None,
                       help="只認指定 sender_id 的回覆（譬如 'gemini-da-xiaojie'），其他人發言不觸發退出。")
    p_run.set_defaults(func=cmd_run)

    # recompile = submit Cmd_Recompile + wait for compile_status.json mtime to advance
    p_rc = sub.add_parser("recompile",
                          help="Trigger Unity recompile + wait until .compile_status.json updates")
    p_rc.add_argument("--no-refresh", dest="refresh", action="store_false",
                      help="Skip AssetDatabase.Refresh() (only call RequestScriptCompilation)")
    p_rc.set_defaults(refresh=True)
    p_rc.add_argument("--description", default=None)
    p_rc.add_argument("--ack-timeout", type=float, default=DEFAULT_ACK_TIMEOUT)
    p_rc.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL)
    p_rc.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                      help=f"Max seconds to wait for compile to finish (default {DEFAULT_RUN_TIMEOUT})")
    p_rc.set_defaults(func=cmd_recompile)

    # list
    p_list = sub.add_parser("list", help="List commands currently in queue.json")
    p_list.set_defaults(func=cmd_list)

    # catalog
    p_cat = sub.add_parser("catalog", help="Show commands_catalog.md content")
    p_cat.set_defaults(func=cmd_catalog)

    args = parser.parse_args()
    # agent-command-pipeline-parallelize T06: --lane / --parallel 在 base agent-id 上疊並行子通道
    # 物理意義: lane 設定 → queue id = '<persona>/<lane>' → 獨立 queue 檔 + running-lock, 與本命 queue 並行不阻塞
    # 數值影響: 無 lane → 行為跟改動前完全相同 (effective = base agent_id)
    _base_agent = getattr(args, "agent_id", None)
    _lane = getattr(args, "lane", None) or ("parallel" if getattr(args, "parallel", False) else None)
    # cmd-identity P1: --persona 在沒帶 --agent-id 時兼任路由來源。
    #   優先序刻意是 --agent-id > --persona：帶了 --agent-id 表示 caller 明確要那條通道
    #   （e.g. basecamp-sw 看直播專用 queue），身分仍由 --persona 宣告並戳進 args ——
    #   **路由與身分是兩件事，只是預設情況下同一個值**。
    _persona = (getattr(args, "persona", None) or "").strip() or None
    _base_agent = _base_agent or _persona
    # lane 是**檔名後綴**不是 id 的一部分：queues/<persona>/queue-<lane>.json。
    # 分隔符從舊制的 '~' 改成 '/' —— 它現在對應真實的目錄層級，不是一個編碼在字串裡的假層級。
    _effective_agent = f"{_base_agent or ANONYMOUS_QUEUE_ID}/{_lane}" if _lane else _base_agent
    # 設 global _AGENT_ID 讓 queue_path/trigger_path 等函式拿 dynamic value
    set_agent_id(_effective_agent)
    set_persona(_persona)
    return args.func(args)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
