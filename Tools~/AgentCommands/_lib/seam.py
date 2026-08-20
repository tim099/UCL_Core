"""`_lib` 底下模組的共用載入器 —— 同一支檔在同一行程只會有**一份**實例。

區塊職責: 把「用檔案路徑載入 `_lib` 模組」收成單一入口，並保證實例唯一。
物理意義: 各消費端刻意用 `spec_from_file_location` 繞開 `_lib` 的名稱遮蔽
          （`awakening.py` 檔頭有血證，別改成裸 `import _lib.xxx`），但那個 API
          **每次呼叫都造一份新 module**，而 `module_from_spec` **不會註冊進 `sys.modules`**
          ⇒ 被載入模組的 per-process 快取跟著複製一份。
          對 `persona_profile` 而言，那個快取就是「這個行程發過幾次 Cmd」
          ⇒ 每多一份就多一趟 Cmd 往返。
          🩸 BUG-17 實測（2026-08-20）：`agent_email._persona_profile()` 是
          **每次呼叫**都 `exec_module` 一份新的（三次呼叫三個不同 id、`sys.modules` 裡零筆）
          ⇒ 不帶 `UCL_PP_SKIP_CMD` 時是「每次 `load_persona` 一趟 Cmd」，
          而症狀只是慢 —— 慢很容易被歸因到「Editor 忙」，所以它不會叫。
數值影響: 快取 key ＝ **解析後的絕對路徑**，不是人工取的模組名。
          模組名要靠多端同步義務維持，而打錯一個字會**靜默**分裂成兩份、沒有任何人會喊；
          路徑是同一份檔案的天然身分，不需要任何人記得。
          載入失敗不留半成品在 `sys.modules` —— 殘留的空模組會讓下一個呼叫端拿到
          「載入成功但什麼都沒有」，那比載入失敗難查。
"""
from __future__ import annotations

import importlib.util as _ilu
import sys
from pathlib import Path

_LIB_DIR = Path(__file__).resolve().parent

# `sys.modules` 的命名空間前綴。冒號刻意選的：它不可能出現在真的 import 名裡
# ⇒ 這組 key 永遠不會跟任何 `import x.y` 撞名，也一眼看得出是誰放的。
_KEY_PREFIX = "_ucl_seam:"


def load(path):
    """載入（或取回既有的）`_lib` 模組實例。

    `path` 可以是檔名（`"persona_profile.py"`）或完整路徑；相對值以 `_lib/` 為基準。
    同一支檔重複呼叫回**同一個實例**，不重跑 module 級初始化。
    """
    p = Path(path)
    if not p.is_absolute():
        p = _LIB_DIR / p
    p = p.resolve()

    key = _KEY_PREFIX + str(p)
    mod = sys.modules.get(key)
    if mod is not None:
        return mod

    spec = _ilu.spec_from_file_location(key, p)
    if spec is None or spec.loader is None:
        # 出聲而不是回 None：回 None 的話呼叫端的 `mod.get_field(...)` 會炸在
        # 離現場很遠的地方，錯誤訊息也不會提到「檔案不見了」。
        raise ImportError(f"[seam] 載入不了：{p}（spec 或 loader 為 None）")

    mod = _ilu.module_from_spec(spec)
    # 先註冊再 exec：被載入模組若在 exec 期間（直接或間接）繞回本 loader，
    # 拿到的是同一份半成品而不是再造第二份 —— 這是 CPython import 機制自己的手勢。
    sys.modules[key] = mod
    try:
        spec.loader.exec_module(mod)
    except BaseException:
        sys.modules.pop(key, None)
        raise
    return mod


def persona_profile():
    """persona 欄位讀取接縫（Plan_Persona_Registry_Retirement §8.7 單端解析）。

    這是本專案唯一該用來讀 persona 欄位的入口；**不要**再自己
    `spec_from_file_location("…persona_profile…")`（那就是 BUG-17 的成因）。
    """
    return load("persona_profile.py")
