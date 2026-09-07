"""persona profile 讀取接縫（Phase 0＋§8.7 A＋B，Plan_Persona_Registry_Retirement）。

區塊職責: persona 身分／路由欄位的**唯一讀取入口**（python 端；對側 = C# UCL_PersonaProfile）。
物理意義: 解析單端化（Tim 2026-08-19 拍板 A＋B）—— python 不自己解析原始 persona json，
          改走三段 fallback：
            ① `senate cmd persona --arg all=1 --arg json=1`（主路徑）：
               **本地 C#（`SCP_PersonaProfile`，與 Unity 同一份實作），不需要 Editor**
               ⇒ 讀到的是**現場值，無標記**
            ② ① 跑不通 ⇒ 讀既有快照 ⇒ 回傳值**帶標記**
               `_source="snapshot"`＋`_snapshot_at=<生成時間>`（標記長在值上，不長在 log 裡）
            ③ 連快照都沒有（首次 checkout）⇒ 本地解析原始檔，帶 `_source="local-parse"`
               （資料是新的、解析器是非典範的 —— 誠實標示，別讓它同形於 ①）
          有標記＝非現場值；無標記＝剛從磁碟解析完。兩態不得同形（Tim 五輪拍板）。

          🩸 2026-09-07（TASK-0157 ③）① 換過一次路：原本是 `senate ucmd run PersonaProfile`
          ——**派遣給 Unity Editor**，由 C# 重寫快照再回頭讀那個檔（實測 2.30s／次，且 Editor 得開著）。
          Tim 拍板「senate.exe 目前所有環境都有，Senate CLI 現在才是核心」⇒ 直接叫本地 CLI。
          實測 **21 位 persona 一次 0.20s**。⚠ 副作用少了一個：本檔**不再讓任何人重寫快照**
          （第②段讀到的檔因此可能更舊，但②本來就只在①失敗時走到，且它的回傳值一直帶標記）。

數值影響: 每個 process 只解析一次（module 級快取）；subprocess timeout 30s、UTF-8。
          env `UCL_PP_SKIP_CMD=1` 可顯式跳過第①段（批次腳本不想付 spawn 成本時用 —— 顯式，不猜）。
          ⚠ 那個環境變數名裡的 "CMD" 是歷史（當年指 Editor 的 Cmd）；它現在跳過的是 Senate CLI 那一段。
          寫入端不在本檔：寫入接縫（§8.6 actor＋reason）另案。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

_HERE = Path(__file__).resolve().parent          # <UCL_Core>/Tools~/AgentCommands/_lib

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


def _extract_json_object(text: str) -> dict | None:
    """把 `senate cmd` 的 stdout 裡那顆 JSON 物件挖出來。

    ⚠ 這是一個**契約**不是一個猜測：`persona --arg json=1` 的 stdout 除了那顆物件，
      只會有 CLI 自己加的 `🔢 k = v` 行（那些行不含大括號）。
      ⇒ 判準是「第一個 `{` 到最後一個 `}`」。
    ⛔ 挖不出來時回 None 讓上層退下一段，**不要**回 `{}` ——
      空 dict 的下游症狀是「這棵樹一個 persona 都沒有」，那跟「解析失敗」處置相反。
    """
    s, e = text.find("{"), text.rfind("}")
    if s < 0 or e <= s:
        return None
    try:
        d = json.loads(text[s:e + 1])
    except Exception:
        return None
    return d if isinstance(d, dict) and isinstance(d.get("personas"), dict) else None


def _refresh_via_cmd() -> dict | None:
    """① 現場值：`senate cmd persona --arg all=1 --arg json=1`（**本地跑，不需要 Editor**）。

    回一份快照形狀的 dict；任何失敗（senate 解不到／timeout／exit≠0／stdout 不成形）回 None。

    # 🩸 2026-09-07（TASK-0157 ③，Tim 拍板「senate.exe 目前所有環境都有，Senate CLI 現在才是核心」）
    #   這一段原本是 `senate ucmd run PersonaProfile` —— **派遣給 Editor**，由 C# 重寫快照再讀檔。
    #   那條路的代價不是慢而已，是它讓「正確」與「貴」綁在一起：
    #     · 要現場值 ⇒ 一趟 Editor 往返（實測 **2.30s／次**）而且 Editor 得開著
    #     · Editor 沒開 ⇒ 退快照，而快照**可能是舊的**
    #   ⇒ TASK-0082 的 bug 就長在那個「退」上：三段回的 `source` 是同一個字串，
    #     拿舊快照組出來的 commit trailer 與拿現場值組出來的**完全同形**，
    #     而落點是改不掉的 git history。
    #   現在這一段是本地 C#（`SCP_PersonaProfile`，與 Unity 同一份實作）：
    #   **21 位 persona 一次拿全部 0.20s、不需要 Editor**（2026-09-07 實測）。
    #
    # ⚠ **副作用少了一個，要講明**：舊版會讓 C# **重寫磁碟上的快照**，本版不寫任何檔。
    #   ⇒ `_persona_profile_snapshot.json` 不再被本檔刷新（它仍由 Editor 端自己的時機寫）。
    #   第②段讀到的快照因此可能比以前更舊 —— 但②本來就**只在①失敗時**才走到，
    #   而它回傳的值一直都帶著 `_source="snapshot"` ＋ `_snapshot_at` 兩個標記。
    """
    global _SKIP_REASON
    if os.environ.get("UCL_PP_SKIP_CMD") == "1":
        _SKIP_REASON = "顯式跳過（UCL_PP_SKIP_CMD=1）"
        return None
    try:
        # 🩸 2026-09-04 的教訓留著：這裡曾寫死裸字串 `"senate"`，理由是「PATH 保證有」——
        #   那是**沒量過就宣告的射程**。走 `ucl_paths.senate_exe()`（env → pointer → PATH 三層）。
        # ⚠ region 拿不到就**不帶**（不填預設）：那只會讓 `agent`（帳號 id）欄缺席，
        #   而缺席與「值是空的」在對側是分開的兩件事。⛔ 不要在這裡補一個猜的區域。
        args = [str(_PATHS.senate_exe()), "cmd", "persona",
                "--arg", f"letters_root={_PATHS.letters_root()}",
                "--arg", "all=1", "--arg", "json=1"]
        region = _project_region()
        if region:
            args += ["--arg", f"region={region}"]
        # ⏱ 30s：這一段現在是**本地行程**（實測 0.2s），不再有 Editor 往返。
        #   留這麼寬只是給冷啟／磁碟慢的機器；⛔ 它不是「等 Editor」的那種等待了。
        r = subprocess.run(args, capture_output=True, encoding="utf-8",
                           errors="replace", timeout=30)
        if r.returncode != 0:
            # ⚠ 這裡**不再**寫「Editor 未開？」—— 那句話在這條路上是假的，
            #   而一個講錯原因的訊息比沒有訊息更貴（BUG-13）。
            _SKIP_REASON = f"senate cmd persona exit={r.returncode}"
            return None
        data = _extract_json_object(r.stdout or "")
        if data is None:
            _SKIP_REASON = "senate cmd persona exit=0 但 stdout 解不出 JSON（輸出格式變了？）"
        return data
    except subprocess.TimeoutExpired:
        _SKIP_REASON = "senate cmd persona timeout（本地行程卡住）"
        return None
    except Exception as e:
        _SKIP_REASON = f"senate spawn 失敗（{type(e).__name__}）"
        return None


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
    # ① 現場值 —— 現在直接拿到資料本身，**不再是「請對方寫檔然後我去讀那個檔」**。
    #    ⇒ 舊版有一格「Cmd 說成功但快照讀不到」的縫（兩個動作、兩個真相源）；那一格連同它消失了。
    live = _refresh_via_cmd()
    if live is not None:
        _STATE.update(mode="live", data=live, snapshot_at="")
        return live
    snap = _read_snapshot()
    if snap is not None:
        _STATE.update(mode="snapshot", data=snap,
                      snapshot_at=str(snap.get("generated_at") or ""))
        print(f"⚠ [persona_profile] 沒拿到現場值：{_SKIP_REASON or '原因不明'} —— 改讀快照"
              f"（generated_at={_STATE['snapshot_at'] or '?'}），回傳值帶 _source 標記",
              file=sys.stderr)
        return snap
    _STATE.update(mode="local-parse", data=_local_parse(), snapshot_at="")
    print(f"⚠ [persona_profile] 沒拿到現場值（{_SKIP_REASON or '原因不明'}）且無快照 —— "
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
