"""persona profile 讀取接縫（Phase 0＋§8.7 A＋B，Plan_Persona_Registry_Retirement）。

區塊職責: persona 身分／路由欄位的**唯一讀取入口**（python 端；對側 = C# UCL_PersonaProfile）。
物理意義: 解析單端化（Tim 2026-08-19 拍板 A＋B）—— python 不自己解析原始 persona json，
          改走三段 fallback：
            ① Cmd `PersonaProfile`（主路徑）：C# 現場解析＋重寫快照 ⇒ 讀到的是**現場值，無標記**
            ② Cmd 跑不通（Editor 未開）⇒ 讀既有快照 ⇒ 回傳值**帶標記**
               `_source="snapshot"`＋`_snapshot_at=<生成時間>`（標記長在值上，不長在 log 裡）
            ③ 連快照都沒有（首次 checkout）⇒ 本地解析原始檔，帶 `_source="local-parse"`
               （資料是新的、解析器是非典範的 —— 誠實標示，別讓它同形於 ①）
          有標記＝非現場值；無標記＝Cmd 剛解析完。兩態不得同形（Tim 五輪拍板）。
數值影響: 每個 process 只跑一次 Cmd（module 級快取）；subprocess timeout 45s、UTF-8。
          env `UCL_PP_SKIP_CMD=1` 可顯式跳過 Cmd 段（批次腳本不想付往返成本時用 —— 顯式，不猜）。
          寫入端不在本檔：寫入接縫（§8.6 actor＋reason）另案。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

_HERE = Path(__file__).resolve().parent          # <UCL_Core>/Tools~/AgentCommands/_lib
# ⛔ 原本這裡有 `_RUN_CMD = _HERE.parent / "run_cmd.py"` —— 2026-09-03 移除。
#   留一個指向即將被刪的檔的常數，等於留一顆會在刪檔那天才爆的雷（而且它是**同資料夾兄弟檔**
#   這種永遠成立的定位方式，所以沒有任何一層會先警告）。派遣改走 `senate`（見 _refresh_via_cmd）。


def _ucl_paths():
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_persona_profile", _HERE / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m


_PATHS = _ucl_paths()
_SNAPSHOT_PATH = _PATHS.awaken_init_dir() / "_persona_profile_snapshot.json"

# ⚠ 常數與 C# UCL_PersonaProfile 同名成員**兩端同步義務**；快照裡也帶一份（單端真相），
#   讀快照成功時以快照內的清單為準 —— 這兩行只是 ③ 本地解析段的後備。
ROUTING_FIELDS = ("agent", "model", "actual_agent")
IDENTITY_FIELDS = ("layer_role", "forked_from", "fork_lineage", "forked_at",
                   "created_at", "identity_vector", "vector_history", "email",
                   "plurk_account", "model", "actual_agent")

# module 級狀態：每 process 解析一次。mode = "live" | "snapshot" | "local-parse"
_STATE: dict = {"mode": None, "data": None, "snapshot_at": ""}


_SKIP_REASON = ""   # BUG-13：跳過 Cmd 的「原因」要跟「跑失敗」分開講 —— 一把會講錯原因的尺不能留


def _refresh_via_cmd() -> bool:
    """跑 Cmd 讓 C# 重寫快照。任何失敗（Editor 未開／timeout／exit≠0）回 False，不丟例外。"""
    global _SKIP_REASON
    if os.environ.get("UCL_PP_SKIP_CMD") == "1":
        _SKIP_REASON = "顯式跳過（UCL_PP_SKIP_CMD=1）"
        return False
    try:
        # 2026-09-03（Tim 拍板「run_cmd.py 不留」）：派遣改走 Senate CLI。
        # 🩸 2026-09-04 修一句我自己寫的話：這裡原本寫死裸字串 `"senate"`，理由是「**PATH 保證有**」——
        #   那是一個**我沒有量過就宣告的射程**（憲法⑤寬報）。PATH 是使用者環境，別台機器不保證。
        #   ⇒ 改走 `ucl_paths.senate_exe()`（env → pointer → PATH 三層，全落空才 raise）。
        #   解不到時 `FileNotFoundError` 由下面的 `except Exception` 收成「spawn 失敗」，行為不變。
        # ⛔ **刻意不帶 `--timeout`** —— senate 預設 120 > 這裡的 45，所以逾時仍由 python 這層先觸發，
        #   `TimeoutExpired` 那一格才保得住。帶 45 會讓 senate 先自己退成 exit≠0 ⇒ 原因被講成
        #   「Cmd exit=…（Editor 未開？）」，而它其實是逾時。那正是 BUG-13 擋的事：
        #   **跳過 Cmd 的原因要跟跑失敗分開講 —— 一把會講錯原因的尺不能留。**
        r = subprocess.run(
            [str(_PATHS.senate_exe()), "ucmd", "run", "PersonaProfile"],
            capture_output=True, encoding="utf-8", errors="replace", timeout=45)
        if r.returncode != 0:
            _SKIP_REASON = f"Cmd exit={r.returncode}（Editor 未開？）"
        return r.returncode == 0
    except subprocess.TimeoutExpired:
        _SKIP_REASON = "Cmd timeout（Editor 卡住或很忙）"
        return False
    except Exception as e:
        _SKIP_REASON = f"Cmd spawn 失敗（{type(e).__name__}）"
        return False


def _read_snapshot() -> dict | None:
    try:
        d = json.loads(_SNAPSHOT_PATH.read_text(encoding="utf-8"))
        return d if isinstance(d, dict) and isinstance(d.get("personas"), dict) else None
    except Exception:
        return None


def _local_parse() -> dict:
    """③ 最後備援：直接讀 `letters/<persona>/`（非典範解析器 —— 只在連快照都沒有時用）。

    區塊職責：把 C# `UCL_PersonaProfile.BuildPersonaRaw` + `MergeProfile` 的形狀在 python 側**近似**重建。
    物理意義：2026-08-21 起 persona 資料整合到 letters（中央 `AwakenInit/personas/` 退場）⇒
             舊版那段 `personas/*.json` glob 在改名之後會回**空 pool**，
             而空 pool 的下游症狀是「這個人不存在」，不是「我讀不到」。
    ⚠ 這裡刻意**不算** wake_count／status（要數信、要讀 lock，屬 C# 的活；備援只需要身分欄）。
      少算的欄位一律缺席，不塞 0／不塞 "offline" —— 缺席與「值是 0」不可同形。
    """
    personas = {}
    root = _PATHS.letters_root()
    if not root.is_dir():
        print(f"⚠ [persona_profile] letters 根目錄不存在：{root}", file=sys.stderr)
        return {"personas": {}, "pool": [], "generated_at": ""}
    for d in sorted(root.iterdir()):
        if not d.is_dir() or d.name.startswith(("_", ".")):
            continue
        prof = d / "profile"
        if not prof.is_dir():
            continue                       # 幽靈目錄（改名／早期實驗殘骸）不是 persona
        data = {}
        for f in sorted(prof.glob("*.md")):
            body = f.read_text(encoding="utf-8").strip()
            if f.stem in ("identity_vector", "vector_history", "fork_lineage"):
                try:
                    data[f.stem] = json.loads(body) if body else []
                except Exception as e:
                    print(f"⚠ [persona_profile] {d.name}/profile/{f.name} 不是合法 JSON：{e}",
                          file=sys.stderr)
                continue
            if f.stem in ("forked_from", "forked_at"):
                data[f.stem] = body or None      # 空檔＝null（與 C# NULLABLE_SCALAR_FIELDS 同契約）
                continue
            data[f.stem] = body
        # agent（＝帳號 id）住 bank/<區域>.md；區域是本專案設定，備援路讀不到就跳過該欄
        bank = d / "bank"
        if bank.is_dir():
            region = _project_region()
            own = bank / f"{region}.md" if region else None
            if own is not None and own.exists():
                v = own.read_text(encoding="utf-8").strip()
                if v:
                    data["agent"] = v
        personas[d.name] = data
    return {"personas": personas, "pool": sorted(personas.keys()), "generated_at": ""}


def _project_region() -> str:
    """本專案的區域（貨幣）ID —— 真相源是 C# 的 `UCL_CentralBankSettings`（bank_settings.json）。"""
    try:
        p = _PATHS.data_root() / "Treasury" / "bank_settings.json"
        if not p.exists():
            return ""
        return str(json.loads(p.read_text(encoding="utf-8")).get("currency_id") or "")
    except Exception as e:
        print(f"⚠ [persona_profile] 讀不到區域 ID（{e}）—— 備援路的 agent 欄會缺席", file=sys.stderr)
        return ""


def _load() -> dict:
    """三段 fallback，每 process 一次。回快照形狀的 dict。"""
    if _STATE["mode"] is not None:
        return _STATE["data"]
    if _refresh_via_cmd():
        snap = _read_snapshot()
        if snap is not None:
            _STATE.update(mode="live", data=snap, snapshot_at="")
            return snap
        print("⚠ [persona_profile] Cmd 成功但快照讀不到 —— 退本地解析（這不該發生，回報它）",
              file=sys.stderr)
    snap = _read_snapshot()
    if snap is not None:
        _STATE.update(mode="snapshot", data=snap,
                      snapshot_at=str(snap.get("generated_at") or ""))
        print(f"⚠ [persona_profile] 未走 Cmd：{_SKIP_REASON or '原因不明'} —— 改讀快照"
              f"（generated_at={_STATE['snapshot_at'] or '?'}），回傳值帶 _source 標記",
              file=sys.stderr)
        return snap
    _STATE.update(mode="local-parse", data=_local_parse(), snapshot_at="")
    print(f"⚠ [persona_profile] 未走 Cmd（{_SKIP_REASON or '原因不明'}）且無快照 —— "
          "本地解析原始檔（非典範解析器），回傳值帶 _source 標記", file=sys.stderr)
    return _STATE["data"]


def _mark(d: dict) -> dict:
    """非現場值加標記（回拷貝，不污染快取）。live 模式原樣回。底線前綴＝非本體欄位。"""
    if _STATE["mode"] == "live" or d is None:
        return d
    out = dict(d)
    out["_source"] = _STATE["mode"]
    out["_snapshot_at"] = _STATE["snapshot_at"]
    return out


def source_info() -> dict:
    """本 process 的資料來源（顯示端用）。live=Cmd 現場值。"""
    _load()
    return {"source": _STATE["mode"], "snapshot_at": _STATE["snapshot_at"]}


def pool_names() -> list:
    """persona pool 名單（權威來源；不要掃 letters 目錄也不要各自 glob）。"""
    data = _load()
    pool = data.get("pool")
    if isinstance(pool, list):
        return sorted(str(x) for x in pool)
    return sorted((data.get("personas") or {}).keys())


def get_raw(persona: str) -> dict | None:
    """整份 persona 資料。非現場值帶 `_source`／`_snapshot_at` 標記（Tim 五輪拍板）。"""
    d = (_load().get("personas") or {}).get(persona)
    return _mark(d) if isinstance(d, dict) else None


def iter_raw():
    """逐 persona 產出 (name, dict)（含標記語意，同 get_raw）。"""
    for name in pool_names():
        d = get_raw(name)
        if d is not None:
            yield name, d


def get_field(persona: str, field: str, default=None):
    d = get_raw(persona)
    return default if d is None else d.get(field, default)


def _fields(kind: str) -> tuple:
    """欄位分類以快照（C# 匯出）為準；沒有快照才用本檔常數。"""
    data = _load()
    v = data.get(f"{kind}_fields")
    if isinstance(v, list) and v:
        return tuple(str(x) for x in v)
    return ROUTING_FIELDS if kind == "routing" else IDENTITY_FIELDS


def get_routing(persona: str) -> dict | None:
    """路由欄（§8.3 綁專案組）。查無此人回 None。含標記語意。"""
    d = get_raw(persona)
    if d is None:
        return None
    out = {k: d.get(k, "") for k in _fields("routing")}
    for k in ("_source", "_snapshot_at"):
        if k in d:
            out[k] = d[k]
    return out


def get_identity(persona: str) -> dict | None:
    """身分欄（§8.3 不綁專案組）。查無此人回 None。含標記語意。"""
    d = get_raw(persona)
    if d is None:
        return None
    out = {k: d.get(k) for k in _fields("identity") if k in d}
    for k in ("_source", "_snapshot_at"):
        if k in d:
            out[k] = d[k]
    return out


def load_personas_into(reg: dict) -> dict:
    """把 pool 全量塞進 reg["personas"]（bank_resolver 要的 reg 形狀）。含標記語意。"""
    reg.setdefault("personas", {})
    for name, d in iter_raw():
        reg["personas"][name] = d
    return reg
