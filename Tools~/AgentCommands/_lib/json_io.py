"""
json_io — Atomic Read-Modify-Write for JSON state files.

Solves Class A "IO race" root cause (per meadow ws-...c73e analysis):
- multiple Python processes touching same json file = last-write-wins overwrite
- TOCTOU pattern (basecamp split-brain check / cross-session add-worker / quota edits)
- partial-write corruption on crash mid-write

Mechanism: filelock advisory lock (already in deps, no install needed) + atomic os.replace
swap from tmp file. Lock held for entire read → mutate → write window so check-then-act
sequences are race-free.

Usage:
    from AgentCommands._lib.json_io import atomic_rmw

    def add_worker(session_id, persona):
        def mutate(d):
            for s in d.get("active_sessions", []):
                if s["id"] == session_id:
                    s.setdefault("workers", []).append({"persona": persona})
            return d
        atomic_rmw(WORK_SESSIONS_PATH, mutate)

Key properties:
- Lock file lives next to target: <path>.lock (auto-created, never deleted; safe)
- Default 30s timeout. Stale lock from crashed process auto-released by OS on file close;
  if process truly hung (impossible release), timeout raises Timeout exception.
- Atomic write via tmp file + os.replace (POSIX + Windows both honor this as atomic
  rename for same-volume targets).
- Returns final dict for caller convenience.

Caveats:
- Single-file lock granularity (MVP). Per-key lock left to future if hot-spot emerges.
- mutator should be a pure function over dict (returning the modified dict). Side effects
  outside dict (e.g. log writes) are OK but won't roll back if write fails.
- Do NOT call atomic_rmw recursively on same path inside a mutator (deadlock).
"""

from __future__ import annotations

import json
import os
import pathlib
import tempfile
from typing import Any, Callable, Optional

import filelock

# Default timeout for lock acquisition. Stale lock detection is OS-dependent; on Windows
# filelock uses a heuristic. 30s is generous enough that legitimate IO under contention
# completes, but short enough that a true deadlock surfaces fast.
DEFAULT_LOCK_TIMEOUT_SEC = 30.0

# JSON dump defaults — match existing project convention (2-space indent, UTF-8, no ASCII
# escape so Chinese / emoji round-trip cleanly).
_DUMP_KW = {"ensure_ascii": False, "indent": 2}


def _lock_path_for(target: pathlib.Path) -> pathlib.Path:
    """Compute lock file path next to target. Lock file is sibling, suffix `.lock`."""
    return target.with_suffix(target.suffix + ".lock")


def atomic_rmw(
    path: os.PathLike | str,
    mutator: Callable[[dict], dict],
    default: Optional[dict] = None,
    timeout: float = DEFAULT_LOCK_TIMEOUT_SEC,
    create_parents: bool = True,
) -> dict:
    """Atomically Read-Modify-Write a JSON file.

    Args:
        path: Target JSON file path.
        mutator: Function taking current dict, returning new dict (or mutating in place
            and returning same dict). Called with file lock held.
        default: Initial value when file doesn't exist. Defaults to {}.
        timeout: Max seconds to wait for lock. Raises filelock.Timeout if exceeded.
        create_parents: If True, mkdir -p parent dirs before write.

    Returns:
        The final dict after mutation (same as written to disk).

    Raises:
        filelock.Timeout: lock acquisition exceeded timeout.
        json.JSONDecodeError: existing file is malformed (mutator never called).
        OSError: filesystem-level failure during atomic rename.
    """
    target = pathlib.Path(path)
    if create_parents and target.parent != pathlib.Path("."):
        target.parent.mkdir(parents=True, exist_ok=True)

    lock_p = _lock_path_for(target)
    lock = filelock.FileLock(str(lock_p), timeout=timeout)

    with lock:
        # Read current state (or default).
        if target.exists():
            with open(target, "r", encoding="utf-8") as f:
                data = json.load(f)
        else:
            data = {} if default is None else dict(default)

        # Apply mutation under lock.
        new_data = mutator(data)
        if new_data is None:
            # Treat None as "no change" — write back original (idempotent guarantee).
            new_data = data

        # Atomic write: tmp file in same dir → os.replace (atomic on POSIX + Windows
        # for same-volume rename).
        tmp_fd, tmp_name = tempfile.mkstemp(
            prefix=f"{target.stem}.", suffix=".tmp", dir=str(target.parent)
        )
        try:
            with os.fdopen(tmp_fd, "w", encoding="utf-8") as f:
                json.dump(new_data, f, **_DUMP_KW)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_name, target)
        except Exception:
            # Cleanup tmp file on failure (replace already moved it on success).
            try:
                os.unlink(tmp_name)
            except OSError:
                pass
            raise

        return new_data


def atomic_read(
    path: os.PathLike | str,
    default: Optional[dict] = None,
    timeout: float = DEFAULT_LOCK_TIMEOUT_SEC,
) -> dict:
    """Atomic read with lock — for callers that need consistent snapshot but won't write.

    Uses same lock as atomic_rmw so a snapshot reflects a fully-written state, never
    a partial mid-write view.
    """
    target = pathlib.Path(path)
    if not target.exists():
        return {} if default is None else dict(default)

    lock_p = _lock_path_for(target)
    lock = filelock.FileLock(str(lock_p), timeout=timeout)
    with lock:
        with open(target, "r", encoding="utf-8") as f:
            return json.load(f)
