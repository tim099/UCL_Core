"""persona profile 讀取接縫（Phase 0，Plan_Persona_Registry_Retirement §4）。

區塊職責: persona 身分／路由欄位的**唯一讀取入口**（python 端；對側 = C# UCL_PersonaProfile）。
物理意義: 退場案要把欄位拆家（身分 → letters/<p>/profile/ 一欄一檔、路由 → 專案層、
          bank → 銀行端反向登記）。消費端若各自讀檔，每動一次家 32 支都要改 ——
          先把讀取收斂到本檔，之後每一期都只改這裡（含 Phase 1 的 read-through lazy migration）。
數值影響: 現階段資料源仍是 AwakenInit/personas/<p>.json（唯讀，不寫）；
          壞檔略過但印 stderr 警告（靜默跳過會讓「檔壞了」跟「沒這個人」同形）。
          寫入端不在本檔：python 端唯一寫入仍是 awakening.save_registry（欄位搬家時同步收斂）。

⚠ 欄位分類（§8.3 拍板）——之後搬家時本檔的實作跟著換，介面不變：
  - ROUTING_FIELDS  綁專案（未來：專案層路由表；bank 另走銀行端反向登記）
  - IDENTITY_FIELDS 不綁專案（未來：letters/<p>/profile/ 一欄一檔）
  - 活體欄（status / last_active / wake_count…）刻意**不在**本接縫 —— 真相源是 lock 與 wakes/，
    要在線名單走 awakening.list_locks()（presence 唯一掃描實作）。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def _ucl_paths():
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_persona_profile", Path(__file__).resolve().parent / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m


_PERSONAS_DIR = _ucl_paths().personas_dir()

# ⚠ email 歸 identity 不歸 routing —— Tim §8.3 二輪拍板「個人信箱進 persona 層」；
#   初版錯放 routing，被紅隊（basecamp seq 12274 題①）對出來：信箱是人的署名，不是專案的路由。
ROUTING_FIELDS = ("agent", "model", "actual_agent")
IDENTITY_FIELDS = ("layer_role", "forked_from", "fork_lineage", "forked_at",
                   "created_at", "identity_vector", "vector_history", "email")


def pool_names() -> list:
    """persona pool 名單（檔名去副檔名；跳過 _ / . 前綴）。排序穩定。

    ⚠ 這是「有哪些 persona」目前的權威來源 —— 不要掃 letters/ 目錄
    （那邊有 9 個幽靈目錄）也不要各自 glob（§0 的教訓）。
    """
    if not _PERSONAS_DIR.is_dir():
        return []
    return sorted(f.stem for f in _PERSONAS_DIR.glob("*.json")
                  if not f.stem.startswith(("_", ".")))


def get_raw(persona: str) -> dict | None:
    """整份 persona 檔（過渡期＝舊檔內容）。不存在回 None；壞檔回 None 並出聲。"""
    p = _PERSONAS_DIR / f"{persona}.json"
    if not p.is_file():
        return None
    try:
        d = json.loads(p.read_text(encoding="utf-8"))
        return d if isinstance(d, dict) else None
    except Exception as e:
        print(f"⚠ [persona_profile] {persona}.json 解析失敗：{e}", file=sys.stderr)
        return None


def iter_raw():
    """逐 persona 產出 (name, dict)。壞檔跳過（get_raw 已出聲）。"""
    for name in pool_names():
        d = get_raw(name)
        if d is not None:
            yield name, d


def get_field(persona: str, field: str, default=None):
    d = get_raw(persona)
    return default if d is None else d.get(field, default)


def get_routing(persona: str) -> dict | None:
    """路由欄（agent / model / actual_agent —— §8.3 綁專案組）。查無此人回 None。"""
    d = get_raw(persona)
    if d is None:
        return None
    return {k: d.get(k, "") for k in ROUTING_FIELDS}


def get_identity(persona: str) -> dict | None:
    """身分欄（§8.3 不綁專案那組）。查無此人回 None。"""
    d = get_raw(persona)
    if d is None:
        return None
    return {k: d.get(k) for k in IDENTITY_FIELDS if k in d}


def load_personas_into(reg: dict) -> dict:
    """把 pool 全量塞進 reg["personas"]（bank_resolver 要的 reg 形狀）。

    registered_mail 等呼叫端原本各自 glob＋json.loads —— 收斂到這裡。
    """
    reg.setdefault("personas", {})
    for name, d in iter_raw():
        reg["personas"][name] = d
    return reg
