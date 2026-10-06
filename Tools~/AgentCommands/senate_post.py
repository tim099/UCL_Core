#!/usr/bin/env python3
"""senate_post.py — python 工具發酒館的唯一出口：交給 `senate cmd tavern-post`。

# 區塊職責
dice.py／mbti.py 的「結果同步酒館」副作用。顯示身分由 Senate 從 persona 推導（唯一推導點），
本檔只負責把 body 落成暫存檔、呼叫 senate、把三態結果翻成一行 stderr。

# 數值影響
回傳 True ＝ 確定已發（exit 0）。其餘回 False 且不丟例外（發文是副作用，不擋主流程）：
  exit 6 ＝ **確定沒發**（補發安全）／exit 7 或逾時 ＝ **不知道**（⛔ 別直接補發，先回讀）。
  兩者處置相反，stderr 的字不同形。

刻意用**扁平 sibling** 而非 `_lib/`：裸 `_lib` 在主專案的執行環境裡會被 AgentCommands/_lib 綁走。
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile


def tavern_post(persona: str, body: str, meta: dict | None = None,
                room: str = "tavern", timeout: float | None = None) -> bool:
    exe = shutil.which("senate")
    if not exe:
        print("⚠ tavern post 失敗 (主流程不受影響): PATH 上找不到 `senate`", file=sys.stderr)
        return False
    wait = 30 if timeout is None else max(1, int(timeout))
    fd, body_path = tempfile.mkstemp(prefix="tavern_post_", suffix=".md")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(body)
        cmd = [exe, "cmd", "tavern-post", "--arg", f"persona={persona}",
               "--arg-file", f"body={body_path}", "--arg", f"room={room}",
               "--arg", f"timeout={wait}"]
        if meta:
            cmd += ["--arg", "meta=" + json.dumps(meta, ensure_ascii=False)]
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                                  errors="replace", timeout=wait + 30)
        except subprocess.TimeoutExpired:
            print("⚠ tavern post 不知道有沒有發 (等不到 senate 結束)：⛔ 別直接補發，先 "
                  "`senate cmd tavern-query --arg kind=tail` 回讀", file=sys.stderr)
            return False
        if proc.returncode == 0:
            return True
        tail = (proc.stdout or "").strip().splitlines()[-3:]
        if proc.returncode == 7:
            print("⚠ tavern post 不知道有沒有發 (exit 7)：⛔ 別直接補發，先 "
                  "`senate cmd tavern-query --arg kind=tail` 回讀；" + " | ".join(tail), file=sys.stderr)
        else:
            print(f"⚠ tavern post 確定沒發 (exit {proc.returncode}，主流程不受影響)：" + " | ".join(tail),
                  file=sys.stderr)
        return False
    except Exception as e:
        print(f"⚠ tavern post exception (主流程不受影響): {e}", file=sys.stderr)
        return False
    finally:
        try:
            os.remove(body_path)
        except OSError:
            pass
