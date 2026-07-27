#!/usr/bin/env python3
"""process_registry.py — Python 端常駐 process 註冊中心 (對偶 C# UCL_ProcessRegistryService)。

區塊職責: 讓 Python 端啟動的常駐 process (daemon / watch session ...) 也能以共通格式註冊,
          與 C# 端共用同一目錄 + 同一 json schema — 統一管理處 = AgentCommands/_process_registry/,
          UI = Unity 的 UCL_ProcessAdminPage (Tim 2026-07-27 拍板「確保有一個統一的管理處」)。
物理意義: 每 process 一檔 <tag>_<pid>.json; 身分 = PID + process name + start time (UTC) 三重比對 —
          PID 會被 OS 回收再發, 光 PID 不足以斷定同一顆; kill 前必過身分驗證 (防誤殺)。
數值影響: process_name 存「無副檔名 basename」(python 而非 python.exe) — 對齊 C# Process.ProcessName
          語意, 兩端互相 Validate 才不會把對方的記錄誤判成 PidReused。
          start_time 容差 2.0s (對齊 C# START_TIME_TOLERANCE_SEC)。

CLI:
    python process_registry.py list [--json]        # 列出全部記錄 + 即時身分狀態 (--json 給 agent parse)
    python process_registry.py cleanup              # 清 Dead / PidReused 殘檔
    python process_registry.py kill-tag <tag>       # kill 所有同 tag (身分驗證通過的才動手)

# Source: ucl_core:Tools~/AgentCommands/process_registry.py
"""
from __future__ import annotations

import datetime
import json
import os
import sys
import time
from pathlib import Path

try:
    import psutil
except ImportError:  # psutil 缺 → 全部 fail-soft 成 unknown (不阻塞 caller 主流程)
    psutil = None

# 對齊 C# UCL_ProcessRegistryService 常數
START_TIME_TOLERANCE_SEC = 2.0
REGISTRY_DIR_RELATIVE = Path("AgentCommands") / "_process_registry"

# 身分狀態 (小寫字串, python 慣例; 語意對齊 C# UCL_ProcessStatus)
ALIVE, DEAD, PID_REUSED, UNKNOWN = "alive", "dead", "pid_reused", "unknown"


# ===========================================================
# 路徑 — repo root 解析 (repo-walk, 對齊其他 Tools~ 腳本慣例)
# ===========================================================
def _repo_root() -> Path:
    """從本檔往上走找 .git (跳過 submodule gitlink 檔) — 對齊 screenstream_daemon 的 data root 慣例。"""
    p = Path(__file__).resolve().parent
    last_dir_git = None
    while p != p.parent:
        git = p / ".git"
        if git.is_dir():
            last_dir_git = p
        p = p.parent
    return last_dir_git or Path.cwd()


def registry_dir(repo_root: Path | None = None) -> Path:
    return (repo_root or _repo_root()) / REGISTRY_DIR_RELATIVE


# ===========================================================
# 身分擷取
# ===========================================================
def _proc_identity(pid: int) -> tuple[str, float] | None:
    """回 (process_name_stem, start_epoch)。取不到 (無 psutil / 無此 pid / 權限) 回 None。"""
    if psutil is None:
        return None
    try:
        p = psutil.Process(pid)
        # 無副檔名 basename — 對齊 C# Process.ProcessName ("python" 而非 "python.exe")
        name = Path(p.name()).stem
        return name, float(p.create_time())
    except psutil.NoSuchProcess:
        return None
    except Exception:
        return None


def _epoch_to_iso_utc(epoch: float) -> str:
    return datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def _iso_to_epoch(iso: str) -> float | None:
    try:
        return datetime.datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()
    except (ValueError, AttributeError):
        return None


def _sanitize_tag(tag: str) -> str:
    return "".join(c if (c.isalnum() or c in "-_") else "_" for c in tag)


# ===========================================================
# Register / Unregister
# ===========================================================
def register_self(tag: str, description: str = "", registered_by: str = "",
                  allow_multiple: bool = False, skip_if_exists: bool = False,
                  repo_root: Path | None = None) -> Path | None:
    """把「自己 (目前 python process)」註冊進共用 registry。

    allow_multiple=False (預設, 對齊 C# Register) = singleton: 先 kill 既存同 tag (排除自己)。
    skip_if_exists=True: 同 (tag, pid) 記錄檔已存在 (e.g. C# spawn 端已註冊) → no-op 不重寫
                         (保留 C# 端寫的 registered_by 出處)。
    回傳記錄檔路徑; 失敗回 None (fail-soft)。
    """
    return register_pid(os.getpid(), tag, description=description, registered_by=registered_by,
                        allow_multiple=allow_multiple, skip_if_exists=skip_if_exists,
                        repo_root=repo_root)


def register_pid(pid: int, tag: str, description: str = "", registered_by: str = "",
                 allow_multiple: bool = False, skip_if_exists: bool = False,
                 repo_root: Path | None = None) -> Path | None:
    """註冊指定 pid (python spawn 別的 child 時用)。schema 與 C# UCL_ProcessRecord 完全一致。"""
    tag = _sanitize_tag(tag)
    if not tag or pid <= 0:
        return None
    try:
        rdir = registry_dir(repo_root)
        rdir.mkdir(parents=True, exist_ok=True)
        path = rdir / f"{tag}_{pid}.json"
        if skip_if_exists and path.exists():
            return path
        if not allow_multiple:
            n = kill_all_by_tag(tag, exclude_pid=pid, repo_root=repo_root)
            if n:
                print(f"[process_registry] register({tag}) singleton: 收掉 {n} 顆既存同 tag process",
                      file=sys.stderr)
        ident = _proc_identity(pid)
        name, start_epoch = ident if ident else ("", 0.0)
        cmdline = ""
        if psutil is not None:
            try:
                cmdline = " ".join(psutil.Process(pid).cmdline())
            except Exception:
                pass
        rec = {
            "pid": pid,
            "process_name": name,
            "start_time_utc": _epoch_to_iso_utc(start_epoch) if start_epoch else "",
            "tag": tag,
            "description": description or "",
            "command_line": cmdline,
            "registered_by": registered_by or "python:process_registry",
            "registered_at_utc": _epoch_to_iso_utc(time.time()),
            "schema_version": 1,
        }
        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(rec, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        tmp.replace(path)
        return path
    except OSError as e:
        print(f"[process_registry] register fail (tag={tag}): {e}", file=sys.stderr)
        return None


def unregister(pid: int, tag: str | None = None, repo_root: Path | None = None) -> None:
    """移除記錄檔 (process 正常收掉時呼叫)。tag 缺省時按 pid 掃。"""
    try:
        rdir = registry_dir(repo_root)
        if not rdir.is_dir():
            return
        if tag:
            path = rdir / f"{_sanitize_tag(tag)}_{pid}.json"
            if path.exists():
                path.unlink()
            return
        for f in rdir.glob(f"*_{pid}.json"):
            rec = _load_record(f)
            if rec and rec.get("pid") == pid:
                f.unlink()
    except OSError as e:
        print(f"[process_registry] unregister fail (pid={pid}): {e}", file=sys.stderr)


# ===========================================================
# Query / Validate
# ===========================================================
def _load_record(path: Path) -> dict | None:
    try:
        rec = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(rec, dict):
            return None
        rec["_source_file"] = str(path)
        return rec
    except (OSError, json.JSONDecodeError, ValueError):
        return None   # 壞檔跳過不連坐 (可由管理頁 / cleanup 清)


def load_all(repo_root: Path | None = None) -> list[dict]:
    rdir = registry_dir(repo_root)
    if not rdir.is_dir():
        return []
    recs = [r for r in (_load_record(f) for f in sorted(rdir.glob("*.json"))) if r]
    recs.sort(key=lambda r: (r.get("tag", ""), r.get("pid", 0)))
    return recs


def load_all_with_status(repo_root: Path | None = None) -> list[dict]:
    """一次拿全部記錄 + 即時身分狀態 (每筆多 `status` 欄) — agent / 腳本端查詢的標準入口。

    區塊職責: 免除 caller 自己組 load_all + validate 的樣板 (Tim 2026-07-27: 讀取所有受管理 Process 資訊)。
    物理意義: status 是「呼叫當下」的即時驗證結果, 不落檔 — 每次查都重新對 OS 比身分。
    """
    recs = load_all(repo_root)
    for r in recs:
        r["status"] = validate(r)
    return recs


def validate(rec: dict) -> str:
    """身分驗證 — 對齊 C# Validate 語意: PID+name+start_time 三重比對。

    alive = 本尊; dead = PID 不存在/已退出; pid_reused = PID 易主 (絕不可 kill);
    unknown = 驗不了 (psutil 缺/權限) — 保守, kill 端拒絕動手。
    """
    pid = int(rec.get("pid", 0) or 0)
    if pid <= 0:
        return UNKNOWN
    if psutil is None:
        return UNKNOWN
    if not psutil.pid_exists(pid):
        return DEAD
    ident = _proc_identity(pid)
    if ident is None:
        # pid_exists 過但拿不到細節 — 可能剛退出 race 或權限不足
        return UNKNOWN
    name, start_epoch = ident
    rec_name = (rec.get("process_name") or "").strip()
    if rec_name and name.lower() != rec_name.lower():
        return PID_REUSED
    rec_start = _iso_to_epoch(rec.get("start_time_utc") or "")
    if rec_start is not None and abs(start_epoch - rec_start) > START_TIME_TOLERANCE_SEC:
        return PID_REUSED
    return ALIVE


# ===========================================================
# Kill / Cleanup
# ===========================================================
def kill_registered(rec: dict) -> tuple[bool, str | None]:
    """身分驗證通過 (alive) 才 kill; 成功順手移除記錄檔。回 (ok, error)。"""
    status = validate(rec)
    if status != ALIVE:
        return False, {
            DEAD: "process 已不存在 (記錄可直接清除)",
            PID_REUSED: "PID 已被別的 process 佔用 — 拒絕 kill (防誤殺)",
        }.get(status, "無法驗證 process 身分 — 拒絕 kill (保守)")
    pid = int(rec["pid"])
    try:
        p = psutil.Process(pid)
        # kill 前最後一次 start_time 複驗 (Validate 到 kill 之間的 race 窗)
        rec_start = _iso_to_epoch(rec.get("start_time_utc") or "")
        if rec_start is not None and abs(float(p.create_time()) - rec_start) > START_TIME_TOLERANCE_SEC:
            return False, "kill 前複驗失敗: start time 不吻合 (PID 已易主)"
        p.kill()
        try:
            p.wait(timeout=3.0)
        except psutil.TimeoutExpired:
            pass
    except psutil.NoSuchProcess:
        pass   # 已自己退出 — 視為成功收掉
    except Exception as e:
        return False, f"kill fail: {e}"
    src = rec.get("_source_file")
    if src:
        try:
            Path(src).unlink(missing_ok=True)
        except OSError:
            pass   # 殘檔交給 cleanup_stale
    return True, None


def kill_all_by_tag(tag: str, exclude_pid: int | None = None,
                    repo_root: Path | None = None) -> int:
    """Kill 所有同 tag 的已註冊 process (singleton guard) — 語意對齊 C# KillAllByTag。

    exclude_pid: 排除不殺 (register_self singleton 時排除自己; 另有絕不殺 os.getpid() 保險)。
    alive → kill+清檔; dead / pid_reused → 只清檔 (現任 PID 持有者絕不碰); unknown → 不動。
    """
    tag = _sanitize_tag(tag)
    killed = 0
    self_pid = os.getpid()
    for rec in load_all(repo_root):
        if (rec.get("tag") or "").lower() != tag.lower():
            continue
        pid = int(rec.get("pid", 0) or 0)
        if pid in (exclude_pid, self_pid):   # 自己絕不殺 (雙保險)
            continue
        status = validate(rec)
        if status == ALIVE:
            ok, err = kill_registered(rec)
            if ok:
                killed += 1
                print(f"[process_registry] kill_all_by_tag({tag}): killed PID {pid}", file=sys.stderr)
            else:
                print(f"[process_registry] kill_all_by_tag({tag}) PID {pid} 未動手: {err}", file=sys.stderr)
        elif status in (DEAD, PID_REUSED):
            src = rec.get("_source_file")
            if src:
                try:
                    Path(src).unlink(missing_ok=True)
                except OSError:
                    pass
        # unknown → 保守不動
    return killed


def cleanup_stale(repo_root: Path | None = None) -> int:
    """清 Dead / PidReused 殘檔。回清除數。"""
    n = 0
    for rec in load_all(repo_root):
        if validate(rec) not in (DEAD, PID_REUSED):
            continue
        src = rec.get("_source_file")
        if src:
            try:
                Path(src).unlink(missing_ok=True)
                n += 1
            except OSError:
                pass
    return n


# ===========================================================
# CLI
# ===========================================================
def _cli() -> int:
    args = sys.argv[1:]
    op = args[0] if args else "list"
    if op == "list":
        recs = load_all_with_status()
        if "--json" in args:   # machine-readable (agent 端好 parse)
            for r in recs:
                r.pop("_source_file", None)
            print(json.dumps(recs, ensure_ascii=False, indent=1))
            return 0
        if not recs:
            print("(registry 為空)")
            return 0
        for r in recs:
            print(f"{r['status']:>10} | [{r.get('tag')}] PID {r.get('pid')} | {r.get('process_name')} | "
                  f"start={r.get('start_time_utc')} | by={r.get('registered_by')}")
        return 0
    if op == "cleanup":
        print(f"清除 {cleanup_stale()} 筆失效記錄")
        return 0
    if op == "kill-tag" and len(args) >= 2:
        print(f"killed {kill_all_by_tag(args[1])} 顆 [{args[1]}]")
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(_cli())
